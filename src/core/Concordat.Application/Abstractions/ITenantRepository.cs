using Concordat.Domain.Identity;
using Concordat.Domain.Registry;

namespace Concordat.Application.Abstractions;

/// <summary>
/// Reads and writes organisations (M9.1).
/// </summary>
/// <remarks>
/// <b>Not tenant-filtered, and it cannot be.</b> This table is what everything else is filtered
/// <em>by</em>; a filter here would stop an organisation reading its own row. Authorisation for
/// these methods is therefore the caller's membership, checked above, rather than a query
/// filter — which is why every method takes the identity explicitly instead of implying it.
/// </remarks>
public interface ITenantRepository
{
    /// <summary>Finds an organisation by identity.</summary>
    /// <param name="id">The identity.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The tenant, or <see langword="null"/>.</returns>
    Task<Tenant?> FindAsync(TenantId id, CancellationToken cancellationToken);

    /// <summary>Finds an organisation by its URL-safe handle.</summary>
    /// <param name="slug">The handle.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The tenant, or <see langword="null"/>.</returns>
    Task<Tenant?> FindBySlugAsync(string slug, CancellationToken cancellationToken);

    /// <summary>Stages a new organisation for insert.</summary>
    /// <param name="tenant">The tenant.</param>
    void Add(Tenant tenant);
}
