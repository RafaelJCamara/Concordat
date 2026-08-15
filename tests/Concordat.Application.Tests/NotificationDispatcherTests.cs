using Concordat.Application.Abstractions;
using Concordat.Application.Governance;
using Concordat.Application.Tests.TestSupport;
using Concordat.Domain.Governance;
using Concordat.Domain.Registry;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Environment = Concordat.Domain.Registry.Environment;

namespace Concordat.Application.Tests;

/// <summary>
/// The outbox pump: who gets told, what happens when a channel fails, and what stops.
/// </summary>
public class NotificationDispatcherTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private readonly RecordingOutbox _outbox = new();
    private readonly FakeSubscriptions _subscriptions = new();
    private readonly FakeEnvironmentStore _environments = new();
    private readonly FakeWebhookSigningKeyStore _signingKeys = new();
    private readonly RecordingUnitOfWork _unitOfWork = new();
    private readonly FakeTimeProvider _clock = new(Now);

    private readonly EnvironmentId _environment = EnvironmentId.New();

    private NotificationDispatcher Dispatcher(params INotificationChannel[] channels) =>
        new(_outbox,
            _subscriptions,
            _environments,
            channels,
            _signingKeys,
            _unitOfWork,
            _clock,
            NullLogger<NotificationDispatcher>.Instance);

    private OutboxMessage Stage(
        NotificationEvent value = NotificationEvent.BreakingChangeSubmitted)
    {
        var message = OutboxMessage.Stage(_environment, value, "acme.Created", "something", Now);
        _outbox.Stage(message);
        return message;
    }

    private NotificationSubscription Subscribe(
        NotificationChannel channel = NotificationChannel.Webhook,
        string endpoint = "https://example.com/hook",
        params NotificationEvent[] events)
    {
        var subscription = NotificationSubscription
            .Create(_environment, channel, endpoint, events, Now).Value;

        _subscriptions.Seed(subscription);
        return subscription;
    }

    [Fact]
    public async Task ANothingToDoPassDoesNoWork()
    {
        var result = await Dispatcher().PumpAsync(CancellationToken.None);

        Assert.Equal(new DispatchResult(0, 0, 0, 0), result);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task AMessageWithNoSubscriberIsMarkedDeliveredRatherThanLeftPending()
    {
        // Nobody subscribing is a legitimate configuration. Treating it as undelivered would
        // grow the outbox forever in the common case of an environment with no notifications.
        var message = Stage();

        var result = await Dispatcher().PumpAsync(CancellationToken.None);

        Assert.Equal(1, result.Delivered);
        Assert.NotNull(message.DeliveredAt);
        Assert.Equal(1, _unitOfWork.Saves);
    }

    [Fact]
    public async Task AMatchingSubscriberIsSentTheNotification()
    {
        Stage();
        Subscribe();
        var channel = new RecordingChannel(NotificationChannel.Webhook);

        var result = await Dispatcher(channel).PumpAsync(CancellationToken.None);

        Assert.Equal(1, result.Delivered);
        var sent = Assert.Single(channel.Sent);
        Assert.Equal("https://example.com/hook", sent.Endpoint);
        Assert.Equal("BREAKING_CHANGE_SUBMITTED", sent.Notification.Event);
        Assert.Equal("acme.Created", sent.Notification.Target);
    }

    [Fact]
    public async Task ASubscriberThatDidNotAskForThisEventIsNotSent()
    {
        Stage(NotificationEvent.VersionRegistered);
        Subscribe(events: NotificationEvent.BreakingChangeSubmitted);
        var channel = new RecordingChannel(NotificationChannel.Webhook);

        await Dispatcher(channel).PumpAsync(CancellationToken.None);

        Assert.Empty(channel.Sent);
    }

    [Fact]
    public async Task AMutedSubscriberIsNotSent()
    {
        Stage();
        Subscribe().SetEnabled(false);
        var channel = new RecordingChannel(NotificationChannel.Webhook);

        await Dispatcher(channel).PumpAsync(CancellationToken.None);

        Assert.Empty(channel.Sent);
    }

    [Fact]
    public async Task TheEnvironmentNameIsResolvedRatherThanItsId()
    {
        // A webhook body saying "environment: 019f..." is useless to whoever receives it.
        Stage();
        Subscribe();
        _environments.Seed(_environment, "production");
        var channel = new RecordingChannel(NotificationChannel.Webhook);

        await Dispatcher(channel).PumpAsync(CancellationToken.None);

        Assert.Equal("production", Assert.Single(channel.Sent).Notification.Environment);
    }

    [Fact]
    public async Task AFailedDeliveryIsRetriedRatherThanLost()
    {
        var message = Stage();
        Subscribe();
        var channel = new ThrowingChannel(NotificationChannel.Webhook, "connection refused");

        var result = await Dispatcher(channel).PumpAsync(CancellationToken.None);

        Assert.Equal(1, result.Failed);
        Assert.Equal(0, result.Delivered);
        Assert.Null(message.DeliveredAt);
        Assert.Equal(1, message.Attempts);
        Assert.Contains("connection refused", message.LastError, StringComparison.Ordinal);
        Assert.True(message.NextAttemptAt > Now);
    }

    [Fact]
    public async Task AMessageNotYetDueIsNotClaimed()
    {
        var message = Stage();
        Subscribe();
        message.MarkFailed(Now, "first failure");

        var result = await Dispatcher(new RecordingChannel(NotificationChannel.Webhook))
            .PumpAsync(CancellationToken.None);

        Assert.Equal(0, result.Claimed);
    }

    [Fact]
    public async Task AMessageIsParkedAfterItsLastAttempt()
    {
        var message = Stage();
        Subscribe();
        var channel = new ThrowingChannel(NotificationChannel.Webhook, "still refused");

        for (var i = 0; i < OutboxMessage.MaxAttempts; i++)
        {
            _clock.SetUtcNow(_clock.GetUtcNow().AddHours(1));
            await Dispatcher(channel).PumpAsync(CancellationToken.None);
        }

        Assert.True(message.Parked);

        // And a parked message is not claimed again, so a permanently broken subscriber stops
        // costing anything rather than being retried forever.
        _clock.SetUtcNow(_clock.GetUtcNow().AddDays(1));
        var after = await Dispatcher(channel).PumpAsync(CancellationToken.None);

        Assert.Equal(0, after.Claimed);
    }

    [Fact]
    public async Task OneBrokenSubscriberDoesNotStopTheOthers()
    {
        // The choice is between duplicates for the working subscriber on retry and silence for
        // the broken one. Duplicates win: delivery is at-least-once and carries an id.
        Stage();
        Subscribe(NotificationChannel.Webhook, "https://good.example.com/hook");
        Subscribe(NotificationChannel.Email, "team@example.com");

        var webhook = new RecordingChannel(NotificationChannel.Webhook);
        var email = new ThrowingChannel(NotificationChannel.Email, "smtp down");

        var result = await Dispatcher(webhook, email).PumpAsync(CancellationToken.None);

        Assert.Single(webhook.Sent);
        Assert.Equal(1, result.Failed);
    }

    [Fact]
    public async Task AChannelWithNoImplementationIsRecordedRatherThanThrown()
    {
        var message = Stage();
        Subscribe(NotificationChannel.Email, "team@example.com");

        var result = await Dispatcher().PumpAsync(CancellationToken.None);

        Assert.Equal(1, result.Failed);
        Assert.Contains("EMAIL", message.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubscriptionsAreReadOncePerEnvironmentPerPass()
    {
        // A batch is usually one environment's worth of activity. Re-reading per message turns
        // a 50-message batch into 50 identical queries.
        for (var i = 0; i < 5; i++)
        {
            Stage();
        }

        Subscribe();

        await Dispatcher(new RecordingChannel(NotificationChannel.Webhook))
            .PumpAsync(CancellationToken.None);

        Assert.Equal(1, _subscriptions.Lists);
    }

    private sealed record Sent(string Endpoint, Notification Notification, string? SigningSecret);

    private sealed class RecordingChannel(NotificationChannel channel) : INotificationChannel
    {
        private readonly List<Sent> _sent = [];

        public NotificationChannel Channel => channel;

        public IReadOnlyList<Sent> Sent => _sent;

        public Task SendAsync(
            string endpoint,
            Notification notification,
            string? signingSecret,
            CancellationToken cancellationToken)
        {
            _sent.Add(new Sent(endpoint, notification, signingSecret));
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingChannel(NotificationChannel channel, string message)
        : INotificationChannel
    {
        public NotificationChannel Channel => channel;

        public Task SendAsync(
            string endpoint,
            Notification notification,
            string? signingSecret,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException(message);
    }

    private sealed class FakeSubscriptions : ISubscriptionRepository
    {
        private readonly List<NotificationSubscription> _all = [];

        public int Lists { get; private set; }

        public void Seed(NotificationSubscription subscription) => _all.Add(subscription);

        public Task<IReadOnlyList<NotificationSubscription>> ListAsync(
            EnvironmentId environmentId, CancellationToken cancellationToken)
        {
            Lists++;
            return Task.FromResult<IReadOnlyList<NotificationSubscription>>(
                [.. _all.Where(s => s.EnvironmentId == environmentId)]);
        }

        public Task<IReadOnlyList<NotificationSubscription>> ListAcrossTenantsAsync(
            TenantId tenantId, EnvironmentId environmentId, CancellationToken cancellationToken)
        {
            // The fake carries no per-subscription tenant, so this counts as a list too --
            // SubscriptionsAreReadOncePerEnvironmentPerPass asserts on this same counter and the
            // pump now always calls the cross-tenant overload.
            Lists++;
            return Task.FromResult<IReadOnlyList<NotificationSubscription>>(
                [.. _all.Where(s => s.EnvironmentId == environmentId)]);
        }

        public Task<NotificationSubscription?> FindAsync(
            EnvironmentId environmentId, Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(_all.FirstOrDefault(
                s => s.EnvironmentId == environmentId && s.Id == id));

        public void Add(NotificationSubscription subscription) => _all.Add(subscription);

        public void Remove(NotificationSubscription subscription) => _all.Remove(subscription);
    }

    private sealed class FakeEnvironmentStore : IEnvironmentRepository
    {
        private readonly List<Environment> _all = [];

        public void Seed(EnvironmentId id, string name) =>
            _all.Add(Environment.Create(name, Now, null, null, null, id).Value);

        public Task<Environment?> FindAsync(EnvironmentName name, CancellationToken cancellationToken) =>
            Task.FromResult(_all.FirstOrDefault(e => e.Name == name));

        public Task<Environment?> FindAsync(EnvironmentId id, CancellationToken cancellationToken) =>
            Task.FromResult(_all.FirstOrDefault(e => e.Id == id));

        public Task<Environment?> FindAcrossTenantsAsync(
            TenantId tenantId, EnvironmentId id, CancellationToken cancellationToken) =>
            Task.FromResult(_all.FirstOrDefault(e => e.Id == id));

        public Task<IReadOnlyList<Environment>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Environment>>(_all);

        public void Add(Environment environment) => _all.Add(environment);
    }
}
