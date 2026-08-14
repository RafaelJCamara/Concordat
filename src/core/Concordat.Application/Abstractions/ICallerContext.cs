using Concordat.Domain.Identity;
using Concordat.Domain.Registry;

namespace Concordat.Application.Abstractions;

/// <summary>How a caller proved who they are.</summary>
public enum CredentialKind
{
    /// <summary>Nobody: the request carried no credential, or one that did not verify.</summary>
    Anonymous = 0,

    /// <summary>A signed-in user.</summary>
    User = 1,

    /// <summary>An API key, from CI or an SDK.</summary>
    ApiKey = 2,
}

/// <summary>
/// Who is making the current request, and what they may do (M8.2).
/// </summary>
/// <remarks>
/// <para>
/// Resolved once per request by the API's authentication middleware and injected wherever the
/// answer is needed. Handlers ask this rather than reading a header, so there is one place that
/// decides what a credential means and no second interpretation to disagree with it.
/// </para>
/// <para>
/// <b>An unauthenticated caller is a real value, not a null.</b> A nullable context invites
/// <c>caller?.Allows(...) ?? true</c>, which is the shape of every authorization bug that ever
/// shipped. <see cref="Caller.Anonymous"/> holds <see cref="ScopeSet.None"/> and allows
/// nothing.
/// </para>
/// </remarks>
public interface ICallerContext
{
    /// <summary>The current caller.</summary>
    Caller Current { get; }
}

/// <summary>Who is making the current request.</summary>
/// <param name="Kind">How they proved it.</param>
/// <param name="TenantId">Which tenant they act in.</param>
/// <param name="Scopes">What they may do.</param>
/// <param name="Actor">How the audit trail names them.</param>
/// <param name="UserId">The signed-in user, when there is one.</param>
/// <param name="ApiKeyId">The key's public id, when a key was used.</param>
public sealed record Caller(
    CredentialKind Kind,
    TenantId TenantId,
    ScopeSet Scopes,
    ActorId Actor,
    UserId? UserId = null,
    string? ApiKeyId = null)
{
    /// <summary>Nobody, holding nothing.</summary>
    public static Caller Anonymous { get; } = new(
        CredentialKind.Anonymous,
        TenantId.SelfHosted,
        ScopeSet.None,
        ActorId.Create("anonymous").Value);

    /// <summary>
    /// The implicit owner of a self-hosted instance that has no accounts yet.
    /// </summary>
    /// <remarks>
    /// <b>It stops existing the moment anyone creates an account.</b> ADR-008 promises that
    /// <c>docker compose up</c> gives you something you can use immediately, and the M4 web app
    /// was built against a stub that returned admin — without this, adopting M8 locks every
    /// existing installation out of its own registry. The API decides when this applies; it is
    /// never the answer for a request that presented a credential and failed to verify.
    /// </remarks>
    public static Caller Unclaimed { get; } = new(
        CredentialKind.User,
        TenantId.SelfHosted,
        Roles.SetFor(Role.Owner),
        ActorId.Create("unclaimed-instance").Value);

    /// <summary>Whether this caller authenticated at all.</summary>
    public bool IsAuthenticated => Kind is not CredentialKind.Anonymous;

    /// <summary>Whether this caller holds a scope.</summary>
    /// <param name="scope">The scope an endpoint requires.</param>
    /// <returns>True when the caller may proceed.</returns>
    public bool Allows(string scope) => Scopes.Allows(scope);
}
