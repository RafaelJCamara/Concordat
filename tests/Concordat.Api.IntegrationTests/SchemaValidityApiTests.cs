using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Concordat.Domain.Registry;
using Concordat.Formats.Abstractions;
using Concordat.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Concordat.Api.IntegrationTests;

/// <summary>
/// A document that is well-formed JSON and not a schema (ADR-039), over real HTTP and real
/// PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule has two halves, and both are the product.</b> A document the registry has not
/// accepted is refused. A document it already holds keeps working, in every way it did, because
/// the rule is new and the registry is not: a registry that started refusing what it had stored
/// would be a registry nobody could safely upgrade.
/// </para>
/// <para>
/// <b>"Registered before the rule" is built, not simulated.</b> <see cref="BeforeTheRule"/> is a
/// host over the same database whose validity checker finds nothing, which is exactly what the
/// previous build did: the same handlers, the same canonicaliser, the same store. The document
/// arrives by the route it would have arrived by.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public class SchemaValidityApiTests(ApiFactory factory)
{
    private const string Env = "test";

    /// <summary>
    /// A document that is not a schema, and has never been seen by this database.
    /// </summary>
    /// <remarks>
    /// Fresh for every test, and that is not tidiness. Every test in this collection shares one
    /// database and schemas are content-addressed, so the second test to use
    /// <c>{"type":"bogus"}</c> would find the first had stored it, and the rule would then excuse
    /// it as a document the registry already holds. That is the rule working, and it is why the
    /// first run of these tests failed four of them.
    /// </remarks>
    private static string NotASchema() =>
        $$"""{"type":"bogus","title":"{{Guid.CreateVersion7().ToString("N")}}"}""";

    private const string V1 = """{"type":"object","properties":{"id":{"type":"string"}}}""";

    private static string Unique(string stem) => $"acme.{stem}.S{Guid.CreateVersion7():N}";

    /// <summary>A validity checker that finds nothing, as the build before ADR-039 did.</summary>
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

    private WebApplicationFactory<Program> AsProduction() =>
        factory.WithWebHostBuilder(b => b
            .UseEnvironment("Production")
            .UseSetting("ConnectionStrings:Concordat", "Host=placeholder;Database=never;Username=never;Password=never"));

    private static async Task<string> NewSubjectAsync(HttpClient client, string environment = Env)
    {
        var name = Unique("validity");
        var response = await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/subjects",
            new CreateSubjectRequest(name, "json", "alice"),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return name;
    }

    private static Task<HttpResponseMessage> RegisterAsync(
        HttpClient client, string subject, string schema, string environment = Env) =>
        client.PostAsJsonAsync(
            $"/v1/environments/{environment}/subjects/{subject}/versions",
            new RegisterVersionRequest(schema, null, null, "alice"),
            ApiFactory.Json);

    private static Task<HttpResponseMessage> CheckAsync(
        HttpClient client, string subject, string schema) =>
        client.PostAsJsonAsync(
            $"/v1/environments/{Env}/subjects/{subject}/compatibility",
            new CheckCompatibilityRequest(schema),
            ApiFactory.Json);

    private static async Task<string> LookupIdAsync(HttpClient client, string schema)
    {
        var response = await client.PostAsJsonAsync(
            "/v1/schemas/lookup", new LookupSchemaRequest("json", schema), ApiFactory.Json);

        return (await ApiFactory.ReadAsync<LookupSchemaResponse>(response)).SchemaId;
    }

    private async Task<bool> IsStoredAsync(string schemaId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ConcordatDbContext>();

        return await context.Schemas.AnyAsync(s => s.Id == SchemaId.FromTrusted(schemaId));
    }

    // ------------------------------------------------------------------ refused

    [Fact]
    public async Task ADocumentThatIsNotASchema_IsRefusedWithACodeAndTheLocation()
    {
        var client = factory.CreateClient();
        var subject = await NewSubjectAsync(client);

        var response = await RegisterAsync(client, subject, NotASchema());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await ApiFactory.ReadProblemAsync(response);
        Assert.Equal("schema_invalid", problem.ConcordatCode);
        Assert.Contains("#/type", problem.Detail, StringComparison.Ordinal);
        Assert.Contains("bogus", problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRefusedDocument_WasNotStoredAndTheSubjectHasNoVersion()
    {
        var client = factory.CreateClient();
        var subject = await NewSubjectAsync(client);
        var document = """{"type":"bogus","x-marker":"never-stored-""" + Guid.NewGuid().ToString("N") + "\"}";

        var response = await RegisterAsync(client, subject, document);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Not given an id by a path that then lost it: absent from the table every tenant's
        // schemas share, which is where a refused document would otherwise sit offered to nobody
        // and visible to a lookup.
        var id = await LookupIdAsync(client, document);
        Assert.False(await IsStoredAsync(id));

        var versions = await client.GetAsync($"/v1/environments/{Env}/subjects/{subject}/versions");
        Assert.Equal(HttpStatusCode.OK, versions.StatusCode);
        using var json = JsonDocument.Parse(await versions.Content.ReadAsStringAsync());
        Assert.Empty(json.RootElement.EnumerateArray());
    }

    [Theory]
    [InlineData("""{"type":"object","properties":{"a":{"type":"strng"}}}""", "#/properties/a/type")]
    [InlineData("""{"minimum":"5"}""", "#/minimum")]
    [InlineData("""{"required":true}""", "#/required")]
    [InlineData("""{"items":[{"type":"string"}]}""", "#/items")]
    [InlineData("""{"$ref":"#/$defs/Adress","$defs":{"Address":{"type":"string"}}}""", "#/$ref")]
    public async Task EachKindOfDefect_IsRefusedAtItsLocation(string schema, string location)
    {
        var client = factory.CreateClient();
        var subject = await NewSubjectAsync(client);

        var response = await RegisterAsync(client, subject, schema);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ApiFactory.ReadProblemAsync(response);
        Assert.Equal("schema_invalid", problem.ConcordatCode);
        Assert.Contains(location, problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDryRun_RefusesExactlyAsRegistrationDoes()
    {
        var client = factory.CreateClient();
        var subject = await NewSubjectAsync(client);

        var document = NotASchema();
        var registered = await RegisterAsync(client, subject, document);
        var checkedRun = await CheckAsync(client, subject, document);

        // The web editor's parity tests and every CI job that runs `check` before publishing rely
        // on this: the same status, the same code, and the same words.
        Assert.Equal(registered.StatusCode, checkedRun.StatusCode);

        var left = await ApiFactory.ReadProblemAsync(registered);
        var right = await ApiFactory.ReadProblemAsync(checkedRun);

        Assert.Equal("schema_invalid", right.ConcordatCode);
        Assert.Equal(left.ConcordatCode, right.ConcordatCode);
        Assert.Equal(left.Detail, right.Detail);
    }

    [Fact]
    public async Task AValidSchema_StillRegistersAndStillChecks()
    {
        var client = factory.CreateClient();
        var subject = await NewSubjectAsync(client);

        Assert.Equal(HttpStatusCode.OK, (await CheckAsync(client, subject, V1)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await RegisterAsync(client, subject, V1)).StatusCode);
    }

    [Fact]
    public async Task ADocumentThatIsStillDraftSeven_IsTheDialectsRefusalNotTheMetaschemas()
    {
        var client = factory.CreateClient();
        var subject = await NewSubjectAsync(client);

        var response = await RegisterAsync(
            client,
            subject,
            """{"$schema":"http://json-schema.org/draft-07/schema#","items":[{"type":"string"}]}""");

        Assert.Equal("schema_dialect_unsupported", (await ApiFactory.ReadProblemAsync(response)).ConcordatCode);
    }

    [Fact]
    public async Task TheRefusalIsTheSame_WhenTheHostIsProduction()
    {
        // A refusal is a domain error, not a binding failure, so nothing here should differ. The
        // body-binding failure that was a 500 in Development and a 400 in Production once is why
        // this is asserted and not assumed.
        using var production = AsProduction();
        var client = production.CreateClient();
        var subject = await NewSubjectAsync(client);

        var response = await RegisterAsync(client, subject, NotASchema());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("schema_invalid", (await ApiFactory.ReadProblemAsync(response)).ConcordatCode);
    }

    // ------------------------------------------- what the registry already holds

    /// <summary>Registers a document that is not a schema as the build before the rule did.</summary>
    private async Task<(string Subject, string SchemaId)> HeldBeforeTheRuleAsync(
        string document, string environment = Env)
    {
        using var older = BeforeTheRule();
        var client = older.CreateClient();
        var subject = await NewSubjectAsync(client, environment);

        var response = await RegisterAsync(client, subject, document, environment);
        var registered = await ApiFactory.ReadAsync<RegisterVersionResponse>(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (subject, registered.SchemaId);
    }

    [Fact]
    public async Task TheCleanRoomHost_AcceptsWhatTheRuleRefuses_SoTheCasesBelowMeanSomething()
    {
        // If BeforeTheRule() did not actually behave like the previous build, every test below
        // would pass for the wrong reason.
        var document = NotASchema();
        var client = factory.CreateClient();
        var subject = await NewSubjectAsync(client);
        Assert.Equal(HttpStatusCode.BadRequest, (await RegisterAsync(client, subject, document)).StatusCode);

        var (_, id) = await HeldBeforeTheRuleAsync(document);

        Assert.True(await IsStoredAsync(id));

        // And now the very same document, to the very same host that just refused it.
        Assert.Equal(HttpStatusCode.Created, (await RegisterAsync(client, subject, document)).StatusCode);
    }

    [Fact]
    public async Task AHeldDocument_IsStillReadableByItsId_Unchanged()
    {
        var (_, id) = await HeldBeforeTheRuleAsync(NotASchema());
        var client = factory.CreateClient();

        var response = await client.GetAsync($"/v1/schemas/{id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ApiFactory.ReadAsync<SchemaResponse>(response);
        Assert.Equal(id, body.SchemaId);
        Assert.Contains("""
            "type":"bogus"
            """, body.Schema, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegisteringAHeldDocumentAgain_IsStillAnIdempotentNoOp()
    {
        var document = NotASchema();
        var (subject, id) = await HeldBeforeTheRuleAsync(document);
        var client = factory.CreateClient();

        var again = await RegisterAsync(client, subject, document);

        // 200 and not 201, and the same id and ordinal: exactly the answer a retried pipeline got
        // before, and the one that stops it from failing now for a reason it did not cause.
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var body = await ApiFactory.ReadAsync<RegisterVersionResponse>(again);
        Assert.False(body.Created);
        Assert.Equal(id, body.SchemaId);
        Assert.Equal(1, body.Ordinal);
    }

    [Fact]
    public async Task TheDryRunOfAHeldDocument_StillPasses()
    {
        var document = NotASchema();
        var (subject, id) = await HeldBeforeTheRuleAsync(document);
        var client = factory.CreateClient();

        var response = await CheckAsync(client, subject, document);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(id, (await ApiFactory.ReadAsync<CompatibilityResponse>(response)).SchemaId);
    }

    [Fact]
    public async Task AHeldDocument_CanBeRegisteredIntoAnotherSubjectOfTheSameTenant()
    {
        var document = NotASchema();
        var (_, id) = await HeldBeforeTheRuleAsync(document);
        var client = factory.CreateClient();
        var other = await NewSubjectAsync(client);

        var response = await RegisterAsync(client, other, document);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(id, (await ApiFactory.ReadAsync<RegisterVersionResponse>(response)).SchemaId);
    }

    [Fact]
    public async Task AHeldDocument_CanStillBePromoted()
    {
        // Promotion is the other route a held document travels. It copies a version the registry
        // already accepted, so it has to keep working; a registry that could not promote what it
        // had stored would strand every environment's last release.
        var source = $"env-{Guid.CreateVersion7():N}"[..24];
        var target = $"env-{Guid.CreateVersion7():N}"[..24];

        var client = factory.CreateClient();
        foreach (var name in new[] { source, target })
        {
            var created = await client.PostAsJsonAsync(
                "/v1/environments", new CreateEnvironmentRequest(name), ApiFactory.Json);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        var (subject, id) = await HeldBeforeTheRuleAsync(NotASchema(), source);

        var promoted = await client.PostAsJsonAsync(
            $"/v1/environments/{source}/subjects/{subject}/promote",
            new PromoteRequest(target, PromotedBy: "alice"),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, promoted.StatusCode);
        Assert.Equal(id, (await ApiFactory.ReadAsync<PromotionResponse>(promoted)).SchemaId);
    }

    [Fact]
    public async Task ADocumentThatDiffersFromAHeldOne_IsStillRefused()
    {
        // The exemption is for what the registry holds and nothing near it: one more defect is a
        // new document.
        var held = NotASchema();
        await HeldBeforeTheRuleAsync(held);
        var client = factory.CreateClient();
        var subject = await NewSubjectAsync(client);

        var response = await RegisterAsync(
            client, subject, held.Replace("{", """{"x-other":1,""", StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
