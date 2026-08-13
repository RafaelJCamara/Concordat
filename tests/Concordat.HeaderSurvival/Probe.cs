using System.Text;
using Concordat.Domain.Messaging;
using Concordat.Domain.Registry;
using RabbitMQ.Client;

namespace Concordat.HeaderSurvival;

/// <summary>
/// The message every experiment sends, and the tools to see what came out the other end.
/// </summary>
/// <remarks>
/// The headers are produced by the real <see cref="EnvelopeWriter"/> rather than hand-written.
/// An experiment that measured the survival of a header set Concordat does not actually emit
/// would prove nothing about Concordat.
/// </remarks>
public static class Probe
{
    /// <summary>A schema id that is valid and obviously synthetic.</summary>
    public const string SchemaIdHex = "0123456789abcdef0123456789abcdef";

    /// <summary>The payload. Content is irrelevant; only the headers are under test.</summary>
    public static ReadOnlyMemory<byte> Body { get; } = "{\"id\":1}"u8.ToArray();

    /// <summary>The canonical envelope, exactly as a Concordat publisher would write it.</summary>
    public static IReadOnlyDictionary<string, string> Envelope { get; } = EnvelopeWriter.Headers(
        SchemaId.Create(SchemaIdHex).Value,
        SubjectName.Create("acme.orders.OrderCreated").Value,
        7,
        SemanticVersion.Create("2.1.0").Value,
        SchemaFormat.Json);

    /// <summary>Turns the envelope into an AMQP header table.</summary>
    /// <returns>A fresh table; RabbitMQ.Client mutates what it is given.</returns>
    public static Dictionary<string, object?> AmqpHeaders() =>
        Envelope.ToDictionary(h => h.Key, h => (object?)h.Value, StringComparer.Ordinal);

    /// <summary>Basic properties carrying the envelope and a subject in <c>type</c>.</summary>
    /// <returns>Properties ready to publish.</returns>
    public static BasicProperties Properties() => new()
    {
        Headers = AmqpHeaders(),
        Type = "acme.orders.OrderCreated",
        ContentType = "application/json",
    };

    /// <summary>
    /// Reads a header table back as text, recording how each value actually arrived.
    /// </summary>
    /// <param name="headers">The table as the broker delivered it.</param>
    /// <returns>Decoded values, keyed as received.</returns>
    /// <remarks>
    /// The <c>byte[]</c> branch is the one that matters. RabbitMQ.Client writes a string as
    /// AMQP type <c>S</c> (long string) and reads it back as <c>byte[]</c>, so an SDK that
    /// assumes symmetry gets <c>System.Byte[]</c> where it expected a schema id. M2.2 asserts
    /// this from the documentation; here it is measured.
    /// </remarks>
    public static Dictionary<string, string> Decode(IDictionary<string, object?>? headers)
    {
        var decoded = new Dictionary<string, string>(StringComparer.Ordinal);

        if (headers is null)
        {
            return decoded;
        }

        foreach (var (key, value) in headers)
        {
            decoded[key] = value switch
            {
                null => "<null>",
                byte[] bytes => Encoding.UTF8.GetString(bytes),
                string text => text,
                _ => value.ToString() ?? "<null>",
            };
        }

        return decoded;
    }

    /// <summary>Names the CLR type each header arrived as, for the wire-type findings.</summary>
    /// <param name="headers">The table as the broker delivered it.</param>
    /// <returns>Type names, keyed as received.</returns>
    public static Dictionary<string, string> WireTypes(IDictionary<string, object?>? headers) =>
        headers is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : headers.ToDictionary(
                h => h.Key,
                h => h.Value?.GetType().Name ?? "null",
                StringComparer.Ordinal);

    /// <summary>Polls <c>basic.get</c> until a message arrives or the budget runs out.</summary>
    /// <param name="channel">The channel.</param>
    /// <param name="queue">The queue to drain from.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="autoAck">
    /// Leave <see langword="true"/> to observe and discard. Pass <see langword="false"/> when
    /// the caller intends to nack — an auto-acked delivery has no tag left to reject, and the
    /// attempt closes the channel.
    /// </param>
    /// <returns>The message, or null if none arrived.</returns>
    /// <remarks>
    /// Polling rather than an async consumer: every experiment here asks "did it arrive, and
    /// with what", and a consumer callback would add a synchronisation problem to a question
    /// that does not have one. Shovels and federation links also take a moment to establish,
    /// so waiting is required regardless.
    /// </remarks>
    public static async Task<BasicGetResult?> WaitForMessageAsync(
        IChannel channel, string queue, TimeSpan? timeout = null, bool autoAck = true)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));

        while (DateTime.UtcNow < deadline)
        {
            var message = await channel.BasicGetAsync(queue, autoAck).ConfigureAwait(false);

            if (message is not null)
            {
                return message;
            }

            await Task.Delay(200).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>Asserts every Concordat header survived unchanged.</summary>
    /// <param name="hop">What the message went through, named for the failure message.</param>
    /// <param name="headers">The delivered header table.</param>
    public static void AssertEnvelopeIntact(string hop, IDictionary<string, object?>? headers)
    {
        var decoded = Decode(headers);

        foreach (var (key, expected) in Envelope)
        {
            Assert.True(
                decoded.TryGetValue(key, out var actual),
                $"{hop}: '{key}' did not survive. Arrived with: " +
                $"{string.Join(", ", decoded.Keys.Order(StringComparer.Ordinal))}");

            Assert.True(
                expected == actual,
                $"{hop}: '{key}' changed from '{expected}' to '{actual}'.");
        }
    }
}
