using Concordat.Domain.Identity;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;

namespace Concordat.Domain.Tests;

/// <summary>
/// The authorisation vocabulary and its implication rules (M8.1, ADR-018).
/// </summary>
/// <remarks>
/// Every assertion here is a security boundary. A scope that grants more than it names, or a
/// set that answers the same question two ways, is not a bug anyone notices until it is
/// exploited.
/// </remarks>
public class ScopeTests
{
    [Fact]
    public void TheVocabularyMatchesWhatTheFrontendPublishes()
    {
        // web/src/app/domain/identity/scope.ts holds the same list. They are compared by hand
        // because the two are separate builds — but a change here without a change there ships
        // a UI that hides an affordance the server would allow, or renders one it refuses.
        Assert.Equal(
            [
                "subject:read",
                "subject:write",
                "subject:admin",
                "contract:read",
                "contract:write",
                "env:read",
                "env:write",
                "broker:read",
                "broker:write",
                "org:admin",
            ],
            Scope.All);
    }

    [Fact]
    public void WriteImpliesReadAndAdminImpliesBoth()
    {
        var set = ScopeSet.Of([Scope.SubjectAdmin]);

        Assert.True(set.Allows(Scope.SubjectAdmin));
        Assert.True(set.Allows(Scope.SubjectWrite));
        Assert.True(set.Allows(Scope.SubjectRead));
    }

    [Fact]
    public void ImplicationDoesNotLeakAcrossResources()
    {
        // subject:admin is not a master key. An estate where schema admins silently gained
        // broker credentials would be a surprise nobody signed off on.
        var set = ScopeSet.Of([Scope.SubjectAdmin]);

        Assert.False(set.Allows(Scope.BrokerRead));
        Assert.False(set.Allows(Scope.ContractWrite));
        Assert.False(set.Allows(Scope.EnvWrite));
        Assert.False(set.Allows(Scope.OrgAdmin));
    }

    [Fact]
    public void OrgAdminDoesNotImplySchemaWrites()
    {
        // ADR-018's whole point is that the set of people who can change a contract stays small
        // and deliberate. Acquiring it by managing the org would be a way around that.
        var set = ScopeSet.Of([Scope.OrgAdmin]);

        Assert.True(set.Allows(Scope.OrgAdmin));
        Assert.False(set.Allows(Scope.SubjectWrite));
        Assert.False(set.Allows(Scope.SubjectRead));
    }

    [Fact]
    public void AnEmptySetAllowsNothing()
    {
        Assert.All(Scope.All, s => Assert.False(ScopeSet.None.Allows(s)));
        Assert.False(ScopeSet.None.AllowsAny([.. Scope.All]));
    }

    [Theory]
    [InlineData("subject:readonly")]
    [InlineData("subject:")]
    [InlineData("subject")]
    [InlineData("SUBJECT:READ")]
    [InlineData("subject:read ")]
    [InlineData("")]
    [InlineData(null)]
    public void OnlyExactTokensAreKnown(string? token) => Assert.False(Scope.IsKnown(token));

    [Fact]
    public void APrefixDoesNotSatisfyACheck()
    {
        // The specific failure an implication table exists to prevent: a StartsWith rule would
        // make 'subject:read-only' satisfy a 'subject:read' check.
        var set = ScopeSet.Of(["subject:read-only"]);

        Assert.False(set.Allows(Scope.SubjectRead));
        Assert.Empty(set.Granted);
    }

    [Fact]
    public void ParsingExpandsImplicationsAndSortsThem()
    {
        var result = Scope.Parse([Scope.SubjectWrite], out var scopes);

        Assert.True(result.IsSuccess);
        Assert.Equal(["subject:read", "subject:write"], scopes);
    }

    [Fact]
    public void ParsingRefusesAnUnknownTokenRatherThanDroppingIt()
    {
        // Dropping it would issue a key that grants less than its creator asked for and says
        // nothing about it — discovered later, in CI, as a mysterious 403.
        var result = Scope.Parse([Scope.SubjectRead, "subject:destroy"], out _);

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.ScopeInvalid, result.Error!.Code);
    }

    [Fact]
    public void ParsingRefusesAnEmptySet()
    {
        Assert.True(Scope.Parse([], out _).IsFailure);
        Assert.True(Scope.Parse(null, out _).IsFailure);
    }

    [Fact]
    public void DuplicatesCollapse()
    {
        Scope.Parse([Scope.SubjectRead, Scope.SubjectRead, Scope.SubjectWrite], out var scopes);

        Assert.Equal(["subject:read", "subject:write"], scopes);
    }
}

/// <summary>The three roles and what each resolves to.</summary>
public class RoleTests
{
    [Fact]
    public void AReaderCanReadEverythingAndWriteNothing()
    {
        var set = Roles.SetFor(Role.Reader);

        Assert.True(set.Allows(Scope.SubjectRead));
        Assert.True(set.Allows(Scope.ContractRead));
        Assert.True(set.Allows(Scope.EnvRead));
        Assert.True(set.Allows(Scope.BrokerRead));

        Assert.False(set.Allows(Scope.SubjectWrite));
        Assert.False(set.Allows(Scope.ContractWrite));
        Assert.False(set.Allows(Scope.EnvWrite));
        Assert.False(set.Allows(Scope.BrokerWrite));
        Assert.False(set.Allows(Scope.OrgAdmin));
    }

    [Fact]
    public void AnAdminCanWriteSchemasButNotManageTheOrg()
    {
        var set = Roles.SetFor(Role.Admin);

        Assert.True(set.Allows(Scope.SubjectAdmin));
        Assert.True(set.Allows(Scope.SubjectWrite));
        Assert.True(set.Allows(Scope.SubjectRead));
        Assert.False(set.Allows(Scope.OrgAdmin));
    }

    [Fact]
    public void AnOwnerIsAnAdminWhoCanAlsoManageTheOrg()
    {
        var owner = Roles.SetFor(Role.Owner);
        var admin = Roles.SetFor(Role.Admin);

        Assert.All(admin.Granted, s => Assert.True(owner.Allows(s)));
        Assert.True(owner.Allows(Scope.OrgAdmin));
    }

    [Theory]
    [InlineData(Role.Reader, "READER")]
    [InlineData(Role.Admin, "ADMIN")]
    [InlineData(Role.Owner, "OWNER")]
    public void RoleTokensRoundTrip(Role role, string expected)
    {
        Assert.Equal(expected, Roles.For(role));

        Assert.True(Roles.Parse(expected, out var parsed).IsSuccess);
        Assert.Equal(role, parsed);
    }

    [Fact]
    public void AnUnknownRoleTokenIsRefusedAndDoesNotDefaultToSomethingPermissive()
    {
        var result = Roles.Parse("SUPERUSER", out var role);

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.RoleInvalid, result.Error!.Code);

        // The out value on failure is the least privileged role, not the first enum member by
        // accident. A caller that ignores the Result must not be handed Owner.
        Assert.Equal(Role.Reader, role);
    }

    [Fact]
    public void EveryRoleResolvesToSomething() =>
        Assert.All(Enum.GetValues<Role>(), r => Assert.NotEmpty(Roles.ScopesFor(r)));
}

/// <summary>M8.1's API keys — issuing, presenting, verifying and revoking.</summary>
public class ApiKeyTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private static IssuedApiKey Issue(
        IReadOnlyList<string>? scopes = null, DateTimeOffset? expiresAt = null) =>
        ApiKey.Issue(
            TenantId.SelfHosted,
            UserId.New(),
            "ci",
            scopes ?? [Scope.SubjectWrite],
            Now,
            expiresAt).Value;

    [Fact]
    public void TheSecretIsReturnedOnceAndNeverStored()
    {
        var issued = Issue();

        Assert.StartsWith("cdt_", issued.Secret, StringComparison.Ordinal);
        Assert.DoesNotContain(issued.Secret, issued.Key.SecretHash, StringComparison.Ordinal);

        // What is stored is a hash of the secret half only, so possessing the row does not
        // yield a working credential.
        Assert.Equal(64, issued.Key.SecretHash.Length);
    }

    [Fact]
    public void TheCredentialCarriesItsOwnKeyId()
    {
        var issued = Issue();

        Assert.True(ApiKey.TryParse(issued.Secret, out var keyId, out var secret));
        Assert.Equal(issued.Key.KeyId, keyId);
        Assert.Equal(ApiKey.Hash(secret), issued.Key.SecretHash);
    }

    [Fact]
    public void EveryIssuedCredentialParsesBack()
    {
        // The regression this exists for: the secret was base64url, whose alphabet contains the
        // '_' this credential uses as a separator. Roughly half of all issued keys failed to
        // parse — which ships looking like an intermittent authentication failure rather than a
        // deterministic bug. 200 keys is enough that a 50% failure rate cannot hide.
        foreach (var issued in Enumerable.Range(0, 200).Select(_ => Issue()))
        {
            Assert.True(
                ApiKey.TryParse(issued.Secret, out var keyId, out var secret),
                $"credential did not parse: {issued.Secret}");

            Assert.Equal(issued.Key.KeyId, keyId);
            Assert.True(issued.Key.Verify(secret, Now));
        }
    }

    [Fact]
    public void ACredentialNeedsNoEscapingInAShellOrAUrl()
    {
        var issued = Issue();

        Assert.Equal(issued.Secret, Uri.EscapeDataString(issued.Secret));
        Assert.DoesNotContain('+', issued.Secret);
        Assert.DoesNotContain('/', issued.Secret);
        Assert.DoesNotContain('=', issued.Secret);
    }

    [Fact]
    public void TwoKeysNeverCollide()
    {
        var keys = Enumerable.Range(0, 200).Select(_ => Issue()).ToList();

        Assert.Equal(200, keys.Select(k => k.Key.KeyId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(200, keys.Select(k => k.Secret).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void TheRightSecretVerifiesAndAWrongOneDoesNot()
    {
        var issued = Issue();
        Assert.True(ApiKey.TryParse(issued.Secret, out _, out var secret));

        Assert.True(issued.Key.Verify(secret, Now));
        Assert.False(issued.Key.Verify(secret + "x", Now));
        Assert.False(issued.Key.Verify(secret[..^1], Now));
        Assert.False(issued.Key.Verify(string.Empty, Now));
        Assert.False(issued.Key.Verify(null, Now));
    }

    [Fact]
    public void AKeyDoesNotVerifyAnotherKeysSecret()
    {
        var first = Issue();
        var second = Issue();
        Assert.True(ApiKey.TryParse(second.Secret, out _, out var otherSecret));

        Assert.False(first.Key.Verify(otherSecret, Now));
    }

    [Fact]
    public void ARevokedKeyStopsVerifyingImmediately()
    {
        var issued = Issue();
        Assert.True(ApiKey.TryParse(issued.Secret, out _, out var secret));

        issued.Key.Revoke(Now);

        Assert.False(issued.Key.Verify(secret, Now));
        Assert.False(issued.Key.IsUsable(Now));
    }

    [Fact]
    public void RevokingTwiceKeepsTheFirstTimestamp()
    {
        // When a key was revoked is what an incident review asks. Overwriting it on a second
        // call would move the answer to whenever someone last clicked the button.
        var issued = Issue();

        issued.Key.Revoke(Now);
        issued.Key.Revoke(Now.AddDays(1));

        Assert.Equal(Now, issued.Key.RevokedAt);
    }

    [Fact]
    public void AnExpiredKeyStopsVerifying()
    {
        var issued = Issue(expiresAt: Now.AddHours(1));
        Assert.True(ApiKey.TryParse(issued.Secret, out _, out var secret));

        Assert.True(issued.Key.Verify(secret, Now.AddMinutes(59)));
        Assert.False(issued.Key.Verify(secret, Now.AddHours(2)));
    }

    [Fact]
    public void AnExpiryInThePastIsRefusedRatherThanIssuingADeadKey()
    {
        var result = ApiKey.Issue(
            TenantId.SelfHosted, null, "ci", [Scope.SubjectRead], Now, Now.AddSeconds(-1));

        Assert.True(result.IsFailure);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnUnlabelledKeyIsRefused(string? label)
    {
        // An unlabelled key is one nobody dares revoke, because nobody can work out what would
        // break.
        var result = ApiKey.Issue(
            TenantId.SelfHosted, null, label, [Scope.SubjectRead], Now);

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.ApiKeyLabelInvalid, result.Error!.Code);
    }

    [Fact]
    public void AKeyWithAnUnknownScopeIsRefused()
    {
        var result = ApiKey.Issue(
            TenantId.SelfHosted, null, "ci", ["subject:destroy"], Now);

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.ScopeInvalid, result.Error!.Code);
    }

    [Fact]
    public void AKeyWithNoScopesIsRefused() =>
        Assert.True(ApiKey.Issue(TenantId.SelfHosted, null, "ci", [], Now).IsFailure);

    [Fact]
    public void IssuedScopesAreExpanded()
    {
        var issued = Issue([Scope.SubjectWrite]);

        Assert.Equal(["subject:read", "subject:write"], issued.Key.Scopes);
        Assert.True(issued.Key.ScopeSet().Allows(Scope.SubjectRead));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-key")]
    [InlineData("cdt_short_secret")]
    [InlineData("xyz_0123456789abcdef_secret")]
    [InlineData("cdt_0123456789abcdef_")]
    [InlineData("cdt_0123456789abcdef")]
    [InlineData("cdt_0123456789abcdef_a_b")]
    public void AMalformedCredentialDoesNotParse(string? credential) =>
        Assert.False(ApiKey.TryParse(credential, out _, out _));

    [Fact]
    public void UsageIsRecordedSoAKeyCanEventuallyBeRetired()
    {
        var issued = Issue();
        Assert.Null(issued.Key.LastUsedAt);

        issued.Key.RecordUse(Now.AddDays(3));

        Assert.Equal(Now.AddDays(3), issued.Key.LastUsedAt);
    }
}

/// <summary>M8.1's local accounts.</summary>
public class UserTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private static User New(string email = "Alice@Example.COM", string? name = null) =>
        User.Create(email, name, "hashed", Now).Value;

    [Fact]
    public void AnEmailIsLowercasedSoOneAccountIsOneAccount()
    {
        // Unlike a subject name, where case is meaning. Letting Alice@ and alice@ be two
        // accounts is how one person ends up locked out of permissions they were granted.
        Assert.Equal("alice@example.com", New().Email.Value);
    }

    [Fact]
    public void ADisplayNameDefaultsToTheLocalPart() => Assert.Equal("alice", New().DisplayName);

    [Fact]
    public void AnExplicitDisplayNameIsKept() =>
        Assert.Equal("Alice Smith", New(name: "  Alice Smith  ").DisplayName);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-address")]
    [InlineData("no@tld")]
    [InlineData("two@@example.com")]
    [InlineData("@example.com")]
    [InlineData("spaces in@example.com")]
    public void AnUnusableEmailIsRefused(string? email)
    {
        var result = User.Create(email, null, "hashed", Now);

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.EmailInvalid, result.Error!.Code);
    }

    [Fact]
    public void TheAggregateNeverAcceptsAnEmptyHash() =>
        Assert.Throws<ArgumentException>(() => User.Create("a@b.com", null, "  ", Now));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("elevenchars")]
    public void AShortPasswordIsRefused(string? password)
    {
        var result = User.CheckPassword(password);

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.PasswordInvalid, result.Error!.Code);
    }

    [Fact]
    public void ATwelveCharacterPasswordIsAccepted()
    {
        // Length only, no character classes: composition rules measurably push people towards
        // predictable substitutions.
        Assert.True(User.CheckPassword("correct horse").IsSuccess);
        Assert.True(User.CheckPassword("aaaaaaaaaaaa").IsSuccess);
    }

    [Fact]
    public void DisablingKeepsTheAccountAndWhatItDid()
    {
        var user = New();

        user.SetDisabled(true);

        Assert.True(user.Disabled);
        Assert.Equal("alice@example.com", user.Email.Value);
    }

    [Fact]
    public void TheActorIsTheEmailSoTheAuditTrailNamesAPerson() =>
        Assert.Equal("alice@example.com", New().Actor().Value);

    [Fact]
    public void SignInIsRecordedInUtc()
    {
        var user = New();
        var local = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.FromHours(4));

        user.RecordSignIn(local);

        Assert.Equal(TimeSpan.Zero, user.LastSignedInAt!.Value.Offset);
    }
}

/// <summary>M8.1's memberships.</summary>
public class MembershipTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AGrantCarriesItsTenantUserAndRole()
    {
        var user = UserId.New();

        var membership = Membership.Grant(TenantId.SelfHosted, user, Role.Admin, Now);

        Assert.Equal(TenantId.SelfHosted, membership.TenantId);
        Assert.Equal(user, membership.UserId);
        Assert.Equal(Role.Admin, membership.Role);
    }

    [Fact]
    public void ARoleCanBeChangedWithoutRegrantingTheMembership()
    {
        var membership = Membership.Grant(TenantId.SelfHosted, UserId.New(), Role.Reader, Now);

        membership.ChangeRole(Role.Owner);

        Assert.Equal(Role.Owner, membership.Role);
        Assert.Equal(Now, membership.CreatedAt);
    }
}
