using Concordat.Application.Abstractions;
using Concordat.Domain.Identity;
using Concordat.Domain.Registry;
using Microsoft.EntityFrameworkCore;

namespace Concordat.Infrastructure.Persistence;

/// <inheritdoc />
internal sealed class UserRepository(ConcordatDbContext context) : IUserRepository
{
    /// <inheritdoc />
    public Task<User?> FindAsync(EmailAddress email, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(email);

        return context.Users.SingleOrDefaultAsync(u => u.Email == email, cancellationToken);
    }

    /// <inheritdoc />
    public Task<User?> FindAsync(UserId id, CancellationToken cancellationToken) =>
        context.Users.SingleOrDefaultAsync(u => u.Id == id, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<(User User, Membership Membership)>> ListMembersAsync(
        TenantId tenantId, CancellationToken cancellationToken)
    {
        // A join rather than two round trips: the members screen wants both halves and there is
        // no case where one is useful without the other.
        var rows = await context.Memberships
            .Where(m => m.TenantId == tenantId)
            .Join(
                context.Users,
                membership => membership.UserId,
                user => user.Id,
                (membership, user) => new { user, membership })
            .OrderBy(r => r.user.Email)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(r => (r.user, r.membership))];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Membership>> ListMembershipsAsync(
        UserId userId, CancellationToken cancellationToken) =>
        await context.Memberships
            .Where(m => m.UserId == userId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public Task<Membership?> FindMembershipAsync(
        TenantId tenantId, UserId userId, CancellationToken cancellationToken) =>
        context.Memberships.SingleOrDefaultAsync(
            m => m.TenantId == tenantId && m.UserId == userId, cancellationToken);

    /// <inheritdoc />
    public Task<bool> AnyAsync(CancellationToken cancellationToken) =>
        context.Users.AnyAsync(cancellationToken);

    /// <inheritdoc />
    public void Add(User user) => context.Users.Add(user);

    /// <inheritdoc />
    public void Add(Membership membership) => context.Memberships.Add(membership);
}

/// <inheritdoc />
internal sealed class ApiKeyRepository(ConcordatDbContext context) : IApiKeyRepository
{
    /// <inheritdoc />
    /// <remarks>
    /// <c>AsNoTracking</c> is deliberately <em>not</em> used: the authenticator updates
    /// <c>LastUsedAt</c> on the row it gets back, and a detached entity would silently discard
    /// that write.
    /// </remarks>
    public Task<ApiKey?> FindByKeyIdAsync(string keyId, CancellationToken cancellationToken) =>
        context.ApiKeys.SingleOrDefaultAsync(k => k.KeyId == keyId, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ApiKey>> ListAsync(
        TenantId tenantId, CancellationToken cancellationToken) =>
        await context.ApiKeys
            .Where(k => k.TenantId == tenantId)
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public Task<ApiKey?> FindAsync(
        TenantId tenantId, Guid id, CancellationToken cancellationToken) =>
        context.ApiKeys.SingleOrDefaultAsync(
            k => k.TenantId == tenantId && k.Id == id, cancellationToken);

    /// <inheritdoc />
    public void Add(ApiKey key) => context.ApiKeys.Add(key);
}

/// <inheritdoc />
internal sealed class TenantRepository(ConcordatDbContext context) : ITenantRepository
{
    /// <inheritdoc />
    public Task<Tenant?> FindAsync(TenantId id, CancellationToken cancellationToken) =>
        context.Tenants.SingleOrDefaultAsync(t => t.Id == id, cancellationToken);

    /// <inheritdoc />
    public Task<Tenant?> FindBySlugAsync(string slug, CancellationToken cancellationToken) =>
        context.Tenants.SingleOrDefaultAsync(t => t.Slug == slug, cancellationToken);

    /// <inheritdoc />
    public void Add(Tenant tenant) => context.Tenants.Add(tenant);
}
