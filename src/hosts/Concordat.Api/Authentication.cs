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
    /// <b>For a single-user self-hosted instance that has not been claimed yet, and nothing
    /// else.</b> It exists because ADR-008 promises <c>docker compose up</c> produces something
    /// you can immediately use, and because the M4 web app was built against a stub that
    /// returned admin. Without it, upgrading into M8 locks every existing installation out of
    /// its own registry.
    /// </para>
    /// <para>
    /// <b>It turns itself off.</b> The moment the instance has an account, this is ignored —
    /// see <see cref="CallerResolver"/>. A flag that stays permissive after someone has
    /// deliberately created users is a flag somebody forgets, and the failure mode is an open
    /// registry that looks locked down.
    /// </para>
    /// </remarks>
    public bool AllowAnonymousUntilClaimed { get; set; } = true;

    /// <summary>How long a sign-in credential lasts.</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(12);
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

/// <summary>Declares what a route requires.</summary>
public static class ScopeRequirements
{
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
