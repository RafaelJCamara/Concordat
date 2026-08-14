using Concordat.Domain.Governance;
using Concordat.Domain.Registry;

namespace Concordat.Application.Abstractions;

/// <summary>
/// Stages notifications alongside the change that caused them (M7.5).
/// </summary>
/// <remarks>
/// The same shape as <see cref="IAuditLog"/> and for the same reason: <see cref="Stage"/>
/// enrols on the current unit of work, so a notification cannot survive a rolled-back change or
/// be lost while the change commits. Delivery is a separate concern with a separate failure
/// mode, which is exactly why it is a separate process.
/// </remarks>
public interface IOutbox
{
    /// <summary>Stages a message, to be written by the current unit of work.</summary>
    /// <param name="message">The message.</param>
    void Stage(OutboxMessage message);

    /// <summary>Claims messages that are due for delivery, oldest first.</summary>
    /// <param name="now">The current time.</param>
    /// <param name="batchSize">The most to claim.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The due messages.</returns>
    Task<IReadOnlyList<OutboxMessage>> ClaimDueAsync(
        DateTimeOffset now, int batchSize, CancellationToken cancellationToken);

    /// <summary>Counts what is waiting, for a health or metrics endpoint.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The pending and parked counts.</returns>
    Task<(int Pending, int Parked)> DepthAsync(CancellationToken cancellationToken);
}

/// <summary>Reads and writes notification subscriptions (M7.5).</summary>
public interface ISubscriptionRepository
{
    /// <summary>Lists an environment's subscriptions.</summary>
    /// <param name="environmentId">The environment.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The subscriptions.</returns>
    Task<IReadOnlyList<NotificationSubscription>> ListAsync(
        EnvironmentId environmentId, CancellationToken cancellationToken);

    /// <summary>Finds one subscription by id within an environment.</summary>
    /// <param name="environmentId">The environment.</param>
    /// <param name="id">The subscription id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The subscription, or <see langword="null"/>.</returns>
    Task<NotificationSubscription?> FindAsync(
        EnvironmentId environmentId, Guid id, CancellationToken cancellationToken);

    /// <summary>Stages a new subscription for insert.</summary>
    /// <param name="subscription">The subscription.</param>
    void Add(NotificationSubscription subscription);

    /// <summary>Stages a subscription for deletion.</summary>
    /// <param name="subscription">The subscription.</param>
    void Remove(NotificationSubscription subscription);
}

/// <summary>What a channel was asked to deliver.</summary>
/// <param name="Id">The message id, which a receiver may deduplicate on.</param>
/// <param name="Event">The stable event token.</param>
/// <param name="Environment">The environment name.</param>
/// <param name="Target">What it happened to — usually a subject name.</param>
/// <param name="Body">The human-readable summary.</param>
/// <param name="OccurredAt">When the change happened, not when delivery was attempted.</param>
public sealed record Notification(
    Guid Id,
    string Event,
    string Environment,
    string Target,
    string Body,
    DateTimeOffset OccurredAt);

/// <summary>
/// One way of delivering a notification (M7.5).
/// </summary>
/// <remarks>
/// Implementations must be safe to call more than once for the same
/// <see cref="Notification.Id"/>: delivery is at-least-once by design, because a crash between
/// sending and recording the send has to re-send rather than drop.
/// </remarks>
public interface INotificationChannel
{
    /// <summary>Which channel this implements.</summary>
    NotificationChannel Channel { get; }

    /// <summary>Delivers one notification.</summary>
    /// <param name="endpoint">Where to deliver.</param>
    /// <param name="notification">What to deliver.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>
    /// A task that completes on success and throws on failure. Throwing is what schedules a
    /// retry, so a channel that swallows its own errors silently drops the message.
    /// </returns>
    Task SendAsync(
        string endpoint, Notification notification, CancellationToken cancellationToken);
}
