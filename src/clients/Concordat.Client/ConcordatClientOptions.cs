namespace Concordat.Client;

/// <summary>
/// What the client does when it <em>cannot reach a verdict</em>.
/// </summary>
/// <remarks>
/// <para>
/// This governs <b>resolution</b> failures only — the registry is unreachable, a subject is
/// unknown, a schema id cannot be fetched. It deliberately does <b>not</b> govern validation
/// verdicts: a payload that fails its schema is rejected under either setting.
/// </para>
/// <para>
/// Conflating the two is the failure that matters. If <c>FailOpen</c> also swallowed
/// validation failures, a registry outage would silently disable enforcement <em>and</em> a
/// genuine schema violation would sail through — and nothing would distinguish the two
/// afterwards.
/// </para>
/// </remarks>
public enum ResolutionFailureMode
{
    /// <summary>
    /// Proceed without enforcement when the schema cannot be resolved. Availability over
    /// certainty — the right default for a consumer that must keep draining a queue.
    /// </summary>
    /// <remarks>
    /// Records the failure in <see cref="ConcordatClientStatus"/>. Fail-open without a signal
    /// is how enforcement dies quietly and nobody notices for a quarter.
    /// </remarks>
    FailOpen = 1,

    /// <summary>
    /// Refuse the operation when the schema cannot be resolved. Certainty over availability.
    /// </summary>
    /// <remarks>
    /// On publish this throws. On consume it quarantines, which converts a registry blip into
    /// permanent message displacement — appropriate only where an unvalidated message is worse
    /// than a delayed one.
    /// </remarks>
    FailClosed = 2,
}

/// <summary>Configuration for <see cref="ConcordatClient"/>.</summary>
public sealed class ConcordatClientOptions
{
    /// <summary>The registry's base address, for example <c>https://concordat.internal</c>.</summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>The environment name that appears in <c>/v1/environments/{env}/…</c>.</summary>
    public string Environment { get; set; } = "default";

    /// <summary>The API key sent with every request.</summary>
    public string? ApiKey { get; set; }

    /// <summary>What to do when a schema cannot be resolved.</summary>
    public ResolutionFailureMode OnResolutionFailure { get; set; } = ResolutionFailureMode.FailOpen;

    /// <summary>
    /// How long a <c>subject → latest</c> answer stays fresh.
    /// </summary>
    /// <remarks>
    /// Short, because <c>latest</c> is the one mutable pointer in the system. Content-addressed
    /// entries have no TTL at all — they cannot change by construction.
    /// </remarks>
    public TimeSpan LatestTtl { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a "no such subject" answer is remembered.</summary>
    /// <remarks>
    /// Negative caching exists so a missing subject cannot retry-storm the registry during a
    /// cold start. It is short, and doubles on repeated misses up to
    /// <see cref="MaxNegativeTtl"/>, so a subject created later becomes visible without a
    /// restart.
    /// </remarks>
    public TimeSpan NegativeTtl { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The ceiling on the negative-cache backoff.</summary>
    public TimeSpan MaxNegativeTtl { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long a stale <c>latest</c> may still be served after the registry starts failing.
    /// </summary>
    /// <remarks>
    /// Serving stale on error is right — far better than failing delivery over a blip. Serving
    /// it <em>forever</em> is how a fleet ends up enforcing a contract from last month without
    /// anyone noticing, so the budget is bounded and its expiry is visible in
    /// <see cref="ConcordatClientStatus"/>.
    /// </remarks>
    public TimeSpan StalenessBudget { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The largest random delay added before warm-up.
    /// </summary>
    /// <remarks>
    /// A fleet-wide rolling restart empties every cache at the same instant. Without jitter
    /// every instance calls <c>/bootstrap</c> simultaneously and the registry — which sits on
    /// the deserialize path — becomes the outage.
    /// </remarks>
    public TimeSpan WarmUpJitter { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Whether the client refuses to start until warm-up succeeds.</summary>
    /// <remarks>
    /// Off by default: a client that cannot start because the registry is down has put the
    /// registry back on the critical path, which is what warm-up exists to avoid.
    /// </remarks>
    public bool RequireWarmUp { get; set; }

    /// <summary>Validates the options, throwing on anything that cannot work.</summary>
    /// <exception cref="InvalidOperationException">A required value is missing or nonsensical.</exception>
    public void Validate()
    {
        if (BaseAddress is null)
        {
            throw new InvalidOperationException($"{nameof(BaseAddress)} is required.");
        }

        if (string.IsNullOrWhiteSpace(Environment))
        {
            throw new InvalidOperationException($"{nameof(Environment)} is required.");
        }

        if (LatestTtl <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{nameof(LatestTtl)} must be positive.");
        }

        if (NegativeTtl <= TimeSpan.Zero || MaxNegativeTtl < NegativeTtl)
        {
            throw new InvalidOperationException(
                $"{nameof(NegativeTtl)} must be positive and no greater than {nameof(MaxNegativeTtl)}.");
        }

        if (WarmUpJitter < TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{nameof(WarmUpJitter)} cannot be negative.");
        }
    }
}
