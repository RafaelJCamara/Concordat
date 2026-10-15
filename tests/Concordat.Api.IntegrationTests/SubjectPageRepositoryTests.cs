using Concordat.Application.Abstractions;
using Concordat.Domain.Registry;
using Concordat.Infrastructure.Persistence;

namespace Concordat.Api.IntegrationTests;

/// <summary>
/// A page of subjects, and counting them, against real PostgreSQL (ADR-045).
/// </summary>
/// <remarks>
/// A value converter on <c>Subject.Name</c> is why the order, the skip and the count need a database: the
/// ordering is the database's, and a fake that sorts with <c>StringComparer.Ordinal</c> would pass for any
/// collation. Each test arranges its own environment, so none sees another's subjects.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class SubjectPageRepositoryTests(PostgresFixture fixture)
{
    private static Subject NewSubject(string name, EnvironmentId environment) =>
        Subject.Create(
            environment,
            SubjectName.Create(name).Value,
            SchemaFormat.Json,
            ActorId.Create("alice").Value,
            CompatibilityPolicy.Default,
            DateTimeOffset.UtcNow).Value;

    private async Task<EnvironmentId> ArrangeAsync(TenantId? tenant, params string[] names)
    {
        var environment = EnvironmentId.New();

        await using var context = fixture.NewContext(tenant);
        foreach (var name in names)
        {
            context.Subjects.Add(NewSubject(name, environment));
        }

        await context.SaveChangesAsync();
        return environment;
    }

    private Task<EnvironmentId> ArrangeAsync(params string[] names) => ArrangeAsync(null, names);

    private async Task<List<string>> PageAsync(
        EnvironmentId environment, int limit, int offset, string? prefix = null, TenantId? tenant = null)
    {
        await using var context = fixture.NewContext(tenant);

        var found = await new SubjectRepository(context).ListPageAsync(
            environment,
            prefix is null ? null : SubjectNamePrefix.Create(prefix).Value,
            new SubjectPaging(limit, offset),
            CancellationToken.None);

        return [.. found.Select(s => s.Name.Value)];
    }

    private async Task<int> CountAsync(EnvironmentId environment, string? prefix = null, TenantId? tenant = null)
    {
        await using var context = fixture.NewContext(tenant);

        return await new SubjectRepository(context).CountAsync(
            environment,
            prefix is null ? null : SubjectNamePrefix.Create(prefix).Value,
            CancellationToken.None);
    }

    private static string[] Names(int count) =>
        [.. Enumerable.Range(0, count).Select(i => $"acme.s{i:D3}.Thing")];

    [Fact]
    public async Task PagesWalkTheListInNameOrderWithNoGapAndNoRepeat()
    {
        // Arranged in the reverse of name order, so insertion order cannot be what makes this pass.
        var environment = await ArrangeAsync([.. Names(23).Reverse()]);

        var walked = new List<string>();
        for (var offset = 0; offset < 30; offset += 10)
        {
            walked.AddRange(await PageAsync(environment, 10, offset));
        }

        Assert.Equal(Names(23), walked);
    }

    [Fact]
    public async Task APagePastTheEndIsEmpty_AndAShortLastPageIsShort()
    {
        var environment = await ArrangeAsync(Names(5));

        Assert.Equal(2, (await PageAsync(environment, 3, 3)).Count);
        Assert.Empty(await PageAsync(environment, 3, 5));
        Assert.Empty(await PageAsync(environment, 3, 500));
    }

    [Fact]
    public async Task ThePageOfAPrefixIsTheRowsThatBeginWithIt_NotThePrefixedRowsOfThePage()
    {
        // Skip and take are applied after the filter: a page of 'orders' subjects is the first rows that
        // match, not the matching rows among the first rows of the whole list.
        var environment = await ArrangeAsync(
            "acme.a.Thing", "acme.b.Thing", "acme.orders.A", "acme.orders.B", "acme.orders.C", "acme.z.Thing");

        Assert.Equal(["acme.orders.A", "acme.orders.B"], await PageAsync(environment, 2, 0, "acme.orders."));
        Assert.Equal(["acme.orders.C"], await PageAsync(environment, 2, 2, "acme.orders."));
    }

    [Fact]
    public async Task ThePrefixIsCaseSensitive_AsTheListIs()
    {
        var environment = await ArrangeAsync("acme.OrderCreated", "acme.ordercreated");

        Assert.Equal(["acme.OrderCreated"], await PageAsync(environment, 10, 0, "acme.Order"));
        Assert.Equal(1, await CountAsync(environment, "acme.Order"));
    }

    [Fact]
    public async Task AnUnderscoreInAPrefixIsALiteral_NotAWildcard()
    {
        var environment = await ArrangeAsync("acme.a_b", "acme.axb");

        Assert.Equal(["acme.a_b"], await PageAsync(environment, 10, 0, "acme.a_"));
        Assert.Equal(1, await CountAsync(environment, "acme.a_"));
    }

    [Fact]
    public async Task TheCountIsTheWholeListAndTheWholePrefixedList_AndIsNotAPage()
    {
        var environment = await ArrangeAsync("acme.orders.A", "acme.orders.B", "acme.billing.A");

        Assert.Equal(3, await CountAsync(environment));
        Assert.Equal(2, await CountAsync(environment, "acme.orders."));
        Assert.Equal(0, await CountAsync(environment, "acme.nothing."));
    }

    [Fact]
    public async Task AnEnvironmentNothingWasWrittenTo_HasNoPageAndNoCount()
    {
        var environment = EnvironmentId.New();

        Assert.Empty(await PageAsync(environment, 10, 0));
        Assert.Equal(0, await CountAsync(environment));
    }

    [Fact]
    public async Task AnotherEnvironmentsSubjectsAreNeitherPagedNorCounted()
    {
        var mine = await ArrangeAsync("acme.mine.A");
        await ArrangeAsync("acme.theirs.A", "acme.theirs.B");

        Assert.Equal(["acme.mine.A"], await PageAsync(mine, 10, 0));
        Assert.Equal(1, await CountAsync(mine));
    }

    [Fact]
    public async Task AnotherTenantsSubjectsAreNeitherPagedNorCounted_EvenInTheSameEnvironmentId()
    {
        // The same EnvironmentId under two tenants, which derived ids make impossible in production and which
        // is the sharpest way to ask whether the tenant filter holds on both statements.
        var environment = EnvironmentId.New();
        var tenantA = TenantId.New();
        var tenantB = TenantId.New();

        foreach (var (tenant, name) in new[] { (tenantA, "acme.a.Thing"), (tenantB, "acme.b.Thing") })
        {
            await using var context = fixture.NewContext(tenant);
            context.Subjects.Add(NewSubject(name, environment));
            await context.SaveChangesAsync();
        }

        Assert.Equal(["acme.a.Thing"], await PageAsync(environment, 10, 0, tenant: tenantA));
        Assert.Equal(1, await CountAsync(environment, tenant: tenantA));
        Assert.Equal(["acme.b.Thing"], await PageAsync(environment, 10, 0, tenant: tenantB));
        Assert.Equal(1, await CountAsync(environment, tenant: tenantB));
    }
}
