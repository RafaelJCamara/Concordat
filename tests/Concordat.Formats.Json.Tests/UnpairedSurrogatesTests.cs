using System.Text;
using Concordat.Domain.Results;
using Concordat.Formats.Abstractions;

namespace Concordat.Formats.Json.Tests;

/// <summary>
/// The scanner under both canonicalisers, on the cases where reading escapes by hand goes wrong.
/// The canonicaliser tests prove a refusal arrives; these prove it is not wrong in either
/// direction, because a false refusal blocks a schema the registry used to take.
/// </summary>
public class UnpairedSurrogatesTests
{
    private static readonly string Backslash = new((char)92, 1);

    /// <summary>Spells <c>\uXXXX</c> without writing one in source.</summary>
    private static string Escape(string hex) => Backslash + "u" + hex;

    private static DomainError? Find(string json) =>
        UnpairedSurrogates.Find(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void AnEscapedBackslashBeforeTheDigits_IsText_NotASurrogateEscape()
    {
        // JSON text: "\\ud800" -- a backslash, then the five characters u d 8 0 0. Reading
        // every backslash-u as an escape would refuse a schema that merely mentions one, in a
        // description or a pattern, which is a thing documentation about this very bug does.
        var json = "{\"a\":\"" + Backslash + Backslash + "ud800\"}";

        Assert.Null(Find(json));
    }

    [Fact]
    public void AnEscapedBackslashThenARealEscape_IsStillFound()
    {
        // Two backslashes, then a lone high half written as a real escape: the first pair is
        // consumed as a unit, so the scan lands on the second escape and not past it.
        var json = "{\"a\":\"" + Backslash + Backslash + Escape("d800") + "\"}";

        var error = Find(json);

        Assert.NotNull(error);
        Assert.Contains(Escape("D800"), error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("d83d", "de00")]
    [InlineData("D83D", "DE00")]
    [InlineData("d83D", "dE00")]
    [InlineData("d800", "dc00")]
    [InlineData("dbff", "dfff")]
    public void AHighHalfFollowedByALowHalf_IsAPair_InEitherCase(string high, string low) =>
        Assert.Null(Find("{\"a\":\"" + Escape(high) + Escape(low) + "\"}"));

    [Fact]
    public void ThePartnerHasToBeNextToIt()
    {
        // A high half, an ordinary character, then a low half: two unpaired units, not one pair.
        var json = "{\"a\":\"" + Escape("d83d") + "x" + Escape("de00") + "\"}";

        var error = Find(json);

        Assert.NotNull(error);
        Assert.Contains(Escape("D83D"), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AHighHalfFollowedByANonSurrogateEscape_IsUnpaired()
    {
        var json = "{\"a\":\"" + Escape("d83d") + Escape("0041") + "\"}";

        Assert.NotNull(Find(json));
    }

    [Theory]
    [InlineData("d7ff")]
    [InlineData("e000")]
    [InlineData("ffff")]
    [InlineData("0000")]
    public void TheCodeUnitsEitherSideOfTheSurrogateBlock_AreOrdinaryCharacters(string unit) =>
        Assert.Null(Find("{\"a\":\"" + Escape(unit) + "\"}"));

    [Fact]
    public void ADocumentWithNoEscapes_IsNotScannedAtAll_AndIsAccepted() =>
        Assert.Null(Find("""{"type":"object","properties":{"a":{"type":"string"}},"required":["a"]}"""));

    [Fact]
    public void ARootString_IsLocatedAsTheDocumentItself()
    {
        var error = Find("\"" + Escape("dc00") + "\"");

        Assert.NotNull(error);
        Assert.Contains("the document itself", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""[1,"x",{"k":["a",true,null,"%"]}]""", "/2/k/3")]
    [InlineData("""{"a":[[],[{}],"%"]}""", "/a/2")]
    [InlineData("""{"x/y":"%"}""", "/x~1y")]
    [InlineData("""{"x~y":{"z":"%"}}""", "/x~0y/z")]
    [InlineData("""{"first":1,"second":"%"}""", "/second")]
    public void TheLocation_IsAJsonPointerToTheString(string template, string expected)
    {
        var json = template.Replace("%", Escape("d800"), StringComparison.Ordinal);

        var error = Find(json);

        Assert.NotNull(error);
        Assert.Contains($"'{expected}'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFirstOneInDocumentOrder_IsTheOneReported()
    {
        var json = "{\"a\":\"" + Escape("dc00") + "\",\"b\":\"" + Escape("d800") + "\"}";

        var error = Find(json);

        Assert.NotNull(error);
        Assert.Contains("'/a'", error.Message, StringComparison.Ordinal);
        Assert.Contains(Escape("DC00"), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALongKey_IsQuotedBounded_AndNeverSplitsAPair()
    {
        // The cut falls between the two halves of a pair: keeping the high half alone would put
        // the very thing being refused into the message that refuses it.
        var key = new string('k', 199) + char.ConvertFromUtf32(0x1F600) + new string('k', 50);
        var json = "{\"" + key + "\":{\"v\":\"" + Escape("d800") + "\"}}";

        var error = Find(json);

        Assert.NotNull(error);
        Assert.Equal(ConcordatCodes.SchemaMalformed, error.Code);
        Assert.True(error.Message.Length < 900, "the quoted location is bounded");
        _ = new UTF8Encoding(false, true).GetByteCount(error.Message);
    }

    [Fact]
    public void TheRefusal_IsAlwaysSchemaMalformed_AndAlwaysValidUtf8()
    {
        var error = Find("{\"" + Escape("d800") + "\":1}");

        Assert.NotNull(error);
        Assert.Equal(ConcordatCodes.SchemaMalformed, error.Code);
        _ = new UTF8Encoding(false, true).GetByteCount(error.Message);
    }
}
