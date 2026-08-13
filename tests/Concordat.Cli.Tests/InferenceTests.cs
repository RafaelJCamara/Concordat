using System.Text.Json;
using Concordat.Cli.Inference;

namespace Concordat.Cli.Tests;

/// <summary>
/// The inference engine. Pure, so no broker and no registry.
/// </summary>
/// <remarks>
/// Inference is a drafting aid, not protocol, so these are ordinary tests rather than
/// conformance fixtures — another SDK inferring slightly differently costs nothing, because a
/// human reads and edits the output before anything is registered (ADR-014).
/// </remarks>
public class InferenceTests
{
    private static InferenceResult Infer(params string[] samples) => JsonSchemaInferrer.Infer(samples);

    private static JsonElement SchemaOf(InferenceResult result) =>
        JsonDocument.Parse(result.Schema).RootElement;

    private static string[] Repeat(Func<int, string> make, int count) =>
        [.. Enumerable.Range(0, count).Select(make)];

    [Fact]
    public void TypesAndRequirednessComeFromTheSamples()
    {
        var result = Infer(Repeat(i => $$"""{"id":{{i}},"name":"n{{i}}"}""", 12));
        var schema = SchemaOf(result);

        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Equal("integer", schema.GetProperty("properties").GetProperty("id").GetProperty("type").GetString());
        Assert.Equal("string", schema.GetProperty("properties").GetProperty("name").GetProperty("type").GetString());

        var required = schema.GetProperty("required").EnumerateArray().Select(r => r.GetString()).ToList();
        Assert.Contains("id", required);
        Assert.Contains("name", required);
    }

    [Fact]
    public void AFieldMissingFromSomeSamplesIsOptional()
    {
        var samples = Repeat(i => i % 2 == 0 ? """{"id":1,"note":"x"}""" : """{"id":1}""", 12);

        var schema = SchemaOf(Infer(samples));
        var required = schema.GetProperty("required").EnumerateArray().Select(r => r.GetString()).ToList();

        Assert.Contains("id", required);
        Assert.DoesNotContain("note", required);
    }

    [Fact]
    public void RequirednessFromPresenceIsAlwaysReported()
    {
        // The single most misleading inference there is: an optional field that happens to be
        // set in every sample is indistinguishable from a required one. The draft has to say so
        // or a reviewer cannot know which fields to question.
        var result = Infer(Repeat(_ => """{"id":1}""", 12));

        Assert.Contains(result.Findings, f => f.Kind == FindingKinds.RequiredFromPresence);
    }

    [Fact]
    public void ASingleRepeatedValueIsNotAnEnum()
    {
        // The most damaging inference this tool could make. enum:["placed"] from twenty
        // identical samples would reject the second value the field ever takes — and it would
        // do it in production, long after anyone remembers where the schema came from.
        var result = Infer(Repeat(_ => """{"status":"placed"}""", 20));
        var status = SchemaOf(result).GetProperty("properties").GetProperty("status");

        Assert.False(status.TryGetProperty("enum", out _));
        Assert.Contains(result.Findings, f => f.Kind == FindingKinds.ConstantValue);
    }

    [Fact]
    public void SeveralRepeatedValuesAreAnEnumButAlwaysLowConfidence()
    {
        var statuses = new[] { "placed", "shipped", "cancelled" };
        var result = Infer(Repeat(i => $$"""{"status":"{{statuses[i % 3]}}"}""", 15));

        var values = SchemaOf(result).GetProperty("properties").GetProperty("status")
            .GetProperty("enum").EnumerateArray().Select(v => v.GetString()).ToList();

        Assert.Equal(3, values.Count);

        var finding = Assert.Single(result.Findings, f => f.Kind == FindingKinds.EnumFromLowCardinality);

        // Never better than low: a value that simply never appeared in the samples looks
        // exactly like a value that cannot occur.
        Assert.Equal(Confidence.Low, finding.Confidence);
    }

    [Fact]
    public void AnEnumNeedsEnoughSamplesToMeanAnything()
    {
        // Two distinct values across four samples is four samples, not a closed set.
        var result = Infer("""{"s":"a"}""", """{"s":"b"}""", """{"s":"a"}""", """{"s":"b"}""");

        Assert.False(SchemaOf(result).GetProperty("properties").GetProperty("s")
            .TryGetProperty("enum", out _));
    }

    [Fact]
    public void WholeNumbersNarrowToIntegerAndSayThatTheyDid()
    {
        // Asymmetric cost: guessing integer rejects a later 1.5, guessing number accepts
        // everything. It narrows anyway — a schema that types every number as `number` is
        // barely worth having — but it must be reported.
        var result = Infer(Repeat(i => $$"""{"total":{{i}}}""", 12));

        Assert.Equal(
            "integer",
            SchemaOf(result).GetProperty("properties").GetProperty("total").GetProperty("type").GetString());

        Assert.Contains(result.Findings, f => f.Kind == FindingKinds.IntegerFromWholeNumbers);
    }

    [Fact]
    public void OneFractionalValueIsEnoughToWidenToNumber()
    {
        var samples = Repeat(i => i == 5 ? """{"total":1.5}""" : """{"total":2}""", 12);

        Assert.Equal(
            "number",
            SchemaOf(Infer(samples)).GetProperty("properties").GetProperty("total").GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("3f2504e0-4f89-11d3-9a0c-0305e82c3301", "uuid")]
    [InlineData("2026-08-13T10:00:00Z", "date-time")]
    [InlineData("2026-08-13", "date")]
    [InlineData("someone@example.com", "email")]
    public void FormatsAreDetectedWhenEveryValueMatches(string value, string expected)
    {
        var result = Infer(Repeat(_ => $$"""{"v":"{{value}}"}""", 12));

        Assert.Equal(
            expected,
            SchemaOf(result).GetProperty("properties").GetProperty("v").GetProperty("format").GetString());
    }

    [Fact]
    public void OneNonMatchingValueSuppressesTheFormat()
    {
        var samples = Repeat(
            i => i == 7 ? """{"v":"not-a-uuid"}""" : """{"v":"3f2504e0-4f89-11d3-9a0c-0305e82c3301"}""", 12);

        Assert.False(SchemaOf(Infer(samples)).GetProperty("properties").GetProperty("v")
            .TryGetProperty("format", out _));
    }

    [Fact]
    public void NullAlongsideAValueBecomesNullable()
    {
        var samples = Repeat(i => i % 2 == 0 ? """{"v":"x"}""" : """{"v":null}""", 12);

        var types = SchemaOf(Infer(samples)).GetProperty("properties").GetProperty("v")
            .GetProperty("type").EnumerateArray().Select(t => t.GetString()).ToList();

        Assert.Equal(["string", "null"], types);
    }

    [Fact]
    public void AnAlwaysNullFieldIsUnconstrainedAndLowConfidence()
    {
        // "Required, of unknown type" is a combination almost nobody means. It usually marks an
        // optional field the samples never exercised, so both findings are low.
        var result = Infer(Repeat(_ => """{"cancelledBy":null}""", 12));

        Assert.False(SchemaOf(result).GetProperty("properties").GetProperty("cancelledBy")
            .TryGetProperty("type", out _));

        Assert.All(
            result.Findings.Where(f => f.Path.Contains("cancelledBy", StringComparison.Ordinal)),
            f => Assert.Equal(Confidence.Low, f.Confidence));
    }

    [Fact]
    public void MixedTypesAreLeftUnconstrainedRatherThanGuessed()
    {
        var samples = Repeat(i => i % 2 == 0 ? """{"v":1}""" : """{"v":"one"}""", 12);
        var result = Infer(samples);

        Assert.False(SchemaOf(result).GetProperty("properties").GetProperty("v")
            .TryGetProperty("type", out _));

        Assert.Contains(result.Findings, f => f.Kind == FindingKinds.MixedTypes);
    }

    [Fact]
    public void NestedObjectsAndArraysAreInferredThrough()
    {
        var samples = Repeat(
            i => $$"""{"customer":{"id":{{i}}},"lines":[{"sku":"a{{i}}"}]}""", 12);

        var schema = SchemaOf(Infer(samples));

        Assert.Equal(
            "integer",
            schema.GetProperty("properties").GetProperty("customer")
                .GetProperty("properties").GetProperty("id").GetProperty("type").GetString());

        Assert.Equal(
            "string",
            schema.GetProperty("properties").GetProperty("lines").GetProperty("items")
                .GetProperty("properties").GetProperty("sku").GetProperty("type").GetString());
    }

    [Fact]
    public void AnAlwaysEmptyArrayIsReportedRatherThanTypedFromNothing()
    {
        var result = Infer(Repeat(_ => """{"tags":[]}""", 12));

        Assert.False(SchemaOf(result).GetProperty("properties").GetProperty("tags")
            .TryGetProperty("items", out _));

        Assert.Contains(result.Findings, f => f.Kind == FindingKinds.EmptyArray);
    }

    [Fact]
    public void ThinEvidenceIsCalledOutBeforeAnythingElse()
    {
        var result = Infer("""{"id":1}""", """{"id":2}""");

        Assert.Equal(FindingKinds.ThinEvidence, result.Findings[0].Kind);
        Assert.Equal(Confidence.Low, result.Findings[0].Confidence);
    }

    [Fact]
    public void AdditionalPropertiesIsNeverEmitted()
    {
        // Concordat's content model defaults to open, and closing a model inferred from samples
        // would reject every field the samples happened to miss — which, for a brownfield
        // estate being onboarded, is the whole long tail.
        var result = Infer(Repeat(_ => """{"id":1}""", 12));

        Assert.False(SchemaOf(result).TryGetProperty("additionalProperties", out _));
    }

    [Fact]
    public void AnUnparsableSampleIsSkippedNotFatal()
    {
        // A drain of a live queue will pick up the odd non-JSON message.
        var samples = Repeat(i => i == 3 ? "not json" : """{"id":1}""", 12);

        Assert.Equal(11, Infer(samples).SampleCount);
    }

    [Fact]
    public void NoParsableSampleAtAllIsAnError()
    {
        Assert.Throws<InvalidOperationException>(() => Infer("not json", "also not json"));
    }

    [Fact]
    public void TheDraftIsValidJsonSchemaThatTheRealValidatorAccepts()
    {
        // The draft has to survive the pipeline it was made for: canonicalise, get an id, and
        // validate one of its own samples. A draft the product rejects is worse than none.
        var samples = Repeat(
            i => $$"""{"id":{{i}},"orderId":"3f2504e0-4f89-11d3-9a0c-0305e82c330{{i % 10}}"}""", 12);

        var result = Infer(samples);

        var canonical = new Concordat.Formats.Json.JsonSchemaCanonicalizer().Canonicalize(result.Schema);
        Assert.True(canonical.IsSuccess, result.Schema);

        var validation = new Concordat.Formats.Json.NJsonSchemaPayloadValidator()
            .Validate(canonical.Value, samples[0]);

        Assert.True(
            validation.IsValid,
            string.Join("; ", validation.Errors.Select(e => $"{e.Path}: {e.Message}")));
    }
}
