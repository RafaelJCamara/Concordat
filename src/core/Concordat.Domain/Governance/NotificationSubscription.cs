using Concordat.Domain.Registry;
using Concordat.Domain.Results;

namespace Concordat.Domain.Governance;

/// <summary>How a notification is delivered.</summary>
public enum NotificationChannel
{
    /// <summary>An SMTP message to one address.</summary>
    Email = 1,

    /// <summary>An HTTP POST carrying a JSON body.</summary>
    Webhook = 2,
}

/// <summary>The wire spellings of <see cref="NotificationChannel"/>.</summary>
public static class ChannelTokens
{
    /// <summary>Maps a channel to its wire token.</summary>
    /// <param name="channel">The channel.</param>
    /// <returns>The stable token.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The channel is not a known member.</exception>
    public static string For(NotificationChannel channel) => channel switch
    {
        NotificationChannel.Email => "EMAIL",
        NotificationChannel.Webhook => "WEBHOOK",
        _ => throw new ArgumentOutOfRangeException(
            nameof(channel), channel, "Unknown notification channel."),
    };

    /// <summary>Parses a wire token.</summary>
    /// <param name="token">The token.</param>
    /// <param name="channel">The channel, when it parsed.</param>
    /// <returns>Whether it parsed.</returns>
    public static bool TryParse(string? token, out NotificationChannel channel)
    {
        channel = default;

        switch (token?.Trim().ToUpperInvariant())
        {
            case "EMAIL":
                channel = NotificationChannel.Email;
                return true;
            case "WEBHOOK":
                channel = NotificationChannel.Webhook;
                return true;
            default:
                return false;
        }
    }
}

/// <summary>
/// Who wants to hear about what, in one environment (M7.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Per-environment, not global.</b> The team that must hear every breaking change in
/// <c>prod</c> is usually not the team that wants a message every time someone registers in
/// <c>dev</c>, and a single global subscription would force one of them to mute the other's
/// traffic — which in practice means muting everything.
/// </para>
/// <para>
/// <b>An empty event set means every event.</b> The alternative is a subscription that is
/// configured, enabled, and silently delivers nothing, which is the worst state a notification
/// system can be in: it looks correct from every screen.
/// </para>
/// </remarks>
public sealed class NotificationSubscription
{
    /// <summary>The longest permitted endpoint, in characters.</summary>
    public const int MaxEndpointLength = 512;

    private NotificationSubscription(
        Guid id,
        EnvironmentId environmentId,
        NotificationChannel channel,
        string endpoint,
        IReadOnlyList<NotificationEvent> events,
        DateTimeOffset createdAt)
    {
        Id = id;
        EnvironmentId = environmentId;
        Channel = channel;
        Endpoint = endpoint;
        Events = events;
        CreatedAt = createdAt;
        Enabled = true;
    }

    /// <summary>
    /// A reference to this subscription's webhook signing secret, held encrypted elsewhere.
    /// </summary>
    /// <remarks>
    /// Same shape as <see cref="BrokerConnection.CredentialRef"/> and for the same
    /// reason: an aggregate that held the secret itself would be one serialisation mistake away
    /// from returning it. Null for an email subscription, which has nothing to sign.
    /// </remarks>
    public string? SigningKeyRef { get; private set; }

    /// <summary>Records where this subscription's signing secret is stored.</summary>
    /// <param name="signingKeyRef">The reference, or null to clear it.</param>
    public void SetSigningKeyRef(string? signingKeyRef) =>
        SigningKeyRef = string.IsNullOrWhiteSpace(signingKeyRef) ? null : signingKeyRef.Trim();

    // Materialisation only.
    private NotificationSubscription()
    {
        Endpoint = null!;
        Events = [];
    }

    /// <summary>The subscription's identity.</summary>
    public Guid Id { get; }

    /// <summary>Which environment's events it wants.</summary>
    public EnvironmentId EnvironmentId { get; }

    /// <summary>How to deliver.</summary>
    public NotificationChannel Channel { get; }

    /// <summary>An email address, or an <c>https</c> URL.</summary>
    public string Endpoint { get; private set; }

    /// <summary>Which events it wants, or empty for all of them.</summary>
    public IReadOnlyList<NotificationEvent> Events { get; private set; }

    /// <summary>Whether it is currently delivering.</summary>
    public bool Enabled { get; private set; }

    /// <summary>When it was created.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>Whether this subscription wants a given event.</summary>
    /// <param name="value">The event.</param>
    /// <returns>True when it should be delivered.</returns>
    public bool Wants(NotificationEvent value) =>
        Enabled && (Events.Count is 0 || Events.Contains(value));

    /// <summary>Creates a subscription.</summary>
    /// <param name="environmentId">Which environment.</param>
    /// <param name="channel">How to deliver.</param>
    /// <param name="endpoint">Where to deliver.</param>
    /// <param name="events">Which events, or empty for all.</param>
    /// <param name="createdAt">When.</param>
    /// <returns>The subscription, or a validation failure.</returns>
    public static Result<NotificationSubscription> Create(
        EnvironmentId environmentId,
        NotificationChannel channel,
        string? endpoint,
        IReadOnlyList<NotificationEvent> events,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(events);

        var validated = ValidateEndpoint(channel, endpoint);

        return validated.IsFailure
            ? Result<NotificationSubscription>.Failure(validated.Error!)
            : Result<NotificationSubscription>.Success(new NotificationSubscription(
                Guid.CreateVersion7(),
                environmentId,
                channel,
                validated.Value,
                [.. events.Distinct()],
                createdAt.ToUniversalTime()));
    }

    /// <summary>Turns delivery on or off without losing the configuration.</summary>
    /// <param name="enabled">Whether to deliver.</param>
    /// <remarks>
    /// Muting rather than deleting, because the common reason to stop is an incident and the
    /// common thing to do afterwards is turn it back on with the same settings.
    /// </remarks>
    public void SetEnabled(bool enabled) => Enabled = enabled;

    private static Result<string> ValidateEndpoint(NotificationChannel channel, string? endpoint)
    {
        var trimmed = endpoint?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return Result<string>.Failure(
                ConcordatCodes.SubscriptionEndpointInvalid, "An endpoint is required.");
        }

        if (trimmed.Length > MaxEndpointLength)
        {
            return Result<string>.Failure(
                ConcordatCodes.SubscriptionEndpointInvalid,
                $"An endpoint may be at most {MaxEndpointLength} characters.");
        }

        return channel switch
        {
            NotificationChannel.Email => ValidateEmail(trimmed),
            NotificationChannel.Webhook => ValidateWebhook(trimmed),
            _ => Result<string>.Failure(
                ConcordatCodes.SubscriptionEndpointInvalid, "Unknown notification channel."),
        };
    }

    // Deliberately not a full RFC 5322 grammar. The addresses that matter are team aliases, the
    // registry is not a mail server, and a regex that claims to validate email is a well-known
    // way to reject valid addresses. This catches the typo and lets SMTP judge the rest.
    private static Result<string> ValidateEmail(string endpoint) =>
        endpoint.Count(c => c is '@') is 1 &&
        endpoint[0] is not '@' &&
        endpoint[^1] is not '@' &&
        endpoint.Contains('.', StringComparison.Ordinal)
            ? Result<string>.Success(endpoint)
            : Result<string>.Failure(
                ConcordatCodes.SubscriptionEndpointInvalid,
                $"'{endpoint}' is not an email address.");

    private static Result<string> ValidateWebhook(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return Result<string>.Failure(
                ConcordatCodes.SubscriptionEndpointInvalid,
                $"'{endpoint}' is not an absolute URL.");
        }

        // http is refused, not warned about. A webhook body names subjects, versions and who
        // approved what -- the shape of an organisation's message contracts -- and there is no
        // reason to hand that to anything on the path in 2026.
        return uri.Scheme is "https"
            ? Result<string>.Success(endpoint)
            : Result<string>.Failure(
                ConcordatCodes.SubscriptionEndpointInvalid,
                $"Webhooks must be https; '{uri.Scheme}' is refused. A webhook body names " +
                "subjects, versions and reviewers, which is not something to send in the clear.");
    }
}
