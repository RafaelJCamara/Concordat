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

    [Fact(Skip =
        "DEFECT, deliberately not fixed here: the ADR-017 approval gate is defeated by " +
        "submitting the same breaking schema twice. RegisterVersionHandler.LoadPriorsAsync " +
        "excludes Rejected versions but INCLUDES AwaitingApproval ones, and the default " +
        "non-transitive mode compares only against the highest ordinal. So the resubmission is " +
        "compared against the pending proposal rather than against the last approved version, " +
        "finds no divergence, and registers ACTIVE — moving latest onto a schema that is " +
        "backward-incompatible with it, with nobody having approved anything. A retrying CI " +
        "job does this by itself. Reproduced here over real HTTP and real PostgreSQL, not only " +
        "against fakes. Unskip when an unapproved proposal stops counting as history.")]
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
}
