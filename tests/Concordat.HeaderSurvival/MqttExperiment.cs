using System.Text;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Packets;
using RabbitMQ.Client;

namespace Concordat.HeaderSurvival;

/// <summary>
/// Does the envelope survive being read over MQTT? (DESIGN §2, M2.5.)
/// </summary>
/// <remarks>
/// The protocol version is the whole question. MQTT 3.1.1 has no user-property mechanism at
/// all, so there is nowhere for a header to go; MQTT 5.0 added them. Both are measured,
/// because "MQTT" on its own is not an answer an adopter can act on.
/// </remarks>
[Collection(BrokerCollection.Name)]
public class MqttExperiment(BrokerFixture broker)
{
    [Fact]
    public async Task Mqtt5CarriesTheEnvelopeAsUserProperties()
    {
        var received = await RoundTripAsync(MqttProtocolVersion.V500);

        Assert.True(received is not null, "nothing arrived over MQTT 5.");

        var properties = (received.UserProperties ?? [])
            .ToDictionary(p => p.Name, p => p.ReadValueAsString(), StringComparer.Ordinal);

        foreach (var (key, expected) in Probe.Envelope)
        {
            Assert.True(
                properties.TryGetValue(key, out var actual),
                $"MQTT 5: '{key}' did not survive. Arrived with: " +
                $"[{string.Join(", ", properties.Keys.Order(StringComparer.Ordinal))}]");

            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public async Task Mqtt311LosesTheEnvelopeEntirely()
    {
        // Not a defect to fix — a limit to publish. MQTT 3.1.1 has no user properties, so a
        // 3.1.1 subscriber cannot be given schema identity by any header scheme whatsoever.
        // An adopter with 3.1.1 consumers needs Mode B, where the id is in the body, or must
        // accept that those consumers are unvalidated.
        var received = await RoundTripAsync(MqttProtocolVersion.V311);

        Assert.True(received is not null, "nothing arrived over MQTT 3.1.1.");
        Assert.Empty(received.UserProperties ?? []);

        // The payload is untouched, which is why Mode B remains an option here.
        Assert.Equal(
            Encoding.UTF8.GetString(Probe.Body.Span),
            Encoding.UTF8.GetString(received.Payload));
    }

    private async Task<MqttApplicationMessage?> RoundTripAsync(MqttProtocolVersion version)
    {
        var topic = $"acme/orders/probe{Guid.NewGuid():N}";
        var routingKey = topic.Replace('/', '.');

        using var client = new MqttClientFactory().CreateMqttClient();

        var arrived = new TaskCompletionSource<MqttApplicationMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        client.ApplicationMessageReceivedAsync += e =>
        {
            arrived.TrySetResult(e.ApplicationMessage);
            return Task.CompletedTask;
        };

        await client.ConnectAsync(new MqttClientOptionsBuilder()
            .WithTcpServer(broker.Host, broker.MqttPort)
            .WithCredentials("guest", "guest")
            .WithProtocolVersion(version)
            .WithCleanSession()
            .Build()).ConfigureAwait(false);

        await client.SubscribeAsync(topic).ConfigureAwait(false);

        await using (var connection = await broker.ConnectAsync().ConfigureAwait(false))
        await using (var channel = await connection.CreateChannelAsync().ConfigureAwait(false))
        {
            // Published over AMQP 0-9-1 to the topic exchange the MQTT plugin is backed by.
            // The dot-to-slash difference between AMQP routing keys and MQTT topics is the
            // plugin's own convention, not ours.
            await channel.BasicPublishAsync(
                "amq.topic", routingKey, mandatory: false, Probe.Properties(), Probe.Body)
                .ConfigureAwait(false);
        }

        var completed = await Task.WhenAny(arrived.Task, Task.Delay(TimeSpan.FromSeconds(30)))
            .ConfigureAwait(false);

        await client.DisconnectAsync().ConfigureAwait(false);

        return completed == arrived.Task ? await arrived.Task.ConfigureAwait(false) : null;
    }
}
