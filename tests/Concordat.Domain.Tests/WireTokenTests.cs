using Concordat.Domain.Governance;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;

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
    // --------------------------------------- the frontend shares these vocabularies

    /// <summary>
    /// Every version status the registry can emit is one the web app knows.
    /// </summary>
    /// <remarks>
    /// <b>Added after a browser found the gap the hard way.</b> <c>DISMISSED</c> shipped with M7
    /// and <c>web/src/app/domain/registry/wire-tokens.ts</c> never learned it — and because the
    /// web app's unknown-token guard is strict, a single dismissed version failed the entire
    /// subject list with "the registry sent 'DISMISSED', which this build does not recognise".
    /// Every unit test on both sides passed throughout.
    /// <para>
    /// Compared by hand, like the scope vocabulary in <c>IdentityTests</c>: the two are separate
    /// builds, so nothing else makes a change here fail over there.
    /// </para>
    /// </remarks>
    [Fact]
    public void VersionStatusesMatchWhatTheFrontendPublishes() =>
        // Sorted on both sides, so adding a member in the middle of the enum is not a failure.
        Assert.Equal(
            ["ACTIVE", "AWAITING_APPROVAL", "DISMISSED", "REJECTED"],
            Enum.GetValues<VersionStatus>().Select(WireTokens.For)
                .Order(StringComparer.Ordinal).ToArray());

    /// <summary>Every subject lifecycle the registry can emit is one the web app knows.</summary>
    [Fact]
    public void SubjectLifecyclesMatchWhatTheFrontendPublishes() =>
        Assert.Equal(
            ["ACTIVE", "DEPRECATED", "RETIRED"],
            Enum.GetValues<SubjectLifecycle>().Select(WireTokens.For)
                .Order(StringComparer.Ordinal).ToArray());

    /// <summary>Every schema format the registry can emit is one the web app knows.</summary>
    [Fact]
    public void SchemaFormatsMatchWhatTheFrontendPublishes() =>
        Assert.Equal(
            ["avro", "json", "protobuf"],
            Enum.GetValues<SchemaFormat>().Select(WireTokens.For)
                .Order(StringComparer.Ordinal).ToArray());

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

    [Theory]
    [InlineData(EnforcementMode.Off, "OFF")]
    [InlineData(EnforcementMode.Monitor, "MONITOR")]
    [InlineData(EnforcementMode.Enforce, "ENFORCE")]
    public void EnforcementTokens(EnforcementMode mode, string expected) =>
        Assert.Equal(expected, WireTokens.For(mode));

    [Theory]
    [InlineData(AuditAction.SubjectCreated, "SUBJECT_CREATED")]
    [InlineData(AuditAction.VersionSubmitted, "VERSION_SUBMITTED")]
    [InlineData(AuditAction.VersionDismissed, "VERSION_DISMISSED")]
    [InlineData(AuditAction.BrokerCredentialRemoved, "BROKER_CREDENTIAL_REMOVED")]
    [InlineData(AuditAction.ContractEnforcementChanged, "CONTRACT_ENFORCEMENT_CHANGED")]
    [InlineData(AuditAction.ServiceRegistered, "SERVICE_REGISTERED")]
    public void AuditActionTokens(AuditAction action, string expected) =>
        Assert.Equal(expected, AuditTokens.For(action));

    [Fact]
    public void EveryAuditActionHasATokenAndParsesBack()
    {
        // The audit catalogue is a lookup rather than a switch, so a missing member is a silent
        // KeyNotFoundException at query time instead of a compile error.
        foreach (var action in Enum.GetValues<AuditAction>())
        {
            var token = AuditTokens.For(action);
            Assert.False(string.IsNullOrEmpty(token));

            Assert.True(AuditTokens.Parse(token, out var parsed).IsSuccess);
            Assert.Equal(action, parsed);
        }
    }

    [Fact]
    public void AnUnknownAuditActionIsRefusedButAnAbsentOneIsNot()
    {
        // No filter means "everything", which is the common case for an audit query; a typo
        // means the caller believes they are filtering and is not.
        Assert.True(AuditTokens.Parse(null, out var none).IsSuccess);
        Assert.Null(none);

        Assert.True(AuditTokens.Parse("   ", out var blank).IsSuccess);
        Assert.Null(blank);

        var wrong = AuditTokens.Parse("SUBJECT_DELETED", out _);
        Assert.True(wrong.IsFailure);
        Assert.Equal(ConcordatCodes.AuditFilterInvalid, wrong.Error!.Code);
    }

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
