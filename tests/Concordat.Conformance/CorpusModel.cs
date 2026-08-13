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

/// <summary>A payload-validation case.</summary>
/// <remarks>
/// Not executed yet: Concordat has no validator of its own, because validation is client-side
/// and uses a different third-party library in every language. M2 wires the first, M6.1 makes
/// every SDK run these.
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
