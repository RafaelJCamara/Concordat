using System.Net;
using System.Net.Http.Json;
using System.Text;
using Concordat.Application.Registry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Concordat.Api.IntegrationTests;

/// <summary>
/// A request body the registry cannot read is a 400, in every hosting environment.
/// </summary>
/// <remarks>
/// <para>
/// <b>It was a 500 in Development and a 400 in Production</b>, and nothing ran the registry in
/// Development except CI's browser job, which started it with <c>dotnet run</c>. ASP.NET Core's
/// <c>RouteHandlerOptions.ThrowOnBadRequest</c> defaults to <em>whether the host is in
/// Development</em>: on, a minimal API's body-binding failure (not JSON, a number where a string
/// goes, a lone surrogate in a string) is thrown as <c>BadHttpRequestException</c> into the
/// exception handler, which has no mapping for it and answers 500; off, the framework logs it and
/// answers 400 itself. Every local run of the browser suite used the Production container and
/// every one of them was green, while CI's was red on exactly the parity specs that send these
/// bodies. A developer running <c>dotnet run</c> was seeing a crash that a deployment would not
/// have, which is the wrong way round for the environment that exists to find crashes.
/// </para>
/// <para>
/// <b>This fixture hosts as Development on purpose.</b> <c>WebApplicationFactory</c> does so by
/// default, and it is the environment the bug lived in; it is named here and asserted in the first
/// test so that the day the factory stops hosting as Development this file says so, rather than
/// quietly proving the Production half only.
/// </para>
/// <para>
/// The status is the contract the browser parity specs pin (<c>400</c>, and no
/// <c>concordatCode</c>: the envelope is refused before any rule runs, so there is no rule to
/// name). What this does not assert is the body, which is the framework's own.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public class UnreadableBodyTests(ApiFactory factory)
{
    private static readonly string Backslash = new((char)92, 1);

    /// <summary>A lone high surrogate escape, spelled without writing one in source.</summary>
    private static readonly string LoneSurrogate = Backslash + "ud800";

    private static string Unique() => $"{Guid.CreateVersion7():N}"[..16];

    private async Task<(string Environment, string Subject)> NewSubjectAsync()
    {
        var environment = $"env-{Unique()}";
        var subject = $"acme.unreadable.S{Unique()}";

        var env = await factory.CreateClient().PostAsJsonAsync(
            "/v1/environments", new CreateEnvironmentRequest(environment), ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Created, env.StatusCode);

        var created = await factory.CreateClient().PostAsJsonAsync(
            $"/v1/environments/{environment}/subjects",
            new CreateSubjectRequest(subject, "json", "alice"),
            ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        return (environment, subject);
    }

    private static StringContent Json(string body) =>
        new(body, Encoding.UTF8, "application/json");

    [Fact]
    public void ThisFixtureHostsAsDevelopment_WhichIsWhereTheBugWas()
    {
        Assert.Equal("Development", factory.Services
            .GetRequiredService<IHostEnvironment>().EnvironmentName);
    }

    [Theory]
    [InlineData("a lone surrogate in the owner", "{\"owner\":\"team {S}\"}")]
    [InlineData("a number for the owner", "{\"owner\":5}")]
    [InlineData("an array for the owner", "{\"owner\":[\"a\"]}")]
    [InlineData("a string for deprecate", "{\"owner\":\"x\",\"deprecate\":\"yes\"}")]
    [InlineData("not JSON", "owner=x")]
    public async Task AnUnreadableOwnerChange_Is400NotA500(string label, string template)
    {
        var (environment, subject) = await NewSubjectAsync();
        var body = template.Replace("{S}", LoneSurrogate, StringComparison.Ordinal);

        var response = await factory.CreateClient().PatchAsync(
            $"/v1/environments/{environment}/subjects/{subject}", Json(body));

        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest,
            $"{label}: expected 400, got {(int)response.StatusCode}.");
    }

    [Theory]
    [InlineData("a lone surrogate in the name", "{\"name\":\"acme.{S}\",\"format\":\"json\",\"owner\":\"a\"}")]
    [InlineData("a number for the name", "{\"name\":5,\"format\":\"json\",\"owner\":\"a\"}")]
    [InlineData("not JSON", "name=x")]
    public async Task AnUnreadableNewSubject_Is400NotA500(string label, string template)
    {
        var (environment, _) = await NewSubjectAsync();
        var body = template.Replace("{S}", LoneSurrogate, StringComparison.Ordinal);

        var response = await factory.CreateClient().PostAsync(
            $"/v1/environments/{environment}/subjects", Json(body));

        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest,
            $"{label}: expected 400, got {(int)response.StatusCode}.");
    }

    [Theory]
    [InlineData("a lone surrogate in the schema", "{\"schema\":\"{S}\"}")]
    [InlineData("a number for the schema", "{\"schema\":5}")]
    public async Task AnUnreadableDryRun_Is400NotA500(string label, string template)
    {
        var (environment, subject) = await NewSubjectAsync();
        var body = template.Replace("{S}", LoneSurrogate, StringComparison.Ordinal);

        var response = await factory.CreateClient().PostAsync(
            $"/v1/environments/{environment}/subjects/{subject}/compatibility", Json(body));

        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest,
            $"{label}: expected 400, got {(int)response.StatusCode}.");
    }

    [Theory]
    [InlineData("a number where a token goes", "{\"mode\":5,\"surface\":\"WIRE\"}")]
    [InlineData("not JSON", "mode=FULL")]
    public async Task AnUnreadablePolicy_Is400NotA500(string label, string body)
    {
        var (environment, subject) = await NewSubjectAsync();

        var response = await factory.CreateClient().PutAsync(
            $"/v1/environments/{environment}/subjects/{subject}/compatibility-policy", Json(body));

        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest,
            $"{label}: expected 400, got {(int)response.StatusCode}.");
    }
}
