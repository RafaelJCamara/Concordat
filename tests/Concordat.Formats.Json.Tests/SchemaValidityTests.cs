using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Concordat.Formats.Abstractions;
using Concordat.Formats.Json;

namespace Concordat.Formats.Json.Tests;

/// <summary>
/// What "a valid JSON Schema" means to the registry (ADR-039): the draft 2020-12 metaschema's
/// assertions, read with <c>format</c> as an annotation, plus a <c>$ref</c> into the same
/// document that points at nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>The two lists are the specification.</b> The accepted list matters as much as the refused
/// one: this check refuses documents, so a false positive stops somebody registering a schema
/// they were entitled to, which is the one failure worse than the gap it closes. Every entry in
/// the accepted list is a document that looks as if it might be refused, with the reason it is
/// not.
/// </para>
/// <para>
/// Both lists were also run through <c>ajv</c> (the editor's engine) as an independent check, and
/// the one place the two disagree is named where it occurs.
/// </para>
/// </remarks>
public class SchemaValidityTests
{
    private static readonly JsonSchemaCanonicalizer Canonicalizer = new();
    private static readonly JsonSchemaValidityChecker Checker = new();

    private static IReadOnlyList<SchemaDefect> Check(string body)
    {
        var canonical = Canonicalizer.Canonicalize(body);
        Assert.True(canonical.IsSuccess, canonical.Error?.Message);

        return Checker.Check(canonical.Value);
    }

    [Fact]
    public void Handles_TheJsonFormat() =>
        Assert.Equal(Concordat.Domain.Registry.SchemaFormat.Json, Checker.Format);

    // ------------------------------------------------------------------ accepted

    [Theory]
    // The ordinary shapes.
    [InlineData("{}")]
    [InlineData("""{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}""")]
    [InlineData("""{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"string"}""")]
    [InlineData("""{"type":["string","null"]}""")]
    [InlineData("""{"enum":[1,"a",null,{"x":[]}]}""")]
    [InlineData("""{"const":null}""")]
    [InlineData("""{"required":[],"properties":{},"$defs":{}}""")]
    [InlineData("""{"dependentRequired":{"a":["b"]},"dependentSchemas":{"a":{"type":"object"}}}""")]
    // A boolean is a schema, at the root and anywhere a schema goes.
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("""{"properties":{"a":true,"b":false},"items":true,"additionalProperties":false}""")]
    // Unknown keywords and vocabularies are allowed by the metaschema; refusing them would refuse
    // every vendor extension and every keyword a later draft adds.
    [InlineData("""{"x-owner":"payments","x-pii":true,"x-nested":{"type":"bogus"}}""")]
    [InlineData("""{"$vocabulary":{"https://example.com/vocab/custom":false}}""")]
    // Draft-07 spellings the 2020-12 metaschema still lists, so that moving a schema forward does
    // not require rewriting every one of them at once.
    [InlineData("""{"definitions":{"a":{"type":"string"}},"dependencies":{"a":["b"],"c":{"required":["d"]}}}""")]
    // Data is not schema. These hold documents that look like schemas, under keywords whose
    // values are instances, and none of them may be read as one.
    [InlineData("""{"default":{"type":"bogus"},"examples":[{"minimum":"5"}],"const":{"type":"bogus"}}""")]
    [InlineData("""{"enum":[{"type":"bogus"},{"$ref":"#/nope"}]}""")]
    [InlineData("""{"default":{"$ref":"#/$defs/nope"},"examples":[{"$ref":"#/nope"}],"const":{"$ref":"#/nope"}}""")]
    // A property called after a keyword is a property, not that keyword.
    [InlineData("""{"properties":{"type":{"type":"string"},"minimum":{"type":"number"},"$ref":{"type":"string"}}}""")]
    [InlineData("""{"properties":{"required":{"type":"boolean"}},"required":["required"]}""")]
    // format is an annotation: the metaschema says uri and regex, and the specification does not
    // make a validator assert it by default. A pattern that is not a regular expression is a real
    // defect and is deliberately not this check's (ADR-039 says what it costs).
    [InlineData("""{"format":"not-a-known-format","pattern":"("}""")]
    [InlineData("""{"patternProperties":{"([":{"type":"string"}}}""")]
    [InlineData("""{"$ref":"not a uri reference at all"}""")]
    // A number is an integer when its fractional part is zero, however it is written.
    [InlineData("""{"minLength":1.0,"maxLength":1e1,"minItems":0,"maxItems":1E+2,"minProperties":10e-1}""")]
    [InlineData("""{"minimum":-1.5e3,"maximum":12345678901234567890,"multipleOf":0.5,"exclusiveMinimum":-0.0}""")]
    // Identifiers.
    [InlineData("""{"$id":"https://example.com/schemas/order","$anchor":"root","$comment":"x"}""")]
    [InlineData("""{"$id":"https://example.com/schemas/order#"}""")]
    [InlineData("""{"$defs":{"a":{"$dynamicAnchor":"node","$anchor":"A_b-c.d"}}}""")]
    // References that are not this check's to resolve: another document, another system, a name.
    [InlineData("""{"$ref":"https://example.com/schemas/other.json"}""")]
    [InlineData("""{"$ref":"other.json#/$defs/a"}""")]
    [InlineData("""{"$ref":"concordat://dev/acme.Customer/3"}""")]
    [InlineData("""{"$ref":"#addressAnchor"}""")]
    [InlineData("""{"$ref":"#addressAnchor/extra"}""")]
    [InlineData("""{"$dynamicRef":"#node","$defs":{"x":{"$dynamicAnchor":"node"}}}""")]
    // References that do resolve.
    [InlineData("""{"$ref":"#"}""")]
    [InlineData("""{"$defs":{"a":{"type":"integer"}},"$ref":"#/$defs/a"}""")]
    [InlineData("""{"$defs":{"a/b":{"type":"integer"},"c~d":{}},"allOf":[{"$ref":"#/$defs/a~1b"},{"$ref":"#/$defs/c~0d"}]}""")]
    [InlineData("""{"$defs":{"a b":{}},"$ref":"#/$defs/a%20b"}""")]
    [InlineData("""{"definitions":{"a":{}},"$ref":"#/definitions/a"}""")]
    [InlineData("""{"properties":{"a":{"type":"string"},"b":{"$ref":"#/properties/a"}}}""")]
    [InlineData("""{"allOf":[{"type":"object"},{"$ref":"#/allOf/0"}]}""")]
    [InlineData("""{"$ref":"#/$defs/a","$defs":{"a":{"$ref":"#/$defs/a"}}}""")]
    // An escape that is not RFC 6901's cannot be told from a pointer to a member that has it, so
    // it is left to the validator rather than refused on a guess.
    [InlineData("""{"$defs":{"x":{}},"$ref":"#/$defs/a~2b"}""")]
    // Once any other resource declares its own $id, '#/...' is relative to that resource and
    // this check cannot say where it points without resolving the whole identifier graph.
    [InlineData("""{"$defs":{"a":{"$id":"https://example.com/a","$ref":"#/nowhere"}}}""")]
    public void ADocumentThatIsAValidSchema_HasNoDefects(string body) =>
        Assert.Empty(Check(body));

    [Fact]
    public void AnotherDialectsDocument_IsJudgedByTheMetaschemaAlone()
    {
        // This check knows one metaschema. A draft-07 declaration is the portability checker's to
        // refuse, and that is what the evaluator does first; here the declaration is just a string.
        Assert.Empty(Check(
            """{"$schema":"http://json-schema.org/draft-07/schema#","type":"object"}"""));
    }

    // ------------------------------------------------------------------ refused

    [Theory]
    // The case that opened this: a type no validator has.
    [InlineData("""{"type":"bogus"}""", "#/type")]
    [InlineData("""{"type":["string","bogus"]}""", "#/type")]
    [InlineData("""{"type":[]}""", "#/type")]
    [InlineData("""{"type":["string","string"]}""", "#/type")]
    [InlineData("""{"type":5}""", "#/type")]
    [InlineData("""{"type":null}""", "#/type")]
    // A keyword's value of the wrong kind.
    [InlineData("""{"minimum":"5"}""", "#/minimum")]
    [InlineData("""{"maxLength":-1}""", "#/maxLength")]
    [InlineData("""{"maxLength":1.5}""", "#/maxLength")]
    [InlineData("""{"minItems":"1"}""", "#/minItems")]
    [InlineData("""{"multipleOf":0}""", "#/multipleOf")]
    [InlineData("""{"multipleOf":-2}""", "#/multipleOf")]
    [InlineData("""{"pattern":5}""", "#/pattern")]
    [InlineData("""{"uniqueItems":"yes"}""", "#/uniqueItems")]
    [InlineData("""{"title":5}""", "#/title")]
    [InlineData("""{"deprecated":"yes"}""", "#/deprecated")]
    [InlineData("""{"examples":5}""", "#/examples")]
    // Draft-03 and draft-04 spellings, the commonest way to hold a schema the metaschema refuses.
    [InlineData("""{"required":true}""", "#/required")]
    [InlineData("""{"properties":{"a":{"required":true}}}""", "#/properties/a/required")]
    [InlineData("""{"exclusiveMinimum":true,"minimum":1}""", "#/exclusiveMinimum")]
    // Draft-07's tuple form. With no $schema this is read as 2020-12, where it is not a schema.
    [InlineData("""{"items":[{"type":"string"},{"type":"integer"}]}""", "#/items")]
    [InlineData("""{"required":"a"}""", "#/required")]
    [InlineData("""{"required":["a","a"]}""", "#/required")]
    [InlineData("""{"required":[1]}""", "#/required/0")]
    [InlineData("""{"enum":"a"}""", "#/enum")]
    [InlineData("""{"dependentRequired":{"a":"b"}}""", "#/dependentRequired/a")]
    // A subschema that is not one, at every depth a subschema can sit.
    [InlineData("""{"properties":[]}""", "#/properties")]
    [InlineData("""{"properties":{"a":"string"}}""", "#/properties/a")]
    [InlineData("""{"properties":{"a":{"type":"bogus"}}}""", "#/properties/a/type")]
    [InlineData("""{"properties":{"a":{"items":{"properties":{"b":{"minimum":"x"}}}}}}""", "#/properties/a/items/properties/b/minimum")]
    [InlineData("""{"additionalProperties":"no"}""", "#/additionalProperties")]
    [InlineData("""{"allOf":[]}""", "#/allOf")]
    [InlineData("""{"allOf":{}}""", "#/allOf")]
    [InlineData("""{"anyOf":[{"type":"bogus"}]}""", "#/anyOf/0/type")]
    [InlineData("""{"oneOf":[{},5]}""", "#/oneOf/1")]
    [InlineData("""{"not":[]}""", "#/not")]
    [InlineData("""{"if":{"minimum":"x"},"then":{"type":"nope"}}""", "#/if/minimum")]
    [InlineData("""{"prefixItems":[{"type":"bogus"}]}""", "#/prefixItems/0/type")]
    [InlineData("""{"contains":5}""", "#/contains")]
    [InlineData("""{"dependentSchemas":{"a":5}}""", "#/dependentSchemas/a")]
    [InlineData("""{"patternProperties":{"^a":5}}""", "#/patternProperties/^a")]
    [InlineData("""{"$defs":[]}""", "#/$defs")]
    [InlineData("""{"$defs":{"a":5}}""", "#/$defs/a")]
    [InlineData("""{"$defs":{"a":{"$defs":{"b":{"type":"bogus"}}}}}""", "#/$defs/a/$defs/b/type")]
    [InlineData("""{"contentSchema":{"type":"bogus"}}""", "#/contentSchema/type")]
    [InlineData("""{"unevaluatedProperties":5}""", "#/unevaluatedProperties")]
    // The legacy spellings the metaschema keeps are checked too.
    [InlineData("""{"definitions":{"a":5}}""", "#/definitions/a")]
    [InlineData("""{"dependencies":{"a":5}}""", "#/dependencies/a")]
    // Identifiers.
    [InlineData("""{"$id":"https://example.com/x#fragment"}""", "#/$id")]
    [InlineData("""{"$anchor":"1abc"}""", "#/$anchor")]
    [InlineData("""{"$anchor":"has space"}""", "#/$anchor")]
    [InlineData("""{"$comment":5}""", "#/$comment")]
    [InlineData("""{"$ref":5}""", "#/$ref")]
    [InlineData("""{"$schema":5}""", "#/$schema")]
    [InlineData("""{"$vocabulary":{"https://example.com/v":"yes"}}""", "#/$vocabulary/https:~1~1example.com~1v")]
    // A document that is not a schema at all.
    [InlineData("[]", "#")]
    [InlineData("5", "#")]
    [InlineData("\"string\"", "#")]
    [InlineData("null", "#")]
    // A key that needs escaping in the pointer.
    [InlineData("""{"properties":{"a/b":{"type":"bogus"}}}""", "#/properties/a~1b/type")]
    [InlineData("""{"properties":{"a~b":{"type":"bogus"}}}""", "#/properties/a~0b/type")]
    public void ADocumentThatIsNotAValidSchema_IsReportedAtTheFirstDefect(string body, string path)
    {
        var defects = Check(body);

        Assert.NotEmpty(defects);
        Assert.Equal(path, defects[0].Path);
        Assert.Equal(SchemaDefectKinds.MetaschemaViolation, defects[0].Kind);
        Assert.False(string.IsNullOrWhiteSpace(defects[0].Message));
    }

    [Fact]
    public void AnAnchorEndingInANewline_IsNotAnAnchor()
    {
        // The metaschema's pattern ends in '$', which .NET also lets match before a final line
        // feed and ECMA-262 does not. A schema written for another validator must not slip through
        // on the difference between the two.
        var defect = Assert.Single(Check("{\"$anchor\":\"abc\\n\"}"));

        Assert.Equal("#/$anchor", defect.Path);
    }

    [Fact]
    public void ATypeNoValidatorHas_SaysWhichTypesThereAre()
    {
        var defect = Assert.Single(Check("""{"type":"bogus"}"""));

        // An author who is told only that the value is wrong has to go and find the list.
        Assert.Contains("array, boolean, integer, null, number, object, string", defect.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryDefect_IsReported_OrderedByPath()
    {
        var defects = Check("""{"type":"bogus","minimum":"x","properties":{"a":{"required":true}}}""");

        Assert.Equal(
            ["#/minimum", "#/properties/a/required", "#/type"],
            defects.Select(d => d.Path).ToArray());
    }

    [Fact]
    public void TheMessage_NeverQuotesMoreOfTheDocumentThanItNeeds()
    {
        // The document is attacker-controlled and the message goes into a response. A uniqueness
        // failure names the repeated value, so the value is bounded.
        var long_ = new string('x', 5_000);
        var defect = Assert.Single(Check($$"""{"required":["{{long_}}","{{long_}}"]}"""));

        Assert.True(defect.Message.Length < 400, $"{defect.Message.Length} characters");
    }

    // ----------------------------------------------------------------- references

    [Theory]
    [InlineData("""{"$ref":"#/$defs/missing"}""", "#/$ref")]
    [InlineData("""{"$defs":{"a":{}},"$ref":"#/$defs/b"}""", "#/$ref")]
    [InlineData("""{"properties":{"a":{"$ref":"#/properties/b"}}}""", "#/properties/a/$ref")]
    [InlineData("""{"$defs":{"a":{}},"$ref":"#/$defs/a/properties/x"}""", "#/$ref")]
    [InlineData("""{"allOf":[{"$ref":"#/allOf/1"}]}""", "#/allOf/0/$ref")]
    [InlineData("""{"allOf":[{"$ref":"#/allOf/01"},{}]}""", "#/allOf/0/$ref")]
    [InlineData("""{"$ref":"#/"}""", "#/$ref")]
    [InlineData("""{"$defs":{"a/b":{}},"$ref":"#/$defs/a/b"}""", "#/$ref")]
    [InlineData("""{"definitions":{"Adress":{}},"$ref":"#/definitions/Address"}""", "#/$ref")]
    public void AReferenceIntoTheDocumentThatPointsAtNothing_IsReported(string body, string path)
    {
        var defect = Assert.Single(Check(body));

        Assert.Equal(path, defect.Path);
        Assert.Equal(SchemaDefectKinds.ReferenceUnresolved, defect.Kind);
        Assert.Contains("#/", defect.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMessageForAnUnresolvedReference_NamesTheReference()
    {
        var defect = Assert.Single(Check("""{"$ref":"#/$defs/Adress"}"""));

        Assert.Contains("#/$defs/Adress", defect.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARemoteReference_IsNeverFetched()
    {
        // The document under test is data. A host that cannot be resolved (this one is reserved
        // by RFC 2606) would hang or fail if anything tried, so a prompt, clean answer is the
        // evidence that nothing did.
        var clock = Stopwatch.StartNew();

        var defects = Check(
            """{"$id":"https://nothing.invalid/root","$ref":"https://nothing.invalid/other","$schema":"https://nothing.invalid/meta"}""");

        Assert.Empty(defects);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
    }

    // ---------------------------------------------------------------------- bounds

    [Fact]
    public void ARequiredListAsLongAsADocumentCanHold_IsAcceptedWhole()
    {
        // The longest list a 512 KiB document can hold is on the order of 60,000 short names. That
        // it is checked in linear time is held by the next test, by counting, not by a clock.
        var names = string.Join(',', Enumerable.Range(0, 60_000).Select(i => $"\"{i}\""));

        Assert.Empty(Check($$"""{"required":[{{names}}]}"""));
    }

    [Fact]
    public void UniqueItems_ComparesEachStringOnceAgainstAHashNotEveryPair()
    {
        // uniqueItems done pairwise is quadratic: for the 60,000 names above, 1.8 billion comparisons
        // per registration. Counted rather than timed, so it says the same on any machine. Hashed,
        // each string is hashed once and compared only against a colliding one.
        var json = "[" + string.Join(',', Enumerable.Range(0, 60_000).Select(i => $"\"{i}\"")) + "]";
        using var document = JsonDocument.Parse(json);
        var comparer = new CountingComparer();

        var repeated = MetaschemaValidator.FirstRepeatedString(document.RootElement, comparer);

        Assert.Null(repeated);
        Assert.True(comparer.Calls < 3 * 60_000, $"{comparer.Calls} comparisons for 60,000 strings");
    }

    [Fact]
    public void UniqueItems_FindsTheFirstStringThatRepeats()
    {
        using var document = JsonDocument.Parse("""["a","b","c","b","a"]""");

        Assert.Equal("b", MetaschemaValidator.FirstRepeatedString(document.RootElement));
    }

    private sealed class CountingComparer : IEqualityComparer<string>
    {
        public int Calls { get; private set; }

        public bool Equals(string? x, string? y)
        {
            Calls++;
            return string.Equals(x, y, StringComparison.Ordinal);
        }

        public int GetHashCode(string obj)
        {
            Calls++;
            return StringComparer.Ordinal.GetHashCode(obj);
        }
    }

    [Fact]
    public void ADocumentNestedAsDeepAsTheReaderAllows_IsCheckedWithoutOverflowingTheStack()
    {
        // The canonicaliser's reader stops at 64 levels, so this is the deepest chain there is. A
        // chain of properties costs three levels of JSON per level of schema.
        var depth = 20;
        var body = new StringBuilder();
        for (var i = 0; i < depth; i++)
        {
            body.Append("""{"properties":{"a":""");
        }

        body.Append("{\"type\":\"bogus\"}");
        body.Append('}', depth * 2);

        var defects = Check(body.ToString());

        Assert.Equal(
            string.Concat(Enumerable.Repeat("/properties/a", depth)) + "/type",
            Assert.Single(defects).Path[1..]);
    }

    [Fact]
    public void ThePathsOfADeepDocumentAreExact_NotTruncated()
    {
        var defect = Assert.Single(Check("""{"$defs":{"a":{"$defs":{"b":{"items":{"minimum":"x"}}}}}}"""));

        Assert.Equal("#/$defs/a/$defs/b/items/minimum", defect.Path);
    }

    [Fact]
    public void AnEmptyOrWhitespaceBody_HasNoDefects()
    {
        // The canonicaliser has already refused it, and saying so twice only buries the real
        // message (the same stance the portability checker takes).
        Assert.Empty(Checker.Check(string.Empty));
        Assert.Empty(Checker.Check("   "));
    }

    [Fact]
    public void ABodyThatIsNotJson_HasNoDefects() =>
        Assert.Empty(Checker.Check("{ not json"));
}
