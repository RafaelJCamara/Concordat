using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Concordat.Client;
using Concordat.Domain.Messaging;
using Concordat.Domain.Registry;
using Concordat.Formats.Abstractions;
using Concordat.Formats.Json;
using Concordat.RabbitMq;
using RabbitMQ.Client;

namespace Concordat.EndToEnd;

/// <summary>
/// A publisher and a consumer, against a real registry and a real broker.
/// </summary>
/// <remarks>
/// Every object here is the one a user would construct. Where a test needs to know something
/// the SDK does not expose — whether a header arrived, what the quarantine exchange received —
/// it asks the broker, not Concordat.
/// </remarks>
[Collection(StackCollection.Name)]
public sealed class PublishAndConsumeTests(StackFixture stack) : IAsyncLifetime
{
    private const string Environment = "dev";

    private const string Schema =
        """
        {
          "type": "object",
          "properties": { "orderId": { "type": "string" }, "total": { "type": "number" } },
          "required": ["orderId"]
        }
        """;

    private IConnection _connection = null!;

    public async Task InitializeAsync() =>
        _connection = await new ConnectionFactory
        {
            HostName = stack.BrokerHost,
            Port = stack.BrokerPort,
        }.CreateConnectionAsync();

    public Task DisposeAsync() => _connection.DisposeAsync().AsTask();

    // ------------------------------------------------------------------ the happy path

    [Fact]
    public async Task AValidMessageIsPublishedAndCarriesItsEnvelope()
    {
        var subject = await RegisterAsync("orders.Valid");
        var (channel, queue) = await TopologyAsync(subject, EnforcementMode.Enforce);

        await PublishAsync(channel, subject, new { orderId = "ord-1", total = 42.5 });

        var delivery = await GetAsync(queue);
        Assert.NotNull(delivery);

        // The envelope is what makes a consumer able to name the exact schema that validated
        // this message without asking anyone. The id is content-addressed, so it is also the
        // id the registry holds -- asserted rather than assumed.
        Assert.Equal(subject.SchemaId, Header(delivery, EnvelopeHeaders.SchemaId));
        Assert.Equal(subject.Name, Header(delivery, EnvelopeHeaders.Subject));
        Assert.Equal(WireTokens.FormatJson, Header(delivery, EnvelopeHeaders.Format));
    }

    [Fact]
    public async Task AnInvalidMessageIsRefusedBeforeItReachesTheBroker()
    {
        var subject = await RegisterAsync("orders.Refused");
        var (channel, queue) = await TopologyAsync(subject, EnforcementMode.Enforce);

        var refusal = await Assert.ThrowsAsync<ConcordatViolationException>(
            () => PublishAsync(channel, subject, new { orderId = "ord-2", total = "not-a-number" }));

        // The path matters as much as the refusal. "Validation failed" is the failure mode
        // this product exists to improve on.
        Assert.Contains("#/total", refusal.Message, StringComparison.Ordinal);

        // And nothing was emitted: under Enforce the bad message never exists, so no consumer
        // downstream has to cope with it.
        Assert.Null(await GetAsync(queue));
    }

    [Fact]
    public async Task MonitorPublishesTheSameMessageItWouldHaveRefused()
    {
        // The default mode, and the reason it is the default: adding a package reference must
        // never start rejecting production traffic.
        var subject = await RegisterAsync("orders.Monitored");
        var (channel, queue) = await TopologyAsync(subject, EnforcementMode.Monitor);

        await PublishAsync(channel, subject, new { orderId = "ord-3", total = "not-a-number" });

        Assert.NotNull(await GetAsync(queue));
    }

    // ------------------------------------------------------- the client against the registry

    [Fact]
    public async Task TheClientResolvesASchemaItWasNeverToldAbout()
    {
        // The seam this suite exists for: a real ConcordatClient fetching over HTTP from the
        // real registry, with no fixture priming its cache.
        var subject = await RegisterAsync("orders.Resolvable");
        var client = NewClient();

        var latest = await client.GetLatestAsync(SubjectName.Create(subject.Name).Value);

        Assert.NotNull(latest);
        Assert.Equal(subject.SchemaId, latest.SchemaId.Value);

        var schema = await client.GetSchemaAsync(latest.SchemaId);
        Assert.NotNull(schema);

        // Canonical text, not the text that was posted. Whitespace and key order are gone.
        Assert.DoesNotContain("\n", schema.CanonicalBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WarmUpLoadsTheEnvironmentInOneRequest()
    {
        await RegisterAsync("orders.Warmed");
        var client = NewClient();

        var status = await client.WarmUpAsync();

        Assert.True(status.IsWarm);
        Assert.True(status.SubjectsLoaded > 0);
        Assert.True(status.SchemasLoaded > 0);

        // Nothing failed to resolve. A non-zero count here is how fail-open enforcement dying
        // quietly becomes visible instead of being discovered a quarter later.
        Assert.Equal(0, status.ResolutionFailures);
    }

    [Fact]
    public async Task AnUnknownSubjectResolvesToNothingRatherThanThrowing()
    {
        var client = NewClient();

        var latest = await client.GetLatestAsync(SubjectName.Create("orders.NeverRegistered").Value);

        // Absent is not an error. A brownfield estate is full of message types nobody has
        // registered yet, and a client that threw would make adoption all-or-nothing.
        Assert.Null(latest);
    }

    // ------------------------------------------------------------------------ consuming

    [Fact]
    public async Task AConsumerValidatesWhatItReceives()
    {
        var subject = await RegisterAsync("orders.Consumed");
        var (channel, queue) = await TopologyAsync(subject, EnforcementMode.Enforce);

        await PublishAsync(channel, subject, new { orderId = "ord-4", total = 1 });

        var delivery = await GetAsync(queue);
        Assert.NotNull(delivery);

        var enforcer = NewEnforcer(EnforcementMode.Enforce);

        var decision = await enforcer.InspectConsumeAsync(
            delivery.BasicProperties.Headers?.ToDictionary(
                h => h.Key, h => h.Value, StringComparer.Ordinal),
            delivery.BasicProperties.Type,
            delivery.BasicProperties.ContentType,
            delivery.Body);

        // Valid, not merely delivered: the consumer resolved the schema the publisher stamped
        // and checked the payload against it. Unenforced here would mean the envelope round
        // trip silently failed and nobody noticed.
        Assert.Equal(EnforcementOutcome.Valid, decision.Outcome);
    }

    // ------------------------------------------------------------------------- helpers

    private sealed record RegisteredSubject(string Name, string SchemaId);

    private async Task<RegisteredSubject> RegisterAsync(string name)
    {
        var http = stack.CreateClient();
        var qualified = $"acme.{name}";

        var created = await http.PostAsJsonAsync(
            $"/v1/environments/{Environment}/subjects",
            new { name = qualified, format = "json", owner = "e2e" });

        created.EnsureSuccessStatusCode();

        var registered = await http.PostAsJsonAsync(
            $"/v1/environments/{Environment}/subjects/{qualified}/versions",
            new { schema = Schema, registeredBy = "e2e" });

        registered.EnsureSuccessStatusCode();

        using var body = JsonDocument.Parse(await registered.Content.ReadAsStringAsync());

        return new RegisteredSubject(
            qualified, body.RootElement.GetProperty("schemaId").GetString()!);
    }

    private ConcordatClient NewClient() =>
        new(
            stack.CreateClient(),
            new ConcordatClientOptions
            {
                BaseAddress = new Uri("http://localhost"),
                Environment = Environment,
            });

    private SchemaEnforcer NewEnforcer(EnforcementMode mode) =>
        new(
            NewClient(),
            new ConcordatRabbitMqOptions { Mode = mode },
            [new NJsonSchemaPayloadValidator()]);

    private async Task<(ConcordatChannel Channel, string Queue)> TopologyAsync(
        RegisteredSubject subject, EnforcementMode mode)
    {
        var raw = await _connection.CreateChannelAsync();
        var queue = $"e2e.{subject.Name}";

        await raw.QueueDeclareAsync(queue, durable: false, exclusive: false, autoDelete: false);

        var options = new ConcordatRabbitMqOptions { Mode = mode };
        var enforcer = new SchemaEnforcer(NewClient(), options, [new NJsonSchemaPayloadValidator()]);

        return (new ConcordatChannel(raw, enforcer, options), queue);
    }

    private static Task PublishAsync(ConcordatChannel channel, RegisteredSubject subject, object payload) =>
        channel.BasicPublishAsync(
            exchange: string.Empty,
            routingKey: $"e2e.{subject.Name}",
            mandatory: false,
            new BasicProperties { Type = subject.Name, ContentType = "application/json" },
            JsonSerializer.SerializeToUtf8Bytes(payload)).AsTask();

    private async Task<BasicGetResult?> GetAsync(string queue)
    {
        using var raw = await _connection.CreateChannelAsync();
        return await raw.BasicGetAsync(queue, autoAck: true);
    }

    private static string? Header(BasicGetResult delivery, string name) =>
        delivery.BasicProperties.Headers is { } headers &&
        headers.TryGetValue(name, out var value) && value is byte[] bytes
            ? Encoding.UTF8.GetString(bytes)
            : null;
}
