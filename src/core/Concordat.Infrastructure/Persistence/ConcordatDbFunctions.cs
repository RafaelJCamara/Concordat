namespace Concordat.Infrastructure.Persistence;

/// <summary>
/// Database functions a query may call, for the things EF Core cannot translate on its own.
/// </summary>
/// <remarks>
/// Each method here has no body that runs: it exists so a LINQ query can name the function, and
/// <see cref="ConcordatDbContext"/> maps it to the SQL one (<c>HasDbFunction</c>). Calling one
/// outside a query is a bug, and says so.
/// </remarks>
internal static class ConcordatDbFunctions
{
    /// <summary>
    /// Whether a subject's name begins with a prefix: PostgreSQL's <c>starts_with</c> (ADR-038).
    /// </summary>
    /// <param name="name">
    /// The column, as <c>EF.Property&lt;string&gt;(s, nameof(Subject.Name))</c>. <b>Not</b>
    /// <c>s.Name</c>: that is a <see cref="Concordat.Domain.Registry.SubjectName"/>, which has no
    /// store mapping of its own, and EF refuses a function parameter it cannot map.
    /// </param>
    /// <param name="prefix">The prefix, as a literal. It is never a pattern.</param>
    /// <returns>Never evaluated in memory.</returns>
    /// <remarks>
    /// <para>
    /// <b>Why this exists at all.</b> <c>Subject.Name</c> is a <c>SubjectName</c> held in a column
    /// through a value converter, and EF Core does not translate <c>StartsWith</c> through one.
    /// Measured, not assumed: <c>EF.Property&lt;string&gt;(s, "Name").StartsWith(p)</c> translates
    /// to a <c>LIKE</c> whose pattern constant inherits the <em>column's</em> mapping, so SQL
    /// generation tries to convert the string <c>p</c> to a <c>SubjectName</c> and throws
    /// <c>InvalidCastException</c> (and a <c>SubjectName</c> could not hold a half-typed prefix
    /// like <c>acme.</c> anyway). A function whose parameters are plain strings has no such
    /// mapping to inherit. The alternatives were a complex-type mapping of the name, which
    /// changes how every query and index that names the column is built, and a second shadow
    /// column that must be kept in step with the first; a mapped function changes neither the
    /// model nor the schema.
    /// </para>
    /// <para>
    /// <b>Why <c>starts_with</c> and not <c>LIKE</c>.</b> LIKE needs the prefix escaped, and the
    /// character that needs it most (<c>_</c>) is a legal name character, so a missed escape is
    /// not a theoretical bug: <c>a_</c> would find <c>ab</c>. <c>starts_with</c> takes a literal
    /// and has no metacharacters to forget. It is bytewise on a deterministic collation, which is
    /// what makes it ordinal and case-sensitive, as names are. Its plan is the same as LIKE's
    /// with or without a supporting index (measured, ADR-038).
    /// </para>
    /// </remarks>
    public static bool StartsWith(string name, string prefix) =>
        throw new NotSupportedException(
            "ConcordatDbFunctions.StartsWith is translated to SQL and cannot be called in memory.");
}
