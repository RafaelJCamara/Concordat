using Concordat.Application.Abstractions;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;

namespace Concordat.Application.Registry;

/// <summary>One version waiting on a decision, with what approving it would replace.</summary>
/// <param name="Subject">The subject it belongs to.</param>
/// <param name="Ordinal">Its ordinal within the subject.</param>
/// <param name="SemanticVersion">The verified intent label, when it carried one.</param>
/// <param name="Changelog">What the proposer said changed, when they said.</param>
/// <param name="RegisteredAt">When it was proposed.</param>
/// <param name="RegisteredBy">Who proposed it.</param>
/// <param name="Baseline">
/// The ordinal consumers resolve as <c>latest</c> today, or null when nothing has been approved.
/// </param>
public sealed record PendingApproval(
    string Subject,
    int Ordinal,
    string? SemanticVersion,
    string? Changelog,
    DateTimeOffset RegisteredAt,
    string RegisteredBy,
    int? Baseline);

/// <summary>A page of the approval queue, and how long the whole queue is.</summary>
/// <param name="Total">Every version awaiting approval in the environment, not only this page.</param>
/// <param name="Items">This page, oldest proposal first.</param>
public sealed record PendingApprovals(int Total, IReadOnlyList<PendingApproval> Items);

/// <summary>Reads the approval queue of one environment (ADR-042).</summary>
/// <param name="EnvironmentId">The environment.</param>
/// <param name="Limit">The most items to return; clamped to <see cref="MaxLimit"/>.</param>
/// <param name="Offset">How many of the oldest to skip.</param>
public sealed record ListPendingApprovalsQuery(
    EnvironmentId EnvironmentId, int? Limit, int? Offset) : IQuery<PendingApprovals>
{
    /// <summary>The largest page a caller may ask for.</summary>
    public const int MaxLimit = 200;

    /// <summary>The page size when the caller names none.</summary>
    public const int DefaultLimit = 50;
}

/// <summary>Handles <see cref="ListPendingApprovalsQuery"/>.</summary>
public sealed class ListPendingApprovalsHandler(IPendingApprovalReader reader)
    : IQueryHandler<ListPendingApprovalsQuery, PendingApprovals>
{
    /// <inheritdoc />
    public async Task<Result<PendingApprovals>> HandleAsync(
        ListPendingApprovalsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Clamped, never refused, like the audit read: a page size is a preference and a bound on
        // cost, and an out-of-range one is answered with the nearest page the registry will give.
        // No existence check, as the subject list and the overview: an environment nothing has
        // been written to has an empty queue.
        var limit = Math.Clamp(
            query.Limit ?? ListPendingApprovalsQuery.DefaultLimit,
            1,
            ListPendingApprovalsQuery.MaxLimit);
        var offset = Math.Max(query.Offset ?? 0, 0);

        var page = await reader.ReadAsync(query.EnvironmentId, limit, offset, cancellationToken)
            .ConfigureAwait(false);

        return Result<PendingApprovals>.Success(page);
    }
}
