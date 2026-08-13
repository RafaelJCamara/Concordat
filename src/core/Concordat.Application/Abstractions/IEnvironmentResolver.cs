using Concordat.Domain.Registry;

namespace Concordat.Application.Abstractions;

/// <summary>Maps an environment name to the identifier subjects are scoped by.</summary>
/// <remarks>
/// A port rather than a concrete type because the answer changes shape in M7: today it is
/// derived from the name, then it becomes a lookup against real environment rows (ADR-012).
/// Reference resolution needs it too — a <c>concordat://</c> reference carries an environment
/// name, not an id.
/// </remarks>
public interface IEnvironmentResolver
{
    /// <summary>Resolves an environment name.</summary>
    /// <param name="name">The name, for example <c>prod</c>.</param>
    /// <returns>The identifier.</returns>
    EnvironmentId Resolve(string name);
}
