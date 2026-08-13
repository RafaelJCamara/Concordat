using Concordat.Domain.Results;

namespace Concordat.Domain.Registry;

/// <summary>
/// One node in the reference graph: a specific version of a specific subject.
/// </summary>
/// <param name="Subject">The subject.</param>
/// <param name="Version">The version ordinal.</param>
/// <remarks>
/// The graph is keyed by <em>version</em>, not by subject. A subject-level graph would reject
/// <c>A@2 → B@1 → A@1</c>, which is a perfectly good directed acyclic graph: version 1 of A
/// existed before B@1 referenced it, and A@2 came later. Only a genuine version-level cycle
/// is an error.
/// </remarks>
public readonly record struct SchemaNode(SubjectName Subject, int Version)
{
    /// <inheritdoc />
    public override string ToString() => $"{Subject.Value}@{Version}";
}

/// <summary>
/// Queries over the reference edges between schema versions.
/// </summary>
/// <remarks>
/// Pure functions over a supplied edge set. The domain does not load anything — M1.6 supplies
/// the edges from the store and decides what to do with the answers.
/// </remarks>
public static class ReferenceGraph
{
    /// <summary>
    /// Detects a cycle reachable from <paramref name="root"/>.
    /// </summary>
    /// <param name="root">The node being registered.</param>
    /// <param name="edges">
    /// The outgoing references of every known node, including the proposed edges for
    /// <paramref name="root"/> itself.
    /// </param>
    /// <returns>
    /// Success when the graph reachable from <paramref name="root"/> is acyclic, otherwise a
    /// failure carrying <see cref="ConcordatCodes.ReferenceCycle"/> and the cycle path.
    /// </returns>
    /// <remarks>
    /// Registration order makes most cycles impossible — a reference can only point at a
    /// version that already exists. This catches the cases that order does not prevent, and
    /// self-reference, which it does not.
    /// </remarks>
    public static Result DetectCycle(
        SchemaNode root,
        IReadOnlyDictionary<SchemaNode, IReadOnlyList<SchemaNode>> edges)
    {
        ArgumentNullException.ThrowIfNull(edges);

        var visiting = new HashSet<SchemaNode>();
        var settled = new HashSet<SchemaNode>();
        var path = new List<SchemaNode>();

        return Walk(root, edges, visiting, settled, path);
    }

    /// <summary>
    /// Finds every node that references <paramref name="target"/>, directly or transitively.
    /// </summary>
    /// <param name="target">The node whose dependants are wanted.</param>
    /// <param name="edges">The outgoing references of every known node.</param>
    /// <returns>The referrers, in no particular order.</returns>
    /// <remarks>
    /// A breaking change inside a referenced schema breaks every referrer, so registering a
    /// new version of a referenced subject means re-checking all of these (DESIGN §4).
    /// Transitive, because a referrer's referrer is affected too.
    /// </remarks>
    public static IReadOnlySet<SchemaNode> ReferrersOf(
        SchemaNode target,
        IReadOnlyDictionary<SchemaNode, IReadOnlyList<SchemaNode>> edges)
    {
        ArgumentNullException.ThrowIfNull(edges);

        // Invert once, then breadth-first from the target.
        var inverted = new Dictionary<SchemaNode, List<SchemaNode>>();
        foreach (var (from, tos) in edges)
        {
            foreach (var to in tos)
            {
                if (!inverted.TryGetValue(to, out var list))
                {
                    list = [];
                    inverted[to] = list;
                }

                list.Add(from);
            }
        }

        var found = new HashSet<SchemaNode>();
        var queue = new Queue<SchemaNode>();
        queue.Enqueue(target);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!inverted.TryGetValue(current, out var referrers))
            {
                continue;
            }

            foreach (var referrer in referrers)
            {
                // The guard also terminates on a cyclic edge set, so this stays safe even if
                // DetectCycle was never run.
                if (found.Add(referrer))
                {
                    queue.Enqueue(referrer);
                }
            }
        }

        return found;
    }

    private static Result Walk(
        SchemaNode node,
        IReadOnlyDictionary<SchemaNode, IReadOnlyList<SchemaNode>> edges,
        HashSet<SchemaNode> visiting,
        HashSet<SchemaNode> settled,
        List<SchemaNode> path)
    {
        if (settled.Contains(node))
        {
            return Result.Success();
        }

        if (!visiting.Add(node))
        {
            var start = path.IndexOf(node);
            var cycle = path.Skip(start < 0 ? 0 : start).Append(node).Select(n => n.ToString());
            return Result.Failure(
                ConcordatCodes.ReferenceCycle,
                $"Reference cycle: {string.Join(" -> ", cycle)}.");
        }

        path.Add(node);

        if (edges.TryGetValue(node, out var targets))
        {
            foreach (var target in targets)
            {
                var result = Walk(target, edges, visiting, settled, path);
                if (result.IsFailure)
                {
                    return result;
                }
            }
        }

        path.RemoveAt(path.Count - 1);
        visiting.Remove(node);
        settled.Add(node);
        return Result.Success();
    }
}
