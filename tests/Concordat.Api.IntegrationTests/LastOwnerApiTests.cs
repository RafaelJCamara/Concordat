using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Concordat.Api.IntegrationTests;

/// <summary>
/// An organisation keeps an owner who can sign in (ADR-048), asked of a real database whose first
/// and only owner is the one the instance was claimed with.
/// </summary>
/// <remarks>
/// <b>One test, deliberately.</b> The rule is about the whole set of owners, and a second test in the
/// same host would find whatever the first left behind. The steps are the story of an instance going
/// from one owner to two to one who can sign in, and each asserts what the organisation looks like
/// after it.
/// </remarks>
public class LastOwnerApiTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Password = "an owner's own password";

    private HttpClient With(string credential)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", credential);

        return client;
    }

    private static Task<HttpResponseMessage> RoleAsync(HttpClient client, Guid userId, string role) =>
        client.PutAsJsonAsync(
            $"/v1/members/{userId}/role", new ChangeRoleRequest(role), ApiFactory.Json);

    private static Task<HttpResponseMessage> SetDisabledAsync(
        HttpClient client, Guid userId, bool disabled) =>
        client.PutAsJsonAsync(
            $"/v1/members/{userId}/disabled", new SetDisabledRequest(disabled), ApiFactory.Json);

    private static async Task AssertRefusedAsync(HttpResponseMessage response, string code)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(code, (await ApiFactory.ReadProblemAsync(response)).ConcordatCode);
    }

    private static async Task<IReadOnlyList<MemberResponse>> MembersAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<IReadOnlyList<MemberResponse>>(
            "/v1/members", ApiFactory.Json))!;

    [Fact]
    public async Task TheLastOwnerWhoCanSignInCannotBeDemotedOrDisabled_WhateverTheOthersAreDoing()
    {
        // ---- One owner: the instance as it is claimed.
        var a = With(await factory.OwnerCredentialAsync());
        var aId = Assert.Single(await MembersAsync(a)).UserId;

        await AssertRefusedAsync(await RoleAsync(a, aId, "READER"), "last_owner");
        await AssertRefusedAsync(await SetDisabledAsync(a, aId, true), "own_account");
        Assert.Equal("OWNER", Assert.Single(await MembersAsync(a)).Role);
        Assert.False(Assert.Single(await MembersAsync(a)).Disabled);

        // ---- Two owners: one may go, so long as another stays.
        var created = await a.PostAsJsonAsync(
            "/v1/members",
            new CreateMemberRequest($"second-{Guid.NewGuid():N}@example.com", Password, "OWNER"),
            ApiFactory.Json);
        var b = await ApiFactory.ReadAsync<MemberResponse>(created);

        Assert.Equal(
            HttpStatusCode.OK,
            (await SetDisabledAsync(a, b.UserId, true)).StatusCode);

        // ---- The second owner is disabled, so the first is the last one who can sign in. Counting
        // owners rather than owners who can sign in is the mistake this guards: it would let the one
        // person able to repair the instance demote themselves.
        await AssertRefusedAsync(await RoleAsync(a, aId, "ADMIN"), "last_owner");
        Assert.Equal(
            "OWNER",
            Assert.Single(await MembersAsync(a), m => m.UserId == aId).Role);

        // ---- Enabled again, the first owner may stand down, and the second is now the last.
        Assert.Equal(
            HttpStatusCode.OK,
            (await SetDisabledAsync(a, b.UserId, false)).StatusCode);

        var signedInB = await ApiFactory.ReadAsync<SignInResponse>(
            await factory.CreateClient().PostAsJsonAsync(
                "/v1/auth/signin", new SignInRequest(b.Email, Password), ApiFactory.Json));
        var bClient = With(signedInB.Credential);

        Assert.Equal(HttpStatusCode.OK, (await RoleAsync(a, aId, "ADMIN")).StatusCode);

        await AssertRefusedAsync(await RoleAsync(bClient, b.UserId, "READER"), "last_owner");
        await AssertRefusedAsync(await SetDisabledAsync(bClient, b.UserId, true), "own_account");

        var final = await MembersAsync(bClient);
        Assert.Equal("OWNER", Assert.Single(final, m => m.UserId == b.UserId).Role);
        Assert.Equal("ADMIN", Assert.Single(final, m => m.UserId == aId).Role);
    }
}
