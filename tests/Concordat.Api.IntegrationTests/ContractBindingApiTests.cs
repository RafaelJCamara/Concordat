using System.Net;
using System.Net.Http.Json;
using Concordat.Application.Registry;

namespace Concordat.Api.IntegrationTests;

/// <summary>
/// Changing and removing a contract's bindings, over real HTTP against real PostgreSQL.
/// </summary>
/// <remarks>
/// A binding used to have no identity, so there was an <c>add</c> endpoint for each kind and
/// nothing else, and a binding added without a precedence could never be separated from a later
/// overlapping one: the registry's own refusal said to "give the two bindings different
/// precedence values" and the first had none to change (STATUS, ADR-035).
/// </remarks>
[Collection(ApiCollection.Name)]
public class ContractBindingApiTests(ApiFactory factory)
{
    private static string UniqueEnvironment() => $"env-{Guid.CreateVersion7():N}"[..24];

    private static string UniqueContract() => $"c-{Guid.CreateVersion7():N}"[..20];

    private static SubjectRefInput[] Subjects(params string[] entries) =>
        [.. entries.Select(e =>
        {
            var at = e.LastIndexOf('@');
            return new SubjectRefInput(e[..at], e[(at + 1)..]);
        })];

    private async Task<(HttpClient Client, string Environment, string Contract)> NewContractAsync()
    {
        var client = factory.CreateClient();
        var environment = UniqueEnvironment();
        var contract = UniqueContract();

        (await client.PostAsJsonAsync(
            "/v1/environments", new CreateEnvironmentRequest(environment), ApiFactory.Json))
            .EnsureSuccessStatusCode();

        (await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/contracts",
            new CreateContractRequest(contract),
            ApiFactory.Json)).EnsureSuccessStatusCode();

        return (client, environment, contract);
    }

    private static async Task<ContractResponse> AddPublishAsync(
        HttpClient client,
        string environment,
        string contract,
        string pattern,
        string[] subjects,
        int? precedence = null,
        string exchange = "orders")
    {
        var response = await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/contracts/{contract}/publishes",
            new AddPublishBindingRequest(
                exchange, pattern, Subjects(subjects), Precedence: precedence),
            ApiFactory.Json);

        return await ApiFactory.ReadAsync<ContractResponse>(response);
    }

    private static Task<HttpResponseMessage> ReplacePublishAsync(
        HttpClient client,
        string environment,
        string contract,
        Guid binding,
        string[] subjects,
        int? precedence = null) =>
        client.PutAsJsonAsync(
            $"/v1/environments/{environment}/contracts/{contract}/publishes/{binding}",
            new ReplacePublishBindingRequest(Subjects(subjects), precedence),
            ApiFactory.Json);

    private static Task<ContractResponse> GetAsync(
        HttpClient client, string environment, string contract) =>
        client.GetFromJsonAsync<ContractResponse>(
            $"/v1/environments/{environment}/contracts/{contract}", ApiFactory.Json)!;

    private static async Task<IReadOnlyList<AuditResponse>> AuditAsync(
        HttpClient client, string environment, string? action = null) =>
        (await client.GetFromJsonAsync<IReadOnlyList<AuditResponse>>(
            $"/v1/audit?env={environment}" + (action is null ? "" : $"&action={action}"),
            ApiFactory.Json))!;

    // ---------------------------------------------------------------------- identity

    [Fact]
    public async Task EveryBindingComesBackWithAnIdThatSurvivesAReadAndAnotherAdd()
    {
        var (client, environment, contract) = await NewContractAsync();

        var first = await AddPublishAsync(client, environment, contract, "orders.created", ["acme.A@latest"]);
        var firstId = Assert.Single(first.Publishes).Id;

        (await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/contracts/{contract}/consumes",
            new AddConsumeBindingRequest("q", Subjects("acme.A@latest")),
            ApiFactory.Json)).EnsureSuccessStatusCode();

        var second = await AddPublishAsync(client, environment, contract, "orders.updated", ["acme.A@latest"]);
        var reread = await GetAsync(client, environment, contract);

        Assert.NotEqual(Guid.Empty, firstId);
        Assert.Equal(
            [firstId, second.Publishes.Single(p => p.Id != firstId).Id],
            reread.Publishes.Select(p => p.Id));
        Assert.NotEqual(Guid.Empty, Assert.Single(reread.Consumes).Id);
        var everyId = reread.Publishes.Select(p => p.Id)
            .Concat(reread.Consumes.Select(c => c.Id)).ToList();

        Assert.Equal(3, everyId.Distinct().Count());
    }

    // ----------------------------------------------------------- the trap, and the way out

    [Fact]
    public async Task ABindingAddedWithoutAPrecedenceCanBeGivenOneAfterwards()
    {
        // The hole, step by step, over HTTP. The second binding is refused for overlapping the
        // first, whose precedence is unset, and the refusal says to give the two bindings
        // different precedence values.
        var (client, environment, contract) = await NewContractAsync();
        var fallback = (await AddPublishAsync(
            client, environment, contract, "orders.#", ["acme.Fallback@latest"])).Publishes.Single();

        var refused = await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/contracts/{contract}/publishes",
            new AddPublishBindingRequest(
                "orders", "orders.created", Subjects("acme.Created@latest"), Precedence: 10),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        var changed = await ReplacePublishAsync(
            client, environment, contract, fallback.Id, ["acme.Fallback@latest"], precedence: 1);

        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(1, Assert.Single((await ApiFactory.ReadAsync<ContractResponse>(changed)).Publishes).Precedence);

        var accepted = await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/contracts/{contract}/publishes",
            new AddPublishBindingRequest(
                "orders", "orders.created", Subjects("acme.Created@latest"), Precedence: 10),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
    }

    [Fact]
    public async Task AChangeIsStoredAndTheOtherFieldsAreUntouched()
    {
        var (client, environment, contract) = await NewContractAsync();
        var original = (await AddPublishAsync(
            client, environment, contract, "orders.*", ["acme.A@latest"], precedence: 3))
            .Publishes.Single();

        var response = await ReplacePublishAsync(
            client, environment, contract, original.Id, ["acme.B@2", "acme.C@>=1"], precedence: 7);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = Assert.Single((await GetAsync(client, environment, contract)).Publishes);
        Assert.Equal(original.Id, stored.Id);
        Assert.Equal("orders", stored.Exchange);
        Assert.Equal("orders.*", stored.RoutingKeyPattern);
        Assert.Equal("/", stored.VirtualHost);
        Assert.Equal(7, stored.Precedence);
        Assert.Equal(
            ["acme.B@2", "acme.C@>=1"], stored.Subjects.Select(s => $"{s.Subject}@{s.Selector}"));
    }

    [Fact]
    public async Task OmittingThePrecedenceClearsIt()
    {
        var (client, environment, contract) = await NewContractAsync();
        var binding = (await AddPublishAsync(
            client, environment, contract, "orders.*", ["acme.A@latest"], precedence: 3))
            .Publishes.Single();

        (await ReplacePublishAsync(
            client, environment, contract, binding.Id, ["acme.A@latest"])).EnsureSuccessStatusCode();

        Assert.Null(Assert.Single((await GetAsync(client, environment, contract)).Publishes).Precedence);
    }

    [Fact]
    public async Task AChangeThatWouldConflictIs409AndTheBindingIsAsItWas()
    {
        var (client, environment, contract) = await NewContractAsync();
        await AddPublishAsync(client, environment, contract, "orders.#", ["acme.Fallback@latest"], 1);
        var specific = (await AddPublishAsync(
            client, environment, contract, "orders.created", ["acme.Created@latest"], 10))
            .Publishes.Single(p => p.RoutingKeyPattern == "orders.created");

        var response = await ReplacePublishAsync(
            client, environment, contract, specific.Id, ["acme.Created@latest"], precedence: 1);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("binding_conflict", (await ApiFactory.ReadProblemAsync(response)).ConcordatCode);

        var stored = (await GetAsync(client, environment, contract))
            .Publishes.Single(p => p.Id == specific.Id);
        Assert.Equal(10, stored.Precedence);
    }

    [Fact]
    public async Task AChangeThatChangesNothingIs200AndWritesNothing()
    {
        var (client, environment, contract) = await NewContractAsync();
        var binding = (await AddPublishAsync(
            client, environment, contract, "orders.*", ["acme.A@latest"], precedence: 3))
            .Publishes.Single();
        var before = (await AuditAsync(client, environment, "CONTRACT_BINDING_CHANGED")).Count;

        var response = await ReplacePublishAsync(
            client, environment, contract, binding.Id, ["acme.A@latest"], precedence: 3);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(before, (await AuditAsync(client, environment, "CONTRACT_BINDING_CHANGED")).Count);
    }

    [Fact]
    public async Task RefusalsAreTheOnesTheOtherRoutesGive()
    {
        var (client, environment, contract) = await NewContractAsync();
        var binding = (await AddPublishAsync(
            client, environment, contract, "orders.*", ["acme.A@latest"])).Publishes.Single();

        var unknownBinding = await ReplacePublishAsync(
            client, environment, contract, Guid.CreateVersion7(), ["acme.A@latest"]);
        var unknownContract = await ReplacePublishAsync(
            client, environment, "no-such-contract", binding.Id, ["acme.A@latest"]);
        var noSubjects = await client.PutAsJsonAsync(
            $"/v1/environments/{environment}/contracts/{contract}/publishes/{binding.Id}",
            new ReplacePublishBindingRequest([], 1),
            ApiFactory.Json);
        var notAnId = await client.PutAsJsonAsync(
            $"/v1/environments/{environment}/contracts/{contract}/publishes/not-a-guid",
            new ReplacePublishBindingRequest(Subjects("acme.A@latest"), 1),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.NotFound, unknownBinding.StatusCode);
        Assert.Equal("binding_not_found", (await ApiFactory.ReadProblemAsync(unknownBinding)).ConcordatCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownContract.StatusCode);
        Assert.Equal("contract_not_found", (await ApiFactory.ReadProblemAsync(unknownContract)).ConcordatCode);
        Assert.Equal(HttpStatusCode.BadRequest, noSubjects.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, notAnId.StatusCode);
    }

    // ------------------------------------------------------------------------- removal

    [Fact]
    public async Task ARemovedBindingStopsGoverningItsRouteAndTheOthersAreUntouched()
    {
        // What the web confirmation will claim, measured: a route that nothing else governs
        // resolves as ungoverned (no contract, OFF) from the next resolution.
        var (client, environment, contract) = await NewContractAsync();
        var created = (await AddPublishAsync(
            client, environment, contract, "orders.created", ["acme.A@latest"])).Publishes.Single();
        await AddPublishAsync(client, environment, contract, "orders.updated", ["acme.B@latest"]);

        (await client.PutAsJsonAsync(
            $"/v1/environments/{environment}/contracts/{contract}/enforcement",
            new SetEnforcementRequest("ENFORCE"),
            ApiFactory.Json)).EnsureSuccessStatusCode();

        async Task<ResolvedBindingResponse> ResolveCreatedAsync()
        {
            var resolved = await ApiFactory.ReadAsync<ResolveContractsResponse>(
                await client.PostAsJsonAsync(
                    $"/v1/environments/{environment}/contracts/resolve",
                    new ResolveContractsRequest(
                        Publishes: [new PublishTargetRequest("orders", "orders.created")]),
                    ApiFactory.Json));

            return Assert.Single(resolved.Publishes);
        }

        var before = await ResolveCreatedAsync();
        Assert.Equal([contract], before.Contracts);
        Assert.Equal("ENFORCE", before.Enforcement);

        var removed = await client.DeleteAsync(
            $"/v1/environments/{environment}/contracts/{contract}/publishes/{created.Id}");

        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        var body = await ApiFactory.ReadAsync<ContractResponse>(removed);
        Assert.Equal("orders.updated", Assert.Single(body.Publishes).RoutingKeyPattern);

        var after = await ResolveCreatedAsync();
        Assert.Empty(after.Contracts);
        Assert.Equal("OFF", after.Enforcement);

        Assert.Equal("orders.updated", Assert.Single((await GetAsync(client, environment, contract)).Publishes).RoutingKeyPattern);
    }

    [Fact]
    public async Task ARouteAnotherBindingOfTheSameContractStillMatchesStaysGoverned()
    {
        // The removal sentence says "if nothing else governs them", and this is the "else": a
        // broader binding of the same contract that still matches the route.
        var (client, environment, contract) = await NewContractAsync();
        await AddPublishAsync(client, environment, contract, "orders.#", ["acme.Fallback@latest"], 1);
        var specific = (await AddPublishAsync(
            client, environment, contract, "orders.created", ["acme.Created@latest"], 10))
            .Publishes.Single(p => p.RoutingKeyPattern == "orders.created");

        (await client.DeleteAsync(
            $"/v1/environments/{environment}/contracts/{contract}/publishes/{specific.Id}"))
            .EnsureSuccessStatusCode();

        var resolved = await ApiFactory.ReadAsync<ResolveContractsResponse>(
            await client.PostAsJsonAsync(
                $"/v1/environments/{environment}/contracts/resolve",
                new ResolveContractsRequest(
                    Publishes: [new PublishTargetRequest("orders", "orders.created")]),
                ApiFactory.Json));

        var route = Assert.Single(resolved.Publishes);
        Assert.Equal([contract], route.Contracts);
        Assert.Equal("acme.Fallback", Assert.Single(route.Subjects).Subject);
    }

    [Fact]
    public async Task RemovingTwiceIs404TheSecondTime()
    {
        var (client, environment, contract) = await NewContractAsync();
        var binding = (await AddPublishAsync(
            client, environment, contract, "orders.created", ["acme.A@latest"])).Publishes.Single();
        var route = $"/v1/environments/{environment}/contracts/{contract}/publishes/{binding.Id}";

        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync(route)).StatusCode);

        var second = await client.DeleteAsync(route);

        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
        Assert.Equal("binding_not_found", (await ApiFactory.ReadProblemAsync(second)).ConcordatCode);
    }

    [Fact]
    public async Task ABindingIsNotRemovedThroughTheOtherKindsRoute()
    {
        var (client, environment, contract) = await NewContractAsync();
        var publish = (await AddPublishAsync(
            client, environment, contract, "orders.created", ["acme.A@latest"])).Publishes.Single();

        var wrongKind = await client.DeleteAsync(
            $"/v1/environments/{environment}/contracts/{contract}/consumes/{publish.Id}");

        Assert.Equal(HttpStatusCode.NotFound, wrongKind.StatusCode);
        Assert.Single((await GetAsync(client, environment, contract)).Publishes);
    }

    // ------------------------------------------------------------------------- consume

    [Fact]
    public async Task AConsumeBindingCanBeChangedAndRemoved()
    {
        var (client, environment, contract) = await NewContractAsync();
        var added = await ApiFactory.ReadAsync<ContractResponse>(
            await client.PostAsJsonAsync(
                $"/v1/environments/{environment}/contracts/{contract}/consumes",
                new AddConsumeBindingRequest("orders.q", Subjects("acme.A@latest")),
                ApiFactory.Json));
        var id = Assert.Single(added.Consumes).Id;
        var route = $"/v1/environments/{environment}/contracts/{contract}/consumes/{id}";

        var changed = await client.PutAsJsonAsync(
            route, new ReplaceConsumeBindingRequest(Subjects("acme.B@2")), ApiFactory.Json);

        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var stored = Assert.Single((await GetAsync(client, environment, contract)).Consumes);
        Assert.Equal("orders.q", stored.Queue);
        Assert.Equal(["acme.B@2"], stored.Subjects.Select(s => $"{s.Subject}@{s.Selector}"));

        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync(route)).StatusCode);
        Assert.Empty((await GetAsync(client, environment, contract)).Consumes);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync(route)).StatusCode);
    }

    // --------------------------------------------------------------------------- audit

    [Fact]
    public async Task ChangesAndRemovalsAreRecordedAsTheirOwnActionsAsTheCaller()
    {
        var (client, environment, contract) = await NewContractAsync();
        var binding = (await AddPublishAsync(
            client, environment, contract, "orders.created", ["acme.A@latest"])).Publishes.Single();

        (await ReplacePublishAsync(
            client, environment, contract, binding.Id, ["acme.A@latest"], precedence: 2))
            .EnsureSuccessStatusCode();
        (await client.DeleteAsync(
            $"/v1/environments/{environment}/contracts/{contract}/publishes/{binding.Id}"))
            .EnsureSuccessStatusCode();

        var all = await AuditAsync(client, environment);
        var created = Assert.Single(all, e => e.Action == "CONTRACT_CREATED");
        var changed = Assert.Single(await AuditAsync(client, environment, "CONTRACT_BINDING_CHANGED"));
        var removed = Assert.Single(await AuditAsync(client, environment, "CONTRACT_BINDING_REMOVED"));

        Assert.Equal(created.Actor, changed.Actor);
        Assert.Equal(created.Actor, removed.Actor);
        Assert.Equal(contract, changed.Target);
        Assert.Equal("publish orders / orders.created: precedence none -> 2", changed.Detail);
        Assert.Equal("publish orders / orders.created -> acme.A@latest", removed.Detail);
    }
}
