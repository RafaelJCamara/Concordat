using Concordat.Domain.Registry;
using Concordat.Domain.Results;
using Concordat.Formats.Avro;

namespace Concordat.Formats.Avro.Tests;

/// <summary>ADR-023: v1 refuses cross-subject Avro references rather than guessing a version.</summary>
public class ReferenceTests
{
    private static readonly AvroSchemaCanonicalizer Canonicalizer = new();
    private static readonly AvroSchemaReferenceExtractor Extractor = new();
    private static readonly AvroSchemaBundler Bundler = new();

    private static string Canonical(string body)
    {
        var result = Canonicalizer.Canonicalize(body);
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value;
    }

    [Fact]
    public void Handles_TheAvroFormat()
    {
        Assert.Equal(SchemaFormat.Avro, Extractor.Format);
        Assert.Equal(SchemaFormat.Avro, Bundler.Format);
    }

    [Fact]
    public void ASelfContainedSchema_HasNoReferences()
    {
        var result = Extractor.Extract(Canonical(
            """
            {"type":"record","name":"acme.Order","fields":[
              {"name":"id","type":"string"},
              {"name":"address","type":{"type":"record","name":"acme.Address",
                "fields":[{"name":"line1","type":"string"}]}}]}
            """));

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Empty(result.Value);
    }

    [Fact]
    public void ASelfReferentialRecord_IsNotAnExternalReference()
    {
        // The linked-list case resolves locally, which is what makes recursive schemas work.
        var result = Extractor.Extract(Canonical(
            """
            {"type":"record","name":"acme.Node","fields":[
              {"name":"value","type":"long"},
              {"name":"next","type":["null","acme.Node"]}]}
            """));

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Empty(result.Value);
    }

    [Fact]
    public void AReferenceToAnUndefinedType_IsRefused()
    {
        var result = Extractor.Extract(Canonical(
            """
            {"type":"record","name":"acme.Order","fields":[
              {"name":"address","type":"acme.common.Address"}]}
            """));

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.SchemaReferencesUnsupported, result.Error!.Code);
        Assert.Contains("acme.common.Address", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AReferenceInsideAUnion_IsAlsoRefused() =>
        // The common shape, and the one with no room to carry a version at all.
        Assert.Equal(
            ConcordatCodes.SchemaReferencesUnsupported,
            Extractor.Extract(Canonical(
                """
                {"type":"record","name":"acme.Order","fields":[
                  {"name":"address","type":["null","acme.common.Address"]}]}
                """)).Error!.Code);

    [Fact]
    public void AReferenceInsideAnArray_IsAlsoRefused() =>
        Assert.Equal(
            ConcordatCodes.SchemaReferencesUnsupported,
            Extractor.Extract(Canonical(
                """
                {"type":"record","name":"acme.Order","fields":[
                  {"name":"items","type":{"type":"array","items":"acme.common.Item"}}]}
                """)).Error!.Code);

    [Fact]
    public void TheRefusal_NamesEveryMissingType()
    {
        var result = Extractor.Extract(Canonical(
            """
            {"type":"record","name":"acme.Order","fields":[
              {"name":"a","type":"acme.common.A"},
              {"name":"b","type":"acme.common.B"}]}
            """));

        Assert.Contains("acme.common.A", result.Error!.Message, StringComparison.Ordinal);
        Assert.Contains("acme.common.B", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRefusal_SaysWhatToDoAboutIt() =>
        Assert.Contains(
            "Inline the definition",
            Extractor.Extract(Canonical(
                """{"type":"record","name":"acme.Order","fields":[{"name":"a","type":"acme.X"}]}"""))
                .Error!.Message,
            StringComparison.Ordinal);

    [Fact]
    public void EmptyBody_IsRejected()
    {
        Assert.Equal(ConcordatCodes.SchemaBodyEmpty, Extractor.Extract("").Error!.Code);
        Assert.Equal(ConcordatCodes.SchemaBodyEmpty, Bundler.Bundle("", new Dictionary<string, string>()).Error!.Code);
    }

    [Fact]
    public void Bundling_ReturnsTheDocumentUnchanged()
    {
        // Every registered Avro schema is self-contained under ADR-023, so there is nothing to
        // inline and the bundle is the document.
        var canonical = Canonical("""{"type":"record","name":"acme.Order","fields":[]}""");

        var result = Bundler.Bundle(canonical, new Dictionary<string, string>());

        Assert.True(result.IsSuccess);
        Assert.Equal(canonical, result.Value);
    }
}
