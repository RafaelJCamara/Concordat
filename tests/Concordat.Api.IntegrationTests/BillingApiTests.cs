using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Concordat.Api.IntegrationTests;

/// <summary>
/// M9.3's metering and plan enforcement, in the profile that bills.
/// </summary>
/// <remarks>
/// A new organisation starts on Free — 1 environment, 10 subjects — which makes the limits
/// reachable in a test without arranging anything.
/// </remarks>
[Collection(CloudApiCollection.Name)]
public class BillingApiTests(CloudApiFactory factory)
{
    private static string UniqueSlug() => $"bill-{Guid.CreateVersion7():N}"[..20];

    private static string UniqueSubject() => $"acme.bill.S{Guid.CreateVersion7():N}";

    private const string Schema = """{"type":"object","properties":{"id":{"type":"string"}}}""";

    private async Task<HttpClient> NewOrganisationAsync()
    {
        var credential = await factory.NewOrganisationAsync(UniqueSlug());
        var client = factory.CreateClient();

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", credential);

        return client;
    }

    private static Task<HttpResponseMessage> CreateSubjectAsync(HttpClient client, string name) =>
        client.PostAsJsonAsync(
            "/v1/environments/dev/subjects",
            new CreateSubjectRequest(name, "json", "owner", null, null, "open"),
            ApiFactory.Json);

    [Fact]
    public async Task ANewOrganisationStartsOnFreeAndSaysSo()
    {
        var client = await NewOrganisationAsync();

        var usage = await client.GetFromJsonAsync<UsageResponse>("/v1/usage", ApiFactory.Json);

        Assert.Equal("FREE", usage!.Tier);
        Assert.Equal("ACTIVE", usage.Status);

        // The limits the plan document promises, reported back to the customer.
        Assert.Equal(1, usage.Environments.Limit);
        Assert.Equal(10, usage.Subjects.Limit);

        // One seat: the owner created at signup.
        Assert.Equal(1, usage.Seats.Used);
        Assert.Equal(0, usage.Environments.Used);
    }

    [Fact]
    public async Task UsageCountsWhatWasActuallyCreated()
    {
        var client = await NewOrganisationAsync();

        await client.PostAsJsonAsync(
            "/v1/environments", new CreateEnvironmentRequest("dev"), ApiFactory.Json);

        await CreateSubjectAsync(client, UniqueSubject());
        await CreateSubjectAsync(client, UniqueSubject());

        var usage = await client.GetFromJsonAsync<UsageResponse>("/v1/usage", ApiFactory.Json);

        Assert.Equal(1, usage!.Environments.Used);
        Assert.Equal(2, usage.Subjects.Used);
        Assert.Equal(0, usage.VersionsThisMonth.Used);
    }

    [Fact]
    public async Task RegisteringAVersionCountsTowardsTheMonth()
    {
        var client = await NewOrganisationAsync();
        var subject = UniqueSubject();

        await client.PostAsJsonAsync(
            "/v1/environments", new CreateEnvironmentRequest("dev"), ApiFactory.Json);
        await CreateSubjectAsync(client, subject);

        await client.PostAsJsonAsync(
            $"/v1/environments/dev/subjects/{subject}/versions",
            new RegisterVersionRequest(Schema, "1.0.0", null, "owner"),
            ApiFactory.Json);

        var usage = await client.GetFromJsonAsync<UsageResponse>("/v1/usage", ApiFactory.Json);

        Assert.Equal(1, usage!.VersionsThisMonth.Used);
    }

    [Fact]
    public async Task TheEnvironmentLimitRefusesTheSecondOneWith402()
    {
        // 402, not 403: the caller has every right to do this and their plan does not stretch
        // to it, which is a distinction a client can act on by upgrading rather than by asking
        // an admin for a scope they already hold.
        var client = await NewOrganisationAsync();

        var first = await client.PostAsJsonAsync(
            "/v1/environments", new CreateEnvironmentRequest("dev"), ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync(
            "/v1/environments", new CreateEnvironmentRequest("prod"), ApiFactory.Json);

        Assert.Equal(HttpStatusCode.PaymentRequired, second.StatusCode);

        var problem = await ApiFactory.ReadProblemAsync(second);
        Assert.Equal("plan_limit_reached", problem.ConcordatCode);

        // The message says what to do about it, and says the existing one keeps working.
        Assert.Contains("Upgrade", problem.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSubjectLimitRefusesTheEleventh()
    {
        var client = await NewOrganisationAsync();

        await client.PostAsJsonAsync(
            "/v1/environments", new CreateEnvironmentRequest("dev"), ApiFactory.Json);

        for (var i = 0; i < 10; i++)
        {
            var created = await CreateSubjectAsync(client, UniqueSubject());
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        var eleventh = await CreateSubjectAsync(client, UniqueSubject());

        Assert.Equal(HttpStatusCode.PaymentRequired, eleventh.StatusCode);
    }

    [Fact]
    public async Task BeingOverALimitNeverBreaksReads()
    {
        // The property that matters most. The registry is on the delivery path: a plan limit
        // that could stop a consumer resolving a schema would turn a billing dispute into a
        // production outage.
        var client = await NewOrganisationAsync();
        var subject = UniqueSubject();

        await client.PostAsJsonAsync(
            "/v1/environments", new CreateEnvironmentRequest("dev"), ApiFactory.Json);
        await CreateSubjectAsync(client, subject);

        await client.PostAsJsonAsync(
            $"/v1/environments/dev/subjects/{subject}/versions",
            new RegisterVersionRequest(Schema, "1.0.0", null, "owner"),
            ApiFactory.Json);

        // Now push past the environment limit.
        var refused = await client.PostAsJsonAsync(
            "/v1/environments", new CreateEnvironmentRequest("prod"), ApiFactory.Json);

        Assert.Equal(HttpStatusCode.PaymentRequired, refused.StatusCode);

        // Everything already registered keeps answering.
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.GetAsync($"/v1/environments/dev/subjects/{subject}")).StatusCode);

        Assert.Equal(
            HttpStatusCode.OK,
            (await client.GetAsync("/v1/environments/dev/subjects")).StatusCode);

        Assert.Equal(
            HttpStatusCode.OK,
            (await client.GetAsync($"/v1/environments/dev/subjects/{subject}/versions/latest"))
                .StatusCode);

        // Including the SDK's startup path, which is the one an outage would be felt through.
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PostAsync("/v1/environments/dev/bootstrap", null)).StatusCode);
    }

    [Fact]
    public async Task OneOrganisationsUsageIsNotAnothersRatherThanTheDeploymentTotal()
    {
        // A meter that ignored the tenant would bill everybody for everything, and would look
        // plausible right up to the first invoice.
        var first = await NewOrganisationAsync();
        var second = await NewOrganisationAsync();

        await first.PostAsJsonAsync(
            "/v1/environments", new CreateEnvironmentRequest("dev"), ApiFactory.Json);

        var theirs = await second.GetFromJsonAsync<UsageResponse>("/v1/usage", ApiFactory.Json);

        Assert.Equal(0, theirs!.Environments.Used);
        Assert.Equal(1, theirs.Seats.Used);
    }

    [Fact]
    public async Task UsageNeedsOrgAdmin()
    {
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/usage")).StatusCode);
    }
}
