using System.Text.Encodings.Web;
using System.Text.Json;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;
using Concordat.Formats.Abstractions;

namespace Concordat.Formats.Json;

/// <summary>
/// Inlines <c>concordat://</c> references into <c>$defs</c> and rewrites them as local
/// pointers.
/// </summary>
/// <remarks>
/// The result validates standalone: every external reference becomes
/// <c>#/$defs/&lt;key&gt;</c>, where the key is a filesystem-safe rendering of the target's
/// subject and version. Local and HTTP refs are left exactly as they were — resolving those
/// is the validator's job, not the registry's.
/// </remarks>
public sealed class JsonSchemaBundler : ISchemaBundler
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <inheritdoc />
    public SchemaFormat Format => SchemaFormat.Json;

    /// <inheritdoc />
    public Result<string> Bundle(
        string canonicalBody, IReadOnlyDictionary<string, string> resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        if (string.IsNullOrWhiteSpace(canonicalBody))
        {
            return Result<string>.Failure(
                ConcordatCodes.SchemaBodyEmpty, "A schema body is required.");
        }

        JsonDocument root;
        try
        {
            root = JsonDocument.Parse(canonicalBody);
        }
        catch (JsonException ex)
        {
            return Result<string>.Failure(
                ConcordatCodes.SchemaMalformed, $"Not well-formed JSON: {ex.Message}");
        }

        using (root)
        {
            var missing = resolved.Count == 0 ? null : (string?)null;
            var defs = new Dictionary<string, JsonDocument>(StringComparer.Ordinal);

            try
            {
                foreach (var (reference, body) in resolved)
                {
                    var key = DefinitionKey(reference);
                    if (key is null)
                    {
                        return Result<string>.Failure(
                            ConcordatCodes.ReferenceInvalid,
                            $"'{reference}' is not a Concordat reference.");
                    }

                    defs[key] = JsonDocument.Parse(body);
                }

                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream, WriterOptions))
                {
                    missing = WriteBundled(root.RootElement, writer, resolved, defs, isRoot: true);
                }

                return missing is not null
                    ? Result<string>.Failure(
                        ConcordatCodes.ReferenceInvalid,
                        $"Cannot bundle: '{missing}' was not supplied among the resolved references.")
                    : Result<string>.Success(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
            }
            finally
            {
                foreach (var document in defs.Values)
                {
                    document.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// Renders a reference as a <c>$defs</c> key: <c>subject__version</c>.
    /// </summary>
    /// <param name="reference">The <c>concordat://</c> reference text.</param>
    /// <returns>The key, or <see langword="null"/> when the reference is not ours.</returns>
    internal static string? DefinitionKey(string reference)
    {
        var parsed = ConcordatRef.Create(reference);
        return parsed.IsFailure
            ? null
            : $"{parsed.Value.Subject.Value}__{parsed.Value.Version}";
    }

    private static string? WriteBundled(
        JsonElement element,
        Utf8JsonWriter writer,
        IReadOnlyDictionary<string, string> resolved,
        Dictionary<string, JsonDocument> defs,
        bool isRoot)
    {
        string? missing = null;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();

                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, "$ref", StringComparison.Ordinal) &&
                        property.Value.ValueKind is JsonValueKind.String &&
                        ConcordatRef.IsConcordatRef(property.Value.GetString()))
                    {
                        var reference = property.Value.GetString()!;
                        var key = DefinitionKey(reference);

                        if (key is null || !resolved.ContainsKey(reference))
                        {
                            missing ??= reference;
                            writer.WritePropertyName(property.Name);
                            writer.WriteStringValue(reference);
                            continue;
                        }

                        writer.WriteString("$ref", $"#/$defs/{key}");
                        continue;
                    }

                    writer.WritePropertyName(property.Name);
                    missing ??= WriteBundled(property.Value, writer, resolved, defs, isRoot: false);
                }

                // $defs is written last so the root keeps its own shape first. Nested schemas
                // never carry it: one flat definition block at the root is what makes the
                // pointers stable regardless of where the reference appeared.
                if (isRoot && defs.Count > 0)
                {
                    writer.WritePropertyName("$defs");
                    writer.WriteStartObject();

                    foreach (var (key, document) in defs.OrderBy(d => d.Key, StringComparer.Ordinal))
                    {
                        writer.WritePropertyName(key);
                        missing ??= WriteBundled(
                            document.RootElement, writer, resolved, defs, isRoot: false);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    missing ??= WriteBundled(item, writer, resolved, defs, isRoot: false);
                }

                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }

        return missing;
    }
}
