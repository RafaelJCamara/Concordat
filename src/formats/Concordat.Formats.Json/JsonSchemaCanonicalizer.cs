using System.Text.Encodings.Web;
using System.Text.Json;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;
using Concordat.Formats.Abstractions;

namespace Concordat.Formats.Json;

/// <summary>
/// Reduces a JSON Schema document to canonical text (ADR-015).
/// </summary>
/// <remarks>
/// <para>The canonical form is:</para>
/// <list type="bullet">
///   <item><description>object members sorted by key, ordinal by UTF-16 code unit;</description></item>
///   <item><description>no insignificant whitespace;</description></item>
///   <item><description>minimal, deterministic string escaping;</description></item>
///   <item><description>array order preserved — it is significant in JSON Schema.</description></item>
/// </list>
/// <para>
/// <b>Not yet done: <c>$id</c> and <c>$ref</c> normalisation.</b> ADR-015 lists "<c>$id</c>
/// resolved" as part of the canonical form. It is deferred to M1.4 rather than half-built
/// here, because normalising <c>$id</c> without also resolving the <c>$ref</c> values that
/// point at it is incoherent — it would change the base a reference resolves against while
/// leaving the reference alone. Until then two documents differing only in the spelling of an
/// equivalent <c>$id</c> URI produce different ids, which is a missed deduplication rather
/// than a correctness bug.
/// </para>
/// <para>
/// <b>Number literals are preserved verbatim</b>, so <c>1.0</c> and <c>1</c> yield different
/// ids. This deviates from RFC 8785, which routes numbers through an ECMAScript double and
/// therefore loses precision on large integers. The conservative choice can only cause a
/// missed deduplication; the RFC 8785 choice can corrupt a <c>maximum</c> or
/// <c>multipleOf</c> value. It is also markedly easier to reimplement identically in Python,
/// Go and Java, which ADR-019 requires.
/// </para>
/// </remarks>
public sealed class JsonSchemaCanonicalizer : ISchemaCanonicalizer
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

    /// <inheritdoc />
    public SchemaFormat Format => SchemaFormat.Json;

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
            using (var writer = new Utf8JsonWriter(stream, WriterOptions))
            {
                WriteCanonical(document.RootElement, writer);
            }

            return Result<string>.Success(
                System.Text.Encoding.UTF8.GetString(stream.ToArray()));
        }
    }

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                             .OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(property.Value, writer);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                // Order is significant: "enum": [1, 2] and [2, 1] are different constraints in
                // some dialects, and "prefixItems" is positional. Never sort.
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(item, writer);
                }

                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                // Round-tripping through the writer normalises escaping: "A" becomes "A".
                writer.WriteStringValue(element.GetString());
                break;

            case JsonValueKind.Number:
                // Raw text, deliberately. See the remarks on this class.
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: false);
                break;

            case JsonValueKind.True:
            case JsonValueKind.False:
                writer.WriteBooleanValue(element.GetBoolean());
                break;

            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;

            case JsonValueKind.Undefined:
            default:
                throw new InvalidOperationException(
                    $"Unexpected JSON value kind '{element.ValueKind}'.");
        }
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
