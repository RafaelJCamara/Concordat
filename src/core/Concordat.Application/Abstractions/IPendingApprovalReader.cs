using Concordat.Application.Registry;
using Concordat.Domain.Registry;

namespace Concordat.Application.Abstractions;

/// <summary>Reads an environment's approval queue where the versions are (ADR-042).</summary>
/// <remarks>
/// A reader, not a repository, for the reason <see cref="IEnvironmentSummaryReader"/> is one: the
/// queue is a question about every subject's versions, and answering it by loading the subjects
/// is what the web app did, and what stops being right the day a list is paged. Sits behind the
/// same tenant filter as every repository, so no method takes a tenant.
/// </remarks>
public interface IPendingApprovalReader
{
    /// <summary>Reads one page of the queue, oldest proposal first.</summary>
    /// <param name="environmentId">The environment.</param>
    /// <param name="limit">The most items to return.</param>
    /// <param name="offset">How many of the oldest to skip.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The page and the length of the whole queue.</returns>
    Task<PendingApprovals> ReadAsync(
        EnvironmentId environmentId, int limit, int offset, CancellationToken cancellationToken);
}
