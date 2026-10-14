using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Concordat.Api.IntegrationTests;

/// <summary>
/// <c>GET /v1/environments/{env}/subjects?prefix=</c>, over real HTTP and real PostgreSQL
/// (ADR-038).
/// </summary>
/// <remarks>
/// <para>
/// <b>This fixture hosts as Development on purpose</b>, like <c>UnreadableBodyTests</c>: a
/// query-string binding failure was a 500 in Development and a 400 in Production once, and the
/// environment that exists to find crashes must not be the one that hides them. The first test
/// asserts the hosting so that the day the factory stops being Development this file says so.
/// </para>
/// <para>
/// The hostile prefixes below are what a person (or a script) can put in a URL. The rule they pin
/// is the one the handler enforces: a prefix no subject name could begin with is <b>200 and an
/// empty list</b>, never a 500 from the database and never a wildcard.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public class SubjectPrefixApiTests(ApiFactory factory)
{
    private static string Unique() => $"{Guid.CreateVersion7():N}"[..16];

    private async Task<string> NewEnvironmentAsync(params string[] subjects)
    {
        var environment = $"env-{Unique()}";
        var client = factory.CreateClient();

        var created = await client.PostAsJsonAsync(
            "/v1/environments", new CreateEnvironmentRequest(environment), ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        foreach (var name in subjects)
        {
            var response = await client.PostAsJsonAsync(
                $"/v1/environments/{environment}/subjects",
                new CreateSubjectRequest(name, "json", "alice"),
                ApiFactory.Json);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        return environment;
    }

    /// <summary>Asks for a list and returns the names, asserting a 200 on the way.</summary>
    /// <param name="environment">The environment.</param>
    /// <param name="query">The raw query string after <c>?</c>, already percent-encoded.</param>
    private async Task<List<string>> NamesAsync(string environment, string query)
    {
        var response = await factory.CreateClient().GetAsync(
            $"/v1/environments/{environment}/subjects?{query}");

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"?{query}: expected 200, got {(int)response.StatusCode}: " +
            await response.Content.ReadAsStringAsync());

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return [.. document.RootElement.EnumerateArray().Select(e => e.GetProperty("name").GetString()!)];
    }

    [Fact]
    public void ThisFixtureHostsAsDevelopment_WhichIsWhereAQueryBindingBugWouldHide()
    {
        Assert.Equal("Development", factory.Services
            .GetRequiredService<IHostEnvironment>().EnvironmentName);
    }

    [Fact]
    public async Task APrefix_ListsOnlyTheNamesThatBeginWithIt()
    {
        var environment = await NewEnvironmentAsync(
            "acme.orders.OrderShipped",
            "acme.orders.OrderCreated",
            "acme.billing.InvoiceRaised",
            "acme.ordersnot.Other");

        Assert.Equal(
            ["acme.orders.OrderCreated", "acme.orders.OrderShipped"],
            await NamesAsync(environment, "prefix=acme.orders."));
        Assert.Equal(
            ["acme.billing.InvoiceRaised"], await NamesAsync(environment, "prefix=acme.b"));
        Assert.Empty(await NamesAsync(environment, "prefix=acme.nothing"));
    }

    [Fact]
    public async Task ThePrefixIsCaseSensitive_BecauseNamesAre()
    {
        var environment = await NewEnvironmentAsync(
            "acme.case.OrderCreated", "acme.case.ordercreated");

        Assert.Equal(["acme.case.OrderCreated"], await NamesAsync(environment, "prefix=acme.case.Order"));
        Assert.Equal(["acme.case.ordercreated"], await NamesAsync(environment, "prefix=acme.case.order"));
        Assert.Empty(await NamesAsync(environment, "prefix=ACME.case."));
    }

    [Fact]
    public async Task AnUnderscore_IsLiteral_NotAWildcardForAnyOneCharacter()
    {
        var environment = await NewEnvironmentAsync("acme.us.a_b", "acme.us.axb", "acme.us.aab");

        Assert.Equal(["acme.us.a_b"], await NamesAsync(environment, "prefix=acme.us.a_"));
    }

    [Theory]
    [InlineData("%25", "a percent sign alone, which as a LIKE pattern is every name")]
    [InlineData("acme.%25", "a percent sign after a prefix")]
    [InlineData("%25cme", "a percent sign inside")]
    [InlineData("acme.us.a%5C_", "a backslash, LIKE's escape character, before an underscore")]
    [InlineData("%5C", "a backslash alone")]
    [InlineData("%5C%25", "the two together")]
    [InlineData("acme-us", "a hyphen")]
    [InlineData("acme..us", "two dots")]
    [InlineData(".acme", "a leading dot")]
    [InlineData("acme.us.a%2A", "a glob star")]
    [InlineData("acme.us.a%3F", "a glob question mark")]
    [InlineData("acme%2Cacme.us", "a comma")]
    [InlineData("acme.us.a%27", "a quote")]
    [InlineData("acme.us.a%3B%20DROP%20TABLE%20subject", "a statement")]
    public async Task APrefixNoNameCanStartWith_IsAnEmptyListAndNeverAWildcard(string prefix, string why)
    {
        // acme.us.* exists, so a prefix that leaked through as a pattern would find something. An
        // empty 200 is the only answer that is both true and safe.
        var environment = await NewEnvironmentAsync("acme.us.a_b", "acme.us.axb", "acme.us.aab");

        Assert.True((await NamesAsync(environment, $"prefix={prefix}")).Count == 0, why);
    }

    [Theory]
    [InlineData("acme%00", "a NUL, which PostgreSQL refuses in a text parameter (22021)")]
    [InlineData("%00", "a NUL alone")]
    [InlineData("%FF", "a byte that is not UTF-8")]
    [InlineData("acme%C3", "a truncated multi-byte sequence")]
    [InlineData("acme%ED%A0%80", "a lone surrogate, encoded the way a broken client encodes one")]
    [InlineData("acme%F0%9F%98%80", "an emoji")]
    [InlineData("caf%C3%A9", "a non-ASCII letter")]
    public async Task APrefixThatIsNotWellFormedText_IsAnEmptyListNotA500(string prefix, string why)
    {
        var environment = await NewEnvironmentAsync("acme.bad.Name");

        Assert.True((await NamesAsync(environment, $"prefix={prefix}")).Count == 0, why);
    }

    [Fact]
    public async Task ARepeatedPrefix_IsAnEmptyListNotAnError()
    {
        // ASP.NET joins repeated values with a comma, and a comma is not a name character.
        var environment = await NewEnvironmentAsync("acme.rep.A", "acme.rep.B");

        Assert.Empty(await NamesAsync(environment, "prefix=acme.rep.A&prefix=acme.rep.B"));
    }

    [Fact]
    public async Task APrefixLongerThanAnyName_IsAnEmptyListNotAnError()
    {
        // Far over the 512 a name can be, and under what Kestrel accepts in a request line: past
        // about 8 KB the server itself answers 414 before this route is reached (seen against the
        // real host; the in-process test server has no such limit and would answer 200 to 20,000).
        var environment = await NewEnvironmentAsync("acme.long.A");

        Assert.Empty(await NamesAsync(environment, "prefix=" + new string('a', 2_000)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("prefix=")]
    [InlineData("prefix=%20%20")]
    [InlineData("other=1")]
    public async Task NoPrefix_BlankOrAbsent_IsTheWholeList(string query)
    {
        var environment = await NewEnvironmentAsync("acme.all.A", "acme.all.B", "zeta.all.C");

        Assert.Equal(
            ["acme.all.A", "acme.all.B", "zeta.all.C"], await NamesAsync(environment, query));
    }

    [Fact]
    public async Task SurroundingWhitespaceInThePrefix_IsTrimmed()
    {
        var environment = await NewEnvironmentAsync("acme.trim.A", "acme.trim.B");

        Assert.Equal(["acme.trim.A", "acme.trim.B"], await NamesAsync(environment, "prefix=%20acme.trim.%20"));
        Assert.Equal(["acme.trim.A", "acme.trim.B"], await NamesAsync(environment, "prefix=acme.trim.%0A"));
    }

    [Fact]
    public async Task TheKeyIsMatchedWithoutRegardToCase_AsEveryOtherQueryKeyIs()
    {
        var environment = await NewEnvironmentAsync("acme.key.A", "zeta.key.B");

        Assert.Equal(["acme.key.A"], await NamesAsync(environment, "Prefix=acme."));
    }

    [Fact]
    public async Task AnotherEnvironmentsSubjectsAreNotListed()
    {
        var mine = await NewEnvironmentAsync("acme.env.Mine");
        await NewEnvironmentAsync("acme.env.Theirs");

        Assert.Equal(["acme.env.Mine"], await NamesAsync(mine, "prefix=acme.env."));
    }

    [Fact]
    public async Task AFilteredSubject_IsExactlyTheSubjectTheUnfilteredListGives()
    {
        // The filter chooses rows; it does not reshape them. Versions, owner and policy are the same
        // bytes, so a client that switches between the two needs no second mapping.
        var environment = await NewEnvironmentAsync("acme.shape.A", "acme.other.B");
        var client = factory.CreateClient();

        var filtered = await client.GetStringAsync(
            $"/v1/environments/{environment}/subjects?prefix=acme.shape.");
        var all = await client.GetStringAsync($"/v1/environments/{environment}/subjects");

        using var filteredDoc = JsonDocument.Parse(filtered);
        using var allDoc = JsonDocument.Parse(all);
        var inAll = allDoc.RootElement.EnumerateArray()
            .Single(e => e.GetProperty("name").GetString() == "acme.shape.A");

        Assert.Equal(inAll.GetRawText(), Assert.Single(filteredDoc.RootElement.EnumerateArray()).GetRawText());
    }
}
