using Concordat.Application.Abstractions;
using Concordat.Application.Registry;
using Concordat.Application.Tests.TestSupport;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;

namespace Concordat.Application.Tests;

/// <summary>
/// Listing the subjects of an environment in an order the caller chose (ADR-049). The order itself, and the
/// tiebreaker that makes it total, are the store's and are proved against PostgreSQL in
/// <c>SubjectPageRepositoryTests</c>; a fake store can only show what the handler decides: which orders exist,
/// that one that does not is refused before anything is read, and which question the store is asked.
/// </summary>
public class SubjectSortHandlerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly EnvironmentId _environment = EnvironmentId.New();
    private readonly FakeSubjects _subjects = new();

    private void Seed(string name, DateTimeOffset createdAt) =>
        _subjects.Seed(Build.Subject(_environment, name, createdAt: createdAt));

    private Task<Result<IReadOnlyList<Subject>>> ListAsync(
        string? sort, string? prefix = null, int? limit = null, int? offset = null) =>
        new ListSubjectsHandler(_subjects).HandleAsync(
            new ListSubjectsQuery(_environment, prefix, limit, offset, sort), CancellationToken.None);

    private async Task<IReadOnlyList<string>> NamesAsync(
        string? sort, string? prefix = null, int? limit = null, int? offset = null)
    {
        var result = await ListAsync(sort, prefix, limit, offset);

        Assert.True(result.IsSuccess, result.Error?.Message);
        return [.. result.Value.Select(s => s.Name.Value)];
    }

    // ------------------------------------------------------------------------------- the token

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("name")]
    [InlineData("NAME")]
    [InlineData(" Name ")]
    public void NoSortABlankOneOrName_IsTheNameOrder(string? text)
    {
        var parsed = SubjectSorts.Parse(text);

        Assert.True(parsed.IsSuccess, parsed.Error?.Message);
        Assert.Equal(SubjectSort.Name, parsed.Value);
    }

    [Theory]
    [InlineData("newest")]
    [InlineData("Newest")]
    [InlineData("NEWEST")]
    [InlineData("  newest\t")]
    public void NewestIsNewest_WhateverItsCaseAndWhitespace(string text)
    {
        var parsed = SubjectSorts.Parse(text);

        Assert.True(parsed.IsSuccess, parsed.Error?.Message);
        Assert.Equal(SubjectSort.Newest, parsed.Value);
    }

    [Theory]
    [InlineData("oldest")]
    [InlineData("newset")]
    [InlineData("created")]
    [InlineData("recent")]
    [InlineData("name,newest")]
    [InlineData("newest,newest")]
    [InlineData("-name")]
    [InlineData("name desc")]
    [InlineData("1")]
    [InlineData("null")]
    public void AnythingElse_IsRefusedWithItsOwnCode_AndNeverReadAsTheNameOrder(string text)
    {
        var parsed = SubjectSorts.Parse(text);

        Assert.True(parsed.IsFailure);
        Assert.Equal(ConcordatCodes.SubjectSortInvalid, parsed.Error!.Code);
        Assert.Equal("subject_sort_invalid", parsed.Error.Code);
        Assert.Contains(text, parsed.Error.Message, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------------------- the handler

    [Theory]
    [InlineData("oldest", null, null)]
    [InlineData("oldest", 10, null)]
    [InlineData("oldest", null, 5)]
    [InlineData("oldest", 10, 5)]
    public async Task AnUnknownSort_IsRefused_PagedOrNot_AndTheStoreIsNeverAsked(string sort, int? limit, int? offset)
    {
        _subjects.Seed(Build.Subject(_environment, "acme.orders.A"));

        var result = await ListAsync(sort, limit: limit, offset: offset);

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.SubjectSortInvalid, result.Error!.Code);
        Assert.Equal(0, _subjects.Listings);
        Assert.Empty(_subjects.PagesAsked);
        Assert.Empty(_subjects.PrefixesAsked);
    }

    [Fact]
    public async Task AnUnknownSort_IsRefusedEvenWhenThePrefixCouldMatchNothing()
    {
        // A prefix no name could begin with lists nothing, but it does not excuse a sort nobody understands:
        // the request is wrong in a way the empty answer would hide.
        var result = await ListAsync("oldest", prefix: "acme-", limit: 10);

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.SubjectSortInvalid, result.Error!.Code);
    }

    [Fact]
    public async Task NoSort_IsTheListAsItWas_WholeAndUnpaged()
    {
        for (var i = 0; i < 120; i++)
        {
            Seed($"acme.s{i:D3}.Thing", T0.AddHours(i));
        }

        Assert.Equal(120, (await NamesAsync(null)).Count);
        Assert.Equal(120, (await NamesAsync("")).Count);
        Assert.Equal(120, (await NamesAsync("name")).Count);

        // Three whole listings, and not one page: the unpaged name order is the route every client reads.
        Assert.Equal(3, _subjects.Listings);
        Assert.Empty(_subjects.PagesAsked);
        Assert.Empty(_subjects.SortsAsked);
    }

    [Fact]
    public async Task NewestWithoutAPage_IsTheWholeList_AsOneUnboundedPage_NewestFirst()
    {
        Seed("acme.b.Thing", T0);
        Seed("acme.a.Thing", T0);
        Seed("acme.c.Thing", T0.AddHours(1));

        var names = await NamesAsync("newest");

        Assert.Equal(["acme.c.Thing", "acme.a.Thing", "acme.b.Thing"], names);
        Assert.Equal(0, _subjects.Listings);
        Assert.Equal([SubjectPaging.Everything], _subjects.PagesAsked);
        Assert.Equal([SubjectSort.Newest], _subjects.SortsAsked);
    }

    [Fact]
    public async Task NewestWithAPage_AsksForThatPageInThatOrder_Clamped()
    {
        for (var i = 0; i < 10; i++)
        {
            Seed($"acme.s{i:D3}.Thing", T0.AddHours(i));
        }

        var page = await NamesAsync("newest", limit: 3, offset: 2);

        // The newest is s009; skipping two leaves s007, s006, s005.
        Assert.Equal(["acme.s007.Thing", "acme.s006.Thing", "acme.s005.Thing"], page);
        Assert.Equal([new SubjectPaging(3, 2)], _subjects.PagesAsked);
        Assert.Equal([SubjectSort.Newest], _subjects.SortsAsked);

        await NamesAsync("newest", limit: 100000, offset: -4);

        Assert.Equal(new SubjectPaging(500, 0), _subjects.PagesAsked[^1]);
    }

    [Fact]
    public async Task NameAsksTheStoreForTheNameOrder_WhenPaged()
    {
        Seed("acme.a.Thing", T0);

        await NamesAsync("name", limit: 5);
        await NamesAsync(null, limit: 5);

        Assert.Equal([SubjectSort.Name, SubjectSort.Name], _subjects.SortsAsked);
    }

    [Fact]
    public async Task NewestAndAPrefix_AreOneQuestion_ForThePrefixesRowsNewestFirst()
    {
        Seed("acme.orders.A", T0);
        Seed("acme.orders.B", T0.AddHours(2));
        Seed("acme.billing.A", T0.AddHours(5));

        var names = await NamesAsync("newest", prefix: "acme.orders.", limit: 10);

        Assert.Equal(["acme.orders.B", "acme.orders.A"], names);
        Assert.Equal(["acme.orders."], _subjects.PrefixesAsked);
    }

    [Fact]
    public async Task NewestAndAPrefixNoNameCouldBeginWith_ListsNothing_AndTheStoreIsNeverAsked()
    {
        Seed("acme.orders.A", T0);

        Assert.Empty(await NamesAsync("newest", prefix: "acme-"));
        Assert.Empty(await NamesAsync("newest", prefix: "100%", limit: 10, offset: 2));

        Assert.Empty(_subjects.PagesAsked);
    }

    [Fact]
    public async Task NewestAndABlankPrefix_IsEverySubject()
    {
        Seed("acme.a.Thing", T0);
        Seed("acme.b.Thing", T0.AddHours(1));

        Assert.Equal(["acme.b.Thing", "acme.a.Thing"], await NamesAsync("newest", prefix: "   "));
        Assert.Empty(_subjects.PrefixesAsked);
    }

    [Fact]
    public async Task TheCountDoesNotKnowTheOrder_ItCountsTheSameWhateverWasAsked()
    {
        Seed("acme.a.Thing", T0);
        Seed("acme.b.Thing", T0.AddHours(1));

        var counted = await new CountSubjectsHandler(_subjects).HandleAsync(
            new CountSubjectsQuery(_environment), CancellationToken.None);

        Assert.Equal(2, counted.Value);
    }
}
