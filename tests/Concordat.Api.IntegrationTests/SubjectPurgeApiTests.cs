using System.Net;
using System.Net.Http.Json;
using Concordat.Application.Registry;

namespace Concordat.Api.IntegrationTests;

/// <summary>
/// Deleting a retired subject for good, over real HTTP against real PostgreSQL (ADR-046).
/// </summary>
/// <remarks>
/// <para>
/// The rules' arithmetic (which refusal first, how many names a message lists) is the handler's and is
/// proved there against hand-written fakes. What only a database can say is here: that the versions go with
/// the subject, that the schema they pointed at does not, that the name is free afterwards, that the
/// contract and reference checks read real rows, and that the audit entry outlives what it describes.
/// </para>
/// <para>Every test makes an environment of its own, so no assertion depends on what else has run.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public class SubjectPurgeApiTests(ApiFactory factory)
{
    private static string UniqueEnvironment() => $"env-{Guid.NewGuid():N}"[..24];

    private static string UniqueSubject() => $"acme.purge.S{Guid.NewGuid():N}"[..32];

    /// <summary>A schema whose bytes no other test registers, so reachability is this subject's alone.</summary>
    private static string UniqueBody(string field) =>
        "{\"type\":\"object\",\"properties\":{\"" + field + "\":{\"type\":\"string\"},\"g" +
        Guid.NewGuid().ToString("N") + "\":{\"type\":\"string\"}}}";

    private async Task<(HttpClient Client, string Environment)> NewEnvironmentAsync()
    {
        var client = factory.CreateClient();
        var environment = UniqueEnvironment();

        var created = await client.PostAsJsonAsync(
            "/v1/environments", new CreateEnvironmentRequest(environment), ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        return (client, environment);
    }

    private static async Task<string> NewSubjectAsync(HttpClient client, string environment, int versions = 1)
    {
        var subject = UniqueSubject();

        var made = await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/subjects",
            new CreateSubjectRequest(subject, "json", "alice"),
            ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);

        // Each version adds an optional property to the one before it, which is compatible, so none is held.
        var properties = new List<string>();

        for (var i = 1; i <= versions; i++)
        {
            properties.Add($"\"p{i}\":{{\"type\":\"string\"}}");
            var body = $"{{\"type\":\"object\",\"properties\":{{{string.Join(',', properties)}}}}}";

            var registered = await client.PostAsJsonAsync(
                $"/v1/environments/{environment}/subjects/{subject}/versions",
                new RegisterVersionRequest(body, null, null, "alice"),
                ApiFactory.Json);
            Assert.True(registered.IsSuccessStatusCode, await registered.Content.ReadAsStringAsync());
        }

        return subject;
    }

    private static async Task RetireAsync(HttpClient client, string environment, string subject)
    {
        var retired = await client.DeleteAsync($"/v1/environments/{environment}/subjects/{subject}");
        Assert.Equal(HttpStatusCode.OK, retired.StatusCode);
    }

    private static Task<HttpResponseMessage> PurgeAsync(
        HttpClient client, string environment, string subject, string? force = "true") =>
        client.PostAsync(
            $"/v1/environments/{environment}/subjects/{subject}/purge" + (force is null ? "" : $"?force={force}"),
            content: null);

    private static async Task<HttpStatusCode> GetStatusAsync(HttpClient client, string environment, string subject) =>
        (await client.GetAsync($"/v1/environments/{environment}/subjects/{subject}")).StatusCode;

    private static async Task AssertRefusedAsync(
        HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await ApiFactory.ReadProblemAsync(response)).ConcordatCode);
    }

    // ------------------------------------------------------------------------ the deletion

    [Fact]
    public async Task ARetiredSubjectIsDeleted_WithItsVersions_AndTheAnswerSaysHowMany()
    {
        var (client, environment) = await NewEnvironmentAsync();
        var subject = await NewSubjectAsync(client, environment, versions: 3);
        await RetireAsync(client, environment, subject);

        var response = await PurgeAsync(client, environment, subject);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var purged = await ApiFactory.ReadAsync<PurgeSubjectResponse>(response);
        Assert.Equal(new PurgeSubjectResponse(subject, 3), purged);

        Assert.Equal(HttpStatusCode.NotFound, await GetStatusAsync(client, environment, subject));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(
            $"/v1/environments/{environment}/subjects/{subject}/versions/1")).StatusCode);

        var listed = await client.GetFromJsonAsync<List<SubjectResponse>>(
            $"/v1/environments/{environment}/subjects", ApiFactory.Json);
        Assert.DoesNotContain(listed!, s => s.Name == subject);
    }

    [Fact]
    public async Task TheNameIsFreeAfterwards_AndANewSubjectWithItStartsAtVersionOne()
    {
        var (client, environment) = await NewEnvironmentAsync();
        var subject = await NewSubjectAsync(client, environment, versions: 2);
        await RetireAsync(client, environment, subject);
        await PurgeAsync(client, environment, subject);

        var again = await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/subjects",
            new CreateSubjectRequest(subject, "json", "bob"),
            ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);

        var registered = await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/subjects/{subject}/versions",
            new RegisterVersionRequest("""{"type":"object"}""", null, null, "bob"),
            ApiFactory.Json);
        var version = await ApiFactory.ReadAsync<RegisterVersionResponse>(registered);

        Assert.Equal(1, version.Ordinal);
    }

    [Fact]
    public async Task OnlyThatSubjectGoes_AnotherInTheSameEnvironmentIsUntouched()
    {
        var (client, environment) = await NewEnvironmentAsync();
        var doomed = await NewSubjectAsync(client, environment, versions: 2);
        var kept = await NewSubjectAsync(client, environment, versions: 2);
        await RetireAsync(client, environment, doomed);

        await PurgeAsync(client, environment, doomed);

        var survivor = await client.GetFromJsonAsync<SubjectResponse>(
            $"/v1/environments/{environment}/subjects/{kept}", ApiFactory.Json);
        Assert.Equal(2, survivor!.Versions.Count);
    }

    [Fact]
    public async Task TheSchemaIsNotDeleted_ButNothingInTheTenantReachesItAnyMore()
    {
        // Content-addressed schemas are never deleted (ADR-015); a lookup by id is answered by reachability, so
        // a schema that only this subject reached is now not found, as an id the tenant never held is.
        var (client, environment) = await NewEnvironmentAsync();
        var subject = UniqueSubject();
        await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/subjects",
            new CreateSubjectRequest(subject, "json", "alice"),
            ApiFactory.Json);
        var registered = await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/subjects/{subject}/versions",
            new RegisterVersionRequest(UniqueBody("only"), null, null, "alice"),
            ApiFactory.Json);
        var id = (await ApiFactory.ReadAsync<RegisterVersionResponse>(registered)).SchemaId;
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/v1/schemas/{id}")).StatusCode);
        await RetireAsync(client, environment, subject);

        await PurgeAsync(client, environment, subject);

        await AssertRefusedAsync(
            await client.GetAsync($"/v1/schemas/{id}"), HttpStatusCode.NotFound, "schema_not_found");
    }

    [Fact]
    public async Task ASchemaAnotherSubjectAlsoHoldsIsStillReachable()
    {
        var (client, environment) = await NewEnvironmentAsync();
        var doomed = UniqueSubject();
        var other = UniqueSubject();
        var body = UniqueBody("shared");

        foreach (var name in new[] { doomed, other })
        {
            await client.PostAsJsonAsync(
                $"/v1/environments/{environment}/subjects",
                new CreateSubjectRequest(name, "json", "alice"),
                ApiFactory.Json);
        }

        var id = "";
        foreach (var name in new[] { doomed, other })
        {
            var registered = await client.PostAsJsonAsync(
                $"/v1/environments/{environment}/subjects/{name}/versions",
                new RegisterVersionRequest(body, null, null, "alice"),
                ApiFactory.Json);
            id = (await ApiFactory.ReadAsync<RegisterVersionResponse>(registered)).SchemaId;
        }

        await RetireAsync(client, environment, doomed);
        await PurgeAsync(client, environment, doomed);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/v1/schemas/{id}")).StatusCode);
    }

    // ------------------------------------------------------------------------ the audit trail

    [Fact]
    public async Task TheDeletionIsRecorded_AndTheEntryOutlivesTheSubject()
    {
        var (client, environment) = await NewEnvironmentAsync();
        var subject = await NewSubjectAsync(client, environment, versions: 2);
        await RetireAsync(client, environment, subject);

        await PurgeAsync(client, environment, subject);

        var entries = await client.GetFromJsonAsync<IReadOnlyList<AuditResponse>>(
            $"/v1/audit?env={environment}&action=SUBJECT_PURGED", ApiFactory.Json);

        var entry = Assert.Single(entries!);
        Assert.Equal("SUBJECT_PURGED", entry.Action);
        Assert.Equal(subject, entry.Target);
        Assert.Equal("unclaimed-instance", entry.Actor);
        Assert.Equal("2 versions removed", entry.Detail);

        // The history it was removing is still there to read: the creation and the retirement.
        var all = await client.GetFromJsonAsync<IReadOnlyList<AuditResponse>>(
            $"/v1/audit?env={environment}", ApiFactory.Json);
        Assert.Contains(all!, e => e.Action == "SUBJECT_CREATED" && e.Target == subject);
        Assert.Contains(all!, e => e.Action == "SUBJECT_RETIRED" && e.Target == subject);
    }

    [Fact]
    public async Task ARefusedDeletionWritesNoEntry()
    {
        var (client, environment) = await NewEnvironmentAsync();
        var subject = await NewSubjectAsync(client, environment);

        await PurgeAsync(client, environment, subject);

        var entries = await client.GetFromJsonAsync<IReadOnlyList<AuditResponse>>(
            $"/v1/audit?env={environment}&action=SUBJECT_PURGED", ApiFactory.Json);
        Assert.Empty(entries!);
    }

    // ------------------------------------------------------------------------ the refusals

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("nonsense")]
    [InlineData("")]
    public async Task WithoutAnExplicitTrue_NothingIsDeleted(string? force)
    {
        var (client, environment) = await NewEnvironmentAsync();
        var subject = await NewSubjectAsync(client, environment);
        await RetireAsync(client, environment, subject);

        var response = await PurgeAsync(client, environment, subject, force);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await GetStatusAsync(client, environment, subject));
    }

    [Fact]
    public async Task ForceAbsent_IsNamedByItsOwnCode()
    {
        var (client, environment) = await NewEnvironmentAsync();
        var subject = await NewSubjectAsync(client, environment);
        await RetireAsync(client, environment, subject);

        await AssertRefusedAsync(
            await PurgeAsync(client, environment, subject, null),
            HttpStatusCode.BadRequest, "purge_requires_force");
    }

    [Fact]
    public async Task ASubjectThatIsNotRetired_IsRefusedAndKept()
    {
        var (client, environment) = await NewEnvironmentAsync();
        var subject = await NewSubjectAsync(client, environment, versions: 2);

        await AssertRefusedAsync(
            await PurgeAsync(client, environment, subject), HttpStatusCode.Conflict, "subject_not_retired");

        var kept = await client.GetFromJsonAsync<SubjectResponse>(
            $"/v1/environments/{environment}/subjects/{subject}", ApiFactory.Json);
        Assert.Equal(2, kept!.Versions.Count);
    }

    [Fact]
    public async Task ASubjectThatDoesNotExist_IsNotFound()
    {
        var (client, environment) = await NewEnvironmentAsync();

        await AssertRefusedAsync(
            await PurgeAsync(client, environment, UniqueSubject()),
            HttpStatusCode.NotFound, "subject_not_found");
    }

    [Fact]
    public async Task ASubjectNameNoSubjectCouldHave_IsRefusedAsAnyOtherRouteRefusesIt()
    {
        var (client, environment) = await NewEnvironmentAsync();

        var response = await PurgeAsync(client, environment, "not-a-valid-name");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task APurgedSubjectCannotBePurgedAgain()
    {
        var (client, environment) = await NewEnvironmentAsync();
        var subject = await NewSubjectAsync(client, environment);
        await RetireAsync(client, environment, subject);
        await PurgeAsync(client, environment, subject);

        await AssertRefusedAsync(
            await PurgeAsync(client, environment, subject), HttpStatusCode.NotFound, "subject_not_found");
    }

    [Fact]
    public async Task AnotherEnvironmentsSubjectOfTheSameNameIsNotTheOneDeleted()
    {
        var (client, one) = await NewEnvironmentAsync();
        var two = UniqueEnvironment();
        await client.PostAsJsonAsync("/v1/environments", new CreateEnvironmentRequest(two), ApiFactory.Json);
        var subject = UniqueSubject();

        foreach (var environment in new[] { one, two })
        {
            await client.PostAsJsonAsync(
                $"/v1/environments/{environment}/subjects",
                new CreateSubjectRequest(subject, "json", "alice"),
                ApiFactory.Json);
        }

        await RetireAsync(client, one, subject);
        await PurgeAsync(client, one, subject);

        Assert.Equal(HttpStatusCode.NotFound, await GetStatusAsync(client, one, subject));
        Assert.Equal(HttpStatusCode.OK, await GetStatusAsync(client, two, subject));
    }

    // ------------------------------------------------------------------------ what is in use

    [Fact]
    public async Task ABindingInAContractStopsIt_NamesTheContract_AndRemovingTheBindingLetsItThrough()
    {
        var (client, environment) = await NewEnvironmentAsync();
        var subject = await NewSubjectAsync(client, environment);
        var contract = $"orders-{Guid.NewGuid():N}"[..20];

        await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/contracts", new CreateContractRequest(contract), ApiFactory.Json);
        var added = await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/contracts/{contract}/publishes",
            new AddPublishBindingRequest("orders", "orders.created", [new SubjectRefInput(subject, "latest")]),
            ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var binding = (await ApiFactory.ReadAsync<ContractResponse>(added)).Publishes.Single();
        await RetireAsync(client, environment, subject);

        var refused = await PurgeAsync(client, environment, subject);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var problem = await ApiFactory.ReadProblemAsync(refused);
        Assert.Equal("subject_in_use", problem.ConcordatCode);
        Assert.Contains($"contract '{contract}'", problem.Detail, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, await GetStatusAsync(client, environment, subject));

        var removed = await client.DeleteAsync(
            $"/v1/environments/{environment}/contracts/{contract}/publishes/{binding.Id}");
        Assert.True(removed.IsSuccessStatusCode, await removed.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, (await PurgeAsync(client, environment, subject)).StatusCode);
    }

    [Fact]
    public async Task AConsumeBindingStopsItToo()
    {
        var (client, environment) = await NewEnvironmentAsync();
        var subject = await NewSubjectAsync(client, environment);
        var contract = $"billing-{Guid.NewGuid():N}"[..20];

        await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/contracts", new CreateContractRequest(contract), ApiFactory.Json);
        var added = await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/contracts/{contract}/consumes",
            new AddConsumeBindingRequest("billing-queue", [new SubjectRefInput(subject, "latest")]),
            ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        await RetireAsync(client, environment, subject);

        await AssertRefusedAsync(
            await PurgeAsync(client, environment, subject), HttpStatusCode.Conflict, "subject_in_use");
    }

    [Fact]
    public async Task AContractInAnotherEnvironmentDoesNotStopIt()
    {
        var (client, environment) = await NewEnvironmentAsync();
        var elsewhere = UniqueEnvironment();
        await client.PostAsJsonAsync("/v1/environments", new CreateEnvironmentRequest(elsewhere), ApiFactory.Json);
        var subject = await NewSubjectAsync(client, environment);
        var inElsewhere = await NewSubjectAsync(client, elsewhere);
        var contract = $"orders-{Guid.NewGuid():N}"[..20];

        await client.PostAsJsonAsync(
            $"/v1/environments/{elsewhere}/contracts", new CreateContractRequest(contract), ApiFactory.Json);
        await client.PostAsJsonAsync(
            $"/v1/environments/{elsewhere}/contracts/{contract}/publishes",
            new AddPublishBindingRequest("orders", "orders.created", [new SubjectRefInput(inElsewhere, "latest")]),
            ApiFactory.Json);
        await RetireAsync(client, environment, subject);

        Assert.Equal(HttpStatusCode.OK, (await PurgeAsync(client, environment, subject)).StatusCode);
    }

    [Fact]
    public async Task ARegisteredServiceThatConsumesItStopsIt_AndNamesTheService()
    {
        var (client, environment) = await NewEnvironmentAsync();
        var subject = await NewSubjectAsync(client, environment);
        var service = $"billing-{Guid.NewGuid():N}"[..20];

        var registered = await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/services",
            new RegisterServiceRequest(service, null, [new SubjectRefInput(subject, "latest")], "ci"),
            ApiFactory.Json);
        Assert.True(registered.IsSuccessStatusCode, await registered.Content.ReadAsStringAsync());
        await RetireAsync(client, environment, subject);

        var refused = await PurgeAsync(client, environment, subject);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var problem = await ApiFactory.ReadProblemAsync(refused);
        Assert.Equal("subject_in_use", problem.ConcordatCode);
        Assert.Contains($"service '{service}'", problem.Detail, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------ what refers to it

    /// <summary>
    /// A new subject in <paramref name="environment"/> whose first version refers to
    /// <paramref name="target"/> in <paramref name="targetEnvironment"/>.
    /// </summary>
    private static async Task<string> RegisterReferrerAsync(
        HttpClient client, string environment, string targetEnvironment, string target, int ordinal)
    {
        var referrer = UniqueSubject();
        await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/subjects",
            new CreateSubjectRequest(referrer, "json", "alice"),
            ApiFactory.Json);

        var body =
            "{\"type\":\"object\",\"properties\":{\"ref\":{\"$ref\":\"concordat://" +
            $"{targetEnvironment}/{target}/{ordinal}" + "\"}}}";

        var registered = await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/subjects/{referrer}/versions",
            new RegisterVersionRequest(body, null, null, "alice"),
            ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);

        return referrer;
    }

    [Fact]
    public async Task ASchemaThatRefersToItStopsIt_NamesTheVersion_AndDeletingTheReferrerFirstLetsItThrough()
    {
        var (client, environment) = await NewEnvironmentAsync();
        var target = await NewSubjectAsync(client, environment);
        var referrer = await RegisterReferrerAsync(client, environment, environment, target, 1);
        await RetireAsync(client, environment, target);

        var refused = await PurgeAsync(client, environment, target);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var problem = await ApiFactory.ReadProblemAsync(refused);
        Assert.Equal("subject_referenced", problem.ConcordatCode);
        Assert.Contains($"{referrer}@1", problem.Detail, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, await GetStatusAsync(client, environment, target));

        // The referrer has to go first, and has to be retired to do it.
        await RetireAsync(client, environment, referrer);
        Assert.Equal(HttpStatusCode.OK, (await PurgeAsync(client, environment, referrer)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await PurgeAsync(client, environment, target)).StatusCode);
    }

    [Fact]
    public async Task AVersionInAnotherEnvironmentThatReferencesItStopsIt_AndSaysItIsElsewhere()
    {
        // A reference carries an environment, so staging can hold one to a subject in dev, and deleting the
        // subject would leave it pointing at nothing.
        var (client, environment) = await NewEnvironmentAsync();
        var elsewhere = UniqueEnvironment();
        await client.PostAsJsonAsync("/v1/environments", new CreateEnvironmentRequest(elsewhere), ApiFactory.Json);
        var target = await NewSubjectAsync(client, environment);
        var referrer = await RegisterReferrerAsync(client, elsewhere, environment, target, 1);
        await RetireAsync(client, environment, target);

        var refused = await PurgeAsync(client, environment, target);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var problem = await ApiFactory.ReadProblemAsync(refused);
        Assert.Equal("subject_referenced", problem.ConcordatCode);
        Assert.Contains($"{referrer}@1 (in another environment)", problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReferenceToTheSameNameInAnotherEnvironmentDoesNotStopIt()
    {
        // `target` exists in both environments. The referrer names the one in `elsewhere`, so it is that
        // one's to keep, and deleting this one leaves nothing dangling.
        var (client, environment) = await NewEnvironmentAsync();
        var elsewhere = UniqueEnvironment();
        await client.PostAsJsonAsync("/v1/environments", new CreateEnvironmentRequest(elsewhere), ApiFactory.Json);
        var target = await NewSubjectAsync(client, environment);
        await client.PostAsJsonAsync(
            $"/v1/environments/{elsewhere}/subjects",
            new CreateSubjectRequest(target, "json", "alice"),
            ApiFactory.Json);
        await client.PostAsJsonAsync(
            $"/v1/environments/{elsewhere}/subjects/{target}/versions",
            new RegisterVersionRequest("""{"type":"object"}""", null, null, "alice"),
            ApiFactory.Json);
        await RegisterReferrerAsync(client, elsewhere, elsewhere, target, 1);
        await RetireAsync(client, environment, target);

        Assert.Equal(HttpStatusCode.OK, (await PurgeAsync(client, environment, target)).StatusCode);
    }
}
