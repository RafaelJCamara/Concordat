using System.Text;
using Concordat.Client;
using Concordat.Domain.Contracts;
using Concordat.Domain.Messaging;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;
using Concordat.Formats.Json;
using Concordat.RabbitMq;

namespace Concordat.RabbitMq.Tests;

/// <summary>A registry that answers whatever the test needs, without a network.</summary>
internal sealed class FakeClient : IConcordatClient
{
    private readonly Dictionary<string, CachedSchema> _schemas = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CachedLatest> _latest = new(StringComparer.Ordinal);
    private readonly Dictionary<PublishRoute, ResolvedRoute> _publishRoutes = [];
    private readonly Dictionary<string, ResolvedRoute> _consumeRoutes = new(StringComparer.Ordinal);

    public ConcordatClientStatus Status { get; } = new();

    public Task<ConcordatClientStatus> WarmUpAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Status);

    public ValueTask<CachedSchema?> GetSchemaAsync(SchemaId schemaId, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_schemas.GetValueOrDefault(schemaId.Value));

    public ValueTask<CachedLatest?> GetLatestAsync(SubjectName subject, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_latest.GetValueOrDefault(subject.Value));

    public ValueTask<ResolvedRoute> GetPublishRouteAsync(
        PublishRoute route, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(
            _publishRoutes.GetValueOrDefault(route) ?? ResolvedRoute.Ungoverned(DateTimeOffset.UtcNow));

    public ValueTask<ResolvedRoute> GetConsumeRouteAsync(
        string queue, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(
            _consumeRoutes.GetValueOrDefault(queue) ?? ResolvedRoute.Ungoverned(DateTimeOffset.UtcNow));

    public FakeClient Register(string subject, string schemaId, string body, int ordinal = 1)
    {
        var id = SchemaId.Create(schemaId).Value;
        _schemas[schemaId] = new CachedSchema(id, SchemaFormat.Json, body);
        _latest[subject] = new CachedLatest(id, ordinal, DateTimeOffset.UtcNow);
        return this;
    }

    /// <summary>Registers a tip whose schema cannot be fetched — a registry mid-outage.</summary>
    public FakeClient RegisterUnfetchableSchema(string subject, string schemaId)
    {
        _latest[subject] = new CachedLatest(SchemaId.Create(schemaId).Value, 1, DateTimeOffset.UtcNow);
        return this;
    }

    /// <summary>Puts a contract over a publish route.</summary>
    public FakeClient Governs(
        string exchange,
        string routingKey,
        string contract,
        EnforcementMode enforcement,
        params string[] subjects)
    {
        _publishRoutes[new PublishRoute(exchange, routingKey)] =
            new ResolvedRoute(contract, enforcement, Parse(subjects), DateTimeOffset.UtcNow);

        return this;
    }

    /// <summary>Puts a contract over a queue.</summary>
    public FakeClient GovernsQueue(
        string queue, string contract, EnforcementMode enforcement, params string[] subjects)
    {
        _consumeRoutes[queue] =
            new ResolvedRoute(contract, enforcement, Parse(subjects), DateTimeOffset.UtcNow);

        return this;
    }

    /// <summary>Reads <c>subject@selector</c>, defaulting a bare subject to <c>latest</c>.</summary>
    private static List<SubjectRef> Parse(IEnumerable<string> entries)
    {
        var refs = new List<SubjectRef>();

        foreach (var entry in entries)
        {
            var at = entry.LastIndexOf('@');
            var name = at < 0 ? entry : entry[..at];
            var selector = at < 0 ? "latest" : entry[(at + 1)..];

            refs.Add(new SubjectRef(
                SubjectName.Create(name).Value, VersionSelector.Parse(selector).Value));
        }

        return refs;
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

    [Fact]
    public async Task ADeclaredFormatThatContradictsTheRegistryIsAViolation()
    {
        // `envelope_format_mismatch` was in the published code catalogue with nothing emitting
        // it — the check it names was specified and never written. The schema id is
        // content-addressed so the registry wins and validation is unaffected; what this catches
        // is a producer and a registry disagreeing about what was sent.
        var envelope = new Dictionary<string, object?>(AsHeaders(
            EnvelopeWriter.Headers(
                SchemaId.Create(SchemaIdHex).Value,
                SubjectName.Create(Subject).Value,
                1,
                null,
                SchemaFormat.Avro)),
            StringComparer.Ordinal);

        var decision = await Build().InspectConsumeAsync(envelope, Subject, null, Conforming);

        Assert.Equal(EnforcementOutcome.Observed, decision.Outcome);
        Assert.Equal(ConcordatCodes.EnvelopeFormatMismatch, decision.Code);
        Assert.Contains("'avro'", decision.Detail, StringComparison.Ordinal);
        Assert.Contains("'json'", decision.Detail, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------- contracts (M7.3)

    [Fact]
    public async Task AnUngovernedRouteFallsBackToTheClientsOwnMode()
    {
        var decision = await Build(configure: o => o.Mode = EnforcementMode.Enforce)
            .InspectPublishAsync(Publishing(), Conforming);

        Assert.Null(decision.Contract);
        Assert.Equal(EnforcementMode.Enforce, decision.EffectiveMode);
    }

    [Fact]
    public async Task AGoverningContractOverridesTheClientUpwards()
    {
        var client = new FakeClient()
            .Register(Subject, SchemaIdHex, Schema)
            .Governs("orders", "order.created", "orders-v1", EnforcementMode.Enforce, Subject);

        // The client asked for Monitor. The operator promoted the contract to ENFORCE, which is
        // the entire reason enforcement lives in the registry rather than in each deployment.
        var decision = await Build(client, o => o.Mode = EnforcementMode.Monitor)
            .InspectPublishAsync(Publishing(), Conforming);

        Assert.Equal("orders-v1", decision.Contract);
        Assert.Equal(EnforcementMode.Enforce, decision.EffectiveMode);
    }

    [Fact]
    public async Task AGoverningContractOverridesTheClientDownwards()
    {
        var client = new FakeClient()
            .Register(Subject, SchemaIdHex, Schema)
            .Governs("orders", "order.created", "orders-v1", EnforcementMode.Off, Subject);

        // THE CENTRAL OFF SWITCH, AND THE REASON 'STRICTER OF THE TWO' WAS REJECTED.
        //
        // Under a stricter-wins rule this service would keep enforcing after the operator had
        // switched the contract off, because it happens to be configured Enforce locally. An off
        // switch that does not switch anything off is worse than none, because it is believed.
        var decision = await Build(client, o => o.Mode = EnforcementMode.Enforce)
            .InspectPublishAsync(Publishing(), Violating);

        Assert.Equal(EnforcementMode.Off, decision.EffectiveMode);
        Assert.Equal(EnforcementOutcome.Unenforced, decision.Outcome);
    }

    [Fact]
    public async Task AGovernedRouteInOffIsDistinguishableFromAnUngovernedOne()
    {
        var governed = new FakeClient()
            .Register(Subject, SchemaIdHex, Schema)
            .Governs("orders", "order.created", "orders-v1", EnforcementMode.Off, Subject);

        var off = await Build(governed).InspectPublishAsync(Publishing(), Violating);
        var ungoverned = await Build().InspectPublishAsync(Publishing(), Violating);

        // Both are "not enforcing", and they are not the same thing: one is an operator's
        // decision, the other is nobody having written a contract yet. Collapsing them would make
        // the off switch indistinguishable from its own absence.
        Assert.Equal("orders-v1", off.Contract);
        Assert.Null(ungoverned.Contract);
        Assert.Equal(EnforcementOutcome.Unenforced, off.Outcome);
        Assert.Equal(EnforcementOutcome.Observed, ungoverned.Outcome);
    }

    [Fact]
    public async Task ASingleSubjectContractSuppliesTheSubjectAPublisherOmitted()
    {
        var client = new FakeClient()
            .Register(Subject, SchemaIdHex, Schema)
            .Governs("orders", "order.created", "orders-v1", EnforcementMode.Monitor, Subject);

        // No properties.type at all — the un-instrumented publisher that was previously
        // unenforceable. The contract knows what belongs on this route, so it is enforceable now
        // without anyone touching the publisher's code.
        var decision = await Build(client).InspectPublishAsync(Publishing(type: null), Conforming);

        Assert.Equal(EnforcementOutcome.Valid, decision.Outcome);
        Assert.Equal(Subject, decision.Subject?.Value);
        Assert.NotNull(decision.Envelope);
    }

    [Fact]
    public async Task AMultiSubjectContractWillNotGuessTheSubject()
    {
        var client = new FakeClient()
            .Register(Subject, SchemaIdHex, Schema)
            .Governs(
                "orders", "order.created", "orders-v1", EnforcementMode.Monitor,
                Subject, "acme.orders.OrderAmended");

        var decision = await Build(client).InspectPublishAsync(Publishing(type: null), Conforming);

        Assert.Equal(EnforcementOutcome.Unenforced, decision.Outcome);
        Assert.Equal(ConcordatCodes.ContractSubjectAmbiguous, decision.Code);
    }

    [Fact]
    public async Task PublishingASubjectTheRouteDoesNotPermitIsAViolation()
    {
        var client = new FakeClient()
            .Register(Subject, SchemaIdHex, Schema)
            .Governs(
                "orders", "order.created", "orders-v1", EnforcementMode.Monitor,
                "acme.orders.OrderAmended");

        // A perfectly valid payload, correctly identified, on the wrong route. Schema validation
        // alone can never catch this: it never sees the topology.
        var decision = await Build(client).InspectPublishAsync(Publishing(), Conforming);

        Assert.Equal(EnforcementOutcome.Observed, decision.Outcome);
        Assert.Equal(ConcordatCodes.ContractSubjectNotPermitted, decision.Code);
        Assert.Contains("acme.orders.OrderAmended@latest", decision.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARouteViolationCarriesNoEnvelopeButAPayloadViolationDoes()
    {
        var wrongRoute = new FakeClient()
            .Register(Subject, SchemaIdHex, Schema)
            .Governs(
                "orders", "order.created", "orders-v1", EnforcementMode.Monitor,
                "acme.orders.OrderAmended");

        var route = await Build(wrongRoute).InspectPublishAsync(Publishing(), Conforming);
        var payload = await Build().InspectPublishAsync(Publishing(), Violating);

        // An envelope asserts "this is schema X, sent here deliberately". Stamping one on a
        // message the contract does not accept would put a claim on the wire that the registry
        // itself contradicts. A bad payload on the right route has no such problem, and stamping
        // it is what lets consumers start reading schema ids before publishers are clean.
        Assert.Null(route.Envelope);
        Assert.NotNull(payload.Envelope);
    }

    [Fact]
    public async Task APinnedBindingRefusesAVersionAheadOfIt()
    {
        var client = new FakeClient()
            .Register(Subject, SchemaIdHex, Schema, ordinal: 7)
            .Governs(
                "orders", "order.created", "orders-v1", EnforcementMode.Monitor, $"{Subject}@3");

        var decision = await Build(client).InspectPublishAsync(Publishing(), Conforming);

        Assert.Equal(EnforcementOutcome.Observed, decision.Outcome);
        Assert.Equal(ConcordatCodes.ContractVersionNotPermitted, decision.Code);
        Assert.Contains("version is 7", decision.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARangeBindingAcceptsAnythingAtOrAboveItsFloor()
    {
        var client = new FakeClient()
            .Register(Subject, SchemaIdHex, Schema, ordinal: 7)
            .Governs(
                "orders", "order.created", "orders-v1", EnforcementMode.Monitor, $"{Subject}@>=2");

        var decision = await Build(client).InspectPublishAsync(Publishing(), Conforming);

        Assert.Equal(EnforcementOutcome.Valid, decision.Outcome);
    }

    [Fact]
    public async Task ConsultContractsOffPinsTheRouteToTheClientMode()
    {
        var client = new FakeClient()
            .Register(Subject, SchemaIdHex, Schema)
            .Governs("orders", "order.created", "orders-v1", EnforcementMode.Enforce, Subject);

        var decision = await Build(
                client,
                o =>
                {
                    o.Mode = EnforcementMode.Monitor;
                    o.ConsultContracts = false;
                })
            .InspectPublishAsync(Publishing(), Conforming);

        Assert.Null(decision.Contract);
        Assert.Equal(EnforcementMode.Monitor, decision.EffectiveMode);
    }

    [Fact]
    public async Task AQueueContractRefusesASubjectItDoesNotExpect()
    {
        var client = new FakeClient()
            .Register(Subject, SchemaIdHex, Schema)
            .GovernsQueue("orders-worker", "orders-v1", EnforcementMode.Monitor, "acme.orders.OrderAmended");

        var envelope = EnvelopeWriter.Headers(
            SchemaId.Create(SchemaIdHex).Value,
            SubjectName.Create(Subject).Value,
            1,
            null,
            SchemaFormat.Json);

        var decision = await Build(client).InspectConsumeAsync(
            AsHeaders(envelope), Subject, null, Conforming, "orders-worker");

        Assert.Equal(EnforcementOutcome.Observed, decision.Outcome);
        Assert.Equal(ConcordatCodes.ContractSubjectNotPermitted, decision.Code);
        Assert.Contains("orders-worker", decision.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AQueueWithNoNameIsGovernedByTheClientModeAlone()
    {
        var client = new FakeClient()
            .Register(Subject, SchemaIdHex, Schema)
            .GovernsQueue("orders-worker", "orders-v1", EnforcementMode.Enforce, "acme.orders.OrderAmended");

        var envelope = EnvelopeWriter.Headers(
            SchemaId.Create(SchemaIdHex).Value,
            SubjectName.Create(Subject).Value,
            1,
            null,
            SchemaFormat.Json);

        // A consumer constructed without a queue name. RabbitMQ does not put the queue on a
        // delivery, so there is nothing to infer it from and the contract is simply unreachable
        // — which must degrade to the client's own mode rather than to a spurious violation.
        var decision = await Build(client).InspectConsumeAsync(
            AsHeaders(envelope), Subject, null, Conforming, queue: null);

        Assert.Equal(EnforcementOutcome.Valid, decision.Outcome);
        Assert.Null(decision.Contract);
    }

    private static Dictionary<string, object?> AsHeaders(IReadOnlyDictionary<string, string> envelope) =>
        // byte[], because that is how RabbitMQ.Client delivers them — measured in M2.5.
        envelope.ToDictionary(
            h => h.Key, h => (object?)Encoding.UTF8.GetBytes(h.Value), StringComparer.Ordinal);
}
