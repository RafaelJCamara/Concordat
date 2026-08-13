using Concordat.Application.Abstractions;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;
using Concordat.Formats.Abstractions;

namespace Concordat.Application.Registry;

/// <summary>Resolves the per-format bundlers.</summary>
public interface ISchemaBundlerRegistry
{
    /// <summary>Gets the bundler for a format.</summary>
    /// <param name="format">The format.</param>
    /// <returns>The bundler.</returns>
    /// <exception cref="NotSupportedException">The format has no bundler yet.</exception>
    ISchemaBundler Bundler(SchemaFormat format);
}

/// <summary>Fetches a schema as one self-contained document.</summary>
/// <param name="SchemaId">The content-addressed id.</param>
public sealed record GetBundledSchemaQuery(string SchemaId) : IQuery<BundledSchema>;

/// <summary>A self-contained schema.</summary>
/// <param name="SchemaId">The id of the root schema.</param>
/// <param name="Format">The schema language.</param>
/// <param name="Bundled">The document, with every Concordat reference inlined into <c>$defs</c>.</param>
/// <param name="Inlined">The references that were inlined, in resolution order.</param>
public sealed record BundledSchema(
    string SchemaId, SchemaFormat Format, string Bundled, IReadOnlyList<string> Inlined);

/// <summary>Handles <see cref="GetBundledSchemaQuery"/>.</summary>
/// <remarks>
/// Resolution is transitive and breadth-first, with a visited set so a reference graph that
/// somehow contains a cycle terminates instead of hanging. Cycles are rejected at
/// registration, but a read path that trusts that assumption is one bug away from an infinite
/// loop.
/// </remarks>
public sealed class GetBundledSchemaHandler(
    ISchemaRepository schemas,
    ISubjectRepository subjects,
    IEnvironmentResolver environments,
    ISchemaBundlerRegistry bundlers)
    : IQueryHandler<GetBundledSchemaQuery, BundledSchema>
{
    /// <inheritdoc />
    public async Task<Result<BundledSchema>> HandleAsync(
        GetBundledSchemaQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var id = SchemaId.Create(query.SchemaId);
        if (id.IsFailure)
        {
            return Result<BundledSchema>.Failure(id.Error!);
        }

        // Same reachability gate as the plain read: bundling must not become a way around it.
        var reachable = await schemas
            .IsReachableByCurrentTenantAsync(id.Value, cancellationToken).ConfigureAwait(false);

        if (!reachable)
        {
            return NotFound(id.Value);
        }

        var root = await schemas.FindAsync(id.Value, cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return NotFound(id.Value);
        }

        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        var queue = new Queue<Schema>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            foreach (var reference in queue.Dequeue().References)
            {
                if (resolved.ContainsKey(reference.Name))
                {
                    continue;
                }

                var target = await ResolveAsync(reference, cancellationToken).ConfigureAwait(false);
                if (target.IsFailure)
                {
                    return Result<BundledSchema>.Failure(target.Error!);
                }

                resolved[reference.Name] = target.Value.Body;
                queue.Enqueue(target.Value);
            }
        }

        var bundled = bundlers.Bundler(root.Format).Bundle(root.Body, resolved);

        return bundled.IsFailure
            ? Result<BundledSchema>.Failure(bundled.Error!)
            : Result<BundledSchema>.Success(new BundledSchema(
                root.Id.Value,
                root.Format,
                bundled.Value,
                [.. resolved.Keys.Order(StringComparer.Ordinal)]));
    }

    private async Task<Result<Schema>> ResolveAsync(
        Reference reference, CancellationToken cancellationToken)
    {
        var parsed = ConcordatRef.Create(reference.Name);
        if (parsed.IsFailure)
        {
            return Result<Schema>.Failure(parsed.Error!);
        }

        var subject = await subjects.FindAsync(
                environments.Resolve(parsed.Value.Environment),
                parsed.Value.Subject,
                cancellationToken)
            .ConfigureAwait(false);

        if (subject is null)
        {
            return Result<Schema>.Failure(
                ConcordatCodes.SubjectNotFound,
                $"'{reference.Name}' points at a subject this tenant cannot see.");
        }

        var version = subject.Versions.FirstOrDefault(v => v.Ordinal == parsed.Value.Version);
        if (version is null)
        {
            return Result<Schema>.Failure(
                ConcordatCodes.VersionNotFound,
                $"'{reference.Name}' points at a version that does not exist.");
        }

        var schema = await schemas.FindAsync(version.SchemaId, cancellationToken)
            .ConfigureAwait(false);

        return schema is null
            ? Result<Schema>.Failure(
                ConcordatCodes.SchemaNotFound,
                $"'{reference.Name}' resolves to a schema that is not stored.")
            : Result<Schema>.Success(schema);
    }

    private static Result<BundledSchema> NotFound(SchemaId id) =>
        Result<BundledSchema>.Failure(ConcordatCodes.SchemaNotFound, $"No schema '{id.Value}'.");
}
