using Concordat.Domain.Registry;
using Concordat.Formats.Abstractions;
using Concordat.Formats.Json;
using Concordat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Concordat.Api.IntegrationTests;

/// <summary>
/// The prefix filter, against real PostgreSQL (ADR-038).
/// </summary>
/// <remarks>
/// <para>
/// A value converter on <c>Subject.Name</c> is why this needs a database at all: EF cannot
/// translate <c>StartsWith</c> through one, so the filter is a mapped database function, and the
/// only thing that proves a mapped function says what it should is running it. The in-memory
/// fake the handler tests use compares with <c>string.StartsWith</c> and would pass for any
/// translation.
/// </para>
/// <para>
/// Each test arranges its own environment, so none sees another's subjects and the order tests run
/// in does not matter.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public class SubjectPrefixRepositoryTests(PostgresFixture fixture)
{
    private static readonly JsonSchemaCanonicalizer Canonicalizer = new();

    private static ActorId Actor => ActorId.Create("alice").Value;

    private static Subject NewSubject(string name, EnvironmentId environment) =>
        Subject.Create(
            environment,
            SubjectName.Create(name).Value,
            SchemaFormat.Json,
            Actor,
            CompatibilityPolicy.Default,
            DateTimeOffset.UtcNow).Value;

    private async Task<EnvironmentId> ArrangeAsync(params string[] names) =>
        await ArrangeAsync(tenant: null, names);

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

    private async Task<List<string>> ListAsync(
        EnvironmentId environment, string prefix, TenantId? tenant = null)
    {
        await using var context = fixture.NewContext(tenant);

        var found = await new SubjectRepository(context).ListByPrefixAsync(
            environment, SubjectNamePrefix.Create(prefix).Value, CancellationToken.None);

        return [.. found.Select(s => s.Name.Value)];
    }

    [Fact]
    public async Task OnlyTheNamesThatBeginWithThePrefixAreReturned_InNameOrder()
    {
        var environment = await ArrangeAsync(
            "acme.orders.OrderShipped",
            "acme.orders.OrderCreated",
            "acme.billing.InvoiceRaised",
            "acme.ordersnot.Other",
            "other.orders.Thing");

        Assert.Equal(
            ["acme.orders.OrderCreated", "acme.orders.OrderShipped"],
            await ListAsync(environment, "acme.orders."));
    }

    [Fact]
    public async Task AWholeName_IsItsOwnPrefix()
    {
        var environment = await ArrangeAsync("acme.exact.Name", "acme.exact.NameAndMore");

        Assert.Equal(
            ["acme.exact.Name", "acme.exact.NameAndMore"],
            await ListAsync(environment, "acme.exact.Name"));
    }

    [Fact]
    public async Task NothingBeginsWithIt_IsAnEmptyList()
    {
        var environment = await ArrangeAsync("acme.orders.OrderCreated");

        Assert.Empty(await ListAsync(environment, "acme.nothing."));
    }

    [Fact]
    public async Task ThePrefixIsCaseSensitive()
    {
        // OrderCreated and ordercreated are two subjects. A case-insensitive match (ILIKE, a
        // lower() on both sides, a citext column) would return both for either spelling and a
        // typo'd casing would look like success.
        var environment = await ArrangeAsync(
            "acme.case.OrderCreated", "acme.case.ordercreated", "acme.Case.OrderCreated");

        Assert.Equal(["acme.case.OrderCreated"], await ListAsync(environment, "acme.case.Order"));
        Assert.Equal(["acme.case.ordercreated"], await ListAsync(environment, "acme.case.order"));
        Assert.Equal(["acme.Case.OrderCreated"], await ListAsync(environment, "acme.Case."));
        Assert.Empty(await ListAsync(environment, "ACME."));
    }

    [Fact]
    public async Task AnUnderscoreIsALiteralNotALikeWildcard()
    {
        // The one LIKE metacharacter that is also a legal name character. Left unescaped, "a_"
        // would match "ab", "ax" and every other two-letter start, and the web app's box would
        // return other people's subjects for a name that happens to contain one.
        var environment = await ArrangeAsync("acme.us.a_b", "acme.us.axb", "acme.us.a", "acme.us.A_b");

        Assert.Equal(["acme.us.a_b"], await ListAsync(environment, "acme.us.a_"));
        Assert.Equal(["acme.us.a_b"], await ListAsync(environment, "acme.us.a_b"));
        Assert.Equal(["acme.us.A_b"], await ListAsync(environment, "acme.us.A_"));
    }

    [Fact]
    public async Task AnUnderscoreAtTheStart_IsStillALiteral()
    {
        var environment = await ArrangeAsync("_internal.Thing", "xinternal.Thing", "_.x");

        // Sorted here, because how "_.x" orders against "_internal.Thing" is the database
        // collation's business (a locale that ignores punctuation reverses them) and not this test's.
        Assert.Equal(
            ["_.x", "_internal.Thing"], (await ListAsync(environment, "_")).Order(StringComparer.Ordinal));
        Assert.Equal(["_internal.Thing"], await ListAsync(environment, "_i"));
    }

    [Fact]
    public async Task APrefixLongerThanAnyNameInTheTable_FindsNothingRatherThanFailing()
    {
        var environment = await ArrangeAsync("acme.short.A");

        Assert.Empty(await ListAsync(environment, "acme.short.A" + new string('b', 400)));
    }

    [Fact]
    public async Task ASubjectInAnotherEnvironmentIsNotReturned()
    {
        var mine = await ArrangeAsync("acme.scope.Mine");
        await ArrangeAsync("acme.scope.Theirs");

        Assert.Equal(["acme.scope.Mine"], await ListAsync(mine, "acme.scope."));
    }

    [Fact]
    public async Task ASubjectOfAnotherTenantIsNotReturned_EvenInTheSameEnvironmentId()
    {
        // The tenant filter is a global query filter, so a hand-written predicate (and any
        // translation of one) is ANDed with it. This is the test that would notice a filter
        // written as raw SQL that bypasses it.
        var other = TenantId.New();
        var environment = await ArrangeAsync(other, "acme.tenant.Theirs");
        await using (var context = fixture.NewContext())
        {
            context.Subjects.Add(NewSubject("acme.tenant.Mine", environment));
            await context.SaveChangesAsync();
        }

        Assert.Equal(["acme.tenant.Mine"], await ListAsync(environment, "acme.tenant."));
        Assert.Equal(
            ["acme.tenant.Theirs"], await ListAsync(environment, "acme.tenant.", tenant: other));
    }

    [Fact]
    public async Task TheVersionsComeWithTheSubject_AsInTheUnfilteredList()
    {
        // SubjectResponse embeds each subject's versions, so a filtered list that returned
        // subjects without them would be a different (and silently emptier) answer.
        var environment = EnvironmentId.New();
        var canonical = Canonicalizer.Canonicalize("""{"type":"object","title":"prefix-versions"}""").Value;
        var schema = Schema.Create(
            SchemaIdComputer.Compute(SchemaFormat.Json, canonical, []),
            SchemaFormat.Json, canonical, []).Value;
        var subject = NewSubject("acme.versions.Thing", environment);
        Assert.True(subject.RegisterVersion(
            schema,
            CompatibilityVerdict.Compatible(CompatibilityPolicy.Default),
            SemanticVersion.Create("1.0.0").Value,
            "initial",
            Actor,
            DateTimeOffset.UtcNow).IsSuccess);

        await using (var write = fixture.NewContext())
        {
            write.Schemas.Add(schema);
            write.Subjects.Add(subject);
            await write.SaveChangesAsync();
        }

        await using var read = fixture.NewContext();
        var found = await new SubjectRepository(read).ListByPrefixAsync(
            environment, SubjectNamePrefix.Create("acme.versions.").Value, CancellationToken.None);

        Assert.Single(Assert.Single(found).Versions);
    }

    [Fact]
    public async Task TheSqlIsAnOrdinalPrefixTest_NotAPatternAndNotALocaleComparison()
    {
        // Names that a locale-aware comparison would order and group differently from bytes: an
        // upper-case letter sorts between lower-case ones under en_US. The prefix test must still
        // be bytewise, or "acme.Zed" would be reachable from "acme.z" (it is not: the first
        // assertion has no Zed in it).
        var environment = await ArrangeAsync("acme.Zed.A", "acme.zed.A", "acme.zEd.A");

        Assert.Equal(
            ["acme.zEd.A", "acme.zed.A"],
            (await ListAsync(environment, "acme.z")).Order(StringComparer.Ordinal));
        Assert.Equal(["acme.Zed.A"], await ListAsync(environment, "acme.Z"));
        Assert.Equal(["acme.zEd.A"], await ListAsync(environment, "acme.zE"));
        Assert.Equal(["acme.zed.A"], await ListAsync(environment, "acme.ze"));
    }

    [Fact]
    public void ThePrefixIsTranslatedToStartsWithAndNotToALikePattern()
    {
        // Looks at the SQL itself, because every behaviour above also passes for a LIKE with
        // correct escaping, and the point of choosing starts_with is that there is nothing to
        // escape and so nothing to forget (ADR-038).
        using var context = fixture.NewContext();

        var sql = context.Subjects
            .Where(s => ConcordatDbFunctions.StartsWith(EF.Property<string>(s, nameof(Subject.Name)), "acme.orders."))
            .ToQueryString();

        Assert.Contains("starts_with(", sql, StringComparison.Ordinal);
        Assert.DoesNotContain(" LIKE ", sql, StringComparison.OrdinalIgnoreCase);
    }
}
