using System.Globalization;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;

namespace Concordat.Domain.Contracts;

/// <summary>How a binding chooses which version of a subject applies.</summary>
public enum VersionSelectorKind
{
    /// <summary>Whatever the gated <c>latest</c> pointer resolves to.</summary>
    Latest = 1,

    /// <summary>One exact ordinal, forever.</summary>
    Pinned = 2,

    /// <summary>Any ordinal at or above a floor.</summary>
    Range = 3,
}

/// <summary>
/// Which version of a subject a binding accepts.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="VersionSelectorKind.Latest"/> is convenient and is not what production should
/// use.</b> DESIGN §4 is explicit: a contract that tracks <c>latest</c> changes meaning when
/// somebody else registers a version, with no deploy on the consumer's side. Concordat's gated
/// pointer (ADR-017) makes that a reviewed event rather than an accident, which is a large
/// improvement on Confluent's mutable pointer — but "reviewed by someone else" is still not
/// "chosen by me".
/// </para>
/// <para>
/// <see cref="VersionSelectorKind.Pinned"/> is the honest default for a consumer that must not
/// change under it. <see cref="VersionSelectorKind.Range"/> is the middle ground: accept
/// anything from a known floor, so additive changes flow and the floor records what the
/// consumer was actually built against.
/// </para>
/// </remarks>
public sealed record VersionSelector
{
    private VersionSelector(VersionSelectorKind kind, int? ordinal)
    {
        Kind = kind;
        Ordinal = ordinal;
    }

    /// <summary>Which rule applies.</summary>
    public VersionSelectorKind Kind { get; }

    /// <summary>
    /// The pinned ordinal, or the floor of a range. Null for <see cref="VersionSelectorKind.Latest"/>.
    /// </summary>
    public int? Ordinal { get; }

    /// <summary>Tracks the gated latest pointer.</summary>
    public static VersionSelector Latest { get; } = new(VersionSelectorKind.Latest, null);

    /// <summary>Pins one exact version.</summary>
    /// <param name="ordinal">The ordinal.</param>
    /// <returns>The selector, or a failure.</returns>
    public static Result<VersionSelector> Pinned(int ordinal) =>
        ordinal < 1
            ? Result<VersionSelector>.Failure(
                ConcordatCodes.VersionSelectorInvalid,
                $"A pinned version must be at least 1; got {ordinal}.")
            : Result<VersionSelector>.Success(new VersionSelector(VersionSelectorKind.Pinned, ordinal));

    /// <summary>Accepts any version at or above a floor.</summary>
    /// <param name="floor">The lowest acceptable ordinal.</param>
    /// <returns>The selector, or a failure.</returns>
    public static Result<VersionSelector> AtLeast(int floor) =>
        floor < 1
            ? Result<VersionSelector>.Failure(
                ConcordatCodes.VersionSelectorInvalid,
                $"A version floor must be at least 1; got {floor}.")
            : Result<VersionSelector>.Success(new VersionSelector(VersionSelectorKind.Range, floor));

    /// <summary>Parses the wire spelling.</summary>
    /// <param name="value"><c>latest</c>, <c>3</c>, or <c>&gt;=2</c>.</param>
    /// <returns>The selector, or a failure.</returns>
    /// <remarks>
    /// The three spellings are protocol (ADR-019) and are deliberately terse, because they are
    /// written by hand into contract definitions far more often than they are generated.
    /// </remarks>
    public static Result<VersionSelector> Parse(string? value)
    {
        var trimmed = value?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return Result<VersionSelector>.Failure(
                ConcordatCodes.VersionSelectorInvalid,
                "A version selector is required: 'latest', an ordinal, or '>=N'.");
        }

        if (string.Equals(trimmed, "latest", StringComparison.OrdinalIgnoreCase))
        {
            return Result<VersionSelector>.Success(Latest);
        }

        if (trimmed.StartsWith(">=", StringComparison.Ordinal))
        {
            return int.TryParse(
                trimmed[2..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var floor)
                ? AtLeast(floor)
                : Result<VersionSelector>.Failure(
                    ConcordatCodes.VersionSelectorInvalid,
                    $"'{trimmed}' is not a valid range. Expected '>=N'.");
        }

        return int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal)
            ? Pinned(ordinal)
            : Result<VersionSelector>.Failure(
                ConcordatCodes.VersionSelectorInvalid,
                $"'{trimmed}' is not a version selector. Expected 'latest', an ordinal, or '>=N'.");
    }

    /// <summary>Whether a version ordinal satisfies this selector.</summary>
    /// <param name="ordinal">The candidate version.</param>
    /// <param name="latestOrdinal">
    /// What the subject's gated pointer currently resolves to, or null when nothing is approved.
    /// </param>
    /// <returns><see langword="true"/> when the binding accepts that version.</returns>
    public bool Accepts(int ordinal, int? latestOrdinal) => Kind switch
    {
        VersionSelectorKind.Latest => latestOrdinal == ordinal,
        VersionSelectorKind.Pinned => Ordinal == ordinal,
        VersionSelectorKind.Range => ordinal >= Ordinal,
        _ => false,
    };

    /// <inheritdoc />
    public override string ToString() => Kind switch
    {
        VersionSelectorKind.Latest => "latest",
        VersionSelectorKind.Pinned => Ordinal!.Value.ToString(CultureInfo.InvariantCulture),
        VersionSelectorKind.Range => $">={Ordinal!.Value.ToString(CultureInfo.InvariantCulture)}",
        _ => "unknown",
    };
}

/// <summary>A subject a binding carries, and which of its versions apply.</summary>
/// <param name="Subject">The subject name.</param>
/// <param name="Selector">Which versions are acceptable.</param>
public sealed record SubjectRef(SubjectName Subject, VersionSelector Selector);
