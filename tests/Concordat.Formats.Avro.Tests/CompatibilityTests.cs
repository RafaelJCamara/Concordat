using Concordat.Domain.Registry;
using Concordat.Formats.Abstractions;
using Concordat.Formats.Avro;

namespace Concordat.Formats.Avro.Tests;

/// <summary>
/// Avro's Schema Resolution rules, stated as tests.
/// </summary>
/// <remarks>
/// Unlike the JSON Schema suite, these are not acceptance criteria Concordat invented — Avro
/// specifies the answers, so a disagreement here is a bug rather than a design choice.
/// </remarks>
public class CompatibilityTests
{
    private static readonly AvroSchemaCanonicalizer Canonicalizer = new();
    private static readonly AvroSchemaCompatibilityChecker Checker = new();

    private static readonly CompatibilityPolicy Backward =
        new(CompatibilityMode.Backward, CompatibilitySurface.WireJson);

    private static readonly CompatibilityPolicy Forward =
        new(CompatibilityMode.Forward, CompatibilitySurface.WireJson);

    private static readonly CompatibilityPolicy Full =
        new(CompatibilityMode.Full, CompatibilitySurface.WireJson);

    private static string Canonical(string body)
    {
        var result = Canonicalizer.Canonicalize(body);
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value;
    }

    private static CompatibilityReport Check(
        string previous, string proposed, CompatibilityPolicy policy) =>
        Checker.Check(
            Canonical(proposed),
            [new PriorSchema(1, Canonical(previous))],
            policy,
            ContentModel.Open);

    private static string Record(string fields) =>
        $$"""{"type":"record","name":"acme.User","fields":[{{fields}}]}""";

    [Fact]
    public void Handles_TheAvroFormat() => Assert.Equal(SchemaFormat.Avro, Checker.Format);

    [Fact]
    public void TheFirstVersion_IsTriviallyCompatible()
    {
        var report = Checker.Check(Canonical(Record("")), [], Backward, ContentModel.Open);

        Assert.True(report.IsCompatible);
        Assert.Equal(SemverBump.None, report.SuggestedBump);
    }

    [Fact]
    public void ModeNone_SkipsCheckingEntirely()
    {
        var report = Check(
            Record("""{"name":"a","type":"string"}"""),
            Record("""{"name":"a","type":"int"}"""),
            new CompatibilityPolicy(CompatibilityMode.None, CompatibilitySurface.Wire));

        Assert.True(report.IsCompatible);
    }

    // ------------------------------------------------------------------- fields

    [Fact]
    public void AddingAFieldWithADefault_IsBackwardCompatible()
    {
        // The ordinary Avro change, and the one that is impossible to get right without the
        // default surviving canonicalisation (DECISIONS-PENDING #17).
        var report = Check(
            Record("""{"name":"a","type":"string"}"""),
            Record("""{"name":"a","type":"string"},{"name":"b","type":"string","default":""}"""),
            Backward);

        Assert.True(report.IsCompatible);
        Assert.Empty(report.AllDivergences);
    }

    [Fact]
    public void AddingAFieldWithoutADefault_IsBackwardBreakingOnTheWire()
    {
        var report = Check(
            Record("""{"name":"a","type":"string"}"""),
            Record("""{"name":"a","type":"string"},{"name":"b","type":"string"}"""),
            Backward);

        Assert.False(report.IsCompatible);
        var breaking = Assert.Single(report.BreakingChanges);
        Assert.Equal(BreakingChangeKinds.RequiredFieldAdded, breaking.Kind);
        Assert.Equal(CompatibilitySurface.Wire, breaking.Surface);
        Assert.Equal("#/fields/b", breaking.Path);
        Assert.Equal(SemverBump.Major, report.SuggestedBump);
    }

    [Fact]
    public void RemovingAField_IsBackwardCompatible()
    {
        // The reader simply skips what the writer wrote. This is Avro's equivalent of the
        // optional-property removal DESIGN §7 requires to be fully compatible.
        var report = Check(
            Record("""{"name":"a","type":"string"},{"name":"b","type":"string"}"""),
            Record("""{"name":"a","type":"string"}"""),
            Backward);

        Assert.True(report.IsCompatible);
        Assert.Empty(report.AllDivergences);
    }

    [Fact]
    public void RemovingAFieldTheOldSchemaCannotDefault_IsForwardBreaking()
    {
        var report = Check(
            Record("""{"name":"a","type":"string"},{"name":"b","type":"string"}"""),
            Record("""{"name":"a","type":"string"}"""),
            Forward);

        Assert.False(report.IsCompatible);
        var breaking = Assert.Single(report.BreakingChanges);
        Assert.Equal(BreakingChangeKinds.RequiredFieldRemoved, breaking.Kind);
        Assert.Equal(CompatibilitySurface.Wire, breaking.Surface);
    }

    [Fact]
    public void RemovingAFieldTheOldSchemaCanDefault_IsFullyCompatible()
    {
        var report = Check(
            Record("""{"name":"a","type":"string"},{"name":"b","type":"string","default":""}"""),
            Record("""{"name":"a","type":"string"}"""),
            Full);

        Assert.True(report.IsCompatible);
    }

    [Fact]
    public void ReorderingFields_IsCompatibleInBothDirections() =>
        // Resolution matches fields by name, so wire order is free to change.
        Assert.True(Check(
            Record("""{"name":"a","type":"int"},{"name":"b","type":"int"}"""),
            Record("""{"name":"b","type":"int"},{"name":"a","type":"int"}"""),
            Full).IsCompatible);

    [Fact]
    public void RenamingAField_IsRecoveredByAnAliasOnTheReader()
    {
        var report = Check(
            Record("""{"name":"old","type":"string"}"""),
            Record("""{"name":"new","type":"string","aliases":["old"]}"""),
            Backward);

        Assert.True(report.IsCompatible);
    }

    // ------------------------------------------------------------ the ADR-016 case

    [Fact]
    public void IntPromotedToLong_IsPermittedUnderTheDefaultPolicy_AndBlockedUnderSource()
    {
        // Avro's int32 -> int64: the case a single-axis model cannot express at all. The bytes
        // decode because Avro defines the promotion; the generated type changes.
        const string before = """{"name":"n","type":"int"}""";
        const string after = """{"name":"n","type":"long"}""";

        var permitted = Check(Record(before), Record(after), Backward);

        Assert.True(permitted.IsCompatible);
        var divergence = Assert.Single(permitted.AllDivergences);
        Assert.Equal(BreakingChangeKinds.TypePromoted, divergence.Kind);
        Assert.Equal(CompatibilitySurface.Source, divergence.Surface);
        Assert.Equal(SemverBump.Minor, permitted.SuggestedBump);

        var blocked = Check(
            Record(before),
            Record(after),
            new CompatibilityPolicy(CompatibilityMode.Backward, CompatibilitySurface.Source));

        Assert.False(blocked.IsCompatible);
        Assert.Equal(SemverBump.Major, blocked.SuggestedBump);
    }

    [Fact]
    public void LongDemotedToInt_IsBackwardBreaking_BecausePromotionIsOneWay()
    {
        var report = Check(
            Record("""{"name":"n","type":"long"}"""),
            Record("""{"name":"n","type":"int"}"""),
            Backward);

        Assert.False(report.IsCompatible);
        Assert.Equal(CompatibilitySurface.Wire, Assert.Single(report.BreakingChanges).Surface);
    }

    [Fact]
    public void IntToLong_IsForwardBreaking_EvenThoughItIsBackwardCompatible() =>
        // The asymmetry that makes running resolution twice necessary rather than tidy.
        Assert.False(Check(
            Record("""{"name":"n","type":"int"}"""),
            Record("""{"name":"n","type":"long"}"""),
            Forward).IsCompatible);

    [Fact]
    public void AnIncompatibleTypeChange_IsBreakingOnTheWire()
    {
        var report = Check(
            Record("""{"name":"a","type":"string"}"""),
            Record("""{"name":"a","type":"boolean"}"""),
            Backward);

        Assert.False(report.IsCompatible);
        Assert.Equal(BreakingChangeKinds.TypeNarrowed, Assert.Single(report.BreakingChanges).Kind);
    }

    // -------------------------------------------------------------------- enums

    /// <summary>A record carrying one enum field, with the given symbols and optional default.</summary>
    private static string Enum(string[] symbols, string? symbolDefault = null)
    {
        var quoted = string.Join(",", symbols.Select(s => $"\"{s}\""));
        var fallback = symbolDefault is null ? "" : $",\"default\":\"{symbolDefault}\"";

        return "{\"type\":\"record\",\"name\":\"acme.User\",\"fields\":[" +
               "{\"name\":\"suit\",\"type\":{\"type\":\"enum\",\"name\":\"acme.Suit\"," +
               $"\"symbols\":[{quoted}]{fallback}}}}}]}}";
    }

    [Fact]
    public void RemovingAnEnumSymbol_IsBackwardBreaking()
    {
        var report = Check(Enum(["A", "B"]), Enum(["A"]), Backward);

        Assert.False(report.IsCompatible);
        var breaking = Assert.Single(report.BreakingChanges);
        Assert.Equal(BreakingChangeKinds.EnumValueRemoved, breaking.Kind);
        Assert.Equal(CompatibilitySurface.Wire, breaking.Surface);
    }

    [Fact]
    public void AddingAnEnumSymbol_IsForwardBreaking()
    {
        var report = Check(Enum(["A"]), Enum(["A", "B"]), Forward);

        Assert.False(report.IsCompatible);
        Assert.Equal(BreakingChangeKinds.EnumValueAdded, Assert.Single(report.BreakingChanges).Kind);
    }

    [Fact]
    public void AddingAnEnumSymbol_IsBackwardCompatible() =>
        Assert.True(Check(Enum(["A"]), Enum(["A", "B"]), Backward).IsCompatible);

    [Fact]
    public void AnEnumDefault_AbsorbsAnUnknownSymbol_AtTheJsonSurfaceNotTheWire()
    {
        // The bytes decode, so this is not a Wire break - but the application sees a different
        // value than was written, which is exactly a broken JSON mapping.
        var report = Check(Enum(["A", "B"]), Enum(["A"], symbolDefault: "A"), Backward);

        Assert.False(report.IsCompatible);
        var breaking = Assert.Single(report.BreakingChanges);
        Assert.Equal(BreakingChangeKinds.EnumValueDefaulted, breaking.Kind);
        Assert.Equal(CompatibilitySurface.WireJson, breaking.Surface);

        // ...and a Wire policy tolerates it, which is the distinction the surface axis buys.
        var tolerated = Check(
            Enum(["A", "B"]),
            Enum(["A"], symbolDefault: "A"),
            new CompatibilityPolicy(CompatibilityMode.Backward, CompatibilitySurface.Wire));

        Assert.True(tolerated.IsCompatible);
    }

    // -------------------------------------------------------------- named types

    [Fact]
    public void RenamingARecord_IsBreaking()
    {
        var report = Checker.Check(
            Canonical("""{"type":"record","name":"acme.Renamed","fields":[]}"""),
            [new PriorSchema(1, Canonical("""{"type":"record","name":"acme.User","fields":[]}"""))],
            Backward,
            ContentModel.Open);

        Assert.False(report.IsCompatible);
        var breaking = Assert.Single(report.BreakingChanges);
        Assert.Equal(BreakingChangeKinds.NameChanged, breaking.Kind);
        Assert.Equal(CompatibilitySurface.Wire, breaking.Surface);
    }

    [Fact]
    public void RenamingARecord_IsRecoveredByAnAliasInTheBackwardDirection()
    {
        var report = Checker.Check(
            Canonical(
                """{"type":"record","name":"acme.Renamed","aliases":["acme.User"],"fields":[]}"""),
            [new PriorSchema(1, Canonical("""{"type":"record","name":"acme.User","fields":[]}"""))],
            Backward,
            ContentModel.Open);

        Assert.True(report.IsCompatible);
    }

    [Fact]
    public void ChangingAFixedSize_IsBreaking()
    {
        var report = Checker.Check(
            Canonical("""{"type":"fixed","name":"acme.Hash","size":32}"""),
            [new PriorSchema(1, Canonical("""{"type":"fixed","name":"acme.Hash","size":16}"""))],
            Backward,
            ContentModel.Open);

        Assert.False(report.IsCompatible);
        Assert.Equal(BreakingChangeKinds.FixedSizeChanged, Assert.Single(report.BreakingChanges).Kind);
    }

    // ------------------------------------------------------------------- unions

    [Fact]
    public void WideningATypeIntoAUnion_IsBackwardCompatible() =>
        Assert.True(Check(
            Record("""{"name":"a","type":"string"}"""),
            Record("""{"name":"a","type":["null","string"]}"""),
            Backward).IsCompatible);

    [Fact]
    public void RemovingAUnionBranch_IsBackwardBreaking() =>
        // Data written under the old schema could have been null, and the new reader has
        // nowhere to put it.
        Assert.False(Check(
            Record("""{"name":"a","type":["null","string"]}"""),
            Record("""{"name":"a","type":"string"}"""),
            Backward).IsCompatible);

    [Fact]
    public void WideningATypeIntoAUnion_IsForwardBreaking() =>
        Assert.False(Check(
            Record("""{"name":"a","type":"string"}"""),
            Record("""{"name":"a","type":["null","string"]}"""),
            Forward).IsCompatible);

    // ----------------------------------------------------------------- recursion

    [Fact]
    public void ASelfReferentialRecord_Terminates()
    {
        const string list =
            """
            {"type":"record","name":"acme.LongList","fields":[
              {"name":"value","type":"long"},
              {"name":"next","type":["null","acme.LongList"]}]}
            """;

        Assert.True(Check(list, list, Full).IsCompatible);
    }

    [Fact]
    public void ADeepChangeInsideARecursiveRecord_IsStillFound()
    {
        const string before =
            """
            {"type":"record","name":"acme.Node","fields":[
              {"name":"value","type":"int"},
              {"name":"next","type":["null","acme.Node"]}]}
            """;

        const string after =
            """
            {"type":"record","name":"acme.Node","fields":[
              {"name":"value","type":"string"},
              {"name":"next","type":["null","acme.Node"]}]}
            """;

        Assert.False(Check(before, after, Backward).IsCompatible);
    }

    // --------------------------------------------------------------- containers

    [Fact]
    public void AnIncompatibleChangeInsideAnArray_IsFound() =>
        Assert.False(Check(
            Record("""{"name":"xs","type":{"type":"array","items":"string"}}"""),
            Record("""{"name":"xs","type":{"type":"array","items":"int"}}"""),
            Backward).IsCompatible);

    [Fact]
    public void AnIncompatibleChangeInsideAMap_IsFound() =>
        Assert.False(Check(
            Record("""{"name":"m","type":{"type":"map","values":"string"}}"""),
            Record("""{"name":"m","type":{"type":"map","values":"int"}}"""),
            Backward).IsCompatible);

    [Fact]
    public void ContainerKindChange_IsBreaking() =>
        Assert.False(Check(
            Record("""{"name":"xs","type":{"type":"array","items":"string"}}"""),
            Record("""{"name":"xs","type":{"type":"map","values":"string"}}"""),
            Backward).IsCompatible);

    // ------------------------------------------------------------------ transitivity

    [Fact]
    public void NonTransitiveMode_ChecksOnlyTheHighestOrdinal()
    {
        // v1 int, v2 long, proposing long: compatible against v2 alone.
        var report = Checker.Check(
            Canonical(Record("""{"name":"n","type":"long"}""")),
            [
                new PriorSchema(1, Canonical(Record("""{"name":"n","type":"int"}"""))),
                new PriorSchema(2, Canonical(Record("""{"name":"n","type":"long"}"""))),
            ],
            Backward,
            ContentModel.Open);

        Assert.True(report.IsCompatible);
        Assert.Empty(report.AllDivergences);
    }

    [Fact]
    public void TransitiveMode_ReachesTheOldestVersion()
    {
        var report = Checker.Check(
            Canonical(Record("""{"name":"n","type":"long"}""")),
            [
                new PriorSchema(1, Canonical(Record("""{"name":"n","type":"int"}"""))),
                new PriorSchema(2, Canonical(Record("""{"name":"n","type":"long"}"""))),
            ],
            new CompatibilityPolicy(
                CompatibilityMode.BackwardTransitive, CompatibilitySurface.Source),
            ContentModel.Open);

        Assert.False(report.IsCompatible);
        Assert.Equal(1, Assert.Single(report.BreakingChanges).ConflictsWithVersion);
    }
}
