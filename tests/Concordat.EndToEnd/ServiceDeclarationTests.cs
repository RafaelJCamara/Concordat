using System.Net.Http.Json;
using System.Text.Json;
using Concordat.Client;

namespace Concordat.EndToEnd;

/// <summary>
/// The M7.4 loop closed end to end: an SDK declares itself at startup, and impact analysis
/// answers with the name it declared.
/// </summary>
/// <remarks>
/// The two halves were built a layer apart and are only useful together. A registry that
/// accepts service declarations nobody sends, or an SDK that reports intent nothing reads, each
/// pass their own tests and deliver nothing.
/// </remarks>
[Collection(StackCollection.Name)]
public sealed class ServiceDeclarationTests(StackFixture stack)
{
    private const string Environment = "governed";

    private const string V1 =
        """{"type":"object","properties":{"orderId":{"type":"string"}}}""";

    private const string TypeChanged =
        """{"type":"object","properties":{"orderId":{"type":"integer"}}}""";

    private HttpClient Http => stack.CreateClient();

    private static string Unique() => $"acme.e2e.S{Guid.CreateVersion7():N}";

    private async Task EnsureEnvironmentAsync()
    {
        // Promotion and impact read the environment's compatibility policy, which a derived id
        // does not have. Registration alone never needed a real row.
        var response = await Http.PostAsJsonAsync(
            "/v1/environments", new { name = Environment });

        // Already there from a sibling test in this collection: that is fine, not a failure.
        if (response.StatusCode is not System.Net.HttpStatusCode.Conflict)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    private async Task<string> RegisterAsync(string body = V1)
    {
        await EnsureEnvironmentAsync();

        var name = Unique();
        var http = Http;

        (await http.PostAsJsonAsync(
            $"/v1/environments/{Environment}/subjects",
            new { name, format = "json", owner = "e2e" })).EnsureSuccessStatusCode();

        (await http.PostAsJsonAsync(
            $"/v1/environments/{Environment}/subjects/{name}/versions",
            new { schema = body, registeredBy = "e2e" })).EnsureSuccessStatusCode();

        return name;
    }

    private ConcordatClient NewClient(Action<ConcordatClientOptions>? configure = null)
    {
        var options = new ConcordatClientOptions
        {
            BaseAddress = new Uri("http://localhost"),
            Environment = Environment,
            WarmUpJitter = TimeSpan.Zero,
        };

        configure?.Invoke(options);
        return new ConcordatClient(stack.CreateClient(), options);
    }

    private async Task<JsonDocument> ImpactAsync(string subject, string candidate)
    {
        var response = await Http.PostAsJsonAsync(
            $"/v1/environments/{Environment}/subjects/{subject}/impact",
            new { schema = candidate });

        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AnSdkThatDeclaresItselfIsNamedByImpactAnalysis()
    {
        var subject = await RegisterAsync();

        var client = NewClient(o =>
        {
            o.ServiceName = "orders-reader";
            o.Consumes.Add($"{subject}@1");
        });

        var status = await client.WarmUpAsync();
        Assert.True(status.IsWarm);

        using var impact = await ImpactAsync(subject, TypeChanged);
        var consumers = impact.RootElement.GetProperty("consumers");

        Assert.Equal(1, impact.RootElement.GetProperty("breakingCount").GetInt32());
        Assert.Equal("orders-reader", consumers[0].GetProperty("service").GetString());
        Assert.True(consumers[0].GetProperty("breaks").GetBoolean());
        Assert.Equal("CHECKED", consumers[0].GetProperty("certainty").GetString());
    }

    [Fact]
    public async Task AClientWithNoServiceNameDeclaresNothing()
    {
        // Opt-in on purpose. A machine name or a process id would fill the service table with
        // rows nobody recognises, which is worse for impact analysis than an empty table.
        var subject = await RegisterAsync();

        var status = await NewClient().WarmUpAsync();
        Assert.True(status.IsWarm);

        using var impact = await ImpactAsync(subject, TypeChanged);

        Assert.Empty(impact.RootElement.GetProperty("consumers").EnumerateArray());
    }

    [Fact]
    public async Task ABareSubjectNameDeclaresLatest()
    {
        var subject = await RegisterAsync();

        var client = NewClient(o =>
        {
            o.ServiceName = $"follower-{Guid.CreateVersion7():N}"[..20];
            o.Consumes.Add(subject);
        });

        await client.WarmUpAsync();

        using var impact = await ImpactAsync(subject, TypeChanged);
        var consumer = impact.RootElement.GetProperty("consumers")[0];

        Assert.Equal("latest", consumer.GetProperty("selector").GetString());

        // And so the registry cannot say whether it breaks, which is the cost of not pinning.
        Assert.Equal("FOLLOWS_LATEST", consumer.GetProperty("certainty").GetString());
    }

    [Fact]
    public async Task WarmingUpTwiceLeavesOneServiceRow()
    {
        // Every restart re-declares. A fleet of fifty pods is one row, or impact analysis
        // reports fifty affected consumers where there is one.
        var subject = await RegisterAsync();
        var name = $"repeat-{Guid.CreateVersion7():N}"[..20];

        for (var i = 0; i < 3; i++)
        {
            var client = NewClient(o =>
            {
                o.ServiceName = name;
                o.Consumes.Add($"{subject}@1");
            });

            await client.WarmUpAsync();
        }

        using var impact = await ImpactAsync(subject, TypeChanged);

        Assert.Single(impact.RootElement.GetProperty("consumers").EnumerateArray());
    }

    [Fact]
    public async Task AFailedDeclarationDoesNotStopTheClientStarting()
    {
        // Declaring intent is bookkeeping for someone else's benefit. A service that would not
        // start because it was refused has put the registry back on the critical path, which is
        // the failure mode warm-up exists to avoid.
        await RegisterAsync();

        var client = NewClient(o => o.ServiceName = "not a legal service name");

        var status = await client.WarmUpAsync();

        Assert.True(status.IsWarm);
        Assert.True(status.SubjectsLoaded > 0);
    }

    [Fact]
    public async Task RequireServiceRegistrationTurnsThatIntoAFailure()
    {
        await RegisterAsync();

        var client = NewClient(o =>
        {
            o.ServiceName = "not a legal service name";
            o.RequireServiceRegistration = true;
        });

        var thrown = await Assert.ThrowsAsync<ConcordatException>(() => client.WarmUpAsync());

        Assert.Equal("service_name_invalid", thrown.Code);
    }
}
