using Concordat.Application.Governance;
using Concordat.Application.Tests.TestSupport;
using Concordat.Domain.Governance;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;

namespace Concordat.Application.Tests;

/// <summary>
/// Reading the audit trail when it holds an action this build was never taught (ADR-036).
/// </summary>
/// <remarks>
/// The trail outlives the build that wrote a row: a rollback past a change that added an action
/// used to make the whole read throw, for as long as such a row existed. These pin the two
/// halves of what replaced that. A row this build cannot name is <em>returned</em>, whole and as
/// stored. And a <em>filter</em> naming such an action is still refused, because the filter is the
/// caller's claim about a vocabulary and an empty answer to a mistyped one reads as "nothing
/// happened".
/// </remarks>
public class AuditQueryHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static ActorId Actor => ActorId.Create("someone@example.com").Value;

    private readonly FakeEnvironmentStore _environments = new();
    private readonly RecordingAuditLog _audit = new();

    private QueryAuditHandler Handler => new(_environments, _audit);

    private static AuditEntry FromANewerBuild(string token = "A_FUTURE_ACTION") =>
        AuditEntry.FromStorage(Guid.CreateVersion7(), null, token, Actor, "acme.Orders", "later", Now);

    [Fact]
    public async Task ARowWhoseActionThisBuildDoesNotKnowIsReturnedBesideTheOnesItDoes()
    {
        _audit.Append(AuditEntry.Record(null, AuditAction.SubjectCreated, Actor, "acme.Orders", Now));
        _audit.Append(FromANewerBuild());

        var result = await Handler.HandleAsync(
            new QueryAuditQuery(null, null, null, null, null, null), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(["SUBJECT_CREATED", "A_FUTURE_ACTION"], result.Value.Select(e => e.ActionToken));
        Assert.Null(result.Value[1].Action);
        Assert.Equal("later", result.Value[1].Detail);
    }

    [Fact]
    public async Task AFilterForAKnownActionIsPassedOnAsThatAction()
    {
        var result = await Handler.HandleAsync(
            new QueryAuditQuery(null, "subject_policy_changed", null, null, null, null),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(AuditAction.SubjectPolicyChanged, Assert.Single(_audit.Queries).Action);
    }

    [Theory]
    [InlineData("A_FUTURE_ACTION")]
    [InlineData("SUBJECT_DELETED")]
    public async Task AFilterNamingAnActionThisBuildDoesNotKnowIsRefusedAndNothingIsRead(string token)
    {
        // Decided in ADR-036, and not an oversight: the rows are reachable by the unfiltered
        // read, and a filter that accepted any text would answer a typo with an empty list, which
        // is the one answer an audit query must never give by accident. A filter for a stored
        // unknown token would also be a scan of a table that only grows, for an action no
        // index covers, on a request that any read scope may make.
        var result = await Handler.HandleAsync(
            new QueryAuditQuery(null, token, null, null, null, null), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.AuditFilterInvalid, result.Error!.Code);
        Assert.Empty(_audit.Queries);
    }
}
