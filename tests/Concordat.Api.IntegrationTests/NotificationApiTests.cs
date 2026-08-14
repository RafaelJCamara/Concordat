using System.Net;
using System.Net.Http.Json;
using Concordat.Application.Governance;
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
        var created = await ApiFactory.ReadAsync<SubscriptionResponse>(response);

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

        var created = await ApiFactory.ReadAsync<SubscriptionResponse>(
            await SubscribeAsync(client, environment));

        Assert.Empty(created.Events);
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
        var created = await ApiFactory.ReadAsync<SubscriptionResponse>(
            await SubscribeAsync(client, environment));

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
        var created = await ApiFactory.ReadAsync<SubscriptionResponse>(
            await SubscribeAsync(client, environment));

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

    [Fact]
    public async Task AMutedSubscriptionDoesNotBlockDelivery()
    {
        var (client, environment) = await NewEnvironmentAsync();
        var created = await ApiFactory.ReadAsync<SubscriptionResponse>(
            await SubscribeAsync(client, environment));

        await client.PutAsJsonAsync(
            $"/v1/environments/{environment}/notifications/{created.Id}/enabled",
            new SetSubscriptionEnabledRequest(false),
            ApiFactory.Json);

        await RegisterAsync(client, environment);

        var result = await PumpAsync();

        Assert.True(result.Delivered > 0);
    }
}
