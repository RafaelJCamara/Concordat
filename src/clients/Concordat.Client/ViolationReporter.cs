using System.Collections.Concurrent;

namespace Concordat.Client;

/// <summary>One violation a client wants the registry to know about.</summary>
/// <param name="Side"><c>PUBLISH</c> or <c>CONSUME</c>.</param>
/// <param name="Route">The exchange and routing key, or the queue.</param>
/// <param name="Subject">The subject, when one was resolved.</param>
/// <param name="Code">The <c>concordatCode</c> that classified it.</param>
/// <param name="Detail">What went wrong.</param>
public readonly record struct ViolationKey(
    string Side, string Route, string? Subject, string Code, string Detail);

/// <summary>
/// Counts violations locally and reports them to the registry in batches (decision 25).
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here may touch the delivery path, and the design follows from that.</b> A
/// violation is observed while a message is being published or consumed — the hot path this
/// whole product exists to keep the registry out of. So <see cref="Record"/> does one dictionary
/// update and returns; the HTTP call happens on somebody else's thread, later, in bulk.
/// </para>
/// <para>
/// <b>Aggregated, not queued per message.</b> A broken publisher emits thousands a second, and a
/// queue of individual violations would grow without bound and then post a batch large enough to
/// hurt the registry — a denial of service written by our own SDK. Counting by fingerprint means
/// a fault of any severity costs one entry.
/// </para>
/// <para>
/// <b>Bounded, and it drops rather than grows.</b> <see cref="MaxDistinct"/> caps how many
/// distinct violations are tracked between flushes. Past it new fingerprints are dropped and
/// counted in <see cref="Dropped"/>, because a client that runs itself out of memory recording
/// that something else is broken has made the outage worse.
/// </para>
/// </remarks>
public sealed class ViolationReporter
{
    /// <summary>The most distinct violations tracked between flushes.</summary>
    /// <remarks>
    /// Generous for a real fault, which is usually one or two routes. A service that exceeds it
    /// is either misconfigured wholesale or sending unbounded routing keys, and both are better
    /// served by a truncated report than by an unbounded one.
    /// </remarks>
    public const int MaxDistinct = 256;

    private readonly ConcurrentDictionary<ViolationKey, long> _counts = new();
    private long _dropped;

    /// <summary>How many distinct violations were dropped because the cap was reached.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>How many distinct violations are waiting to be reported.</summary>
    public int Pending => _counts.Count;

    /// <summary>Counts one violation.</summary>
    /// <param name="key">What went wrong.</param>
    /// <remarks>Called on the delivery path, so it does no I/O and takes no lock that blocks.</remarks>
    public void Record(ViolationKey key)
    {
        if (_counts.ContainsKey(key))
        {
            _counts.AddOrUpdate(key, 1, (_, current) => current + 1);
            return;
        }

        if (_counts.Count >= MaxDistinct)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }

        _counts.AddOrUpdate(key, 1, (_, current) => current + 1);
    }

    /// <summary>
    /// Takes everything counted so far, clearing it.
    /// </summary>
    /// <returns>The batch, empty when there is nothing to report.</returns>
    /// <remarks>
    /// <b>Removed one key at a time rather than by <c>Clear()</c>.</b> Clearing would discard
    /// counts recorded between building the snapshot and clearing it — losing violations at
    /// exactly the moment they are most frequent, which is the worst possible sampling bias for
    /// a metric somebody is about to be alerted on.
    /// </remarks>
    public IReadOnlyList<(ViolationKey Key, long Count)> Drain()
    {
        if (_counts.IsEmpty)
        {
            return [];
        }

        var batch = new List<(ViolationKey, long)>(_counts.Count);

        foreach (var key in _counts.Keys)
        {
            if (_counts.TryRemove(key, out var count) && count > 0)
            {
                batch.Add((key, count));
            }
        }

        return batch;
    }
}
