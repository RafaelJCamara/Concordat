using Concordat.Domain.Registry;
using Concordat.Domain.Results;

namespace Concordat.Formats.Abstractions;

/// <summary>
/// Reduces a schema to the single canonical text that represents its meaning.
/// </summary>
/// <remarks>
/// <para>
/// Canonicalisation is day-one work, not an optimisation (ADR-015). Content-addressed
/// identity is only useful if semantically identical schemas produce identical text: without
/// it a registry accumulates thousands of near-duplicates that differ only in whitespace.
/// Confluent's <c>normalize.schemas</c> still defaults to <see langword="false"/>, which is
/// how that happens in practice.
/// </para>
/// <para>
/// Implementations must be <b>deterministic</b> and <b>idempotent</b>: canonicalising an
/// already-canonical document must return it unchanged. The conformance corpus asserts both.
/// </para>
/// </remarks>
public interface ISchemaCanonicalizer
{
    /// <summary>The format this canonicaliser handles.</summary>
    SchemaFormat Format { get; }

    /// <summary>Reduces a schema document to its canonical text.</summary>
    /// <param name="body">The schema as written by its author.</param>
    /// <returns>
    /// The canonical text, or a failure carrying <see cref="ConcordatCodes.SchemaMalformed"/>
    /// when the document is not well-formed for this format.
    /// </returns>
    Result<string> Canonicalize(string? body);
}
