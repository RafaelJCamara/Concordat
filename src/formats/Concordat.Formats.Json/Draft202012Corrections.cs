using System.Globalization;
using System.Text.Json;

namespace Concordat.Formats.Json;

/// <summary>
/// Reconciles NJsonSchema's behaviour with draft 2020-12, which is the dialect Concordat pins.
/// </summary>
/// <remarks>
/// <para>
/// <b>The corpus is normative and the library is not.</b> Each correction here exists because a
/// conformance fixture failed, and the fixture was presumed right — the same call made for
/// <c>integer</c> in M2. An SDK built on <c>ajv</c>, <c>jsonschema</c> or
/// <c>santhosh-tekuri</c> follows the specification on every point below, so leaving .NET
/// divergent would mean the same payload passing in one language and being quarantined in
/// another with no bug on anyone's part.
/// </para>
/// <para>
/// <b>These corrections never touch identity.</b> The rewritten text is handed to the compiler
/// and thrown away; the canonical body, its hash and everything stored stay exactly as
/// authored. A correction that changed the schema id would be a far worse bug than the one it
/// fixed.
/// </para>
/// </remarks>
internal static class Draft202012Corrections
{
    /// <summary>Keywords whose value is itself a schema.</summary>
    private static readonly HashSet<string> SchemaValued = new(StringComparer.Ordinal)
    {
        "additionalProperties", "propertyNames", "contains", "not", "if", "then", "else",
        "items", "unevaluatedItems", "unevaluatedProperties", "contentSchema",
    };

    /// <summary>Keywords whose value is a map of name to schema.</summary>
    private static readonly HashSet<string> SchemaMapValued = new(StringComparer.Ordinal)
    {
        "properties", "patternProperties", "$defs", "definitions", "dependentSchemas",
    };

    /// <summary>Keywords whose value is an array of schemas.</summary>
    private static readonly HashSet<string> SchemaArrayValued = new(StringComparer.Ordinal)
    {
        "allOf", "anyOf", "oneOf", "prefixItems",
    };

    /// <summary>
    /// Replaces boolean subschemas with the object schemas that mean the same thing.
    /// </summary>
    /// <param name="canonicalSchema">The schema as stored.</param>
    /// <returns>Equivalent text NJsonSchema can compile, or the input when nothing changed.</returns>
    /// <remarks>
    /// <para>
    /// <c>true</c> and <c>false</c> are legal schemas from draft-06 onward: <c>true</c> accepts
    /// every instance, <c>false</c> rejects every instance. NJsonSchema <b>fails to compile a
    /// schema containing one</b>, and because an uncompilable schema is reported as invalid,
    /// every message under it would be quarantined. A registerable schema that quarantines all
    /// its own traffic is the worst failure shape available, so this is the most severe of the
    /// corrections here.
    /// </para>
    /// <para>
    /// The rewrite is exact: <c>{}</c> constrains nothing, and <c>{"not":{}}</c> is the negation
    /// of a schema that accepts everything, so it accepts nothing.
    /// </para>
    /// <para>
    /// <b>Only schema positions are rewritten.</b> <c>{"uniqueItems": true}</c> is a boolean
    /// <em>keyword</em>, not a subschema, and turning it into <c>{"uniqueItems": {}}</c> would
    /// corrupt the schema. That is why the keyword sets above are explicit rather than "any
    /// boolean".
    /// </para>
    /// </remarks>
    public static string RewriteBooleanSubschemas(string canonicalSchema)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(canonicalSchema);
        }
        catch (JsonException)
        {
            return canonicalSchema;
        }

        using (document)
        {
            if (!ContainsBooleanSubschema(document.RootElement, inSchemaPosition: true))
            {
                return canonicalSchema;
            }

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                WriteRewritten(document.RootElement, writer, inSchemaPosition: true);
            }

            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }
    }

    private static bool ContainsBooleanSubschema(JsonElement node, bool inSchemaPosition)
    {
        if (inSchemaPosition && node.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return true;
        }

        if (node.ValueKind is not JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in node.EnumerateObject())
        {
            if (SchemaValued.Contains(property.Name) &&
                ContainsBooleanSubschema(property.Value, inSchemaPosition: true))
            {
                return true;
            }

            if (SchemaMapValued.Contains(property.Name) &&
                property.Value.ValueKind is JsonValueKind.Object &&
                property.Value.EnumerateObject()
                    .Any(m => ContainsBooleanSubschema(m.Value, inSchemaPosition: true)))
            {
                return true;
            }

            if (SchemaArrayValued.Contains(property.Name) &&
                property.Value.ValueKind is JsonValueKind.Array &&
                property.Value.EnumerateArray()
                    .Any(m => ContainsBooleanSubschema(m, inSchemaPosition: true)))
            {
                return true;
            }
        }

        return false;
    }

    private static void WriteRewritten(
        JsonElement node, Utf8JsonWriter writer, bool inSchemaPosition)
    {
        if (inSchemaPosition && node.ValueKind is JsonValueKind.True)
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
            return;
        }

        if (inSchemaPosition && node.ValueKind is JsonValueKind.False)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("not");
            writer.WriteStartObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
            return;
        }

        if (node.ValueKind is not JsonValueKind.Object)
        {
            node.WriteTo(writer);
            return;
        }

        writer.WriteStartObject();

        foreach (var property in node.EnumerateObject())
        {
            writer.WritePropertyName(property.Name);

            if (SchemaValued.Contains(property.Name))
            {
                WriteRewritten(property.Value, writer, inSchemaPosition: true);
                continue;
            }

            if (SchemaMapValued.Contains(property.Name) &&
                property.Value.ValueKind is JsonValueKind.Object)
            {
                writer.WriteStartObject();
                foreach (var member in property.Value.EnumerateObject())
                {
                    writer.WritePropertyName(member.Name);
                    WriteRewritten(member.Value, writer, inSchemaPosition: true);
                }

                writer.WriteEndObject();
                continue;
            }

            if (SchemaArrayValued.Contains(property.Name) &&
                property.Value.ValueKind is JsonValueKind.Array)
            {
                writer.WriteStartArray();
                foreach (var member in property.Value.EnumerateArray())
                {
                    WriteRewritten(member, writer, inSchemaPosition: true);
                }

                writer.WriteEndArray();
                continue;
            }

            property.Value.WriteTo(writer);
        }

        writer.WriteEndObject();
    }

    // ------------------------------------------------------------------ over-strict

    /// <summary>
    /// Whether a length error is one NJsonSchema raised by counting UTF-16 code units.
    /// </summary>
    /// <param name="value">The string the error was raised against.</param>
    /// <param name="bound">The declared bound.</param>
    /// <param name="isMaximum">Whether the bound is <c>maxLength</c>.</param>
    /// <returns><see langword="true"/> when the correct count satisfies the bound.</returns>
    /// <remarks>
    /// <c>minLength</c> and <c>maxLength</c> count <em>characters</em> — Unicode code points.
    /// .NET's <c>string.Length</c>, like JavaScript's, counts UTF-16 code units, so anything
    /// outside the Basic Multilingual Plane counts double: an emoji or the treble clef
    /// U+1D11E is one character and two units. Python and Go count correctly, so this is a
    /// real three-way split rather than a nicety.
    /// </remarks>
    public static bool LengthSatisfiedByCodePoints(string value, int bound, bool isMaximum)
    {
        var codePoints = CountCodePoints(value);

        return isMaximum ? codePoints <= bound : codePoints >= bound;
    }

    private static int CountCodePoints(string value)
    {
        var count = 0;

        for (var i = 0; i < value.Length; i++)
        {
            count++;

            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length &&
                char.IsLowSurrogate(value[i + 1]))
            {
                i++;
            }
        }

        return count;
    }

    // ------------------------------------------------------------------ too permissive

    /// <summary>
    /// Finds violations NJsonSchema misses, walking the schema and payload together.
    /// </summary>
    /// <param name="canonicalSchema">The schema as stored.</param>
    /// <param name="payload">The document.</param>
    /// <returns>Findings as (pointer, kind, message) triples; empty when there are none.</returns>
    /// <remarks>
    /// <para>Two keywords where NJsonSchema accepts what the specification rejects:</para>
    /// <list type="bullet">
    ///   <item><description><b><c>enum</c> and <c>const</c> coerce.</b> The library matches the
    ///     string <c>"1"</c> against <c>enum: [1]</c>. JSON equality is type-strict, so every
    ///     other validator rejects it.</description></item>
    ///   <item><description><b><c>uniqueItems</c> compares serialised text.</b> Two objects with
    ///     the same members in a different order are the same JSON value and must violate
    ///     uniqueness; comparing their text says they differ.</description></item>
    /// </list>
    /// <para>
    /// The walk covers the keywords the compatibility engine also understands — <c>properties</c>,
    /// <c>items</c>, <c>prefixItems</c> — and deliberately stops at the applicator keywords.
    /// Those are exactly the ones <c>JsonSchemaPortabilityChecker</c> already warns are outside
    /// the interoperable subset, so the boundary is one line rather than two.
    /// </para>
    /// </remarks>
    public static List<(string Path, string Kind, string Message)> FindMissedViolations(
        string canonicalSchema, string payload)
    {
        var found = new List<(string, string, string)>();

        JsonDocument schema;
        JsonDocument document;

        try
        {
            schema = JsonDocument.Parse(canonicalSchema);
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return found;
        }

        using (schema)
        using (document)
        {
            Walk(schema.RootElement, document.RootElement, "#", found);
        }

        return found;
    }

    private static void Walk(
        JsonElement schema,
        JsonElement instance,
        string path,
        List<(string, string, string)> found)
    {
        if (schema.ValueKind is not JsonValueKind.Object)
        {
            return;
        }

        if (schema.TryGetProperty("enum", out var choices) &&
            choices.ValueKind is JsonValueKind.Array &&
            !choices.EnumerateArray().Any(c => JsonEqual(c, instance)))
        {
            found.Add((path, "EnumNotMatched",
                $"EnumNotMatched at {path}. The value is not a member of 'enum'; membership " +
                "compares JSON values, so a string never matches a number."));
        }

        if (schema.TryGetProperty("const", out var constant) && !JsonEqual(constant, instance))
        {
            found.Add((path, "ConstNotMatched",
                $"ConstNotMatched at {path}. The value does not equal 'const'."));
        }

        if (schema.TryGetProperty("uniqueItems", out var unique) &&
            unique.ValueKind is JsonValueKind.True &&
            instance.ValueKind is JsonValueKind.Array &&
            HasDuplicate(instance))
        {
            found.Add((path, "ArrayItemNotUnique",
                $"ArrayItemNotUnique at {path}. Two items are the same JSON value; member " +
                "order and number spelling do not make values distinct."));
        }

        if (schema.TryGetProperty("properties", out var properties) &&
            properties.ValueKind is JsonValueKind.Object &&
            instance.ValueKind is JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                if (instance.TryGetProperty(property.Name, out var value))
                {
                    Walk(property.Value, value, $"{path}/{Escape(property.Name)}", found);
                }
            }
        }

        if (instance.ValueKind is not JsonValueKind.Array)
        {
            return;
        }

        if (schema.TryGetProperty("items", out var items))
        {
            var index = 0;
            foreach (var element in instance.EnumerateArray())
            {
                Walk(items, element, $"{path}/{index.ToString(CultureInfo.InvariantCulture)}", found);
                index++;
            }
        }

        if (schema.TryGetProperty("prefixItems", out var prefix) &&
            prefix.ValueKind is JsonValueKind.Array)
        {
            var index = 0;
            foreach (var element in prefix.EnumerateArray())
            {
                if (index >= instance.GetArrayLength())
                {
                    break;
                }

                Walk(element, instance[index],
                    $"{path}/{index.ToString(CultureInfo.InvariantCulture)}", found);
                index++;
            }
        }
    }

    private static bool HasDuplicate(JsonElement array)
    {
        var items = array.EnumerateArray().ToList();

        for (var i = 0; i < items.Count; i++)
        {
            for (var j = i + 1; j < items.Count; j++)
            {
                if (JsonEqual(items[i], items[j]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Compares two JSON values by the specification's equality, not by their text.</summary>
    /// <remarks>
    /// Numbers compare mathematically, so <c>1</c> equals <c>1.0</c>. Object members are
    /// unordered. Both are places a text comparison quietly gives the wrong answer.
    /// </remarks>
    private static bool JsonEqual(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
        {
            return false;
        }

        switch (left.ValueKind)
        {
            case JsonValueKind.Number:
                return left.TryGetDecimal(out var a) && right.TryGetDecimal(out var b)
                    ? a == b
                    : string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal);

            case JsonValueKind.String:
                return string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal);

            case JsonValueKind.Object:
                var leftMembers = left.EnumerateObject().ToList();
                var rightMembers = right.EnumerateObject().ToList();

                return leftMembers.Count == rightMembers.Count &&
                    leftMembers.All(m =>
                        right.TryGetProperty(m.Name, out var counterpart) &&
                        JsonEqual(m.Value, counterpart));

            case JsonValueKind.Array:
                if (left.GetArrayLength() != right.GetArrayLength())
                {
                    return false;
                }

                for (var i = 0; i < left.GetArrayLength(); i++)
                {
                    if (!JsonEqual(left[i], right[i]))
                    {
                        return false;
                    }
                }

                return true;

            default:
                // null, true and false carry no payload beyond their kind.
                return true;
        }
    }

    // RFC 6901: '~' becomes '~0' and '/' becomes '~1'.
    private static string Escape(string token) =>
        token.Replace("~", "~0", StringComparison.Ordinal)
            .Replace("/", "~1", StringComparison.Ordinal);
}
