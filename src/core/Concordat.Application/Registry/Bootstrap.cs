using Concordat.Application.Abstractions;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;

namespace Concordat.Application.Registry;

/// <summary>Everything a client needs to warm its cache, in one request.</summary>
/// <param name="EnvironmentId">The environment.</param>
public sealed record BootstrapQuery(EnvironmentId EnvironmentId) : IQuery<BootstrapResult>;

/// <summary>One subject's current state.</summary>
/// <param name="Name">The subject name.</param>
/// <param name="Format">The schema language.</param>
/// <param name="LatestOrdinal">The gated latest pointer, or <see langword="null"/>.</param>
/// <param name="LatestSchemaId">The schema the pointer resolves to.</param>
/// <param name="LatestSemver">Its optional label.</param>
public sealed record BootstrapSubject(
    string Name,
    SchemaFormat Format,
    int? LatestOrdinal,
    string? LatestSchemaId,
    string? LatestSemver);

/// <summary>The bootstrap payload.</summary>
/// <param name="Subjects">Every subject in the environment, ordered by name.</param>
/// <param name="Schemas">
/// Every schema those subjects need, keyed by id and deduplicated — including schemas reached
/// only through references.
/// </param>
public sealed record BootstrapResult(
    IReadOnlyList<BootstrapSubject> Subjects,
    IReadOnlyDictionary<string, Schema> Schemas);

/// <summary>Handles <see cref="BootstrapQuery"/>.</summary>
/// <remarks>
/// <para>
/// <b>Cold start is the real load pattern, not steady state</b> (DESIGN §5). A fleet-wide
/// rolling restart empties every cache at the same moment and stampedes the registry — which
/// sits on the deserialize path, so a registry that buckles takes consumption down with it.
/// Confluent Cloud caps Schema Registry at 75 reads/sec on every tier, which is a low ceiling
/// for exactly that burst.
/// </para>
/// <para>
/// One request instead of N is the mitigation. The payload is deliberately self-sufficient:
/// referenced schemas are included transitively, so a client never has to follow a reference
/// with a second call it did not plan for.
/// </para>
/// </remarks>
public sealed class BootstrapHandler(
    ISubjectRepository subjects,
    ISchemaRepository schemas,
    IEnvironmentResolver environments)
    : IQueryHandler<BootstrapQuery, BootstrapResult>
{
    /// <inheritdoc />
    public async Task<Result<BootstrapResult>> HandleAsync(
        BootstrapQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var found = await subjects.ListAsync(query.EnvironmentId, cancellationToken)
            .ConfigureAwait(false);

        var payload = new List<BootstrapSubject>(found.Count);
        var collected = new Dictionary<string, Schema>(StringComparer.Ordinal);
        var pending = new Queue<Schema>();

        foreach (var subject in found)
        {
            // Retired subjects are excluded: a client warming a cache should not be primed
            // with contracts that are soft-deleted.
            if (subject.Lifecycle is SubjectLifecycle.Retired)
            {
                continue;
            }

            var latest = subject.LatestVersion();

            payload.Add(new BootstrapSubject(
                subject.Name.Value,
                subject.Format,
                latest?.Ordinal,
                latest?.SchemaId.Value,
                latest?.SemanticVersion?.ToString()));

            if (latest is null)
            {
                continue;
            }

            var schema = await schemas.FindAsync(latest.SchemaId, cancellationToken)
                .ConfigureAwait(false);

            if (schema is not null && collected.TryAdd(schema.Id.Value, schema))
            {
                pending.Enqueue(schema);
            }
        }

        // Follow references transitively, so the payload validates standalone. The visited set
        // is what stops a cyclic edge set - rejected at registration, but not to be trusted
        // blindly on a read path - from looping forever.
        while (pending.Count > 0)
        {
            foreach (var reference in pending.Dequeue().References)
            {
                var target = await ResolveAsync(reference, cancellationToken).ConfigureAwait(false);

                if (target is not null && collected.TryAdd(target.Id.Value, target))
                {
                    pending.Enqueue(target);
                }
            }
        }

        return Result<BootstrapResult>.Success(new BootstrapResult(payload, collected));
    }

    private async Task<Schema?> ResolveAsync(Reference reference, CancellationToken cancellationToken)
    {
        var parsed = ConcordatRef.Create(reference.Name);
        if (parsed.IsFailure)
        {
            return null;
        }

        var subject = await subjects.FindAsync(
            environments.Resolve(parsed.Value.Environment), parsed.Value.Subject, cancellationToken)
            .ConfigureAwait(false);

        var version = subject?.Versions.FirstOrDefault(v => v.Ordinal == parsed.Value.Version);

        return version is null
            ? null
            : await schemas.FindAsync(version.SchemaId, cancellationToken).ConfigureAwait(false);
    }
}
