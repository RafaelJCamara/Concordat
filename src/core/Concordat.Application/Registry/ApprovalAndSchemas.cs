using Concordat.Application.Abstractions;
using Concordat.Domain.Governance;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;

namespace Concordat.Application.Registry;

/// <summary>Approves or rejects a version awaiting review (ADR-017).</summary>
/// <param name="EnvironmentId">The environment.</param>
/// <param name="SubjectName">The subject.</param>
/// <param name="Ordinal">The version.</param>
/// <param name="DecidedBy">
/// Informational only — accepted from the request body but never trusted as the audit
/// identity, which is always <c>ICallerContext.Current.Actor</c>. A client-supplied string is
/// attacker-assertable; the authenticated caller is not.
/// </param>
/// <param name="Approve"><see langword="true"/> to approve, <see langword="false"/> to reject.</param>
public sealed record DecideVersionCommand(
    EnvironmentId EnvironmentId,
    string SubjectName,
    int Ordinal,
    string DecidedBy,
    bool Approve) : ICommand<Subject>;

/// <summary>Handles <see cref="DecideVersionCommand"/>.</summary>
public sealed class DecideVersionHandler(
    ISubjectRepository subjects,
    ICallerContext caller,
    IAuditLog audit,
    IOutbox outbox,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
    : ICommandHandler<DecideVersionCommand, Subject>
{
    /// <inheritdoc />
    public async Task<Result<Subject>> HandleAsync(
        DecideVersionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var name = SubjectName.Create(command.SubjectName);
        if (name.IsFailure)
        {
            return Result<Subject>.Failure(name.Error!);
        }

        var actor = caller.Current.Actor;

        var subject = await subjects.FindAsync(command.EnvironmentId, name.Value, cancellationToken)
            .ConfigureAwait(false);

        if (subject is null)
        {
            return Result<Subject>.Failure(
                ConcordatCodes.SubjectNotFound,
                $"No subject '{name.Value}' in this environment.");
        }

        var decision = command.Approve
            ? subject.Approve(command.Ordinal, actor, clock.GetUtcNow())
            : subject.Reject(command.Ordinal, actor, clock.GetUtcNow());

        if (decision.IsFailure)
        {
            return Result<Subject>.Failure(decision.Error!);
        }

        var at = clock.GetUtcNow();

        audit.Append(AuditEntry.Record(
            command.EnvironmentId,
            command.Approve ? AuditAction.VersionApproved : AuditAction.VersionRejected,
            actor,
            name.Value.Value,
            at,
            $"version {command.Ordinal}"));

        outbox.Stage(OutboxMessage.Stage(
            command.EnvironmentId,
            command.Approve
                ? NotificationEvent.BreakingChangeApproved
                : NotificationEvent.BreakingChangeRejected,
            name.Value.Value,
            $"{name.Value} version {command.Ordinal} was " +
            $"{(command.Approve ? "approved" : "rejected")} by {actor.Value}.",
            at));

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result<Subject>.Success(subject);
    }
}

/// <summary>Fetches a schema by its content-addressed id.</summary>
/// <param name="SchemaId">The id.</param>
public sealed record GetSchemaQuery(string SchemaId) : IQuery<Schema>;

/// <summary>Handles <see cref="GetSchemaQuery"/>.</summary>
/// <remarks>
/// <para>
/// <b>This is where the global schema table's authorisation obligation is discharged.</b>
/// Schemas are stored once for everyone (ADR-015), so there is no tenant column to filter on
/// and the query filter that protects subjects does nothing here.
/// </para>
/// <para>
/// A caller may read a schema only if some subject in their tenant references it. Without
/// that check, anyone who can guess or observe a 128-bit id reads any tenant's schema — and
/// the naive implementation, a plain lookup by primary key, looks completely correct in
/// review.
/// </para>
/// <para>
/// Unreachable returns the same <c>schema_not_found</c> as genuinely absent. Distinguishing
/// them would confirm the existence of another tenant's schema.
/// </para>
/// </remarks>
public sealed class GetSchemaHandler(ISchemaRepository schemas)
    : IQueryHandler<GetSchemaQuery, Schema>
{
    /// <inheritdoc />
    public async Task<Result<Schema>> HandleAsync(
        GetSchemaQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var id = SchemaId.Create(query.SchemaId);
        if (id.IsFailure)
        {
            return Result<Schema>.Failure(id.Error!);
        }

        var reachable = await schemas
            .IsReachableByCurrentTenantAsync(id.Value, cancellationToken)
            .ConfigureAwait(false);

        if (!reachable)
        {
            return NotFound(id.Value);
        }

        var schema = await schemas.FindAsync(id.Value, cancellationToken).ConfigureAwait(false);
        return schema is null ? NotFound(id.Value) : Result<Schema>.Success(schema);
    }

    private static Result<Schema> NotFound(SchemaId id) =>
        Result<Schema>.Failure(ConcordatCodes.SchemaNotFound, $"No schema '{id.Value}'.");
}

/// <summary>Lists the subjects and versions using a schema, within the caller's tenant.</summary>
/// <param name="SchemaId">The id.</param>
public sealed record GetSchemaUsagesQuery(string SchemaId)
    : IQuery<IReadOnlyList<SchemaUsage>>;

/// <summary>One place a schema is in use.</summary>
/// <param name="Subject">The subject name.</param>
/// <param name="Version">The version ordinal.</param>
public sealed record SchemaUsage(string Subject, int Version);

/// <summary>Handles <see cref="GetSchemaUsagesQuery"/>.</summary>
public sealed class GetSchemaUsagesHandler(ISchemaRepository schemas)
    : IQueryHandler<GetSchemaUsagesQuery, IReadOnlyList<SchemaUsage>>
{
    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<SchemaUsage>>> HandleAsync(
        GetSchemaUsagesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var id = SchemaId.Create(query.SchemaId);
        if (id.IsFailure)
        {
            return Result<IReadOnlyList<SchemaUsage>>.Failure(id.Error!);
        }

        // Naturally tenant-scoped: it walks subjects, which the query filter protects.
        var usages = await schemas.FindUsagesAsync(id.Value, cancellationToken).ConfigureAwait(false);

        return Result<IReadOnlyList<SchemaUsage>>.Success(
            [.. usages.Select(u => new SchemaUsage(u.Subject.Value, u.Version))]);
    }
}
