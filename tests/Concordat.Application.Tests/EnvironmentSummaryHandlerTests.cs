using Concordat.Application.Abstractions;
using Concordat.Application.Registry;
using Concordat.Application.Tests.TestSupport;
using Concordat.Domain.Registry;
using Microsoft.Extensions.Time.Testing;

namespace Concordat.Application.Tests;

/// <summary>
/// What the overview handler decides, which is little by design (ADR-037): which environment,
/// how many rows, and how far back "recent" reaches. The counting is the reader's and is proved
/// against PostgreSQL in <c>EnvironmentSummaryReaderTests</c>; a fake reader cannot count.
/// </summary>
public class EnvironmentSummaryHandlerTests
{
    private static readonly EnvironmentSummary Empty = new(
        new SubjectTotals(0, 0, 0),
        new VersionTotals(0, 0, 0, 0),
        [],
        [],
        [
            new EnforcementTier(EnforcementMode.Off, 0, 0, 0),
            new EnforcementTier(EnforcementMode.Monitor, 0, 0, 0),
            new EnforcementTier(EnforcementMode.Enforce, 0, 0, 0),
        ],
        new ViolationTotals(0, 0));

    private readonly FakeTimeProvider _clock = new(Build.At);
    private readonly RecordingReader _reader = new();

    private Task<Domain.Results.Result<EnvironmentSummary>> HandleAsync(EnvironmentId environment) =>
        new GetEnvironmentSummaryHandler(_reader, _clock).HandleAsync(
            new GetEnvironmentSummaryQuery(environment), CancellationToken.None);

    [Fact]
    public async Task ItAsksTheReaderAboutTheEnvironmentItWasGiven()
    {
        var environment = EnvironmentId.New();

        await HandleAsync(environment);

        Assert.Equal(environment, _reader.Environment);
    }

    [Fact]
    public async Task ItBoundsTheTwoListsItAsksFor()
    {
        // The bound is what keeps the route's cost independent of how many subjects there are, so
        // it is the handler's to fix and not a caller's to choose.
        await HandleAsync(EnvironmentId.New());

        Assert.Equal(GetEnvironmentSummaryQuery.NewestSubjectsLimit, _reader.NewestSubjects);
        Assert.Equal(GetEnvironmentSummaryQuery.BreakingChangesLimit, _reader.BreakingChanges);
        Assert.Equal(6, GetEnvironmentSummaryQuery.NewestSubjectsLimit);
        Assert.Equal(5, GetEnvironmentSummaryQuery.BreakingChangesLimit);
    }

    [Fact]
    public async Task RecentMeansSeenInTheLastTwentyFourHours_ByTheClockItWasGiven()
    {
        // The reader is handed a timestamp rather than a duration, so the window is the handler's
        // clock and a test (or a skewed database clock) cannot move it.
        await HandleAsync(EnvironmentId.New());

        Assert.Equal(Build.At.AddHours(-24), _reader.ViolationsSince);

        _clock.Advance(TimeSpan.FromHours(5));
        await HandleAsync(EnvironmentId.New());

        Assert.Equal(Build.At.AddHours(5).AddHours(-24), _reader.ViolationsSince);
    }

    [Fact]
    public async Task AnEnvironmentNothingWasWrittenTo_IsAnEmptyOverviewNotAnError()
    {
        // The landing page of a fresh install, before the first subject exists. Refusing it would
        // show an error at the moment the page should be offering to create that subject, and it is
        // what the subject list answers for the same environment.
        _reader.Answer = Empty;

        var result = await HandleAsync(EnvironmentId.New());

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Same(Empty, result.Value);
    }

    [Fact]
    public async Task TheReadersAnswerIsReturnedAsItIs()
    {
        var answer = Empty with { Violations = new ViolationTotals(7, 2) };
        _reader.Answer = answer;

        var result = await HandleAsync(EnvironmentId.New());

        Assert.Same(answer, result.Value);
    }

    private sealed class RecordingReader : IEnvironmentSummaryReader
    {
        public EnvironmentSummary Answer { get; set; } = Empty;

        public EnvironmentId? Environment { get; private set; }

        public int NewestSubjects { get; private set; }

        public int BreakingChanges { get; private set; }

        public DateTimeOffset ViolationsSince { get; private set; }

        public Task<EnvironmentSummary> ReadAsync(
            EnvironmentId environmentId,
            int newestSubjects,
            int breakingChanges,
            DateTimeOffset violationsSince,
            CancellationToken cancellationToken)
        {
            Environment = environmentId;
            NewestSubjects = newestSubjects;
            BreakingChanges = breakingChanges;
            ViolationsSince = violationsSince;
            return Task.FromResult(Answer);
        }
    }
}
