using System.Text;
using Concordat.Client;
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
    IReadOnlyDictionary<string, string>? Envelope = null);

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

        var resolution = _options.SubjectResolver.Resolve(context);

        if (resolution.IsUnusable)
        {
            return new EnforcementDecision(
                EnforcementOutcome.Observed, resolution.Error!.Code, resolution.Error.Message);
        }

        if (!resolution.IsResolved)
        {
            // No type set. The ordinary un-instrumented publisher, and not something to refuse
            // — but every message it sends is unenforced, and that is worth counting.
            return new EnforcementDecision(
                EnforcementOutcome.Unenforced,
                ConcordatCodes.EnvelopeSubjectUnresolvable,
                "No properties.type was set, so there is no subject to enforce against.");
        }

        var subject = resolution.Subject!;
        var latest = await _client.GetLatestAsync(subject, cancellationToken).ConfigureAwait(false);

        if (latest is null)
        {
            return new EnforcementDecision(
                EnforcementOutcome.Unenforced,
                ConcordatCodes.SubjectNotFound,
                $"No schema could be resolved for subject '{subject.Value}'.",
                subject);
        }

        var schema = await _client.GetSchemaAsync(latest.SchemaId, cancellationToken).ConfigureAwait(false);

        if (schema is null)
        {
            return new EnforcementDecision(
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
            ? new EnforcementDecision(
                EnforcementOutcome.Valid, null, "Conforms.", subject, latest.SchemaId, envelope)
            : new EnforcementDecision(
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
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The decision.</returns>
    public async Task<EnforcementDecision> InspectConsumeAsync(
        IReadOnlyDictionary<string, object?>? headers,
        string? propertiesType,
        string? contentType,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default)
    {
        var read = EnvelopeReader.Read(headers, propertiesType, contentType);

        if (read.IsMalformed)
        {
            return new EnforcementDecision(
                EnforcementOutcome.Observed, read.Error!.Code, read.Error.Message);
        }

        if (!read.IsEnveloped)
        {
            // Mode A adoption: a publisher that has not been instrumented yet. Delivering it is
            // the entire point of ADR-010, so this is counted, never refused.
            return new EnforcementDecision(
                EnforcementOutcome.Unenforced,
                null,
                "The message carries no Concordat envelope.");
        }

        var envelope = read.Envelope!;
        var schema = await _client.GetSchemaAsync(envelope.SchemaId, cancellationToken).ConfigureAwait(false);

        if (schema is null)
        {
            // Fail-open on resolution, always. Quarantining because the registry blinked would
            // turn a registry outage into permanent message displacement — the client's
            // FailClosed setting already governs whether that is acceptable, and it throws
            // there rather than here.
            return new EnforcementDecision(
                EnforcementOutcome.Unenforced,
                ConcordatCodes.SchemaUnresolvable,
                $"Schema {envelope.SchemaId.Value} could not be resolved, so the payload was not checked.",
                envelope.Subject,
                envelope.SchemaId);
        }

        var verdict = Validate(schema, body);

        return verdict is null
            ? new EnforcementDecision(
                EnforcementOutcome.Valid, null, "Conforms.", envelope.Subject, envelope.SchemaId)
            : new EnforcementDecision(
                EnforcementOutcome.Observed,
                ConcordatCodes.PayloadInvalid,
                verdict,
                envelope.Subject,
                envelope.SchemaId);
    }

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
