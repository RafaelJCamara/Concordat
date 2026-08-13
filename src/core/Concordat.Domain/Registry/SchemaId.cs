using System.Text.RegularExpressions;
using Concordat.Domain.Results;

namespace Concordat.Domain.Registry;

/// <summary>
/// A content-addressed schema identifier: 128 bits as 32 lowercase hexadecimal characters
/// (ADR-015).
/// </summary>
/// <remarks>
/// <para>
/// The hexadecimal string is the source of truth rather than a <c>UInt128</c> or byte array,
/// because it <em>is</em> the wire form — it travels in the <c>concordat-schema-id</c> header,
/// in URLs, and in the Mode B content-type token. Storing it as text removes any round-trip
/// risk on the path that matters.
/// </para>
/// <para>
/// M1.1 provides validation, equality and parsing only. Deriving the value by hashing a
/// canonical form is M1.2.
/// </para>
/// </remarks>
public sealed partial record SchemaId
{
    private SchemaId(string value) => Value = value;

    /// <summary>The canonical 32-character lowercase hexadecimal representation.</summary>
    public string Value { get; }

    /// <summary>Parses and validates a schema id.</summary>
    /// <param name="value">Candidate text.</param>
    /// <returns>
    /// The identifier, or a failure carrying <see cref="ConcordatCodes.SchemaIdMalformed"/>
    /// when the value is null, empty, not 32 characters, or contains anything outside
    /// <c>[0-9a-f]</c>. Uppercase is rejected rather than normalised: two spellings of one id
    /// would defeat the point of content addressing.
    /// </returns>
    public static Result<SchemaId> Create(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Result<SchemaId>.Failure(
                ConcordatCodes.SchemaIdMalformed, "A schema id is required.");
        }

        if (!HexPattern().IsMatch(value))
        {
            return Result<SchemaId>.Failure(
                ConcordatCodes.SchemaIdMalformed,
                $"A schema id must be exactly 32 lowercase hexadecimal characters; got '{value}'.");
        }

        return Result<SchemaId>.Success(new SchemaId(value));
    }

    /// <summary>
    /// Wraps a value already known to satisfy the invariant, skipping validation.
    /// </summary>
    /// <param name="value">A value the caller guarantees is 32 lowercase hexadecimal characters.</param>
    /// <returns>The identifier.</returns>
    /// <remarks>
    /// For M1.2 hash derivation and M1.5 materialisation from storage only. Every other caller
    /// must use <see cref="Create(string?)"/>.
    /// </remarks>
    public static SchemaId FromTrusted(string value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value;

    // \z rather than $: in .NET, $ also matches immediately before a trailing newline, so a
    // value ending "\n" would pass a $-anchored pattern.
    [GeneratedRegex(@"^[0-9a-f]{32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex HexPattern();
}
