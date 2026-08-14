using Concordat.Client;

namespace Concordat.RabbitMq;

/// <summary>
/// Forwards enforcement decisions to an inner observer and counts the violations for the
/// registry (decision 25).
/// </summary>
/// <remarks>
/// <para>
/// <b>A decorator rather than a replacement.</b> Whatever a host already wired up — counters, a
/// logger, a metrics exporter — keeps working, because taking somebody's observability away in
/// order to add reporting would be a poor trade.
/// </para>
/// <para>
/// <b>Only outcomes a contract actually refused.</b> <c>Unenforced</c> is not a violation: it is
/// an un-instrumented publisher or a registry blip, which is worth counting locally and is not
/// worth telling the registry about — a brownfield estate would report every message it sends.
/// </para>
/// </remarks>
public sealed class ViolationReportingObserver(
    IEnforcementObserver inner, IConcordatClient client) : IEnforcementObserver
{
    /// <inheritdoc />
    public void Observe(EnforcementEvent decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        inner.Observe(decision);

        if (decision.Outcome is EnforcementOutcome.Valid or EnforcementOutcome.Unenforced)
        {
            return;
        }

        // The route as the CONTRACT names it, which differs by side. A publish contract binds an
        // exchange and a routing key; a consume contract binds a queue. Reporting a consume
        // violation by the exchange the message happened to be published to would file it under
        // a route nobody wrote a rule for, and the row would never match the binding it broke.
        var route = decision.Side is EnforcementSide.Publish
            ? $"{decision.Exchange}/{decision.RoutingKey}"
            : decision.Queue ?? string.Empty;

        client.RecordViolation(new ViolationKey(
            decision.Side is EnforcementSide.Publish ? "PUBLISH" : "CONSUME",
            string.IsNullOrEmpty(route) ? "(unknown)" : route,
            decision.Subject,
            decision.Code ?? "payload_invalid",
            decision.Detail));
    }
}
