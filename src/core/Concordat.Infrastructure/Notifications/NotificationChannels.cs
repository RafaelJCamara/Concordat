using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Concordat.Application.Abstractions;
using Concordat.Domain.Governance;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Concordat.Infrastructure.Notifications;

/// <summary>How to reach an SMTP server (M7.5).</summary>
public sealed class SmtpOptions
{
    /// <summary>The SMTP host. Email delivery is disabled when this is empty.</summary>
    public string? Host { get; set; }

    /// <summary>The port.</summary>
    public int Port { get; set; } = 587;

    /// <summary>Whether to negotiate STARTTLS.</summary>
    public bool UseStartTls { get; set; } = true;

    /// <summary>The username, or null for an unauthenticated relay.</summary>
    public string? Username { get; set; }

    /// <summary>The password.</summary>
    public string? Password { get; set; }

    /// <summary>The From address.</summary>
    public string From { get; set; } = "concordat@localhost";

    /// <summary>How long to wait on the server before giving up.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Delivers notifications by email (M7.5).
/// </summary>
/// <remarks>
/// <b>An unconfigured host throws rather than silently succeeding.</b> A channel that returns
/// success without sending anything would have the dispatcher mark every message delivered, and
/// the first anyone would know is that the alerts they configured never arrived. Throwing parks
/// the message after five attempts with the reason recorded on it.
/// </remarks>
public sealed class EmailNotificationChannel(IOptions<SmtpOptions> options) : INotificationChannel
{
    private readonly SmtpOptions _options = options?.Value
        ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc />
    public NotificationChannel Channel => NotificationChannel.Email;

    /// <inheritdoc />
    public async Task SendAsync(
        string endpoint,
        Notification notification,
        string? signingSecret,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);

        if (string.IsNullOrWhiteSpace(_options.Host))
        {
            throw new InvalidOperationException(
                "No SMTP host is configured, so email subscriptions cannot be delivered. Set " +
                "Concordat:Smtp:Host, or remove the email subscription.");
        }

        var message = new MimeMessage
        {
            Subject = $"[Concordat/{notification.Environment}] {notification.Event}: {notification.Target}",
            Body = new TextPart("plain")
            {
                Text =
                    $"{notification.Body}\n\n" +
                    $"Environment: {notification.Environment}\n" +
                    $"Event:       {notification.Event}\n" +
                    $"Subject:     {notification.Target}\n" +
                    $"Occurred:    {notification.OccurredAt:u}\n" +
                    $"Message id:  {notification.Id}\n",
            },
        };

        message.From.Add(MailboxAddress.Parse(_options.From));
        message.To.Add(MailboxAddress.Parse(endpoint));

        // The message id travels as a header too, so a mail client can thread or deduplicate a
        // re-send the same way a webhook receiver does.
        message.Headers.Add("X-Concordat-Message-Id", notification.Id.ToString());
        message.Headers.Add("X-Concordat-Event", notification.Event);

        using var client = new SmtpClient { Timeout = (int)_options.Timeout.TotalMilliseconds };

        await client.ConnectAsync(
            _options.Host,
            _options.Port,
            _options.UseStartTls
                ? MailKit.Security.SecureSocketOptions.StartTls
                : MailKit.Security.SecureSocketOptions.None,
            cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(_options.Username))
        {
            await client.AuthenticateAsync(
                _options.Username, _options.Password ?? string.Empty, cancellationToken)
                .ConfigureAwait(false);
        }

        await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>The JSON body a webhook receives.</summary>
/// <param name="Id">The message id. <b>Deduplicate on this</b> — delivery is at-least-once.</param>
/// <param name="Event">The stable event token.</param>
/// <param name="Environment">The environment name.</param>
/// <param name="Subject">What it happened to.</param>
/// <param name="Message">The human-readable summary.</param>
/// <param name="OccurredAt">When the change happened, not when this was delivered.</param>
public sealed record WebhookPayload(
    Guid Id,
    string Event,
    string Environment,
    string Subject,
    string Message,
    DateTimeOffset OccurredAt);

/// <summary>
/// Delivers notifications by HTTP POST (M7.5).
/// </summary>
/// <remarks>
/// The endpoint is validated as <c>https</c> when the subscription is created, so nothing here
/// re-checks the scheme. What this does check is the response: a non-2xx throws, because a
/// receiver that answered 500 has not received anything and a silent success would lose the
/// message for good.
/// </remarks>
public sealed class WebhookNotificationChannel(HttpClient http) : INotificationChannel
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <inheritdoc />
    public NotificationChannel Channel => NotificationChannel.Webhook;

    /// <inheritdoc />
    public async Task SendAsync(
        string endpoint,
        Notification notification,
        string? signingSecret,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);

        var payload = new WebhookPayload(
            notification.Id,
            notification.Event,
            notification.Environment,
            notification.Target,
            notification.Body,
            notification.OccurredAt);

        var body = JsonSerializer.SerializeToUtf8Bytes(payload, Json);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(body),
        };

        request.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        // Also in headers, so a receiver can deduplicate or route without parsing the body.
        request.Headers.TryAddWithoutValidation("X-Concordat-Message-Id", notification.Id.ToString());
        request.Headers.TryAddWithoutValidation("X-Concordat-Event", notification.Event);

        if (signingSecret is { Length: > 0 })
        {
            // HMAC-SHA256 over the exact bytes sent, not a re-serialisation of the payload
            // record: signing anything else would let a receiver's JSON library disagree with
            // this one about whitespace or key order and reject a genuine delivery.
            var key = System.Text.Encoding.UTF8.GetBytes(signingSecret);
            var signature = System.Security.Cryptography.HMACSHA256.HashData(key, body);

            request.Headers.TryAddWithoutValidation(
                "X-Concordat-Signature", $"sha256={Convert.ToHexStringLower(signature)}");
        }

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"The webhook answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
    }
}
