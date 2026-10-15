using System.Net;
using System.Net.Http.Json;
using Concordat.Domain.Registry;
using Concordat.Formats.Abstractions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Concordat.Api.IntegrationTests;

/// <summary>
/// A pattern no regular-expression engine accepts is refused like any document that is not a schema, and
/// one the registry already holds keeps working (ADR-044, over ADR-039's rule).
/// </summary>
[Collection(ApiCollection.Name)]
public class PatternRefusalApiTests(ApiFactory factory)
{
    private const string Env = "test";

    private static string Unique(string stem) => $"acme.{stem}.S{Guid.CreateVersion7():N}";

    /// <summary>A schema nobody has registered: content-addressed, so a fresh title keeps tests apart.</summary>
    private static string WithPattern(string pattern) =>
        $$"""{"type":"string","title":"{{Guid.CreateVersion7():N}}","pattern":"{{pattern}}"}""";

    private sealed class NoValidity : ISchemaValidityChecker
    {
        public SchemaFormat Format => SchemaFormat.Json;

        public IReadOnlyList<SchemaDefect> Check(string canonicalBody) => [];
    }

    private WebApplicationFactory<Program> BeforeTheRule() =>
        factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISchemaValidityChecker>();
            services.AddSingleton<ISchemaValidityChecker, NoValidity>();
        }));

    private static async Task<string> NewSubjectAsync(HttpClient client)
    {
        var name = Unique("pattern");
        var response = await client.PostAsJsonAsync(
            $"/v1/environments/{Env}/subjects",
            new CreateSubjectRequest(name, "json", "alice"),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return name;
    }

    private static Task<HttpResponseMessage> RegisterAsync(HttpClient client, string subject, string schema) =>
        client.PostAsJsonAsync(
            $"/v1/environments/{Env}/subjects/{subject}/versions",
            new RegisterVersionRequest(schema, null, null, "alice"),
            ApiFactory.Json);

    private static Task<HttpResponseMessage> CheckAsync(HttpClient client, string subject, string schema) =>
        client.PostAsJsonAsync(
            $"/v1/environments/{Env}/subjects/{subject}/compatibility",
            new CheckCompatibilityRequest(schema),
            ApiFactory.Json);

    [Theory]
    [InlineData("(")]
    [InlineData("a)")]
    [InlineData("*abc")]
    [InlineData("[z-a]")]
    [InlineData("a{3,2}")]
    [InlineData("[a-")]
    public async Task APatternNoEngineAccepts_IsRefusedWithTheLocationAndTheReason(string pattern)
    {
        var client = factory.CreateClient();
        var subject = await NewSubjectAsync(client);

        var response = await RegisterAsync(client, subject, WithPattern(pattern));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ApiFactory.ReadProblemAsync(response);
        Assert.Equal("schema_invalid", problem.ConcordatCode);
        Assert.Contains("#/pattern", problem.Detail, StringComparison.Ordinal);
        Assert.Contains("any dialect", problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDryRunCheckRefusesWhatARegistrationWould_SoACheckCannotGoGreenForAPublishThatFails()
    {
        var client = factory.CreateClient();
        var subject = await NewSubjectAsync(client);

        var response = await CheckAsync(client, subject, WithPattern("("));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("schema_invalid", (await ApiFactory.ReadProblemAsync(response)).ConcordatCode);
    }

    [Theory]
    [InlineData("[^]")]
    [InlineData("a++")]
    [InlineData("\\\\p{Letter}")]
    public async Task APatternSomeEngineAccepts_Registers(string pattern)
    {
        var client = factory.CreateClient();
        var subject = await NewSubjectAsync(client);

        var response = await RegisterAsync(client, subject, WithPattern(pattern));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task APatternTheRegistryAlreadyHolds_KeepsRegisteringIntoAnotherSubject()
    {
        // Stored by the build before the rule, as ADR-039 arranges it: the document is in the table, and an
        // idempotent registration of it elsewhere must not turn into a failure nothing the caller did caused.
        var schema = WithPattern("(");
        using var before = BeforeTheRule();
        var firstSubject = await NewSubjectAsync(before.CreateClient());

        var stored = await RegisterAsync(before.CreateClient(), firstSubject, schema);
        Assert.Equal(HttpStatusCode.Created, stored.StatusCode);

        var client = factory.CreateClient();
        var secondSubject = await NewSubjectAsync(client);

        var again = await RegisterAsync(client, secondSubject, schema);

        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
    }

    [Fact]
    public async Task ADocumentTheRegistryHoldsStillAnswersItsOwnSubject_Idempotently()
    {
        var schema = WithPattern("(");
        using var before = BeforeTheRule();
        var subject = await NewSubjectAsync(before.CreateClient());
        Assert.Equal(HttpStatusCode.Created, (await RegisterAsync(before.CreateClient(), subject, schema)).StatusCode);

        var again = await RegisterAsync(factory.CreateClient(), subject, schema);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }
}
