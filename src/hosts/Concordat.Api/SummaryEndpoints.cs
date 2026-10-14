using Concordat.Application.Abstractions;
using Concordat.Application.Registry;
using Concordat.Domain.Identity;
using Concordat.Domain.Registry;

namespace Concordat.Api;

/// <summary>The environment overview route (ADR-037).</summary>
public static class SummaryEndpoints
{
    /// <summary>Maps the overview route.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapSummaryEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/v1/environments/{env}/summary", GetSummary)
            .WithTags("Summary")
            .WithSummary("The overview of one environment")
            .WithDescription(
                "What the dashboard shows, computed in the database with a fixed number of " +
                "aggregate queries and no per-subject loop: subject and version counts, the " +
                "newest subjects, the most recent versions held at the approval gate or " +
                "rejected, how many contracts and bindings are set to each enforcement mode, " +
                "and how many distinct violations SDKs have reported. Enforcement coverage is " +
                "what contracts DECLARE; the registry never sees message traffic, so it does " +
                "not say any service is enforcing anything, and a count of zero violations " +
                "means none were reported, not that none happened. An environment nothing has " +
                "been written to answers zeros rather than 404, as the subject list does.")
            .Produces<EnvironmentSummaryResponse>()
            .RequireScope(Scope.SubjectRead);

        return app;
    }

    private static async Task<IResult> GetSummary(
        string env,
        IDispatcher dispatcher,
        IEnvironmentResolver environments,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.QueryAsync(
            new GetEnvironmentSummaryQuery(environments.Resolve(env)), cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ProblemDetailsMapping.From(result.Error!)
            : TypedResults.Ok(EnvironmentSummaryResponse.From(result.Value));
    }
}

/// <summary>Subjects by lifecycle.</summary>
/// <param name="Total">Every subject, retired ones included.</param>
/// <param name="Active">In use.</param>
/// <param name="Deprecated">Advisory: consumers are asked to migrate.</param>
/// <param name="Retired">Soft-deleted.</param>
public sealed record SubjectTotalsResponse(int Total, int Active, int Deprecated, int Retired);

/// <summary>Versions by approval status.</summary>
/// <param name="Total">Every version of every status.</param>
/// <param name="Active"><c>ACTIVE</c>.</param>
/// <param name="AwaitingApproval"><c>AWAITING_APPROVAL</c>: held at the gate.</param>
/// <param name="Rejected"><c>REJECTED</c>.</param>
/// <param name="Dismissed"><c>DISMISSED</c>: reverted before anyone reviewed it.</param>
public sealed record VersionTotalsResponse(
    int Total, int Active, int AwaitingApproval, int Rejected, int Dismissed);

/// <summary>One of the most recently created subjects.</summary>
/// <param name="Name">The subject name.</param>
/// <param name="Format">The schema language.</param>
/// <param name="Owner">Who owns the contract.</param>
/// <param name="Lifecycle"><c>ACTIVE</c>, <c>DEPRECATED</c> or <c>RETIRED</c>.</param>
/// <param name="Latest">The gated latest pointer, or <see langword="null"/> when none is active.</param>
/// <param name="Versions">How many versions it has, of any status.</param>
/// <param name="AwaitingApproval">How many of them are held at the gate.</param>
/// <param name="CreatedAt">When the subject was created, UTC.</param>
public sealed record NewestSubjectResponse(
    string Name,
    string Format,
    string Owner,
    string Lifecycle,
    int? Latest,
    int Versions,
    int AwaitingApproval,
    DateTimeOffset CreatedAt);

/// <summary>A version held at the approval gate, or rejected.</summary>
/// <param name="Subject">The subject it belongs to.</param>
/// <param name="Ordinal">Its ordinal within the subject.</param>
/// <param name="Status"><c>AWAITING_APPROVAL</c> or <c>REJECTED</c>.</param>
/// <param name="SemanticVersion">The verified label, when it carried one.</param>
/// <param name="RegisteredAt">When it was proposed, UTC.</param>
/// <param name="RegisteredBy">Who proposed it.</param>
/// <param name="DecidedAt">When it was rejected, or <see langword="null"/> while it awaits a decision.</param>
/// <param name="DecidedBy">Who rejected it, or <see langword="null"/> while it awaits a decision.</param>
public sealed record HeldVersionResponse(
    string Subject,
    int Ordinal,
    string Status,
    string? SemanticVersion,
    DateTimeOffset RegisteredAt,
    string RegisteredBy,
    DateTimeOffset? DecidedAt,
    string? DecidedBy);

/// <summary>What is declared under one enforcement mode.</summary>
/// <param name="Mode"><c>OFF</c>, <c>MONITOR</c> or <c>ENFORCE</c>.</param>
/// <param name="Contracts">How many contracts are set to it.</param>
/// <param name="PublishBindings">How many publish bindings those contracts carry.</param>
/// <param name="ConsumeBindings">How many consume bindings those contracts carry.</param>
public sealed record EnforcementTierResponse(
    string Mode, int Contracts, int PublishBindings, int ConsumeBindings);

/// <summary>Violations SDKs have reported.</summary>
/// <param name="Distinct">How many distinct violations (not messages) have ever been reported.</param>
/// <param name="Recent">How many of them were last seen inside the window.</param>
/// <param name="WindowHours">The window <paramref name="Recent"/> is counted over.</param>
public sealed record ViolationTotalsResponse(int Distinct, int Recent, int WindowHours);

/// <summary>The overview of one environment (ADR-037).</summary>
/// <param name="Subjects">Subjects by lifecycle.</param>
/// <param name="Versions">Versions by approval status.</param>
/// <param name="NewestSubjects">The most recently created subjects, newest first.</param>
/// <param name="BreakingChanges">
/// The most recently proposed versions held at the gate or rejected, newest first. Approved ones
/// are live and are not listed.
/// </param>
/// <param name="Enforcement">One entry per mode, always <c>OFF</c>, <c>MONITOR</c>, <c>ENFORCE</c>.</param>
/// <param name="Violations">Violations SDKs have reported.</param>
public sealed record EnvironmentSummaryResponse(
    SubjectTotalsResponse Subjects,
    VersionTotalsResponse Versions,
    IReadOnlyList<NewestSubjectResponse> NewestSubjects,
    IReadOnlyList<HeldVersionResponse> BreakingChanges,
    IReadOnlyList<EnforcementTierResponse> Enforcement,
    ViolationTotalsResponse Violations)
{
    /// <summary>Projects an overview.</summary>
    /// <param name="summary">The overview.</param>
    /// <returns>The wire form.</returns>
    public static EnvironmentSummaryResponse From(EnvironmentSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        return new EnvironmentSummaryResponse(
            new SubjectTotalsResponse(
                summary.Subjects.Total,
                summary.Subjects.Active,
                summary.Subjects.Deprecated,
                summary.Subjects.Retired),
            new VersionTotalsResponse(
                summary.Versions.Total,
                summary.Versions.Active,
                summary.Versions.AwaitingApproval,
                summary.Versions.Rejected,
                summary.Versions.Dismissed),
            [.. summary.NewestSubjects.Select(s => new NewestSubjectResponse(
                s.Name,
                WireTokens.For(s.Format),
                s.Owner,
                WireTokens.For(s.Lifecycle),
                s.Latest,
                s.Versions,
                s.AwaitingApproval,
                s.CreatedAt))],
            [.. summary.BreakingChanges.Select(v => new HeldVersionResponse(
                v.Subject,
                v.Ordinal,
                WireTokens.For(v.Status),
                v.SemanticVersion,
                v.RegisteredAt,
                v.RegisteredBy,
                v.DecidedAt,
                v.DecidedBy))],
            [.. summary.Enforcement.Select(t => new EnforcementTierResponse(
                WireTokens.For(t.Mode), t.Contracts, t.PublishBindings, t.ConsumeBindings))],
            new ViolationTotalsResponse(
                summary.Violations.Distinct,
                summary.Violations.Recent,
                (int)GetEnvironmentSummaryQuery.ViolationWindow.TotalHours));
    }
}
