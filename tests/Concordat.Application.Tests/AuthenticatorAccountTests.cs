using Concordat.Application.Abstractions;
using Concordat.Application.Identity;
using Concordat.Application.Tests.TestSupport;
using Concordat.Domain.Identity;
using Concordat.Domain.Registry;
using Microsoft.Extensions.Time.Testing;

namespace Concordat.Application.Tests;

/// <summary>
/// What a credential means once the account behind it has been disabled, or told to change its
/// password (ADR-048).
/// </summary>
/// <remarks>
/// Revoking a disabled member's credentials is what ends them, but it is a write at one moment and
/// a request can be in flight. The authenticator is where "stops working immediately" is true
/// whatever else happened, so it is asked here without any revocation having been done.
/// </remarks>
public class AuthenticatorAccountTests
{
    private readonly FakeUsers _users = new();
    private readonly FakeApiKeys _keys = new();
    private readonly FakePasswordHasher _passwords = new();
    private readonly RecordingUnitOfWork _unitOfWork = new();
    private readonly FakeTimeProvider _clock = new(Build.At);

    private Authenticator Authenticator() =>
        new(_keys, _users, new FakeTenants(), _passwords, _unitOfWork, _clock);

    [Fact]
    public async Task AKeyOfAnEnabledPersonAuthenticatesWithTheScopesItWasIssuedWith()
    {
        var alice = _users.Seed("alice@example.com", Role.Admin);
        var (key, secret) = _keys.Seed(alice.Id, ApiKeyKind.Session, [Scope.SubjectWrite]);

        var caller = await Authenticator().FromApiKeyAsync(secret, CancellationToken.None);

        Assert.True(caller.IsAuthenticated);
        Assert.Equal(alice.Id, caller.UserId);
        Assert.Equal(key.KeyId, caller.ApiKeyId);
        Assert.True(caller.Allows(Scope.SubjectWrite));
        Assert.False(caller.MustChangePassword);
    }

    [Theory]
    [InlineData(ApiKeyKind.Session)]
    [InlineData(ApiKeyKind.Standing)]
    public async Task AKeyOfADisabledPersonIsAnonymousEvenThoughNobodyRevokedIt(ApiKeyKind kind)
    {
        var alice = _users.Seed("alice@example.com", Role.Owner, disabled: true);
        var (key, secret) = _keys.Seed(alice.Id, kind, [Scope.SubjectAdmin]);

        var caller = await Authenticator().FromApiKeyAsync(secret, CancellationToken.None);

        Assert.False(caller.IsAuthenticated);
        Assert.Empty(caller.Scopes.Granted);
        Assert.Null(key.RevokedAt);
    }

    [Fact]
    public async Task AKeyOfADisabledPersonDoesNotEvenRecordTheAttempt()
    {
        // A write per request from somebody who must not be here is a lever, and "last used" on a
        // key would read as the key having worked.
        var alice = _users.Seed("alice@example.com", Role.Reader, disabled: true);
        var (key, secret) = _keys.Seed(alice.Id, ApiKeyKind.Standing);

        await Authenticator().FromApiKeyAsync(secret, CancellationToken.None);

        Assert.Null(key.LastUsedAt);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task AKeyWhoseAccountNoLongerExistsIsAnonymous()
    {
        var (_, secret) = _keys.Seed(UserId.New(), ApiKeyKind.Standing);

        var caller = await Authenticator().FromApiKeyAsync(secret, CancellationToken.None);

        Assert.False(caller.IsAuthenticated);
    }

    [Fact]
    public async Task AKeyNotTiedToAPersonNeedsNoAccountToAuthenticate()
    {
        // Keys issued while an instance was open to the unclaimed caller carry no user.
        var (_, secret) = _keys.Seed(user: null, ApiKeyKind.Standing, [Scope.SubjectRead]);

        var caller = await Authenticator().FromApiKeyAsync(secret, CancellationToken.None);

        Assert.True(caller.IsAuthenticated);
        Assert.Null(caller.UserId);
    }

    [Fact]
    public async Task ASessionOfAPersonWhoMustChangeTheirPasswordHoldsNothing()
    {
        // Authenticated, because the one thing they may do is prove who they are and replace the
        // password, and holding nothing, because everything else waits until they have.
        var alice = _users.Seed("alice@example.com", Role.Owner);
        alice.ResetPassword(FakePasswordHasher.HashOf("temporary"));
        var (_, secret) = _keys.Seed(
            alice.Id,
            ApiKeyKind.Session,
            [.. Roles.ScopesFor(Role.Owner)],
            actor: alice.Actor());

        var caller = await Authenticator().FromApiKeyAsync(secret, CancellationToken.None);

        Assert.True(caller.IsAuthenticated);
        Assert.True(caller.MustChangePassword);
        Assert.Equal(alice.Id, caller.UserId);
        Assert.Equal("alice@example.com", caller.Actor.Value);
        Assert.Empty(caller.Scopes.Granted);
        Assert.All(Scope.All, scope => Assert.False(caller.Allows(scope)));
    }

    [Fact]
    public async Task AStandingKeyOfAPersonWhoMustChangeTheirPasswordIsNotLimited()
    {
        // A reset ends sessions and leaves keys alone. A key is not made with the password, and a
        // pipeline cannot change anybody's password, so limiting it would only stop the pipeline.
        var alice = _users.Seed("alice@example.com", Role.Owner);
        alice.ResetPassword(FakePasswordHasher.HashOf("temporary"));
        var (_, secret) = _keys.Seed(
            alice.Id, ApiKeyKind.Standing, [Scope.SubjectRead], label: "ci");

        var caller = await Authenticator().FromApiKeyAsync(secret, CancellationToken.None);

        Assert.True(caller.IsAuthenticated);
        Assert.False(caller.MustChangePassword);
        Assert.True(caller.Allows(Scope.SubjectRead));
    }

    [Fact]
    public async Task TheSameKeyHoldsEverythingAgainOnceThePasswordIsChanged()
    {
        // The credential that was limited is not replaced when the password changes: its scopes were
        // never cut, the limit was applied on the way in.
        var alice = _users.Seed("alice@example.com", Role.Owner);
        alice.ResetPassword(FakePasswordHasher.HashOf("temporary"));
        var (_, secret) = _keys.Seed(alice.Id, ApiKeyKind.Session, [.. Roles.ScopesFor(Role.Owner)]);
        Assert.Empty((await Authenticator().FromApiKeyAsync(secret, CancellationToken.None)).Scopes.Granted);

        alice.ChangePassword(FakePasswordHasher.HashOf("chosen by alice"));

        var caller = await Authenticator().FromApiKeyAsync(secret, CancellationToken.None);

        Assert.False(caller.MustChangePassword);
        Assert.True(caller.Allows(Scope.OrgAdmin));
    }

    [Fact]
    public async Task APasswordSignInOfAPersonWhoMustChangeItSaysSo()
    {
        var alice = _users.Seed(
            "alice@example.com", Role.Reader, passwordHash: FakePasswordHasher.HashOf("temporary"));
        alice.ResetPassword(FakePasswordHasher.HashOf("temporary"));

        var caller = await Authenticator().FromPasswordAsync(
            "alice@example.com", "temporary", null, CancellationToken.None);

        Assert.True(caller.IsAuthenticated);
        Assert.True(caller.MustChangePassword);
    }

    [Fact]
    public async Task APasswordSignInOfADisabledPersonIsStillAnonymous()
    {
        _users.Seed(
            "alice@example.com",
            Role.Reader,
            disabled: true,
            passwordHash: FakePasswordHasher.HashOf("the right password"));

        var caller = await Authenticator().FromPasswordAsync(
            "alice@example.com", "the right password", null, CancellationToken.None);

        Assert.False(caller.IsAuthenticated);
    }
}
