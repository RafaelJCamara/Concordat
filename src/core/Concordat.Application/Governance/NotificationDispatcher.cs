using Concordat.Application.Abstractions;
using Concordat.Domain.Governance;
using Concordat.Domain.Registry;
using Microsoft.Extensions.Logging;

namespace Concordat.Application.Governance;

/// <summary>What one pump pass did.</summary>
/// <param name="Claimed">How many messages were due.</param>
/// <param name="Delivered">How many were delivered to at least one subscriber.</param>
/// <param name="Failed">How many failed and will be retried.</param>
/// <param name="Parked">How many exhausted their attempts on this pass.</param>
public sealed record DispatchResult(int Claimed, int Delivered, int Failed, int Parked);

/// <summary>
/// Delivers what the outbox has staged (M7.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>A message with no matching subscription is delivered, not left pending.</b> Nobody
/// subscribing is a legitimate configuration, and treating it as an undelivered message would
/// grow the outbox forever in the common case where an environment has no notifications set up
/// at all.
/// </para>
/// <para>
/// <b>Partial success counts as success.</b> When two subscribers want a message and one
/// endpoint is down, retrying re-delivers to the healthy one — so the choice is between
/// duplicates for the working subscriber and silence for the broken one. Duplicates win: the
/// contract with receivers is at-least-once, and the message id is there to deduplicate on.
/// The failure is still recorded against the message.
/// </para>
/// </remarks>
public sealed class NotificationDispatcher(
    IOutbox outbox,
    ISubscriptionRepository subscriptions,
    IEnvironmentRepository environments,
    IEnumerable<INotificationChannel> channels,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<NotificationDispatcher> logger)
{
    /// <summary>The most messages one pass will attempt.</summary>
    /// <remarks>
    /// Bounded so a backlog is worked through steadily rather than in one pass that holds a
    /// transaction open for minutes and re-sends everything if it dies halfway.
    /// </remarks>
    public const int BatchSize = 50;

    /// <summary>Delivers one batch.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What the pass did.</returns>
    public async Task<DispatchResult> PumpAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        var due = await outbox.ClaimDueAsync(now, BatchSize, cancellationToken)
            .ConfigureAwait(false);

        if (due.Count is 0)
        {
            return new DispatchResult(0, 0, 0, 0);
        }

        var delivered = 0;
        var failed = 0;
        var parked = 0;

        // Subscriptions are read once per environment per pass, not once per message. A batch
        // is usually one environment's worth of activity, and re-reading per message turns a
        // 50-message batch into 50 identical queries.
        var byEnvironment = new Dictionary<EnvironmentId, IReadOnlyList<NotificationSubscription>>();

        foreach (var message in due)
        {
            if (!byEnvironment.TryGetValue(message.EnvironmentId, out var subscribers))
            {
                subscribers = await subscriptions
                    .ListAsync(message.EnvironmentId, cancellationToken).ConfigureAwait(false);

                byEnvironment[message.EnvironmentId] = subscribers;
            }

            var wanted = subscribers.Where(s => s.Wants(message.Event)).ToList();

            if (wanted.Count is 0)
            {
                message.MarkDelivered(clock.GetUtcNow());
                delivered++;
                continue;
            }

            var error = await DeliverAsync(message, wanted, cancellationToken).ConfigureAwait(false);

            if (error is null)
            {
                message.MarkDelivered(clock.GetUtcNow());
                delivered++;
                continue;
            }

            message.MarkFailed(clock.GetUtcNow(), error);
            failed++;

            if (message.Parked)
            {
                parked++;

                logger.LogWarning(
                    "Notification {MessageId} for {Event} on {Target} was parked after " +
                    "{Attempts} attempts: {Error}",
                    message.Id,
                    NotificationTokens.For(message.Event),
                    message.Target,
                    message.Attempts,
                    error);
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new DispatchResult(due.Count, delivered, failed, parked);
    }

    private async Task<string?> DeliverAsync(
        OutboxMessage message,
        IReadOnlyList<NotificationSubscription> wanted,
        CancellationToken cancellationToken)
    {
        var environment = await environments
            .FindAsync(message.EnvironmentId, cancellationToken).ConfigureAwait(false);

        var notification = new Notification(
            message.Id,
            NotificationTokens.For(message.Event),
            environment?.Name.Value ?? message.EnvironmentId.ToString(),
            message.Target,
            message.Body,
            message.OccurredAt);

        string? firstError = null;

        foreach (var subscription in wanted)
        {
            var channel = channels.FirstOrDefault(c => c.Channel == subscription.Channel);

            if (channel is null)
            {
                // A configured channel with no implementation registered. Recorded rather than
                // thrown: one unconfigured channel must not stop the others from delivering.
                firstError ??=
                    $"No implementation registered for {ChannelTokens.For(subscription.Channel)}.";
                continue;
            }

            try
            {
                await channel.SendAsync(subscription.Endpoint, notification, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Broad on purpose. A channel talks to something outside the process — SMTP, an
                // arbitrary URL — and the set of ways that fails is not enumerable. Letting one
                // subscriber's exception escape would abandon the whole batch.
                firstError ??= $"{subscription.Endpoint}: {ex.Message}";

                logger.LogWarning(
                    ex,
                    "Delivering {MessageId} to {Endpoint} failed",
                    message.Id,
                    subscription.Endpoint);
            }
        }

        return firstError;
    }
}
