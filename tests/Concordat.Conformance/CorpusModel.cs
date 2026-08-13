using System.Text.Json;
using System.Text.Json.Serialization;

namespace Concordat.Conformance;

/// <summary>Locates and deserialises the corpus files.</summary>
/// <remarks>
/// The fixtures are plain JSON on disk, not embedded resources and not C# literals: another
/// language's test runner has to be able to read exactly the same files (ADR-019).
/// </remarks>
public static class Corpus
{
    /// <summary>Deserialisation settings shared by every fixture category.</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    /// <summary>The corpus root, beside the test assembly.</summary>
    public static string Root { get; } =
        Path.Combine(AppContext.BaseDirectory, "corpus");

    /// <summary>Loads every fixture in a category.</summary>
    /// <typeparam name="T">The fixture type.</typeparam>
    /// <param name="category">A directory name under <see cref="Root"/>.</param>
    /// <returns>Each fixture with the file it came from, so a failure names the file.</returns>
    public static IEnumerable<object[]> Load<T>(string category)
    {
        var directory = Path.Combine(Root, category);

        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                $"Corpus category '{category}' is missing at {directory}. " +
                "Check that the fixtures are copied to the output directory.");
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            var fixture = JsonSerializer.Deserialize<T>(File.ReadAllText(file), Options)
                ?? throw new InvalidOperationException($"Fixture {file} deserialised to null.");

            yield return [Path.GetFileNameWithoutExtension(file), fixture!];
        }
    }
}

/// <summary>Fields every fixture carries.</summary>
public abstract record FixtureBase
{
    /// <summary>A short identifier, matching the file name.</summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// Why this case exists. Not decoration — a fixture whose purpose nobody recorded is one
    /// nobody dares change when it fails.
    /// </summary>
    public string Why { get; init; } = "";
}

/// <summary>A canonicalisation case.</summary>
public sealed record CanonicalisationFixture : FixtureBase
{
    /// <summary>The schema language.</summary>
    public string Format { get; init; } = "json";

    /// <summary>The document as authored.</summary>
    public string Input { get; init; } = "";

    /// <summary>The expected canonical text, when the input is valid.</summary>
    public string? Canonical { get; init; }

    /// <summary>The expected <c>concordatCode</c>, when the input must be refused.</summary>
    public string? Error { get; init; }
}

/// <summary>A reference inside a schema-id fixture.</summary>
public sealed record FixtureReference
{
    /// <summary>The <c>$ref</c> text.</summary>
    public string Name { get; init; } = "";

    /// <summary>The referenced subject.</summary>
    public string Subject { get; init; } = "";

    /// <summary>The referenced version ordinal.</summary>
    public int Version { get; init; }
}

/// <summary>A schema-identity case, pinning the preimage as well as the id.</summary>
public sealed record SchemaIdFixture : FixtureBase
{
    /// <summary>The schema language.</summary>
    public string Format { get; init; } = "json";

    /// <summary>The already-canonical body.</summary>
    public string CanonicalBody { get; init; } = "";

    /// <summary>The references covered by the hash.</summary>
    public IReadOnlyList<FixtureReference> References { get; init; } = [];

    /// <summary>The exact bytes hashed, with <c>\n</c> separators.</summary>
    public string Preimage { get; init; } = "";

    /// <summary>The resulting 32-character lowercase hexadecimal id.</summary>
    public string SchemaId { get; init; } = "";
}

/// <summary>A prior version in a compatibility fixture.</summary>
public sealed record FixturePrior
{
    /// <summary>The version ordinal.</summary>
    public int Ordinal { get; init; }

    /// <summary>Its schema, as authored.</summary>
    public string Schema { get; init; } = "";
}

/// <summary>A policy in a compatibility fixture.</summary>
public sealed record FixturePolicy
{
    /// <summary>The who-breaks axis.</summary>
    public string Mode { get; init; } = "BACKWARD";

    /// <summary>The what-breaks axis.</summary>
    public string Surface { get; init; } = "WIRE_JSON";
}

/// <summary>One expected finding. Messages are excluded on purpose: they must be free to improve.</summary>
public sealed record FixtureFinding
{
    /// <summary>The JSON Pointer into the schema document.</summary>
    public string Path { get; init; } = "";

    /// <summary>The stable kind token.</summary>
    public string Kind { get; init; } = "";

    /// <summary>The direction broken.</summary>
    public string Direction { get; init; } = "";

    /// <summary>The narrowest surface violated.</summary>
    public string Surface { get; init; } = "";
}

/// <summary>What a compatibility fixture expects.</summary>
public sealed record FixtureExpectation
{
    /// <summary>Whether the proposal satisfies the policy.</summary>
    public bool Compatible { get; init; }

    /// <summary>The semantic version increment warranted.</summary>
    public string SuggestedBump { get; init; } = "";

    /// <summary>Findings that violate the policy.</summary>
    public IReadOnlyList<FixtureFinding> BreakingChanges { get; init; } = [];

    /// <summary>
    /// Every finding, including tolerated ones. Omitted when a fixture does not pin it.
    /// </summary>
    public IReadOnlyList<FixtureFinding>? AllDivergences { get; init; }
}

/// <summary>A compatibility case.</summary>
public sealed record CompatibilityFixture : FixtureBase
{
    /// <summary>The schema language.</summary>
    public string Format { get; init; } = "json";

    /// <summary>The subject's content model.</summary>
    public string ContentModel { get; init; } = "open";

    /// <summary>The policy in force.</summary>
    public FixturePolicy Policy { get; init; } = new();

    /// <summary>Previously registered versions.</summary>
    public IReadOnlyList<FixturePrior> Previous { get; init; } = [];

    /// <summary>The proposed schema.</summary>
    public string Proposed { get; init; } = "";

    /// <summary>The expected outcome.</summary>
    public FixtureExpectation Expected { get; init; } = new();
}

/// <summary>
/// A header value, tagged so the corpus can express types JSON cannot.
/// </summary>
/// <remarks>
/// Exactly one property is set. A plain string map would be unable to express the wrong-type
/// and invalid-UTF-8 cases, which are two of the four decode behaviours most likely to differ
/// between SDKs.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1720:Identifier contains type name",
    Justification = "These names are the fixture JSON's discriminator keys and are part of the " +
        "corpus format that every SDK reads. Renaming them for a C# analyzer would change the " +
        "wire format of the specification.")]
public sealed record FixtureHeaderValue
{
    /// <summary>A UTF-8 string value.</summary>
    public string? String { get; init; }

    /// <summary>Raw bytes, base64-encoded — what an AMQP client typically hands back.</summary>
    public string? BytesBase64 { get; init; }

    /// <summary>An integer, to exercise the wrong-type path.</summary>
    public int? Integer { get; init; }

    /// <summary>A boolean, to exercise the wrong-type path.</summary>
    public bool? Boolean { get; init; }
}

/// <summary>What a decode fixture expects to be read.</summary>
public sealed record EnvelopeExpectation
{
    /// <summary><c>NONE</c>, <c>HEADERS</c> or <c>CONTENT_TYPE</c>.</summary>
    public string Kind { get; init; } = "NONE";

    /// <summary>The schema id, when one was read.</summary>
    public string? SchemaId { get; init; }

    /// <summary>The subject, when one was resolved.</summary>
    public string? Subject { get; init; }

    /// <summary>The version ordinal, when readable.</summary>
    public int? Ordinal { get; init; }

    /// <summary>The semantic version label, when readable.</summary>
    public string? Semver { get; init; }

    /// <summary>The declared format token.</summary>
    public string? Format { get; init; }

    /// <summary>Advisory problem codes, in any order.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>An envelope encode case: identity in, headers out.</summary>
public sealed record EnvelopeEncodeFixture : FixtureBase
{
    /// <summary>The schema id to write.</summary>
    public string SchemaId { get; init; } = "";

    /// <summary>The subject, when the producer knows it.</summary>
    public string? Subject { get; init; }

    /// <summary>The version ordinal.</summary>
    public int? Ordinal { get; init; }

    /// <summary>The semantic version label.</summary>
    public string? Semver { get; init; }

    /// <summary>The format token.</summary>
    public string? Format { get; init; }

    /// <summary>The exact headers that must be produced — no more, no fewer.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>An envelope decode case: a message in, identity or a verdict out.</summary>
public sealed record EnvelopeDecodeFixture : FixtureBase
{
    /// <summary>The header table, or null to represent a message with none.</summary>
    public IReadOnlyDictionary<string, FixtureHeaderValue>? Headers { get; init; }

    /// <summary>AMQP <c>properties.type</c>.</summary>
    public string? PropertiesType { get; init; }

    /// <summary>AMQP <c>properties.content-type</c>.</summary>
    public string? ContentType { get; init; }

    /// <summary>What must be read, when reading succeeds.</summary>
    public EnvelopeExpectation? Expected { get; init; }

    /// <summary>The <c>concordatCode</c> that must be returned, when the envelope is unusable.</summary>
    public string? Error { get; init; }
}

/// <summary>A subject-resolution case: what a publisher declared, and the subject it becomes.</summary>
/// <remarks>
/// Resolution is client-side and publish-side only (ADR-011), so the registry never runs any
/// of this. The fixtures are normative all the same, and arguably more so than most: every
/// normalisation rule is a rule five SDKs must apply identically, or one publisher's message
/// type becomes two subjects depending on which language sent it.
/// </remarks>
public sealed record SubjectResolutionFixture : FixtureBase
{
    /// <summary>AMQP <c>properties.type</c> as the publisher set it, or null if unset.</summary>
    public string? MessageType { get; init; }

    /// <summary>The exchange, present only to prove it is never used as a fallback.</summary>
    public string? Exchange { get; init; }

    /// <summary>The routing key, present only to prove it is never used as a fallback.</summary>
    public string? RoutingKey { get; init; }

    /// <summary><c>resolved</c>, <c>absent</c>, or <c>unusable</c>.</summary>
    public string Outcome { get; init; } = "";

    /// <summary>The subject that must be produced, when the outcome is <c>resolved</c>.</summary>
    public string? Subject { get; init; }

    /// <summary>The <c>concordatCode</c> that must be returned, when the outcome is <c>unusable</c>.</summary>
    public string? Error { get; init; }
}

/// <summary>A payload-validation case.</summary>
/// <remarks>
/// Executed since M2.0, against <c>NJsonSchemaPayloadValidator</c> behind the
/// <c>IPayloadValidator</c> port. M6.1 makes every SDK run the same fixtures.
/// </remarks>
public sealed record PayloadValidationFixture : FixtureBase
{
    /// <summary>The schema documents are validated against.</summary>
    public string Schema { get; init; } = "";

    /// <summary>Documents every conforming validator must accept.</summary>
    public IReadOnlyList<string> MustAccept { get; init; } = [];

    /// <summary>Documents every conforming validator must reject.</summary>
    public IReadOnlyList<string> MustReject { get; init; } = [];
}

/// <summary>
/// A reference-extraction case.
/// </summary>
/// <remarks>
/// Added in M5. ADR-023 refuses cross-subject references for Avro and Protobuf, and that
/// refusal is protocol rather than a .NET choice: an SDK that resolves what this one rejects
/// would accept schemas the registry will not, and the disagreement would surface as a failed
/// registration nobody could explain. Pinned here so every implementation refuses the same
/// documents.
/// </remarks>
public sealed record ReferenceFixture : FixtureBase
{
    /// <summary>The schema language.</summary>
    public string Format { get; init; } = "json";

    /// <summary>The already-canonical body.</summary>
    public string CanonicalBody { get; init; } = "";

    /// <summary>The edges the extractor must derive, when it must succeed.</summary>
    public IReadOnlyList<FixtureReference> References { get; init; } = [];

    /// <summary>The expected <c>concordatCode</c>, when the document must be refused.</summary>
    public string? Error { get; init; }
}
