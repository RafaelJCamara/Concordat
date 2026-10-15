using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Concordat.Api.IntegrationTests;

/// <summary>
/// <c>GET /v1/environments/{env}/subjects?limit=&amp;offset=</c> and the <c>X-Total-Count</c> header, over real
/// HTTP against real PostgreSQL (ADR-045).
/// </summary>
/// <remarks>
/// The body is still the array every client reads, and an unpaged request still returns all of it. What is
/// new is only what a caller who asks for a page can do: read how much of the list it is not seeing.
/// </remarks>
[Collection(ApiCollection.Name)]
public class SubjectPagingApiTests(ApiFactory factory)
{
    private static string Unique() => $"{Guid.CreateVersion7():N}"[..16];

    private async Task<(HttpClient Client, string Environment, string Stem)> ArrangeAsync(int subjects)
    {
        var client = factory.CreateClient();
        var environment = $"env-{Unique()}";
        var stem = $"acme.p{Unique()[..8]}";

        var created = await client.PostAsJsonAsync(
            "/v1/environments", new CreateEnvironmentRequest(environment), ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        for (var i = 0; i < subjects; i++)
        {
            var response = await client.PostAsJsonAsync(
                $"/v1/environments/{environment}/subjects",
                new CreateSubjectRequest($"{stem}.s{i:D3}.Thing", "json", "alice"),
                ApiFactory.Json);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        return (client, environment, stem);
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

    [Fact]
    public async Task APageIsTheRowsFromTheOffset_AndTheHeaderIsTheWholeList()
    {
        var (client, environment, stem) = await ArrangeAsync(12);

        var (body, total, _) = await GetAsync(client, environment, "?limit=5&offset=5");

        Assert.Equal(
            [$"{stem}.s005.Thing", $"{stem}.s006.Thing", $"{stem}.s007.Thing", $"{stem}.s008.Thing", $"{stem}.s009.Thing"],
            Names(body));
        Assert.Equal("12", total);
    }

    [Fact]
    public async Task WithoutALimitOrAnOffset_EveryRowIsReturnedAsBefore_AndTheHeaderSaysSo()
    {
        var (client, environment, _) = await ArrangeAsync(60);

        var (body, total, _) = await GetAsync(client, environment);

        Assert.Equal(60, body.Count);
        Assert.Equal("60", total);
    }

    [Fact]
    public async Task AnOffsetAloneIsAPageOfFifty()
    {
        var (client, environment, _) = await ArrangeAsync(60);

        var (body, total, _) = await GetAsync(client, environment, "?offset=40");

        Assert.Equal(20, body.Count);
        Assert.Equal("60", total);

        var (first, _, _) = await GetAsync(client, environment, "?offset=0");
        Assert.Equal(50, first.Count);
    }

    [Theory]
    [InlineData("?limit=0", 1)]
    [InlineData("?limit=-4", 1)]
    [InlineData("?limit=100000", 6)]
    [InlineData("?limit=3&offset=-9", 3)]
    public async Task AnOutOfRangePageIsClampedAndNeverRefused(string query, int expected)
    {
        var (client, environment, _) = await ArrangeAsync(6);

        var (body, total, _) = await GetAsync(client, environment, query);

        Assert.Equal(expected, body.Count);
        Assert.Equal("6", total);
    }

    [Fact]
    public async Task APrefixAndAPageTogether_TheHeaderCountsThePrefixedList()
    {
        var (client, environment, stem) = await ArrangeAsync(8);
        var other = await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/subjects",
            new CreateSubjectRequest("zzz.unrelated.Thing", "json", "alice"),
            ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);

        var (body, total, _) = await GetAsync(client, environment, $"?prefix={stem}.&limit=3&offset=6");

        Assert.Equal([$"{stem}.s006.Thing", $"{stem}.s007.Thing"], Names(body));
        Assert.Equal("8", total);
    }

    [Fact]
    public async Task APrefixNoNameCouldBeginWith_IsAnEmptyPageAndAZeroTotal_NotAnError()
    {
        var (client, environment, _) = await ArrangeAsync(3);

        var (body, total, _) = await GetAsync(client, environment, "?prefix=acme-&limit=10");

        Assert.Empty(body);
        Assert.Equal("0", total);
    }

    [Fact]
    public async Task AnEnvironmentNothingWasWrittenTo_IsAnEmptyListAndAZeroTotal()
    {
        var client = factory.CreateClient();

        var (body, total, _) = await GetAsync(client, $"env-{Unique()}", "?limit=10");

        Assert.Empty(body);
        Assert.Equal("0", total);
    }

    [Fact]
    public async Task PagesWalkedToTheEnd_AreTheWholeListWithNoGapAndNoRepeat()
    {
        var (client, environment, _) = await ArrangeAsync(23);

        var walked = new List<string>();
        for (var offset = 0; offset < 30; offset += 10)
        {
            walked.AddRange(Names((await GetAsync(client, environment, $"?limit=10&offset={offset}")).Body));
        }

        Assert.Equal(Names((await GetAsync(client, environment)).Body), walked);
        Assert.Equal(23, walked.Distinct().Count());
    }

    [Fact]
    public async Task APageHasTheSameBodyShapeAsTheWholeList()
    {
        // The body is the list every client already reads: a page is the same objects, fewer of them.
        var (client, environment, _) = await ArrangeAsync(3);

        var whole = (await GetAsync(client, environment)).Body[0]!.AsObject().Select(p => p.Key).ToList();
        var page = (await GetAsync(client, environment, "?limit=1")).Body[0]!.AsObject().Select(p => p.Key).ToList();

        Assert.Equal(whole, page);
    }

    [Fact]
    public async Task TheAnswerIsTheSameHostedAsProductionAndAsDevelopment()
    {
        var (client, environment, _) = await ArrangeAsync(4);
        var development = await GetAsync(client, environment, "?limit=2&offset=1");

        using var production = factory.WithWebHostBuilder(b => b
            .UseEnvironment("Production")
            .UseSetting(
                "ConnectionStrings:Concordat",
                "Host=placeholder;Database=never;Username=never;Password=never"));
        Assert.Equal("Production", production.Services.GetRequiredService<IHostEnvironment>().EnvironmentName);

        var inProduction = await GetAsync(production.CreateClient(), environment, "?limit=2&offset=1");

        Assert.Equal(development.Raw, inProduction.Raw);
        Assert.Equal(development.Total, inProduction.Total);
    }
}
