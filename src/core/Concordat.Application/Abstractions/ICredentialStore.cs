namespace Concordat.Application.Abstractions;

/// <summary>The credentials Concordat uses to reach a broker.</summary>
/// <param name="Username">The AMQP user.</param>
/// <param name="Password">Its password.</param>
/// <remarks>
/// <b>This type exists only in flight.</b> It is constructed from a write request, handed
/// straight to <see cref="ICredentialStore"/>, and reconstructed only when a connection is
/// about to be opened. It is never a property of an aggregate, never projected onto a
/// response, and never logged — the surest way to keep a secret out of a response body is for
/// the object holding it to be unreachable from anything that builds one.
/// </remarks>
public sealed record BrokerCredential(string Username, string Password);

/// <summary>
/// Stores broker credentials encrypted at rest, addressed by an opaque reference (M7.2).
/// </summary>
/// <remarks>
/// <para>
/// <b>A port, so the domain never holds a secret.</b> <c>BrokerConnection</c> carries a
/// <c>CredentialRef</c> — a name for something kept elsewhere — and that is the whole design:
/// an aggregate that held ciphertext would be one serialisation mistake away from returning
/// it, and an aggregate that held plaintext would be one logging statement away.
/// </para>
/// <para>
/// <b>Credentials are write-only over the API</b> (ADR-012). There is deliberately no
/// operation here that a read endpoint could call: <see cref="ResolveAsync"/> exists for
/// opening connections, and the only caller is the health probe.
/// </para>
/// </remarks>
public interface ICredentialStore
{
    /// <summary>Encrypts and stores a credential.</summary>
    /// <param name="credential">The secret.</param>
    /// <param name="existingRef">
    /// A reference to replace, when the broker already had one. Replacing in place rather than
    /// allocating a new reference means a rotation cannot leave the previous secret behind.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The reference to record on the broker.</returns>
    Task<string> StoreAsync(
        BrokerCredential credential, string? existingRef, CancellationToken cancellationToken);

    /// <summary>Decrypts a stored credential.</summary>
    /// <param name="credentialRef">The reference recorded on the broker.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>
    /// The credential, or <see langword="null"/> when the reference names nothing — which is a
    /// recoverable state, not a crash: a broker whose secret has been removed underneath it
    /// should fail to connect, not fail to load.
    /// </returns>
    Task<BrokerCredential?> ResolveAsync(
        string credentialRef, CancellationToken cancellationToken);

    /// <summary>Removes a stored credential.</summary>
    /// <param name="credentialRef">The reference.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task RemoveAsync(string credentialRef, CancellationToken cancellationToken);
}

/// <summary>
/// Stores a webhook subscription's HMAC signing secret encrypted at rest, addressed by an
/// opaque reference (M7.5).
/// </summary>
/// <remarks>
/// The same shape as <see cref="ICredentialStore"/> and for the same reason: the receiver of a
/// webhook has no way to verify it came from this registry — or was not tampered with in
/// transit past a compromised or misconfigured intermediary — without a shared secret, and
/// that secret must never be recoverable from the aggregate itself.
/// </remarks>
public interface IWebhookSigningKeyStore
{
    /// <summary>Generates a new secret and stores it encrypted.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The reference to record on the subscription, and the secret itself.</returns>
    /// <remarks>
    /// The secret is generated here, not accepted from a caller: an operator-supplied secret
    /// would have to travel through a request body and this process's logs on the way in, and
    /// the whole point is a value that never does.
    /// </remarks>
    Task<(string Reference, string Secret)> GenerateAsync(CancellationToken cancellationToken);

    /// <summary>Decrypts a stored secret.</summary>
    /// <param name="reference">The reference recorded on the subscription.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>
    /// The secret, or <see langword="null"/> when the reference names nothing or no longer
    /// decrypts — a recoverable state (the key ring lost the key that wrote it), not a crash.
    /// </returns>
    Task<string?> ResolveAsync(string reference, CancellationToken cancellationToken);

    /// <summary>Removes a stored secret.</summary>
    /// <param name="reference">The reference.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task RemoveAsync(string reference, CancellationToken cancellationToken);
}
