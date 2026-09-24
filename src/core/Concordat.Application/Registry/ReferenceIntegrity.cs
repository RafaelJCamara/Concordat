using Concordat.Application.Abstractions;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;

namespace Concordat.Application.Registry;

/// <summary>A <c>concordat://</c> reference resolved to the version it names.</summary>
/// <param name="Subject">The referenced subject.</param>
/// <param name="Version">The referenced version ordinal.</param>
/// <param name="Schema">The stored schema that version points at.</param>
internal sealed record ResolvedReference(SubjectName Subject, int Version, Schema Schema);

/// <summary>
/// Resolves reference edges and refuses a proposal whose graph is unusable (M1.4, DESIGN §4).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> <see cref="ReferenceGraph"/> has been able to answer both of
/// DESIGN §4's questions since M1.4, and nothing asked it: registration extracted a schema's
/// edges, stored them, and checked neither that they resolve nor that they are acyclic. The
/// visible consequence was a schema accepted with a reference to a subject or version that does
/// not exist, whose only symptom arrived later, on the read path, when
/// <c>GET /v1/schemas/{id}/bundled</c> could not produce a self-contained document. The
/// invisible one was <see cref="ConcordatCodes.ReferenceCycle"/>: a code the API mapped to 409
/// that no path could emit.
/// </para>
/// <para>
/// <b>Resolution is the load-bearing half.</b> Once every reference must name a version that
/// already exists, a cycle stops being something the registry can accumulate — see
/// <see cref="VerifyAsync"/>.
/// </para>
/// </remarks>
internal static class ReferenceIntegrity
{
    /// <summary>Resolves one reference to the schema it names.</summary>
    /// <param name="reference">The reference, as derived from the referring document.</param>
    /// <param name="subjects">The subject repository.</param>
    /// <param name="schemas">The schema repository.</param>
    /// <param name="environments">The environment resolver.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The resolved reference, or the failure that stopped it resolving.</returns>
    /// <remarks>
    /// Tenant scoping is inherited rather than re-implemented: the subject lookup runs through
    /// the same global query filter as every other read, so a reference into another tenant
    /// resolves to nothing and is reported as a subject this tenant cannot see.
    /// </remarks>
    public static async Task<Result<ResolvedReference>> ResolveAsync(
        Reference reference,
        ISubjectRepository subjects,
        ISchemaRepository schemas,
        IEnvironmentResolver environments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);

        var parsed = ConcordatRef.Create(reference.Name);
        if (parsed.IsFailure)
        {
            return Result<ResolvedReference>.Failure(parsed.Error!);
        }

        var subject = await subjects.FindAsync(
                environments.Resolve(parsed.Value.Environment),
                parsed.Value.Subject,
                cancellationToken)
            .ConfigureAwait(false);

        if (subject is null)
        {
            return Result<ResolvedReference>.Failure(
                ConcordatCodes.SubjectNotFound,
                $"'{reference.Name}' points at a subject this tenant cannot see.");
        }

        var version = subject.Versions.FirstOrDefault(v => v.Ordinal == parsed.Value.Version);
        if (version is null)
        {
            return Result<ResolvedReference>.Failure(
                ConcordatCodes.VersionNotFound,
                $"'{reference.Name}' points at a version that does not exist.");
        }

        var schema = await schemas.FindAsync(version.SchemaId, cancellationToken)
            .ConfigureAwait(false);

        return schema is null
            ? Result<ResolvedReference>.Failure(
                ConcordatCodes.SchemaNotFound,
                $"'{reference.Name}' resolves to a schema that is not stored.")
            : Result<ResolvedReference>.Success(
                new ResolvedReference(subject.Name, version.Ordinal, schema));
    }

    /// <summary>
    /// Verifies that a proposed version's references all resolve and introduce no cycle.
    /// </summary>
    /// <param name="proposed">The node the new version would occupy.</param>
    /// <param name="references">The edges derived from the proposed document.</param>
    /// <param name="subjects">The subject repository.</param>
    /// <param name="schemas">The schema repository.</param>
    /// <param name="environments">The environment resolver.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>
    /// Success, or the first failure: an unresolvable reference, or
    /// <see cref="ConcordatCodes.ReferenceCycle"/> naming the path.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>The cycle check cannot currently fail, and runs anyway.</b> Every reference is pinned
    /// to an ordinal (<see cref="Reference"/>) and, from here on, must name a version that
    /// already exists — so an edge can only ever point backwards in time, and a set of edges
    /// that all point backwards cannot close a loop. Even a self-reference is caught earlier, as
    /// a version that does not exist yet, because the proposal's own ordinal is unallocated
    /// while this runs.
    /// </para>
    /// <para>
    /// It is kept because that argument depends entirely on references staying pinned. A
    /// <c>latest</c> selector, a reference repaired to follow a moving pointer, or promotion
    /// learning to rewrite references would each make cycles reachable again, and would do it
    /// without touching this file. A check that is free when the graph is small and correct when
    /// the assumption changes is cheaper than rediscovering the assumption.
    /// </para>
    /// </remarks>
    public static async Task<Result> VerifyAsync(
        SchemaNode proposed,
        IReadOnlyList<Reference> references,
        ISubjectRepository subjects,
        ISchemaRepository schemas,
        IEnvironmentResolver environments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(references);

        if (references.Count == 0)
        {
            return Result.Success();
        }

        var edges = new Dictionary<SchemaNode, IReadOnlyList<SchemaNode>>();
        var pending = new Queue<(SchemaNode Node, IReadOnlyList<Reference> References)>();
        pending.Enqueue((proposed, references));

        while (pending.Count > 0)
        {
            var (node, outgoing) = pending.Dequeue();

            if (edges.ContainsKey(node))
            {
                continue;
            }

            var targets = new List<SchemaNode>(outgoing.Count);
            edges[node] = targets;

            foreach (var reference in outgoing)
            {
                var resolved = await ResolveAsync(
                        reference, subjects, schemas, environments, cancellationToken)
                    .ConfigureAwait(false);

                if (resolved.IsFailure)
                {
                    return Result.Failure(resolved.Error!);
                }

                var target = new SchemaNode(resolved.Value.Subject, resolved.Value.Version);
                targets.Add(target);

                // Enqueued rather than recursed so a deep graph cannot exhaust the stack, and
                // guarded by the edges map above so a cyclic set of stored edges terminates
                // here instead of walking forever -- the same defence the bundler's own walk
                // carries, for the same reason.
                if (!edges.ContainsKey(target))
                {
                    pending.Enqueue((target, resolved.Value.Schema.References));
                }
            }
        }

        return ReferenceGraph.DetectCycle(proposed, edges);
    }
}
