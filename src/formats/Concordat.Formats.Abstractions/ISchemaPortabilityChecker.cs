using Concordat.Domain.Registry;

namespace Concordat.Formats.Abstractions;

/// <summary>How much a portability finding costs.</summary>
public enum PortabilitySeverity
{
    /// <summary>
    /// The schema is refused. Reserved for cases where Concordat cannot honestly claim to
    /// understand the document at all — a dialect it does not implement.
    /// </summary>
    Error = 1,

    /// <summary>
    /// The schema registers, and something about it will not behave identically everywhere.
    /// A warning, not a refusal: the schema is legal and the author may well have meant it.
    /// </summary>
    Warning = 2,
}

/// <summary>The stable <c>kind</c> tokens on a portability finding.</summary>
/// <remarks>
/// Normative protocol (ADR-019). They appear in registration responses and in
/// <c>concordat lint --json</c>, so a pipeline may branch on them.
/// </remarks>
public static class PortabilityKinds
{
    /// <summary>The document declares a JSON Schema dialect Concordat does not implement.</summary>
    public const string DialectUnsupported = "dialect_unsupported";

    /// <summary>
    /// A keyword outside the interoperable subset: legal, validated, but <b>not compared</b> by
    /// the compatibility engine, so a change confined to it reads as compatible.
    /// </summary>
    public const string KeywordNotCompared = "keyword_not_compared";

    /// <summary>
    /// A regular expression using a construct that is not portable across the SDK validators —
    /// most importantly Go's RE2, which has no lookaround or backreferences at all.
    /// </summary>
    public const string RegexNotPortable = "regex_not_portable";
}

/// <summary>One reason a schema may not behave identically in every SDK.</summary>
/// <param name="Path">
/// A JSON Pointer into the <em>schema</em> document, prefixed with <c>#</c>, matching
/// <see cref="BreakingChange.Path"/>'s convention.
/// </param>
/// <param name="Kind">A token from <see cref="PortabilityKinds"/>.</param>
/// <param name="Severity">Whether this refuses the schema or merely warns.</param>
/// <param name="Message">
/// What will differ and what it costs. Never just the rule — a warning nobody can act on gets
/// suppressed, and then the real ones go with it.
/// </param>
public sealed record PortabilityFinding(
    string Path,
    string Kind,
    PortabilitySeverity Severity,
    string Message);

/// <summary>
/// Reports where a schema relies on behaviour that differs between Concordat's SDKs.
/// </summary>
/// <remarks>
/// <para>
/// <b>M6.1's instrument, and the reason it exists is a specific failure.</b> ADR-021 commits to
/// five independent implementations — <c>ajv</c>, <c>jsonschema</c>,
/// <c>santhosh-tekuri</c>, <c>networknt</c> and .NET's — and they disagree at the edges of
/// JSON Schema with no bug on anyone's part. A message that passes in Node and is quarantined
/// in Go is indistinguishable from a Concordat defect to whoever gets paged.
/// </para>
/// <para>
/// The conformance corpus turns the disagreements it knows about into CI failures. This port
/// covers what a corpus cannot: telling an author, at the moment they register, that the
/// schema they just wrote sits in the region where implementations diverge.
/// </para>
/// <para>
/// <b>Warnings, almost entirely.</b> The schemas this flags are legal and usually intentional.
/// Refusing them would be a worse product than warning about them — the mistake Confluent's
/// JSON Schema compatibility defaults make in the other direction. Only an unimplemented
/// dialect is an error, because there Concordat genuinely does not know what the document
/// means.
/// </para>
/// <para>
/// <b>Not every format needs one.</b> Avro and Protobuf are specified formats with reference
/// implementations and normative resolution rules; JSON Schema is the one with no compatibility
/// specification and five libraries interpreting the same text. A format with no registered
/// checker reports no findings, which is a statement about the format rather than a gap.
/// </para>
/// </remarks>
public interface ISchemaPortabilityChecker
{
    /// <summary>The format this checker handles.</summary>
    SchemaFormat Format { get; }

    /// <summary>Inspects a schema for cross-implementation divergence.</summary>
    /// <param name="canonicalBody">The canonical schema text.</param>
    /// <returns>
    /// Every finding, ordered by path. Empty when the schema stays inside the interoperable
    /// subset.
    /// </returns>
    IReadOnlyList<PortabilityFinding> Check(string canonicalBody);
}
