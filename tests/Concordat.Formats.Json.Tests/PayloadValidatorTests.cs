using Concordat.Domain.Registry;
using Concordat.Formats.Json;

namespace Concordat.Formats.Json.Tests;

public class PayloadValidatorTests
{
    private static readonly NJsonSchemaPayloadValidator Validator = new();
    private static readonly JsonSchemaCanonicalizer Canonicalizer = new();

    private static string Schema(string body) => Canonicalizer.Canonicalize(body).Value;

    [Fact]
    public void Handles_TheJsonFormat() =>
        Assert.Equal(SchemaFormat.Json, Validator.Format);

    [Fact]
    public void AValidDocument_Passes()
    {
        var result = Validator.Validate(
            Schema("""{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}"""),
            """{"id":"a"}""");

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void AMissingRequiredProperty_Fails()
    {
        var result = Validator.Validate(
            Schema("""{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}"""),
            "{}");

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("1.0")]
    [InlineData("-3.00")]
    [InlineData("1e2")]
    public void AWholeNumber_SatisfiesInteger(string value)
    {
        // Draft 2020-12: "integer" matches any number with a zero fractional part. NJsonSchema
        // implements draft-04 semantics and rejects 1.0, so the adapter corrects it. Without
        // this, a JavaScript producer emitting 1.0 for a whole number would be quarantined by
        // the .NET consumer and accepted by every other SDK.
        var result = Validator.Validate(
            Schema("""{"type":"object","properties":{"n":{"type":"integer"}}}"""),
            $$"""{"n":{{value}}}""");

        Assert.True(
            result.IsValid,
            $"{value} should satisfy 'integer': " +
            string.Join("; ", result.Errors.Select(e => $"{e.Kind} at {e.Path}")));
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("-0.25")]
    [InlineData("\"1\"")]
    public void AFractionalOrNonNumber_DoesNotSatisfyInteger(string value)
    {
        // The correction must not overreach into accepting everything.
        var result = Validator.Validate(
            Schema("""{"type":"object","properties":{"n":{"type":"integer"}}}"""),
            $$"""{"n":{{value}}}""");

        Assert.False(result.IsValid, $"{value} should not satisfy 'integer'.");
    }

    [Fact]
    public void TheCorrectionIsScopedToTheOffendingPath()
    {
        // Two integer properties, one whole and one fractional. Only the fractional one may
        // survive as an error - a blanket drop of IntegerExpected would pass this document.
        var result = Validator.Validate(
            Schema("""{"type":"object","properties":{"ok":{"type":"integer"},"bad":{"type":"integer"}}}"""),
            """{"ok":2.0,"bad":2.5}""");

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Path.Contains("bad", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Errors, e => e.Path.Contains("ok", StringComparison.Ordinal));
    }

    [Fact]
    public void MalformedJson_IsAVerdictNotAnException()
    {
        // This runs on the delivery path. A throw there is a far worse failure than a verdict.
        var result = Validator.Validate(Schema("""{"type":"object"}"""), "not json");

        Assert.False(result.IsValid);
        Assert.Equal("malformed_json", Assert.Single(result.Errors).Kind);
    }

    [Fact]
    public void AnEmptyPayload_IsAVerdictNotAnException()
    {
        var result = Validator.Validate(Schema("""{"type":"object"}"""), "");

        Assert.False(result.IsValid);
        Assert.Equal("empty", Assert.Single(result.Errors).Kind);
    }

    [Fact]
    public void ErrorPathsAreJsonPointersIntoTheDocument()
    {
        // Not into the schema - that is what BreakingChange.Path does, and confusing the two
        // would send a reader to the wrong file.
        var result = Validator.Validate(
            Schema("""{"type":"object","properties":{"outer":{"type":"object","properties":{"inner":{"type":"string"}}}}}"""),
            """{"outer":{"inner":5}}""");

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Path == "#/outer/inner");
    }

    [Fact]
    public void ASchemaIsCompiledOncePerCanonicalText()
    {
        // Compilation is the expensive part and this sits on the delivery path.
        var validator = new NJsonSchemaPayloadValidator();
        var schema = Schema("""{"type":"object","properties":{"id":{"type":"string"}}}""");

        for (var i = 0; i < 5; i++)
        {
            validator.Validate(schema, $$"""{"id":"{{i}}"}""");
        }

        Assert.Equal(1, validator.CompiledCount);
    }

    [Fact]
    public void AnUncompilableSchema_IsAVerdictNotAnException()
    {
        var result = Validator.Validate("{\"type\":", """{"a":1}""");

        Assert.False(result.IsValid);
        Assert.Equal("schema_uncompilable", Assert.Single(result.Errors).Kind);
    }
}
