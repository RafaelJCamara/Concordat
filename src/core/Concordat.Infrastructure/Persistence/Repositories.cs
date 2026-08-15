using Concordat.Application.Abstractions;
using Concordat.Domain.Contracts;
using Concordat.Domain.Registry;
using Microsoft.EntityFrameworkCore;

using Environment = Concordat.Domain.Registry.Environment;

namespace Concordat.Infrastructure.Persistence;

/// <inheritdoc />
internal sealed class SubjectRepository(ConcordatDbContext context) : ISubjectRepository
{
    /// <inheritdoc />
    public Task<Subject?> FindAsync(
        EnvironmentId environmentId, SubjectName name, CancellationToken cancellationToken) =>
        context.Subjects.SingleOrDefaultAsync(
            s => s.EnvironmentId == environmentId && s.Name == name, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Subject>> ListAsync(
        EnvironmentId environmentId, CancellationToken cancellationToken) =>
        await context.Subjects
            .Where(s => s.EnvironmentId == environmentId)
            .OrderBy(s => s.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public void Add(Subject subject) => context.Subjects.Add(subject);
}

/// <inheritdoc />
internal sealed class SchemaRepository(ConcordatDbContext context) : ISchemaRepository
{
    /// <inheritdoc />
    public Task<Schema?> FindAsync(SchemaId id, CancellationToken cancellationToken) =>
        context.Schemas.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<SchemaId, Schema>> FindManyAsync(
        IReadOnlyCollection<SchemaId> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);

        // Short-circuits rather than sending `WHERE id IN ()`: EF translates an empty Contains
        // to a query that is always false, which is correct but is still a network round trip
        // for an answer known ahead of time.
        if (ids.Count == 0)
        {
            return new Dictionary<SchemaId, Schema>();
        }

        var distinct = ids as ISet<SchemaId> ?? ids.ToHashSet();

        return await context.Schemas
            .Where(s => distinct.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<bool> IsReachableByCurrentTenantAsync(
        SchemaId id, CancellationToken cancellationToken) =>
        // Deliberately expressed over Subjects, not Schemas: Subjects carries the tenant query
        // filter, so this inherits the isolation the schema table cannot provide itself.
        context.Subjects
            .SelectMany(s => s.Versions)
            .AnyAsync(v => v.SchemaId == id, cancellationToken);

    /// <inheritdoc />
    public async Task<Schema> AddIfMissingAsync(Schema schema, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schema);

        var existing = await FindAsync(schema.Id, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        // Also already tracked when the same content was staged earlier in this unit of work.
        var tracked = context.ChangeTracker.Entries<Schema>()
            .FirstOrDefault(e => e.Entity.Id == schema.Id);

        if (tracked is not null)
        {
            return tracked.Entity;
        }

        context.Schemas.Add(schema);
        return schema;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<(SubjectName Subject, int Version)>> FindUsagesAsync(
        SchemaId id, CancellationToken cancellationToken)
    {
        var rows = await context.Subjects
            .SelectMany(s => s.Versions.Select(v => new { s.Name, v.SchemaId, v.Ordinal }))
            .Where(x => x.SchemaId == id)
            .OrderBy(x => x.Name)
            .ThenBy(x => x.Ordinal)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(r => (r.Name, r.Ordinal))];
    }
}

/// <inheritdoc />
internal sealed class UnitOfWork(ConcordatDbContext context) : IUnitOfWork
{
    /// <inheritdoc />
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) =>
        context.SaveChangesAsync(cancellationToken);
}

/// <inheritdoc />
internal sealed class EnvironmentRepository(ConcordatDbContext context) : IEnvironmentRepository
{
    /// <inheritdoc />
    public Task<Environment?> FindAsync(
        EnvironmentName name, CancellationToken cancellationToken) =>
        context.Environments.SingleOrDefaultAsync(e => e.Name == name, cancellationToken);

    /// <inheritdoc />
    public Task<Environment?> FindAsync(EnvironmentId id, CancellationToken cancellationToken) =>
        context.Environments.SingleOrDefaultAsync(e => e.Id == id, cancellationToken);

    /// <inheritdoc />
    public Task<Environment?> FindAcrossTenantsAsync(
        TenantId tenantId, EnvironmentId id, CancellationToken cancellationToken) =>
        context.Environments
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(
                e => e.Id == id
                    && EF.Property<Guid>(e, EnvironmentConfiguration.TenantIdProperty)
                        == tenantId.Value,
                cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Environment>> ListAsync(CancellationToken cancellationToken) =>
        await context.Environments
            .OrderBy(e => e.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public void Add(Environment environment) => context.Environments.Add(environment);
}

/// <inheritdoc />
internal sealed class ContractRepository(ConcordatDbContext context) : IContractRepository
{
    /// <inheritdoc />
    public Task<Contract?> FindAsync(
        EnvironmentId environmentId, string name, CancellationToken cancellationToken) =>
        context.Contracts.SingleOrDefaultAsync(
            c => c.EnvironmentId == environmentId && c.Name == name, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Contract>> ListAsync(
        EnvironmentId environmentId, CancellationToken cancellationToken) =>
        await context.Contracts
            .Where(c => c.EnvironmentId == environmentId)
            .OrderBy(c => c.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public void Add(Contract contract) => context.Contracts.Add(contract);
}
