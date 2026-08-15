using Concordat.Domain.Results;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Concordat.Api;

/// <summary>
/// Turns a domain failure into an RFC 9457 Problem Details response.
/// </summary>
/// <remarks>
/// <para>
/// The <c>concordatCode</c> is the contract, not the HTTP status. Statuses are coarse — three
/// unrelated failures share 409 — so a client that branches on status alone cannot tell a
/// name collision from an incompatible schema. Confluent's opaque numeric codes are the
/// counter-example this exists to avoid.
/// </para>
/// <para>
/// Every code the domain can emit must appear in <see cref="StatusFor"/>. An unmapped code
/// falls through to 400, which is a deliberate choice: a wrong-but-safe status beats an
/// unhandled exception, and the fallback is visible in the response body.
/// </para>
/// </remarks>
public static class ProblemDetailsMapping
{
    private const string TypeBase = "https://concordat.dev/errors/";

    /// <summary>Maps a domain error to an HTTP status code.</summary>
    /// <param name="code">A constant from <see cref="ConcordatCodes"/>.</param>
    /// <returns>The status to return.</returns>
    public static int StatusFor(string code) => code switch
    {
        // Gone or never existed. Schema refusal deliberately lands here too: telling a caller
        // "forbidden" would confirm another tenant's schema exists.
        ConcordatCodes.SubjectNotFound => StatusCodes.Status404NotFound,
        ConcordatCodes.VersionNotFound => StatusCodes.Status404NotFound,
        ConcordatCodes.SchemaNotFound => StatusCodes.Status404NotFound,
        ConcordatCodes.EnvironmentNotFound => StatusCodes.Status404NotFound,
        ConcordatCodes.BrokerNotFound => StatusCodes.Status404NotFound,
        ConcordatCodes.ContractNotFound => StatusCodes.Status404NotFound,
        ConcordatCodes.ServiceNotFound => StatusCodes.Status404NotFound,
        ConcordatCodes.SubscriptionNotFound => StatusCodes.Status404NotFound,
        ConcordatCodes.UserNotFound => StatusCodes.Status404NotFound,
        ConcordatCodes.ApiKeyNotFound => StatusCodes.Status404NotFound,
        ConcordatCodes.TenantNotFound => StatusCodes.Status404NotFound,

        // M8. Unauthenticated is 401 and insufficient_scope is 403, and the difference is the
        // whole point: 401 means "tell me who you are", 403 means "I know who you are and the
        // answer is still no". Collapsing them sends a client into a sign-in loop it cannot win.
        ConcordatCodes.Unauthenticated => StatusCodes.Status401Unauthorized,
        ConcordatCodes.InsufficientScope => StatusCodes.Status403Forbidden,

        // 403 and not 400. The request is well formed and the credential is valid — what refused
        // it is a property of the environment. Falling through to the 400 default would tell a
        // pipeline author their payload was malformed, which is the one thing it was not.
        ConcordatCodes.RegistrationPolicyForbids => StatusCodes.Status403Forbidden,

        // 402, not 403. The caller has the right to do this and their plan does not stretch to
        // it — a distinction a client can act on by upgrading rather than by asking an admin
        // for a scope they already have.
        ConcordatCodes.PlanLimitReached => StatusCodes.Status402PaymentRequired,

        // State conflicts: the request was well-formed but the world says no.
        ConcordatCodes.SubjectAlreadyExists => StatusCodes.Status409Conflict,
        ConcordatCodes.EnvironmentAlreadyExists => StatusCodes.Status409Conflict,
        ConcordatCodes.BrokerAlreadyExists => StatusCodes.Status409Conflict,
        ConcordatCodes.ContractAlreadyExists => StatusCodes.Status409Conflict,
        ConcordatCodes.UserAlreadyExists => StatusCodes.Status409Conflict,
        ConcordatCodes.TenantAlreadyExists => StatusCodes.Status409Conflict,

        // The request is well-formed; it is the existing bindings that refuse it.
        ConcordatCodes.BindingConflict => StatusCodes.Status409Conflict,

        // Two requests raced at the database. See DbConflictExceptionHandler for where this
        // code comes from -- it is never raised by application code directly.
        ConcordatCodes.ConcurrentWriteConflict => StatusCodes.Status409Conflict,

        // The source version exists but is not something that may be promoted.
        ConcordatCodes.PromotionSourceNotActive => StatusCodes.Status409Conflict,
        ConcordatCodes.SubjectRetired => StatusCodes.Status409Conflict,
        ConcordatCodes.LifecycleTransitionInvalid => StatusCodes.Status409Conflict,
        ConcordatCodes.VersionNotAwaitingApproval => StatusCodes.Status409Conflict,
        ConcordatCodes.FormatMismatch => StatusCodes.Status409Conflict,
        ConcordatCodes.SemverNotIncreasing => StatusCodes.Status409Conflict,
        ConcordatCodes.SemverLabelUnderstatesBreakage => StatusCodes.Status409Conflict,
        ConcordatCodes.VerdictPolicyMismatch => StatusCodes.Status409Conflict,
        ConcordatCodes.ReferenceCycle => StatusCodes.Status409Conflict,

        // Too big is its own status; everything else malformed is a 400.
        ConcordatCodes.SchemaTooLarge => StatusCodes.Status413PayloadTooLarge,

        _ => StatusCodes.Status400BadRequest,
    };

    /// <summary>Builds a Problem Details response for a domain failure.</summary>
    /// <param name="error">The failure.</param>
    /// <param name="extensions">Extra members to include, such as <c>breakingChanges</c>.</param>
    /// <returns>The response.</returns>
    public static ProblemHttpResult From(
        DomainError error, IDictionary<string, object?>? extensions = null)
    {
        ArgumentNullException.ThrowIfNull(error);

        var status = StatusFor(error.Code);
        var problem = new ProblemDetails
        {
            Type = TypeBase + error.Code.Replace('_', '-'),
            Title = error.Code,
            Status = status,
            Detail = error.Message,
        };

        // The stable string code, alongside the type URI. Clients branch on this.
        problem.Extensions["concordatCode"] = error.Code;

        if (extensions is not null)
        {
            foreach (var (key, value) in extensions)
            {
                problem.Extensions[key] = value;
            }
        }

        return TypedResults.Problem(problem);
    }
}

/// <summary>
/// Turns a race the database caught into a 409, before it reaches the generic 500 handler.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing in application code catches these, and that is deliberate.</b> Every write path
/// checks for an existing row before creating one, but that check and the insert are two
/// separate statements: two requests can both pass the check and then both insert, and only
/// the database's own unique constraint — or, for a row already loaded and changed underneath
/// a caller, the <c>xmin</c> concurrency token — can catch the second one. Catching that here,
/// once, is simpler and more honest than teaching every handler to guess which of its own
/// constraints just fired.
/// </para>
/// <para>
/// Registered ahead of the default problem-details handler, so a race lands as 409 with
/// <c>ConcordatCodes.ConcurrentWriteConflict</c> instead of an unhandled-exception 500 — the
/// same shape every other refusal in this API already has.
/// </para>
/// </remarks>
public sealed class DbConflictExceptionHandler : IExceptionHandler
{
    /// <inheritdoc />
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        var message = exception switch
        {
            DbUpdateConcurrencyException =>
                "This record was changed by another request in between your read and your " +
                "write. Reload it and try again.",

            // 23505 is Postgres's unique_violation SQLSTATE. Everything else DbUpdateException
            // can wrap (a check constraint, a foreign key, a transient connection failure) is
            // not a race between two legitimate requests and is left to the generic handler.
            DbUpdateException { InnerException: PostgresException { SqlState: "23505" } } =>
                "Another request created a conflicting record first. Retry the operation " +
                "against the current state.",

            _ => null,
        };

        if (message is null)
        {
            return false;
        }

        var problem = ProblemDetailsMapping.From(
            new DomainError(ConcordatCodes.ConcurrentWriteConflict, message));

        await problem.ExecuteAsync(httpContext).ConfigureAwait(false);
        return true;
    }
}
