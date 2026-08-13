using Concordat.Domain.Results;
using Concordat.Formats.Json;

namespace Concordat.Formats.Json.Tests;

public class ReferenceExtractionTests
{
    private static readonly JsonSchemaCanonicalizer Canonicalizer = new();
    private static readonly JsonSchemaReferenceExtractor Extractor = new();

    private static IReadOnlyList<Domain.Registry.Reference> Extract(string body)
    {
        var canonical = Canonicalizer.Canonicalize(body);
        Assert.True(canonical.IsSuccess, canonical.Error?.Message);

        var result = Extractor.Extract(canonical.Value);
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value;
    }

    [Fact]
    public void ASchemaWithNoReferences_YieldsNone() =>
        Assert.Empty(Extract("""{"type":"object","properties":{"id":{"type":"string"}}}"""));

    [Fact]
    public void AConcordatRef_BecomesAnEdge()
    {
        var references = Extract("""
            {"properties":{"addr":{"$ref":"concordat://prod/acme.Address/2"}}}
            """);

        var reference = Assert.Single(references);
        Assert.Equal("concordat://prod/acme.Address/2", reference.Name);
        Assert.Equal("acme.Address", reference.Subject.Value);
        Assert.Equal(2, reference.Version);
    }

    [Fact]
    public void LocalAndHttpRefs_AreIgnored()
    {
        // Those are the validator's business. Claiming them would create edges to subjects
        // that do not exist.
        var references = Extract("""
            {"properties":{"a":{"$ref":"#/$defs/Address"},"b":{"$ref":"https://example.com/s.json"}}}
            """);

        Assert.Empty(references);
    }

    [Fact]
    public void TheSameTargetReferencedTwice_IsOneEdge()
    {
        // The edge set is a set. Two edges would also collide on Reference.Name, which
        // Schema.Create rejects.
        var references = Extract("""
            {"properties":{
              "billing":{"$ref":"concordat://prod/acme.Address/1"},
              "shipping":{"$ref":"concordat://prod/acme.Address/1"}}}
            """);

        Assert.Single(references);
    }

    [Fact]
    public void RefsAreFoundAtAnyDepthIncludingInsideArrays()
    {
        var references = Extract("""
            {"allOf":[{"properties":{"deep":{"items":{"$ref":"concordat://prod/acme.Item/1"}}}}]}
            """);

        Assert.Equal("acme.Item", Assert.Single(references).Subject.Value);
    }

    [Fact]
    public void ReferencesAreOrderedByName()
    {
        var references = Extract("""
            {"properties":{
              "z":{"$ref":"concordat://prod/acme.Zeta/1"},
              "a":{"$ref":"concordat://prod/acme.Alpha/1"}}}
            """);

        Assert.Equal(
            ["concordat://prod/acme.Alpha/1", "concordat://prod/acme.Zeta/1"],
            references.Select(r => r.Name));
    }

    [Fact]
    public void AMalformedConcordatRef_IsReportedNotSkipped()
    {
        // A typo in our own scheme must fail loudly, or the schema registers with no edges and
        // fails to resolve much later.
        var canonical = Canonicalizer.Canonicalize("""
            {"properties":{"a":{"$ref":"concordat://prod/acme.Address"}}}
            """).Value;

        var result = Extractor.Extract(canonical);

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.ReferenceInvalid, result.Error!.Code);
    }
}

public class UriNormalisationTests
{
    private static readonly JsonSchemaCanonicalizer Canonicalizer = new();

    private static string Canonical(string body)
    {
        var result = Canonicalizer.Canonicalize(body);
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value;
    }

    [Fact]
    public void RefCasing_IsNormalised() =>
        Assert.Equal(
            Canonical("""{"$ref":"concordat://prod/acme.Common/1"}"""),
            Canonical("""{"$ref":"CONCORDAT://Prod/acme.Common/1"}"""));

    [Fact]
    public void IdCasingAndDefaultPort_AreNormalised() =>
        Assert.Equal(
            Canonical("""{"$id":"https://example.com/schema"}"""),
            Canonical("""{"$id":"HTTPS://Example.COM:443/schema"}"""));

    [Fact]
    public void DotSegments_AreResolved() =>
        Assert.Equal(
            Canonical("""{"$id":"https://example.com/b"}"""),
            Canonical("""{"$id":"https://example.com/a/../b"}"""));

    [Fact]
    public void FragmentOnlyRefs_AreLeftAlone()
    {
        // Resolving these needs a base document the registry does not have.
        const string body = """{"$ref":"#/$defs/Address"}""";

        Assert.Equal(body, Canonical(body));
    }

    [Fact]
    public void OtherStringKeywords_AreNotTouched()
    {
        // Only $id and $ref. Normalising any string that happens to parse as a URI would
        // rewrite ordinary schema content such as a description or a const value.
        const string body = """{"const":"HTTPS://Example.COM/x","description":"HTTPS://Example.COM/x"}""";

        Assert.Equal(body, Canonical(body));
    }
}
