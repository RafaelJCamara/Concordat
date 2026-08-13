using Concordat.Domain.Registry;
using Concordat.Domain.Results;

namespace Concordat.Formats.Abstractions;

/// <summary>
/// Reads the outgoing references out of a schema document.
/// </summary>
/// <remarks>
/// The edges are <b>derived from the document</b>, not supplied alongside it. A client-supplied
/// list can disagree with what the schema actually points at, and the disagreement would only
/// surface later as a resolution failure or a missed transitive compatibility check.
/// </remarks>
public interface ISchemaReferenceExtractor
{
    /// <summary>The format this extractor handles.</summary>
    SchemaFormat Format { get; }

    /// <summary>Extracts every Concordat reference in a schema document.</summary>
    /// <param name="canonicalBody">The canonical schema text.</param>
    /// <returns>
    /// The distinct references, ordered by name, or a failure when the document carries a
    /// malformed <c>concordat://</c> reference. Local and HTTP <c>$ref</c>s are ignored, not
    /// reported: they are resolved by the validator, not by the registry.
    /// </returns>
    Result<IReadOnlyList<Reference>> Extract(string canonicalBody);
}
