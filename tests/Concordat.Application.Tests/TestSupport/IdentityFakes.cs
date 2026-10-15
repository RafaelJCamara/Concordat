using Concordat.Application.Abstractions;
using Concordat.Domain.Identity;
using Concordat.Domain.Registry;

namespace Concordat.Application.Tests.TestSupport;

/// <summary>
/// An in-memory <see cref="IUserRepository"/>: accounts and the memberships that give them a role.
/// </summary>
/// <remarks>
/// <see cref="Seed"/> arranges an account without going through <see cref="Add(User)"/>, so
/// <see cref="Added"/> stays a record of what the handler under test wrote.
/// </remarks>
internal sealed class FakeUsers : IUserRepository
{
    private readonly List<User> _users = [];
    private readonly List<Membership> _memberships = [];

    /// <summary>Accounts the handler staged for insert.</summary>
    public List<User> Added { get; } = [];

    /// <summary>Arranges an account and its membership in a tenant.</summary>
    public User Seed(
        string email,
        Role role,
        TenantId? tenant = null,
        bool disabled = false,
        string passwordHash = "hash:seeded password")
    {
        var created = User.Create(email, null, passwordHash, Build.At);
        Assert.True(created.IsSuccess, created.Error?.Message);

        var user = created.Value;
        user.SetDisabled(disabled);

        _users.Add(user);
        _memberships.Add(Membership.Grant(tenant ?? TenantId.SelfHosted, user.Id, role, Build.At));

        return user;
    }

    /// <summary>Gives an existing account a membership in another tenant.</summary>
    public void AlsoBelongTo(User user, TenantId tenant, Role role) =>
        _memberships.Add(Membership.Grant(tenant, user.Id, role, Build.At));

    public Task<User?> FindAsync(EmailAddress email, CancellationToken cancellationToken) =>
        Task.FromResult(_users.Find(u => u.Email == email));

    public Task<User?> FindAsync(UserId id, CancellationToken cancellationToken) =>
        Task.FromResult(_users.Find(u => u.Id == id));

    public Task<IReadOnlyList<(User User, Membership Membership)>> ListMembersAsync(
        TenantId tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<(User, Membership)>>(
            [.. _memberships
                .Where(m => m.TenantId == tenantId)
                .Select(m => (_users.Single(u => u.Id == m.UserId), m))]);

    public Task<IReadOnlyList<Membership>> ListMembershipsAsync(
        UserId userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Membership>>(
            [.. _memberships.Where(m => m.UserId == userId)]);

    public Task<Membership?> FindMembershipAsync(
        TenantId tenantId, UserId userId, CancellationToken cancellationToken) =>
        Task.FromResult(
            _memberships.Find(m => m.TenantId == tenantId && m.UserId == userId));

    public Task<bool> AnyAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_users.Count > 0);

    public void Add(User user)
    {
        _users.Add(user);
        Added.Add(user);
    }

    public void Add(Membership membership) => _memberships.Add(membership);
}

/// <summary>An in-memory <see cref="IApiKeyRepository"/>.</summary>
internal sealed class FakeApiKeys : IApiKeyRepository
{
    private readonly List<ApiKey> _keys = [];

    /// <summary>Every key, so a test can say what became of each.</summary>
    public IReadOnlyList<ApiKey> All => _keys;

    /// <summary>How many times the authenticator looked a key up by its public id.</summary>
    public int Lookups { get; private set; }

    /// <summary>Arranges a key, and returns it with the credential that presents it.</summary>
    public (ApiKey Key, string Secret) Seed(
        UserId? user,
        ApiKeyKind kind,
        IReadOnlyList<string>? scopes = null,
        string label = "arranged",
        TenantId? tenant = null,
        ActorId? actor = null)
    {
        var issued = ApiKey.Issue(
            tenant ?? TenantId.SelfHosted,
            user,
            label,
            scopes ?? [Scope.SubjectRead],
            Build.At,
            Build.At.AddHours(12),
            kind,
            actor);

        Assert.True(issued.IsSuccess, issued.Error?.Message);

        _keys.Add(issued.Value.Key);
        return (issued.Value.Key, issued.Value.Secret);
    }

    public Task<ApiKey?> FindByKeyIdAsync(string keyId, CancellationToken cancellationToken)
    {
        Lookups++;

        return Task.FromResult(_keys.Find(k => k.KeyId == keyId));
    }

    public Task<IReadOnlyList<ApiKey>> ListAsync(
        TenantId tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ApiKey>>([.. _keys.Where(k => k.TenantId == tenantId)]);

    public Task<ApiKey?> FindAsync(TenantId tenantId, Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(_keys.Find(k => k.TenantId == tenantId && k.Id == id));

    public Task<IReadOnlyList<ApiKey>> ListForUserAsync(
        UserId userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ApiKey>>([.. _keys.Where(k => k.UserId == userId)]);

    public void Add(ApiKey key) => _keys.Add(key);
}

/// <summary>
/// A password hasher whose output is legible, so a test can see which password a hash is of.
/// </summary>
/// <remarks>
/// <c>hash:</c> followed by the plaintext. That is the opposite of a hash and exactly what a test
/// wants: it can assert that the stored value is the new password's, and that a refused request
/// left the old one in place, without the cost or the randomness of the real derivation.
/// </remarks>
internal sealed class FakePasswordHasher : IPasswordHasher
{
    /// <summary>What a stored hash of this password looks like.</summary>
    public static string HashOf(string password) => $"hash:{password}";

    public string DummyHash => HashOf("nobody knows this");

    public string Hash(string password) => HashOf(password);

    public (bool Verified, bool NeedsRehash) Verify(string hash, string password) =>
        (hash == HashOf(password), false);
}

/// <summary>An <see cref="ITenantRepository"/> that has no organisations.</summary>
internal sealed class FakeTenants : ITenantRepository
{
    public Task<Tenant?> FindAsync(
        TenantId id, CancellationToken cancellationToken) =>
        Task.FromResult<Tenant?>(null);

    public Task<Tenant?> FindBySlugAsync(
        string slug, CancellationToken cancellationToken) =>
        Task.FromResult<Tenant?>(null);

    public void Add(Tenant tenant) =>
        throw new NotSupportedException("The tests that use this never create an organisation.");
}
