using Concordat.Application.Registry;
using Concordat.Application.Tests.TestSupport;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;

namespace Concordat.Application.Tests;

/// <summary>
/// The dry run: what it refuses, what it never writes, and the semantic version it proposes.
/// </summary>
/// <remarks>
/// The suggestion is the part with a correctness obligation nobody would guess at: it has to
/// be a label the aggregate will then <em>accept</em>. A suggestion the registry immediately
/// refuses turns <c>concordat check</c> from a guide into a trap.
/// </remarks>
public class CheckCompatibilityHandlerTests
{
    private const string V1 = """{"type":"object","properties":{"id":{"type":"string"}}}""";

    private const string V1Required =
        """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}""";

    private const string V1PlusOptional =
        """{"type":"object","properties":{"id":{"type":"string"},"note":{"type":"string"}}}""";

    private readonly EnvironmentId _environment = EnvironmentId.New();
    private readonly FakeSubjects _subjects = new();
    private readonly FakeSchemas _schemas = new();
    private readonly RecordingUnitOfWork _unitOfWork = new();
    private readonly RecordingEvaluator _evaluator = new(new CompatibilityEvaluator(new JsonFormats()));

    private Task<Result<CompatibilityCheckResult>> CheckAsync(
        string? body, string subject = "acme.orders.OrderCreated") =>
        new CheckCompatibilityHandler(_subjects, _schemas, _evaluator).HandleAsync(
            new CheckCompatibilityQuery(_environment, subject, body), CancellationToken.None);

    [Fact]
    public async Task AnInvalidSubjectName_IsRefusedBeforeTheRepositoryIsTouched()
    {
        var result = await CheckAsync(V1, subject: "..");

        Assert.Equal(ConcordatCodes.SubjectNameInvalid, result.Error!.Code);
        Assert.Equal(0, _subjects.Finds);
    }

    [Fact]
    public async Task AnUnknownSubject_IsSubjectNotFound()
    {
        var result = await CheckAsync(V1);

        Assert.Equal(ConcordatCodes.SubjectNotFound, result.Error!.Code);
    }

    [Fact]
    public async Task ADryRunStagesNoSchemaAndCommitsNothing()
    {
        // The handler is not even given a unit of work, and this test is what keeps it that
        // way. A check that persisted the schema it was asked about would populate the store
        // with every proposal any CI job ever tried, including the rejected ones.
        var subject = Build.Subject(_environment);
        subject.Register(_schemas, V1);
        _subjects.Seed(subject);

        var result = await CheckAsync(V1Required);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.False(result.Value.Compatible);
        Assert.Equal(0, _schemas.Staged);
        Assert.Equal(0, _unitOfWork.Saves);
        Assert.Single(subject.Versions);
    }

    [Fact]
    public async Task AMalformedBody_PropagatesTheCanonicalisersCode()
    {
        _subjects.Seed(Build.Subject(_environment));

        var result = await CheckAsync("{\"type\":");

        Assert.Equal(ConcordatCodes.SchemaMalformed, result.Error!.Code);
    }

    [Fact]
    public async Task AnUnimplementedDialect_IsRefusedRatherThanCheckedUnderTheWrongRules()
    {
        _subjects.Seed(Build.Subject(_environment));

        var result = await CheckAsync(
            """{"$schema":"http://json-schema.org/draft-07/schema#","type":"object"}""");

        Assert.Equal(ConcordatCodes.SchemaDialectUnsupported, result.Error!.Code);
    }

    [Fact]
    public async Task RejectedVersionsAreExcludedFromTheHistoryTheCheckComparesAgainst()
    {
        // The check must answer the same question registration will. If the two disagreed on
        // which versions count, CI would go green on a change the registry then refuses.
        var subject = Build.Subject(_environment);
        subject.Register(_schemas, V1);
        subject.Register(_schemas, V1Required, breaking: true);
        subject.RejectVersion(2);
        _subjects.Seed(subject);

        await CheckAsync(V1PlusOptional);

        Assert.Equal([1], _evaluator.Priors.Select(p => p.Ordinal));
    }

    [Fact]
    public async Task TheSuggestionIsTheNextPatchWhenNothingDiverges()
    {
        // Adding an optional property is a forward-direction finding, which a Backward policy
        // does not look at, so there is no divergence at all.
        var subject = Build.Subject(_environment);
        subject.Register(_schemas, V1, semver: "1.2.3");
        _subjects.Seed(subject);

        var result = await CheckAsync(V1PlusOptional);

        Assert.True(result.Value.Compatible);
        Assert.Equal("1.2.4", result.Value.SuggestedSemver);
    }

    [Fact]
    public async Task TheSuggestionIsTheNextMinorWhenADivergenceIsToleratedRatherThanAbsent()
    {
        // 'integer' widening to 'number' is a Source-surface finding: every document still
        // validates, but generated code changes from an integral type to a floating-point one.
        // The default WireJson policy permits it — and a consumer that regenerates its models
        // still has work to do, which is what separates MINOR from PATCH.
        var subject = Build.Subject(_environment);
        subject.Register(
            _schemas, """{"type":"object","properties":{"n":{"type":"integer"}}}""", semver: "1.2.3");
        _subjects.Seed(subject);

        var result = await CheckAsync("""{"type":"object","properties":{"n":{"type":"number"}}}""");

        Assert.True(result.Value.Compatible);
        Assert.NotEmpty(result.Value.Report.AllDivergences);
        Assert.Empty(result.Value.Report.BreakingChanges);
        Assert.Equal("1.3.0", result.Value.SuggestedSemver);
    }

    [Fact]
    public async Task TheSuggestionIsTheNextMajorWhenThePolicyIsViolated()
    {
        var subject = Build.Subject(_environment);
        subject.Register(_schemas, V1, semver: "1.2.3");
        _subjects.Seed(subject);

        var result = await CheckAsync(V1Required);

        Assert.False(result.Value.Compatible);
        Assert.Equal("2.0.0", result.Value.SuggestedSemver);
    }

    [Fact]
    public async Task AnUnlabelledHistoryGetsOneDotZeroDotZeroRatherThanZeroDotOneDotZero()
    {
        // It has to clear the aggregate's bar: a breaking change with no previous label is
        // refused unless the label carries a non-zero major. Suggesting "0.1.0" here would be
        // advice the registry then rejects.
        var subject = Build.Subject(_environment);
        subject.Register(_schemas, V1);
        _subjects.Seed(subject);

        var result = await CheckAsync(V1Required);

        Assert.False(result.Value.Compatible);
        Assert.Equal("1.0.0", result.Value.SuggestedSemver);
    }

    [Fact]
    public async Task LabelsOnRejectedVersionsAreIgnoredWhenChoosingTheOneToBuildOn()
    {
        // The aggregate ignores them too, so counting a rejected 2.0.0 here would suggest
        // 2.0.1 and then have registration refuse it for not increasing on... nothing.
        var subject = Build.Subject(_environment);
        subject.Register(_schemas, V1, semver: "1.0.0");
        subject.Register(_schemas, V1Required, breaking: true, semver: "2.0.0");
        subject.RejectVersion(2);
        _subjects.Seed(subject);

        var result = await CheckAsync(V1PlusOptional);

        Assert.Equal("1.0.1", result.Value.SuggestedSemver);
    }

    [Fact]
    public async Task AFirstVersionGetsNoSuggestionAtAll()
    {
        // With no priors the engine reports no bump, and no bump means no advice. Worth
        // knowing rather than assuming: `concordat check` on a brand-new subject cannot tell
        // an author to label it 1.0.0, so any such prompt has to come from the client.
        _subjects.Seed(Build.Subject(_environment));

        var result = await CheckAsync(V1);

        Assert.True(result.Value.Compatible);
        Assert.Null(result.Value.SuggestedSemver);
    }

    [Fact]
    public async Task ASubjectWithCheckingDisabledGetsNoSuggestionEither()
    {
        // CompatibilityMode.None short-circuits the engine, so there are no divergences to
        // reason from. The suggestion is derived from the verdict, never from the documents.
        var subject = Build.Subject(
            _environment,
            policy: new CompatibilityPolicy(CompatibilityMode.None, CompatibilitySurface.WireJson));
        subject.Register(_schemas, V1, semver: "1.0.0");
        _subjects.Seed(subject);

        var result = await CheckAsync(V1Required);

        Assert.True(result.Value.Compatible);
        Assert.Null(result.Value.SuggestedSemver);
    }

    [Fact]
    public async Task ThePolicyReportedIsTheSubjectsEffectiveOneNotTheEnvironmentDefault()
    {
        // The caller is told which rules produced the verdict. Reporting the default while
        // evaluating under something else would make a surprising answer unexplainable.
        var policy = new CompatibilityPolicy(CompatibilityMode.Full, CompatibilitySurface.Source);
        var subject = Build.Subject(_environment, policy: policy);
        subject.Register(_schemas, V1);
        _subjects.Seed(subject);

        var result = await CheckAsync(V1PlusOptional);

        Assert.Equal(policy, result.Value.Policy);
        Assert.Equal(policy, _evaluator.Policy);
    }

    [Fact]
    public async Task ARetiredSubjectStillAnswersTheDryRun()
    {
        // Documented rather than endorsed: the check speaks only about compatibility, so it
        // reports "compatible" for a subject whose registration will refuse with
        // subject_retired. A pipeline that gates on the check alone goes green and then fails
        // at the publish step.
        var subject = Build.Subject(_environment);
        subject.Register(_schemas, V1);
        Assert.True(subject.Retire().IsSuccess);
        _subjects.Seed(subject);

        var result = await CheckAsync(V1PlusOptional);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(result.Value.Compatible);
    }

    [Fact]
    public async Task TheSchemaIdReportedIsTheOneRegistrationWouldMint()
    {
        // The whole point of a dry run: the id it names has to be the id that turns up if the
        // author then registers. They are derived by the same evaluator, and this is the
        // assertion that keeps them derived by the same evaluator.
        _subjects.Seed(Build.Subject(_environment));

        var result = await CheckAsync(V1);

        Assert.Equal(Build.JsonSchema(V1).Id.Value, result.Value.SchemaId);
    }
}
