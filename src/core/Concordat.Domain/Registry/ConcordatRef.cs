using System.Globalization;
using Concordat.Domain.Results;

namespace Concordat.Domain.Registry;

/// <summary>
/// A reference to another subject's version, in the form
/// <c>concordat://&lt;environment&gt;/&lt;subject&gt;/&lt;version&gt;</c>.
/// </summary>
/// <param name="Environment">The environment name the target lives in.</param>
/// <param name="Subject">The referenced subject.</param>
/// <param name="Version">The referenced version ordinal.</param>
/// <remarks>
/// This is the spelling a JSON Schema <c>$ref</c> uses to point at another registered subject
/// (DESIGN §4). It is normative protocol under ADR-019: it appears inside schema documents
/// that clients in every language must be able to read.
/// </remarks>
public sealed record ConcordatRef(string Environment, SubjectName Subject, int Version)
{
    /// <summary>The URI scheme, without the separator.</summary>
    public const string Scheme = "concordat";

    /// <summary>Attempts to parse a reference URI.</summary>
    /// <param name="value">Candidate text.</param>
    /// <returns>
    /// The parsed reference, or a failure carrying
    /// <see cref="ConcordatCodes.ReferenceInvalid"/>. A value that is simply not a
    /// <c>concordat://</c> URI — an ordinary local <c>$ref</c> such as
    /// <c>#/$defs/Address</c>, or an HTTP URL — fails here too; callers distinguish
    /// "not ours" from "ours but malformed" with <see cref="IsConcordatRef(string?)"/>.
    /// </returns>
    public static Result<ConcordatRef> Create(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Result<ConcordatRef>.Failure(
                ConcordatCodes.ReferenceInvalid, "A reference is required.");
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Scheme, StringComparison.Ordinal))
        {
            return Result<ConcordatRef>.Failure(
                ConcordatCodes.ReferenceInvalid,
                $"'{value}' is not a {Scheme}:// reference.");
        }

        var environment = uri.Host;
        if (string.IsNullOrEmpty(environment))
        {
            return Result<ConcordatRef>.Failure(
                ConcordatCodes.ReferenceInvalid,
                $"'{value}' has no environment. Expected {Scheme}://<env>/<subject>/<version>.");
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2)
        {
            return Result<ConcordatRef>.Failure(
                ConcordatCodes.ReferenceInvalid,
                $"'{value}' must be {Scheme}://<env>/<subject>/<version>.");
        }

        var subject = SubjectName.Create(Uri.UnescapeDataString(segments[0]));
        if (subject.IsFailure)
        {
            return Result<ConcordatRef>.Failure(
                ConcordatCodes.ReferenceInvalid,
                $"'{value}' has an invalid subject: {subject.Error!.Message}");
        }

        if (!int.TryParse(segments[1], NumberStyles.None, CultureInfo.InvariantCulture, out var version) ||
            version < 1)
        {
            return Result<ConcordatRef>.Failure(
                ConcordatCodes.ReferenceInvalid,
                $"'{value}' must end in a version ordinal of at least 1.");
        }

        return Result<ConcordatRef>.Success(
            new ConcordatRef(environment, subject.Value, version));
    }

    /// <summary>Whether the value looks like a Concordat reference at all.</summary>
    /// <param name="value">Candidate text.</param>
    /// <returns>
    /// <see langword="true"/> when the value carries the <c>concordat</c> scheme, regardless
    /// of whether the rest parses. Lets a caller ignore local and HTTP <c>$ref</c>s while
    /// still reporting a malformed Concordat one instead of silently skipping it.
    /// </returns>
    public static bool IsConcordatRef(string? value) =>
        value is not null &&
        value.TrimStart().StartsWith($"{Scheme}://", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override string ToString() =>
        $"{Scheme}://{Environment}/{Subject.Value}/{Version.ToString(CultureInfo.InvariantCulture)}";
}
