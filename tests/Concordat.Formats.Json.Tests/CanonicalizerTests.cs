using Concordat.Domain.Registry;
using Concordat.Domain.Results;
using Concordat.Formats.Json;

namespace Concordat.Formats.Json.Tests;

public class CanonicalizerTests
{
    private static readonly JsonSchemaCanonicalizer Canonicalizer = new();

    private static string Canonical(string body)
    {
        var result = Canonicalizer.Canonicalize(body);
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value;
    }

    [Fact]
    public void Handles_TheJsonFormat() =>
        Assert.Equal(SchemaFormat.Json, Canonicalizer.Format);

    [Fact]
    public void Whitespace_IsRemoved() =>
        Assert.Equal(
            """{"type":"object"}""",
            Canonical("  {\n  \"type\"  :  \"object\"\n }  "));

    [Fact]
    public void ObjectKeys_AreSortedOrdinally() =>
        Assert.Equal(
            """{"a":1,"b":2,"z":3}""",
            Canonical("""{"z":3,"a":1,"b":2}"""));

    [Fact]
    public void NestedObjectKeys_AreAlsoSorted() =>
        Assert.Equal(
            """{"outer":{"a":1,"z":2}}""",
            Canonical("""{"outer":{"z":2,"a":1}}"""));

    [Fact]
    public void ArrayOrder_IsPreserved()
    {
        // Significant in JSON Schema: prefixItems is positional, and enum order is observable.
        Assert.Equal("""{"enum":[3,1,2]}""", Canonical("""{"enum":[3,1,2]}"""));
    }

    [Fact]
    public void StringEscaping_IsNormalised()
    {
        // A is 'A'. Two spellings of one string must collapse.
        Assert.Equal(Canonical("""{"t":"A"}"""), Canonical("""{"t":"A"}"""));
    }

    [Fact]
    public void NonAsciiAndMarkup_AreNotOverEscaped()
    {
        // The default encoder escapes '<' and non-ASCII for HTML safety, which would make the
        // canonical form depend on where the text might later be displayed.
        Assert.Equal("""{"t":"<é>"}""", Canonical("""{"t":"<é>"}"""));
    }

    [Fact]
    public void Canonicalisation_IsIdempotent()
    {
        var once = Canonical("""{ "z": 1, "a": { "y": 2, "b": 3 } }""");

        Assert.Equal(once, Canonical(once));
    }

    [Fact]
    public void NumberLiterals_ArePreservedVerbatim()
    {
        // Deliberate deviation from RFC 8785, documented on the canonicaliser: routing numbers
        // through an ECMAScript double loses precision on large integers, which would silently
        // corrupt a "maximum". A missed dedup is the safer failure.
        Assert.Equal("""{"n":1.0}""", Canonical("""{"n":1.0}"""));
        Assert.NotEqual(Canonical("""{"n":1}"""), Canonical("""{"n":1.0}"""));
    }

    [Fact]
    public void LargeIntegers_SurviveExactly()
    {
        const string huge = """{"maximum":123456789012345678901234567890}""";

        Assert.Equal(huge, Canonical(huge));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"unterminated\": ")]
    [InlineData("{'single':'quotes'}")]
    [InlineData("{\"trailing\":1,}")]
    [InlineData("{\"c\":1} // comment")]
    public void MalformedJson_IsRejected(string body)
    {
        var result = Canonicalizer.Canonicalize(body);

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.SchemaMalformed, result.Error!.Code);
    }

    [Fact]
    public void DuplicateKeys_AreRejected()
    {
        // Parsers disagree on which value wins, so the document has no single meaning.
        var result = Canonicalizer.Canonicalize("""{"a":1,"a":2}""");

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.SchemaMalformed, result.Error!.Code);
        Assert.Contains("'a'", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateKeys_AreRejectedWhenNested()
    {
        var result = Canonicalizer.Canonicalize("""{"outer":{"dup":1,"dup":2}}""");

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
    public void AllOfNullBooleanAndNestedArrays_RoundTrip()
    {
        const string body = """{"a":[{"b":null},[true,false]],"c":{}}""";

        Assert.Equal(body, Canonical(body));
    }
}
