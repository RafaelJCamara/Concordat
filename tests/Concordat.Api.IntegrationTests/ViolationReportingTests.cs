using System.Net;
using System.Net.Http.Json;
using Concordat.Application.Registry;
using Concordat.Client;
using Concordat.Domain.Governance;
using Concordat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Concordat.Api.IntegrationTests;

/// <summary>
/// Client-reported enforcement violations, end to end (decision 25).
/// </summary>
/// <remarks>
/// `ENFORCEMENT_VIOLATION` sat in the published notification catalogue from M7.5 with nothing
/// able to raise it: the violation happens in the SDK, against a contract the registry never
/// sees the traffic for. A subscriber could subscribe to silence.
/// </remarks>
[Collection(ApiCollection.Name)]
public class ViolationReportingTests(ApiFactory factory)
{
    private static string UniqueEnvironment() => $"env-{Guid.CreateVersion7():N}"[..24];

    private async Task<(HttpClient Http, string Environment)> NewEnvironmentAsync()
    {
        var http = factory.CreateClient();
        var environment = UniqueEnvironment();

        var created = await http.PostAsJsonAsync(
            "/v1/environments", new CreateEnvironmentRequest(environment), ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (http, environment);
    }

    private static ReportViolationsRequest Batch(params ViolationReportRequest[] reports) =>
        new("orders-api", reports);

    private static ViolationReportRequest Report(
        string route = "orders/order.created", long occurrences = 1, string? subject = "acme.Order") =>
        new("PUBLISH", route, subject, "payload_invalid", "#/total: NumberExpected", occurrences);

    [Fact]
    public async Task AReportedViolationIsRecordedAndFiresTheNotificationOnce()
    {
        var (http, environment) = await NewEnvironmentAsync();

        var first = await http.PostAsJsonAsync(
            $"/v1/environments/{environment}/violations", Batch(Report(occurrences: 3)), ApiFactory.Json);

        var accepted = await ApiFactory.ReadAsync<ViolationsAcceptedResponse>(first);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(1, accepted.Accepted);
        Assert.Equal(1, accepted.Opened);

        // The same violation again. It must not open a second row, and it must not fire a second
        // notification -- "this started happening" is the alert, "this is still happening" is the
        // counter, and paging somebody every reporting window is how alerting stops being read.
        var second = await http.PostAsJsonAsync(
            $"/v1/environments/{environment}/violations", Batch(Report(occurrences: 7)), ApiFactory.Json);

        var again = await ApiFactory.ReadAsync<ViolationsAcceptedResponse>(second);

        Assert.Equal(1, again.Accepted);
        Assert.Equal(0, again.Opened);

        var listed = await ApiFactory.ReadAsync<List<ViolationResponse>>(
            await http.GetAsync($"/v1/environments/{environment}/violations"));

        var only = Assert.Single(listed);
        Assert.Equal(10, only.Occurrences);
        Assert.Equal("orders-api", only.ReportedBy);
        Assert.Equal("PUBLISH", only.Side);
        Assert.True(only.LastSeenAt >= only.FirstSeenAt);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ConcordatDbContext>();

        var staged = await context.Outbox
            .AsNoTracking()
            .Where(m => m.Event == NotificationEvent.EnforcementViolation)
            .Where(m => m.Target == "orders/order.created")
            .CountAsync();

        Assert.Equal(1, staged);
    }

    [Fact]
    public async Task DistinctRoutesAreDistinctViolations()
    {
        var (http, environment) = await NewEnvironmentAsync();

        var response = await http.PostAsJsonAsync(
            $"/v1/environments/{environment}/violations",
            Batch(Report(route: "orders/order.created"), Report(route: "orders/order.shipped")),
            ApiFactory.Json);

        var accepted = await ApiFactory.ReadAsync<ViolationsAcceptedResponse>(response);

        Assert.Equal(2, accepted.Opened);
    }

    [Fact]
    public async Task AMalformedReportIsDroppedAndTheGoodOnesInTheBatchSurvive()
    {
        // This runs fire-and-forget from every publisher in the estate. Failing the whole batch
        // over one bad entry would lose the good ones with it, and nobody is reading the status
        // code -- so the count is the only way a malformed reporter is ever visible.
        var (http, environment) = await NewEnvironmentAsync();

        var response = await http.PostAsJsonAsync(
            $"/v1/environments/{environment}/violations",
            Batch(
                Report(),
                new ViolationReportRequest("SIDEWAYS", "orders/x", null, "payload_invalid", "d", 1),
                new ViolationReportRequest("PUBLISH", null, null, "payload_invalid", "d", 1),
                new ViolationReportRequest("PUBLISH", "orders/y", null, "payload_invalid", "d", 0)),
            ApiFactory.Json);

        var accepted = await ApiFactory.ReadAsync<ViolationsAcceptedResponse>(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, accepted.Accepted);
        Assert.Equal(3, accepted.Rejected);
    }

    [Fact]
    public async Task TheSdkReportsWhatItCountedAndKeepsTheRegistryOffTheDeliveryPath()
    {
        var (http, environment) = await NewEnvironmentAsync();

        var options = new ConcordatClientOptions
        {
            BaseAddress = http.BaseAddress,
            Environment = environment,
            WarmUpJitter = TimeSpan.Zero,
            ServiceName = "orders-api",
        };

        using var sdk = new ConcordatClient(http, options);

        var key = new ViolationKey(
            "PUBLISH", "orders/order.created", "acme.Order", "payload_invalid", "#/total");

        // Recording is what happens on the hot path: no I/O, however many messages hit it.
        for (var i = 0; i < 500; i++)
        {
            sdk.RecordViolation(key);
        }

        Assert.Equal(1, sdk.Violations.Pending);

        var sent = await sdk.FlushViolationsAsync();
        Assert.Equal(1, sent);

        var listed = await ApiFactory.ReadAsync<List<ViolationResponse>>(
            await http.GetAsync($"/v1/environments/{environment}/violations"));

        Assert.Equal(500, Assert.Single(listed).Occurrences);
    }

    [Fact]
    public async Task AClientWithNoServiceNameReportsNothing()
    {
        // Same opt-in rule as service registration: a table of violations reported by "unknown"
        // names a problem and nobody to talk to about it.
        var (http, environment) = await NewEnvironmentAsync();

        using var sdk = new ConcordatClient(http, new ConcordatClientOptions
        {
            BaseAddress = http.BaseAddress,
            Environment = environment,
            WarmUpJitter = TimeSpan.Zero,
        });

        sdk.RecordViolation(new ViolationKey("PUBLISH", "orders/x", null, "payload_invalid", "d"));

        Assert.Equal(0, await sdk.FlushViolationsAsync());
    }
}
