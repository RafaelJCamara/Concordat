using Concordat.Domain.Registry;

namespace Concordat.Application.Abstractions;

/// <summary>Reads and writes subjects, always within the caller's tenant.</summary>
/// <remarks>
/// Implementations sit behind the global query filter, so no method here takes a tenant.
/// Passing one would invite a caller to supply the wrong one.
/// </remarks>
public interface ISubjectRepository
{
    /// <summary>Finds a subject by name within an environment.</summary>
    /// <param name="environmentId">The environment.</param>
    /// <param name="name">The subject name.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The subject, or <see langword="null"/>.</returns>
    Task<Subject?> FindAsync(
        EnvironmentId environmentId, SubjectName name, CancellationToken cancellationToken);

    /// <summary>Lists the subjects in an environment, ordered by name.</summary>
    /// <param name="environmentId">The environment.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The subjects.</returns>
    Task<IReadOnlyList<Subject>> ListAsync(
        EnvironmentId environmentId, CancellationToken cancellationToken);

    /// <summary>Stages a new subject for insert.</summary>
    /// <param name="subject">The subject.</param>
    void Add(Subject subject);
}

/// <summary>Reads and writes the global schema store.</summary>
/// <remarks>
/// <b>Not tenant-scoped</b> (ADR-015): the same content is one row for everyone. That is why
/// <see cref="IsReachableByCurrentTenantAsync"/> exists — with no tenant column to filter on,
/// authorisation has to be answered by reachability instead.
/// </remarks>
public interface ISchemaRepository
{
    /// <summary>Finds a schema by its content-addressed id.</summary>
    /// <param name="id">The schema id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The schema, or <see langword="null"/>.</returns>
    Task<Schema?> FindAsync(SchemaId id, CancellationToken cancellationToken);

    /// <summary>
    /// Whether any subject in the caller's tenant references this schema.
    /// </summary>
    /// <param name="id">The schema id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns><see langword="true"/> when the caller is entitled to see it.</returns>
    /// <remarks>
    /// The authorisation the global schema table cannot provide structurally. Without this
    /// check, anyone who can guess a 128-bit hash reads any tenant's schema.
    /// </remarks>
    Task<bool> IsReachableByCurrentTenantAsync(SchemaId id, CancellationToken cancellationToken);

    /// <summary>Stages a schema for insert if it is not already stored.</summary>
    /// <param name="schema">The schema.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The stored schema, whether newly added or already present.</returns>
    Task<Schema> AddIfMissingAsync(Schema schema, CancellationToken cancellationToken);

    /// <summary>Finds the subjects and versions that point at a schema, within the tenant.</summary>
    /// <param name="id">The schema id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Subject name and version ordinal pairs.</returns>
    Task<IReadOnlyList<(SubjectName Subject, int Version)>> FindUsagesAsync(
        SchemaId id, CancellationToken cancellationToken);
}

/// <summary>Commits staged changes.</summary>
public interface IUnitOfWork
{
    /// <summary>Persists everything staged on this unit of work.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The number of state entries written.</returns>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
