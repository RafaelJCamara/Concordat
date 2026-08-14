using Concordat.Domain.Results;

namespace Concordat.Domain.Registry;

/// <summary>Whether Concordat has reached a broker, and what happened last time.</summary>
public enum BrokerStatus
{
    /// <summary>Never contacted since it was registered.</summary>
    Unknown = 1,

    /// <summary>The last check connected.</summary>
    Reachable = 2,

    /// <summary>The last check failed.</summary>
    Unreachable = 3,
}

/// <summary>
/// One broker an environment publishes to or consumes from (ADR-012).
/// </summary>
/// <remarks>
/// <para>
/// An entity inside <see cref="Environment"/>, not an aggregate of its own: a broker has no
/// meaning apart from the environment that uses it, and the invariant worth protecting —
/// that two brokers in one environment do not claim the same host and virtual host — can only
/// be enforced from the environment.
/// </para>
/// <para>
/// <b>The same host appears more than once on purpose.</b> DESIGN §4's own example has
/// <c>eu-1</c> twice with different virtual hosts. A virtual host is a separate topology, so
/// the identity of a connection is the pair, not the host.
/// </para>
/// <para>
/// <b>No credentials live here.</b> <see cref="CredentialRef"/> names a secret held elsewhere,
/// encrypted at rest; the secret itself never enters the domain model and never leaves the
/// registry. M7.2 owns that storage.
/// </para>
/// </remarks>
public sealed class BrokerConnection
{
    private BrokerConnection(
        Guid id,
        string displayName,
        Uri uri,
        string virtualHost,
        string? credentialRef,
        bool useTls,
        BrokerStatus status,
        DateTimeOffset? lastCheckedAt,
        string? lastError)
    {
        Id = id;
        DisplayName = displayName;
        Uri = uri;
        VirtualHost = virtualHost;
        CredentialRef = credentialRef;
        UseTls = useTls;
        Status = status;
        LastCheckedAt = lastCheckedAt;
        LastError = lastError;
    }

    // Materialisation only.
    private BrokerConnection()
    {
        DisplayName = null!;
        Uri = null!;
        VirtualHost = null!;
    }

    /// <summary>The surrogate identity.</summary>
    public Guid Id { get; }

    /// <summary>A human label, unique within the environment.</summary>
    public string DisplayName { get; private set; }

    /// <summary>The AMQP endpoint.</summary>
    public Uri Uri { get; private set; }

    /// <summary>The virtual host, which is a separate topology on the same broker.</summary>
    public string VirtualHost { get; private set; }

    /// <summary>Names the stored credential, or null when the broker needs none.</summary>
    /// <remarks>
    /// A reference, never a secret. Reads of this entity over the API report only whether a
    /// credential exists.
    /// </remarks>
    public string? CredentialRef { get; private set; }

    /// <summary>Whether the connection uses TLS.</summary>
    /// <remarks>
    /// Derived from the scheme at creation rather than configured separately, because two
    /// sources for one fact is one source too many: <c>amqps://</c> with TLS disabled is a
    /// configuration nobody means.
    /// </remarks>
    public bool UseTls { get; private set; }

    /// <summary>What the last health check found.</summary>
    public BrokerStatus Status { get; private set; }

    /// <summary>When the last health check ran.</summary>
    public DateTimeOffset? LastCheckedAt { get; private set; }

    /// <summary>Why the last health check failed, when it did.</summary>
    public string? LastError { get; private set; }

    /// <summary>Creates a broker connection.</summary>
    /// <param name="displayName">A human label.</param>
    /// <param name="uri">An <c>amqp</c> or <c>amqps</c> endpoint.</param>
    /// <param name="virtualHost">The virtual host; defaults to <c>/</c>.</param>
    /// <param name="credentialRef">A reference to a stored credential, if any.</param>
    /// <returns>
    /// The connection, or a failure carrying <see cref="ConcordatCodes.BrokerUriInvalid"/>.
    /// </returns>
    public static Result<BrokerConnection> Create(
        string? displayName,
        string? uri,
        string? virtualHost = null,
        string? credentialRef = null)
    {
        var label = displayName?.Trim();
        if (string.IsNullOrEmpty(label))
        {
            return Result<BrokerConnection>.Failure(
                ConcordatCodes.BrokerUriInvalid, "A broker display name is required.");
        }

        if (!Uri.TryCreate(uri?.Trim(), UriKind.Absolute, out var parsed))
        {
            return Result<BrokerConnection>.Failure(
                ConcordatCodes.BrokerUriInvalid,
                $"'{uri}' is not an absolute URI. Expected something like " +
                "'amqps://rabbit-eu:5671'.");
        }

        // Only the two AMQP 0-9-1 schemes. ADR-013 scopes v1 to that protocol, and accepting
        // an mqtt:// or http:// endpoint here would register a broker nothing can ever use.
        if (parsed.Scheme is not ("amqp" or "amqps"))
        {
            return Result<BrokerConnection>.Failure(
                ConcordatCodes.BrokerUriInvalid,
                $"Scheme '{parsed.Scheme}' is not supported. v1 speaks AMQP 0-9-1 only " +
                "(ADR-013), so a broker must be 'amqp://' or 'amqps://'.");
        }

        var vhost = string.IsNullOrWhiteSpace(virtualHost) ? "/" : virtualHost.Trim();

        return Result<BrokerConnection>.Success(new BrokerConnection(
            Guid.CreateVersion7(),
            label,
            parsed,
            vhost,
            string.IsNullOrWhiteSpace(credentialRef) ? null : credentialRef.Trim(),
            parsed.Scheme is "amqps",
            BrokerStatus.Unknown,
            lastCheckedAt: null,
            lastError: null));
    }

    /// <summary>Records the outcome of a health check.</summary>
    /// <param name="reachable">Whether the broker answered.</param>
    /// <param name="checkedAt">When the check ran.</param>
    /// <param name="error">Why it failed, when it did.</param>
    public void RecordCheck(bool reachable, DateTimeOffset checkedAt, string? error = null)
    {
        Status = reachable ? BrokerStatus.Reachable : BrokerStatus.Unreachable;
        LastCheckedAt = checkedAt.ToUniversalTime();

        // The error is cleared on success rather than left behind. A stale failure beside a
        // healthy status is the kind of dashboard that teaches people to ignore dashboards.
        LastError = reachable ? null : error;
    }

    /// <summary>Points the connection at a stored credential, or clears it.</summary>
    /// <param name="credentialRef">The reference, or null to clear.</param>
    public void SetCredentialRef(string? credentialRef) =>
        CredentialRef = string.IsNullOrWhiteSpace(credentialRef) ? null : credentialRef.Trim();
}
