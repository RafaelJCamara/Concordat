using Concordat.Domain.Registry;
using Concordat.Formats.Abstractions;
using Concordat.Formats.Protobuf;

namespace Concordat.Formats.Protobuf.Tests;

/// <summary>
/// Protobuf compatibility, with the two-axis distinctions Confluent cannot express.
/// </summary>
public class CompatibilityTests
{
    private static readonly ProtoSchemaCanonicalizer Canonicalizer = new();
    private static readonly ProtoSchemaCompatibilityChecker Checker = new();

    private static readonly CompatibilityPolicy Backward =
        new(CompatibilityMode.Backward, CompatibilitySurface.WireJson);

    private static readonly CompatibilityPolicy BackwardWire =
        new(CompatibilityMode.Backward, CompatibilitySurface.Wire);

    private static readonly CompatibilityPolicy BackwardSource =
        new(CompatibilityMode.Backward, CompatibilitySurface.Source);

    private static readonly CompatibilityPolicy Forward =
        new(CompatibilityMode.Forward, CompatibilitySurface.WireJson);

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

    private static string Msg(string body) =>
        $"syntax = \"proto3\"; package acme; message Order {{ {body} }}";

    [Fact]
    public void Handles_TheProtobufFormat() => Assert.Equal(SchemaFormat.Protobuf, Checker.Format);

    [Fact]
    public void TheFirstVersion_IsTriviallyCompatible()
    {
        var report = Checker.Check(
            Canonical(Msg("string id = 1;")), [], Backward, ContentModel.Open);

        Assert.True(report.IsCompatible);
        Assert.Equal(SemverBump.None, report.SuggestedBump);
    }

    // ------------------------------------------------ the cases Confluent gets wrong

    [Fact]
    public void RenamingAMessageWithStableFieldTags_PassesWire_AndFailsWireJson()
    {
        // DESIGN §12 names this exactly. Confluent rejects it outright even though the encoded
        // bytes are identical; the surface axis says what actually changed.
        const string before = "syntax = \"proto3\"; package acme; message Order { string id = 1; }";
        const string after = "syntax = \"proto3\"; package acme; message Purchase { string id = 1; }";

        var onWire = Check(before, after, BackwardWire);
        Assert.True(onWire.IsCompatible);

        var onJson = Check(before, after, Backward);
        Assert.False(onJson.IsCompatible);

        var breaking = onJson.BreakingChanges[0];
        Assert.Equal(BreakingChangeKinds.NameChanged, breaking.Kind);
        Assert.Equal(CompatibilitySurface.WireJson, breaking.Surface);
    }

    [Fact]
    public void RenamingAField_PassesWire_AndFailsWireJson()
    {
        const string before = "syntax = \"proto3\"; package acme; message Order { string id = 1; }";
        const string after = "syntax = \"proto3\"; package acme; message Order { string order_id = 1; }";

        Assert.True(Check(before, after, BackwardWire).IsCompatible);

        var report = Check(before, after, Backward);
        Assert.False(report.IsCompatible);
        Assert.Equal(BreakingChangeKinds.NameChanged, report.BreakingChanges[0].Kind);
    }

    [Fact]
    public void Int32ToInt64_PassesWire_AndFailsSource()
    {
        // The corpus case from DESIGN §12. The varint bytes are unchanged; proto3 JSON quotes
        // 64-bit integers and leaves 32-bit ones bare, so the finding sits at WireJson - which
        // a Wire policy tolerates and a Source policy blocks.
        const string before = "syntax = \"proto3\"; package acme; message Order { int32 n = 1; }";
        const string after = "syntax = \"proto3\"; package acme; message Order { int64 n = 1; }";

        Assert.True(Check(before, after, BackwardWire).IsCompatible);
        Assert.False(Check(before, after, BackwardSource).IsCompatible);

        var divergence = Check(before, after, BackwardWire).AllDivergences[0];
        Assert.Equal(BreakingChangeKinds.TypePromoted, divergence.Kind);
        Assert.Equal(CompatibilitySurface.WireJson, divergence.Surface);
    }

    // -------------------------------------------------------------------- additions

    [Fact]
    public void AddingAFieldWithANewNumber_IsFullyCompatible()
    {
        // proto3 has no required fields and readers skip unknown tags, so this is the safe,
        // ordinary change in both directions.
        var report = Check(
            Msg("string id = 1;"),
            Msg("string id = 1; int32 quantity = 2;"),
            new CompatibilityPolicy(CompatibilityMode.Full, CompatibilitySurface.Source));

        Assert.True(report.IsCompatible);
        Assert.Empty(report.AllDivergences);
    }

    // --------------------------------------------------------------------- removals

    [Fact]
    public void RemovingAFieldWithoutReservingIt_IsBreaking()
    {
        var report = Check(Msg("string id = 1; int32 q = 2;"), Msg("string id = 1;"), Backward);

        Assert.False(report.IsCompatible);
        var breaking = Assert.Single(report.BreakingChanges);
        Assert.Equal(BreakingChangeKinds.FieldRemovedWithoutReserved, breaking.Kind);
        Assert.Equal(CompatibilitySurface.Wire, breaking.Surface);
        Assert.Contains("reserved 2;", breaking.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RemovingAFieldAndReservingIt_IsCompatible() =>
        Assert.True(Check(
            Msg("string id = 1; int32 q = 2;"),
            Msg("reserved 2; string id = 1;"),
            Backward).IsCompatible);

    [Fact]
    public void ReusingAReservedNumber_IsBreakingOnTheWire()
    {
        var report = Check(
            Msg("reserved 2; string id = 1;"),
            Msg("string id = 1; bool flag = 2;"),
            Backward);

        Assert.False(report.IsCompatible);
        var breaking = Assert.Single(report.BreakingChanges);
        Assert.Equal(BreakingChangeKinds.FieldNumberReused, breaking.Kind);
        Assert.Equal(CompatibilitySurface.Wire, breaking.Surface);
    }

    // ----------------------------------------------------------------- type changes

    [Fact]
    public void ChangingWireType_IsBreakingOnTheWire()
    {
        // int32 is a varint; fixed32 is four literal bytes. The tag tells the decoder how much
        // to read, so this misaligns everything after it.
        var report = Check(Msg("int32 n = 1;"), Msg("fixed32 n = 1;"), Backward);

        Assert.False(report.IsCompatible);
        Assert.Equal(BreakingChangeKinds.WireTypeChanged, report.BreakingChanges[0].Kind);
        Assert.Equal(CompatibilitySurface.Wire, report.BreakingChanges[0].Surface);
    }

    [Fact]
    public void ChangingIntToSint_IsBreaking_BecauseZigzagMeansDifferentBytes()
    {
        // Both are varints, so the bytes are read - but as a different number.
        var report = Check(Msg("int32 n = 1;"), Msg("sint32 n = 1;"), BackwardWire);

        Assert.False(report.IsCompatible);
        Assert.Equal(BreakingChangeKinds.WireTypeChanged, report.BreakingChanges[0].Kind);
    }

    [Fact]
    public void ChangingIntToUint_IsBreaking_BecauseNegativesReinterpret() =>
        Assert.False(Check(Msg("int32 n = 1;"), Msg("uint32 n = 1;"), BackwardWire).IsCompatible);

    [Fact]
    public void StringToBytes_KeepsTheWire_AndChangesTheJsonMapping()
    {
        // Both are length-delimited, so bytes decode; proto3 JSON base64-encodes bytes.
        Assert.True(Check(Msg("string s = 1;"), Msg("bytes s = 1;"), BackwardWire).IsCompatible);
        Assert.False(Check(Msg("string s = 1;"), Msg("bytes s = 1;"), Backward).IsCompatible);
    }

    [Fact]
    public void ChangingCardinality_IsBreakingOnTheWire() =>
        // A repeated scalar packs into one length-delimited entry.
        Assert.False(Check(
            Msg("int32 n = 1;"), Msg("repeated int32 n = 1;"), BackwardWire).IsCompatible);

    [Fact]
    public void AddingExplicitPresence_IsOnlyASourceChange()
    {
        // proto3 'optional' adds a presence bit in generated code; encoding is identical.
        Assert.True(Check(Msg("int32 n = 1;"), Msg("optional int32 n = 1;"), Backward).IsCompatible);

        var report = Check(Msg("int32 n = 1;"), Msg("optional int32 n = 1;"), BackwardSource);
        Assert.False(report.IsCompatible);
        Assert.Equal(BreakingChangeKinds.PresenceChanged, report.BreakingChanges[0].Kind);
    }

    [Fact]
    public void ChangingAMapToANonMap_IsBreaking() =>
        Assert.False(Check(
            Msg("map<string, int32> m = 1;"), Msg("int32 m = 1;"), BackwardWire).IsCompatible);

    // ----------------------------------------------------------------------- enums

    private static string Enum(string values) =>
        $"syntax = \"proto3\"; package acme; enum Status {{ {values} }}";

    [Fact]
    public void AddingAnEnumValue_IsForwardBreakingOnlyAtTheJsonSurface()
    {
        // proto3 preserves unrecognised enum numbers rather than failing, so the wire holds.
        Assert.True(Check(
            Enum("UNKNOWN = 0;"),
            Enum("UNKNOWN = 0; ACTIVE = 1;"),
            new CompatibilityPolicy(CompatibilityMode.Forward, CompatibilitySurface.Wire))
            .IsCompatible);

        var report = Check(Enum("UNKNOWN = 0;"), Enum("UNKNOWN = 0; ACTIVE = 1;"), Forward);
        Assert.False(report.IsCompatible);
        Assert.Equal(BreakingChangeKinds.EnumValueAdded, report.BreakingChanges[0].Kind);
    }

    [Fact]
    public void AddingAnEnumValue_IsBackwardCompatible() =>
        Assert.True(Check(
            Enum("UNKNOWN = 0;"), Enum("UNKNOWN = 0; ACTIVE = 1;"), Backward).IsCompatible);

    [Fact]
    public void RemovingAnEnumValue_IsBackwardBreakingAtTheJsonSurface()
    {
        var report = Check(Enum("UNKNOWN = 0; ACTIVE = 1;"), Enum("UNKNOWN = 0;"), Backward);

        Assert.False(report.IsCompatible);
        Assert.Equal(BreakingChangeKinds.EnumValueRemoved, report.BreakingChanges[0].Kind);
        Assert.Equal(CompatibilitySurface.WireJson, report.BreakingChanges[0].Surface);
    }

    [Fact]
    public void RenamingAnEnumValue_KeepsTheWire_AndChangesTheJson()
    {
        Assert.True(Check(
            Enum("UNKNOWN = 0;"),
            Enum("UNSET = 0;"),
            new CompatibilityPolicy(CompatibilityMode.Backward, CompatibilitySurface.Wire))
            .IsCompatible);

        Assert.False(Check(Enum("UNKNOWN = 0;"), Enum("UNSET = 0;"), Backward).IsCompatible);
    }

    // -------------------------------------------------------------------- direction

    [Fact]
    public void ABidirectionalBreak_IsCaughtByAForwardPolicyToo() =>
        // Protobuf is not reader/writer asymmetric the way Avro is: a wire-type change breaks
        // old and new readers alike, so a Forward-only policy must not miss it.
        Assert.False(Check(Msg("int32 n = 1;"), Msg("fixed32 n = 1;"), Forward).IsCompatible);

    [Fact]
    public void TransitiveMode_ReachesTheOldestVersion()
    {
        var report = Checker.Check(
            Canonical(Msg("int64 n = 1;")),
            [
                new PriorSchema(1, Canonical(Msg("int32 n = 1;"))),
                new PriorSchema(2, Canonical(Msg("int64 n = 1;"))),
            ],
            new CompatibilityPolicy(
                CompatibilityMode.BackwardTransitive, CompatibilitySurface.Source),
            ContentModel.Open);

        Assert.False(report.IsCompatible);
        Assert.Equal(1, report.BreakingChanges[0].ConflictsWithVersion);
    }

    [Fact]
    public void NonTransitiveMode_ChecksOnlyTheHighestOrdinal()
    {
        var report = Checker.Check(
            Canonical(Msg("int64 n = 1;")),
            [
                new PriorSchema(1, Canonical(Msg("int32 n = 1;"))),
                new PriorSchema(2, Canonical(Msg("int64 n = 1;"))),
            ],
            BackwardSource,
            ContentModel.Open);

        Assert.True(report.IsCompatible);
    }

    [Fact]
    public void NoChange_IsCompatibleWithNoDivergences()
    {
        var report = Check(Msg("string id = 1;"), Msg("string id = 1;"), BackwardSource);

        Assert.True(report.IsCompatible);
        Assert.Empty(report.AllDivergences);
        Assert.Equal(SemverBump.Patch, report.SuggestedBump);
    }

    [Fact]
    public void ReorderingFields_ChangesNothing() =>
        Assert.True(Check(
            Msg("string a = 1; string b = 2;"),
            Msg("string b = 2; string a = 1;"),
            BackwardSource).IsCompatible);

    [Fact]
    public void MovingAFieldIntoAOneof_DoesNotAffectTheWire() =>
        // Oneof membership is a source-level guarantee; the encoding of each member is
        // unchanged, so compatibility is judged over the flattened field set.
        Assert.True(Check(
            Msg("string id = 1; string card = 2;"),
            Msg("string id = 1; oneof pay { string card = 2; }"),
            BackwardSource).IsCompatible);
}
