using Concordat.Application.Abstractions;
using Concordat.Application.Registry;
using Concordat.Domain.Contracts;
using Concordat.Domain.Governance;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;
using Concordat.Formats.Abstractions;

namespace Concordat.Application.Governance;

/// <summary>
/// Answers "who breaks if I change this subject?" (M7.4, DESIGN §4 Context D).
/// </summary>
/// <param name="EnvironmentName">Which environment.</param>
/// <param name="SubjectName">The subject being changed.</param>
/// <param name="CandidateBody">
/// A schema that is not registered yet — what CI asks before pushing. When null, the analysis
/// runs against a version already in the registry.
/// </param>
/// <param name="Ordinal">
/// Which registered version to analyse. Ignored when <paramref name="CandidateBody"/> is
/// supplied; defaults to the subject's latest when both are absent.
/// </param>
public sealed record AnalyseImpactQuery(
    string? EnvironmentName,
    string? SubjectName,
    string? CandidateBody,
    int? Ordinal) : IQuery<ImpactReport>;

/// <summary>Why a consumer could not be judged.</summary>
public enum ImpactCertainty
{
    /// <summary>The registry compared two schemas and knows the answer.</summary>
    Checked = 1,

    /// <summary>
    /// The consumer declared <c>latest</c>, so it follows the pointer wherever it goes.
    /// </summary>
    /// <remarks>
    /// The registry cannot say whether such a consumer breaks, because it does not know what
    /// its code was built against — only what it fetches. Reporting it as safe would be a
    /// guess; reporting it as broken would make every analysis useless. It is reported as
    /// exactly what it is.
    /// </remarks>
    FollowsLatest = 2,

    /// <summary>The version the consumer pinned no longer exists, or its schema is missing.</summary>
    Unresolvable = 3,
}

/// <summary>What a change does to one registered consumer.</summary>
/// <param name="Service">The service name.</param>
/// <param name="Selector">What it declared: <c>latest</c>, an ordinal, or <c>&gt;=N</c>.</param>
/// <param name="ReaderOrdinal">
/// The version its verdict was computed against — the pinned ordinal, or the floor of a range.
/// </param>
/// <param name="Breaks">Whether the change stops this consumer reading new messages.</param>
/// <param name="Certainty">Whether the registry knows, and why not when it does not.</param>
/// <param name="LastSeenAt">When the service last declared itself.</param>
/// <param name="Stale">Whether nothing has been heard from it in a long time.</param>
/// <param name="Reasons">The divergences that break it, in the engine's own words.</param>
public sealed record ConsumerImpact(
    string Service,
    string Selector,
    int? ReaderOrdinal,
    bool Breaks,
    ImpactCertainty Certainty,
    DateTimeOffset LastSeenAt,
    bool Stale,
    IReadOnlyList<string> Reasons);

/// <summary>The full answer.</summary>
/// <param name="Subject">The subject analysed.</param>
/// <param name="CandidateOrdinal">
/// The version analysed, or null when the candidate is not registered.
/// </param>
/// <param name="CandidateSchemaId">The candidate's content-addressed id.</param>
/// <param name="BreakingCount">How many registered consumers break.</param>
/// <param name="Consumers">Every registered consumer of the subject.</param>
/// <param name="Contracts">
/// The contracts whose bindings carry this subject. Not consumers, but the other place a
/// change to this subject is felt.
/// </param>
public sealed record ImpactReport(
    string Subject,
    int? CandidateOrdinal,
    string? CandidateSchemaId,
    int BreakingCount,
    IReadOnlyList<ConsumerImpact> Consumers,
    IReadOnlyList<string> Contracts);

/// <summary>
/// Handles <see cref="AnalyseImpactQuery"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The direction is FORWARD, and getting it backwards would invert every answer.</b>
/// Registration asks BACKWARD by default: can the new schema read what the old one wrote?
/// Impact asks the opposite question about a different party — a consumer still holding
/// version K, faced with data written under the candidate. That is "can the old schema read
/// what the new one writes", which is FORWARD, evaluated once per distinct reader version.
/// </para>
/// <para>
/// The surface comes from the subject's own effective policy rather than being fixed. A subject
/// governed at <c>WIRE</c> tolerates JSON-name changes by design, and reporting its consumers as
/// broken by one would contradict the verdict the registry itself gave the change.
/// </para>
/// </remarks>
public sealed class AnalyseImpactHandler(
    IEnvironmentRepository environments,
    ISubjectRepository subjects,
    ISchemaRepository schemas,
    IServiceRegistrationRepository services,
    IContractRepository contracts,
    ICompatibilityEvaluator evaluator,
    TimeProvider clock)
    : IQueryHandler<AnalyseImpactQuery, ImpactReport>
{
    /// <inheritdoc />
    public async Task<Result<ImpactReport>> HandleAsync(
        AnalyseImpactQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var environment = await EnvironmentPolicies.RequireAsync(
            environments, query.EnvironmentName, cancellationToken).ConfigureAwait(false);

        if (environment.IsFailure)
        {
            return Result<ImpactReport>.Failure(environment.Error!);
        }

        var name = SubjectName.Create(query.SubjectName);
        if (name.IsFailure)
        {
            return Result<ImpactReport>.Failure(name.Error!);
        }

        var subject = await subjects.FindAsync(environment.Value.Id, name.Value, cancellationToken)
            .ConfigureAwait(false);

        if (subject is null)
        {
            return Result<ImpactReport>.Failure(
                ConcordatCodes.SubjectNotFound,
                $"No subject '{name.Value}' in environment '{environment.Value.Name.Value}'.");
        }

        var candidate = await ResolveCandidateAsync(subject, query, cancellationToken)
            .ConfigureAwait(false);

        if (candidate.IsFailure)
        {
            return Result<ImpactReport>.Failure(candidate.Error!);
        }

        var policy = subject.EffectivePolicy(
            environment.Value.DefaultCompatibilityPolicy);

        var forward = new CompatibilityPolicy(CompatibilityMode.Forward, policy.Surface);

        var registered = await services.ListAsync(environment.Value.Id, cancellationToken)
            .ConfigureAwait(false);

        var now = clock.GetUtcNow();
        var verdicts = new Dictionary<int, (bool Breaks, IReadOnlyList<string> Reasons)>();
        var impacts = new List<ConsumerImpact>();

        foreach (var service in registered)
        {
            if (service.ConsumerOf(name.Value) is not { } reference)
            {
                continue;
            }

            impacts.Add(await JudgeAsync(
                subject, service, reference, candidate.Value, forward, verdicts, now,
                cancellationToken).ConfigureAwait(false));
        }

        var governing = await GoverningContractsAsync(
            environment.Value.Id, name.Value, cancellationToken).ConfigureAwait(false);

        return Result<ImpactReport>.Success(new ImpactReport(
            subject.Name.Value,
            candidate.Value.Ordinal,
            candidate.Value.SchemaId,
            impacts.Count(i => i.Breaks),
            [.. impacts.OrderByDescending(i => i.Breaks).ThenBy(i => i.Service, StringComparer.Ordinal)],
            governing));
    }

    private async Task<ConsumerImpact> JudgeAsync(
        Subject subject,
        ServiceRegistration service,
        SubjectRef reference,
        Candidate candidate,
        CompatibilityPolicy forward,
        Dictionary<int, (bool Breaks, IReadOnlyList<string> Reasons)> verdicts,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ConsumerImpact Report(
            int? readerOrdinal,
            bool breaks,
            ImpactCertainty certainty,
            IReadOnlyList<string> reasons) =>
            new(service.Name, reference.Selector.ToString(), readerOrdinal, breaks, certainty,
                service.LastSeenAt, service.IsStale(now), reasons);

        if (reference.Selector.Kind is VersionSelectorKind.Latest)
        {
            return Report(subject.Latest?.Ordinal, false, ImpactCertainty.FollowsLatest, []);
        }

        // A range's floor is the oldest version the consumer claims to handle, so it is the
        // reader that has to survive the change. Checking the newest instead would clear a
        // consumer whose oldest supported version is exactly the one that breaks.
        var readerOrdinal = reference.Selector.Ordinal!.Value;

        if (verdicts.TryGetValue(readerOrdinal, out var cached))
        {
            return Report(readerOrdinal, cached.Breaks, ImpactCertainty.Checked, cached.Reasons);
        }

        var reader = subject.Versions.FirstOrDefault(v => v.Ordinal == readerOrdinal);
        if (reader is null)
        {
            return Report(readerOrdinal, false, ImpactCertainty.Unresolvable, []);
        }

        var schema = await schemas.FindAsync(reader.SchemaId, cancellationToken)
            .ConfigureAwait(false);

        if (schema is null)
        {
            return Report(readerOrdinal, false, ImpactCertainty.Unresolvable, []);
        }

        var evaluated = evaluator.Evaluate(
            subject.Format,
            candidate.Body,
            [new PriorSchema(readerOrdinal, schema.Body)],
            forward,
            subject.ContentModel);

        if (evaluated.IsFailure)
        {
            return Report(readerOrdinal, false, ImpactCertainty.Unresolvable, []);
        }

        var breaks = !evaluated.Value.Report.IsCompatible;
        var reasons = evaluated.Value.Report.BreakingChanges
            .Select(b => b.Message)
            .ToList();

        verdicts[readerOrdinal] = (breaks, reasons);
        return Report(readerOrdinal, breaks, ImpactCertainty.Checked, reasons);
    }

    private async Task<IReadOnlyList<string>> GoverningContractsAsync(
        EnvironmentId environmentId, SubjectName subject, CancellationToken cancellationToken)
    {
        var all = await contracts.ListAsync(environmentId, cancellationToken).ConfigureAwait(false);

        return
        [
            .. all
                .Where(c =>
                    c.Publishes.Any(b => b.Subjects.Any(r => r.Subject == subject)) ||
                    c.Consumes.Any(b => b.Subjects.Any(r => r.Subject == subject)))
                .Select(c => c.Name)
                .OrderBy(n => n, StringComparer.Ordinal),
        ];
    }

    private async Task<Result<Candidate>> ResolveCandidateAsync(
        Subject subject, AnalyseImpactQuery query, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(query.CandidateBody))
        {
            // Canonicalised through the evaluator with no priors, so the body compared against
            // each reader is the same text the registry would store — not what the caller
            // happened to type.
            var evaluated = evaluator.Evaluate(
                subject.Format,
                query.CandidateBody,
                [],
                CompatibilityPolicy.Default,
                subject.ContentModel);

            return evaluated.IsFailure
                ? Result<Candidate>.Failure(evaluated.Error!)
                : Result<Candidate>.Success(new Candidate(
                    evaluated.Value.Schema.Body, null, evaluated.Value.Schema.Id.Value));
        }

        var ordinal = query.Ordinal ?? subject.Latest?.Ordinal;

        if (ordinal is null)
        {
            return Result<Candidate>.Failure(
                ConcordatCodes.VersionNotFound,
                $"Subject '{subject.Name}' has no approved version to analyse, and no " +
                "candidate schema was supplied.");
        }

        var version = subject.Versions.FirstOrDefault(v => v.Ordinal == ordinal);
        if (version is null)
        {
            return Result<Candidate>.Failure(
                ConcordatCodes.VersionNotFound,
                $"Subject '{subject.Name}' has no version {ordinal}.");
        }

        var schema = await schemas.FindAsync(version.SchemaId, cancellationToken)
            .ConfigureAwait(false);

        return schema is null
            ? Result<Candidate>.Failure(
                ConcordatCodes.SchemaNotFound,
                $"Version {ordinal} of '{subject.Name}' points at a schema that is not stored.")
            : Result<Candidate>.Success(
                new Candidate(schema.Body, version.Ordinal, version.SchemaId.Value));
    }

    private sealed record Candidate(string Body, int? Ordinal, string SchemaId);
}
