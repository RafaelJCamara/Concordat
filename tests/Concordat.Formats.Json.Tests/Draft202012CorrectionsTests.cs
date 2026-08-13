using System.Text.Json;
using Concordat.Formats.Json;

namespace Concordat.Formats.Json.Tests;

/// <summary>
/// Direct tests for the corrections that reconcile NJsonSchema with draft 2020-12.
/// </summary>
/// <remarks>
/// The conformance corpus already proves the <em>outcomes</em> — a boolean subschema validates,
/// an emoji satisfies <c>maxLength: 1</c>. These test the machinery underneath, because two of
/// its rules are the kind that pass every end-to-end case while being subtly wrong in a way
/// that only shows up on someone else's schema.
/// </remarks>
public class Draft202012CorrectionsTests
{
    // ------------------------------------------------------------ boolean subschemas

    [Fact]
    public void ABooleanSubschemaBecomesItsObjectEquivalent()
    {
        var rewritten = Draft202012Corrections.RewriteBooleanSubschemas(
            """{"properties":{"anything":true,"nothing":false}}""");

        Assert.Equal(
            """{"properties":{"anything":{},"nothing":{"not":{}}}}""",
            Compact(rewritten));
    }

    [Fact]
    public void ABooleanKEYWORDIsLeftAlone()
    {
        // The rule the whole rewrite turns on, and the one that would do real damage if it
        // were "rewrite any boolean". 'uniqueItems' is a keyword whose value happens to be a
        // boolean; turning it into {} would silently drop the constraint.
        const string schema = """{"type":"array","uniqueItems":true}""";

        Assert.Equal(schema, Draft202012Corrections.RewriteBooleanSubschemas(schema));
    }

    [Theory]
    [InlineData("""{"type":"string","deprecated":true}""")]
    [InlineData("""{"type":"number","exclusiveMinimum":0}""")]
    [InlineData("""{"type":"object","additionalProperties":{"type":"string"}}""")]
    public void SchemasWithNoBooleanSubschemaAreReturnedUnchanged(string schema) =>
        // Returned by reference-equality of content: no reserialisation, so a schema that
        // needs no correction cannot be perturbed by one.
        Assert.Equal(schema, Draft202012Corrections.RewriteBooleanSubschemas(schema));

    [Fact]
    public void AdditionalPropertiesFalseIsRewritten_BecauseItIsASchemaPosition() =>
        // 'additionalProperties' takes a schema, so false there is the reject-everything
        // schema rather than a flag -- even though it reads exactly like a flag.
        Assert.Equal(
            """{"additionalProperties":{"not":{}}}""",
            Compact(Draft202012Corrections.RewriteBooleanSubschemas(
                """{"additionalProperties":false}""")));

    [Fact]
    public void BooleansInsideApplicatorArraysAreRewritten() =>
        Assert.Equal(
            """{"allOf":[{},{"not":{}}]}""",
            Compact(Draft202012Corrections.RewriteBooleanSubschemas("""{"allOf":[true,false]}""")));

    [Fact]
    public void BooleansNestedDeeplyAreFound() =>
        Assert.Equal(
            """{"properties":{"a":{"items":{}}}}""",
            Compact(Draft202012Corrections.RewriteBooleanSubschemas(
                """{"properties":{"a":{"items":true}}}""")));

    [Fact]
    public void MalformedJsonIsReturnedUnchanged() =>
        // The canonicaliser has already refused it with a better message; failing here as
        // well would only make the real one harder to find.
        Assert.Equal("not json", Draft202012Corrections.RewriteBooleanSubschemas("not json"));

    // ------------------------------------------------------------------ string length

    [Theory]
    [InlineData("a", 1, true)]
    [InlineData("ab", 1, false)]
    [InlineData("\U0001D11E", 1, true)]     // treble clef: 1 character, 2 UTF-16 units
    [InlineData("\U0001F600\U0001F600", 2, true)]
    [InlineData("\U0001F600\U0001F600", 1, false)]
    public void MaxLengthCountsCharactersNotUtf16Units(string value, int bound, bool satisfied) =>
        Assert.Equal(satisfied, Draft202012Corrections.LengthSatisfiedByCodePoints(value, bound, true));

    [Theory]
    [InlineData("\U0001D11E", 2, false)]    // one character cannot satisfy minLength 2
    [InlineData("\U0001D11E", 1, true)]
    [InlineData("ab", 2, true)]
    public void MinLengthCountsCharactersToo(string value, int bound, bool satisfied) =>
        Assert.Equal(satisfied, Draft202012Corrections.LengthSatisfiedByCodePoints(value, bound, false));

    [Fact]
    public void ALoneSurrogateCountsAsOneCharacter()
    {
        // Unpaired surrogates are not legal in well-formed text, but they reach this code from
        // a payload rather than from us, so it must not miscount or throw.
        var lone = "\ud834";

        Assert.True(Draft202012Corrections.LengthSatisfiedByCodePoints(lone, 1, true));
    }

    // ------------------------------------------------------------- missed violations

    private static List<(string Path, string Kind, string Message)> Missed(
        string schema, string payload) =>
        Draft202012Corrections.FindMissedViolations(schema, payload);

    [Fact]
    public void EnumMembershipIsTypeStrict()
    {
        var found = Missed("""{"properties":{"v":{"enum":[1]}}}""", """{"v":"1"}""");

        Assert.Equal("EnumNotMatched", Assert.Single(found).Kind);
        Assert.Equal("#/v", found[0].Path);
    }

    [Fact]
    public void EnumAcceptsAMatchingMember() =>
        Assert.Empty(Missed("""{"properties":{"v":{"enum":[1,"a"]}}}""", """{"v":"a"}"""));

    [Fact]
    public void EnumComparesNumbersMathematically() =>
        // 1 and 1.0 are the same JSON value even though canonicalisation keeps the literals
        // apart, which is the sort of asymmetry that invites a wrong equality helper.
        Assert.Empty(Missed("""{"properties":{"v":{"enum":[1]}}}""", """{"v":1.0}"""));

    [Fact]
    public void ConstIsCheckedTheSameWay()
    {
        Assert.Single(Missed("""{"properties":{"v":{"const":"x"}}}""", """{"v":"y"}"""));
        Assert.Empty(Missed("""{"properties":{"v":{"const":"x"}}}""", """{"v":"x"}"""));
    }

    [Fact]
    public void UniqueItemsComparesValuesNotText()
    {
        var found = Missed(
            """{"properties":{"xs":{"uniqueItems":true}}}""",
            """{"xs":[{"a":1,"b":2},{"b":2,"a":1}]}""");

        Assert.Equal("ArrayItemNotUnique", Assert.Single(found).Kind);
    }

    [Fact]
    public void UniqueItemsTreatsEqualNumbersAsDuplicates() =>
        Assert.Single(Missed(
            """{"properties":{"xs":{"uniqueItems":true}}}""", """{"xs":[1,1.0]}"""));

    [Fact]
    public void UniqueItemsAcceptsGenuinelyDistinctItems() =>
        Assert.Empty(Missed(
            """{"properties":{"xs":{"uniqueItems":true}}}""", """{"xs":[{"a":1},{"a":2}]}"""));

    [Fact]
    public void UniqueItemsIgnoresNonArrays() =>
        // The keyword only applies to arrays; applying it to anything else would invent a
        // violation the specification does not describe.
        Assert.Empty(Missed(
            """{"properties":{"xs":{"uniqueItems":true}}}""", """{"xs":"not an array"}"""));

    [Fact]
    public void TheWalkDescendsThroughArrayItems()
    {
        var found = Missed(
            """{"properties":{"xs":{"items":{"enum":["a"]}}}}""",
            """{"xs":["a","b"]}""");

        Assert.Equal("#/xs/1", Assert.Single(found).Path);
    }

    [Fact]
    public void TheWalkHandlesPrefixItemsPositionally()
    {
        var found = Missed(
            """{"properties":{"xs":{"prefixItems":[{"enum":["a"]},{"enum":["b"]}]}}}""",
            """{"xs":["a","WRONG"]}""");

        Assert.Equal("#/xs/1", Assert.Single(found).Path);
    }

    [Fact]
    public void TheWalkStopsAtApplicatorKeywords()
    {
        // A documented boundary, not an oversight. Descending into oneOf would mean deciding
        // which branch was meant to apply, which is the whole of validation -- and these
        // keywords are already reported by JsonSchemaPortabilityChecker as outside the
        // interoperable subset, so the boundary here is the same line as the warning there.
        Assert.Empty(Missed(
            """{"oneOf":[{"properties":{"v":{"enum":[1]}}}]}""", """{"v":"1"}"""));
    }

    [Fact]
    public void AnAbsentPropertyIsNotAViolation() =>
        // enum constrains a value that is present; requiredness is a separate keyword and
        // NJsonSchema already enforces it correctly.
        Assert.Empty(Missed("""{"properties":{"v":{"enum":[1]}}}""", """{}"""));

    [Fact]
    public void MalformedInputYieldsNothing()
    {
        Assert.Empty(Missed("not json", """{"v":1}"""));
        Assert.Empty(Missed("""{"properties":{}}""", "not json"));
    }

    [Fact]
    public void PathsAreJsonPointerEscaped()
    {
        var found = Missed(
            """{"properties":{"a/b":{"enum":[1]}}}""", """{"a/b":"x"}""");

        // RFC 6901: '/' inside a member name is '~1', or the pointer would read as two segments.
        Assert.Equal("#/a~1b", Assert.Single(found).Path);
    }

    private static string Compact(string json)
    {
        using var document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(document.RootElement);
    }
}
