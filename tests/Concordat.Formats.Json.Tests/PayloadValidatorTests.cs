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

    [Fact]
    public void AnOversizedPayload_IsRejectedBeforeParsing()
    {
        var schema = Schema("""{"type":"string"}""");
        var payload = "\"" + new string('a', NJsonSchemaPayloadValidator.MaxPayloadBytes) + "\"";

        var result = Validator.Validate(schema, payload);

        Assert.False(result.IsValid);
        Assert.Equal("payload_too_large", Assert.Single(result.Errors).Kind);
    }

    [Fact]
    public async Task ACatastrophicallyBacktrackingPattern_TimesOutRatherThanHangingTheThread()
    {
        // ^(a+)+$ against a run of 'a's with no terminating match is the textbook ReDoS
        // trigger: unbounded, it explores exponentially many ways to partition the run and
        // would pin this thread for a duration nobody would ever wait out. RegexSafety's
        // process-wide match timeout is what turns that into a bounded, reportable failure.
        // Both the pattern and the payload are attacker-controlled in production -- the
        // pattern from a registered schema, the payload from a message on the wire.
        var schema = Schema("""{"type":"string","pattern":"^(a+)+$"}""");
        var payload = "\"" + new string('a', 40) + "X\"";

        var validated = Task.Run(() => Validator.Validate(schema, payload));

        // A generous outer bound so a genuine failure to time out fails this test instead of
        // hanging the run forever -- RegexSafety's own timeout is 1 second.
        var result = await validated.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(result.IsValid);
        Assert.Equal("pattern_match_timeout", Assert.Single(result.Errors).Kind);
    }
}
