namespace Concordat.Cli.Inference;

/// <summary>How much a reviewer should trust one inferred decision.</summary>
public enum Confidence
{
    /// <summary>Consistent across enough samples to be worth trusting.</summary>
    High = 1,

    /// <summary>Consistent, but on thin evidence.</summary>
    Medium = 2,

    /// <summary>A guess. Read this line before registering anything.</summary>
    Low = 3,
}

/// <summary>One thing the inferrer decided that it could plausibly have decided differently.</summary>
/// <param name="Path">A JSON Pointer into the inferred schema.</param>
/// <param name="Confidence">How much to trust it.</param>
/// <param name="Kind">A stable token, for example <c>required_from_presence</c>.</param>
/// <param name="Message">What was assumed, and what would happen if the assumption is wrong.</param>
public sealed record InferenceFinding(string Path, Confidence Confidence, string Kind, string Message);

/// <summary>
/// The inferred schema and everything questionable about it.
/// </summary>
/// <remarks>
/// <b>The report is the deliverable, not a footnote.</b> ADR-014 is explicit that inference
/// never auto-registers, and the reason is that a schema inferred from samples is a
/// well-informed guess: a field absent from every sample is inferred absent, and an optional
/// field present in all of them is inferred required. Handing over a bare schema would hide
/// exactly the decisions a human needs to overrule.
/// </remarks>
/// <param name="Schema">The draft, as canonical-ish JSON text.</param>
/// <param name="SampleCount">How many documents were read.</param>
/// <param name="Findings">Everything worth a second look, worst first.</param>
public sealed record InferenceResult(string Schema, int SampleCount, IReadOnlyList<InferenceFinding> Findings)
{
    /// <summary>The findings that most deserve review.</summary>
    public IEnumerable<InferenceFinding> LowConfidence =>
        Findings.Where(f => f.Confidence is Confidence.Low);
}

/// <summary>The stable <c>kind</c> tokens on an inference finding.</summary>
/// <remarks>
/// Stable so a reviewer can grep or filter, and so <c>--json</c> consumers can branch. Not
/// normative protocol, unlike <c>concordatCode</c>: inference is a drafting aid and other SDKs
/// are free to infer differently.
/// </remarks>
public static class FindingKinds
{
    /// <summary>Too few samples for any of this to mean much.</summary>
    public const string ThinEvidence = "thin_evidence";

    /// <summary>A property was required because it appeared in every sample.</summary>
    public const string RequiredFromPresence = "required_from_presence";

    /// <summary>All observed numbers were whole, so the type was narrowed to integer.</summary>
    public const string IntegerFromWholeNumbers = "integer_from_whole_numbers";

    /// <summary>A small set of distinct values was read as an enum.</summary>
    public const string EnumFromLowCardinality = "enum_from_low_cardinality";

    /// <summary>Every observed value was identical, which is not enough to call it an enum.</summary>
    public const string ConstantValue = "constant_value";

    /// <summary>Every observed string matched a known format.</summary>
    public const string FormatFromPattern = "format_from_pattern";

    /// <summary>The property was only ever null, so its type is unknown.</summary>
    public const string AlwaysNull = "always_null";

    /// <summary>The property held more than one JSON type across samples.</summary>
    public const string MixedTypes = "mixed_types";

    /// <summary>An array was always empty, so nothing is known about its items.</summary>
    public const string EmptyArray = "empty_array";

    /// <summary>A property appeared in some samples but not all, so it was left optional.</summary>
    public const string OptionalFromAbsence = "optional_from_absence";
}
