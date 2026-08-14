using Concordat.Domain.Governance;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;

namespace Concordat.Domain.Tests;

/// <summary>M7.5's outbox message and its retry schedule.</summary>
public class OutboxMessageTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private static OutboxMessage New() =>
        OutboxMessage.Stage(
            EnvironmentId.New(),
            NotificationEvent.BreakingChangeSubmitted,
            "acme.Created",
            "version 4 is a breaking change and is waiting for review.",
            Now);

    [Fact]
    public void AStagedMessageIsDueImmediatelyAndUndelivered()
    {
        var message = New();

        Assert.Null(message.DeliveredAt);
        Assert.False(message.Parked);
        Assert.Equal(0, message.Attempts);
        Assert.Equal(Now, message.NextAttemptAt);
        Assert.Equal(Now, message.OccurredAt);
    }

    [Fact]
    public void DeliveryRecordsWhenAndClearsTheLastError()
    {
        var message = New();
        message.MarkFailed(Now, "connection refused");

        message.MarkDelivered(Now.AddMinutes(5));

        Assert.Equal(Now.AddMinutes(5), message.DeliveredAt);
        Assert.Null(message.LastError);
    }

    [Fact]
    public void FailureBacksOffExponentiallyFromOneMinute()
    {
        // Coarse on purpose. An endpoint that is down stays down for minutes, and retrying
        // tightly turns one broken subscriber into load on the registry.
        var message = New();

        message.MarkFailed(Now, "timeout");
        Assert.Equal(Now.AddMinutes(1), message.NextAttemptAt);

        message.MarkFailed(Now, "timeout");
        Assert.Equal(Now.AddMinutes(2), message.NextAttemptAt);

        message.MarkFailed(Now, "timeout");
        Assert.Equal(Now.AddMinutes(4), message.NextAttemptAt);
    }

    [Fact]
    public void AMessageIsParkedRatherThanDeletedAfterTheLastAttempt()
    {
        // A message nobody could deliver is evidence about the channel. Discarding it would
        // make a misconfigured webhook indistinguishable from a quiet week.
        var message = New();

        for (var i = 0; i < OutboxMessage.MaxAttempts; i++)
        {
            Assert.False(message.Parked);
            message.MarkFailed(Now, "still refused");
        }

        Assert.True(message.Parked);
        Assert.Equal(OutboxMessage.MaxAttempts, message.Attempts);
        Assert.Null(message.DeliveredAt);
        Assert.Equal("still refused", message.LastError);
    }

    [Fact]
    public void OverlongContentIsTruncatedRatherThanRefused()
    {
        var message = OutboxMessage.Stage(
            EnvironmentId.New(),
            NotificationEvent.VersionRegistered,
            new string('t', OutboxMessage.MaxTargetLength + 50),
            new string('b', OutboxMessage.MaxBodyLength + 50),
            Now);

        Assert.Equal(OutboxMessage.MaxTargetLength, message.Target.Length);
        Assert.Equal(OutboxMessage.MaxBodyLength, message.Body.Length);
    }

    [Fact]
    public void TheTimestampIsNormalisedToUtc()
    {
        var local = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.FromHours(2));

        var message = OutboxMessage.Stage(
            EnvironmentId.New(), NotificationEvent.VersionRegistered, "x", "y", local);

        Assert.Equal(TimeSpan.Zero, message.OccurredAt.Offset);
        Assert.Equal(local.UtcDateTime, message.OccurredAt.UtcDateTime);
    }

    [Fact]
    public void EveryEventHasATokenAndParsesBack()
    {
        foreach (var value in Enum.GetValues<NotificationEvent>())
        {
            var token = NotificationTokens.For(value);
            Assert.False(string.IsNullOrEmpty(token));

            Assert.True(NotificationTokens.TryParse(token, out var parsed));
            Assert.Equal(value, parsed);
        }
    }

    [Fact]
    public void AnUnknownEventTokenDoesNotParse() =>
        Assert.False(NotificationTokens.TryParse("VERSION_DELETED", out _));
}

/// <summary>M7.5's subscriptions.</summary>
public class NotificationSubscriptionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private static Result<NotificationSubscription> Create(
        NotificationChannel channel, string endpoint, params NotificationEvent[] events) =>
        NotificationSubscription.Create(EnvironmentId.New(), channel, endpoint, events, Now);

    [Fact]
    public void AnEmptyEventSetMeansEveryEvent()
    {
        // The alternative is a subscription that is configured, enabled, and silently delivers
        // nothing — which looks correct from every screen.
        var subscription = Create(NotificationChannel.Webhook, "https://example.com/hook").Value;

        Assert.All(
            Enum.GetValues<NotificationEvent>(),
            e => Assert.True(subscription.Wants(e)));
    }

    [Fact]
    public void AnExplicitSetWantsOnlyWhatItNamed()
    {
        var subscription = Create(
            NotificationChannel.Webhook,
            "https://example.com/hook",
            NotificationEvent.BreakingChangeSubmitted).Value;

        Assert.True(subscription.Wants(NotificationEvent.BreakingChangeSubmitted));
        Assert.False(subscription.Wants(NotificationEvent.VersionRegistered));
    }

    [Fact]
    public void MutingStopsDeliveryWithoutLosingTheConfiguration()
    {
        var subscription = Create(NotificationChannel.Email, "team@example.com").Value;

        subscription.SetEnabled(false);
        Assert.False(subscription.Wants(NotificationEvent.VersionRegistered));

        subscription.SetEnabled(true);
        Assert.True(subscription.Wants(NotificationEvent.VersionRegistered));
        Assert.Equal("team@example.com", subscription.Endpoint);
    }

    [Fact]
    public void HttpWebhooksAreRefused()
    {
        // A webhook body names subjects, versions and reviewers — the shape of an
        // organisation's message contracts. There is no reason to send that in the clear.
        var result = Create(NotificationChannel.Webhook, "http://example.com/hook");

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.SubscriptionEndpointInvalid, result.Error!.Code);
        Assert.Contains("https", result.Error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    [InlineData("")]
    [InlineData(null)]
    public void AnUnusableWebhookEndpointIsRefused(string? endpoint)
    {
        var result = NotificationSubscription.Create(
            EnvironmentId.New(), NotificationChannel.Webhook, endpoint, [], Now);

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.SubscriptionEndpointInvalid, result.Error!.Code);
    }

    [Theory]
    [InlineData("team@example.com")]
    [InlineData("schema-owners@sub.example.co.uk")]
    public void AUsableEmailAddressIsAccepted(string endpoint) =>
        Assert.True(Create(NotificationChannel.Email, endpoint).IsSuccess);

    [Theory]
    [InlineData("no-at-sign")]
    [InlineData("two@@example.com")]
    [InlineData("@example.com")]
    [InlineData("team@")]
    [InlineData("team@localhost")]
    public void AnUnusableEmailAddressIsRefused(string endpoint) =>
        Assert.True(Create(NotificationChannel.Email, endpoint).IsFailure);

    [Fact]
    public void DuplicateEventsAreCollapsed()
    {
        var subscription = Create(
            NotificationChannel.Email,
            "team@example.com",
            NotificationEvent.VersionRegistered,
            NotificationEvent.VersionRegistered).Value;

        Assert.Single(subscription.Events);
    }

    [Fact]
    public void AnEndpointIsTrimmed()
    {
        var subscription = Create(NotificationChannel.Email, "  team@example.com  ").Value;

        Assert.Equal("team@example.com", subscription.Endpoint);
    }

    [Theory]
    [InlineData(NotificationChannel.Email, "EMAIL")]
    [InlineData(NotificationChannel.Webhook, "WEBHOOK")]
    public void ChannelTokensRoundTrip(NotificationChannel channel, string expected)
    {
        Assert.Equal(expected, ChannelTokens.For(channel));
        Assert.True(ChannelTokens.TryParse(expected, out var parsed));
        Assert.Equal(channel, parsed);
    }

    [Fact]
    public void AnUnknownChannelTokenDoesNotParse() =>
        Assert.False(ChannelTokens.TryParse("SLACK", out _));
}
