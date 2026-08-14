using System.Net;
using System.Net.Http.Json;

namespace Concordat.Api.IntegrationTests;

/// <summary>
/// The session cookie that survives a reload (decision 26).
/// </summary>
/// <remarks>
/// The SPA holds its credential in memory, deliberately: a token in `localStorage` is readable
/// by any script on the page, and ADR-006 already declined one XSS hole. The cost was that F5
/// signed you out, which is irritating enough that somebody would eventually have fixed it the
/// wrong way. An httpOnly cookie is the one browser store script cannot read.
/// </remarks>
[Collection(CloudApiCollection.Name)]
public class SessionCookieTests(CloudApiFactory factory)
{
    private static string UniqueSlug() => $"s{Guid.CreateVersion7():N}"[..16];

    private const string Password = "correct horse battery";

    /// <summary>Signs up and signs in, returning the client that holds the cookie.</summary>
    private async Task<(HttpClient Http, string Email)> SignedInAsync()
    {
        var slug = UniqueSlug();
        var email = $"owner@{slug}.example.com";

        // A handler that keeps cookies, which is what a browser is.
        var http = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
        });

        var created = await http.PostAsJsonAsync(
            "/v1/auth/signup", new SignUpRequest(slug, email, Password, slug), ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var signIn = await http.PostAsJsonAsync(
            "/v1/auth/signin", new SignInRequest(email, Password), ApiFactory.Json);

        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);

        return (http, email);
    }

    [Fact]
    public async Task SignInSetsAnHttpOnlyCookieScriptCannotRead()
    {
        var slug = UniqueSlug();
        var email = $"owner@{slug}.example.com";
        var http = factory.CreateClient();

        await http.PostAsJsonAsync(
            "/v1/auth/signup", new SignUpRequest(slug, email, Password, slug), ApiFactory.Json);

        var signIn = await http.PostAsJsonAsync(
            "/v1/auth/signin", new SignInRequest(email, Password), ApiFactory.Json);

        var cookie = Assert.Single(
            signIn.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(SessionCookie.Name, StringComparison.Ordinal));

        // httpOnly is the whole point: it is the one browser store a script on the page cannot
        // read, which is what makes this different from the localStorage answer ADR-006 refused.
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResumeReturnsTheCredentialAfterAReload()
    {
        var (http, email) = await SignedInAsync();

        // A reload: no Authorization header, nothing in memory, only what the browser kept.
        var resumed = await http.PostAsync("/v1/auth/resume", null);
        var session = await ApiFactory.ReadAsync<SignInResponse>(resumed);

        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
        Assert.Equal(email, session.Actor);
        Assert.NotEmpty(session.Credential);
        Assert.Contains("subject:read", session.Scopes, StringComparer.Ordinal);
    }

    [Fact]
    public async Task ResumeWithoutACookieIsUnauthorisedRatherThanAnonymous()
    {
        // In Cloud there is no unclaimed-instance owner to fall back to, and falling back at all
        // would turn "your session ended" into "you are somebody else".
        var http = factory.CreateClient();

        var resumed = await http.PostAsync("/v1/auth/resume", null);

        Assert.Equal(HttpStatusCode.Unauthorized, resumed.StatusCode);
    }

    [Fact]
    public async Task SignOutClearsTheCookieSoTheNextResumeFails()
    {
        var (http, _) = await SignedInAsync();

        var signedOut = await http.PostAsync("/v1/auth/signout", null);
        Assert.Equal(HttpStatusCode.NoContent, signedOut.StatusCode);

        // Script cannot delete an httpOnly cookie, which is why signing out needs the server at
        // all -- the property that makes the cookie safe is the one that makes this a round trip.
        var resumed = await http.PostAsync("/v1/auth/resume", null);

        Assert.Equal(HttpStatusCode.Unauthorized, resumed.StatusCode);
    }

    [Fact]
    public async Task TheCookieIsNotAcceptedAsACredentialOnAnyOtherRoute()
    {
        // THE PROPERTY THAT MAKES CSRF STRUCTURALLY IMPOSSIBLE RATHER THAN MITIGATED.
        //
        // A cookie that authenticated ordinary requests would let any page on the internet
        // trigger a state change in the browser's name. This one's entire power is handing back
        // a credential the browser already holds; every mutating route still needs an
        // Authorization header, which a cross-site request cannot set.
        var (http, _) = await SignedInAsync();

        var write = await http.PostAsJsonAsync(
            "/v1/environments", new CreateEnvironmentRequest("cookie-only"), ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Unauthorized, write.StatusCode);
    }
}
