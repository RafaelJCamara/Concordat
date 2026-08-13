using System.Text.Json;
using RabbitMQ.Client;

namespace Concordat.HeaderSurvival;

/// <summary>
/// Does the envelope survive a shovel? (DESIGN §2, M2.5.)
/// </summary>
/// <remarks>
/// A shovel is a consume-and-republish, not a forward. The broker reads the message on one side
/// and issues a fresh <c>basic.publish</c> on the other, so every property has to be copied
/// across deliberately by the shovel implementation. That is a different risk from
/// dead-lettering, where the message never leaves the broker's own machinery.
/// </remarks>
[Collection(BrokerCollection.Name)]
public class ShovelExperiment(BrokerFixture broker)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheEnvelopeSurvivesAShovel(bool addForwardHeaders)
    {
        await using var connection = await broker.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();

        var suffix = $"{(addForwardHeaders ? "fwd" : "plain")}-{Guid.NewGuid():N}";
        var source = $"shovel-src-{suffix}";
        var destination = $"shovel-dst-{suffix}";

        await channel.QueueDeclareAsync(source, durable: true, exclusive: false, autoDelete: false);
        await channel.QueueDeclareAsync(destination, durable: true, exclusive: false, autoDelete: false);

        var definition = JsonSerializer.Serialize(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["src-protocol"] = "amqp091",
            ["src-uri"] = "amqp://",
            ["src-queue"] = source,
            ["dest-protocol"] = "amqp091",
            ["dest-uri"] = "amqp://",
            ["dest-queue"] = destination,

            // The setting worth measuring under both values. When on, the shovel injects its
            // own x-shovelled bookkeeping into the header table — the same class of rewrite as
            // x-death, and the same question: does adding disturb what is already there.
            ["dest-add-forward-headers"] = addForwardHeaders,
            ["ack-mode"] = "on-confirm",
        });

        await broker.ExecAsync("rabbitmqctl", "set_parameter", "shovel", $"probe-{suffix}", definition);

        await channel.BasicPublishAsync(
            string.Empty, source, mandatory: true, Probe.Properties(), Probe.Body);

        var shovelled = await Probe.WaitForMessageAsync(channel, destination, TimeSpan.FromSeconds(45));
        Assert.True(shovelled is not null, "nothing arrived through the shovel.");

        Probe.AssertEnvelopeIntact(
            $"shovel (dest-add-forward-headers={addForwardHeaders})", shovelled.BasicProperties.Headers);

        // properties.type is the fallback subject source when the envelope is absent (ADR-011),
        // so a shovel that dropped it would silently break Mode A adopters.
        Assert.Equal("acme.orders.OrderCreated", shovelled.BasicProperties.Type);
        Assert.Equal("application/json", shovelled.BasicProperties.ContentType);

        var decoded = Probe.Decode(shovelled.BasicProperties.Headers);
        if (addForwardHeaders)
        {
            Assert.Contains(
                decoded.Keys,
                k => k.StartsWith("x-shovelled", StringComparison.Ordinal));
        }

        // Whatever the shovel adds, it must not be mistakable for ours.
        Assert.DoesNotContain(
            decoded.Keys,
            k => k.StartsWith("concordat-", StringComparison.Ordinal) && !Probe.Envelope.ContainsKey(k));

        await broker.ExecAsync("rabbitmqctl", "clear_parameter", "shovel", $"probe-{suffix}");
    }
}
