using System.Text.RegularExpressions;

namespace Concordat.Formats.Json;

/// <summary>
/// What can be said about a regular expression without choosing whose syntax is right (ADR-044).
/// </summary>
/// <remarks>
/// <para>
/// <b>ECMA-262, RE2 and .NET disagree about what a regular expression is, in both directions</b>
/// (ADR-041 measured 78 patterns: 17 refused by all three, and many refused by one or two). A rule
/// that refused whatever .NET cannot parse would turn away schemas that are valid on another SDK, and
/// a rule that refused whatever RE2 cannot would turn away lookahead and <c>\uXXXX</c>, which the
/// specification's own dialect allows. So the registry refuses one thing only: <em>a pattern no
/// engine accepts</em>. Everything else is a warning.
/// </para>
/// <para>
/// <b>That set is measured, not reasoned.</b> <c>scripts/regex-oracle</c> runs a corpus through Go's
/// <c>regexp</c> (RE2), Node's <c>RegExp</c> with and without the <c>u</c> flag (ECMA-262) and Python's
/// <c>re</c>; the tests hold this class to it. For each class of syntax error .NET reports, every
/// instance in the corpus was refused by all of them, which is what puts that class here. The
/// classes that are not here are the ones some engine accepts: <c>a++</c> is a possessive quantifier
/// in Python and Java, <c>\1</c> with no group is an octal escape in a non-<c>u</c> ECMA-262 engine,
/// a lone <c>\c</c> is a literal backslash and a <c>c</c> there, and <c>[a-\d]</c> is allowed
/// without <c>u</c>. Java was not measured (no JDK on the machine); the classes below are errors in
/// <c>java.util.regex</c> by its documented grammar.
/// </para>
/// </remarks>
internal static class RegexSyntax
{
    /// <summary>
    /// Why no engine would accept this pattern, or null when some engine might.
    /// </summary>
    /// <param name="pattern">The expression, as written in the schema.</param>
    /// <returns>A sentence naming the defect, or <see langword="null"/>.</returns>
    /// <remarks>
    /// Null is "not certain", never "valid": a pattern .NET parses, or fails on in a way an engine
    /// accepts, is the portability checker's to warn about and not this class's to refuse.
    /// </remarks>
    public static string? NoEngineAccepts(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        try
        {
            _ = new Regex(pattern);
            return null;
        }
        catch (RegexParseException ex)
        {
            return IsRefusedEverywhere(ex.Error, pattern) ? Describe(ex.Error) : null;
        }
        catch (ArgumentException)
        {
            // Not a parse error this class understands; the portability checker will say so.
            return null;
        }
    }

    private static bool IsRefusedEverywhere(RegexParseError error, string pattern) => error switch
    {
        RegexParseError.InsufficientClosingParentheses => true,
        RegexParseError.InsufficientOpeningParentheses => true,
        RegexParseError.QuantifierAfterNothing => true,
        RegexParseError.ReversedCharacterRange => true,
        RegexParseError.ReversedQuantifierRange => true,
        RegexParseError.UnescapedEndingBackslash => true,

        // .NET reads '[]' and '[^]' as a class that has not closed yet ('[]a]' is a class holding ']'
        // and 'a'). ECMA-262 reads them as the empty class and the class of everything, and accepts
        // them, so a pattern containing either is not certain to be refused.
        RegexParseError.UnterminatedBracket =>
            !pattern.Contains("[]", StringComparison.Ordinal) &&
            !pattern.Contains("[^]", StringComparison.Ordinal),

        _ => false,
    };

    private static string Describe(RegexParseError error) => error switch
    {
        RegexParseError.InsufficientClosingParentheses => "a group is opened and never closed",
        RegexParseError.InsufficientOpeningParentheses => "a ')' closes a group that was never opened",
        RegexParseError.QuantifierAfterNothing => "a quantifier has nothing before it to repeat",
        RegexParseError.ReversedCharacterRange => "a character range runs backwards",
        RegexParseError.ReversedQuantifierRange => "a {n,m} quantifier has its bounds the wrong way round",
        RegexParseError.UnescapedEndingBackslash => "the pattern ends in a lone backslash",
        RegexParseError.UnterminatedBracket => "a character class is opened and never closed",
        _ => "the pattern is not a regular expression",
    };

    /// <summary>
    /// The first construct in a pattern that Go's RE2 cannot compile, or null when there is none.
    /// </summary>
    /// <param name="pattern">An expression that .NET has already parsed.</param>
    /// <returns>A name for the construct, fit to put in a sentence.</returns>
    /// <remarks>
    /// <para>
    /// <b>A scan, and not a substring search</b>, because the old check (<c>Contains("(?=")</c>) took
    /// <c>\(?=</c> for a lookahead and <c>[(?=]</c> too. It walks the pattern once, knows what an
    /// escape and a character class are, and so says nothing about <c>\\u00e9</c> (a backslash and the
    /// text <c>u00e9</c>) where it says something about <c>é</c>.
    /// </para>
    /// <para>
    /// The list is what RE2 refuses and .NET accepts, measured against Go 1.26 by
    /// <c>scripts/regex-oracle</c> and held by <c>RegexCorpusTests</c>. It is not a promise that
    /// everything else compiles in Go: a construct nobody has measured is a gap and not an
    /// approval.
    /// </para>
    /// </remarks>
    public static string? Re2Cannot(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        var inClass = false;
        var i = 0;

        while (i < pattern.Length)
        {
            var c = pattern[i];

            if (c == '\\')
            {
                if (i + 1 >= pattern.Length)
                {
                    return null;
                }

                var name = EscapeRe2Cannot(pattern[i + 1], inClass);
                if (name is not null)
                {
                    return name;
                }

                i += 2;
                continue;
            }

            if (inClass)
            {
                if (c == ']')
                {
                    inClass = false;
                }
                else if (c == '[' && i + 1 < pattern.Length && pattern[i + 1] == ':')
                {
                    var unknown = UnknownPosixClass(pattern, i + 2);
                    if (unknown is not null)
                    {
                        return unknown;
                    }
                }

                i++;
                continue;
            }

            if (c == '[')
            {
                inClass = true;
                i++;

                // A '^' negates, and a ']' straight after the opening is a member, not the end.
                if (i < pattern.Length && pattern[i] == '^')
                {
                    i++;
                }

                if (i < pattern.Length && pattern[i] == ']')
                {
                    i++;
                }

                continue;
            }

            if (c == '(' && i + 2 < pattern.Length && pattern[i + 1] == '?')
            {
                var name = GroupRe2Cannot(pattern, i + 2);
                if (name is not null)
                {
                    return name;
                }
            }

            if (c == '{')
            {
                var name = RepetitionRe2Cannot(pattern, i);
                if (name is not null)
                {
                    return name;
                }
            }

            i++;
        }

        return null;
    }

    private static string? EscapeRe2Cannot(char escaped, bool inClass) => escaped switch
    {
        'u' => "a \\uXXXX escape",
        'c' => "a \\cX control escape",
        'e' => "a \\e escape",
        'Z' => "the \\Z end-of-text anchor",
        'G' => "the \\G anchor",
        'k' => "a named backreference",
        'b' when inClass => "\\b as a backspace inside a character class",
        >= '1' and <= '9' when !inClass => "a backreference",
        _ => null,
    };

    private static string? GroupRe2Cannot(string pattern, int at) => pattern[at] switch
    {
        '=' => "lookahead",
        '!' => "negative lookahead",
        '<' when at + 1 < pattern.Length && pattern[at + 1] == '=' => "lookbehind",
        '<' when at + 1 < pattern.Length && pattern[at + 1] == '!' => "negative lookbehind",
        '#' => "an inline comment",
        '>' => "an atomic group",
        '(' => "a conditional group",
        _ => null,
    };

    /// <summary>The POSIX classes RE2 knows, which .NET does not and reads as literal characters.</summary>
    private static readonly HashSet<string> Re2PosixClasses = new(StringComparer.Ordinal)
    {
        "alnum", "alpha", "ascii", "blank", "cntrl", "digit", "graph", "lower", "print", "punct", "space",
        "upper", "word", "xdigit",
    };

    /// <summary>
    /// A <c>[:name:]</c> inside a class whose name RE2 does not know, which it refuses outright.
    /// </summary>
    /// <remarks>
    /// .NET has no POSIX classes and reads <c>[[:foo:]]</c> as a class of the characters <c>[:fo</c>
    /// followed by a literal <c>]</c>, so it parses. A known name (<c>[[:alpha:]]</c>) compiles in RE2
    /// and means something else there; that disagreement is real and is not this check's, which is only
    /// about what RE2 cannot compile.
    /// </remarks>
    private static string? UnknownPosixClass(string pattern, int nameStart)
    {
        var end = pattern.IndexOf(":]", nameStart, StringComparison.Ordinal);

        if (end < 0)
        {
            return null;
        }

        var name = pattern[nameStart..end].TrimStart('^');

        return name.All(char.IsAsciiLetter) && !Re2PosixClasses.Contains(name)
            ? "an unknown POSIX character class"
            : null;
    }

    /// <summary>RE2 refuses a repetition count above 1000.</summary>
    private const int Re2MaxRepeat = 1000;

    private static string? RepetitionRe2Cannot(string pattern, int open)
    {
        // {n}, {n,} or {n,m}: anything else with a brace is a literal brace, to .NET and to RE2.
        var j = open + 1;
        var first = ReadNumber(pattern, ref j);

        if (first is null)
        {
            return null;
        }

        long? second = first;

        if (j < pattern.Length && pattern[j] == ',')
        {
            j++;
            second = ReadNumber(pattern, ref j);
        }

        if (j >= pattern.Length || pattern[j] != '}')
        {
            return null;
        }

        return first > Re2MaxRepeat || second > Re2MaxRepeat
            ? $"a repetition count over {Re2MaxRepeat}"
            : null;
    }

    private static long? ReadNumber(string pattern, ref int at)
    {
        var start = at;
        long value = 0;

        while (at < pattern.Length && char.IsAsciiDigit(pattern[at]))
        {
            // Saturating, because a count with forty digits is over the limit and not a crash.
            value = Math.Min(value * 10 + (pattern[at] - '0'), long.MaxValue / 20);
            at++;
        }

        return at == start ? null : value;
    }
}
