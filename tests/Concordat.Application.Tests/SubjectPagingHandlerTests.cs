using Concordat.Application.Abstractions;
using Concordat.Application.Registry;
using Concordat.Application.Tests.TestSupport;
using Concordat.Domain.Registry;

namespace Concordat.Application.Tests;

/// <summary>
/// Listing a page of an environment's subjects, and counting them (ADR-045). The ordering and the
/// arithmetic are the store's and are proved against PostgreSQL in <c>SubjectPageRepositoryTests</c>; a
/// fake store can only show what the handler decides: whether to page, how large, from where.
/// </summary>
public class SubjectPagingHandlerTests
{
    private readonly EnvironmentId _environment = EnvironmentId.New();
    private readonly FakeSubjects _subjects = new();

    private void Seed(params string[] names)
    {
        foreach (var name in names)
        {
            _subjects.Seed(Build.Subject(_environment, name));
        }
    }

    private async Task<IReadOnlyList<string>> ListAsync(
        string? prefix = null, int? limit = null, int? offset = null)
    {
        var result = await new ListSubjectsHandler(_subjects).HandleAsync(
            new ListSubjectsQuery(_environment, prefix, limit, offset), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        return [.. result.Value.Select(s => s.Name.Value)];
    }

    private async Task<int> CountAsync(string? prefix = null)
    {
        var result = await new CountSubjectsHandler(_subjects).HandleAsync(
            new CountSubjectsQuery(_environment, prefix), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value;
    }

    private static string[] Names(int count) =>
        [.. Enumerable.Range(0, count).Select(i => $"acme.s{i:D3}.Thing")];

    // ----------------------------------------------------------------------------- not paged

    [Fact]
    public async Task WithoutALimitOrAnOffset_TheWholeListIsReturned_AsBeforeAndWithoutAPage()
    {
        Seed(Names(120));

        var listed = await ListAsync();

        Assert.Equal(120, listed.Count);
        Assert.Equal(1, _subjects.Listings);
        Assert.Empty(_subjects.PagesAsked);
    }

    [Fact]
    public void ARequestIsPagedExactlyWhenALimitOrAnOffsetWasNamed()
    {
        Assert.False(new ListSubjectsQuery(_environment).IsPaged);
        Assert.False(new ListSubjectsQuery(_environment, "acme.").IsPaged);
        Assert.True(new ListSubjectsQuery(_environment, null, 10).IsPaged);
        Assert.True(new ListSubjectsQuery(_environment, null, null, 0).IsPaged);
        Assert.True(new ListSubjectsQuery(_environment, null, 0, 0).IsPaged);
    }

    // --------------------------------------------------------------------------------- paged

    [Fact]
    public async Task APageIsTheRowsFromTheOffsetForTheLimit_InNameOrder()
    {
        Seed(Names(10));

        Assert.Equal(["acme.s003.Thing", "acme.s004.Thing", "acme.s005.Thing"], await ListAsync(limit: 3, offset: 3));
        Assert.Equal(["acme.s009.Thing"], await ListAsync(limit: 3, offset: 9));
        Assert.Empty(await ListAsync(limit: 3, offset: 10));
        Assert.Empty(await ListAsync(limit: 3, offset: 1000));
    }

    [Fact]
    public async Task ARequestForAPageNeverReadsTheWholeList()
    {
        Seed(Names(120));

        await ListAsync(limit: 5);

        Assert.Equal(0, _subjects.Listings);
        Assert.Equal([new SubjectPaging(5, 0)], _subjects.PagesAsked);
    }

    [Theory]
    [InlineData(null, 0, 50, 0)]
    [InlineData(null, 40, 50, 40)]
    [InlineData(0, 0, 1, 0)]
    [InlineData(-5, 0, 1, 0)]
    [InlineData(1, 0, 1, 0)]
    [InlineData(500, 0, 500, 0)]
    [InlineData(501, 0, 500, 0)]
    [InlineData(int.MaxValue, 0, 500, 0)]
    [InlineData(10, -1, 10, 0)]
    [InlineData(10, int.MinValue, 10, 0)]
    public async Task ThePageIsClampedAndNeverRefused(int? asked, int offset, int limit, int from)
    {
        Seed(Names(3));

        await ListAsync(limit: asked, offset: offset);

        Assert.Equal([new SubjectPaging(limit, from)], _subjects.PagesAsked);
    }

    [Fact]
    public async Task APrefixAndAPageTogether_AreOneQuestion_ForThePrefixesRows()
    {
        Seed("acme.orders.A", "acme.orders.B", "acme.orders.C", "acme.billing.A");

        var page = await ListAsync(prefix: "acme.orders.", limit: 2, offset: 1);

        Assert.Equal(["acme.orders.B", "acme.orders.C"], page);
        Assert.Equal(["acme.orders."], _subjects.PrefixesAsked);
    }

    [Fact]
    public async Task APrefixNoNameCouldBeginWith_ListsNothingAndTheStoreIsNeverAsked_PagedOrNot()
    {
        Seed(Names(3));

        Assert.Empty(await ListAsync(prefix: "acme-", limit: 10));
        Assert.Empty(await ListAsync(prefix: "100%", limit: 10, offset: 2));

        Assert.Empty(_subjects.PagesAsked);
    }

    [Fact]
    public async Task ABlankPrefixAndAPage_IsEveryRowsPage()
    {
        Seed(Names(4));

        Assert.Equal(["acme.s000.Thing", "acme.s001.Thing"], await ListAsync(prefix: "  ", limit: 2));
        Assert.Empty(_subjects.PrefixesAsked);
    }

    [Fact]
    public async Task AnotherEnvironmentsSubjectsAreNotInThePage()
    {
        Seed(Names(3));
        _subjects.Seed(Build.Subject(EnvironmentId.New(), "acme.elsewhere.Thing"));

        Assert.DoesNotContain("acme.elsewhere.Thing", await ListAsync(limit: 50));
    }

    // ---------------------------------------------------------------------------------- count

    [Fact]
    public async Task TheCountIsTheWholeListWhateverPageWasAsked()
    {
        Seed(Names(120));

        Assert.Equal(120, await CountAsync());
        Assert.Equal(1, _subjects.Counts);
    }

    [Fact]
    public async Task TheCountOfAPrefixIsTheNumberThatBeginWithIt()
    {
        Seed("acme.orders.A", "acme.orders.B", "acme.billing.A");

        Assert.Equal(2, await CountAsync("acme.orders."));
        Assert.Equal(3, await CountAsync("acme."));
        Assert.Equal(0, await CountAsync("ACME."));
    }

    [Fact]
    public async Task TheCountOfAPrefixNoNameCouldBeginWith_IsZeroAndTheStoreIsNeverAsked()
    {
        Seed(Names(3));

        Assert.Equal(0, await CountAsync("acme-"));
        Assert.Equal(0, _subjects.Counts);
    }

    [Fact]
    public async Task TheCountOfABlankPrefix_IsEverySubject()
    {
        Seed(Names(3));

        Assert.Equal(3, await CountAsync("   "));
    }

    [Fact]
    public async Task TheCountAndTheListAgreeOnWhatABlankOrAbsentPrefixMeans()
    {
        Seed(Names(7));

        Assert.Equal((await ListAsync(limit: 500)).Count, await CountAsync());
        Assert.Equal((await ListAsync(prefix: "", limit: 500)).Count, await CountAsync(""));
    }
}
