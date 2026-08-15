using System.Text;
using Concordat.Cli.Inference;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using RabbitMQ.Client;

namespace Concordat.Cli.Tests;

/// <summary>A plain broker for the drain tests.</summary>
public sealed class DrainBrokerFixture : IAsyncLifetime
{
    private readonly IContainer _container = new ContainerBuilder("rabbitmq:4.1")
        .WithPortBinding(5672, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Server startup complete"))
        .Build();

    /// <summary>The AMQP URI for the container.</summary>
    public Uri Uri => new($"amqp://guest:guest@{_container.Hostname}:{_container.GetMappedPublicPort(5672)}/");

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>Opens a connection.</summary>
    /// <returns>A connection the caller disposes.</returns>
    public Task<IConnection> ConnectAsync() => new ConnectionFactory { Uri = Uri }.CreateConnectionAsync();
}

/// <summary>Marks a class as sharing one broker.</summary>
[CollectionDefinition(Name)]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "'Collection' is xunit's own term for a shared-fixture group.")]
public sealed class DrainBrokerCollection : ICollectionFixture<DrainBrokerFixture>
{
    /// <summary>The collection name.</summary>
    public const string Name = "drain-broker";
}

/// <summary>
/// Queue mode, against a real broker.
/// </summary>
/// <remarks>
/// <b>One property matters more than everything else here: nothing may be lost.</b> This
/// command reads production traffic, and ADR-014 permits it only because the read is
/// non-destructive. That claim cannot be made against a mock — it is a statement about what
/// the broker still holds afterwards.
/// </remarks>
[Collection(DrainBrokerCollection.Name)]
public class QueueSamplerTests(DrainBrokerFixture broker)
{
    private async Task<string> SeedAsync(int count, Func<int, string>? type = null)
    {
        var queue = $"drain-{Guid.NewGuid():N}";

        await using var connection = await broker.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();

        await channel.QueueDeclareAsync(queue, durable: false, exclusive: false, autoDelete: false);

        for (var i = 0; i < count; i++)
        {
            var properties = new BasicProperties { Type = type?.Invoke(i) };

            await channel.BasicPublishAsync(
                string.Empty,
                queue,
                mandatory: true,
                properties,
                Encoding.UTF8.GetBytes($$"""{"id":{{i}},"status":"placed"}"""));
        }

        return queue;
    }

    private async Task<uint> DepthAsync(string queue)
    {
        await using var connection = await broker.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        return await channel.MessageCountAsync(queue);
    }

    [Fact]
    public async Task DrainingLosesNothing()
    {
        // The safety property the whole feature rests on.
        var queue = await SeedAsync(25);

        var sample = await QueueSampler.DrainAsync(broker.Uri, queue, 25, default);

        Assert.Equal(25, sample.Inspected);
        Assert.Equal(25, sample.Requeued);
        Assert.Equal(25, sample.Payloads.Count);

        // Every message is back. Requeued to the head rather than its original position — which
        // is the reordering the command makes you acknowledge — but present.
        Assert.Equal(25u, await DepthAsync(queue));
    }

    [Fact]
    public async Task DrainingIsBoundedByMax()
    {
        var queue = await SeedAsync(30);

        var sample = await QueueSampler.DrainAsync(broker.Uri, queue, 10, default);

        Assert.Equal(10, sample.Inspected);
        Assert.Equal(30u, await DepthAsync(queue));
    }

    [Fact]
    public async Task DrainingStopsAtTheEndRatherThanWaitingForMore()
    {
        // Waiting would hold every fetched message unacknowledged and starve the real consumers
        // for as long as the wait lasted.
        var queue = await SeedAsync(3);

        var sample = await QueueSampler.DrainAsync(broker.Uri, queue, 500, default);

        Assert.Equal(3, sample.Inspected);
        Assert.Equal(3u, await DepthAsync(queue));
    }

    [Fact]
    public async Task AnEmptyQueueIsNotAnError()
    {
        var queue = await SeedAsync(0);

        var sample = await QueueSampler.DrainAsync(broker.Uri, queue, 100, default);

        Assert.Equal(0, sample.Inspected);
        Assert.Empty(sample.Payloads);
    }

    [Fact]
    public async Task ABinaryPayloadIsInspectedButNotSampled()
    {
        // Encoding.UTF8.GetString never throws -- it replaces invalid bytes with U+FFFD -- so
        // a strict decoder is what makes a binary message recognisable as binary instead of
        // silently becoming a garbled string handed to the inferrer as though it were JSON.
        var queue = $"drain-{Guid.NewGuid():N}";

        await using var connection = await broker.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.QueueDeclareAsync(queue, durable: false, exclusive: false, autoDelete: false);

        byte[] binary = [0xFF, 0xFE, 0x00, 0x01, 0x80, 0x81];
        await channel.BasicPublishAsync(
            string.Empty, queue, mandatory: true, new BasicProperties(), binary);

        await channel.BasicPublishAsync(
            string.Empty, queue, mandatory: true, new BasicProperties(),
            Encoding.UTF8.GetBytes("""{"id":1}"""));

        var sample = await QueueSampler.DrainAsync(broker.Uri, queue, 2, default);

        Assert.Equal(2, sample.Inspected);
        Assert.Equal(["{\"id\":1}"], sample.Payloads);
    }

    [Fact]
    public async Task MessageTypesAreCountedSoAMixedQueueCanBeDetected()
    {
        // One queue routinely carries several message types (ADR-011), and inferring a single
        // schema across them would produce a union of unrelated shapes that looks plausible.
        var queue = await SeedAsync(12, i => i % 3 == 0 ? "acme.A" : "acme.B");

        var sample = await QueueSampler.DrainAsync(broker.Uri, queue, 12, default);

        Assert.Equal(2, sample.Subjects.Count);
        Assert.Equal(4, sample.Subjects["acme.A"]);
        Assert.Equal(8, sample.Subjects["acme.B"]);
    }

    [Fact]
    public async Task InferenceRunsEndToEndFromALiveQueue()
    {
        var queue = await SeedAsync(15);

        var sample = await QueueSampler.DrainAsync(broker.Uri, queue, 15, default);
        var result = JsonSchemaInferrer.Infer(sample.Payloads);

        using var schema = System.Text.Json.JsonDocument.Parse(result.Schema);
        Assert.Equal(
            "integer",
            schema.RootElement.GetProperty("properties").GetProperty("id").GetProperty("type").GetString());

        Assert.Equal(15u, await DepthAsync(queue));
    }
}
