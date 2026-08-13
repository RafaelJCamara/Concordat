using Concordat.Application.Registry;
using Concordat.Application.Tests.TestSupport;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;
using Concordat.Formats.Abstractions;

namespace Concordat.Application.Tests;

/// <summary>
/// The diff answers "what actually changed", which is a different question from "does the
/// policy allow it".
/// </summary>
public class DiffVersionsHandlerTests
{
    private const string Name = "acme.orders.OrderCreated";
    private const string V1 = """{"type":"object","properties":{"id":{"type":"string"}}}""";

    private const string V1PlusOptional =
        """{"type":"object","properties":{"id":{"type":"string"},"note":{"type":"string"}}}""";

    private const string V1Required =
        """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}""";

    private readonly EnvironmentId _environment = EnvironmentId.New();
    private readonly FakeSubjects _subjects = new();
    private readonly FakeSchemas _schemas = new();

    private Task<Result<DiffResult>> DiffAsync(int from, int to, string name = Name) =>
        new DiffVersionsHandler(_subjects, _schemas, new JsonFormats()).HandleAsync(
            new DiffVersionsQuery(_environment, name, from, to), CancellationToken.None);

    [Fact]
    public async Task AnInvalidSubjectName_IsRefusedBeforeTheRepositoryIsTouched()
    {
        var result = await DiffAsync(1, 2, name: "1.2.3.");

        Assert.Equal(ConcordatCodes.SubjectNameInvalid, result.Error!.Code);
        Assert.Equal(0, _subjects.Finds);
    }

    [Fact]
    public async Task AnUnknownSubject_IsSubjectNotFound()
    {
        var result = await DiffAsync(1, 2);

        Assert.Equal(ConcordatCodes.SubjectNotFound, result.Error!.Code);
    }

    [Fact]
    public async Task AnAbsentFromVersion_IsVersionNotFoundAndNamesTheOrdinal()
    {
        var subject = Build.Subject(_environment);
        subject.Register(_schemas, V1);
        _subjects.Seed(subject);

        var result = await DiffAsync(7, 1);

        Assert.Equal(ConcordatCodes.VersionNotFound, result.Error!.Code);
        Assert.Contains("7", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAbsentToVersion_IsVersionNotFoundToo()
    {
        // Both ends are loaded, and the second is easy to leave unchecked because the first
        // one already proved the subject exists.
        var subject = Build.Subject(_environment);
        subject.Register(_schemas, V1);
        _subjects.Seed(subject);

        var result = await DiffAsync(1, 7);

        Assert.Equal(ConcordatCodes.VersionNotFound, result.Error!.Code);
        Assert.Contains("7", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AVersionPointingAtAnUnstoredSchema_IsSchemaNotFound()
    {
        // Distinct from version_not_found: the version exists and the store has lost what it
        // points at, which is an operational problem rather than a caller mistake.
        var subject = Build.Subject(_environment);
        var registered = subject.RegisterVersion(
            Build.JsonSchema(V1),
            CompatibilityVerdict.Compatible(CompatibilityPolicy.Default),
            null,
            null,
            Build.Actor(),
            Build.At);
        Assert.True(registered.IsSuccess, registered.Error?.Message);
        _subjects.Seed(subject);

        var result = await DiffAsync(1, 1);

        Assert.Equal(ConcordatCodes.SchemaNotFound, result.Error!.Code);
    }

    [Fact]
    public async Task ASubjectWithCheckingDisabled_StillGetsARealDiff()
    {
        // The handler overrides the mode to Full for the comparison. Without that, a subject
        // set to CompatibilityMode.None would report no differences at all — an empty diff for
        // two visibly different documents, which reads as a bug in the diff rather than a
        // consequence of a policy the reader may not even know about.
        var subject = Build.Subject(
            _environment,
            policy: new CompatibilityPolicy(CompatibilityMode.None, CompatibilitySurface.WireJson));
        subject.Register(_schemas, V1);
        subject.Register(_schemas, V1Required, breaking: true);
        _subjects.Seed(subject);

        var result = await DiffAsync(1, 2);

        Assert.NotEmpty(result.Value.Divergences);
    }

    [Fact]
    public async Task ThePolicyReportedIsTheSubjectsOwnNotTheOneUsedToCompare()
    {
        // The comparison runs under Full so both directions are reported, but the caller is
        // told which policy actually governs the subject. Echoing Full back would tell an
        // operator their subject is configured for something it is not.
        var policy = new CompatibilityPolicy(CompatibilityMode.None, CompatibilitySurface.WireJson);
        var subject = Build.Subject(_environment, policy: policy);
        subject.Register(_schemas, V1);
        subject.Register(_schemas, V1PlusOptional);
        _subjects.Seed(subject);

        var result = await DiffAsync(1, 2);

        Assert.Equal(policy, result.Value.Policy);
    }

    [Fact]
    public async Task DivergencesThePolicyToleratesAreReportedAllTheSame()
    {
        // A Source-surface finding is invisible to the default WireJson policy, so it never
        // appears in a compatibility verdict. Filtering it out of the diff too would hide the
        // one difference that matters to a reader whose consumer generates code from the
        // schema.
        var subject = Build.Subject(_environment);
        subject.Register(_schemas, """{"type":"object","properties":{"n":{"type":"integer"}}}""");
        subject.Register(_schemas, """{"type":"object","properties":{"n":{"type":"number"}}}""");
        _subjects.Seed(subject);

        var result = await DiffAsync(1, 2);

        var divergence = Assert.Single(result.Value.Divergences);
        Assert.Equal(BreakingChangeKinds.IntegerWidenedToNumber, divergence.Kind);
        Assert.Equal(CompatibilitySurface.Source, divergence.Surface);
    }

    [Fact]
    public async Task ARejectedVersionCanStillBeDiffed()
    {
        // Rejected proposals are kept so the history of what was attempted survives. Being
        // unable to look at one would defeat the reason for keeping it — reviewing a refused
        // change is exactly when someone wants the diff.
        var subject = Build.Subject(_environment);
        subject.Register(_schemas, V1);
        subject.Register(
            _schemas,
            """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}""",
            breaking: true);
        subject.RejectVersion(2);
        _subjects.Seed(subject);

        var result = await DiffAsync(1, 2);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotEmpty(result.Value.Divergences);
    }

    [Fact]
    public async Task DiffingAVersionAgainstItself_IsIdenticalWithNoDivergences()
    {
        var subject = Build.Subject(_environment);
        subject.Register(_schemas, V1);
        _subjects.Seed(subject);

        var result = await DiffAsync(1, 1);

        Assert.True(result.Value.Identical);
        Assert.Empty(result.Value.Divergences);
    }

    [Fact]
    public async Task TwoOrdinalsHoldingTheSameContentAreIdenticalEvenThoughTheyAreDifferentVersions()
    {
        // Identity is the content-addressed id, not the ordinal. A revert re-registers an older
        // schema under a new ordinal, and a reader comparing the two needs to be told they are
        // the same document rather than left to compare bodies themselves.
        var subject = Build.Subject(_environment);
        subject.Register(_schemas, V1);
        subject.Register(_schemas, V1PlusOptional);
        subject.Register(_schemas, V1);
        _subjects.Seed(subject);

        var result = await DiffAsync(1, 3);

        Assert.True(result.Value.Identical);
        Assert.Equal(result.Value.FromSchemaId, result.Value.ToSchemaId);
    }

    [Fact]
    public async Task DivergencesAreAttributedToTheOrdinalTheyWereComparedAgainst()
    {
        // The handler synthesises the prior with the ordinal the caller asked for. Passing the
        // wrong one would put a plausible but incorrect version number on every finding.
        var subject = Build.Subject(_environment);
        subject.Register(_schemas, V1);
        subject.Register(_schemas, V1PlusOptional);
        subject.Register(_schemas, V1Required, breaking: true);
        _subjects.Seed(subject);

        var result = await DiffAsync(2, 3);

        Assert.Equal(2, Assert.Single(result.Value.Divergences).ConflictsWithVersion);
    }
}
