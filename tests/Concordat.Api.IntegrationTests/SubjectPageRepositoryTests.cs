using Concordat.Application.Abstractions;
using Concordat.Domain.Registry;
using Concordat.Formats.Abstractions;
using Concordat.Formats.Json;
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
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private static Subject NewSubject(string name, EnvironmentId environment, DateTimeOffset? createdAt = null) =>
        Subject.Create(
            environment,
            SubjectName.Create(name).Value,
            SchemaFormat.Json,
            ActorId.Create("alice").Value,
            CompatibilityPolicy.Default,
            createdAt ?? DateTimeOffset.UtcNow).Value;

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

    /// <summary>Arranges subjects with the creation times given, in the order given (ADR-049).</summary>
    private async Task<EnvironmentId> ArrangeCreatedAsync(
        params (string Name, DateTimeOffset CreatedAt)[] subjects)
    {
        var environment = EnvironmentId.New();

        await using var context = fixture.NewContext();
        foreach (var (name, createdAt) in subjects)
        {
            context.Subjects.Add(NewSubject(name, environment, createdAt));
        }

        await context.SaveChangesAsync();
        return environment;
    }

    private async Task<List<string>> PageAsync(
        EnvironmentId environment,
        int limit,
        int offset,
        string? prefix = null,
        TenantId? tenant = null,
        SubjectSort sort = SubjectSort.Name)
    {
        await using var context = fixture.NewContext(tenant);

        var found = await new SubjectRepository(context).ListPageAsync(
            environment,
            prefix is null ? null : SubjectNamePrefix.Create(prefix).Value,
            sort,
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

    // ------------------------------------------------------------------ newest first (ADR-049)

    /// <summary>
    /// Thirty subjects in six groups of five. Each group shares one creation instant, so five names tie on
    /// time and only the name can order them. The groups are an hour apart, and the whole set is arranged in a
    /// scrambled order that is neither the time order nor the name order, so neither insertion order nor a
    /// default scan order can be what makes a test pass.
    /// </summary>
    private static (string Name, DateTimeOffset CreatedAt)[] TiedSubjects() =>
    [
        .. Enumerable.Range(0, 30)
            .OrderBy(i => (i * 7) % 30)
            .Select(i => ($"acme.t{i:D2}.Thing", T0.AddHours(i % 6)))
    ];

    /// <summary>The order the 30 must come in: newest group first, and by name inside a group.</summary>
    private static string[] TiedSubjectsNewestFirst() =>
    [
        .. TiedSubjects()
            .OrderByDescending(s => s.CreatedAt)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .Select(s => s.Name)
    ];

    [Fact]
    public async Task Newest_IsTheLatestCreatedFirst_WhateverTheNamesAndTheInsertionOrder()
    {
        // The newest has the name that sorts LAST and was inserted FIRST; the oldest the reverse.
        var environment = await ArrangeCreatedAsync(
            ("acme.z.Newest", T0.AddDays(3)),
            ("acme.m.Middle", T0.AddDays(2)),
            ("acme.a.Oldest", T0.AddDays(1)));

        Assert.Equal(
            ["acme.z.Newest", "acme.m.Middle", "acme.a.Oldest"],
            await PageAsync(environment, 10, 0, sort: SubjectSort.Newest));
        Assert.Equal(
            ["acme.a.Oldest", "acme.m.Middle", "acme.z.Newest"],
            await PageAsync(environment, 10, 0, sort: SubjectSort.Name));
    }

    [Fact]
    public async Task Newest_SubjectsCreatedInTheSameInstantFollowInNameOrder()
    {
        // The tiebreaker. Inserted in the reverse of name order, so insertion order cannot supply it.
        var environment = await ArrangeCreatedAsync(
            ("acme.c.Thing", T0), ("acme.b.Thing", T0), ("acme.a.Thing", T0), ("acme.d.Later", T0.AddMinutes(1)));

        Assert.Equal(
            ["acme.d.Later", "acme.a.Thing", "acme.b.Thing", "acme.c.Thing"],
            await PageAsync(environment, 10, 0, sort: SubjectSort.Newest));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(29)]
    [InlineData(30)]
    public async Task Newest_PagesWalkTheListWithNoGapAndNoRepeat_EvenWhenManySubjectsShareAnInstant(int size)
    {
        // A page boundary that falls INSIDE a group of five subjects with one timestamp is the case a missing
        // or unstable tiebreaker breaks: the same subject on two pages, or on none.
        var environment = await ArrangeCreatedAsync(TiedSubjects());

        var walked = new List<string>();
        for (var offset = 0; offset < 40; offset += size)
        {
            walked.AddRange(await PageAsync(environment, size, offset, sort: SubjectSort.Newest));
        }

        Assert.Equal(TiedSubjectsNewestFirst(), walked);
        Assert.Equal(30, walked.Distinct().Count());
    }

    [Fact]
    public async Task Newest_AskedForTwice_IsTheSameOrderTwice()
    {
        // Two requests agree about where a page ends. A tiebreaker that depended on the plan or on the
        // physical order of rows would not survive the second read.
        var environment = await ArrangeCreatedAsync(TiedSubjects());

        var first = await PageAsync(environment, 13, 4, sort: SubjectSort.Newest);
        var second = await PageAsync(environment, 13, 4, sort: SubjectSort.Newest);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Newest_AndAPrefix_IsThePrefixedRowsNewestFirst_AndTheirPagesWalkWithoutGapOrRepeat()
    {
        var environment = await ArrangeCreatedAsync(
            ("acme.billing.A", T0.AddDays(9)),
            ("acme.orders.A", T0.AddDays(1)),
            ("acme.orders.B", T0.AddDays(3)),
            ("acme.orders.C", T0.AddDays(3)),
            ("acme.orders.D", T0.AddDays(2)),
            ("acme.shipping.A", T0.AddDays(8)));

        // Skip and take are applied after the filter and the order: the first page of 'orders' is the two
        // newest orders subjects, not the orders subjects among the newest of the whole environment.
        Assert.Equal(
            ["acme.orders.B", "acme.orders.C"],
            await PageAsync(environment, 2, 0, "acme.orders.", sort: SubjectSort.Newest));
        Assert.Equal(
            ["acme.orders.D", "acme.orders.A"],
            await PageAsync(environment, 2, 2, "acme.orders.", sort: SubjectSort.Newest));
        Assert.Empty(await PageAsync(environment, 2, 4, "acme.orders.", sort: SubjectSort.Newest));
        Assert.Equal(4, await CountAsync(environment, "acme.orders."));
    }

    [Fact]
    public async Task Newest_WithEveryRow_IsTheWholeListNewestFirst()
    {
        var environment = await ArrangeCreatedAsync(TiedSubjects());

        await using var context = fixture.NewContext();
        var all = await new SubjectRepository(context).ListPageAsync(
            environment, null, SubjectSort.Newest, SubjectPaging.Everything, CancellationToken.None);

        Assert.Equal(TiedSubjectsNewestFirst(), all.Select(s => s.Name.Value));
    }

    [Fact]
    public async Task TheCountDoesNotDependOnTheOrder_AndEitherOrderShowsEveryRowTheCountCounts()
    {
        var environment = await ArrangeCreatedAsync(TiedSubjects());

        Assert.Equal(30, await CountAsync(environment));
        Assert.Equal(30, (await PageAsync(environment, 500, 0, sort: SubjectSort.Newest)).Count);
        Assert.Equal(30, (await PageAsync(environment, 500, 0, sort: SubjectSort.Name)).Count);
    }

    [Fact]
    public async Task Newest_DoesNotMoveWhenAVersionIsRegistered_ItIsCreationAndNotLastChange()
    {
        // The reason the order is by creation time. The older subject is given a version, the most recent
        // change anywhere in the environment, and stays where it was: a page does not reshuffle under the
        // reader because somebody registered something.
        var tenant = TenantId.New();
        var environment = EnvironmentId.New();

        var older = NewSubject("acme.older.Thing", environment, T0);
        var newer = NewSubject("acme.newer.Thing", environment, T0.AddDays(1));

        var canonical = new JsonSchemaCanonicalizer().Canonicalize("{\"type\":\"object\"}").Value;
        var schema = Schema.Create(
            SchemaIdComputer.Compute(SchemaFormat.Json, canonical), SchemaFormat.Json, canonical).Value;

        Assert.True(older.RegisterVersion(
            schema,
            CompatibilityVerdict.Compatible(CompatibilityPolicy.Default),
            null,
            null,
            ActorId.Create("alice").Value,
            T0.AddDays(30)).IsSuccess);

        await using (var context = fixture.NewContext(tenant))
        {
            context.Schemas.Add(schema);
            context.Subjects.AddRange(older, newer);
            await context.SaveChangesAsync();
        }

        Assert.Equal(
            ["acme.newer.Thing", "acme.older.Thing"],
            await PageAsync(environment, 10, 0, tenant: tenant, sort: SubjectSort.Newest));
    }

    [Fact]
    public async Task Newest_NeitherPagesNorCountsAnotherEnvironmentOrAnotherTenant()
    {
        var mine = await ArrangeCreatedAsync(("acme.mine.Thing", T0));
        await ArrangeCreatedAsync(("acme.theirs.Thing", T0.AddDays(5)));

        var environment = EnvironmentId.New();
        var tenantA = TenantId.New();
        var tenantB = TenantId.New();

        foreach (var (tenant, name) in new[] { (tenantA, "acme.a.Thing"), (tenantB, "acme.b.Thing") })
        {
            await using var context = fixture.NewContext(tenant);
            context.Subjects.Add(NewSubject(name, environment, T0.AddDays(2)));
            await context.SaveChangesAsync();
        }

        Assert.Equal(["acme.mine.Thing"], await PageAsync(mine, 10, 0, sort: SubjectSort.Newest));
        Assert.Equal(["acme.a.Thing"], await PageAsync(environment, 10, 0, tenant: tenantA, sort: SubjectSort.Newest));
        Assert.Equal(["acme.b.Thing"], await PageAsync(environment, 10, 0, tenant: tenantB, sort: SubjectSort.Newest));
    }
}
