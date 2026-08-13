using Concordat.Domain.Registry;
using Concordat.Formats.Abstractions;
using Concordat.Formats.Json;

namespace Concordat.Formats.Json.Tests;

public abstract class CompatibilityTestBase
{
    private static readonly JsonSchemaCanonicalizer Canonicalizer = new();
    private protected static readonly JsonSchemaCompatibilityChecker Checker = new();

    private protected static CompatibilityPolicy Policy(
        CompatibilityMode mode = CompatibilityMode.Backward,
        CompatibilitySurface surface = CompatibilitySurface.WireJson) => new(mode, surface);

    private protected static CompatibilityReport Check(
        string previous,
        string proposed,
        CompatibilityMode mode = CompatibilityMode.Backward,
        CompatibilitySurface surface = CompatibilitySurface.WireJson,
        ContentModel contentModel = ContentModel.Open) =>
        Checker.Check(
            Canonicalizer.Canonicalize(proposed).Value,
            [new PriorSchema(1, Canonicalizer.Canonicalize(previous).Value)],
            new CompatibilityPolicy(mode, surface),
            contentModel);
}

/// <summary>
/// The acceptance criteria from DESIGN §7. These are the cases Confluent gets wrong, and the
/// reason the engine was designed rather than ported.
/// </summary>
public class AcceptanceCriteriaTests : CompatibilityTestBase
{
    private const string Base = """
        {"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}
        """;

    [Theory]
    [InlineData(CompatibilityMode.Backward)]
    [InlineData(CompatibilityMode.Forward)]
    [InlineData(CompatibilityMode.Full)]
    public void AddingAnOptionalProperty_IsFullyCompatible(CompatibilityMode mode)
    {
        // THE criterion. The single most common schema change; if it is blocked the product is
        // unusable, and it is exactly what Confluent's defaults reject.
        const string proposed = """
            {"type":"object","properties":{"id":{"type":"string"},"note":{"type":"string"}},"required":["id"]}
            """;

        var report = Check(Base, proposed, mode);

        Assert.True(report.IsCompatible, Describe(report));
        Assert.Empty(report.BreakingChanges);
    }

    [Theory]
    [InlineData(CompatibilityMode.Backward)]
    [InlineData(CompatibilityMode.Forward)]
    [InlineData(CompatibilityMode.Full)]
    public void RemovingAnOptionalProperty_IsFullyCompatible(CompatibilityMode mode)
    {
        const string previous = """
            {"type":"object","properties":{"id":{"type":"string"},"note":{"type":"string"}},"required":["id"]}
            """;

        var report = Check(previous, Base, mode);

        Assert.True(report.IsCompatible, Describe(report));
    }

    [Fact]
    public void AddingToRequired_IsBackwardBreaking()
    {
        const string proposed = """
            {"type":"object","properties":{"id":{"type":"string"},"note":{"type":"string"}},"required":["id","note"]}
            """;

        var report = Check(Base, proposed);

        Assert.False(report.IsCompatible);
        var change = Assert.Single(report.BreakingChanges);
        Assert.Equal(BreakingChangeKinds.RequiredFieldAdded, change.Kind);
        Assert.Equal("#/required", change.Path);
        Assert.Equal(1, change.ConflictsWithVersion);
        Assert.Contains("'note'", change.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RemovingFromRequired_IsForwardBreakingNotBackward()
    {
        // Under DESIGN §7's definitions: data written under the old schema always carries the
        // field, so a new reader is fine. It is readers on the OLD schema that break.
        const string proposed = """
            {"type":"object","properties":{"id":{"type":"string"}}}
            """;

        Assert.True(Check(Base, proposed, CompatibilityMode.Backward).IsCompatible);

        var forward = Check(Base, proposed, CompatibilityMode.Forward);
        Assert.False(forward.IsCompatible);
        Assert.Equal(
            BreakingChangeKinds.RequiredFieldRemoved,
            Assert.Single(forward.BreakingChanges).Kind);
    }

    [Fact]
    public void NarrowingAType_IsBackwardBreaking()
    {
        const string previous = """{"type":"object","properties":{"id":{"type":["string","null"]}}}""";
        const string proposed = """{"type":"object","properties":{"id":{"type":"string"}}}""";

        var report = Check(previous, proposed);

        Assert.False(report.IsCompatible);
        var change = Assert.Single(report.BreakingChanges);
        Assert.Equal(BreakingChangeKinds.TypeNarrowed, change.Kind);
        Assert.Equal("#/properties/id", change.Path);
    }

    [Fact]
    public void WideningAType_IsForwardBreaking()
    {
        const string previous = """{"type":"object","properties":{"id":{"type":"string"}}}""";
        const string proposed = """{"type":"object","properties":{"id":{"type":["string","null"]}}}""";

        Assert.True(Check(previous, proposed, CompatibilityMode.Backward).IsCompatible);
        Assert.False(Check(previous, proposed, CompatibilityMode.Forward).IsCompatible);
    }

    [Fact]
    public void NarrowingAnEnum_IsBackwardBreaking()
    {
        const string previous = """{"type":"object","properties":{"s":{"enum":["a","b","c"]}}}""";
        const string proposed = """{"type":"object","properties":{"s":{"enum":["a","b"]}}}""";

        var report = Check(previous, proposed);

        Assert.False(report.IsCompatible);
        Assert.Equal(
            BreakingChangeKinds.EnumValueRemoved,
            Assert.Single(report.BreakingChanges).Kind);
    }

    [Fact]
    public void NarrowingMaximum_IsBackwardBreaking()
    {
        const string previous = """{"type":"object","properties":{"n":{"maximum":100}}}""";
        const string proposed = """{"type":"object","properties":{"n":{"maximum":50}}}""";

        var report = Check(previous, proposed);

        Assert.False(report.IsCompatible);
        var change = Assert.Single(report.BreakingChanges);
        Assert.Equal(BreakingChangeKinds.NumericRangeNarrowed, change.Kind);
        Assert.Equal("#/properties/n/maximum", change.Path);
    }

    [Fact]
    public void ClosingAdditionalProperties_IsBackwardBreaking()
    {
        const string previous = """{"type":"object","additionalProperties":true}""";
        const string proposed = """{"type":"object","additionalProperties":false}""";

        var report = Check(previous, proposed);

        Assert.False(report.IsCompatible);
        Assert.Equal(
            BreakingChangeKinds.AdditionalPropertiesClosed,
            Assert.Single(report.BreakingChanges).Kind);
    }

    [Fact]
    public void AnIdenticalSchema_IsCompatibleWithNoDivergences()
    {
        var report = Check(Base, Base);

        Assert.True(report.IsCompatible);
        Assert.Empty(report.AllDivergences);
        Assert.Equal(SemverBump.Patch, report.SuggestedBump);
    }

    private protected static string Describe(CompatibilityReport report) =>
        string.Join("; ", report.BreakingChanges.Select(c => $"{c.Kind} at {c.Path}"));
}

/// <summary>
/// The second axis. A single-axis registry cannot express any of this.
/// </summary>
public class SurfaceAxisTests : CompatibilityTestBase
{
    private const string IntegerSchema = """{"type":"object","properties":{"n":{"type":"integer"}}}""";
    private const string NumberSchema = """{"type":"object","properties":{"n":{"type":"number"}}}""";

    [Fact]
    public void IntegerWidenedToNumber_PassesWireJsonAndFailsSource()
    {
        // JSON Schema's int32 -> int64: every existing document still validates, but generated
        // code changes from an integral type to a floating-point one. ADR-016 exists for this.
        var permissive = Check(IntegerSchema, NumberSchema, surface: CompatibilitySurface.WireJson);
        Assert.True(permissive.IsCompatible, Describe(permissive));

        var strict = Check(IntegerSchema, NumberSchema, surface: CompatibilitySurface.Source);
        Assert.False(strict.IsCompatible);
        Assert.Equal(
            BreakingChangeKinds.IntegerWidenedToNumber,
            Assert.Single(strict.BreakingChanges).Kind);
    }

    [Fact]
    public void ASourceOnlyDivergence_IsStillReportedUnderAPermissivePolicy()
    {
        // Reported in AllDivergences but not in BreakingChanges. This is what lets the API
        // explain why a change was allowed rather than staying silent about it.
        var report = Check(IntegerSchema, NumberSchema, surface: CompatibilitySurface.WireJson);

        Assert.True(report.IsCompatible);
        Assert.Empty(report.BreakingChanges);
        Assert.Single(report.AllDivergences);
        Assert.Equal(CompatibilitySurface.Source, report.AllDivergences[0].Surface);
    }

    [Fact]
    public void ChangingFormat_IsSourceOnly()
    {
        const string previous = """{"type":"object","properties":{"t":{"type":"string","format":"date-time"}}}""";
        const string proposed = """{"type":"object","properties":{"t":{"type":"string","format":"email"}}}""";

        Assert.True(Check(previous, proposed, surface: CompatibilitySurface.WireJson).IsCompatible);

        var strict = Check(previous, proposed, surface: CompatibilitySurface.Source);
        Assert.False(strict.IsCompatible);
        Assert.Equal("#/properties/t/format", Assert.Single(strict.BreakingChanges).Path);
    }

    [Fact]
    public void AWirePolicy_IsEffectivelyNoCheckingForJsonSchema()
    {
        // JSON is self-describing, so no JSON Schema divergence breaks byte decoding. Recorded
        // as a test because it is the justification for Backward x WireJson being the default.
        const string previous = """{"type":"object","properties":{"id":{"type":"string"}},"required":[]}""";
        const string proposed = """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}""";

        var report = Check(previous, proposed, surface: CompatibilitySurface.Wire);

        Assert.True(report.IsCompatible);
        Assert.NotEmpty(report.AllDivergences);
    }

    private static string Describe(CompatibilityReport report) =>
        string.Join("; ", report.BreakingChanges.Select(c => $"{c.Kind} at {c.Path}"));
}

public class ContentModelTests : CompatibilityTestBase
{
    private const string OneProperty = """{"type":"object","properties":{"a":{"type":"string"}}}""";
    private const string TwoProperties = """{"type":"object","properties":{"a":{"type":"string"},"b":{"type":"string"}}}""";

    [Fact]
    public void UnderAnOpenModel_AddingAPropertyIsCompatibleBothWays()
    {
        Assert.True(Check(OneProperty, TwoProperties, CompatibilityMode.Full).IsCompatible);
    }

    [Fact]
    public void UnderAClosedModel_AddingAPropertyIsForwardBreaking()
    {
        var forward = Check(
            OneProperty, TwoProperties, CompatibilityMode.Forward, contentModel: ContentModel.Closed);

        Assert.False(forward.IsCompatible);
        var change = Assert.Single(forward.BreakingChanges);
        Assert.Equal(BreakingChangeKinds.PropertyAdded, change.Kind);
        Assert.Equal("#/properties/b", change.Path);
    }

    [Fact]
    public void UnderAClosedModel_RemovingAPropertyIsBackwardBreaking()
    {
        var backward = Check(
            TwoProperties, OneProperty, CompatibilityMode.Backward, contentModel: ContentModel.Closed);

        Assert.False(backward.IsCompatible);
        Assert.Equal(
            BreakingChangeKinds.PropertyRemoved,
            Assert.Single(backward.BreakingChanges).Kind);
    }

    [Fact]
    public void TheContentModelIsSuppliedNotInferred()
    {
        // Same two documents, opposite verdicts, decided entirely by configuration. Inferring
        // it per-schema is what lets it flip silently between versions.
        Assert.True(Check(OneProperty, TwoProperties, CompatibilityMode.Full).IsCompatible);
        Assert.False(Check(
            OneProperty, TwoProperties, CompatibilityMode.Full,
            contentModel: ContentModel.Closed).IsCompatible);
    }
}

public class ModeAndTransitivityTests : CompatibilityTestBase
{
    private static readonly JsonSchemaCanonicalizer Canon = new();

    private static CompatibilityReport CheckAgainst(
        string proposed, CompatibilityMode mode, params (int Ordinal, string Body)[] priors) =>
        Checker.Check(
            Canon.Canonicalize(proposed).Value,
            priors.Select(p => new PriorSchema(p.Ordinal, Canon.Canonicalize(p.Body).Value)).ToList(),
            new CompatibilityPolicy(mode, CompatibilitySurface.WireJson),
            ContentModel.Open);

    [Fact]
    public void NoPriors_IsTriviallyCompatible()
    {
        var report = Checker.Check(
            """{"type":"object"}""", [], CompatibilityPolicy.Default, ContentModel.Open);

        Assert.True(report.IsCompatible);
        Assert.Equal(SemverBump.None, report.SuggestedBump);
    }

    [Fact]
    public void ModeNone_SkipsCheckingEntirely()
    {
        var report = Check(
            """{"type":"object","properties":{"n":{"type":["string","null"]}}}""",
            """{"type":"object","properties":{"n":{"type":"string"}}}""",
            CompatibilityMode.None);

        Assert.True(report.IsCompatible);
        Assert.Empty(report.AllDivergences);
    }

    [Fact]
    public void NonTransitive_ComparesOnlyAgainstTheHighestOrdinal()
    {
        // v1 permits string|null, v2 narrowed to string. Proposing string again is compatible
        // with v2 and would be a no-op, so nothing is reported.
        var report = CheckAgainst(
            """{"type":"object","properties":{"n":{"type":"string"}}}""",
            CompatibilityMode.Backward,
            (1, """{"type":"object","properties":{"n":{"type":["string","null"]}}}"""),
            (2, """{"type":"object","properties":{"n":{"type":"string"}}}"""));

        Assert.True(report.IsCompatible);
        Assert.Empty(report.AllDivergences);
    }

    [Fact]
    public void Transitive_ComparesAgainstEveryPriorVersion()
    {
        // The same proposal, now checked against v1 too, where the narrowing is visible. This
        // is the case a chain of individually-compatible changes hides.
        var report = CheckAgainst(
            """{"type":"object","properties":{"n":{"type":"string"}}}""",
            CompatibilityMode.BackwardTransitive,
            (1, """{"type":"object","properties":{"n":{"type":["string","null"]}}}"""),
            (2, """{"type":"object","properties":{"n":{"type":"string"}}}"""));

        Assert.False(report.IsCompatible);
        var change = Assert.Single(report.BreakingChanges);
        Assert.Equal(1, change.ConflictsWithVersion);
    }

    [Fact]
    public void Full_ReportsBothDirections()
    {
        const string previous = """{"type":"object","properties":{"a":{"type":"string"}},"required":["a"]}""";
        const string proposed = """{"type":"object","properties":{"a":{"type":["string","null"]}},"required":[]}""";

        var report = Check(previous, proposed, CompatibilityMode.Full);

        Assert.False(report.IsCompatible);
        Assert.Contains(report.BreakingChanges, c => c.Direction == CompatibilityDirection.Forward);
    }

    [Fact]
    public void SuggestedBump_IsMajorWhenThePolicyIsViolated()
    {
        var report = Check(
            """{"type":"object","properties":{"a":{"type":"string"}}}""",
            """{"type":"object","properties":{"a":{"type":"string"}},"required":["a"]}""");

        Assert.Equal(SemverBump.Major, report.SuggestedBump);
    }

    [Fact]
    public void SuggestedBump_IsMinorWhenDivergencesAreTolerated()
    {
        var report = Check(
            """{"type":"object","properties":{"n":{"type":"integer"}}}""",
            """{"type":"object","properties":{"n":{"type":"number"}}}""",
            surface: CompatibilitySurface.WireJson);

        Assert.True(report.IsCompatible);
        Assert.Equal(SemverBump.Minor, report.SuggestedBump);
    }
}

public class JsonPointerTests : CompatibilityTestBase
{
    [Fact]
    public void Paths_AreNestedCorrectly()
    {
        const string previous = """{"properties":{"outer":{"properties":{"inner":{"maximum":10}}}}}""";
        const string proposed = """{"properties":{"outer":{"properties":{"inner":{"maximum":5}}}}}""";

        var report = Check(previous, proposed);

        Assert.Equal(
            "#/properties/outer/properties/inner/maximum",
            Assert.Single(report.BreakingChanges).Path);
    }

    [Fact]
    public void Paths_DescendIntoArrayItems()
    {
        const string previous = """{"properties":{"tags":{"items":{"type":["string","null"]}}}}""";
        const string proposed = """{"properties":{"tags":{"items":{"type":"string"}}}}""";

        var report = Check(previous, proposed);

        Assert.Equal(
            "#/properties/tags/items",
            Assert.Single(report.BreakingChanges).Path);
    }

    [Fact]
    public void Paths_EscapePerRfc6901()
    {
        // '/' becomes '~1' and '~' becomes '~0', or the pointer is unparseable.
        const string previous = """{"properties":{"a/b":{"type":["string","null"]}}}""";
        const string proposed = """{"properties":{"a/b":{"type":"string"}}}""";

        var report = Check(previous, proposed);

        Assert.Equal("#/properties/a~1b", Assert.Single(report.BreakingChanges).Path);
    }
}
