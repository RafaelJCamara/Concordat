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
}

/// <summary>A canonicalised proposal, ready to register or to check.</summary>
/// <param name="Schema">The schema, with its content-addressed id and derived references.</param>
/// <param name="Report">The engine's verdict and findings.</param>
/// <param name="Verdict">The verdict in the form the aggregate accepts.</param>
public sealed record EvaluatedProposal(
    Schema Schema,
    CompatibilityReport Report,
    CompatibilityVerdict Verdict);

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

        return Result<EvaluatedProposal>.Success(new EvaluatedProposal(schema.Value, report, verdict));
    }
}
