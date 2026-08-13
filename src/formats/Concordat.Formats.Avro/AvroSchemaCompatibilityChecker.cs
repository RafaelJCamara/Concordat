using Concordat.Domain.Registry;
using Concordat.Formats.Abstractions;

namespace Concordat.Formats.Avro;

/// <summary>
/// Decides whether a proposed Avro schema may follow the versions already registered.
/// </summary>
/// <remarks>
/// <para>
/// Unlike JSON Schema, Avro <b>has</b> a specified answer: the Schema Resolution section of the
/// specification defines exactly when a reader can read a writer's output. So this is an
/// implementation of that specification rather than a design, and
/// <see cref="AvroSchemaResolver"/> holds the rules.
/// </para>
/// <para>
/// <b>The direction axis is applied by swapping the roles, not by inspecting the finding.</b>
/// Avro resolution is asymmetric — <c>int</c> promotes to <c>long</c> but not the reverse — so
/// "who breaks" cannot be derived from a single comparison the way a narrower/wider heuristic
/// allows. Backward runs <c>(writer: previous, reader: proposed)</c>; forward runs
/// <c>(writer: proposed, reader: previous)</c>. The same edit legitimately produces different
/// findings in each.
/// </para>
/// <para>
/// <b><see cref="ContentModel"/> is not consulted.</b> It exists because JSON Schema's
/// <c>additionalProperties</c> makes "may a document carry undescribed members" a per-subject
/// choice. Avro records are closed by construction — the wire format is positional and there is
/// no way to encode an undeclared field — so there is no equivalent knob to honour, and
/// pretending to read the setting would suggest it does something.
/// </para>
/// </remarks>
public sealed class AvroSchemaCompatibilityChecker : ICompatibilityChecker
{
    /// <inheritdoc />
    public SchemaFormat Format => SchemaFormat.Avro;

    /// <inheritdoc />
    public CompatibilityReport Check(
        string proposedCanonicalBody,
        IReadOnlyList<PriorSchema> priors,
        CompatibilityPolicy policy,
        ContentModel contentModel)
    {
        ArgumentNullException.ThrowIfNull(proposedCanonicalBody);
        ArgumentNullException.ThrowIfNull(priors);

        // The first version has no predecessor, and CompatibilityMode.None disables checking
        // entirely. Both are trivially compatible.
        if (priors.Count == 0 || !policy.IsChecked)
        {
            return new CompatibilityReport(true, [], [], SemverBump.None);
        }

        var proposed = AvroSchemaParser.Parse(proposedCanonicalBody);
        var divergences = new List<BreakingChange>();

        foreach (var prior in SelectPriors(priors, policy.Mode))
        {
            var previous = AvroSchemaParser.Parse(prior.CanonicalBody);

            foreach (var direction in DirectionsFor(policy.Mode))
            {
                // Backward asks whether the proposal can read the past; forward asks whether
                // the past can read the proposal. Same rules, opposite roles.
                var (writer, reader) = direction is CompatibilityDirection.Backward
                    ? (previous, proposed)
                    : (proposed, previous);

                foreach (var divergence in AvroSchemaResolver.Check(writer, reader, direction))
                {
                    divergences.Add(new BreakingChange(
                        divergence.Path,
                        divergence.Kind,
                        direction,
                        divergence.Surface,
                        divergence.Message,
                        prior.Ordinal));
                }
            }
        }

        // The surface axis decides which divergences actually violate. A Source-only finding is
        // invisible to a WireJson policy: this is what lets 'int' promote to 'long' under the
        // default while a Source policy blocks it (ADR-016).
        var violations = divergences
            .Where(d => policy.IsViolatedBy(d.Surface))
            .ToList();

        var bump = violations.Count > 0
            ? SemverBump.Major
            : divergences.Count > 0
                ? SemverBump.Minor
                : SemverBump.Patch;

        return new CompatibilityReport(violations.Count == 0, violations, divergences, bump);
    }

    /// <summary>
    /// Transitive modes compare against every prior version; non-transitive modes compare only
    /// against the highest ordinal.
    /// </summary>
    /// <remarks>
    /// Chosen here rather than by the caller so it cannot be got wrong at a call site, matching
    /// the JSON checker. Non-transitive is the default and, as with Confluent, a chain of
    /// individually compatible changes can still leave you unable to read the oldest data.
    /// </remarks>
    private static List<PriorSchema> SelectPriors(
        IReadOnlyList<PriorSchema> priors, CompatibilityMode mode)
    {
        var ordered = priors.OrderByDescending(p => p.Ordinal).ToList();

        return mode is CompatibilityMode.BackwardTransitive
            or CompatibilityMode.ForwardTransitive
            or CompatibilityMode.FullTransitive
            ? ordered
            : [ordered[0]];
    }

    private static CompatibilityDirection[] DirectionsFor(CompatibilityMode mode) => mode switch
    {
        CompatibilityMode.Backward or CompatibilityMode.BackwardTransitive =>
            [CompatibilityDirection.Backward],
        CompatibilityMode.Forward or CompatibilityMode.ForwardTransitive =>
            [CompatibilityDirection.Forward],
        CompatibilityMode.Full or CompatibilityMode.FullTransitive =>
            [CompatibilityDirection.Backward, CompatibilityDirection.Forward],
        _ => [],
    };
}
