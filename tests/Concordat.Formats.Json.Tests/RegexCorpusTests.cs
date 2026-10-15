using System.Text.Json;
using System.Text.RegularExpressions;

namespace Concordat.Formats.Json.Tests;

/// <summary>
/// The two rules in <see cref="RegexSyntax"/> held to what the engines actually say (ADR-044).
/// </summary>
/// <remarks>
/// <para>
/// <c>RegexCorpus/engines.json</c> is written by <c>scripts/regex-oracle/measure.mjs</c>: for each of
/// a few hundred patterns, whether Go's <c>regexp</c> (RE2), Node's <c>RegExp</c> (with and without the
/// <c>u</c> flag) and Python's <c>re</c> compile it. .NET is not in the file; it is asked here, at run
/// time, because it is the engine under test. Re-measure with <c>node scripts/regex-oracle/measure.mjs</c>,
/// and <c>--check</c> says whether the committed file still matches the installed engines.
/// </para>
/// <para>
/// These are tests of a claim ("every engine refuses it", "RE2 cannot compile it") against a
/// measurement, so a rule that drifts from the engines fails here instead of being believed.
/// </para>
/// </remarks>
public class RegexCorpusTests
{
    private sealed record Entry(string Pattern, bool Re2, bool EcmaU, bool EcmaLegacy, bool Python);

    private static readonly Lazy<IReadOnlyList<Entry>> Corpus = new(() =>
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "RegexCorpus", "engines.json"));
        using var document = JsonDocument.Parse(stream);

        return (IReadOnlyList<Entry>)[.. document.RootElement.GetProperty("patterns").EnumerateArray().Select(e => new Entry(
            e.GetProperty("pattern").GetString()!,
            e.GetProperty("re2").GetBoolean(),
            e.GetProperty("ecmaU").GetBoolean(),
            e.GetProperty("ecmaLegacy").GetBoolean(),
            e.GetProperty("python").GetBoolean()))];
    });

    private static bool DotNetParses(string pattern)
    {
        try
        {
            _ = new Regex(pattern);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [Fact]
    public void TheCorpusIsNotEmptyAndNamesTheEnginesItWasMeasuredWith()
    {
        Assert.True(Corpus.Value.Count > 100);
        Assert.Contains(Corpus.Value, e => e.Pattern == "(");
    }

    // ------------------------------------------------------------ the refusal: "no engine accepts it"

    [Fact]
    public void EveryPatternTheRegistryWouldRefuse_IsRefusedByEveryEngineMeasured()
    {
        var wrong = Corpus.Value
            .Where(e => RegexSyntax.NoEngineAccepts(e.Pattern) is not null)
            .Where(e => e.Re2 || e.EcmaU || e.EcmaLegacy || e.Python)
            .Select(e => $"{e.Pattern}  re2={e.Re2} ecma(u)={e.EcmaU} ecma={e.EcmaLegacy} python={e.Python}")
            .ToList();

        Assert.True(wrong.Count == 0, "Refused although some engine accepts it:\n" + string.Join('\n', wrong));
    }

    [Fact]
    public void TheRefusalCoversTheSyntaxErrorsEveryEngineReports()
    {
        // Not a theorem, a floor: the refusal is only worth having if it catches the plain mistakes. These
        // are refused by .NET, ECMA-262 (with and without u), RE2 and Python in the corpus, and each must
        // be refused here.
        var certain = Corpus.Value
            .Where(e => !DotNetParses(e.Pattern) && !e.Re2 && !e.EcmaU && !e.EcmaLegacy && !e.Python)
            .Select(e => e.Pattern)
            .ToList();

        Assert.NotEmpty(certain);

        // What .NET refuses and every engine refuses, that this class declines to call certain: the
        // classes of error whose every corpus instance is refused today but whose class also covers
        // something an engine accepts (so the class is left alone). Named, so adding to it is a decision.
        var knownNotRefused = new HashSet<string>(StringComparer.Ordinal)
        {
            "(?x", "(?", "(?<n", "(?#c", "(?(1)a|b)", "(?<1a>x)", "(?<>x)", "\\8", "\\2", "\\1", "\\N", "\\h", "\\R", "\\X",
            "\\C", "\\Q.\\E", "\\p{Foo}", "\\u12", "\\x4", "\\x{41}", "(?z)a", "(?U)a", "a{2}{3}", "a**", "[a-\\d]",
            "\\p{Greek}", "\\P{Greek}", "\\c", "a++", "a*+", "a?+", "(?P<n>a)", "\\u{1F600}", "\\p{Letter}",
        };

        var missed = certain
            .Where(p => RegexSyntax.NoEngineAccepts(p) is null && !knownNotRefused.Contains(p))
            .ToList();

        Assert.True(missed.Count == 0, "Refused by every engine and not by the registry:\n" + string.Join('\n', missed));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[^]")]
    [InlineData("a[]b")]
    public void AnEmptyClassIsNotRefused_BecauseEcma262AcceptsIt(string pattern)
    {
        // .NET reports these as an unterminated bracket; ECMA-262 reads the empty class and the class of
        // everything. Refusing would turn away a schema a JavaScript consumer compiles.
        Assert.Null(RegexSyntax.NoEngineAccepts(pattern));
        Assert.Contains(Corpus.Value, e => e.Pattern == pattern && (e.EcmaU && e.EcmaLegacy));
    }

    [Theory]
    [InlineData("a++")]
    [InlineData("a*+")]
    [InlineData("(?i)a")]
    [InlineData("\\p{Letter}")]
    [InlineData("\\1")]
    [InlineData("a{2}{3}")]
    public void WhatSomeEngineAcceptsIsNotRefused(string pattern) =>
        Assert.Null(RegexSyntax.NoEngineAccepts(pattern));

    [Theory]
    [InlineData("(")]
    [InlineData("a)")]
    [InlineData("*a")]
    [InlineData("[z-a]")]
    [InlineData("a{3,2}")]
    [InlineData("a\\")]
    [InlineData("[a")]
    public void WhatNoEngineAcceptsIsRefused_AndSaysWhy(string pattern) =>
        Assert.False(string.IsNullOrWhiteSpace(RegexSyntax.NoEngineAccepts(pattern)));

    // ------------------------------------------------------------ the warning: "RE2 cannot compile it"

    [Fact]
    public void WhatTheScannerCallsUnportable_IsWhatGoRefuses_ForEveryPatternDotNetParses()
    {
        var wrong = Corpus.Value
            .Where(e => DotNetParses(e.Pattern))
            .Where(e => (RegexSyntax.Re2Cannot(e.Pattern) is not null) == e.Re2)
            .Select(e => $"{e.Pattern}  scanner={RegexSyntax.Re2Cannot(e.Pattern) ?? "nothing"}  go-compiles={e.Re2}")
            .ToList();

        Assert.True(
            wrong.Count == 0,
            "The scanner and Go disagree (the scanner names a construct exactly when Go refuses):\n" +
            string.Join('\n', wrong));
    }

    [Theory]
    [InlineData("\\u00e9", "\\uXXXX")]
    [InlineData("[\\u4e00-\\u9fff]", "\\uXXXX")]
    [InlineData("\\cA", "control escape")]
    [InlineData("\\e", "\\e")]
    [InlineData("\\Z", "end-of-text")]
    [InlineData("\\G", "\\G")]
    [InlineData("[\\b]", "backspace")]
    [InlineData("(a)\\1", "backreference")]
    [InlineData("(?<n>a)\\k<n>", "named backreference")]
    [InlineData("(?#c)a", "comment")]
    [InlineData("(?>a)", "atomic")]
    [InlineData("(?(a)b|c)", "conditional")]
    [InlineData("(?=a)", "lookahead")]
    [InlineData("(?!a)", "negative lookahead")]
    [InlineData("\\\\(?=a)", "lookahead")]
    [InlineData("[[:foo:]]", "POSIX")]
    [InlineData("[[:^foo:]]", "POSIX")]
    [InlineData("(?<=a)", "lookbehind")]
    [InlineData("(?<!a)", "negative lookbehind")]
    [InlineData("x{1001}", "repetition count")]
    [InlineData("a{1,100000}", "repetition count")]
    public void AConstructRe2CannotCompile_IsNamed(string pattern, string name)
    {
        Assert.Contains(name, RegexSyntax.Re2Cannot(pattern), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\\\\u00e9")]
    [InlineData("\\\\1")]
    [InlineData("\\\\Z")]
    [InlineData("\\(?=a")]
    [InlineData("[(?=a]")]
    [InlineData("[(?<=]")]
    [InlineData("[\\\\1]")]
    [InlineData("[\\\\b]")]
    [InlineData("a{1000}")]
    [InlineData("a{0,1000}")]
    [InlineData("\\{2000\\}")]
    [InlineData("[[:alpha:]]")]
    [InlineData("[[:^digit:]]")]
    [InlineData("[[:")]
    [InlineData("[[:ab")]
    [InlineData("a{2000")]
    [InlineData("a{x,2000}")]
    [InlineData("^[a-z0-9_.]+$")]
    [InlineData("\\d{3}-\\d{4}")]
    public void WhatLooksLikeAnUnportableConstructButIsNot_IsLeftAlone(string pattern) =>
        Assert.Null(RegexSyntax.Re2Cannot(pattern));

    [Fact]
    public void AClassThatStartsWithABracketMember_DoesNotEndEarly()
    {
        // '[]\u0041]' is a class holding ']' and the escape; the scanner must reach the escape inside it.
        Assert.Contains("\\uXXXX", RegexSyntax.Re2Cannot("[]\\u0041]"), StringComparison.Ordinal);
    }

    [Fact]
    public void ARepetitionCountWithFortyDigits_IsOverTheLimitAndNotACrash() =>
        Assert.Contains(
            "repetition count",
            RegexSyntax.Re2Cannot("a{" + new string('9', 40) + "}"),
            StringComparison.Ordinal);
}
