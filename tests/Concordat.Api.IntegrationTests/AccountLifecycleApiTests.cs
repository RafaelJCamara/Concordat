using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Concordat.Domain.Identity;

namespace Concordat.Api.IntegrationTests;

/// <summary>
/// Changing a password, resetting somebody else's, and disabling or enabling a member, over HTTP
/// against a real PostgreSQL (ADR-048).
/// </summary>
/// <remarks>
/// <para>
/// Its own host and database (<see cref="IClassFixture{TFixture}"/>, not the shared collection),
/// because it disables and resets accounts and the other suites rely on theirs staying usable. Every
/// test makes the members it acts on, and nothing here touches the owner except to act as them.
/// </para>
/// <para>
/// <b>The central question is "does the old credential stop working", asked of the server and
/// answered by a 401</b>, for a session and for a standing key, not inferred from a flag being set.
/// </para>
/// </remarks>
public class AccountLifecycleApiTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string MemberPassword = "a member's first password";
    private const string NewPassword = "a freshly chosen password";
    private const string Temporary = "temporary password 1";

    private HttpClient Anonymous() => factory.CreateClient();

    private HttpClient With(string credential)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", credential);

        return client;
    }

    private async Task<HttpClient> OwnerAsync() => With(await factory.OwnerCredentialAsync());

    private async Task<MemberResponse> CreateMemberAsync(string role, string? password = null)
    {
        var owner = await OwnerAsync();
        var email = $"{role.ToLowerInvariant()}-{Guid.NewGuid():N}@example.com";

        var created = await owner.PostAsJsonAsync(
            "/v1/members",
            new CreateMemberRequest(email, password ?? MemberPassword, role),
            ApiFactory.Json);

        return await ApiFactory.ReadAsync<MemberResponse>(created);
    }

    private async Task<SignInResponse> SignInAsync(string email, string password)
    {
        var response = await Anonymous().PostAsJsonAsync(
            "/v1/auth/signin", new SignInRequest(email, password), ApiFactory.Json);

        return await ApiFactory.ReadAsync<SignInResponse>(response);
    }

    private async Task<string> IssueKeyAsync(string credential, string label = "ci")
    {
        var issued = await With(credential).PostAsJsonAsync(
            "/v1/api-keys",
            new IssueApiKeyRequest(label, [Scope.EnvRead]),
            ApiFactory.Json);

        return (await ApiFactory.ReadAsync<IssuedApiKeyResponse>(issued)).Secret;
    }

    private static Task<HttpResponseMessage> ChangePasswordAsync(
        HttpClient client, string current, string replacement) =>
        client.PostAsJsonAsync(
            "/v1/auth/password",
            new ChangePasswordRequest(current, replacement),
            ApiFactory.Json);

    private static Task<HttpResponseMessage> ResetAsync(
        HttpClient client, Guid userId, string password = Temporary) =>
        client.PutAsJsonAsync(
            $"/v1/members/{userId}/password", new ResetPasswordRequest(password), ApiFactory.Json);

    private static Task<HttpResponseMessage> SetDisabledAsync(
        HttpClient client, Guid userId, bool disabled) =>
        client.PutAsJsonAsync(
            $"/v1/members/{userId}/disabled",
            new SetDisabledRequest(disabled),
            ApiFactory.Json);

    /// <summary>A read every role may make, so a 401 means the credential is dead.</summary>
    private static Task<HttpResponseMessage> ReadAsync(HttpClient client) =>
        client.GetAsync("/v1/environments");

    private async Task<IReadOnlyList<AuditResponse>> AuditAsync(string action, string target)
    {
        var owner = await OwnerAsync();

        return (await owner.GetFromJsonAsync<IReadOnlyList<AuditResponse>>(
            $"/v1/audit?action={action}&target={Uri.EscapeDataString(target)}&limit=100",
            ApiFactory.Json))!;
    }

    private async Task<MemberResponse> MemberAsync(Guid userId)
    {
        var owner = await OwnerAsync();
        var members = await owner.GetFromJsonAsync<IReadOnlyList<MemberResponse>>(
            "/v1/members", ApiFactory.Json);

        return Assert.Single(members!, m => m.UserId == userId);
    }

    private static async Task AssertProblemAsync(
        HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await ApiFactory.ReadProblemAsync(response)).ConcordatCode);
    }

    // ----------------------------------------------------------- change my own password

    [Fact]
    public async Task ChangingYourPassword_ReplacesItAndTheSessionYouUsedKeepsWorking()
    {
        var member = await CreateMemberAsync("READER");
        var session = await SignInAsync(member.Email, MemberPassword);

        var changed = await ChangePasswordAsync(With(session.Credential), MemberPassword, NewPassword);

        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(0, (await ApiFactory.ReadAsync<PasswordChangedResponse>(changed)).OtherSessionsSignedOut);

        Assert.Equal(HttpStatusCode.OK, (await ReadAsync(With(session.Credential))).StatusCode);

        var old = await Anonymous().PostAsJsonAsync(
            "/v1/auth/signin", new SignInRequest(member.Email, MemberPassword), ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);

        Assert.False((await SignInAsync(member.Email, NewPassword)).MustChangePassword);
    }

    [Fact]
    public async Task ChangingYourPassword_SignsOutYourOtherSessionsButNotYourApiKeys()
    {
        // An owner, because only an owner can issue the standing key whose survival is the point.
        var member = await CreateMemberAsync("OWNER");
        var current = await SignInAsync(member.Email, MemberPassword);
        var other = await SignInAsync(member.Email, MemberPassword);
        var standingKey = await IssueKeyAsync(current.Credential);

        var changed = await ChangePasswordAsync(With(current.Credential), MemberPassword, NewPassword);

        Assert.Equal(1, (await ApiFactory.ReadAsync<PasswordChangedResponse>(changed)).OtherSessionsSignedOut);
        Assert.Equal(HttpStatusCode.OK, (await ReadAsync(With(current.Credential))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ReadAsync(With(other.Credential))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ReadAsync(With(standingKey))).StatusCode);
    }

    [Fact]
    public async Task AWrongCurrentPasswordIsTheRefusalASignInGivesAndChangesNothing()
    {
        var member = await CreateMemberAsync("READER");
        var session = await SignInAsync(member.Email, MemberPassword);
        var other = await SignInAsync(member.Email, MemberPassword);

        var response = await ChangePasswordAsync(
            With(session.Credential), "not my password", NewPassword);

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "unauthenticated");

        // Nothing moved: the password is the one it was, the new one was not stored, and no session
        // was ended by the attempt.
        Assert.Equal(HttpStatusCode.OK, (await ReadAsync(With(session.Credential))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ReadAsync(With(other.Credential))).StatusCode);
        _ = await SignInAsync(member.Email, MemberPassword);

        var attempt = await Anonymous().PostAsJsonAsync(
            "/v1/auth/signin", new SignInRequest(member.Email, NewPassword), ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Unauthorized, attempt.StatusCode);
        Assert.Empty(await AuditAsync("PASSWORD_CHANGED", member.Email));
    }

    [Fact]
    public async Task ARefusedChangeNeverTellsTheCallerAboutTheNewPassword()
    {
        var member = await CreateMemberAsync("READER");
        var session = await SignInAsync(member.Email, MemberPassword);

        var response = await ChangePasswordAsync(With(session.Credential), "wrong one", "short");

        // The current password is judged first, so a caller who does not know it learns nothing
        // about the rule the new one would have had to meet.
        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "unauthenticated");
    }

    [Theory]
    [InlineData("short")]
    [InlineData("elevenchars")]
    [InlineData("")]
    public async Task ANewPasswordUnderTwelveCharactersIsRefusedAndNothingIsWritten(string replacement)
    {
        var member = await CreateMemberAsync("READER");
        var session = await SignInAsync(member.Email, MemberPassword);

        var response = await ChangePasswordAsync(With(session.Credential), MemberPassword, replacement);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "password_invalid");
        _ = await SignInAsync(member.Email, MemberPassword);
        Assert.Empty(await AuditAsync("PASSWORD_CHANGED", member.Email));
    }

    [Fact]
    public async Task TheNewPasswordMustDifferFromTheCurrentOne()
    {
        var member = await CreateMemberAsync("READER");
        var session = await SignInAsync(member.Email, MemberPassword);

        var response = await ChangePasswordAsync(With(session.Credential), MemberPassword, MemberPassword);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "password_invalid");
    }

    [Fact]
    public async Task AReaderMayChangeTheirOwnPasswordWithNoScopeAtAll()
    {
        // Not an org:admin matter: every person owns their own password.
        var member = await CreateMemberAsync("READER");
        var session = await SignInAsync(member.Email, MemberPassword);

        var response = await ChangePasswordAsync(With(session.Credential), MemberPassword, NewPassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ChangingAPasswordWithNoCredentialIs401()
    {
        var response = await ChangePasswordAsync(Anonymous(), MemberPassword, NewPassword);

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "unauthenticated");
    }

    [Fact]
    public async Task APasswordChangeIsAuditedAsThePersonWhoMadeIt_WithNoPasswordInTheTrail()
    {
        var member = await CreateMemberAsync("READER");
        var session = await SignInAsync(member.Email, MemberPassword);

        var changed = await ChangePasswordAsync(With(session.Credential), MemberPassword, NewPassword);
        var body = await changed.Content.ReadAsStringAsync();

        var entry = Assert.Single(await AuditAsync("PASSWORD_CHANGED", member.Email));
        Assert.Equal(member.Email, entry.Actor);
        Assert.Equal(member.Email, entry.Target);

        var owner = await OwnerAsync();
        var trail = await owner.GetStringAsync("/v1/audit?limit=500");

        foreach (var secret in new[] { MemberPassword, NewPassword })
        {
            Assert.DoesNotContain(secret, trail, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
        }
    }

    // ------------------------------------------------------ reset a member's password

    [Fact]
    public async Task AResetEndsTheMembersSessionsAndTheTemporaryPasswordSignsIn()
    {
        var member = await CreateMemberAsync("READER");
        var session = await SignInAsync(member.Email, MemberPassword);
        var owner = await OwnerAsync();

        var reset = await ResetAsync(owner, member.UserId);
        var body = await ApiFactory.ReadAsync<MemberPasswordResetResponse>(reset);

        Assert.True(body.MustChangePassword);
        Assert.Equal(1, body.SessionsSignedOut);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ReadAsync(With(session.Credential))).StatusCode);

        var old = await Anonymous().PostAsJsonAsync(
            "/v1/auth/signin", new SignInRequest(member.Email, MemberPassword), ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);

        var temporary = await SignInAsync(member.Email, Temporary);
        Assert.True(temporary.MustChangePassword);
    }

    [Fact]
    public async Task ACredentialForAnAccountThatMustChangeItsPasswordCanDoNothingElse()
    {
        var member = await CreateMemberAsync("OWNER");
        var owner = await OwnerAsync();
        await ResetAsync(owner, member.UserId);

        var limited = await SignInAsync(member.Email, Temporary);
        var client = With(limited.Credential);

        // Told so, and holding nothing: a client that reads this knows where to send the person.
        Assert.True(limited.MustChangePassword);
        Assert.Empty(limited.Scopes);

        var status = await client.GetFromJsonAsync<AuthStatusResponse>(
            "/v1/auth/status", ApiFactory.Json);
        Assert.True(status!.Authenticated);
        Assert.True(status.MustChangePassword);
        Assert.Empty(status.Scopes);

        // An owner's credential, refused everything that needs a scope, reads included.
        await AssertProblemAsync(
            await ReadAsync(client), HttpStatusCode.Forbidden, "password_change_required");
        await AssertProblemAsync(
            await client.GetAsync("/v1/members"), HttpStatusCode.Forbidden, "password_change_required");
        await AssertProblemAsync(
            await client.PostAsJsonAsync(
                "/v1/api-keys", new IssueApiKeyRequest("x", [Scope.SubjectRead]), ApiFactory.Json),
            HttpStatusCode.Forbidden,
            "password_change_required");
    }

    [Fact]
    public async Task ChangingTheTemporaryPasswordGivesTheSameCredentialEverythingBack()
    {
        var member = await CreateMemberAsync("OWNER");
        var owner = await OwnerAsync();
        await ResetAsync(owner, member.UserId);
        var limited = await SignInAsync(member.Email, Temporary);
        var client = With(limited.Credential);

        var changed = await ChangePasswordAsync(client, Temporary, NewPassword);

        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ReadAsync(client)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/members")).StatusCode);

        var status = await client.GetFromJsonAsync<AuthStatusResponse>(
            "/v1/auth/status", ApiFactory.Json);
        Assert.False(status!.MustChangePassword);
        Assert.Contains(Scope.OrgAdmin, status.Scopes);

        Assert.False((await MemberAsync(member.UserId)).MustChangePassword);
        Assert.False((await SignInAsync(member.Email, NewPassword)).MustChangePassword);

        // The temporary password, known to the administrator who typed it, is no longer a password.
        var temporary = await Anonymous().PostAsJsonAsync(
            "/v1/auth/signin", new SignInRequest(member.Email, Temporary), ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Unauthorized, temporary.StatusCode);
    }

    [Fact]
    public async Task ReplacingTheTemporaryPasswordWithItselfDoesNotCountAsAChange()
    {
        var member = await CreateMemberAsync("READER");
        var owner = await OwnerAsync();
        await ResetAsync(owner, member.UserId);
        var client = With((await SignInAsync(member.Email, Temporary)).Credential);

        var response = await ChangePasswordAsync(client, Temporary, Temporary);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "password_invalid");
        Assert.True((await MemberAsync(member.UserId)).MustChangePassword);
    }

    [Fact]
    public async Task AResetLeavesTheMembersApiKeysAlone()
    {
        var member = await CreateMemberAsync("OWNER");
        var key = await IssueKeyAsync((await SignInAsync(member.Email, MemberPassword)).Credential);
        var owner = await OwnerAsync();

        await ResetAsync(owner, member.UserId);

        Assert.Equal(HttpStatusCode.OK, (await ReadAsync(With(key))).StatusCode);
    }

    [Fact]
    public async Task AResetIsAuditedAsTheAdministratorWithNoPasswordInTheTrail()
    {
        var member = await CreateMemberAsync("READER");
        var owner = await OwnerAsync();

        var reset = await ResetAsync(owner, member.UserId);
        var body = await reset.Content.ReadAsStringAsync();

        var entry = Assert.Single(await AuditAsync("MEMBER_PASSWORD_RESET", member.Email));
        Assert.Equal(AuthApiFactory.OwnerEmail, entry.Actor);
        Assert.Equal(member.Email, entry.Target);

        var trail = await owner.GetStringAsync("/v1/audit?limit=500");

        foreach (var secret in new[] { MemberPassword, Temporary })
        {
            Assert.DoesNotContain(secret, trail, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TheMembersListSaysWhoseTemporaryPasswordIsStillWaitingToBeChanged()
    {
        var member = await CreateMemberAsync("READER");
        Assert.False(member.MustChangePassword);
        var owner = await OwnerAsync();

        await ResetAsync(owner, member.UserId);

        Assert.True((await MemberAsync(member.UserId)).MustChangePassword);
    }

    [Fact]
    public async Task OnlyAnOwnerMayReset_ARefusedResetWritesNothing()
    {
        var target = await CreateMemberAsync("READER");
        var admin = await CreateMemberAsync("ADMIN");
        var reader = await CreateMemberAsync("READER");

        foreach (var who in new[] { admin, reader })
        {
            var client = With((await SignInAsync(who.Email, MemberPassword)).Credential);

            await AssertProblemAsync(
                await ResetAsync(client, target.UserId), HttpStatusCode.Forbidden, "insufficient_scope");
        }

        _ = await SignInAsync(target.Email, MemberPassword);
        Assert.Empty(await AuditAsync("MEMBER_PASSWORD_RESET", target.Email));
    }

    [Fact]
    public async Task YouCannotResetYourOwnPassword()
    {
        var self = await CreateMemberAsync("OWNER");
        var client = With((await SignInAsync(self.Email, MemberPassword)).Credential);

        await AssertProblemAsync(
            await ResetAsync(client, self.UserId), HttpStatusCode.Conflict, "own_account");

        _ = await SignInAsync(self.Email, MemberPassword);
        Assert.Empty(await AuditAsync("MEMBER_PASSWORD_RESET", self.Email));
    }

    [Fact]
    public async Task ResettingAMemberWhoDoesNotExistIs404()
    {
        var owner = await OwnerAsync();

        await AssertProblemAsync(
            await ResetAsync(owner, Guid.NewGuid()), HttpStatusCode.NotFound, "user_not_found");
    }

    [Theory]
    [InlineData("short")]
    [InlineData("")]
    public async Task ATemporaryPasswordMeetsTheSameRule_AndNothingIsWrittenWhenItDoesNot(string password)
    {
        var member = await CreateMemberAsync("READER");
        var owner = await OwnerAsync();

        await AssertProblemAsync(
            await ResetAsync(owner, member.UserId, password),
            HttpStatusCode.BadRequest,
            "password_invalid");

        _ = await SignInAsync(member.Email, MemberPassword);
        Assert.False((await MemberAsync(member.UserId)).MustChangePassword);
        Assert.Empty(await AuditAsync("MEMBER_PASSWORD_RESET", member.Email));
    }

    [Fact]
    public async Task AKeyHoldingOnlyOrgAdminCannotResetAnOwnersPassword()
    {
        // The rank rule, on the one credential that can break it: org:admin implies nothing, so this
        // key may administer the organisation and holds none of what the owner it would take over
        // does. Without the rule, resetting an owner's password is how it becomes one.
        var victim = await CreateMemberAsync("OWNER");
        var owner = await OwnerAsync();

        var issued = await ApiFactory.ReadAsync<IssuedApiKeyResponse>(
            await owner.PostAsJsonAsync(
                "/v1/api-keys",
                new IssueApiKeyRequest("narrow", [Scope.OrgAdmin]),
                ApiFactory.Json));

        var narrow = With(issued.Secret);

        await AssertProblemAsync(
            await ResetAsync(narrow, victim.UserId),
            HttpStatusCode.Forbidden,
            "member_outranks_caller");
        await AssertProblemAsync(
            await SetDisabledAsync(narrow, victim.UserId, true),
            HttpStatusCode.Forbidden,
            "member_outranks_caller");

        _ = await SignInAsync(victim.Email, MemberPassword);
        Assert.Empty(await AuditAsync("MEMBER_PASSWORD_RESET", victim.Email));
        Assert.Empty(await AuditAsync("MEMBER_DISABLED", victim.Email));
    }

    // ------------------------------------------------------------ disable and enable

    [Fact]
    public async Task ADisabledMembersSessionAndSignInStopAtOnce()
    {
        var member = await CreateMemberAsync("READER");
        var session = await SignInAsync(member.Email, MemberPassword);
        var owner = await OwnerAsync();
        Assert.Equal(HttpStatusCode.OK, (await ReadAsync(With(session.Credential))).StatusCode);

        var disabled = await SetDisabledAsync(owner, member.UserId, true);

        var body = await ApiFactory.ReadAsync<MemberDisabledResponse>(disabled);
        Assert.True(body.Disabled);
        Assert.Equal(1, body.CredentialsRevoked);

        // The session the person already held, with nobody having done anything to it but the
        // account: 401, which is the answer for a credential that does not exist.
        await AssertProblemAsync(
            await ReadAsync(With(session.Credential)), HttpStatusCode.Unauthorized, "unauthenticated");

        var signIn = await Anonymous().PostAsJsonAsync(
            "/v1/auth/signin", new SignInRequest(member.Email, MemberPassword), ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Unauthorized, signIn.StatusCode);

        var refusal = await ApiFactory.ReadProblemAsync(signIn);
        Assert.Equal("unauthenticated", refusal.ConcordatCode);
        Assert.Equal("That email and password do not match.", refusal.Detail);

        Assert.True((await MemberAsync(member.UserId)).Disabled);
    }

    [Fact]
    public async Task ADisabledMembersApiKeysStopAtOnceToo()
    {
        var member = await CreateMemberAsync("OWNER");
        var session = await SignInAsync(member.Email, MemberPassword);
        var key = await IssueKeyAsync(session.Credential, "the member's pipeline");
        var owner = await OwnerAsync();
        Assert.Equal(HttpStatusCode.OK, (await ReadAsync(With(key))).StatusCode);

        var disabled = await SetDisabledAsync(owner, member.UserId, true);

        Assert.Equal(2, (await ApiFactory.ReadAsync<MemberDisabledResponse>(disabled)).CredentialsRevoked);
        await AssertProblemAsync(
            await ReadAsync(With(key)), HttpStatusCode.Unauthorized, "unauthenticated");
        await AssertProblemAsync(
            await ReadAsync(With(session.Credential)), HttpStatusCode.Unauthorized, "unauthenticated");

        // And it is gone from what an administrator would revoke by hand: nothing is left to find.
        var keys = await owner.GetFromJsonAsync<IReadOnlyList<ApiKeyResponse>>(
            "/v1/api-keys", ApiFactory.Json);
        Assert.NotNull(Assert.Single(keys!, k => k.Label == "the member's pipeline").RevokedAt);
    }

    [Fact]
    public async Task OtherPeoplesCredentialsSurviveADisabling()
    {
        var gone = await CreateMemberAsync("READER");
        var staying = await CreateMemberAsync("READER");
        var stayingSession = await SignInAsync(staying.Email, MemberPassword);
        var owner = await OwnerAsync();

        await SetDisabledAsync(owner, gone.UserId, true);

        Assert.Equal(HttpStatusCode.OK, (await ReadAsync(With(stayingSession.Credential))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ReadAsync(owner)).StatusCode);
    }

    [Fact]
    public async Task EnablingLetsThePersonSignInAgainAndRestoresNothingTheyHeld()
    {
        var member = await CreateMemberAsync("OWNER");
        var session = await SignInAsync(member.Email, MemberPassword);
        var key = await IssueKeyAsync(session.Credential);
        var owner = await OwnerAsync();
        await SetDisabledAsync(owner, member.UserId, true);

        var enabled = await SetDisabledAsync(owner, member.UserId, false);

        var body = await ApiFactory.ReadAsync<MemberDisabledResponse>(enabled);
        Assert.False(body.Disabled);
        Assert.Equal(0, body.CredentialsRevoked);

        Assert.Equal(HttpStatusCode.Unauthorized, (await ReadAsync(With(session.Credential))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ReadAsync(With(key))).StatusCode);

        var again = await SignInAsync(member.Email, MemberPassword);
        Assert.Equal(HttpStatusCode.OK, (await ReadAsync(With(again.Credential))).StatusCode);
        Assert.False((await MemberAsync(member.UserId)).Disabled);
    }

    [Fact]
    public async Task ADisablingAndAnEnablingAreAuditedAsTheAdministrator()
    {
        var member = await CreateMemberAsync("READER");
        var owner = await OwnerAsync();

        await SetDisabledAsync(owner, member.UserId, true);
        await SetDisabledAsync(owner, member.UserId, false);

        var disabled = Assert.Single(await AuditAsync("MEMBER_DISABLED", member.Email));
        Assert.Equal(AuthApiFactory.OwnerEmail, disabled.Actor);
        Assert.Equal(member.Email, disabled.Target);

        var enabled = Assert.Single(await AuditAsync("MEMBER_ENABLED", member.Email));
        Assert.Equal(AuthApiFactory.OwnerEmail, enabled.Actor);
    }

    [Fact]
    public async Task ARepeatedRequestChangesNothingAndLeavesNoSecondEntry()
    {
        var member = await CreateMemberAsync("READER");
        var owner = await OwnerAsync();

        await SetDisabledAsync(owner, member.UserId, true);
        var again = await SetDisabledAsync(owner, member.UserId, true);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(0, (await ApiFactory.ReadAsync<MemberDisabledResponse>(again)).CredentialsRevoked);
        Assert.Single(await AuditAsync("MEMBER_DISABLED", member.Email));

        var enabledAlready = await CreateMemberAsync("READER");
        Assert.Equal(
            HttpStatusCode.OK,
            (await SetDisabledAsync(owner, enabledAlready.UserId, false)).StatusCode);
        Assert.Empty(await AuditAsync("MEMBER_ENABLED", enabledAlready.Email));
    }

    [Fact]
    public async Task YouCannotDisableYourself()
    {
        var self = await CreateMemberAsync("OWNER");
        var session = await SignInAsync(self.Email, MemberPassword);
        var client = With(session.Credential);

        await AssertProblemAsync(
            await SetDisabledAsync(client, self.UserId, true), HttpStatusCode.Conflict, "own_account");

        Assert.Equal(HttpStatusCode.OK, (await ReadAsync(client)).StatusCode);
        Assert.False((await MemberAsync(self.UserId)).Disabled);
        Assert.Empty(await AuditAsync("MEMBER_DISABLED", self.Email));
    }

    [Fact]
    public async Task OnlyAnOwnerMayDisable_ARefusedRequestWritesNothing()
    {
        var target = await CreateMemberAsync("READER");
        var admin = await CreateMemberAsync("ADMIN");
        var targetSession = await SignInAsync(target.Email, MemberPassword);

        var client = With((await SignInAsync(admin.Email, MemberPassword)).Credential);

        await AssertProblemAsync(
            await SetDisabledAsync(client, target.UserId, true),
            HttpStatusCode.Forbidden,
            "insufficient_scope");

        Assert.Equal(HttpStatusCode.OK, (await ReadAsync(With(targetSession.Credential))).StatusCode);
        Assert.Empty(await AuditAsync("MEMBER_DISABLED", target.Email));
    }

    [Fact]
    public async Task DisablingAMemberWhoDoesNotExistIs404()
    {
        var owner = await OwnerAsync();

        await AssertProblemAsync(
            await SetDisabledAsync(owner, Guid.NewGuid(), true),
            HttpStatusCode.NotFound,
            "user_not_found");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"disable":true}""")]
    [InlineData("""{"disabled":null}""")]
    public async Task ABodyThatForgotTheFieldIsRefusedAndEnablesNobody(string body)
    {
        // A missing bool reads as false, which is "enable". A misspelt field must not do that to the
        // member it was meant to disable.
        var member = await CreateMemberAsync("READER");
        var owner = await OwnerAsync();
        await SetDisabledAsync(owner, member.UserId, true);

        var response = await owner.PutAsync(
            $"/v1/members/{member.UserId}/disabled",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        Assert.True((await MemberAsync(member.UserId)).Disabled);
        Assert.Empty(await AuditAsync("MEMBER_ENABLED", member.Email));
    }

    [Fact]
    public async Task AnOwnerCanBeDisabledAndEnabledByAnotherOwnerAndKeepsTheirRole()
    {
        var other = await CreateMemberAsync("OWNER");
        var owner = await OwnerAsync();

        Assert.Equal(HttpStatusCode.OK, (await SetDisabledAsync(owner, other.UserId, true)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SetDisabledAsync(owner, other.UserId, false)).StatusCode);
        Assert.Equal("OWNER", (await MemberAsync(other.UserId)).Role);
    }

    [Fact]
    public async Task TheNewMemberRoutesAreGatedByTheOrgAdminScope()
    {
        // A Reader gets 403 and not 404, which is what says the route exists and is gated rather
        // than missing.
        var reader = With((await SignInAsync(
            (await CreateMemberAsync("READER")).Email, MemberPassword)).Credential);

        await AssertProblemAsync(
            await ResetAsync(reader, Guid.NewGuid()), HttpStatusCode.Forbidden, "insufficient_scope");
        await AssertProblemAsync(
            await SetDisabledAsync(reader, Guid.NewGuid(), true),
            HttpStatusCode.Forbidden,
            "insufficient_scope");

        // And an anonymous caller is asked who they are, which is 401 rather than 403.
        await AssertProblemAsync(
            await ResetAsync(Anonymous(), Guid.NewGuid()), HttpStatusCode.Unauthorized, "unauthenticated");
        await AssertProblemAsync(
            await SetDisabledAsync(Anonymous(), Guid.NewGuid(), true),
            HttpStatusCode.Unauthorized,
            "unauthenticated");
    }

    [Fact]
    public async Task NoAccountChangeNotifiesAnybody()
    {
        // The confirmations say "Nobody is notified", and the outbox is where a notification would be
        // staged. The test host runs no pump, so nothing drains it behind this test's back.
        var member = await CreateMemberAsync("READER");
        var session = await SignInAsync(member.Email, MemberPassword);
        var owner = await OwnerAsync();
        var before = await owner.GetStringAsync("/v1/notifications/outbox");

        await ChangePasswordAsync(With(session.Credential), MemberPassword, NewPassword);
        await ResetAsync(owner, member.UserId);
        await SetDisabledAsync(owner, member.UserId, true);
        await SetDisabledAsync(owner, member.UserId, false);

        Assert.Equal(before, await owner.GetStringAsync("/v1/notifications/outbox"));
    }

    [Fact]
    public async Task NoResponseCarriesAPasswordHashOrAPassword()
    {
        var member = await CreateMemberAsync("READER");
        var owner = await OwnerAsync();

        var reset = await (await ResetAsync(owner, member.UserId)).Content.ReadAsStringAsync();
        var disabled = await (await SetDisabledAsync(owner, member.UserId, true)).Content.ReadAsStringAsync();
        var members = await owner.GetStringAsync("/v1/members");

        foreach (var text in new[] { reset, disabled, members })
        {
            Assert.DoesNotContain(Temporary, text, StringComparison.Ordinal);
            Assert.DoesNotContain(MemberPassword, text, StringComparison.Ordinal);
            Assert.DoesNotContain("passwordHash", text, StringComparison.OrdinalIgnoreCase);
        }

        // Valid JSON, so the assertions above read what was sent and not an error page.
        Assert.Equal(JsonValueKind.Array, JsonDocument.Parse(members).RootElement.ValueKind);
    }
}
