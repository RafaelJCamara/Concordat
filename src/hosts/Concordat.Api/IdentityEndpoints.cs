using Concordat.Application.Abstractions;
using Concordat.Application.Identity;
using Concordat.Domain.Identity;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Concordat.Api;

/// <summary>M8's identity routes: sign-in, members and API keys.</summary>
public static class IdentityEndpoints
{
    /// <summary>Maps the identity routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var auth = app.MapGroup("/v1/auth").WithTags("Identity");

        auth.MapGet("/status", Status)
            .WithSummary("Whether this instance has been claimed, and who you are")
            .WithDescription(
                "Unauthenticated on purpose: a sign-in screen has to be able to ask whether " +
                "there is anyone to sign in as.")
            .Produces<AuthStatusResponse>();

        auth.MapPost("/bootstrap", Bootstrap)
            .WithSummary("Create the first owner")
            .WithDescription(
                "Works exactly once. ADR-008 promises 'docker compose up' produces a working " +
                "authenticated instance with no external dependency, which means the first run " +
                "must be able to create an account — and every run after it must not. When the " +
                "host has a bootstrap token configured, the request must carry it in 'token' " +
                "or this answers 401 -- otherwise whoever reaches a public instance first would " +
                "keep it.")
            .Produces<MemberResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequireRateLimiting("auth-attempt");

        auth.MapPost("/signup", SignUp)
            .WithSummary("Create an organisation and its first owner")
            .WithDescription(
                "Cloud's equivalent of first-run bootstrap. On a self-hosted deployment there " +
                "is one organisation and this is not the way to get it — use /bootstrap.")
            .Produces<SignedUpResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequireRateLimiting("auth-attempt");

        auth.MapPost("/signin", SignIn)
            .WithSummary("Exchange an email and password for a credential")
            .WithDescription(
                "The credential is a short-lived API key, not a second token format: one thing " +
                "to verify, one thing to revoke, and sign-in exercises the same path CI does. " +
                "It is also set as an httpOnly cookie so a browser reload does not sign you " +
                "out -- see POST /v1/auth/resume.")
            .Produces<SignInResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireRateLimiting("auth-attempt");

        auth.MapPost("/resume", Resume)
            .WithSummary("Recover the credential from the session cookie")
            .WithDescription(
                "The only route that accepts the session cookie, and it accepts nothing else. " +
                "Its whole power is handing back a credential the browser already holds, so " +
                "every other route still needs an Authorization header -- which is what makes " +
                "CSRF structurally impossible rather than merely mitigated. " +
                "204 when there is no cookie at all, 401 only when one was presented and did " +
                "not verify: a signed-out visitor is not an authentication failure, and " +
                "answering 401 made every signed-out page load log a console error.")
            .Produces<SignInResponse>()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        auth.MapPost("/signout", SignOut)
            .WithSummary("Clear the session cookie")
            .WithDescription(
                "Needed because script cannot delete an httpOnly cookie. The API key itself is " +
                "left to expire: revoking it here would mean a row deleted per browser tab " +
                "closed, and a user who needs it gone sooner revokes it from the keys screen.")
            .Produces(StatusCodes.Status204NoContent);

        var members = app.MapGroup("/v1/members").WithTags("Identity")
            .RequireScope(Scope.OrgAdmin);

        members.MapGet("/", ListMembers)
            .WithSummary("List the tenant's members")
            .Produces<IReadOnlyList<MemberResponse>>();

        members.MapPost("/", CreateMember)
            .WithSummary("Add a member")
            .Produces<MemberResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        members.MapPut("/{userId:guid}/role", ChangeRole)
            .WithSummary("Change a member's role")
            .Produces<MemberRoleResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        var keys = app.MapGroup("/v1/api-keys").WithTags("Identity")
            .RequireScope(Scope.OrgAdmin);

        keys.MapGet("/", ListKeys)
            .WithSummary("List the tenant's API keys")
            .WithDescription("Never their secrets — those exist only in the issue response.")
            .Produces<IReadOnlyList<ApiKeyResponse>>();

        keys.MapPost("/", IssueKey)
            .WithSummary("Issue an API key")
            .WithDescription(
                "The secret is returned once and never again. A key may not grant more than " +
                "its issuer holds.")
            .Produces<IssuedApiKeyResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        keys.MapDelete("/{id:guid}", RevokeKey)
            .WithSummary("Revoke an API key")
            .Produces<ApiKeyResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> Status(
        ICallerContext caller, IUserRepository users, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(users);

        var claimed = await users.AnyAsync(cancellationToken).ConfigureAwait(false);
        var current = caller.Current;

        return TypedResults.Ok(new AuthStatusResponse(
            claimed,
            current.IsAuthenticated,
            current.IsAuthenticated ? current.Actor.Value : null,
            current.Scopes.Granted));
    }

    private static async Task<IResult> Bootstrap(
        BootstrapRequest request, IDispatcher dispatcher, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await dispatcher.SendAsync(
            new BootstrapOwnerCommand(
                request.Email, request.DisplayName, request.Password, request.Token),
            cancellationToken).ConfigureAwait(false);

        return result.IsFailure
            ? ProblemDetailsMapping.From(result.Error!)
            : TypedResults.Created(
                "/v1/members", MemberResponse.From(result.Value, Role.Owner));
    }

    private static async Task<IResult> SignUp(
        SignUpRequest request, IDispatcher dispatcher, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await dispatcher.SendAsync(
            new SignUpCommand(
                request.OrganisationName,
                request.Slug,
                request.Email,
                request.DisplayName,
                request.Password),
            cancellationToken).ConfigureAwait(false);

        return result.IsFailure
            ? ProblemDetailsMapping.From(result.Error!)
            : TypedResults.Created(
                "/v1/members",
                new SignedUpResponse(
                    result.Value.Tenant.Id.Value,
                    result.Value.Tenant.Name,
                    result.Value.Tenant.Slug,
                    MemberResponse.From(result.Value.Owner, Role.Owner)));
    }

    private static async Task<IResult> SignIn(
        SignInRequest request,
        HttpContext http,
        Authenticator authenticator,
        IApiKeyRepository apiKeys,
        IUnitOfWork unitOfWork,
        IOptions<AuthenticationOptions> options,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(authenticator);
        ArgumentNullException.ThrowIfNull(options);

        var caller = await authenticator.FromPasswordAsync(
            request.Email, request.Password, request.Organisation, cancellationToken)
            .ConfigureAwait(false);

        if (!caller.IsAuthenticated)
        {
            // One answer for a wrong address, a wrong password, a disabled account and a user
            // with no membership here. Which one it was is not information a caller who knows
            // their own credential needs, and it is information an attacker cannot otherwise
            // get.
            return ProblemDetailsMapping.From(new DomainError(
                ConcordatCodes.Unauthenticated, "That email and password do not match."));
        }

        var now = clock.GetUtcNow();

        var session = ApiKey.Issue(
            caller.TenantId,
            caller.UserId,
            $"session for {caller.Actor.Value}",
            caller.Scopes.Granted,
            now,
            now + options.Value.SessionLifetime,
            ApiKeyKind.Session,
            // The person, not the key. Otherwise every change made through the web app is
            // attributed to "key:session for alice@example.com" and the audit trail stops
            // naming anybody.
            caller.Actor);

        if (session.IsFailure)
        {
            return ProblemDetailsMapping.From(session.Error!);
        }

        apiKeys.Add(session.Value.Key);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        SessionCookie.Issue(http, session.Value.Secret, session.Value.Key.ExpiresAt!.Value);

        return TypedResults.Ok(new SignInResponse(
            session.Value.Secret,
            caller.Actor.Value,
            caller.Scopes.Granted,
            session.Value.Key.ExpiresAt!.Value));
    }

    /// <summary>
    /// Hands back the credential the browser is already holding (decision 26).
    /// </summary>
    /// <remarks>
    /// <b>The credential lives in memory in the SPA, deliberately</b> — a token in
    /// <c>localStorage</c> is readable by any script that runs on the page, and this project has
    /// already declined to ship one XSS hole (ADR-006). The cost was that F5 signed you out,
    /// which is irritating enough that somebody would eventually have fixed it the wrong way.
    /// </remarks>
    private static async Task<IResult> Resume(
        HttpContext http,
        Authenticator authenticator,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(authenticator);

        var credential = http.Request.Cookies[SessionCookie.Name];

        if (string.IsNullOrEmpty(credential))
        {
            // NO COOKIE IS NOT AN AUTHENTICATION FAILURE.
            //
            // The caller is not trying to authenticate; it is asking whether it already is, and
            // "no" is an ordinary answer. 401 was the first shape and it made every signed-out
            // page load log a console error, because the app must probe on startup -- it cannot
            // read an httpOnly cookie to find out whether probing is worthwhile. A browser test
            // asserting a clean console is what surfaced it.
            //
            // The same absent-versus-invalid distinction this codebase draws everywhere else.
            return TypedResults.NoContent();
        }

        var caller = await authenticator
            .FromApiKeyAsync(credential, cancellationToken).ConfigureAwait(false);

        if (!caller.IsAuthenticated)
        {
            // Expired, revoked, or from a registry that has since been rebuilt. Clearing it is
            // the difference between "sign in again" and a browser that retries a dead cookie on
            // every load forever.
            SessionCookie.Clear(http);

            return ProblemDetailsMapping.From(new DomainError(
                ConcordatCodes.Unauthenticated, "That session is no longer valid."));
        }

        // The expiry is not re-read from the key here: Resume returns what the caller needs to
        // act, and the key's own expiry already governs whether it works. Sliding it would turn
        // a short-lived session into an indefinite one held open by a page left in a tab.
        return TypedResults.Ok(new SignInResponse(
            credential,
            caller.Actor.Value,
            caller.Scopes.Granted,
            clock.GetUtcNow()));
    }

    private static NoContent SignOut(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        SessionCookie.Clear(http);

        return TypedResults.NoContent();
    }

    private static async Task<IResult> ListMembers(
        IDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var result = await dispatcher.QueryAsync(new ListMembersQuery(), cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ProblemDetailsMapping.From(result.Error!)
            : TypedResults.Ok(
                result.Value.Select(m => MemberResponse.From(m.User, m.Role)).ToList());
    }

    private static async Task<IResult> CreateMember(
        CreateMemberRequest request, IDispatcher dispatcher, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await dispatcher.SendAsync(
            new CreateMemberCommand(
                request.Email, request.DisplayName, request.Password, request.Role),
            cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            return ProblemDetailsMapping.From(result.Error!);
        }

        Roles.Parse(request.Role ?? "READER", out var role);

        return TypedResults.Created(
            $"/v1/members/{result.Value.Id.Value}", MemberResponse.From(result.Value, role));
    }

    private static async Task<IResult> ChangeRole(
        Guid userId,
        ChangeRoleRequest request,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await dispatcher.SendAsync(
            new ChangeMemberRoleCommand(userId, request.Role), cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ProblemDetailsMapping.From(result.Error!)
            : TypedResults.Ok(new MemberRoleResponse(
                result.Value.UserId.Value, Roles.For(result.Value.Role)));
    }

    private static async Task<IResult> ListKeys(
        IDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var result = await dispatcher.QueryAsync(new ListApiKeysQuery(), cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ProblemDetailsMapping.From(result.Error!)
            : TypedResults.Ok(result.Value
                .Where(k => k.Kind is ApiKeyKind.Standing)
                .Select(ApiKeyResponse.From)
                .ToList());
    }

    private static async Task<IResult> IssueKey(
        IssueApiKeyRequest request, IDispatcher dispatcher, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await dispatcher.SendAsync(
            new IssueApiKeyCommand(request.Label, request.Scopes, request.ExpiresAt),
            cancellationToken).ConfigureAwait(false);

        return result.IsFailure
            ? ProblemDetailsMapping.From(result.Error!)
            : TypedResults.Created(
                $"/v1/api-keys/{result.Value.Key.Id}",
                new IssuedApiKeyResponse(
                    ApiKeyResponse.From(result.Value.Key), result.Value.Secret));
    }

    private static async Task<IResult> RevokeKey(
        Guid id, IDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var result = await dispatcher.SendAsync(new RevokeApiKeyCommand(id), cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ProblemDetailsMapping.From(result.Error!)
            : TypedResults.Ok(ApiKeyResponse.From(result.Value));
    }
}

// ------------------------------------------------------------------------- requests

/// <summary>Request to create the first owner.</summary>
/// <param name="Email">The login.</param>
/// <param name="Password">At least 12 characters.</param>
/// <param name="DisplayName">What to show; defaults to the address's local part.</param>
/// <param name="Token">
/// The shared secret, required only when the host has a bootstrap token configured.
/// </param>
public sealed record BootstrapRequest(
    string Email, string Password, string? DisplayName = null, string? Token = null);

/// <summary>Request to create an organisation and its first owner.</summary>
/// <param name="OrganisationName">What the organisation calls itself.</param>
/// <param name="Email">The owner's login.</param>
/// <param name="Password">At least 12 characters.</param>
/// <param name="Slug">A URL-safe handle, or omit to derive one from the name.</param>
/// <param name="DisplayName">What to show for the owner.</param>
public sealed record SignUpRequest(
    string OrganisationName,
    string Email,
    string Password,
    string? Slug = null,
    string? DisplayName = null);

/// <summary>A newly created organisation and its owner.</summary>
/// <param name="TenantId">The organisation's identity.</param>
/// <param name="Name">What it calls itself.</param>
/// <param name="Slug">Its URL-safe handle.</param>
/// <param name="Owner">The first owner.</param>
public sealed record SignedUpResponse(
    Guid TenantId, string Name, string Slug, MemberResponse Owner);

/// <summary>Request to sign in.</summary>
/// <param name="Email">The login.</param>
/// <param name="Password">The password.</param>
/// <param name="Organisation">
/// Which organisation to sign in to, by slug. Only needed by someone who belongs to more than
/// one — which is nobody on a self-hosted deployment.
/// </param>
public sealed record SignInRequest(string Email, string Password, string? Organisation = null);

/// <summary>Request to add a member.</summary>
/// <param name="Email">Their login.</param>
/// <param name="Password">An initial password, at least 12 characters.</param>
/// <param name="Role"><c>READER</c>, <c>ADMIN</c> or <c>OWNER</c>; defaults to READER.</param>
/// <param name="DisplayName">What to show.</param>
public sealed record CreateMemberRequest(
    string Email, string Password, string? Role = null, string? DisplayName = null);

/// <summary>Request to change a member's role.</summary>
/// <param name="Role">The new role.</param>
public sealed record ChangeRoleRequest(string Role);

/// <summary>Request to issue an API key.</summary>
/// <param name="Label">What it is for. Required — an unlabelled key is one nobody dares revoke.</param>
/// <param name="Scopes">What it may do. Never more than the issuer holds.</param>
/// <param name="ExpiresAt">When it should stop working, or omit for a key that never expires.</param>
public sealed record IssueApiKeyRequest(
    string Label, IReadOnlyList<string> Scopes, DateTimeOffset? ExpiresAt = null);

// ------------------------------------------------------------------------ responses

/// <summary>Whether this instance has been claimed, and who the caller is.</summary>
/// <param name="Claimed">Whether any account exists.</param>
/// <param name="Authenticated">Whether this request carried a credential that verified.</param>
/// <param name="Actor">Who the caller is, when there is one.</param>
/// <param name="Scopes">What the caller may do.</param>
public sealed record AuthStatusResponse(
    bool Claimed, bool Authenticated, string? Actor, IReadOnlyList<string> Scopes);

/// <summary>A credential and what it grants.</summary>
/// <param name="Credential">Send this as <c>Authorization: Bearer</c>. Hold it in memory only.</param>
/// <param name="Actor">Who you are, as the audit trail will record it.</param>
/// <param name="Scopes">What you may do.</param>
/// <param name="ExpiresAt">When the credential stops working.</param>
public sealed record SignInResponse(
    string Credential, string Actor, IReadOnlyList<string> Scopes, DateTimeOffset ExpiresAt);

/// <summary>A member.</summary>
/// <param name="UserId">Their identity.</param>
/// <param name="Email">Their login.</param>
/// <param name="DisplayName">What to show.</param>
/// <param name="Role">Their role.</param>
/// <param name="Disabled">Whether they may sign in.</param>
/// <param name="CreatedAt">When the account was created.</param>
/// <param name="LastSignedInAt">When they last signed in.</param>
public sealed record MemberResponse(
    Guid UserId,
    string Email,
    string DisplayName,
    string Role,
    bool Disabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSignedInAt)
{
    /// <summary>Projects a member. <b>Never the password hash.</b></summary>
    /// <param name="user">The account.</param>
    /// <param name="role">Their role.</param>
    /// <returns>The wire form.</returns>
    public static MemberResponse From(User user, Role role)
    {
        ArgumentNullException.ThrowIfNull(user);

        return new MemberResponse(
            user.Id.Value,
            user.Email.Value,
            user.DisplayName,
            Roles.For(role),
            user.Disabled,
            user.CreatedAt,
            user.LastSignedInAt);
    }
}

/// <summary>A member's role after a change.</summary>
/// <param name="UserId">Their identity.</param>
/// <param name="Role">The new role.</param>
public sealed record MemberRoleResponse(Guid UserId, string Role);

/// <summary>An API key, without its secret.</summary>
/// <param name="Id">Its identity, for revocation.</param>
/// <param name="KeyId">The public half, which appears in the credential and in logs.</param>
/// <param name="Label">What it is for.</param>
/// <param name="Scopes">What it may do.</param>
/// <param name="CreatedAt">When it was issued.</param>
/// <param name="ExpiresAt">When it stops working, or null.</param>
/// <param name="LastUsedAt">When it last authenticated, or null.</param>
/// <param name="RevokedAt">When it was revoked, or null.</param>
public sealed record ApiKeyResponse(
    Guid Id,
    string KeyId,
    string Label,
    IReadOnlyList<string> Scopes,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt)
{
    /// <summary>Projects a key. <b>Never the secret or its hash.</b></summary>
    /// <param name="key">The key.</param>
    /// <returns>The wire form.</returns>
    public static ApiKeyResponse From(ApiKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return new ApiKeyResponse(
            key.Id,
            key.KeyId,
            key.Label,
            key.Scopes,
            key.CreatedAt,
            key.ExpiresAt,
            key.LastUsedAt,
            key.RevokedAt);
    }
}

/// <summary>A newly issued key and its one-time secret.</summary>
/// <param name="Key">The key.</param>
/// <param name="Secret">
/// The full credential. <b>This is the only time it is ever returned</b> — the server stores
/// only a hash and cannot show it again.
/// </param>
public sealed record IssuedApiKeyResponse(ApiKeyResponse Key, string Secret);
