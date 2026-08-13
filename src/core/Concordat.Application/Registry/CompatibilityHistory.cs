using Concordat.Domain.Registry;

namespace Concordat.Application.Registry;

/// <summary>
/// Decides which versions a proposal must stay compatible with.
/// </summary>
/// <remarks>
/// <para>
/// <b>One definition, deliberately.</b> This rule was previously written out at both call
/// sites — registration and the dry-run check — and they were wrong in exactly the same way,
/// so the preview agreed with the gate and neither contradicted the other. A subtle rule
/// duplicated in two places is a rule that drifts silently or, worse, does not drift and is
/// simply wrong twice.
/// </para>
/// <para>
/// <b>History is what was approved.</b> Only <see cref="VersionStatus.Active"/> versions
/// count:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <see cref="VersionStatus.Rejected"/> proposals were declined precisely so that nothing
///     would depend on them.
///   </description></item>
///   <item><description>
///     <see cref="VersionStatus.AwaitingApproval"/> proposals have not been accepted yet, so
///     treating one as history lets a proposal justify itself. That was a real defect: the
///     default mode is non-transitive and compares against the highest ordinal, so submitting
///     the same breaking schema twice compared the second attempt against the first, found no
///     divergence, and registered <c>Active</c> — moving <c>latest</c> onto a schema
///     incompatible with the last approved version, with nobody having approved anything. A
///     retrying CI job did it unaided. This is the fix for that, and it is what makes
///     ADR-017's gate mean something.
///   </description></item>
/// </list>
/// <para>
/// Deprecated versions <em>are</em> included: deprecation is advisory and the version is still
/// approved history that consumers may be reading.
/// </para>
/// <para>
/// <b>Not used for the semver suggestion.</b> That deliberately still counts pending labels,
/// because <see cref="Subject"/>'s own increasing-label check does — suggesting a label the
/// aggregate would then refuse would be a worse bug than the one this fixes.
/// </para>
/// </remarks>
internal static class CompatibilityHistory
{
    /// <summary>The versions a proposal is checked against.</summary>
    /// <param name="subject">The subject.</param>
    /// <returns>Its approved versions, in the order the subject holds them.</returns>
    public static IEnumerable<SchemaVersion> Of(Subject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);

        return subject.Versions.Where(v => v.Status is VersionStatus.Active);
    }
}
