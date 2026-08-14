using System.Net;
using System.Net.Http.Json;

namespace Concordat.Api.IntegrationTests;

/// <summary>M7.1's environment and broker routes, over real HTTP against real PostgreSQL.</summary>
[Collection(ApiCollection.Name)]
public class EnvironmentApiTests(ApiFactory factory)
{
    private static string Unique() => $"env-{Guid.CreateVersion7():N}"[..24];

    private static async Task<EnvironmentResponse> CreateAsync(
        HttpClient client, string name, object? extra = null)
    {
        var body = extra ?? new CreateEnvironmentRequest(name);
        var response = await client.PostAsJsonAsync("/v1/environments", body, ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ApiFactory.ReadAsync<EnvironmentResponse>(response);
    }

    [Fact]
    public async Task ACreatedEnvironmentComesBackWithItsDefaults()
    {
        var client = factory.CreateClient();
        var name = Unique();

        var created = await CreateAsync(client, name);

        Assert.Equal(name, created.Name);
        Assert.Equal("OPEN", created.RegistrationPolicy);
        Assert.Equal("BACKWARD", created.DefaultCompatibilityPolicy.Mode);
        Assert.Equal("WIRE_JSON", created.DefaultCompatibilityPolicy.Surface);
        Assert.Empty(created.Brokers);
    }

    [Fact]
    public async Task NamesAreFoldedToLowercase()
    {
        var client = factory.CreateClient();
        var name = Unique();

        var created = await CreateAsync(client, name.ToUpperInvariant());

        Assert.Equal(name, created.Name);
    }

    [Fact]
    public async Task AnInvalidNameIs400()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/v1/environments", new CreateEnvironmentRequest("not valid!"), ApiFactory.Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreatingTheSameNameTwiceIs409()
    {
        var client = factory.CreateClient();
        var name = Unique();
        await CreateAsync(client, name);

        var again = await client.PostAsJsonAsync(
            "/v1/environments", new CreateEnvironmentRequest(name), ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task AnUnknownEnvironmentIs404()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync($"/v1/environments/{Unique()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RegistrationPolicyTokensRoundTrip()
    {
        // CI_ONLY, not CIONLY. The whole class of bug M6.1 found across the rest of the API
        // was an enum member name uppercased and shipped as protocol.
        var client = factory.CreateClient();
        var name = Unique();

        var created = await CreateAsync(
            client,
            name,
            new CreateEnvironmentRequest(name, RegistrationPolicy: "CI_ONLY"));

        Assert.Equal("CI_ONLY", created.RegistrationPolicy);
    }

    [Fact]
    public async Task PoliciesCanBeChanged()
    {
        var client = factory.CreateClient();
        var name = Unique();
        await CreateAsync(client, name);

        var response = await client.PatchAsJsonAsync(
            $"/v1/environments/{name}",
            new UpdateEnvironmentRequest(
                Description: "the one that matters",
                CompatibilityMode: "FULL_TRANSITIVE",
                CompatibilitySurface: "SOURCE",
                RegistrationPolicy: "CLOSED"),
            ApiFactory.Json);

        var updated = await ApiFactory.ReadAsync<EnvironmentResponse>(response);

        Assert.Equal("the one that matters", updated.Description);
        Assert.Equal("FULL_TRANSITIVE", updated.DefaultCompatibilityPolicy.Mode);
        Assert.Equal("SOURCE", updated.DefaultCompatibilityPolicy.Surface);
        Assert.Equal("CLOSED", updated.RegistrationPolicy);
    }

    [Fact]
    public async Task HalfAPolicyIsRefused()
    {
        // A policy is a pair (ADR-016). Applying one axis would leave the environment in a
        // state nobody asked for.
        var client = factory.CreateClient();
        var name = Unique();
        await CreateAsync(client, name);

        var response = await client.PatchAsJsonAsync(
            $"/v1/environments/{name}",
            new UpdateEnvironmentRequest(CompatibilityMode: "FULL"),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ------------------------------------------------------------------- brokers

    [Fact]
    public async Task ABrokerIsRegisteredAndNeverReportsItsCredential()
    {
        var client = factory.CreateClient();
        var name = Unique();
        await CreateAsync(client, name);

        var response = await client.PostAsJsonAsync(
            $"/v1/environments/{name}/brokers",
            new AddBrokerRequest("local", "amqp://localhost:5672"),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var environment = await ApiFactory.ReadAsync<EnvironmentResponse>(response);
        var broker = Assert.Single(environment.Brokers);

        Assert.Equal("local", broker.DisplayName);
        Assert.Equal("/", broker.VirtualHost);
        Assert.False(broker.UseTls);
        Assert.Equal("UNKNOWN", broker.Status);

        // Credentials are write-only over the API (ADR-012): the response says whether one
        // exists and never what it is. There is no field here that could carry it.
        Assert.False(broker.HasCredentials);
    }

    [Fact]
    public async Task TheSameHostOnADifferentVirtualHostIsAllowed()
    {
        var client = factory.CreateClient();
        var name = Unique();
        await CreateAsync(client, name);

        await client.PostAsJsonAsync(
            $"/v1/environments/{name}/brokers",
            new AddBrokerRequest("eu-1-orders", "amqps://rabbit-eu:5671", "/orders"),
            ApiFactory.Json);

        var second = await client.PostAsJsonAsync(
            $"/v1/environments/{name}/brokers",
            new AddBrokerRequest("eu-1-billing", "amqps://rabbit-eu:5671", "/billing"),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        var environment = await ApiFactory.ReadAsync<EnvironmentResponse>(second);
        Assert.Equal(2, environment.Brokers.Count);
        Assert.All(environment.Brokers, b => Assert.True(b.UseTls));
    }

    [Fact]
    public async Task ADuplicateEndpointIs409()
    {
        var client = factory.CreateClient();
        var name = Unique();
        await CreateAsync(client, name);

        await client.PostAsJsonAsync(
            $"/v1/environments/{name}/brokers",
            new AddBrokerRequest("first", "amqp://localhost:5672"),
            ApiFactory.Json);

        var duplicate = await client.PostAsJsonAsync(
            $"/v1/environments/{name}/brokers",
            new AddBrokerRequest("second", "amqp://localhost:5672"),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task ANonAmqpSchemeIs400()
    {
        var client = factory.CreateClient();
        var name = Unique();
        await CreateAsync(client, name);

        var response = await client.PostAsJsonAsync(
            $"/v1/environments/{name}/brokers",
            new AddBrokerRequest("web", "https://example.com"),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ABrokerCanBeRemoved()
    {
        var client = factory.CreateClient();
        var name = Unique();
        await CreateAsync(client, name);

        var added = await client.PostAsJsonAsync(
            $"/v1/environments/{name}/brokers",
            new AddBrokerRequest("local", "amqp://localhost:5672"),
            ApiFactory.Json);

        var environment = await ApiFactory.ReadAsync<EnvironmentResponse>(added);
        var brokerId = environment.Brokers[0].BrokerId;

        var removed = await client.DeleteAsync($"/v1/environments/{name}/brokers/{brokerId}");
        var after = await ApiFactory.ReadAsync<EnvironmentResponse>(removed);

        Assert.Empty(after.Brokers);
    }

    // ------------------------------------------------------- the id-adoption path

    [Fact]
    public async Task SubjectsRegisteredBeforeTheEnvironmentExistedStayVisible()
    {
        // The M7 migration commitment, discharged. Before this milestone the routes worked
        // with no Environment row at all, because the id was derived from the name. Creating
        // the environment afterwards adopts that same id -- so subjects registered under the
        // old scheme are still found. A freshly generated id would have orphaned every one of
        // them, silently.
        var client = factory.CreateClient();
        var name = Unique();
        var subject = $"acme.adopt.S{Guid.CreateVersion7():N}";

        var createdSubject = await client.PostAsJsonAsync(
            $"/v1/environments/{name}/subjects",
            new CreateSubjectRequest(subject, "json", "alice"),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, createdSubject.StatusCode);

        // Now the environment gains a row of its own.
        await CreateAsync(client, name);

        var listed = await client.GetAsync($"/v1/environments/{name}/subjects");
        var subjects = await ApiFactory.ReadAsync<List<SubjectResponse>>(listed);

        Assert.Contains(subjects, s => s.Name == subject);
    }
}
