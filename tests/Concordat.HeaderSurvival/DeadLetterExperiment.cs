using RabbitMQ.Client;

namespace Concordat.HeaderSurvival;

/// <summary>
/// Does the envelope survive dead-lettering? (DESIGN §2, M2.5.)
/// </summary>
/// <remarks>
/// <para>
/// The most important hop to measure, because it is the one Concordat itself causes. M2.4
/// rejects a non-conforming message to a quarantine exchange, and if headers were lost on that
/// path the quarantined message would arrive stripped of the very identity needed to explain
/// why it was quarantined.
/// </para>
/// <para>
/// All three dead-letter triggers are exercised. They are different code paths in the broker
/// and there is no reason from the outside to assume they agree.
/// </para>
/// </remarks>
[Collection(BrokerCollection.Name)]
public class DeadLetterExperiment(BrokerFixture broker)
{
    [Fact]
    public async Task HeadersArriveAsByteArraysNotStrings()
    {
        // M2.2 asserts this from the documentation and builds the reader around it. It is the
        // single assumption whose failure would break every SDK silently: a consumer that
        // trusted symmetry would compare "System.Byte[]" against a schema id and quarantine
        // every message it received.
        await using var connection = await broker.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();

        var queue = await channel.QueueDeclareAsync();
        await channel.BasicPublishAsync(
            string.Empty, queue.QueueName, mandatory: true, Probe.Properties(), Probe.Body);

        var message = await Probe.WaitForMessageAsync(channel, queue.QueueName);
        Assert.NotNull(message);

        var types = Probe.WireTypes(message.BasicProperties.Headers);
        Assert.All(
            Probe.Envelope.Keys,
            key => Assert.True(
                types[key] == "Byte[]",
                $"'{key}' arrived as {types[key]}, not Byte[]. M2.2's decode step assumes Byte[]."));

        // properties.type is a short string in the AMQP frame, not a header table entry, so it
        // does round-trip as a string. The asymmetry is exactly why the envelope lives in
        // headers and the subject is only mirrored into `type`.
        Assert.Equal("acme.orders.OrderCreated", message.BasicProperties.Type);
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("expired")]
    [InlineData("maxlen")]
    public async Task TheEnvelopeSurvivesDeadLettering(string trigger)
    {
        await using var connection = await broker.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();

        var suffix = $"{trigger}-{Guid.NewGuid():N}";
        var dlx = $"dlx-{suffix}";
        var dlq = $"dlq-{suffix}";
        var main = $"main-{suffix}";

        await channel.ExchangeDeclareAsync(dlx, ExchangeType.Fanout, durable: false, autoDelete: false);
        await channel.QueueDeclareAsync(dlq, durable: false, exclusive: false, autoDelete: false);
        await channel.QueueBindAsync(dlq, dlx, routingKey: string.Empty);

        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["x-dead-letter-exchange"] = dlx,
        };

        // Each trigger is a different route into the same machinery: an explicit nack, the
        // message TTL expiring, and the queue overflowing.
        switch (trigger)
        {
            case "expired": arguments["x-message-ttl"] = 1; break;
            case "maxlen": arguments["x-max-length"] = 0; break;
            default: break;
        }

        await channel.QueueDeclareAsync(main, durable: false, exclusive: false, autoDelete: false, arguments);
        await channel.BasicPublishAsync(
            string.Empty, main, mandatory: true, Probe.Properties(), Probe.Body);

        if (trigger == "rejected")
        {
            var delivery = await Probe.WaitForMessageAsync(channel, main, autoAck: false);
            Assert.NotNull(delivery);

            // requeue: false is what M2.4 will do. A schema violation is deterministic, so
            // redelivery is pure waste.
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false);
        }

        var dead = await Probe.WaitForMessageAsync(channel, dlq);
        Assert.True(dead is not null, $"nothing reached the dead-letter queue via '{trigger}'.");

        Probe.AssertEnvelopeIntact($"dead-letter ({trigger})", dead.BasicProperties.Headers);

        // The broker adds its own account of what happened. Recorded because it proves the
        // header table is rewritten rather than passed through untouched — survival is a
        // property of that rewrite preserving what it does not recognise, not of nothing
        // having happened.
        var decoded = Probe.Decode(dead.BasicProperties.Headers);
        Assert.Contains("x-death", decoded.Keys, StringComparer.Ordinal);
        Assert.Equal("acme.orders.OrderCreated", dead.BasicProperties.Type);
    }

    [Fact]
    public async Task ConcordatHeadersAreNotConfusedWithBrokerHeaders()
    {
        // ADR-013 keeps the envelope clear of the x- prefix. This measures the consequence:
        // after a hop that injects x-death, x-first-death-reason and friends, the concordat-*
        // keys are still distinguishable from the broker's own bookkeeping by prefix alone.
        await using var connection = await broker.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();

        var suffix = Guid.NewGuid().ToString("N");
        var dlx = $"dlx-{suffix}";
        var dlq = $"dlq-{suffix}";
        var main = $"main-{suffix}";

        await channel.ExchangeDeclareAsync(dlx, ExchangeType.Fanout, durable: false, autoDelete: false);
        await channel.QueueDeclareAsync(dlq, durable: false, exclusive: false, autoDelete: false);
        await channel.QueueBindAsync(dlq, dlx, routingKey: string.Empty);
        await channel.QueueDeclareAsync(
            main, durable: false, exclusive: false, autoDelete: false,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["x-dead-letter-exchange"] = dlx,
                ["x-message-ttl"] = 1,
            });

        await channel.BasicPublishAsync(
            string.Empty, main, mandatory: true, Probe.Properties(), Probe.Body);

        var dead = await Probe.WaitForMessageAsync(channel, dlq);
        Assert.NotNull(dead);

        var keys = Probe.Decode(dead.BasicProperties.Headers).Keys.ToList();
        var brokerKeys = keys.Where(k => k.StartsWith("x-", StringComparison.Ordinal)).ToList();
        var ours = keys.Where(k => k.StartsWith("concordat-", StringComparison.Ordinal)).ToList();

        Assert.NotEmpty(brokerKeys);
        Assert.Equal(Probe.Envelope.Count, ours.Count);
        Assert.Empty(ours.Intersect(brokerKeys, StringComparer.Ordinal));
    }
}
