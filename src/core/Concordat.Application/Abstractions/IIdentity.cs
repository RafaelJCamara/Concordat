using Concordat.Domain.Identity;
using Concordat.Domain.Registry;

namespace Concordat.Application.Abstractions;

/// <summary>
/// Hashes and verifies passwords (M8.1).
/// </summary>
/// <remarks>
/// An abstraction not because a second implementation is expected, but because the algorithm
/// and its parameters are the kind of thing that has to change on a schedule the domain must
/// not know about — and because nothing above Infrastructure should be able to reach a
/// plaintext password by accident.
/// </remarks>
public interface IPasswordHasher
{
    /// <summary>
    /// A hash of a value nobody knows, for verifying against when no account was found.
    /// </summary>
    /// <remarks>
    /// <b>Real, and computed once at startup.</b> Without it a sign-in for a nonexistent
    /// address returns as soon as the lookup misses, while one for a real address pays for a
    /// key derivation — and the difference enumerates users. A hard-coded constant would not
    /// do: the implementation has to actually derive against it for the cost to match.
    /// </remarks>
    string DummyHash { get; }

    /// <summary>Hashes a password for storage.</summary>
    /// <param name="password">The plaintext.</param>
    /// <returns>An opaque, self-describing hash.</returns>
    string Hash(string password);

    /// <summary>Verifies a password against a stored hash.</summary>
    /// <param name="hash">The stored hash.</param>
    /// <param name="password">The plaintext presented.</param>
    /// <returns>
    /// Whether it matched, and whether the stored hash used out-of-date parameters and should
    /// be replaced.
    /// </returns>
    (bool Verified, bool NeedsRehash) Verify(string hash, string password);
}

/// <summary>Reads and writes local accounts (M8.1).</summary>
public interface IUserRepository
{
    /// <summary>Finds a user by their login.</summary>
    /// <param name="email">The normalised address.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The user, or <see langword="null"/>.</returns>
    Task<User?> FindAsync(EmailAddress email, CancellationToken cancellationToken);

    /// <summary>Finds a user by identity.</summary>
    /// <param name="id">The identity.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The user, or <see langword="null"/>.</returns>
    Task<User?> FindAsync(UserId id, CancellationToken cancellationToken);

    /// <summary>Lists the users who are members of a tenant, with their roles.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The members.</returns>
    Task<IReadOnlyList<(User User, Membership Membership)>> ListMembersAsync(
        TenantId tenantId, CancellationToken cancellationToken);

    /// <summary>Finds a user's membership in a tenant.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="userId">The user.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The membership, or <see langword="null"/>.</returns>
    Task<Membership?> FindMembershipAsync(
        TenantId tenantId, UserId userId, CancellationToken cancellationToken);

    /// <summary>Whether any account exists at all.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>True once the instance has been claimed.</returns>
    /// <remarks>
    /// Drives first-run bootstrap: an instance with no accounts lets the first caller create
    /// the owner, and an instance with one does not.
    /// </remarks>
    Task<bool> AnyAsync(CancellationToken cancellationToken);

    /// <summary>Stages a new user for insert.</summary>
    /// <param name="user">The user.</param>
    void Add(User user);

    /// <summary>Stages a new membership for insert.</summary>
    /// <param name="membership">The membership.</param>
    void Add(Membership membership);
}

/// <summary>Reads and writes API keys (M8.1).</summary>
public interface IApiKeyRepository
{
    /// <summary>Finds a key by its public id, across every tenant.</summary>
    /// <param name="keyId">The public half of a presented credential.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The key, or <see langword="null"/>.</returns>
    /// <remarks>
    /// <b>Deliberately not tenant-filtered.</b> Authentication is what establishes which tenant
    /// the caller is in, so filtering by the current tenant here would be circular — and in a
    /// self-hosted install it would appear to work, then fail the day Cloud has two.
    /// </remarks>
    Task<ApiKey?> FindByKeyIdAsync(string keyId, CancellationToken cancellationToken);

    /// <summary>Lists a tenant's keys, newest first.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The keys. <b>Never their secrets.</b></returns>
    Task<IReadOnlyList<ApiKey>> ListAsync(TenantId tenantId, CancellationToken cancellationToken);

    /// <summary>Finds one key by identity within a tenant.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="id">The key's identity.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The key, or <see langword="null"/>.</returns>
    Task<ApiKey?> FindAsync(TenantId tenantId, Guid id, CancellationToken cancellationToken);

    /// <summary>Stages a new key for insert.</summary>
    /// <param name="key">The key.</param>
    void Add(ApiKey key);
}
