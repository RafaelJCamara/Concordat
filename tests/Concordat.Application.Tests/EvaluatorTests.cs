using Concordat.Application.Registry;
using Concordat.Domain.Registry;
using Concordat.Formats.Abstractions;
using Concordat.Formats.Json;

namespace Concordat.Application.Tests;

/// <summary>
/// Discharges the commitment M1.1 recorded: the aggregate trusts the verdict it is handed, so
/// exactly one type may construct one and it must source it from the engine.
/// </summary>
public class EvaluatorTests
{
    /// <summary>Records whether it was consulted, and answers however the test wants.</summary>
    private sealed class RecordingChecker(bool breaking) : ICompatibilityChecker
    {
        public int Calls { get; private set; }

        public SchemaFormat Format => SchemaFormat.Json;

        public CompatibilityReport Check(
            string proposedCanonicalBody,
            IReadOnlyList<PriorSchema> priors,
            CompatibilityPolicy policy,
            ContentModel contentModel)
        {
            Calls++;

            var change = new BreakingChange(
                "#/required",
                BreakingChangeKinds.RequiredFieldAdded,
                CompatibilityDirection.Backward,
                CompatibilitySurface.WireJson,
                "recorded",
                1);

            return breaking
                ? new CompatibilityReport(false, [change], [change], SemverBump.Major)
                : new CompatibilityReport(true, [], [], SemverBump.Patch);
        }
    }

    private sealed class Registry(ICompatibilityChecker checker) : ISchemaFormatRegistry
    {
        public ISchemaCanonicalizer Canonicalizer(SchemaFormat format) => new JsonSchemaCanonicalizer();

        public ICompatibilityChecker Checker(SchemaFormat format) => checker;

        public ISchemaReferenceExtractor ReferenceExtractor(SchemaFormat format) =>
            new JsonSchemaReferenceExtractor();
    }

    private static IReadOnlyList<PriorSchema> OnePrior() =>
        [new PriorSchema(1, """{"type":"object"}""")];

    [Fact]
    public void Evaluate_AlwaysConsultsTheChecker()
    {
        // The load-bearing assertion. If a future refactor adds a fast path that fabricates a
        // Compatible verdict — for an unchanged body, say — this fails.
        var checker = new RecordingChecker(breaking: false);
        var evaluator = new CompatibilityEvaluator(new Registry(checker));

        var result = evaluator.Evaluate(
            SchemaFormat.Json,
            """{"type":"object"}""",
            OnePrior(),
            CompatibilityPolicy.Default,
            ContentModel.Open);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(1, checker.Calls);
    }

    [Fact]
    public void Evaluate_ReportsBreakingExactlyWhenTheEngineDoes()
    {
        var evaluator = new CompatibilityEvaluator(new Registry(new RecordingChecker(breaking: true)));

        var result = evaluator.Evaluate(
            SchemaFormat.Json,
            """{"type":"object"}""",
            OnePrior(),
            CompatibilityPolicy.Default,
            ContentModel.Open);

        Assert.True(result.Value.Verdict.IsBreaking);
        Assert.Equal(CompatibilityPolicy.Default, result.Value.Verdict.EvaluatedUnder);
    }

    [Fact]
    public void Evaluate_CarriesThePolicyItWasAskedToUse()
    {
        // The aggregate rejects a verdict evaluated under a policy that is not the subject's,
        // so the evaluator must stamp the one it actually used.
        var policy = new CompatibilityPolicy(CompatibilityMode.Full, CompatibilitySurface.Source);
        var evaluator = new CompatibilityEvaluator(new Registry(new RecordingChecker(breaking: false)));

        var result = evaluator.Evaluate(
            SchemaFormat.Json, """{"type":"object"}""", OnePrior(), policy, ContentModel.Open);

        Assert.Equal(policy, result.Value.Verdict.EvaluatedUnder);
    }

    [Fact]
    public void Evaluate_DerivesTheIdAndReferencesFromTheDocument()
    {
        var evaluator = new CompatibilityEvaluator(new Registry(new RecordingChecker(breaking: false)));

        var result = evaluator.Evaluate(
            SchemaFormat.Json,
            """{"properties":{"a":{"$ref":"concordat://prod/acme.Address/2"}}}""",
            OnePrior(),
            CompatibilityPolicy.Default,
            ContentModel.Open);

        var reference = Assert.Single(result.Value.Schema.References);
        Assert.Equal("acme.Address", reference.Subject.Value);
        Assert.Equal(2, reference.Version);

        // The id covers the references, so it must differ from the same body with none.
        var withoutRefs = evaluator.Evaluate(
            SchemaFormat.Json, """{"properties":{"a":{}}}""",
            OnePrior(), CompatibilityPolicy.Default, ContentModel.Open);

        Assert.NotEqual(withoutRefs.Value.Schema.Id, result.Value.Schema.Id);
    }

    [Fact]
    public void Evaluate_FailsBeforeConsultingTheCheckerWhenTheBodyIsMalformed()
    {
        var checker = new RecordingChecker(breaking: false);
        var evaluator = new CompatibilityEvaluator(new Registry(checker));

        var result = evaluator.Evaluate(
            SchemaFormat.Json, "not json", OnePrior(), CompatibilityPolicy.Default, ContentModel.Open);

        Assert.True(result.IsFailure);
        Assert.Equal(0, checker.Calls);
    }

    [Fact]
    public void Evaluate_RejectsAMalformedConcordatReference()
    {
        var evaluator = new CompatibilityEvaluator(new Registry(new RecordingChecker(breaking: false)));

        var result = evaluator.Evaluate(
            SchemaFormat.Json,
            """{"properties":{"a":{"$ref":"concordat://prod/acme.Address"}}}""",
            OnePrior(),
            CompatibilityPolicy.Default,
            ContentModel.Open);

        Assert.True(result.IsFailure);
    }
}
