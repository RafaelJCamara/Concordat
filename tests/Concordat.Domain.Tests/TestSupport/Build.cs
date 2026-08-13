using Concordat.Domain.Registry;

namespace Concordat.Domain.Tests.TestSupport;

/// <summary>Terse constructors for domain objects, so tests read as behaviour not plumbing.</summary>
internal static class Build
{
    public static readonly DateTimeOffset At = new(2026, 8, 13, 9, 0, 0, TimeSpan.Zero);

    public static readonly CompatibilityPolicy BackwardWireJson =
        new(CompatibilityMode.Backward, CompatibilitySurface.WireJson);

    public static ActorId Actor(string value = "alice") => ActorId.Create(value).Value;

    public static SubjectName Name(string value = "acme.orders.OrderCreated") =>
        SubjectName.Create(value).Value;

    /// <summary>A distinct, valid 32-character lowercase hex id per seed.</summary>
    public static SchemaId Id(int seed) =>
        SchemaId.Create(seed.ToString("x32", System.Globalization.CultureInfo.InvariantCulture)).Value;

    public static Schema Schema(int seed, SchemaFormat format = SchemaFormat.Json) =>
        Registry.Schema.Create(Id(seed), format, $$"""{"type":"object","x":{{seed}}}""").Value;

    public static Subject Subject(
        CompatibilityPolicy? policy = null,
        SchemaFormat format = SchemaFormat.Json) =>
        Registry.Subject.Create(
            EnvironmentId.New(), Name(), format, Actor(), policy, At).Value;

    public static CompatibilityVerdict Ok(CompatibilityPolicy? policy = null) =>
        CompatibilityVerdict.Compatible(policy ?? BackwardWireJson);

    public static CompatibilityVerdict Breaks(CompatibilityPolicy? policy = null) =>
        CompatibilityVerdict.Breaking(policy ?? BackwardWireJson);

    /// <summary>Registers a compatible version and returns the subject, for arranging state.</summary>
    public static Subject WithVersion(this Subject subject, int seed, string? semver = null)
    {
        var label = semver is null ? (SemanticVersion?)null : SemanticVersion.Create(semver).Value;
        var outcome = subject.RegisterVersion(Schema(seed), Ok(), label, null, Actor(), At);
        Assert.True(outcome.IsSuccess, outcome.Error?.Message);
        return subject;
    }
}
