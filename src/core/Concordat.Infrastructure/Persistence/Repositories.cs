using Concordat.Application.Abstractions;
using Concordat.Domain.Registry;
using Microsoft.EntityFrameworkCore;

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
