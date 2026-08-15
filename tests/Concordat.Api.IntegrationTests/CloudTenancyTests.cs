using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Concordat.Domain.Governance;
using Concordat.Domain.Identity;
using Concordat.Infrastructure;
using Concordat.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Concordat.Api.IntegrationTests;

/// <summary>
/// The API running as <see cref="ConcordatProfile.Cloud"/>, over its own database.
/// </summary>
/// <remarks>
/// The same image and the same registrations as self-hosted, with one difference: the tenant
/// comes from the credential rather than from a constant. That difference is a registration at
/// the composition root, which is what this fixture exercises — if tenancy needed a branch
/// anywhere in query code, standing the host up in a different profile would not be enough to
/// change behaviour.
/// </remarks>
public sealed class CloudApiFactory : ApiFactory
{
    /// <inheritdoc />
    protected override ConcordatProfile Profile => ConcordatProfile.Cloud;

    /// <inheritdoc />
    /// <remarks>
    /// A key URI is required to start in Cloud (M9.1) and this one points nowhere. That is
    /// sound because registering key protection performs no I/O — the credential and the vault
    /// are reached lazily, on the first Protect or Unprotect — and nothing in this suite stores
    /// a broker credential. A test that did would hang trying to reach Azure, which is a loud
    /// failure rather than a silently unprotected key ring, and is the point of the refusal
    /// this fixture is working around.
    /// </remarks>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.UseSetting(
            "Concordat:KeyProtection:KeyUri",
            "https://concordat-tests.vault.azure.net/keys/data-protection/none");
    }

    /// <summary>Creates an organisation with an owner, and returns a credential for them.</summary>
    /// <param name="slug">The organisation's handle.</param>
    /// <returns>A bearer credential for the new owner.</returns>
    public async Task<string> NewOrganisationAsync(string slug)
    {
        using var client = CreateClient();
        var email = $"owner@{slug}.example.com";
        const string password = "correct horse battery";

        var created = await client.PostAsJsonAsync(
            "/v1/auth/signup",
            new SignUpRequest(slug, email, password, slug),
            Json).ConfigureAwait(false);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var signIn = await client.PostAsJsonAsync(
            "/v1/auth/signin", new SignInRequest(email, password), Json).ConfigureAwait(false);

        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);

        return (await ReadAsync<SignInResponse>(signIn).ConfigureAwait(false)).Credential;
    }
}

/// <summary>Marks a class as sharing the Cloud-profile host and its database.</summary>
[CollectionDefinition(Name)]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "'Collection' is xunit's own term for a shared-fixture group.")]
public sealed class CloudApiCollection : ICollectionFixture<CloudApiFactory>
{
    /// <summary>The collection name.</summary>
    public const string Name = "cloud-api";
}

/// <summary>
/// M9's exit criterion: two organisations on one deployment cannot see each other's data.
/// </summary>
/// <remarks>
/// Every assertion here is the failure mode tenancy exists to prevent, and none of them would
/// be caught by a functional test — a leak looks exactly like a working registry to whoever is
/// doing the leaking.
/// </remarks>
[Collection(CloudApiCollection.Name)]
public class CloudTenancyTests(CloudApiFactory factory)
{
    private static string UniqueSlug() => $"org-{Guid.CreateVersion7():N}"[..20];

    private static string UniqueSubject() => $"acme.cloud.S{Guid.CreateVersion7():N}";

    private HttpClient For(string credential)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", credential);

        return client;
    }

    /// <summary>Two organisations, each with an owner credential.</summary>
    private async Task<(HttpClient First, HttpClient Second)> TwoOrganisationsAsync()
    {
        var one = await factory.NewOrganisationAsync(UniqueSlug());
        var two = await factory.NewOrganisationAsync(UniqueSlug());

        return (For(one), For(two));
    }

    [Fact]
    public async Task TwoOrganisationsCanBothOwnAnEnvironmentCalledProd()
    {
        // The bug this milestone had to fix before anything else: environment ids were derived
        // from the name alone, so the second organisation to create 'prod' collided on a
        // primary key. It failed loudly, which was lucky — the same derivation is what
        // subject rows point at.
        var (first, second) = await TwoOrganisationsAsync();

        var a = await first.PostAsJsonAsync(
            "/v1/environments", new CreateEnvironmentRequest("prod"), ApiFactory.Json);
        var b = await second.PostAsJsonAsync(
            "/v1/environments", new CreateEnvironmentRequest("prod"), ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, a.StatusCode);
        Assert.Equal(HttpStatusCode.Created, b.StatusCode);
    }

    [Fact]
    public async Task AnOrganisationSeesOnlyItsOwnEnvironments()
    {
        var (first, second) = await TwoOrganisationsAsync();

        await first.PostAsJsonAsync(
            "/v1/environments", new CreateEnvironmentRequest("mine"), ApiFactory.Json);

        var theirs = await second.GetFromJsonAsync<IReadOnlyList<EnvironmentResponse>>(
            "/v1/environments", ApiFactory.Json);

        Assert.DoesNotContain(theirs!, e => e.Name == "mine");
    }

    [Fact]
    public async Task AnOrganisationSeesOnlyItsOwnSubjects()
    {
        var (first, second) = await TwoOrganisationsAsync();
        var subject = UniqueSubject();

        await first.PostAsJsonAsync(
            "/v1/environments", new CreateEnvironmentRequest("dev"), ApiFactory.Json);
        await second.PostAsJsonAsync(
            "/v1/environments", new CreateEnvironmentRequest("dev"), ApiFactory.Json);

        var created = await first.PostAsJsonAsync(
            "/v1/environments/dev/subjects",
            new CreateSubjectRequest(subject, "json", "a", null, null, "open"),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        // Same environment name, same subject name, different organisation. A shared row would
        // make this a 200.
        var direct = await second.GetAsync($"/v1/environments/dev/subjects/{subject}");
        Assert.Equal(HttpStatusCode.NotFound, direct.StatusCode);

        var listed = await second.GetFromJsonAsync<IReadOnlyList<SubjectResponse>>(
            "/v1/environments/dev/subjects", ApiFactory.Json);

        Assert.Empty(listed!);
    }

    [Fact]
    public async Task TheSameSubjectNameInTwoOrganisationsIsTwoSubjects()
    {
        var (first, second) = await TwoOrganisationsAsync();
        var subject = UniqueSubject();

        foreach (var client in new[] { first, second })
        {
            await client.PostAsJsonAsync(
                "/v1/environments", new CreateEnvironmentRequest("dev"), ApiFactory.Json);

            var created = await client.PostAsJsonAsync(
                "/v1/environments/dev/subjects",
                new CreateSubjectRequest(subject, "json", "owner", null, null, "open"),
                ApiFactory.Json);

            // Neither is a 409: the uniqueness constraint is per (tenant, environment, name).
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }
    }

    [Fact]
    public async Task AnOrganisationSeesOnlyItsOwnAuditTrail()
    {
        var (first, second) = await TwoOrganisationsAsync();

        await first.PostAsJsonAsync(
            "/v1/environments", new CreateEnvironmentRequest("audited"), ApiFactory.Json);

        var theirs = await second.GetFromJsonAsync<IReadOnlyList<AuditResponse>>(
            "/v1/audit", ApiFactory.Json);

        Assert.DoesNotContain(theirs!, e => e.Target == "audited");
    }

    [Fact]
    public async Task AnOrganisationSeesOnlyItsOwnMembersAndKeys()
    {
        var (first, second) = await TwoOrganisationsAsync();

        await first.PostAsJsonAsync(
            "/v1/api-keys",
            new IssueApiKeyRequest("theirs-to-find", [Scope.SubjectRead]),
            ApiFactory.Json);

        var keys = await second.GetFromJsonAsync<IReadOnlyList<ApiKeyResponse>>(
            "/v1/api-keys", ApiFactory.Json);

        Assert.DoesNotContain(keys!, k => k.Label == "theirs-to-find");

        var members = await second.GetFromJsonAsync<IReadOnlyList<MemberResponse>>(
            "/v1/members", ApiFactory.Json);

        // One owner each. A membership query that ignored the tenant would return two.
        Assert.Single(members!);
    }

    [Fact]
    public async Task AKeyIssuedInOneOrganisationActsOnlyInThatOne()
    {
        // The sharpest version: a valid credential, used against a resource that exists — in
        // somebody else's organisation.
        var (first, second) = await TwoOrganisationsAsync();
        var subject = UniqueSubject();

        await second.PostAsJsonAsync(
            "/v1/environments", new CreateEnvironmentRequest("dev"), ApiFactory.Json);
        await second.PostAsJsonAsync(
            "/v1/environments/dev/subjects",
            new CreateSubjectRequest(subject, "json", "b", null, null, "open"),
            ApiFactory.Json);

        var issued = await ApiFactory.ReadAsync<IssuedApiKeyResponse>(
            await first.PostAsJsonAsync(
                "/v1/api-keys",
                new IssueApiKeyRequest("first-org-key", [Scope.SubjectAdmin]),
                ApiFactory.Json));

        var withKey = For(issued.Secret);

        // Not 403: from this organisation's point of view the subject does not exist, and
        // saying "forbidden" would confirm that it does somewhere else.
        var read = await withKey.GetAsync($"/v1/environments/dev/subjects/{subject}");
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
    }

    [Fact]
    public async Task ASlugCannotBeClaimedTwice()
    {
        var slug = UniqueSlug();
        await factory.NewOrganisationAsync(slug);

        var again = await factory.CreateClient().PostAsJsonAsync(
            "/v1/auth/signup",
            new SignUpRequest("Someone Else", "other@example.com", "correct horse battery", slug),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(
            "tenant_already_exists",
            (await ApiFactory.ReadProblemAsync(again)).ConcordatCode);
    }

    [Fact]
    public async Task SignupDoesNotSayWhetherAnEmailIsAlreadyKnown()
    {
        // A signup form open to the internet that distinguishes "taken" from "invalid" is an
        // account enumeration oracle.
        var slug = UniqueSlug();
        await factory.NewOrganisationAsync(slug);

        var response = await factory.CreateClient().PostAsJsonAsync(
            "/v1/auth/signup",
            new SignUpRequest(
                "Another", $"owner@{slug}.example.com", "correct horse battery", UniqueSlug()),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var problem = await ApiFactory.ReadProblemAsync(response);
        Assert.Equal(
            "That email address cannot be used to create an organisation.", problem.Detail);
    }

    [Fact]
    public async Task AnAnonymousCallerInCloudGetsNothingRatherThanEverything()
    {
        // The unclaimed-instance owner is a self-hosted first-run affordance. In Cloud there is
        // no "unclaimed": an anonymous caller resolves to a tenant nobody is a member of, and a
        // filter that matches nothing beats one that matches everything.
        await factory.NewOrganisationAsync(UniqueSlug());

        var anonymous = factory.CreateClient();

        var environments = await anonymous.GetFromJsonAsync<IReadOnlyList<EnvironmentResponse>>(
            "/v1/environments", ApiFactory.Json);

        Assert.Empty(environments!);

        var write = await anonymous.PostAsJsonAsync(
            "/v1/environments", new CreateEnvironmentRequest("sneaky"), ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Unauthorized, write.StatusCode);
    }
    [Fact]
    public async Task ASignupIsRecordedOnTheDeploymentTrailAndNotInAnybodyElsesAudit()
    {
        // Decision 29. Audit rows are stamped with the tenant in scope, and at signup nobody has
        // authenticated -- so a row there would land in whichever organisation an anonymous
        // caller resolves to, which is not the one being created. The fix is not a cross-tenant
        // audit write: creating an organisation is something the OPERATOR's deployment did, and
        // it wants a different retention and a different reader.
        var slug = UniqueSlug();
        await factory.NewOrganisationAsync(slug);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ConcordatDbContext>();

        var events = await context.DeploymentEvents
            .AsNoTracking()
            .Where(e => e.Action == DeploymentAction.OrganisationCreated)
            .ToListAsync();

        var recorded = Assert.Single(events, e => e.Detail.Contains(slug, StringComparison.Ordinal));

        Assert.Equal($"owner@{slug}.example.com", recorded.Actor);
        Assert.NotNull(recorded.TenantId);

        // And the table is readable without a tenant in scope, which is the property that makes
        // it a deployment trail rather than a tenant one. Every other table here is filtered.
        Assert.NotEmpty(await context.DeploymentEvents.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task TheDeploymentTrailCommitsWithTheOrganisationOrNotAtAll()
    {
        // Staged on the same change tracker, deliberately. A second transaction could leave an
        // organisation that exists with nothing recording its creation -- the exact hole this
        // closes -- so a refused signup must leave no event behind either.
        var slug = UniqueSlug();
        await factory.NewOrganisationAsync(slug);

        using var client = factory.CreateClient();

        var duplicate = await client.PostAsJsonAsync(
            "/v1/auth/signup",
            new SignUpRequest(slug, $"someone-else@{slug}.example.com", "correct horse battery", slug),
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ConcordatDbContext>();

        var forThisSlug = await context.DeploymentEvents
            .AsNoTracking()
            .Where(e => e.Detail.Contains(slug))
            .ToListAsync();

        Assert.Single(forThisSlug);
    }
}
