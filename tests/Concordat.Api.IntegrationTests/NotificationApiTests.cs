using System.Net;
using System.Net.Http.Json;
using Concordat.Application.Abstractions;
using Concordat.Application.Governance;
using Concordat.Domain.Governance;
using Concordat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Concordat.Api.IntegrationTests;

/// <summary>
/// M7.5's notification routes and the outbox behind them, over real PostgreSQL.
/// </summary>
[Collection(ApiCollection.Name)]
public class NotificationApiTests(ApiFactory factory)
{
    private const string V1 = """{"type":"object","properties":{"id":{"type":"string"}}}""";

    private const string V1Breaking =
        """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}""";

    private static string UniqueEnvironment() => $"env-{Guid.CreateVersion7():N}"[..24];

    private static string UniqueSubject() => $"acme.notify.S{Guid.CreateVersion7():N}";

    private async Task<(HttpClient Client, string Environment)> NewEnvironmentAsync()
    {
        var client = factory.CreateClient();
        var name = UniqueEnvironment();

        var response = await client.PostAsJsonAsync(
            "/v1/environments", new CreateEnvironmentRequest(name), ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (client, name);
    }

    private static Task<HttpResponseMessage> SubscribeAsync(
        HttpClient client,
        string environment,
        string channel = "WEBHOOK",
        string endpoint = "https://example.invalid/hook",
        string[]? events = null) =>
        client.PostAsJsonAsync(
            $"/v1/environments/{environment}/notifications",
            new CreateSubscriptionRequest(channel, endpoint, events),
            ApiFactory.Json);

    private static async Task<string> RegisterAsync(
        HttpClient client, string environment, string? second = null)
    {
        var name = UniqueSubject();

        (await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/subjects",
            new CreateSubjectRequest(name, "json", "alice", null, null, "open"),
            ApiFactory.Json)).EnsureSuccessStatusCode();

        (await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/subjects/{name}/versions",
            new RegisterVersionRequest(V1, "1.0.0", null, "alice"),
            ApiFactory.Json)).EnsureSuccessStatusCode();

        if (second is not null)
        {
            (await client.PostAsJsonAsync(
                $"/v1/environments/{environment}/subjects/{name}/versions",
                new RegisterVersionRequest(second, null, null, "alice"),
                ApiFactory.Json)).EnsureSuccessStatusCode();
        }

        return name;
    }

    /// <summary>Runs one pump pass against the real dispatcher and the real database.</summary>
    private async Task<DispatchResult> PumpAsync()
    {
        using var scope = factory.Services.CreateScope();

        return await scope.ServiceProvider
            .GetRequiredService<NotificationDispatcher>()
            .PumpAsync(CancellationToken.None);
    }

    // ------------------------------------------------------------------ subscriptions

    [Fact]
    public async Task ASubscriptionRoundTripsWithItsEvents()
    {
        var (client, environment) = await NewEnvironmentAsync();

        var response = await SubscribeAsync(
            client, environment, events: ["BREAKING_CHANGE_SUBMITTED", "VERSION_PROMOTED"]);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await ApiFactory.ReadAsync<CreatedSubscriptionResponse>(response)).Subscription;

        Assert.Equal("WEBHOOK", created.Channel);
        Assert.True(created.Enabled);
        Assert.Equal(["BREAKING_CHANGE_SUBMITTED", "VERSION_PROMOTED"], created.Events);

        var listed = await client.GetFromJsonAsync<IReadOnlyList<SubscriptionResponse>>(
            $"/v1/environments/{environment}/notifications", ApiFactory.Json);

        Assert.Equal(created.Id, Assert.Single(listed!).Id);
    }

    [Fact]
    public async Task NoEventsMeansEveryEvent()
    {
        var (client, environment) = await NewEnvironmentAsync();

        var created = (await ApiFactory.ReadAsync<CreatedSubscriptionResponse>(
            await SubscribeAsync(client, environment))).Subscription;

        Assert.Empty(created.Events);
    }

    [Fact]
    public async Task AWebhookSubscriptionReturnsASigningSecretOnceAndOnlyOnce()
    {
        var (client, environment) = await NewEnvironmentAsync();

        var created = await ApiFactory.ReadAsync<CreatedSubscriptionResponse>(
            await SubscribeAsync(client, environment));

        Assert.False(string.IsNullOrWhiteSpace(created.SigningSecret));

        var listed = await client.GetFromJsonAsync<IReadOnlyList<SubscriptionResponse>>(
            $"/v1/environments/{environment}/notifications", ApiFactory.Json);

        // The list/get shape never carries a secret -- there is nowhere on SubscriptionResponse
        // for one to leak into, which this asserts by construction rather than by inspecting
        // JSON for an absent property.
        Assert.Single(listed!);
    }

    [Fact]
    public async Task AnEmailSubscriptionHasNoSigningSecret()
    {
        var (client, environment) = await NewEnvironmentAsync();

        var created = await ApiFactory.ReadAsync<CreatedSubscriptionResponse>(
            await SubscribeAsync(client, environment, channel: "EMAIL", endpoint: "team@example.com"));

        Assert.Null(created.SigningSecret);
    }

    [Fact]
    public async Task AnHttpWebhookIs400()
    {
        var (client, environment) = await NewEnvironmentAsync();

        var response = await SubscribeAsync(
            client, environment, endpoint: "http://example.invalid/hook");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "subscription_endpoint_invalid",
            (await ApiFactory.ReadProblemAsync(response)).ConcordatCode);
    }

    [Fact]
    public async Task AnUnknownEventTokenIs400RatherThanBeingIgnored()
    {
        // A typo would otherwise produce a subscription that is configured, enabled, and
        // quietly delivers nothing it was meant to.
        var (client, environment) = await NewEnvironmentAsync();

        var response = await SubscribeAsync(
            client, environment, events: ["VERSION_DELETED"]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "subscription_invalid",
            (await ApiFactory.ReadProblemAsync(response)).ConcordatCode);
    }

    [Fact]
    public async Task AnUnknownChannelIs400()
    {
        var (client, environment) = await NewEnvironmentAsync();

        var response = await SubscribeAsync(client, environment, channel: "SLACK");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task MutingAndUnmutingKeepsTheSettings()
    {
        var (client, environment) = await NewEnvironmentAsync();
        var created = (await ApiFactory.ReadAsync<CreatedSubscriptionResponse>(
            await SubscribeAsync(client, environment))).Subscription;

        var muted = await client.PutAsJsonAsync(
            $"/v1/environments/{environment}/notifications/{created.Id}/enabled",
            new SetSubscriptionEnabledRequest(false),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.OK, muted.StatusCode);
        var body = await ApiFactory.ReadAsync<SubscriptionResponse>(muted);

        Assert.False(body.Enabled);
        Assert.Equal(created.Endpoint, body.Endpoint);
    }

    [Fact]
    public async Task DeletingRemovesItAndThenIs404()
    {
        var (client, environment) = await NewEnvironmentAsync();
        var created = (await ApiFactory.ReadAsync<CreatedSubscriptionResponse>(
            await SubscribeAsync(client, environment))).Subscription;

        var deleted = await client.DeleteAsync(
            $"/v1/environments/{environment}/notifications/{created.Id}");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var again = await client.DeleteAsync(
            $"/v1/environments/{environment}/notifications/{created.Id}");

        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task AnUnknownEnvironmentIs404()
    {
        var response = await factory.CreateClient().GetAsync(
            $"/v1/environments/{UniqueEnvironment()}/notifications");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ------------------------------------------------------------------------ outbox

    [Fact]
    public async Task RegisteringStagesANotificationInTheSameTransaction()
    {
        var (client, environment) = await NewEnvironmentAsync();

        var before = await client.GetFromJsonAsync<OutboxDepthResponse>(
            "/v1/notifications/outbox", ApiFactory.Json);

        await RegisterAsync(client, environment);

        var after = await client.GetFromJsonAsync<OutboxDepthResponse>(
            "/v1/notifications/outbox", ApiFactory.Json);

        Assert.True(after!.Pending > before!.Pending);
    }

    [Fact]
    public async Task ARefusedRegistrationStagesNothing()
    {
        // The whole reason the outbox is written by the same unit of work: a notification for a
        // change that did not happen is worse than a missing one.
        var (client, environment) = await NewEnvironmentAsync();

        var before = await client.GetFromJsonAsync<OutboxDepthResponse>(
            "/v1/notifications/outbox", ApiFactory.Json);

        var refused = await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/subjects",
            new CreateSubjectRequest("not a name!", "json", "alice", null, null, "open"),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var after = await client.GetFromJsonAsync<OutboxDepthResponse>(
            "/v1/notifications/outbox", ApiFactory.Json);

        Assert.Equal(before!.Pending, after!.Pending);
    }

    [Fact]
    public async Task ThePumpDrainsMessagesNobodySubscribedTo()
    {
        // Nobody subscribing is a legitimate configuration, and leaving those messages pending
        // would grow the table forever in the commonest case there is.
        var (client, environment) = await NewEnvironmentAsync();
        await RegisterAsync(client, environment);

        var before = await client.GetFromJsonAsync<OutboxDepthResponse>(
            "/v1/notifications/outbox", ApiFactory.Json);

        var result = await PumpAsync();
        Assert.True(result.Delivered > 0);

        var after = await client.GetFromJsonAsync<OutboxDepthResponse>(
            "/v1/notifications/outbox", ApiFactory.Json);

        // Relative, not absolute: the outbox is tenant-wide and sibling tests in this
        // collection leave their own messages behind on purpose.
        Assert.True(after!.Pending < before!.Pending);
    }

    [Fact]
    public async Task ABreakingRegistrationIsStagedAsAReviewEventAndDeliveryIsRetried()
    {
        var (client, environment) = await NewEnvironmentAsync();

        // An endpoint that cannot resolve: the delivery fails, and the message has to survive
        // that rather than being dropped.
        await SubscribeAsync(
            client, environment, events: ["BREAKING_CHANGE_SUBMITTED"]);

        await RegisterAsync(client, environment, V1Breaking);

        var result = await PumpAsync();

        Assert.True(result.Failed > 0);

        var after = await client.GetFromJsonAsync<OutboxDepthResponse>(
            "/v1/notifications/outbox", ApiFactory.Json);

        // Still pending, and now scheduled for a later attempt rather than lost.
        Assert.True(after!.Pending > 0);
        Assert.Equal(0, after.Parked);
    }

    // ----------------------------------------------- a policy change, delivered (ADR-034)

    /// <summary>The events the registry could be subscribed to before a policy change was one.</summary>
    private static readonly string[] EventsBeforeAPolicyChangeWasOne =
    [
        "VERSION_REGISTERED",
        "BREAKING_CHANGE_SUBMITTED",
        "BREAKING_CHANGE_APPROVED",
        "BREAKING_CHANGE_REJECTED",
        "VERSION_PROMOTED",
        "SUBJECT_DEPRECATED",
        "ENFORCEMENT_VIOLATION",
    ];

    /// <summary>
    /// Pumps the real dispatcher, against the real database and real subscriptions, with a channel
    /// that keeps what it is sent in place of the webhook that would leave the machine.
    /// </summary>
    private async Task<IReadOnlyList<(string Endpoint, Notification Notification)>> PumpCapturingAsync()
    {
        var channel = new CapturingWebhookChannel();

        // Until nothing is due: the pump claims fifty oldest-first across the whole tenant, and a
        // sibling test's leftovers can sit ahead of the message under test.
        for (var pass = 0; pass < 100; pass++)
        {
            using var scope = factory.Services.CreateScope();

            var dispatcher = ActivatorUtilities.CreateInstance<NotificationDispatcher>(
                scope.ServiceProvider, (IEnumerable<INotificationChannel>)[channel]);

            var result = await dispatcher.PumpAsync(CancellationToken.None);

            if (result.Claimed is 0)
            {
                break;
            }
        }

        return channel.Sent;
    }

    [Fact]
    public async Task APolicyChangeIsDeliveredToTheSubscriptionsThatWantIt()
    {
        // The three cases the upgrade note turns on. A subscription that lists the event is told.
        // A subscription that lists every *other* event is not (so an explicit list is untouched by
        // this release). And one that lists nothing, which means every event, is told too: that is
        // the behaviour change, and it is deliberate (ADR-034).
        var (client, environment) = await NewEnvironmentAsync();
        var subject = await RegisterAsync(client, environment);

        const string Wants = "https://wants.example.invalid/hook";
        const string ListsTheOthers = "https://lists-the-others.example.invalid/hook";
        const string Unfiltered = "https://unfiltered.example.invalid/hook";

        (await SubscribeAsync(client, environment, endpoint: Wants, events: ["SUBJECT_POLICY_CHANGED"]))
            .EnsureSuccessStatusCode();
        (await SubscribeAsync(
            client, environment, endpoint: ListsTheOthers, events: EventsBeforeAPolicyChangeWasOne))
            .EnsureSuccessStatusCode();
        (await SubscribeAsync(client, environment, endpoint: Unfiltered)).EnsureSuccessStatusCode();

        var put = await client.PutAsJsonAsync(
            $"/v1/environments/{environment}/subjects/{subject}/compatibility-policy",
            new SetCompatibilityPolicyRequest("FULL", "SOURCE"),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var sent = (await PumpCapturingAsync())
            .Where(s => s.Notification.Environment == environment)
            .ToList();

        var wanted = Assert.Single(
            sent, s => s.Endpoint == Wants && s.Notification.Event == "SUBJECT_POLICY_CHANGED");
        Assert.Equal(subject, wanted.Notification.Target);
        Assert.Contains(
            $"The compatibility policy of {subject} was changed by ",
            wanted.Notification.Body,
            StringComparison.Ordinal);
        Assert.Contains(": inherited -> FULL/SOURCE.", wanted.Notification.Body, StringComparison.Ordinal);

        // It asked for nothing else, so the registration that created the subject is not sent.
        Assert.Single(sent, s => s.Endpoint == Wants);

        // The explicit list is the same list it was: the registration, and no policy change.
        Assert.DoesNotContain(
            sent, s => s.Endpoint == ListsTheOthers && s.Notification.Event == "SUBJECT_POLICY_CHANGED");
        Assert.Single(
            sent, s => s.Endpoint == ListsTheOthers && s.Notification.Event == "VERSION_REGISTERED");

        // Every event means this one too.
        Assert.Single(
            sent, s => s.Endpoint == Unfiltered && s.Notification.Event == "SUBJECT_POLICY_CHANGED");
        Assert.Single(
            sent, s => s.Endpoint == Unfiltered && s.Notification.Event == "VERSION_REGISTERED");
    }

    [Fact]
    public async Task ASubscriptionWhoseListNamesAPolicyChangeSurvivesAReadBack()
    {
        // The token is stored as text and parsed on every read (the rollback cost in ADR-034 is the
        // other direction of this). A round trip through the real column proves the token this
        // build writes is one this build reads.
        var (client, environment) = await NewEnvironmentAsync();

        var created = (await ApiFactory.ReadAsync<CreatedSubscriptionResponse>(
            await SubscribeAsync(client, environment, events: ["SUBJECT_POLICY_CHANGED"])))
            .Subscription;

        var listed = await client.GetFromJsonAsync<IReadOnlyList<SubscriptionResponse>>(
            $"/v1/environments/{environment}/notifications", ApiFactory.Json);

        Assert.Equal(["SUBJECT_POLICY_CHANGED"], created.Events);
        Assert.Equal(["SUBJECT_POLICY_CHANGED"], Assert.Single(listed!).Events);
    }

    // -------------------------------- a row this build cannot read blocks nobody (the hazard)

    private async Task<T> ScalarAsync<T>(string sql)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ConcordatDbContext>();

        return (await context.Database.SqlQueryRaw<T>(sql).ToListAsync()).Single();
    }

    private async Task ExecuteAsync(string sql)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ConcordatDbContext>();

        await context.Database.ExecuteSqlRawAsync(sql);
    }

    [Fact]
    public async Task AnOutboxRowWithATokenThisBuildDoesNotKnow_IsLeftPendingAndBlocksNobody()
    {
        // What an older build sees when a newer one has written an event it was never taught (a
        // rollback, or an old instance beside a new one). The converter used to throw while the
        // pump materialised its batch, which failed every pass for every tenant, and nothing marked
        // the row delivered: notifications stopped everywhere while the registry kept accepting
        // changes (measured with the previous image, STATUS). The claim now leaves such a row alone.
        // It is not parked or discarded: it stays pending, and counted, for a build that can read it.
        var (client, stuckEnvironment) = await NewEnvironmentAsync();
        var (_, otherEnvironment) = await NewEnvironmentAsync();
        var stuckSubject = await RegisterAsync(client, stuckEnvironment);
        var otherSubject = await RegisterAsync(client, otherEnvironment);

        try
        {
            await ExecuteAsync(
                $"UPDATE outbox_message SET event = 'A_FUTURE_EVENT' WHERE target = '{stuckSubject}'");

            var depthBefore = await client.GetFromJsonAsync<OutboxDepthResponse>(
                "/v1/notifications/outbox", ApiFactory.Json);

            // Not asserting a count: other tests leave messages for the pump to find. What matters is
            // that the pass completes and reaches the one behind the row it cannot read.
            await PumpAsync();

            Assert.Equal(1, await ScalarAsync<int>(
                $"SELECT count(*)::int AS \"Value\" FROM outbox_message WHERE target = '{otherSubject}' AND delivered_at IS NOT NULL"));

            Assert.Equal(1, await ScalarAsync<int>(
                "SELECT count(*)::int AS \"Value\" FROM outbox_message " +
                $"WHERE target = '{stuckSubject}' AND event = 'A_FUTURE_EVENT' " +
                "AND delivered_at IS NULL AND NOT parked AND attempts = 0"));

            // Still counted as pending, so the depth endpoint does not hide it either.
            var depthAfter = await client.GetFromJsonAsync<OutboxDepthResponse>(
                "/v1/notifications/outbox", ApiFactory.Json);

            Assert.True(depthAfter!.Pending >= 1);
            Assert.True(depthAfter.Pending <= depthBefore!.Pending);
        }
        finally
        {
            await ExecuteAsync($"DELETE FROM outbox_message WHERE target = '{stuckSubject}'");
        }
    }

    [Fact]
    public async Task ASubscriptionNamingATokenThisBuildDoesNotKnow_FailsOnlyItsOwnEnvironment()
    {
        // The other half: a subscription row the converter cannot read made the pump's read of that
        // environment's subscriptions throw, which aborted the pass. Now the messages that needed
        // that read fail and back off, and the pass delivers the rest.
        var (client, stuckEnvironment) = await NewEnvironmentAsync();
        var (_, otherEnvironment) = await NewEnvironmentAsync();

        (await SubscribeAsync(client, stuckEnvironment, endpoint: "https://stuck.example.invalid/hook"))
            .EnsureSuccessStatusCode();

        var stuckSubject = await RegisterAsync(client, stuckEnvironment);
        var otherSubject = await RegisterAsync(client, otherEnvironment);

        try
        {
            await ExecuteAsync(
                "UPDATE notification_subscription SET events = 'A_FUTURE_EVENT' " +
                "WHERE endpoint = 'https://stuck.example.invalid/hook'");

            await PumpAsync();

            Assert.Equal(1, await ScalarAsync<int>(
                $"SELECT count(*)::int AS \"Value\" FROM outbox_message WHERE target = '{otherSubject}' AND delivered_at IS NOT NULL"));

            Assert.Equal(1, await ScalarAsync<int>(
                "SELECT count(*)::int AS \"Value\" FROM outbox_message " +
                $"WHERE target = '{stuckSubject}' AND delivered_at IS NULL AND attempts = 1 " +
                "AND last_error LIKE '%A_FUTURE_EVENT%'"));
        }
        finally
        {
            await ExecuteAsync($"DELETE FROM outbox_message WHERE target = '{stuckSubject}'");
            await ExecuteAsync(
                "DELETE FROM notification_subscription WHERE endpoint = 'https://stuck.example.invalid/hook'");
        }
    }

    [Fact]
    public async Task AMutedSubscriptionDoesNotBlockDelivery()
    {
        var (client, environment) = await NewEnvironmentAsync();
        var created = (await ApiFactory.ReadAsync<CreatedSubscriptionResponse>(
            await SubscribeAsync(client, environment))).Subscription;

        await client.PutAsJsonAsync(
            $"/v1/environments/{environment}/notifications/{created.Id}/enabled",
            new SetSubscriptionEnabledRequest(false),
            ApiFactory.Json);

        await RegisterAsync(client, environment);

        var result = await PumpAsync();

        Assert.True(result.Delivered > 0);
    }
}

/// <summary>
/// Stands in for the webhook channel and keeps what it was asked to send, so a test can say who
/// was told what without a request leaving the machine.
/// </summary>
internal sealed class CapturingWebhookChannel : INotificationChannel
{
    private readonly List<(string Endpoint, Notification Notification)> _sent = [];

    public NotificationChannel Channel => NotificationChannel.Webhook;

    public IReadOnlyList<(string Endpoint, Notification Notification)> Sent => _sent;

    public Task SendAsync(
        string endpoint,
        Notification notification,
        string? signingSecret,
        CancellationToken cancellationToken)
    {
        _sent.Add((endpoint, notification));
        return Task.CompletedTask;
    }
}
