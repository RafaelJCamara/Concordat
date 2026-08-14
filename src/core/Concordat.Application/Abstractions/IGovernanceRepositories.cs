using Concordat.Domain.Governance;
using Concordat.Domain.Registry;

namespace Concordat.Application.Abstractions;

/// <summary>Reads and writes service registrations (M7.4).</summary>
public interface IServiceRegistrationRepository
{
    /// <summary>Finds a service by name within an environment.</summary>
    /// <param name="environmentId">The environment.</param>
    /// <param name="name">The service name.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The registration, or <see langword="null"/>.</returns>
    Task<ServiceRegistration?> FindAsync(
        EnvironmentId environmentId, string name, CancellationToken cancellationToken);

    /// <summary>Lists the services registered in an environment, ordered by name.</summary>
    /// <param name="environmentId">The environment.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The registrations.</returns>
    Task<IReadOnlyList<ServiceRegistration>> ListAsync(
        EnvironmentId environmentId, CancellationToken cancellationToken);

    /// <summary>Stages a new registration for insert.</summary>
    /// <param name="registration">The registration.</param>
    void Add(ServiceRegistration registration);
}

/// <summary>How an audit query is narrowed.</summary>
/// <param name="EnvironmentId">One environment, or null for all of them.</param>
/// <param name="Action">One action, or null for all of them.</param>
/// <param name="Target">A subject, contract or broker name, or null for all of them.</param>
/// <param name="Since">The earliest entry to return, or null.</param>
/// <param name="Until">The latest entry to return, or null.</param>
/// <param name="Limit">The most entries to return.</param>
public sealed record AuditFilter(
    EnvironmentId? EnvironmentId,
    AuditAction? Action,
    string? Target,
    DateTimeOffset? Since,
    DateTimeOffset? Until,
    int Limit);

/// <summary>
/// Appends to and reads the audit trail (M7.4).
/// </summary>
/// <remarks>
/// <b>There is no update and no delete, and the absence is the point.</b> An audit trail that
/// can be edited answers a different question from the one it is opened to answer. Retention
/// will eventually need a way to drop old rows; that is a scheduled operation on the store, not
/// a method a request handler can reach.
/// </remarks>
public interface IAuditLog
{
    /// <summary>
    /// Stages an entry, to be written by the same <see cref="IUnitOfWork.SaveChangesAsync"/>
    /// call as the change it records.
    /// </summary>
    /// <param name="entry">The entry.</param>
    void Append(AuditEntry entry);

    /// <summary>Reads the trail, newest first.</summary>
    /// <param name="filter">How to narrow it.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The entries.</returns>
    Task<IReadOnlyList<AuditEntry>> QueryAsync(
        AuditFilter filter, CancellationToken cancellationToken);
}
