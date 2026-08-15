using System.Net;
using System.Net.Http.Json;
using Concordat.Application.Registry;

namespace Concordat.Api.IntegrationTests;

/// <summary>
/// Pre-release labels, admitted per environment (decision 8).
/// </summary>
/// <remarks>
/// The label used to be refused by the parser, which meant a team whose pipeline emits `-rc`
/// labels could not label a version at all, anywhere. Whether `2.0.0-rc.1` is acceptable is a
/// property of the environment it is being registered into, not of the string.
/// </remarks>
[Collection(ApiCollection.Name)]
public class PreReleaseVersionTests(ApiFactory factory)
{
    private const string Schema = """{"type":"object","properties":{"id":{"type":"string"}}}""";

    private static string UniqueEnvironment() => $"env-{Guid.CreateVersion7():N}"[..24];

    private static string UniqueSubject() => $"acme.rc.S{Guid.CreateVersion7():N}"[..26];

    private async Task<(HttpClient Http, string Environment, string Subject)> ReadyAsync(
        bool allowPreRelease)
    {
        var http = factory.CreateClient();
        var environment = UniqueEnvironment();
        var subject = UniqueSubject();

        var created = await http.PostAsJsonAsync(
            "/v1/environments",
            new CreateEnvironmentRequest(environment, AllowPreReleaseVersions: allowPreRelease),
            ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var madeSubject = await http.PostAsJsonAsync(
            $"/v1/environments/{environment}/subjects",
            new CreateSubjectRequest(subject, "json", "alice"),
            ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Created, madeSubject.StatusCode);

        return (http, environment, subject);
    }

    private static Task<HttpResponseMessage> RegisterAsync(
        HttpClient http, string environment, string subject, string? semver) =>
        http.PostAsJsonAsync(
            $"/v1/environments/{environment}/subjects/{subject}/versions",
            new RegisterVersionRequest(Schema, semver, RegisteredBy: "alice"),
            ApiFactory.Json);

    [Fact]
    public async Task AnEnvironmentThatAllowsThemAcceptsAnRcLabel()
    {
        var (http, environment, subject) = await ReadyAsync(allowPreRelease: true);

        var registered = await RegisterAsync(http, environment, subject, "2.0.0-rc.1");

        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
    }

    [Fact]
    public async Task AnEnvironmentThatDoesNotRefusesWithAnActionableMessage()
    {
        var (http, environment, subject) = await ReadyAsync(allowPreRelease: false);

        var refused = await RegisterAsync(http, environment, subject, "2.0.0-rc.1");
        var problem = await ApiFactory.ReadProblemAsync(refused);

        Assert.Equal("semver_prerelease_unsupported", problem.ConcordatCode);

        // The message has to name the environment and both ways out, because the caller is a
        // pipeline author who does not know this setting exists.
        Assert.Contains(environment, problem.Detail, StringComparison.Ordinal);
        Assert.Contains("Turn it on", problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDefaultIsOff()
    {
        // A team that wants rc labels turns them on deliberately. The permissive answer being
        // the default would put them in production for everyone who never thought about it.
        var http = factory.CreateClient();
        var environment = UniqueEnvironment();

        var created = await http.PostAsJsonAsync(
            "/v1/environments", new CreateEnvironmentRequest(environment), ApiFactory.Json);

        var body = await ApiFactory.ReadAsync<EnvironmentResponse>(created);

        Assert.False(body.AllowPreReleaseVersions);
    }

    [Fact]
    public async Task ThePolicyCanBeTurnedOnAfterTheFact()
    {
        var (http, environment, subject) = await ReadyAsync(allowPreRelease: false);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await RegisterAsync(http, environment, subject, "2.0.0-rc.1")).StatusCode);

        var updated = await http.PatchAsJsonAsync(
            $"/v1/environments/{environment}",
            new UpdateEnvironmentRequest(AllowPreReleaseVersions: true),
            ApiFactory.Json);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        Assert.Equal(
            HttpStatusCode.Created,
            (await RegisterAsync(http, environment, subject, "2.0.0-rc.1")).StatusCode);
    }

    [Fact]
    public async Task TurningItOffLeavesLabelsAlreadyRegisteredAlone()
    {
        // Versions are immutable and their labels are history. Re-judging the past on a policy
        // change would make the audit trail disagree with the data.
        var (http, environment, subject) = await ReadyAsync(allowPreRelease: true);

        Assert.Equal(
            HttpStatusCode.Created,
            (await RegisterAsync(http, environment, subject, "2.0.0-rc.1")).StatusCode);

        await http.PatchAsJsonAsync(
            $"/v1/environments/{environment}",
            new UpdateEnvironmentRequest(AllowPreReleaseVersions: false),
            ApiFactory.Json);

        var versions = await ApiFactory.ReadAsync<List<VersionResponse>>(
            await http.GetAsync($"/v1/environments/{environment}/subjects/{subject}/versions"));

        Assert.Contains(versions, v => v.SemanticVersion == "2.0.0-rc.1");
    }

    [Fact]
    public async Task BuildMetadataIsRefusedEvenWherePreReleasesAreAllowed()
    {
        // SemVer ignores build metadata for precedence, so two labels carrying different
        // metadata compare equal -- and the registry requires each label to increase.
        var (http, environment, subject) = await ReadyAsync(allowPreRelease: true);

        var refused = await RegisterAsync(http, environment, subject, "2.0.0+build.5");
        var problem = await ApiFactory.ReadProblemAsync(refused);

        Assert.Equal("semver_build_metadata_unsupported", problem.ConcordatCode);
    }
}
