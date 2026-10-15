using Concordat.Application.Abstractions;
using Concordat.Application.Identity;
using Concordat.Domain.Identity;
using Concordat.Domain.Results;

namespace Concordat.Api;

/// <summary>
/// How much authentication this instance requires (M8.2).
/// </summary>
public sealed class AuthenticationOptions
{
    /// <summary>
    /// Whether an unauthenticated request is treated as a full-privilege owner.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Off by default, as of v1 (ADR-047).</b> It was on, so that <c>docker compose up</c> produced
    /// something usable with no account (ADR-008), and it is still available for exactly that: a
    /// single-user instance on a machine only its operator can reach, which opts in with
    /// <c>Concordat:Authentication:AllowAnonymousUntilClaimed=true</c>. An instance that is reachable by
    /// anybody else must not start as an open owner-level registry, so a fresh one now answers every
    /// request <c>401</c> except the ones that claim it (<c>/v1/auth/bootstrap</c>, <c>/v1/auth/status</c>,
    /// sign-in), and the web app shows its first-run setup screen.
    /// </para>
    /// <para>
    /// <b>It turns itself off.</b> The moment the instance has an account, this is ignored —
    /// see <see cref="CallerResolver"/>. A flag that stays permissive after someone has
    /// deliberately created users is a flag somebody forgets, and the failure mode is an open
    /// registry that looks locked down.
    /// </para>
    /// </remarks>
    public bool AllowAnonymousUntilClaimed { get; set; }

    /// <summary>How long a sign-in credential lasts.</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(12);

    /// <summary>
    /// A shared secret <c>POST /v1/auth/bootstrap</c> must be sent before it will create the
    /// first owner.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Closes the deploy-to-claim race.</b> <see cref="AllowAnonymousUntilClaimed"/> means an
    /// unclaimed instance answers every request as owner, so on a reachable deployment whoever
    /// calls <c>/bootstrap</c> first keeps the instance and locks the real operator out. Setting
    /// this (via <c>Concordat:Authentication:BootstrapToken</c>, e.g. the
    /// <c>CONCORDAT_BOOTSTRAP_TOKEN</c> environment variable) means possession of the deployment
    /// — not mere reachability — is what authorises the claim.
    /// </para>
    /// <para>
    /// <b>Null by default.</b> Left unset, whoever reaches <c>/v1/auth/bootstrap</c> first claims the
    /// instance, which is fine on a machine only its operator can reach and not on a public host:
    /// any internet-reachable deployment should set it.
    /// </para>
    /// </remarks>
    public string? BootstrapToken { get; set; }
}

/// <summary>Holds the caller resolved for the current request.</summary>
/// <remarks>
/// Scoped, written once by <see cref="AuthenticationMiddleware"/> and read everywhere else. The
/// setter is deliberately not on <see cref="ICallerContext"/>: a handler that could reassign
/// the caller is a handler that could grant itself scopes.
/// </remarks>
public sealed class CallerContext : ICallerContext
{
    /// <inheritdoc />
    public Caller Current { get; private set; } = Caller.Anonymous;

    /// <summary>Records who is calling. Called once per request, by the middleware.</summary>
    /// <param name="caller">The resolved caller.</param>
    internal void Set(Caller caller) => Current = caller;
}

/// <summary>
/// Resolves the caller for a request (M8.2).
/// </summary>
public sealed class CallerResolver(
    Authenticator authenticator,
    IUserRepository users,
    Microsoft.Extensions.Options.IOptions<AuthenticationOptions> options)
{
    private readonly AuthenticationOptions _options = options?.Value
        ?? throw new ArgumentNullException(nameof(options));

    /// <summary>Resolves a caller from an <c>Authorization</c> header.</summary>
    /// <param name="authorization">The raw header value.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The caller, which may be anonymous.</returns>
    public async Task<Caller> ResolveAsync(
        string? authorization, CancellationToken cancellationToken)
    {
        var credential = Bearer(authorization);

        if (!string.IsNullOrEmpty(credential))
        {
            var caller = await authenticator.FromApiKeyAsync(credential, cancellationToken)
                .ConfigureAwait(false);

            // A credential that was presented and did not verify is never silently downgraded
            // to the unclaimed-instance owner. Someone typing a stale key must get a 401, not
            // full access.
            return caller;
        }

        if (!_options.AllowAnonymousUntilClaimed)
        {
            return Caller.Anonymous;
        }

        // The instance is claimed the moment it has one account, and this stops applying with
        // no configuration change and nothing to remember.
        return await users.AnyAsync(cancellationToken).ConfigureAwait(false)
            ? Caller.Anonymous
            : Caller.Unclaimed;
    }

    private static string? Bearer(string? authorization)
    {
        if (string.IsNullOrWhiteSpace(authorization))
        {
            return null;
        }

        const string scheme = "Bearer ";

        return authorization.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)
            ? authorization[scheme.Length..].Trim()
            : authorization.Trim();
    }
}

/// <summary>Reads the configured bootstrap token from <see cref="AuthenticationOptions"/>.</summary>
public sealed class ConfiguredBootstrapPolicy(
    Microsoft.Extensions.Options.IOptions<AuthenticationOptions> options) : IBootstrapPolicy
{
    /// <inheritdoc />
    public string? RequiredToken => options.Value.BootstrapToken;
}

/// <summary>Resolves the caller before anything else looks at the request.</summary>
public sealed class AuthenticationMiddleware(RequestDelegate next)
{
    /// <summary>Runs the middleware.</summary>
    /// <param name="context">The request.</param>
    /// <param name="resolver">Resolves credentials.</param>
    /// <param name="caller">Where the answer goes.</param>
    /// <returns>A task.</returns>
    public async Task InvokeAsync(
        HttpContext context, CallerResolver resolver, CallerContext caller)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(caller);

        caller.Set(await resolver
            .ResolveAsync(context.Request.Headers.Authorization, context.RequestAborted)
            .ConfigureAwait(false));

        await next(context).ConfigureAwait(false);
    }
}

/// <summary>
/// Refuses a request whose caller lacks the scope an endpoint requires (ADR-018).
/// </summary>
/// <remarks>
/// An endpoint filter rather than a check inside each handler. A handler that forgets the check
/// is a write path that ships ungated, and the failure is silent — the endpoint works, for
/// everyone. Declaring the requirement next to the route makes the omission visible in review
/// and lets a test enumerate every mutating route (M8.3).
/// </remarks>
public sealed class RequireScopeFilter(string[] scopes) : IEndpointFilter
{
    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var caller = context.HttpContext.RequestServices
            .GetRequiredService<ICallerContext>().Current;

        if (!caller.IsAuthenticated)
        {
            return ProblemDetailsMapping.From(new DomainError(
                ConcordatCodes.Unauthenticated,
                "This endpoint needs a credential. Send one as 'Authorization: Bearer " +
                "cdt_...', or sign in."));
        }

        // Before the scope comparison, whose answer would be true and no help: this caller holds no
        // scopes, and none would help until the password is replaced (ADR-048).
        if (caller.MustChangePassword)
        {
            return PasswordChangeRequired.Refusal();
        }

        if (!caller.Scopes.AllowsAny(scopes))
        {
            return ProblemDetailsMapping.From(new DomainError(
                ConcordatCodes.InsufficientScope,
                $"This endpoint requires {string.Join(" or ", scopes)}. Your credential holds " +
                $"{string.Join(", ", caller.Scopes.Granted)}."));
        }

        return await next(context).ConfigureAwait(false);
    }
}

/// <summary>
/// Refuses a request that needs only that the caller is somebody (ADR-048).
/// </summary>
/// <remarks>
/// For the routes about one's own account: any signed-in person may change their own password, a Reader
/// included, so no scope is the right requirement, and a caller who must change their password is
/// <i>exactly</i> who is let through, because it is the one thing they may do. Declared like a scope
/// requirement (with an empty list) so the routes that carry it stay visible to the tests that
/// enumerate every route and to the OpenAPI document.
/// </remarks>
public sealed class RequireAuthenticatedFilter : IEndpointFilter
{
    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var caller = context.HttpContext.RequestServices
            .GetRequiredService<ICallerContext>().Current;

        if (!caller.IsAuthenticated)
        {
            return ProblemDetailsMapping.From(new DomainError(
                ConcordatCodes.Unauthenticated,
                "This endpoint needs a credential. Send one as 'Authorization: Bearer " +
                "cdt_...', or sign in."));
        }

        return await next(context).ConfigureAwait(false);
    }
}

/// <summary>The refusal a limited credential gets from every route that needs a scope.</summary>
public static class PasswordChangeRequired
{
    /// <summary>Builds the 403.</summary>
    /// <returns>The response.</returns>
    public static IResult Refusal() => ProblemDetailsMapping.From(new DomainError(
        ConcordatCodes.PasswordChangeRequired,
        "Your password was set by an administrator and has to be replaced before anything else " +
        "works. Change it at POST /v1/auth/password, or in the web app."));
}

/// <summary>Declares what a route requires.</summary>
public static class ScopeRequirements
{
    /// <summary>Requires the caller to be authenticated, and nothing more.</summary>
    /// <typeparam name="TBuilder">The builder type.</typeparam>
    /// <param name="builder">The route or group.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static TBuilder RequireAuthenticated<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddEndpointFilter(new RequireAuthenticatedFilter());
        builder.WithMetadata(new RequiredScopes([]));

        return builder;
    }

    /// <summary>Requires the caller to hold one of these scopes.</summary>
    /// <typeparam name="TBuilder">The builder type.</typeparam>
    /// <param name="builder">The route or group.</param>
    /// <param name="scopes">The acceptable scopes.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static TBuilder RequireScope<TBuilder>(this TBuilder builder, params string[] scopes)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(scopes);

        builder.AddEndpointFilter(new RequireScopeFilter(scopes));

        // Recorded as metadata so M8.3 can enumerate every route and assert that no mutating
        // one is missing a requirement. A convention nothing checks is a convention.
        builder.WithMetadata(new RequiredScopes(scopes));

        return builder;
    }
}

/// <summary>The scopes a route requires, for tests and for OpenAPI.</summary>
/// <param name="Scopes">The acceptable scopes.</param>
public sealed record RequiredScopes(IReadOnlyList<string> Scopes);
