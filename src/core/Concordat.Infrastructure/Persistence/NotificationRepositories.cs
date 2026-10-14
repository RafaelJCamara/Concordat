using Concordat.Application.Abstractions;
using Concordat.Domain.Governance;
using Concordat.Domain.Registry;
using Microsoft.EntityFrameworkCore;

namespace Concordat.Infrastructure.Persistence;

/// <inheritdoc />
internal sealed class Outbox(ConcordatDbContext context) : IOutbox
{
    // Every event this build can parse. EF turns each into its stored token, so the claim's filter
    // is `event IN (...)`: the events a build knows are enumerated, not the ones it does not.
    private static readonly NotificationEvent[] KnownEvents = Enum.GetValues<NotificationEvent>();

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
    /// <para>
    /// <b>A row this build cannot read is not claimed, rather than thrown over.</b> The stored
    /// event is a token, parsed on materialisation, and a build that is older than the one that
    /// wrote it (a rollback, or an old instance beside a new one) used to throw while reading the
    /// batch. That failed every pass for every tenant, nothing marked the row delivered, and the
    /// registry went on accepting changes while telling nobody anything. Filtering on the tokens
    /// this build knows keeps the row out of the batch, and out of the <c>Take</c>, in SQL. It is
    /// left exactly as it is: still pending, still counted by <see cref="DepthAsync"/>, delivered
    /// by whichever build can read it.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<(TenantId TenantId, OutboxMessage Message)>>
        ClaimDueAcrossTenantsAsync(
            DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
    {
        var known = KnownEvents;

        var rows = await context.Outbox
            .IgnoreQueryFilters()
            .Where(m => m.DeliveredAt == null && !m.Parked && m.NextAttemptAt <= now
                && known.Contains(m.Event))
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
