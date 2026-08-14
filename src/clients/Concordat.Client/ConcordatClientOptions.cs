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

    /// <summary>
    /// How long a contract resolution stays fresh (M2.1, deferred to M7).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Longer than <see cref="LatestTtl"/> because the two pointers move on different clocks.
    /// <c>latest</c> advances whenever a pipeline registers a schema; a contract changes when a
    /// person edits a binding or promotes MONITOR to ENFORCE. Sixty seconds is the figure M2.1
    /// specified before contracts existed, and nothing since has argued for a different one.
    /// </para>
    /// <para>
    /// It is also the delay on the central off switch: turning a contract to OFF takes effect
    /// across a fleet within this window, without a redeploy. Shortening it buys a faster switch
    /// at the cost of one resolve call per route per interval.
    /// </para>
    /// </remarks>
    public TimeSpan ContractTtl { get; set; } = TimeSpan.FromMinutes(1);

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

    /// <summary>
    /// This service's name, reported to the registry at warm-up so impact analysis can name it
    /// (M7.4).
    /// </summary>
    /// <remarks>
    /// <b>Opt-in, and null by default.</b> Nothing is reported unless a name is set, because
    /// the registry cannot invent a meaningful one — a machine name or a process id would fill
    /// the service table with rows nobody recognises, which is worse for impact analysis than
    /// an empty table that at least says "nobody has declared themselves".
    /// </remarks>
    public string? ServiceName { get; set; }

    /// <summary>The subjects this service publishes, as <c>subject</c> or <c>subject@selector</c>.</summary>
    /// <remarks>Ignored unless <see cref="ServiceName"/> is set.</remarks>
    public IList<string> Produces { get; } = [];

    /// <summary>
    /// The subjects this service reads, as <c>subject@selector</c> where the selector is
    /// <c>latest</c>, an ordinal, or <c>&gt;=N</c>.
    /// </summary>
    /// <remarks>
    /// <b>Pin or range these if you want a useful answer.</b> A consumer declared as
    /// <c>latest</c> can only ever be reported as "follows the pointer" — the registry does not
    /// know what your code was built against, only what you fetch. An ordinal or a
    /// <c>&gt;=N</c> floor is what lets it tell you that a proposed change would stop you.
    /// </remarks>
    public IList<string> Consumes { get; } = [];

    /// <summary>
    /// The routes this service publishes on, resolved against the environment's contracts in a
    /// single call during warm-up (M7.3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Optional, and an optimisation rather than a requirement.</b> A route that is not
    /// declared is resolved the first time a message goes to it and cached from then on, so
    /// declaring nothing still works — it just moves the first resolve of each route onto the
    /// delivery path, which is exactly what DESIGN §5 says must not happen at steady state.
    /// </para>
    /// <para>
    /// Declaring them is what makes <c>/contracts/resolve</c> worth its batch shape: a
    /// fleet-wide restart asks once per instance for the whole topology rather than once per
    /// route per instance.
    /// </para>
    /// </remarks>
    public IList<PublishRoute> PublishRoutes { get; } = [];

    /// <summary>The queues this service consumes from, resolved during warm-up alongside
    /// <see cref="PublishRoutes"/>.</summary>
    public IList<string> ConsumeQueues { get; } = [];

    /// <summary>
    /// Which broker this service is connected to, when contracts are scoped per broker.
    /// </summary>
    /// <remarks>
    /// Null resolves against bindings that apply to every broker in the environment, which is
    /// the common case. Set it only where the same exchange means different things on different
    /// brokers.
    /// </remarks>
    public Guid? BrokerId { get; set; }

    /// <summary>The virtual host this service is connected to. Defaults to <c>/</c> at the registry.</summary>
    public string? VirtualHost { get; set; }

    /// <summary>Whether a failed service report should fail warm-up.</summary>
    /// <remarks>
    /// Off, and separate from <see cref="RequireWarmUp"/> on purpose. Declaring intent is a
    /// governance nicety; failing to start a service because a bookkeeping call was refused
    /// would put the registry back on the critical path for a reason nobody would accept.
    /// </remarks>
    public bool RequireServiceRegistration { get; set; }

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

        if (ContractTtl <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{nameof(ContractTtl)} must be positive.");
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
