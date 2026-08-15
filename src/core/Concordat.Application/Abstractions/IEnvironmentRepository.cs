using Concordat.Domain.Registry;
using Environment = Concordat.Domain.Registry.Environment;

namespace Concordat.Application.Abstractions;

/// <summary>Reads and writes environments (M7.1).</summary>
public interface IEnvironmentRepository
{
    /// <summary>Finds an environment by name.</summary>
    /// <param name="name">The name, as it appears in the route.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The environment, or <see langword="null"/>.</returns>
    /// <remarks>
    /// By name rather than by id, because the name is what every route carries. The id exists
    /// for subjects to reference and is never typed by anyone.
    /// </remarks>
    Task<Environment?> FindAsync(EnvironmentName name, CancellationToken cancellationToken);

    /// <summary>Finds an environment by id.</summary>
    /// <param name="id">The identity.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The environment, or <see langword="null"/>.</returns>
    Task<Environment?> FindAsync(EnvironmentId id, CancellationToken cancellationToken);

    /// <summary>
    /// Finds an environment by id for an explicitly named tenant, bypassing the ambient
    /// <see cref="ITenantContext"/>.
    /// </summary>
    /// <param name="tenantId">The tenant that owns the environment.</param>
    /// <param name="id">The identity.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The environment, or <see langword="null"/>.</returns>
    /// <remarks>
    /// For the background notification pump only, which claims outbox messages across every
    /// tenant and has no ambient tenant to read the environment's display name through.
    /// </remarks>
    Task<Environment?> FindAcrossTenantsAsync(
        TenantId tenantId, EnvironmentId id, CancellationToken cancellationToken);

    /// <summary>Lists every environment, ordered by name.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The environments.</returns>
    Task<IReadOnlyList<Environment>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Stages a new environment for insert.</summary>
    /// <param name="environment">The environment.</param>
    void Add(Environment environment);
}
