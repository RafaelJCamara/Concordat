using System.Security.Cryptography;
using System.Text;
using Concordat.Domain.Registry;

namespace Concordat.Api;

/// <summary>Maps an environment name in a URL to the identifier subjects are scoped by.</summary>
public interface IEnvironmentResolver
{
    /// <summary>Resolves an environment name.</summary>
    /// <param name="name">The name from the route, for example <c>prod</c>.</param>
    /// <returns>The identifier.</returns>
    EnvironmentId Resolve(string name);
}

/// <summary>
/// Derives an environment id deterministically from its name.
/// </summary>
/// <remarks>
/// <para>
/// <b>An M1 shim.</b> Environments are a first-class aggregate in M7 (ADR-012) with real rows,
/// broker connections and a default policy. Until those exist there is nothing to look an id
/// up in, and the API still needs <c>/environments/{env}/…</c> to work.
/// </para>
/// <para>
/// Deriving rather than auto-creating keeps the shim honest: it holds no state, so it cannot
/// drift, and two processes agree without coordination. It also means <c>prod</c> and
/// <c>PROD</c> are different environments, which is why the name is lower-cased first.
/// </para>
/// <para>
/// <b>M7 obligation:</b> when real environments arrive they must either adopt these derived
/// ids or migrate existing <c>subject.environment_id</c> values. Recorded in
/// DECISIONS-PENDING.md.
/// </para>
/// </remarks>
public sealed class DerivedEnvironmentResolver : IEnvironmentResolver
{
    private const string Namespace = "concordat-environment/v1:";

    /// <inheritdoc />
    public EnvironmentId Resolve(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var digest = SHA256.HashData(
            Encoding.UTF8.GetBytes(Namespace + name.Trim().ToLowerInvariant()));

        return new EnvironmentId(new Guid(digest.AsSpan(0, 16)));
    }
}
