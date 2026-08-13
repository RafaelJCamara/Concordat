using System.Text;
using Concordat.Domain.Registry;
using Concordat.Formats.Abstractions;
using Concordat.Formats.Json;

namespace Concordat.Formats.Json.Tests;

/// <summary>The ADR-015 acceptance criteria, stated as tests.</summary>
public class SchemaIdTests
{
    private static readonly JsonSchemaCanonicalizer Canonicalizer = new();

    private static SchemaId IdOf(string body, params Reference[] references) =>
        SchemaIdComputer.Compute(
            SchemaFormat.Json,
            Canonicalizer.Canonicalize(body).Value,
            references);

    private static Reference Ref(string name, string subject, int version) =>
        Reference.Create(name, SubjectName.Create(subject).Value, version).Value;

    [Fact]
    public void Id_Is32LowercaseHexCharacters()
    {
        var id = IdOf("""{"type":"object"}""");

        Assert.Equal(32, id.Value.Length);
        Assert.True(SchemaId.Create(id.Value).IsSuccess, "the computed id must round-trip");
    }

    [Fact]
    public void SchemasDifferingOnlyInWhitespace_ShareAnId() =>
        Assert.Equal(
            IdOf("""{"type":"object"}"""),
            IdOf("  {  \"type\" :  \"object\"  }  "));

    [Fact]
    public void SchemasDifferingOnlyInKeyOrder_ShareAnId() =>
        Assert.Equal(
            IdOf("""{"a":1,"z":2}"""),
            IdOf("""{"z":2,"a":1}"""));

    [Fact]
    public void SchemasDifferingOnlyInStringEscaping_ShareAnId() =>
        Assert.Equal(
            IdOf("""{"t":"A"}"""),
            IdOf("""{"t":"A"}"""));

    [Fact]
    public void DifferentBodies_HaveDifferentIds() =>
        Assert.NotEqual(
            IdOf("""{"type":"object"}"""),
            IdOf("""{"type":"array"}"""));

    [Fact]
    public void SameBodyDifferentFormat_HasADifferentId()
    {
        var json = SchemaIdComputer.Compute(SchemaFormat.Json, "{}");
        var avro = SchemaIdComputer.Compute(SchemaFormat.Avro, "{}");

        Assert.NotEqual(json, avro);
    }

    [Fact]
    public void SameBodyDifferentReferenceSets_HaveDifferentIds()
    {
        // The hash covers references, not just the body. Getting this wrong is the specific
        // mistake Confluent's CP 8.1 GUID computation exists to avoid.
        var none = IdOf("""{"type":"object"}""");
        var one = IdOf("""{"type":"object"}""", Ref("common", "acme.Common", 1));

        Assert.NotEqual(none, one);
    }

    [Fact]
    public void SameReferencesAtDifferentVersions_HaveDifferentIds() =>
        Assert.NotEqual(
            IdOf("{}", Ref("common", "acme.Common", 1)),
            IdOf("{}", Ref("common", "acme.Common", 2)));

    [Fact]
    public void ReferenceOrder_DoesNotAffectTheId()
    {
        var a = Ref("alpha", "acme.A", 1);
        var z = Ref("zeta", "acme.Z", 1);

        Assert.Equal(IdOf("{}", a, z), IdOf("{}", z, a));
    }

    [Fact]
    public void TheSameSchemaInTwoEnvironments_HasTheSameId()
    {
        // The property that makes promotion safe: an in-flight envelope stays valid because
        // the id did not change (ADR-012, ADR-015). There is no environment input at all, so
        // this holds by construction — asserted to keep it that way.
        const string body = """{"type":"object","properties":{"id":{"type":"string"}}}""";

        Assert.Equal(IdOf(body), IdOf(body));
    }

    [Fact]
    public void Preimage_IsUnambiguousAcrossFieldBoundaries()
    {
        // Without length prefixes, one reference named "a:b" and two named "a" and "b" could
        // serialise to the same bytes. This is the collision the framing exists to prevent.
        var single = IdOf("{}", Ref("a:b", "acme.X", 1));
        var pair = IdOf("{}", Ref("a", "acme.X", 1), Ref("b", "acme.X", 1));

        Assert.NotEqual(single, pair);
    }

    [Fact]
    public void Preimage_IsVersionTagged()
    {
        // Changing the derivation invalidates every stored id in every installation, so it
        // must be explicitly versioned - the lesson of Azure's unversioned scheme (ADR-010).
        var preimage = Encoding.UTF8.GetString(
            SchemaIdComputer.BuildPreimage(SchemaFormat.Json, "{}"));

        Assert.StartsWith("concordat-schema-id/v1\n", preimage, StringComparison.Ordinal);
    }

    [Fact]
    public void Preimage_UsesStableWireTokensNotEnumNames()
    {
        var preimage = Encoding.UTF8.GetString(
            SchemaIdComputer.BuildPreimage(SchemaFormat.Protobuf, "{}"));

        Assert.Contains("format:protobuf\n", preimage, StringComparison.Ordinal);
        Assert.DoesNotContain("Protobuf", preimage, StringComparison.Ordinal);
    }

    [Fact]
    public void Preimage_LengthPrefixesAreUtf8ByteCounts()
    {
        // "é" is one UTF-16 char but two UTF-8 bytes. A char count would not delimit the
        // encoded field.
        var preimage = Encoding.UTF8.GetString(
            SchemaIdComputer.BuildPreimage(SchemaFormat.Json, """{"t":"é"}"""));

        Assert.Contains("body:10:", preimage, StringComparison.Ordinal);
    }

    [Fact]
    public void Id_IsStableAcrossRuns()
    {
        // A golden value. If this changes, every stored id in every installation is invalid
        // and the change needs a preimage version bump plus a migration.
        Assert.Equal("696e1e6b82db0848e5c59eaa7a89f7d0", IdOf("""{"type":"object"}""").Value);
    }
}
