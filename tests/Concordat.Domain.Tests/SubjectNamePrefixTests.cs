using Concordat.Domain.Registry;
using Concordat.Domain.Results;

namespace Concordat.Domain.Tests;

/// <summary>
/// A prefix is accepted when some subject name could begin with it, and refused otherwise
/// (ADR-038).
/// </summary>
/// <remarks>
/// The rule is exactly "a valid name, optionally followed by one dot" because that is the set of
/// strings that are a prefix of a name <see cref="SubjectName"/> accepts. Anything else cannot
/// match a stored name, and — more to the point — contains the only characters that could do harm
/// to a query (<c>%</c>, a backslash, a NUL that PostgreSQL refuses outright, a lone surrogate
/// that cannot be encoded), so the grammar is also what keeps them out of SQL.
/// </remarks>
public class SubjectNamePrefixTests
{
    private static readonly string LoneSurrogate = new((char)0xD800, 1);
    private static readonly string Nul = new((char)0, 1);
    private static readonly string Backslash = new((char)92, 1);

    [Theory]
    [InlineData("a")]
    [InlineData("acme")]
    [InlineData("acme.")]
    [InlineData("acme.orders")]
    [InlineData("acme.orders.")]
    [InlineData("acme.orders.OrderCreated")]
    [InlineData("Acme_1.x_")]
    [InlineData("_")]
    [InlineData("a_")]
    [InlineData("a.b.c.")]
    public void APrefixOfAValidName_IsAccepted(string value)
    {
        var result = SubjectNamePrefix.Create(value);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(value, result.Value.Value);
    }

    [Theory]
    [InlineData(".", "a lone dot starts no name")]
    [InlineData(".acme", "a leading dot")]
    [InlineData("acme..", "two dots in a row")]
    [InlineData("acme..orders", "an empty segment")]
    [InlineData("acme-orders", "a hyphen")]
    [InlineData("acme orders", "a space inside")]
    [InlineData("acme/orders", "a slash")]
    [InlineData("acme*", "a glob star")]
    [InlineData("acme?", "a glob question mark")]
    [InlineData("acme,orders", "a comma, which is how repeated query values are joined")]
    [InlineData("acme%", "a LIKE wildcard")]
    [InlineData("%", "the LIKE wildcard alone, which would match every name")]
    [InlineData("acme.%", "a LIKE wildcard after a dot")]
    [InlineData("a\nb", "a newline inside")]
    [InlineData("café", "a non-ASCII letter")]
    public void ATextNoNameCanStartWith_IsRefused(string value, string why)
    {
        var result = SubjectNamePrefix.Create(value);

        Assert.True(result.IsFailure, why);
        Assert.Equal(ConcordatCodes.SubjectNameInvalid, result.Error!.Code);
    }

    [Fact]
    public void ABackslash_IsRefused()
    {
        // LIKE's default escape character. Written from its code so no editor or tool input
        // can halve it into a different test.
        Assert.True(SubjectNamePrefix.Create("acme" + Backslash).IsFailure);
        Assert.True(SubjectNamePrefix.Create(Backslash + "%").IsFailure);
        Assert.True(SubjectNamePrefix.Create("acme" + Backslash + Backslash).IsFailure);
    }

    [Fact]
    public void ANul_IsRefusedBecausePostgreSqlCannotStoreOrCompareOne()
    {
        // Npgsql sends it and the server answers 22021 invalid byte sequence, which would be a 500.
        Assert.True(SubjectNamePrefix.Create("acme" + Nul).IsFailure);
        Assert.True(SubjectNamePrefix.Create(Nul).IsFailure);
    }

    [Fact]
    public void ALoneSurrogate_IsRefusedBecauseItCannotBeEncodedAsUtf8()
    {
        Assert.True(SubjectNamePrefix.Create("acme" + LoneSurrogate).IsFailure);
    }

    [Fact]
    public void AnAstralCharacter_IsRefused()
    {
        // A valid surrogate pair (one emoji) is well formed UTF-16 and still not a name character.
        var pair = new string([(char)0xD83D, (char)0xDE00]);

        Assert.True(SubjectNamePrefix.Create("acme" + pair).IsFailure);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankPrefix_IsRefusedHereBecauseNoPrefixMeansNoFilter(string? value)
    {
        // "Every name" is the absence of a prefix, and the caller says so by passing none. A value
        // object that could also hold "" would give every call site a second spelling of
        // "unfiltered" to forget.
        var result = SubjectNamePrefix.Create(value);

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.SubjectNameInvalid, result.Error!.Code);
    }

    [Theory]
    [InlineData("  acme.orders  ", "acme.orders")]
    [InlineData("\tacme.\n", "acme.")]
    public void SurroundingWhitespace_IsTrimmedAsASubjectNameIs(string value, string expected)
    {
        // SubjectName.Create trims, so GET /subjects/%20acme finds acme. A prefix that did not
        // would answer "acme." with nothing for a trailing space pasted from a terminal.
        Assert.Equal(expected, SubjectNamePrefix.Create(value).Value.Value);
    }

    [Fact]
    public void ACaseIsKept()
    {
        // Names are case-sensitive (OrderCreated and ordercreated are two subjects), so the
        // prefix is not normalised.
        Assert.Equal("Acme.Orders", SubjectNamePrefix.Create("Acme.Orders").Value.Value);
    }

    [Fact]
    public void ThePrefixIsBoundedByTheLongestNameThatCouldStartWithIt()
    {
        var longest = new string('a', SubjectName.MaxLength);
        Assert.True(SubjectNamePrefix.Create(longest).IsSuccess);
        Assert.True(SubjectNamePrefix.Create(longest + "a").IsFailure);

        // A trailing dot promises one more segment character after it, so the longest prefix that
        // can still be completed leaves room for that character.
        Assert.True(SubjectNamePrefix.Create(new string('a', SubjectName.MaxLength - 2) + ".").IsSuccess);
        Assert.True(SubjectNamePrefix.Create(new string('a', SubjectName.MaxLength - 1) + ".").IsFailure);
    }

    [Theory]
    [InlineData("acme.orders.OrderCreated")]
    [InlineData("acme.orders.")]
    [InlineData("a")]
    public void EveryAcceptedPrefixIsAPrefixOfSomeAcceptedName(string value)
    {
        // The property the grammar claims, checked against the grammar it claims it of: complete
        // the prefix with one letter if it ends in a dot and it must be a name.
        var prefix = SubjectNamePrefix.Create(value).Value.Value;
        var name = prefix.EndsWith('.') ? prefix + "x" : prefix;

        Assert.True(SubjectName.Create(name).IsSuccess);
        Assert.StartsWith(prefix, name, StringComparison.Ordinal);
    }
}
