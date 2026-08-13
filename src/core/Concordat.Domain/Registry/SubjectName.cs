using System.Text.RegularExpressions;
using Concordat.Domain.Results;

namespace Concordat.Domain.Registry;

/// <summary>
/// A subject name: dot-separated segments of letters, digits and underscores (ADR-011).
/// </summary>
/// <remarks>
/// Deliberately not a CLR type name. The grammar carries no language's type system, so a
/// Python publisher writing <c>acme.orders.OrderCreated</c> is a first-class citizen
/// (ADR-019). Framework separators and assembly qualification are stripped client-side before
/// a name reaches the registry.
/// </remarks>
public sealed partial record SubjectName
{
    /// <summary>The longest permitted subject name, in characters.</summary>
    public const int MaxLength = 512;

    private SubjectName(string value) => Value = value;

    /// <summary>The canonical name.</summary>
    public string Value { get; }

    /// <summary>Parses and validates a subject name.</summary>
    /// <param name="value">Candidate text. Surrounding whitespace is trimmed before validation.</param>
    /// <returns>
    /// The name, or a failure carrying <see cref="ConcordatCodes.SubjectNameInvalid"/>.
    /// </returns>
    public static Result<SubjectName> Create(string? value)
    {
        var trimmed = value?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return Result<SubjectName>.Failure(
                ConcordatCodes.SubjectNameInvalid, "A subject name is required.");
        }

        if (trimmed.Length > MaxLength)
        {
            return Result<SubjectName>.Failure(
                ConcordatCodes.SubjectNameInvalid,
                $"A subject name may be at most {MaxLength} characters; got {trimmed.Length}.");
        }

        if (!NamePattern().IsMatch(trimmed))
        {
            return Result<SubjectName>.Failure(
                ConcordatCodes.SubjectNameInvalid,
                $"'{trimmed}' is not a valid subject name. Expected dot-separated segments of " +
                "letters, digits and underscores, with no leading, trailing or repeated dots.");
        }

        return Result<SubjectName>.Success(new SubjectName(trimmed));
    }

    /// <inheritdoc />
    public override string ToString() => Value;

    [GeneratedRegex(@"^[A-Za-z0-9_]+(\.[A-Za-z0-9_]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
