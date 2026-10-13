using Concordat.Domain.Registry;

namespace Concordat.Domain.Tests;

public class WireEnumsTests
{
    [Theory]
    [InlineData("Backward", CompatibilityMode.Backward)]
    [InlineData("backward", CompatibilityMode.Backward)]
    [InlineData("BACKWARD", CompatibilityMode.Backward)]
    [InlineData("FullTransitive", CompatibilityMode.FullTransitive)]
    public void AMemberNamedInAnyCase_IsRead(string text, CompatibilityMode expected)
    {
        Assert.True(WireEnums.TryParseDefined<CompatibilityMode>(text, out var value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void ANumberThatNumbersAMember_IsStillReadAsThatMember()
    {
        // As it always was. Narrowing what existing callers may send is a different decision from
        // refusing what no member can be.
        Assert.True(WireEnums.TryParseDefined<CompatibilityMode>("2", out var value));
        Assert.Equal(CompatibilityMode.Backward, value);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("8")]
    [InlineData("99")]
    [InlineData("-1")]
    [InlineData("2147483647")]
    public void ANumberThatNumbersNoMember_IsRefused(string text) =>
        // `Enum.TryParse` alone says yes to all of these, and the value was then stored and failed
        // whatever projected it, long after the request that stored it had answered.
        Assert.False(WireEnums.TryParseDefined<CompatibilityMode>(text, out _));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("SIDEWAYS")]
    [InlineData("Backward,Forward")]
    public void ATextThatNamesNothing_IsRefused(string? text) =>
        Assert.False(WireEnums.TryParseDefined<CompatibilityMode>(text, out _));

    [Theory]
    [InlineData("OPEN", RegistrationPolicy.Open)]
    [InlineData("CIONLY", RegistrationPolicy.CiOnly)]
    [InlineData("closed", RegistrationPolicy.Closed)]
    public void EveryEnumerationTheRegistryParsesGoesThroughIt(
        string text, RegistrationPolicy expected)
    {
        Assert.True(WireEnums.TryParseDefined<RegistrationPolicy>(text, out var value));
        Assert.Equal(expected, value);
        Assert.False(WireEnums.TryParseDefined<RegistrationPolicy>("99", out _));
        Assert.False(WireEnums.TryParseDefined<EnforcementMode>("99", out _));
        Assert.False(WireEnums.TryParseDefined<CompatibilitySurface>("99", out _));
    }
}
