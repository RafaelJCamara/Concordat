using Concordat.Application.Abstractions;
using Concordat.Domain.Registry;
using RabbitMQ.Client;

namespace Concordat.Infrastructure;

/// <summary>
/// Opens a real AMQP connection to decide whether a broker is reachable (M7.1).
/// </summary>
/// <remarks>
/// <para>
/// <b>A connection, not a TCP probe.</b> A port that accepts a socket tells you a load
/// balancer answered; it does not tell you the virtual host exists or that the credentials
/// work. Those are exactly the misconfigurations an operator registering a broker wants to
/// find, so the check completes an AMQP handshake against the specific virtual host and then
/// closes.
/// </para>
/// <para>
/// <b>Short timeout, and failure is data.</b> This runs inside a request, so it cannot wait
/// out a default connect timeout; and an unreachable broker is a recorded status rather than
/// an exception, because the caller asked what the state was and "unreachable" is a complete
/// answer to that question.
/// </para>
/// <para>
/// <b>Credentials come from the URI for now.</b> A URI may embed them
/// (<c>amqp://user:pass@host</c>), which is what the quickstart uses. Stored, encrypted
/// credentials arrive in M7.2, and this is where they will be resolved.
/// </para>
/// </remarks>
public sealed class RabbitMqBrokerHealthProbe : IBrokerHealthProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public async Task<BrokerProbeResult> ProbeAsync(
        BrokerConnection broker, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(broker);

        // Built from parts rather than by assigning ConnectionFactory.Uri. Setting Uri also
        // sets host, port, credentials and virtual host from the URI, so mixing it with
        // explicit properties means the result depends on assignment order — which is exactly
        // the kind of thing that works until someone reorders an object initialiser. Naming
        // each field is longer and cannot be got wrong that way.
        var factory = new ConnectionFactory
        {
            HostName = broker.Uri.Host,
            Port = broker.Uri.IsDefaultPort ? Protocols.DefaultProtocol.DefaultPort : broker.Uri.Port,
            VirtualHost = broker.VirtualHost,
            RequestedConnectionTimeout = Timeout,
            SocketReadTimeout = Timeout,
            SocketWriteTimeout = Timeout,
        };

        // Credentials embedded in the URI, which is what the quickstart uses. Stored,
        // encrypted credentials arrive in M7.2 and are resolved here.
        if (broker.Uri.UserInfo is { Length: > 0 } userInfo)
        {
            var parts = userInfo.Split(':', 2);
            factory.UserName = Uri.UnescapeDataString(parts[0]);

            if (parts.Length is 2)
            {
                factory.Password = Uri.UnescapeDataString(parts[1]);
            }
        }

        if (broker.UseTls)
        {
            factory.Ssl = new SslOption { Enabled = true, ServerName = broker.Uri.Host };
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);

        try
        {
            await using var connection = await factory
                .CreateConnectionAsync(deadline.Token)
                .ConfigureAwait(false);

            return new BrokerProbeResult(true, null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // The message is what an operator reads, so it keeps the broker's own wording —
            // "ACCESS_REFUSED - Login was refused" says more than "the check failed".
            return new BrokerProbeResult(false, Describe(ex));
        }
    }

    private static string Describe(Exception ex) =>
        ex.InnerException is { } inner && !string.IsNullOrWhiteSpace(inner.Message)
            ? $"{ex.Message} ({inner.Message})"
            : ex.Message;
}
