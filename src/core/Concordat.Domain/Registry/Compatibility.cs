using Concordat.Domain.Results;

namespace Concordat.Domain.Registry;

/// <summary>
/// The <em>who breaks</em> axis of a compatibility policy (ADR-016).
/// </summary>
public enum CompatibilityMode
{
    /// <summary>No compatibility checking.</summary>
    None = 1,

    /// <summary>The new schema reads data written by the previous one.</summary>
    Backward = 2,

    /// <summary>As <see cref="Backward"/>, checked against every prior version.</summary>
    BackwardTransitive = 3,

    /// <summary>The previous schema reads data written by the new one.</summary>
    Forward = 4,

    /// <summary>As <see cref="Forward"/>, checked against every prior version.</summary>
    ForwardTransitive = 5,

    /// <summary>Both <see cref="Backward"/> and <see cref="Forward"/>.</summary>
    Full = 6,

    /// <summary>As <see cref="Full"/>, checked against every prior version.</summary>
    FullTransitive = 7,
}

/// <summary>
/// The <em>what breaks</em> axis of a compatibility policy (ADR-016).
/// </summary>
/// <remarks>
/// The three values form a lattice: <see cref="Wire"/> ⊂ <see cref="WireJson"/> ⊂
/// <see cref="Source"/>. A policy at a given surface is violated by any finding at that
/// surface or narrower. Numbered so the ordering is the enum ordering.
/// </remarks>
public enum CompatibilitySurface
{
    /// <summary>The bytes still decode.</summary>
    Wire = 1,

    /// <summary>The bytes decode and the JSON mapping holds.</summary>
    WireJson = 2,

    /// <summary>The bytes decode, the JSON mapping holds, and generated code still compiles.</summary>
    Source = 3,
}

/// <summary>
/// A compatibility policy: the orthogonal pair of <em>who breaks</em> and <em>what breaks</em>
/// (ADR-016).
/// </summary>
/// <param name="Mode">Which direction of compatibility is required.</param>
/// <param name="Surface">How much must keep working.</param>
/// <remarks>
/// Modelled as a pair rather than a flat enumeration so that, for example,
/// <c>Backward × Wire</c> permits <c>int32 → int64</c> while <c>Backward × Source</c> blocks
/// it. A single axis cannot express that distinction at all.
/// </remarks>
public readonly record struct CompatibilityPolicy(
    CompatibilityMode Mode,
    CompatibilitySurface Surface)
{
    /// <summary>Whether this policy performs any checking at all.</summary>
    public bool IsChecked => Mode != CompatibilityMode.None;

    /// <summary>
    /// Whether a finding at <paramref name="finding"/> violates this policy's surface.
    /// </summary>
    /// <param name="finding">The narrowest surface the finding violates.</param>
    /// <returns>
    /// <see langword="true"/> when the finding is at or below this policy's surface. A
    /// <see cref="CompatibilitySurface.Wire"/> finding violates every policy; a
    /// <see cref="CompatibilitySurface.Source"/> finding violates only a
    /// <see cref="CompatibilitySurface.Source"/> policy.
    /// </returns>
    public bool IsViolatedBy(CompatibilitySurface finding) => finding <= Surface;

    /// <inheritdoc />
    public override string ToString() => $"{Mode} × {Surface}";
}

/// <summary>
/// The compatibility engine's answer for one proposed version.
/// </summary>
/// <remarks>
/// <para>
/// The domain treats this as an <em>input</em>: evaluating schemas is M1.3's job, not the
/// aggregate's. Carrying the policy the engine used lets the aggregate reject a verdict
/// computed against a different policy than the subject's, which is the one disagreement it
/// can detect on its own.
/// </para>
/// <para>
/// This leaves a hole the domain cannot close: a caller that reports
/// <see cref="Compatible"/> for a genuinely breaking change still moves the pointer. M1.6
/// must guarantee that exactly one handler constructs a verdict and that it sources it from
/// the engine.
/// </para>
/// </remarks>
public sealed record CompatibilityVerdict
{
    private CompatibilityVerdict(bool isBreaking, CompatibilityPolicy evaluatedUnder)
    {
        IsBreaking = isBreaking;
        EvaluatedUnder = evaluatedUnder;
    }

    /// <summary>Whether the proposed schema breaks the policy.</summary>
    public bool IsBreaking { get; }

    /// <summary>The policy the engine evaluated against.</summary>
    public CompatibilityPolicy EvaluatedUnder { get; }

    /// <summary>The proposed schema satisfies the policy.</summary>
    /// <param name="evaluatedUnder">The policy the engine used.</param>
    /// <returns>A non-breaking verdict.</returns>
    public static CompatibilityVerdict Compatible(CompatibilityPolicy evaluatedUnder) =>
        new(false, evaluatedUnder);

    /// <summary>The proposed schema breaks the policy.</summary>
    /// <param name="evaluatedUnder">The policy the engine used.</param>
    /// <returns>A breaking verdict.</returns>
    public static CompatibilityVerdict Breaking(CompatibilityPolicy evaluatedUnder) =>
        new(true, evaluatedUnder);
}

/// <summary>
/// Whether an SDK may register schemas directly against an environment.
/// </summary>
/// <remarks>
/// Enforced server-side. Confluent's equivalent is client-side only with no server override,
/// so one misconfigured producer can permanently pollute the registry.
/// </remarks>
public enum RegistrationPolicy
{
    /// <summary>Any authorised caller may register.</summary>
    Open = 1,

    /// <summary>Only CI may register. The default for production environments.</summary>
    CiOnly = 2,

    /// <summary>No registration; schemas arrive only by promotion from another environment.</summary>
    Closed = 3,
}
