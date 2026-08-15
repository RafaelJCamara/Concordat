using System.Security.Cryptography;
using System.Text;
using Concordat.Application.Abstractions;
using Concordat.Infrastructure.Notifications;

namespace Concordat.Api.IntegrationTests;

/// <summary>
/// The HMAC signature a webhook delivery carries (M7.5, L1 of the security review).
/// </summary>
/// <remarks>
/// A focused unit test against <see cref="WebhookNotificationChannel"/> directly, rather than a
/// full round trip through the dispatcher and a real HTTP listener -- the thing worth proving
/// precisely is that the signature is a correct HMAC-SHA256 of the exact bytes sent, keyed on
/// the exact secret handed to <c>SendAsync</c>. <see cref="NotificationDispatcherTests"/>
/// already proves the secret is resolved and threaded through for the right subscriptions.
/// </remarks>
public class WebhookSigningTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public byte[]? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null
                ? []
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }

    private static Notification SampleNotification() => new(
        Guid.CreateVersion7(),
        "BREAKING_CHANGE_SUBMITTED",
        "prod",
        "acme.orders.OrderCreated",
        "A breaking change is awaiting review.",
        DateTimeOffset.UtcNow);

    [Fact]
    public async Task ASignedDeliveryCarriesAVerifiableHmacOfExactlyTheBytesSent()
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var channel = new WebhookNotificationChannel(http);

        const string secret = "a-shared-secret-known-to-both-sides";

        await channel.SendAsync(
            "https://example.invalid/hook", SampleNotification(), secret, CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        var signatureHeader = Assert.Single(handler.LastRequest.Headers.GetValues("X-Concordat-Signature"));

        Assert.StartsWith("sha256=", signatureHeader, StringComparison.Ordinal);
        var claimed = signatureHeader["sha256=".Length..];

        // What a receiver would compute independently: HMAC-SHA256 of the raw body bytes,
        // keyed on the secret they were given at subscription creation.
        var expected = Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), handler.LastBody!));

        Assert.Equal(expected, claimed, StringComparer.Ordinal);
    }

    [Fact]
    public async Task TamperingWithTheBodyInvalidatesTheSignature()
    {
        // The property the whole mechanism exists for: a receiver that recomputes the HMAC
        // over a body that was altered in transit gets a different value and rejects it.
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var channel = new WebhookNotificationChannel(http);

        const string secret = "a-shared-secret-known-to-both-sides";

        await channel.SendAsync(
            "https://example.invalid/hook", SampleNotification(), secret, CancellationToken.None);

        var signatureHeader = Assert.Single(
            handler.LastRequest!.Headers.GetValues("X-Concordat-Signature"));
        var claimed = signatureHeader["sha256=".Length..];

        var tampered = (byte[])handler.LastBody!.Clone();
        tampered[0] ^= 0xFF;

        var overTamperedBody = Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), tampered));

        Assert.NotEqual(overTamperedBody, claimed, StringComparer.Ordinal);
    }

    [Fact]
    public async Task NoSecretMeansNoSignatureHeader()
    {
        // An old subscription created before signing existed has no secret to sign with, and
        // must keep delivering rather than being refused.
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var channel = new WebhookNotificationChannel(http);

        await channel.SendAsync(
            "https://example.invalid/hook", SampleNotification(), null, CancellationToken.None);

        Assert.False(handler.LastRequest!.Headers.Contains("X-Concordat-Signature"));
    }
}
