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
    /// Tracked, not <c>AsNoTracking</c>: the pump mutates what it claims and saves it back
    /// through the same unit of work.
    /// </remarks>
    public async Task<IReadOnlyList<OutboxMessage>> ClaimDueAsync(
        DateTimeOffset now, int batchSize, CancellationToken cancellationToken) =>
        await context.Outbox
            .Where(m => m.DeliveredAt == null && !m.Parked && m.NextAttemptAt <= now)
            .OrderBy(m => m.OccurredAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

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
