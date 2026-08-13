using Concordat.Domain.Registry;

namespace Concordat.Formats.Abstractions;

/// <summary>One reason a payload failed validation.</summary>
/// <param name="Path">
/// A JSON Pointer into the <em>document</em>, prefixed with <c>#</c> — not into the schema.
/// This is the opposite of <see cref="BreakingChange.Path"/>, which points into the schema,
/// and the distinction matters to anyone reading both.
/// </param>
/// <param name="Kind">A short, stable token for the rule that failed, for example <c>type</c>.</param>
/// <param name="Message">An actionable explanation.</param>
public sealed record PayloadError(string Path, string Kind, string Message);

/// <summary>The outcome of validating one payload.</summary>
/// <param name="IsValid">Whether the document satisfies the schema.</param>
/// <param name="Errors">Why not, when it does not.</param>
public sealed record PayloadValidationResult(bool IsValid, IReadOnlyList<PayloadError> Errors)
{
    /// <summary>A successful validation.</summary>
    public static PayloadValidationResult Valid { get; } = new(true, []);
}

/// <summary>
/// Validates a message payload against the schema it declares.
/// </summary>
/// <remarks>
/// <para>
/// <b>A port, deliberately.</b> Concordat does not implement JSON Schema validation itself and
/// should not: it is a large, subtle specification with mature implementations. But the choice
/// of implementation is a dependency-posture decision that can change — the obvious candidate
/// turned out to ship its binary under a maintenance-fee agreement that would propagate an
/// obligation to Concordat's own users, which ADR-009 forbids. A port keeps that decision
/// reversible and lets a host substitute a validator its compliance process has already
/// cleared.
/// </para>
/// <para>
/// <b>This is where cross-language divergence lives.</b> Validation is client-side, so each
/// SDK uses a different third-party library — <c>ajv</c>, <c>jsonschema</c>,
/// <c>santhosh-tekuri</c>, <c>networknt</c>, and .NET's. They disagree at the edges, which
/// means the same message can pass in one language and fail in another with no bug on
/// Concordat's part. The payload fixtures in the conformance corpus exist to turn that from a
/// support ticket into a CI failure, and every implementation of this port must pass them.
/// </para>
/// </remarks>
public interface IPayloadValidator
{
    /// <summary>The format this validator handles.</summary>
    SchemaFormat Format { get; }

    /// <summary>Validates a document against a schema.</summary>
    /// <param name="canonicalSchema">The canonical schema text.</param>
    /// <param name="payload">The document, as written on the wire.</param>
    /// <returns>
    /// The outcome. A malformed <paramref name="payload"/> is invalid, not an exception:
    /// this runs on the delivery path, where a throw is a far worse failure than a verdict.
    /// </returns>
    PayloadValidationResult Validate(string canonicalSchema, string payload);
}
