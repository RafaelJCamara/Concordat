using System.Globalization;
using System.Text.RegularExpressions;
using Concordat.Domain.Results;

namespace Concordat.Domain.Registry;

/// <summary>
/// An optional <c>MAJOR.MINOR.PATCH</c> label on a schema version (ADR-004).
/// </summary>
/// <param name="Major">The major component.</param>
/// <param name="Minor">The minor component.</param>
/// <param name="Patch">The patch component.</param>
/// <param name="PreRelease">
/// The pre-release identifiers after the hyphen, or null for an ordinary release.
/// </param>
/// <remarks>
/// <para>
/// The integer ordinal is canonical identity; this label carries <em>intent</em> and the
/// registry verifies it.
/// </para>
/// <para>
/// <b>A pre-release label parses here and is refused by policy elsewhere</b> (decision 8).
/// Whether <c>2.0.0-rc.1</c> is acceptable is a property of the environment it is being
/// registered into, not of the string — so the value object understands the grammar and
/// <c>Environment.AllowPreReleaseVersions</c> decides. Folding the policy into parsing was the
/// old behaviour and meant a team whose pipeline emits <c>-rc</c> labels could not label a
/// version at all, anywhere, ever.
/// </para>
/// <para>
/// <b>Build metadata is still refused.</b> SemVer 2.0.0 says it is ignored for precedence, so
/// <c>1.0.0+a</c> and <c>1.0.0+b</c> compare equal while being different strings — and this
/// registry's "the label must increase" rule would then accept a label that did not increase.
/// A grammar that can express something the ordering cannot see is a trap, not a feature.
/// </para>
/// </remarks>
public readonly partial record struct SemanticVersion(
    int Major, int Minor, int Patch, string? PreRelease = null)
    : IComparable<SemanticVersion>
{
    [GeneratedRegex(@"^(\d+)\.(\d+)\.(\d+)(?<rest>.*)\z", RegexOptions.CultureInvariant)]
    private static partial Regex CorePattern();

    // SemVer 2.0.0's pre-release grammar: dot-separated identifiers of ASCII alphanumerics and
    // hyphens, and a numeric identifier may not carry a leading zero -- because `01` and `1`
    // would otherwise be different strings that compare equal.
    [GeneratedRegex(
        @"^(0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(\.(0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex PreReleasePattern();

    /// <summary>Whether this label is a pre-release, and so precedes its own release.</summary>
    public bool IsPreRelease => PreRelease is not null;

    /// <summary>Parses a <c>MAJOR.MINOR.PATCH</c> label.</summary>
    /// <param name="value">Candidate text, for example <c>2.0.0</c>.</param>
    /// <returns>
    /// The parsed label, or a failure carrying <see cref="ConcordatCodes.SemverInvalid"/> or
    /// <see cref="ConcordatCodes.SemverPrereleaseUnsupported"/>.
    /// </returns>
    public static Result<SemanticVersion> Create(string? value)
    {
        var trimmed = value?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return Result<SemanticVersion>.Failure(
                ConcordatCodes.SemverInvalid, "A semantic version label is required.");
        }

        // Match the core triple first, then inspect what follows. Scanning for '-' or '+'
        // anywhere would misreport "-1.0.0" as an unsupported pre-release when it is simply
        // malformed, which sends the user looking in the wrong place.
        var match = CorePattern().Match(trimmed);
        if (!match.Success)
        {
            return Result<SemanticVersion>.Failure(
                ConcordatCodes.SemverInvalid,
                $"Expected MAJOR.MINOR.PATCH; got '{trimmed}'.");
        }

        var rest = match.Groups["rest"].Value;
        string? preRelease = null;

        if (rest.Length > 0)
        {
            if (rest[0] is '+')
            {
                return Result<SemanticVersion>.Failure(
                    ConcordatCodes.SemverBuildMetadataUnsupported,
                    $"Build metadata is not supported; got '{trimmed}'. SemVer ignores it for " +
                    "precedence, so two labels carrying different metadata compare equal — and " +
                    "this registry requires each label to increase on the last.");
            }

            if (rest[0] is not '-')
            {
                return Result<SemanticVersion>.Failure(
                    ConcordatCodes.SemverInvalid,
                    $"Unexpected trailing text '{rest}' in '{trimmed}'.");
            }

            preRelease = rest[1..];

            if (preRelease.Contains('+', StringComparison.Ordinal))
            {
                return Result<SemanticVersion>.Failure(
                    ConcordatCodes.SemverBuildMetadataUnsupported,
                    $"Build metadata is not supported; got '{trimmed}'.");
            }

            if (!PreReleasePattern().IsMatch(preRelease))
            {
                return Result<SemanticVersion>.Failure(
                    ConcordatCodes.SemverInvalid,
                    $"'{preRelease}' is not a valid pre-release: dot-separated alphanumerics " +
                    $"and hyphens, no leading zero on a numeric identifier, in '{trimmed}'.");
            }
        }

        var numbers = new int[3];
        for (var i = 0; i < 3; i++)
        {
            // TryParse still required: the pattern permits digit runs that overflow Int32.
            if (!int.TryParse(
                    match.Groups[i + 1].ValueSpan,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out numbers[i]))
            {
                return Result<SemanticVersion>.Failure(
                    ConcordatCodes.SemverInvalid,
                    $"'{match.Groups[i + 1].Value}' is out of range, in '{trimmed}'.");
            }
        }

        return Result<SemanticVersion>.Success(
            new SemanticVersion(numbers[0], numbers[1], numbers[2], preRelease));
    }

    /// <summary>Whether this label increases the major component relative to <paramref name="previous"/>.</summary>
    /// <param name="previous">The label on the preceding version.</param>
    /// <returns><see langword="true"/> when the major component increased.</returns>
    public bool IsMajorBumpOver(SemanticVersion previous) => Major > previous.Major;

    /// <summary>Orders two labels by SemVer 2.0.0 precedence.</summary>
    /// <param name="other">The label to compare against.</param>
    /// <returns>Negative, zero or positive in the usual way.</returns>
    /// <remarks>
    /// <b>A pre-release precedes its own release</b> — <c>1.0.0-rc.1 &lt; 1.0.0</c> — which is
    /// the rule that makes the registry's "each label must increase" check do what a team using
    /// release candidates expects: rc.1, rc.2, then the release.
    /// </remarks>
    public int CompareTo(SemanticVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0)
        {
            return major;
        }

        var minor = Minor.CompareTo(other.Minor);
        if (minor != 0)
        {
            return minor;
        }

        var patch = Patch.CompareTo(other.Patch);
        return patch != 0 ? patch : ComparePreRelease(PreRelease, other.PreRelease);
    }

    /// <summary>Compares the pre-release parts of two otherwise equal labels.</summary>
    private static int ComparePreRelease(string? left, string? right)
    {
        if (left is null && right is null)
        {
            return 0;
        }

        // Absent beats present: 1.0.0 follows 1.0.0-rc.1. This is the one place SemVer's
        // ordering is counter-intuitive if read as plain string comparison.
        if (left is null)
        {
            return 1;
        }

        if (right is null)
        {
            return -1;
        }

        var mine = left.Split('.');
        var theirs = right.Split('.');

        for (var i = 0; i < Math.Min(mine.Length, theirs.Length); i++)
        {
            var a = mine[i];
            var b = theirs[i];

            var aNumeric = int.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out var an);
            var bNumeric = int.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out var bn);

            // Numeric identifiers compare numerically, so rc.10 follows rc.9 -- which plain
            // string ordering gets backwards, and which is exactly the sequence a release-
            // candidate pipeline produces.
            if (aNumeric && bNumeric)
            {
                var numeric = an.CompareTo(bn);
                if (numeric != 0)
                {
                    return numeric;
                }

                continue;
            }

            // A numeric identifier always precedes an alphanumeric one.
            if (aNumeric != bNumeric)
            {
                return aNumeric ? -1 : 1;
            }

            var text = string.CompareOrdinal(a, b);
            if (text != 0)
            {
                return text;
            }
        }

        // Everything shared is equal, so more identifiers wins: 1.0.0-rc.1.1 follows 1.0.0-rc.1.
        return mine.Length.CompareTo(theirs.Length);
    }

    /// <summary>Orders two labels.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> precedes <paramref name="right"/>.</returns>
    public static bool operator <(SemanticVersion left, SemanticVersion right) =>
        left.CompareTo(right) < 0;

    /// <summary>Orders two labels.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> follows <paramref name="right"/>.</returns>
    public static bool operator >(SemanticVersion left, SemanticVersion right) =>
        left.CompareTo(right) > 0;

    /// <summary>Orders two labels.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> does not follow <paramref name="right"/>.</returns>
    public static bool operator <=(SemanticVersion left, SemanticVersion right) =>
        left.CompareTo(right) <= 0;

    /// <summary>Orders two labels.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> does not precede <paramref name="right"/>.</returns>
    public static bool operator >=(SemanticVersion left, SemanticVersion right) =>
        left.CompareTo(right) >= 0;

    /// <inheritdoc />
    public override string ToString() =>
        PreRelease is null
            ? string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}")
            : string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}-{PreRelease}");
}
