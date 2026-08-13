using System.Net;
using System.Text;
using Concordat.Client;
using Concordat.Domain.Registry;
using Microsoft.Extensions.Time.Testing;

namespace Concordat.Client.Tests;

/// <summary>Answers whatever the test wants, and counts what was asked.</summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

    public List<string> Requests { get; } = [];

    public int CallsTo(string fragment) =>
        Requests.Count(r => r.Contains(fragment, StringComparison.Ordinal));

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!.PathAndQuery);
        return Task.FromResult(_respond(request));
    }

    public static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Status(HttpStatusCode code) => new(code);

    public static HttpResponseMessage Problem(HttpStatusCode code, string concordatCode) =>
        new(code)
        {
            Content = new StringContent(
                $$"""
                {"type":"https://concordat.dev/errors/{{concordatCode}}","title":"Nope",
                 "status":{{(int)code}},"detail":"The API key has expired.",
                 "concordatCode":"{{concordatCode}}"}
                """,
                Encoding.UTF8,
                "application/problem+json"),
        };
}

public class ClientTests
{
    private const string IdA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string IdB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static SubjectName Subject(string name = "acme.Order") => SubjectName.Create(name).Value;

    private static SchemaId Id(string value) => SchemaId.Create(value).Value;

    private static string BootstrapBody() => $$"""
        {
          "subjects": [
            { "name": "acme.Order", "format": "json", "latestOrdinal": 2,
              "latestSchemaId": "{{IdA}}", "latestSemver": "1.1.0" }
          ],
          "schemas": {
            "{{IdA}}": { "schemaId": "{{IdA}}", "format": "json", "schema": "{\"type\":\"object\"}" }
          }
        }
        """;

    private static (ConcordatClient Client, FakeHandler Handler, FakeTimeProvider Clock) Build(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        Action<ConcordatClientOptions>? configure = null)
    {
        var handler = new FakeHandler(respond);
        var options = new ConcordatClientOptions
        {
            BaseAddress = new Uri("https://registry.test"),
            Environment = "test",
            // Jitter off by default: these tests assert behaviour, not timing, and a random
            // delay would only make them slow.
            WarmUpJitter = TimeSpan.Zero,
        };
        configure?.Invoke(options);

        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero));
        var http = new HttpClient(handler) { BaseAddress = options.BaseAddress };

        return (new ConcordatClient(http, options, clock), handler, clock);
    }

    [Fact]
    public async Task WarmUp_LoadsSubjectsAndSchemasInOneRequest()
    {
        var (client, handler, _) = Build(_ => FakeHandler.Json(BootstrapBody()));

        var status = await client.WarmUpAsync();

        Assert.True(status.IsWarm);
        Assert.Equal(1, status.SubjectsLoaded);
        Assert.Equal(1, status.SchemasLoaded);
        // One request, not N. That is the entire reason bootstrap exists.
        var only = Assert.Single(handler.Requests);
        Assert.Contains("/bootstrap", only, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AfterWarmUp_AKnownSchemaNeverTouchesTheRegistry()
    {
        // DESIGN §5's hard rule, for the case it actually covers: a cache hit.
        var (client, handler, _) = Build(_ => FakeHandler.Json(BootstrapBody()));
        await client.WarmUpAsync();
        var before = handler.Requests.Count;

        for (var i = 0; i < 10; i++)
        {
            Assert.NotNull(await client.GetSchemaAsync(Id(IdA)));
            Assert.NotNull(await client.GetLatestAsync(Subject()));
        }

        Assert.Equal(before, handler.Requests.Count);
    }

    [Fact]
    public async Task AnOlderSchemaIdIsFetchedOnceThenCachedForever()
    {
        // The caveat on the hard rule. /bootstrap ships only each subject's LATEST schema, so
        // a message pinned to an older id has never been seen and must be fetched. Content
        // addressing then guarantees it is never fetched again.
        var (client, handler, _) = Build(request =>
            request.RequestUri!.AbsolutePath.Contains("/bootstrap", StringComparison.Ordinal)
                ? FakeHandler.Json(BootstrapBody())
                : FakeHandler.Json(
                    $$"""{"schemaId":"{{IdB}}","format":"json","schema":"{\"type\":\"array\"}"}"""));

        await client.WarmUpAsync();

        for (var i = 0; i < 5; i++)
        {
            Assert.NotNull(await client.GetSchemaAsync(Id(IdB)));
        }

        Assert.Equal(1, handler.CallsTo($"/v1/schemas/{IdB}"));
    }

    [Fact]
    public async Task LatestIsRefetchedAfterItsTtl()
    {
        // The one mutable pointer in the system, and so the only thing with a TTL.
        var (client, handler, clock) = Build(_ => FakeHandler.Json(BootstrapBody()));
        await client.WarmUpAsync();

        await client.GetLatestAsync(Subject());
        Assert.Equal(0, handler.CallsTo("/versions/latest"));

        clock.Advance(TimeSpan.FromSeconds(31));
        await client.GetLatestAsync(Subject());

        Assert.Equal(1, handler.CallsTo("/versions/latest"));
    }

    [Fact]
    public async Task AnUnknownSubjectIsCachedSoItCannotRetryStorm()
    {
        var (client, handler, _) = Build(_ => FakeHandler.Status(HttpStatusCode.NotFound));

        for (var i = 0; i < 20; i++)
        {
            Assert.Null(await client.GetLatestAsync(Subject("acme.Ghost")));
        }

        // One question, not twenty. During a cold start every instance in the fleet asks about
        // the same unknown name at once.
        Assert.Equal(1, handler.CallsTo("acme.Ghost"));
    }

    [Fact]
    public async Task ASubjectCreatedLaterBecomesVisibleWithoutARestart()
    {
        var exists = false;
        var (client, _, clock) = Build(_ => exists
            ? FakeHandler.Json($$"""{"ordinal":1,"schemaId":"{{IdA}}"}""")
            : FakeHandler.Status(HttpStatusCode.NotFound));

        Assert.Null(await client.GetLatestAsync(Subject("acme.Later")));

        exists = true;
        clock.Advance(TimeSpan.FromSeconds(6));

        Assert.NotNull(await client.GetLatestAsync(Subject("acme.Later")));
    }

    [Fact]
    public async Task TheNegativeCacheBacksOff()
    {
        var (client, handler, clock) = Build(_ => FakeHandler.Status(HttpStatusCode.NotFound));

        await client.GetLatestAsync(Subject("acme.Ghost"));   // asks, caches for 5s
        clock.Advance(TimeSpan.FromSeconds(6));
        await client.GetLatestAsync(Subject("acme.Ghost"));   // asks again, now caches for 10s
        clock.Advance(TimeSpan.FromSeconds(6));
        await client.GetLatestAsync(Subject("acme.Ghost"));   // still inside the 10s window

        // A name missing because of a configuration typo must not be asked about forever at a
        // fixed rate.
        Assert.Equal(2, handler.CallsTo("acme.Ghost"));
    }

    [Fact]
    public async Task AStaleLatestIsServedWhenTheRegistryIsUnreachable()
    {
        var healthy = true;
        var (client, _, clock) = Build(_ => healthy
            ? FakeHandler.Json(BootstrapBody())
            : throw new HttpRequestException("registry down"));

        await client.WarmUpAsync();
        healthy = false;
        clock.Advance(TimeSpan.FromSeconds(31));

        var latest = await client.GetLatestAsync(Subject());

        // Failing delivery over a registry blip would be worse than a slightly stale pointer.
        Assert.NotNull(latest);
        Assert.Equal(1, client.Status.StaleServed);
        Assert.True(client.Status.IsDegraded);
    }

    [Fact]
    public async Task StalenessIsBounded()
    {
        // Unbounded stale is how a fleet quietly enforces last month's contract.
        var healthy = true;
        var (client, _, clock) = Build(_ => healthy
            ? FakeHandler.Json(BootstrapBody())
            : throw new HttpRequestException("registry down"));

        await client.WarmUpAsync();
        healthy = false;
        clock.Advance(TimeSpan.FromMinutes(6));

        Assert.Null(await client.GetLatestAsync(Subject()));
        Assert.Equal(1, client.Status.ResolutionFailures);
    }

    [Fact]
    public async Task RunningUnenforcedIsCountedAndVisible()
    {
        // Fail-open without a signal is how enforcement stops and nobody notices.
        var (client, _, _) = Build(_ => throw new HttpRequestException("registry down"));

        await client.GetSchemaAsync(Id(IdA));
        await client.GetSchemaAsync(Id(IdB));

        var status = client.Status;
        Assert.Equal(2, status.ResolutionFailures);
        Assert.True(status.IsDegraded);
        Assert.Contains("DEGRADED", status.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedWarmUpDoesNotThrowByDefault()
    {
        // A client that will not start because the registry is down has put the registry back
        // on the critical path, which is exactly what warm-up exists to avoid.
        var (client, _, _) = Build(_ => FakeHandler.Status(HttpStatusCode.ServiceUnavailable));

        var status = await client.WarmUpAsync();

        Assert.False(status.IsWarm);
        Assert.True(status.IsDegraded);
    }

    [Fact]
    public async Task RequireWarmUpMakesAFailedWarmUpFatal()
    {
        var (client, _, _) = Build(
            _ => FakeHandler.Status(HttpStatusCode.ServiceUnavailable),
            o => o.RequireWarmUp = true);

        var ex = await Assert.ThrowsAsync<ConcordatException>(() => client.WarmUpAsync());
        Assert.Equal("warm_up_failed", ex.Code);
    }

    [Fact]
    public async Task AMissingSchemaIsDistinctFromAnUnreachableRegistry()
    {
        // Absent is a producer bug; unreachable is an operational condition. Conflating them
        // sends whoever is paged to the wrong system.
        var (missing, _, _) = Build(_ => FakeHandler.Status(HttpStatusCode.NotFound));
        Assert.Null(await missing.GetSchemaAsync(Id(IdA)));
        Assert.False(missing.Status.IsDegraded);

        var (down, _, _) = Build(_ => throw new HttpRequestException("down"));
        Assert.Null(await down.GetSchemaAsync(Id(IdA)));
        Assert.True(down.Status.IsDegraded);
    }

    [Fact]
    public async Task AnExpiredApiKeyIsNotReportedAsAnUnreachableRegistry()
    {
        // The case this distinction exists for. An expired key answers every request with 401,
        // which presents as total enforcement loss while every registry dashboard stays green.
        // Reporting it as "unreachable" sends whoever is paged to the wrong system entirely.
        var (client, _, _) = Build(_ => FakeHandler.Problem(HttpStatusCode.Unauthorized, "auth_invalid_key"));

        Assert.Null(await client.GetSchemaAsync(Id(IdA)));

        var status = client.Status;
        Assert.False(status.IsDegraded);
        Assert.Equal("401 auth_invalid_key", status.LastFailure);
        Assert.Equal(1, status.ResolutionFailures);
        Assert.Contains("auth_invalid_key", status.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AServerErrorIsDegradation()
    {
        var (client, _, _) = Build(_ => FakeHandler.Status(HttpStatusCode.BadGateway));

        Assert.Null(await client.GetLatestAsync(Subject()));

        // No body at all, so no code — but the status still says what happened.
        Assert.True(client.Status.IsDegraded);
        Assert.Equal("502 no concordatCode", client.Status.LastFailure);
    }

    [Fact]
    public async Task ProblemDetailsCodesSurviveOntoTheException()
    {
        // Callers must be able to branch on a stable token rather than parse prose.
        var (client, _, _) = Build(
            _ => FakeHandler.Problem(HttpStatusCode.Forbidden, "auth_forbidden_environment"),
            o => o.RequireWarmUp = true);

        var ex = await Assert.ThrowsAsync<ConcordatException>(() => client.WarmUpAsync());

        Assert.Equal("auth_forbidden_environment", ex.Code);
        Assert.Contains("The API key has expired.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnparseableErrorBodyDoesNotBecomeTheClientsOwnFailure()
    {
        var (client, _, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("<html>502 Bad Gateway</html>", Encoding.UTF8, "text/html"),
        });

        // A proxy returning HTML must not turn a legible registry problem into an illegible
        // client one.
        Assert.Null(await client.GetSchemaAsync(Id(IdA)));
        Assert.True(client.Status.IsDegraded);
    }

    [Fact]
    public async Task FailClosedRefusesRatherThanProceedUnenforced()
    {
        var (client, _, _) = Build(
            _ => FakeHandler.Status(HttpStatusCode.NotFound),
            o => o.OnResolutionFailure = ResolutionFailureMode.FailClosed);

        var ex = await Assert.ThrowsAsync<ConcordatException>(
            async () => await client.GetSchemaAsync(Id(IdA)));

        Assert.Equal("schema_not_found", ex.Code);
        Assert.Equal(1, client.Status.ResolutionFailures);
    }

    [Fact]
    public async Task FailClosedStillThrowsOnceTheMissIsNegativelyCached()
    {
        // The negative cache short-circuits the request. It must not short-circuit the verdict:
        // a fail-closed consumer that silently proceeded from the second message onward would
        // be the exact opposite of what the setting asks for.
        var (client, handler, _) = Build(
            _ => FakeHandler.Status(HttpStatusCode.NotFound),
            o => o.OnResolutionFailure = ResolutionFailureMode.FailClosed);

        await Assert.ThrowsAsync<ConcordatException>(
            async () => await client.GetLatestAsync(Subject("acme.Ghost")));
        await Assert.ThrowsAsync<ConcordatException>(
            async () => await client.GetLatestAsync(Subject("acme.Ghost")));

        Assert.Equal(1, handler.CallsTo("acme.Ghost"));
        Assert.Equal(2, client.Status.ResolutionFailures);
    }

    [Fact]
    public async Task EveryUnenforcedOperationIsCountedNotJustTheFirst()
    {
        // ResolutionFailures is the number to alert on, so it counts operations. A standing
        // enforcement hole must not read as one historic blip.
        var (client, _, _) = Build(_ => FakeHandler.Status(HttpStatusCode.NotFound));

        for (var i = 0; i < 5; i++)
        {
            Assert.Null(await client.GetLatestAsync(Subject("acme.Ghost")));
        }

        Assert.Equal(5, client.Status.ResolutionFailures);
    }

    [Fact]
    public void OptionsAreValidatedEagerly()
    {
        var options = new ConcordatClientOptions();

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public async Task TheApiKeyIsSentAsABearerToken()
    {
        var handler = new FakeHandler(_ => FakeHandler.Json(BootstrapBody()));
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://registry.test") };
        using var client = new ConcordatClient(
            http,
            new ConcordatClientOptions
            {
                BaseAddress = new Uri("https://registry.test"),
                Environment = "test",
                ApiKey = "secret",
                WarmUpJitter = TimeSpan.Zero,
            });

        await client.WarmUpAsync();

        Assert.Equal("Bearer secret", http.DefaultRequestHeaders.Authorization?.ToString());
    }
}
