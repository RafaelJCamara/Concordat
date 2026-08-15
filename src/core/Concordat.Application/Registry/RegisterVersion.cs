using Concordat.Application.Abstractions;
using Concordat.Domain.Governance;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;
using Concordat.Formats.Abstractions;

namespace Concordat.Application.Registry;

/// <summary>Registers a new version of a subject.</summary>
/// <param name="EnvironmentId">The environment.</param>
/// <param name="SubjectName">The subject.</param>
/// <param name="Body">The schema as authored.</param>
/// <param name="SemanticVersion">An optional intent label, verified against the verdict.</param>
/// <param name="Changelog">An optional note.</param>
/// <param name="RegisteredBy">
/// Informational only — accepted from the request body but never trusted as the audit
/// identity, which is always <c>ICallerContext.Current.Actor</c>. A client-supplied string is
/// attacker-assertable; the authenticated caller is not.
/// </param>
/// <param name="EnvironmentName">
/// The environment as it appeared in the route, so the row can be created if this is the first
/// thing anyone has written to it (decision 23).
/// </param>
public sealed record RegisterVersionCommand(
    EnvironmentId EnvironmentId,
    string SubjectName,
    string? Body,
    string? SemanticVersion,
    string? Changelog,
    string RegisteredBy,
    string? EnvironmentName = null) : ICommand<RegisterVersionResult>;

/// <summary>What registration produced.</summary>
/// <param name="SubjectName">The subject.</param>
/// <param name="Ordinal">The version ordinal.</param>
/// <param name="SchemaId">The content-addressed schema id.</param>
/// <param name="Status">Whether the version is active or awaiting approval.</param>
/// <param name="Created">
/// <see langword="false"/> when the schema was already at the tip and no ordinal was allocated.
/// </param>
/// <param name="Report">The engine's findings, including ones the policy tolerated.</param>
/// <param name="Portability">
/// Where the registered schema relies on behaviour that differs between SDKs (M6.1). Never a
/// reason the registration failed — those refuse before this record exists — but the moment the
/// author is most likely to act on it.
/// </param>
public sealed record RegisterVersionResult(
    string SubjectName,
    int Ordinal,
    string SchemaId,
    VersionStatus Status,
    bool Created,
    CompatibilityReport Report,
    IReadOnlyList<PortabilityFinding> Portability);

/// <summary>Handles <see cref="RegisterVersionCommand"/>.</summary>
public sealed class RegisterVersionHandler(
    ISubjectRepository subjects,
    ISchemaRepository schemas,
    IEnvironmentRepository environments,
    ICallerContext caller,
    ICompatibilityEvaluator evaluator,
    IAuditLog audit,
    IOutbox outbox,
    IBillingGate billing,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
    : ICommandHandler<RegisterVersionCommand, RegisterVersionResult>
{
    /// <inheritdoc />
    public async Task<Result<RegisterVersionResult>> HandleAsync(
        RegisterVersionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var name = SubjectName.Create(command.SubjectName);
        if (name.IsFailure)
        {
            return Result<RegisterVersionResult>.Failure(name.Error!);
        }

        // The authenticated caller, not command.RegisteredBy: the audit trail is presented as
        // the authoritative record of who did what, and a client-supplied field is attacker-
        // assertable -- a shared CI key could otherwise attribute a change to any colleague.
        // caller.Current.Actor is set once at authentication and cannot be influenced by the
        // request body.
        var actor = caller.Current.Actor;

        // THE REGISTRATION POLICY, CHECKED BEFORE ANYTHING ELSE COSTS ANYTHING.
        //
        // Creates the environment's row if this is the first write to it, so the policy applies
        // everywhere rather than only where somebody happened to create an environment first.
        // See RegistrationGate for why auto-creating is safe and what it closes.
        var admitted = await RegistrationGate.AdmitAsync(
            environments,
            caller,
            command.EnvironmentId,
            command.EnvironmentName,
            clock,
            cancellationToken).ConfigureAwait(false);

        if (admitted.IsFailure)
        {
            return Result<RegisterVersionResult>.Failure(admitted.Error!);
        }

        var subject = await subjects.FindAsync(command.EnvironmentId, name.Value, cancellationToken)
            .ConfigureAwait(false);

        if (subject is null)
        {
            return Result<RegisterVersionResult>.Failure(
                ConcordatCodes.SubjectNotFound,
                $"No subject '{name.Value}' in this environment. Create it first.");
        }

        SemanticVersion? semver = null;
        if (!string.IsNullOrWhiteSpace(command.SemanticVersion))
        {
            var parsed = SemanticVersion.Create(command.SemanticVersion);
            if (parsed.IsFailure)
            {
                return Result<RegisterVersionResult>.Failure(parsed.Error!);
            }

            // A pre-release label parses everywhere and is admitted per environment (decision 8).
            // Some teams treat a release candidate as a real contract their consumers build
            // against; others want production carrying released labels only. Both are reasonable,
            // which is why this is a setting rather than a rule -- and why refusing the label in
            // the parser, as this did until now, meant the first sort of team could not label a
            // version at all, anywhere.
            //
            // An environment with no row has no policy and refuses, matching the default: the
            // permissive answer has to be something somebody switched on.
            if (parsed.Value.IsPreRelease &&
                admitted.Value.Environment?.AllowPreReleaseVersions is not true)
            {
                return Result<RegisterVersionResult>.Failure(
                    ConcordatCodes.SemverPrereleaseUnsupported,
                    $"'{command.SemanticVersion}' is a pre-release label and environment " +
                    $"'{command.EnvironmentName ?? "(unnamed)"}' does not accept them. Turn it " +
                    "on for this environment, or label the version without the suffix.");
            }

            semver = parsed.Value;
        }

        // Checked before the schema is canonicalised and hashed, which is the expensive part.
        // A tenant at their monthly limit should not pay for the work either.
        var allowed = await billing.MayCreateAsync(Meter.VersionsPerMonth, cancellationToken)
            .ConfigureAwait(false);

        if (!allowed.Allowed)
        {
            return Result<RegisterVersionResult>.Failure(
                ConcordatCodes.PlanLimitReached,
                $"This organisation has registered its plan's limit of {allowed.Limit} versions " +
                "this month. Existing versions keep resolving; only new registrations are held.");
        }

        var priors = await LoadPriorsAsync(subject, cancellationToken).ConfigureAwait(false);
        var policy = subject.EffectivePolicy(CompatibilityPolicy.Default);

        var evaluated = evaluator.Evaluate(
            subject.Format, command.Body, priors, policy, subject.ContentModel);

        if (evaluated.IsFailure)
        {
            return Result<RegisterVersionResult>.Failure(evaluated.Error!);
        }

        var stored = await schemas.AddIfMissingAsync(evaluated.Value.Schema, cancellationToken)
            .ConfigureAwait(false);

        var registered = subject.RegisterVersion(
            stored,
            evaluated.Value.Verdict,
            semver,
            command.Changelog,
            actor,
            clock.GetUtcNow());

        if (registered.IsFailure)
        {
            return Result<RegisterVersionResult>.Failure(registered.Error!);
        }

        Audit(command.EnvironmentId, subject, registered.Value, actor);

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var version = registered.Value.Version;
        return Result<RegisterVersionResult>.Success(new RegisterVersionResult(
            subject.Name.Value,
            version.Ordinal,
            version.SchemaId.Value,
            version.Status,
            registered.Value.Created,
            evaluated.Value.Report,
            evaluated.Value.Portability));
    }

    /// <summary>
    /// Records what the registration actually did (M7.4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An idempotent retry writes nothing.</b> A CI job that reruns its pipeline registers
    /// the same schema again and nothing happens; an entry for it would bury the trail in rows
    /// that describe no change, which is the failure mode that makes an audit log stop being
    /// read.
    /// </para>
    /// <para>
    /// <b>A revert writes one entry, not two.</b> Nothing was registered — the schema was
    /// already the tip — but proposals were abandoned, and that is the event a reviewer needs
    /// to find when a queue empties on its own.
    /// </para>
    /// </remarks>
    private void Audit(
        EnvironmentId environmentId, Subject subject, RegistrationOutcome outcome, ActorId actor)
    {
        var at = clock.GetUtcNow();

        if (outcome.Dismissed.Count > 0)
        {
            audit.Append(AuditEntry.Record(
                environmentId,
                AuditAction.VersionDismissed,
                actor,
                subject.Name.Value,
                at,
                $"version {string.Join(", ", outcome.Dismissed)} abandoned: the submitted " +
                "schema is already the approved tip, so nothing is proposing the change."));
        }

        if (!outcome.Created)
        {
            return;
        }

        var submitted = outcome.Version.Status is VersionStatus.AwaitingApproval;

        audit.Append(AuditEntry.Record(
            environmentId,
            submitted ? AuditAction.VersionSubmitted : AuditAction.VersionRegistered,
            actor,
            subject.Name.Value,
            at,
            $"version {outcome.Version.Ordinal}, schema {outcome.Version.SchemaId}"));

        // Staged, not sent (M7.5). Sending here would mean a notification can go out for a
        // registration that then rolls back, or vanish with a crash after the commit.
        outbox.Stage(OutboxMessage.Stage(
            environmentId,
            submitted
                ? NotificationEvent.BreakingChangeSubmitted
                : NotificationEvent.VersionRegistered,
            subject.Name.Value,
            submitted
                ? $"{subject.Name} version {outcome.Version.Ordinal} is a breaking change and " +
                  $"is waiting for review. Registered by {actor.Value}."
                : $"{subject.Name} version {outcome.Version.Ordinal} was registered by " +
                  $"{actor.Value}.",
            at));
    }

    private async Task<IReadOnlyList<PriorSchema>> LoadPriorsAsync(
        Subject subject, CancellationToken cancellationToken)
    {
        var history = CompatibilityHistory.Of(subject);

        // One round trip for the whole history rather than one per prior version: a subject
        // that has been through a hundred revisions used to mean a hundred queries on every
        // single registration against it, not just its own.
        var bodies = await schemas.FindManyAsync(
            [.. history.Select(v => v.SchemaId).Distinct()], cancellationToken)
            .ConfigureAwait(false);

        return [.. history
            .Where(v => bodies.ContainsKey(v.SchemaId))
            .Select(v => new PriorSchema(v.Ordinal, bodies[v.SchemaId].Body))];
    }
}
