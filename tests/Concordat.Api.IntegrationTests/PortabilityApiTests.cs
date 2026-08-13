using System.Net;
using System.Net.Http.Json;

namespace Concordat.Api.IntegrationTests;

/// <summary>
/// M6.1's portability findings, over real HTTP.
/// </summary>
/// <remarks>
/// These are published protocol: they ride on the registration response and a pipeline may
/// branch on the <c>kind</c> token. A finding that is correct in the checker and lost on the
/// way out reaches nobody, so the projection needs asserting separately from the rules.
/// </remarks>
[Collection(ApiCollection.Name)]
public class PortabilityApiTests(ApiFactory factory)
{
    private const string Env = "test";

    private static string Unique(string stem) =>
        $"acme.{stem}.S{Guid.CreateVersion7():N}";

    private static async Task<string> NewSubjectAsync(HttpClient client)
    {
        var name = Unique("portability");
        var response = await client.PostAsJsonAsync(
            $"/v1/environments/{Env}/subjects",
            new CreateSubjectRequest(name, "json", "alice"),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return name;
    }

    private static Task<HttpResponseMessage> RegisterAsync(
        HttpClient client, string subject, string schema) =>
        client.PostAsJsonAsync(
            $"/v1/environments/{Env}/subjects/{subject}/versions",
            new RegisterVersionRequest(schema, null, null, "alice"),
            ApiFactory.Json);

    [Fact]
    public async Task AnOrdinarySchemaReportsNoFindings()
    {
        var client = factory.CreateClient();
        var subject = await NewSubjectAsync(client);

        var response = await RegisterAsync(
            client, subject, """{"type":"object","properties":{"id":{"type":"string"}}}""");

        var body = await ApiFactory.ReadAsync<RegisterVersionResponse>(response);
        Assert.Empty(body.Portability);
    }

    [Fact]
    public async Task AKeywordOutsideTheInteroperableSubsetWarnsWithoutBlocking()
    {
        var client = factory.CreateClient();
        var subject = await NewSubjectAsync(client);

        var response = await RegisterAsync(
            client, subject, """{"type":"object","oneOf":[{"required":["a"]}]}""");

        // A warning, not a refusal. These schemas are legal and usually intentional; refusing
        // them would be Confluent's mistake in the other direction.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await ApiFactory.ReadAsync<RegisterVersionResponse>(response);
        var finding = Assert.Single(body.Portability);

        Assert.Equal("keyword_not_compared", finding.Kind);
        Assert.Equal("WARNING", finding.Severity);
        Assert.Equal("#/oneOf", finding.Path);

        // The message has to say what it costs, or it gets suppressed along with the useful ones.
        Assert.Contains("reported as compatible", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARegexGoCannotCompileIsReported()
    {
        var client = factory.CreateClient();
        var subject = await NewSubjectAsync(client);

        var response = await RegisterAsync(
            client,
            subject,
            """{"type":"object","properties":{"code":{"type":"string","pattern":"^(?=.*[A-Z]).+$"}}}""");

        var body = await ApiFactory.ReadAsync<RegisterVersionResponse>(response);
        var finding = Assert.Single(body.Portability);

        Assert.Equal("regex_not_portable", finding.Kind);
        Assert.Contains("RE2", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnsupportedDialectIsRefused()
    {
        var client = factory.CreateClient();
        var subject = await NewSubjectAsync(client);

        var response = await RegisterAsync(
            client,
            subject,
            """{"$schema":"http://json-schema.org/draft-07/schema#","type":"object"}""");

        // The one error-severity finding, and the only one that refuses. Keywords changed
        // meaning between drafts, so validating a draft-07 document under 2020-12 rules would
        // apply rules its author never wrote against.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonProblem>(ApiFactory.Json);

        Assert.NotNull(problem);
        Assert.Equal("schema_dialect_unsupported", problem.ConcordatCode);
    }

    [Fact]
    public async Task TheSupportedDialectIsAcceptedExplicitly()
    {
        // The pair to the refusal: declaring 2020-12 must not be treated as "declaring a
        // dialect at all is suspicious".
        var client = factory.CreateClient();
        var subject = await NewSubjectAsync(client);

        var response = await RegisterAsync(
            client,
            subject,
            """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object"}""");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>The bits of RFC 9457 these tests read.</summary>
    /// <param name="ConcordatCode">The stable code clients branch on.</param>
    private sealed record JsonProblem(string? ConcordatCode);
}
