using System.Net;
using System.Net.Http.Json;
using Concordat.Application.Registry;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Concordat.Api.IntegrationTests;

/// <summary>
/// The registration policy: its routes, and the refusals it is supposed to produce (M7.1).
/// </summary>
/// <remarks>
/// <para>
/// The policy was stored from M7.1, defaulted to <c>CI_ONLY</c> for anything named like
/// production, and described in three places as "enforced server-side, which is the whole
/// point" — while no handler read it. These tests exist mostly to make sure that cannot quietly
/// become true again: most of them fail if the check is removed, not merely if the routes are.
/// </para>
/// <para>
/// A test client on an unclaimed instance holds the owner scopes, and <b>no role grants
/// <c>ci</c></b> — it belongs on an API key issued to a pipeline, not on a human's role. So the
/// default client here is deliberately a producer, not CI.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public class RegistrationPolicyTests(ApiFactory factory)
{
    private const string Schema = """{"type":"object","properties":{"id":{"type":"string"}}}""";

    private static string UniqueEnvironment() => $"env-{Guid.CreateVersion7():N}"[..24];

    private static string UniqueSubject() => $"acme.test.S{Guid.CreateVersion7():N}"[..28];

    private async Task<(HttpClient Http, string Environment)> NewEnvironmentAsync(
        string? name = null, string? policy = null)
    {
        var http = factory.CreateClient();
        var environment = name ?? UniqueEnvironment();

        var created = await http.PostAsJsonAsync(
            "/v1/environments",
            new CreateEnvironmentRequest(environment, null, null, null, policy),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (http, environment);
    }

    private static Task<HttpResponseMessage> CreateSubjectAsync(
        HttpClient http, string environment, string subject) =>
        http.PostAsJsonAsync(
            $"/v1/environments/{environment}/subjects",
            new CreateSubjectRequest(subject, "json", "alice"),
            ApiFactory.Json);

    // ------------------------------------------------------------------ the routes

    [Fact]
    public async Task TheRouteReportsThePolicyAndWhatItRequires()
    {
        var (http, environment) = await NewEnvironmentAsync();

        var response = await http.GetAsync($"/v1/environments/{environment}/registration-policy");
        var policy = await ApiFactory.ReadAsync<RegistrationPolicyResponse>(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(environment, policy.Environment);
        Assert.Equal("OPEN", policy.Policy);

        // The required scopes are on the response so a caller debugging a refusal can compare
        // them against their own key, rather than inferring which of three policies is in force.
        Assert.Equal(["subject:write"], policy.RequiredScopes);
    }

    [Fact]
    public async Task PuttingAPolicyChangesItAndReportsTheNewRequirement()
    {
        var (http, environment) = await NewEnvironmentAsync();

        var response = await http.PutAsJsonAsync(
            $"/v1/environments/{environment}/registration-policy",
            new SetRegistrationPolicyRequest("CI_ONLY"),
            ApiFactory.Json);

        var policy = await ApiFactory.ReadAsync<RegistrationPolicyResponse>(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("CI_ONLY", policy.Policy);
        Assert.Equal(["subject:write", "ci"], policy.RequiredScopes);
    }

    [Fact]
    public async Task ClosedNamesNoRequiredScopeBecauseNoCredentialAdmitsYou()
    {
        var (http, environment) = await NewEnvironmentAsync();

        var response = await http.PutAsJsonAsync(
            $"/v1/environments/{environment}/registration-policy",
            new SetRegistrationPolicyRequest("CLOSED"),
            ApiFactory.Json);

        var policy = await ApiFactory.ReadAsync<RegistrationPolicyResponse>(response);

        // Naming a scope here would send somebody to go and request one that would not help.
        Assert.Empty(policy.RequiredScopes);
    }

    [Fact]
    public async Task AnUnknownPolicyIsRefusedRatherThanIgnored()
    {
        var (http, environment) = await NewEnvironmentAsync();

        var response = await http.PutAsJsonAsync(
            $"/v1/environments/{environment}/registration-policy",
            new SetRegistrationPolicyRequest("SOMETIMES"),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AnOmittedPolicyIsRefusedRatherThanSilentlyDoingNothing()
    {
        // PATCH means "change what I named", so an absent field there means leave it alone. PUT
        // on a single value means "make it this" — answering 200 to a request that changed
        // nothing would tell an operator mid-incident they had closed an environment they had not.
        var (http, environment) = await NewEnvironmentAsync();

        var response = await http.PutAsJsonAsync(
            $"/v1/environments/{environment}/registration-policy",
            new SetRegistrationPolicyRequest(null!),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TheRouteIsScopedToEnvWrite()
    {
        // Structural: EveryMutatingRouteDeclaresAScope covers the existence of a requirement,
        // this pins which one. Read is deliberately looser — a producer author debugging a
        // refusal has to be able to read the policy that refused them.
        var mutating = factory.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Single(e =>
                e.RoutePattern.RawText!.EndsWith("registration-policy", StringComparison.Ordinal) &&
                e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("PUT"));

        Assert.Contains(
            "env:write",
            mutating.Metadata.GetMetadata<RequiredScopes>()!.Scopes,
            StringComparer.Ordinal);
    }

    // ------------------------------------------------------------- the enforcement

    [Fact]
    public async Task AProductionNameDefaultsToCiOnlyAndActuallyRefuses()
    {
        // The default has existed since M7.1 and did nothing at all, because no handler read the
        // field. This is the test that would have failed then.
        var (http, environment) = await NewEnvironmentAsync(name: "prod");

        var refused = await CreateSubjectAsync(http, environment, UniqueSubject());
        var problem = await ApiFactory.ReadProblemAsync(refused);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("registration_policy_forbids", problem.ConcordatCode);
        Assert.Contains("'ci'", problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AClosedEnvironmentRefusesSubjectCreation()
    {
        var (http, environment) = await NewEnvironmentAsync(policy: "CLOSED");

        var refused = await CreateSubjectAsync(http, environment, UniqueSubject());
        var problem = await ApiFactory.ReadProblemAsync(refused);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("registration_policy_forbids", problem.ConcordatCode);
        Assert.Contains("promotion", problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClosingAnEnvironmentStopsRegistrationIntoAnExistingSubject()
    {
        // Closed after the subject exists, which is the realistic order: an environment is
        // opened, populated, then locked down. Existing subjects must stop accepting versions.
        var (http, environment) = await NewEnvironmentAsync();
        var subject = UniqueSubject();

        Assert.Equal(
            HttpStatusCode.Created,
            (await CreateSubjectAsync(http, environment, subject)).StatusCode);

        var closed = await http.PutAsJsonAsync(
            $"/v1/environments/{environment}/registration-policy",
            new SetRegistrationPolicyRequest("CLOSED"),
            ApiFactory.Json);
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);

        var refused = await http.PostAsJsonAsync(
            $"/v1/environments/{environment}/subjects/{subject}/versions",
            new RegisterVersionRequest(Schema, RegisteredBy: "alice"),
            ApiFactory.Json);

        var problem = await ApiFactory.ReadProblemAsync(refused);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("registration_policy_forbids", problem.ConcordatCode);
    }

    [Fact]
    public async Task ReopeningAnEnvironmentLetsRegistrationResume()
    {
        var (http, environment) = await NewEnvironmentAsync(policy: "CLOSED");

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await CreateSubjectAsync(http, environment, UniqueSubject())).StatusCode);

        var reopened = await http.PutAsJsonAsync(
            $"/v1/environments/{environment}/registration-policy",
            new SetRegistrationPolicyRequest("OPEN"),
            ApiFactory.Json);
        Assert.Equal(HttpStatusCode.OK, reopened.StatusCode);

        Assert.Equal(
            HttpStatusCode.Created,
            (await CreateSubjectAsync(http, environment, UniqueSubject())).StatusCode);
    }

    [Fact]
    public async Task AnEnvironmentThatWasNeverCreatedHasNoPolicyAndAccepts()
    {
        // Routes take an environment name before any Environment row exists — the id is derived
        // from the name. Refusing there would refuse every registration on a registry nobody had
        // explicitly configured, which is every quickstart.
        var http = factory.CreateClient();

        var created = await CreateSubjectAsync(http, UniqueEnvironment(), UniqueSubject());

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }
}
