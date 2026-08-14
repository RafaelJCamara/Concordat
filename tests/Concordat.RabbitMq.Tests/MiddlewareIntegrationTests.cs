using System.Text;
using Concordat.Domain.Registry;
using Concordat.Formats.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using RabbitMQ.Client;

namespace Concordat.RabbitMq.Tests;

/// <summary>A plain broker. No plugins needed — the middleware speaks only AMQP 0-9-1.</summary>
public sealed class PlainBrokerFixture : IAsyncLifetime
{
    private readonly IContainer _container = new ContainerBuilder("rabbitmq:4.1")
        .WithPortBinding(5672, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Server startup complete"))
        .Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public Task<IConnection> ConnectAsync() =>
        new ConnectionFactory
        {
            HostName = _container.Hostname,
            Port = _container.GetMappedPublicPort(5672),
            UserName = "guest",
            Password = "guest",
        }.CreateConnectionAsync();
}

/// <summary>Marks a class as sharing one broker.</summary>
[CollectionDefinition(Name)]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "'Collection' is xunit's own term for a shared-fixture group.")]
public sealed class PlainBrokerCollection : ICollectionFixture<PlainBrokerFixture>
{
    /// <summary>The collection name.</summary>
    public const string Name = "plain-broker";
}

/// <summary>Records what the application would have received.</summary>
internal sealed class RecordingConsumer : IAsyncBasicConsumer
{
    public List<string> Delivered { get; } = [];

    public IChannel? Channel { get; set; }

    public Task HandleBasicDeliverAsync(
        string consumerTag,
        ulong deliveryTag,
        bool redelivered,
        string exchange,
        string routingKey,
        IReadOnlyBasicProperties properties,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default)
    {
        lock (Delivered)
        {
            Delivered.Add(Encoding.UTF8.GetString(body.Span));
        }

        return Channel is null
            ? Task.CompletedTask
            : Channel.BasicAckAsync(deliveryTag, multiple: false, cancellationToken).AsTask();
    }

    public Task HandleBasicCancelAsync(string consumerTag, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task HandleBasicCancelOkAsync(string consumerTag, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task HandleBasicConsumeOkAsync(string consumerTag, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task HandleChannelShutdownAsync(object channel, RabbitMQ.Client.Events.ShutdownEventArgs reason) =>
        Task.CompletedTask;
}

/// <summary>
/// The middleware against a real broker (M2.6).
/// </summary>
/// <remarks>
/// The enforcement <em>rules</em> are unit-tested in <see cref="SchemaEnforcerTests"/>. What is
/// left is the wiring, and wiring is exactly what a mock cannot check: that a blocked publish
/// really put nothing on the queue, that a quarantined delivery really landed in another
/// exchange and was really not redelivered.
/// </remarks>
[Collection(PlainBrokerCollection.Name)]
public class MiddlewareIntegrationTests(PlainBrokerFixture broker)
{
    private const string SchemaIdHex = "0123456789abcdef0123456789abcdef";
    private const string Subject = "acme.orders.OrderCreated";

    private const string Schema = """
        {"type":"object","properties":{"id":{"type":"integer"}},"required":["id"]}
        """;

    private static readonly ReadOnlyMemory<byte> Conforming = "{\"id\":1}"u8.ToArray();
    private static readonly ReadOnlyMemory<byte> Violating = "{\"id\":\"nope\"}"u8.ToArray();

    private static (SchemaEnforcer Enforcer, ConcordatRabbitMqOptions Options, EnforcementCounters Counters)
        Build(EnforcementMode mode)
    {
        var counters = new EnforcementCounters();
        var options = new ConcordatRabbitMqOptions { Mode = mode, Observer = counters };
        var client = new FakeClient().Register(Subject, SchemaIdHex, Schema);

        return (new SchemaEnforcer(client, options, [new NJsonSchemaPayloadValidator()]), options, counters);
    }

    private static BasicProperties Properties() =>
        new() { Type = Subject, ContentType = "application/json" };

    [Fact]
    public async Task AConformingPublishArrivesCarryingItsEnvelope()
    {
        var (enforcer, options, _) = Build(EnforcementMode.Enforce);

        await using var connection = await broker.ConnectAsync();
        await using var raw = await connection.CreateChannelAsync();
        var channel = new ConcordatChannel(raw, enforcer, options);

        var queue = (await raw.QueueDeclareAsync()).QueueName;
        await channel.BasicPublishAsync(string.Empty, queue, mandatory: true, Properties(), Conforming);

        var message = await WaitAsync(raw, queue);
        Assert.NotNull(message);

        var headers = Decode(message.BasicProperties.Headers);
        Assert.Equal(SchemaIdHex, headers["concordat-schema-id"]);
        Assert.Equal(Subject, headers["concordat-subject"]);
        Assert.Equal("1", headers["concordat-v"]);
    }

    [Fact]
    public async Task AnEnforcedViolationPutsNothingOnTheQueue()
    {
        // The assertion a mock cannot make. "Threw" is not the same as "published nothing", and
        // the difference is a message the application believes it never sent.
        var (enforcer, options, counters) = Build(EnforcementMode.Enforce);

        await using var connection = await broker.ConnectAsync();
        await using var raw = await connection.CreateChannelAsync();
        var channel = new ConcordatChannel(raw, enforcer, options);

        var queue = (await raw.QueueDeclareAsync()).QueueName;

        var violation = await Assert.ThrowsAsync<ConcordatViolationException>(
            async () => await channel.BasicPublishAsync(
                string.Empty, queue, mandatory: true, Properties(), Violating));

        Assert.Equal("payload_invalid", violation.Code);
        Assert.Equal(Subject, violation.Subject);

        Assert.Equal(0u, await raw.MessageCountAsync(queue));
        Assert.Equal(1, counters.Count(EnforcementSide.Publish, EnforcementOutcome.Blocked));
    }

    [Fact]
    public async Task MonitorModePublishesTheViolationAndSaysSo()
    {
        var (enforcer, options, counters) = Build(EnforcementMode.Monitor);

        await using var connection = await broker.ConnectAsync();
        await using var raw = await connection.CreateChannelAsync();
        var channel = new ConcordatChannel(raw, enforcer, options);

        var queue = (await raw.QueueDeclareAsync()).QueueName;
        await channel.BasicPublishAsync(string.Empty, queue, mandatory: true, Properties(), Violating);

        var message = await WaitAsync(raw, queue);
        Assert.NotNull(message);

        // Delivered, and carrying identity — which is the point of Monitor. A consumer can
        // start reading schema ids while publishers are still being cleaned up.
        Assert.Equal(SchemaIdHex, Decode(message.BasicProperties.Headers)["concordat-schema-id"]);
        Assert.Equal(1, counters.Count(EnforcementSide.Publish, EnforcementOutcome.Observed));
        Assert.Equal(0, counters.Count(EnforcementSide.Publish, EnforcementOutcome.Blocked));
    }

    [Fact]
    public async Task OffModeTouchesNothing()
    {
        var (enforcer, options, _) = Build(EnforcementMode.Off);

        await using var connection = await broker.ConnectAsync();
        await using var raw = await connection.CreateChannelAsync();
        var channel = new ConcordatChannel(raw, enforcer, options);

        var queue = (await raw.QueueDeclareAsync()).QueueName;
        await channel.BasicPublishAsync(string.Empty, queue, mandatory: true, Properties(), Violating);

        var message = await WaitAsync(raw, queue);
        Assert.NotNull(message);
        Assert.DoesNotContain(
            Decode(message.BasicProperties.Headers).Keys,
            k => k.StartsWith("concordat-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AViolatingDeliveryIsQuarantinedWithReasonsAndNotRedelivered()
    {
        var (enforcer, options, counters) = Build(EnforcementMode.Enforce);

        await using var connection = await broker.ConnectAsync();
        await using var raw = await connection.CreateChannelAsync();

        var suffix = Guid.NewGuid().ToString("N");
        var queue = $"app-{suffix}";
        var quarantineQueue = $"quarantined-{suffix}";
        options.QuarantineExchange = $"concordat.quarantine.{suffix}";

        await raw.QueueDeclareAsync(queue, durable: false, exclusive: false, autoDelete: false);
        await raw.ExchangeDeclareAsync(
            options.QuarantineExchange, ExchangeType.Topic, durable: true, autoDelete: false);
        await raw.QueueDeclareAsync(quarantineQueue, durable: false, exclusive: false, autoDelete: false);
        await raw.QueueBindAsync(quarantineQueue, options.QuarantineExchange, "#");

        var application = new RecordingConsumer { Channel = raw };
        var consumer = new ConcordatConsumer(application, raw, enforcer, options);

        // Published raw and pre-enveloped, standing in for a producer elsewhere that was
        // enforcing a different (or no) contract.
        var properties = Properties();
        properties.Headers = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["concordat-v"] = "1",
            ["concordat-schema-id"] = SchemaIdHex,
            ["concordat-subject"] = Subject,
            ["concordat-format"] = "json",
        };

        await raw.BasicPublishAsync(string.Empty, queue, mandatory: true, properties, Violating);
        await raw.BasicConsumeAsync(queue, autoAck: false, consumerTag: string.Empty,
            noLocal: false, exclusive: false, arguments: null, consumer);

        var quarantined = await WaitAsync(raw, quarantineQueue);
        Assert.True(quarantined is not null, "the violating message never reached quarantine.");

        var headers = Decode(quarantined.BasicProperties.Headers);
        Assert.Equal("payload_invalid", headers["concordat-quarantine-reason"]);
        Assert.Equal(queue, headers["concordat-quarantine-routing-key"]);
        Assert.Contains("id", headers["concordat-quarantine-detail"], StringComparison.Ordinal);
        Assert.True(headers.ContainsKey("concordat-quarantine-at"));

        // The original envelope travels with it. A quarantined message stripped of the identity
        // that condemned it would be unusable for diagnosis.
        Assert.Equal(SchemaIdHex, headers["concordat-schema-id"]);

        // Never handed to the application, and never retried: a schema violation is
        // deterministic, so redelivery is pure waste.
        lock (application.Delivered)
        {
            Assert.Empty(application.Delivered);
        }

        Assert.Equal(0u, await raw.MessageCountAsync(queue));
        Assert.Equal(1, counters.Count(EnforcementSide.Consume, EnforcementOutcome.Quarantined));
    }

    [Fact]
    public async Task MonitorModeDeliversTheViolationToTheApplication()
    {
        var (enforcer, options, counters) = Build(EnforcementMode.Monitor);

        await using var connection = await broker.ConnectAsync();
        await using var raw = await connection.CreateChannelAsync();

        var queue = $"monitored-{Guid.NewGuid():N}";
        await raw.QueueDeclareAsync(queue, durable: false, exclusive: false, autoDelete: false);

        var application = new RecordingConsumer { Channel = raw };
        var consumer = new ConcordatConsumer(application, raw, enforcer, options);

        var properties = Properties();
        properties.Headers = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["concordat-v"] = "1",
            ["concordat-schema-id"] = SchemaIdHex,
        };

        await raw.BasicPublishAsync(string.Empty, queue, mandatory: true, properties, Violating);
        await raw.BasicConsumeAsync(queue, autoAck: false, consumerTag: string.Empty,
            noLocal: false, exclusive: false, arguments: null, consumer);

        await WaitUntilAsync(() =>
        {
            lock (application.Delivered)
            {
                return application.Delivered.Count > 0;
            }
        });

        lock (application.Delivered)
        {
            Assert.Single(application.Delivered);
        }

        Assert.Equal(1, counters.Count(EnforcementSide.Consume, EnforcementOutcome.Observed));
    }

    [Fact]
    public async Task AnUnenvelopedDeliveryReachesTheApplicationEvenUnderEnforce()
    {
        // Incremental adoption, asserted end to end. If turning enforcement on diverted every
        // legacy publisher's traffic to quarantine, nobody would ever turn it on.
        var (enforcer, options, counters) = Build(EnforcementMode.Enforce);

        await using var connection = await broker.ConnectAsync();
        await using var raw = await connection.CreateChannelAsync();

        var queue = $"brownfield-{Guid.NewGuid():N}";
        await raw.QueueDeclareAsync(queue, durable: false, exclusive: false, autoDelete: false);

        var application = new RecordingConsumer { Channel = raw };
        var consumer = new ConcordatConsumer(application, raw, enforcer, options);

        await raw.BasicPublishAsync(string.Empty, queue, mandatory: true, Properties(), Violating);
        await raw.BasicConsumeAsync(queue, autoAck: false, consumerTag: string.Empty,
            noLocal: false, exclusive: false, arguments: null, consumer);

        await WaitUntilAsync(() =>
        {
            lock (application.Delivered)
            {
                return application.Delivered.Count > 0;
            }
        });

        Assert.Equal(1, counters.Count(EnforcementSide.Consume, EnforcementOutcome.Unenforced));
    }

    [Fact]
    public async Task ConcordatHeadersDoNotCollideWithOtherFrameworks()
    {
        // M2.6's prefix check. Every one of these is a real convention in a library that could
        // be publishing to the same broker.
        var (enforcer, options, _) = Build(EnforcementMode.Enforce);

        await using var connection = await broker.ConnectAsync();
        await using var raw = await connection.CreateChannelAsync();
        var channel = new ConcordatChannel(raw, enforcer, options);

        var queue = (await raw.QueueDeclareAsync()).QueueName;

        var foreign = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["MT-Activity-Id"] = "masstransit",
            ["NServiceBus.EnclosedMessageTypes"] = "nservicebus",
            ["rbs2-msg-id"] = "rebus",
            ["rabbitmq-stream-offset"] = "streams",
            ["x-delay"] = "broker-reserved",
        };

        var properties = Properties();
        properties.Headers = new Dictionary<string, object?>(foreign, StringComparer.Ordinal);

        await channel.BasicPublishAsync(string.Empty, queue, mandatory: true, properties, Conforming);

        var message = await WaitAsync(raw, queue);
        Assert.NotNull(message);

        var headers = Decode(message.BasicProperties.Headers);

        foreach (var (key, value) in foreign)
        {
            Assert.True(headers.TryGetValue(key, out var actual), $"'{key}' was dropped.");
            Assert.Equal(value, actual);
        }

        Assert.Equal(SchemaIdHex, headers["concordat-schema-id"]);
    }

    [Fact]
    public async Task StampingDoesNotMutateTheCallersProperties()
    {
        // A caller reusing one properties object across publishes must not find our headers
        // accumulating on it — and worse, a stale schema id from a previous message.
        var (enforcer, options, _) = Build(EnforcementMode.Enforce);

        await using var connection = await broker.ConnectAsync();
        await using var raw = await connection.CreateChannelAsync();
        var channel = new ConcordatChannel(raw, enforcer, options);

        var queue = (await raw.QueueDeclareAsync()).QueueName;
        var properties = Properties();

        await channel.BasicPublishAsync(string.Empty, queue, mandatory: true, properties, Conforming);

        Assert.True(
            properties.Headers is null || properties.Headers.Count == 0,
            "the caller's properties were mutated by stamping.");
    }

    private static async Task<BasicGetResult?> WaitAsync(IChannel channel, string queue)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);

        while (DateTime.UtcNow < deadline)
        {
            var message = await channel.BasicGetAsync(queue, autoAck: true);
            if (message is not null)
            {
                return message;
            }

            await Task.Delay(100);
        }

        return null;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);

        while (DateTime.UtcNow < deadline && !condition())
        {
            await Task.Delay(100);
        }

        Assert.True(condition(), "the expected delivery never arrived.");
    }

    private static Dictionary<string, string> Decode(IDictionary<string, object?>? headers) =>
        headers is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : headers.ToDictionary(
                h => h.Key,
                h => h.Value switch
                {
                    byte[] bytes => Encoding.UTF8.GetString(bytes),
                    null => "<null>",
                    var other => other.ToString() ?? "<null>",
                },
                StringComparer.Ordinal);
}
