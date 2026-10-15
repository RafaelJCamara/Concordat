using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Concordat.Api.IntegrationTests;

/// <summary>
/// Naming who may decide a subject's held versions, over real HTTP against real PostgreSQL (ADR-043).
/// </summary>
/// <remarks>
/// <para>
/// An unclaimed instance answers every request as <c>unclaimed-instance</c>, which is the one caller
/// these tests have. So "somebody else is the reviewer" is a set naming another actor, and "the caller is
/// the reviewer" is a set naming <c>unclaimed-instance</c>. The rule's arithmetic (case, prefixes,
/// limits) is the domain's and is proved there.
/// </para>
/// <para>Every test makes an environment of its own, so no assertion depends on what else has run.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public class SubjectReviewerApiTests(ApiFactory factory)
{
    private const string Caller = "unclaimed-instance";

    private const string V1 = """{"type":"object","properties":{"id":{"type":"string"}}}""";

    private const string Breaking =
        """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}""";

    private static string UniqueEnvironment() => $"env-{Guid.NewGuid():N}"[..24];

    private static string UniqueSubject() => $"acme.reviewers.S{Guid.NewGuid():N}"[..32];

    private static async Task<(HttpClient Client, string Environment, string Subject)> HeldAsync(
        ApiFactory factory)
    {
        var client = factory.CreateClient();
        var environment = UniqueEnvironment();

        var created = await client.PostAsJsonAsync(
            "/v1/environments", new CreateEnvironmentRequest(environment), ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var subject = UniqueSubject();
        var made = await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/subjects",
            new CreateSubjectRequest(subject, "json", "alice"),
            ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);

        foreach (var schema in new[] { V1, Breaking })
        {
            var registered = await client.PostAsJsonAsync(
                $"/v1/environments/{environment}/subjects/{subject}/versions",
                new RegisterVersionRequest(schema, null, null, "alice"),
                ApiFactory.Json);
            Assert.True(registered.IsSuccessStatusCode, await registered.Content.ReadAsStringAsync());
        }

        return (client, environment, subject);
    }

    private static Task<HttpResponseMessage> SetAsync(
        HttpClient client, string environment, string subject, params string[] reviewers) =>
        client.PutAsJsonAsync(
            $"/v1/environments/{environment}/subjects/{subject}/reviewers",
            new SetSubjectReviewersRequest(reviewers),
            ApiFactory.Json);

    private static Task<HttpResponseMessage> DecideAsync(
        HttpClient client, string environment, string subject, string verb) =>
        client.PostAsJsonAsync(
            $"/v1/environments/{environment}/subjects/{subject}/versions/2/{verb}",
            new DecideVersionRequest("ignored"),
            ApiFactory.Json);

    private static async Task<string> StatusOfVersion2Async(
        HttpClient client, string environment, string subject)
    {
        var read = await client.GetFromJsonAsync<SubjectResponse>(
            $"/v1/environments/{environment}/subjects/{subject}", ApiFactory.Json);

        return read!.Versions.Single(v => v.Ordinal == 2).Status;
    }

    private WebApplicationFactory<Program> AsProduction() =>
        factory.WithWebHostBuilder(b => b
            .UseEnvironment("Production")
            .UseSetting(
                "ConnectionStrings:Concordat",
                "Host=placeholder;Database=never;Username=never;Password=never"));

    // ------------------------------------------------------------------------ the default

    [Fact]
    public async Task ASubjectNamesNoReviewersUntilItIsToldTo()
    {
        var (client, environment, subject) = await HeldAsync(factory);

        var read = await client.GetFromJsonAsync<SubjectResponse>(
            $"/v1/environments/{environment}/subjects/{subject}", ApiFactory.Json);

        Assert.Empty(read!.Reviewers);
    }

    [Fact]
    public async Task TheWireNamesTheMemberReviewers_ForABrowserToRead()
    {
        var (client, environment, subject) = await HeldAsync(factory);
        await SetAsync(client, environment, subject, "alice@example.com");

        // Raw JSON, not the response record: deserialising into the type that produced the body
        // would pass whatever the property name was.
        using var json = JsonDocument.Parse(
            await client.GetStringAsync($"/v1/environments/{environment}/subjects/{subject}"));

        var reviewers = json.RootElement.GetProperty("reviewers").EnumerateArray()
            .Select(r => r.GetString());

        Assert.Equal(["alice@example.com"], reviewers);
    }

    // --------------------------------------------------------------------------- setting

    [Fact]
    public async Task ASetIsReturnedWithTheSubject_AndReadBackFromTheDatabase()
    {
        var (client, environment, subject) = await HeldAsync(factory);

        var response = await SetAsync(client, environment, subject, "Bob@Example.com", "alice@example.com");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var returned = await ApiFactory.ReadAsync<SubjectResponse>(response);
        Assert.Equal(["Bob@Example.com", "alice@example.com"], returned.Reviewers);

        // A fresh read, through a fresh context: what was stored, in the order and the case given.
        var read = await client.GetFromJsonAsync<SubjectResponse>(
            $"/v1/environments/{environment}/subjects/{subject}", ApiFactory.Json);
        Assert.Equal(["Bob@Example.com", "alice@example.com"], read!.Reviewers);
    }

    [Fact]
    public async Task AnActorNameWithACommaAndAQuote_IsStoredAndReadBackWhole()
    {
        // The reason the column is a JSON array and not a comma-joined list: an actor id is any
        // text, and an API key's label is whatever its issuer typed.
        var (client, environment, subject) = await HeldAsync(factory);

        await SetAsync(client, environment, subject, "ci, prod \"blue\"", "b");

        var read = await client.GetFromJsonAsync<SubjectResponse>(
            $"/v1/environments/{environment}/subjects/{subject}", ApiFactory.Json);

        Assert.Equal(["ci, prod \"blue\"", "b"], read!.Reviewers);
    }

    [Fact]
    public async Task AnEmptyListClearsTheSet()
    {
        var (client, environment, subject) = await HeldAsync(factory);
        await SetAsync(client, environment, subject, "alice@example.com");

        var response = await SetAsync(client, environment, subject);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await ApiFactory.ReadAsync<SubjectResponse>(response)).Reviewers);
    }

    [Fact]
    public async Task ABodyThatForgotTheMember_IsRefused_AndDoesNotLoosenTheRule()
    {
        // `{}` would otherwise read as "clear it", and a typo in a field name would remove a control.
        var (client, environment, subject) = await HeldAsync(factory);
        await SetAsync(client, environment, subject, "alice@example.com");

        var response = await client.PutAsync(
            $"/v1/environments/{environment}/subjects/{subject}/reviewers",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var read = await client.GetFromJsonAsync<SubjectResponse>(
            $"/v1/environments/{environment}/subjects/{subject}", ApiFactory.Json);
        Assert.Equal(["alice@example.com"], read!.Reviewers);
    }

    [Fact]
    public async Task ABlankEntry_IsRefusedByPosition()
    {
        var (client, environment, subject) = await HeldAsync(factory);

        var response = await SetAsync(client, environment, subject, "alice@example.com", " ");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ApiFactory.ReadProblemAsync(response);
        Assert.Equal("reviewers_invalid", problem.ConcordatCode);
        Assert.Contains("Reviewer 2", problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MoreThanTwentyReviewers_IsRefused()
    {
        var (client, environment, subject) = await HeldAsync(factory);

        var response = await SetAsync(
            client, environment, subject,
            [.. Enumerable.Range(0, 21).Select(i => $"r{i}@example.com")]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("reviewers_invalid", (await ApiFactory.ReadProblemAsync(response)).ConcordatCode);
    }

    [Fact]
    public async Task AnUnknownSubject_Is404()
    {
        var client = factory.CreateClient();
        var environment = UniqueEnvironment();
        await client.PostAsJsonAsync(
            "/v1/environments", new CreateEnvironmentRequest(environment), ApiFactory.Json);

        var response = await SetAsync(client, environment, UniqueSubject(), "alice@example.com");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ARetiredSubject_Is409()
    {
        var (client, environment, subject) = await HeldAsync(factory);
        var retired = await client.DeleteAsync($"/v1/environments/{environment}/subjects/{subject}");
        Assert.Equal(HttpStatusCode.OK, retired.StatusCode);

        var response = await SetAsync(client, environment, subject, "alice@example.com");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ----------------------------------------------------------------------------- audit

    [Fact]
    public async Task ASetIsRecordedAsTheCallerWithTheOldAndNewSet_AndTheSameSetAgainIsNot()
    {
        var (client, environment, subject) = await HeldAsync(factory);

        await SetAsync(client, environment, subject, "alice@example.com");
        await SetAsync(client, environment, subject, "alice@example.com");
        await SetAsync(client, environment, subject, "alice@example.com", "bob@example.com");

        var entries = await client.GetFromJsonAsync<IReadOnlyList<AuditResponse>>(
            $"/v1/audit?env={environment}&action=SUBJECT_REVIEWERS_CHANGED", ApiFactory.Json);

        // Newest first. Two changes: the repeat wrote nothing.
        Assert.Equal(
            [
                "reviewers alice@example.com -> alice@example.com, bob@example.com",
                "reviewers anyone with the scope -> alice@example.com",
            ],
            entries!.Select(e => e.Detail));
        Assert.All(entries!, e =>
        {
            Assert.Equal(Caller, e.Actor);
            Assert.Equal(subject, e.Target);
        });
    }

    // ----------------------------------------------------------------------------- the gate

    [Fact]
    public async Task NamingSomeoneElse_RefusesTheCaller_ForApprovingAndForRejecting()
    {
        var (client, environment, subject) = await HeldAsync(factory);
        await SetAsync(client, environment, subject, "someone-else@example.com");

        var approve = await DecideAsync(client, environment, subject, "approve");
        var reject = await DecideAsync(client, environment, subject, "reject");

        Assert.Equal(HttpStatusCode.Forbidden, approve.StatusCode);
        Assert.Equal("not_a_reviewer", (await ApiFactory.ReadProblemAsync(approve)).ConcordatCode);
        Assert.Equal(HttpStatusCode.Forbidden, reject.StatusCode);
        Assert.Equal("not_a_reviewer", (await ApiFactory.ReadProblemAsync(reject)).ConcordatCode);

        // The registry, not the response, says nothing happened.
        Assert.Equal("AWAITING_APPROVAL", await StatusOfVersion2Async(client, environment, subject));
    }

    [Fact]
    public async Task NamingTheCaller_LetsThemDecide()
    {
        var (client, environment, subject) = await HeldAsync(factory);
        await SetAsync(client, environment, subject, Caller);

        var approve = await DecideAsync(client, environment, subject, "approve");

        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        Assert.Equal("ACTIVE", await StatusOfVersion2Async(client, environment, subject));
    }

    [Fact]
    public async Task NamingTheCallerInAnotherCase_LetsThemDecide()
    {
        var (client, environment, subject) = await HeldAsync(factory);
        await SetAsync(client, environment, subject, Caller.ToUpperInvariant());

        var reject = await DecideAsync(client, environment, subject, "reject");

        Assert.Equal(HttpStatusCode.OK, reject.StatusCode);
    }

    [Fact]
    public async Task ClearingTheSet_LetsTheCallerDecideAgain()
    {
        var (client, environment, subject) = await HeldAsync(factory);
        await SetAsync(client, environment, subject, "someone-else@example.com");
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await DecideAsync(client, environment, subject, "approve")).StatusCode);

        await SetAsync(client, environment, subject);

        Assert.Equal(
            HttpStatusCode.OK, (await DecideAsync(client, environment, subject, "approve")).StatusCode);
    }

    [Fact]
    public async Task ARefusedDecisionLeavesNothingInTheTrail()
    {
        var (client, environment, subject) = await HeldAsync(factory);
        await SetAsync(client, environment, subject, "someone-else@example.com");

        await DecideAsync(client, environment, subject, "approve");

        var entries = await client.GetFromJsonAsync<IReadOnlyList<AuditResponse>>(
            $"/v1/audit?env={environment}", ApiFactory.Json);

        Assert.DoesNotContain(entries!, e => e.Action is "VERSION_APPROVED" or "VERSION_REJECTED");
    }

    // ----------------------------------------------------------------------------- the queue

    [Fact]
    public async Task TheQueueShowsWhoDecides_AndWhetherTheCallerMay()
    {
        var (client, environment, subject) = await HeldAsync(factory);

        async Task<JsonElement> ItemAsync()
        {
            using var json = JsonDocument.Parse(
                await client.GetStringAsync($"/v1/environments/{environment}/approvals"));

            return json.RootElement.GetProperty("items").EnumerateArray().Single().Clone();
        }

        var open = await ItemAsync();
        Assert.Equal(0, open.GetProperty("reviewers").GetArrayLength());
        Assert.True(open.GetProperty("mayDecide").GetBoolean());

        await SetAsync(client, environment, subject, "someone-else@example.com");
        var elsewhere = await ItemAsync();
        Assert.Equal(
            ["someone-else@example.com"],
            elsewhere.GetProperty("reviewers").EnumerateArray().Select(r => r.GetString()));
        Assert.False(elsewhere.GetProperty("mayDecide").GetBoolean());

        await SetAsync(client, environment, subject, "someone-else@example.com", Caller);
        Assert.True((await ItemAsync()).GetProperty("mayDecide").GetBoolean());
    }

    // ------------------------------------------------------------------------ hosting

    [Fact]
    public async Task TheRuleHoldsHostedAsProduction_AsItDoesAsDevelopment()
    {
        // The refusal is the domain's and the status comes from ProblemDetailsMapping, so nothing
        // here should differ; the point is that a host built differently is what a deployment is.
        var (client, environment, subject) = await HeldAsync(factory);
        await SetAsync(client, environment, subject, "someone-else@example.com");

        using var production = AsProduction();
        Assert.Equal(
            "Production", production.Services.GetRequiredService<IHostEnvironment>().EnvironmentName);

        var refused = await DecideAsync(production.CreateClient(), environment, subject, "approve");

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("not_a_reviewer", (await ApiFactory.ReadProblemAsync(refused)).ConcordatCode);
    }
}
