using Concordat.Application.Abstractions;
using Concordat.Application.Registry;
using Concordat.Domain.Governance;
using Concordat.Domain.Results;

namespace Concordat.Application.Governance;

/// <summary>
/// Reads the audit trail, newest first (M7.4).
/// </summary>
/// <param name="EnvironmentName">One environment, or null for every environment in the tenant.</param>
/// <param name="Action">One action's wire token, or null for all of them.</param>
/// <param name="Target">A subject, contract or broker name, or null for all of them.</param>
/// <param name="Since">The earliest entry to return.</param>
/// <param name="Until">The latest entry to return.</param>
/// <param name="Limit">The most entries to return; clamped to <see cref="MaxLimit"/>.</param>
public sealed record QueryAuditQuery(
    string? EnvironmentName,
    string? Action,
    string? Target,
    DateTimeOffset? Since,
    DateTimeOffset? Until,
    int? Limit) : IQuery<IReadOnlyList<AuditEntry>>
{
    /// <summary>The most entries one request may return.</summary>
    /// <remarks>
    /// An audit trail is the one table that only grows, so an unbounded read is a request that
    /// gets slower every day it is not changed. Paging is M8's, alongside the identity that
    /// makes a cursor meaningful.
    /// </remarks>
    public const int MaxLimit = 500;

    /// <summary>What a caller gets without asking.</summary>
    public const int DefaultLimit = 100;
}

/// <summary>Handles <see cref="QueryAuditQuery"/>.</summary>
public sealed class QueryAuditHandler(IEnvironmentRepository environments, IAuditLog audit)
    : IQueryHandler<QueryAuditQuery, IReadOnlyList<AuditEntry>>
{
    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<AuditEntry>>> HandleAsync(
        QueryAuditQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        Domain.Registry.EnvironmentId? environmentId = null;

        if (!string.IsNullOrWhiteSpace(query.EnvironmentName))
        {
            var environment = await EnvironmentPolicies.RequireAsync(
                environments, query.EnvironmentName, cancellationToken).ConfigureAwait(false);

            if (environment.IsFailure)
            {
                return Result<IReadOnlyList<AuditEntry>>.Failure(environment.Error!);
            }

            environmentId = environment.Value.Id;
        }

        var parsed = AuditTokens.Parse(query.Action, out var action);
        if (parsed.IsFailure)
        {
            return Result<IReadOnlyList<AuditEntry>>.Failure(parsed.Error!);
        }

        if (query.Since is { } since && query.Until is { } until && since > until)
        {
            return Result<IReadOnlyList<AuditEntry>>.Failure(
                ConcordatCodes.AuditFilterInvalid,
                "The window ends before it begins. An empty result from a reversed range reads " +
                "as 'nothing happened', which is the one answer an audit query must never give " +
                "by accident.");
        }

        var limit = Math.Clamp(
            query.Limit ?? QueryAuditQuery.DefaultLimit, 1, QueryAuditQuery.MaxLimit);

        var target = string.IsNullOrWhiteSpace(query.Target) ? null : query.Target.Trim();

        var entries = await audit.QueryAsync(
            new AuditFilter(environmentId, action, target, query.Since, query.Until, limit),
            cancellationToken).ConfigureAwait(false);

        return Result<IReadOnlyList<AuditEntry>>.Success(entries);
    }
}
