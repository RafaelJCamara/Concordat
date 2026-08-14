using Concordat.Application.Abstractions;
using Concordat.Domain.Identity;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;

namespace Concordat.Application.Identity;

/// <summary>
/// Turns a presented credential into a <see cref="Caller"/> (M8.2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every path that fails returns the same thing.</b> An unknown key id, a wrong secret, a
/// revoked key, an expired key, a disabled account and a user with no membership all produce
/// <see cref="Caller.Anonymous"/>. Distinguishing them would tell an attacker which of their
/// guesses was once real, and the distinction is worth nothing to a legitimate caller who
/// already knows their own credential.
/// </para>
/// <para>
/// <b>It fails closed by construction.</b> There is no branch that returns an authenticated
/// caller without having verified something, and the only non-anonymous constructions in this
/// file are after a successful <see cref="ApiKey.Verify"/> or password check.
/// </para>
/// </remarks>
public sealed class Authenticator(
    IApiKeyRepository apiKeys,
    IUserRepository users,
    ITenantRepository tenants,
    IPasswordHasher passwords,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
{
    /// <summary>Resolves an API key credential.</summary>
    /// <param name="credential">The value from the <c>Authorization</c> header.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The caller, or <see cref="Caller.Anonymous"/>.</returns>
    public async Task<Caller> FromApiKeyAsync(
        string? credential, CancellationToken cancellationToken)
    {
        if (!ApiKey.TryParse(credential, out var keyId, out var secret))
        {
            return Caller.Anonymous;
        }

        var key = await apiKeys.FindByKeyIdAsync(keyId, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();

        if (key is null || !key.Verify(secret, now))
        {
            return Caller.Anonymous;
        }

        // Recorded so an operator can answer "is this key still needed?", which is the question
        // that has to be answered before anyone will revoke anything. Written at most once a
        // minute rather than on every request: this sits on the SDK's hot path, and a row
        // update per schema lookup would turn a read into a write.
        if (key.LastUsedAt is null || now - key.LastUsedAt > TimeSpan.FromMinutes(1))
        {
            key.RecordUse(now);
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return new Caller(
            CredentialKind.ApiKey,
            key.TenantId,
            key.ScopeSet(),
            key.Actor,
            key.UserId,
            key.KeyId);
    }

    /// <summary>Resolves an email and password.</summary>
    /// <param name="email">The login.</param>
    /// <param name="password">The plaintext.</param>
    /// <param name="organisation">
    /// Which organisation to sign in to, by slug. Optional, and unnecessary for anyone who
    /// belongs to exactly one — which is everybody on a self-hosted deployment.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The caller, or <see cref="Caller.Anonymous"/>.</returns>
    /// <remarks>
    /// <b>The organisation is discovered, not assumed.</b> An earlier version took the ambient
    /// tenant, which is fine when there is one and wrong the moment there are two: at sign-in
    /// nobody has authenticated yet, so the ambient tenant is whatever an anonymous caller
    /// resolves to — never the one the user actually belongs to.
    /// </remarks>
    public async Task<Caller> FromPasswordAsync(
        string? email, string? password, string? organisation, CancellationToken cancellationToken)
    {
        var address = EmailAddress.Create(email);

        var user = address.IsSuccess
            ? await users.FindAsync(address.Value, cancellationToken).ConfigureAwait(false)
            : null;

        if (user is null || user.Disabled || string.IsNullOrEmpty(password))
        {
            // The derivation still runs against a throwaway hash when no user was found, so
            // that a request for a nonexistent account costs the same as one for a real
            // account with a wrong password. Without this, response timing enumerates users.
            passwords.Verify(passwords.DummyHash, password ?? string.Empty);
            return Caller.Anonymous;
        }

        var (verified, needsRehash) = passwords.Verify(user.PasswordHash, password);

        if (!verified)
        {
            return Caller.Anonymous;
        }

        var membership = await ResolveMembershipAsync(user, organisation, cancellationToken)
            .ConfigureAwait(false);

        if (membership is null)
        {
            return Caller.Anonymous;
        }

        var now = clock.GetUtcNow();
        user.RecordSignIn(now);

        // A password stored under out-of-date parameters is upgraded the moment its owner
        // proves they know it. Waiting for a password reset means the weakest hashes belong to
        // the accounts that change least, which is exactly backwards.
        if (needsRehash)
        {
            user.SetPasswordHash(passwords.Hash(password));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new Caller(
            CredentialKind.User,
            membership.TenantId,
            Roles.SetFor(membership.Role),
            user.Actor(),
            user.Id);
    }

    /// <summary>Works out which organisation a verified user is signing in to.</summary>
    /// <remarks>
    /// An ambiguous sign-in — several memberships and no organisation named — fails rather than
    /// picking one. Choosing for them would silently drop somebody into the wrong organisation,
    /// and the first sign they were in the wrong place would be data they did not expect.
    /// </remarks>
    private async Task<Membership?> ResolveMembershipAsync(
        User user, string? organisation, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(organisation))
        {
            var tenant = await tenants
                .FindBySlugAsync(organisation.Trim().ToLowerInvariant(), cancellationToken)
                .ConfigureAwait(false);

            return tenant is null
                ? null
                : await users.FindMembershipAsync(tenant.Id, user.Id, cancellationToken)
                    .ConfigureAwait(false);
        }

        var memberships = await users.ListMembershipsAsync(user.Id, cancellationToken)
            .ConfigureAwait(false);

        return memberships.Count is 1 ? memberships[0] : null;
    }
}
