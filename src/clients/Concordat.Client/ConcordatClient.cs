using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Concordat.Domain.Contracts;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;

namespace Concordat.Client;

/// <summary>The registry, as the client sees it.</summary>
public interface IConcordatClient
{
    /// <summary>What the client is currently doing, and whether it is still enforcing.</summary>
    ConcordatClientStatus Status { get; }

    /// <summary>Loads everything needed to serve without touching the registry again.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The status after warm-up.</returns>
    Task<ConcordatClientStatus> WarmUpAsync(CancellationToken cancellationToken = default);

    /// <summary>Resolves a schema by its content-addressed id.</summary>
    /// <param name="schemaId">The id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The schema, or <see langword="null"/> when it cannot be resolved.</returns>
    ValueTask<CachedSchema?> GetSchemaAsync(SchemaId schemaId, CancellationToken cancellationToken = default);

    /// <summary>Resolves a subject's current tip.</summary>
    /// <param name="subject">The subject.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The tip, or <see langword="null"/> when the subject is unknown or unreachable.</returns>
    ValueTask<CachedLatest?> GetLatestAsync(SubjectName subject, CancellationToken cancellationToken = default);

    /// <summary>Resolves what the environment's contracts say about a publish route (M7.3).</summary>
    /// <param name="route">The exchange and concrete routing key.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The governing contract, or an ungoverned answer.</returns>
    /// <remarks>
    /// <b>Never returns null and never throws for a resolution failure.</b> A registry that
    /// cannot be reached degrades to <see cref="ResolvedRoute.Ungoverned"/>, which hands the
    /// decision back to the client's own <c>Mode</c> — the behaviour that existed before
    /// contracts. See <see cref="ConcordatClient"/> for why this does not honour
    /// <see cref="ResolutionFailureMode.FailClosed"/>.
    /// </remarks>
    ValueTask<ResolvedRoute> GetPublishRouteAsync(
        PublishRoute route, CancellationToken cancellationToken = default);

    /// <summary>Resolves what the environment's contracts say about a queue (M7.3).</summary>
    /// <param name="queue">The queue name.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The governing contract, or an ungoverned answer.</returns>
    ValueTask<ResolvedRoute> GetConsumeRouteAsync(
        string queue, CancellationToken cancellationToken = default);
}

/// <summary>
/// A caching HTTP client for the registry.
/// </summary>
/// <remarks>
/// <para>
/// DESIGN §5's hard rule is that after warm-up the registry is never in the critical path of
/// message delivery. <b>That rule needs a caveat, and it is better stated here than
/// discovered in production:</b> <c>/bootstrap</c> returns each subject's <em>latest</em>
/// schema, so a consumer handed a message pinned to an <em>older</em> schema id has never
/// seen it and must fetch it. Cache hits never touch the registry; a genuine first-sight miss
/// does, once, and then never again because content-addressed entries are immutable.
/// </para>
/// <para>
/// The client is therefore honest about three states rather than two: served from cache,
/// fetched once, or unresolvable. Only the third engages
/// <see cref="ResolutionFailureMode"/>, and every occurrence is counted in
/// <see cref="Status"/>.
/// </para>
/// <para>
/// <b>Contract resolution is deliberately outside <see cref="ResolutionFailureMode"/></b>
/// (M7.3). That setting answers "can I check this payload?" — a question whose honest failure
/// is to refuse the message. Contract resolution answers "is anyone governing this route?",
/// and the honest failure there is to fall back to the client's own <c>Mode</c>, which is the
/// behaviour that existed before contracts. Wiring it to <c>FailClosed</c> would mean a
/// registry blip refused every publish in a fail-closed service, including on the routes no
/// contract has ever covered — an outage manufactured out of a governance lookup.
/// </para>
/// </remarks>
public sealed class ConcordatClient : IConcordatClient, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ConcordatClientOptions _options;
    private readonly SchemaCache _cache;
    private readonly ContractCache _contracts;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _warmUpGate = new(1, 1);

    private long _resolutionFailures;
    private long _contractFailures;
    private long _staleServed;
    private DateTimeOffset? _warmedAt;
    private DateTimeOffset? _lastContact;
    private int _subjectsLoaded;
    private int _schemasLoaded;
    private bool _degraded;
    private string? _lastFailure;

    /// <summary>Creates a client.</summary>
    /// <param name="http">The transport. Its <c>BaseAddress</c> is set from the options if unset.</param>
    /// <param name="options">Configuration.</param>
    /// <param name="clock">Time source, injected so TTL behaviour is testable without waiting.</param>
    public ConcordatClient(HttpClient http, ConcordatClientOptions options, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _http = http;
        _options = options;
        _clock = clock ?? TimeProvider.System;
        _cache = new SchemaCache(_clock, options);
        _contracts = new ContractCache(_clock, options);

        _http.BaseAddress ??= options.BaseAddress;

        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {options.ApiKey}");
        }
    }

    /// <inheritdoc />
    public ConcordatClientStatus Status => new()
    {
        IsWarm = _warmedAt is not null,
        WarmedAt = _warmedAt,
        SubjectsLoaded = _subjectsLoaded,
        SchemasLoaded = _schemasLoaded,
        ResolutionFailures = Interlocked.Read(ref _resolutionFailures),
        StaleServed = Interlocked.Read(ref _staleServed),
        LastSuccessfulContact = _lastContact,
        IsDegraded = _degraded,
        LastFailure = _lastFailure,
        RoutesResolved = _contracts.PublishRouteCount + _contracts.ConsumeRouteCount,
        GovernedRoutes = _contracts.GovernedCount,
        ContractResolutionFailures = Interlocked.Read(ref _contractFailures),
    };

    /// <summary>The cache, exposed so a host can report on it.</summary>
    public SchemaCache Cache => _cache;

    /// <summary>The contract cache, exposed so a host can report on it.</summary>
    public ContractCache Contracts => _contracts;

    /// <inheritdoc />
    public async Task<ConcordatClientStatus> WarmUpAsync(CancellationToken cancellationToken = default)
    {
        await _warmUpGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await JitterAsync(cancellationToken).ConfigureAwait(false);

            var response = await _http
                .PostAsync($"/v1/environments/{_options.Environment}/bootstrap", null, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var problem = await ReadProblemAsync(response, cancellationToken).ConfigureAwait(false);
                RecordRefusal(response.StatusCode, problem);

                if (_options.RequireWarmUp)
                {
                    throw new ConcordatException(
                        problem.Code ?? "warm_up_failed",
                        $"Warm-up failed with {(int)response.StatusCode}: {problem.Describe()} " +
                        "RequireWarmUp is on, so the client will not start unenforced.");
                }

                return Status;
            }

            var payload = await response.Content
                .ReadFromJsonAsync<BootstrapPayload>(Json, cancellationToken)
                .ConfigureAwait(false);

            Ingest(payload);

            _warmedAt = _clock.GetUtcNow();
            _lastContact = _warmedAt;
            _degraded = false;

            await DeclareServiceAsync(cancellationToken).ConfigureAwait(false);

            // The declared topology in one round trip, which is what the batch shape of
            // /contracts/resolve exists for. A failure here is not fatal and not retried: every
            // route resolves on demand the first time a message uses it, so the cost of the
            // registry being unavailable right now is one lookup later, not a broken client.
            await ResolveAsync(
                [.. _options.PublishRoutes], [.. _options.ConsumeQueues], cancellationToken)
                .ConfigureAwait(false);

            return Status;
        }
        finally
        {
            _warmUpGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<CachedSchema?> GetSchemaAsync(
        SchemaId schemaId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schemaId);

        // Immutable by construction (ADR-015), so a hit is always correct and never expires.
        if (_cache.TryGetSchema(schemaId, out var cached))
        {
            return cached;
        }

        try
        {
            var response = await _http
                .GetAsync($"/v1/schemas/{schemaId.Value}", cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.NotFound)
            {
                _lastContact = _clock.GetUtcNow();
                return Unresolvable(
                    ConcordatCodes.SchemaNotFound,
                    $"The registry does not know schema {schemaId.Value}.");
            }

            if (!response.IsSuccessStatusCode)
            {
                var problem = await ReadProblemAsync(response, cancellationToken).ConfigureAwait(false);
                RecordRefusal(response.StatusCode, problem);
                return Unresolvable(
                    problem.Code ?? ConcordatCodes.SchemaUnresolvable,
                    $"The registry refused the lookup of schema {schemaId.Value}: {problem.Describe()}");
            }

            _lastContact = _clock.GetUtcNow();
            _degraded = false;
            _lastFailure = null;

            var body = await response.Content
                .ReadFromJsonAsync<SchemaPayload>(Json, cancellationToken)
                .ConfigureAwait(false);

            if (body is null || !TryParseFormat(body.Format, out var format))
            {
                return Unresolvable(
                    ConcordatCodes.EnvelopeFormatUnknown,
                    $"Schema {schemaId.Value} declares a format this client cannot handle.");
            }

            var schema = new CachedSchema(schemaId, format, body.Schema);
            _cache.PutSchema(schema);
            return schema;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Unreachable, not absent. The distinction matters: absent is a producer bug,
            // unreachable is an operational condition that must not look like one.
            _degraded = true;
            return Unresolvable(
                ConcordatCodes.SchemaUnresolvable,
                $"The registry is unreachable, so schema {schemaId.Value} could not be resolved.");
        }
    }

    /// <inheritdoc />
    public async ValueTask<CachedLatest?> GetLatestAsync(
        SubjectName subject, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);

        var freshness = _cache.TryGetLatest(subject, out var cached);
        if (freshness is CacheFreshness.Fresh)
        {
            return cached;
        }

        if (_cache.IsKnownMissing(subject))
        {
            // Short-circuits the request, not the accounting. Every message for an unregistered
            // subject is an unenforced message, and a counter that recorded the first one only
            // would report a standing enforcement hole as a single historic blip.
            return UnresolvableLatest(
                ConcordatCodes.SubjectNotFound,
                $"The registry does not know subject {subject.Value}.");
        }

        try
        {
            var response = await _http
                .GetAsync(
                    $"/v1/environments/{_options.Environment}/subjects/{subject.Value}/versions/latest",
                    cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.NotFound)
            {
                // A definite answer, so it is cached — with backoff, so a name that is missing
                // because of a configuration typo is not asked about forever at a fixed rate.
                _cache.PutMissing(subject);
                _lastContact = _clock.GetUtcNow();
                return UnresolvableLatest(
                    ConcordatCodes.SubjectNotFound,
                    $"The registry does not know subject {subject.Value}.");
            }

            if (!response.IsSuccessStatusCode)
            {
                var problem = await ReadProblemAsync(response, cancellationToken).ConfigureAwait(false);
                RecordRefusal(response.StatusCode, problem);
                return UnresolvableLatest(
                    problem.Code ?? ConcordatCodes.SchemaUnresolvable,
                    $"The registry refused the lookup of subject {subject.Value}: {problem.Describe()}");
            }

            _lastContact = _clock.GetUtcNow();
            _degraded = false;
            _lastFailure = null;

            var body = await response.Content
                .ReadFromJsonAsync<VersionPayload>(Json, cancellationToken)
                .ConfigureAwait(false);

            var id = body is null ? null : SchemaId.Create(body.SchemaId);
            if (id is null || id.IsFailure)
            {
                return UnresolvableLatest(
                    ConcordatCodes.SchemaIdMalformed,
                    $"The registry returned an unusable schema id for subject {subject.Value}.");
            }

            var latest = new CachedLatest(id.Value, body!.Ordinal, _clock.GetUtcNow());
            _cache.PutLatest(subject, latest);
            return latest;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _degraded = true;

            // Stale beats failing delivery over a blip — but only inside the budget, and the
            // fact that it happened is counted. Unbounded stale is how a fleet ends up
            // enforcing last month's contract with nobody aware.
            if (freshness is CacheFreshness.Stale && cached is not null)
            {
                Interlocked.Increment(ref _staleServed);
                return cached;
            }

            return UnresolvableLatest(
                ConcordatCodes.SchemaUnresolvable,
                $"The registry is unreachable, so subject {subject.Value} could not be resolved.");
        }
    }

    /// <inheritdoc />
    public async ValueTask<ResolvedRoute> GetPublishRouteAsync(
        PublishRoute route, CancellationToken cancellationToken = default)
    {
        var freshness = _contracts.TryGetPublish(route, out var cached);

        if (freshness is CacheFreshness.Fresh)
        {
            return cached!;
        }

        var outcome = await ResolveAsync([route], [], cancellationToken).ConfigureAwait(false);

        // The resolved value, not a re-read of the cache. Reading it back and demanding it be
        // Fresh made a successful resolve depend on the clock not having moved between the write
        // and the read — so a short TTL, or a pause on a loaded machine, would silently discard a
        // perfectly good answer and report the route as ungoverned. That turns enforcement off.
        return outcome is not null ? outcome.Publishes[0] : Fallback(freshness, cached);
    }

    /// <inheritdoc />
    public async ValueTask<ResolvedRoute> GetConsumeRouteAsync(
        string queue, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queue);

        var freshness = _contracts.TryGetConsume(queue, out var cached);

        if (freshness is CacheFreshness.Fresh)
        {
            return cached!;
        }

        var outcome = await ResolveAsync([], [queue], cancellationToken).ConfigureAwait(false);

        return outcome is not null ? outcome.Consumes[0] : Fallback(freshness, cached);
    }

    /// <summary>
    /// What to answer when a resolve failed.
    /// </summary>
    /// <remarks>
    /// Stale beats ungoverned, and by a wide margin. A route governed in ENFORCE that degrades
    /// to "ungoverned" during a registry blip silently stops enforcing, which is the exact
    /// failure this whole subsystem exists to prevent — so a cached answer inside the staleness
    /// budget is served and counted. Past the budget there is nothing honest left to say, and
    /// the client falls back to its own <c>Mode</c>.
    /// </remarks>
    private ResolvedRoute Fallback(CacheFreshness freshness, ResolvedRoute? cached)
    {
        if (freshness is CacheFreshness.Stale && cached is not null)
        {
            Interlocked.Increment(ref _staleServed);
            return cached;
        }

        Interlocked.Increment(ref _contractFailures);
        return ResolvedRoute.Ungoverned(_clock.GetUtcNow());
    }

    /// <summary>What one resolve pass established, in the order it was asked.</summary>
    private sealed record ResolveOutcome(
        IReadOnlyList<ResolvedRoute> Publishes, IReadOnlyList<ResolvedRoute> Consumes);

    /// <summary>
    /// Asks the registry which contracts govern a topology, and caches every answer.
    /// </summary>
    /// <returns>What was resolved, or <see langword="null"/> when the registry could not answer.</returns>
    private async Task<ResolveOutcome?> ResolveAsync(
        IReadOnlyList<PublishRoute> publishes,
        IReadOnlyList<string> consumes,
        CancellationToken cancellationToken)
    {
        if (publishes.Count is 0 && consumes.Count is 0)
        {
            return new ResolveOutcome([], []);
        }

        try
        {
            var request = new ResolveRequestPayload(
                _options.BrokerId,
                _options.VirtualHost,
                [.. publishes.Select(p => new PublishTargetPayload(p.Exchange, p.RoutingKey))],
                consumes);

            using var response = await _http.PostAsJsonAsync(
                $"/v1/environments/{_options.Environment}/contracts/resolve",
                request,
                Json,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var problem = await ReadProblemAsync(response, cancellationToken).ConfigureAwait(false);
                RecordRefusal(response.StatusCode, problem);
                return null;
            }

            var body = await response.Content
                .ReadFromJsonAsync<ResolveResponsePayload>(Json, cancellationToken)
                .ConfigureAwait(false);

            if (body is null)
            {
                return null;
            }

            // POSITIONAL, SO A LENGTH MISMATCH IS NOT RECOVERABLE.
            //
            // The endpoint answers in request order, which is what makes one round trip work for
            // a whole topology. It also means a short or long array cannot be zipped: entry two
            // would carry entry three's contract, and the client would enforce the wrong
            // subjects on a route while reporting complete success. Refusing the whole response
            // costs one unresolved pass; guessing costs correctness silently.
            if (body.Publishes.Count != publishes.Count || body.Consumes.Count != consumes.Count)
            {
                _lastFailure =
                    $"contracts/resolve answered {body.Publishes.Count} publishes and " +
                    $"{body.Consumes.Count} consumes for {publishes.Count} and {consumes.Count} asked";
                return null;
            }

            _lastContact = _clock.GetUtcNow();
            _degraded = false;

            var resolvedPublishes = new List<ResolvedRoute>(publishes.Count);
            var resolvedConsumes = new List<ResolvedRoute>(consumes.Count);

            for (var i = 0; i < publishes.Count; i++)
            {
                var route = ToRoute(body.Publishes[i], _lastContact.Value);
                _contracts.PutPublish(publishes[i], route);
                resolvedPublishes.Add(route);
            }

            for (var i = 0; i < consumes.Count; i++)
            {
                var route = ToRoute(body.Consumes[i], _lastContact.Value);
                _contracts.PutConsume(consumes[i], route);
                resolvedConsumes.Add(route);
            }

            return new ResolveOutcome(resolvedPublishes, resolvedConsumes);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _degraded = true;
            return null;
        }
    }

    /// <summary>
    /// Projects a resolved binding, dropping references this client cannot parse.
    /// </summary>
    /// <remarks>
    /// A subject reference that will not parse is skipped rather than failing the route. The
    /// registry is the newer component in most deployments, so an unrecognised selector spelling
    /// is far more likely to be a client that predates it than corruption — and refusing the
    /// whole route would turn a forward-compatible addition into an enforcement outage.
    /// </remarks>
    private static ResolvedRoute ToRoute(ResolvedBindingPayload payload, DateTimeOffset at)
    {
        var subjects = new List<SubjectRef>(payload.Subjects.Count);

        foreach (var entry in payload.Subjects)
        {
            var name = SubjectName.Create(entry.Subject);
            var selector = VersionSelector.Parse(entry.Selector);

            if (name.IsSuccess && selector.IsSuccess)
            {
                subjects.Add(new SubjectRef(name.Value, selector.Value));
            }
        }

        return new ResolvedRoute(payload.Contract, ParseEnforcement(payload.Enforcement), subjects, at);
    }

    /// <summary>
    /// Reads an enforcement token, defaulting an unrecognised one to <c>Monitor</c>.
    /// </summary>
    /// <remarks>
    /// Not <c>Off</c> and not <c>Enforce</c>. A token this client does not know means the
    /// registry is newer than the client: defaulting to Off would silently disable governance
    /// the operator believes is on, and defaulting to Enforce would start refusing production
    /// traffic on the strength of a string nobody here understands. Monitor observes and reports
    /// without doing either.
    /// </remarks>
    private static EnforcementMode ParseEnforcement(string? token) => token switch
    {
        "OFF" => EnforcementMode.Off,
        "MONITOR" => EnforcementMode.Monitor,
        "ENFORCE" => EnforcementMode.Enforce,
        _ => EnforcementMode.Monitor,
    };

    private void Ingest(BootstrapPayload? payload)
    {
        if (payload is null)
        {
            return;
        }

        var schemas = 0;
        foreach (var (id, schema) in payload.Schemas)
        {
            var parsed = SchemaId.Create(id);
            if (parsed.IsFailure || !TryParseFormat(schema.Format, out var format))
            {
                continue;
            }

            _cache.PutSchema(new CachedSchema(parsed.Value, format, schema.Schema));
            schemas++;
        }

        var subjects = 0;
        var now = _clock.GetUtcNow();
        foreach (var subject in payload.Subjects)
        {
            var name = SubjectName.Create(subject.Name);
            if (name.IsFailure || subject.LatestSchemaId is null || subject.LatestOrdinal is null)
            {
                continue;
            }

            var id = SchemaId.Create(subject.LatestSchemaId);
            if (id.IsFailure)
            {
                continue;
            }

            _cache.PutLatest(name.Value, new CachedLatest(id.Value, subject.LatestOrdinal.Value, now));
            subjects++;
        }

        _subjectsLoaded = subjects;
        _schemasLoaded = schemas;
    }

    private Task JitterAsync(CancellationToken cancellationToken)
    {
        if (_options.WarmUpJitter <= TimeSpan.Zero)
        {
            return Task.CompletedTask;
        }

        // Full jitter rather than a fixed stagger: a rolling restart releases instances in
        // waves, and a fixed delay would simply move the thundering herd rather than spread it.
        var delay = TimeSpan.FromMilliseconds(
            Random.Shared.NextDouble() * _options.WarmUpJitter.TotalMilliseconds);

        return Task.Delay(delay, _clock, cancellationToken);
    }

    /// <summary>Releases the warm-up gate.</summary>
    /// <remarks>
    /// Deliberately does not dispose the <see cref="HttpClient"/>: it is supplied by the
    /// caller, usually from <c>IHttpClientFactory</c>, and disposing someone else's pooled
    /// handler is how socket exhaustion gets introduced.
    /// </remarks>
    public void Dispose() => _warmUpGate.Dispose();

    /// <summary>
    /// Counts an operation that could not be enforced, and applies the configured mode.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Counted <b>per operation</b>, not per distinct cause. A subject that stays unregistered
    /// keeps producing unenforced messages, and a counter that recorded it once would report a
    /// permanent enforcement hole as a single historic blip.
    /// </para>
    /// <para>
    /// Under <see cref="ResolutionFailureMode.FailClosed"/> this throws. It never throws for a
    /// <em>validation</em> verdict — that is a different decision, taken elsewhere, and
    /// conflating the two is what <see cref="ResolutionFailureMode"/> documents at length.
    /// </para>
    /// </remarks>
    /// <returns><see langword="null"/>, so callers can <c>return Unresolvable(…)</c>.</returns>
    private CachedSchema? Unresolvable(string code, string what)
    {
        Interlocked.Increment(ref _resolutionFailures);

        return _options.OnResolutionFailure is ResolutionFailureMode.FailClosed
            ? throw new ConcordatException(code, what)
            : null;
    }

    private CachedLatest? UnresolvableLatest(string code, string what)
    {
        _ = Unresolvable(code, what);
        return null;
    }

    /// <summary>
    /// Records a registry refusal, and decides whether it counts as degradation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A 4xx is not an outage.</b> An expired API key answers every request with 403, and
    /// reporting that as "registry unreachable" sends whoever is paged to go and stare at a
    /// perfectly healthy registry. The same mis-attribution as absent-vs-unreachable, one layer
    /// up: 5xx, 408 and 429 are the registry failing to answer; the rest are it answering, with
    /// a refusal.
    /// </para>
    /// <para>
    /// Either way the reason is retained verbatim in <see cref="ConcordatClientStatus"/>, so an
    /// operator reads <c>403 auth_invalid_key</c> rather than inferring it from a boolean.
    /// </para>
    /// </remarks>
    private void RecordRefusal(HttpStatusCode status, ProblemDetails problem)
    {
        _lastFailure = $"{(int)status} {problem.Code ?? "no concordatCode"}";

        _degraded = (int)status >= 500
            || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
    }

    /// <summary>
    /// Reads an RFC 9457 body, keeping the stable <c>concordatCode</c> if one is present.
    /// </summary>
    /// <remarks>
    /// Never throws. This runs on a path that is already handling a failure, and a client that
    /// fell over while parsing an error body would replace a legible registry problem with an
    /// illegible client one.
    /// </remarks>
    private static async Task<ProblemDetails> ReadProblemAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content
                .ReadFromJsonAsync<ProblemDetails>(Json, cancellationToken)
                .ConfigureAwait(false) ?? ProblemDetails.None;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or HttpRequestException)
        {
            return ProblemDetails.None;
        }
    }

    private static bool TryParseFormat(string? token, out SchemaFormat format)
    {
        switch (token)
        {
            case WireTokens.FormatJson: format = SchemaFormat.Json; return true;
            case WireTokens.FormatAvro: format = SchemaFormat.Avro; return true;
            case WireTokens.FormatProtobuf: format = SchemaFormat.Protobuf; return true;
            default: format = default; return false;
        }
    }

    /// <summary>An RFC 9457 body, with the registry's stable code alongside the human text.</summary>
    private sealed record ProblemDetails(string? Title, string? Detail, string? ConcordatCode)
    {
        public static ProblemDetails None { get; } = new(null, null, null);

        /// <summary>The stable token to branch on, never the prose.</summary>
        public string? Code => ConcordatCode;

        public string Describe() =>
            Detail ?? Title ?? "the registry gave no problem details.";
    }

    private sealed record BootstrapPayload(
        IReadOnlyList<BootstrapSubjectPayload> Subjects,
        IReadOnlyDictionary<string, SchemaPayload> Schemas);

    private sealed record BootstrapSubjectPayload(
        string Name, string Format, int? LatestOrdinal, string? LatestSchemaId, string? LatestSemver);

    private sealed record SchemaPayload(string SchemaId, string Format, string Schema);

    private sealed record VersionPayload(int Ordinal, string SchemaId, string? SemanticVersion);

    private sealed record SubjectRefPayload(string Subject, string Selector);

    private sealed record PublishTargetPayload(string Exchange, string RoutingKey);

    private sealed record ResolveRequestPayload(
        Guid? BrokerId,
        string? VirtualHost,
        IReadOnlyList<PublishTargetPayload> Publishes,
        IReadOnlyList<string> Consumes);

    private sealed record ResolvedBindingPayload(
        string? Contract, string? Enforcement, IReadOnlyList<SubjectRefPayload> Subjects);

    private sealed record ResolveResponsePayload(
        IReadOnlyList<ResolvedBindingPayload> Publishes,
        IReadOnlyList<ResolvedBindingPayload> Consumes);

    private sealed record RegisterServicePayload(
        string Name,
        IReadOnlyList<SubjectRefPayload> Produces,
        IReadOnlyList<SubjectRefPayload> Consumes,
        string ReportedBy);

    /// <summary>
    /// Tells the registry what this service publishes and reads (M7.4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs after the bootstrap payload is ingested, not before. Warm-up's job is to make the
    /// client able to work; declaring intent is bookkeeping for someone else's benefit, and a
    /// slow or refused report must not delay the thing that actually matters.
    /// </para>
    /// <para>
    /// <b>Failures are swallowed unless the caller asks otherwise.</b> A service that would not
    /// start because a governance nicety was refused has put the registry back on the critical
    /// path, which is the failure mode warm-up exists to avoid.
    /// </para>
    /// </remarks>
    private async Task DeclareServiceAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ServiceName))
        {
            return;
        }

        var payload = new RegisterServicePayload(
            _options.ServiceName,
            [.. _options.Produces.Select(ParseRef)],
            [.. _options.Consumes.Select(ParseRef)],
            "sdk");

        try
        {
            using var response = await _http.PostAsJsonAsync(
                $"/v1/environments/{_options.Environment}/services", payload, Json, cancellationToken)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode || !_options.RequireServiceRegistration)
            {
                return;
            }

            var problem = await ReadProblemAsync(response, cancellationToken).ConfigureAwait(false);

            throw new ConcordatException(
                problem.Code ?? "service_registration_failed",
                $"Declaring service '{_options.ServiceName}' failed with " +
                $"{(int)response.StatusCode}: {problem.Describe()} " +
                "RequireServiceRegistration is on, so the client will not start undeclared.");
        }
        catch (HttpRequestException ex) when (!_options.RequireServiceRegistration)
        {
            // Deliberately not rethrown and deliberately not silent-by-accident: warm-up has
            // already succeeded, so the client is usable. Only the governance record is missing.
            _lastFailure = $"service registration: {ex.Message}";
        }
    }

    /// <summary>Splits <c>subject@selector</c>, defaulting a bare subject to <c>latest</c>.</summary>
    private static SubjectRefPayload ParseRef(string entry)
    {
        var at = entry.LastIndexOf('@');

        return at < 0
            ? new SubjectRefPayload(entry.Trim(), "latest")
            : new SubjectRefPayload(entry[..at].Trim(), entry[(at + 1)..].Trim());
    }
}

/// <summary>A failure the client cannot proceed through.</summary>
public sealed class ConcordatException : Exception
{
    /// <summary>Creates an exception carrying a stable code.</summary>
    /// <param name="code">A <c>concordatCode</c>.</param>
    /// <param name="message">What went wrong.</param>
    public ConcordatException(string code, string message)
        : base(message) => Code = code;

    /// <summary>Creates an exception carrying a stable code and an inner cause.</summary>
    /// <param name="code">A <c>concordatCode</c>.</param>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The cause.</param>
    public ConcordatException(string code, string message, Exception innerException)
        : base(message, innerException) => Code = code;

    /// <summary>Creates an exception with no code. Prefer the overloads that carry one.</summary>
    public ConcordatException()
        : this(ConcordatCodes.EnvelopeMalformed, "An unspecified Concordat error occurred.")
    {
    }

    /// <summary>Creates an exception with no code.</summary>
    /// <param name="message">What went wrong.</param>
    public ConcordatException(string message)
        : this(ConcordatCodes.EnvelopeMalformed, message)
    {
    }

    /// <summary>Creates an exception with no code.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The cause.</param>
    public ConcordatException(string message, Exception innerException)
        : this(ConcordatCodes.EnvelopeMalformed, message, innerException)
    {
    }

    /// <summary>The stable <c>concordatCode</c>, so callers branch on a token not a message.</summary>
    public string Code { get; } = string.Empty;
}
