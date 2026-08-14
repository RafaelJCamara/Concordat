using Concordat.Application.Abstractions;
using Concordat.Application.Registry;
using Concordat.Domain.Governance;
using Concordat.Domain.Results;

namespace Concordat.Application.Governance;

/// <summary>One violation an SDK counted over its reporting window.</summary>
/// <param name="Side"><c>PUBLISH</c> or <c>CONSUME</c>.</param>
/// <param name="Route">The exchange and routing key, or the queue.</param>
/// <param name="Subject">The subject, when the SDK resolved one.</param>
/// <param name="Code">The <c>concordatCode</c> that classified it.</param>
/// <param name="Detail">What went wrong, most recently.</param>
/// <param name="Occurrences">How many messages hit it in the window.</param>
public sealed record ViolationReport(
    string? Side,
    string? Route,
    string? Subject,
    string? Code,
    string? Detail,
    long Occurrences);

/// <summary>Records violations an SDK saw (decision 25).</summary>
/// <param name="EnvironmentName">Which environment the reporting service belongs to.</param>
/// <param name="ReportedBy">The reporting service, for a human reading the row later.</param>
/// <param name="Reports">The batch.</param>
public sealed record ReportViolationsCommand(
    string? EnvironmentName,
    string? ReportedBy,
    IReadOnlyList<ViolationReport>? Reports) : ICommand<ViolationsAccepted>;

/// <summary>What the registry did with a batch.</summary>
/// <param name="Accepted">How many reports were recorded.</param>
/// <param name="Opened">How many were violations nobody had reported before.</param>
/// <param name="Rejected">How many were unusable and dropped.</param>
public sealed record ViolationsAccepted(int Accepted, int Opened, int Rejected);

/// <summary>
/// Handles <see cref="ReportViolationsCommand"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>A bad report is dropped, not refused.</b> This runs on a fire-and-forget path from every
/// publisher in the estate; failing the whole batch because one entry was malformed would lose
/// the good ones with it, and nobody is reading the status code. The count comes back so a
/// malformed reporter is visible to anyone who looks.
/// </para>
/// <para>
/// <b>The notification fires on first sight only.</b> Staging one per report would page somebody
/// every reporting window for as long as the fault lasted. "This started happening" is the
/// alert; "this is still happening" is the row's counter.
/// </para>
/// </remarks>
public sealed class ReportViolationsHandler(
    IEnvironmentRepository environments,
    IViolationRepository violations,
    IOutbox outbox,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
    : ICommandHandler<ReportViolationsCommand, ViolationsAccepted>
{
    /// <summary>The most reports one batch may carry.</summary>
    /// <remarks>
    /// Bounded because this is an unauthenticated-shaped write path in every respect except the
    /// credential: a client decides how much to send. The SDK aggregates to a handful of
    /// fingerprints per window, so a batch anywhere near this is a client bug.
    /// </remarks>
    public const int MaxBatch = 200;

    /// <inheritdoc />
    public async Task<Result<ViolationsAccepted>> HandleAsync(
        ReportViolationsCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var environment = await EnvironmentPolicies
            .RequireAsync(environments, command.EnvironmentName, cancellationToken)
            .ConfigureAwait(false);

        if (environment.IsFailure)
        {
            return Result<ViolationsAccepted>.Failure(environment.Error!);
        }

        var reports = command.Reports ?? [];

        if (reports.Count > MaxBatch)
        {
            return Result<ViolationsAccepted>.Failure(
                ConcordatCodes.ViolationReportInvalid,
                $"A batch may carry at most {MaxBatch} reports; got {reports.Count}.");
        }

        var now = clock.GetUtcNow();
        var environmentId = environment.Value.Id;
        var accepted = 0;
        var opened = 0;
        var rejected = 0;

        foreach (var report in reports)
        {
            if (ViolationTokens.Parse(report.Side, out var side).IsFailure)
            {
                rejected++;
                continue;
            }

            var fingerprint = EnforcementViolation.Compose(
                environmentId, side, report.Route?.Trim() ?? string.Empty, report.Subject, report.Code?.Trim() ?? string.Empty);

            var existing = await violations
                .FindAsync(fingerprint, cancellationToken).ConfigureAwait(false);

            if (existing is not null)
            {
                existing.Observe(report.Occurrences, report.Detail, command.ReportedBy, now);
                accepted++;
                continue;
            }

            var open = EnforcementViolation.Open(
                environmentId,
                side,
                report.Route,
                report.Subject,
                report.Code,
                report.Detail,
                command.ReportedBy,
                report.Occurrences,
                now);

            if (open.IsFailure)
            {
                rejected++;
                continue;
            }

            violations.Add(open.Value);
            accepted++;
            opened++;

            outbox.Stage(OutboxMessage.Stage(
                environmentId,
                NotificationEvent.EnforcementViolation,
                open.Value.Route,
                $"{ViolationTokens.For(side)} on '{open.Value.Route}' violated " +
                $"'{open.Value.Code}': {open.Value.Detail}",
                now));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result<ViolationsAccepted>.Success(
            new ViolationsAccepted(accepted, opened, rejected));
    }
}

/// <summary>Lists the violations reported in an environment (decision 25).</summary>
/// <param name="EnvironmentName">Which environment.</param>
/// <param name="Limit">The most rows to return.</param>
public sealed record ListViolationsQuery(string? EnvironmentName, int Limit)
    : IQuery<IReadOnlyList<EnforcementViolation>>;

/// <summary>Handles <see cref="ListViolationsQuery"/>.</summary>
public sealed class ListViolationsHandler(
    IEnvironmentRepository environments, IViolationRepository violations)
    : IQueryHandler<ListViolationsQuery, IReadOnlyList<EnforcementViolation>>
{
    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<EnforcementViolation>>> HandleAsync(
        ListViolationsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var environment = await EnvironmentPolicies
            .RequireAsync(environments, query.EnvironmentName, cancellationToken)
            .ConfigureAwait(false);

        if (environment.IsFailure)
        {
            return Result<IReadOnlyList<EnforcementViolation>>.Failure(environment.Error!);
        }

        var found = await violations
            .ListAsync(environment.Value.Id, Math.Clamp(query.Limit, 1, 500), cancellationToken)
            .ConfigureAwait(false);

        return Result<IReadOnlyList<EnforcementViolation>>.Success(found);
    }
}
