using Concordat.Application.Abstractions;
using Concordat.Application.Identity;
using Concordat.Application.Tests.TestSupport;
using Concordat.Domain.Governance;
using Concordat.Domain.Identity;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;
using Microsoft.Extensions.Time.Testing;

namespace Concordat.Application.Tests;

/// <summary>
/// Changing a password, resetting somebody else's, and disabling or enabling an account (ADR-048).
/// </summary>
/// <remarks>
/// <para>
/// Every refusal is asserted to have written nothing: the unit of work was not committed, so no
/// audit entry exists either, because an entry stages with the change it records and a request that
/// was refused never reaches the commit.
/// </para>
/// <para>
/// The hasher in these tests answers <c>hash:</c> and the plaintext, so "the stored hash is the
/// new password's" and "a refusal left the old one in place" are assertions on a string rather
/// than on a derivation.
/// </para>
/// </remarks>
public class AccountLifecycleHandlerTests
{
    private const string OldPassword = "the old password";
    private const string NewPassword = "a brand new password";
    private const string Temporary = "temporary secret 1";

    private readonly FakeUsers _users = new();
    private readonly FakeApiKeys _keys = new();
    private readonly FakePasswordHasher _passwords = new();
    private readonly SettableCaller _caller = new();
    private readonly RecordingUnitOfWork _unitOfWork = new();
    private readonly RecordingAuditLog _audit = new();
    private readonly FakeTimeProvider _clock = new(Build.At);

    private User Member(
        string email,
        Role role = Role.Reader,
        bool disabled = false,
        string password = OldPassword) =>
        _users.Seed(email, role, disabled: disabled, passwordHash: FakePasswordHasher.HashOf(password));

    /// <summary>Makes a person the caller, as the credential they presented says.</summary>
    private void CallerIs(User user, Role role = Role.Owner, string? keyId = null) =>
        _caller.Current = new Caller(
            CredentialKind.User,
            TenantId.SelfHosted,
            Roles.SetFor(role),
            user.Actor(),
            user.Id,
            keyId);

    private Task<Result<PasswordChange>> ChangeOwnAsync(string? current, string? replacement) =>
        new ChangeOwnPasswordHandler(
            _users, _keys, _passwords, _audit, _unitOfWork, _caller, _clock)
            .HandleAsync(new ChangeOwnPasswordCommand(current, replacement), CancellationToken.None);

    private Task<Result<MemberAccountChange>> ResetAsync(User target, string? password = Temporary) =>
        new ResetMemberPasswordHandler(
            _users, _keys, _passwords, _audit, _unitOfWork, _caller, _clock)
            .HandleAsync(
                new ResetMemberPasswordCommand(target.Id.Value, password), CancellationToken.None);

    private Task<Result<MemberAccountChange>> SetDisabledAsync(User target, bool disabled) =>
        new SetMemberDisabledHandler(_users, _keys, _audit, _unitOfWork, _caller, _clock)
            .HandleAsync(
                new SetMemberDisabledCommand(target.Id.Value, disabled), CancellationToken.None);

    private Task<Result<Membership>> ChangeRoleAsync(User target, string role) =>
        new ChangeMemberRoleHandler(_users, _audit, _unitOfWork, _caller, _clock)
            .HandleAsync(
                new ChangeMemberRoleCommand(target.Id.Value, role), CancellationToken.None);

    private void AssertNothingWritten()
    {
        Assert.Equal(0, _unitOfWork.Saves);
        Assert.Empty(_audit.Entries);
    }

    private static bool Live(ApiKey key) => key.RevokedAt is null;

    // ------------------------------------------------------- change my own password

    [Fact]
    public async Task ChangingYourPassword_StoresTheNewOneAndAuditsItAsYou()
    {
        var alice = Member("alice@example.com", Role.Reader);
        CallerIs(alice, Role.Reader);

        var result = await ChangeOwnAsync(OldPassword, NewPassword);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(FakePasswordHasher.HashOf(NewPassword), alice.PasswordHash);
        Assert.False(alice.MustChangePassword);
        Assert.Equal(1, _unitOfWork.Saves);

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal(AuditAction.PasswordChanged, entry.Action);
        Assert.Equal("alice@example.com", entry.Actor.Value);
        Assert.Equal("alice@example.com", entry.Target);
        Assert.Null(entry.EnvironmentId);
    }

    [Fact]
    public async Task NoPasswordIsEverAuditedOrReturned()
    {
        // The two passwords are the only things in this exchange nobody may ever read back, so the
        // assertion is on every string the handler produced.
        var alice = Member("alice@example.com", Role.Reader);
        CallerIs(alice, Role.Reader);

        var result = await ChangeOwnAsync(OldPassword, NewPassword);

        var entry = Assert.Single(_audit.Entries);
        var everything = $"{entry.Target}|{entry.Detail}|{entry.Actor.Value}|{result.Value}";

        Assert.DoesNotContain(OldPassword, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(NewPassword, everything, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWrongCurrentPassword_IsRefusedAsUnauthenticatedAndWritesNothing()
    {
        var alice = Member("alice@example.com", Role.Reader);
        CallerIs(alice, Role.Reader);

        var result = await ChangeOwnAsync("not the password", NewPassword);

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.Unauthenticated, result.Error!.Code);
        Assert.Equal(FakePasswordHasher.HashOf(OldPassword), alice.PasswordHash);
        AssertNothingWritten();
    }

    [Fact]
    public async Task AWrongCurrentPassword_IsRefusedBeforeTheNewOneIsJudged()
    {
        // Somebody who does not know the current password learns nothing about the new one, not
        // even that it was too short.
        var alice = Member("alice@example.com", Role.Reader);
        CallerIs(alice, Role.Reader);

        var result = await ChangeOwnAsync("not the password", "short");

        Assert.Equal(ConcordatCodes.Unauthenticated, result.Error!.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task AMissingCurrentPassword_IsTheSameRefusalAsAWrongOne(string? current)
    {
        var alice = Member("alice@example.com", Role.Reader);
        CallerIs(alice, Role.Reader);

        var result = await ChangeOwnAsync(current, NewPassword);

        Assert.Equal(ConcordatCodes.Unauthenticated, result.Error!.Code);
        AssertNothingWritten();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("elevenchars")]
    public async Task ANewPasswordUnderTwelveCharacters_IsRefusedAndWritesNothing(string? replacement)
    {
        var alice = Member("alice@example.com", Role.Reader);
        CallerIs(alice, Role.Reader);

        var result = await ChangeOwnAsync(OldPassword, replacement);

        Assert.Equal(ConcordatCodes.PasswordInvalid, result.Error!.Code);
        Assert.Equal(FakePasswordHasher.HashOf(OldPassword), alice.PasswordHash);
        AssertNothingWritten();
    }

    [Fact]
    public async Task ATwelveCharacterNewPasswordIsEnoughAndNothingElseIsAsked()
    {
        var alice = Member("alice@example.com", Role.Reader);
        CallerIs(alice, Role.Reader);

        var result = await ChangeOwnAsync(OldPassword, "aaaaaaaaaaaa");

        Assert.True(result.IsSuccess, result.Error?.Message);
    }

    [Fact]
    public async Task TheNewPasswordMustDifferFromTheCurrentOne()
    {
        // Otherwise a person told to replace a temporary password could "replace" it with itself.
        var alice = Member("alice@example.com", Role.Reader);
        CallerIs(alice, Role.Reader);

        var result = await ChangeOwnAsync(OldPassword, OldPassword);

        Assert.Equal(ConcordatCodes.PasswordInvalid, result.Error!.Code);
        AssertNothingWritten();
    }

    [Fact]
    public async Task ChangingYourPassword_EndsYourOtherSessionsAndKeepsTheOneYouAreUsing()
    {
        var alice = Member("alice@example.com", Role.Reader);
        var current = _keys.Seed(alice.Id, ApiKeyKind.Session);
        var other = _keys.Seed(alice.Id, ApiKeyKind.Session);
        var another = _keys.Seed(alice.Id, ApiKeyKind.Session);
        CallerIs(alice, Role.Reader, current.Key.KeyId);

        var result = await ChangeOwnAsync(OldPassword, NewPassword);

        Assert.Equal(2, result.Value.SessionsEnded);
        Assert.True(Live(current.Key));
        Assert.False(Live(other.Key));
        Assert.False(Live(another.Key));
    }

    [Fact]
    public async Task ChangingYourPassword_LeavesYourApiKeysAlone()
    {
        // A key is a credential of its own (256 bits from a CSPRNG), not something derived from the
        // password, and a CI pipeline that stopped on a password change would punish the hygiene.
        var alice = Member("alice@example.com", Role.Reader);
        var standing = _keys.Seed(alice.Id, ApiKeyKind.Standing, label: "ci");
        CallerIs(alice, Role.Reader);

        var result = await ChangeOwnAsync(OldPassword, NewPassword);

        Assert.True(Live(standing.Key));
        Assert.Equal(0, result.Value.SessionsEnded);
    }

    [Fact]
    public async Task ChangingYourPassword_NeverTouchesAnotherPersonsSessions()
    {
        var alice = Member("alice@example.com", Role.Reader);
        var bob = Member("bob@example.com", Role.Reader);
        var bobs = _keys.Seed(bob.Id, ApiKeyKind.Session);
        CallerIs(alice, Role.Reader);

        await ChangeOwnAsync(OldPassword, NewPassword);

        Assert.True(Live(bobs.Key));
    }

    [Fact]
    public async Task WhenAKeyIsTheCredential_EverySessionEnds()
    {
        // A standing key may change its holder's password too (it must still know the current one).
        // It is not a session, so there is no "this one" to keep.
        var alice = Member("alice@example.com", Role.Reader);
        var standing = _keys.Seed(alice.Id, ApiKeyKind.Standing);
        var session = _keys.Seed(alice.Id, ApiKeyKind.Session);
        CallerIs(alice, Role.Reader, standing.Key.KeyId);

        var result = await ChangeOwnAsync(OldPassword, NewPassword);

        Assert.Equal(1, result.Value.SessionsEnded);
        Assert.False(Live(session.Key));
        Assert.True(Live(standing.Key));
    }

    [Fact]
    public async Task ChangingYourPassword_ClearsTheObligationToChangeIt()
    {
        var alice = Member("alice@example.com", Role.Reader, password: Temporary);
        alice.ResetPassword(FakePasswordHasher.HashOf(Temporary));
        CallerIs(alice, Role.Reader);

        var result = await ChangeOwnAsync(Temporary, NewPassword);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.False(alice.MustChangePassword);
    }

    [Fact]
    public async Task TheChangeTheEntryAndTheEndedSessionsAreOneUnitOfWork()
    {
        // An entry saved apart from the change can outlive a rollback, and sessions that ended
        // apart from the password could be left live by a failure between the two.
        var alice = Member("alice@example.com", Role.Reader);
        var other = _keys.Seed(alice.Id, ApiKeyKind.Session);
        CallerIs(alice, Role.Reader);

        var atCommit = (Staged: -1, Live: true, Hash: string.Empty);
        _unitOfWork.OnSave = () =>
            atCommit = (_audit.Entries.Count, Live(other.Key), alice.PasswordHash);

        await ChangeOwnAsync(OldPassword, NewPassword);

        Assert.Equal(1, atCommit.Staged);
        Assert.False(atCommit.Live);
        Assert.Equal(FakePasswordHasher.HashOf(NewPassword), atCommit.Hash);
        Assert.Equal(1, _unitOfWork.Saves);
    }

    [Fact]
    public async Task ACredentialNotTiedToAPersonHasNoPasswordToChange()
    {
        _caller.Current = new Caller(
            CredentialKind.ApiKey,
            TenantId.SelfHosted,
            Roles.SetFor(Role.Owner),
            Build.Actor("key:ci"),
            UserId: null,
            ApiKeyId: "somekey");

        var result = await ChangeOwnAsync(OldPassword, NewPassword);

        Assert.Equal(ConcordatCodes.UserNotFound, result.Error!.Code);
        AssertNothingWritten();
    }

    // ---------------------------------------------------- reset a member's password

    [Fact]
    public async Task ResettingAPassword_StoresTheTemporaryOneAndRequiresItToBeChanged()
    {
        var owner = Member("owner@example.com", Role.Owner);
        var bob = Member("bob@example.com", Role.Reader);
        CallerIs(owner);

        var result = await ResetAsync(bob);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(FakePasswordHasher.HashOf(Temporary), bob.PasswordHash);
        Assert.True(bob.MustChangePassword);
        Assert.Equal(1, _unitOfWork.Saves);

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal(AuditAction.MemberPasswordReset, entry.Action);
        Assert.Equal("owner@example.com", entry.Actor.Value);
        Assert.Equal("bob@example.com", entry.Target);
    }

    [Fact]
    public async Task ATemporaryPasswordIsNeverAuditedOrReturned()
    {
        var owner = Member("owner@example.com", Role.Owner);
        var bob = Member("bob@example.com", Role.Reader);
        CallerIs(owner);

        var result = await ResetAsync(bob);

        var entry = Assert.Single(_audit.Entries);
        var everything = $"{entry.Target}|{entry.Detail}|{result.Value.User.Email}|{result.Value.Role}";

        Assert.DoesNotContain(Temporary, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(OldPassword, everything, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResettingAPassword_EndsEverySessionTheMemberHadAndLeavesTheirKeys()
    {
        var owner = Member("owner@example.com", Role.Owner);
        var bob = Member("bob@example.com", Role.Reader);
        var one = _keys.Seed(bob.Id, ApiKeyKind.Session);
        var two = _keys.Seed(bob.Id, ApiKeyKind.Session);
        var standing = _keys.Seed(bob.Id, ApiKeyKind.Standing, label: "ci");
        CallerIs(owner);

        var result = await ResetAsync(bob);

        Assert.Equal(2, result.Value.CredentialsEnded);
        Assert.False(Live(one.Key));
        Assert.False(Live(two.Key));
        Assert.True(Live(standing.Key));
    }

    [Fact]
    public async Task ResettingAPassword_DoesNotEnableADisabledMember()
    {
        var owner = Member("owner@example.com", Role.Owner);
        var bob = Member("bob@example.com", Role.Reader, disabled: true);
        CallerIs(owner);

        var result = await ResetAsync(bob);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(bob.Disabled);
    }

    [Fact]
    public async Task ResettingAPassword_WorksOnAnOwner()
    {
        var owner = Member("owner@example.com", Role.Owner);
        var other = Member("other@example.com", Role.Owner);
        CallerIs(owner);

        var result = await ResetAsync(other);

        Assert.True(result.IsSuccess, result.Error?.Message);
    }

    [Fact]
    public async Task YouCannotResetYourOwnPassword()
    {
        // It would change a password without proving the current one is known, which is the proof
        // a password change exists to ask for: a stolen session could lock the real owner out.
        var owner = Member("owner@example.com", Role.Owner);
        CallerIs(owner);

        var result = await ResetAsync(owner);

        Assert.Equal(ConcordatCodes.OwnAccount, result.Error!.Code);
        Assert.Equal(FakePasswordHasher.HashOf(OldPassword), owner.PasswordHash);
        AssertNothingWritten();
    }

    [Fact]
    public async Task ACredentialThatHoldsLessThanTheMemberCannotResetThem()
    {
        // A key issued with only org:admin administers the organisation and holds none of what an
        // Owner holds. Letting it set an Owner's password would be signing in as them.
        var owner = Member("owner@example.com", Role.Owner);
        var other = Member("other@example.com", Role.Owner);
        CallerIs(owner);
        _caller.Holding(Scope.OrgAdmin);

        var result = await ResetAsync(other);

        Assert.Equal(ConcordatCodes.MemberOutranksCaller, result.Error!.Code);
        Assert.Equal(FakePasswordHasher.HashOf(OldPassword), other.PasswordHash);
        AssertNothingWritten();
    }

    [Fact]
    public async Task ACredentialWithTheScopesTheMemberHoldsMayResetThem()
    {
        var owner = Member("owner@example.com", Role.Owner);
        var admin = Member("admin@example.com", Role.Admin);
        CallerIs(owner);
        _caller.Holding([.. Roles.ScopesFor(Role.Admin), Scope.OrgAdmin]);

        var result = await ResetAsync(admin);

        Assert.True(result.IsSuccess, result.Error?.Message);
    }

    [Fact]
    public async Task AMemberOfAnotherOrganisationIsNotFound()
    {
        var owner = Member("owner@example.com", Role.Owner);
        var elsewhere = _users.Seed(
            "elsewhere@example.com", Role.Reader, tenant: TenantId.New());
        CallerIs(owner);

        var result = await ResetAsync(elsewhere);

        Assert.Equal(ConcordatCodes.UserNotFound, result.Error!.Code);
        AssertNothingWritten();
    }

    [Fact]
    public async Task AnUnknownMemberIsNotFound()
    {
        var owner = Member("owner@example.com", Role.Owner);
        CallerIs(owner);

        var result = await new ResetMemberPasswordHandler(
                _users, _keys, _passwords, _audit, _unitOfWork, _caller, _clock)
            .HandleAsync(
                new ResetMemberPasswordCommand(Guid.NewGuid(), Temporary), CancellationToken.None);

        Assert.Equal(ConcordatCodes.UserNotFound, result.Error!.Code);
        AssertNothingWritten();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("short")]
    public async Task ATemporaryPasswordMeetsTheSameRuleAsEveryOther(string? password)
    {
        var owner = Member("owner@example.com", Role.Owner);
        var bob = Member("bob@example.com", Role.Reader);
        CallerIs(owner);

        var result = await ResetAsync(bob, password);

        Assert.Equal(ConcordatCodes.PasswordInvalid, result.Error!.Code);
        Assert.Equal(FakePasswordHasher.HashOf(OldPassword), bob.PasswordHash);
        Assert.False(bob.MustChangePassword);
        AssertNothingWritten();
    }

    // ----------------------------------------------------------- disable and enable

    [Fact]
    public async Task Disabling_StopsTheAccountAndAuditsItAsTheCaller()
    {
        var owner = Member("owner@example.com", Role.Owner);
        var bob = Member("bob@example.com", Role.Reader);
        CallerIs(owner);

        var result = await SetDisabledAsync(bob, disabled: true);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(bob.Disabled);
        Assert.True(result.Value.User.Disabled);
        Assert.Equal(1, _unitOfWork.Saves);

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal(AuditAction.MemberDisabled, entry.Action);
        Assert.Equal("owner@example.com", entry.Actor.Value);
        Assert.Equal("bob@example.com", entry.Target);
    }

    [Fact]
    public async Task Disabling_RevokesEveryCredentialTheMemberHolds()
    {
        // Their sessions, which sign-in would refuse but which are still good for their lifetime,
        // and the API keys they issued, which carry their authority.
        var owner = Member("owner@example.com", Role.Owner);
        var bob = Member("bob@example.com", Role.Admin);
        var session = _keys.Seed(bob.Id, ApiKeyKind.Session);
        var standing = _keys.Seed(bob.Id, ApiKeyKind.Standing, label: "ci");
        var alreadyRevoked = _keys.Seed(bob.Id, ApiKeyKind.Standing, label: "old");
        alreadyRevoked.Key.Revoke(Build.At.AddMinutes(-5));
        var revokedAt = alreadyRevoked.Key.RevokedAt;
        CallerIs(owner);

        var result = await SetDisabledAsync(bob, disabled: true);

        Assert.Equal(2, result.Value.CredentialsEnded);
        Assert.False(Live(session.Key));
        Assert.False(Live(standing.Key));
        Assert.Equal(revokedAt, alreadyRevoked.Key.RevokedAt);
        Assert.Equal("2 credentials revoked", Assert.Single(_audit.Entries).Detail);
    }

    [Fact]
    public async Task Disabling_LeavesEveryoneElsesCredentialsAlone()
    {
        var owner = Member("owner@example.com", Role.Owner);
        var bob = Member("bob@example.com", Role.Reader);
        var ownersSession = _keys.Seed(owner.Id, ApiKeyKind.Session);
        var detached = _keys.Seed(user: null, ApiKeyKind.Standing, label: "not a person's");
        CallerIs(owner);

        await SetDisabledAsync(bob, disabled: true);

        Assert.True(Live(ownersSession.Key));
        Assert.True(Live(detached.Key));
    }

    [Fact]
    public async Task TheDisablementTheEntryAndTheRevokedCredentialsAreOneUnitOfWork()
    {
        var owner = Member("owner@example.com", Role.Owner);
        var bob = Member("bob@example.com", Role.Reader);
        var session = _keys.Seed(bob.Id, ApiKeyKind.Session);
        CallerIs(owner);

        var atCommit = (Staged: -1, Live: true, Disabled: false);
        _unitOfWork.OnSave = () =>
            atCommit = (_audit.Entries.Count, Live(session.Key), bob.Disabled);

        await SetDisabledAsync(bob, disabled: true);

        Assert.Equal((1, false, true), atCommit);
        Assert.Equal(1, _unitOfWork.Saves);
    }

    [Fact]
    public async Task Disabling_SomeoneAlreadyDisabledIsAnAnswerNotAChange()
    {
        // A repeat writes nothing: no second entry for a change that did not happen.
        var owner = Member("owner@example.com", Role.Owner);
        var bob = Member("bob@example.com", Role.Reader, disabled: true);
        CallerIs(owner);

        var result = await SetDisabledAsync(bob, disabled: true);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(result.Value.User.Disabled);
        Assert.Equal(0, result.Value.CredentialsEnded);
        AssertNothingWritten();
    }

    [Fact]
    public async Task Enabling_AllowsSignInAgainButDoesNotRestoreRevokedCredentials()
    {
        // Revocation is terminal (ApiKey.Revoke). Coming back means signing in again, and whatever
        // the person issued before is re-issued on purpose.
        var owner = Member("owner@example.com", Role.Owner);
        var bob = Member("bob@example.com", Role.Reader);
        var standing = _keys.Seed(bob.Id, ApiKeyKind.Standing, label: "ci");
        CallerIs(owner);
        await SetDisabledAsync(bob, disabled: true);
        _audit.Clear();
        _unitOfWork.OnSave = null;

        var result = await SetDisabledAsync(bob, disabled: false);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.False(bob.Disabled);
        Assert.False(Live(standing.Key));

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal(AuditAction.MemberEnabled, entry.Action);
        Assert.Equal("owner@example.com", entry.Actor.Value);
        Assert.Equal("bob@example.com", entry.Target);
    }

    [Fact]
    public async Task Enabling_SomeoneWhoIsNotDisabledIsAnAnswerNotAChange()
    {
        var owner = Member("owner@example.com", Role.Owner);
        var bob = Member("bob@example.com", Role.Reader);
        CallerIs(owner);

        var result = await SetDisabledAsync(bob, disabled: false);

        Assert.True(result.IsSuccess, result.Error?.Message);
        AssertNothingWritten();
    }

    [Fact]
    public async Task YouCannotDisableYourself()
    {
        var owner = Member("owner@example.com", Role.Owner);
        var second = Member("second@example.com", Role.Owner);
        CallerIs(owner);

        var result = await SetDisabledAsync(owner, disabled: true);

        Assert.Equal(ConcordatCodes.OwnAccount, result.Error!.Code);
        Assert.False(owner.Disabled);
        Assert.False(second.Disabled);
        AssertNothingWritten();
    }

    [Fact]
    public async Task TheLastEnabledOwnerCannotBeDisabled()
    {
        // Asked by someone who is not the owner (a credential holding owner scopes), so "you cannot
        // disable yourself" is not what stops it: the rule is about the organisation, not about who
        // is asking.
        var owner = Member("owner@example.com", Role.Owner);
        var admin = Member("admin@example.com", Role.Admin);
        CallerIs(admin, Role.Owner);

        var result = await SetDisabledAsync(owner, disabled: true);

        Assert.Equal(ConcordatCodes.LastOwner, result.Error!.Code);
        Assert.False(owner.Disabled);
        AssertNothingWritten();
    }

    [Fact]
    public async Task ADisabledOwnerDoesNotCountAsAnotherOwner()
    {
        var owner = Member("owner@example.com", Role.Owner);
        Member("gone@example.com", Role.Owner, disabled: true);
        var admin = Member("admin@example.com", Role.Admin);
        CallerIs(admin, Role.Owner);

        var result = await SetDisabledAsync(owner, disabled: true);

        Assert.Equal(ConcordatCodes.LastOwner, result.Error!.Code);
        AssertNothingWritten();
    }

    [Fact]
    public async Task AnOwnerCanBeDisabledWhileAnotherEnabledOwnerRemains()
    {
        var owner = Member("owner@example.com", Role.Owner);
        var other = Member("other@example.com", Role.Owner);
        CallerIs(owner);

        var result = await SetDisabledAsync(other, disabled: true);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(other.Disabled);
    }

    [Fact]
    public async Task ADisabledAccountThatIsNotAnOwnerNeedsNoOwnerToSpare()
    {
        var owner = Member("owner@example.com", Role.Owner);
        var admin = Member("admin@example.com", Role.Admin);
        CallerIs(owner);

        var result = await SetDisabledAsync(admin, disabled: true);

        Assert.True(result.IsSuccess, result.Error?.Message);
    }

    [Fact]
    public async Task ACredentialThatHoldsLessThanTheMemberCannotDisableThem()
    {
        var owner = Member("owner@example.com", Role.Owner);
        var other = Member("other@example.com", Role.Owner);
        CallerIs(owner);
        _caller.Holding(Scope.OrgAdmin);

        var result = await SetDisabledAsync(other, disabled: true);

        Assert.Equal(ConcordatCodes.MemberOutranksCaller, result.Error!.Code);
        Assert.False(other.Disabled);
        AssertNothingWritten();
    }

    [Fact]
    public async Task ACredentialThatHoldsLessThanTheMemberCannotEnableThemEither()
    {
        var owner = Member("owner@example.com", Role.Owner);
        var other = Member("other@example.com", Role.Owner, disabled: true);
        CallerIs(owner);
        _caller.Holding(Scope.OrgAdmin);

        var result = await SetDisabledAsync(other, disabled: false);

        Assert.Equal(ConcordatCodes.MemberOutranksCaller, result.Error!.Code);
        Assert.True(other.Disabled);
        AssertNothingWritten();
    }

    [Fact]
    public async Task DisablingAMemberOfAnotherOrganisationIsNotFound()
    {
        var owner = Member("owner@example.com", Role.Owner);
        var elsewhere = _users.Seed(
            "elsewhere@example.com", Role.Reader, tenant: TenantId.New());
        CallerIs(owner);

        var result = await SetDisabledAsync(elsewhere, disabled: true);

        Assert.Equal(ConcordatCodes.UserNotFound, result.Error!.Code);
        Assert.False(elsewhere.Disabled);
        AssertNothingWritten();
    }

    // ------------------------------------------------------------- the role guard

    [Fact]
    public async Task TheOnlyOwnerCannotBeDemoted()
    {
        var owner = Member("owner@example.com", Role.Owner);
        CallerIs(owner);

        var result = await ChangeRoleAsync(owner, "ADMIN");

        Assert.Equal(ConcordatCodes.LastOwner, result.Error!.Code);
        AssertNothingWritten();
    }

    [Fact]
    public async Task TheOnlyEnabledOwnerCannotBeDemotedWhenTheOtherOwnerIsDisabled()
    {
        // The guard counted every owner, so with one disabled it let the one who could sign in
        // demote themselves and left an organisation nobody could administer.
        var owner = Member("owner@example.com", Role.Owner);
        Member("gone@example.com", Role.Owner, disabled: true);
        CallerIs(owner);

        var result = await ChangeRoleAsync(owner, "READER");

        Assert.Equal(ConcordatCodes.LastOwner, result.Error!.Code);
        AssertNothingWritten();
    }

    [Fact]
    public async Task AnOwnerCanBeDemotedWhileAnotherEnabledOwnerRemains()
    {
        var owner = Member("owner@example.com", Role.Owner);
        var other = Member("other@example.com", Role.Owner);
        CallerIs(owner);

        var result = await ChangeRoleAsync(other, "ADMIN");

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(Role.Admin, result.Value.Role);
        Assert.Equal(AuditAction.MemberRoleChanged, Assert.Single(_audit.Entries).Action);
    }

    [Fact]
    public async Task ASoleOwnerStaysOwnerWhenTheRoleIsOwner()
    {
        var owner = Member("owner@example.com", Role.Owner);
        CallerIs(owner);

        var result = await ChangeRoleAsync(owner, "OWNER");

        Assert.True(result.IsSuccess, result.Error?.Message);
    }

    [Fact]
    public async Task AReaderCanBePromotedToOwnerBySomeoneElse()
    {
        var owner = Member("owner@example.com", Role.Owner);
        var bob = Member("bob@example.com", Role.Reader);
        CallerIs(owner);

        var result = await ChangeRoleAsync(bob, "OWNER");

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(Role.Owner, result.Value.Role);
    }
}
