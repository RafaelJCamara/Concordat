using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Concordat.Application.Registry;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Concordat.Api.IntegrationTests;

/// <summary>
/// <c>GET /v1/environments/{env}/summary</c> over real HTTP against real PostgreSQL (ADR-037).
/// </summary>
/// <remarks>
/// <para>
/// The arithmetic is proved against the reader in <c>EnvironmentSummaryReaderTests</c>, where a
/// timestamp can be chosen. What is left for HTTP is what only HTTP can get wrong: the wire
/// names and tokens a browser reads, the status for an environment nothing was written to, that
/// the route is the same in both hosting environments, and that one environment's numbers are
/// not another's when a person drives the whole thing through the API.
/// </para>
/// <para>
/// Every test creates its own environment, so no count here depends on what else has run.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public class EnvironmentSummaryApiTests(ApiFactory factory)
{
    private const string V1 = """{"type":"object","properties":{"id":{"type":"string"}}}""";

    // Adds 'id' to required, which is backward-breaking: documents written under V1 that omit it
    // stop validating (the same arrangement ApprovalGateTests uses).
    private const string Breaking =
        """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}""";

    private static string UniqueEnvironment() => $"env-{Guid.NewGuid():N}"[..24];

    private static string UniqueSubject() => $"acme.summary.S{Guid.NewGuid():N}"[..32];

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
        HttpClient client, string environment, string subject, string schema)
    {
        var response = await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/subjects/{subject}/versions",
            new RegisterVersionRequest(schema, null, null, "alice"),
            ApiFactory.Json);

        return await ApiFactory.ReadAsync<RegisterVersionResponse>(response);
    }

    private static async Task AddContractAsync(
        HttpClient client, string environment, string enforcement, int publishes, int consumes)
    {
        var name = $"c-{Guid.NewGuid():N}"[..16];

        var created = await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/contracts",
            new CreateContractRequest(name, enforcement),
            ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        for (var i = 0; i < publishes; i++)
        {
            var added = await client.PostAsJsonAsync(
                $"/v1/environments/{environment}/contracts/{name}/publishes",
                new AddPublishBindingRequest(
                    $"ex-{i}", "orders.created", [new SubjectRefInput("acme.Order", "latest")]),
                ApiFactory.Json);
            Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        }

        for (var i = 0; i < consumes; i++)
        {
            var added = await client.PostAsJsonAsync(
                $"/v1/environments/{environment}/contracts/{name}/consumes",
                new AddConsumeBindingRequest(
                    $"queue-{i}", [new SubjectRefInput("acme.Order", "latest")]),
                ApiFactory.Json);
            Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        }
    }

    private static async Task ReportViolationAsync(
        HttpClient client, string environment, string route)
    {
        var response = await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/violations",
            new ReportViolationsRequest(
                "orders-api",
                [new ViolationReportRequest("PUBLISH", route, "acme.Order", "payload_invalid", "bad", 3)]),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>One environment with every number non-trivial, and the names of what was made.</summary>
    private static async Task<(string Environment, string Held, string Rejected, string Bare)>
        PopulateAsync(HttpClient client)
    {
        var environment = UniqueEnvironment();
        await CreateEnvironmentAsync(client, environment);

        // Held: a breaking change nobody has decided.
        var held = await CreateSubjectAsync(client, environment);
        await RegisterAsync(client, environment, held, V1);
        Assert.Equal(
            "AWAITING_APPROVAL", (await RegisterAsync(client, environment, held, Breaking)).Status);

        // Rejected: a breaking change somebody declined. Registered after the one above, so it is
        // the newer proposal.
        var rejected = await CreateSubjectAsync(client, environment);
        await RegisterAsync(client, environment, rejected, V1);
        var proposal = await RegisterAsync(client, environment, rejected, Breaking);
        Assert.Equal("AWAITING_APPROVAL", proposal.Status);

        var decided = await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/subjects/{rejected}/versions/{proposal.Ordinal}/reject",
            new DecideVersionRequest("bob"),
            ApiFactory.Json);
        Assert.Equal(HttpStatusCode.OK, decided.StatusCode);

        // The newest subject, with nothing registered yet.
        var bare = await CreateSubjectAsync(client, environment);

        await AddContractAsync(client, environment, "ENFORCE", publishes: 2, consumes: 1);
        await AddContractAsync(client, environment, "MONITOR", publishes: 0, consumes: 0);
        await ReportViolationAsync(client, environment, "orders/created");

        return (environment, held, rejected, bare);
    }


    /// <summary>
    /// A host built as Production over the same database.
    /// </summary>
    /// <remarks>
    /// Production refuses to start without a connection string rather than guess one, so the
    /// factory is given a placeholder: <c>ApiFactory.CreateHost</c> replaces the context's options
    /// with the container's after configuration is read, so the placeholder is only what lets the
    /// startup check pass and is never connected to.
    /// </remarks>
    private WebApplicationFactory<Program> AsProduction() =>
        factory.WithWebHostBuilder(b => b
            .UseEnvironment("Production")
            .UseSetting("ConnectionStrings:Concordat", "Host=placeholder;Database=never;Username=never;Password=never"));

    [Fact]
    public async Task ThisFixtureHostsAsDevelopment_AndTheProductionHalfIsProvedBelow()
    {
        // Named so that the day WebApplicationFactory stops hosting as Development this file says
        // so, rather than quietly proving one half of "the same in both" (UnreadableBodyTests
        // makes the same promise for the same reason).
        Assert.Equal("Development", factory.Services
            .GetRequiredService<IHostEnvironment>().EnvironmentName);

        using var production = AsProduction();
        Assert.Equal("Production", production.Services
            .GetRequiredService<IHostEnvironment>().EnvironmentName);

        await Task.CompletedTask;
    }

    [Fact]
    public async Task TheOverviewReportsWhatWasArranged_InTheWordsABrowserReads()
    {
        var client = factory.CreateClient();
        var (environment, held, rejected, bare) = await PopulateAsync(client);

        var response = await client.GetAsync($"/v1/environments/{environment}/summary");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Read as raw JSON, not through the response record: deserialising into the type that
        // produced the body would pass whatever the property names were.
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        Assert.Equal(3, root.GetProperty("subjects").GetProperty("total").GetInt32());
        Assert.Equal(3, root.GetProperty("subjects").GetProperty("active").GetInt32());
        Assert.Equal(0, root.GetProperty("subjects").GetProperty("retired").GetInt32());

        var versions = root.GetProperty("versions");
        Assert.Equal(4, versions.GetProperty("total").GetInt32());
        Assert.Equal(2, versions.GetProperty("active").GetInt32());
        Assert.Equal(1, versions.GetProperty("awaitingApproval").GetInt32());
        Assert.Equal(1, versions.GetProperty("rejected").GetInt32());
        Assert.Equal(0, versions.GetProperty("dismissed").GetInt32());

        var newest = root.GetProperty("newestSubjects").EnumerateArray().ToList();
        Assert.Equal([bare, rejected, held], newest.Select(s => s.GetProperty("name").GetString()));
        Assert.Equal("json", newest[0].GetProperty("format").GetString());
        Assert.Equal("ACTIVE", newest[0].GetProperty("lifecycle").GetString());
        Assert.Equal(JsonValueKind.Null, newest[0].GetProperty("latest").ValueKind);
        Assert.Equal(1, newest[2].GetProperty("latest").GetInt32());
        Assert.Equal(2, newest[2].GetProperty("versions").GetInt32());
        Assert.Equal(1, newest[2].GetProperty("awaitingApproval").GetInt32());

        var breaking = root.GetProperty("breakingChanges").EnumerateArray().ToList();
        Assert.Equal([rejected, held], breaking.Select(b => b.GetProperty("subject").GetString()));
        Assert.Equal("REJECTED", breaking[0].GetProperty("status").GetString());
        // The authenticated caller, not the "bob" the request body names: an unclaimed instance
        // answers every request as this actor (M8.2), and the registry attributes the decision to it.
        Assert.Equal("unclaimed-instance", breaking[0].GetProperty("decidedBy").GetString());
        Assert.Equal(2, breaking[0].GetProperty("ordinal").GetInt32());
        Assert.Equal("AWAITING_APPROVAL", breaking[1].GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, breaking[1].GetProperty("decidedAt").ValueKind);

        var enforcement = root.GetProperty("enforcement").EnumerateArray().ToList();
        Assert.Equal(
            ["OFF", "MONITOR", "ENFORCE"], enforcement.Select(e => e.GetProperty("mode").GetString()));
        Assert.Equal([0, 1, 1], enforcement.Select(e => e.GetProperty("contracts").GetInt32()));
        Assert.Equal([0, 0, 2], enforcement.Select(e => e.GetProperty("publishBindings").GetInt32()));
        Assert.Equal([0, 0, 1], enforcement.Select(e => e.GetProperty("consumeBindings").GetInt32()));

        var violations = root.GetProperty("violations");
        Assert.Equal(1, violations.GetProperty("distinct").GetInt32());
        Assert.Equal(1, violations.GetProperty("recent").GetInt32());
        Assert.Equal(24, violations.GetProperty("windowHours").GetInt32());
    }

    [Fact]
    public async Task AnEnvironmentNothingWasWrittenTo_AnswersZerosAndNot404()
    {
        // The same answer the subject list gives for it, and what the dashboard of a fresh
        // install lands on: an error there would greet a new operator with a failure at the
        // moment the page should be offering to create the first subject.
        var response = await factory.CreateClient()
            .GetAsync($"/v1/environments/{UniqueEnvironment()}/summary");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var summary = await ApiFactory.ReadAsync<EnvironmentSummaryResponse>(response);

        Assert.Equal(0, summary.Subjects.Total);
        Assert.Equal(0, summary.Versions.Total);
        Assert.Empty(summary.NewestSubjects);
        Assert.Empty(summary.BreakingChanges);
        Assert.Equal(["OFF", "MONITOR", "ENFORCE"], summary.Enforcement.Select(e => e.Mode));
        Assert.Equal(0, summary.Violations.Distinct);
    }

    [Fact]
    public async Task AnotherEnvironmentInTheSameTenantContributesNothing()
    {
        var client = factory.CreateClient();
        var (populated, _, _, _) = await PopulateAsync(client);

        var other = UniqueEnvironment();
        await CreateEnvironmentAsync(client, other);
        var lonely = await CreateSubjectAsync(client, other);
        await RegisterAsync(client, other, lonely, V1);

        var summary = await ApiFactory.ReadAsync<EnvironmentSummaryResponse>(
            await client.GetAsync($"/v1/environments/{other}/summary"));

        Assert.Equal(1, summary.Subjects.Total);
        Assert.Equal(1, summary.Versions.Total);
        Assert.Equal([lonely], summary.NewestSubjects.Select(s => s.Name));
        Assert.Empty(summary.BreakingChanges);
        Assert.All(summary.Enforcement, e => Assert.Equal(0, e.Contracts));
        Assert.Equal(0, summary.Violations.Distinct);

        // And the populated one is as it was left.
        var again = await ApiFactory.ReadAsync<EnvironmentSummaryResponse>(
            await client.GetAsync($"/v1/environments/{populated}/summary"));

        Assert.Equal(3, again.Subjects.Total);
    }

    [Fact]
    public async Task TheOverviewIsIdenticalHostedAsProductionAndAsDevelopment()
    {
        // Development changes what an unhandled exception looks like, validates the container's
        // lifetimes when the host is built, and (for routes with bodies) whether a bad body is a
        // 400 or a 500. A read route has no body, so the realistic risk is the first two, and the
        // proof is the same either way: the same request, against the same database, answered
        // alike by a host built as each.
        var client = factory.CreateClient();
        var (environment, _, _, _) = await PopulateAsync(client);

        using var production = AsProduction();
        var asProduction = production.CreateClient();

        var dev = await client.GetAsync($"/v1/environments/{environment}/summary");
        var prod = await asProduction.GetAsync($"/v1/environments/{environment}/summary");

        Assert.Equal(HttpStatusCode.OK, dev.StatusCode);
        Assert.Equal(HttpStatusCode.OK, prod.StatusCode);
        Assert.Equal(
            await dev.Content.ReadAsStringAsync(), await prod.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task APostIsNotARoute()
    {
        // A read route answers reads. 405 rather than 404, so a client that tries to write the
        // overview is told the path is right and the verb is not.
        var response = await factory.CreateClient().PostAsJsonAsync(
            $"/v1/environments/{UniqueEnvironment()}/summary", new { }, ApiFactory.Json);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }
}

/// <summary>
/// The overview needs a caller who can read subjects, and nobody else (ADR-027, ADR-037).
/// </summary>
[Collection(AuthApiCollection.Name)]
public class EnvironmentSummaryAuthorizationTests(AuthApiFactory factory)
{
    private static string UniqueEnvironment() => $"env-{Guid.NewGuid():N}"[..24];

    private HttpClient WithCredential(string credential)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", credential);

        return client;
    }

    private async Task<string> KeyWithAsync(params string[] scopes)
    {
        var owner = WithCredential(await factory.OwnerCredentialAsync());

        var issued = await owner.PostAsJsonAsync(
            "/v1/api-keys",
            new IssueApiKeyRequest($"summary-{Guid.NewGuid():N}"[..20], scopes),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, issued.StatusCode);
        return (await ApiFactory.ReadAsync<IssuedApiKeyResponse>(issued)).Secret;
    }

    [Fact]
    public async Task AnAnonymousCallerIsUnauthorizedOnceTheInstanceIsClaimed()
    {
        await factory.OwnerCredentialAsync();

        var response = await factory.CreateClient()
            .GetAsync($"/v1/environments/{UniqueEnvironment()}/summary");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AKeyThatCannotReadSubjectsIsRefused_AndOneThatCanIsNot()
    {
        var environment = UniqueEnvironment();

        var contractsOnly = WithCredential(await KeyWithAsync("contract:read"));
        var refused = await contractsOnly.GetAsync($"/v1/environments/{environment}/summary");

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("insufficient_scope", (await ApiFactory.ReadProblemAsync(refused)).ConcordatCode);

        var reader = WithCredential(await KeyWithAsync("subject:read"));
        Assert.Equal(
            HttpStatusCode.OK, (await reader.GetAsync($"/v1/environments/{environment}/summary")).StatusCode);
    }

    [Fact]
    public async Task AReaderMemberCanOpenTheOverview()
    {
        var owner = WithCredential(await factory.OwnerCredentialAsync());
        var email = $"summary-{Guid.NewGuid():N}"[..20] + "@example.com";
        const string password = "another long password";

        var created = await owner.PostAsJsonAsync(
            "/v1/members", new CreateMemberRequest(email, password, "READER"), ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var signIn = await factory.CreateClient().PostAsJsonAsync(
            "/v1/auth/signin", new SignInRequest(email, password), ApiFactory.Json);
        var reader = WithCredential((await ApiFactory.ReadAsync<SignInResponse>(signIn)).Credential);

        var response = await reader.GetAsync($"/v1/environments/{UniqueEnvironment()}/summary");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

/// <summary>Two organisations on one deployment, and the overview (ADR-037, M9's exit criterion).</summary>
[Collection(CloudApiCollection.Name)]
public class CloudEnvironmentSummaryTests(CloudApiFactory factory)
{
    private static string UniqueSlug() => $"org-{Guid.NewGuid():N}"[..20];

    private HttpClient For(string credential)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", credential);

        return client;
    }

    [Fact]
    public async Task TwoOrganisationsWithAnEnvironmentOfTheSameNameSeeOnlyTheirOwnNumbers()
    {
        var first = For(await factory.NewOrganisationAsync(UniqueSlug()));
        var second = For(await factory.NewOrganisationAsync(UniqueSlug()));

        // The same environment name in both, which the tenant-aware id derivation makes two
        // environments: the case a leak would be hardest to see in, since the URLs are equal.
        foreach (var organisation in new[] { first, second })
        {
            var created = await organisation.PostAsJsonAsync(
                "/v1/environments", new CreateEnvironmentRequest("dev"), ApiFactory.Json);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        var subject = $"acme.cloud.S{Guid.NewGuid():N}"[..32];
        Assert.Equal(
            HttpStatusCode.Created,
            (await first.PostAsJsonAsync(
                "/v1/environments/dev/subjects",
                new CreateSubjectRequest(subject, "json", "alice"),
                ApiFactory.Json)).StatusCode);

        Assert.Equal(
            HttpStatusCode.Created,
            (await first.PostAsJsonAsync(
                "/v1/environments/dev/contracts",
                new CreateContractRequest("orders", "ENFORCE"),
                ApiFactory.Json)).StatusCode);

        Assert.Equal(
            HttpStatusCode.OK,
            (await first.PostAsJsonAsync(
                "/v1/environments/dev/violations",
                new ReportViolationsRequest(
                    "orders-api",
                    [new ViolationReportRequest("PUBLISH", "orders/created", subject, "payload_invalid", "bad", 1)]),
                ApiFactory.Json)).StatusCode);

        var mine = await ApiFactory.ReadAsync<EnvironmentSummaryResponse>(
            await first.GetAsync("/v1/environments/dev/summary"));
        var theirs = await ApiFactory.ReadAsync<EnvironmentSummaryResponse>(
            await second.GetAsync("/v1/environments/dev/summary"));

        Assert.Equal(1, mine.Subjects.Total);
        Assert.Equal([subject], mine.NewestSubjects.Select(s => s.Name));
        Assert.Equal(1, mine.Enforcement.Single(e => e.Mode == "ENFORCE").Contracts);
        Assert.Equal(1, mine.Violations.Distinct);

        Assert.Equal(0, theirs.Subjects.Total);
        Assert.Empty(theirs.NewestSubjects);
        Assert.All(theirs.Enforcement, e => Assert.Equal(0, e.Contracts));
        Assert.Equal(0, theirs.Violations.Distinct);
    }
}
