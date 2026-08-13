using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;
using Concordat.Formats.Abstractions;

namespace Concordat.Formats.Avro;

/// <summary>
/// Reduces an Avro schema document to canonical text (ADR-015).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the Avro specification's Parsing Canonical Form with one deliberate deviation:
/// nothing semantic is stripped.</b> PCF's <c>[STRIP]</c> rule keeps only <c>type</c>,
/// <c>name</c>, <c>fields</c>, <c>symbols</c>, <c>items</c>, <c>values</c> and <c>size</c>,
/// which discards <c>default</c>, <c>aliases</c> and <c>logicalType</c>. Concordat cannot do
/// that, for two reasons that only became visible when Avro landed:
/// </para>
/// <list type="number">
///   <item><description>
///     The canonical text is what the registry <b>stores and serves</b>
///     (<c>CompatibilityEvaluator</c> passes it to <c>Schema.Create</c>). A consumer that
///     fetches an Avro schema with its defaults stripped cannot read data written under an
///     older version — the one job an Avro reader schema exists to do.
///   </description></item>
///   <item><description>
///     The canonical text is also what <c>ICompatibilityChecker</c> compares. Avro's schema
///     resolution rules run on <c>default</c> and <c>aliases</c>; without them the checker has
///     to call every added field breaking, including the ones carrying a default, which is the
///     ordinary Avro change.
///   </description></item>
/// </list>
/// <para>
/// So the rule here is <b>lossless normalisation</b>: every attribute that can change how data
/// is read or resolved survives, and only <c>doc</c> — the one purely presentational
/// attribute — is dropped, so that a comment edit does not mint a new schema id. See
/// DECISIONS-PENDING #17; this is reversible until the first Avro schema is stored.
/// </para>
/// <para>
/// <b>For a schema that uses none of the attributes PCF would have stripped, the output is
/// byte-identical to PCF.</b> The form only diverges where PCF would have lost information.
/// </para>
/// <para>The transformation, applied to every node:</para>
/// <list type="bullet">
///   <item><description><b>FULLNAMES</b> — every named type and every alias is rewritten to
///     its fully-qualified name, resolved against the nearest enclosing namespace, and the
///     now-redundant <c>namespace</c> attribute is dropped.</description></item>
///   <item><description><b>PRIMITIVES</b> — a primitive carrying no surviving attributes
///     reduces to the bare string, so <c>{"type":"long"}</c> becomes <c>"long"</c>. One
///     carrying a <c>logicalType</c> keeps its object form, because that attribute decides
///     the generated type.</description></item>
///   <item><description><b>ORDER</b> — the structural keys are written in the specification's
///     fixed order (<c>name</c>, <c>type</c>, <c>fields</c>, <c>symbols</c>, <c>items</c>,
///     <c>values</c>, <c>size</c>); every other attribute follows, sorted ordinally, so that
///     arbitrary attributes still canonicalise deterministically.</description></item>
///   <item><description><b>ORDER IS PRESERVED</b> in <c>fields</c>, <c>symbols</c> and union
///     branches — all three are semantic in Avro.</description></item>
///   <item><description><b>WHITESPACE</b> — none outside string literals, and number literals
///     are preserved verbatim, matching the JSON canonicaliser's rule.</description></item>
/// </list>
/// <para>
/// <b>Hand-written against the specification, not delegated to a parser library</b>, for the
/// same reason as <c>JsonSchemaCanonicalizer</c>: ADR-019 requires every SDK to reproduce this
/// byte-for-byte, and a third-party library's own notion of "canonical" is a dependency this
/// project cannot audit for cross-language agreement.
/// </para>
/// </remarks>
public sealed class AvroSchemaCanonicalizer : ISchemaCanonicalizer
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        // The default encoder escapes '<', '>', '&' and non-ASCII for HTML safety. Canonical
        // output wants the minimum, and it must not depend on where the text is later shown.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        SkipValidation = false,
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    internal static readonly HashSet<string> PrimitiveTypes = new(StringComparer.Ordinal)
    {
        "null", "boolean", "int", "long", "float", "double", "bytes", "string",
    };

    /// <summary>
    /// The only attribute dropped outright.
    /// </summary>
    /// <remarks>
    /// Purely presentational: no Avro reader, writer or resolver consults it. Dropping it is
    /// what keeps a comment edit from producing a second schema id for the same schema, which
    /// is the near-duplicate accumulation DESIGN §4 wants canonicalisation to prevent.
    /// </remarks>
    private static readonly HashSet<string> DroppedAttributes = new(StringComparer.Ordinal)
    {
        "doc",
    };

    /// <inheritdoc />
    public SchemaFormat Format => SchemaFormat.Avro;

    /// <inheritdoc />
    public Result<string> Canonicalize(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return Result<string>.Failure(
                ConcordatCodes.SchemaBodyEmpty, "A schema body is required.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body, DocumentOptions);
        }
        catch (JsonException ex)
        {
            return Result<string>.Failure(
                ConcordatCodes.SchemaMalformed, $"Not well-formed JSON: {ex.Message}");
        }

        using (document)
        {
            var duplicate = FindDuplicateKey(document.RootElement);
            if (duplicate is not null)
            {
                // Parsers disagree on which value wins, so the same bytes could mean different
                // things to producer and consumer. Refuse rather than pick.
                return Result<string>.Failure(
                    ConcordatCodes.SchemaMalformed,
                    $"Duplicate object key '{duplicate}'. JSON parsers disagree on which " +
                    "value wins, so this document has no single meaning.");
            }

            using var stream = new MemoryStream();
            try
            {
                using (var writer = new Utf8JsonWriter(stream, WriterOptions))
                {
                    WriteSchema(document.RootElement, writer, enclosingNamespace: null);
                }
            }
            catch (MalformedAvroSchemaException ex)
            {
                return Result<string>.Failure(ConcordatCodes.SchemaMalformed, ex.Message);
            }

            return Result<string>.Success(Encoding.UTF8.GetString(stream.ToArray()));
        }
    }

    // -------------------------------------------------------------------- schema nodes

    private static void WriteSchema(
        JsonElement schema, Utf8JsonWriter writer, string? enclosingNamespace)
    {
        switch (schema.ValueKind)
        {
            case JsonValueKind.String:
                WriteNameOrPrimitive(schema.GetString(), writer, enclosingNamespace);
                return;

            case JsonValueKind.Array:
                // A union. Branch order is significant - it is the wire index of each branch.
                writer.WriteStartArray();
                foreach (var branch in schema.EnumerateArray())
                {
                    WriteSchema(branch, writer, enclosingNamespace);
                }

                writer.WriteEndArray();
                return;

            case JsonValueKind.Object:
                WriteObjectSchema(schema, writer, enclosingNamespace);
                return;

            default:
                throw new MalformedAvroSchemaException(
                    $"A schema must be a string, an array or an object, not {schema.ValueKind}.");
        }
    }

    private static void WriteNameOrPrimitive(
        string? name, Utf8JsonWriter writer, string? enclosingNamespace)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new MalformedAvroSchemaException("A type name cannot be empty.");
        }

        writer.WriteStringValue(
            PrimitiveTypes.Contains(name)
                ? name
                : ResolveFullName(name, explicitNamespace: null, enclosingNamespace));
    }

    private static void WriteObjectSchema(
        JsonElement schema, Utf8JsonWriter writer, string? enclosingNamespace)
    {
        if (!schema.TryGetProperty("type", out var typeProperty) ||
            typeProperty.ValueKind != JsonValueKind.String)
        {
            throw new MalformedAvroSchemaException("An object schema must carry a string 'type'.");
        }

        var type = typeProperty.GetString()!;

        if (PrimitiveTypes.Contains(type))
        {
            WritePrimitiveObject(schema, writer, type, enclosingNamespace);
            return;
        }

        switch (type)
        {
            case "record" or "error":
                // 'error' is a record in every respect that affects data; it exists only to
                // mark a type usable as a protocol error.
                WriteNamed(schema, writer, enclosingNamespace, "record", WriteRecordBody);
                return;

            case "enum":
                WriteNamed(schema, writer, enclosingNamespace, "enum", WriteEnumBody);
                return;

            case "fixed":
                WriteNamed(schema, writer, enclosingNamespace, "fixed", WriteFixedBody);
                return;

            case "array":
                writer.WriteStartObject();
                writer.WriteString("type", "array");
                writer.WritePropertyName("items");
                WriteSchema(RequireProperty(schema, "items"), writer, enclosingNamespace);
                WriteExtras(schema, writer, enclosingNamespace, "type", "items");
                writer.WriteEndObject();
                return;

            case "map":
                writer.WriteStartObject();
                writer.WriteString("type", "map");
                writer.WritePropertyName("values");
                WriteSchema(RequireProperty(schema, "values"), writer, enclosingNamespace);
                WriteExtras(schema, writer, enclosingNamespace, "type", "values");
                writer.WriteEndObject();
                return;

            default:
                throw new MalformedAvroSchemaException($"Unknown Avro type '{type}'.");
        }
    }

    /// <summary>
    /// Writes a primitive given in object form, reducing it to a bare string when nothing
    /// survives beside the type.
    /// </summary>
    /// <remarks>
    /// PCF always reduces here. This does not, because <c>logicalType</c> (and the
    /// <c>precision</c>/<c>scale</c> that accompany <c>decimal</c>) decide the type a generator
    /// emits and how a reader interprets the bytes. Discarding them would turn a timestamp back
    /// into a bare <c>long</c> in the schema the registry serves.
    /// </remarks>
    private static void WritePrimitiveObject(
        JsonElement schema, Utf8JsonWriter writer, string type, string? enclosingNamespace)
    {
        var extras = Extras(schema, "type").ToList();
        if (extras.Count == 0)
        {
            writer.WriteStringValue(type);
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("type", type);
        WriteExtraProperties(extras, writer, enclosingNamespace, isNamedType: false);
        writer.WriteEndObject();
    }

    private static void WriteNamed(
        JsonElement schema,
        Utf8JsonWriter writer,
        string? enclosingNamespace,
        string canonicalType,
        Action<JsonElement, Utf8JsonWriter, string?> writeBody)
    {
        var (fullName, ownNamespace) = ResolveName(schema, enclosingNamespace);

        writer.WriteStartObject();
        writer.WriteString("name", fullName);
        writer.WriteString("type", canonicalType);
        writeBody(schema, writer, ownNamespace);

        // 'namespace' is folded into the fullname above; 'name' and 'type' are already written.
        // Everything else - including 'aliases' and 'default' - survives, sorted.
        WriteExtras(schema, writer, ownNamespace, "name", "namespace", "type", BodyKeyOf(canonicalType));
        writer.WriteEndObject();
    }

    private static string BodyKeyOf(string canonicalType) => canonicalType switch
    {
        "record" => "fields",
        "enum" => "symbols",
        "fixed" => "size",
        _ => throw new MalformedAvroSchemaException($"Unknown named type '{canonicalType}'."),
    };

    private static void WriteRecordBody(
        JsonElement schema, Utf8JsonWriter writer, string? ownNamespace)
    {
        writer.WritePropertyName("fields");
        writer.WriteStartArray();
        foreach (var field in RequireArray(schema, "fields").EnumerateArray())
        {
            WriteField(field, writer, ownNamespace);
        }

        writer.WriteEndArray();
    }

    private static void WriteField(JsonElement field, Utf8JsonWriter writer, string? ownNamespace)
    {
        if (field.ValueKind != JsonValueKind.Object ||
            !field.TryGetProperty("name", out var nameProperty) ||
            nameProperty.ValueKind != JsonValueKind.String ||
            string.IsNullOrEmpty(nameProperty.GetString()))
        {
            throw new MalformedAvroSchemaException(
                "A field must be an object with a non-empty 'name'.");
        }

        writer.WriteStartObject();
        writer.WriteString("name", nameProperty.GetString());
        writer.WritePropertyName("type");
        WriteSchema(RequireProperty(field, "type"), writer, ownNamespace);

        // 'default', 'order' and 'aliases' all land here. A field alias is a plain name, not a
        // fullname, so it is written through unchanged.
        WriteExtras(field, writer, ownNamespace, "name", "type");
        writer.WriteEndObject();
    }

    private static void WriteEnumBody(
        JsonElement schema, Utf8JsonWriter writer, string? ownNamespace)
    {
        writer.WritePropertyName("symbols");
        writer.WriteStartArray();
        foreach (var symbol in RequireArray(schema, "symbols").EnumerateArray())
        {
            if (symbol.ValueKind != JsonValueKind.String)
            {
                throw new MalformedAvroSchemaException("Every enum symbol must be a string.");
            }

            // Order is significant: it is the symbol's ordinal on the wire.
            writer.WriteStringValue(symbol.GetString());
        }

        writer.WriteEndArray();
    }

    private static void WriteFixedBody(
        JsonElement schema, Utf8JsonWriter writer, string? ownNamespace)
    {
        var sizeProperty = RequireProperty(schema, "size");
        if (sizeProperty.ValueKind != JsonValueKind.Number ||
            !sizeProperty.TryGetInt64(out var size) ||
            size < 0)
        {
            throw new MalformedAvroSchemaException("'size' must be a non-negative integer.");
        }

        writer.WriteNumber("size", size);
    }

    // -------------------------------------------------------------------- attributes

    private static IEnumerable<JsonProperty> Extras(JsonElement node, params string[] structural)
    {
        var skip = new HashSet<string>(structural, StringComparer.Ordinal);

        return node.EnumerateObject()
            .Where(p => !skip.Contains(p.Name) && !DroppedAttributes.Contains(p.Name))
            .OrderBy(p => p.Name, StringComparer.Ordinal);
    }

    private static void WriteExtras(
        JsonElement node, Utf8JsonWriter writer, string? ownNamespace, params string[] structural)
    {
        var isNamedType = structural.Contains("namespace", StringComparer.Ordinal);
        WriteExtraProperties(Extras(node, structural), writer, ownNamespace, isNamedType);
    }

    private static void WriteExtraProperties(
        IEnumerable<JsonProperty> extras,
        Utf8JsonWriter writer,
        string? ownNamespace,
        bool isNamedType)
    {
        foreach (var property in extras)
        {
            writer.WritePropertyName(property.Name);

            // A named type's aliases are names, resolved against the same namespace the type
            // itself resolved in. Leaving them unqualified would make 'Old' and 'acme.Old'
            // canonicalise differently while meaning the same thing.
            if (isNamedType &&
                string.Equals(property.Name, "aliases", StringComparison.Ordinal) &&
                property.Value.ValueKind is JsonValueKind.Array)
            {
                writer.WriteStartArray();
                foreach (var alias in property.Value.EnumerateArray())
                {
                    if (alias.ValueKind != JsonValueKind.String)
                    {
                        throw new MalformedAvroSchemaException("Every alias must be a string.");
                    }

                    writer.WriteStringValue(
                        ResolveFullName(alias.GetString()!, null, ownNamespace));
                }

                writer.WriteEndArray();
                continue;
            }

            WriteJsonValue(property.Value, writer);
        }
    }

    /// <summary>
    /// Writes an arbitrary JSON value — a <c>default</c>, a <c>logicalType</c>, anything a
    /// future Avro revision adds — deterministically.
    /// </summary>
    /// <remarks>
    /// Object keys sort; array order is preserved, because a <c>default</c> for an array field
    /// is data and its order is meaningful. Number literals are written verbatim, the same
    /// deliberate deviation from RFC 8785 the JSON canonicaliser makes: routing them through a
    /// double loses precision on large integers, and a missed dedup is the safer failure.
    /// </remarks>
    private static void WriteJsonValue(JsonElement value, Utf8JsonWriter writer)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject()
                             .OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteJsonValue(property.Value, writer);
                }

                writer.WriteEndObject();
                return;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteJsonValue(item, writer);
                }

                writer.WriteEndArray();
                return;

            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                return;

            case JsonValueKind.Number:
                writer.WriteRawValue(value.GetRawText(), skipInputValidation: false);
                return;

            case JsonValueKind.True:
            case JsonValueKind.False:
                writer.WriteBooleanValue(value.GetBoolean());
                return;

            case JsonValueKind.Null:
                writer.WriteNullValue();
                return;

            case JsonValueKind.Undefined:
            default:
                throw new MalformedAvroSchemaException(
                    $"Unexpected JSON value kind '{value.ValueKind}'.");
        }
    }

    // -------------------------------------------------------------------- naming

    /// <summary>Resolves a named type's fullname and the namespace its children inherit.</summary>
    private static (string FullName, string? Namespace) ResolveName(
        JsonElement schema, string? enclosingNamespace)
    {
        if (!schema.TryGetProperty("name", out var nameProperty) ||
            nameProperty.ValueKind != JsonValueKind.String ||
            string.IsNullOrEmpty(nameProperty.GetString()))
        {
            throw new MalformedAvroSchemaException("A named type must carry a non-empty 'name'.");
        }

        string? explicitNamespace = null;
        if (schema.TryGetProperty("namespace", out var namespaceProperty))
        {
            if (namespaceProperty.ValueKind != JsonValueKind.String)
            {
                throw new MalformedAvroSchemaException("'namespace' must be a string.");
            }

            explicitNamespace = namespaceProperty.GetString();
        }

        var fullName = ResolveFullName(
            nameProperty.GetString()!, explicitNamespace, enclosingNamespace);

        var lastDot = fullName.LastIndexOf('.');
        return (fullName, lastDot < 0 ? null : fullName[..lastDot]);
    }

    /// <summary>Resolves a name to a fullname per the specification's Names rules.</summary>
    internal static string ResolveFullName(
        string name, string? explicitNamespace, string? enclosingNamespace)
    {
        if (name.Contains('.', StringComparison.Ordinal))
        {
            // Already a fullname; any namespace given alongside it is ignored.
            return name;
        }

        var ns = explicitNamespace ?? enclosingNamespace;
        return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
    }

    // -------------------------------------------------------------------- plumbing

    private static JsonElement RequireProperty(JsonElement schema, string name)
    {
        if (!schema.TryGetProperty(name, out var value))
        {
            throw new MalformedAvroSchemaException($"Missing required property '{name}'.");
        }

        return value;
    }

    private static JsonElement RequireArray(JsonElement schema, string name)
    {
        var value = RequireProperty(schema, name);
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new MalformedAvroSchemaException($"'{name}' must be an array.");
        }

        return value;
    }

    private static string? FindDuplicateKey(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!seen.Add(property.Name))
                    {
                        return property.Name;
                    }

                    var nested = FindDuplicateKey(property.Value);
                    if (nested is not null)
                    {
                        return nested;
                    }
                }

                return null;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var nested = FindDuplicateKey(item);
                    if (nested is not null)
                    {
                        return nested;
                    }
                }

                return null;

            default:
                return null;
        }
    }
}
