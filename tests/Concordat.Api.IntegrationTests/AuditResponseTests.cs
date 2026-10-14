using Concordat.Domain.Governance;
using Concordat.Domain.Registry;

namespace Concordat.Api.IntegrationTests;

/// <summary>
/// How an audit entry is spelt on the wire, with no database in the way (ADR-036).
/// </summary>
/// <remarks>
/// Plain unit tests that live here because <see cref="AuditResponse"/> is this host's type. The
/// rule they hold is the one the web app relies on: an action this build knows is spelt from the
/// catalogue, whatever the row holds, and one it does not is spelt exactly as the row holds it.
/// </remarks>
public class AuditResponseTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static ActorId Actor => ActorId.Create("someone@example.com").Value;

    [Fact]
    public void EveryKnownActionIsSpeltByTheCatalogue()
    {
        foreach (var action in Enum.GetValues<AuditAction>())
        {
            var response = AuditResponse.From(AuditEntry.Record(null, action, Actor, "x", At));

            Assert.Equal(AuditTokens.For(action), response.Action);
        }
    }

    [Fact]
    public void AKnownActionStoredInAnotherCaseIsStillSpeltByTheCatalogue()
    {
        // "Known actions are byte-identical on the wire" is a property of the enum's spelling, not
        // of whatever a hand-edited row happens to contain. It was true before this change, because
        // the enum was the only thing the projection had; it is pinned now that the row's own text
        // is available to be echoed.
        var entry = AuditEntry.FromStorage(
            Guid.CreateVersion7(), null, "subject_created", Actor, "x", null, At);

        Assert.Equal("SUBJECT_CREATED", AuditResponse.From(entry).Action);
    }

    [Theory]
    [InlineData("A_FUTURE_ACTION")]
    [InlineData("a_future_action")]
    [InlineData("constructor")]
    public void AnActionThisBuildDoesNotKnowIsSpeltExactlyAsStored(string token)
    {
        var entry = AuditEntry.FromStorage(
            Guid.CreateVersion7(), null, token, Actor, "acme.Orders", "later", At);

        var response = AuditResponse.From(entry);

        Assert.Equal(token, response.Action);
        Assert.Equal("acme.Orders", response.Target);
        Assert.Equal("later", response.Detail);
        Assert.Equal(At, response.At);
    }
}
