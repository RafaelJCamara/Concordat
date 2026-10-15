using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Concordat.Application.Abstractions;
using Concordat.Domain.Governance;
using Concordat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Concordat.Api.IntegrationTests;

/// <summary>
/// A row written by a build that knows more than this one must not fail every read of its table
/// (ADR-036 for the audit trail, and the same arrangement for the two tables that had a converter that
/// threw: a violation's side and a deployment event's action).
/// </summary>
/// <remarks>
/// <para>
/// Each test writes what a newer build would have written, by SQL, because nothing this build can do
/// writes it. Before the change, the list below answered <c>500</c> for as long as the row existed;
/// that was measured and not assumed (a converter that parsed the column into an enum had to throw for
/// a token it had no member for).
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public class StoredTokenToleranceTests(ApiFactory factory)
{
    private static string UniqueEnvironment() => $"env-{Guid.NewGuid():N}"[..24];

    private async Task ExecuteAsync(string sql)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ConcordatDbContext>();

        await context.Database.ExecuteSqlRawAsync(sql);
    }

    private async Task<string> ScalarTextAsync(string sql)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ConcordatDbContext>();

        return (await context.Database.SqlQueryRaw<string>(sql).ToListAsync()).Single();
    }

    private async Task<(HttpClient Client, string Environment)> NewEnvironmentAsync()
    {
        var client = factory.CreateClient();
        var name = UniqueEnvironment();

        var response = await client.PostAsJsonAsync(
            "/v1/environments", new CreateEnvironmentRequest(name), ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (client, name);
    }

    private static async Task ReportAsync(HttpClient client, string environment, string route)
    {
        var response = await client.PostAsJsonAsync(
            $"/v1/environments/{environment}/violations",
            new ReportViolationsRequest(
                "orders-api",
                [new ViolationReportRequest("PUBLISH", route, "acme.Order", "payload_invalid", "bad", 3)]),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---------------------------------------------------------------- a violation's side

    [Fact]
    public async Task AViolationWhoseSideThisBuildWasNeverTaughtIsListedAsStored_AndTheRestUnchanged()
    {
        var (client, environment) = await NewEnvironmentAsync();
        var route = $"orders/{Guid.NewGuid():N}";
        await ReportAsync(client, environment, route);

        var before = await client.GetFromJsonAsync<JsonArray>($"/v1/environments/{environment}/violations");

        // What a newer build would have recorded: the same violation, seen on a side this build has no
        // member for. Its own fingerprint, as the real one would differ in the side.
        await ExecuteAsync(
            "INSERT INTO enforcement_violation (violation_id, environment_id, side, route, subject, code, " +
            "detail, reported_by, first_seen_at, last_seen_at, occurrences, fingerprint, tenant_id) " +
            "SELECT gen_random_uuid(), environment_id, 'RELAY', route, subject, code, 'written by a newer build', " +
            "reported_by, first_seen_at, last_seen_at + interval '1 minute', occurrences, fingerprint || '|relay', tenant_id " +
            $"FROM enforcement_violation WHERE route = '{route}'");

        try
        {
            var stored = await ScalarTextAsync(
                "SELECT side || '|' || route || '|' || detail || '|' || occurrences::text || '|' || fingerprint " +
                $"AS \"Value\" FROM enforcement_violation WHERE side = 'RELAY' AND route = '{route}'");

            var response = await client.GetAsync($"/v1/environments/{environment}/violations");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var after = (await response.Content.ReadFromJsonAsync<JsonArray>())!;
            var future = Assert.Single(after, v => v!["side"]!.GetValue<string>() == "RELAY")!;
            Assert.Equal(route, future["route"]!.GetValue<string>());
            Assert.Equal("written by a newer build", future["detail"]!.GetValue<string>());

            // Every violation this build knows is what it was without the row, to the property.
            var known = new JsonArray(
                [.. after.Where(v => v!["side"]!.GetValue<string>() != "RELAY").Select(v => v!.DeepClone())]);
            Assert.True(JsonNode.DeepEquals(before, known), "the known violations changed on the wire");

            // Reading wrote nothing: the row is the row that was inserted, byte for byte.
            Assert.Equal(
                stored,
                await ScalarTextAsync(
                    "SELECT side || '|' || route || '|' || detail || '|' || occurrences::text || '|' || fingerprint " +
                    $"AS \"Value\" FROM enforcement_violation WHERE side = 'RELAY' AND route = '{route}'"));
        }
        finally
        {
            await ExecuteAsync(
                $"DELETE FROM enforcement_violation WHERE side = 'RELAY' AND route = '{route}'");
        }
    }

    [Fact]
    public async Task ReportingAKnownViolationAgain_StillFindsItsRowBesideOneOfAnUnknownSide()
    {
        // The upsert looks a row up by fingerprint and materialises it. A row of a side this build
        // does not know must neither be found by it nor break it.
        var (client, environment) = await NewEnvironmentAsync();
        var route = $"orders/{Guid.NewGuid():N}";
        await ReportAsync(client, environment, route);
        await ExecuteAsync(
            "INSERT INTO enforcement_violation (violation_id, environment_id, side, route, subject, code, " +
            "detail, reported_by, first_seen_at, last_seen_at, occurrences, fingerprint, tenant_id) " +
            "SELECT gen_random_uuid(), environment_id, 'RELAY', route, subject, code, detail, reported_by, " +
            "first_seen_at, last_seen_at, occurrences, fingerprint || '|relay', tenant_id " +
            $"FROM enforcement_violation WHERE route = '{route}'");

        try
        {
            await ReportAsync(client, environment, route);

            var list = await client.GetFromJsonAsync<JsonArray>($"/v1/environments/{environment}/violations");

            var publish = Assert.Single(list!, v => v!["side"]!.GetValue<string>() == "PUBLISH")!;
            Assert.Equal(6, publish["occurrences"]!.GetValue<long>());
            Assert.Equal(2, list!.Count);
        }
        finally
        {
            await ExecuteAsync(
                $"DELETE FROM enforcement_violation WHERE side = 'RELAY' AND route = '{route}'");
        }
    }

    [Fact]
    public async Task TheOverviewStillCountsARowOfAnUnknownSide()
    {
        var (client, environment) = await NewEnvironmentAsync();
        var route = $"orders/{Guid.NewGuid():N}";
        await ReportAsync(client, environment, route);
        await ExecuteAsync(
            "INSERT INTO enforcement_violation (violation_id, environment_id, side, route, subject, code, " +
            "detail, reported_by, first_seen_at, last_seen_at, occurrences, fingerprint, tenant_id) " +
            "SELECT gen_random_uuid(), environment_id, 'RELAY', route, subject, code, detail, reported_by, " +
            "first_seen_at, last_seen_at, occurrences, fingerprint || '|relay', tenant_id " +
            $"FROM enforcement_violation WHERE route = '{route}'");

        try
        {
            var summary = await client.GetFromJsonAsync<EnvironmentSummaryResponse>(
                $"/v1/environments/{environment}/summary", ApiFactory.Json);

            Assert.Equal(2, summary!.Violations.Distinct);
        }
        finally
        {
            await ExecuteAsync(
                $"DELETE FROM enforcement_violation WHERE side = 'RELAY' AND route = '{route}'");
        }
    }

    // --------------------------------------------------------------- a deployment event's action

    [Fact]
    public async Task ADeploymentEventWhoseActionThisBuildWasNeverTaughtIsReadAsStored()
    {
        // Nothing reads this table over HTTP today (there is no operator role to gate it with), so the
        // read is the log's own, which is what the day somebody adds a route will call.
        var marker = $"newer-build-{Guid.NewGuid():N}";

        await ExecuteAsync(
            "INSERT INTO deployment_event (deployment_event_id, action, actor, tenant_id, detail, occurred_at) " +
            $"VALUES (gen_random_uuid(), 'SUSPENDED', '{marker}', NULL, 'written by a newer build', now() + interval '1 day')");

        try
        {
            using var scope = factory.Services.CreateScope();
            var log = scope.ServiceProvider.GetRequiredService<IDeploymentLog>();

            var events = await log.ReadAsync(50, CancellationToken.None);

            var future = Assert.Single(events, e => e.Actor == marker);
            Assert.Equal("SUSPENDED", future.ActionToken);
            Assert.Null(future.Action);
        }
        finally
        {
            await ExecuteAsync($"DELETE FROM deployment_event WHERE actor = '{marker}'");
        }
    }

    [Fact]
    public async Task EventsThisBuildKnowStillResolve_BesideARowItDoesNot()
    {
        var marker = $"newer-build-{Guid.NewGuid():N}";

        await ExecuteAsync(
            "INSERT INTO deployment_event (deployment_event_id, action, actor, tenant_id, detail, occurred_at) " +
            $"VALUES (gen_random_uuid(), 'SUSPENDED', '{marker}', NULL, 'written by a newer build', now()), " +
            $"(gen_random_uuid(), 'INSTANCE_CLAIMED', '{marker}', NULL, 'claimed', now())");

        try
        {
            using var scope = factory.Services.CreateScope();
            var log = scope.ServiceProvider.GetRequiredService<IDeploymentLog>();

            var events = (await log.ReadAsync(50, CancellationToken.None))
                .Where(e => e.Actor == marker)
                .ToList();

            Assert.Equal(2, events.Count);
            Assert.Equal(
                DeploymentAction.InstanceClaimed, events.Single(e => e.ActionToken == "INSTANCE_CLAIMED").Action);
        }
        finally
        {
            await ExecuteAsync($"DELETE FROM deployment_event WHERE actor = '{marker}'");
        }
    }
}
