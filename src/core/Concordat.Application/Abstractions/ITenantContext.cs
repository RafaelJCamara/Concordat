using Concordat.Domain.Registry;

namespace Concordat.Application.Abstractions;

/// <summary>
/// Supplies the tenant every query and write is scoped to.
/// </summary>
/// <remarks>
/// <para>
/// One code path for both deployment flavours (DESIGN §8). Self-hosted binds a fixed tenant;
/// Cloud resolves one per request from the API key or session. Nothing above this interface
/// branches on which is in play, so there is no <c>if (cloud)</c> anywhere in query code.
/// </para>
/// <para>
/// Implementations must never return a tenant the caller has not been authenticated for —
/// this is the sole input to the global query filters, so a wrong answer silently returns
/// another tenant's data rather than failing.
/// </para>
/// </remarks>
public interface ITenantContext
{
    /// <summary>The tenant in scope for the current operation.</summary>
    TenantId Current { get; }
}
