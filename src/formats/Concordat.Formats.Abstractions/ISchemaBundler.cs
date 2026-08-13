using Concordat.Domain.Registry;
using Concordat.Domain.Results;

namespace Concordat.Formats.Abstractions;

/// <summary>
/// Inlines referenced schemas into one self-contained document.
/// </summary>
/// <remarks>
/// <para>
/// <b>A serving concern, never part of the stored canonical form.</b> Bundling at
/// registration would make canonicalisation depend on registry state: the same authored
/// document would canonicalise differently depending on which schemas happened to be
/// registered, and no SDK could reproduce a schema id offline. ADR-019 and the M1.7
/// conformance corpus both rule that out.
/// </para>
/// <para>
/// So the stored body keeps its <c>concordat://</c> references and the edges stay
/// authoritative; a bundle is produced on demand for clients that want to validate without
/// fetching N more documents.
/// </para>
/// </remarks>
public interface ISchemaBundler
{
    /// <summary>The format this bundler handles.</summary>
    SchemaFormat Format { get; }

    /// <summary>Produces a self-contained document.</summary>
    /// <param name="canonicalBody">The schema to bundle.</param>
    /// <param name="resolved">
    /// Every reachable reference, keyed by the exact <c>$ref</c> text, mapped to that target's
    /// canonical body. Must be transitively closed: a reference inside a referenced schema
    /// has to be present too, or the bundle is not self-contained.
    /// </param>
    /// <returns>The bundled document, or a failure when a reference is missing.</returns>
    Result<string> Bundle(string canonicalBody, IReadOnlyDictionary<string, string> resolved);
}
