using Concordat.Domain.Registry;
using Concordat.Domain.Results;
using Concordat.Formats.Abstractions;

namespace Concordat.Formats.Protobuf;

/// <summary>
/// Refuses Protobuf schemas that import another subject's file (ADR-023).
/// </summary>
/// <remarks>
/// <para>
/// <b>This extractor never returns an edge.</b> An <c>import</c> names a file, not a version,
/// so resolving one would bind to whatever that subject currently holds — the
/// silent-behaviour-change ADR-017 exists to prevent. v1 refuses instead, and names the import
/// it will not follow.
/// </para>
/// <para>
/// <b><c>google/protobuf/*</c> imports are allowed, and are not an exception to the rule.</b>
/// The well-known types ship with every Protobuf runtime and are resolved by the compiler, not
/// by a registry — an import of <c>timestamp.proto</c> is a dependency on the language, the
/// same shape as a JSON Schema <c>$ref</c> to a local fragment. Refusing them would rule out
/// most real Protobuf schemas, since <c>google.protobuf.Timestamp</c> is close to universal,
/// and would make the format's support decorative for no correctness gain.
/// </para>
/// </remarks>
public sealed class ProtoSchemaReferenceExtractor : ISchemaReferenceExtractor
{
    /// <summary>
    /// The import prefix that resolves from the Protobuf runtime rather than from a registry.
    /// </summary>
    private const string WellKnownPrefix = "google/protobuf/";

    /// <inheritdoc />
    public SchemaFormat Format => SchemaFormat.Protobuf;

    /// <inheritdoc />
    public Result<IReadOnlyList<Reference>> Extract(string canonicalBody)
    {
        if (string.IsNullOrWhiteSpace(canonicalBody))
        {
            return Result<IReadOnlyList<Reference>>.Failure(
                ConcordatCodes.SchemaBodyEmpty, "A schema body is required.");
        }

        ProtoFile file;
        try
        {
            file = ProtoParser.Parse(canonicalBody);
        }
        catch (MalformedProtoSchemaException ex)
        {
            return Result<IReadOnlyList<Reference>>.Failure(
                ConcordatCodes.SchemaMalformed, ex.Message);
        }

        var external = file.Imports
            .Where(i => !i.StartsWith(WellKnownPrefix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (external.Count > 0)
        {
            var names = string.Join(", ", external.Select(i => $"'{i}'"));

            return Result<IReadOnlyList<Reference>>.Failure(
                ConcordatCodes.SchemaReferencesUnsupported,
                $"This schema imports {names}. Concordat v1 does not resolve Protobuf imports " +
                "across subjects, because an import names a file and carries no version, so " +
                "following it would bind to whatever that subject currently holds (ADR-023). " +
                $"Inline the definitions, or keep only {WellKnownPrefix}* imports, which the " +
                "Protobuf runtime resolves for itself.");
        }

        return Result<IReadOnlyList<Reference>>.Success([]);
    }
}

/// <summary>
/// Returns a Protobuf schema unchanged, because a registered one is always self-contained.
/// </summary>
/// <remarks>
/// Under ADR-023 a registered Protobuf schema imports nothing but the well-known types, which
/// every <c>protoc</c> already has, so the bundle is the document. Implemented rather than left
/// unregistered so the bundled endpoint answers for Protobuf instead of failing.
/// </remarks>
public sealed class ProtoSchemaBundler : ISchemaBundler
{
    /// <inheritdoc />
    public SchemaFormat Format => SchemaFormat.Protobuf;

    /// <inheritdoc />
    public Result<string> Bundle(string canonicalBody, IReadOnlyDictionary<string, string> resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        return string.IsNullOrWhiteSpace(canonicalBody)
            ? Result<string>.Failure(ConcordatCodes.SchemaBodyEmpty, "A schema body is required.")
            : Result<string>.Success(canonicalBody);
    }
}
