namespace Concordat.Client;

/// <summary>
/// What the client is currently doing, and whether it is still enforcing.
/// </summary>
/// <remarks>
/// <para>
/// <b>The primary detection mechanism for silent degradation.</b> Fail-open is the right
/// default — a consumer must keep draining its queue when the registry blips — but fail-open
/// without a signal is how enforcement stops and nobody notices for a quarter. Everything
/// here exists to make "we are not actually validating anything right now" observable.
/// </para>
/// <para>
/// Deliberately a plain value rather than an <c>IHealthCheck</c>: a client library should not
/// require a hosting stack to report on itself, and a host that wants a health check can
/// trivially wrap this.
/// </para>
/// </remarks>
public sealed record ConcordatClientStatus
{
    /// <summary>Whether warm-up has completed at least once.</summary>
    public bool IsWarm { get; init; }

    /// <summary>When warm-up last succeeded.</summary>
    public DateTimeOffset? WarmedAt { get; init; }

    /// <summary>
    /// How many subjects warm-up loaded, against how many it was told about.
    /// </summary>
    /// <remarks>
    /// A half-completed warm-up that reported success is a real failure mode: the cache looks
    /// populated, the first miss goes to the registry mid-delivery, and the symptom is latency
    /// rather than an error. Reporting both numbers makes the shortfall visible.
    /// </remarks>
    public int SubjectsLoaded { get; init; }

    /// <summary>How many schemas warm-up loaded.</summary>
    public int SchemasLoaded { get; init; }

    /// <summary>How many operations proceeded unenforced because a schema could not be resolved.</summary>
    /// <remarks>
    /// <b>The number to alert on.</b> Non-zero and growing means the fleet is running without
    /// enforcement, whatever the dashboards say about the registry being up.
    /// </remarks>
    public long ResolutionFailures { get; init; }

    /// <summary>How many answers were served from beyond their TTL because a refresh failed.</summary>
    public long StaleServed { get; init; }

    /// <summary>When the registry was last reachable.</summary>
    public DateTimeOffset? LastSuccessfulContact { get; init; }

    /// <summary>Whether the client is currently unable to reach the registry.</summary>
    /// <remarks>
    /// Reserved for the registry <em>failing to answer</em> — 5xx, timeouts, throttling, a dead
    /// socket. A 4xx is the registry answering with a refusal, and is reported through
    /// <see cref="LastFailure"/> without setting this flag.
    /// </remarks>
    public bool IsDegraded { get; init; }

    /// <summary>The most recent refusal, as <c>&lt;status&gt; &lt;concordatCode&gt;</c>.</summary>
    /// <remarks>
    /// An expired API key is the case this exists for. It presents as total enforcement loss
    /// while every dashboard shows the registry healthy, and <c>401 auth_invalid_key</c> is the
    /// one string that ends that investigation immediately.
    /// </remarks>
    public string? LastFailure { get; init; }

    /// <summary>How many routes and queues have a cached contract answer, governed or not.</summary>
    public int RoutesResolved { get; init; }

    /// <summary>
    /// How many of those routes a contract actually governs.
    /// </summary>
    /// <remarks>
    /// <b>Zero against a non-zero <see cref="RoutesResolved"/> is the signal worth reading.</b>
    /// It means the registry answered about every route this service uses and said no contract
    /// covers any of them — a topology nobody has written a contract for, which from every other
    /// signal is indistinguishable from governance working perfectly.
    /// </remarks>
    public int GovernedRoutes { get; init; }

    /// <summary>
    /// How many contract lookups fell back to ungoverned because the registry could not answer.
    /// </summary>
    /// <remarks>
    /// Counted apart from <see cref="ResolutionFailures"/> because the consequence differs. A
    /// schema that will not resolve leaves a message unchecked; a contract that will not resolve
    /// leaves the route governed by the client's own <c>Mode</c>, which may well still be
    /// enforcing. Adding them together would overstate the enforcement hole.
    /// </remarks>
    public long ContractResolutionFailures { get; init; }

    /// <summary>A one-line summary for a log line or a health endpoint.</summary>
    /// <returns>The summary.</returns>
    public override string ToString()
    {
        var state = IsWarm
            ? $"warm since {WarmedAt:O}: {SubjectsLoaded} subjects, {SchemasLoaded} schemas, " +
              $"{GovernedRoutes}/{RoutesResolved} routes governed, " +
              $"{ResolutionFailures} unenforced, {StaleServed} stale"
            : $"cold — warm-up has not completed, {ResolutionFailures} unenforced";

        // Degradation is reported whether or not the client ever warmed. A cold client that
        // cannot reach the registry is the worst case, not an uninteresting one, and hiding it
        // behind "cold" is how it gets read as merely "starting up".
        if (IsDegraded)
        {
            state += " — DEGRADED, registry unreachable";
        }

        return LastFailure is null ? state : $"{state} (last failure: {LastFailure})";
    }
}
