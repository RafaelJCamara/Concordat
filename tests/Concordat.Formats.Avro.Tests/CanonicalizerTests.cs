using Concordat.Domain.Registry;
using Concordat.Domain.Results;
using Concordat.Formats.Avro;

namespace Concordat.Formats.Avro.Tests;

public class CanonicalizerTests
{
    private static readonly AvroSchemaCanonicalizer Canonicalizer = new();

    private static string Canonical(string body)
    {
        var result = Canonicalizer.Canonicalize(body);
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value;
    }

    [Fact]
    public void Handles_TheAvroFormat() =>
        Assert.Equal(SchemaFormat.Avro, Canonicalizer.Format);

    [Theory]
    [InlineData("null")]
    [InlineData("boolean")]
    [InlineData("int")]
    [InlineData("long")]
    [InlineData("float")]
    [InlineData("double")]
    [InlineData("bytes")]
    [InlineData("string")]
    public void BarePrimitives_RoundTripAsThemselves(string primitive) =>
        Assert.Equal($"\"{primitive}\"", Canonical($"\"{primitive}\""));

    [Fact]
    public void PrimitiveInObjectForm_ReducesToTheBareForm_WhenNothingSurvives() =>
        Assert.Equal("\"long\"", Canonical("""{"type":"long"}"""));

    [Fact]
    public void PrimitiveInObjectForm_KeepsItsObjectForm_WhenItCarriesDoc() =>
        // Since decision #17, `doc` survives -- so there IS something left and the object form
        // is the honest canonical text. Under the old rule this reduced to "long".
        Assert.Equal(
            """{"type":"long","doc":"when it happened"}""",
            Canonical("""{"type":"long","doc":"when it happened"}"""));

    [Fact]
    public void LogicalType_IsKept_NotStripped() =>
        // The one deliberate deviation from Parsing Canonical Form. PCF's [PRIMITIVES] rule
        // would reduce this to "long", discarding the fact that it is a timestamp - which is
        // the type a generator emits and how a reader interprets the value.
        Assert.Equal(
            """{"type":"long","logicalType":"timestamp-millis"}""",
            Canonical("""{"logicalType":"timestamp-millis","type":"long"}"""));

    [Fact]
    public void DecimalPrecisionAndScale_Survive() =>
        Assert.Equal(
            """{"name":"acme.Money","type":"fixed","size":8,"logicalType":"decimal","precision":16,"scale":2}""",
            Canonical(
                """
                {"type":"fixed","name":"Money","namespace":"acme","size":8,
                 "logicalType":"decimal","precision":16,"scale":2}
                """));

    [Fact]
    public void NameAndNamespace_CombineIntoAFullname() =>
        Assert.Equal(
            """{"name":"acme.orders.Address","type":"record","fields":[]}""",
            Canonical("""{"type":"record","name":"Address","namespace":"acme.orders","fields":[]}"""));

    [Fact]
    public void ADottedName_IgnoresAnAccompanyingNamespace() =>
        Assert.Equal(
            """{"name":"acme.orders.Address","type":"record","fields":[]}""",
            Canonical(
                """{"type":"record","name":"acme.orders.Address","namespace":"ignored","fields":[]}"""));

    [Fact]
    public void NestedTypes_InheritTheEnclosingRecordsNamespace() =>
        Assert.Equal(
            """{"name":"acme.Outer","type":"record","fields":[{"name":"inner","type":{"name":"acme.Inner","type":"record","fields":[]}}]}""",
            Canonical(
                """
                {
                  "type": "record", "name": "Outer", "namespace": "acme",
                  "fields": [
                    { "name": "inner", "type": { "type": "record", "name": "Inner", "fields": [] } }
                  ]
                }
                """));

    [Fact]
    public void NamespaceIsDropped_AsRedundantOnceFullnamesAreResolved() =>
        Assert.DoesNotContain(
            "namespace",
            Canonical("""{"type":"record","name":"X","namespace":"acme","fields":[]}"""),
            StringComparison.Ordinal);

    [Fact]
    public void SelfReference_ResolvesToItsOwnFullname() =>
        // The linked-list case: a record referencing itself before its own definition
        // completes.
        Assert.Equal(
            """{"name":"acme.LongList","type":"record","fields":[{"name":"value","type":"long"},{"name":"next","type":["null","acme.LongList"]}]}""",
            Canonical(
                """
                {
                  "type": "record", "name": "LongList", "namespace": "acme",
                  "fields": [
                    { "name": "value", "type": "long" },
                    { "name": "next", "type": ["null", "LongList"] }
                  ]
                }
                """));

    [Fact]
    public void StructuralKeys_ComeFirstInSpecOrder_NotAlphabetically() =>
        Assert.Equal(
            """{"name":"acme.Fx","type":"fixed","size":16}""",
            Canonical("""{"size":16,"namespace":"acme","name":"Fx","type":"fixed"}"""));

    [Fact]
    public void NothingIsStripped()
    {
        // 'default' and 'aliases' decide how data resolves, so discarding them - which is what
        // Parsing Canonical Form does - would leave the registry serving a schema that cannot
        // read older data. Since decision #17 `doc` survives too: an id is a claim about a
        // DOCUMENT, not about the subset of it this build considers meaningful, and once one
        // attribute is dropped for being presentational every later one needs the same
        // judgement made about it by five SDKs identically.
        var canonical = Canonical(
            """
            {
              "type": "record", "name": "X", "namespace": "acme", "doc": "docs",
              "aliases": ["OldX"],
              "fields": [
                { "name": "a", "type": "string", "doc": "field doc", "default": "d",
                  "order": "ascending", "aliases": ["oldA"] }
              ]
            }
            """);

        Assert.Equal(
            """{"name":"acme.X","type":"record","fields":[{"name":"a","type":"string","aliases":["oldA"],"default":"d","doc":"field doc","order":"ascending"}],"aliases":["acme.OldX"],"doc":"docs"}""",
            canonical);
    }

    [Fact]
    public void NamedTypeAliases_ResolveToFullnames() =>
        // Otherwise 'Old' and 'acme.Old' would canonicalise differently while naming the same
        // type, and alias matching during resolution would miss.
        Assert.Contains(
            "\"aliases\":[\"acme.Old\"]",
            Canonical("""{"type":"record","name":"X","namespace":"acme","aliases":["Old"],"fields":[]}"""),
            StringComparison.Ordinal);

    [Fact]
    public void FieldAliases_AreLeftUnqualified() =>
        // A field alias is a plain field name, not a fullname: fields do not live in a
        // namespace.
        Assert.Contains(
            "\"aliases\":[\"oldA\"]",
            Canonical(
                """
                {"type":"record","name":"X","namespace":"acme",
                 "fields":[{"name":"a","type":"string","aliases":["oldA"]}]}
                """),
            StringComparison.Ordinal);

    [Fact]
    public void ADefaultObject_HasItsKeysSorted() =>
        Assert.Contains(
            "\"default\":{\"a\":1,\"z\":2}",
            Canonical(
                """
                {"type":"record","name":"X",
                 "fields":[{"name":"f","type":{"type":"map","values":"int"},"default":{"z":2,"a":1}}]}
                """),
            StringComparison.Ordinal);

    [Fact]
    public void ADefaultArray_KeepsItsOrder() =>
        // A default for an array field is data, and its order is meaningful.
        Assert.Contains(
            "\"default\":[3,1,2]",
            Canonical(
                """
                {"type":"record","name":"X",
                 "fields":[{"name":"f","type":{"type":"array","items":"int"},"default":[3,1,2]}]}
                """),
            StringComparison.Ordinal);

    [Fact]
    public void EnumSymbols_PreserveOrder() =>
        Assert.Equal(
            """{"name":"acme.Suit","type":"enum","symbols":["SPADES","HEARTS","CLUBS"]}""",
            Canonical(
                """{"type":"enum","name":"Suit","namespace":"acme","symbols":["SPADES","HEARTS","CLUBS"]}"""));

    [Fact]
    public void EnumDefault_Survives() =>
        // Avro 1.9's enum default absorbs unknown symbols on read. PCF discards it; the
        // compatibility engine needs it.
        Assert.Equal(
            """{"name":"Suit","type":"enum","symbols":["SPADES"],"default":"SPADES"}""",
            Canonical("""{"type":"enum","name":"Suit","symbols":["SPADES"],"default":"SPADES"}"""));

    [Fact]
    public void Array_CanonicalisesItsItemsSchema() =>
        Assert.Equal("""{"type":"array","items":"string"}""", Canonical("""{"type":"array","items":"string"}"""));

    [Fact]
    public void Map_CanonicalisesItsValuesSchema() =>
        Assert.Equal("""{"type":"map","values":"long"}""", Canonical("""{"type":"map","values":"long"}"""));

    [Fact]
    public void Union_PreservesBranchOrder() =>
        Assert.Equal("""["null","string","int"]""", Canonical("""["null", "string", "int"]"""));

    [Fact]
    public void Error_IsCanonicalisedAsRecord() =>
        // 'error' differs from 'record' only in being usable as a protocol error; nothing about
        // the data changes.
        Assert.Equal(
            """{"name":"Boom","type":"record","fields":[]}""",
            Canonical("""{"type":"error","name":"Boom","fields":[]}"""));

    [Fact]
    public void Canonicalisation_IsIdempotent()
    {
        var once = Canonical(
            """
            {"type":"record","name":"X","namespace":"acme","doc":"d","aliases":["Old"],
             "fields":[{"name":"a","type":"string","default":"x"}]}
            """);

        Assert.Equal(once, Canonical(once));
    }

    [Fact]
    public void MatchesParsingCanonicalForm_WhenNothingSemanticWouldBeStripped()
    {
        // The deviation is confined to what PCF would lose. A schema using none of the
        // attributes PCF strips canonicalises to exactly PCF, which is what keeps this form
        // recognisable to anyone who knows Avro.
        //
        // `doc` is now one of those attributes (decision #17), so the input here carries none.
        // A schema WITH doc deliberately no longer matches PCF -- see the test below.
        const string pcf =
            """{"name":"acme.User","type":"record","fields":[{"name":"id","type":"string"},{"name":"age","type":"int"}]}""";

        Assert.Equal(
            pcf,
            Canonical(
                """
                {"type":"record","name":"User","namespace":"acme",
                 "fields":[{"name":"id","type":"string"},{"name":"age","type":"int"}]}
                """));
    }

    [Fact]
    public void DivergesFromParsingCanonicalForm_WhenTheSchemaCarriesDoc()
    {
        // The cost of decision #17, pinned so nobody rediscovers it as a bug. PCF strips `doc`;
        // this form keeps it, so anyone diffing Concordat's canonical text against another
        // tool's PCF output sees a difference here. Schema IDS were never comparable across
        // tools anyway -- 128-bit truncated SHA-256 over a versioned preimage versus Avro's
        // 64-bit CRC -- so nothing that interoperates is affected.
        Assert.Equal(
            """{"name":"acme.User","type":"record","fields":[{"name":"id","type":"string"}],"doc":"a user"}""",
            Canonical(
                """
                {"type":"record","name":"User","namespace":"acme","doc":"a user",
                 "fields":[{"name":"id","type":"string"}]}
                """));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"type\": ")]
    [InlineData("""{"type":"record","name":"X"}""")]
    [InlineData("""{"type":"fixed","name":"X","size":-1}""")]
    [InlineData("""{"type":"bogus"}""")]
    [InlineData("""{"name":"X"}""")]
    public void MalformedSchema_IsRejected(string body)
    {
        var result = Canonicalizer.Canonicalize(body);

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.SchemaMalformed, result.Error!.Code);
    }

    [Fact]
    public void DuplicateKeys_AreRejected()
    {
        var result = Canonicalizer.Canonicalize("""{"type":"long","type":"string"}""");

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.SchemaMalformed, result.Error!.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyBody_IsRejected(string? body)
    {
        var result = Canonicalizer.Canonicalize(body);

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.SchemaBodyEmpty, result.Error!.Code);
    }

    [Fact]
    public void AnOversizedBody_IsRejectedBeforeParsing()
    {
        // Padding lives in a "doc" value, not whitespace, so this is valid Avro JSON well past
        // Schema.MaxBodyBytes -- the point is that the size check runs before parsing ever sees
        // it, not that the document is otherwise unparseable.
        var body = $$"""
            {"type":"record","name":"R","fields":[
              {"name":"a","type":"string","doc":"{{new string('a', Schema.MaxBodyBytes + 1)}}"}
            ]}
            """;

        var result = Canonicalizer.Canonicalize(body);

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.SchemaTooLarge, result.Error!.Code);
    }
}
