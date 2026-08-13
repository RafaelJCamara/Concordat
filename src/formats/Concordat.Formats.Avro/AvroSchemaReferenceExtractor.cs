using Concordat.Domain.Registry;
using Concordat.Domain.Results;
using Concordat.Formats.Abstractions;

namespace Concordat.Formats.Avro;

/// <summary>
/// Refuses Avro schemas that depend on a type they do not define (ADR-023).
/// </summary>
/// <remarks>
/// <para>
/// <b>This extractor never returns an edge.</b> A cross-subject Avro reference is a bare
/// named-type fullname — in the common case a plain JSON string inside a union or a field's
/// <c>"type"</c> — with nowhere to pin a version. Resolving one would mean binding to whatever
/// that subject happens to be at read time, which is the silent-behaviour-change ADR-017's
/// gated <c>latest</c> pointer exists to prevent, one layer down in the reference graph.
/// </para>
/// <para>
/// So v1 refuses rather than guesses, and says which type it could not find. A self-contained
/// schema — everything the document needs defined inside it — registers normally, and that is
/// the common shape for Avro.
/// </para>
/// <para>
/// <b>Self-reference is not an external reference.</b> A record naming itself, or a sibling
/// defined elsewhere in the same document, resolves locally and is allowed; that is what makes
/// linked-list and tree schemas work.
/// </para>
/// </remarks>
public sealed class AvroSchemaReferenceExtractor : ISchemaReferenceExtractor
{
    /// <inheritdoc />
    public SchemaFormat Format => SchemaFormat.Avro;

    /// <inheritdoc />
    public Result<IReadOnlyList<Reference>> Extract(string canonicalBody)
    {
        if (string.IsNullOrWhiteSpace(canonicalBody))
        {
            return Result<IReadOnlyList<Reference>>.Failure(
                ConcordatCodes.SchemaBodyEmpty, "A schema body is required.");
        }

        AvroDocument document;
        try
        {
            document = AvroSchemaParser.Parse(canonicalBody);
        }
        catch (MalformedAvroSchemaException ex)
        {
            return Result<IReadOnlyList<Reference>>.Failure(
                ConcordatCodes.SchemaMalformed, ex.Message);
        }

        var unresolved = new SortedSet<string>(StringComparer.Ordinal);
        Collect(document.Root, document, unresolved, []);

        if (unresolved.Count > 0)
        {
            var names = string.Join(", ", unresolved.Select(n => $"'{n}'"));

            return Result<IReadOnlyList<Reference>>.Failure(
                ConcordatCodes.SchemaReferencesUnsupported,
                $"This schema references {names}, which it does not define. Concordat v1 does " +
                "not resolve Avro references across subjects, because an Avro fullname carries " +
                "no version and binding it to whatever is current would change behaviour with " +
                "no deploy (ADR-023). Inline the definition to register this schema.");
        }

        return Result<IReadOnlyList<Reference>>.Success([]);
    }

    private static void Collect(
        AvroType type,
        AvroDocument document,
        SortedSet<string> unresolved,
        HashSet<string> visited)
    {
        switch (type)
        {
            case AvroNamedRef reference:
                if (!document.Named.ContainsKey(reference.FullName))
                {
                    unresolved.Add(reference.FullName);
                }

                return;

            case AvroUnion union:
                foreach (var branch in union.Branches)
                {
                    Collect(branch, document, unresolved, visited);
                }

                return;

            case AvroArray array:
                Collect(array.Items, document, unresolved, visited);
                return;

            case AvroMap map:
                Collect(map.Values, document, unresolved, visited);
                return;

            case AvroRecord record:
                // Recursion is legal and expected here, so the walk is bounded by fullname.
                if (!visited.Add(record.FullName))
                {
                    return;
                }

                foreach (var field in record.Fields)
                {
                    Collect(field.Type, document, unresolved, visited);
                }

                return;

            default:
                return;
        }
    }
}

/// <summary>
/// Returns an Avro schema unchanged, because a registered one is always self-contained.
/// </summary>
/// <remarks>
/// Bundling exists to inline referenced schemas so a client can validate without fetching more
/// documents. Under ADR-023 no registered Avro schema has references, so the bundle is the
/// document. Implemented rather than left unregistered so the bundled endpoint answers for Avro
/// instead of failing — and it stops being the identity the moment references are supported.
/// </remarks>
public sealed class AvroSchemaBundler : ISchemaBundler
{
    /// <inheritdoc />
    public SchemaFormat Format => SchemaFormat.Avro;

    /// <inheritdoc />
    public Result<string> Bundle(string canonicalBody, IReadOnlyDictionary<string, string> resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        return string.IsNullOrWhiteSpace(canonicalBody)
            ? Result<string>.Failure(ConcordatCodes.SchemaBodyEmpty, "A schema body is required.")
            : Result<string>.Success(canonicalBody);
    }
}
