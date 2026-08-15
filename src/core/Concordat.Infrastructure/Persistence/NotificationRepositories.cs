using Concordat.Application.Abstractions;
using Concordat.Domain.Governance;
using Concordat.Domain.Registry;
using Microsoft.EntityFrameworkCore;

namespace Concordat.Infrastructure.Persistence;

/// <inheritdoc />
internal sealed class Outbox(ConcordatDbContext context) : IOutbox
{
    /// <inheritdoc />
    public void Stage(OutboxMessage message) => context.Outbox.Add(message);

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <c>IgnoreQueryFilters</c>, deliberately: the pump runs with no request and therefore no
    /// tenant, and a tenant-scoped query here would silently drain only whichever tenant an
    /// unauthenticated caller resolves to — every other tenant's outbox would back up forever
    /// with nothing ever failing loudly (M9.5).
    /// </para>
    /// <para>
    /// Tracked, not <c>AsNoTracking</c>: the pump mutates what it claims and saves it back
    /// through the same unit of work.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<(TenantId TenantId, OutboxMessage Message)>>
        ClaimDueAcrossTenantsAsync(
            DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
    {
        var rows = await context.Outbox
            .IgnoreQueryFilters()
            .Where(m => m.DeliveredAt == null && !m.Parked && m.NextAttemptAt <= now)
            .OrderBy(m => m.OccurredAt)
            .Take(batchSize)
            .Select(m => new
            {
                TenantId = EF.Property<Guid>(m, OutboxMessageConfiguration.TenantIdProperty),
                Message = m,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(r => (new TenantId(r.TenantId), r.Message))];
    }

    /// <inheritdoc />
    public async Task<(int Pending, int Parked)> DepthAsync(CancellationToken cancellationToken)
    {
        var pending = await context.Outbox
            .CountAsync(m => m.DeliveredAt == null && !m.Parked, cancellationToken)
            .ConfigureAwait(false);

        var parked = await context.Outbox
            .CountAsync(m => m.Parked, cancellationToken)
            .ConfigureAwait(false);

        return (pending, parked);
    }
}

/// <inheritdoc />
internal sealed class SubscriptionRepository(ConcordatDbContext context) : ISubscriptionRepository
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<NotificationSubscription>> ListAsync(
        EnvironmentId environmentId, CancellationToken cancellationToken) =>
        await context.Subscriptions
            .Where(s => s.EnvironmentId == environmentId)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<NotificationSubscription>> ListAcrossTenantsAsync(
        TenantId tenantId, EnvironmentId environmentId, CancellationToken cancellationToken) =>
        await context.Subscriptions
            .IgnoreQueryFilters()
            .Where(s => s.EnvironmentId == environmentId
                && EF.Property<Guid>(s, NotificationSubscriptionConfiguration.TenantIdProperty)
                    == tenantId.Value)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public Task<NotificationSubscription?> FindAsync(
        EnvironmentId environmentId, Guid id, CancellationToken cancellationToken) =>
        context.Subscriptions.SingleOrDefaultAsync(
            s => s.EnvironmentId == environmentId && s.Id == id, cancellationToken);

    /// <inheritdoc />
    public void Add(NotificationSubscription subscription) =>
        context.Subscriptions.Add(subscription);

    /// <inheritdoc />
    public void Remove(NotificationSubscription subscription) =>
        context.Subscriptions.Remove(subscription);
}
