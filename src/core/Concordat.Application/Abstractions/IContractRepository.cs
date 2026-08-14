using Concordat.Domain.Contracts;
using Concordat.Domain.Registry;

namespace Concordat.Application.Abstractions;

/// <summary>Reads and writes contracts (M7.3).</summary>
public interface IContractRepository
{
    /// <summary>Finds a contract by name within an environment.</summary>
    /// <param name="environmentId">The environment.</param>
    /// <param name="name">The contract name.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The contract, or <see langword="null"/>.</returns>
    Task<Contract?> FindAsync(
        EnvironmentId environmentId, string name, CancellationToken cancellationToken);

    /// <summary>Lists the contracts in an environment, ordered by name.</summary>
    /// <param name="environmentId">The environment.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The contracts.</returns>
    Task<IReadOnlyList<Contract>> ListAsync(
        EnvironmentId environmentId, CancellationToken cancellationToken);

    /// <summary>Stages a new contract for insert.</summary>
    /// <param name="contract">The contract.</param>
    void Add(Contract contract);
}
