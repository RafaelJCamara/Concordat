using System.Text.Json;

namespace Concordat.Formats.Avro;

/// <summary>A parsed Avro type.</summary>
/// <remarks>
/// Only what schema resolution consults is modelled. Attributes that survive canonicalisation
/// but do not affect resolution — <c>logicalType</c>, <c>order</c>, <c>precision</c> — are
/// deliberately absent: the compatibility engine has no rule that reads them, and modelling
/// them would invite one to be added without a corresponding corpus case.
/// </remarks>
internal abstract record AvroType;

/// <summary>One of the eight primitive types.</summary>
/// <param name="Name">The primitive's spelling.</param>
internal sealed record AvroPrimitive(string Name) : AvroType;

/// <summary>A record field.</summary>
/// <param name="Name">The field name, which is how resolution matches it.</param>
/// <param name="Type">The field's schema.</param>
/// <param name="HasDefault">
/// Whether a <c>default</c> was declared. This is the single most load-bearing bit in the
/// model: a reader field absent from the writer resolves to its default, and errors without
/// one.
/// </param>
/// <param name="Aliases">
/// Names this field also answers to when it is the <em>reader's</em> field. Plain names, not
/// fullnames.
/// </param>
internal sealed record AvroField(
    string Name,
    AvroType Type,
    bool HasDefault,
    IReadOnlyList<string> Aliases);

/// <summary>A record type.</summary>
/// <param name="FullName">The resolved fullname.</param>
/// <param name="Fields">Fields in declaration order, which is also wire order.</param>
/// <param name="Aliases">Fullnames this record also answers to as a reader.</param>
internal sealed record AvroRecord(
    string FullName,
    IReadOnlyList<AvroField> Fields,
    IReadOnlyList<string> Aliases) : AvroType;

/// <summary>An enum type.</summary>
/// <param name="FullName">The resolved fullname.</param>
/// <param name="Symbols">Symbols in declaration order, which is their ordinal on the wire.</param>
/// <param name="Default">
/// The symbol an unknown writer symbol resolves to, added in Avro 1.9. Parsing Canonical Form
/// discards this; Concordat's canonical form keeps it, so the engine can honour it.
/// </param>
/// <param name="Aliases">Fullnames this enum also answers to as a reader.</param>
internal sealed record AvroEnum(
    string FullName,
    IReadOnlyList<string> Symbols,
    string? Default,
    IReadOnlyList<string> Aliases) : AvroType;

/// <summary>A fixed-width type.</summary>
/// <param name="FullName">The resolved fullname.</param>
/// <param name="Size">The width in bytes. Part of identity: resolution requires it to match.</param>
/// <param name="Aliases">Fullnames this type also answers to as a reader.</param>
internal sealed record AvroFixed(
    string FullName,
    int Size,
    IReadOnlyList<string> Aliases) : AvroType;

/// <summary>An array type.</summary>
/// <param name="Items">The item schema.</param>
internal sealed record AvroArray(AvroType Items) : AvroType;

/// <summary>A map type, whose keys are always strings.</summary>
/// <param name="Values">The value schema.</param>
internal sealed record AvroMap(AvroType Values) : AvroType;

/// <summary>A union.</summary>
/// <param name="Branches">Branches in declaration order, which is their wire index.</param>
internal sealed record AvroUnion(IReadOnlyList<AvroType> Branches) : AvroType;

/// <summary>
/// A reference to a named type defined elsewhere in the same document.
/// </summary>
/// <remarks>
/// Kept unresolved in the tree rather than inlined, because Avro permits a named type to
/// reference itself. Inlining would not terminate; the resolver dereferences through the
/// document's symbol table instead, guarding recursion with a visited-pair set.
/// </remarks>
/// <param name="FullName">The referenced fullname.</param>
internal sealed record AvroNamedRef(string FullName) : AvroType;

/// <summary>A parsed document: its root type and every named type defined within it.</summary>
/// <param name="Root">The root schema.</param>
/// <param name="Named">Every named type, keyed by fullname, for dereferencing.</param>
internal sealed record AvroDocument(AvroType Root, IReadOnlyDictionary<string, AvroType> Named)
{
    /// <summary>Follows a reference to the type it names.</summary>
    /// <param name="type">A type that may be a reference.</param>
    /// <returns>
    /// The referenced type, or the input unchanged when it is not a reference or names
    /// something this document does not define.
    /// </returns>
    /// <remarks>
    /// An unresolvable reference is returned as-is rather than throwing. Cross-document
    /// references are a real Avro pattern and are unresolved here by design — see
    /// ADR-023, which is why they cannot be registered yet.
    /// </remarks>
    public AvroType Deref(AvroType type) =>
        type is AvroNamedRef reference && Named.TryGetValue(reference.FullName, out var target)
            ? target
            : type;
}

/// <summary>
/// Builds an <see cref="AvroDocument"/> from canonical Avro text.
/// </summary>
/// <remarks>
/// Parses the <em>canonical</em> form only, so it can assume fullnames are already resolved
/// and <c>namespace</c> is already folded away. Feeding it an authored document would appear
/// to work and silently mismatch names that differ only in qualification.
/// </remarks>
internal static class AvroSchemaParser
{
    /// <summary>Parses canonical Avro text.</summary>
    /// <param name="canonicalBody">Output of <see cref="AvroSchemaCanonicalizer"/>.</param>
    /// <returns>The parsed document.</returns>
    /// <exception cref="MalformedAvroSchemaException">The document is not valid Avro.</exception>
    public static AvroDocument Parse(string canonicalBody)
    {
        using var document = JsonDocument.Parse(canonicalBody);

        var named = new Dictionary<string, AvroType>(StringComparer.Ordinal);
        var root = ParseType(document.RootElement, named);

        return new AvroDocument(root, named);
    }

    private static AvroType ParseType(JsonElement element, Dictionary<string, AvroType> named)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var name = element.GetString()!;
                return AvroSchemaCanonicalizer.PrimitiveTypes.Contains(name)
                    ? new AvroPrimitive(name)
                    : new AvroNamedRef(name);

            case JsonValueKind.Array:
                return new AvroUnion(
                    [.. element.EnumerateArray().Select(branch => ParseType(branch, named))]);

            case JsonValueKind.Object:
                return ParseObject(element, named);

            default:
                throw new MalformedAvroSchemaException(
                    $"A schema must be a string, an array or an object, not {element.ValueKind}.");
        }
    }

    private static AvroType ParseObject(JsonElement element, Dictionary<string, AvroType> named)
    {
        var type = element.GetProperty("type").GetString()!;

        if (AvroSchemaCanonicalizer.PrimitiveTypes.Contains(type))
        {
            return new AvroPrimitive(type);
        }

        switch (type)
        {
            case "array":
                return new AvroArray(ParseType(element.GetProperty("items"), named));

            case "map":
                return new AvroMap(ParseType(element.GetProperty("values"), named));

            case "record":
                return ParseRecord(element, named);

            case "enum":
                return Register(
                    named,
                    new AvroEnum(
                        element.GetProperty("name").GetString()!,
                        [.. element.GetProperty("symbols").EnumerateArray().Select(s => s.GetString()!)],
                        Text(element, "default"),
                        Aliases(element)));

            case "fixed":
                return Register(
                    named,
                    new AvroFixed(
                        element.GetProperty("name").GetString()!,
                        element.GetProperty("size").GetInt32(),
                        Aliases(element)));

            default:
                throw new MalformedAvroSchemaException($"Unknown Avro type '{type}'.");
        }
    }

    private static AvroRecord ParseRecord(JsonElement element, Dictionary<string, AvroType> named)
    {
        var fullName = element.GetProperty("name").GetString()!;
        var fields = new List<AvroField>();

        // Registered before its fields are parsed, so a field referring back to this record
        // resolves. The instance is mutated in place afterwards - the list is the same object.
        var record = new AvroRecord(fullName, fields, Aliases(element));
        named[fullName] = record;

        foreach (var field in element.GetProperty("fields").EnumerateArray())
        {
            fields.Add(new AvroField(
                field.GetProperty("name").GetString()!,
                ParseType(field.GetProperty("type"), named),
                field.TryGetProperty("default", out _),
                Aliases(field)));
        }

        return record;
    }

    private static AvroType Register(Dictionary<string, AvroType> named, AvroType type)
    {
        var fullName = type switch
        {
            AvroEnum e => e.FullName,
            AvroFixed f => f.FullName,
            AvroRecord r => r.FullName,
            _ => null,
        };

        if (fullName is not null)
        {
            named[fullName] = type;
        }

        return type;
    }

    private static IReadOnlyList<string> Aliases(JsonElement element) =>
        element.TryGetProperty("aliases", out var aliases) &&
        aliases.ValueKind is JsonValueKind.Array
            ? [.. aliases.EnumerateArray()
                .Where(a => a.ValueKind is JsonValueKind.String)
                .Select(a => a.GetString()!)]
            : [];

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;
}
