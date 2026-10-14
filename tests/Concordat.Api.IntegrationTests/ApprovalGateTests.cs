using System.Net;
using System.Net.Http.Json;

namespace Concordat.Api.IntegrationTests;

/// <summary>
/// ADR-017's approval gate, over real HTTP against real PostgreSQL.
/// </summary>
[Collection(ApiCollection.Name)]
public class ApprovalGateTests(ApiFactory factory)
{
    private const string Env = "test";

    private const string V1 =
        """{"type":"object","properties":{"id":{"type":"string"}}}""";

    // Adds 'id' to required, which is backward-breaking: documents written under V1 that omit
    // it stop validating.
    private const string Breaking =
        """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}""";

    private static string Unique() => $"acme.gate.S{Guid.CreateVersion7():N}";

    private static async Task<string> NewSubjectAsync(HttpClient client)
    {
        var name = Unique();
        var response = await client.PostAsJsonAsync(
            $"/v1/environments/{Env}/subjects",
            new CreateSubjectRequest(name, "json", "alice"),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return name;
    }

    private static async Task<RegisterVersionResponse> RegisterAsync(
        HttpClient client, string subject, string schema)
    {
        var response = await client.PostAsJsonAsync(
            $"/v1/environments/{Env}/subjects/{subject}/versions",
            new RegisterVersionRequest(schema, null, null, "alice"),
            ApiFactory.Json);

        return await ApiFactory.ReadAsync<RegisterVersionResponse>(response);
    }

    private static async Task<int?> LatestAsync(HttpClient client, string subject)
    {
        var response = await client.GetAsync($"/v1/environments/{Env}/subjects/{subject}");
        var body = await ApiFactory.ReadAsync<SubjectResponse>(response);
        return body.Latest;
    }

    [Fact]
    public async Task ABreakingChangeIsGatedAndDoesNotMoveLatest()
    {
        var client = factory.CreateClient();
        var subject = await NewSubjectAsync(client);

        await RegisterAsync(client, subject, V1);
        var pending = await RegisterAsync(client, subject, Breaking);

        Assert.Equal("AWAITING_APPROVAL", pending.Status);
        Assert.Equal(1, await LatestAsync(client, subject));
    }

    [Fact]
    public async Task ResubmittingAPendingBreakingSchemaMustNotBypassTheGate()
    {
        var client = factory.CreateClient();
        var subject = await NewSubjectAsync(client);

        await RegisterAsync(client, subject, V1);
        await RegisterAsync(client, subject, Breaking);

        // Nobody has approved anything in between.
        var again = await RegisterAsync(client, subject, Breaking);

        Assert.Equal("AWAITING_APPROVAL", again.Status);
        Assert.Equal(1, await LatestAsync(client, subject));
    }

    [Fact]
    public async Task ResubmittingAPendingSchemaIsIdempotentAndAllocatesNoOrdinal()
    {
        var client = factory.CreateClient();
        var subject = await NewSubjectAsync(client);

        await RegisterAsync(client, subject, V1);
        var first = await client.PostAsJsonAsync(
            $"/v1/environments/{Env}/subjects/{subject}/versions",
            new RegisterVersionRequest(Breaking, null, null, "alice"), ApiFactory.Json);
        var proposed = await ApiFactory.ReadAsync<RegisterVersionResponse>(first);

        // A CI job that reruns: the same breaking schema, with nothing decided in between.
        var retry = await client.PostAsJsonAsync(
            $"/v1/environments/{Env}/subjects/{subject}/versions",
            new RegisterVersionRequest(Breaking, null, null, "alice"), ApiFactory.Json);
        var again = await ApiFactory.ReadAsync<RegisterVersionResponse>(retry);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.False(again.Created);
        Assert.Equal(proposed.Ordinal, again.Ordinal);
        Assert.Equal("AWAITING_APPROVAL", again.Status);

        var held = await client.GetFromJsonAsync<SubjectResponse>(
            $"/v1/environments/{Env}/subjects/{subject}", ApiFactory.Json);
        Assert.Equal(2, held!.Versions.Count);
    }

    [Fact]
    public async Task ResubmittingAPendingSchemaWritesNoSecondSubmissionEntry()
    {
        var client = factory.CreateClient();
        var subject = await NewSubjectAsync(client);

        await RegisterAsync(client, subject, V1);
        await RegisterAsync(client, subject, Breaking);
        await RegisterAsync(client, subject, Breaking);
        await RegisterAsync(client, subject, Breaking);

        var entries = await client.GetFromJsonAsync<IReadOnlyList<AuditResponse>>(
            $"/v1/audit?env={Env}&action=VERSION_SUBMITTED&limit=1000", ApiFactory.Json);

        Assert.Single(entries!, e => e.Target == subject);
    }
}
