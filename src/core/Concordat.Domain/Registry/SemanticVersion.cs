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
/// <remarks>
/// The integer ordinal is canonical identity; this label carries <em>intent</em> and the
/// registry verifies it. Pre-release and build metadata are rejected in v1 — see
/// <see cref="ConcordatCodes.SemverPrereleaseUnsupported"/>.
/// </remarks>
public readonly partial record struct SemanticVersion(int Major, int Minor, int Patch)
    : IComparable<SemanticVersion>
{
    [GeneratedRegex(@"^(\d+)\.(\d+)\.(\d+)(?<rest>.*)\z", RegexOptions.CultureInvariant)]
    private static partial Regex CorePattern();

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
        if (rest.Length > 0)
        {
            return rest[0] is '-' or '+'
                ? Result<SemanticVersion>.Failure(
                    ConcordatCodes.SemverPrereleaseUnsupported,
                    $"Pre-release and build metadata are not supported in v1; got '{trimmed}'.")
                : Result<SemanticVersion>.Failure(
                    ConcordatCodes.SemverInvalid,
                    $"Unexpected trailing text '{rest}' in '{trimmed}'.");
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
            new SemanticVersion(numbers[0], numbers[1], numbers[2]));
    }

    /// <summary>Whether this label increases the major component relative to <paramref name="previous"/>.</summary>
    /// <param name="previous">The label on the preceding version.</param>
    /// <returns><see langword="true"/> when the major component increased.</returns>
    public bool IsMajorBumpOver(SemanticVersion previous) => Major > previous.Major;

    /// <inheritdoc />
    public int CompareTo(SemanticVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0)
        {
            return major;
        }

        var minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
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
        string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
}
