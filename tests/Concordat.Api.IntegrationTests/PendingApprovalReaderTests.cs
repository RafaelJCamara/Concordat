using Concordat.Application.Registry;
using Concordat.Domain.Registry;
using Concordat.Formats.Abstractions;
using Concordat.Formats.Json;
using Concordat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Concordat.Api.IntegrationTests;

/// <summary>
/// The approval queue's ordering, counting and isolation, against real PostgreSQL (ADR-042).
/// </summary>
/// <remarks>
/// Every test arranges rows with the domain's own methods and owns a fresh tenant, for the
/// reasons <see cref="EnvironmentSummaryReaderTests"/> gives.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class PendingApprovalReaderTests(PostgresFixture fixture)
{
    private static readonly JsonSchemaCanonicalizer Canonicalizer = new();

    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private static ActorId Actor(string name = "alice") => ActorId.Create(name).Value;

    private static string Unique() => Guid.NewGuid().ToString("N")[..12];

    private static Subject NewSubject(EnvironmentId environment, string name) =>
        Subject.Create(
            environment,
            SubjectName.Create(name).Value,
            SchemaFormat.Json,
            Actor("owner"),
            CompatibilityPolicy.Default,
            T0).Value;

    private static Schema NewSchema()
    {
        var canonical = Canonicalizer.Canonicalize(
            "{\"type\":\"object\",\"properties\":{\"p" + Unique() + "\":{\"type\":\"string\"}}}").Value;

        return Schema.Create(
            SchemaIdComputer.Compute(SchemaFormat.Json, canonical), SchemaFormat.Json, canonical)
            .Value;
    }

    private static int Register(
        Subject subject,
        List<Schema> schemas,
        DateTimeOffset at,
        bool breaking = false,
        string by = "alice",
        string? semver = null,
        string? changelog = null)
    {
        var schema = NewSchema();
        schemas.Add(schema);

        var verdict = breaking
            ? CompatibilityVerdict.Breaking(CompatibilityPolicy.Default)
            : CompatibilityVerdict.Compatible(CompatibilityPolicy.Default);

        var outcome = subject.RegisterVersion(
            schema,
            verdict,
            semver is null ? null : SemanticVersion.Create(semver).Value,
            changelog,
            Actor(by),
            at);

        Assert.True(outcome.IsSuccess, outcome.Error?.Message);
        return outcome.Value.Version.Ordinal;
    }

    private async Task SaveAsync(TenantId tenant, List<Schema> schemas, params Subject[] subjects)
    {
        await using var context = fixture.NewContext(tenant);

        foreach (var schema in schemas)
        {
            context.Schemas.Add(schema);
        }

        foreach (var subject in subjects)
        {
            context.Add(subject);
        }

        await context.SaveChangesAsync();
    }

    private async Task<PendingApprovals> ReadAsync(
        TenantId tenant, EnvironmentId environment, int limit = 50, int offset = 0)
    {
        await using var context = fixture.NewContext(tenant);

        return await new PendingApprovalReader(context).ReadAsync(
            environment, limit, offset, CancellationToken.None);
    }

    [Fact]
    public async Task AnEnvironmentNothingWasWrittenTo_IsAnEmptyQueue()
    {
        var page = await ReadAsync(TenantId.New(), EnvironmentId.New());

        Assert.Equal(0, page.Total);
        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task OnlyAwaitingVersionsAreInTheQueue()
    {
        var tenant = TenantId.New();
        var environment = EnvironmentId.New();
        var schemas = new List<Schema>();

        // v1 active, v2 rejected, v3 approved, v4 dismissed, v5 awaiting: one of each status.
        var subject = NewSubject(environment, $"acme.all.S{Unique()}");
        Register(subject, schemas, T0.AddHours(1));
        var rejected = Register(subject, schemas, T0.AddHours(2), breaking: true);
        Assert.True(subject.Reject(rejected, Actor("bob"), T0.AddHours(3)).IsSuccess);
        var approved = Register(subject, schemas, T0.AddHours(4), breaking: true);
        Assert.True(subject.Approve(approved, Actor("bob"), T0.AddHours(5)).IsSuccess);
        Register(subject, schemas, T0.AddHours(6), breaking: true);
        Assert.Equal([4], subject.DismissPending(T0.AddHours(7)));
        var awaiting = Register(subject, schemas, T0.AddHours(8), breaking: true);

        await SaveAsync(tenant, schemas, subject);

        var page = await ReadAsync(tenant, environment);

        Assert.Equal(1, page.Total);
        var item = Assert.Single(page.Items);
        Assert.Equal(awaiting, item.Ordinal);
        Assert.Equal(subject.Name.Value, item.Subject);
    }

    [Fact]
    public async Task ItCarriesWhatTheReviewerNeeds_AndTheOrdinalApprovalWouldReplace()
    {
        var tenant = TenantId.New();
        var environment = EnvironmentId.New();
        var schemas = new List<Schema>();

        var subject = NewSubject(environment, $"acme.carry.S{Unique()}");
        Register(subject, schemas, T0.AddHours(1));
        var proposed = Register(
            subject,
            schemas,
            T0.AddHours(2),
            breaking: true,
            by: "carol",
            semver: "2.0.0",
            changelog: "required id");

        await SaveAsync(tenant, schemas, subject);

        var item = Assert.Single((await ReadAsync(tenant, environment)).Items);

        Assert.Equal(proposed, item.Ordinal);
        Assert.Equal("2.0.0", item.SemanticVersion);
        Assert.Equal("required id", item.Changelog);
        Assert.Equal("carol", item.RegisteredBy);
        Assert.Equal(T0.AddHours(2), item.RegisteredAt);
        Assert.Equal(1, item.Baseline);
    }

    [Fact]
    public async Task TheBaselineMovesWhenAnEarlierProposalIsApproved()
    {
        var tenant = TenantId.New();
        var environment = EnvironmentId.New();
        var schemas = new List<Schema>();

        // v1 live, v2 and v3 both held. Approving v2 makes it latest, so v3 now replaces v2: the
        // reason the page refetches the queue after a decision instead of patching one row.
        var subject = NewSubject(environment, $"acme.move.S{Unique()}");
        Register(subject, schemas, T0.AddHours(1));
        var v2 = Register(subject, schemas, T0.AddHours(2), breaking: true);
        var v3 = Register(subject, schemas, T0.AddHours(3), breaking: true);
        Assert.True(subject.Approve(v2, Actor("bob"), T0.AddHours(4)).IsSuccess);

        await SaveAsync(tenant, schemas, subject);

        var item = Assert.Single((await ReadAsync(tenant, environment)).Items);

        Assert.Equal(v3, item.Ordinal);
        Assert.Equal(v2, item.Baseline);
    }

    [Fact]
    public async Task TheLongestWaitLeads_AndTiesAreBrokenByNameThenOrdinal()
    {
        var tenant = TenantId.New();
        var environment = EnvironmentId.New();
        var schemas = new List<Schema>();

        var suffix = Unique();
        var bravo = NewSubject(environment, $"acme.b.{suffix}");
        var alpha = NewSubject(environment, $"acme.a.{suffix}");
        var charlie = NewSubject(environment, $"acme.c.{suffix}");

        foreach (var subject in new[] { bravo, alpha, charlie })
        {
            Register(subject, schemas, T0.AddHours(1));
        }

        // Proposed in this order: charlie first (oldest), then alpha and bravo in the same instant,
        // alpha twice. Insertion order is deliberately not the expected order.
        Register(charlie, schemas, T0.AddHours(2), breaking: true);
        Register(bravo, schemas, T0.AddHours(3), breaking: true);
        Register(alpha, schemas, T0.AddHours(3), breaking: true);
        Register(alpha, schemas, T0.AddHours(3), breaking: true);

        await SaveAsync(tenant, schemas, bravo, alpha, charlie);

        var page = await ReadAsync(tenant, environment);

        Assert.Equal(
            [
                (charlie.Name.Value, 2),
                (alpha.Name.Value, 2),
                (alpha.Name.Value, 3),
                (bravo.Name.Value, 2),
            ],
            page.Items.Select(i => (i.Subject, i.Ordinal)));
    }

    [Fact]
    public async Task APageIsASliceOfTheWholeQueue_AndTotalIsTheWholeQueue()
    {
        var tenant = TenantId.New();
        var environment = EnvironmentId.New();
        var schemas = new List<Schema>();

        // Seven held versions, one subject each, proposed an hour apart.
        var subjects = Enumerable.Range(0, 7)
            .Select(i => NewSubject(environment, $"acme.p{i}.S{Unique()}"))
            .ToList();

        for (var i = 0; i < subjects.Count; i++)
        {
            Register(subjects[i], schemas, T0.AddHours(1));
            Register(subjects[i], schemas, T0.AddHours(10 + i), breaking: true);
        }

        await SaveAsync(tenant, schemas, subjects.ToArray());

        var first = await ReadAsync(tenant, environment, limit: 3, offset: 0);
        var second = await ReadAsync(tenant, environment, limit: 3, offset: 3);
        var last = await ReadAsync(tenant, environment, limit: 3, offset: 6);
        var past = await ReadAsync(tenant, environment, limit: 3, offset: 7);

        // Total is the queue's length on every page, so a screen can say "3 of 7" from any of them.
        Assert.All(new[] { first, second, last, past }, p => Assert.Equal(7, p.Total));

        Assert.Equal(
            subjects.Select(s => s.Name.Value),
            first.Items.Concat(second.Items).Concat(last.Items).Select(i => i.Subject));
        Assert.Equal(3, first.Items.Count);
        Assert.Equal(3, second.Items.Count);
        Assert.Single(last.Items);
        Assert.Empty(past.Items);
    }

    [Fact]
    public async Task AnotherEnvironmentsProposalsAreNotInThisQueue()
    {
        var tenant = TenantId.New();
        var mine = EnvironmentId.New();
        var theirs = EnvironmentId.New();
        var schemas = new List<Schema>();

        var ours = NewSubject(mine, $"acme.ours.S{Unique()}");
        var other = NewSubject(theirs, $"acme.other.S{Unique()}");

        foreach (var subject in new[] { ours, other })
        {
            Register(subject, schemas, T0.AddHours(1));
            Register(subject, schemas, T0.AddHours(2), breaking: true);
        }

        await SaveAsync(tenant, schemas, ours, other);

        var page = await ReadAsync(tenant, mine);

        Assert.Equal(1, page.Total);
        Assert.Equal(ours.Name.Value, Assert.Single(page.Items).Subject);
    }

    [Fact]
    public async Task AnotherTenantsProposalsAreNotInThisQueue_EvenInTheSameEnvironmentId()
    {
        // The same EnvironmentId under two tenants, which derived ids make impossible in
        // production and which is the sharpest way to ask whether the tenant filter holds.
        var environment = EnvironmentId.New();
        var tenantA = TenantId.New();
        var tenantB = TenantId.New();

        foreach (var tenant in new[] { tenantA, tenantB })
        {
            var schemas = new List<Schema>();
            var subject = NewSubject(environment, $"acme.shared.S{Unique()}");
            Register(subject, schemas, T0.AddHours(1));
            Register(subject, schemas, T0.AddHours(2), breaking: true);
            await SaveAsync(tenant, schemas, subject);
        }

        var a = await ReadAsync(tenantA, environment);
        var b = await ReadAsync(tenantB, environment);

        Assert.Equal(1, a.Total);
        Assert.Equal(1, b.Total);
        Assert.NotEqual(Assert.Single(a.Items).Subject, Assert.Single(b.Items).Subject);
    }

    [Fact]
    public async Task ItCarriesTheReviewersTheSubjectNames_AsStored_AndNoneWhenItNamesNone()
    {
        var tenant = TenantId.New();
        var environment = EnvironmentId.New();
        var schemas = new List<Schema>();

        var named = NewSubject(environment, $"acme.named.S{Unique()}");
        var open = NewSubject(environment, $"acme.open.S{Unique()}");

        foreach (var subject in new[] { named, open })
        {
            Register(subject, schemas, T0.AddHours(1));
            Register(subject, schemas, T0.AddHours(2), breaking: true);
        }

        // An actor id can hold a comma and a quote; the column must give it back whole.
        Assert.True(named.SetReviewers(
            [Actor("Alice@Example.com"), Actor("ci, prod \"blue\"")]).IsSuccess);

        await SaveAsync(tenant, schemas, named, open);

        var page = await ReadAsync(tenant, environment);

        Assert.Equal(
            ["Alice@Example.com", "ci, prod \"blue\""],
            page.Items.Single(i => i.Subject == named.Name.Value).Reviewers);
        Assert.Empty(page.Items.Single(i => i.Subject == open.Name.Value).Reviewers);
    }

    [Fact]
    public async Task TheReviewersSurviveASaveAndALoad_AndASecondEdit()
    {
        // An edit of an edit, through a freshly loaded aggregate: what is stored is what the second
        // edit wrote, not the first. And a subject that is only loaded is not dirty, which is what the
        // column's comparer is for (a converter without one compares the list by reference, and a
        // loaded subject would be written back on every save).
        var tenant = TenantId.New();
        var environment = EnvironmentId.New();
        var subject = NewSubject(environment, $"acme.edit.S{Unique()}");
        Assert.True(subject.SetReviewers([Actor("a@example.com")]).IsSuccess);

        await SaveAsync(tenant, [], subject);

        await using (var context = fixture.NewContext(tenant))
        {
            var loaded = await context.Subjects.SingleAsync(s => s.Id == subject.Id);
            Assert.Equal(["a@example.com"], loaded.Reviewers.Select(r => r.Value));
            Assert.False(context.ChangeTracker.HasChanges());

            Assert.True(loaded.SetReviewers([Actor("b@example.com"), Actor("c@example.com")]).IsSuccess);
            await context.SaveChangesAsync();
        }

        await using (var context = fixture.NewContext(tenant))
        {
            var reloaded = await context.Subjects.AsNoTracking().SingleAsync(s => s.Id == subject.Id);
            Assert.Equal(["b@example.com", "c@example.com"], reloaded.Reviewers.Select(r => r.Value));
        }
    }

    [Fact]
    public async Task ARetiredSubjectsProposalIsStillWaiting()
    {
        var tenant = TenantId.New();
        var environment = EnvironmentId.New();
        var schemas = new List<Schema>();

        var subject = NewSubject(environment, $"acme.retired.S{Unique()}");
        Register(subject, schemas, T0.AddHours(1));
        Register(subject, schemas, T0.AddHours(2), breaking: true);
        Assert.True(subject.Retire().IsSuccess);

        await SaveAsync(tenant, schemas, subject);

        // The dashboard's awaiting count includes it, and the queue the browser used to build
        // from the subject list included it; a queue that dropped it would disagree with both.
        var page = await ReadAsync(tenant, environment);

        Assert.Equal(1, page.Total);
    }
}
