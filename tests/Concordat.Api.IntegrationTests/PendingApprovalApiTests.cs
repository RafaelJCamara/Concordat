using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Concordat.Api.IntegrationTests;

/// <summary>
/// <c>GET /v1/environments/{env}/approvals</c> over real HTTP against real PostgreSQL (ADR-042).
/// </summary>
/// <remarks>
/// The ordering and the arithmetic are proved against the reader in
/// <c>PendingApprovalReaderTests</c>. What is left for HTTP is the wire a browser reads, how the
/// query string is taken, that the decision a person makes through the API changes the queue, and
/// that the route answers the same in both hosting environments.
/// </remarks>
[Collection(ApiCollection.Name)]
public class PendingApprovalApiTests(ApiFactory factory)
{
    private const string V1 = """{"type":"object","properties":{"id":{"type":"string"}}}""";

    // Adds 'id' to required: backward-breaking, so it is held at the gate.
    private const string Breaking =
        """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}""";

    private static string UniqueEnvironment() => $"env-{Guid.NewGuid():N}"[..24];

    private static string UniqueSubject() => $"acme.queue.S{Guid.NewGuid():N}"[..32];

    private static async Task CreateEnvironmentAsync(HttpClient client, string environment)
    {
        var response = await client.PostAsJsonAsync(
            "/v1/environments", new CreateEnvironmentRequest(environment), ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task<string> CreateSubjectAsync(HttpClient client, string environment)
    {
        var name = UniqueSubject();
        var response = await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/subjects",
            new CreateSubjectRequest(name, "json", "alice"),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return name;
    }

    private static async Task<RegisterVersionResponse> RegisterAsync(
        HttpClient client,
        string environment,
        string subject,
        string schema,
        string? semanticVersion = null,
        string? changelog = null)
    {
        var response = await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/subjects/{subject}/versions",
            new RegisterVersionRequest(schema, semanticVersion, changelog, "alice"),
            ApiFactory.Json);

        return await ApiFactory.ReadAsync<RegisterVersionResponse>(response);
    }

    /// <summary>An environment with <paramref name="held"/> subjects, each holding one breaking change.</summary>
    private static async Task<(string Environment, List<string> Subjects)> HoldAsync(
        HttpClient client, int held)
    {
        var environment = UniqueEnvironment();
        await CreateEnvironmentAsync(client, environment);

        var subjects = new List<string>();

        for (var i = 0; i < held; i++)
        {
            var subject = await CreateSubjectAsync(client, environment);
            await RegisterAsync(client, environment, subject, V1);
            var proposal = await RegisterAsync(
                client, environment, subject, Breaking, semanticVersion: "2.0.0", changelog: "required id");
            Assert.Equal("AWAITING_APPROVAL", proposal.Status);
            subjects.Add(subject);
        }

        return (environment, subjects);
    }

    private static async Task<JsonElement> GetQueueAsync(
        HttpClient client, string environment, string query = "")
    {
        var response = await client.GetAsync($"/v1/environments/{environment}/approvals{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Raw JSON, not the response record: deserialising into the type that produced the body
        // would pass whatever the property names were.
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    private WebApplicationFactory<Program> AsProduction() =>
        factory.WithWebHostBuilder(b => b
            .UseEnvironment("Production")
            .UseSetting(
                "ConnectionStrings:Concordat",
                "Host=placeholder;Database=never;Username=never;Password=never"));

    [Fact]
    public async Task TheQueueIsTheSameHostedAsProductionAndAsDevelopment()
    {
        var client = factory.CreateClient();
        var (environment, _) = await HoldAsync(client, 2);

        var development = await client.GetStringAsync($"/v1/environments/{environment}/approvals");

        using var production = AsProduction();
        Assert.Equal(
            "Production",
            production.Services.GetRequiredService<IHostEnvironment>().EnvironmentName);

        var inProduction = await production.CreateClient()
            .GetStringAsync($"/v1/environments/{environment}/approvals");

        Assert.Equal(development, inProduction);
    }

    [Fact]
    public async Task TheQueueReportsWhatWasProposed_InTheWordsABrowserReads()
    {
        var client = factory.CreateClient();
        var (environment, subjects) = await HoldAsync(client, 2);

        var root = await GetQueueAsync(client, environment);

        Assert.Equal(2, root.GetProperty("total").GetInt32());

        var items = root.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(subjects, items.Select(i => i.GetProperty("subject").GetString()));

        var first = items[0];
        Assert.Equal(2, first.GetProperty("ordinal").GetInt32());
        Assert.Equal("2.0.0", first.GetProperty("semanticVersion").GetString());
        Assert.Equal("required id", first.GetProperty("changelog").GetString());
        Assert.Equal(1, first.GetProperty("baseline").GetInt32());
        Assert.Equal(JsonValueKind.String, first.GetProperty("registeredAt").ValueKind);
        Assert.Equal("unclaimed-instance", first.GetProperty("registeredBy").GetString());
    }

    [Fact]
    public async Task APageIsTakenFromTheQueryString_AndTotalStaysTheWholeQueue()
    {
        var client = factory.CreateClient();
        var (environment, subjects) = await HoldAsync(client, 3);

        var page = await GetQueueAsync(client, environment, "?limit=2&offset=1");

        Assert.Equal(3, page.GetProperty("total").GetInt32());
        Assert.Equal(
            subjects.Skip(1),
            page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("subject").GetString()));
    }

    [Theory]
    [InlineData("?limit=0")]
    [InlineData("?limit=-3")]
    [InlineData("?limit=100000")]
    [InlineData("?offset=-4")]
    public async Task AnOutOfRangePageIsClampedAndNeverRefused(string query)
    {
        var client = factory.CreateClient();
        var (environment, _) = await HoldAsync(client, 1);

        var response = await client.GetAsync($"/v1/environments/{environment}/approvals{query}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ADecisionTakesTheVersionOutOfTheQueue()
    {
        var client = factory.CreateClient();
        var (environment, subjects) = await HoldAsync(client, 2);

        var decided = await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/subjects/{subjects[0]}/versions/2/reject",
            new DecideVersionRequest("bob"),
            ApiFactory.Json);
        Assert.Equal(HttpStatusCode.OK, decided.StatusCode);

        var root = await GetQueueAsync(client, environment);

        Assert.Equal(1, root.GetProperty("total").GetInt32());
        Assert.Equal(
            [subjects[1]],
            root.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("subject").GetString()));
    }

    [Fact]
    public async Task AnEnvironmentNothingWasWrittenTo_AnswersAnEmptyQueueAndNot404()
    {
        var root = await GetQueueAsync(factory.CreateClient(), UniqueEnvironment());

        Assert.Equal(0, root.GetProperty("total").GetInt32());
        Assert.Equal(0, root.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task AnotherEnvironmentsProposalsAreNotInThisQueue()
    {
        var client = factory.CreateClient();
        var (busy, _) = await HoldAsync(client, 1);
        var quiet = UniqueEnvironment();
        await CreateEnvironmentAsync(client, quiet);

        var root = await GetQueueAsync(client, quiet);

        Assert.Equal(0, root.GetProperty("total").GetInt32());
        Assert.Equal(1, (await GetQueueAsync(client, busy)).GetProperty("total").GetInt32());
    }
}
