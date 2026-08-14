using System.Net;
using System.Net.Http.Json;
using Concordat.Application.Registry;

namespace Concordat.Api.IntegrationTests;

/// <summary>M7.3's contract routes, over real HTTP against real PostgreSQL.</summary>
[Collection(ApiCollection.Name)]
public class ContractApiTests(ApiFactory factory)
{
    private static string UniqueEnvironment() => $"env-{Guid.CreateVersion7():N}"[..24];

    private static string UniqueContract() => $"c-{Guid.CreateVersion7():N}"[..20];

    private static SubjectRefInput[] Subjects(params string[] entries) =>
        [.. entries.Select(e =>
        {
            var at = e.LastIndexOf('@');
            return new SubjectRefInput(e[..at], e[(at + 1)..]);
        })];

    private async Task<(HttpClient Client, string Environment)> NewEnvironmentAsync()
    {
        var client = factory.CreateClient();
        var name = UniqueEnvironment();

        var response = await client.PostAsJsonAsync(
            "/v1/environments", new CreateEnvironmentRequest(name), ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (client, name);
    }

    private static async Task<ContractResponse> CreateAsync(
        HttpClient client, string environment, string name, string? enforcement = null)
    {
        var response = await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/contracts",
            new CreateContractRequest(name, enforcement),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ApiFactory.ReadAsync<ContractResponse>(response);
    }

    private static Task<HttpResponseMessage> AddPublishAsync(
        HttpClient client,
        string environment,
        string contract,
        string exchange,
        string pattern,
        string[] subjects,
        int? precedence = null) =>
        client.PostAsJsonAsync(
            $"/v1/environments/{environment}/contracts/{contract}/publishes",
            new AddPublishBindingRequest(
                exchange, pattern, Subjects(subjects), Precedence: precedence),
            ApiFactory.Json);

    [Fact]
    public async Task ANewContractDefaultsToMonitor()
    {
        // A contract that started blocking the moment it was written would be authored by
        // guessing and then discovered in production.
        var (client, environment) = await NewEnvironmentAsync();

        var created = await CreateAsync(client, environment, UniqueContract());

        Assert.Equal("MONITOR", created.Enforcement);
        Assert.Empty(created.Publishes);
        Assert.Empty(created.Consumes);
    }

    [Fact]
    public async Task ABindingComesBackWithItsSubjectsAndSelectors()
    {
        var (client, environment) = await NewEnvironmentAsync();
        var contract = UniqueContract();
        await CreateAsync(client, environment, contract);

        var added = await AddPublishAsync(
            client, environment, contract, "orders", "orders.*.created",
            ["acme.Created@latest", "acme.Other@>=2"]);

        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var body = await ApiFactory.ReadAsync<ContractResponse>(added);

        var binding = Assert.Single(body.Publishes);
        Assert.Equal("orders", binding.Exchange);
        Assert.Equal("orders.*.created", binding.RoutingKeyPattern);
        Assert.Equal("/", binding.VirtualHost);
        Assert.Null(binding.BrokerId);
        Assert.Equal(
            ["acme.Created@latest", "acme.Other@>=2"],
            binding.Subjects.Select(s => $"{s.Subject}@{s.Selector}"));
    }

    [Fact]
    public async Task AnOverlappingBindingWithDifferentSubjectsIs409()
    {
        // Overlap is intersection, not text equality: 'orders.*' and '*.created' both match
        // 'orders.created', so a publisher would have no way to know which one governs it.
        var (client, environment) = await NewEnvironmentAsync();
        var contract = UniqueContract();
        await CreateAsync(client, environment, contract);

        await AddPublishAsync(
            client, environment, contract, "orders", "orders.*", ["acme.A@latest"]);

        var clash = await AddPublishAsync(
            client, environment, contract, "orders", "*.created", ["acme.B@latest"]);

        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);

        var problem = await ApiFactory.ReadProblemAsync(clash);
        Assert.Equal("binding_conflict", problem.ConcordatCode);

        // The message has to name a key that both patterns match, or the author is left to work
        // out the intersection of two topic patterns by hand.
        Assert.Contains("orders.created", problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOverlappingBindingIsAcceptedOncePrecedenceSeparatesThem()
    {
        var (client, environment) = await NewEnvironmentAsync();
        var contract = UniqueContract();
        await CreateAsync(client, environment, contract);

        await AddPublishAsync(
            client, environment, contract, "orders", "orders.*", ["acme.A@latest"], precedence: 1);

        var narrower = await AddPublishAsync(
            client, environment, contract, "orders", "orders.created", ["acme.B@latest"],
            precedence: 10);

        Assert.Equal(HttpStatusCode.Created, narrower.StatusCode);
    }

    [Fact]
    public async Task ABindingWithNoSubjectsIs400()
    {
        var (client, environment) = await NewEnvironmentAsync();
        var contract = UniqueContract();
        await CreateAsync(client, environment, contract);

        var response = await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/contracts/{contract}/publishes",
            new AddPublishBindingRequest("orders", "orders.*", []),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AnInvalidRoutingKeyPatternIs400WithItsOwnCode()
    {
        var (client, environment) = await NewEnvironmentAsync();
        var contract = UniqueContract();
        await CreateAsync(client, environment, contract);

        var response = await AddPublishAsync(
            client, environment, contract, "orders", "orders..created", ["acme.A@latest"]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "routing_key_pattern_invalid",
            (await ApiFactory.ReadProblemAsync(response)).ConcordatCode);
    }

    [Fact]
    public async Task TheSameContractNameTwiceIs409()
    {
        var (client, environment) = await NewEnvironmentAsync();
        var contract = UniqueContract();
        await CreateAsync(client, environment, contract);

        var again = await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/contracts",
            new CreateContractRequest(contract),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task AnUnknownContractIs404AndAnUnknownEnvironmentIsToo()
    {
        var (client, environment) = await NewEnvironmentAsync();

        var contract = await client.GetAsync(
            $"/v1/environments/{environment}/contracts/{UniqueContract()}");
        var env = await client.GetAsync(
            $"/v1/environments/{UniqueEnvironment()}/contracts");

        Assert.Equal(HttpStatusCode.NotFound, contract.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, env.StatusCode);
    }

    [Fact]
    public async Task EnforcementCanBeRaisedAndTheChangeSticks()
    {
        var (client, environment) = await NewEnvironmentAsync();
        var contract = UniqueContract();
        await CreateAsync(client, environment, contract);

        var changed = await client.PutAsJsonAsync(
            $"/v1/environments/{environment}/contracts/{contract}/enforcement",
            new SetEnforcementRequest("ENFORCE"),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal("ENFORCE", (await ApiFactory.ReadAsync<ContractResponse>(changed)).Enforcement);

        var reread = await client.GetFromJsonAsync<ContractResponse>(
            $"/v1/environments/{environment}/contracts/{contract}", ApiFactory.Json);

        Assert.Equal("ENFORCE", reread!.Enforcement);
    }

    [Fact]
    public async Task AnUnknownEnforcementModeIs400()
    {
        var (client, environment) = await NewEnvironmentAsync();
        var contract = UniqueContract();
        await CreateAsync(client, environment, contract);

        var response = await client.PutAsJsonAsync(
            $"/v1/environments/{environment}/contracts/{contract}/enforcement",
            new SetEnforcementRequest("STRICT"),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ListingReturnsOnlyThisEnvironmentsContracts()
    {
        var (client, mine) = await NewEnvironmentAsync();
        var (_, theirs) = await NewEnvironmentAsync();

        await CreateAsync(client, mine, UniqueContract());
        await CreateAsync(client, theirs, UniqueContract());

        var listed = await client.GetFromJsonAsync<IReadOnlyList<ContractResponse>>(
            $"/v1/environments/{mine}/contracts", ApiFactory.Json);

        Assert.Single(listed!);
    }

    // ------------------------------------------------------------------------- resolve

    [Fact]
    public async Task ResolveAnswersAMatchedRouteWithItsSubjects()
    {
        var (client, environment) = await NewEnvironmentAsync();
        var contract = UniqueContract();
        await CreateAsync(client, environment, contract, "ENFORCE");

        await AddPublishAsync(
            client, environment, contract, "orders", "orders.#", ["acme.Created@latest"]);

        var response = await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/contracts/resolve",
            new ResolveContractsRequest(
                Publishes: [new PublishTargetRequest("orders", "orders.eu.created")]),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ApiFactory.ReadAsync<ResolveContractsResponse>(response);

        var resolved = Assert.Single(body.Publishes);
        Assert.Equal([contract], resolved.Contracts);
        Assert.Equal("ENFORCE", resolved.Enforcement);
        Assert.Equal("acme.Created", Assert.Single(resolved.Subjects).Subject);
    }

    [Fact]
    public async Task ResolveAnswersAnUngovernedRouteRatherThanOmittingIt()
    {
        // The SDK has to tell "nothing governs this" from "I forgot to ask". Dropping the entry
        // would make those indistinguishable, and the answers are positional.
        var (client, environment) = await NewEnvironmentAsync();
        var contract = UniqueContract();
        await CreateAsync(client, environment, contract);

        await AddPublishAsync(
            client, environment, contract, "orders", "orders.#", ["acme.Created@latest"]);

        var response = await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/contracts/resolve",
            new ResolveContractsRequest(
                Publishes:
                [
                    new PublishTargetRequest("payments", "payments.settled"),
                    new PublishTargetRequest("orders", "orders.created"),
                ],
                Consumes: ["nobody.listens.here"]),
            ApiFactory.Json);

        var body = await ApiFactory.ReadAsync<ResolveContractsResponse>(response);

        Assert.Equal(2, body.Publishes.Count);
        Assert.Empty(body.Publishes[0].Contracts);
        Assert.Equal("OFF", body.Publishes[0].Enforcement);
        Assert.Empty(body.Publishes[0].Subjects);

        Assert.Equal([contract], body.Publishes[1].Contracts);

        var consume = Assert.Single(body.Consumes);
        Assert.Empty(consume.Contracts);
        Assert.Equal("OFF", consume.Enforcement);
    }

    [Fact]
    public async Task ResolveRespectsTheVirtualHostAndBrokerScope()
    {
        // A binding scoped to one vhost must not answer for another, or a staging queue would
        // inherit production's contract because they share a name.
        var (client, environment) = await NewEnvironmentAsync();
        var contract = UniqueContract();
        await CreateAsync(client, environment, contract);

        var scoped = await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/contracts/{contract}/consumes",
            new AddConsumeBindingRequest(
                "orders.q", Subjects("acme.Created@latest"), VirtualHost: "/tenant-a"),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, scoped.StatusCode);

        var matching = await ApiFactory.ReadAsync<ResolveContractsResponse>(
            await client.PostAsJsonAsync(
                $"/v1/environments/{environment}/contracts/resolve",
                new ResolveContractsRequest(VirtualHost: "/tenant-a", Consumes: ["orders.q"]),
                ApiFactory.Json));

        var elsewhere = await ApiFactory.ReadAsync<ResolveContractsResponse>(
            await client.PostAsJsonAsync(
                $"/v1/environments/{environment}/contracts/resolve",
                new ResolveContractsRequest(VirtualHost: "/tenant-b", Consumes: ["orders.q"]),
                ApiFactory.Json));

        Assert.Equal([contract], matching.Consumes[0].Contracts);
        Assert.Empty(elsewhere.Consumes[0].Contracts);
    }

    [Fact]
    public async Task ResolveWithNothingToResolveIsAnEmptyAnswerNotAnError()
    {
        var (client, environment) = await NewEnvironmentAsync();

        var response = await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/contracts/resolve",
            new ResolveContractsRequest(),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ApiFactory.ReadAsync<ResolveContractsResponse>(response);

        Assert.Empty(body.Publishes);
        Assert.Empty(body.Consumes);
    }

    [Fact]
    public async Task ResolveOnAnUnknownEnvironmentIs404()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/v1/environments/{UniqueEnvironment()}/contracts/resolve",
            new ResolveContractsRequest(),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ResolveIsNotShadowedByTheContractNameRoute()
    {
        // '/contracts/resolve' and '/contracts/{contract}' both match the same path. If the
        // parameterised route won, resolve would 404 as a missing contract named 'resolve'.
        var (client, environment) = await NewEnvironmentAsync();

        var response = await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/contracts/resolve",
            new ResolveContractsRequest(),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
