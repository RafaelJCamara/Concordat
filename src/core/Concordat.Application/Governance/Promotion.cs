using Concordat.Application.Abstractions;
using Concordat.Application.Registry;
using Concordat.Domain.Governance;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;
using Concordat.Formats.Abstractions;

namespace Concordat.Application.Governance;

/// <summary>
/// Moves a version from one environment into another, re-checking it there (M7.4).
/// </summary>
/// <param name="FromEnvironment">Where the version is now.</param>
/// <param name="ToEnvironment">Where it is going.</param>
/// <param name="SubjectName">The subject.</param>
/// <param name="Ordinal">Which version, or null for the source's latest.</param>
/// <param name="PromotedBy">
/// Informational only — accepted from the request body but never trusted as the audit
/// identity, which is always <c>ICallerContext.Current.Actor</c>. A client-supplied string is
/// attacker-assertable; the authenticated caller is not.
/// </param>
public sealed record PromoteVersionCommand(
    string? FromEnvironment,
    string? ToEnvironment,
    string? SubjectName,
    int? Ordinal,
    string? PromotedBy) : ICommand<PromotionResult>;

/// <summary>What a promotion produced.</summary>
/// <param name="Subject">The subject.</param>
/// <param name="FromEnvironment">Where it came from.</param>
/// <param name="ToEnvironment">Where it went.</param>
/// <param name="SourceOrdinal">The version promoted.</param>
/// <param name="TargetOrdinal">The ordinal it landed on in the target.</param>
/// <param name="SchemaId">The content-addressed id, identical in both environments.</param>
/// <param name="Status">Active, or awaiting approval when the target's history disagrees.</param>
/// <param name="Created">False when the target already had this schema at its tip.</param>
/// <param name="SubjectCreated">Whether the target subject was created by this promotion.</param>
/// <param name="Report">The target environment's verdict, including tolerated divergences.</param>
public sealed record PromotionResult(
    string Subject,
    string FromEnvironment,
    string ToEnvironment,
    int SourceOrdinal,
    int TargetOrdinal,
    string SchemaId,
    VersionStatus Status,
    bool Created,
    bool SubjectCreated,
    CompatibilityReport Report);

/// <summary>
/// Handles <see cref="PromoteVersionCommand"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The re-check is the point.</b> A version that is compatible in <c>dev</c> says nothing
/// about <c>prod</c>, whose history is older, whose policy may be stricter, and whose consumers
/// are the ones that matter. Promotion is therefore an ordinary registration in the target,
/// judged entirely by the target — not a copy of a row.
/// </para>
/// <para>
/// <b>A breaking promotion lands as a proposal, it is not refused.</b> That is ADR-017 applied
/// consistently: the registry records breaking changes for review rather than rejecting them,
/// and making promotion the one exception would mean the safest-sounding operation is the one
/// that cannot be reviewed.
/// </para>
/// <para>
/// <b>The schema id must not change, and this asserts it.</b> Content addressing (ADR-015) is
/// what lets an in-flight envelope stay valid across a promotion. If the id ever differed
/// between environments, every consumer holding the old id would fail to resolve it, so a
/// mismatch is an internal error rather than something to report and continue past.
/// </para>
/// </remarks>
public sealed class PromoteVersionHandler(
    IEnvironmentRepository environments,
    ISubjectRepository subjects,
    ISchemaRepository schemas,
    ICompatibilityEvaluator evaluator,
    ICallerContext caller,
    IAuditLog audit,
    IOutbox outbox,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
    : ICommandHandler<PromoteVersionCommand, PromotionResult>
{
    /// <inheritdoc />
    public async Task<Result<PromotionResult>> HandleAsync(
        PromoteVersionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var source = await EnvironmentPolicies.RequireAsync(
            environments, command.FromEnvironment, cancellationToken).ConfigureAwait(false);

        if (source.IsFailure)
        {
            return Result<PromotionResult>.Failure(source.Error!);
        }

        var target = await EnvironmentPolicies.RequireAsync(
            environments, command.ToEnvironment, cancellationToken).ConfigureAwait(false);

        if (target.IsFailure)
        {
            return Result<PromotionResult>.Failure(target.Error!);
        }

        if (source.Value.Id == target.Value.Id)
        {
            return Result<PromotionResult>.Failure(
                ConcordatCodes.PromotionTargetInvalid,
                "The source and target environments are the same. A promotion into the same " +
                "environment would re-register a version against its own history.");
        }

        var name = SubjectName.Create(command.SubjectName);
        if (name.IsFailure)
        {
            return Result<PromotionResult>.Failure(name.Error!);
        }

        var actor = caller.Current.Actor;

        var found = await LoadSourceAsync(source.Value, name.Value, command.Ordinal, cancellationToken)
            .ConfigureAwait(false);

        if (found.IsFailure)
        {
            return Result<PromotionResult>.Failure(found.Error!);
        }

        var (sourceSubject, sourceVersion, body) = found.Value;

        var targetSubject = await subjects.FindAsync(target.Value.Id, name.Value, cancellationToken)
            .ConfigureAwait(false);

        var subjectCreated = targetSubject is null;

        if (targetSubject is null)
        {
            // Created rather than refused, unlike an ordinary registration (M1.6). Promotion is
            // precisely the flow where the target subject legitimately does not exist yet, and
            // requiring a manual create first makes it a two-step everyone would script -- with
            // the format and content model guessed by whoever wrote the script rather than
            // carried across from the source.
            var created = Subject.Create(
                target.Value.Id,
                sourceSubject.Name,
                sourceSubject.Format,
                sourceSubject.Owner,
                sourceSubject.CompatibilityPolicy,
                clock.GetUtcNow(),
                sourceSubject.ContentModel);

            if (created.IsFailure)
            {
                return Result<PromotionResult>.Failure(created.Error!);
            }

            targetSubject = created.Value;
            subjects.Add(targetSubject);
        }

        var priors = await LoadPriorsAsync(targetSubject, cancellationToken).ConfigureAwait(false);

        var evaluated = evaluator.Evaluate(
            targetSubject.Format,
            body,
            priors,
            targetSubject.EffectivePolicy(target.Value.DefaultCompatibilityPolicy),
            targetSubject.ContentModel);

        if (evaluated.IsFailure)
        {
            return Result<PromotionResult>.Failure(evaluated.Error!);
        }

        // Thrown, not returned as a failure. A Result says "your request cannot be satisfied";
        // this says the content-addressing invariant is broken, which is a defect in the
        // registry and not something the caller can fix by asking differently.
        if (evaluated.Value.Schema.Id != sourceVersion.SchemaId)
        {
            throw new InvalidOperationException(
                $"Promoting '{name.Value}' produced schema id {evaluated.Value.Schema.Id} in " +
                $"'{target.Value.Name.Value}' but it is {sourceVersion.SchemaId} in " +
                $"'{source.Value.Name.Value}'. Identity is content-addressed and must not " +
                "change across environments, or every in-flight envelope carrying the old id " +
                "becomes unresolvable.");
        }

        var stored = await schemas.AddIfMissingAsync(evaluated.Value.Schema, cancellationToken)
            .ConfigureAwait(false);

        var registered = targetSubject.RegisterVersion(
            stored,
            evaluated.Value.Verdict,
            sourceVersion.SemanticVersion,
            $"Promoted from {source.Value.Name.Value} version {sourceVersion.Ordinal}.",
            actor,
            clock.GetUtcNow());

        if (registered.IsFailure)
        {
            return Result<PromotionResult>.Failure(registered.Error!);
        }

        var landed = registered.Value.Version;

        audit.Append(AuditEntry.Record(
            target.Value.Id,
            AuditAction.VersionPromoted,
            actor,
            name.Value.Value,
            clock.GetUtcNow(),
            $"{source.Value.Name.Value} v{sourceVersion.Ordinal} -> " +
            $"{target.Value.Name.Value} v{landed.Ordinal} ({WireTokens.For(landed.Status)})"));

        // Notified into the target environment, which is where the people who care about a
        // change arriving in prod have subscribed.
        outbox.Stage(OutboxMessage.Stage(
            target.Value.Id,
            landed.Status is VersionStatus.AwaitingApproval
                ? NotificationEvent.BreakingChangeSubmitted
                : NotificationEvent.VersionPromoted,
            name.Value.Value,
            $"{name.Value} was promoted from {source.Value.Name.Value} " +
            $"v{sourceVersion.Ordinal} into {target.Value.Name.Value} as v{landed.Ordinal} " +
            $"({WireTokens.For(landed.Status)}) by {actor.Value}.",
            clock.GetUtcNow()));

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result<PromotionResult>.Success(new PromotionResult(
            name.Value.Value,
            source.Value.Name.Value,
            target.Value.Name.Value,
            sourceVersion.Ordinal,
            landed.Ordinal,
            landed.SchemaId.Value,
            landed.Status,
            registered.Value.Created,
            subjectCreated,
            evaluated.Value.Report));
    }

    private async Task<Result<(Subject Subject, SchemaVersion Version, string Body)>> LoadSourceAsync(
        Domain.Registry.Environment source,
        SubjectName name,
        int? ordinal,
        CancellationToken cancellationToken)
    {
        var subject = await subjects.FindAsync(source.Id, name, cancellationToken)
            .ConfigureAwait(false);

        if (subject is null)
        {
            return Result<(Subject, SchemaVersion, string)>.Failure(
                ConcordatCodes.SubjectNotFound,
                $"No subject '{name}' in environment '{source.Name.Value}'.");
        }

        var wanted = ordinal ?? subject.Latest?.Ordinal;

        if (wanted is null)
        {
            return Result<(Subject, SchemaVersion, string)>.Failure(
                ConcordatCodes.VersionNotFound,
                $"Subject '{name}' has no approved version in '{source.Name.Value}' to promote.");
        }

        var version = subject.Versions.FirstOrDefault(v => v.Ordinal == wanted);

        if (version is null)
        {
            return Result<(Subject, SchemaVersion, string)>.Failure(
                ConcordatCodes.VersionNotFound,
                $"Subject '{name}' has no version {wanted} in '{source.Name.Value}'.");
        }

        if (version.Status is not VersionStatus.Active)
        {
            return Result<(Subject, SchemaVersion, string)>.Failure(
                ConcordatCodes.PromotionSourceNotActive,
                $"Version {wanted} of '{name}' is {WireTokens.For(version.Status)} in " +
                $"'{source.Name.Value}'. Only an accepted version can be promoted — otherwise " +
                "a proposal would be laundered into the target and judged only there.");
        }

        var schema = await schemas.FindAsync(version.SchemaId, cancellationToken)
            .ConfigureAwait(false);

        return schema is null
            ? Result<(Subject, SchemaVersion, string)>.Failure(
                ConcordatCodes.SchemaNotFound,
                $"Version {wanted} of '{name}' points at a schema that is not stored.")
            : Result<(Subject, SchemaVersion, string)>.Success((subject, version, schema.Body));
    }

    private async Task<IReadOnlyList<PriorSchema>> LoadPriorsAsync(
        Subject subject, CancellationToken cancellationToken)
    {
        var history = CompatibilityHistory.Of(subject);

        // One round trip for the whole history rather than one per prior version: a subject
        // that has been through a hundred revisions used to mean a hundred queries on every
        // single promotion into it, not just its own.
        var bodies = await schemas.FindManyAsync(
            [.. history.Select(v => v.SchemaId).Distinct()], cancellationToken)
            .ConfigureAwait(false);

        return [.. history
            .Where(v => bodies.ContainsKey(v.SchemaId))
            .Select(v => new PriorSchema(v.Ordinal, bodies[v.SchemaId].Body))];
    }
}
