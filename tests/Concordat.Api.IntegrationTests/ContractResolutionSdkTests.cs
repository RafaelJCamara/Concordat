using System.Net;
using System.Net.Http.Json;
using Concordat.Application.Registry;
using Concordat.Client;
using Concordat.Domain.Registry;

namespace Concordat.Api.IntegrationTests;

/// <summary>
/// The SDK resolving contracts against the real registry (M7.3, closing M2.1's deferral).
/// </summary>
/// <remarks>
/// <para>
/// Driven through <see cref="ConcordatClient"/> rather than hand-written requests, because the
/// failure this is here to catch is the two sides disagreeing about the wire — a casing
/// difference, an omitted field, an array that does not come back in the order it was sent.
/// Each side's own tests pass happily while that is broken; only a test that uses both finds it.
/// </para>
/// <para>
/// The endpoint was built in M7.3 and described in its own summary as "what an SDK calls at
/// startup". Until this existed, nothing called it.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public class ContractResolutionSdkTests(ApiFactory factory)
{
    private static string UniqueEnvironment() => $"env-{Guid.CreateVersion7():N}"[..24];

    private static string UniqueContract() => $"c-{Guid.CreateVersion7():N}"[..20];

    private static SubjectRefInput[] Subjects(params string[] entries) =>
        [.. entries.Select(e =>
        {
            var at = e.LastIndexOf('@');
            return new SubjectRefInput(e[..at], e[(at + 1)..]);
        })];

    /// <summary>An environment with one contract governing <c>orders / order.created</c>.</summary>
    private async Task<(HttpClient Http, string Environment, string Contract)> GovernedAsync(
        string enforcement = "ENFORCE", string pattern = "order.created")
    {
        var http = factory.CreateClient();
        var environment = UniqueEnvironment();
        var contract = UniqueContract();

        var created = await http.PostAsJsonAsync(
            "/v1/environments", new CreateEnvironmentRequest(environment), ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var madeContract = await http.PostAsJsonAsync(
            $"/v1/environments/{environment}/contracts",
            new CreateContractRequest(contract, enforcement),
            ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Created, madeContract.StatusCode);

        var bound = await http.PostAsJsonAsync(
            $"/v1/environments/{environment}/contracts/{contract}/publishes",
            new AddPublishBindingRequest(
                "orders", pattern, Subjects("acme.Order@latest", "acme.OrderAmended@>=2")),
            ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Created, bound.StatusCode);

        return (http, environment, contract);
    }

    private static ConcordatClient Sdk(
        HttpClient http, string environment, Action<ConcordatClientOptions>? configure = null)
    {
        var options = new ConcordatClientOptions
        {
            BaseAddress = http.BaseAddress,
            Environment = environment,
            WarmUpJitter = TimeSpan.Zero,
        };

        configure?.Invoke(options);

        return new ConcordatClient(http, options);
    }

    [Fact]
    public async Task TheSdkReadsAContractTheRegistryWrote()
    {
        var (http, environment, contract) = await GovernedAsync();
        using var sdk = Sdk(http, environment);

        var route = await sdk.GetPublishRouteAsync(new PublishRoute("orders", "order.created"));

        Assert.True(route.IsGoverned);
        Assert.Equal(contract, route.Contract);
        Assert.Equal(EnforcementMode.Enforce, route.Enforcement);

        // Selectors survive the round trip in both spellings. `>=2` is the one that would break
        // silently: an unparsed selector is dropped, and a dropped subject reads as "not
        // permitted on this route" — a violation reported against a perfectly correct publisher.
        Assert.Collection(
            route.Subjects.OrderBy(s => s.Subject.Value, StringComparer.Ordinal),
            first =>
            {
                Assert.Equal("acme.Order", first.Subject.Value);
                Assert.Equal("latest", first.Selector.ToString());
            },
            second =>
            {
                Assert.Equal("acme.OrderAmended", second.Subject.Value);
                Assert.Equal(">=2", second.Selector.ToString());
            });
    }

    [Fact]
    public async Task AnUngovernedRouteComesBackAnsweredRatherThanOmitted()
    {
        var (http, environment, _) = await GovernedAsync();
        using var sdk = Sdk(http, environment);

        var route = await sdk.GetPublishRouteAsync(new PublishRoute("orders", "order.archived"));

        // The SDK has to tell "nothing governs this" from "I forgot to ask". An omitted entry
        // would be indistinguishable from a short response, which the client refuses wholesale.
        Assert.False(route.IsGoverned);
        Assert.Null(route.Contract);
        Assert.Empty(route.Subjects);
    }

    [Fact]
    public async Task AWholeTopologyResolvesInOneRequestAndKeepsItsOrder()
    {
        var (http, environment, contract) = await GovernedAsync();

        // Governed second, not first, so that an implementation which quietly returned only the
        // matches — or returned them sorted — would misalign rather than accidentally agree.
        using var sdk = Sdk(http, environment, o =>
        {
            o.PublishRoutes.Add(new PublishRoute("orders", "order.archived"));
            o.PublishRoutes.Add(new PublishRoute("orders", "order.created"));
            o.PublishRoutes.Add(new PublishRoute("orders", "order.cancelled"));
            o.ConsumeQueues.Add("orders-worker");
        });

        var status = await sdk.WarmUpAsync();

        Assert.Equal(4, status.RoutesResolved);
        Assert.Equal(1, status.GovernedRoutes);

        // The positional guarantee, verified against the server rather than assumed. If order
        // were not preserved, this is the assertion that fails — and it fails here rather than
        // as a mystery violation on a route the client had attributed to the wrong contract.
        var governed = await sdk.GetPublishRouteAsync(new PublishRoute("orders", "order.created"));
        var before = await sdk.GetPublishRouteAsync(new PublishRoute("orders", "order.archived"));
        var after = await sdk.GetPublishRouteAsync(new PublishRoute("orders", "order.cancelled"));

        Assert.Equal(contract, governed.Contract);
        Assert.Null(before.Contract);
        Assert.Null(after.Contract);
    }

    [Fact]
    public async Task AWildcardBindingGovernsEveryRoutingKeyItMatches()
    {
        var (http, environment, contract) = await GovernedAsync(pattern: "order.*");
        using var sdk = Sdk(http, environment);

        var created = await sdk.GetPublishRouteAsync(new PublishRoute("orders", "order.created"));
        var deeper = await sdk.GetPublishRouteAsync(new PublishRoute("orders", "order.line.added"));

        // '*' is exactly one word, so 'order.line.added' is outside it. Matching is the
        // registry's job; this asserts the SDK is asking with a concrete key and not a pattern
        // of its own, which is the mistake that would make every route look governed.
        Assert.Equal(contract, created.Contract);
        Assert.Null(deeper.Contract);
    }

    [Fact]
    public async Task TwoContractsOverOneRouteAreBothReported()
    {
        // Decision 21. The overlap invariant holds WITHIN a contract — two bindings that
        // intersect and carry different subjects are refused unless precedence separates them —
        // and never held across contracts. Nothing stopped two contracts in one environment from
        // binding the same route, and resolve answered with whichever sorted first by name: the
        // arbitrary outcome the within-contract invariant exists to prevent, one level up, and
        // invisible to the publisher it affected.
        var (http, environment, first) = await GovernedAsync(enforcement: "MONITOR");

        // A second contract over the same route, stricter, naming a different subject.
        var second = $"a-{Guid.CreateVersion7():N}"[..20];   // sorts before `first`, so a
                                                            // first-by-name bug would pick it
        var created = await http.PostAsJsonAsync(
            $"/v1/environments/{environment}/contracts",
            new CreateContractRequest(second, "ENFORCE"),
            ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var bound = await http.PostAsJsonAsync(
            $"/v1/environments/{environment}/contracts/{second}/publishes",
            new AddPublishBindingRequest(
                "orders", "order.created", Subjects("acme.Other@latest")),
            ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Created, bound.StatusCode);

        using var sdk = Sdk(http, environment);
        var route = await sdk.GetPublishRouteAsync(new PublishRoute("orders", "order.created"));

        Assert.True(route.IsAmbiguous);
        Assert.Equal([second, first], [.. route.Contracts.OrderBy(c => c, StringComparer.Ordinal)]);

        // Strictest enforcement and the union of subjects: the combination that fails safe both
        // ways while the ambiguity stands. The loosest mode would let an authoring mistake switch
        // enforcement off; intersecting the subjects would refuse a publisher one contract plainly
        // permits. Neither silently picks a winner.
        Assert.Equal(EnforcementMode.Enforce, route.Enforcement);
        Assert.Contains(route.Subjects, s => s.Subject.Value == "acme.Order");
        Assert.Contains(route.Subjects, s => s.Subject.Value == "acme.Other");

        // And the SDK says so once, on its status, rather than a million times per message.
        Assert.Equal(1, sdk.Status.AmbiguousRoutes);
        Assert.Contains(
            "more than one contract", sdk.Status.ToString(), StringComparison.Ordinal);

        // `Contract` is null under ambiguity, deliberately: nothing in the SDK should be able to
        // name one of a colliding pair as though it were the answer.
        Assert.Null(route.Contract);
    }

    [Fact]
    public async Task AContractSwitchedToOffStaysGovernedAndStopsEnforcing()
    {
        var (http, environment, contract) = await GovernedAsync();

        // A short TTL so the test observes the switch rather than the cache. In production this
        // window is the latency of the central off switch, not a test artefact.
        using var sdk = Sdk(http, environment, o => o.ContractTtl = TimeSpan.Zero.Add(TimeSpan.FromTicks(1)));

        var before = await sdk.GetPublishRouteAsync(new PublishRoute("orders", "order.created"));
        Assert.Equal(EnforcementMode.Enforce, before.Enforcement);

        var switched = await http.PutAsJsonAsync(
            $"/v1/environments/{environment}/contracts/{contract}/enforcement",
            new SetEnforcementRequest("OFF"),
            ApiFactory.Json);
        Assert.Equal(HttpStatusCode.OK, switched.StatusCode);

        var after = await sdk.GetPublishRouteAsync(new PublishRoute("orders", "order.created"));

        // Still governed, now doing nothing. The distinction is what makes the off switch
        // reachable at all: an ungoverned route falls back to the client's own Mode, so
        // collapsing these two would leave a locally-enforcing service still enforcing.
        Assert.True(after.IsGoverned);
        Assert.Equal(contract, after.Contract);
        Assert.Equal(EnforcementMode.Off, after.Enforcement);
    }
}
