using Concordat.Domain.Registry;

namespace Concordat.Application.Abstractions;

/// <summary>What a health check found.</summary>
/// <param name="Reachable">Whether the broker accepted a connection.</param>
/// <param name="Error">Why it did not, when it did not.</param>
public sealed record BrokerProbeResult(bool Reachable, string? Error);

/// <summary>
/// Attempts a connection to a broker (M7.1).
/// </summary>
/// <remarks>
/// <para>
/// A port, because the Application layer must not know about AMQP. It also means the check can
/// be substituted in tests without a broker, which matters: every other test in this layer runs
/// in milliseconds and a real connection attempt with a timeout does not.
/// </para>
/// <para>
/// <b>The probe reports, it does not decide.</b> An unreachable broker is recorded on the
/// connection and shown to an operator; it never blocks registration or changes a compatibility
/// verdict. Concordat is a registry, and a registry that stopped accepting schemas because a
/// broker was down would have converted someone else's outage into its own.
/// </para>
/// </remarks>
public interface IBrokerHealthProbe
{
    /// <summary>Tries to reach a broker.</summary>
    /// <param name="broker">The connection to test.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What happened.</returns>
    Task<BrokerProbeResult> ProbeAsync(
        BrokerConnection broker, CancellationToken cancellationToken);
}
