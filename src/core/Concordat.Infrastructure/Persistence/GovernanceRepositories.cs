using Concordat.Application.Abstractions;
using Concordat.Domain.Governance;
using Concordat.Domain.Registry;
using Microsoft.EntityFrameworkCore;

namespace Concordat.Infrastructure.Persistence;

/// <inheritdoc />
internal sealed class ServiceRegistrationRepository(ConcordatDbContext context)
    : IServiceRegistrationRepository
{
    /// <inheritdoc />
    public Task<ServiceRegistration?> FindAsync(
        EnvironmentId environmentId, string name, CancellationToken cancellationToken) =>
        context.Services.SingleOrDefaultAsync(
            s => s.EnvironmentId == environmentId && s.Name == name, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ServiceRegistration>> ListAsync(
        EnvironmentId environmentId, CancellationToken cancellationToken) =>
        await context.Services
            .Where(s => s.EnvironmentId == environmentId)
            .OrderBy(s => s.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public void Add(ServiceRegistration registration) => context.Services.Add(registration);
}

/// <inheritdoc />
/// <remarks>
/// <see cref="Append"/> stages onto the same change tracker as everything else, so the entry is
/// written by whichever <c>SaveChangesAsync</c> commits the change it describes. That is the
/// whole design: there is no second transaction that could succeed or fail independently.
/// </remarks>
internal sealed class AuditLog(ConcordatDbContext context) : IAuditLog
{
    /// <inheritdoc />
    public void Append(AuditEntry entry) => context.AuditEntries.Add(entry);

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuditEntry>> QueryAsync(
        AuditFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var query = context.AuditEntries.AsNoTracking().AsQueryable();

        if (filter.EnvironmentId is { } environmentId)
        {
            query = query.Where(e => e.EnvironmentId == environmentId);
        }

        if (filter.Action is { } action)
        {
            query = query.Where(e => e.Action == action);
        }

        if (filter.Target is { } target)
        {
            query = query.Where(e => e.Target == target);
        }

        if (filter.Since is { } since)
        {
            query = query.Where(e => e.At >= since);
        }

        if (filter.Until is { } until)
        {
            query = query.Where(e => e.At <= until);
        }

        // Id breaks ties rather than leaving them to the database. Entries written by one
        // SaveChanges share a timestamp to the microsecond, and an unstable order would make
        // two identical requests disagree about what happened.
        return await query
            .OrderByDescending(e => e.At)
            .ThenByDescending(e => e.Id)
            .Take(filter.Limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
