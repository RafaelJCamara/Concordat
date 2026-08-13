using System.Collections.Concurrent;
using Concordat.Domain.Registry;

namespace Concordat.Client;

/// <summary>A cached schema, with the text every validator needs.</summary>
/// <param name="SchemaId">The content-addressed id.</param>
/// <param name="Format">The schema language.</param>
/// <param name="CanonicalBody">The canonical text.</param>
public sealed record CachedSchema(SchemaId SchemaId, SchemaFormat Format, string CanonicalBody);

/// <summary>What the client currently knows about a subject's tip.</summary>
/// <param name="SchemaId">The schema the pointer resolves to.</param>
/// <param name="Ordinal">The version ordinal.</param>
/// <param name="FetchedAt">When this was last confirmed with the registry.</param>
public sealed record CachedLatest(SchemaId SchemaId, int Ordinal, DateTimeOffset FetchedAt);

/// <summary>
/// The client's two-tier cache.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tier one is immutable and never expires.</b> A schema id is a hash of its content
/// (ADR-015), so <c>schema-id → schema</c> cannot go stale — that is guaranteed by the
/// identity scheme rather than assumed, which is what makes "cached forever" safe rather than
/// merely convenient.
/// </para>
/// <para>
/// <b>Tier two is the mutable pointer</b>, <c>subject → latest</c>, and it is the only thing
/// with a TTL. It is also the only thing that can serve a stale answer, which it does
/// deliberately when the registry is unreachable — bounded by a staleness budget, because
/// unbounded stale is how a fleet quietly enforces last month's contract.
/// </para>
/// <para>
/// Hand-rolled over <see cref="ConcurrentDictionary{TKey,TValue}"/> rather than
/// <c>HybridCache</c> or <c>IMemoryCache</c>: the tiers have genuinely different semantics,
/// the immutable tier wants no eviction policy at all, and a client library should not drag a
/// caching stack and its DI conventions into every host that references it.
/// </para>
/// </remarks>
public sealed class SchemaCache(TimeProvider clock, ConcordatClientOptions options)
{
    private readonly ConcurrentDictionary<string, CachedSchema> _schemas = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CachedLatest> _latest = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, NegativeEntry> _misses = new(StringComparer.Ordinal);

    /// <summary>How many immutable schemas are held.</summary>
    public int SchemaCount => _schemas.Count;

    /// <summary>How many subject tips are held, fresh or stale.</summary>
    public int LatestCount => _latest.Count;

    /// <summary>Looks up a schema by its content-addressed id.</summary>
    /// <param name="id">The id.</param>
    /// <param name="schema">The schema, when held.</param>
    /// <returns>Whether it was held.</returns>
    public bool TryGetSchema(SchemaId id, out CachedSchema? schema)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _schemas.TryGetValue(id.Value, out schema);
    }

    /// <summary>Stores a schema. Immutable, so a repeat store is a no-op.</summary>
    /// <param name="schema">The schema.</param>
    public void PutSchema(CachedSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        _schemas.TryAdd(schema.SchemaId.Value, schema);
    }

    /// <summary>Looks up a subject's tip.</summary>
    /// <param name="subject">The subject.</param>
    /// <param name="latest">The tip, when held and usable.</param>
    /// <returns>Whether the answer is fresh, stale but usable, or absent.</returns>
    public CacheFreshness TryGetLatest(SubjectName subject, out CachedLatest? latest)
    {
        ArgumentNullException.ThrowIfNull(subject);

        if (!_latest.TryGetValue(subject.Value, out latest))
        {
            return CacheFreshness.Absent;
        }

        var age = clock.GetUtcNow() - latest.FetchedAt;

        if (age <= options.LatestTtl)
        {
            return CacheFreshness.Fresh;
        }

        // Past the TTL but inside the staleness budget. Usable only when a refresh has just
        // failed — the caller decides, because serving stale on a healthy registry would be a
        // bug rather than a mercy.
        return age <= options.StalenessBudget ? CacheFreshness.Stale : CacheFreshness.Expired;
    }

    /// <summary>Records a subject's tip.</summary>
    /// <param name="subject">The subject.</param>
    /// <param name="latest">The tip.</param>
    public void PutLatest(SubjectName subject, CachedLatest latest)
    {
        ArgumentNullException.ThrowIfNull(subject);
        _latest[subject.Value] = latest;
        _misses.TryRemove(subject.Value, out _);
    }

    /// <summary>Whether a known-missing subject should still be treated as missing.</summary>
    /// <param name="subject">The subject.</param>
    /// <returns><see langword="true"/> while the negative entry is still in force.</returns>
    /// <remarks>
    /// Negative caching stops a missing subject retry-storming the registry during a cold
    /// start, when every instance is asking about the same unknown name at once.
    /// </remarks>
    public bool IsKnownMissing(SubjectName subject)
    {
        ArgumentNullException.ThrowIfNull(subject);

        if (!_misses.TryGetValue(subject.Value, out var entry))
        {
            return false;
        }

        // Expired entries are retained, not removed. Removing them would reset the backoff on
        // every expiry, so a name missing because of a configuration typo would be asked about
        // at a fixed rate forever instead of backing off. The caller asks again either way,
        // which is how a subject created after the miss becomes visible without a restart.
        return clock.GetUtcNow() < entry.Until;
    }

    /// <summary>Records that a subject does not exist, backing off on repeats.</summary>
    /// <param name="subject">The subject.</param>
    public void PutMissing(SubjectName subject)
    {
        ArgumentNullException.ThrowIfNull(subject);

        _misses.AddOrUpdate(
            subject.Value,
            _ => new NegativeEntry(clock.GetUtcNow() + options.NegativeTtl, options.NegativeTtl),
            (_, existing) =>
            {
                // Doubling, capped. A name that is missing because of a typo in configuration
                // would otherwise be asked about forever at a fixed rate.
                var next = existing.Backoff * 2;
                if (next > options.MaxNegativeTtl)
                {
                    next = options.MaxNegativeTtl;
                }

                return new NegativeEntry(clock.GetUtcNow() + next, next);
            });
    }

    /// <summary>Empties every tier. For tests and for an explicit operator-triggered refresh.</summary>
    public void Clear()
    {
        _schemas.Clear();
        _latest.Clear();
        _misses.Clear();
    }

    private readonly record struct NegativeEntry(DateTimeOffset Until, TimeSpan Backoff);
}

/// <summary>How usable a cached answer is.</summary>
public enum CacheFreshness
{
    /// <summary>Nothing cached.</summary>
    Absent = 1,

    /// <summary>Within its TTL.</summary>
    Fresh = 2,

    /// <summary>Past its TTL but inside the staleness budget — usable if a refresh fails.</summary>
    Stale = 3,

    /// <summary>Past the staleness budget. Must not be served.</summary>
    Expired = 4,
}
