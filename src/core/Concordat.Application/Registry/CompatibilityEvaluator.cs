using Concordat.Domain.Registry;
using Concordat.Domain.Results;
using Concordat.Formats.Abstractions;

namespace Concordat.Application.Registry;

/// <summary>Resolves the per-format services.</summary>
public interface ISchemaFormatRegistry
{
    /// <summary>Gets the canonicaliser for a format.</summary>
    /// <param name="format">The format.</param>
    /// <returns>The canonicaliser.</returns>
    /// <exception cref="NotSupportedException">The format has no implementation yet.</exception>
    ISchemaCanonicalizer Canonicalizer(SchemaFormat format);

    /// <summary>Gets the compatibility checker for a format.</summary>
    /// <param name="format">The format.</param>
    /// <returns>The checker.</returns>
    /// <exception cref="NotSupportedException">The format has no implementation yet.</exception>
    ICompatibilityChecker Checker(SchemaFormat format);

    /// <summary>Gets the reference extractor for a format.</summary>
    /// <param name="format">The format.</param>
    /// <returns>The extractor.</returns>
    /// <exception cref="NotSupportedException">The format has no implementation yet.</exception>
    ISchemaReferenceExtractor ReferenceExtractor(SchemaFormat format);

    /// <summary>Gets the portability checker for a format, if it has one.</summary>
    /// <param name="format">The format.</param>
    /// <returns>
    /// The checker, or <see langword="null"/> when the format has none. Unlike the other
    /// members this does not throw: a format with no checker has no cross-implementation
    /// divergence modelled, which is a statement about the format rather than a gap. JSON
    /// Schema is the one with five independent validators interpreting the same text.
    /// </returns>
    ISchemaPortabilityChecker? PortabilityChecker(SchemaFormat format);
}

/// <summary>A canonicalised proposal, ready to register or to check.</summary>
/// <param name="Schema">The schema, with its content-addressed id and derived references.</param>
/// <param name="Report">The engine's verdict and findings.</param>
/// <param name="Verdict">The verdict in the form the aggregate accepts.</param>
/// <param name="Portability">
/// Where this schema relies on behaviour that differs between SDKs (M6.1). Warnings only —
/// an error-severity finding refuses the proposal before one of these is built.
/// </param>
public sealed record EvaluatedProposal(
    Schema Schema,
    CompatibilityReport Report,
    CompatibilityVerdict Verdict,
    IReadOnlyList<PortabilityFinding> Portability);

/// <summary>
/// Canonicalises a proposed schema, derives its identity and references, and asks the engine
/// whether it may follow the versions already registered.
/// </summary>
/// <remarks>
/// <para>
/// <b>The only place in the system that constructs a <see cref="CompatibilityVerdict"/>.</b>
/// The aggregate trusts the verdict it is handed — it can reject one computed under the wrong
/// policy, but a caller that simply asserts <c>Compatible</c> for a breaking change would
/// still move the pointer. Funnelling construction through one type, which cannot produce a
/// verdict without calling the checker, is what closes that hole.
/// </para>
/// <para>
/// <c>EvaluatorTests.Evaluate_AlwaysConsultsTheChecker</c> holds the line with a recording
/// fake: it fails if any path returns a verdict the engine did not produce.
/// </para>
/// </remarks>
public interface ICompatibilityEvaluator
{
    /// <summary>Canonicalises and evaluates a proposed schema.</summary>
    /// <param name="format">The schema format.</param>
    /// <param name="body">The schema as authored.</param>
    /// <param name="priorVersions">Previously registered versions, newest first or in any order.</param>
    /// <param name="policy">The resolved policy in force for the subject.</param>
    /// <param name="contentModel">The subject's content model.</param>
    /// <returns>The evaluated proposal, or the first failure from canonicalisation.</returns>
    Result<EvaluatedProposal> Evaluate(
        SchemaFormat format,
        string? body,
        IReadOnlyList<PriorSchema> priorVersions,
        CompatibilityPolicy policy,
        ContentModel contentModel);
}

/// <inheritdoc />
public sealed class CompatibilityEvaluator(ISchemaFormatRegistry formats) : ICompatibilityEvaluator
{
    /// <inheritdoc />
    public Result<EvaluatedProposal> Evaluate(
        SchemaFormat format,
        string? body,
        IReadOnlyList<PriorSchema> priorVersions,
        CompatibilityPolicy policy,
        ContentModel contentModel)
    {
        ArgumentNullException.ThrowIfNull(priorVersions);

        var canonical = formats.Canonicalizer(format).Canonicalize(body);
        if (canonical.IsFailure)
        {
            return Result<EvaluatedProposal>.Failure(canonical.Error!);
        }

        // Portability runs before anything is derived from the document. An unsupported dialect
        // means the rules this schema was written against are not the rules that would be
        // applied to it, so computing an id and a verdict from it would be confidently wrong.
        var portability = formats.PortabilityChecker(format)?.Check(canonical.Value) ?? [];

        var dialect = portability.FirstOrDefault(
            f => f.Severity is PortabilitySeverity.Error);

        if (dialect is not null)
        {
            return Result<EvaluatedProposal>.Failure(
                ConcordatCodes.SchemaDialectUnsupported, dialect.Message);
        }

        // Edges come from the document, never from the caller (M1.4).
        var references = formats.ReferenceExtractor(format).Extract(canonical.Value);
        if (references.IsFailure)
        {
            return Result<EvaluatedProposal>.Failure(references.Error!);
        }

        var id = SchemaIdComputer.Compute(format, canonical.Value, references.Value);

        var schema = Schema.Create(id, format, canonical.Value, references.Value);
        if (schema.IsFailure)
        {
            return Result<EvaluatedProposal>.Failure(schema.Error!);
        }

        // Unconditional: there is no branch that skips the checker and fabricates a verdict.
        var report = formats.Checker(format).Check(canonical.Value, priorVersions, policy, contentModel);

        var verdict = report.IsCompatible
            ? CompatibilityVerdict.Compatible(policy)
            : CompatibilityVerdict.Breaking(policy);

        return Result<EvaluatedProposal>.Success(
            new EvaluatedProposal(schema.Value, report, verdict, portability));
    }
}
