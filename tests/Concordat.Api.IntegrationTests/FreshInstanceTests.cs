using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Concordat.Api.IntegrationTests;

/// <summary>
/// An API host with its own database that has <b>not</b> been told to answer an unclaimed instance as its
/// owner, which is how a fresh install runs (ADR-047).
/// </summary>
/// <remarks>
/// <see cref="ApiFactory"/> opts in, because the rest of the suite is written against an instance that
/// answers every request. This is the one place the default itself is looked at, so it is a class
/// fixture and not a collection one: it claims its instance, and nothing else may share it.
/// </remarks>
public sealed class ClosedApiFactory : ApiFactory
{
    /// <inheritdoc />
    /// <remarks>Absent, not set to false: absent is the default under test, and a test that wrote the value would pass if the default were ever changed back.</remarks>
    protected override bool OpenUnclaimedInstance => false;
}

/// <summary>
/// What a brand-new instance does when nobody has claimed it (ADR-047).
/// </summary>
public class FreshInstanceTests(ClosedApiFactory factory) : IClassFixture<ClosedApiFactory>
{
    private const string Email = "first-owner@example.com";
    private const string Password = "correct horse battery staple";

    [Fact]
    public void TheSwitchIsOffUnlessSomebodyTurnsItOn()
    {
        // The host test above runs with the setting absent; this pins the value it falls back to, so the
        // two cannot drift apart.
        Assert.False(new AuthenticationOptions().AllowAnonymousUntilClaimed);
    }

    [Fact]
    public async Task AnUnclaimedInstanceAnswersNoRequestAsItsOwner()
    {
        var client = factory.CreateClient();

        // Reads and writes alike: nothing but the claim is open to somebody with no credential.
        foreach (var (method, route) in new[]
        {
            (HttpMethod.Get, "/v1/environments"),
            (HttpMethod.Get, "/v1/environments/dev/subjects"),
            (HttpMethod.Get, "/v1/audit"),
            (HttpMethod.Post, "/v1/environments"),
        })
        {
            using var request = new HttpRequestMessage(method, route);
            using var response = await client.SendAsync(request);

            Assert.True(
                response.StatusCode is HttpStatusCode.Unauthorized,
                $"{method} {route} answered {(int)response.StatusCode} on a fresh instance.");
        }
    }

    [Fact]
    public async Task ItSaysSoToAClientThatAsks_SoTheWebAppCanOfferSetup()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/v1/auth/status");

        Assert.True(response.StatusCode is HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var status = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.False(status["claimed"]!.GetValue<bool>());
        Assert.False(status["authenticated"]!.GetValue<bool>());
        Assert.Empty(status["scopes"]!.AsArray());
    }

    [Fact]
    public async Task ItIsStillClaimableAndTheClaimClosesIt()
    {
        // Own host, own database: this test claims, which the others in this class must not see.
        await using var fresh = new ClosedApiFactory();
        await ((Xunit.IAsyncLifetime)fresh).InitializeAsync();

        try
        {
            var client = fresh.CreateClient();

            var claimed = await client.PostAsJsonAsync(
                "/v1/auth/bootstrap", new BootstrapRequest(Email, Password, "Owner"), ApiFactory.Json);
            Assert.Equal(HttpStatusCode.Created, claimed.StatusCode);

            var signIn = await client.PostAsJsonAsync(
                "/v1/auth/signin", new SignInRequest(Email, Password), ApiFactory.Json);
            Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
            var credential = (await ApiFactory.ReadAsync<SignInResponse>(signIn)).Credential;

            using var anonymous = await client.GetAsync("/v1/environments");
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

            using var authenticated = new HttpRequestMessage(HttpMethod.Get, "/v1/environments");
            authenticated.Headers.Authorization = new("Bearer", credential);
            using var response = await client.SendAsync(authenticated);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // Claiming happens once.
            var again = await client.PostAsJsonAsync(
                "/v1/auth/bootstrap", new BootstrapRequest("someone@example.com", Password, null), ApiFactory.Json);
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        }
        finally
        {
            await ((Xunit.IAsyncLifetime)fresh).DisposeAsync();
        }
    }

    [Fact]
    public async Task LivenessAndReadinessNeedNoCredential_SoAnOrchestratorCanProbeIt()
    {
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }
}
