using Concordat.Domain.Results;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

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

        // State conflicts: the request was well-formed but the world says no.
        ConcordatCodes.SubjectAlreadyExists => StatusCodes.Status409Conflict,
        ConcordatCodes.EnvironmentAlreadyExists => StatusCodes.Status409Conflict,
        ConcordatCodes.BrokerAlreadyExists => StatusCodes.Status409Conflict,
        ConcordatCodes.ContractAlreadyExists => StatusCodes.Status409Conflict,

        // The request is well-formed; it is the existing bindings that refuse it.
        ConcordatCodes.BindingConflict => StatusCodes.Status409Conflict,
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
