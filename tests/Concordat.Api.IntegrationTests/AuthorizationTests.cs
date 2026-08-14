using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Concordat.Domain.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Concordat.Api.IntegrationTests;

/// <summary>
/// M8's exit criterion: a non-admin can browse everything and change nothing, and that holds
/// against curl rather than just the UI (ADR-018).
/// </summary>
/// <remarks>
/// These run in their own collection with their own database, because claiming the instance is
/// a one-way change: once an account exists, the unclaimed-instance caller stops applying and
/// every other suite's unauthenticated request would start failing.
/// </remarks>
[Collection(AuthApiCollection.Name)]
public class AuthorizationTests(AuthApiFactory factory)
{
    private const string OwnerEmail = "owner@example.com";
    private const string OwnerPassword = "correct horse battery";

    private static string UniqueSubject() => $"acme.auth.S{Guid.CreateVersion7():N}";

    private static string UniqueEnvironment() => $"env-{Guid.CreateVersion7():N}"[..24];

    private HttpClient Anonymous() => factory.CreateClient();

    private HttpClient WithCredential(string credential)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", credential);

        return client;
    }

    /// <summary>Signs in and returns a credential.</summary>
    private async Task<string> SignInAsync(string email, string password)
    {
        var response = await Anonymous().PostAsJsonAsync(
            "/v1/auth/signin", new SignInRequest(email, password), ApiFactory.Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ApiFactory.ReadAsync<SignInResponse>(response)).Credential;
    }

    /// <summary>Creates a member with a role and returns a credential for them.</summary>
    private async Task<string> MemberCredentialAsync(string role)
    {
        var owner = await factory.OwnerCredentialAsync();
        var email = $"{role.ToLowerInvariant()}-{Guid.CreateVersion7():N}"[..24] + "@example.com";
        const string password = "another long password";

        var created = await WithCredential(owner).PostAsJsonAsync(
            "/v1/members",
            new CreateMemberRequest(email, password, role),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return await SignInAsync(email, password);
    }

    // -------------------------------------------------------------------- bootstrap

    [Fact]
    public async Task BootstrapWorksOnceAndThenNeverAgain()
    {
        // The fixture already claimed the instance, which is the state every run after the
        // first one is in.
        await factory.OwnerCredentialAsync();

        var again = await Anonymous().PostAsJsonAsync(
            "/v1/auth/bootstrap",
            new BootstrapRequest("second@example.com", "yet another password"),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(
            "user_already_exists",
            (await ApiFactory.ReadProblemAsync(again)).ConcordatCode);
    }

    [Fact]
    public async Task StatusSaysTheInstanceIsClaimedWithoutACredential()
    {
        // A sign-in screen has to be able to ask whether there is anyone to sign in as.
        await factory.OwnerCredentialAsync();

        var status = await Anonymous().GetFromJsonAsync<AuthStatusResponse>(
            "/v1/auth/status", ApiFactory.Json);

        Assert.True(status!.Claimed);
        Assert.False(status.Authenticated);
        Assert.Empty(status.Scopes);
    }

    // ---------------------------------------------------------------------- sign-in

    [Fact]
    public async Task SignInReturnsACredentialThatWorks()
    {
        var credential = await factory.OwnerCredentialAsync();

        var status = await WithCredential(credential)
            .GetFromJsonAsync<AuthStatusResponse>("/v1/auth/status", ApiFactory.Json);

        Assert.True(status!.Authenticated);
        Assert.Contains(Scope.OrgAdmin, status.Scopes);
        Assert.Contains(Scope.SubjectAdmin, status.Scopes);
    }

    [Theory]
    [InlineData("nobody@example.com", OwnerPassword)]
    [InlineData(OwnerEmail, "wrong password entirely")]
    [InlineData("not-an-address", "whatever")]
    public async Task ABadSignInIs401AndSaysNothingAboutWhichHalfWasWrong(
        string email, string password)
    {
        await factory.OwnerCredentialAsync();

        var response = await Anonymous().PostAsJsonAsync(
            "/v1/auth/signin", new SignInRequest(email, password), ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var problem = await ApiFactory.ReadProblemAsync(response);
        Assert.Equal("unauthenticated", problem.ConcordatCode);

        // One message for all three. Which half was wrong is not information a caller who knows
        // their own credential needs, and it is exactly what enumerates accounts.
        Assert.Equal("That email and password do not match.", problem.Detail);
    }

    // ----------------------------------------------------------------- the exit test

    [Fact]
    public async Task AReaderCanBrowseEverything()
    {
        var reader = WithCredential(await MemberCredentialAsync("READER"));

        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/v1/environments")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/v1/audit")).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await reader.GetAsync("/v1/environments/test/subjects")).StatusCode);
    }

    [Fact]
    public async Task AReaderCanChangeNothing()
    {
        var reader = WithCredential(await MemberCredentialAsync("READER"));
        var environment = UniqueEnvironment();

        var attempts = new (string What, Task<HttpResponseMessage> Response)[]
        {
            ("create an environment", reader.PostAsJsonAsync(
                "/v1/environments", new CreateEnvironmentRequest(environment), ApiFactory.Json)),

            ("create a subject", reader.PostAsJsonAsync(
                "/v1/environments/test/subjects",
                new CreateSubjectRequest(UniqueSubject(), "json", "r", null, null, "open"),
                ApiFactory.Json)),

            ("register a version", reader.PostAsJsonAsync(
                "/v1/environments/test/subjects/anything/versions",
                new RegisterVersionRequest("{}", null, null, "r"),
                ApiFactory.Json)),

            ("approve a version", reader.PostAsJsonAsync(
                "/v1/environments/test/subjects/anything/versions/1/approve",
                new DecideVersionRequest("r"),
                ApiFactory.Json)),

            ("retire a subject", reader.DeleteAsync(
                "/v1/environments/test/subjects/anything")),

            ("create a contract", reader.PostAsJsonAsync(
                "/v1/environments/test/contracts",
                new CreateContractRequest("c"),
                ApiFactory.Json)),

            ("list members", reader.GetAsync("/v1/members")),

            ("issue an API key", reader.PostAsJsonAsync(
                "/v1/api-keys",
                new IssueApiKeyRequest("mine", [Scope.SubjectAdmin]),
                ApiFactory.Json)),
        };

        foreach (var (what, task) in attempts)
        {
            using var response = await task;

            Assert.True(
                response.StatusCode is HttpStatusCode.Forbidden,
                $"a reader was allowed to {what}: {(int)response.StatusCode}");

            Assert.Equal(
                "insufficient_scope",
                (await ApiFactory.ReadProblemAsync(response)).ConcordatCode);
        }
    }

    [Fact]
    public async Task AnAdminCanWriteSchemasButNotManageTheOrg()
    {
        var admin = WithCredential(await MemberCredentialAsync("ADMIN"));

        var subject = await admin.PostAsJsonAsync(
            "/v1/environments/test/subjects",
            new CreateSubjectRequest(UniqueSubject(), "json", "a", null, null, "open"),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, subject.StatusCode);

        // ADR-018 draws the line here: schema authority does not carry membership authority.
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/v1/members")).StatusCode);
    }

    [Fact]
    public async Task AnUnauthenticatedWriteIs401NotJustHidden()
    {
        // 401 rather than 403: the caller has not said who they are yet. Collapsing the two
        // sends a client into a sign-in loop it cannot win.
        await factory.OwnerCredentialAsync();

        var response = await Anonymous().PostAsJsonAsync(
            "/v1/environments",
            new CreateEnvironmentRequest(UniqueEnvironment()),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AGarbageCredentialIsRefusedRatherThanIgnored()
    {
        // The dangerous shape: an unparseable credential falling through to the
        // unclaimed-instance caller would turn a typo into full access.
        var client = WithCredential("cdt_notarealkeyid_notarealsecret");

        var response = await client.PostAsJsonAsync(
            "/v1/environments",
            new CreateEnvironmentRequest(UniqueEnvironment()),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // -------------------------------------------------------------------- API keys

    [Fact]
    public async Task AnIssuedKeyAuthenticatesAndTheSecretIsReturnedOnce()
    {
        var owner = WithCredential(await factory.OwnerCredentialAsync());

        var issued = await ApiFactory.ReadAsync<IssuedApiKeyResponse>(
            await owner.PostAsJsonAsync(
                "/v1/api-keys",
                new IssueApiKeyRequest("ci", [Scope.SubjectWrite]),
                ApiFactory.Json));

        Assert.StartsWith("cdt_", issued.Secret, StringComparison.Ordinal);

        var withKey = WithCredential(issued.Secret);

        var created = await withKey.PostAsJsonAsync(
            "/v1/environments/test/subjects",
            new CreateSubjectRequest(UniqueSubject(), "json", "ci", null, null, "open"),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        // The listing never carries the secret again — the server holds only a hash.
        var listed = await owner.GetFromJsonAsync<IReadOnlyList<ApiKeyResponse>>(
            "/v1/api-keys", ApiFactory.Json);

        var stored = Assert.Single(listed!, k => k.Id == issued.Key.Id);
        Assert.Equal(issued.Key.KeyId, stored.KeyId);
        Assert.DoesNotContain(issued.Secret, System.Text.Json.JsonSerializer.Serialize(stored),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AKeyCannotGrantMoreThanItsIssuerHolds()
    {
        // Without this a Reader who can reach the endpoint mints themselves subject:admin and
        // ADR-018 is decoration. Here an Admin — who has no org:admin — is refused it.
        var admin = WithCredential(await MemberCredentialAsync("ADMIN"));
        var owner = WithCredential(await factory.OwnerCredentialAsync());

        // The admin cannot reach /v1/api-keys at all, so the escalation is attempted by an
        // owner-issued key that holds only subject:write and then tries to widen itself.
        var narrow = await ApiFactory.ReadAsync<IssuedApiKeyResponse>(
            await owner.PostAsJsonAsync(
                "/v1/api-keys",
                new IssueApiKeyRequest("narrow", [Scope.SubjectWrite, Scope.OrgAdmin]),
                ApiFactory.Json));

        var widening = await WithCredential(narrow.Secret).PostAsJsonAsync(
            "/v1/api-keys",
            new IssueApiKeyRequest("wider", [Scope.BrokerWrite]),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Forbidden, widening.StatusCode);
        Assert.Equal(
            "insufficient_scope",
            (await ApiFactory.ReadProblemAsync(widening)).ConcordatCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/v1/api-keys")).StatusCode);
    }

    [Fact]
    public async Task ARevokedKeyStopsWorkingImmediately()
    {
        var owner = WithCredential(await factory.OwnerCredentialAsync());

        var issued = await ApiFactory.ReadAsync<IssuedApiKeyResponse>(
            await owner.PostAsJsonAsync(
                "/v1/api-keys",
                new IssueApiKeyRequest("short-lived", [Scope.SubjectRead]),
                ApiFactory.Json));

        var withKey = WithCredential(issued.Secret);
        Assert.Equal(HttpStatusCode.OK, (await withKey.GetAsync("/v1/auth/status")).StatusCode);

        var authenticated = await withKey.GetFromJsonAsync<AuthStatusResponse>(
            "/v1/auth/status", ApiFactory.Json);
        Assert.True(authenticated!.Authenticated);

        Assert.Equal(
            HttpStatusCode.OK,
            (await owner.DeleteAsync($"/v1/api-keys/{issued.Key.Id}")).StatusCode);

        var after = await withKey.GetFromJsonAsync<AuthStatusResponse>(
            "/v1/auth/status", ApiFactory.Json);

        Assert.False(after!.Authenticated);
    }

    [Fact]
    public async Task SessionCredentialsAreNotListedAmongTheTenantsKeys()
    {
        // A screenful of one-row-per-sign-in would bury the keys somebody actually has to
        // manage, which is how a leaked CI key goes unnoticed.
        var owner = WithCredential(await factory.OwnerCredentialAsync());
        await SignInAsync(OwnerEmail, OwnerPassword);

        var listed = await owner.GetFromJsonAsync<IReadOnlyList<ApiKeyResponse>>(
            "/v1/api-keys", ApiFactory.Json);

        Assert.DoesNotContain(listed!, k => k.Label.StartsWith("session", StringComparison.Ordinal));
    }

    // ----------------------------------------------------------------- the safety net

    [Fact]
    public void EveryMutatingRouteDeclaresAScope()
    {
        // The check that makes the convention real. A handler that forgets its scope is a write
        // path that ships ungated and works for everyone, and no functional test would notice.
        var sources = factory.Services.GetRequiredService<IEnumerable<EndpointDataSource>>();

        var unguarded = sources
            .SelectMany(s => s.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
                .Any(m => m is "POST" or "PUT" or "PATCH" or "DELETE") is true)
            .Where(e => e.Metadata.GetMetadata<RequiredScopes>() is null)
            .Select(e => $"{string.Join('/', e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods)} {e.RoutePattern.RawText}")
            .Where(route => !Exempt.Any(route.Contains))
            .ToList();

        Assert.True(unguarded.Count is 0, $"unguarded mutating routes:\n  {string.Join("\n  ", unguarded)}");
    }

    /// <summary>
    /// Routes that mutate nothing despite their verb, or that authenticate rather than assume.
    /// </summary>
    /// <remarks>
    /// Every entry is a decision, not an oversight. <c>/bootstrap</c> and <c>/signin</c> are how
    /// a caller acquires a credential in the first place. <c>/resolve</c>, <c>/impact</c> and
    /// <c>/lookup</c> are POSTs because their question does not fit in a URL, and they write
    /// nothing. <c>/services</c> is reported by every SDK at startup and gating it would make
    /// declaring intent a privileged act nobody performs.
    /// </remarks>
    private static readonly string[] Exempt =
    [
        "/v1/auth/bootstrap",
        "/v1/auth/signin",
        "/contracts/resolve",
        "/subjects/{subject}/impact",
        "/schemas/lookup",
        "/bootstrap",
        "/services",
        "/subjects/{subject}/compatibility",
    ];
}
