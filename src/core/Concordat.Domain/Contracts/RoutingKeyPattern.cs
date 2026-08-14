using System.Text.RegularExpressions;
using Concordat.Domain.Results;

namespace Concordat.Domain.Contracts;

/// <summary>
/// An AMQP topic pattern, and the rules for deciding whether two of them can both match.
/// </summary>
/// <remarks>
/// <para>
/// A topic pattern is dot-separated words where <c>*</c> matches exactly one word and
/// <c>#</c> matches zero or more. Both are validated here rather than trusted, because a
/// pattern that the broker would reject is a binding that silently never matches anything —
/// and a contract that never matches is indistinguishable from no contract at all.
/// </para>
/// <para>
/// <b><see cref="Overlaps"/> is what makes the conflicting-binding invariant possible.</b>
/// Two patterns overlap when some routing key would match both, and that is emphatically not
/// string equality: <c>orders.*</c> and <c>*.created</c> share <c>orders.created</c> while
/// having no textual resemblance. Without this, a publisher could satisfy one binding and
/// violate another it never knew applied.
/// </para>
/// </remarks>
public sealed partial record RoutingKeyPattern
{
    /// <summary>The AMQP ceiling on a routing key, and so on a pattern.</summary>
    public const int MaxLength = 255;

    private readonly string[] _segments;

    private RoutingKeyPattern(string value, string[] segments)
    {
        Value = value;
        _segments = segments;
    }

    /// <summary>The pattern as written.</summary>
    public string Value { get; }

    /// <summary>Validates a topic pattern.</summary>
    /// <param name="value">The pattern.</param>
    /// <returns>
    /// The pattern, or a failure carrying <see cref="ConcordatCodes.RoutingKeyPatternInvalid"/>.
    /// </returns>
    public static Result<RoutingKeyPattern> Create(string? value)
    {
        var trimmed = value?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return Result<RoutingKeyPattern>.Failure(
                ConcordatCodes.RoutingKeyPatternInvalid,
                "A routing key pattern is required. Use '#' to match every key.");
        }

        if (trimmed.Length > MaxLength)
        {
            return Result<RoutingKeyPattern>.Failure(
                ConcordatCodes.RoutingKeyPatternInvalid,
                $"A routing key may be at most {MaxLength} characters; got {trimmed.Length}.");
        }

        if (!Grammar().IsMatch(trimmed))
        {
            return Result<RoutingKeyPattern>.Failure(
                ConcordatCodes.RoutingKeyPatternInvalid,
                $"'{trimmed}' is not a valid AMQP topic pattern. Words are separated by '.', " +
                "'*' matches exactly one word and '#' matches zero or more — for example " +
                "'orders.*.created' or 'orders.#'.");
        }

        return Result<RoutingKeyPattern>.Success(
            new RoutingKeyPattern(trimmed, trimmed.Split('.')));
    }

    /// <summary>Creates a pattern already known to be valid.</summary>
    /// <param name="value">A previously validated pattern.</param>
    /// <returns>The pattern.</returns>
    public static RoutingKeyPattern FromTrusted(string value) => new(value, value.Split('.'));

    /// <summary>Whether a concrete routing key matches this pattern.</summary>
    /// <param name="routingKey">The key a publisher used.</param>
    /// <returns><see langword="true"/> when the broker would deliver it.</returns>
    public bool Matches(string? routingKey) =>
        routingKey is not null &&
        Intersects(_segments, 0, routingKey.Split('.'), 0);

    /// <summary>
    /// Whether some routing key would match both patterns.
    /// </summary>
    /// <param name="other">The pattern to compare with.</param>
    /// <returns><see langword="true"/> when the two can both match one key.</returns>
    /// <remarks>
    /// Note this is not "one is a subset of the other" — it is non-empty intersection, which is
    /// the question the invariant actually asks. Two bindings conflict when a single published
    /// message could fall under both, regardless of which is broader.
    /// </remarks>
    public bool Overlaps(RoutingKeyPattern other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return Intersects(_segments, 0, other._segments, 0);
    }

    /// <summary>
    /// Decides whether two segment sequences have a common match.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A concrete routing key is just a pattern with no wildcards, so matching and overlap are
    /// the same computation and share this one implementation. Keeping them separate would be
    /// two chances to disagree about what <c>#</c> means.
    /// </para>
    /// <para>
    /// <c>#</c> matching <b>zero</b> or more words is the case that catches people out:
    /// <c>orders.#</c> matches the bare key <c>orders</c>, so the recursion has to try
    /// consuming nothing before it tries consuming words.
    /// </para>
    /// </remarks>
    private static bool Intersects(string[] left, int i, string[] right, int j)
    {
        while (true)
        {
            if (i == left.Length && j == right.Length)
            {
                return true;
            }

            // Whatever is left on the other side can only survive if it is all '#', each of
            // which is allowed to match nothing.
            if (i == left.Length)
            {
                return Remaining(right, j);
            }

            if (j == right.Length)
            {
                return Remaining(left, i);
            }

            if (left[i] is "#")
            {
                return AbsorbsRest(left, i, right, j);
            }

            if (right[j] is "#")
            {
                return AbsorbsRest(right, j, left, i);
            }

            // Both sides are a literal word or '*', which matches exactly one word.
            if (left[i] is not "*" && right[j] is not "*" &&
                !string.Equals(left[i], right[j], StringComparison.Ordinal))
            {
                return false;
            }

            i++;
            j++;
        }
    }

    private static bool AbsorbsRest(string[] hash, int hashIndex, string[] other, int otherIndex)
    {
        // '#' may consume any number of words from the other side, including none.
        for (var consumed = otherIndex; consumed <= other.Length; consumed++)
        {
            if (Intersects(hash, hashIndex + 1, other, consumed))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Remaining(string[] segments, int from)
    {
        for (var k = from; k < segments.Length; k++)
        {
            if (segments[k] is not "#")
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override string ToString() => Value;

    // Words are letters, digits, underscore and hyphen — the set RabbitMQ accepts in practice.
    // A segment may instead be exactly '*' or '#'; a bare '.' or an empty segment is refused.
    [GeneratedRegex(@"^([A-Za-z0-9_\-]+|\*|#)(\.([A-Za-z0-9_\-]+|\*|#))*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Grammar();
}
