using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Concordat.Api.IntegrationTests;

/// <summary>
/// The M1 exit criterion, exercised over real HTTP against real PostgreSQL.
/// </summary>
[Collection(ApiCollection.Name)]
public class RegistryApiTests(ApiFactory factory)
{
    private const string Env = "test";

    private static string Unique(string stem) =>
        $"acme.{stem}.S{Guid.CreateVersion7():N}";

    private HttpClient Client() => factory.CreateClient();

    private static async Task<string> NewSubjectAsync(
        HttpClient client, string? contentModel = null, string? mode = null, string? surface = null)
    {
        var name = Unique("api");
        var response = await client.PostAsJsonAsync(
            $"/v1/environments/{Env}/subjects",
            new CreateSubjectRequest(name, "json", "alice", mode, surface, contentModel ?? "open"),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return name;
    }

    private static Task<HttpResponseMessage> RegisterAsync(
        HttpClient client, string subject, string schema, string? semver = null) =>
        client.PostAsJsonAsync(
            $"/v1/environments/{Env}/subjects/{subject}/versions",
            new RegisterVersionRequest(schema, semver, null, "alice"),
            ApiFactory.Json);

    [Fact]
    public async Task Health_ReportsLiveAndReady()
    {
        var client = Client();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public async Task RegisterFirstVersion_IsActiveAndMovesLatest()
    {
        var client = Client();
        var subject = await NewSubjectAsync(client);

        var response = await RegisterAsync(
            client, subject, """{"type":"object","properties":{"id":{"type":"string"}}}""", "1.0.0");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ApiFactory.ReadAsync<RegisterVersionResponse>(response);

        Assert.Equal(1, body.Ordinal);
        Assert.Equal("ACTIVE", body.Status);
        Assert.True(body.Created);
        Assert.Equal(32, body.SchemaId.Length);

        var latest = await client.GetAsync(
            $"/v1/environments/{Env}/subjects/{subject}/versions/latest");
        var version = await ApiFactory.ReadAsync<VersionResponse>(latest);
        Assert.Equal(1, version.Ordinal);
    }

    [Fact]
    public async Task AddingAnOptionalProperty_IsAccepted()
    {
        // The acceptance criterion the whole engine exists for, now end to end.
        var client = Client();
        var subject = await NewSubjectAsync(client);

        await RegisterAsync(client, subject, """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}""");

        var response = await RegisterAsync(
            client, subject,
            """{"type":"object","properties":{"id":{"type":"string"},"note":{"type":"string"}},"required":["id"]}""");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ApiFactory.ReadAsync<RegisterVersionResponse>(response);
        Assert.Equal("ACTIVE", body.Status);
        Assert.Equal(2, body.Ordinal);
    }

    [Fact]
    public async Task ABreakingChange_RegistersAwaitingApprovalAndLeavesLatestAlone()
    {
        var client = Client();
        var subject = await NewSubjectAsync(client);

        await RegisterAsync(client, subject, """{"type":"object","properties":{"id":{"type":"string"}}}""");

        var response = await RegisterAsync(
            client, subject,
            """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}""");

        // 201, not 409: CI never wedges, and the proposal is a reviewable artifact (ADR-017).
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ApiFactory.ReadAsync<RegisterVersionResponse>(response);
        Assert.Equal("AWAITING_APPROVAL", body.Status);

        var latest = await client.GetAsync(
            $"/v1/environments/{Env}/subjects/{subject}/versions/latest");
        var version = await ApiFactory.ReadAsync<VersionResponse>(latest);
        Assert.Equal(1, version.Ordinal);
    }

    [Fact]
    public async Task ApprovingAPendingVersion_AdvancesLatest()
    {
        var client = Client();
        var subject = await NewSubjectAsync(client);

        await RegisterAsync(client, subject, """{"type":"object","properties":{"id":{"type":"string"}}}""");
        await RegisterAsync(client, subject, """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}""");

        var approve = await client.PostAsJsonAsync(
            $"/v1/environments/{Env}/subjects/{subject}/versions/2/approve",
            new DecideVersionRequest("bob"),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        var body = await ApiFactory.ReadAsync<SubjectResponse>(approve);
        Assert.Equal(2, body.Latest);
    }

    [Fact]
    public async Task RegisteringTheTipSchemaAgain_Returns200AndAllocatesNoOrdinal()
    {
        // Idempotent at the tip, so a retried publish does not inflate history. 200 rather
        // than 201, because claiming creation would be a lie a retrying client acts on.
        var client = Client();
        var subject = await NewSubjectAsync(client);
        const string schema = """{"type":"object","properties":{"id":{"type":"string"}}}""";

        await RegisterAsync(client, subject, schema);
        var again = await RegisterAsync(client, subject, schema);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var body = await ApiFactory.ReadAsync<RegisterVersionResponse>(again);
        Assert.False(body.Created);
        Assert.Equal(1, body.Ordinal);
    }

    [Fact]
    public async Task ABreakingChangeLabelledMinor_IsRejectedWithAnActionableProblem()
    {
        var client = Client();
        var subject = await NewSubjectAsync(client);

        await RegisterAsync(client, subject, """{"type":"object","properties":{"id":{"type":"string"}}}""", "1.0.0");

        var response = await RegisterAsync(
            client, subject,
            """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}""",
            "1.1.0");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "semver_label_understates_breakage",
            problem.RootElement.GetProperty("concordatCode").GetString());
    }

    [Fact]
    public async Task CompatibilityCheck_IsADryRunWithJsonPointerPaths()
    {
        var client = Client();
        var subject = await NewSubjectAsync(client);
        await RegisterAsync(
            client, subject, """{"type":"object","properties":{"id":{"type":"string"}}}""", "1.0.0");

        var response = await client.PostAsJsonAsync(
            $"/v1/environments/{Env}/subjects/{subject}/compatibility",
            new CheckCompatibilityRequest(
                """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}"""),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ApiFactory.ReadAsync<CompatibilityResponse>(response);

        Assert.False(body.Compatible);
        var change = Assert.Single(body.BreakingChanges);
        Assert.Equal("#/required", change.Path);
        Assert.Equal("required_field_added", change.Kind);
        Assert.Equal("BACKWARD", change.Direction);
        Assert.Equal("WIRE_JSON", change.Surface);

        // Bumped from the tip's label. With no prior label the suggestion would be 1.0.0
        // instead — an unlabelled history reads as pre-1.0, and the first breaking change
        // after that is what 1.0.0 means.
        Assert.Equal("2.0.0", body.SuggestedSemver);

        // A dry run must not have written anything.
        var versions = await client.GetAsync(
            $"/v1/environments/{Env}/subjects/{subject}/versions");
        var list = await ApiFactory.ReadAsync<List<VersionResponse>>(versions);
        Assert.Single(list);
    }

    [Fact]
    public async Task IntegerWidenedToNumber_PassesTheDefaultPolicyAndIsStillReported()
    {
        // The second axis, end to end: source-breaking but wire- and JSON-safe, so it passes
        // Backward x WireJson while remaining visible in AllDivergences.
        var client = Client();
        var subject = await NewSubjectAsync(client);
        await RegisterAsync(client, subject, """{"type":"object","properties":{"n":{"type":"integer"}}}""");

        var response = await client.PostAsJsonAsync(
            $"/v1/environments/{Env}/subjects/{subject}/compatibility",
            new CheckCompatibilityRequest("""{"type":"object","properties":{"n":{"type":"number"}}}"""),
            ApiFactory.Json);

        var body = await ApiFactory.ReadAsync<CompatibilityResponse>(response);

        Assert.True(body.Compatible);
        Assert.Empty(body.BreakingChanges);
        var divergence = Assert.Single(body.AllDivergences);
        Assert.Equal("SOURCE", divergence.Surface);
        Assert.Equal("integer_widened_to_number", divergence.Kind);
    }

    [Fact]
    public async Task RegisteringAgainstAnUnknownSubject_Is404()
    {
        var client = Client();

        var response = await RegisterAsync(client, Unique("ghost"), """{"type":"object"}""");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreatingTheSameSubjectTwice_Is409()
    {
        var client = Client();
        var name = await NewSubjectAsync(client);

        var again = await client.PostAsJsonAsync(
            $"/v1/environments/{Env}/subjects",
            new CreateSubjectRequest(name, "json", "alice"),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task AMalformedSchema_Is400WithAConcordatCode()
    {
        var client = Client();
        var subject = await NewSubjectAsync(client);

        var response = await RegisterAsync(client, subject, "not json at all");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("schema_malformed", problem.RootElement.GetProperty("concordatCode").GetString());
    }

    [Fact]
    public async Task SchemaLookup_ReturnsTheIdAndWhetherItIsKnown()
    {
        var client = Client();
        var subject = await NewSubjectAsync(client);
        const string schema = """{"type":"object","properties":{"looked":{"type":"string"}}}""";

        var before = await client.PostAsJsonAsync(
            "/v1/schemas/lookup", new LookupSchemaRequest("json", schema), ApiFactory.Json);
        var beforeBody = await ApiFactory.ReadAsync<LookupSchemaResponse>(before);
        Assert.False(beforeBody.Known);

        await RegisterAsync(client, subject, schema);

        var after = await client.PostAsJsonAsync(
            "/v1/schemas/lookup", new LookupSchemaRequest("json", schema), ApiFactory.Json);
        var afterBody = await ApiFactory.ReadAsync<LookupSchemaResponse>(after);

        Assert.True(afterBody.Known);
        Assert.Equal(beforeBody.SchemaId, afterBody.SchemaId);
    }

    [Fact]
    public async Task GetSchema_ReturnsTheCanonicalBodyAndUsages()
    {
        var client = Client();
        var subject = await NewSubjectAsync(client);

        // Deliberately un-canonical input: whitespace and key order must be normalised.
        var registered = await RegisterAsync(
            client, subject, "{  \"properties\" : { \"z\":{}, \"a\":{} } , \"type\":\"object\" }");
        var id = (await ApiFactory.ReadAsync<RegisterVersionResponse>(registered)).SchemaId;

        var schema = await client.GetAsync($"/v1/schemas/{id}");
        Assert.Equal(HttpStatusCode.OK, schema.StatusCode);
        var body = await ApiFactory.ReadAsync<SchemaResponse>(schema);

        Assert.Equal("""{"properties":{"a":{},"z":{}},"type":"object"}""", body.Schema);

        var usages = await client.GetAsync($"/v1/schemas/{id}/subjects");
        var list = await ApiFactory.ReadAsync<List<SchemaUsageResponse>>(usages);
        Assert.Contains(list, u => u.Subject == subject && u.Version == 1);
    }

    [Fact]
    public async Task ReferencedSchemasAreBundledIntoOneSelfContainedDocument()
    {
        // The end-to-end shape of the M1.4 deferral: edges are derived from the document at
        // registration, resolved transitively at read time, and inlined on demand.
        var client = Client();

        var address = await NewSubjectAsync(client);
        await RegisterAsync(
            client, address, """{"type":"object","properties":{"city":{"type":"string"}}}""");

        var order = await NewSubjectAsync(client);
        var reference = $"concordat://{Env}/{address}/1";
        var registered = await RegisterAsync(
            client, order,
            """{"type":"object","properties":{"addr":{"$ref":"REF"}}}""".Replace(
                "REF", reference, StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        var id = (await ApiFactory.ReadAsync<RegisterVersionResponse>(registered)).SchemaId;

        // The stored body keeps the reference: bundling must not have leaked into storage,
        // or canonicalisation would depend on registry state.
        var stored = await ApiFactory.ReadAsync<SchemaResponse>(
            await client.GetAsync($"/v1/schemas/{id}"));
        Assert.Contains("concordat://", stored.Schema, StringComparison.Ordinal);
        Assert.Single(stored.References);

        var bundled = await ApiFactory.ReadAsync<BundledSchemaResponse>(
            await client.GetAsync($"/v1/schemas/{id}/bundled"));

        Assert.DoesNotContain("concordat://", bundled.Bundled, StringComparison.Ordinal);
        Assert.Contains($"#/$defs/{address}__1", bundled.Bundled, StringComparison.Ordinal);
        Assert.Contains("\"city\"", bundled.Bundled, StringComparison.Ordinal);
        Assert.Single(bundled.Inlined);
    }

    [Fact]
    public async Task Bootstrap_ReturnsEverySubjectAndEverySchemaItNeeds()
    {
        // One request instead of N. The payload must be self-sufficient, including schemas
        // reachable only by reference, or a cold client still makes the follow-up calls this
        // endpoint exists to avoid.
        var client = Client();

        var address = await NewSubjectAsync(client);
        await RegisterAsync(
            client, address, """{"type":"object","properties":{"city":{"type":"string"}}}""");

        var order = await NewSubjectAsync(client);
        var reference = $"concordat://{Env}/{address}/1";
        await RegisterAsync(
            client, order,
            """{"type":"object","properties":{"addr":{"$ref":"REF"}}}""".Replace(
                "REF", reference, StringComparison.Ordinal));

        var response = await client.PostAsync($"/v1/environments/{Env}/bootstrap", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ApiFactory.ReadAsync<BootstrapResponse>(response);

        var orderEntry = Assert.Single(body.Subjects, s => s.Name == order);
        Assert.Equal(1, orderEntry.LatestOrdinal);
        Assert.NotNull(orderEntry.LatestSchemaId);

        // Both the referring schema and the referenced one, keyed by id.
        Assert.True(body.Schemas.ContainsKey(orderEntry.LatestSchemaId!));
        var addressEntry = Assert.Single(body.Subjects, s => s.Name == address);
        Assert.True(body.Schemas.ContainsKey(addressEntry.LatestSchemaId!));
    }

    [Fact]
    public async Task Bootstrap_ExcludesRetiredSubjects()
    {
        var client = Client();
        var subject = await NewSubjectAsync(client);
        await RegisterAsync(client, subject, """{"type":"object","x":"retiring"}""");

        await client.DeleteAsync($"/v1/environments/{Env}/subjects/{subject}");

        var body = await ApiFactory.ReadAsync<BootstrapResponse>(
            await client.PostAsync($"/v1/environments/{Env}/bootstrap", null));

        Assert.DoesNotContain(body.Subjects, s => s.Name == subject);
    }

    [Fact]
    public async Task Diff_ReportsBothDirectionsRegardlessOfPolicy()
    {
        var client = Client();
        var subject = await NewSubjectAsync(client);

        await RegisterAsync(client, subject, """{"type":"object","properties":{"a":{"type":"string"}}}""");
        await RegisterAsync(
            client, subject,
            """{"type":"object","properties":{"a":{"type":["string","null"]}}}""");

        var response = await client.GetAsync(
            $"/v1/environments/{Env}/subjects/{subject}/versions/1/diff/2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ApiFactory.ReadAsync<DiffResponse>(response);

        Assert.False(body.Identical);
        // Widening is forward-breaking. The subject's policy is Backward, so a
        // policy-filtered view would show nothing - a diff must not hide it.
        Assert.Contains(body.Divergences, d => d.Direction == "FORWARD");
    }

    [Fact]
    public async Task Diff_OfAVersionAgainstItself_IsIdentical()
    {
        var client = Client();
        var subject = await NewSubjectAsync(client);
        await RegisterAsync(client, subject, """{"type":"object","x":"same"}""");

        var body = await ApiFactory.ReadAsync<DiffResponse>(
            await client.GetAsync($"/v1/environments/{Env}/subjects/{subject}/versions/1/diff/1"));

        Assert.True(body.Identical);
        Assert.Empty(body.Divergences);
    }

    [Fact]
    public async Task Retire_IsTerminalAndBlocksFurtherRegistration()
    {
        var client = Client();
        var subject = await NewSubjectAsync(client);
        await RegisterAsync(client, subject, """{"type":"object","x":"gone"}""");

        var retired = await client.DeleteAsync($"/v1/environments/{Env}/subjects/{subject}");
        Assert.Equal(HttpStatusCode.OK, retired.StatusCode);
        Assert.Equal("RETIRED", (await ApiFactory.ReadAsync<SubjectResponse>(retired)).Lifecycle);

        var blocked = await RegisterAsync(client, subject, """{"type":"object","x":"after"}""");
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
    }

    [Fact]
    public async Task Deprecate_IsAdvisoryAndStillAcceptsVersions()
    {
        // Existing producers still need to be able to patch their contract.
        var client = Client();
        var subject = await NewSubjectAsync(client);
        await RegisterAsync(client, subject, """{"type":"object","properties":{"a":{"type":"string"}}}""");

        var patched = await client.PatchAsJsonAsync(
            $"/v1/environments/{Env}/subjects/{subject}",
            new UpdateSubjectRequest(Owner: "bob", Deprecate: true),
            ApiFactory.Json);

        var body = await ApiFactory.ReadAsync<SubjectResponse>(patched);
        Assert.Equal("DEPRECATED", body.Lifecycle);
        Assert.Equal("bob", body.Owner);

        var still = await RegisterAsync(
            client, subject,
            """{"type":"object","properties":{"a":{"type":"string"},"b":{"type":"string"}}}""");
        Assert.Equal(HttpStatusCode.Created, still.StatusCode);
    }

    [Fact]
    public async Task AnUnreachableSchemaId_Is404()
    {
        var client = Client();

        var response = await client.GetAsync($"/v1/schemas/{new string('b', 32)}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ClosedContentModel_ChangesTheVerdictForTheSameTwoDocuments()
    {
        var client = Client();
        var closed = await NewSubjectAsync(client, contentModel: "closed", mode: "FULL", surface: "WIRE_JSON");

        await RegisterAsync(client, closed, """{"type":"object","properties":{"a":{"type":"string"}}}""");

        var response = await client.PostAsJsonAsync(
            $"/v1/environments/{Env}/subjects/{closed}/compatibility",
            new CheckCompatibilityRequest(
                """{"type":"object","properties":{"a":{"type":"string"},"b":{"type":"string"}}}"""),
            ApiFactory.Json);

        var body = await ApiFactory.ReadAsync<CompatibilityResponse>(response);
        Assert.False(body.Compatible);
        Assert.Equal("property_added", Assert.Single(body.BreakingChanges).Kind);
    }

    [Fact]
    public async Task CompatibilityPolicy_RoundTripsAndNullMeansInherit()
    {
        var client = Client();
        var subject = await NewSubjectAsync(client);

        var initial = await ApiFactory.ReadAsync<PolicyResponse>(
            await client.GetAsync($"/v1/environments/{Env}/subjects/{subject}/compatibility-policy"));
        Assert.Null(initial.Mode);

        await client.PutAsJsonAsync(
            $"/v1/environments/{Env}/subjects/{subject}/compatibility-policy",
            new SetCompatibilityPolicyRequest("FULL", "SOURCE"),
            ApiFactory.Json);

        var set = await ApiFactory.ReadAsync<PolicyResponse>(
            await client.GetAsync($"/v1/environments/{Env}/subjects/{subject}/compatibility-policy"));
        Assert.Equal("FULL", set.Mode);
        Assert.Equal("SOURCE", set.Surface);

        await client.PutAsJsonAsync(
            $"/v1/environments/{Env}/subjects/{subject}/compatibility-policy",
            new SetCompatibilityPolicyRequest(null, null),
            ApiFactory.Json);

        var cleared = await ApiFactory.ReadAsync<PolicyResponse>(
            await client.GetAsync($"/v1/environments/{Env}/subjects/{subject}/compatibility-policy"));
        Assert.Null(cleared.Mode);
    }
}

/// <summary>Mirrors the API's schema-usage shape.</summary>
/// <param name="Subject">The subject name.</param>
/// <param name="Version">The version ordinal.</param>
public sealed record SchemaUsageResponse(string Subject, int Version);
