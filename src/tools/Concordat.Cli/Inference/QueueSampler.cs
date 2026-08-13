using System.Text;
using RabbitMQ.Client;

namespace Concordat.Cli.Inference;

/// <summary>What a drain collected.</summary>
/// <param name="Payloads">The message bodies, as text.</param>
/// <param name="Inspected">How many messages were looked at.</param>
/// <param name="Subjects">Distinct <c>properties.type</c> values seen, with counts.</param>
/// <param name="Requeued">How many messages were returned to the queue.</param>
public sealed record QueueSample(
    IReadOnlyList<string> Payloads,
    int Inspected,
    IReadOnlyDictionary<string, int> Subjects,
    int Requeued);

/// <summary>
/// Reads messages off a live queue without consuming them (ADR-014).
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-only by construction, and it still reorders the queue.</b> Every message is fetched
/// with <c>basic.get</c> and immediately nacked with <c>requeue: true</c>, so nothing is lost —
/// but a requeued message goes back at the <em>head</em>, not the position it came from, and
/// anything delivered to a real consumer meanwhile has moved past it. On a queue whose order
/// matters, that is a real side effect on production traffic.
/// </para>
/// <para>
/// This is why file mode is the default (ADR-014). Queue mode exists because the alternative —
/// asking someone to collect 200 sample payloads by hand before they can evaluate the product —
/// is where adoption dies.
/// </para>
/// </remarks>
public static class QueueSampler
{
    /// <summary>Drains up to <paramref name="max"/> messages, requeuing every one.</summary>
    /// <param name="broker">AMQP URI, for example <c>amqp://guest:guest@localhost:5672/</c>.</param>
    /// <param name="queue">The queue to read.</param>
    /// <param name="max">How many messages to inspect.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What was collected.</returns>
    public static async Task<QueueSample> DrainAsync(
        Uri broker, string queue, int max, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(broker);

        var factory = new ConnectionFactory { Uri = broker };

        await using var connection = await factory.CreateConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var payloads = new List<string>();
        var subjects = new Dictionary<string, int>(StringComparer.Ordinal);
        var pending = new List<ulong>();
        var inspected = 0;

        try
        {
            while (inspected < max && !cancellationToken.IsCancellationRequested)
            {
                var message = await channel.BasicGetAsync(queue, autoAck: false, cancellationToken)
                    .ConfigureAwait(false);

                if (message is null)
                {
                    // Drained everything currently visible. Stopping here rather than polling:
                    // waiting for more would hold every fetched message unacknowledged and
                    // starve the real consumers for as long as the wait lasts.
                    break;
                }

                inspected++;
                pending.Add(message.DeliveryTag);

                var type = message.BasicProperties.Type;
                if (!string.IsNullOrWhiteSpace(type))
                {
                    subjects[type] = subjects.GetValueOrDefault(type) + 1;
                }

                try
                {
                    payloads.Add(Encoding.UTF8.GetString(message.Body.Span));
                }
                catch (DecoderFallbackException)
                {
                    // A binary payload on the queue. Counted as inspected, not as a sample.
                }
            }
        }
        finally
        {
            // Requeued in a finally, and only after the loop. Every fetched message goes back
            // even if the drain is cancelled or throws mid-way — the one thing this command
            // must never do is swallow production messages.
            foreach (var tag in pending)
            {
                await channel.BasicNackAsync(tag, multiple: false, requeue: true, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }

        return new QueueSample(payloads, inspected, subjects, pending.Count);
    }
}
