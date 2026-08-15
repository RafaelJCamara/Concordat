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

    /// <summary>
    /// Claims messages due for delivery across every tenant, oldest first.
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <param name="batchSize">The most to claim.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The due messages, paired with the tenant that staged each one.</returns>
    /// <remarks>
    /// <b>Across every tenant, deliberately.</b> The only caller is the background pump, which
    /// runs with no request and therefore no <see cref="ITenantContext"/> to scope to — a
    /// tenant-filtered claim would see only whichever tenant an unauthenticated caller happens
    /// to resolve to (<c>TenantId.SelfHosted</c> in Cloud, where nothing real lives), and every
    /// other tenant's notifications would queue forever without ever failing loudly.
    /// </remarks>
    Task<IReadOnlyList<(TenantId TenantId, OutboxMessage Message)>> ClaimDueAcrossTenantsAsync(
        DateTimeOffset now, int batchSize, CancellationToken cancellationToken);

    /// <summary>Counts what is waiting, for a health or metrics endpoint.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The pending and parked counts.</returns>
    /// <remarks>
    /// Scoped to the current tenant, unlike <see cref="ClaimDueAcrossTenantsAsync"/>: this is
    /// read from an authenticated request (M7.5's <c>GET /v1/notifications/outbox</c>), which
    /// has a real tenant to report on.
    /// </remarks>
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

    /// <summary>
    /// Lists an environment's subscriptions for an explicitly named tenant, bypassing the
    /// ambient <see cref="ITenantContext"/>.
    /// </summary>
    /// <param name="tenantId">The tenant that owns the environment.</param>
    /// <param name="environmentId">The environment.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The subscriptions.</returns>
    /// <remarks>
    /// For the background pump only, which claims across every tenant via
    /// <see cref="IOutbox.ClaimDueAcrossTenantsAsync"/> and has no ambient tenant to read
    /// through. Every other caller should use <see cref="ListAsync(EnvironmentId, CancellationToken)"/>.
    /// </remarks>
    Task<IReadOnlyList<NotificationSubscription>> ListAcrossTenantsAsync(
        TenantId tenantId, EnvironmentId environmentId, CancellationToken cancellationToken);

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
    /// <param name="signingSecret">
    /// The subscription's signing secret, resolved by the dispatcher — null when the
    /// subscription has none, which is every channel except a WEBHOOK created after this
    /// existed. A channel that signs ignores this when null rather than refusing to deliver:
    /// an old subscription predating signing support must not stop working.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>
    /// A task that completes on success and throws on failure. Throwing is what schedules a
    /// retry, so a channel that swallows its own errors silently drops the message.
    /// </returns>
    Task SendAsync(
        string endpoint,
        Notification notification,
        string? signingSecret,
        CancellationToken cancellationToken);
}
