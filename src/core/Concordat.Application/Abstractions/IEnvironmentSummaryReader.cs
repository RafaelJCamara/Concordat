using Concordat.Application.Registry;
using Concordat.Domain.Registry;

namespace Concordat.Application.Abstractions;

/// <summary>Reads an environment's overview with aggregate queries (ADR-037).</summary>
/// <remarks>
/// <para>
/// <b>A reader, not a repository.</b> A repository hands out aggregates to be changed; this
/// answers a question about all of them, and the whole point is that it never materialises one.
/// Loading every subject to count its versions is exactly what the dashboard did in the browser,
/// and moving that loop to the server would have kept its cost and only changed whose machine
/// paid for it.
/// </para>
/// <para>
/// Sits behind the same tenant filter as every repository, so no method takes a tenant.
/// </para>
/// </remarks>
public interface IEnvironmentSummaryReader
{
    /// <summary>Reads the overview of one environment.</summary>
    /// <param name="environmentId">The environment.</param>
    /// <param name="newestSubjects">How many of the most recently created subjects to return.</param>
    /// <param name="breakingChanges">How many held or rejected versions to return.</param>
    /// <param name="violationsSince">A violation last seen at or after this counts as recent.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The overview. An environment nothing was written to is simply empty.</returns>
    Task<EnvironmentSummary> ReadAsync(
        EnvironmentId environmentId,
        int newestSubjects,
        int breakingChanges,
        DateTimeOffset violationsSince,
        CancellationToken cancellationToken);
}
