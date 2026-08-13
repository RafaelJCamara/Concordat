using Concordat.Domain.Registry;
using Concordat.Formats.Abstractions;
using Concordat.Formats.Json;

namespace Concordat.Formats.Json.Tests;

/// <summary>M6.1's interoperable-subset warnings.</summary>
public class PortabilityTests
{
    private static readonly JsonSchemaCanonicalizer Canonicalizer = new();
    private static readonly JsonSchemaPortabilityChecker Checker = new();

    private static IReadOnlyList<PortabilityFinding> Check(string body) =>
        Checker.Check(Canonicalizer.Canonicalize(body).Value);

    [Fact]
    public void Handles_TheJsonFormat() => Assert.Equal(SchemaFormat.Json, Checker.Format);

    [Fact]
    public void AnOrdinarySchema_HasNoFindings() =>
        Assert.Empty(Check(
            """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}"""));

    // ------------------------------------------------------------------ dialect

    [Fact]
    public void AnAbsentDialect_IsNotWarnedAbout() =>
        // 2020-12 is the assumption. Warning here would fire on almost every schema anyone
        // writes, and a warning that always fires is noise that buries the real ones.
        Assert.Empty(Check("""{"type":"object"}"""));

    [Theory]
    [InlineData("https://json-schema.org/draft/2020-12/schema")]
    [InlineData("https://json-schema.org/draft/2020-12/schema#")]
    public void TheSupportedDialect_IsAccepted(string dialect) =>
        Assert.Empty(Check($$"""{"$schema":"{{dialect}}","type":"object"}"""));

    [Fact]
    public void AnOlderDraft_IsAnError()
    {
        var finding = Assert.Single(Check(
            """{"$schema":"http://json-schema.org/draft-07/schema#","type":"object"}"""));

        Assert.Equal(PortabilityKinds.DialectUnsupported, finding.Kind);
        Assert.Equal(PortabilitySeverity.Error, finding.Severity);
        Assert.Equal("#/$schema", finding.Path);
        Assert.Contains("2020-12", finding.Message, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------- keywords

    [Theory]
    [InlineData("oneOf")]
    [InlineData("anyOf")]
    [InlineData("allOf")]
    [InlineData("if")]
    [InlineData("dependentRequired")]
    [InlineData("patternProperties")]
    [InlineData("prefixItems")]
    [InlineData("unevaluatedProperties")]
    public void AKeywordTheEngineDoesNotCompare_Warns(string keyword)
    {
        var findings = Check($$$"""{"type":"object","{{{keyword}}}":{}}""");

        var finding = Assert.Single(findings, f => f.Kind == PortabilityKinds.KeywordNotCompared);
        Assert.Equal(PortabilitySeverity.Warning, finding.Severity);
        Assert.Contains(keyword, finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWarning_SaysWhatItCosts() =>
        // The point is not "we do not support this" but "a change here reads as compatible
        // when it is not". A warning nobody can act on gets suppressed.
        Assert.Contains(
            "reported as compatible",
            Assert.Single(Check("""{"oneOf":[]}""")).Message,
            StringComparison.Ordinal);

    [Fact]
    public void AKeywordNestedInsideAProperty_IsFoundAndPathed()
    {
        var finding = Assert.Single(Check(
            """{"type":"object","properties":{"a":{"oneOf":[{"type":"string"}]}}}"""));

        Assert.Equal("#/properties/a/oneOf", finding.Path);
    }

    [Fact]
    public void APropertyNamedLikeAKeyword_IsStillReported_AndThatIsAccepted()
    {
        // A property literally named "if" produces a finding at #/properties/if. Distinguishing
        // a keyword from a property name means tracking schema position through every
        // applicator, and the false positive is one warning on an unusual name - far cheaper
        // than missing a real 'if' somewhere the tracking got wrong.
        var findings = Check("""{"type":"object","properties":{"if":{"type":"string"}}}""");

        Assert.Single(findings);
    }

    // -------------------------------------------------------------------- regex

    [Theory]
    [InlineData("^(?=.*[A-Z]).+$", "lookahead")]
    [InlineData("^(?!admin).+$", "negative lookahead")]
    [InlineData("(?<=x)y", "lookbehind")]
    public void ARegexRE2CannotCompile_Warns(string pattern, string construct)
    {
        // The sharpest real divergence in the set: Go's validator is built on RE2, which has no
        // lookaround at all, so this is not "behaves differently" but "fails to build".
        var finding = Assert.Single(Check($$"""{"type":"string","pattern":"{{pattern}}"}"""));

        Assert.Equal(PortabilityKinds.RegexNotPortable, finding.Kind);
        Assert.Contains(construct, finding.Message, StringComparison.Ordinal);
        Assert.Contains("RE2", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOrdinaryRegex_DoesNotWarn() =>
        Assert.Empty(Check("""{"type":"string","pattern":"^[a-z0-9_.]+$"}"""));

    [Fact]
    public void ABackreference_Warns() =>
        Assert.Equal(
            PortabilityKinds.RegexNotPortable,
            Assert.Single(Check("""{"type":"string","pattern":"(a)\\1"}""")).Kind);

    [Fact]
    public void PatternPropertiesKeys_AreCheckedAsRegexes()
    {
        // Both findings are correct here: patternProperties is not compared, and its key is not
        // portable. They are different problems with different fixes.
        var findings = Check("""{"patternProperties":{"^(?=x).*$":{"type":"string"}}}""");

        Assert.Contains(findings, f => f.Kind == PortabilityKinds.KeywordNotCompared);
        Assert.Contains(findings, f => f.Kind == PortabilityKinds.RegexNotPortable);
    }

    // ------------------------------------------------------------------ plumbing

    [Fact]
    public void MalformedInput_ReportsNothing() =>
        // The canonicaliser already refused it with a better message. Saying so twice in
        // different words only makes the real one harder to find.
        Assert.Empty(Checker.Check("not json"));

    [Fact]
    public void FindingsAreOrderedByPath()
    {
        var findings = Check(
            """{"properties":{"z":{"oneOf":[]},"a":{"allOf":[]}},"type":"object"}""");

        Assert.Equal(
            findings.Select(f => f.Path).OrderBy(p => p, StringComparer.Ordinal),
            findings.Select(f => f.Path));
    }
}
