using System.Collections.Concurrent;
using Concordat.Domain.Contracts;
using Concordat.Domain.Registry;

namespace Concordat.Client;

/// <summary>One route a publisher sends on.</summary>
/// <param name="Exchange">The exchange.</param>
/// <param name="RoutingKey">A concrete routing key, never a pattern — the registry does the matching.</param>
public readonly record struct PublishRoute(string Exchange, string RoutingKey);

/// <summary>
/// What the environment's contracts say about one route or queue (M7.3).
/// </summary>
/// <param name="Contract">The contract that matched, or <see langword="null"/> when nothing governs it.</param>
/// <param name="Enforcement">How much that contract may do. <c>Off</c> when nothing matched.</param>
/// <param name="Subjects">The subjects the contract permits, with the versions of each it accepts.</param>
/// <param name="FetchedAt">When the registry last confirmed this.</param>
public sealed record ResolvedRoute(
    string? Contract,
    EnforcementMode Enforcement,
    IReadOnlyList<SubjectRef> Subjects,
    DateTimeOffset FetchedAt)
{
    /// <summary>
    /// Whether a contract actually matched.
    /// </summary>
    /// <remarks>
    /// <b>Not the same as <c>Enforcement is not Off</c>,</b> and the difference decides
    /// behaviour. A governed route in <c>OFF</c> is an operator saying "stop checking this"; an
    /// ungoverned route is the registry saying "nobody has written a contract for this yet", and
    /// falls back to the client's own <c>Mode</c>. Collapsing the two would make writing a
    /// contract and setting it to OFF indistinguishable from never writing one — so the central
    /// off switch would silently do nothing wherever a client had opted into local enforcement.
    /// </remarks>
    public bool IsGoverned => Contract is not null;

    /// <summary>The answer for a route no contract covers.</summary>
    /// <param name="at">When this was established.</param>
    /// <returns>An ungoverned answer.</returns>
    public static ResolvedRoute Ungoverned(DateTimeOffset at) => new(null, EnforcementMode.Off, [], at);
}

/// <summary>
/// The client's cache of contract resolutions.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="SchemaCache"/> because it is keyed by <em>topology</em> —
/// <c>(exchange, routing key)</c> and queue name — rather than by subject or schema id, and
/// because it expires on its own clock. Contracts are the third mutable pointer in the system,
/// after <c>subject → latest</c>: an operator can change a contract's enforcement or its
/// permitted subjects at any moment, and the point of doing so centrally is that fleets pick it
/// up without a redeploy.
/// </para>
/// <para>
/// <b>Ungoverned answers are cached too, and that is deliberate.</b> Most routes in a real
/// deployment have no contract, so caching only the governed ones would send every message on
/// every uncovered route back to the registry — turning the feature meant to enforce DESIGN
/// §5's hard rule into the thing that breaks it. A negative answer here is a real answer.
/// </para>
/// <para>
/// The TTL is longer than <see cref="ConcordatClientOptions.LatestTtl"/> because contracts
/// change on human timescales — someone edits a binding or promotes MONITOR to ENFORCE — while
/// <c>latest</c> moves every time a schema is registered by a pipeline.
/// </para>
/// </remarks>
public sealed class ContractCache(TimeProvider clock, ConcordatClientOptions options)
{
    private readonly ConcurrentDictionary<PublishRoute, ResolvedRoute> _publishes = new();
    private readonly ConcurrentDictionary<string, ResolvedRoute> _consumes = new(StringComparer.Ordinal);

    /// <summary>How many publish routes are held, governed or not.</summary>
    public int PublishRouteCount => _publishes.Count;

    /// <summary>How many queues are held, governed or not.</summary>
    public int ConsumeRouteCount => _consumes.Count;

    /// <summary>How many held routes a contract actually governs.</summary>
    /// <remarks>
    /// Reported separately from the totals so an operator can see at a glance that a service
    /// resolved forty routes and none of them are covered — which looks identical to working
    /// enforcement in every other signal.
    /// </remarks>
    public int GovernedCount =>
        _publishes.Values.Count(r => r.IsGoverned) + _consumes.Values.Count(r => r.IsGoverned);

    /// <summary>Looks up what governs a publish route.</summary>
    /// <param name="route">The exchange and routing key.</param>
    /// <param name="resolved">The answer, when held.</param>
    /// <returns>How usable the answer is.</returns>
    public CacheFreshness TryGetPublish(PublishRoute route, out ResolvedRoute? resolved) =>
        Freshness(_publishes.TryGetValue(route, out var hit) ? hit : null, out resolved);

    /// <summary>Looks up what governs a queue.</summary>
    /// <param name="queue">The queue name.</param>
    /// <param name="resolved">The answer, when held.</param>
    /// <returns>How usable the answer is.</returns>
    public CacheFreshness TryGetConsume(string queue, out ResolvedRoute? resolved)
    {
        ArgumentNullException.ThrowIfNull(queue);
        return Freshness(_consumes.TryGetValue(queue, out var hit) ? hit : null, out resolved);
    }

    /// <summary>Records what governs a publish route.</summary>
    /// <param name="route">The route.</param>
    /// <param name="resolved">The answer.</param>
    public void PutPublish(PublishRoute route, ResolvedRoute resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        _publishes[route] = resolved;
    }

    /// <summary>Records what governs a queue.</summary>
    /// <param name="queue">The queue.</param>
    /// <param name="resolved">The answer.</param>
    public void PutConsume(string queue, ResolvedRoute resolved)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(resolved);
        _consumes[queue] = resolved;
    }

    /// <summary>Empties the cache. For tests and for an explicit operator-triggered refresh.</summary>
    public void Clear()
    {
        _publishes.Clear();
        _consumes.Clear();
    }

    private CacheFreshness Freshness(ResolvedRoute? hit, out ResolvedRoute? resolved)
    {
        resolved = hit;

        if (hit is null)
        {
            return CacheFreshness.Absent;
        }

        var age = clock.GetUtcNow() - hit.FetchedAt;

        if (age <= options.ContractTtl)
        {
            return CacheFreshness.Fresh;
        }

        // Past the TTL but inside the staleness budget. Usable only when a refresh has just
        // failed, and the caller decides — the same rule as `subject → latest`, and for the same
        // reason: serving stale against a healthy registry would be a bug rather than a mercy.
        return age <= options.StalenessBudget ? CacheFreshness.Stale : CacheFreshness.Expired;
    }
}
