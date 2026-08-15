using Concordat.Domain.Registry;
using Concordat.Domain.Results;
using Concordat.Domain.Tests.TestSupport;

namespace Concordat.Domain.Tests;

public class SchemaIdTests
{
    [Fact]
    public void Create_WithCanonicalHex_Succeeds()
    {
        var result = SchemaId.Create("7f3a9c2ea1b84d5c9e07f2b3c4d5e6b4");

        Assert.True(result.IsSuccess);
        Assert.Equal("7f3a9c2ea1b84d5c9e07f2b3c4d5e6b4", result.Value.Value);
    }

    [Theory]
    [InlineData(null, "null")]
    [InlineData("", "empty")]
    [InlineData("   ", "whitespace")]
    [InlineData("7F3A9C2EA1B84D5C9E07F2B3C4D5E6B4", "uppercase")]
    [InlineData("7f3a9c2ea1b84d5c9e07f2b3c4d5e6b", "31 chars")]
    [InlineData("7f3a9c2ea1b84d5c9e07f2b3c4d5e6b44", "33 chars")]
    [InlineData("7f3a9c2ea1b84d5c9e07f2b3c4d5e6bg", "non-hex g")]
    [InlineData("0x3a9c2ea1b84d5c9e07f2b3c4d5e6b4", "0x prefix")]
    public void Create_WithMalformedValue_Fails(string? value, string why)
    {
        var result = SchemaId.Create(value);

        Assert.True(result.IsFailure, why);
        Assert.Equal(ConcordatCodes.SchemaIdMalformed, result.Error!.Code);
    }

    [Fact]
    public void Create_WithTrailingNewline_Fails()
    {
        // Guards the \z anchor: in .NET, $ also matches before a trailing newline, so a
        // $-anchored pattern would accept this and admit two spellings of one id.
        var result = SchemaId.Create("7f3a9c2ea1b84d5c9e07f2b3c4d5e6b4\n");

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.SchemaIdMalformed, result.Error!.Code);
    }

    [Fact]
    public void Equality_IsByValue()
    {
        var a = SchemaId.Create("7f3a9c2ea1b84d5c9e07f2b3c4d5e6b4").Value;
        var b = SchemaId.Create("7f3a9c2ea1b84d5c9e07f2b3c4d5e6b4").Value;

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }
}

public class SubjectNameTests
{
    [Theory]
    [InlineData("acme.orders.OrderCreated")]
    [InlineData("OrderCreated")]
    [InlineData("a_b.c_d")]
    [InlineData("a1.b2.c3")]
    public void Create_WithValidGrammar_Succeeds(string value) =>
        Assert.True(SubjectName.Create(value).IsSuccess);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(".leading")]
    [InlineData("trailing.")]
    [InlineData("double..dot")]
    [InlineData("has space")]
    [InlineData("has-hyphen")]
    [InlineData("Ns+Type")]
    [InlineData("has\ninterior\nnewline")]
    public void Create_WithInvalidGrammar_Fails(string? value)
    {
        var result = SubjectName.Create(value);

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.SubjectNameInvalid, result.Error!.Code);
    }

    [Theory]
    [InlineData("  acme.Order  ")]
    [InlineData("acme.Order\n")]
    [InlineData("\tacme.Order")]
    public void Create_TrimsSurroundingWhitespace(string value)
    {
        // Unlike SchemaId, which rejects a trailing newline outright: a subject name is
        // user-typed, so trimming is a kindness. A content address is not, so two spellings
        // of one id would defeat the point.
        Assert.Equal("acme.Order", SubjectName.Create(value).Value.Value);
    }

    [Fact]
    public void Create_OverMaxLength_Fails()
    {
        var tooLong = new string('a', SubjectName.MaxLength + 1);

        Assert.True(SubjectName.Create(tooLong).IsFailure);
    }
}

public class SemanticVersionTests
{
    [Fact]
    public void Create_WithMajorMinorPatch_Succeeds()
    {
        var result = SemanticVersion.Create("2.1.3");

        Assert.True(result.IsSuccess);
        Assert.Equal(new SemanticVersion(2, 1, 3), result.Value);
    }

    [Theory]
    [InlineData("2.0.0-rc.1", "rc.1")]
    [InlineData("2.0.0-alpha", "alpha")]
    [InlineData("2.0.0-0.3.7", "0.3.7")]
    [InlineData("2.0.0-x-y-z.-", "x-y-z.-")]
    public void Create_WithPrerelease_Parses(string value, string expected)
    {
        // Decision 8. The label parses everywhere; whether an ENVIRONMENT accepts it is policy,
        // decided at registration. Folding the policy into the parser meant a team whose
        // pipeline emits -rc labels could not label a version at all, anywhere, ever.
        var result = SemanticVersion.Create(value);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(expected, result.Value.PreRelease);
        Assert.True(result.Value.IsPreRelease);
        Assert.Equal(value, result.Value.ToString());
    }

    [Theory]
    [InlineData("2.0.0+build.5")]
    [InlineData("2.0.0-rc.1+build.5")]
    public void Create_WithBuildMetadata_FailsWithItsOwnCode(string value)
    {
        // Still refused, and for a reason worth its own code: SemVer ignores build metadata for
        // precedence, so 1.0.0+a and 1.0.0+b compare EQUAL while being different strings -- and
        // this registry requires each label to increase on the last. A grammar that can express
        // something the ordering cannot see is a trap.
        var result = SemanticVersion.Create(value);

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.SemverBuildMetadataUnsupported, result.Error!.Code);
    }

    [Theory]
    [InlineData("2.0.0-")]
    [InlineData("2.0.0-01")]
    [InlineData("2.0.0-rc..1")]
    [InlineData("2.0.0-rc.1$")]
    public void Create_WithAMalformedPrerelease_IsInvalid(string value)
    {
        // '01' is refused because SemVer compares numeric identifiers numerically -- so 01 and 1
        // would be different strings that compare equal, the same trap as build metadata.
        var result = SemanticVersion.Create(value);

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.SemverInvalid, result.Error!.Code);
    }

    [Fact]
    public void APrerelease_PrecedesItsOwnRelease()
    {
        // The rule that makes "each label must increase" do what a release-candidate pipeline
        // expects: rc.1, rc.2, then the release.
        var rc1 = SemanticVersion.Create("2.0.0-rc.1").Value;
        var rc2 = SemanticVersion.Create("2.0.0-rc.2").Value;
        var release = SemanticVersion.Create("2.0.0").Value;

        Assert.True(rc1 < rc2);
        Assert.True(rc2 < release);
        Assert.True(rc1 < release);
    }

    [Fact]
    public void NumericPrereleaseIdentifiers_CompareNumericallyNotAsText()
    {
        // rc.10 follows rc.9. Plain string ordering gets this backwards, and it is exactly the
        // sequence a release-candidate pipeline produces.
        Assert.True(
            SemanticVersion.Create("2.0.0-rc.9").Value < SemanticVersion.Create("2.0.0-rc.10").Value);
    }

    [Fact]
    public void MorePrereleaseIdentifiers_FollowFewer_WhenTheSharedOnesAreEqual() =>
        Assert.True(
            SemanticVersion.Create("2.0.0-rc.1").Value <
            SemanticVersion.Create("2.0.0-rc.1.1").Value);

    [Fact]
    public void ANumericIdentifier_PrecedesAnAlphanumericOne() =>
        Assert.True(
            SemanticVersion.Create("2.0.0-1").Value < SemanticVersion.Create("2.0.0-alpha").Value);

    [Theory]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("a.b.c")]
    [InlineData("-1.0.0")]
    [InlineData("")]
    public void Create_WithMalformedLabel_Fails(string value)
    {
        var result = SemanticVersion.Create(value);

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.SemverInvalid, result.Error!.Code);
    }

    [Fact]
    public void Ordering_IsMajorThenMinorThenPatch()
    {
        Assert.True(new SemanticVersion(1, 0, 0) < new SemanticVersion(2, 0, 0));
        Assert.True(new SemanticVersion(1, 2, 0) > new SemanticVersion(1, 1, 9));
        Assert.True(new SemanticVersion(1, 1, 2) > new SemanticVersion(1, 1, 1));
    }
}

public class SchemaTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithEmptyBody_Fails(string? body)
    {
        var result = Schema.Create(Build.Id(1), SchemaFormat.Json, body);

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.SchemaBodyEmpty, result.Error!.Code);
    }

    [Fact]
    public void Create_WithDuplicateReferenceNames_Fails()
    {
        var a = Reference.Create("common", Build.Name("acme.Common"), 1).Value;
        var b = Reference.Create("common", Build.Name("acme.Other"), 2).Value;

        var result = Schema.Create(Build.Id(1), SchemaFormat.Json, "{}", [a, b]);

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.DuplicateReferenceName, result.Error!.Code);
    }

    [Fact]
    public void Create_OrdersReferencesCanonically()
    {
        // The identity hash covers references (ADR-015), so input order must not change it.
        var z = Reference.Create("zeta", Build.Name("acme.Z"), 1).Value;
        var a = Reference.Create("alpha", Build.Name("acme.A"), 1).Value;

        var schema = Schema.Create(Build.Id(1), SchemaFormat.Json, "{}", [z, a]).Value;

        Assert.Equal(["alpha", "zeta"], schema.References.Select(r => r.Name));
    }

    [Fact]
    public void Create_WithReferenceVersionBelowOne_Fails() =>
        Assert.True(Reference.Create("r", Build.Name("acme.A"), 0).IsFailure);
}

public class CompatibilityTests
{
    [Fact]
    public void Policy_IsAPairOfOrthogonalAxes()
    {
        var wire = new CompatibilityPolicy(CompatibilityMode.Backward, CompatibilitySurface.Wire);
        var source = new CompatibilityPolicy(CompatibilityMode.Backward, CompatibilitySurface.Source);

        Assert.NotEqual(wire, source);
        Assert.Equal(wire.Mode, source.Mode);
    }

    [Fact]
    public void Surface_FormsAWireSubsetWireJsonSubsetSourceLattice()
    {
        var wire = new CompatibilityPolicy(CompatibilityMode.Backward, CompatibilitySurface.Wire);
        var source = new CompatibilityPolicy(CompatibilityMode.Backward, CompatibilitySurface.Source);

        // A wire-level finding violates every policy.
        Assert.True(wire.IsViolatedBy(CompatibilitySurface.Wire));
        Assert.True(source.IsViolatedBy(CompatibilitySurface.Wire));

        // int32 -> int64: source-breaking, wire-safe. This is the distinction a single-axis
        // model cannot express at all (ADR-016).
        Assert.False(wire.IsViolatedBy(CompatibilitySurface.Source));
        Assert.True(source.IsViolatedBy(CompatibilitySurface.Source));
    }

    [Fact]
    public void None_IsNotChecked() =>
        Assert.False(
            new CompatibilityPolicy(CompatibilityMode.None, CompatibilitySurface.Wire).IsChecked);

    [Fact]
    public void Verdict_CarriesThePolicyItWasEvaluatedUnder()
    {
        var verdict = CompatibilityVerdict.Breaking(Build.BackwardWireJson);

        Assert.True(verdict.IsBreaking);
        Assert.Equal(Build.BackwardWireJson, verdict.EvaluatedUnder);
    }
}

public class ResultTests
{
    [Fact]
    public void Value_OnFailure_Throws()
    {
        var result = Result<string>.Failure("some_code", "boom");

        var ex = Assert.Throws<InvalidOperationException>(() => result.Value);
        Assert.Contains("some_code", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Success_CarriesValueAndNoError()
    {
        var result = Result<string>.Success("ok");

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Null(result.Error);
        Assert.Equal("ok", result.Value);
    }
}
