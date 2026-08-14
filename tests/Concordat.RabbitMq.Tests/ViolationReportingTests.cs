using Concordat.Client;
using Concordat.Domain.Results;

namespace Concordat.RabbitMq.Tests;

/// <summary>
/// Counting violations for the registry (decision 25).
/// </summary>
/// <remarks>
/// `ENFORCEMENT_VIOLATION` was in the published notification catalogue from M7.5 and nothing
/// could ever raise it: a violation happens in the SDK, on the publisher's machine, against a
/// contract the registry never sees the traffic for. These tests pin the half that closes it
/// without putting the registry back on the delivery path.
/// </remarks>
public class ViolationReportingTests
{
    private static EnforcementEvent Event(
        EnforcementOutcome outcome,
        EnforcementSide side = EnforcementSide.Publish,
        string? queue = null) =>
        new(
            side,
            outcome,
            ConcordatCodes.PayloadInvalid,
            "#/total: NumberExpected",
            "acme.Order",
            null,
            "orders",
            "order.created",
            queue);

    [Fact]
    public void AViolationIsCountedAndForwarded()
    {
        var inner = new EnforcementCounters();
        var client = new FakeClient();

        new ViolationReportingObserver(inner, client)
            .Observe(Event(EnforcementOutcome.Blocked));

        // Forwarded, because taking a host's observability away in order to add reporting would
        // be a poor trade.
        Assert.Equal(1, inner.Count(EnforcementSide.Publish, EnforcementOutcome.Blocked));

        var reported = Assert.Single(client.Violations);
        Assert.Equal("PUBLISH", reported.Side);
        Assert.Equal("orders/order.created", reported.Route);
        Assert.Equal("acme.Order", reported.Subject);
    }

    [Theory]
    [InlineData(EnforcementOutcome.Valid)]
    [InlineData(EnforcementOutcome.Unenforced)]
    public void ConformingAndUnenforcedMessagesAreNotReported(EnforcementOutcome outcome)
    {
        // Unenforced is an un-instrumented publisher or a registry blip, not a violation. A
        // brownfield estate would otherwise report every message it sends.
        var client = new FakeClient();

        new ViolationReportingObserver(new EnforcementCounters(), client).Observe(Event(outcome));

        Assert.Empty(client.Violations);
    }

    [Fact]
    public void AConsumeViolationIsKeyedByTheQueueNotTheExchange()
    {
        // A publish contract binds an exchange and a routing key; a consume contract binds a
        // queue. A delivery carries the exchange it was published to, which is not what its
        // consumer's contract names -- reporting by exchange would file the violation under a
        // route nobody wrote a rule for.
        var client = new FakeClient();

        new ViolationReportingObserver(new EnforcementCounters(), client).Observe(
            Event(EnforcementOutcome.Quarantined, EnforcementSide.Consume, "orders-worker"));

        Assert.Equal("orders-worker", Assert.Single(client.Violations).Route);
    }

    [Fact]
    public void RepeatedViolationsAggregateToOneEntryWithACount()
    {
        // The property that makes this safe to call on the delivery path. A broken publisher
        // emits thousands a second; one entry each would be a denial of service written by our
        // own SDK.
        var reporter = new ViolationReporter();
        var key = new ViolationKey("PUBLISH", "orders/order.created", "acme.Order", "payload_invalid", "nope");

        for (var i = 0; i < 10_000; i++)
        {
            reporter.Record(key);
        }

        Assert.Equal(1, reporter.Pending);

        var drained = Assert.Single(reporter.Drain());
        Assert.Equal(10_000, drained.Count);
    }

    [Fact]
    public void DistinctViolationsAreBoundedAndTheOverflowIsCounted()
    {
        // A client that runs itself out of memory recording that something else is broken has
        // made the outage worse.
        var reporter = new ViolationReporter();

        for (var i = 0; i < ViolationReporter.MaxDistinct + 50; i++)
        {
            reporter.Record(new ViolationKey("PUBLISH", $"orders/key.{i}", null, "payload_invalid", "nope"));
        }

        Assert.Equal(ViolationReporter.MaxDistinct, reporter.Pending);
        Assert.Equal(50, reporter.Dropped);
    }

    [Fact]
    public void DrainingEmptiesTheCounterSoASecondFlushSendsNothing()
    {
        var reporter = new ViolationReporter();
        reporter.Record(new ViolationKey("PUBLISH", "orders/a", null, "payload_invalid", "nope"));

        Assert.Single(reporter.Drain());
        Assert.Empty(reporter.Drain());
    }
}
