using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace Concordat.Api.IntegrationTests;

/// <summary>
/// <c>PUT …/notifications/{id}/events</c>: keeping a subscription out of an event it did not choose,
/// without deleting it and so without issuing a new webhook signing secret.
/// </summary>
[Collection(ApiCollection.Name)]
public class SubscriptionEventsApiTests(ApiFactory factory)
{
    private static string UniqueEnvironment() => $"env-{Guid.NewGuid():N}"[..24];

    private async Task<(HttpClient Client, string Environment)> NewEnvironmentAsync()
    {
        var client = factory.CreateClient();
        var name = UniqueEnvironment();

        var response = await client.PostAsJsonAsync(
            "/v1/environments", new CreateEnvironmentRequest(name), ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (client, name);
    }

    private static async Task<SubscriptionResponse> SubscribeAsync(
        HttpClient client, string environment, string[]? events = null)
    {
        var response = await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/notifications",
            new CreateSubscriptionRequest("WEBHOOK", "https://example.invalid/hook", events),
            ApiFactory.Json);

        return (await ApiFactory.ReadAsync<CreatedSubscriptionResponse>(response)).Subscription;
    }

    private static Task<HttpResponseMessage> SetEventsAsync(
        HttpClient client, string environment, Guid id, params string[] events) =>
        client.PutAsJsonAsync(
            $"/v1/environments/{environment}/notifications/{id}/events",
            new SetSubscriptionEventsRequest(events),
            ApiFactory.Json);

    private static async Task<IReadOnlyList<SubscriptionResponse>> ListAsync(
        HttpClient client, string environment) =>
        (await client.GetFromJsonAsync<IReadOnlyList<SubscriptionResponse>>(
            $"/v1/environments/{environment}/notifications", ApiFactory.Json))!;

    [Fact]
    public async Task ASubscriptionWithNoFilterCanBeKeptOutOfAnEventAndIsTheSameSubscription()
    {
        var (client, environment) = await NewEnvironmentAsync();
        var original = await SubscribeAsync(client, environment);
        Assert.Empty(original.Events);

        var response = await SetEventsAsync(
            client, environment, original.Id, "BREAKING_CHANGE_SUBMITTED", "VERSION_PROMOTED");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var changed = await ApiFactory.ReadAsync<SubscriptionResponse>(response);

        Assert.Equal(original.Id, changed.Id);
        Assert.Equal(original.Endpoint, changed.Endpoint);
        // Not CreatedAt: the creation response carries the clock's full precision and a row read back
        // carries the database's, so the two differ in the last digits for the same instant.
        Assert.Equal(["BREAKING_CHANGE_SUBMITTED", "VERSION_PROMOTED"], changed.Events);

        // Stored, and not a second subscription beside the first.
        var listed = Assert.Single(await ListAsync(client, environment));
        Assert.Equal(["BREAKING_CHANGE_SUBMITTED", "VERSION_PROMOTED"], listed.Events);
    }

    [Fact]
    public async Task AnEmptyListMeansEveryEvent_AsWhenCreating()
    {
        var (client, environment) = await NewEnvironmentAsync();
        var original = await SubscribeAsync(client, environment, ["VERSION_PROMOTED"]);

        var response = await SetEventsAsync(client, environment, original.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await ApiFactory.ReadAsync<SubscriptionResponse>(response)).Events);
    }

    [Fact]
    public async Task ABodyThatForgotTheMember_IsRefused_AndDoesNotWidenWhoIsTold()
    {
        // An empty list is "every event", so an absent member read as one would turn a typo into a
        // subscription that hears everything.
        var (client, environment) = await NewEnvironmentAsync();
        var original = await SubscribeAsync(client, environment, ["VERSION_PROMOTED"]);

        var response = await client.PutAsync(
            $"/v1/environments/{environment}/notifications/{original.Id}/events",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["VERSION_PROMOTED"], Assert.Single(await ListAsync(client, environment)).Events);
    }

    [Fact]
    public async Task AnEventTheRegistryDoesNotKnow_IsRefused_AndNothingChanges()
    {
        var (client, environment) = await NewEnvironmentAsync();
        var original = await SubscribeAsync(client, environment, ["VERSION_PROMOTED"]);

        var response = await SetEventsAsync(client, environment, original.Id, "VERSION_PROMOTD");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ApiFactory.ReadProblemAsync(response);
        Assert.Equal("subscription_invalid", problem.ConcordatCode);
        Assert.Contains("VERSION_PROMOTD", problem.Detail, StringComparison.Ordinal);
        Assert.Equal(["VERSION_PROMOTED"], Assert.Single(await ListAsync(client, environment)).Events);
    }

    [Fact]
    public async Task AnUnknownSubscription_Is404()
    {
        var (client, environment) = await NewEnvironmentAsync();

        var response = await SetEventsAsync(client, environment, Guid.NewGuid(), "VERSION_PROMOTED");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AnotherEnvironmentsSubscription_Is404_NotChanged()
    {
        var (client, mine) = await NewEnvironmentAsync();
        var (_, theirs) = await NewEnvironmentAsync();
        var original = await SubscribeAsync(client, theirs, ["VERSION_PROMOTED"]);

        var response = await SetEventsAsync(client, mine, original.Id, "SUBJECT_DEPRECATED");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(["VERSION_PROMOTED"], Assert.Single(await ListAsync(client, theirs)).Events);
    }

    [Fact]
    public async Task TheMuteIsKept()
    {
        var (client, environment) = await NewEnvironmentAsync();
        var original = await SubscribeAsync(client, environment);
        var muted = await client.PutAsJsonAsync(
            $"/v1/environments/{environment}/notifications/{original.Id}/enabled",
            new SetSubscriptionEnabledRequest(false),
            ApiFactory.Json);
        Assert.Equal(HttpStatusCode.OK, muted.StatusCode);

        var response = await SetEventsAsync(client, environment, original.Id, "VERSION_PROMOTED");

        Assert.False((await ApiFactory.ReadAsync<SubscriptionResponse>(response)).Enabled);
    }
}
