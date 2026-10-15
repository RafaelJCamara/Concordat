using Concordat.Application.Abstractions;
using Concordat.Application.Registry;
using Concordat.Domain.Registry;

namespace Concordat.Application.Tests;

/// <summary>
/// What the approval-queue handler decides, which is the page: how big, and from where (ADR-042).
/// The ordering and the counting are the reader's and are proved against PostgreSQL in
/// <c>PendingApprovalReaderTests</c>; a fake reader cannot order or count.
/// </summary>
public class PendingApprovalsHandlerTests
{
    private readonly RecordingReader _reader = new();

    private Task<Domain.Results.Result<PendingApprovals>> HandleAsync(
        int? limit = null, int? offset = null, EnvironmentId? environment = null) =>
        new ListPendingApprovalsHandler(_reader).HandleAsync(
            new ListPendingApprovalsQuery(environment ?? EnvironmentId.New(), limit, offset),
            CancellationToken.None);

    [Fact]
    public async Task ItAsksTheReaderAboutTheEnvironmentItWasGiven()
    {
        var environment = EnvironmentId.New();

        await HandleAsync(environment: environment);

        Assert.Equal(environment, _reader.Environment);
    }

    [Fact]
    public async Task WithNoPageNamed_ItAsksForTheFirstFifty()
    {
        await HandleAsync();

        Assert.Equal(50, _reader.Limit);
        Assert.Equal(0, _reader.Offset);
        Assert.Equal(50, ListPendingApprovalsQuery.DefaultLimit);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(75, 75)]
    [InlineData(200, 200)]
    [InlineData(201, 200)]
    [InlineData(int.MaxValue, 200)]
    public async Task ThePageSizeIsClampedAndNeverRefused(int asked, int expected)
    {
        // A page size is a preference and a bound on cost: the nearest page the registry will give
        // is a better answer than an error, and it is what the audit read does with its limit.
        var result = await HandleAsync(limit: asked);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(expected, _reader.Limit);
        Assert.Equal(200, ListPendingApprovalsQuery.MaxLimit);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(120, 120)]
    public async Task ANegativeOffsetStartsAtTheOldest(int? asked, int expected)
    {
        await HandleAsync(offset: asked);

        Assert.Equal(expected, _reader.Offset);
    }

    [Fact]
    public async Task AnEnvironmentNothingWasWrittenTo_IsAnEmptyQueueNotAnError()
    {
        _reader.Answer = new PendingApprovals(0, []);

        var result = await HandleAsync();

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(0, result.Value.Total);
        Assert.Empty(result.Value.Items);
    }

    [Fact]
    public async Task TheReadersAnswerIsReturnedAsItIs()
    {
        var answer = new PendingApprovals(
            3,
            [
                new PendingApproval(
                    "acme.Order", 4, "2.0.0", "why", DateTimeOffset.UnixEpoch, "alice", 3),
            ]);
        _reader.Answer = answer;

        var result = await HandleAsync();

        Assert.Same(answer, result.Value);
    }

    private sealed class RecordingReader : IPendingApprovalReader
    {
        public PendingApprovals Answer { get; set; } = new(0, []);

        public EnvironmentId? Environment { get; private set; }

        public int Limit { get; private set; }

        public int Offset { get; private set; }

        public Task<PendingApprovals> ReadAsync(
            EnvironmentId environmentId,
            int limit,
            int offset,
            CancellationToken cancellationToken)
        {
            Environment = environmentId;
            Limit = limit;
            Offset = offset;
            return Task.FromResult(Answer);
        }
    }
}
