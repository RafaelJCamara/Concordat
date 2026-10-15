using Concordat.Application.Abstractions;
using Concordat.Application.Registry;
using Concordat.Application.Tests.TestSupport;
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
    private readonly SettableCaller _caller = new();

    private Task<Domain.Results.Result<PendingApprovals>> HandleAsync(
        int? limit = null, int? offset = null, EnvironmentId? environment = null) =>
        new ListPendingApprovalsHandler(_reader, _caller).HandleAsync(
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
                Item("acme.Order", reviewers: []),
            ]);
        _reader.Answer = answer;

        var result = await HandleAsync();

        Assert.Equal(answer.Total, result.Value.Total);
        Assert.Equal("acme.Order", Assert.Single(result.Value.Items).Subject);
    }

    private static PendingApproval Item(string subject, string[] reviewers) =>
        new(subject, 4, "2.0.0", "why", DateTimeOffset.UnixEpoch, "alice", 3, reviewers);

    private void CallerIs(string actor) =>
        _caller.Current = _caller.Current with { Actor = ActorId.Create(actor).Value };

    [Fact]
    public async Task ASubjectNamingNoReviewers_MayBeDecidedByAnyone()
    {
        CallerIs("anyone@example.com");
        _reader.Answer = new PendingApprovals(1, [Item("acme.Open", reviewers: [])]);

        var result = await HandleAsync();

        Assert.True(Assert.Single(result.Value.Items).MayDecide);
    }

    [Fact]
    public async Task ASubjectNamingTheCaller_MayBeDecidedByThem_WhateverTheCase()
    {
        CallerIs("Alice@Example.com");
        _reader.Answer = new PendingApprovals(1, [Item("acme.Named", ["alice@example.com"])]);

        var result = await HandleAsync();

        Assert.True(Assert.Single(result.Value.Items).MayDecide);
    }

    [Fact]
    public async Task ASubjectNamingSomeoneElse_IsNotForTheCallerToDecide()
    {
        CallerIs("mallory@example.com");
        _reader.Answer = new PendingApprovals(1, [Item("acme.Named", ["alice@example.com"])]);

        var result = await HandleAsync();

        var item = Assert.Single(result.Value.Items);
        Assert.False(item.MayDecide);
        Assert.Equal(["alice@example.com"], item.Reviewers);
    }

    [Fact]
    public async Task EachItemIsJudgedBySubject()
    {
        CallerIs("alice@example.com");
        _reader.Answer = new PendingApprovals(
            3,
            [
                Item("acme.Mine", ["alice@example.com"]),
                Item("acme.Theirs", ["bob@example.com"]),
                Item("acme.Open", []),
            ]);

        var result = await HandleAsync();

        Assert.Equal([true, false, true], result.Value.Items.Select(i => i.MayDecide));
    }

    [Fact]
    public async Task AnItemThatNeverMetTheHandlerDenies_NotAllows()
    {
        // The reader leaves the answer for the handler. A default that allowed would turn a refactor
        // that skipped the handler into a queue that offers every decision to everyone.
        Assert.False(Item("acme.Named", ["alice@example.com"]).MayDecide);
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
