using System.Security.Cryptography;
using System.Text;
using Concordat.Formats.Json;

namespace Concordat.Formats.Json.Tests;

/// <summary>
/// The interpreter's own guarantees: it reads exactly the specification's files, and it refuses
/// to run a rule it does not implement (ADR-039).
/// </summary>
public class MetaschemaValidatorTests
{
    /// <summary>
    /// SHA-256 of each embedded file, line endings as LF. Pinned so that changing the
    /// specification's text is a visible decision with a diff, not an edit that any other test
    /// would notice only if it happened to exercise the changed rule.
    /// </summary>
    private static readonly Dictionary<string, string> Pinned = new(StringComparer.Ordinal)
    {
        ["applicator.json"] = "bf273b26f9f735b93ece78f2b61b36676e1d122ce78ab37ad5a2e45dfa1ca2b1",
        ["content.json"] = "a10456605b2b5bb12a1b4dcfc0300f02f54d3e8bb3646bed7724583866627682",
        ["core.json"] = "21f79d143fab1f180245c331e5657057045b36794d41fe151e6e4fed65035299",
        ["format-annotation.json"] = "5c79404f831dd905c0f40fefac7c6f3e51bf3729b4a876a5c2020178d97f3bcc",
        ["meta-data.json"] = "c664d438a84d58889c8edecd248ce2f945a4bc0e3b087323b11303dc136abfbe",
        ["schema.json"] = "41da76f5afb7ce062d248f762463a92f7ca47e4e0f905b224ba6afeef91ded0f",
        ["unevaluated.json"] = "fc99f32188da41689a9382af174dd42e8b255e4374965c157b8286556b4ab2bc",
        ["validation.json"] = "e921c5b79264d3689af01c1af1ffdf692e09f1c45df90a0f08eb7288c9acdeab",
    };

    private static string Hash(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n", StringComparison.Ordinal))));

    [Fact]
    public void TheEmbeddedFiles_AreTheEightThatWereReviewed()
    {
        var documents = Draft202012Metaschema.Documents();

        Assert.Equal(Pinned.Keys.Order(), documents.Keys.Order());

        foreach (var (name, text) in documents)
        {
            Assert.True(Pinned[name] == Hash(text), $"{name} is not the file that was reviewed.");
        }
    }

    [Fact]
    public void EveryEmbeddedFile_IsItselfAValidSchema()
    {
        // The meta-schema is a schema, so the checker must accept the files it is built from. It is
        // also the broadest valid document available: $dynamicRef, $vocabulary, $dynamicAnchor,
        // $comment, deprecated and every applicator in one place.
        var checker = new JsonSchemaValidityChecker();
        var canonicalizer = new JsonSchemaCanonicalizer();

        foreach (var (name, text) in Draft202012Metaschema.Documents())
        {
            var canonical = canonicalizer.Canonicalize(text);
            Assert.True(canonical.IsSuccess, name);
            Assert.True(checker.Check(canonical.Value).Count == 0,
                $"{name}: {string.Join("; ", checker.Check(canonical.Value).Select(d => d.Path + " " + d.Message))}");
        }
    }

    [Fact]
    public void TheMetaschemaIsLoadedOnce_AndSharedByEveryCall() =>
        Assert.Same(Draft202012Metaschema.Validator, Draft202012Metaschema.Validator);

    // ----------------------------------------------- a rule that is not implemented

    private const string Root = "https://example.com/meta";

    private static MetaschemaValidator Over(string body) =>
        new(["{\"$id\":\"" + Root + "\",\"type\":\"object\",\"properties\":{\"a\":{" + body + "}}}"], Root);

    [Theory]
    [InlineData("\"minLength\":1", "minLength")]
    [InlineData("\"required\":[\"x\"]", "required")]
    [InlineData("\"oneOf\":[true]", "oneOf")]
    [InlineData("\"not\":true", "not")]
    [InlineData("\"if\":true", "if")]
    [InlineData("\"const\":1", "const")]
    [InlineData("\"patternProperties\":{}", "patternProperties")]
    [InlineData("\"maximum\":1", "maximum")]
    [InlineData("\"prefixItems\":[]", "prefixItems")]
    [InlineData("\"unevaluatedProperties\":false", "unevaluatedProperties")]
    [InlineData("\"properties\":{\"b\":{\"maxItems\":1}}", "maxItems")]
    [InlineData("\"anyOf\":[{\"dependentRequired\":{}}]", "dependentRequired")]
    public void AKeywordThatIsNotImplemented_FailsWhenTheValidatorIsBuilt(string body, string keyword)
    {
        // The alternative is the quiet one: the file's rule is skipped, every document passes it,
        // and nothing says so.
        var error = Assert.Throws<NotSupportedException>(() => Over(body));

        Assert.Contains($"'{keyword}'", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"$dynamicRef\":\"#other\"")]
    [InlineData("\"enum\":[1]")]
    [InlineData("\"minimum\":5")]
    [InlineData("\"exclusiveMinimum\":\"0\"")]
    [InlineData("\"type\":\"nonsense\"")]
    [InlineData("\"type\":[\"string\",\"nonsense\"]")]
    [InlineData("\"uniqueItems\":\"yes\"")]
    public void AnImplementedKeywordWithAValueThatIsNotImplemented_FailsToo(string body) =>
        Assert.Throws<NotSupportedException>(() => Over(body));

    [Fact]
    public void AReferenceToNothing_FailsWhenTheValidatorIsBuilt()
    {
        Assert.Throws<InvalidOperationException>(() => Over("\"$ref\":\"#/$defs/missing\""));
        Assert.Throws<InvalidOperationException>(() => Over("\"$ref\":\"https://example.com/not-supplied\""));
    }

    [Fact]
    public void ARootThatWasNotSupplied_FailsWhenTheValidatorIsBuilt() =>
        Assert.Throws<InvalidOperationException>(
            () => new MetaschemaValidator(["""{"$id":"https://example.com/a"}"""], Root));

    [Fact]
    public void AKeywordInsideADefinition_IsCheckedEvenWhenNothingReachesIt() =>
        // $defs members are only ever reached through a $ref, so a walk that skipped them would
        // never notice an unsupported keyword in the one place a $ref sends every document.
        Assert.Throws<NotSupportedException>(() => new MetaschemaValidator(
            ["{\"$id\":\"" + Root + "\",\"$defs\":{\"x\":{\"maxLength\":1}},\"properties\":{\"a\":{\"$ref\":\"#/$defs/x\"}}}"],
            Root));

    // ----------------------------------------------------------------- the numbers

    [Theory]
    [InlineData("0", true)]
    [InlineData("-0", true)]
    [InlineData("0.0", true)]
    [InlineData("1", true)]
    [InlineData("-7", true)]
    [InlineData("1.0", true)]
    [InlineData("1.000", true)]
    [InlineData("10e-1", true)]
    [InlineData("1e1", true)]
    [InlineData("1E+2", true)]
    [InlineData("1.5e1", true)]
    [InlineData("0.25e2", true)]
    [InlineData("12345678901234567890", true)]
    [InlineData("1e400", true)]
    [InlineData("1.5", false)]
    [InlineData("-1.5", false)]
    [InlineData("0.5", false)]
    [InlineData("1e-1", false)]
    [InlineData("15e-1", false)]
    [InlineData("1.05e1", false)]
    [InlineData("1e-400", false)]
    [InlineData("0.000001", false)]
    [InlineData("1e-99999999999999999999", false)]
    [InlineData("1e99999999999999999999", true)]
    public void AnIntegerIsANumberWithAZeroFractionalPart(string number, bool expected) =>
        Assert.Equal(expected, MetaschemaValidator.IsIntegral(number));

    [Theory]
    [InlineData("0", 0)]
    [InlineData("-0", 0)]
    [InlineData("0.000", 0)]
    [InlineData("-0.0e5", 0)]
    [InlineData("1", 1)]
    [InlineData("0.001", 1)]
    [InlineData("1e-400", 1)]
    [InlineData("5E+3", 1)]
    [InlineData("-1", -1)]
    [InlineData("-0.001", -1)]
    [InlineData("-1e-400", -1)]
    public void TheSignOfANumberIgnoresHowItIsWritten(string number, int expected) =>
        Assert.Equal(expected, MetaschemaValidator.NumberSign(number));
}
