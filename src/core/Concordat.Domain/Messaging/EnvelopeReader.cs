using System.Globalization;
using System.Text;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;

namespace Concordat.Domain.Messaging;

/// <summary>The outcome of reading an envelope: read, absent, or malformed.</summary>
/// <remarks>
/// Three outcomes, not two. "No envelope" is not a failure — Mode A exists so a consumer
/// without a Concordat client still reads plain JSON, and treating an un-enveloped message as
/// an error would break the incremental adoption ADR-010 is built around.
/// </remarks>
public sealed class EnvelopeReadResult
{
    private EnvelopeReadResult(EnvelopeKind kind, MessageEnvelope? envelope, DomainError? error)
    {
        Kind = kind;
        Envelope = envelope;
        Error = error;
    }

    /// <summary>How the identity was carried, or <see cref="EnvelopeKind.None"/>.</summary>
    public EnvelopeKind Kind { get; }

    /// <summary>The envelope, when one was read.</summary>
    public MessageEnvelope? Envelope { get; }

    /// <summary>Why reading failed, when it did.</summary>
    public DomainError? Error { get; }

    /// <summary>Whether identity was found and read.</summary>
    public bool IsEnveloped => Envelope is not null;

    /// <summary>Whether an envelope was present but unusable.</summary>
    public bool IsMalformed => Error is not null;

    /// <summary>The message carries no Concordat identity.</summary>
    public static EnvelopeReadResult NotEnveloped { get; } = new(EnvelopeKind.None, null, null);

    /// <summary>An envelope was read.</summary>
    /// <param name="kind">How it was carried.</param>
    /// <param name="envelope">What it said.</param>
    /// <returns>The result.</returns>
    public static EnvelopeReadResult Read(EnvelopeKind kind, MessageEnvelope envelope) =>
        new(kind, envelope, null);

    /// <summary>An envelope was present but unusable.</summary>
    /// <param name="code">A constant from <see cref="ConcordatCodes"/>.</param>
    /// <param name="message">What was wrong.</param>
    /// <returns>The result.</returns>
    public static EnvelopeReadResult Malformed(string code, string message) =>
        new(EnvelopeKind.None, null, new DomainError(code, message));
}

/// <summary>
/// Reads schema identity off a message (ADR-010).
/// </summary>
/// <remarks>
/// <para>
/// Operates over a plain header dictionary rather than any broker's types, so it is testable
/// without a broker and shared by every transport adapter.
/// </para>
/// <para>
/// Several decisions here look fussy and are not. Lookup is <b>ordinal and case-sensitive</b>,
/// because an implementer in another language reaching for HTTP-style header canonicalisation
/// would silently diverge. Decoding is <b>strict UTF-8</b>, because the lenient default in
/// .NET and Go substitutes U+FFFD and would turn a corrupt id into a plausible-looking wrong
/// one. Values are <b>not trimmed</b>, so <c>" acme.A"</c> and <c>"acme.A"</c> cannot become
/// two spellings of one wire value. A key present with a null or empty value is
/// <b>malformed, not absent</b>.
/// </para>
/// </remarks>
public static class EnvelopeReader
{
    // Not Encoding.UTF8: that one substitutes U+FFFD for invalid bytes, which is precisely
    // how a corrupted schema id becomes a valid-looking wrong one.
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Reads identity from a message's headers and properties.</summary>
    /// <param name="headers">The AMQP header table, or <see langword="null"/> when absent.</param>
    /// <param name="propertiesType">
    /// AMQP <c>properties.type</c>, used to recover the subject when the header is absent.
    /// </param>
    /// <param name="contentType">AMQP <c>properties.content-type</c>, for Mode B.</param>
    /// <returns>Read, absent, or malformed.</returns>
    public static EnvelopeReadResult Read(
        IReadOnlyDictionary<string, object?>? headers,
        string? propertiesType = null,
        string? contentType = null)
    {
        var modeA = ReadHeaders(headers, propertiesType);
        if (modeA.Kind is not EnvelopeKind.None || modeA.IsMalformed)
        {
            return modeA;
        }

        return ReadContentType(contentType, propertiesType);
    }

    private static EnvelopeReadResult ReadHeaders(
        IReadOnlyDictionary<string, object?>? headers, string? propertiesType)
    {
        if (headers is null || headers.Count == 0)
        {
            return EnvelopeReadResult.NotEnveloped;
        }

        var version = Value(headers, EnvelopeHeaders.Version);
        if (version.State is ValueState.Absent)
        {
            return EnvelopeReadResult.NotEnveloped;
        }

        if (version.State is not ValueState.Present)
        {
            return Failure(version, EnvelopeHeaders.Version);
        }

        if (!string.Equals(version.Text, EnvelopeHeaders.CurrentVersion, StringComparison.Ordinal))
        {
            // Do not interpret any other concordat-* header: a later envelope version may
            // have redefined them, and guessing would be worse than declining.
            return EnvelopeReadResult.Malformed(
                ConcordatCodes.EnvelopeVersionUnsupported,
                $"Envelope version '{version.Text}' is not supported; this client implements " +
                $"'{EnvelopeHeaders.CurrentVersion}'.");
        }

        var rawId = Value(headers, EnvelopeHeaders.SchemaId);
        if (rawId.State is ValueState.Absent)
        {
            return EnvelopeReadResult.Malformed(
                ConcordatCodes.EnvelopeSchemaIdMissing,
                $"The envelope declares version {EnvelopeHeaders.CurrentVersion} but carries no " +
                $"'{EnvelopeHeaders.SchemaId}'.");
        }

        if (rawId.State is not ValueState.Present)
        {
            return Failure(rawId, EnvelopeHeaders.SchemaId);
        }

        var schemaId = SchemaId.Create(rawId.Text);
        if (schemaId.IsFailure)
        {
            return EnvelopeReadResult.Malformed(schemaId.Error!.Code, schemaId.Error.Message);
        }

        var warnings = new List<EnvelopeWarning>();

        var format = ReadFormat(headers, warnings);
        if (format.Error is not null)
        {
            return format.Error;
        }

        return EnvelopeReadResult.Read(
            EnvelopeKind.Headers,
            new MessageEnvelope(
                schemaId.Value,
                ReadSubject(headers, propertiesType, warnings),
                ReadOrdinal(headers, warnings),
                ReadSemver(headers, warnings),
                format.Format,
                warnings));
    }

    private static SubjectName? ReadSubject(
        IReadOnlyDictionary<string, object?> headers,
        string? propertiesType,
        List<EnvelopeWarning> warnings)
    {
        var raw = Value(headers, EnvelopeHeaders.Subject);

        if (raw.State is ValueState.WrongType or ValueState.BadEncoding or ValueState.Empty)
        {
            // Advisory, so this warns rather than rejects — but it must not pass silently,
            // or an unreadable subject looks identical to an absent one.
            warnings.Add(new EnvelopeWarning(
                raw.State switch
                {
                    ValueState.WrongType => ConcordatCodes.EnvelopeHeaderTypeInvalid,
                    ValueState.BadEncoding => ConcordatCodes.EnvelopeHeaderEncodingInvalid,
                    _ => ConcordatCodes.EnvelopeMalformed,
                },
                $"'{EnvelopeHeaders.Subject}' is unreadable; falling back to properties.type."));

            raw = new HeaderValue(ValueState.Absent, string.Empty);
        }

        // Fall back to properties.type. A subject is recoverable from the registry via
        // GET /v1/schemas/{id}/subjects, so its absence is not fatal here.
        var candidate = raw.State is ValueState.Present ? raw.Text : propertiesType;
        if (string.IsNullOrEmpty(candidate))
        {
            return null;
        }

        // Validated EXACTLY as it arrived. SubjectName.Create trims, which is right for a
        // user typing into a form and wrong here: if the reader trims, "  acme.A  " and
        // "acme.A" become two spellings of one wire value, and an SDK that does not trim
        // disagrees with one that does. Rejecting the padded form makes that loud.
        if (candidate.Length != candidate.Trim().Length)
        {
            warnings.Add(new EnvelopeWarning(
                ConcordatCodes.SubjectNameInvalid,
                $"'{candidate}' has surrounding whitespace. A subject on the wire must be " +
                "exactly canonical; the reader does not trim."));
            return null;
        }

        var subject = SubjectName.Create(candidate);
        if (subject.IsFailure)
        {
            warnings.Add(new EnvelopeWarning(subject.Error!.Code, subject.Error.Message));
            return null;
        }

        if (raw.State is ValueState.Present &&
            !string.IsNullOrEmpty(propertiesType) &&
            !string.Equals(raw.Text, propertiesType, StringComparison.Ordinal))
        {
            // The header wins: it is the one Concordat wrote. Worth reporting, because a
            // disagreement usually means two libraries are both setting identity.
            warnings.Add(new EnvelopeWarning(
                ConcordatCodes.EnvelopeSubjectTypeMismatch,
                $"'{EnvelopeHeaders.Subject}' says '{raw.Text}' but properties.type says " +
                $"'{propertiesType}'. Using the header."));
        }

        return subject.Value;
    }

    private static int? ReadOrdinal(
        IReadOnlyDictionary<string, object?> headers, List<EnvelopeWarning> warnings)
    {
        var raw = Value(headers, EnvelopeHeaders.VersionOrdinal);
        if (raw.State is ValueState.Absent)
        {
            return null;
        }

        if (raw.State is ValueState.Present &&
            int.TryParse(raw.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal) &&
            ordinal >= 1)
        {
            return ordinal;
        }

        warnings.Add(new EnvelopeWarning(
            ConcordatCodes.EnvelopeOrdinalMalformed,
            $"'{EnvelopeHeaders.VersionOrdinal}' is not a positive integer. Ignoring it; the " +
            "schema id already identifies the schema."));

        return null;
    }

    private static SemanticVersion? ReadSemver(
        IReadOnlyDictionary<string, object?> headers, List<EnvelopeWarning> warnings)
    {
        var raw = Value(headers, EnvelopeHeaders.Semver);
        if (raw.State is ValueState.Absent)
        {
            return null;
        }

        if (raw.State is ValueState.Present)
        {
            var semver = SemanticVersion.Create(raw.Text);
            if (semver.IsSuccess)
            {
                return semver.Value;
            }

            warnings.Add(new EnvelopeWarning(semver.Error!.Code, semver.Error.Message));
            return null;
        }

        warnings.Add(new EnvelopeWarning(
            ConcordatCodes.EnvelopeMalformed, $"'{EnvelopeHeaders.Semver}' is unreadable."));

        return null;
    }

    private static (SchemaFormat? Format, EnvelopeReadResult? Error) ReadFormat(
        IReadOnlyDictionary<string, object?> headers, List<EnvelopeWarning> warnings)
    {
        var raw = Value(headers, EnvelopeHeaders.Format);
        if (raw.State is ValueState.Absent)
        {
            return (null, null);
        }

        if (raw.State is not ValueState.Present)
        {
            return (null, Failure(raw, EnvelopeHeaders.Format));
        }

        return raw.Text switch
        {
            WireTokens.FormatJson => (SchemaFormat.Json, null),
            WireTokens.FormatAvro => (SchemaFormat.Avro, null),
            WireTokens.FormatProtobuf => (SchemaFormat.Protobuf, null),

            // Rejects rather than warns: an unknown format means this client cannot know how
            // to validate the payload, so proceeding would be a guess.
            _ => (null, EnvelopeReadResult.Malformed(
                ConcordatCodes.EnvelopeFormatUnknown,
                $"'{EnvelopeHeaders.Format}' is '{raw.Text}', which is not a known format token.")),
        };
    }

    private static EnvelopeReadResult ReadContentType(string? contentType, string? propertiesType)
    {
        if (!ContentTypeEnvelope.TryParse(contentType, out var schemaId, out var format))
        {
            return EnvelopeReadResult.NotEnveloped;
        }

        SubjectName? subject = null;
        if (!string.IsNullOrEmpty(propertiesType))
        {
            var parsed = SubjectName.Create(propertiesType);
            subject = parsed.IsSuccess ? parsed.Value : null;
        }

        return EnvelopeReadResult.Read(
            EnvelopeKind.ContentType,
            new MessageEnvelope(schemaId!, subject, null, null, format, []));
    }

    // ------------------------------------------------------------------ values

    private enum ValueState
    {
        Absent,
        Present,
        WrongType,
        BadEncoding,
        Empty,
    }

    private readonly record struct HeaderValue(ValueState State, string Text);

    private static HeaderValue Value(IReadOnlyDictionary<string, object?> headers, string name)
    {
        // Ordinal and case-sensitive on purpose. A case-folding lookup would accept
        // "Concordat-V" from one SDK and not another.
        if (!headers.TryGetValue(name, out var raw) || raw is null)
        {
            return new HeaderValue(ValueState.Absent, string.Empty);
        }

        string text;
        switch (raw)
        {
            case string s:
                text = s;
                break;

            // What RabbitMQ.Client hands back for a value it wrote as a field-table long
            // string. By design and permanent (ADR-010).
            case byte[] bytes:
                try
                {
                    text = StrictUtf8.GetString(bytes);
                }
                catch (DecoderFallbackException)
                {
                    return new HeaderValue(ValueState.BadEncoding, string.Empty);
                }

                break;

            case ReadOnlyMemory<byte> memory:
                try
                {
                    text = StrictUtf8.GetString(memory.Span);
                }
                catch (DecoderFallbackException)
                {
                    return new HeaderValue(ValueState.BadEncoding, string.Empty);
                }

                break;

            // Never ToString(): an int or a timestamp rendered as text would look like a
            // plausible value and be silently wrong.
            default:
                return new HeaderValue(ValueState.WrongType, string.Empty);
        }

        if (text.Length > 0 && text[0] == '﻿')
        {
            // Rejected rather than stripped. A BOM inside a header value means something
            // upstream is encoding wrongly, and silently accepting it hides that.
            return new HeaderValue(ValueState.BadEncoding, string.Empty);
        }

        return text.Length == 0
            ? new HeaderValue(ValueState.Empty, string.Empty)
            : new HeaderValue(ValueState.Present, text);
    }

    private static EnvelopeReadResult Failure(HeaderValue value, string header) => value.State switch
    {
        ValueState.WrongType => EnvelopeReadResult.Malformed(
            ConcordatCodes.EnvelopeHeaderTypeInvalid,
            $"'{header}' is not a string or byte array. Every envelope header is a UTF-8 string."),

        ValueState.BadEncoding => EnvelopeReadResult.Malformed(
            ConcordatCodes.EnvelopeHeaderEncodingInvalid,
            $"'{header}' is not well-formed UTF-8."),

        _ => EnvelopeReadResult.Malformed(
            ConcordatCodes.EnvelopeMalformed, $"'{header}' is present but empty."),
    };
}
