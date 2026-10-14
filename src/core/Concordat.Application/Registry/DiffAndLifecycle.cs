using Concordat.Application.Abstractions;
using Concordat.Domain.Governance;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;
using Concordat.Formats.Abstractions;

namespace Concordat.Application.Registry;

/// <summary>Compares two registered versions of a subject.</summary>
/// <param name="EnvironmentId">The environment.</param>
/// <param name="SubjectName">The subject.</param>
/// <param name="From">The earlier version ordinal.</param>
/// <param name="To">The later version ordinal.</param>
public sealed record DiffVersionsQuery(
    EnvironmentId EnvironmentId, string SubjectName, int From, int To) : IQuery<DiffResult>;

/// <summary>The difference between two versions.</summary>
/// <param name="From">The earlier ordinal.</param>
/// <param name="To">The later ordinal.</param>
/// <param name="FromSchemaId">The earlier schema id.</param>
/// <param name="ToSchemaId">The later schema id.</param>
/// <param name="Identical">Whether both ordinals point at the same content.</param>
/// <param name="Policy">The policy the comparison was evaluated under.</param>
/// <param name="Divergences">Every difference, whether or not the policy tolerates it.</param>
public sealed record DiffResult(
    int From,
    int To,
    string FromSchemaId,
    string ToSchemaId,
    bool Identical,
    CompatibilityPolicy Policy,
    IReadOnlyList<BreakingChange> Divergences);

/// <summary>Handles <see cref="DiffVersionsQuery"/>.</summary>
/// <remarks>
/// Reports <em>every</em> divergence, not only policy violations. A diff exists to answer
/// "what actually changed", and filtering it by the current policy would hide differences
/// that matter to a reader whose consumer generates code.
/// </remarks>
public sealed class DiffVersionsHandler(
    ISubjectRepository subjects,
    ISchemaRepository schemas,
    IEnvironmentRepository environments,
    ISchemaFormatRegistry formats)
    : IQueryHandler<DiffVersionsQuery, DiffResult>
{
    /// <inheritdoc />
    public async Task<Result<DiffResult>> HandleAsync(
        DiffVersionsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var name = SubjectName.Create(query.SubjectName);
        if (name.IsFailure)
        {
            return Result<DiffResult>.Failure(name.Error!);
        }

        var subject = await subjects.FindAsync(query.EnvironmentId, name.Value, cancellationToken)
            .ConfigureAwait(false);

        if (subject is null)
        {
            return Result<DiffResult>.Failure(
                ConcordatCodes.SubjectNotFound, $"No subject '{name.Value}' in this environment.");
        }

        var from = await LoadAsync(subject, query.From, cancellationToken).ConfigureAwait(false);
        if (from.IsFailure)
        {
            return Result<DiffResult>.Failure(from.Error!);
        }

        var to = await LoadAsync(subject, query.To, cancellationToken).ConfigureAwait(false);
        if (to.IsFailure)
        {
            return Result<DiffResult>.Failure(to.Error!);
        }

        // The policy that governs the subject, which for one with none of its own is its
        // environment's default (not the built-in), so the diff names the same rules the
        // registration did. Only reported here -- the comparison below runs under Full.
        var environment = await environments.FindAsync(query.EnvironmentId, cancellationToken)
            .ConfigureAwait(false);

        var policy = EnvironmentPolicies.EffectiveFor(subject, environment);

        // Full, so both directions are reported. A diff filtered to one direction would answer
        // a different question than the one the caller asked.
        var report = formats.Checker(subject.Format).Check(
            to.Value.Body,
            [new PriorSchema(query.From, from.Value.Body)],
            policy with { Mode = CompatibilityMode.Full },
            subject.ContentModel);

        return Result<DiffResult>.Success(new DiffResult(
            query.From,
            query.To,
            from.Value.Id.Value,
            to.Value.Id.Value,
            from.Value.Id == to.Value.Id,
            policy,
            report.AllDivergences));
    }

    private async Task<Result<Schema>> LoadAsync(
        Subject subject, int ordinal, CancellationToken cancellationToken)
    {
        var version = subject.Versions.FirstOrDefault(v => v.Ordinal == ordinal);
        if (version is null)
        {
            return Result<Schema>.Failure(
                ConcordatCodes.VersionNotFound,
                $"Subject '{subject.Name}' has no version {ordinal}.");
        }

        var schema = await schemas.FindAsync(version.SchemaId, cancellationToken)
            .ConfigureAwait(false);

        return schema is null
            ? Result<Schema>.Failure(
                ConcordatCodes.SchemaNotFound,
                $"Version {ordinal} points at a schema that is not stored.")
            : Result<Schema>.Success(schema);
    }
}

/// <summary>Changes a subject's owner or lifecycle.</summary>
/// <param name="EnvironmentId">The environment.</param>
/// <param name="SubjectName">The subject.</param>
/// <param name="Owner">A new owner, or <see langword="null"/> to leave it.</param>
/// <param name="Deprecate">
/// <see langword="true"/> to mark deprecated. Deprecation is advisory and does not stop
/// registration; retiring does.
/// </param>
public sealed record UpdateSubjectCommand(
    EnvironmentId EnvironmentId, string SubjectName, string? Owner, bool Deprecate)
    : ICommand<Subject>;

/// <summary>Handles <see cref="UpdateSubjectCommand"/>.</summary>
public sealed class UpdateSubjectHandler(
    ISubjectRepository subjects,
    ICallerContext caller,
    IAuditLog audit,
    IOutbox outbox,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
    : ICommandHandler<UpdateSubjectCommand, Subject>
{
    /// <inheritdoc />
    public async Task<Result<Subject>> HandleAsync(
        UpdateSubjectCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var subject = await Find(subjects, command.EnvironmentId, command.SubjectName, cancellationToken)
            .ConfigureAwait(false);

        if (subject.IsFailure)
        {
            return subject;
        }

        string? ownerChange = null;

        if (!string.IsNullOrWhiteSpace(command.Owner))
        {
            var owner = ActorId.Create(command.Owner);
            if (owner.IsFailure)
            {
                return Result<Subject>.Failure(owner.Error!);
            }

            var previous = subject.Value.Owner;

            var changed = subject.Value.ChangeOwner(owner.Value);
            if (changed.IsFailure)
            {
                return Result<Subject>.Failure(changed.Error!);
            }

            // Both owners, as stored (the new one after the registry's own trim). Who owned a
            // contract before, and who took it, is the first question after an incident, and an
            // owner is not a secret: any reader sees it on the subject. The row used to say only
            // "owner changed" (ADR-033).
            ownerChange = $"owner {previous.Value} -> {owner.Value.Value}";
        }

        if (command.Deprecate)
        {
            var deprecated = subject.Value.Deprecate();
            if (deprecated.IsFailure)
            {
                return Result<Subject>.Failure(deprecated.Error!);
            }
        }

        // A request that did neither (an empty body, a null or blank owner) changed nothing, so
        // there is nothing to record, save or notify: it is answered as the success it always was,
        // with the subject as it stands. It used to write a row reading "owner changed" for a
        // change nobody made, which tells whoever reads the trail for an ownership change that one
        // happened. The rule a policy change and an idempotent registration already follow. An
        // *explicit* owner is a request even when it is the one already held, and is recorded as
        // the request it was (`owner a -> a`).
        if (ownerChange is null && !command.Deprecate)
        {
            return subject;
        }

        // The authenticated caller, as every handler that changes something records. This row
        // used to name the *new owner* as its actor ("unknown" when none was given), so an owner
        // change was attributed to whoever was being made responsible; and a PATCH whose owner
        // was only whitespace answered 500, because the actor was built from the very text the
        // owner rule had just declined to apply. The command carries no actor on purpose (a
        // client-supplied name is attacker-assertable, see `DecideVersionCommand`); the caller
        // is the one who made the change.
        audit.Append(AuditEntry.Record(
            command.EnvironmentId,
            AuditAction.SubjectUpdated,
            caller.Current.Actor,
            subject.Value.Name.Value,
            clock.GetUtcNow(),
            Describe(ownerChange, command.Deprecate)));

        if (command.Deprecate)
        {
            // Only deprecation is notified. An ownership change is worth auditing and not worth
            // interrupting anyone about; deprecation is a signal to every team consuming it.
            outbox.Stage(OutboxMessage.Stage(
                command.EnvironmentId,
                NotificationEvent.SubjectDeprecated,
                subject.Value.Name.Value,
                $"{subject.Value.Name} was deprecated. It still accepts versions, but consumers " +
                "should plan to move off it.",
                clock.GetUtcNow()));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return subject;
    }

    // What the request did, in one line. Only reached when it did something: a request that
    // changed neither the owner nor the lifecycle returned before any row was built, so the
    // remaining case (no owner change) is the deprecation.
    private static string Describe(string? ownerChange, bool deprecate) =>
        (ownerChange, deprecate) switch
        {
            ({ } change, true) => $"{change}; deprecated",
            ({ } change, false) => change,
            _ => "deprecated",
        };

    internal static async Task<Result<Subject>> Find(
        ISubjectRepository subjects,
        EnvironmentId environmentId,
        string subjectName,
        CancellationToken cancellationToken)
    {
        var name = SubjectName.Create(subjectName);
        if (name.IsFailure)
        {
            return Result<Subject>.Failure(name.Error!);
        }

        var subject = await subjects.FindAsync(environmentId, name.Value, cancellationToken)
            .ConfigureAwait(false);

        return subject is null
            ? Result<Subject>.Failure(
                ConcordatCodes.SubjectNotFound, $"No subject '{name.Value}' in this environment.")
            : Result<Subject>.Success(subject);
    }
}

/// <summary>Sets a subject's compatibility policy, or clears it so the subject inherits.</summary>
/// <param name="EnvironmentId">The environment.</param>
/// <param name="SubjectName">The subject.</param>
/// <param name="Policy">The new policy, or <see langword="null"/> to inherit.</param>
public sealed record SetCompatibilityPolicyCommand(
    EnvironmentId EnvironmentId, string SubjectName, CompatibilityPolicy? Policy)
    : ICommand<Subject>;

/// <summary>Handles <see cref="SetCompatibilityPolicyCommand"/>.</summary>
/// <remarks>
/// <para>
/// <b>A command handler, so that the audit entry and the save are one unit of work.</b> This was the
/// endpoint's own body (load the subject, set the policy, save), with no <c>IAuditLog</c> in reach,
/// so changing what the registry will let through left no trace while the owner change beside it
/// did. A row saved separately can outlive a rolled-back change or be lost while the change
/// survives; this one rides the same <c>SaveChanges</c> as the change (ADR-033).
/// </para>
/// <para>
/// <b>Attributed to the authenticated caller</b>, never to anything in the request: the command
/// carries no actor on purpose, for the reason <see cref="UpdateSubjectHandler"/> spells out.
/// </para>
/// <para>
/// <b>A change that changes nothing writes nothing.</b> Setting the policy a subject already has
/// (including inherit on one that inherits) is answered as before, and the trail is not told that
/// something happened which did not. The same rule an idempotent registration follows.
/// </para>
/// <para>
/// <b>Subscribers are notified, in the same unit of work and under the same guard.</b> ADR-033
/// held a policy-change event back as a decision about who is told; ADR-034 is that decision. A
/// team reading a subject that became laxer (a breaking change now goes live without a reviewer)
/// or stricter (a change that used to go straight in is now held) is exactly who an alert is for.
/// The event is <see cref="NotificationEvent.SubjectPolicyChanged"/>, spelled as the audit action
/// is, and a subscription with no event filter wants it, because an empty filter means every
/// event: a behaviour change for every such subscription the day this ships, written down in the
/// ADR for the operator to read first.
/// </para>
/// </remarks>
public sealed class SetCompatibilityPolicyHandler(
    ISubjectRepository subjects,
    ICallerContext caller,
    IAuditLog audit,
    IOutbox outbox,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
    : ICommandHandler<SetCompatibilityPolicyCommand, Subject>
{
    /// <inheritdoc />
    public async Task<Result<Subject>> HandleAsync(
        SetCompatibilityPolicyCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var subject = await UpdateSubjectHandler
            .Find(subjects, command.EnvironmentId, command.SubjectName, cancellationToken)
            .ConfigureAwait(false);

        if (subject.IsFailure)
        {
            return subject;
        }

        var previous = subject.Value.CompatibilityPolicy;

        // Refuses a retired subject, which is why it is asked before anything is recorded: a
        // refused request never reaches a commit, so there is no entry to be wrong.
        var applied = subject.Value.SetCompatibilityPolicy(command.Policy);
        if (applied.IsFailure)
        {
            return Result<Subject>.Failure(applied.Error!);
        }

        if (previous != command.Policy)
        {
            // Old and new, which is what answers "who made this stricter". Nothing here is
            // private: a policy is two enum tokens, and the subject page shows it to any reader.
            var change = $"{Describe(previous)} -> {Describe(command.Policy)}";

            audit.Append(AuditEntry.Record(
                command.EnvironmentId,
                AuditAction.SubjectPolicyChanged,
                caller.Current.Actor,
                subject.Value.Name.Value,
                clock.GetUtcNow(),
                $"policy {change}"));

            // Under the same guard as the entry, so a no-op and a refusal tell nobody, and in the
            // same unit of work, so there is no alert for a change that rolled back. It names the
            // caller, as the approval, rejection, promotion and breaking-registration messages do
            // ("... by carol"): a team told its contract is now judged differently asks who, and a
            // mailbox on the receiving end cannot read the audit log. The deprecation message is
            // the one that names nobody, and it is advisory; a rule change is not. The actor is the
            // authenticated caller, never anything in the request (ADR-034).
            outbox.Stage(OutboxMessage.Stage(
                command.EnvironmentId,
                NotificationEvent.SubjectPolicyChanged,
                subject.Value.Name.Value,
                $"The compatibility policy of {subject.Value.Name} was changed by " +
                $"{caller.Current.Actor.Value}: {change}. " +
                "(\"inherited\" means the environment's default applies.) " +
                "Versions already registered are not judged again.",
                clock.GetUtcNow()));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return subject;
    }

    // MODE/SURFACE, the spelling ENVIRONMENT_UPDATED already uses for an environment's default,
    // or "inherited" for no policy of the subject's own.
    private static string Describe(CompatibilityPolicy? policy) =>
        policy is { } set
            ? $"{WireTokens.For(set.Mode)}/{WireTokens.For(set.Surface)}"
            : "inherited";
}

/// <summary>Retires a subject. This is the soft delete and is terminal.</summary>
/// <param name="EnvironmentId">The environment.</param>
/// <param name="SubjectName">The subject.</param>
public sealed record RetireSubjectCommand(EnvironmentId EnvironmentId, string SubjectName)
    : ICommand<Subject>;

/// <summary>Handles <see cref="RetireSubjectCommand"/>.</summary>
/// <remarks>
/// <para>
/// <b>There is no hard delete.</b> Schemas are never removed (ADR-015), and a subject moves to
/// <see cref="SubjectLifecycle.Retired"/> instead of disappearing. Hard delete needs the
/// registered-consumer check and the audit entry DESIGN §4 requires, both of which are M7.
/// </para>
/// <para>
/// <b>Attributed to the authenticated caller, never to the subject's owner.</b> The entry named
/// the owner as the actor, so retiring someone else's contract, the one change that cannot be
/// undone, was recorded as their doing. The command carries no actor on purpose, for the reason
/// <see cref="UpdateSubjectHandler"/> spells out.
/// </para>
/// </remarks>
public sealed class RetireSubjectHandler(
    ISubjectRepository subjects,
    ICallerContext caller,
    IAuditLog audit,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
    : ICommandHandler<RetireSubjectCommand, Subject>
{
    /// <inheritdoc />
    public async Task<Result<Subject>> HandleAsync(
        RetireSubjectCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var subject = await UpdateSubjectHandler
            .Find(subjects, command.EnvironmentId, command.SubjectName, cancellationToken)
            .ConfigureAwait(false);

        if (subject.IsFailure)
        {
            return subject;
        }

        var retired = subject.Value.Retire();
        if (retired.IsFailure)
        {
            return Result<Subject>.Failure(retired.Error!);
        }

        audit.Append(AuditEntry.Record(
            command.EnvironmentId,
            AuditAction.SubjectRetired,
            caller.Current.Actor,
            subject.Value.Name.Value,
            clock.GetUtcNow()));

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return subject;
    }
}
