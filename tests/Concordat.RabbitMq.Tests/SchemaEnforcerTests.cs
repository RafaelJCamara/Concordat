using System.Text;
using Concordat.Client;
using Concordat.Domain.Messaging;
using Concordat.Domain.Registry;
using Concordat.Formats.Json;
using Concordat.RabbitMq;

namespace Concordat.RabbitMq.Tests;

/// <summary>A registry that answers whatever the test needs, without a network.</summary>
internal sealed class FakeClient : IConcordatClient
{
    private readonly Dictionary<string, CachedSchema> _schemas = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CachedLatest> _latest = new(StringComparer.Ordinal);

    public ConcordatClientStatus Status { get; } = new();

    public Task<ConcordatClientStatus> WarmUpAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Status);

    public ValueTask<CachedSchema?> GetSchemaAsync(SchemaId schemaId, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_schemas.GetValueOrDefault(schemaId.Value));

    public ValueTask<CachedLatest?> GetLatestAsync(SubjectName subject, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_latest.GetValueOrDefault(subject.Value));

    public FakeClient Register(string subject, string schemaId, string body)
    {
        var id = SchemaId.Create(schemaId).Value;
        _schemas[schemaId] = new CachedSchema(id, SchemaFormat.Json, body);
        _latest[subject] = new CachedLatest(id, 1, DateTimeOffset.UtcNow);
        return this;
    }

    /// <summary>Registers a tip whose schema cannot be fetched — a registry mid-outage.</summary>
    public FakeClient RegisterUnfetchableSchema(string subject, string schemaId)
    {
        _latest[subject] = new CachedLatest(SchemaId.Create(schemaId).Value, 1, DateTimeOffset.UtcNow);
        return this;
    }
}

public class SchemaEnforcerTests
{
    private const string SchemaIdHex = "0123456789abcdef0123456789abcdef";
    private const string Subject = "acme.orders.OrderCreated";

    private const string Schema = """
        {"type":"object","properties":{"id":{"type":"integer"}},"required":["id"]}
        """;

    private static readonly ReadOnlyMemory<byte> Conforming = "{\"id\":1}"u8.ToArray();
    private static readonly ReadOnlyMemory<byte> Violating = "{\"id\":\"not-a-number\"}"u8.ToArray();

    private static SchemaEnforcer Build(
        FakeClient? client = null, Action<ConcordatRabbitMqOptions>? configure = null)
    {
        var options = new ConcordatRabbitMqOptions();
        configure?.Invoke(options);

        return new SchemaEnforcer(
            client ?? new FakeClient().Register(Subject, SchemaIdHex, Schema),
            options,
            [new NJsonSchemaPayloadValidator()]);
    }

    private static PublishContext Publishing(string? type = Subject) =>
        new() { MessageType = type, Exchange = "orders", RoutingKey = "order.created" };

    [Fact]
    public async Task AConformingPublishGetsAnEnvelope()
    {
        var decision = await Build().InspectPublishAsync(Publishing(), Conforming);

        Assert.Equal(EnforcementOutcome.Valid, decision.Outcome);
        Assert.NotNull(decision.Envelope);
        Assert.Equal(SchemaIdHex, decision.Envelope["concordat-schema-id"]);
        Assert.Equal(Subject, decision.Envelope["concordat-subject"]);
        Assert.Equal("json", decision.Envelope["concordat-format"]);
    }

    [Fact]
    public async Task AViolatingPublishStillGetsAnEnvelope()
    {
        // The subtle one. In Monitor mode a violating message goes out anyway, and it must go
        // out carrying correct identity — that is what lets consumers start reading schema ids
        // before every publisher has been cleaned up. Withholding the envelope from exactly the
        // messages someone is trying to diagnose would be precisely backwards.
        var decision = await Build().InspectPublishAsync(Publishing(), Violating);

        Assert.Equal(EnforcementOutcome.Observed, decision.Outcome);
        Assert.Equal("payload_invalid", decision.Code);
        Assert.NotNull(decision.Envelope);
        Assert.Equal(SchemaIdHex, decision.Envelope["concordat-schema-id"]);
    }

    [Fact]
    public async Task NoMessageTypeIsUnenforcedNotRefused()
    {
        var decision = await Build().InspectPublishAsync(Publishing(type: null), Conforming);

        Assert.Equal(EnforcementOutcome.Unenforced, decision.Outcome);
        Assert.Null(decision.Envelope);
    }

    [Fact]
    public async Task RoutingDataIsNotUsedAsASubjectFallback()
    {
        // Guards the M2.3 finding at the layer where the temptation is strongest: the enforcer
        // has a perfectly good exchange and routing key in hand and must still decline.
        var decision = await Build().InspectPublishAsync(
            new PublishContext { Exchange = "orders", RoutingKey = "order.created" }, Conforming);

        Assert.Equal(EnforcementOutcome.Unenforced, decision.Outcome);
        Assert.Null(decision.Subject);
    }

    [Fact]
    public async Task AnUnknownSubjectIsUnenforcedNotAViolation()
    {
        // Availability over certainty on resolution, always. Treating "no contract registered"
        // as a violation would block every publisher the moment enforcement was switched on.
        var decision = await Build(new FakeClient()).InspectPublishAsync(Publishing(), Violating);

        Assert.Equal(EnforcementOutcome.Unenforced, decision.Outcome);
        Assert.Equal("subject_not_found", decision.Code);
    }

    [Fact]
    public async Task AResolvableSubjectWithAnUnfetchableSchemaIsUnenforced()
    {
        var client = new FakeClient().RegisterUnfetchableSchema(Subject, SchemaIdHex);

        var decision = await Build(client).InspectPublishAsync(Publishing(), Violating);

        Assert.Equal(EnforcementOutcome.Unenforced, decision.Outcome);
        Assert.Equal("schema_unresolvable", decision.Code);
    }

    [Fact]
    public async Task AnUnusableMessageTypeIsAViolationNotSilence()
    {
        // Distinct from "no type set". Someone did try, and got it wrong; that is a bug to
        // surface rather than a brownfield publisher to tolerate.
        var decision = await Build().InspectPublishAsync(Publishing("acme.order-created"), Conforming);

        Assert.Equal(EnforcementOutcome.Observed, decision.Outcome);
        Assert.Equal("subject_name_invalid", decision.Code);
    }

    [Fact]
    public async Task AConformingDeliveryIsValid()
    {
        var envelope = EnvelopeWriter.Headers(
            SchemaId.Create(SchemaIdHex).Value,
            SubjectName.Create(Subject).Value,
            1,
            null,
            SchemaFormat.Json);

        var decision = await Build().InspectConsumeAsync(
            AsHeaders(envelope), Subject, "application/json", Conforming);

        Assert.Equal(EnforcementOutcome.Valid, decision.Outcome);
        Assert.Equal(SchemaIdHex, decision.SchemaId!.Value);
    }

    [Fact]
    public async Task AViolatingDeliveryIsAViolation()
    {
        var envelope = EnvelopeWriter.Headers(
            SchemaId.Create(SchemaIdHex).Value, SubjectName.Create(Subject).Value, 1, null, SchemaFormat.Json);

        var decision = await Build().InspectConsumeAsync(
            AsHeaders(envelope), Subject, "application/json", Violating);

        Assert.Equal(EnforcementOutcome.Observed, decision.Outcome);
        Assert.Equal("payload_invalid", decision.Code);
        Assert.Contains("id", decision.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnenvelopedDeliveryIsUnenforcedNotQuarantined()
    {
        // ADR-010's whole reason for existing. Quarantining un-instrumented publishers would
        // make incremental adoption impossible: turning enforcement on would immediately
        // divert every legacy message in the estate.
        var decision = await Build().InspectConsumeAsync(null, Subject, "application/json", Violating);

        Assert.Equal(EnforcementOutcome.Unenforced, decision.Outcome);
    }

    [Fact]
    public async Task AMalformedEnvelopeIsAViolation()
    {
        var headers = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["concordat-v"] = "1"u8.ToArray(),
            ["concordat-schema-id"] = "not-a-schema-id"u8.ToArray(),
        };

        var decision = await Build().InspectConsumeAsync(headers, Subject, null, Conforming);

        Assert.Equal(EnforcementOutcome.Observed, decision.Outcome);
        Assert.Equal("schema_id_malformed", decision.Code);
    }

    [Fact]
    public async Task AnUnresolvableSchemaOnConsumeFailsOpen()
    {
        // Quarantining because the registry blinked would turn an outage into permanent
        // message displacement. The client's FailClosed setting is where that trade is made,
        // and it throws there rather than silently diverting messages here.
        var envelope = EnvelopeWriter.Headers(
            SchemaId.Create("ffffffffffffffffffffffffffffffff").Value, null, null, null, SchemaFormat.Json);

        var decision = await Build().InspectConsumeAsync(AsHeaders(envelope), null, null, Violating);

        Assert.Equal(EnforcementOutcome.Unenforced, decision.Outcome);
        Assert.Equal("schema_unresolvable", decision.Code);
    }

    [Fact]
    public async Task AnInvalidUtf8PayloadIsReportedAsEncodingNotSchema()
    {
        // Lenient decoding substitutes U+FFFD, which turns an encoding fault into a puzzling
        // schema violation and sends whoever investigates to the wrong place.
        var envelope = EnvelopeWriter.Headers(
            SchemaId.Create(SchemaIdHex).Value, SubjectName.Create(Subject).Value, 1, null, SchemaFormat.Json);

        var decision = await Build().InspectConsumeAsync(
            AsHeaders(envelope), Subject, null, new byte[] { 0xC3, 0x28 });

        Assert.Equal(EnforcementOutcome.Observed, decision.Outcome);
        Assert.Contains("not valid UTF-8", decision.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidationCanBeTurnedOffWithoutLosingTheEnvelope()
    {
        var decision = await Build(configure: o => o.ValidatePayloads = false)
            .InspectPublishAsync(Publishing(), Violating);

        Assert.Equal(EnforcementOutcome.Valid, decision.Outcome);
        Assert.NotNull(decision.Envelope);
    }

    [Fact]
    public async Task AFormatWithNoValidatorIsNotAViolation()
    {
        // Avro and Protobuf have no validator yet. Reporting every such message as invalid
        // would be a fleet-wide false positive on the day the first Avro subject is registered.
        var enforcer = new SchemaEnforcer(
            new FakeClient().Register(Subject, SchemaIdHex, Schema),
            new ConcordatRabbitMqOptions(),
            []);

        var decision = await enforcer.InspectPublishAsync(Publishing(), Violating);

        Assert.Equal(EnforcementOutcome.Valid, decision.Outcome);
    }

    private static Dictionary<string, object?> AsHeaders(IReadOnlyDictionary<string, string> envelope) =>
        // byte[], because that is how RabbitMQ.Client delivers them — measured in M2.5.
        envelope.ToDictionary(
            h => h.Key, h => (object?)Encoding.UTF8.GetBytes(h.Value), StringComparer.Ordinal);
}
