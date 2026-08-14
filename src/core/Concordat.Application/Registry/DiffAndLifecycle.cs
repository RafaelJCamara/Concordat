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
    ISubjectRepository subjects, ISchemaRepository schemas, ISchemaFormatRegistry formats)
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

        var policy = subject.EffectivePolicy(CompatibilityPolicy.Default);

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
    ISubjectRepository subjects, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
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

        // "unknown" rather than a fabricated identity: the command carries no actor, and M8 is
        // where one starts existing. An audit row that names the wrong person is worse than one
        // that admits it does not know.
        audit.Append(AuditEntry.Record(
            command.EnvironmentId,
            AuditAction.SubjectUpdated,
            ActorId.Create(command.Owner is { Length: > 0 } ? command.Owner : "unknown").Value,
            subject.Value.Name.Value,
            clock.GetUtcNow(),
            command.Deprecate ? "deprecated" : "owner changed"));

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
