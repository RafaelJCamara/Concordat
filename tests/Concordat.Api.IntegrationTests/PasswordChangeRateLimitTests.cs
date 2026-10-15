using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;

namespace Concordat.Api.IntegrationTests;

/// <summary>
/// A host that, unlike the rest of the suite, leaves the rate limiter on (see
/// <c>Concordat:DisableRateLimiting</c> in <c>Program.cs</c>).
/// </summary>
public sealed class RateLimitedApiFactory : ApiFactory
{
    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        base.ConfigureWebHost(builder);

        // The in-memory TestServer has no remote address, so every request falls in one partition,
        // which is what lets a test exhaust the limit without a thousand clients.
        builder.UseSetting("Concordat:DisableRateLimiting", "false");
    }
}

/// <summary>
/// Changing a password is limited like signing in (ADR-048): both guess at a password, and one
/// budget per client is what stops a stolen session being used to guess the current one.
/// </summary>
public class PasswordChangeRateLimitTests(RateLimitedApiFactory factory)
    : IClassFixture<RateLimitedApiFactory>
{
    private const string Email = "owner@example.com";
    private const string Password = "correct horse battery";

    [Fact]
    public async Task GuessingTheCurrentPasswordIsStoppedByTheLimitSignInSharesWithIt()
    {
        var client = factory.CreateClient();

        var bootstrap = await client.PostAsJsonAsync(
            "/v1/auth/bootstrap", new BootstrapRequest(Email, Password, "Owner"), ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Created, bootstrap.StatusCode);

        var signIn = await ApiFactory.ReadAsync<SignInResponse>(
            await client.PostAsJsonAsync(
                "/v1/auth/signin", new SignInRequest(Email, Password), ApiFactory.Json));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", signIn.Credential);

        // The limit is 20 in five minutes, per client, and bootstrap and sign-in have used two.
        var statuses = new List<HttpStatusCode>();

        for (var guess = 0; guess < 30; guess++)
        {
            var response = await client.PostAsJsonAsync(
                "/v1/auth/password",
                new ChangePasswordRequest($"guess number {guess}", "a replacement password"),
                ApiFactory.Json);

            statuses.Add(response.StatusCode);
        }

        var firstRefusal = statuses.IndexOf(HttpStatusCode.TooManyRequests);

        Assert.True(firstRefusal >= 0, "thirty wrong guesses were all answered");
        Assert.True(firstRefusal <= 18, $"the limit let {firstRefusal} guesses through");

        // Every answer before it was an ordinary wrong-password refusal, and every one after is the limit.
        Assert.All(statuses.Take(firstRefusal), s => Assert.Equal(HttpStatusCode.Unauthorized, s));
        Assert.All(statuses.Skip(firstRefusal), s => Assert.Equal(HttpStatusCode.TooManyRequests, s));

        // The same budget: signing in is refused too, which is what "like sign-in" means.
        var after = await factory.CreateClient().PostAsJsonAsync(
            "/v1/auth/signin", new SignInRequest(Email, Password), ApiFactory.Json);
        Assert.Equal(HttpStatusCode.TooManyRequests, after.StatusCode);
    }
}
