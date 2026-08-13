using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Concordat.Domain.Registry;

namespace Concordat.Formats.Abstractions;

/// <summary>
/// Derives the content-addressed <see cref="SchemaId"/> (ADR-015).
/// </summary>
/// <remarks>
/// <para>
/// The identity is the SHA-256 of a <em>preimage</em> covering the canonical body, the
/// format, and the reference set — not the body alone. Two schemas with identical bodies but
/// different references are different schemas, and hashing only the body would collide them.
/// This is the mistake Confluent's CP 8.1 GUID computation exists to avoid.
/// </para>
/// <para>
/// <b>The preimage format is itself normative protocol.</b> Changing it changes every id in
/// every installation, so it carries an explicit version tag — the same lesson as the
/// envelope's <c>concordat-v</c> (ADR-010), where Azure's unversioned scheme is the
/// cautionary example.
/// </para>
/// </remarks>
public static class SchemaIdComputer
{
    /// <summary>
    /// The version tag opening every preimage. Bump only alongside a migration plan.
    /// </summary>
    public const string PreimageVersion = "concordat-schema-id/v1";

    /// <summary>The number of bytes of the digest retained: 128 bits.</summary>
    public const int IdBytes = 16;

    /// <summary>Computes the identity of a canonical schema.</summary>
    /// <param name="format">The schema format.</param>
    /// <param name="canonicalBody">
    /// The output of the format's <see cref="ISchemaCanonicalizer"/>. Passing a non-canonical
    /// body produces a valid but wrong id, so callers must canonicalise first.
    /// </param>
    /// <param name="references">
    /// The schema's references. Order is not significant — they are sorted by name before
    /// hashing, matching the ordering <see cref="Schema"/> already imposes.
    /// </param>
    /// <returns>The content-addressed identity.</returns>
    public static SchemaId Compute(
        SchemaFormat format,
        string canonicalBody,
        IReadOnlyList<Reference>? references = null)
    {
        ArgumentNullException.ThrowIfNull(canonicalBody);

        var preimage = BuildPreimage(format, canonicalBody, references);
        var digest = SHA256.HashData(preimage);

        // Truncation to 128 bits: ample against collision for this population, and it halves
        // the bytes carried in every message header.
        return SchemaId.FromTrusted(Convert.ToHexStringLower(digest.AsSpan(0, IdBytes)));
    }

    /// <summary>
    /// Builds the exact byte sequence that gets hashed.
    /// </summary>
    /// <param name="format">The schema format.</param>
    /// <param name="canonicalBody">The canonical schema text.</param>
    /// <param name="references">The schema's references.</param>
    /// <returns>The preimage bytes.</returns>
    /// <remarks>
    /// Exposed so the conformance corpus can pin it directly. Every variable-length field is
    /// length-prefixed in bytes: without that, a reference named <c>"a:b"</c> and a pair of
    /// references <c>"a"</c> and <c>"b"</c> could serialise identically and collide.
    /// </remarks>
    public static byte[] BuildPreimage(
        SchemaFormat format,
        string canonicalBody,
        IReadOnlyList<Reference>? references = null)
    {
        ArgumentNullException.ThrowIfNull(canonicalBody);

        var ordered = (references ?? [])
            .OrderBy(r => r.Name, StringComparer.Ordinal)
            .ToList();

        var preimage = new StringBuilder()
            .Append(PreimageVersion).Append('\n')
            .Append("format:").Append(WireTokens.For(format)).Append('\n');

        AppendLengthPrefixed(preimage, "body", canonicalBody);

        preimage.Append("refs:")
            .Append(ordered.Count.ToString(CultureInfo.InvariantCulture))
            .Append('\n');

        foreach (var reference in ordered)
        {
            AppendLengthPrefixed(preimage, "ref-name", reference.Name);
            AppendLengthPrefixed(preimage, "ref-subject", reference.Subject.Value);
            preimage.Append("ref-version:")
                .Append(reference.Version.ToString(CultureInfo.InvariantCulture))
                .Append('\n');
        }

        return Encoding.UTF8.GetBytes(preimage.ToString());
    }

    // Length is in UTF-8 BYTES, not UTF-16 chars: the preimage is hashed as UTF-8, so a
    // char count would not actually delimit the encoded field for non-ASCII content.
    private static void AppendLengthPrefixed(StringBuilder buffer, string label, string value) =>
        buffer.Append(label)
            .Append(':')
            .Append(Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value)
            .Append('\n');
}
