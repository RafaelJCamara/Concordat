using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Concordat.Domain.Registry;
using Concordat.Formats.Abstractions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Concordat.Api.IntegrationTests;

/// <summary>
/// ADR-039's exemption is "reachable by the caller's tenant", not "stored", and only a second
/// tenant can tell the two apart.
/// </summary>
/// <remarks>
/// <para>
/// Schemas are stored once across tenants (ADR-015). A gate that excused a document because
/// <em>anyone</em> had stored it would answer "may I register this?" differently depending on
/// what another organisation had registered, which makes registration the oracle that
/// <c>GET /v1/schemas/{id}</c> and <c>schemas/lookup</c> were built not to be: refuse once, then
/// accept, and the second answer says somebody else holds exactly this document.
/// </para>
/// <para>
/// Nothing in the single-tenant suite can fail on that, because with one tenant "stored" and
/// "reachable" are the same set. The document is put in the database by a host whose validity
/// checker finds nothing, as the build before the rule did, exactly as
/// <see cref="SchemaValidityApiTests"/> builds "registered before the rule".
/// </para>
/// </remarks>
[Collection(CloudApiCollection.Name)]
public class SchemaValidityTenancyTests(CloudApiFactory factory)
{
    private const string Env = "dev";

    private sealed class NoValidity : ISchemaValidityChecker
    {
        public SchemaFormat Format => SchemaFormat.Json;

        public IReadOnlyList<SchemaDefect> Check(string canonicalBody) => [];
    }

    /// <summary>
    /// Fresh for every test: every test here shares one database and schemas are
    /// content-addressed, so a reused document would already be held by the second test.
    /// </summary>
    private static string NotASchema() =>
        $$"""{"type":"bogus","title":"{{Guid.CreateVersion7():N}}"}""";

    private static string UniqueSlug() => $"org-{Guid.CreateVersion7():N}"[..20];

    private static string UniqueSubject() => $"acme.validity.S{Guid.CreateVersion7():N}";

    private static HttpClient As(WebApplicationFactory<Program> host, string credential)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return client;
    }

    private static async Task<string> NewSubjectAsync(HttpClient client)
    {
        await client.PostAsJsonAsync(
            "/v1/environments", new CreateEnvironmentRequest(Env), ApiFactory.Json);

        var name = UniqueSubject();
        var created = await client.PostAsJsonAsync(
            $"/v1/environments/{Env}/subjects",
            new CreateSubjectRequest(name, "json", "owner", null, null, "open"),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return name;
    }

    private static Task<HttpResponseMessage> RegisterAsync(
        HttpClient client, string subject, string document) =>
        client.PostAsJsonAsync(
            $"/v1/environments/{Env}/subjects/{subject}/versions",
            new RegisterVersionRequest(document, null, null, "owner"),
            ApiFactory.Json);

    [Fact]
    public async Task ADocumentAnotherTenantHolds_IsStillRefusedToThisOne()
    {
        var document = NotASchema();
        var holder = await factory.NewOrganisationAsync(UniqueSlug());
        var stranger = await factory.NewOrganisationAsync(UniqueSlug());

        // The holder registers it as the build before the rule would have.
        using var older = factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISchemaValidityChecker>();
            services.AddSingleton<ISchemaValidityChecker, NoValidity>();
        }));

        var asHolderThen = As(older, holder);
        var held = await RegisterAsync(asHolderThen, await NewSubjectAsync(asHolderThen), document);
        Assert.Equal(HttpStatusCode.Created, held.StatusCode);

        // The exemption is real for the tenant that holds it: the same document again, into a
        // second subject of theirs, on the host that has the rule. Without this the refusal
        // below could be the rule refusing everything.
        var asHolder = As(factory, holder);
        var again = await RegisterAsync(asHolder, await NewSubjectAsync(asHolder), document);
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);

        // And for a tenant that does not, the document is a stranger's, however stored.
        var asStranger = As(factory, stranger);
        var refused = await RegisterAsync(asStranger, await NewSubjectAsync(asStranger), document);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("schema_invalid", (await ApiFactory.ReadProblemAsync(refused)).ConcordatCode);
    }

    [Fact]
    public async Task AFreshDocument_IsRefusedToEveryTenant_SoTheTestAboveIsAboutHoldingAndNotTheRule()
    {
        // Control: with nobody holding it, both tenants get the same answer, so the difference
        // the first test measures is the exemption and not the tenants.
        var document = NotASchema();

        foreach (var credential in new[]
        {
            await factory.NewOrganisationAsync(UniqueSlug()),
            await factory.NewOrganisationAsync(UniqueSlug()),
        })
        {
            var client = As(factory, credential);
            var refused = await RegisterAsync(client, await NewSubjectAsync(client), document);

            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }
    }
}
