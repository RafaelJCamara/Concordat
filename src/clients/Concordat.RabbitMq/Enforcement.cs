using System.Collections.Concurrent;

namespace Concordat.RabbitMq;

/// <summary>How much Concordat is allowed to do about what it finds.</summary>
/// <remarks>
/// The three-step adoption path, and the reason it has three steps rather than a boolean: no
/// team turns on enforcement across an existing estate in one change. <see cref="Monitor"/> is
/// where the argument gets won, by showing which publishers are already violating a contract
/// nobody had written down.
/// </remarks>
public enum EnforcementMode
{
    /// <summary>Do nothing. No envelope is written, no payload is read.</summary>
    /// <remarks>
    /// A real setting, not a debug one: it is the switch an operator reaches for at 3am, and it
    /// must cost nothing and touch nothing.
    /// </remarks>
    Off = 1,

    /// <summary>
    /// Stamp the envelope and report violations, but never block or quarantine anything.
    /// </summary>
    /// <remarks>
    /// The default for adoption. Publishers still get an envelope — consumers downstream can
    /// start reading identity immediately — while a violation only ever produces a report.
    /// </remarks>
    Monitor = 2,

    /// <summary>Block invalid publishes and quarantine invalid deliveries.</summary>
    Enforce = 3,
}

/// <summary>Which side of the wire an enforcement decision was taken on.</summary>
public enum EnforcementSide
{
    /// <summary>Publishing.</summary>
    Publish = 1,

    /// <summary>Consuming.</summary>
    Consume = 2,
}

/// <summary>What Concordat did about a message.</summary>
public enum EnforcementOutcome
{
    /// <summary>The payload was checked against its schema and conformed.</summary>
    Valid = 1,

    /// <summary>
    /// The message passed through without being checked, because no schema could be resolved.
    /// </summary>
    /// <remarks>
    /// <b>The outcome to alert on.</b> Not an error — un-instrumented publishers and registry
    /// blips both land here — but a fleet running mostly unenforced looks exactly like a fleet
    /// running enforced unless somebody is counting.
    /// </remarks>
    Unenforced = 2,

    /// <summary>A violation was found and allowed through, because the mode is Monitor.</summary>
    Observed = 3,

    /// <summary>A publish was refused.</summary>
    Blocked = 4,

    /// <summary>A delivery was routed to the quarantine exchange and not retried.</summary>
    Quarantined = 5,

    /// <summary>
    /// A violation was found, could not be quarantined, and was delivered anyway.
    /// </summary>
    /// <remarks>
    /// The deliberate last resort. See <see cref="ConcordatConsumer"/>: silently dropping the
    /// message or looping it forever are both worse than handing the application something
    /// invalid and saying so loudly.
    /// </remarks>
    QuarantineFailed = 6,
}

/// <summary>One enforcement decision, with everything needed to act on it.</summary>
/// <param name="Side">Publish or consume.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="Code">A stable <c>concordatCode</c>, or null when nothing went wrong.</param>
/// <param name="Detail">A human-readable explanation.</param>
/// <param name="Subject">The subject, when known.</param>
/// <param name="SchemaId">The schema id, when known.</param>
/// <param name="Exchange">The exchange.</param>
/// <param name="RoutingKey">The routing key.</param>
public sealed record EnforcementEvent(
    EnforcementSide Side,
    EnforcementOutcome Outcome,
    string? Code,
    string Detail,
    string? Subject = null,
    string? SchemaId = null,
    string? Exchange = null,
    string? RoutingKey = null);

/// <summary>Receives every enforcement decision.</summary>
/// <remarks>
/// A seam rather than a logger or a metrics dependency: a client library should not choose a
/// host's observability stack. The default implementation counts, so
/// <see cref="EnforcementMode.Monitor"/> is useful with nothing wired up at all.
/// </remarks>
public interface IEnforcementObserver
{
    /// <summary>Called once per message, whatever the outcome.</summary>
    /// <param name="decision">What happened.</param>
    void Observe(EnforcementEvent decision);
}

/// <summary>Counts outcomes. The default observer.</summary>
/// <remarks>
/// Exists so that turning enforcement on in Monitor mode produces something to look at
/// immediately. A monitoring mode whose findings go nowhere is indistinguishable from Off.
/// </remarks>
public sealed class EnforcementCounters : IEnforcementObserver
{
    private readonly ConcurrentDictionary<(EnforcementSide, EnforcementOutcome), long> _counts = new();
    private readonly ConcurrentQueue<EnforcementEvent> _recent = new();

    /// <summary>How many recent violations are retained for inspection.</summary>
    public const int RecentCapacity = 32;

    /// <inheritdoc />
    public void Observe(EnforcementEvent decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        _counts.AddOrUpdate((decision.Side, decision.Outcome), 1, (_, current) => current + 1);

        if (decision.Outcome is EnforcementOutcome.Valid)
        {
            return;
        }

        // A bounded sample of what went wrong. A count tells an operator that something is
        // failing; the first few examples tell them which publisher to go and talk to.
        _recent.Enqueue(decision);
        while (_recent.Count > RecentCapacity && _recent.TryDequeue(out _))
        {
            // Trimming to capacity.
        }
    }

    /// <summary>How many messages had this outcome on this side.</summary>
    /// <param name="side">Publish or consume.</param>
    /// <param name="outcome">The outcome.</param>
    /// <returns>The count.</returns>
    public long Count(EnforcementSide side, EnforcementOutcome outcome) =>
        _counts.TryGetValue((side, outcome), out var count) ? count : 0;

    /// <summary>The most recent non-conforming messages, oldest first.</summary>
    public IReadOnlyList<EnforcementEvent> Recent => [.. _recent];

    /// <summary>A one-line summary for a log line or a health endpoint.</summary>
    /// <returns>The summary.</returns>
    public override string ToString()
    {
        var parts = _counts
            .OrderBy(c => c.Key.Item1)
            .ThenBy(c => c.Key.Item2)
            .Select(c => $"{c.Key.Item1}/{c.Key.Item2}={c.Value}");

        return _counts.IsEmpty ? "no messages seen" : string.Join(", ", parts);
    }
}
