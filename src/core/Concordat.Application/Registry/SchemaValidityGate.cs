using Concordat.Application.Abstractions;
using Concordat.Domain.Results;

namespace Concordat.Application.Registry;

/// <summary>
/// Refuses a proposal whose document is not a valid schema, unless the registry already holds it
/// (ADR-039).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why "already holds it" is an exemption and not an oversight.</b> Before this rule the
/// registry accepted any well-formed JSON as a schema, so documents it would now refuse are
/// stored, have ids, and are in use. An idempotent registration of one — a CI job that reruns, a
/// second subject taking the same content, a promotion that copies a version to the next
/// environment — would turn from a no-op into a failure that nothing the caller did could have
/// caused. Nothing already accepted is rewritten, hidden or made to fail; only a document the
/// registry has not yet accepted meets the rule.
/// </para>
/// <para>
/// <b>"Holds" means reachable by the caller's tenant, not stored.</b> Schemas are stored once,
/// across tenants (ADR-015). Excusing a document because <em>anyone</em> stored it would make
/// the answer to "may I register this?" depend on what other tenants have registered, which
/// turns registration into the oracle <c>schemas/lookup</c> and <c>GET /v1/schemas/{id}</c> were
/// built not to be.
/// </para>
/// <para>
/// <b>The one place that decides.</b> Registration and the dry-run check both call it, so a
/// check cannot go green for a document the publish step then refuses. Promotion and impact
/// analysis do not: a promotion carries a version the registry already holds, so it would always
/// be excused, and an impact analysis writes nothing.
/// </para>
/// </remarks>
public static class SchemaValidityGate
{
    /// <summary>Decides whether a proposal's defects refuse it.</summary>
    /// <param name="proposal">The evaluated proposal.</param>
    /// <param name="schemas">The schema store, asked only when there is a defect to excuse.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The refusal, or <see langword="null"/> when the proposal may go on.</returns>
    public static async Task<DomainError?> RefuseAsync(
        EvaluatedProposal proposal, ISchemaRepository schemas, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(schemas);

        // The common case, and it costs nothing: no defect, no question.
        if (proposal.Defects.Count == 0)
        {
            return null;
        }

        var held = await schemas.IsReachableByCurrentTenantAsync(proposal.Schema.Id, cancellationToken)
            .ConfigureAwait(false);

        if (held)
        {
            return null;
        }

        // The first defect, in path order, and a count of the rest. Listing them all would put an
        // attacker-sized document back into the response; one precise location is what an author
        // acts on, and fixing it and trying again reveals the next.
        var first = proposal.Defects[0];
        var rest = proposal.Defects.Count - 1;

        return new DomainError(
            ConcordatCodes.SchemaInvalid,
            // Some defects are fragments ("must be a number") and some are sentences; trimmed so
            // that the one full stop here is the only one, and not "…cannot follow..".
            $"The document is not a valid schema. At {first.Path}: {first.Message.TrimEnd('.')}." +
            (rest > 0 ? $" ({rest} more.)" : string.Empty));
    }
}
