using System.Text.RegularExpressions;
using Concordat.Domain.Results;

namespace Concordat.Domain.Registry;

/// <summary>
/// The start of a subject name, as a search box or a query string gives it: a valid name, or a
/// valid name and one more dot (ADR-038).
/// </summary>
/// <remarks>
/// <para>
/// <b>The set is exactly the strings that are a prefix of some name <see cref="SubjectName"/>
/// accepts</b>, so a prefix this type refuses is one no subject can ever match. That is what lets
/// the handler answer an empty list without asking the database, and it is also what keeps every
/// character that could do harm to a query out of one: <c>%</c> and a backslash (LIKE's wildcard
/// and escape), a NUL (PostgreSQL refuses it in text outright, which would be a 500), and a lone
/// surrogate (which cannot be encoded as UTF-8). It holds <c>_</c>, the one LIKE wildcard that is
/// also a legal name character, which is why the repository does not use LIKE at all.
/// </para>
/// <para>
/// A trailing dot is allowed because <c>acme.orders.</c> is what someone types to mean "the
/// <c>acme.orders</c> namespace", and it is the one prefix that is not itself a name. A prefix is
/// not required to end on a segment boundary otherwise: <c>acme.ord</c> finds
/// <c>acme.orders.OrderCreated</c>, because a box that waits for a whole word is a box that shows
/// nothing while you are still typing it.
/// </para>
/// <para>
/// Case is kept. Names are case-sensitive (<c>OrderCreated</c> and <c>ordercreated</c> are two
/// subjects), so a prefix that folded case would find one while naming the other.
/// </para>
/// </remarks>
public sealed partial record SubjectNamePrefix
{
    private SubjectNamePrefix(string value) => Value = value;

    /// <summary>The prefix, trimmed, with its case as given.</summary>
    public string Value { get; }

    /// <summary>Parses and validates a prefix.</summary>
    /// <param name="value">
    /// Candidate text. Surrounding whitespace is trimmed first, as <see cref="SubjectName.Create"/>
    /// does, so <c>GET /subjects/%20acme</c> and <c>?prefix=%20acme</c> agree.
    /// </param>
    /// <returns>
    /// The prefix, or a failure carrying <see cref="ConcordatCodes.SubjectNameInvalid"/> when no
    /// subject name could begin with it. <b>A blank value fails too:</b> no prefix means no
    /// filter, and the caller says that by not constructing one.
    /// </returns>
    public static Result<SubjectNamePrefix> Create(string? value)
    {
        var trimmed = value?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return Result<SubjectNamePrefix>.Failure(
                ConcordatCodes.SubjectNameInvalid, "A subject name prefix is required.");
        }

        // A name that ends in a dot is not a name, but "acme." is what the next segment will be
        // appended to. One more character must still fit, so it counts against the limit.
        var endsInDot = trimmed[^1] == '.';
        var longest = endsInDot ? SubjectName.MaxLength - 1 : SubjectName.MaxLength;

        if (trimmed.Length > longest || !PrefixPattern().IsMatch(trimmed))
        {
            return Result<SubjectNamePrefix>.Failure(
                ConcordatCodes.SubjectNameInvalid,
                "No subject name can begin with that. A name is dot-separated segments of " +
                "letters, digits and underscores, with no leading or repeated dots.");
        }

        return Result<SubjectNamePrefix>.Success(new SubjectNamePrefix(trimmed));
    }

    /// <inheritdoc />
    public override string ToString() => Value;

    // SubjectName's grammar with one optional trailing dot. \z and not $: in .NET, $ also
    // matches before a final newline, which would admit a prefix with a newline in it.
    [GeneratedRegex(@"^[A-Za-z0-9_]+(\.[A-Za-z0-9_]+)*\.?\z", RegexOptions.CultureInvariant)]
    private static partial Regex PrefixPattern();
}
