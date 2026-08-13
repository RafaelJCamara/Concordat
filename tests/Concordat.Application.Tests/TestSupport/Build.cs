using Concordat.Domain.Registry;
using Concordat.Formats.Abstractions;
using Concordat.Formats.Json;
using DomainSubject = Concordat.Domain.Registry.Subject;

namespace Concordat.Application.Tests.TestSupport;

/// <summary>Terse constructors, so the tests read as behaviour rather than plumbing.</summary>
internal static class Build
{
    /// <summary>A fixed instant, so a timestamp is never a source of flake.</summary>
    public static readonly DateTimeOffset At = new(2026, 8, 13, 9, 0, 0, TimeSpan.Zero);

    private static readonly JsonSchemaCanonicalizer Canonicalizer = new();
    private static readonly JsonSchemaReferenceExtractor Extractor = new();

    public static ActorId Actor(string value = "alice") => ActorId.Create(value).Value;

    public static SubjectName Name(string value = "acme.orders.OrderCreated") =>
        SubjectName.Create(value).Value;

    public static DomainSubject Subject(
        EnvironmentId environment,
        string name = "acme.orders.OrderCreated",
        SchemaFormat format = SchemaFormat.Json,
        CompatibilityPolicy? policy = null,
        ContentModel contentModel = ContentModel.Open) =>
        DomainSubject.Create(
            environment, Name(name), format, Actor(), policy, At, contentModel).Value;

    /// <summary>
    /// Canonicalises, extracts references and derives the id exactly as the evaluator does.
    /// </summary>
    /// <remarks>
    /// Reproducing the evaluator's pipeline rather than inventing an id is what makes the
    /// arranged history line up with what a handler computes for the same body — the
    /// idempotent-at-the-tip path depends on the two agreeing.
    /// </remarks>
    public static Schema JsonSchema(string body)
    {
        var canonical = Canonicalizer.Canonicalize(body);
        Assert.True(canonical.IsSuccess, canonical.Error?.Message);

        var references = Extractor.Extract(canonical.Value);
        Assert.True(references.IsSuccess, references.Error?.Message);

        var id = SchemaIdComputer.Compute(SchemaFormat.Json, canonical.Value, references.Value);

        var schema = Schema.Create(id, SchemaFormat.Json, canonical.Value, references.Value);
        Assert.True(schema.IsSuccess, schema.Error?.Message);

        return schema.Value;
    }

    /// <summary>A schema in a format nothing in this project can canonicalise.</summary>
    /// <remarks>
    /// Only for the format-mismatch branch, which needs a schema the evaluator would never
    /// have produced for a JSON subject.
    /// </remarks>
    public static Schema AvroSchema(string body = """{"type":"record","name":"X","fields":[]}""")
    {
        var id = SchemaIdComputer.Compute(SchemaFormat.Avro, body);
        var schema = Schema.Create(id, SchemaFormat.Avro, body);
        Assert.True(schema.IsSuccess, schema.Error?.Message);

        return schema.Value;
    }

    /// <summary>
    /// Registers a version on a subject and stores its schema, so prior loading finds it.
    /// </summary>
    /// <remarks>
    /// The verdict is supplied rather than computed: arranging a version that is
    /// <see cref="VersionStatus.AwaitingApproval"/> means asserting it is breaking, and going
    /// through the engine to arrange that would make every test depend on the engine's answer
    /// for a pair of documents chosen for a different reason.
    /// </remarks>
    public static Schema Register(
        this DomainSubject subject,
        FakeSchemas schemas,
        string body,
        bool breaking = false,
        string? semver = null)
    {
        var schema = JsonSchema(body);
        var policy = subject.EffectivePolicy(CompatibilityPolicy.Default);

        var verdict = breaking
            ? CompatibilityVerdict.Breaking(policy)
            : CompatibilityVerdict.Compatible(policy);

        var label = semver is null ? (SemanticVersion?)null : SemanticVersion.Create(semver).Value;

        var outcome = subject.RegisterVersion(schema, verdict, label, null, Actor(), At);
        Assert.True(outcome.IsSuccess, outcome.Error?.Message);

        schemas.Seed(schema);
        return schema;
    }

    /// <summary>Rejects a pending version, which is how a Rejected one comes to exist.</summary>
    public static DomainSubject RejectVersion(this DomainSubject subject, int ordinal)
    {
        var rejected = subject.Reject(ordinal, Actor("bob"), At);
        Assert.True(rejected.IsSuccess, rejected.Error?.Message);

        return subject;
    }

    /// <summary>A schema whose only content is Concordat references to other subjects.</summary>
    /// <remarks>
    /// Composed with escapes rather than a raw literal: a raw literal ending in a run of
    /// closing braces cannot carry an interpolation hole without four dollar signs.
    /// </remarks>
    public static Schema Referring(params string[] references) =>
        JsonSchema(
            "{\"allOf\":[" +
            string.Join(',', references.Select(r => $"{{\"$ref\":\"{r}\"}}")) +
            "]}");
}
