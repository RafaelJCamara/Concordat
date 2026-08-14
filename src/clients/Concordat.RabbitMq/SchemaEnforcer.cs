using System.Text;
using Concordat.Client;
using Concordat.Domain.Contracts;
using Concordat.Domain.Messaging;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;
using Concordat.Formats.Abstractions;

namespace Concordat.RabbitMq;

/// <summary>What the enforcer concluded about one message.</summary>
/// <param name="Outcome">What should happen to it.</param>
/// <param name="Code">A stable <c>concordatCode</c>, or null when nothing went wrong.</param>
/// <param name="Detail">A human-readable explanation.</param>
/// <param name="Subject">The subject, when known.</param>
/// <param name="SchemaId">The schema id, when known.</param>
/// <param name="Envelope">Headers to stamp on the outgoing message, on the publish side.</param>
public sealed record EnforcementDecision(
    EnforcementOutcome Outcome,
    string? Code,
    string Detail,
    SubjectName? Subject = null,
    SchemaId? SchemaId = null,
    IReadOnlyDictionary<string, string>? Envelope = null)
{
    /// <summary>
    /// The mode that governs this message, after the route's contract has had its say (M7.3).
    /// </summary>
    /// <remarks>
    /// <b>Callers must act on this rather than on their configured <c>Mode</c>.</b> A contract
    /// governing the route overrides local configuration in both directions — that is what makes
    /// central enforcement worth having — so a channel that consulted its own options would
    /// ignore an operator who had just promoted a contract to ENFORCE, and would keep refusing
    /// traffic after one had been switched to OFF.
    /// </remarks>
    public EnforcementMode EffectiveMode { get; init; } = EnforcementMode.Monitor;

    /// <summary>The contract that governed the route, or null when nothing did.</summary>
    public string? Contract { get; init; }
}

/// <summary>
/// The rules both sides share: resolve a schema, validate a payload, decide.
/// </summary>
/// <remarks>
/// <para>
/// Shared deliberately. A publisher that stamped an envelope under one set of rules and a
/// consumer that verified it under another would produce disagreements that look like broker
/// faults, and the two sides are written by different people at different times.
/// </para>
/// <para>
/// <b>Nothing here throws for a message-level problem.</b> Verdicts are returned, not raised.
/// Deciding what a bad message deserves belongs to the caller, because the answer differs by
/// side: a publisher can refuse, while a consumer that throws merely loses the message down
/// <c>CallbackExceptionAsync</c>.
/// </para>
/// <para>
/// <b>Contracts decide two things the payload cannot (M7.3):</b> whether this subject belongs
/// on this route at all, and how much may be done about it. A message can be perfectly valid
/// against the schema it claims and still be the wrong message on that exchange — schema
/// validation alone has no way to know, because it never sees the topology.
/// </para>
/// </remarks>
public sealed class SchemaEnforcer
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    private readonly IConcordatClient _client;
    private readonly ConcordatRabbitMqOptions _options;
    private readonly Dictionary<SchemaFormat, IPayloadValidator> _validators;

    /// <summary>Creates an enforcer.</summary>
    /// <param name="client">The registry client.</param>
    /// <param name="options">Configuration.</param>
    /// <param name="validators">One validator per format. A format with none is not validated.</param>
    public SchemaEnforcer(
        IConcordatClient client,
        ConcordatRabbitMqOptions options,
        IEnumerable<IPayloadValidator> validators)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(validators);
        options.Validate();

        _client = client;
        _options = options;
        _validators = validators.ToDictionary(v => v.Format);
    }

    /// <summary>Decides what to do with a message about to be published.</summary>
    /// <param name="context">What the publisher knows.</param>
    /// <param name="body">The payload.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The decision, including the envelope to stamp when one could be built.</returns>
    public async Task<EnforcementDecision> InspectPublishAsync(
        PublishContext context, ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var route = _options.ConsultContracts
            ? await _client.GetPublishRouteAsync(
                new PublishRoute(context.Exchange ?? string.Empty, context.RoutingKey ?? string.Empty),
                cancellationToken).ConfigureAwait(false)
            : ResolvedRoute.Ungoverned(default);

        var decide = Decision(route);

        if (decide.Mode is EnforcementMode.Off)
        {
            // A governed route the operator has switched off. Reported rather than silent: a
            // route that stopped being checked is worth seeing in the counters, and it is
            // distinguishable from an un-instrumented publisher by carrying a contract name.
            return decide.Build(
                EnforcementOutcome.Unenforced,
                null,
                $"Contract '{route.Contract}' sets enforcement to OFF for this route.");
        }

        var resolution = _options.SubjectResolver.Resolve(context);

        if (resolution.IsUnusable)
        {
            return decide.Build(
                EnforcementOutcome.Observed, resolution.Error!.Code, resolution.Error.Message);
        }

        var subject = resolution.IsResolved ? resolution.Subject : null;

        if (subject is null)
        {
            // No properties.type. Before contracts this was the end of the line; now the route's
            // own binding can supply the subject the publisher omitted, which is what closes the
            // largest enforcement hole in a brownfield deployment.
            if (!route.IsGoverned)
            {
                return decide.Build(
                    EnforcementOutcome.Unenforced,
                    ConcordatCodes.EnvelopeSubjectUnresolvable,
                    "No properties.type was set and no contract governs this route, " +
                    "so there is no subject to enforce against.");
            }

            if (route.Subjects.Count is not 1)
            {
                return decide.Build(
                    EnforcementOutcome.Unenforced,
                    ConcordatCodes.ContractSubjectAmbiguous,
                    $"No properties.type was set and contract '{route.Contract}' permits " +
                    $"{route.Subjects.Count} subjects on this route, so the subject cannot be " +
                    "chosen without guessing.");
            }

            subject = route.Subjects[0].Subject;
        }

        var permitted = route.IsGoverned
            ? route.Subjects.FirstOrDefault(s => s.Subject.Value == subject.Value)
            : null;

        if (route.IsGoverned && permitted is null)
        {
            // The right message on the wrong route. No envelope is returned, and that is the
            // deliberate difference from a payload violation below: an envelope asserts "this
            // is schema X, sent here on purpose", and stamping one on a message the contract
            // does not accept would put a claim on the wire the registry would contradict.
            return decide.Build(
                EnforcementOutcome.Observed,
                ConcordatCodes.ContractSubjectNotPermitted,
                $"Contract '{route.Contract}' does not permit subject '{subject.Value}' on " +
                $"exchange '{context.Exchange}' with routing key '{context.RoutingKey}'. " +
                $"Permitted: {Describe(route.Subjects)}.",
                subject);
        }

        var latest = await _client.GetLatestAsync(subject, cancellationToken).ConfigureAwait(false);

        if (latest is null)
        {
            return decide.Build(
                EnforcementOutcome.Unenforced,
                ConcordatCodes.SubjectNotFound,
                $"No schema could be resolved for subject '{subject.Value}'.",
                subject);
        }

        // A pinned or floored binding judged against what this publisher would actually stamp.
        // `latest` is passed as both arguments because that is precisely the claim being tested:
        // the publisher sends the tip, and the question is whether the route accepts the tip.
        if (permitted is not null && !permitted.Selector.Accepts(latest.Ordinal, latest.Ordinal))
        {
            return decide.Build(
                EnforcementOutcome.Observed,
                ConcordatCodes.ContractVersionNotPermitted,
                $"Contract '{route.Contract}' accepts '{subject.Value}@{permitted.Selector}' on " +
                $"this route, but the current version is {latest.Ordinal}.",
                subject,
                latest.SchemaId);
        }

        var schema = await _client.GetSchemaAsync(latest.SchemaId, cancellationToken).ConfigureAwait(false);

        if (schema is null)
        {
            return decide.Build(
                EnforcementOutcome.Unenforced,
                ConcordatCodes.SchemaUnresolvable,
                $"Schema {latest.SchemaId.Value} could not be fetched.",
                subject,
                latest.SchemaId);
        }

        // The envelope is built whether or not validation passes, and stamped by the caller
        // only when the message is actually sent. In Monitor mode a violating message still
        // goes out carrying correct identity, which is what lets a consumer downstream start
        // reading schema ids before publishers are clean.
        var envelope = EnvelopeWriter.Headers(
            latest.SchemaId, subject, latest.Ordinal, null, schema.Format);

        var verdict = Validate(schema, body);

        return verdict is null
            ? decide.Build(
                EnforcementOutcome.Valid, null, "Conforms.", subject, latest.SchemaId, envelope)
            : decide.Build(
                EnforcementOutcome.Observed,
                ConcordatCodes.PayloadInvalid,
                verdict,
                subject,
                latest.SchemaId,
                envelope);
    }

    /// <summary>Decides what to do with a delivered message.</summary>
    /// <param name="headers">The delivered header table.</param>
    /// <param name="propertiesType">AMQP <c>properties.type</c>.</param>
    /// <param name="contentType">AMQP <c>properties.content-type</c>, which carries Mode B.</param>
    /// <param name="body">The payload.</param>
    /// <param name="queue">The queue the message arrived on, for contract resolution.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The decision.</returns>
    public async Task<EnforcementDecision> InspectConsumeAsync(
        IReadOnlyDictionary<string, object?>? headers,
        string? propertiesType,
        string? contentType,
        ReadOnlyMemory<byte> body,
        string? queue = null,
        CancellationToken cancellationToken = default)
    {
        var route = _options.ConsultContracts && !string.IsNullOrEmpty(queue)
            ? await _client.GetConsumeRouteAsync(queue, cancellationToken).ConfigureAwait(false)
            : ResolvedRoute.Ungoverned(default);

        var decide = Decision(route);

        if (decide.Mode is EnforcementMode.Off)
        {
            return decide.Build(
                EnforcementOutcome.Unenforced,
                null,
                $"Contract '{route.Contract}' sets enforcement to OFF for queue '{queue}'.");
        }

        var read = EnvelopeReader.Read(headers, propertiesType, contentType);

        if (read.IsMalformed)
        {
            return decide.Build(EnforcementOutcome.Observed, read.Error!.Code, read.Error.Message);
        }

        if (!read.IsEnveloped)
        {
            // Mode A adoption: a publisher that has not been instrumented yet. Delivering it is
            // the entire point of ADR-010, so this is counted, never refused.
            return decide.Build(
                EnforcementOutcome.Unenforced,
                null,
                "The message carries no Concordat envelope.");
        }

        var envelope = read.Envelope!;

        // Route conformance on this side is best-effort by construction. Mode B carries only the
        // schema id in the content type, so a message that arrives that way has no subject to
        // compare against the contract — and inferring one from the schema id would need a
        // reverse lookup the registry does not offer. Stated rather than silently skipped.
        if (route.IsGoverned && envelope.Subject is not null)
        {
            var violation = await CheckConsumeConformanceAsync(
                route, envelope, queue!, cancellationToken).ConfigureAwait(false);

            if (violation is not null)
            {
                return decide.Build(
                    EnforcementOutcome.Observed,
                    violation.Value.Code,
                    violation.Value.Detail,
                    envelope.Subject,
                    envelope.SchemaId);
            }
        }

        var schema = await _client.GetSchemaAsync(envelope.SchemaId, cancellationToken).ConfigureAwait(false);

        if (schema is null)
        {
            // Fail-open on resolution, always. Quarantining because the registry blinked would
            // turn a registry outage into permanent message displacement — the client's
            // FailClosed setting already governs whether that is acceptable, and it throws
            // there rather than here.
            return decide.Build(
                EnforcementOutcome.Unenforced,
                ConcordatCodes.SchemaUnresolvable,
                $"Schema {envelope.SchemaId.Value} could not be resolved, so the payload was not checked.",
                envelope.Subject,
                envelope.SchemaId);
        }

        // The declared format against the authoritative one. `concordat-format` is advisory and
        // the schema id is content-addressed, so the registry wins and validation is unaffected
        // — but a message claiming JSON for a schema the registry holds as Avro means the
        // producer and the registry disagree about what was sent, and that is worth saying out
        // loud rather than quietly validating past.
        if (envelope.Format is { } declared && declared != schema.Format)
        {
            return decide.Build(
                EnforcementOutcome.Observed,
                ConcordatCodes.EnvelopeFormatMismatch,
                $"The message declares format '{WireTokens.For(declared)}' but the registry " +
                $"holds schema {envelope.SchemaId.Value} as " +
                $"'{WireTokens.For(schema.Format)}'.",
                envelope.Subject,
                envelope.SchemaId);
        }

        var verdict = Validate(schema, body);

        return verdict is null
            ? decide.Build(
                EnforcementOutcome.Valid, null, "Conforms.", envelope.Subject, envelope.SchemaId)
            : decide.Build(
                EnforcementOutcome.Observed,
                ConcordatCodes.PayloadInvalid,
                verdict,
                envelope.Subject,
                envelope.SchemaId);
    }

    /// <summary>Whether a delivered message belongs on the queue it arrived on.</summary>
    private async Task<(string Code, string Detail)?> CheckConsumeConformanceAsync(
        ResolvedRoute route,
        MessageEnvelope envelope,
        string queue,
        CancellationToken cancellationToken)
    {
        var permitted = route.Subjects.FirstOrDefault(s => s.Subject.Value == envelope.Subject!.Value);

        if (permitted is null)
        {
            return (
                ConcordatCodes.ContractSubjectNotPermitted,
                $"Contract '{route.Contract}' does not expect subject '{envelope.Subject!.Value}' " +
                $"on queue '{queue}'. Expected: {Describe(route.Subjects)}.");
        }

        if (envelope.Ordinal is not { } ordinal)
        {
            // Advisory field, legitimately absent. The subject matched, which is the check worth
            // having; refusing over a missing optional would punish a conforming producer.
            return null;
        }

        // Only a `latest` selector needs the tip, and asking for it is a cache hit on any warm
        // client. A pinned or floored binding answers from the ordinal on the wire alone.
        int? tip = null;
        if (permitted.Selector.Kind is VersionSelectorKind.Latest)
        {
            var latest = await _client
                .GetLatestAsync(envelope.Subject!, cancellationToken).ConfigureAwait(false);

            if (latest is null)
            {
                return null;
            }

            tip = latest.Ordinal;
        }

        return permitted.Selector.Accepts(ordinal, tip)
            ? null
            : (
                ConcordatCodes.ContractVersionNotPermitted,
                $"Contract '{route.Contract}' expects " +
                $"'{envelope.Subject!.Value}@{permitted.Selector}' on queue '{queue}', " +
                $"but the message carries version {ordinal}.");
    }

    /// <summary>
    /// Works out who decides, and carries that decision onto every verdict.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A governing contract wins outright; the client's <c>Mode</c> is the default for routes
    /// nothing covers.</b> Taking the stricter of the two was the obvious alternative and is
    /// wrong: it would mean an operator could switch enforcement on centrally but never off
    /// again, because any service configured locally to Enforce would keep refusing traffic
    /// after the contract had been set to OFF. An off switch that does not switch anything off
    /// is worse than no off switch, because it is believed.
    /// </para>
    /// <para>
    /// This is why <see cref="ResolvedRoute.IsGoverned"/> is distinct from an <c>Off</c>
    /// enforcement: "no contract covers this" has to mean something different from "a contract
    /// covers this and says do nothing", or the central control is unreachable from here.
    /// </para>
    /// </remarks>
    private Decider Decision(ResolvedRoute route) =>
        new(route.IsGoverned ? route.Enforcement : _options.Mode, route.Contract);

    private readonly record struct Decider(EnforcementMode Mode, string? Contract)
    {
        public EnforcementDecision Build(
            EnforcementOutcome outcome,
            string? code,
            string detail,
            SubjectName? subject = null,
            SchemaId? schemaId = null,
            IReadOnlyDictionary<string, string>? envelope = null) =>
            new(outcome, code, detail, subject, schemaId, envelope)
            {
                EffectiveMode = Mode,
                Contract = Contract,
            };
    }

    /// <summary>Names what a route permits, for an error somebody has to act on.</summary>
    private static string Describe(IReadOnlyList<SubjectRef> subjects) =>
        subjects.Count is 0
            ? "nothing"
            : string.Join(", ", subjects.Select(s => $"{s.Subject.Value}@{s.Selector}"));

    /// <summary>Validates a payload, or explains why not.</summary>
    /// <returns>Null when the payload conforms or cannot be checked; otherwise the reason.</returns>
    private string? Validate(CachedSchema schema, ReadOnlyMemory<byte> body)
    {
        if (!_options.ValidatePayloads || !_validators.TryGetValue(schema.Format, out var validator))
        {
            return null;
        }

        string payload;
        try
        {
            // Strict UTF-8, matching the envelope reader. The lenient default substitutes
            // U+FFFD, which would turn a corrupt payload into a differently-corrupt one and
            // report a puzzling schema violation instead of an encoding fault.
            payload = StrictUtf8.GetString(body.Span);
        }
        catch (DecoderFallbackException ex)
        {
            return $"The payload is not valid UTF-8: {ex.Message}";
        }

        var result = validator.Validate(schema.CanonicalBody, payload);

        return result.IsValid
            ? null
            : string.Join("; ", result.Errors.Select(e => $"{e.Path}: {e.Message}"));
    }
}
