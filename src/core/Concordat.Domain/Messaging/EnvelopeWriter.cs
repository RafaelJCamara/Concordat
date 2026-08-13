using System.Globalization;
using Concordat.Domain.Registry;

namespace Concordat.Domain.Messaging;

/// <summary>
/// Writes schema identity onto a message (ADR-010).
/// </summary>
/// <remarks>
/// Produces a plain string-to-string map rather than touching any broker's types, so the
/// encoding is shared and testable without a broker.
/// </remarks>
public static class EnvelopeWriter
{
    /// <summary>Builds the Mode A headers for a message.</summary>
    /// <param name="schemaId">The content-addressed schema id.</param>
    /// <param name="subject">The subject, when known.</param>
    /// <param name="ordinal">The version ordinal, when known.</param>
    /// <param name="semver">The semantic version label, when the version carries one.</param>
    /// <param name="format">The schema language.</param>
    /// <returns>
    /// The headers to apply. Values are strings; a caller writing them into an AMQP field
    /// table must write them as strings and expect to read back <c>byte[]</c>.
    /// </returns>
    /// <remarks>
    /// An absent optional value is <b>omitted entirely</b>, never written as an empty string.
    /// The reader treats a present-but-empty header as malformed, so writing one would turn a
    /// perfectly good message into a quarantined one.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> Headers(
        SchemaId schemaId,
        SubjectName? subject = null,
        int? ordinal = null,
        SemanticVersion? semver = null,
        SchemaFormat? format = null)
    {
        ArgumentNullException.ThrowIfNull(schemaId);

        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EnvelopeHeaders.Version] = EnvelopeHeaders.CurrentVersion,
            [EnvelopeHeaders.SchemaId] = schemaId.Value,
        };

        if (subject is not null)
        {
            headers[EnvelopeHeaders.Subject] = subject.Value;
        }

        if (ordinal is { } n)
        {
            // Invariant culture, or a host running under a locale with digit grouping would
            // emit a value no other SDK can read.
            headers[EnvelopeHeaders.VersionOrdinal] = n.ToString(CultureInfo.InvariantCulture);
        }

        if (semver is { } label)
        {
            headers[EnvelopeHeaders.Semver] = label.ToString();
        }

        if (format is { } f)
        {
            headers[EnvelopeHeaders.Format] = WireTokens.For(f);
        }

        return headers;
    }
}

/// <summary>
/// Mode B: identity carried in the content-type token (ADR-010).
/// </summary>
/// <remarks>
/// <para>
/// For paths where headers may not survive. The payload is still untouched, so the body stays
/// readable by any tool — this is Azure's approach with its version defect fixed. Azure's
/// unversioned <c>avro/binary+{id}</c> cannot evolve; the <c>v1</c> token here can.
/// </para>
/// <para>
/// AMQP 0-9-1's <c>content_type</c> is a <c>shortstr</c>, capped at 255 bytes, and a version
/// token plus a 32-character hex id fits with room to spare.
/// </para>
/// </remarks>
public static class ContentTypeEnvelope
{
    private const string Marker = "+concordat.v1.";

    /// <summary>Builds the content-type for a payload.</summary>
    /// <param name="format">The schema language.</param>
    /// <param name="schemaId">The content-addressed schema id.</param>
    /// <returns>For example <c>application/json+concordat.v1.7f3a…</c>.</returns>
    public static string Format(SchemaFormat format, SchemaId schemaId)
    {
        ArgumentNullException.ThrowIfNull(schemaId);

        var media = format switch
        {
            SchemaFormat.Json => "application/json",
            SchemaFormat.Avro => "application/avro",
            SchemaFormat.Protobuf => "application/x-protobuf",
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
        };

        return media + Marker + schemaId.Value;
    }

    /// <summary>Attempts to read identity out of a content-type.</summary>
    /// <param name="contentType">The content-type, or <see langword="null"/>.</param>
    /// <param name="schemaId">The id, when present and valid.</param>
    /// <param name="format">The format implied by the media type.</param>
    /// <returns><see langword="true"/> when the content-type carries a valid Concordat token.</returns>
    /// <remarks>
    /// A content-type that carries the marker but a malformed id returns
    /// <see langword="false"/> rather than throwing: an unparseable Mode B token is
    /// indistinguishable, from here, from an ordinary content-type that happens to contain
    /// the marker text.
    /// </remarks>
    public static bool TryParse(string? contentType, out SchemaId? schemaId, out SchemaFormat? format)
    {
        schemaId = null;
        format = null;

        if (string.IsNullOrEmpty(contentType))
        {
            return false;
        }

        var marker = contentType.IndexOf(Marker, StringComparison.Ordinal);
        if (marker < 0)
        {
            return false;
        }

        var parsed = SchemaId.Create(contentType[(marker + Marker.Length)..]);
        if (parsed.IsFailure)
        {
            return false;
        }

        schemaId = parsed.Value;
        format = contentType[..marker] switch
        {
            "application/json" => SchemaFormat.Json,
            "application/avro" => SchemaFormat.Avro,
            "application/x-protobuf" => SchemaFormat.Protobuf,
            _ => null,
        };

        return true;
    }
}
