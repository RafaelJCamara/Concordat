using System.Net;
using System.Net.Http.Json;
using Concordat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Concordat.Api.IntegrationTests;

/// <summary>
/// M7.2: broker credentials are write-only over the API and encrypted at rest.
/// </summary>
/// <remarks>
/// The assertions worth having here are negative ones. That a credential can be stored is
/// mundane; that it cannot be read back, and is not sitting in the database in plaintext, is
/// the entire feature.
/// </remarks>
[Collection(ApiCollection.Name)]
public class BrokerCredentialTests(ApiFactory factory)
{
    private const string Password = "correct-horse-battery-staple";

    private static string Unique() => $"cred-{Guid.CreateVersion7():N}"[..24];

    private static async Task<(string Env, Guid BrokerId)> NewBrokerAsync(HttpClient client)
    {
        var env = Unique();

        var created = await client.PostAsJsonAsync(
            "/v1/environments", new CreateEnvironmentRequest(env), ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var added = await client.PostAsJsonAsync(
            $"/v1/environments/{env}/brokers",
            new AddBrokerRequest("local", "amqp://localhost:5672"),
            ApiFactory.Json);

        var environment = await ApiFactory.ReadAsync<EnvironmentResponse>(added);
        return (env, environment.Brokers[0].BrokerId);
    }

    private static Task<HttpResponseMessage> SetAsync(
        HttpClient client, string env, Guid brokerId, string password) =>
        client.PutAsJsonAsync(
            $"/v1/environments/{env}/brokers/{brokerId}/credentials",
            new SetBrokerCredentialRequest("app-user", password),
            ApiFactory.Json);

    /// <summary>
    /// Reads a broker's credential reference from the database.
    /// </summary>
    /// <remarks>
    /// Deliberately going around the API, because the API never exposes the reference — which
    /// is the property under test. Counting rows in the whole table would be simpler and
    /// wrong: this suite shares a database with every other test in the collection, so a
    /// global count measures whatever else happened to run.
    /// </remarks>
    private async Task<string?> CredentialRefAsync(Guid brokerId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ConcordatDbContext>();

        var refs = await context.Database
            .SqlQuery<string?>(
                $"SELECT credential_ref AS \"Value\" FROM broker_connection WHERE broker_id = {brokerId}")
            .ToListAsync(CancellationToken.None);

        return refs.SingleOrDefault();
    }

    private async Task<int> CredentialRowsAsync(string reference)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ConcordatDbContext>();

        return await context.Database
            .SqlQuery<int>(
                $"SELECT COUNT(*)::int AS \"Value\" FROM broker_credential WHERE credential_ref = {reference}")
            .SingleAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SettingACredentialReportsThatOneExistsAndNothingMore()
    {
        var client = factory.CreateClient();
        var (env, brokerId) = await NewBrokerAsync(client);

        var response = await SetAsync(client, env, brokerId, Password);
        var environment = await ApiFactory.ReadAsync<EnvironmentResponse>(response);

        Assert.True(environment.Brokers[0].HasCredentials);
    }

    [Fact]
    public async Task NoResponseBodyEverContainsTheSecret()
    {
        // The load-bearing test. Every read surface that could plausibly carry it is checked
        // as raw text, so a future field added to BrokerResponse that happens to serialise the
        // credential fails here rather than in production.
        var client = factory.CreateClient();
        var (env, brokerId) = await NewBrokerAsync(client);

        var write = await SetAsync(client, env, brokerId, Password);

        var bodies = new List<string>
        {
            await write.Content.ReadAsStringAsync(),
            await (await client.GetAsync($"/v1/environments/{env}")).Content.ReadAsStringAsync(),
            await (await client.GetAsync("/v1/environments")).Content.ReadAsStringAsync(),
        };

        foreach (var body in bodies)
        {
            Assert.DoesNotContain(Password, body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ThereIsNoEndpointThatReturnsACredential()
    {
        // Write-only means the read does not exist, not that it is filtered. A GET on the
        // credentials route must not quietly resolve to something else.
        var client = factory.CreateClient();
        var (env, brokerId) = await NewBrokerAsync(client);
        await SetAsync(client, env, brokerId, Password);

        var response = await client.GetAsync(
            $"/v1/environments/{env}/brokers/{brokerId}/credentials");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task TheStoredValueIsNotPlaintextInTheDatabase()
    {
        // "Encrypted at rest" asserted against the actual row rather than trusted because a
        // protector was called. A misconfigured protector that returned its input would pass
        // every other test in this file.
        var client = factory.CreateClient();
        var (env, brokerId) = await NewBrokerAsync(client);
        await SetAsync(client, env, brokerId, Password);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ConcordatDbContext>();

        var ciphertexts = await context.Database
            .SqlQuery<string>($"SELECT ciphertext FROM broker_credential")
            .ToListAsync(CancellationToken.None);

        Assert.NotEmpty(ciphertexts);
        Assert.All(ciphertexts, c => Assert.DoesNotContain(Password, c, StringComparison.Ordinal));
        Assert.All(ciphertexts, c => Assert.DoesNotContain("app-user", c, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReplacingACredentialRotatesInPlace()
    {
        // A rotation that allocated a new reference would leave the previous secret in the
        // table under a key nothing points at — undeletable in practice, because nothing
        // remembers it was ever there.
        var client = factory.CreateClient();
        var (env, brokerId) = await NewBrokerAsync(client);

        await SetAsync(client, env, brokerId, Password);
        var first = await CredentialRefAsync(brokerId);

        await SetAsync(client, env, brokerId, "a-different-secret");
        var second = await CredentialRefAsync(brokerId);

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.Equal(1, await CredentialRowsAsync(first));
    }

    [Fact]
    public async Task RemovingACredentialDeletesTheStoredSecret()
    {
        // Clearing the reference alone would leave an unreachable ciphertext row behind for as
        // long as the database lives.
        var client = factory.CreateClient();
        var (env, brokerId) = await NewBrokerAsync(client);
        await SetAsync(client, env, brokerId, Password);

        var reference = await CredentialRefAsync(brokerId);
        Assert.NotNull(reference);

        var response = await client.DeleteAsync(
            $"/v1/environments/{env}/brokers/{brokerId}/credentials");

        var environment = await ApiFactory.ReadAsync<EnvironmentResponse>(response);
        Assert.False(environment.Brokers[0].HasCredentials);
        Assert.Equal(0, await CredentialRowsAsync(reference));
    }

    [Fact]
    public async Task AHalfSuppliedCredentialIsRefused()
    {
        var client = factory.CreateClient();
        var (env, brokerId) = await NewBrokerAsync(client);

        var response = await client.PutAsJsonAsync(
            $"/v1/environments/{env}/brokers/{brokerId}/credentials",
            new SetBrokerCredentialRequest("app-user", ""),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AnUnknownBrokerIs404()
    {
        var client = factory.CreateClient();
        var (env, _) = await NewBrokerAsync(client);

        var response = await SetAsync(client, env, Guid.CreateVersion7(), Password);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
