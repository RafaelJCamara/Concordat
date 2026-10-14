using Concordat.Domain.Registry;

namespace Concordat.Formats.Abstractions;

/// <summary>The stable <c>kind</c> tokens on a schema defect.</summary>
public static class SchemaDefectKinds
{
    /// <summary>
    /// A value the schema's own dialect's metaschema does not allow: <c>{"type":"bogus"}</c>,
    /// <c>{"minimum":"5"}</c>, <c>{"required":true}</c>, a tuple-form <c>items</c>.
    /// </summary>
    public const string MetaschemaViolation = "metaschema_violation";

    /// <summary>
    /// A <c>$ref</c> into this same document (<c>#/$defs/Missing</c>) whose target is not there.
    /// </summary>
    public const string ReferenceUnresolved = "reference_unresolved";
}

/// <summary>One reason a document is not a schema of its declared format.</summary>
/// <param name="Path">
/// A JSON Pointer into the <em>schema</em> document, prefixed with <c>#</c> (the convention
/// <see cref="PortabilityFinding.Path"/> and <see cref="BreakingChange.Path"/> share).
/// </param>
/// <param name="Kind">A token from <see cref="SchemaDefectKinds"/>.</param>
/// <param name="Message">What is wrong and what the rule is, in a sentence an author can act on.</param>
public sealed record SchemaDefect(string Path, string Kind, string Message);

/// <summary>
/// Reports where a document that is well-formed JSON is still not a valid schema (ADR-039).
/// </summary>
/// <remarks>
/// <para>
/// <b>Not the canonicaliser's question and not the portability checker's.</b> Canonicalisation
/// asks whether the text is JSON with a single meaning; portability asks whether a valid schema
/// leaves the subset every SDK interprets alike. This asks whether the document is a schema at
/// all, which is what Avro's and Protobuf's canonicalisers already answer for their formats by
/// parsing them. JSON Schema has no parser to fail, so the answer needs its own step.
/// </para>
/// <para>
/// <b>It reports; it does not refuse.</b> Whether a defect stops a registration depends on
/// something this cannot see: whether the registry already holds the document, which it must go
/// on accepting (ADR-039). The decision is the application's, so a format's checker stays a pure
/// function of text and the same one serves a linter that never touches a registry.
/// </para>
/// <para>
/// <b>Never fetches.</b> The document under test is data to the checker, not a schema it
/// compiles, so a <c>$ref</c> or <c>$id</c> in it is a string to look at and never an address to
/// visit.
/// </para>
/// </remarks>
public interface ISchemaValidityChecker
{
    /// <summary>The format this checker handles.</summary>
    SchemaFormat Format { get; }

    /// <summary>Inspects a canonical schema for defects.</summary>
    /// <param name="canonicalBody">The canonical schema text.</param>
    /// <returns>Every defect, ordered by path. Empty when the document is a valid schema.</returns>
    IReadOnlyList<SchemaDefect> Check(string canonicalBody);
}
