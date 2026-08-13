using Concordat.Domain.Registry;

namespace Concordat.Domain.Tests;

/// <summary>
/// Pins every wire token, because these strings are the protocol (ADR-019).
/// </summary>
/// <remarks>
/// <para>
/// Added in M6.1 after the protocol freeze found the API deriving these from CLR member names
/// with <c>ToUpperInvariant()</c>. That shipped <c>WIREJSON</c> from one endpoint and
/// <c>WIRE_JSON</c> from another for the same value, and would have turned any future C# rename
/// into a silent wire-format change.
/// </para>
/// <para>
/// The tokens are written out as literals here rather than computed. A test that derived them
/// the same way the code does would agree with any bug the code has.
/// </para>
/// </remarks>
public class WireTokenTests
{
    [Theory]
    [InlineData(SchemaFormat.Json, "json")]
    [InlineData(SchemaFormat.Avro, "avro")]
    [InlineData(SchemaFormat.Protobuf, "protobuf")]
    public void FormatTokens(SchemaFormat format, string expected) =>
        Assert.Equal(expected, WireTokens.For(format));

    [Theory]
    [InlineData(CompatibilityMode.None, "NONE")]
    [InlineData(CompatibilityMode.Backward, "BACKWARD")]
    [InlineData(CompatibilityMode.BackwardTransitive, "BACKWARD_TRANSITIVE")]
    [InlineData(CompatibilityMode.Forward, "FORWARD")]
    [InlineData(CompatibilityMode.ForwardTransitive, "FORWARD_TRANSITIVE")]
    [InlineData(CompatibilityMode.Full, "FULL")]
    [InlineData(CompatibilityMode.FullTransitive, "FULL_TRANSITIVE")]
    public void ModeTokens(CompatibilityMode mode, string expected) =>
        Assert.Equal(expected, WireTokens.For(mode));

    [Theory]
    [InlineData(CompatibilitySurface.Wire, "WIRE")]
    [InlineData(CompatibilitySurface.WireJson, "WIRE_JSON")]
    [InlineData(CompatibilitySurface.Source, "SOURCE")]
    public void SurfaceTokens(CompatibilitySurface surface, string expected) =>
        Assert.Equal(expected, WireTokens.For(surface));

    [Theory]
    [InlineData(SubjectLifecycle.Active, "ACTIVE")]
    [InlineData(SubjectLifecycle.Deprecated, "DEPRECATED")]
    [InlineData(SubjectLifecycle.Retired, "RETIRED")]
    public void LifecycleTokens(SubjectLifecycle lifecycle, string expected) =>
        Assert.Equal(expected, WireTokens.For(lifecycle));

    [Theory]
    [InlineData(ContentModel.Open, "OPEN")]
    [InlineData(ContentModel.Closed, "CLOSED")]
    public void ContentModelTokens(ContentModel contentModel, string expected) =>
        Assert.Equal(expected, WireTokens.For(contentModel));

    [Theory]
    [InlineData(VersionStatus.Active, "ACTIVE")]
    [InlineData(VersionStatus.AwaitingApproval, "AWAITING_APPROVAL")]
    [InlineData(VersionStatus.Rejected, "REJECTED")]
    public void VersionStatusTokens(VersionStatus status, string expected) =>
        Assert.Equal(expected, WireTokens.For(status));

    [Fact]
    public void MultiWordTokensAreNotJustTheUppercasedMemberName()
    {
        // The specific regression. Every single-word token happens to match ToUpperInvariant(),
        // which is why the bug hid for five milestones: only the multi-word members expose it.
        Assert.NotEqual(
            CompatibilitySurface.WireJson.ToString().ToUpperInvariant(),
            WireTokens.For(CompatibilitySurface.WireJson));

        Assert.NotEqual(
            CompatibilityMode.BackwardTransitive.ToString().ToUpperInvariant(),
            WireTokens.For(CompatibilityMode.BackwardTransitive));

        Assert.NotEqual(
            VersionStatus.AwaitingApproval.ToString().ToUpperInvariant(),
            WireTokens.For(VersionStatus.AwaitingApproval));
    }

    [Fact]
    public void EveryEnumMemberHasAToken()
    {
        // Guards the gap a switch expression leaves: adding a member without a token compiles
        // and throws at runtime, on the wire, in front of a client.
        foreach (var mode in Enum.GetValues<CompatibilityMode>())
        {
            Assert.False(string.IsNullOrEmpty(WireTokens.For(mode)));
        }

        foreach (var surface in Enum.GetValues<CompatibilitySurface>())
        {
            Assert.False(string.IsNullOrEmpty(WireTokens.For(surface)));
        }

        foreach (var lifecycle in Enum.GetValues<SubjectLifecycle>())
        {
            Assert.False(string.IsNullOrEmpty(WireTokens.For(lifecycle)));
        }

        foreach (var contentModel in Enum.GetValues<ContentModel>())
        {
            Assert.False(string.IsNullOrEmpty(WireTokens.For(contentModel)));
        }

        foreach (var status in Enum.GetValues<VersionStatus>())
        {
            Assert.False(string.IsNullOrEmpty(WireTokens.For(status)));
        }

        foreach (var format in Enum.GetValues<SchemaFormat>())
        {
            Assert.False(string.IsNullOrEmpty(WireTokens.For(format)));
        }
    }
}
