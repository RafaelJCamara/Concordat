using System.Text.RegularExpressions;
using Concordat.Domain.Results;

namespace Concordat.Domain.Registry;

/// <summary>
/// An environment's name, as it appears in <c>/v1/environments/{env}/…</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The name is in every URL, so its grammar is protocol</b> (ADR-019). It is deliberately
/// narrower than a subject name: lowercase letters, digits and hyphens only. Environments are
/// operational labels — <c>dev</c>, <c>staging</c>, <c>prod</c>, <c>eu-west</c> — not
/// namespaced identifiers, and keeping the set small means a name never needs escaping in a
/// path segment, a header, or a shell command an operator types.
/// </para>
/// <para>
/// <b>Case is folded, not preserved.</b> This is the opposite of
/// <see cref="SubjectName"/>, and the reason is the difference in what they name: a subject
/// name comes from a message type, where <c>OrderCreated</c> and <c>ordercreated</c> are
/// genuinely different types in most languages. An environment name is typed by a human into a
/// pipeline variable, and <c>PROD</c> meaning something other than <c>prod</c> is a trap with
/// no upside.
/// </para>
/// </remarks>
public sealed partial record EnvironmentName
{
    /// <summary>The longest permitted name.</summary>
    /// <remarks>
    /// Generous for the purpose. The ceiling exists so the column is bounded, not because
    /// anyone should approach it.
    /// </remarks>
    public const int MaxLength = 64;

    private EnvironmentName(string value) => Value = value;

    /// <summary>The canonical, lowercase name.</summary>
    public string Value { get; }

    /// <summary>Validates and normalises an environment name.</summary>
    /// <param name="value">The name as supplied.</param>
    /// <returns>
    /// The name, or a failure carrying <see cref="ConcordatCodes.EnvironmentNameInvalid"/>.
    /// </returns>
    public static Result<EnvironmentName> Create(string? value)
    {
        var trimmed = value?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return Result<EnvironmentName>.Failure(
                ConcordatCodes.EnvironmentNameInvalid, "An environment name is required.");
        }

        if (trimmed.Length > MaxLength)
        {
            return Result<EnvironmentName>.Failure(
                ConcordatCodes.EnvironmentNameInvalid,
                $"An environment name may be at most {MaxLength} characters; got {trimmed.Length}.");
        }

        var folded = trimmed.ToLowerInvariant();

        if (!Grammar().IsMatch(folded))
        {
            return Result<EnvironmentName>.Failure(
                ConcordatCodes.EnvironmentNameInvalid,
                $"'{trimmed}' is not a valid environment name. Use lowercase letters, digits " +
                "and hyphens, starting and ending with a letter or digit — for example " +
                "'prod' or 'eu-west'.");
        }

        return Result<EnvironmentName>.Success(new EnvironmentName(folded));
    }

    /// <summary>Creates a name already known to be valid.</summary>
    /// <param name="value">A previously validated name.</param>
    /// <returns>The name.</returns>
    /// <remarks>For rehydration from storage, which cannot contain an invalid name.</remarks>
    public static EnvironmentName FromTrusted(string value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value;

    // Anchored, and no consecutive hyphens: 'eu--west' and 'eu-west' reading as different
    // environments would be a support call, not a feature.
    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex Grammar();
}
