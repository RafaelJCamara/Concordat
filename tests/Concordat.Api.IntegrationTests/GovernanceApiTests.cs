using System.Net;
using System.Net.Http.Json;
using Concordat.Application.Registry;

namespace Concordat.Api.IntegrationTests;

/// <summary>
/// M7.4's governance routes: services, impact, promotion and the audit trail.
/// </summary>
[Collection(ApiCollection.Name)]
public class GovernanceApiTests(ApiFactory factory)
{
    private const string V1 = """{"type":"object","properties":{"id":{"type":"string"}}}""";

    private const string V1Plus =
        """{"type":"object","properties":{"id":{"type":"string"},"note":{"type":"string"}}}""";

    /// <summary>Breaking to <em>register</em>: a new reader cannot read old data (BACKWARD).</summary>
    private const string V1Breaking =
        """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}""";

    /// <summary>
    /// Breaking to <em>consume</em>: an existing reader cannot read new data (FORWARD).
    /// </summary>
    /// <remarks>
    /// The two are not the same change, and the distinction is the whole of impact analysis.
    /// Adding a required field is the first and not the second — data written under it always
    /// carries the field, so a reader that never required it is unaffected. Changing a
    /// property's type is what actually stops an existing consumer.
    /// </remarks>
    private const string V1Incompatible =
        """{"type":"object","properties":{"id":{"type":"integer"}}}""";

    private static string UniqueEnvironment() => $"env-{Guid.CreateVersion7():N}"[..24];

    private static string UniqueSubject() => $"acme.gov.S{Guid.CreateVersion7():N}";

    private static SubjectRefInput[] Refs(params string[] entries) =>
        [.. entries.Select(e =>
        {
            var at = e.LastIndexOf('@');
            return new SubjectRefInput(e[..at], e[(at + 1)..]);
        })];

    private async Task<(HttpClient Client, string Environment)> NewEnvironmentAsync()
    {
        var client = factory.CreateClient();
        var name = UniqueEnvironment();

        var response = await client.PostAsJsonAsync(
            "/v1/environments", new CreateEnvironmentRequest(name), ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (client, name);
    }

    private static async Task<string> NewSubjectAsync(
        HttpClient client, string environment, string? body = V1)
    {
        var name = UniqueSubject();

        var created = await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/subjects",
            new CreateSubjectRequest(name, "json", "alice", null, null, "open"),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        if (body is not null)
        {
            await RegisterAsync(client, environment, name, body, "1.0.0");
        }

        return name;
    }

    private static async Task<RegisterVersionResponse> RegisterAsync(
        HttpClient client, string environment, string subject, string body, string? semver = null)
    {
        var response = await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/subjects/{subject}/versions",
            new RegisterVersionRequest(body, semver, null, "alice"),
            ApiFactory.Json);

        Assert.True(
            response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK,
            $"register returned {response.StatusCode}");

        return await ApiFactory.ReadAsync<RegisterVersionResponse>(response);
    }

    private static Task<HttpResponseMessage> RegisterServiceAsync(
        HttpClient client,
        string environment,
        string name,
        string[]? produces = null,
        string[]? consumes = null) =>
        client.PostAsJsonAsync(
            $"/v1/environments/{environment}/services",
            new RegisterServiceRequest(
                name, Refs(produces ?? []), Refs(consumes ?? []), "ci"),
            ApiFactory.Json);

    // ------------------------------------------------------------------- services

    [Fact]
    public async Task AServiceDeclaresWhatItProducesAndConsumes()
    {
        var (client, environment) = await NewEnvironmentAsync();
        var subject = await NewSubjectAsync(client, environment);

        var response = await RegisterServiceAsync(
            client, environment, "orders-api", [$"{subject}@latest"], [$"{subject}@2"]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ApiFactory.ReadAsync<ServiceResponse>(response);

        Assert.Equal("orders-api", body.Name);
        Assert.Equal("latest", Assert.Single(body.Produces).Selector);
        Assert.Equal("2", Assert.Single(body.Consumes).Selector);
        Assert.False(body.Stale);
    }

    [Fact]
    public async Task ReportingTwiceIsOneServiceAndTheLastReportWins()
    {
        // A fleet-wide restart reports once per pod. Fifty rows would make impact analysis
        // report fifty affected consumers where there is one.
        var (client, environment) = await NewEnvironmentAsync();
        var subject = await NewSubjectAsync(client, environment);

        await RegisterServiceAsync(client, environment, "orders-api", consumes: [$"{subject}@1"]);
        await RegisterServiceAsync(client, environment, "orders-api", consumes: [$"{subject}@3"]);

        var listed = await client.GetFromJsonAsync<IReadOnlyList<ServiceResponse>>(
            $"/v1/environments/{environment}/services", ApiFactory.Json);

        var only = Assert.Single(listed!);
        Assert.Equal("3", Assert.Single(only.Consumes).Selector);
    }

    [Fact]
    public async Task AServiceThatDeclaresNothingIsStillAccepted()
    {
        // Partial adoption is the state every brownfield estate is in.
        var (client, environment) = await NewEnvironmentAsync();

        var response = await RegisterServiceAsync(client, environment, "not-instrumented-yet");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AnUnusableServiceNameIs400()
    {
        var (client, environment) = await NewEnvironmentAsync();

        var response = await RegisterServiceAsync(client, environment, "orders api");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "service_name_invalid",
            (await ApiFactory.ReadProblemAsync(response)).ConcordatCode);
    }

    [Fact]
    public async Task AnUnknownServiceIs404()
    {
        var (client, environment) = await NewEnvironmentAsync();

        var response = await client.GetAsync($"/v1/environments/{environment}/services/nope");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --------------------------------------------------------------------- impact

    [Fact]
    public async Task ImpactNamesThePinnedConsumerABreakingChangeWouldStop()
    {
        var (client, environment) = await NewEnvironmentAsync();
        var subject = await NewSubjectAsync(client, environment);

        await RegisterServiceAsync(client, environment, "reader", consumes: [$"{subject}@1"]);

        var response = await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/subjects/{subject}/impact",
            new ImpactRequest(V1Incompatible),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ApiFactory.ReadAsync<ImpactResponse>(response);

        Assert.Equal(1, body.BreakingCount);
        var consumer = Assert.Single(body.Consumers);
        Assert.Equal("reader", consumer.Service);
        Assert.True(consumer.Breaks);
        Assert.Equal("CHECKED", consumer.Certainty);
        Assert.Equal(1, consumer.ReaderOrdinal);
        Assert.NotEmpty(consumer.Reasons);

        // Nothing was registered by asking. That is the whole value of the candidate form.
        var latest = await client.GetFromJsonAsync<VersionResponse>(
            $"/v1/environments/{environment}/subjects/{subject}/versions/latest", ApiFactory.Json);

        Assert.Equal(1, latest!.Ordinal);
    }

    [Fact]
    public async Task AnAdditiveChangeBreaksNobody()
    {
        var (client, environment) = await NewEnvironmentAsync();
        var subject = await NewSubjectAsync(client, environment);

        await RegisterServiceAsync(client, environment, "reader", consumes: [$"{subject}@1"]);

        var body = await ApiFactory.ReadAsync<ImpactResponse>(
            await client.PostAsJsonAsync(
                $"/v1/environments/{environment}/subjects/{subject}/impact",
                new ImpactRequest(V1Plus),
                ApiFactory.Json));

        Assert.Equal(0, body.BreakingCount);
        Assert.False(Assert.Single(body.Consumers).Breaks);
    }

    [Fact]
    public async Task AddingARequiredFieldDoesNotBreakExistingConsumers()
    {
        // Pins the direction, which is the one thing in this feature that is easy to get
        // backwards and impossible to notice. Adding a required field is breaking to REGISTER —
        // the new schema cannot read old data — and harmless to CONSUME, because data written
        // under it always carries the field. Reporting it as consumer-breaking would cry wolf
        // on the single most common schema change there is.
        var (client, environment) = await NewEnvironmentAsync();
        var subject = await NewSubjectAsync(client, environment);

        await RegisterServiceAsync(client, environment, "reader", consumes: [$"{subject}@1"]);

        var body = await ApiFactory.ReadAsync<ImpactResponse>(
            await client.PostAsJsonAsync(
                $"/v1/environments/{environment}/subjects/{subject}/impact",
                new ImpactRequest(V1Breaking),
                ApiFactory.Json));

        Assert.Equal(0, body.BreakingCount);
        Assert.Equal("CHECKED", Assert.Single(body.Consumers).Certainty);
    }

    [Fact]
    public async Task AConsumerTrackingLatestIsReportedAsFollowingRatherThanGuessedAt()
    {
        // The registry knows what such a consumer fetches, not what its code was built against.
        // Calling it safe would be a guess; calling it broken would make every report useless.
        var (client, environment) = await NewEnvironmentAsync();
        var subject = await NewSubjectAsync(client, environment);

        await RegisterServiceAsync(client, environment, "follower", consumes: [$"{subject}@latest"]);

        var body = await ApiFactory.ReadAsync<ImpactResponse>(
            await client.PostAsJsonAsync(
                $"/v1/environments/{environment}/subjects/{subject}/impact",
                new ImpactRequest(V1Breaking),
                ApiFactory.Json));

        var consumer = Assert.Single(body.Consumers);
        Assert.Equal("FOLLOWS_LATEST", consumer.Certainty);
        Assert.False(consumer.Breaks);
        Assert.Equal(0, body.BreakingCount);
    }

    [Fact]
    public async Task ARangeIsJudgedAtItsFloorNotItsCeiling()
    {
        // '>=1' claims to handle version 1 onward, so version 1 is the reader that has to
        // survive. Checking the newest instead would clear a consumer whose oldest supported
        // version is exactly the one that breaks.
        var (client, environment) = await NewEnvironmentAsync();
        var subject = await NewSubjectAsync(client, environment);
        await RegisterAsync(client, environment, subject, V1Plus, "1.1.0");

        await RegisterServiceAsync(client, environment, "ranged", consumes: [$"{subject}@>=1"]);

        var body = await ApiFactory.ReadAsync<ImpactResponse>(
            await client.PostAsJsonAsync(
                $"/v1/environments/{environment}/subjects/{subject}/impact",
                new ImpactRequest(V1Incompatible),
                ApiFactory.Json));

        var consumer = Assert.Single(body.Consumers);
        Assert.Equal(1, consumer.ReaderOrdinal);
        Assert.True(consumer.Breaks);
    }

    [Fact]
    public async Task AProducerIsNotReportedAsBrokenByItsOwnChange()
    {
        var (client, environment) = await NewEnvironmentAsync();
        var subject = await NewSubjectAsync(client, environment);

        await RegisterServiceAsync(client, environment, "writer", produces: [$"{subject}@latest"]);

        var body = await ApiFactory.ReadAsync<ImpactResponse>(
            await client.PostAsJsonAsync(
                $"/v1/environments/{environment}/subjects/{subject}/impact",
                new ImpactRequest(V1Breaking),
                ApiFactory.Json));

        Assert.Empty(body.Consumers);
    }

    [Fact]
    public async Task ImpactAlsoNamesTheContractsThatCarryTheSubject()
    {
        var (client, environment) = await NewEnvironmentAsync();
        var subject = await NewSubjectAsync(client, environment);

        var contract = $"c-{Guid.CreateVersion7():N}"[..16];
        await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/contracts",
            new CreateContractRequest(contract),
            ApiFactory.Json);

        await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/contracts/{contract}/publishes",
            new AddPublishBindingRequest("orders", "orders.#", Refs($"{subject}@latest")),
            ApiFactory.Json);

        var body = await ApiFactory.ReadAsync<ImpactResponse>(
            await client.PostAsJsonAsync(
                $"/v1/environments/{environment}/subjects/{subject}/impact",
                new ImpactRequest(V1Plus),
                ApiFactory.Json));

        Assert.Equal(contract, Assert.Single(body.Contracts));
    }

    [Fact]
    public async Task ImpactOfARegisteredVersionNeedsNoBody()
    {
        var (client, environment) = await NewEnvironmentAsync();
        var subject = await NewSubjectAsync(client, environment);

        var response = await client.GetAsync(
            $"/v1/environments/{environment}/subjects/{subject}/impact");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ApiFactory.ReadAsync<ImpactResponse>(response);

        Assert.Equal(1, body.CandidateOrdinal);
        Assert.Equal(32, body.CandidateSchemaId!.Length);
    }

    [Fact]
    public async Task ImpactOnAnUnknownSubjectIs404()
    {
        var (client, environment) = await NewEnvironmentAsync();

        var response = await client.GetAsync(
            $"/v1/environments/{environment}/subjects/{UniqueSubject()}/impact");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ------------------------------------------------------------------ promotion

    [Fact]
    public async Task PromotionCarriesTheSchemaIdAcrossUnchanged()
    {
        // Content addressing is what lets an in-flight envelope stay resolvable across a
        // promotion. If the id changed, every consumer holding the old one would fail.
        var (client, source) = await NewEnvironmentAsync();
        var (_, target) = await NewEnvironmentAsync();

        var subject = await NewSubjectAsync(client, source);
        var registered = await RegisterAsync(client, source, subject, V1);

        var response = await client.PostAsJsonAsync(
            $"/v1/environments/{source}/subjects/{subject}/promote",
            new PromoteRequest(target, PromotedBy: "alice"),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ApiFactory.ReadAsync<PromotionResponse>(response);

        Assert.Equal(registered.SchemaId, body.SchemaId);
        Assert.Equal("ACTIVE", body.Status);
        Assert.True(body.SubjectCreated);
        Assert.Equal(1, body.TargetOrdinal);
    }

    [Fact]
    public async Task PromotionCreatesTheTargetSubjectWithTheSourcesFormat()
    {
        // The bug this route exists to remove: the CLI's client-side promotion created the
        // target subject as JSON regardless of what the source actually was.
        var (client, source) = await NewEnvironmentAsync();
        var (_, target) = await NewEnvironmentAsync();

        var subject = UniqueSubject();
        await client.PostAsJsonAsync(
            $"/v1/environments/{source}/subjects",
            new CreateSubjectRequest(subject, "avro", "alice", null, null, "open"),
            ApiFactory.Json);

        await client.PostAsJsonAsync(
            $"/v1/environments/{source}/subjects/{subject}/versions",
            new RegisterVersionRequest(
                """{"type":"record","name":"R","fields":[{"name":"id","type":"string"}]}""",
                "1.0.0",
                null,
                "alice"),
            ApiFactory.Json);

        var promoted = await client.PostAsJsonAsync(
            $"/v1/environments/{source}/subjects/{subject}/promote",
            new PromoteRequest(target),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, promoted.StatusCode);

        var landed = await client.GetFromJsonAsync<SubjectResponse>(
            $"/v1/environments/{target}/subjects/{subject}", ApiFactory.Json);

        Assert.Equal("avro", landed!.Format);
    }

    [Fact]
    public async Task PromotionIsRecheckedAgainstTheTargetsOwnHistory()
    {
        // A version compatible in dev says nothing about prod, whose history is older. Here the
        // target already holds the additive schema, so promoting the stricter one is breaking
        // there and lands as a proposal rather than going active.
        var (client, source) = await NewEnvironmentAsync();
        var (_, target) = await NewEnvironmentAsync();

        var subject = await NewSubjectAsync(client, source, V1Breaking);

        await client.PostAsJsonAsync(
            $"/v1/environments/{target}/subjects",
            new CreateSubjectRequest(subject, "json", "alice", null, null, "open"),
            ApiFactory.Json);

        // Deliberately an earlier label than the source's: promotion carries the source's
        // semver, and the target's own increasing-label rule still applies to it.
        await RegisterAsync(client, target, subject, V1, "0.9.0");

        var promoted = await client.PostAsJsonAsync(
            $"/v1/environments/{source}/subjects/{subject}/promote",
            new PromoteRequest(target),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, promoted.StatusCode);
        var body = await ApiFactory.ReadAsync<PromotionResponse>(promoted);

        Assert.Equal("AWAITING_APPROVAL", body.Status);
        Assert.False(body.SubjectCreated);
        Assert.NotEmpty(body.Divergences);
    }

    [Fact]
    public async Task PromotingIntoTheSameEnvironmentIsRefused()
    {
        var (client, environment) = await NewEnvironmentAsync();
        var subject = await NewSubjectAsync(client, environment);

        var response = await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/subjects/{subject}/promote",
            new PromoteRequest(environment),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "promotion_target_invalid",
            (await ApiFactory.ReadProblemAsync(response)).ConcordatCode);
    }

    [Fact]
    public async Task APendingProposalCannotBePromoted()
    {
        // Otherwise a proposal would be laundered into the target and judged only there, never
        // against the review it was waiting for.
        var (client, source) = await NewEnvironmentAsync();
        var (_, target) = await NewEnvironmentAsync();

        var subject = await NewSubjectAsync(client, source);
        var breaking = await RegisterAsync(client, source, subject, V1Breaking);
        Assert.Equal("AWAITING_APPROVAL", breaking.Status);

        var response = await client.PostAsJsonAsync(
            $"/v1/environments/{source}/subjects/{subject}/promote",
            new PromoteRequest(target, breaking.Ordinal),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            "promotion_source_not_active",
            (await ApiFactory.ReadProblemAsync(response)).ConcordatCode);
    }

    [Fact]
    public async Task PromotingToAnUnknownEnvironmentIs404()
    {
        var (client, source) = await NewEnvironmentAsync();
        var subject = await NewSubjectAsync(client, source);

        var response = await client.PostAsJsonAsync(
            $"/v1/environments/{source}/subjects/{subject}/promote",
            new PromoteRequest(UniqueEnvironment()),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------------- audit

    [Fact]
    public async Task TheTrailRecordsWhatActuallyHappened()
    {
        var (client, environment) = await NewEnvironmentAsync();
        var subject = await NewSubjectAsync(client, environment);

        var entries = await client.GetFromJsonAsync<IReadOnlyList<AuditResponse>>(
            $"/v1/audit?env={environment}", ApiFactory.Json);

        Assert.Contains(entries!, e => e.Action == "ENVIRONMENT_CREATED");
        Assert.Contains(entries!, e => e.Action == "SUBJECT_CREATED" && e.Target == subject);
        Assert.Contains(entries!, e => e.Action == "VERSION_REGISTERED" && e.Target == subject);
    }

    [Fact]
    public async Task ARefusedRequestLeavesNoTrace()
    {
        // The trail records state changes, and a refusal never reaches a commit. An entry
        // written outside the transaction could survive a rollback of the thing it claims
        // happened, which is worse than a gap.
        var (client, environment) = await NewEnvironmentAsync();

        await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/subjects",
            new CreateSubjectRequest("not a valid name!", "json", "alice", null, null, "open"),
            ApiFactory.Json);

        var entries = await client.GetFromJsonAsync<IReadOnlyList<AuditResponse>>(
            $"/v1/audit?env={environment}&action=SUBJECT_CREATED", ApiFactory.Json);

        Assert.Empty(entries!);
    }

    [Fact]
    public async Task TheTrailIsNewestFirst()
    {
        var (client, environment) = await NewEnvironmentAsync();
        await NewSubjectAsync(client, environment);

        var entries = await client.GetFromJsonAsync<IReadOnlyList<AuditResponse>>(
            $"/v1/audit?env={environment}", ApiFactory.Json);

        Assert.Equal(entries!.OrderByDescending(e => e.At), entries);
    }

    [Fact]
    public async Task AnUnknownActionFilterIsRefusedRatherThanIgnored()
    {
        // A typo would otherwise return an empty list, which reads as "nothing happened" — the
        // one answer an audit query must never give by accident.
        var response = await factory.CreateClient().GetAsync("/v1/audit?action=SUBJECT_DELETED");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "audit_filter_invalid",
            (await ApiFactory.ReadProblemAsync(response)).ConcordatCode);
    }

    [Fact]
    public async Task AReversedWindowIsRefused()
    {
        var response = await factory.CreateClient().GetAsync(
            "/v1/audit?since=2026-08-14T00:00:00Z&until=2026-08-01T00:00:00Z");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CredentialEntriesCarryNoSecret()
    {
        // The one entry whose whole purpose is to record that a credential changed, in a table
        // designed to be read widely.
        var (client, environment) = await NewEnvironmentAsync();

        var broker = await ApiFactory.ReadAsync<EnvironmentResponse>(
            await client.PostAsJsonAsync(
                $"/v1/environments/{environment}/brokers",
                new AddBrokerRequest("primary", "amqp://localhost:5672", "/"),
                ApiFactory.Json));

        await client.PutAsJsonAsync(
            $"/v1/environments/{environment}/brokers/{broker.Brokers[0].BrokerId}/credentials",
            new SetBrokerCredentialRequest("guest", "hunter2"),
            ApiFactory.Json);

        var entries = await client.GetFromJsonAsync<IReadOnlyList<AuditResponse>>(
            $"/v1/audit?env={environment}", ApiFactory.Json);

        var entry = Assert.Single(entries!, e => e.Action == "BROKER_CREDENTIAL_SET");
        Assert.Equal("primary", entry.Target);
        Assert.Null(entry.Detail);
        Assert.DoesNotContain(entries!, e => (e.Detail ?? "").Contains("hunter2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnOwnerChangeIsRecordedAsTheCallerWithThePreviousAndNewOwner()
    {
        // The subject screen's owner confirmation says exactly this, so it is pinned here rather
        // than left to a reading of the handler: one SUBJECT_UPDATED row, attributed to whoever
        // made the request — not to the owner they named, which is what it recorded until the
        // owner screen was written — and reading both owners (it read only "owner changed" until
        // ADR-033).
        var (client, environment) = await NewEnvironmentAsync();
        var subject = await NewSubjectAsync(client, environment);

        var patched = await client.PatchAsJsonAsync(
            $"/v1/environments/{environment}/subjects/{subject}",
            new UpdateSubjectRequest(Owner: "bob"),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);

        var entries = await client.GetFromJsonAsync<IReadOnlyList<AuditResponse>>(
            $"/v1/audit?env={environment}", ApiFactory.Json);

        var changed = Assert.Single(entries!, e => e.Action == "SUBJECT_UPDATED");
        var registered = Assert.Single(entries!, e => e.Action == "VERSION_REGISTERED");

        Assert.Equal(registered.Actor, changed.Actor);
        Assert.NotEqual("bob", changed.Actor);
        Assert.Equal("owner alice -> bob", changed.Detail);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankOwnerIsNothingToChangeAndNotACrash(string owner)
    {
        // `{"owner":"   "}` answered 500: the audit row's actor was built from the owner text,
        // which the owner rule had just declined to apply as blank.
        var (client, environment) = await NewEnvironmentAsync();
        var subject = await NewSubjectAsync(client, environment);

        var patched = await client.PatchAsJsonAsync(
            $"/v1/environments/{environment}/subjects/{subject}",
            new UpdateSubjectRequest(Owner: owner),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        Assert.Equal("alice", (await ApiFactory.ReadAsync<SubjectResponse>(patched)).Owner);
    }

    [Fact]
    public async Task APolicyChangeIsRecordedAsTheCallerWithTheOldAndNewPolicy()
    {
        // Until ADR-033 this test asserted the opposite and said so: a gap, not a decision.
        // Changing a subject's compatibility policy changes what the registry will let through,
        // and the trail did not say it happened. The subject screen's policy confirmation says
        // what is recorded, and this is the registry-side pin for each word of it: one entry of
        // its own action (SUBJECT_UPDATED stays the row for an owner change), attributed to the
        // caller, reading the old policy and the new.
        var (client, environment) = await NewEnvironmentAsync();
        var subject = await NewSubjectAsync(client, environment);

        var put = await client.PutAsJsonAsync(
            $"/v1/environments/{environment}/subjects/{subject}/compatibility-policy",
            new SetCompatibilityPolicyRequest("FULL", "SOURCE"),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var entries = await client.GetFromJsonAsync<IReadOnlyList<AuditResponse>>(
            $"/v1/audit?env={environment}", ApiFactory.Json);

        var changed = Assert.Single(entries!, e => e.Action == "SUBJECT_POLICY_CHANGED");
        var registered = Assert.Single(entries!, e => e.Action == "VERSION_REGISTERED");

        Assert.Equal(subject, changed.Target);
        Assert.Equal(registered.Actor, changed.Actor);
        Assert.Equal("policy inherited -> FULL/SOURCE", changed.Detail);
        Assert.DoesNotContain(entries!, e => e.Action == "SUBJECT_UPDATED");

        // And back: clearing it is a change too, and says what it was.
        var cleared = await client.PutAsJsonAsync(
            $"/v1/environments/{environment}/subjects/{subject}/compatibility-policy",
            new SetCompatibilityPolicyRequest(null, null),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);

        var filtered = await client.GetFromJsonAsync<IReadOnlyList<AuditResponse>>(
            $"/v1/audit?env={environment}&action=SUBJECT_POLICY_CHANGED", ApiFactory.Json);

        // Newest first, and the filter by action returns only these: the point of an action of
        // its own.
        Assert.Equal(
            ["policy FULL/SOURCE -> inherited", "policy inherited -> FULL/SOURCE"],
            filtered!.Select(e => e.Detail));
    }

    [Fact]
    public async Task SettingThePolicyASubjectAlreadyHasWritesNothingAndStillAnswers200()
    {
        var (client, environment) = await NewEnvironmentAsync();
        var subject = await NewSubjectAsync(client, environment);

        // It inherits, and inheriting again changes nothing.
        var put = await client.PutAsJsonAsync(
            $"/v1/environments/{environment}/subjects/{subject}/compatibility-policy",
            new SetCompatibilityPolicyRequest(null, null),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var entries = await client.GetFromJsonAsync<IReadOnlyList<AuditResponse>>(
            $"/v1/audit?env={environment}", ApiFactory.Json);

        Assert.DoesNotContain(entries!, e => e.Action == "SUBJECT_POLICY_CHANGED");
    }

    [Fact]
    public async Task ARefusedPolicyChangeLeavesNoEntry()
    {
        // "Records what happened, not what was attempted": half a policy, an unknown subject and a
        // retired one are each refused and none reaches a commit.
        var (client, environment) = await NewEnvironmentAsync();
        var subject = await NewSubjectAsync(client, environment);
        var route = $"/v1/environments/{environment}/subjects/{subject}/compatibility-policy";

        var half = await client.PutAsJsonAsync(
            route, new SetCompatibilityPolicyRequest("FULL", null), ApiFactory.Json);
        var unknown = await client.PutAsJsonAsync(
            $"/v1/environments/{environment}/subjects/acme.gov.Missing/compatibility-policy",
            new SetCompatibilityPolicyRequest("FULL", "SOURCE"),
            ApiFactory.Json);

        await client.DeleteAsync($"/v1/environments/{environment}/subjects/{subject}");
        var retired = await client.PutAsJsonAsync(
            route, new SetCompatibilityPolicyRequest("FULL", "SOURCE"), ApiFactory.Json);

        Assert.Equal(HttpStatusCode.BadRequest, half.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, retired.StatusCode);

        var entries = await client.GetFromJsonAsync<IReadOnlyList<AuditResponse>>(
            $"/v1/audit?env={environment}", ApiFactory.Json);

        Assert.DoesNotContain(entries!, e => e.Action == "SUBJECT_POLICY_CHANGED");
    }

    [Fact]
    public async Task APolicyChangeNotifiesNobody()
    {
        // The confirmation says "Nobody is notified", and the handler holds no outbox to say it
        // with, so this is measured where a notification would show: the tenant's outbox does not
        // deepen. Relative, because the outbox is tenant-wide, and nothing else runs in between.
        var (client, environment) = await NewEnvironmentAsync();
        var subject = await NewSubjectAsync(client, environment);

        var before = await client.GetFromJsonAsync<OutboxDepthResponse>(
            "/v1/notifications/outbox", ApiFactory.Json);

        var put = await client.PutAsJsonAsync(
            $"/v1/environments/{environment}/subjects/{subject}/compatibility-policy",
            new SetCompatibilityPolicyRequest("FULL", "SOURCE"),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var after = await client.GetFromJsonAsync<OutboxDepthResponse>(
            "/v1/notifications/outbox", ApiFactory.Json);

        Assert.Equal(before!.Pending, after!.Pending);
    }

    [Fact]
    public async Task HealthChecksAreNotAudited()
    {
        // A probe on a timer would produce most of the rows in the table and none of the
        // answers anyone opens an audit log for.
        var (client, environment) = await NewEnvironmentAsync();

        var broker = await ApiFactory.ReadAsync<EnvironmentResponse>(
            await client.PostAsJsonAsync(
                $"/v1/environments/{environment}/brokers",
                new AddBrokerRequest("primary", "amqp://localhost:1", "/"),
                ApiFactory.Json));

        await client.PostAsync(
            $"/v1/environments/{environment}/brokers/{broker.Brokers[0].BrokerId}/check", null);

        var entries = await client.GetFromJsonAsync<IReadOnlyList<AuditResponse>>(
            $"/v1/audit?env={environment}", ApiFactory.Json);

        Assert.Equal(2, entries!.Count);
        Assert.Contains(entries, e => e.Action == "ENVIRONMENT_CREATED");
        Assert.Contains(entries, e => e.Action == "BROKER_ADDED");
    }

    // --------------------------------------------------------- revert auto-dismiss

    [Fact]
    public async Task RevertingAChangeDismissesThePendingProposalAndSaysSo()
    {
        // The CI shape of the bug: push a breaking change, think better of it, revert the file.
        // The next pipeline run re-registers the deployed schema and previously left a reviewer
        // holding a proposal no repository contained.
        var (client, environment) = await NewEnvironmentAsync();
        var subject = await NewSubjectAsync(client, environment);

        var proposal = await RegisterAsync(client, environment, subject, V1Breaking);
        Assert.Equal("AWAITING_APPROVAL", proposal.Status);

        await RegisterAsync(client, environment, subject, V1);

        var version = await client.GetFromJsonAsync<VersionResponse>(
            $"/v1/environments/{environment}/subjects/{subject}/versions/{proposal.Ordinal}",
            ApiFactory.Json);

        Assert.Equal("DISMISSED", version!.Status);

        var entries = await client.GetFromJsonAsync<IReadOnlyList<AuditResponse>>(
            $"/v1/audit?env={environment}&action=VERSION_DISMISSED", ApiFactory.Json);

        Assert.Single(entries!);
    }

    [Fact]
    public async Task ADismissedProposalCanNoLongerBeApproved()
    {
        var (client, environment) = await NewEnvironmentAsync();
        var subject = await NewSubjectAsync(client, environment);

        var proposal = await RegisterAsync(client, environment, subject, V1Breaking);
        await RegisterAsync(client, environment, subject, V1);

        var approve = await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/subjects/{subject}/versions/{proposal.Ordinal}/approve",
            new DecideVersionRequest("bob"),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Conflict, approve.StatusCode);
    }

    [Fact]
    public async Task AnIdempotentRetryWithNothingPendingWritesNoAuditEntry()
    {
        // The common case by far, and the one that would bury the trail in rows describing no
        // change if it were recorded.
        var (client, environment) = await NewEnvironmentAsync();
        var subject = await NewSubjectAsync(client, environment);

        await RegisterAsync(client, environment, subject, V1);
        await RegisterAsync(client, environment, subject, V1);

        var entries = await client.GetFromJsonAsync<IReadOnlyList<AuditResponse>>(
            $"/v1/audit?env={environment}&action=VERSION_REGISTERED", ApiFactory.Json);

        Assert.Single(entries!);
    }
}
