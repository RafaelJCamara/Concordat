using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Concordat.Api.IntegrationTests;

/// <summary>
/// <c>GET /v1/environments/{env}/subjects?sort=</c> and the <c>createdAt</c> a subject carries, over real HTTP
/// against real PostgreSQL (ADR-049).
/// </summary>
/// <remarks>
/// The order and its tiebreaker are proved against the repository in <c>SubjectPageRepositoryTests</c>, where
/// creation times can be chosen. Here they are the registry's own clock, so subjects are created a few
/// milliseconds apart and the order asserted is the order of creation. What is left for HTTP is the wire: the
/// parameter, the refusal of a sort nobody offers, the header on every answer, and the new field.
/// </remarks>
[Collection(ApiCollection.Name)]
public partial class SubjectNewestApiTests(ApiFactory factory)
{
    private const string V1 = """{"type":"object","properties":{"id":{"type":"string"}}}""";

    private static string Unique() => $"{Guid.CreateVersion7():N}"[..16];

    [GeneratedRegex(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(\.\d+)?(Z|\+00:00)$")]
    private static partial Regex Iso8601Utc();

    private static async Task<string> NewEnvironmentAsync(HttpClient client)
    {
        var environment = $"env-{Unique()}";

        var created = await client.PostAsJsonAsync(
            "/v1/environments", new CreateEnvironmentRequest(environment), ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        return environment;
    }

    /// <summary>Creates subjects one after another, a few milliseconds apart, and returns their names in that order.</summary>
    private static async Task<List<string>> CreateInOrderAsync(
        HttpClient client, string environment, params string[] names)
    {
        foreach (var name in names)
        {
            var response = await client.PostAsJsonAsync(
                $"/v1/environments/{environment}/subjects",
                new CreateSubjectRequest(name, "json", "alice"),
                ApiFactory.Json);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);

            await Task.Delay(8);
        }

        return [.. names];
    }

    private static async Task<(JsonArray Body, string? Total, string Raw)> GetAsync(
        HttpClient client, string environment, string query = "")
    {
        var response = await client.GetAsync($"/v1/environments/{environment}/subjects{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var raw = await response.Content.ReadAsStringAsync();
        response.Headers.TryGetValues("X-Total-Count", out var values);

        return (JsonNode.Parse(raw)!.AsArray(), values?.SingleOrDefault(), raw);
    }

    private static List<string> Names(JsonArray body) =>
        [.. body.Select(s => s!["name"]!.GetValue<string>())];

    // ------------------------------------------------------------------- the default is unchanged

    [Fact]
    public async Task WithoutASort_TheOrderIsTheNameOrder_AsItAlwaysWas()
    {
        var client = factory.CreateClient();
        var environment = await NewEnvironmentAsync(client);
        var stem = $"acme.d{Unique()[..8]}";

        // Created in an order that is neither name order nor its reverse.
        await CreateInOrderAsync(client, environment, $"{stem}.b.Thing", $"{stem}.c.Thing", $"{stem}.a.Thing");

        var (body, _, _) = await GetAsync(client, environment);

        Assert.Equal([$"{stem}.a.Thing", $"{stem}.b.Thing", $"{stem}.c.Thing"], Names(body));
    }

    [Theory]
    [InlineData("?sort=name")]
    [InlineData("?sort=")]
    [InlineData("?sort=%20NAME%20")]
    public async Task SortNameBlankOrSpelledDifferently_IsByteForByteTheListWithNoSort(string query)
    {
        var client = factory.CreateClient();
        var environment = await NewEnvironmentAsync(client);
        var stem = $"acme.d{Unique()[..8]}";
        await CreateInOrderAsync(client, environment, $"{stem}.b.Thing", $"{stem}.a.Thing", $"{stem}.c.Thing");

        var without = await GetAsync(client, environment);
        var with = await GetAsync(client, environment, query);

        Assert.Equal(without.Raw, with.Raw);
        Assert.Equal(without.Total, with.Total);
    }

    [Fact]
    public async Task SortNameWithAPage_IsTheSamePageAsNoSortWithAPage()
    {
        var client = factory.CreateClient();
        var environment = await NewEnvironmentAsync(client);
        var stem = $"acme.d{Unique()[..8]}";
        await CreateInOrderAsync(
            client, environment, $"{stem}.d.Thing", $"{stem}.b.Thing", $"{stem}.a.Thing", $"{stem}.c.Thing");

        var without = await GetAsync(client, environment, "?limit=2&offset=1");
        var with = await GetAsync(client, environment, "?sort=name&limit=2&offset=1");

        Assert.Equal(without.Raw, with.Raw);
        Assert.Equal([$"{stem}.b.Thing", $"{stem}.c.Thing"], Names(with.Body));
        Assert.Equal("4", with.Total);
    }

    // ----------------------------------------------------------------------------------- newest

    [Fact]
    public async Task Newest_IsTheLastCreatedFirst_AndIsNotTheNameOrder()
    {
        var client = factory.CreateClient();
        var environment = await NewEnvironmentAsync(client);
        var stem = $"acme.n{Unique()[..8]}";

        // Name order would be a, b, c. The creation order is b, a, c, so newest first is c, a, b.
        await CreateInOrderAsync(client, environment, $"{stem}.b.Thing", $"{stem}.a.Thing", $"{stem}.c.Thing");

        var (body, total, _) = await GetAsync(client, environment, "?sort=newest&limit=10");

        Assert.Equal([$"{stem}.c.Thing", $"{stem}.a.Thing", $"{stem}.b.Thing"], Names(body));
        Assert.Equal("3", total);
    }

    [Fact]
    public async Task Newest_WithoutAPage_IsTheWholeListNewestFirst_AndTheHeaderSaysHowLong()
    {
        var client = factory.CreateClient();
        var environment = await NewEnvironmentAsync(client);
        var stem = $"acme.u{Unique()[..8]}";
        var created = await CreateInOrderAsync(
            client, environment, $"{stem}.b.Thing", $"{stem}.a.Thing", $"{stem}.c.Thing", $"{stem}.d.Thing");

        var (body, total, _) = await GetAsync(client, environment, "?sort=newest");

        Assert.Equal([.. created.AsEnumerable().Reverse()], Names(body));
        Assert.Equal("4", total);
    }

    [Fact]
    public async Task Newest_PagesWalkedToTheEnd_AreTheWholeListWithNoGapAndNoRepeat()
    {
        var client = factory.CreateClient();
        var environment = await NewEnvironmentAsync(client);
        var stem = $"acme.w{Unique()[..8]}";
        var created = await CreateInOrderAsync(
            client, environment, [.. Enumerable.Range(0, 23).Select(i => $"{stem}.s{(i * 11) % 23:D2}.Thing")]);

        var walked = new List<string>();
        for (var offset = 0; offset < 30; offset += 10)
        {
            walked.AddRange(Names((await GetAsync(client, environment, $"?sort=newest&limit=10&offset={offset}")).Body));
        }

        Assert.Equal([.. created.AsEnumerable().Reverse()], walked);
        Assert.Equal(23, walked.Distinct().Count());
    }

    [Fact]
    public async Task Newest_AndAPrefix_IsThePrefixedSubjectsNewestFirst_AndTheTotalIsThePrefixedCount()
    {
        var client = factory.CreateClient();
        var environment = await NewEnvironmentAsync(client);
        var stem = $"acme.p{Unique()[..8]}";

        // Two namespaces, created interleaved.
        await CreateInOrderAsync(
            client,
            environment,
            $"{stem}.orders.A",
            $"{stem}.billing.A",
            $"{stem}.orders.B",
            $"{stem}.billing.B",
            $"{stem}.orders.C");

        var first = await GetAsync(client, environment, $"?sort=newest&prefix={stem}.orders.&limit=2&offset=0");
        var second = await GetAsync(client, environment, $"?sort=newest&prefix={stem}.orders.&limit=2&offset=2");

        Assert.Equal([$"{stem}.orders.C", $"{stem}.orders.B"], Names(first.Body));
        Assert.Equal([$"{stem}.orders.A"], Names(second.Body));
        Assert.Equal("3", first.Total);
        Assert.Equal("3", second.Total);
    }

    [Fact]
    public async Task Newest_PastTheEnd_IsEmptyAndTheTotalIsStillTheWholeLength()
    {
        var client = factory.CreateClient();
        var environment = await NewEnvironmentAsync(client);
        await CreateInOrderAsync(client, environment, $"acme.e{Unique()[..8]}.A", $"acme.e{Unique()[..8]}.B");

        var (body, total, _) = await GetAsync(client, environment, "?sort=newest&limit=10&offset=500");

        Assert.Empty(body);
        Assert.Equal("2", total);
    }

    [Theory]
    [InlineData("?sort=newest&limit=0", 1)]
    [InlineData("?sort=newest&limit=100000", 4)]
    [InlineData("?sort=newest&limit=2&offset=-9", 2)]
    [InlineData("?sort=newest&offset=2", 2)]
    public async Task Newest_ClampsAPageAndNeverRefusesIt_AsTheNameOrderDoes(string query, int expected)
    {
        var client = factory.CreateClient();
        var environment = await NewEnvironmentAsync(client);
        var stem = $"acme.c{Unique()[..8]}";
        await CreateInOrderAsync(
            client, environment, $"{stem}.a.Thing", $"{stem}.b.Thing", $"{stem}.c.Thing", $"{stem}.d.Thing");

        var (body, total, _) = await GetAsync(client, environment, query);

        Assert.Equal(expected, body.Count);
        Assert.Equal("4", total);
    }

    [Fact]
    public async Task Newest_AndAPrefixNoNameCouldBeginWith_IsAnEmptyPageAndAZeroTotal_NotAnError()
    {
        var client = factory.CreateClient();
        var environment = await NewEnvironmentAsync(client);
        await CreateInOrderAsync(client, environment, $"acme.z{Unique()[..8]}.A");

        var (body, total, _) = await GetAsync(client, environment, "?sort=newest&prefix=acme-&limit=10");

        Assert.Empty(body);
        Assert.Equal("0", total);
    }

    [Fact]
    public async Task Newest_InAnEnvironmentNothingWasWrittenTo_IsAnEmptyListAndAZeroTotal()
    {
        var client = factory.CreateClient();

        var (body, total, _) = await GetAsync(client, $"env-{Unique()}", "?sort=newest&limit=10");

        Assert.Empty(body);
        Assert.Equal("0", total);
    }

    [Theory]
    [InlineData("?sort=NEWEST")]
    [InlineData("?sort=%20newest%20")]
    public async Task TheSortIsTrimmedAndNotCaseSensitive(string query)
    {
        var client = factory.CreateClient();
        var environment = await NewEnvironmentAsync(client);
        var stem = $"acme.t{Unique()[..8]}";
        await CreateInOrderAsync(client, environment, $"{stem}.b.Thing", $"{stem}.a.Thing");

        var (body, _, _) = await GetAsync(client, environment, query);

        Assert.Equal([$"{stem}.a.Thing", $"{stem}.b.Thing"], Names(body));
    }

    [Fact]
    public async Task Newest_DoesNotMove_WhenAVersionIsRegisteredOnAnOlderSubject()
    {
        // The order is by creation, not by last change: giving the oldest subject a version, the freshest
        // activity in the environment, does not bring it to the top.
        var client = factory.CreateClient();
        var environment = await NewEnvironmentAsync(client);
        var stem = $"acme.m{Unique()[..8]}";
        await CreateInOrderAsync(client, environment, $"{stem}.old.Thing", $"{stem}.new.Thing");

        var registered = await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/subjects/{stem}.old.Thing/versions",
            new RegisterVersionRequest(V1, null, null, "alice"),
            ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);

        var (body, _, _) = await GetAsync(client, environment, "?sort=newest&limit=10");

        Assert.Equal([$"{stem}.new.Thing", $"{stem}.old.Thing"], Names(body));
    }

    // ------------------------------------------------------------------- an unknown sort is refused

    [Theory]
    [InlineData("?sort=oldest")]
    [InlineData("?sort=newset")]
    [InlineData("?sort=name,newest")]
    [InlineData("?sort=name&sort=newest")]
    [InlineData("?sort=newest%20desc")]
    [InlineData("?sort=oldest&limit=10")]
    [InlineData("?sort=oldest&limit=10&offset=5")]
    [InlineData("?sort=oldest&prefix=acme.")]
    [InlineData("?sort=oldest&prefix=acme-")]
    public async Task AnUnknownSort_Is400WithItsOwnCode_PagedOrNot(string query)
    {
        var client = factory.CreateClient();
        var environment = await NewEnvironmentAsync(client);
        await CreateInOrderAsync(client, environment, $"acme.r{Unique()[..8]}.Thing");

        var response = await client.GetAsync($"/v1/environments/{environment}/subjects{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("subject_sort_invalid", (await ApiFactory.ReadProblemAsync(response)).ConcordatCode);
        Assert.False(response.Headers.Contains("X-Total-Count"));
    }

    [Fact]
    public async Task AnUnknownSort_IsRefused_EvenInAnEnvironmentNothingWasWrittenTo()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync($"/v1/environments/env-{Unique()}/subjects?sort=oldest");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("subject_sort_invalid", (await ApiFactory.ReadProblemAsync(response)).ConcordatCode);
    }

    // -------------------------------------------------------------------------- createdAt on the wire

    [Fact]
    public async Task ASubject_CarriesWhenItWasCreated_AsAnIsoUtcInstant_OnEveryRouteThatReturnsOne()
    {
        var client = factory.CreateClient();
        var environment = await NewEnvironmentAsync(client);
        var name = $"acme.a{Unique()[..8]}.Thing";

        var before = DateTimeOffset.UtcNow;
        var created = await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/subjects",
            new CreateSubjectRequest(name, "json", "alice"),
            ApiFactory.Json);
        var after = DateTimeOffset.UtcNow;
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var fromCreate = JsonNode.Parse(await created.Content.ReadAsStringAsync())!["createdAt"]!.GetValue<string>();
        var fromGet = JsonNode.Parse(
            await client.GetStringAsync($"/v1/environments/{environment}/subjects/{name}"))!["createdAt"]!.GetValue<string>();
        var fromList = (await GetAsync(client, environment)).Body[0]!["createdAt"]!.GetValue<string>();
        var fromNewest = (await GetAsync(client, environment, "?sort=newest&limit=1")).Body[0]!["createdAt"]!.GetValue<string>();

        foreach (var text in new[] { fromCreate, fromGet, fromList, fromNewest })
        {
            Assert.Matches(Iso8601Utc(), text);

            var instant = DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(TimeSpan.Zero, instant.Offset);

            // Inside the window the request took, give or take the database's microsecond.
            Assert.InRange(instant, before.AddMilliseconds(-1), after.AddMilliseconds(1));
        }

        // One instant, however it is read.
        Assert.Equal(fromGet, fromList);
        Assert.Equal(fromGet, fromNewest);
    }

    [Fact]
    public async Task AddingCreatedAt_RemovesAndRenamesNothing()
    {
        // Additive: a client written before the field existed reads the same members it always did.
        var client = factory.CreateClient();
        var environment = await NewEnvironmentAsync(client);
        await CreateInOrderAsync(client, environment, $"acme.k{Unique()[..8]}.Thing");

        var keys = (await GetAsync(client, environment)).Body[0]!.AsObject().Select(p => p.Key).ToList();

        Assert.Equal(
            [
                "name", "format", "owner", "reviewers", "lifecycle", "contentModel", "compatibilityPolicy",
                "latest", "createdAt", "versions",
            ],
            keys);
    }
}
