using Concordat.Domain.Results;
using Concordat.Formats.Json;

namespace Concordat.Formats.Json.Tests;

public class BundlerTests
{
    private static readonly JsonSchemaBundler Bundler = new();
    private static readonly JsonSchemaCanonicalizer Canonicalizer = new();

    private static string Canonical(string body) => Canonicalizer.Canonicalize(body).Value;

    private static string Bundle(string body, params (string Ref, string Body)[] resolved)
    {
        var map = resolved.ToDictionary(r => r.Ref, r => Canonical(r.Body), StringComparer.Ordinal);
        var result = Bundler.Bundle(Canonical(body), map);
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value;
    }

    [Fact]
    public void ASchemaWithNoReferences_IsUnchanged()
    {
        const string body = """{"properties":{"id":{"type":"string"}},"type":"object"}""";

        Assert.Equal(body, Bundle(body));
    }

    [Fact]
    public void AReferenceIsInlinedAndRewrittenToALocalPointer()
    {
        var bundled = Bundle(
            """{"properties":{"addr":{"$ref":"concordat://prod/acme.Address/1"}}}""",
            ("concordat://prod/acme.Address/1", """{"type":"object","properties":{"city":{"type":"string"}}}"""));

        Assert.Contains("""{"$ref":"#/$defs/acme.Address__1"}""", bundled, StringComparison.Ordinal);
        Assert.Contains("\"$defs\":{\"acme.Address__1\":", bundled, StringComparison.Ordinal);
        Assert.DoesNotContain("concordat://", bundled, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameTargetReferencedTwice_IsInlinedOnce()
    {
        var bundled = Bundle(
            """
            {"properties":{
              "billing":{"$ref":"concordat://prod/acme.Address/1"},
              "shipping":{"$ref":"concordat://prod/acme.Address/1"}}}
            """,
            ("concordat://prod/acme.Address/1", """{"type":"object"}"""));

        var occurrences = bundled.Split("\"acme.Address__1\":").Length - 1;
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void LocalAndHttpRefsAreLeftAlone()
    {
        const string body = """{"properties":{"a":{"$ref":"#/$defs/Local"},"b":{"$ref":"https://example.com/s.json"}}}""";

        var bundled = Bundle(body);

        Assert.Contains("#/$defs/Local", bundled, StringComparison.Ordinal);
        Assert.Contains("https://example.com/s.json", bundled, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingReference_IsAFailureNotASilentlyBrokenBundle()
    {
        // A bundle that still contains a concordat:// ref is not self-contained, and a client
        // would only discover that when validation failed at consume time.
        var result = Bundler.Bundle(
            Canonical("""{"properties":{"a":{"$ref":"concordat://prod/acme.Missing/1"}}}"""),
            new Dictionary<string, string>(StringComparer.Ordinal));

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.ReferenceInvalid, result.Error!.Code);
        Assert.Contains("acme.Missing", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DefsAreWrittenAtTheRootOnlyAndOrderedByKey()
    {
        var bundled = Bundle(
            """
            {"properties":{
              "z":{"$ref":"concordat://prod/acme.Zeta/1"},
              "a":{"$ref":"concordat://prod/acme.Alpha/1"}}}
            """,
            ("concordat://prod/acme.Zeta/1", """{"type":"object"}"""),
            ("concordat://prod/acme.Alpha/1", """{"type":"string"}"""));

        var alpha = bundled.IndexOf("acme.Alpha__1", StringComparison.Ordinal);
        var zeta = bundled.IndexOf("acme.Zeta__1", StringComparison.Ordinal);

        // Both appear as pointers before $defs; the assertion is that inside $defs the keys
        // are ordered, which keeps the bundle byte-stable across runs.
        var defs = bundled[bundled.IndexOf("\"$defs\"", StringComparison.Ordinal)..];
        Assert.True(
            defs.IndexOf("acme.Alpha__1", StringComparison.Ordinal)
            < defs.IndexOf("acme.Zeta__1", StringComparison.Ordinal));
        Assert.True(alpha > 0 && zeta > 0);
    }

    [Fact]
    public void Bundling_IsDeterministic()
    {
        // It has to be: a bundle served twice must be byte-identical or clients cannot cache it.
        var first = Bundle(
            """{"properties":{"a":{"$ref":"concordat://prod/acme.A/1"}}}""",
            ("concordat://prod/acme.A/1", """{"type":"object"}"""));
        var second = Bundle(
            """{"properties":{"a":{"$ref":"concordat://prod/acme.A/1"}}}""",
            ("concordat://prod/acme.A/1", """{"type":"object"}"""));

        Assert.Equal(first, second);
    }

    [Fact]
    public void DefinitionKey_RendersSubjectAndVersion() =>
        Assert.Equal(
            "acme.orders.OrderCreated__3",
            JsonSchemaBundler.DefinitionKey("concordat://prod/acme.orders.OrderCreated/3"));
}
