using Concordat.Application.Abstractions;
using Concordat.Domain.Registry;

namespace Concordat.Infrastructure;

/// <summary>
/// Which deployment flavour this process is (DESIGN §10, ADR-009).
/// </summary>
/// <remarks>
/// <b>The same image serves both.</b> Everything Cloud does is Apache-2.0 as well — Cloud
/// competes on managed upgrades, backups, HA, SLA and support, not on withheld code. The
/// profile picks implementations at the composition root and nothing below it branches, which
/// is what keeps "if (cloud)" out of query code where the failure mode is silently returning
/// another organisation's data.
/// </remarks>
public enum ConcordatProfile
{
    /// <summary>One organisation, one tenant row, no billing. The default.</summary>
    SelfHosted = 0,

    /// <summary>Many organisations on one deployment.</summary>
    Cloud = 1,
}

/// <summary>Binds a fixed tenant for every operation.</summary>
/// <remarks>
/// The self-hosted implementation of <see cref="ITenantContext"/>. Cloud swaps this at the
/// composition root for <see cref="CallerTenantContext"/>; no code above this line changes,
/// which is the point of wiring the seam from M1.5.
/// </remarks>
public sealed class SingleTenantContext : ITenantContext
{
    /// <inheritdoc />
    public TenantId Current => TenantId.SelfHosted;
}

/// <summary>
/// Resolves the tenant from whoever authenticated (M9.1).
/// </summary>
/// <remarks>
/// <para>
/// <b>The tenant comes from the credential, never from the request.</b> A header, a path
/// segment or a subdomain would all be caller-supplied, and this value is the sole input to
/// every global query filter — a wrong answer silently returns another organisation's data
/// rather than failing. An API key names its tenant because it was issued inside one, and a
/// session names the tenant its membership is in.
/// </para>
/// <para>
/// An unauthenticated caller resolves to <see cref="TenantId.SelfHosted"/>, which in a Cloud
/// deployment is an organisation nobody is a member of and therefore an empty view. That is
/// the safe direction: a filter that matches nothing beats one that matches everything.
/// </para>
/// </remarks>
public sealed class CallerTenantContext(ICallerContext caller) : ITenantContext
{
    /// <inheritdoc />
    public TenantId Current => caller.Current.TenantId;
}
