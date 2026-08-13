using System.Text.Json;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;
using Concordat.Formats.Abstractions;

namespace Concordat.Formats.Json;

/// <summary>
/// Reads <c>concordat://</c> references out of a JSON Schema document.
/// </summary>
public sealed class JsonSchemaReferenceExtractor : ISchemaReferenceExtractor
{
    /// <inheritdoc />
    public SchemaFormat Format => SchemaFormat.Json;

    /// <inheritdoc />
    public Result<IReadOnlyList<Reference>> Extract(string canonicalBody)
    {
        if (string.IsNullOrWhiteSpace(canonicalBody))
        {
            return Result<IReadOnlyList<Reference>>.Failure(
                ConcordatCodes.SchemaBodyEmpty, "A schema body is required.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(canonicalBody);
        }
        catch (JsonException ex)
        {
            return Result<IReadOnlyList<Reference>>.Failure(
                ConcordatCodes.SchemaMalformed, $"Not well-formed JSON: {ex.Message}");
        }

        using (document)
        {
            // Keyed by the raw $ref text, which is also Reference.Name. The same target
            // referenced from two places is one edge, not two - and Schema.Create would reject
            // the duplicate name anyway.
            var byName = new SortedDictionary<string, Reference>(StringComparer.Ordinal);

            var failure = Collect(document.RootElement, byName);
            if (failure is not null)
            {
                return Result<IReadOnlyList<Reference>>.Failure(failure);
            }

            return Result<IReadOnlyList<Reference>>.Success([.. byName.Values]);
        }
    }

    private static DomainError? Collect(
        JsonElement element,
        SortedDictionary<string, Reference> found)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, "$ref", StringComparison.Ordinal) &&
                        property.Value.ValueKind is JsonValueKind.String)
                    {
                        var error = TryAdd(property.Value.GetString(), found);
                        if (error is not null)
                        {
                            return error;
                        }

                        continue;
                    }

                    var nested = Collect(property.Value, found);
                    if (nested is not null)
                    {
                        return nested;
                    }
                }

                return null;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var nested = Collect(item, found);
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

    private static DomainError? TryAdd(
        string? value,
        SortedDictionary<string, Reference> found)
    {
        // Local refs (#/$defs/Address) and HTTP refs are the validator's business, not the
        // registry's. Only claim the ones addressed to us.
        if (!ConcordatRef.IsConcordatRef(value))
        {
            return null;
        }

        var parsed = ConcordatRef.Create(value);
        if (parsed.IsFailure)
        {
            // Malformed but clearly ours: report rather than silently skip, or a typo in the
            // scheme becomes a schema that registers with no edges and fails to resolve later.
            return parsed.Error;
        }

        var name = parsed.Value.ToString();
        if (found.ContainsKey(name))
        {
            return null;
        }

        var reference = Reference.Create(name, parsed.Value.Subject, parsed.Value.Version);
        if (reference.IsFailure)
        {
            return reference.Error;
        }

        found[name] = reference.Value;
        return null;
    }
}
