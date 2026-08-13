using Concordat.Domain.Registry;
using Concordat.Formats.Abstractions;
using Concordat.Formats.Avro;

namespace Concordat.Formats.Avro.Tests;

/// <summary>The ADR-015 acceptance criteria, stated as tests, for the Avro canonicaliser.</summary>
public class SchemaIdTests
{
    private static readonly AvroSchemaCanonicalizer Canonicalizer = new();

    private static SchemaId IdOf(string body) =>
        SchemaIdComputer.Compute(SchemaFormat.Avro, Canonicalizer.Canonicalize(body).Value);

    [Fact]
    public void SchemasDifferingOnlyInWhitespace_ShareAnId() =>
        Assert.Equal(
            IdOf("""{"type":"record","name":"X","fields":[]}"""),
            IdOf(" {  \"type\" : \"record\" , \"name\": \"X\" , \"fields\" : [ ] }  "));

    [Fact]
    public void SchemasDifferingOnlyInAttributeOrder_ShareAnId() =>
        Assert.Equal(
            IdOf("""{"type":"record","name":"X","fields":[]}"""),
            IdOf("""{"fields":[],"name":"X","type":"record"}"""));

    [Fact]
    public void SchemasDifferingOnlyInDoc_ShareAnId() =>
        // 'doc' is the only attribute dropped, so a comment edit does not mint a new id.
        Assert.Equal(
            IdOf("""{"type":"record","name":"X","fields":[]}"""),
            IdOf("""{"type":"record","name":"X","doc":"some docs","fields":[]}"""));

    [Fact]
    public void NameAndNamespaceSplit_SharesAnIdWithTheEquivalentFullname() =>
        Assert.Equal(
            IdOf("""{"type":"record","name":"acme.orders.X","fields":[]}"""),
            IdOf("""{"type":"record","name":"X","namespace":"acme.orders","fields":[]}"""));

    [Fact]
    public void SchemasDifferingOnlyInAFieldDefault_HaveDifferentIds() =>
        // The inversion of Parsing Canonical Form, and the point of DECISIONS-PENDING #17.
        // These two schemas encode identical bytes but resolve differently: only one of them
        // can read data that omits 'a'. Content addressing has to mean "same id, same
        // meaning", so they cannot share an id.
        Assert.NotEqual(
            IdOf("""{"type":"record","name":"X","fields":[{"name":"a","type":"string"}]}"""),
            IdOf(
                """{"type":"record","name":"X","fields":[{"name":"a","type":"string","default":"d"}]}"""));

    [Fact]
    public void SchemasDifferingOnlyInAliases_HaveDifferentIds() =>
        // Aliases decide whether a rename resolves, so they are semantic too.
        Assert.NotEqual(
            IdOf("""{"type":"record","name":"acme.X","fields":[]}"""),
            IdOf("""{"type":"record","name":"acme.X","aliases":["acme.Old"],"fields":[]}"""));

    [Fact]
    public void SchemasDifferingOnlyInLogicalType_HaveDifferentIds() =>
        Assert.NotEqual(
            IdOf("""{"type":"record","name":"X","fields":[{"name":"at","type":"long"}]}"""),
            IdOf(
                """
                {"type":"record","name":"X","fields":[
                  {"name":"at","type":{"type":"long","logicalType":"timestamp-millis"}}]}
                """));

    [Fact]
    public void DifferentFieldOrder_HasADifferentId() =>
        // Field order is the wire order: it is never insignificant.
        Assert.NotEqual(
            IdOf(
                """{"type":"record","name":"X","fields":[{"name":"a","type":"int"},{"name":"b","type":"int"}]}"""),
            IdOf(
                """{"type":"record","name":"X","fields":[{"name":"b","type":"int"},{"name":"a","type":"int"}]}"""));

    [Fact]
    public void DifferentEnumSymbolOrder_HasADifferentId() =>
        // Symbol order is the symbol's ordinal on the wire.
        Assert.NotEqual(
            IdOf("""{"type":"enum","name":"X","symbols":["A","B"]}"""),
            IdOf("""{"type":"enum","name":"X","symbols":["B","A"]}"""));

    [Fact]
    public void SameBodyDifferentFormat_HasADifferentIdThanJson() =>
        Assert.NotEqual(
            SchemaIdComputer.Compute(SchemaFormat.Avro, "\"string\""),
            SchemaIdComputer.Compute(SchemaFormat.Json, "\"string\""));

    [Fact]
    public void CanonicalisationIsAPrerequisite_RawBodiesMustNotBeHashed() =>
        // Guards the contract on SchemaIdComputer: callers canonicalise first. Two bodies that
        // are canonically identical still differ before canonicalising.
        Assert.NotEqual(
            SchemaIdComputer.Compute(SchemaFormat.Avro, """{"type":"record","name":"X","fields":[]}"""),
            SchemaIdComputer.Compute(SchemaFormat.Avro, """{ "type": "record", "name": "X" }"""));
}
