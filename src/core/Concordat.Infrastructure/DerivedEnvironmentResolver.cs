using System.Security.Cryptography;
using System.Text;
using Concordat.Application.Abstractions;
using Concordat.Domain.Registry;

namespace Concordat.Infrastructure;

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
/// drift, and two processes agree without coordination. Names are lower-cased first, so
/// <c>prod</c> and <c>PROD</c> are the same environment.
/// </para>
/// <para>
/// <b>M7 obligation:</b> real environments must either adopt these derived ids or migrate the
/// existing <c>subject.environment_id</c> values.
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
