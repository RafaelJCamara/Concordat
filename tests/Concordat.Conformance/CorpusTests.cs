using System.Text;
using Concordat.Domain.Registry;
using Concordat.Formats.Abstractions;
using Concordat.Formats.Json;

namespace Concordat.Conformance;

/// <summary>
/// Runs the normative corpus against the .NET implementation.
/// </summary>
/// <remarks>
/// When one of these fails, the corpus is presumed right. It is the specification; this
/// assembly is one implementation of it, and the fact that it happens to be the first does
/// not make it the reference (ADR-019).
/// </remarks>
public class CorpusTests
{
    private static readonly JsonSchemaCanonicalizer Canonicalizer = new();
    private static readonly JsonSchemaCompatibilityChecker Checker = new();
    private static readonly JsonSchemaReferenceExtractor Extractor = new();

    public static IEnumerable<object[]> Canonicalisation() =>
        Corpus.Load<CanonicalisationFixture>("canonicalisation");

    public static IEnumerable<object[]> SchemaIds() =>
        Corpus.Load<SchemaIdFixture>("schema-id");

    public static IEnumerable<object[]> Compatibility() =>
        Corpus.Load<CompatibilityFixture>("compatibility");

    public static IEnumerable<object[]> PayloadValidation() =>
        Corpus.Load<PayloadValidationFixture>("payload-validation");

    [Theory]
    [MemberData(nameof(Canonicalisation))]
    public void CanonicalisationMatchesTheCorpus(string file, CanonicalisationFixture fixture)
    {
        var result = Canonicalizer.Canonicalize(fixture.Input);

        if (fixture.Error is not null)
        {
            Assert.True(result.IsFailure, $"{file}: expected rejection. {fixture.Why}");
            Assert.Equal(fixture.Error, result.Error!.Code);
            return;
        }

        Assert.True(result.IsSuccess, $"{file}: {result.Error?.Message}. {fixture.Why}");
        Assert.Equal(fixture.Canonical, result.Value);

        // Idempotence is part of the contract, not a bonus: canonicalising an already-canonical
        // document must be a no-op or the id is not stable under re-registration.
        Assert.Equal(fixture.Canonical, Canonicalizer.Canonicalize(result.Value).Value);
    }

    [Theory]
    [MemberData(nameof(SchemaIds))]
    public void SchemaIdAndPreimageMatchTheCorpus(string file, SchemaIdFixture fixture)
    {
        var references = fixture.References
            .Select(r => Reference.Create(
                r.Name, SubjectName.Create(r.Subject).Value, r.Version).Value)
            .ToList();

        var format = ParseFormat(fixture.Format);

        // The preimage is checked directly, not only the resulting id. An implementation that
        // produces the right hash from the wrong framing diverges the moment a reference set
        // changes, and the id alone would not have caught it.
        var preimage = Encoding.UTF8.GetString(
            SchemaIdComputer.BuildPreimage(format, fixture.CanonicalBody, references));

        Assert.True(
            string.Equals(fixture.Preimage, preimage, StringComparison.Ordinal),
            $"{file}: preimage mismatch. {fixture.Why}\n" +
            $"  expected: {fixture.Preimage.Replace("\n", "\\n", StringComparison.Ordinal)}\n" +
            $"  actual:   {preimage.Replace("\n", "\\n", StringComparison.Ordinal)}");

        var id = SchemaIdComputer.Compute(format, fixture.CanonicalBody, references);
        Assert.Equal(fixture.SchemaId, id.Value);
    }

    [Theory]
    [MemberData(nameof(Compatibility))]
    public void CompatibilityVerdictsMatchTheCorpus(string file, CompatibilityFixture fixture)
    {
        var priors = fixture.Previous
            .Select(p => new PriorSchema(p.Ordinal, Canonicalizer.Canonicalize(p.Schema).Value))
            .ToList();

        var proposed = Canonicalizer.Canonicalize(fixture.Proposed);
        Assert.True(proposed.IsSuccess, $"{file}: proposed schema did not canonicalise.");

        var report = Checker.Check(
            proposed.Value,
            priors,
            new CompatibilityPolicy(
                ParseMode(fixture.Policy.Mode), ParseSurface(fixture.Policy.Surface)),
            ParseContentModel(fixture.ContentModel));

        Assert.True(
            fixture.Expected.Compatible == report.IsCompatible,
            $"{file}: expected compatible={fixture.Expected.Compatible}, got {report.IsCompatible}. " +
            $"{fixture.Why}");

        Assert.Equal(fixture.Expected.SuggestedBump, report.SuggestedBump.ToString().ToUpperInvariant());

        AssertFindings(file, "breakingChanges", fixture.Expected.BreakingChanges, report.BreakingChanges);

        if (fixture.Expected.AllDivergences is { } expectedAll)
        {
            AssertFindings(file, "allDivergences", expectedAll, report.AllDivergences);
        }
    }

    [Theory]
    [MemberData(nameof(PayloadValidation))]
    public void PayloadFixturesAreWellFormed(string file, PayloadValidationFixture fixture)
    {
        // Concordat has no payload validator of its own - validation is client-side and uses a
        // different third-party library per language. Until M2 wires the first one, this checks
        // only that the fixtures are usable: the schema canonicalises and every document is
        // parseable JSON. Weak, but it keeps the corpus honest rather than letting it rot.
        var schema = Canonicalizer.Canonicalize(fixture.Schema);
        Assert.True(schema.IsSuccess, $"{file}: schema did not canonicalise. {fixture.Why}");

        Assert.NotEmpty(fixture.MustAccept);
        Assert.NotEmpty(fixture.MustReject);

        foreach (var document in fixture.MustAccept.Concat(fixture.MustReject))
        {
            var parsed = System.Text.Json.JsonDocument.Parse(document);
            parsed.Dispose();
        }
    }

    [Fact]
    public void EveryFixtureExplainsWhyItExists()
    {
        // A fixture whose purpose nobody recorded is one nobody dares change when it fails,
        // so it either gets deleted or silently suppressed. Both are worse than the failure.
        var missing = new List<string>();

        foreach (var category in Directory.EnumerateDirectories(Corpus.Root))
        {
            foreach (var file in Directory.EnumerateFiles(category, "*.json"))
            {
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));

                if (!document.RootElement.TryGetProperty("why", out var why) ||
                    string.IsNullOrWhiteSpace(why.GetString()))
                {
                    missing.Add(Path.GetFileName(file));
                }
            }
        }

        Assert.Empty(missing);
    }

    private static void AssertFindings(
        string file,
        string label,
        IReadOnlyList<FixtureFinding> expected,
        IReadOnlyList<BreakingChange> actual)
    {
        var actualKeys = actual
            .Select(c => $"{c.Path}|{c.Kind}|{c.Direction.ToString().ToUpperInvariant()}|{Surface(c.Surface)}")
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        var expectedKeys = expected
            .Select(e => $"{e.Path}|{e.Kind}|{e.Direction}|{e.Surface}")
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            expectedKeys.SequenceEqual(actualKeys, StringComparer.Ordinal),
            $"{file}: {label} mismatch.\n  expected: {string.Join("\n            ", expectedKeys)}" +
            $"\n  actual:   {string.Join("\n            ", actualKeys)}");
    }

    private static string Surface(CompatibilitySurface surface) => surface switch
    {
        CompatibilitySurface.Wire => "WIRE",
        CompatibilitySurface.WireJson => "WIRE_JSON",
        CompatibilitySurface.Source => "SOURCE",
        _ => throw new ArgumentOutOfRangeException(nameof(surface), surface, null),
    };

    private static SchemaFormat ParseFormat(string token) => token switch
    {
        WireTokens.FormatJson => SchemaFormat.Json,
        WireTokens.FormatAvro => SchemaFormat.Avro,
        WireTokens.FormatProtobuf => SchemaFormat.Protobuf,
        _ => throw new ArgumentOutOfRangeException(nameof(token), token, "Unknown format token."),
    };

    private static CompatibilityMode ParseMode(string token) =>
        Enum.Parse<CompatibilityMode>(token.Replace("_", "", StringComparison.Ordinal), true);

    private static CompatibilitySurface ParseSurface(string token) =>
        Enum.Parse<CompatibilitySurface>(token.Replace("_", "", StringComparison.Ordinal), true);

    private static ContentModel ParseContentModel(string token) =>
        Enum.Parse<ContentModel>(token, true);
}
