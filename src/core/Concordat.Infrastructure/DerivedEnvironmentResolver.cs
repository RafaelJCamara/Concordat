using System.Security.Cryptography;
using System.Text;
using Concordat.Application.Abstractions;
using Concordat.Domain.Registry;

namespace Concordat.Infrastructure;

/// <summary>
/// Derives an environment id deterministically from its name and tenant.
/// </summary>
/// <remarks>
/// <para>
/// <b>An M1 shim that outlived M1.</b> Environments became a first-class aggregate in M7
/// (ADR-012), but <c>/environments/{env}/…</c> still turns a name into an id without a
/// lookup, so this is what every subject row's <c>environment_id</c> was computed by. Deriving
/// rather than auto-creating keeps it honest: it holds no state, so it cannot drift, and two
/// processes agree without coordination.
/// </para>
/// <para>
/// <b>The tenant is in the preimage as of M9.1, and it had to be.</b> Without it two
/// organisations that both name an environment <c>prod</c> derive the same id and collide on a
/// primary key — one of them fails to create it, and if the collision were ever missed they
/// would share subjects. This was recorded as a known M9 constraint when the aggregate landed
/// rather than discovered here.
/// </para>
/// <para>
/// <b><see cref="TenantId.SelfHosted"/> keeps the original preimage.</b> Every id an existing
/// installation has stored was computed without a tenant segment, and changing it would point
/// every subject at an environment that no longer exists — silently, because nothing joins on
/// it. One branch buys a zero-migration upgrade; the alternative is rewriting two columns on
/// live data to fix a collision that cannot happen in a single-tenant deployment.
/// </para>
/// </remarks>
public sealed class DerivedEnvironmentResolver(ITenantContext tenant) : IEnvironmentResolver
{
    private const string Namespace = "concordat-environment/v1:";

    /// <inheritdoc />
    public EnvironmentId Resolve(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var current = tenant.Current;
        var normalised = name.Trim().ToLowerInvariant();

        var preimage = current == TenantId.SelfHosted
            ? Namespace + normalised
            : $"{Namespace}{current.Value:D}:{normalised}";

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(preimage));

        return new EnvironmentId(new Guid(digest.AsSpan(0, 16)));
    }
}
