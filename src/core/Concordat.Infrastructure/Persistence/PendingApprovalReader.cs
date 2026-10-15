using Concordat.Application.Abstractions;
using Concordat.Application.Registry;
using Concordat.Domain.Registry;
using Microsoft.EntityFrameworkCore;

namespace Concordat.Infrastructure.Persistence;

/// <summary>
/// Answers an environment's approval queue with two statements and no loop (ADR-042).
/// </summary>
/// <remarks>
/// Both statements are projections, so neither materialises a <c>Subject</c> (which would
/// auto-include every version of it; see <see cref="EnvironmentSummaryReader"/>). Tenant scoping is
/// the context's query filter, on both.
/// </remarks>
internal sealed class PendingApprovalReader(ConcordatDbContext context) : IPendingApprovalReader
{
    /// <inheritdoc />
    public async Task<PendingApprovals> ReadAsync(
        EnvironmentId environmentId, int limit, int offset, CancellationToken cancellationToken)
    {
        // Retired subjects are included, as they were when the browser filtered the subject list:
        // a proposal nobody has decided is waiting whatever became of its subject, and hiding it
        // would make the dashboard's awaiting count (which includes it) disagree with this queue.
        var awaiting = context.Subjects
            .AsNoTracking()
            .Where(s => s.EnvironmentId == environmentId)
            .SelectMany(
                s => s.Versions,
                (s, v) => new { s.Name, s.Latest, s.Reviewers, Version = v })
            .Where(x => x.Version.Status == VersionStatus.AwaitingApproval);

        var total = await awaiting.CountAsync(cancellationToken).ConfigureAwait(false);

        // Oldest first, so the longest wait leads. The name and ordinal break ties between
        // proposals registered in one instant, which makes two requests agree about where a page
        // ends and so makes offset paging deterministic.
        var rows = await awaiting
            .OrderBy(x => x.Version.RegisteredAt)
            .ThenBy(x => x.Name)
            .ThenBy(x => x.Version.Ordinal)
            .Skip(offset)
            .Take(limit)
            .Select(x => new
            {
                x.Name,
                x.Version.Ordinal,
                x.Version.SemanticVersion,
                x.Version.Changelog,
                x.Version.RegisteredAt,
                x.Version.RegisteredBy,
                Baseline = x.Latest == null ? (int?)null : x.Latest.Ordinal,
                x.Reviewers,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new PendingApprovals(
            total,
            [
                .. rows.Select(r => new PendingApproval(
                    r.Name.Value,
                    r.Ordinal,
                    r.SemanticVersion?.ToString(),
                    r.Changelog,
                    r.RegisteredAt,
                    r.RegisteredBy.Value,
                    r.Baseline,
                    [.. r.Reviewers.Select(a => a.Value)])),
            ]);
    }
}
