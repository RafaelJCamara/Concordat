using Concordat.Application.Abstractions;
using Concordat.Domain.Governance;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;
using Concordat.Formats.Abstractions;

namespace Concordat.Application.Registry;

/// <summary>Creates a subject.</summary>
/// <param name="EnvironmentId">The environment.</param>
/// <param name="Name">The subject name.</param>
/// <param name="Format">The schema language, fixed for the subject's life.</param>
/// <param name="Owner">Who owns the contract.</param>
/// <param name="CompatibilityPolicy">An explicit policy, or null to inherit the environment default.</param>
/// <param name="ContentModel">Whether unknown properties are permitted.</param>
/// <param name="EnvironmentName">
/// The environment as it appeared in the route, so the row can be created if this is the first
/// thing anyone has written to it (decision 23).
/// </param>
public sealed record CreateSubjectCommand(
    EnvironmentId EnvironmentId,
    string Name,
    SchemaFormat Format,
    string Owner,
    CompatibilityPolicy? CompatibilityPolicy,
    ContentModel ContentModel,
    string? EnvironmentName = null) : ICommand<Subject>;

/// <summary>Handles <see cref="CreateSubjectCommand"/>.</summary>
public sealed class CreateSubjectHandler(
    ISubjectRepository subjects,
    IEnvironmentRepository environments,
    ICallerContext caller,
    IAuditLog audit,
    IBillingGate billing,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
    : ICommandHandler<CreateSubjectCommand, Subject>
{
    /// <inheritdoc />
    public async Task<Result<Subject>> HandleAsync(
        CreateSubjectCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var name = SubjectName.Create(command.Name);
        if (name.IsFailure)
        {
            return Result<Subject>.Failure(name.Error!);
        }

        var owner = ActorId.Create(command.Owner);
        if (owner.IsFailure)
        {
            return Result<Subject>.Failure(owner.Error!);
        }

        // The registration policy gates creation as well as registration. Gating only versions
        // would let a misconfigured producer fill a closed production environment with empty
        // subjects — permanent clutter, arriving through the exact door the policy exists to
        // shut, and reported as a success every time.
        var admitted = await RegistrationGate.AdmitAsync(
            environments,
            caller,
            command.EnvironmentId,
            command.EnvironmentName,
            clock,
            cancellationToken).ConfigureAwait(false);

        if (admitted.IsFailure)
        {
            return Result<Subject>.Failure(admitted.Error!);
        }

        var existing = await subjects.FindAsync(command.EnvironmentId, name.Value, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            return Result<Subject>.Failure(
                ConcordatCodes.SubjectAlreadyExists,
                $"Subject '{name.Value}' already exists in this environment.");
        }

        // The plan limit is checked here, at the point of creation, and nowhere else. Reads are
        // never refused for a billing reason: the registry is on the delivery path, and a limit
        // that could stop a consumer resolving a schema would turn a billing dispute into a
        // production outage.
        var allowed = await billing.MayCreateAsync(Meter.Subjects, cancellationToken)
            .ConfigureAwait(false);

        if (!allowed.Allowed)
        {
            return Result<Subject>.Failure(
                ConcordatCodes.PlanLimitReached,
                $"This organisation is at its plan's limit of {allowed.Limit} subjects. " +
                "Upgrade, or retire something you no longer need — nothing already registered " +
                "stops working.");
        }

        var subject = Subject.Create(
            command.EnvironmentId,
            name.Value,
            command.Format,
            owner.Value,
            command.CompatibilityPolicy,
            clock.GetUtcNow(),
            command.ContentModel);

        if (subject.IsFailure)
        {
            return subject;
        }

        subjects.Add(subject.Value);

        audit.Append(AuditEntry.Record(
            command.EnvironmentId,
            AuditAction.SubjectCreated,
            owner.Value,
            name.Value.Value,
            clock.GetUtcNow(),
            $"{WireTokens.For(command.Format)}, {WireTokens.For(command.ContentModel)}"));

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return subject;
    }
}

/// <summary>Fetches one subject with its versions.</summary>
/// <param name="EnvironmentId">The environment.</param>
/// <param name="Name">The subject name.</param>
public sealed record GetSubjectQuery(EnvironmentId EnvironmentId, string Name) : IQuery<Subject>;

/// <summary>Handles <see cref="GetSubjectQuery"/>.</summary>
public sealed class GetSubjectHandler(ISubjectRepository subjects)
    : IQueryHandler<GetSubjectQuery, Subject>
{
    /// <inheritdoc />
    public async Task<Result<Subject>> HandleAsync(
        GetSubjectQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var name = SubjectName.Create(query.Name);
        if (name.IsFailure)
        {
            return Result<Subject>.Failure(name.Error!);
        }

        var subject = await subjects.FindAsync(query.EnvironmentId, name.Value, cancellationToken)
            .ConfigureAwait(false);

        return subject is null
            ? Result<Subject>.Failure(
                ConcordatCodes.SubjectNotFound,
                $"No subject '{name.Value}' in this environment.")
            : Result<Subject>.Success(subject);
    }
}

/// <summary>
/// Lists the subjects in an environment, optionally only those whose name begins with a prefix.
/// </summary>
/// <param name="EnvironmentId">The environment.</param>
/// <param name="Prefix">
/// The raw text the caller typed, or <see langword="null"/> or blank for every subject. It is
/// judged by <see cref="SubjectNamePrefix"/>: ordinal, case-sensitive, and a prefix no name could
/// begin with lists nothing (ADR-038).
/// </param>
/// <param name="Limit">
/// The most subjects to return, or <see langword="null"/> for no paging at all (every subject, as before).
/// Clamped to <see cref="MaxLimit"/>; asking for an offset alone pages with <see cref="DefaultLimit"/>.
/// </param>
/// <param name="Offset">How many of the first subjects to skip. Negative is read as none.</param>
public sealed record ListSubjectsQuery(
    EnvironmentId EnvironmentId, string? Prefix = null, int? Limit = null, int? Offset = null)
    : IQuery<IReadOnlyList<Subject>>
{
    /// <summary>The largest page a caller may ask for.</summary>
    public const int MaxLimit = 500;

    /// <summary>The page size when an offset is named and a limit is not.</summary>
    public const int DefaultLimit = 50;

    /// <summary>Whether the caller asked for a page and not for the whole list.</summary>
    public bool IsPaged => Limit is not null || Offset is not null;
}

/// <summary>Counts the subjects in an environment, optionally only those whose name begins with a prefix.</summary>
/// <param name="EnvironmentId">The environment.</param>
/// <param name="Prefix">The raw text, judged as <see cref="ListSubjectsQuery.Prefix"/> is.</param>
public sealed record CountSubjectsQuery(EnvironmentId EnvironmentId, string? Prefix = null)
    : IQuery<int>;

/// <summary>Handles <see cref="CountSubjectsQuery"/>.</summary>
public sealed class CountSubjectsHandler(ISubjectRepository subjects)
    : IQueryHandler<CountSubjectsQuery, int>
{
    /// <inheritdoc />
    public async Task<Result<int>> HandleAsync(
        CountSubjectsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        SubjectNamePrefix? prefix = null;

        if (!string.IsNullOrWhiteSpace(query.Prefix))
        {
            var parsed = SubjectNamePrefix.Create(query.Prefix);

            // The same answer the list gives for text no name could begin with: none, and not an error.
            if (parsed.IsFailure)
            {
                return Result<int>.Success(0);
            }

            prefix = parsed.Value;
        }

        return Result<int>.Success(
            await subjects.CountAsync(query.EnvironmentId, prefix, cancellationToken).ConfigureAwait(false));
    }
}

/// <summary>Handles <see cref="ListSubjectsQuery"/>.</summary>
public sealed class ListSubjectsHandler(ISubjectRepository subjects)
    : IQueryHandler<ListSubjectsQuery, IReadOnlyList<Subject>>
{
    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<Subject>>> HandleAsync(
        ListSubjectsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.IsPaged)
        {
            return await HandlePageAsync(query, cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(query.Prefix))
        {
            var all = await subjects.ListAsync(query.EnvironmentId, cancellationToken)
                .ConfigureAwait(false);

            return Result<IReadOnlyList<Subject>>.Success(all);
        }

        var prefix = SubjectNamePrefix.Create(query.Prefix);

        if (prefix.IsFailure)
        {
            // Not an error, and not asked of the store. A filter is not a lookup: the true answer
            // to "which subjects begin with this" is none, and a search box is typed into one
            // character at a time, so refusing "acme-" would turn a keystroke into an error. The
            // store is never asked because the text is exactly what must not reach a query.
            return Result<IReadOnlyList<Subject>>.Success([]);
        }

        var found = await subjects.ListByPrefixAsync(
            query.EnvironmentId, prefix.Value, cancellationToken).ConfigureAwait(false);

        return Result<IReadOnlyList<Subject>>.Success(found);
    }

    private async Task<Result<IReadOnlyList<Subject>>> HandlePageAsync(
        ListSubjectsQuery query, CancellationToken cancellationToken)
    {
        SubjectNamePrefix? prefix = null;

        if (!string.IsNullOrWhiteSpace(query.Prefix))
        {
            var parsed = SubjectNamePrefix.Create(query.Prefix);

            if (parsed.IsFailure)
            {
                // As unpaged: text no name could begin with lists nothing, and the store is not asked.
                return Result<IReadOnlyList<Subject>>.Success([]);
            }

            prefix = parsed.Value;
        }

        // Clamped and never refused, as the audit read and the approval queue clamp theirs: a page size is a
        // preference and a bound on cost, and the nearest page is a better answer than an error.
        var paging = new SubjectPaging(
            Math.Clamp(query.Limit ?? ListSubjectsQuery.DefaultLimit, 1, ListSubjectsQuery.MaxLimit),
            Math.Max(query.Offset ?? 0, 0));

        var page = await subjects.ListPageAsync(query.EnvironmentId, prefix, paging, cancellationToken)
            .ConfigureAwait(false);

        return Result<IReadOnlyList<Subject>>.Success(page);
    }
}

/// <summary>Checks a proposed schema without writing anything.</summary>
/// <param name="EnvironmentId">The environment.</param>
/// <param name="SubjectName">The subject.</param>
/// <param name="Body">The proposed schema.</param>
public sealed record CheckCompatibilityQuery(
    EnvironmentId EnvironmentId, string SubjectName, string? Body)
    : IQuery<CompatibilityCheckResult>;

/// <summary>The dry-run answer.</summary>
/// <param name="Compatible">Whether the proposal satisfies the policy.</param>
/// <param name="SchemaId">The id the proposal would receive.</param>
/// <param name="Policy">The policy it was evaluated under.</param>
/// <param name="Report">Findings, including ones the policy tolerated.</param>
/// <param name="SuggestedSemver">The label the change warrants, given the current tip.</param>
public sealed record CompatibilityCheckResult(
    bool Compatible,
    string SchemaId,
    CompatibilityPolicy Policy,
    CompatibilityReport Report,
    string? SuggestedSemver);

/// <summary>Handles <see cref="CheckCompatibilityQuery"/>.</summary>
/// <remarks>Never writes. A dry run that persisted anything would be a trap in CI.</remarks>
public sealed class CheckCompatibilityHandler(
    ISubjectRepository subjects,
    ISchemaRepository schemas,
    IEnvironmentRepository environments,
    ICompatibilityEvaluator evaluator)
    : IQueryHandler<CheckCompatibilityQuery, CompatibilityCheckResult>
{
    /// <inheritdoc />
    public async Task<Result<CompatibilityCheckResult>> HandleAsync(
        CheckCompatibilityQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var name = SubjectName.Create(query.SubjectName);
        if (name.IsFailure)
        {
            return Result<CompatibilityCheckResult>.Failure(name.Error!);
        }

        var subject = await subjects.FindAsync(query.EnvironmentId, name.Value, cancellationToken)
            .ConfigureAwait(false);

        if (subject is null)
        {
            return Result<CompatibilityCheckResult>.Failure(
                ConcordatCodes.SubjectNotFound,
                $"No subject '{name.Value}' in this environment.");
        }

        // The same history registration uses. A dry run that answered a different question
        // from the gate would be worse than no dry run at all.
        //
        // One round trip for the whole history rather than one per prior version — this is a
        // read endpoint with no billing gate ahead of it to absorb the cost, so it is the one
        // most exposed to a subject with a long history being queried repeatedly (CI running
        // `check` on every push, for instance).
        var history = CompatibilityHistory.Of(subject);
        var bodies = await schemas.FindManyAsync(
            [.. history.Select(v => v.SchemaId).Distinct()], cancellationToken)
            .ConfigureAwait(false);
        var priors = history
            .Where(v => bodies.ContainsKey(v.SchemaId))
            .Select(v => new PriorSchema(v.Ordinal, bodies[v.SchemaId].Body))
            .ToList();

        // The same environment registration reads, found the way its gate finds it (by id: the
        // query carries no name). A dry run that judged an inheriting subject by the built-in
        // while registration judged it by the environment's default would go green or red for a
        // reason the real registration then did not share. No row is not a refusal: this is a read
        // about a subject, and one can outlive an environment that was never a row.
        var environment = await environments.FindAsync(query.EnvironmentId, cancellationToken)
            .ConfigureAwait(false);

        var policy = EnvironmentPolicies.EffectiveFor(subject, environment);
        var evaluated = evaluator.Evaluate(
            subject.Format, query.Body, priors, policy, subject.ContentModel);

        if (evaluated.IsFailure)
        {
            return Result<CompatibilityCheckResult>.Failure(evaluated.Error!);
        }

        // The same gate registration uses, for the same reason as the same history and the same
        // policy above: a dry run that said yes to a document the registration then refused would
        // go green in CI for a reason the real thing does not share.
        var invalid = await SchemaValidityGate
            .RefuseAsync(evaluated.Value, schemas, cancellationToken).ConfigureAwait(false);

        if (invalid is not null)
        {
            return Result<CompatibilityCheckResult>.Failure(invalid);
        }

        return Result<CompatibilityCheckResult>.Success(new CompatibilityCheckResult(
            evaluated.Value.Report.IsCompatible,
            evaluated.Value.Schema.Id.Value,
            policy,
            evaluated.Value.Report,
            SuggestSemver(subject, evaluated.Value.Report.SuggestedBump)));
    }

    private static string? SuggestSemver(Subject subject, SemverBump bump)
    {
        var latest = subject.Versions
            .Where(v => v.SemanticVersion is not null && v.Status is not VersionStatus.Rejected)
            .Select(v => v.SemanticVersion!.Value)
            .DefaultIfEmpty()
            .Max();

        if (latest == default)
        {
            return bump is SemverBump.None ? null : "1.0.0";
        }

        return bump switch
        {
            SemverBump.Major => new SemanticVersion(latest.Major + 1, 0, 0).ToString(),
            SemverBump.Minor => new SemanticVersion(latest.Major, latest.Minor + 1, 0).ToString(),
            SemverBump.Patch => new SemanticVersion(latest.Major, latest.Minor, latest.Patch + 1).ToString(),
            _ => null,
        };
    }
}
