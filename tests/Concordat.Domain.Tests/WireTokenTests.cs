using Concordat.Domain.Contracts;
using Concordat.Domain.Governance;
using Concordat.Domain.Identity;
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

    /// <summary>
    /// Every audit action the registry can record is one the web app's filter offers.
    /// </summary>
    /// <remarks>
    /// Unlike the vocabularies around it, the web app does not <i>reject</i> an action it has
    /// not heard of — a row renders as received, because a log that cannot show the newest kind
    /// of entry hides exactly what somebody opened it to find. What a missing action costs is
    /// that it cannot be filtered for, which is quiet, and so is pinned here. See
    /// <c>web/src/app/domain/governance/audit.ts</c>.
    /// </remarks>
    [Fact]
    public void AuditActionsMatchWhatTheFrontendPublishes() =>
        Assert.Equal(
            [
                "API_KEY_ISSUED",
                "API_KEY_REVOKED",
                "BROKER_ADDED",
                "BROKER_CREDENTIAL_REMOVED",
                "BROKER_CREDENTIAL_SET",
                "BROKER_REMOVED",
                "CONTRACT_BINDING_ADDED",
                "CONTRACT_BINDING_CHANGED",
                "CONTRACT_BINDING_REMOVED",
                "CONTRACT_CREATED",
                "CONTRACT_ENFORCEMENT_CHANGED",
                "ENVIRONMENT_CREATED",
                "ENVIRONMENT_UPDATED",
                "MEMBER_ADDED",
                "MEMBER_DISABLED",
                "MEMBER_ENABLED",
                "MEMBER_PASSWORD_RESET",
                "MEMBER_ROLE_CHANGED",
                "PASSWORD_CHANGED",
                "SERVICE_REGISTERED",
                "SUBJECT_CREATED",
                "SUBJECT_POLICY_CHANGED",
                "SUBJECT_PURGED",
                "SUBJECT_RETIRED",
                "SUBJECT_REVIEWERS_CHANGED",
                "SUBJECT_UPDATED",
                "VERSION_APPROVED",
                "VERSION_DISMISSED",
                "VERSION_PROMOTED",
                "VERSION_REGISTERED",
                "VERSION_REJECTED",
                "VERSION_SUBMITTED",
            ],
            Enum.GetValues<AuditAction>().Select(AuditTokens.For)
                .Order(StringComparer.Ordinal).ToArray());

    /// <summary>Every enforcement mode the registry can emit is one the web app knows.</summary>
    /// <remarks>
    /// Strict on the web side: a contract shown as one thing that is in fact another is a
    /// contract somebody believes is not blocking, and enforcement is the one setting where that
    /// mistake has a cost.
    /// </remarks>
    [Fact]
    public void EnforcementModesMatchWhatTheFrontendPublishes() =>
        Assert.Equal(
            ["ENFORCE", "MONITOR", "OFF"],
            Enum.GetValues<EnforcementMode>().Select(WireTokens.For)
                .Order(StringComparer.Ordinal).ToArray());

    /// <summary>Every role the registry can emit is one the web app knows.</summary>
    [Fact]
    public void RolesMatchWhatTheFrontendPublishes() =>
        Assert.Equal(
            ["ADMIN", "OWNER", "READER"],
            Enum.GetValues<Role>().Select(Roles.For).Order(StringComparer.Ordinal).ToArray());

    /// <summary>
    /// Every broker status the registry can emit is one the web app knows.
    /// </summary>
    /// <remarks>
    /// Derived on the wire the way the API derives it — the member name, upper-cased — which is
    /// safe for these three single words and is exactly what would break if a member were ever
    /// renamed to a compound one, so this is where that would be noticed.
    /// </remarks>
    [Fact]
    public void BrokerStatusesMatchWhatTheFrontendPublishes() =>
        Assert.Equal(
            ["REACHABLE", "UNKNOWN", "UNREACHABLE"],
            Enum.GetValues<BrokerStatus>().Select(s => s.ToString().ToUpperInvariant())
                .Order(StringComparer.Ordinal).ToArray());

    /// <summary>
    /// Every notification event the registry can emit is one the web app's form offers.
    /// </summary>
    /// <remarks>
    /// Lenient on the web side for display, like the audit actions: a subscription's events are
    /// shown as received. What a missing event costs is that it cannot be ticked, which is quiet,
    /// and so is pinned here. See <c>web/src/app/domain/notifications/subscription.ts</c>.
    /// </remarks>
    [Fact]
    public void NotificationEventsMatchWhatTheFrontendPublishes() =>
        Assert.Equal(
            [
                "BREAKING_CHANGE_APPROVED",
                "BREAKING_CHANGE_REJECTED",
                "BREAKING_CHANGE_SUBMITTED",
                "ENFORCEMENT_VIOLATION",
                "SUBJECT_DEPRECATED",
                "SUBJECT_POLICY_CHANGED",
                "VERSION_PROMOTED",
                "VERSION_REGISTERED",
            ],
            Enum.GetValues<NotificationEvent>().Select(NotificationTokens.For)
                .Order(StringComparer.Ordinal).ToArray());

    /// <summary>
    /// A policy change is one name on both sides of the registry: the audit action an auditor
    /// filters by and the event a subscriber subscribes to.
    /// </summary>
    /// <remarks>
    /// Two spellings for one thing would make "who changed it" and "tell me when it changes" look
    /// like different questions, and the subscription screen would offer a token the audit page
    /// does not know (ADR-034).
    /// </remarks>
    [Fact]
    public void APolicyChangeIsOneNameInTheAuditTrailAndInTheNotificationVocabulary() =>
        Assert.Equal(
            AuditTokens.For(AuditAction.SubjectPolicyChanged),
            NotificationTokens.For(NotificationEvent.SubjectPolicyChanged));

    /// <summary>Every notification channel the registry can emit is one the web app knows.</summary>
    [Fact]
    public void NotificationChannelsMatchWhatTheFrontendPublishes() =>
        Assert.Equal(
            ["EMAIL", "WEBHOOK"],
            Enum.GetValues<NotificationChannel>().Select(ChannelTokens.For)
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
    public void ResolvingAStoredAuditTokenNeverThrowsAndNamesOnlyWhatThisBuildKnows()
    {
        // The read side of the catalogue (ADR-036): classification of a stored token, as opposed
        // to Parse, which judges a caller's filter. There is no fake enum member for "unknown",
        // so Enum.GetValues above is still exactly the vocabulary, and this is where an
        // unknown token is accounted for.
        foreach (var action in Enum.GetValues<AuditAction>())
        {
            Assert.Equal(action, AuditTokens.TryResolve(AuditTokens.For(action)));
        }

        Assert.Null(AuditTokens.TryResolve("A_FUTURE_ACTION"));
        Assert.Null(AuditTokens.TryResolve(string.Empty));
        Assert.Null(AuditTokens.TryResolve(null));
        Assert.Null(AuditTokens.TryResolve("constructor"));
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
