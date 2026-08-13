using Concordat.Domain.Registry;

namespace Concordat.Formats.Abstractions;

/// <summary>
/// Which direction of compatibility a divergence breaks.
/// </summary>
public enum CompatibilityDirection
{
    /// <summary>
    /// The new schema cannot read data written under the old one. The new schema is narrower.
    /// </summary>
    Backward = 1,

    /// <summary>
    /// The old schema cannot read data written under the new one. The new schema is wider.
    /// </summary>
    Forward = 2,
}

/// <summary>
/// The stable <c>kind</c> tokens on a breaking change.
/// </summary>
/// <remarks>
/// Normative protocol (ADR-019): they appear in <c>breakingChanges[]</c> and clients branch on
/// them. Deliberately not enum member names, for the same reason as
/// <see cref="WireTokens"/>.
/// </remarks>
public static class BreakingChangeKinds
{
    /// <summary>A member was added to <c>required</c>.</summary>
    public const string RequiredFieldAdded = "required_field_added";

    /// <summary>A member was removed from <c>required</c>.</summary>
    public const string RequiredFieldRemoved = "required_field_removed";

    /// <summary>A described property was added.</summary>
    public const string PropertyAdded = "property_added";

    /// <summary>A described property was removed.</summary>
    public const string PropertyRemoved = "property_removed";

    /// <summary>The permitted type set shrank.</summary>
    public const string TypeNarrowed = "type_narrowed";

    /// <summary>The permitted type set grew.</summary>
    public const string TypeWidened = "type_widened";

    /// <summary>An <c>enum</c> value was removed.</summary>
    public const string EnumValueRemoved = "enum_value_removed";

    /// <summary>An <c>enum</c> value was added.</summary>
    public const string EnumValueAdded = "enum_value_added";

    /// <summary>A numeric bound became stricter.</summary>
    public const string NumericRangeNarrowed = "numeric_range_narrowed";

    /// <summary>A numeric bound became looser.</summary>
    public const string NumericRangeWidened = "numeric_range_widened";

    /// <summary>A string constraint became stricter.</summary>
    public const string StringConstraintNarrowed = "string_constraint_narrowed";

    /// <summary>A string constraint became looser.</summary>
    public const string StringConstraintWidened = "string_constraint_widened";

    /// <summary><c>additionalProperties</c> went from permitted to forbidden.</summary>
    public const string AdditionalPropertiesClosed = "additional_properties_closed";

    /// <summary><c>additionalProperties</c> went from forbidden to permitted.</summary>
    public const string AdditionalPropertiesOpened = "additional_properties_opened";

    /// <summary>A <c>format</c> annotation changed, altering generated code but not validation.</summary>
    public const string FormatChanged = "format_changed";

    /// <summary>
    /// <c>integer</c> widened to <c>number</c>: every existing document still validates, but
    /// generated code changes from an integral type to a floating-point one.
    /// </summary>
    public const string IntegerWidenedToNumber = "integer_widened_to_number";

    // ------------------------------------------------------------------------- Avro
    //
    // Added in M5.2. Additive to the catalogue, but still normative protocol under ADR-019:
    // once published, a client may branch on them.

    /// <summary>
    /// A named type's fullname changed and no reader alias covers it. Avro resolves named
    /// types by fullname, so the bytes do not decode.
    /// </summary>
    public const string NameChanged = "name_changed";

    /// <summary>
    /// A <c>fixed</c> type's width changed. The width is schema-declared rather than carried in
    /// the data, so a change misaligns everything after it.
    /// </summary>
    public const string FixedSizeChanged = "fixed_size_changed";

    /// <summary>
    /// One primitive is promoted to a wider one on read — Avro's <c>int</c> → <c>long</c>.
    /// Values still decode; the generated type changes. The second-axis case of ADR-016.
    /// </summary>
    public const string TypePromoted = "type_promoted";

    /// <summary>
    /// An enum symbol is absent from the reader and absorbed by its declared <c>default</c>.
    /// Decoding succeeds, but the value read is not the value written.
    /// </summary>
    public const string EnumValueDefaulted = "enum_value_defaulted";
}

/// <summary>
/// One way a proposed schema diverges from a previous version.
/// </summary>
/// <param name="Path">
/// A JSON Pointer into the <em>schema</em> document, prefixed with <c>#</c>, for example
/// <c>#/properties/legacyId</c>. Confluent's inability to say precisely where is its most
/// cited usability failure, so this is required, never approximate.
/// </param>
/// <param name="Kind">A token from <see cref="BreakingChangeKinds"/>.</param>
/// <param name="Direction">Which reader this breaks.</param>
/// <param name="Surface">
/// The <b>narrowest</b> surface this violates. A policy is violated by findings at or below
/// its own surface, so a <see cref="CompatibilitySurface.Source"/> finding is invisible to a
/// <see cref="CompatibilitySurface.Wire"/> policy.
/// </param>
/// <param name="Message">An actionable explanation naming the consequence, not just the rule.</param>
/// <param name="ConflictsWithVersion">The prior version ordinal this was compared against.</param>
public sealed record BreakingChange(
    string Path,
    string Kind,
    CompatibilityDirection Direction,
    CompatibilitySurface Surface,
    string Message,
    int ConflictsWithVersion);

/// <summary>The smallest semantic version increment a change warrants.</summary>
public enum SemverBump
{
    /// <summary>Nothing changed.</summary>
    None = 1,

    /// <summary>A change with no divergences that violate the policy.</summary>
    Patch = 2,

    /// <summary>Additive divergences that do not violate the policy.</summary>
    Minor = 3,

    /// <summary>At least one divergence violates the policy.</summary>
    Major = 4,
}

/// <summary>A previously registered version to check against.</summary>
/// <param name="Ordinal">The version ordinal.</param>
/// <param name="CanonicalBody">Its canonical text, as produced by the format's canonicaliser.</param>
public sealed record PriorSchema(int Ordinal, string CanonicalBody);

/// <summary>The engine's full answer for one proposed schema.</summary>
/// <param name="IsCompatible">Whether the proposal satisfies the policy.</param>
/// <param name="BreakingChanges">
/// Divergences that <b>violate the policy</b>, most recent prior version first.
/// </param>
/// <param name="AllDivergences">
/// Every divergence found, including those the policy tolerates. A
/// <see cref="CompatibilitySurface.Source"/> divergence under a
/// <see cref="CompatibilitySurface.Wire"/> policy appears here but not in
/// <see cref="BreakingChanges"/> — this is what lets the API explain why a change was allowed.
/// </param>
/// <param name="SuggestedBump">The semantic version increment this change warrants.</param>
public sealed record CompatibilityReport(
    bool IsCompatible,
    IReadOnlyList<BreakingChange> BreakingChanges,
    IReadOnlyList<BreakingChange> AllDivergences,
    SemverBump SuggestedBump);

/// <summary>
/// Decides whether a proposed schema may follow the versions already registered.
/// </summary>
/// <remarks>
/// The correctness heart of the product. A wrong verdict either blocks a safe change or waves
/// a breaking one into production, and neither is discovered until it has already happened.
/// </remarks>
public interface ICompatibilityChecker
{
    /// <summary>The format this checker handles.</summary>
    SchemaFormat Format { get; }

    /// <summary>Checks a proposed schema against the versions preceding it.</summary>
    /// <param name="proposedCanonicalBody">The proposal, already canonicalised.</param>
    /// <param name="priors">
    /// Previously registered versions. May be empty, in which case the proposal is trivially
    /// compatible. The checker selects which to compare against from the policy's mode:
    /// transitive modes use all of them, non-transitive modes use the highest ordinal only.
    /// </param>
    /// <param name="policy">The resolved policy: which direction, and how much must keep working.</param>
    /// <param name="contentModel">The subject's explicit content model.</param>
    /// <returns>The verdict, the violating changes, and every divergence found.</returns>
    CompatibilityReport Check(
        string proposedCanonicalBody,
        IReadOnlyList<PriorSchema> priors,
        CompatibilityPolicy policy,
        ContentModel contentModel);
}
