namespace Concordat.Domain.Registry;

/// <summary>
/// The stable wire spellings of domain enumerations.
/// </summary>
/// <remarks>
/// These strings are normative protocol (ADR-019). They appear in the
/// <c>concordat-format</c> envelope header, in REST payloads, and inside the schema-id
/// preimage, so they are deliberately not <c>Enum.ToString()</c> — renaming a C# member must
/// not silently change the wire format or invalidate every stored schema id.
/// </remarks>
public static class WireTokens
{
    /// <summary>The wire token for <see cref="SchemaFormat.Json"/>.</summary>
    public const string FormatJson = "json";

    /// <summary>The wire token for <see cref="SchemaFormat.Avro"/>.</summary>
    public const string FormatAvro = "avro";

    /// <summary>The wire token for <see cref="SchemaFormat.Protobuf"/>.</summary>
    public const string FormatProtobuf = "protobuf";

    /// <summary>Maps a format to its wire token.</summary>
    /// <param name="format">The format.</param>
    /// <returns>The stable token.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The format is not a known member.</exception>
    public static string For(SchemaFormat format) => format switch
    {
        SchemaFormat.Json => FormatJson,
        SchemaFormat.Avro => FormatAvro,
        SchemaFormat.Protobuf => FormatProtobuf,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown schema format."),
    };
}
