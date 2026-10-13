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

        if (!string.IsNullOrWhiteSpace(command.Owner))
        {
            var owner = ActorId.Create(command.Owner);
            if (owner.IsFailure)
            {
                return Result<Subject>.Failure(owner.Error!);
            }

            var changed = subject.Value.ChangeOwner(owner.Value);
            if (changed.IsFailure)
            {
                return Result<Subject>.Failure(changed.Error!);
            }
        }

        if (command.Deprecate)
        {
            var deprecated = subject.Value.Deprecate();
            if (deprecated.IsFailure)
            {
                return Result<Subject>.Failure(deprecated.Error!);
            }
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
            command.Deprecate ? "deprecated" : "owner changed"));

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

/// <summary>Retires a subject. This is the soft delete and is terminal.</summary>
/// <param name="EnvironmentId">The environment.</param>
/// <param name="SubjectName">The subject.</param>
public sealed record RetireSubjectCommand(EnvironmentId EnvironmentId, string SubjectName)
    : ICommand<Subject>;

/// <summary>Handles <see cref="RetireSubjectCommand"/>.</summary>
/// <remarks>
/// <b>There is no hard delete.</b> Schemas are never removed (ADR-015), and a subject moves to
/// <see cref="SubjectLifecycle.Retired"/> instead of disappearing. Hard delete needs the
/// registered-consumer check and the audit entry DESIGN §4 requires, both of which are M7.
/// </remarks>
public sealed class RetireSubjectHandler(
    ISubjectRepository subjects, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
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
            subject.Value.Owner,
            subject.Value.Name.Value,
            clock.GetUtcNow()));

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return subject;
    }
}
