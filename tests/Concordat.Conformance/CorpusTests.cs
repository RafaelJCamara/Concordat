using System.Text;
using Concordat.Domain.Messaging;
using Concordat.Domain.Registry;
using Concordat.Formats.Abstractions;
using Concordat.Formats.Avro;
using Concordat.Formats.Json;
using Concordat.Formats.Protobuf;

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
    private static readonly NJsonSchemaPayloadValidator Validator = new();

    /// <summary>
    /// Every format's canonicaliser, chosen per fixture by its <c>format</c> field.
    /// </summary>
    /// <remarks>
    /// The corpus is normative for all three formats (ADR-019), so the runner cannot assume
    /// JSON Schema. Until M5 it did — every fixture happened to declare <c>"format": "json"</c>
    /// and the field went unread, which would have silently passed an Avro fixture through the
    /// JSON canonicaliser.
    /// </remarks>
    private static readonly Dictionary<SchemaFormat, ISchemaCanonicalizer> Canonicalizers = new()
    {
        [SchemaFormat.Json] = Canonicalizer,
        [SchemaFormat.Avro] = new AvroSchemaCanonicalizer(),
        [SchemaFormat.Protobuf] = new ProtoSchemaCanonicalizer(),
    };

    private static readonly Dictionary<SchemaFormat, ICompatibilityChecker> Checkers = new()
    {
        [SchemaFormat.Json] = new JsonSchemaCompatibilityChecker(),
        [SchemaFormat.Avro] = new AvroSchemaCompatibilityChecker(),
        [SchemaFormat.Protobuf] = new ProtoSchemaCompatibilityChecker(),
    };

    private static readonly Dictionary<SchemaFormat, ISchemaReferenceExtractor> Extractors = new()
    {
        [SchemaFormat.Json] = new JsonSchemaReferenceExtractor(),
        [SchemaFormat.Avro] = new AvroSchemaReferenceExtractor(),
        [SchemaFormat.Protobuf] = new ProtoSchemaReferenceExtractor(),
    };

    public static IEnumerable<object[]> Canonicalisation() =>
        Corpus.Load<CanonicalisationFixture>("canonicalisation");

    public static IEnumerable<object[]> SchemaIds() =>
        Corpus.Load<SchemaIdFixture>("schema-id");

    public static IEnumerable<object[]> Compatibility() =>
        Corpus.Load<CompatibilityFixture>("compatibility");

    public static IEnumerable<object[]> PayloadValidation() =>
        Corpus.Load<PayloadValidationFixture>("payload-validation");

    public static IEnumerable<object[]> EnvelopeEncode() =>
        Corpus.Load<EnvelopeEncodeFixture>("envelope-encode");

    public static IEnumerable<object[]> EnvelopeDecode() =>
        Corpus.Load<EnvelopeDecodeFixture>("envelope-decode");

    public static IEnumerable<object[]> SubjectResolution() =>
        Corpus.Load<SubjectResolutionFixture>("subject-resolution");

    public static IEnumerable<object[]> References() =>
        Corpus.Load<ReferenceFixture>("references");

    [Theory]
    [MemberData(nameof(References))]
    public void ReferenceExtractionMatchesTheCorpus(string file, ReferenceFixture fixture)
    {
        var result = Extractors[ParseFormat(fixture.Format)].Extract(fixture.CanonicalBody);

        if (fixture.Error is not null)
        {
            // ADR-023's refusals live here rather than only in the format test suites, because
            // an SDK that resolves what this one rejects accepts schemas the registry will not.
            Assert.True(result.IsFailure, $"{file}: expected refusal. {fixture.Why}");
            Assert.Equal(fixture.Error, result.Error!.Code);
            return;
        }

        Assert.True(result.IsSuccess, $"{file}: {result.Error?.Message}. {fixture.Why}");

        var expected = fixture.References
            .Select(r => $"{r.Name}|{r.Subject}|{r.Version}")
            .OrderBy(k => k, StringComparer.Ordinal);

        var actual = result.Value
            .Select(r => $"{r.Name}|{r.Subject.Value}|{r.Version}")
            .OrderBy(k => k, StringComparer.Ordinal);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(SubjectResolution))]
    public void SubjectResolutionMatchesTheCorpus(string file, SubjectResolutionFixture fixture)
    {
        var resolution = MessageTypeSubjectResolver.Instance.Resolve(new PublishContext
        {
            MessageType = fixture.MessageType,
            Exchange = fixture.Exchange,
            RoutingKey = fixture.RoutingKey,
        });

        switch (fixture.Outcome)
        {
            case "resolved":
                Assert.True(
                    resolution.IsResolved,
                    $"{file}: expected '{fixture.Subject}'. {fixture.Why}\n" +
                    $"  got: {Describe(resolution)}");
                Assert.Equal(fixture.Subject, resolution.Subject!.Value);
                break;

            case "absent":
                // Asserted separately from "unusable" on purpose. An SDK that reports absent as
                // an error passes a looser check and fails real brownfield estates.
                Assert.True(
                    !resolution.IsResolved && !resolution.IsUnusable,
                    $"{file}: expected no subject and no error. {fixture.Why}\n" +
                    $"  got: {Describe(resolution)}");
                break;

            case "unusable":
                Assert.True(
                    resolution.IsUnusable,
                    $"{file}: expected refusal with '{fixture.Error}'. {fixture.Why}\n" +
                    $"  got: {Describe(resolution)}");
                Assert.Equal(fixture.Error, resolution.Error!.Code);
                break;

            default:
                Assert.Fail($"{file}: unknown outcome '{fixture.Outcome}'.");
                break;
        }
    }

    private static string Describe(Concordat.Domain.Messaging.SubjectResolution resolution) =>
        resolution.IsResolved ? $"resolved '{resolution.Subject!.Value}'"
        : resolution.IsUnusable ? $"unusable ({resolution.Error!.Code})"
        : "absent";

    [Theory]
    [MemberData(nameof(EnvelopeEncode))]
    public void EnvelopeEncodingMatchesTheCorpus(string file, EnvelopeEncodeFixture fixture)
    {
        var headers = EnvelopeWriter.Headers(
            SchemaId.Create(fixture.SchemaId).Value,
            fixture.Subject is null ? null : SubjectName.Create(fixture.Subject).Value,
            fixture.Ordinal,
            fixture.Semver is null ? null : SemanticVersion.Create(fixture.Semver).Value,
            fixture.Format is null ? null : ParseFormat(fixture.Format));

        // Exact set equality: an extra header is as much a divergence as a missing one, and an
        // absent optional written as an empty string would quarantine its own message.
        Assert.True(
            fixture.Headers.OrderBy(h => h.Key, StringComparer.Ordinal)
                .SequenceEqual(headers.OrderBy(h => h.Key, StringComparer.Ordinal)),
            $"{file}: header mismatch. {fixture.Why}\n" +
            $"  expected: {string.Join(", ", fixture.Headers.Select(h => $"{h.Key}={h.Value}"))}\n" +
            $"  actual:   {string.Join(", ", headers.Select(h => $"{h.Key}={h.Value}"))}");
    }

    [Theory]
    [MemberData(nameof(EnvelopeDecode))]
    public void EnvelopeDecodingMatchesTheCorpus(string file, EnvelopeDecodeFixture fixture)
    {
        var headers = fixture.Headers?.ToDictionary(
            h => h.Key, h => ToHeaderValue(h.Value), StringComparer.Ordinal);

        var result = EnvelopeReader.Read(headers, fixture.PropertiesType, fixture.ContentType);

        if (fixture.Error is not null)
        {
            Assert.True(result.IsMalformed, $"{file}: expected rejection. {fixture.Why}");
            Assert.Equal(fixture.Error, result.Error!.Code);
            return;
        }

        var expected = fixture.Expected!;
        Assert.False(result.IsMalformed, $"{file}: {result.Error?.Message}. {fixture.Why}");
        Assert.Equal(expected.Kind, KindToken(result.Kind));

        if (expected.Kind == "NONE")
        {
            return;
        }

        var envelope = result.Envelope!;
        Assert.Equal(expected.SchemaId, envelope.SchemaId.Value);
        Assert.Equal(expected.Subject, envelope.Subject?.Value);
        Assert.Equal(expected.Ordinal, envelope.Ordinal);
        Assert.Equal(expected.Semver, envelope.Semver?.ToString());
        Assert.Equal(expected.Format, envelope.Format is { } f ? WireTokens.For(f) : null);

        Assert.Equal(
            expected.Warnings.Order(StringComparer.Ordinal),
            envelope.Warnings.Select(w => w.Code).Order(StringComparer.Ordinal));
    }

    private static object? ToHeaderValue(FixtureHeaderValue value) =>
        value switch
        {
            { String: { } s } => s,
            { BytesBase64: { } b } => Convert.FromBase64String(b),
            { Integer: { } i } => i,
            { Boolean: { } b } => b,
            _ => null,
        };

    private static string KindToken(EnvelopeKind kind) => kind switch
    {
        EnvelopeKind.None => "NONE",
        EnvelopeKind.Headers => "HEADERS",
        EnvelopeKind.ContentType => "CONTENT_TYPE",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    [Theory]
    [MemberData(nameof(Canonicalisation))]
    public void CanonicalisationMatchesTheCorpus(string file, CanonicalisationFixture fixture)
    {
        var canonicalizer = Canonicalizers[ParseFormat(fixture.Format)];
        var result = canonicalizer.Canonicalize(fixture.Input);

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
        Assert.Equal(fixture.Canonical, canonicalizer.Canonicalize(result.Value).Value);
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
        var format = ParseFormat(fixture.Format);
        var canonicalizer = Canonicalizers[format];

        var priors = fixture.Previous
            .Select(p => new PriorSchema(p.Ordinal, canonicalizer.Canonicalize(p.Schema).Value))
            .ToList();

        var proposed = canonicalizer.Canonicalize(fixture.Proposed);
        Assert.True(proposed.IsSuccess, $"{file}: proposed schema did not canonicalise.");

        var report = Checkers[format].Check(
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
    public void PayloadValidationMatchesTheCorpus(string file, PayloadValidationFixture fixture)
    {
        // Executed from M2, against the real validator behind IPayloadValidator. This is the
        // category where SDKs diverge without anyone writing a bug: five independent
        // implementations of a specification with genuine edge-case disagreement.
        var schema = Canonicalizer.Canonicalize(fixture.Schema);
        Assert.True(schema.IsSuccess, $"{file}: schema did not canonicalise. {fixture.Why}");

        Assert.NotEmpty(fixture.MustAccept);
        Assert.NotEmpty(fixture.MustReject);

        foreach (var document in fixture.MustAccept)
        {
            var result = Validator.Validate(schema.Value, document);
            Assert.True(
                result.IsValid,
                $"{file}: must ACCEPT {document} but got " +
                $"{string.Join("; ", result.Errors.Select(e => $"{e.Kind} at {e.Path}"))}. {fixture.Why}");
        }

        foreach (var document in fixture.MustReject)
        {
            var result = Validator.Validate(schema.Value, document);
            Assert.False(
                result.IsValid,
                $"{file}: must REJECT {document} but it was accepted. {fixture.Why}");
            Assert.NotEmpty(result.Errors);
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
