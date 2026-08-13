using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using RabbitMQ.Client;

namespace Concordat.HeaderSurvival;

/// <summary>
/// Does the envelope survive federation across two brokers? (DESIGN §2, M2.5.)
/// </summary>
/// <remarks>
/// <para>
/// The only experiment here that needs two brokers, and the one closest to how a large estate
/// actually loses headers: the downstream broker consumes from the upstream over a link it
/// manages itself and republishes locally, so the message is reconstructed by code neither
/// publisher nor consumer controls.
/// </para>
/// <para>
/// Raises its own containers rather than sharing the fixture, because federation is a property
/// of a pair.
/// </para>
/// </remarks>
public class FederationExperiment : IAsyncLifetime
{
    private INetwork _network = null!;
    private IContainer _upstream = null!;
    private IContainer _downstream = null!;

    public async Task InitializeAsync()
    {
        _network = new NetworkBuilder().Build();
        await _network.CreateAsync().ConfigureAwait(false);

        _upstream = BrokerFixture.Builder()
            .WithNetwork(_network)
            .WithNetworkAliases("upstream")
            .Build();

        _downstream = BrokerFixture.Builder()
            .WithNetwork(_network)
            .WithNetworkAliases("downstream")
            .Build();

        await Task.WhenAll(_upstream.StartAsync(), _downstream.StartAsync()).ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        await _downstream.DisposeAsync().ConfigureAwait(false);
        await _upstream.DisposeAsync().ConfigureAwait(false);
        await _network.DisposeAsync().ConfigureAwait(false);
    }

    [Fact]
    public async Task TheEnvelopeSurvivesFederation()
    {
        const string exchange = "fed.orders";
        const string routingKey = "order.created";
        var queue = $"fed-consumer-{Guid.NewGuid():N}";

        // The upstream must know the exchange the link will bind against.
        await using (var connection = await BrokerFixture.ConnectAsync(
            _upstream.Hostname, _upstream.GetMappedPublicPort(5672)))
        await using (var channel = await connection.CreateChannelAsync())
        {
            await channel.ExchangeDeclareAsync(exchange, ExchangeType.Topic, durable: true, autoDelete: false);
        }

        var upstreamDefinition = JsonSerializer.Serialize(
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                // Reached over the container network, so the link is a genuine broker-to-broker
                // hop rather than a loopback that would prove nothing.
                ["uri"] = "amqp://guest:guest@upstream:5672",
            });

        var policyDefinition = JsonSerializer.Serialize(
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["federation-upstream"] = "probe-upstream",
            });

        await BrokerFixture.ExecAsync(
            _downstream, "rabbitmqctl", "set_parameter", "federation-upstream",
            "probe-upstream", upstreamDefinition);

        await BrokerFixture.ExecAsync(
            _downstream, "rabbitmqctl", "set_policy", "probe-federation",
            "^fed\\.", policyDefinition, "--priority", "1", "--apply-to", "exchanges");

        await using var downstreamConnection = await BrokerFixture.ConnectAsync(
            _downstream.Hostname, _downstream.GetMappedPublicPort(5672));
        await using var downstreamChannel = await downstreamConnection.CreateChannelAsync();

        await downstreamChannel.ExchangeDeclareAsync(
            exchange, ExchangeType.Topic, durable: true, autoDelete: false);
        await downstreamChannel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false);
        await downstreamChannel.QueueBindAsync(queue, exchange, routingKey);

        // The link is established asynchronously and the upstream binding propagates with it,
        // so a message published too early is legitimately dropped. Publishing repeatedly is
        // the honest way to wait: it measures survival, not startup timing.
        await using var upstreamConnection = await BrokerFixture.ConnectAsync(
            _upstream.Hostname, _upstream.GetMappedPublicPort(5672));
        await using var upstreamChannel = await upstreamConnection.CreateChannelAsync();

        BasicGetResult? federated = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);

        while (federated is null && DateTime.UtcNow < deadline)
        {
            await upstreamChannel.BasicPublishAsync(
                exchange, routingKey, mandatory: false, Probe.Properties(), Probe.Body);

            federated = await Probe.WaitForMessageAsync(
                downstreamChannel, queue, TimeSpan.FromSeconds(3));
        }

        Assert.True(federated is not null, "nothing crossed the federation link.");

        Probe.AssertEnvelopeIntact("federation", federated.BasicProperties.Headers);
        Assert.Equal("acme.orders.OrderCreated", federated.BasicProperties.Type);

        // Federation stamps its own provenance. Recorded for the same reason as x-death: the
        // header table is rebuilt on the far side, and survival is a property of that rebuild
        // preserving what it does not recognise.
        var decoded = Probe.Decode(federated.BasicProperties.Headers);
        Assert.Contains("x-received-from", decoded.Keys, StringComparer.Ordinal);
    }
}
