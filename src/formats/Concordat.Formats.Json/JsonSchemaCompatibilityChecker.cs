using System.Text.Json;
using Concordat.Domain.Registry;
using Concordat.Formats.Abstractions;

namespace Concordat.Formats.Json;

/// <summary>
/// Decides whether a proposed JSON Schema may follow the versions already registered.
/// </summary>
/// <remarks>
/// <para>
/// Designed from scratch rather than ported. There is no compatibility specification for JSON
/// Schema, and Confluent's attempt is widely judged unusable: because it treats open and
/// closed content models under mutually exclusive rules, <b>adding an optional field is not
/// backward compatible under its defaults</b>. The workaround teams actually ship is setting
/// compatibility to <c>NONE</c> — a registry with its central value proposition switched off.
/// </para>
/// <para>The acceptance criteria this exists to satisfy (DESIGN §7):</para>
/// <list type="bullet">
///   <item><description>adding an optional property is fully compatible;</description></item>
///   <item><description>removing an optional property is fully compatible;</description></item>
///   <item><description>the content model is explicit subject config, never inferred;</description></item>
///   <item><description>narrowing is backward-breaking, widening is forward-breaking;</description></item>
///   <item><description>every finding carries an exact JSON-Pointer path.</description></item>
/// </list>
/// </remarks>
public sealed class JsonSchemaCompatibilityChecker : ICompatibilityChecker
{
    /// <inheritdoc />
    public SchemaFormat Format => SchemaFormat.Json;

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

        using var proposed = JsonDocument.Parse(proposedCanonicalBody);

        var compared = SelectPriors(priors, policy.Mode);
        var divergences = new List<BreakingChange>();

        foreach (var prior in compared)
        {
            using var previous = JsonDocument.Parse(prior.CanonicalBody);

            foreach (var divergence in JsonSchemaDiff.Compare(
                         previous.RootElement, proposed.RootElement, contentModel))
            {
                if (!AppliesTo(divergence.Direction, policy.Mode))
                {
                    continue;
                }

                divergences.Add(new BreakingChange(
                    divergence.Path,
                    divergence.Kind,
                    divergence.Direction,
                    divergence.Surface,
                    divergence.Message,
                    prior.Ordinal));
            }
        }

        // The surface axis decides which divergences actually violate. A Source-only finding
        // is invisible to a WireJson policy: this is what lets 'integer' widen to 'number'
        // under the default while a Source policy blocks it (ADR-016).
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
    /// Chosen here rather than by the caller so it cannot be got wrong at a call site. Note
    /// that non-transitive is the default and, as with Confluent, a chain of individually
    /// compatible changes can still leave you unable to read the oldest data — which is why
    /// DESIGN §7 says to document it loudly.
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

    private static bool AppliesTo(CompatibilityDirection direction, CompatibilityMode mode) =>
        mode switch
        {
            CompatibilityMode.Backward or CompatibilityMode.BackwardTransitive =>
                direction is CompatibilityDirection.Backward,
            CompatibilityMode.Forward or CompatibilityMode.ForwardTransitive =>
                direction is CompatibilityDirection.Forward,
            CompatibilityMode.Full or CompatibilityMode.FullTransitive => true,
            _ => false,
        };
}
