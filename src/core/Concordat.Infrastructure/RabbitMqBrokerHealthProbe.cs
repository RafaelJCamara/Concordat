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
/// <b>A stored credential wins over one embedded in the URI.</b> The URI appears in every
/// listing and log line, so once a broker has been given a real secret it should stop
/// depending on whatever an operator typed into a connection string. The URI form still works,
/// because the quickstart uses it and a local broker is not worth a key ring.
/// </para>
/// </remarks>
public sealed class RabbitMqBrokerHealthProbe(ICredentialStore credentials) : IBrokerHealthProbe
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

        // A stored credential wins over anything embedded in the URI. The URI is visible in
        // every listing and log line, so a broker that has been given a real secret should stop
        // depending on the one an operator typed into a connection string.
        var stored = broker.CredentialRef is { Length: > 0 } reference
            ? await credentials.ResolveAsync(reference, cancellationToken).ConfigureAwait(false)
            : null;

        if (stored is not null)
        {
            factory.UserName = stored.Username;
            factory.Password = stored.Password;
        }
        else if (broker.Uri.UserInfo is { Length: > 0 } userInfo)
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
