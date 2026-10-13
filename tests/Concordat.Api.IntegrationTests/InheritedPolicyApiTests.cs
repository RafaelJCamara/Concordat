using System.Net;
using System.Net.Http.Json;
using Concordat.Application.Registry;

namespace Concordat.Api.IntegrationTests;

/// <summary>
/// A subject with no policy of its own is judged by its environment's default, at every endpoint
/// that judges it (ADR-033).
/// </summary>
/// <remarks>
/// <para>
/// <b>Registration, the dry-run check and the diff read the built-in default instead</b>, for as
/// long as an environment's default was the built-in one, which is what a new environment is
/// created with and so what nobody ever changed: an environment set to <c>NONE</c> held the
/// breaking version of an inheriting subject, and the dry run CI calls first agreed. Impact
/// analysis and promotion read the environment all along, so one subject had two answers to "what
/// is my policy". Every test here names an environment whose default is <em>not</em> the built-in,
/// because in one whose default is the built-in the two readings are the same value.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public class InheritedPolicyApiTests(ApiFactory factory)
{
    private const string V1 = """{"type":"object","properties":{"id":{"type":"string"}}}""";

    /// <summary>Breaking to register (BACKWARD) and to consume (FORWARD): `id` changes type.</summary>
    private const string V2Breaking = """{"type":"object","properties":{"id":{"type":"integer"}}}""";

    private const string V3Breaking = """{"type":"object","properties":{"id":{"type":"boolean"}}}""";

    private static string Unique() => $"{Guid.CreateVersion7():N}"[..16];

    private async Task<string> NewEnvironmentAsync(string mode, string surface)
    {
        var name = $"env-{Unique()}";

        var created = await factory.CreateClient().PostAsJsonAsync(
            "/v1/environments",
            new CreateEnvironmentRequest(
                name, CompatibilityMode: mode, CompatibilitySurface: surface),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return name;
    }

    private async Task<string> NewSubjectAsync(
        string environment, string? mode = null, string? surface = null)
    {
        var name = $"acme.inherit.S{Unique()}";

        var created = await factory.CreateClient().PostAsJsonAsync(
            $"/v1/environments/{environment}/subjects",
            new CreateSubjectRequest(name, "json", "alice", mode, surface),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return name;
    }

    private async Task<string> RegisterAsync(string environment, string subject, string body)
    {
        var response = await factory.CreateClient().PostAsJsonAsync(
            $"/v1/environments/{environment}/subjects/{subject}/versions",
            new RegisterVersionRequest(body),
            ApiFactory.Json);

        Assert.True(
            response.IsSuccessStatusCode, $"registering answered {(int)response.StatusCode}");
        return (await ApiFactory.ReadAsync<RegisterVersionResponse>(response)).Status;
    }

    private async Task<CompatibilityResponse> CheckAsync(
        string environment, string subject, string body) =>
        await ApiFactory.ReadAsync<CompatibilityResponse>(
            await factory.CreateClient().PostAsJsonAsync(
                $"/v1/environments/{environment}/subjects/{subject}/compatibility",
                new CheckCompatibilityRequest(body),
                ApiFactory.Json));

    private async Task<DiffResponse> DiffAsync(string environment, string subject, int from, int to) =>
        await ApiFactory.ReadAsync<DiffResponse>(
            await factory.CreateClient().GetAsync(
                $"/v1/environments/{environment}/subjects/{subject}/versions/{from}/diff/{to}"));

    private async Task SetEnvironmentDefaultAsync(string environment, string mode, string surface)
    {
        var response = await factory.CreateClient().PatchAsJsonAsync(
            $"/v1/environments/{environment}",
            new UpdateEnvironmentRequest(CompatibilityMode: mode, CompatibilitySurface: surface),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RegistrationJudgesAnInheritingSubjectByItsEnvironmentsDefault()
    {
        var environment = await NewEnvironmentAsync("NONE", "WIRE_JSON");
        var subject = await NewSubjectAsync(environment);

        Assert.Equal("ACTIVE", await RegisterAsync(environment, subject, V1));

        // Held before the fix: the built-in BACKWARD x WIRE_JSON was applied to a subject whose
        // environment says nothing is checked.
        Assert.Equal("ACTIVE", await RegisterAsync(environment, subject, V2Breaking));

        var read = await ApiFactory.ReadAsync<SubjectResponse>(
            await factory.CreateClient().GetAsync(
                $"/v1/environments/{environment}/subjects/{subject}"));

        Assert.Equal(2, read.Latest);
    }

    [Fact]
    public async Task TheDryRunAgreesWithTheRegistrationItRehearses()
    {
        var environment = await NewEnvironmentAsync("NONE", "WIRE_JSON");
        var subject = await NewSubjectAsync(environment);
        await RegisterAsync(environment, subject, V1);

        var check = await CheckAsync(environment, subject, V2Breaking);

        Assert.True(check.Compatible);
        Assert.Equal("NONE", check.Policy.Mode);
        Assert.Equal("WIRE_JSON", check.Policy.Surface);

        // And what it said is what registration then does.
        Assert.Equal("ACTIVE", await RegisterAsync(environment, subject, V2Breaking));
    }

    [Fact]
    public async Task TheDiffNamesTheEnvironmentsDefaultForAnInheritingSubject()
    {
        var environment = await NewEnvironmentAsync("NONE", "WIRE_JSON");
        var subject = await NewSubjectAsync(environment);
        await RegisterAsync(environment, subject, V1);
        await RegisterAsync(environment, subject, V2Breaking);

        var diff = await DiffAsync(environment, subject, 1, 2);

        Assert.Equal("NONE", diff.Policy.Mode);
        Assert.Equal("WIRE_JSON", diff.Policy.Surface);
        Assert.NotEmpty(diff.Divergences);
    }

    [Fact]
    public async Task AStricterEnvironmentDefaultHoldsAVersionTheBuiltInOneWouldAccept()
    {
        // The other direction. integer -> number is a Source-surface finding, which the built-in
        // WIRE_JSON surface tolerates and a SOURCE one does not.
        var environment = await NewEnvironmentAsync("BACKWARD", "SOURCE");
        var subject = await NewSubjectAsync(environment);

        Assert.Equal(
            "ACTIVE",
            await RegisterAsync(
                environment, subject, """{"type":"object","properties":{"n":{"type":"integer"}}}"""));

        Assert.Equal(
            "AWAITING_APPROVAL",
            await RegisterAsync(
                environment, subject, """{"type":"object","properties":{"n":{"type":"number"}}}"""));
    }

    [Fact]
    public async Task APinnedSubjectIgnoresTheEnvironmentAtEveryEndpoint()
    {
        var environment = await NewEnvironmentAsync("NONE", "WIRE_JSON");
        var subject = await NewSubjectAsync(environment, "BACKWARD", "WIRE_JSON");
        await RegisterAsync(environment, subject, V1);

        var check = await CheckAsync(environment, subject, V2Breaking);

        Assert.False(check.Compatible);
        Assert.Equal("BACKWARD", check.Policy.Mode);

        Assert.Equal("AWAITING_APPROVAL", await RegisterAsync(environment, subject, V2Breaking));
        Assert.Equal("BACKWARD", (await DiffAsync(environment, subject, 1, 2)).Policy.Mode);
    }

    [Fact]
    public async Task ChangingTheEnvironmentsDefaultChangesTheNextRegistrationOfAnInheritingSubjectOnly()
    {
        var environment = await NewEnvironmentAsync("BACKWARD", "WIRE_JSON");
        var inheriting = await NewSubjectAsync(environment);
        var pinned = await NewSubjectAsync(environment, "BACKWARD", "WIRE_JSON");

        foreach (var subject in new[] { inheriting, pinned })
        {
            await RegisterAsync(environment, subject, V1);
            Assert.Equal("AWAITING_APPROVAL", await RegisterAsync(environment, subject, V2Breaking));
        }

        await SetEnvironmentDefaultAsync(environment, "NONE", "WIRE_JSON");

        Assert.Equal("ACTIVE", await RegisterAsync(environment, inheriting, V3Breaking));
        Assert.Equal("AWAITING_APPROVAL", await RegisterAsync(environment, pinned, V3Breaking));

        // Not re-judged: version 2 was held under the old default and is held under the new one.
        var read = await ApiFactory.ReadAsync<SubjectResponse>(
            await factory.CreateClient().GetAsync(
                $"/v1/environments/{environment}/subjects/{inheriting}"));

        Assert.Equal("AWAITING_APPROVAL", read.Versions.Single(v => v.Ordinal == 2).Status);
        Assert.Equal("ACTIVE", read.Versions.Single(v => v.Ordinal == 3).Status);
    }

    [Fact]
    public async Task ImpactAnalysisAndPromotionAgreeWithRegistration()
    {
        // The two that already read the environment, held next to the three that now do, so a
        // regression in either direction shows up as a disagreement and not as a reading of code.

        // Impact: the policy's *surface* decides what a consumer pinned to v1 is told. WIRE
        // tolerates the change, the built-in WIRE_JSON would not.
        var wire = await NewEnvironmentAsync("BACKWARD", "WIRE");
        var subject = await NewSubjectAsync(wire);
        await RegisterAsync(wire, subject, V1);

        var service = await factory.CreateClient().PostAsJsonAsync(
            $"/v1/environments/{wire}/services",
            new RegisterServiceRequest("probe", Consumes: [new SubjectRefInput(subject, "1")]),
            ApiFactory.Json);
        Assert.Equal(HttpStatusCode.OK, service.StatusCode);

        var impact = await ApiFactory.ReadAsync<ImpactResponse>(
            await factory.CreateClient().PostAsJsonAsync(
                $"/v1/environments/{wire}/subjects/{subject}/impact",
                new ImpactRequest(V2Breaking),
                ApiFactory.Json));

        Assert.Equal(0, impact.BreakingCount);

        // Promotion: the target's environment decides, as it always did.
        var source = await NewEnvironmentAsync("NONE", "WIRE_JSON");
        var target = await NewEnvironmentAsync("NONE", "WIRE_JSON");
        var promoted = await NewSubjectAsync(source, "NONE", "WIRE_JSON");
        await RegisterAsync(source, promoted, V1);
        await RegisterAsync(source, promoted, V2Breaking);

        var targetSubject = await factory.CreateClient().PostAsJsonAsync(
            $"/v1/environments/{target}/subjects",
            new CreateSubjectRequest(promoted, "json", "alice"),
            ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Created, targetSubject.StatusCode);
        await RegisterAsync(target, promoted, V1);

        var promotion = await ApiFactory.ReadAsync<PromotionResponse>(
            await factory.CreateClient().PostAsJsonAsync(
                $"/v1/environments/{source}/subjects/{promoted}/promote",
                new PromoteRequest(target, 2),
                ApiFactory.Json));

        Assert.Equal("ACTIVE", promotion.Status);
    }
}
