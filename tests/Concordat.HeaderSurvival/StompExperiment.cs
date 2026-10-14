using System.Globalization;
using System.Net.Sockets;
using System.Text;
using RabbitMQ.Client;

namespace Concordat.HeaderSurvival;

/// <summary>
/// Does the envelope survive being read over STOMP? (DESIGN §2, M2.5.)
/// </summary>
/// <remarks>
/// Spoken over a raw socket rather than through a STOMP library. The protocol is a handful of
/// text frames, and adding a dependency to a test whose entire purpose is to observe bytes on
/// the wire would put a translation layer between the measurement and the thing measured.
/// </remarks>
[Collection(BrokerCollection.Name)]
public class StompExperiment(BrokerFixture broker)
{
    [Fact]
    public async Task TheEnvelopeSurvivesToASubscriberOverStomp()
    {
        var routingKey = $"acme.orders.probe{Guid.NewGuid():N}";

        using var stomp = new StompClient();
        await stomp.ConnectAsync(broker.Host, broker.StompPort);
        await stomp.SubscribeAsync($"/topic/{routingKey}");

        await using (var connection = await broker.ConnectAsync())
        await using (var channel = await connection.CreateChannelAsync())
        {
            // Published as AMQP 0-9-1 to the topic exchange the STOMP adapter is backed by.
            await channel.BasicPublishAsync(
                "amq.topic", routingKey, mandatory: false, Probe.Properties(), Probe.Body);
        }

        var frame = await stomp.ReceiveAsync(TimeSpan.FromSeconds(30));

        Assert.True(frame is not null, "nothing arrived over STOMP.");
        Assert.Equal("MESSAGE", frame.Command);

        foreach (var (key, expected) in Probe.Envelope)
        {
            Assert.True(
                frame.Headers.TryGetValue(key, out var actual),
                $"STOMP: '{key}' did not survive. Arrived with: " +
                $"{string.Join(", ", frame.Headers.Keys.Order(StringComparer.Ordinal))}");

            Assert.Equal(expected, actual);
        }

        // The STOMP adapter surfaces AMQP headers as frame headers with no prefix or mangling,
        // so a non-Concordat STOMP consumer can read the schema id without an SDK at all —
        // which is the whole argument for a header envelope over a body prefix (ADR-010).
        Assert.Equal("acme.orders.OrderCreated", frame.Headers["concordat-subject"]);
    }

    /// <summary>The few STOMP frames this experiment needs, and nothing else.</summary>
    private sealed class StompClient : IDisposable
    {
        private const byte Nul = 0;

        private readonly TcpClient _tcp = new();
        private NetworkStream _stream = null!;
        private readonly List<byte> _buffer = [];

        public async Task ConnectAsync(string host, int port)
        {
            await _tcp.ConnectAsync(host, port).ConfigureAwait(false);
            _stream = _tcp.GetStream();

            await SendAsync(
                "CONNECT", new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["accept-version"] = "1.2",
                    ["host"] = "/",
                    ["login"] = "guest",
                    ["passcode"] = "guest",
                }).ConfigureAwait(false);

            var connected = await ReceiveAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            Assert.True(connected is not null, "the broker did not answer CONNECT.");
            Assert.Equal("CONNECTED", connected.Command);
        }

        /// <summary>Subscribes, and returns only once the broker says the subscription exists.</summary>
        /// <remarks>
        /// <b>A SUBSCRIBE has no reply unless a receipt is asked for, and this test publishes the
        /// moment it returns.</b> The broker creates the subscription's queue and binds it to
        /// <c>amq.topic</c> after it has read the frame, so a message published in between has no
        /// binding to be routed to and is dropped, and the test then waits its full 30 seconds for
        /// something that was never delivered. A fast machine almost never loses that race and a
        /// busy CI runner does (it failed on two of five consecutive runs); measured
        /// against a real broker without the wait, about one publish in 1,700 was lost, and with
        /// it none in 6,000. The MQTT experiment is not exposed to this: its subscribe call
        /// returns on the broker's SUBACK.
        /// </remarks>
        public async Task SubscribeAsync(string destination)
        {
            const string receipt = "concordat-probe-subscribed";

            await SendAsync("SUBSCRIBE", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["id"] = "concordat-probe",
                ["destination"] = destination,
                ["ack"] = "auto",
                ["receipt"] = receipt,
            }).ConfigureAwait(false);

            var confirmed = await ReceiveAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);

            Assert.True(confirmed is not null, "the broker did not confirm the subscription.");
            Assert.Equal("RECEIPT", confirmed.Command);
            Assert.Equal(receipt, confirmed.Headers["receipt-id"]);
        }

        public async Task<StompFrame?> ReceiveAsync(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            var chunk = new byte[8192];

            while (DateTime.UtcNow < deadline)
            {
                var terminator = _buffer.IndexOf(Nul);
                if (terminator >= 0)
                {
                    var text = Encoding.UTF8.GetString([.. _buffer.Take(terminator)]);
                    _buffer.RemoveRange(0, terminator + 1);
                    return Parse(text);
                }

                if (!_stream.DataAvailable)
                {
                    await Task.Delay(100).ConfigureAwait(false);
                    continue;
                }

                var read = await _stream.ReadAsync(chunk).ConfigureAwait(false);
                _buffer.AddRange(chunk.Take(read));
            }

            return null;
        }

        private async Task SendAsync(string command, IReadOnlyDictionary<string, string> headers)
        {
            var frame = new StringBuilder(command).Append('\n');

            foreach (var (key, value) in headers)
            {
                frame.Append(CultureInfo.InvariantCulture, $"{key}:{value}\n");
            }

            frame.Append('\n');

            var bytes = Encoding.UTF8.GetBytes(frame.ToString());
            await _stream.WriteAsync(bytes).ConfigureAwait(false);
            await _stream.WriteAsync(new byte[] { Nul }).ConfigureAwait(false);
            await _stream.FlushAsync().ConfigureAwait(false);
        }

        private static StompFrame Parse(string text)
        {
            // Leading newlines are heartbeats, which the broker sends between frames.
            var lines = text.TrimStart('\n', '\r').Split('\n');
            var headers = new Dictionary<string, string>(StringComparer.Ordinal);
            var index = 1;

            for (; index < lines.Length && lines[index].Length > 0; index++)
            {
                var separator = lines[index].IndexOf(':', StringComparison.Ordinal);
                if (separator > 0)
                {
                    headers[lines[index][..separator]] = lines[index][(separator + 1)..].TrimEnd('\r');
                }
            }

            return new StompFrame(
                lines[0].Trim(), headers, string.Join('\n', lines.Skip(index + 1)));
        }

        public void Dispose() => _tcp.Dispose();
    }

    private sealed record StompFrame(
        string Command, IReadOnlyDictionary<string, string> Headers, string Body);
}
