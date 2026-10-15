using Concordat.Application.Abstractions;
using Concordat.Application.Registry;
using Concordat.Domain.Identity;

namespace Concordat.Api;

/// <summary>The approval queue route (ADR-042).</summary>
public static class ApprovalQueueEndpoints
{
    /// <summary>Maps the approval queue route.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapApprovalQueueEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/v1/environments/{env}/approvals", GetQueue)
            .WithTags("Approvals")
            .WithSummary("The versions waiting on an approval decision")
            .WithDescription(
                "Every version held at the approval gate in one environment, oldest proposal " +
                "first, so the longest wait leads. `total` is the length of the whole queue and " +
                "`items` is one page of it: `limit` (default 50, at most 200; out-of-range values " +
                "are clamped) and `offset` (default 0) choose the page. Each item names the " +
                "ordinal consumers resolve as latest today (`baseline`, null when nothing has " +
                "been approved), which is what approving the item would replace. Reading the " +
                "queue needs only `subject:read`; deciding is `subject:admin`. An environment " +
                "nothing has been written to answers an empty queue rather than 404.")
            .Produces<PendingApprovalsResponse>()
            .RequireScope(Scope.SubjectRead);

        return app;
    }

    private static async Task<IResult> GetQueue(
        string env,
        int? limit,
        int? offset,
        IDispatcher dispatcher,
        IEnvironmentResolver environments,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.QueryAsync(
            new ListPendingApprovalsQuery(environments.Resolve(env), limit, offset),
            cancellationToken).ConfigureAwait(false);

        return result.IsFailure
            ? ProblemDetailsMapping.From(result.Error!)
            : TypedResults.Ok(PendingApprovalsResponse.From(result.Value));
    }
}

/// <summary>One version waiting on a decision.</summary>
/// <param name="Subject">The subject it belongs to.</param>
/// <param name="Ordinal">Its ordinal within the subject.</param>
/// <param name="SemanticVersion">The verified label, when it carried one.</param>
/// <param name="Changelog">What the proposer said changed, when they said.</param>
/// <param name="RegisteredAt">When it was proposed, UTC.</param>
/// <param name="RegisteredBy">Who proposed it.</param>
/// <param name="Baseline">
/// The ordinal consumers resolve as latest today, or <see langword="null"/> when none is approved.
/// </param>
public sealed record PendingApprovalResponse(
    string Subject,
    int Ordinal,
    string? SemanticVersion,
    string? Changelog,
    DateTimeOffset RegisteredAt,
    string RegisteredBy,
    int? Baseline);

/// <summary>A page of the approval queue.</summary>
/// <param name="Total">Every version awaiting approval, not only this page.</param>
/// <param name="Items">This page, oldest proposal first.</param>
public sealed record PendingApprovalsResponse(
    int Total, IReadOnlyList<PendingApprovalResponse> Items)
{
    /// <summary>Projects a page.</summary>
    /// <param name="page">The page.</param>
    /// <returns>The wire form.</returns>
    public static PendingApprovalsResponse From(PendingApprovals page)
    {
        ArgumentNullException.ThrowIfNull(page);

        return new PendingApprovalsResponse(
            page.Total,
            [.. page.Items.Select(i => new PendingApprovalResponse(
                i.Subject,
                i.Ordinal,
                i.SemanticVersion,
                i.Changelog,
                i.RegisteredAt,
                i.RegisteredBy,
                i.Baseline))]);
    }
}
