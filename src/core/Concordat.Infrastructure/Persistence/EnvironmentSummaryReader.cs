using Concordat.Application.Abstractions;
using Concordat.Application.Registry;
using Concordat.Domain.Registry;
using Microsoft.EntityFrameworkCore;

namespace Concordat.Infrastructure.Persistence;

/// <summary>
/// Answers an environment's overview with six statements and no loop (ADR-037).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every query is a projection or an aggregate, and none materialises an aggregate.</b>
/// <c>Subject</c> and <c>Contract</c> both auto-include their children (the wire contract for
/// the list endpoints needs them), so a query that returned either would load every version and
/// every binding in the environment. That is the cost this reader exists to avoid, and EF drops
/// an auto-include only when the query projects to something else, so a change here that returns
/// an entity instead of a row would silently put the cost back. The query-count test pins how
/// many statements run; it cannot see how much each one reads, which is what the
/// <c>AsNoTracking</c> projections and the cost table in ADR-037 are for.
/// </para>
/// <para>
/// <b>Tenant scoping is the context's query filter, on every statement.</b> No method takes a
/// tenant, so none can be handed the wrong one.
/// </para>
/// <para>
/// <b>The six are independent statements, not one snapshot.</b> A registration landing between
/// the second and the third can leave the version total and the newest-subjects list a version
/// apart for a moment. Each figure is internally consistent, and a dashboard that is refreshed
/// is right a second later; a repeatable-read transaction around six cheap reads would buy a
/// guarantee nobody is relying on.
/// </para>
/// </remarks>
internal sealed class EnvironmentSummaryReader(ConcordatDbContext context)
    : IEnvironmentSummaryReader
{
    /// <inheritdoc />
    public async Task<EnvironmentSummary> ReadAsync(
        EnvironmentId environmentId,
        int newestSubjects,
        int breakingChanges,
        DateTimeOffset violationsSince,
        CancellationToken cancellationToken)
    {
        // Sequential on purpose: a DbContext runs one command at a time, and the alternative to
        // awaiting each here is six contexts, six connections and six tenant resolutions to save
        // a few milliseconds on a screen that is read once per page load.
        var subjects = await ReadSubjectTotalsAsync(environmentId, cancellationToken)
            .ConfigureAwait(false);
        var versions = await ReadVersionTotalsAsync(environmentId, cancellationToken)
            .ConfigureAwait(false);
        var newest = await ReadNewestSubjectsAsync(environmentId, newestSubjects, cancellationToken)
            .ConfigureAwait(false);
        var held = await ReadHeldVersionsAsync(environmentId, breakingChanges, cancellationToken)
            .ConfigureAwait(false);
        var enforcement = await ReadEnforcementAsync(environmentId, cancellationToken)
            .ConfigureAwait(false);
        var violations = await ReadViolationsAsync(environmentId, violationsSince, cancellationToken)
            .ConfigureAwait(false);

        return new EnvironmentSummary(subjects, versions, newest, held, enforcement, violations);
    }

    private async Task<SubjectTotals> ReadSubjectTotalsAsync(
        EnvironmentId environmentId, CancellationToken cancellationToken)
    {
        // One GROUP BY over the environment's subjects: O(subjects), served by the unique
        // (tenant, environment, name) index's prefix.
        var rows = await context.Subjects
            .AsNoTracking()
            .Where(s => s.EnvironmentId == environmentId)
            .GroupBy(s => s.Lifecycle)
            .Select(g => new { Lifecycle = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new SubjectTotals(
            CountOf(rows, SubjectLifecycle.Active, r => r.Lifecycle, r => r.Count),
            CountOf(rows, SubjectLifecycle.Deprecated, r => r.Lifecycle, r => r.Count),
            CountOf(rows, SubjectLifecycle.Retired, r => r.Lifecycle, r => r.Count));
    }

    private async Task<VersionTotals> ReadVersionTotalsAsync(
        EnvironmentId environmentId, CancellationToken cancellationToken)
    {
        // A version row carries no tenant or environment of its own, so it is reached through its
        // subject: the environment's subjects joined to their versions, grouped by status.
        // O(versions in the environment), which is the one cost here that is not bounded by a
        // limit, and the reason ADR-037 states it rather than hides it.
        var rows = await context.Subjects
            .AsNoTracking()
            .Where(s => s.EnvironmentId == environmentId)
            .SelectMany(s => s.Versions)
            .GroupBy(v => v.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new VersionTotals(
            CountOf(rows, VersionStatus.Active, r => r.Status, r => r.Count),
            CountOf(rows, VersionStatus.AwaitingApproval, r => r.Status, r => r.Count),
            CountOf(rows, VersionStatus.Rejected, r => r.Status, r => r.Count),
            CountOf(rows, VersionStatus.Dismissed, r => r.Status, r => r.Count));
    }

    private async Task<IReadOnlyList<NewestSubject>> ReadNewestSubjectsAsync(
        EnvironmentId environmentId, int limit, CancellationToken cancellationToken)
    {
        // Ordered by creation, not by when a version was last registered: creation time is a
        // column of the subject itself, so the top N is a bounded read and a subject with no
        // versions yet takes its true place instead of sorting last. The per-subject counts are
        // correlated sub-selects over the primary key (subject_id, ordinal), and run for at most
        // `limit` subjects.
        var rows = await context.Subjects
            .AsNoTracking()
            .Where(s => s.EnvironmentId == environmentId)
            .OrderByDescending(s => s.CreatedAt)
            .ThenBy(s => s.Name)
            .Take(limit)
            .Select(s => new
            {
                s.Name,
                s.Format,
                s.Owner,
                s.Lifecycle,
                Latest = s.Latest == null ? (int?)null : s.Latest.Ordinal,
                Versions = s.Versions.Count,
                AwaitingApproval = s.Versions.Count(v => v.Status == VersionStatus.AwaitingApproval),
                s.CreatedAt,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return
        [
            .. rows.Select(r => new NewestSubject(
                r.Name.Value,
                r.Format,
                r.Owner.Value,
                r.Lifecycle,
                r.Latest,
                r.Versions,
                r.AwaitingApproval,
                r.CreatedAt)),
        ];
    }

    private async Task<IReadOnlyList<HeldVersion>> ReadHeldVersionsAsync(
        EnvironmentId environmentId, int limit, CancellationToken cancellationToken)
    {
        // Held or rejected, and nothing else. A dismissed proposal was reverted before anyone
        // judged it, an approved one is live, and neither is something the registry turned away
        // or is still waiting on. Ties on the proposal time are broken by name and ordinal so two
        // identical requests cannot disagree about which five of a batch registered together.
        var rows = await context.Subjects
            .AsNoTracking()
            .Where(s => s.EnvironmentId == environmentId)
            .SelectMany(
                s => s.Versions,
                (s, v) => new { s.Name, Version = v })
            .Where(x => x.Version.Status == VersionStatus.AwaitingApproval ||
                        x.Version.Status == VersionStatus.Rejected)
            .OrderByDescending(x => x.Version.RegisteredAt)
            .ThenBy(x => x.Name)
            .ThenByDescending(x => x.Version.Ordinal)
            .Take(limit)
            .Select(x => new
            {
                x.Name,
                x.Version.Ordinal,
                x.Version.Status,
                x.Version.SemanticVersion,
                x.Version.RegisteredAt,
                x.Version.RegisteredBy,
                x.Version.DecidedAt,
                x.Version.DecidedBy,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return
        [
            .. rows.Select(r => new HeldVersion(
                r.Name.Value,
                r.Ordinal,
                r.Status,
                r.SemanticVersion?.ToString(),
                r.RegisteredAt,
                r.RegisteredBy.Value,
                r.DecidedAt,
                r.DecidedBy?.Value)),
        ];
    }

    private async Task<IReadOnlyList<EnforcementTier>> ReadEnforcementAsync(
        EnvironmentId environmentId, CancellationToken cancellationToken)
    {
        // Contracts grouped by their mode, with each group's binding rows counted by correlated
        // sub-selects over the foreign key (contract_id, indexed since ADR-035). O(contracts +
        // bindings in the environment); there are as many contracts as there are services with a
        // routing topology to govern, so this stays small long after the subject list does not.
        var rows = await context.Contracts
            .AsNoTracking()
            .Where(c => c.EnvironmentId == environmentId)
            .GroupBy(c => c.Enforcement)
            .Select(g => new
            {
                Mode = g.Key,
                Contracts = g.Count(),
                Publishes = g.Sum(c => c.Publishes.Count),
                Consumes = g.Sum(c => c.Consumes.Count),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Always all three, in this order, so the wire shape does not depend on the data.
        return
        [
            .. new[] { EnforcementMode.Off, EnforcementMode.Monitor, EnforcementMode.Enforce }
                .Select(mode =>
                {
                    var row = rows.SingleOrDefault(r => r.Mode == mode);

                    return new EnforcementTier(
                        mode,
                        row?.Contracts ?? 0,
                        row?.Publishes ?? 0,
                        row?.Consumes ?? 0);
                }),
        ];
    }

    private async Task<ViolationTotals> ReadViolationsAsync(
        EnvironmentId environmentId, DateTimeOffset since, CancellationToken cancellationToken)
    {
        // One row of two counts, over the (tenant, environment, last_seen_at) index. A row is one
        // distinct violation however many messages hit it, so this counts problems and not volume.
        var row = await context.Violations
            .AsNoTracking()
            .Where(v => v.EnvironmentId == environmentId)
            .GroupBy(_ => 1)
            .Select(g => new { Distinct = g.Count(), Recent = g.Count(v => v.LastSeenAt >= since) })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return new ViolationTotals(row?.Distinct ?? 0, row?.Recent ?? 0);
    }

    private static int CountOf<TRow, TKey>(
        IEnumerable<TRow> rows, TKey key, Func<TRow, TKey> keyOf, Func<TRow, int> countOf)
        where TKey : notnull =>
        rows.Where(r => keyOf(r).Equals(key)).Sum(countOf);
}
