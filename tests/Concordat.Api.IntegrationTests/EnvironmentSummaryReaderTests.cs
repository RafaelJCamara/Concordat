using System.Data.Common;
using Concordat.Application.Registry;
using Concordat.Domain.Contracts;
using Concordat.Domain.Governance;
using Concordat.Domain.Registry;
using Concordat.Formats.Abstractions;
using Concordat.Formats.Json;
using Concordat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Concordat.Api.IntegrationTests;

/// <summary>
/// The overview's arithmetic, against real PostgreSQL (ADR-037).
/// </summary>
/// <remarks>
/// <para>
/// A fake reader cannot count, and the numbers on a dashboard are exactly the thing a person
/// believes without checking. Every test here arranges rows with the domain's own methods (so a
/// held version is held because the aggregate says so, not because a column was set) and reads
/// them back through the real reader.
/// </para>
/// <para>
/// <b>Each test owns a fresh tenant.</b> The fixture's database is shared by the whole
/// collection, and a count taken over a shared tenant would be a count of whatever else had run.
/// A fresh tenant is also what makes the isolation tests mean something: the same
/// <see cref="EnvironmentId"/> is deliberately reused across two tenants, which the derived
/// environment ids make impossible in production but which is the sharpest possible way to ask
/// "does the tenant filter hold".
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public class EnvironmentSummaryReaderTests(PostgresFixture fixture)
{
    private static readonly JsonSchemaCanonicalizer Canonicalizer = new();

    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Since = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static ActorId Actor(string name = "alice") => ActorId.Create(name).Value;

    // The random half of a GUID. A version-7 GUID leads with its timestamp, so the first twelve
    // characters are the same for everything created within one millisecond, which is how two
    // schemas and two contracts in one test came to share a key.
    private static string Unique() => Guid.NewGuid().ToString("N")[..12];

    private static Subject NewSubject(
        EnvironmentId environment, string name, DateTimeOffset createdAt) =>
        Subject.Create(
            environment,
            SubjectName.Create(name).Value,
            SchemaFormat.Json,
            Actor("owner-" + name[^4..]),
            CompatibilityPolicy.Default,
            createdAt).Value;

    private static Schema NewSchema()
    {
        var canonical = Canonicalizer.Canonicalize(
            "{\"type\":\"object\",\"properties\":{\"p" + Unique() + "\":{\"type\":\"string\"}}}").Value;

        return Schema.Create(
            SchemaIdComputer.Compute(SchemaFormat.Json, canonical), SchemaFormat.Json, canonical)
            .Value;
    }

    /// <summary>Registers a version, and keeps the schema so it can be stored with the subject.</summary>
    private static int Register(
        Subject subject,
        List<Schema> schemas,
        DateTimeOffset at,
        bool breaking = false,
        string by = "alice",
        string? semver = null)
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
            null,
            Actor(by),
            at);

        Assert.True(outcome.IsSuccess, outcome.Error?.Message);
        return outcome.Value.Version.Ordinal;
    }

    private async Task SaveAsync(
        TenantId tenant, List<Schema> schemas, params object[] aggregates)
    {
        await using var context = fixture.NewContext(tenant);

        foreach (var schema in schemas)
        {
            context.Schemas.Add(schema);
        }

        foreach (var aggregate in aggregates)
        {
            context.Add(aggregate);
        }

        await context.SaveChangesAsync();
    }

    private async Task<EnvironmentSummary> ReadAsync(
        TenantId tenant,
        EnvironmentId environment,
        int newest = 6,
        int breaking = 5,
        DateTimeOffset? since = null)
    {
        await using var context = fixture.NewContext(tenant);

        return await new EnvironmentSummaryReader(context).ReadAsync(
            environment, newest, breaking, since ?? Since, CancellationToken.None);
    }

    private static Contract NewContract(
        EnvironmentId environment,
        EnforcementMode mode,
        int publishes,
        int consumes)
    {
        var contract = Contract.Create(environment, $"c-{Unique()}", T0, mode).Value;

        for (var i = 0; i < publishes; i++)
        {
            // Distinct exchanges, so no two bindings overlap and the contract invariant has
            // nothing to say about how many there are.
            var added = contract.AddPublish(new PublishBinding(
                Scope(),
                $"ex-{i}",
                RoutingKeyPattern.Create("orders.created").Value,
                [new SubjectRef(SubjectName.Create("acme.Order").Value, VersionSelector.Latest)]));

            Assert.True(added.IsSuccess, added.Error?.Message);
        }

        for (var i = 0; i < consumes; i++)
        {
            var added = contract.AddConsume(new ConsumeBinding(
                Scope(),
                $"queue-{i}",
                [new SubjectRef(SubjectName.Create("acme.Order").Value, VersionSelector.Latest)]));

            Assert.True(added.IsSuccess, added.Error?.Message);
        }

        return contract;
    }

    // A new instance per binding, not TopologyScope.Default: EF tracks an owned reference by
    // identity, so one shared instance on two bindings is saved for the first and nulled for the
    // second (a not-null violation on virtual_host). Requests never meet it, since each adds one
    // binding in its own context.
    private static TopologyScope Scope() => new(null, "/");

    private static EnforcementViolation NewViolation(
        EnvironmentId environment, DateTimeOffset lastSeen) =>
        EnforcementViolation.Open(
            environment,
            ViolationSide.Publish,
            $"orders/{Unique()}",
            "acme.Order",
            "payload_invalid",
            "bad",
            "orders-api",
            occurrences: 4,
            lastSeen).Value;

    // ------------------------------------------------------------------- an empty environment

    [Fact]
    public async Task AnEnvironmentNothingWasWrittenTo_IsZerosAndThreeEnforcementTiers()
    {
        var summary = await ReadAsync(TenantId.New(), EnvironmentId.New());

        Assert.Equal(new SubjectTotals(0, 0, 0), summary.Subjects);
        Assert.Equal(new VersionTotals(0, 0, 0, 0), summary.Versions);
        Assert.Empty(summary.NewestSubjects);
        Assert.Empty(summary.BreakingChanges);
        Assert.Equal(new ViolationTotals(0, 0), summary.Violations);

        // Always all three, in this order, zeros included: a client that had to notice a missing
        // mode would render "no contracts are in monitor" as an absent row instead of a zero.
        Assert.Equal(
            [EnforcementMode.Off, EnforcementMode.Monitor, EnforcementMode.Enforce],
            summary.Enforcement.Select(t => t.Mode));
        Assert.All(
            summary.Enforcement,
            t => Assert.Equal(0, t.Contracts + t.PublishBindings + t.ConsumeBindings));
    }

    // ------------------------------------------------------------------------ subject counts

    [Fact]
    public async Task SubjectsAreCountedByLifecycle_RetiredOnesIncluded()
    {
        var tenant = TenantId.New();
        var environment = EnvironmentId.New();
        var schemas = new List<Schema>();

        var active1 = NewSubject(environment, $"acme.a.S{Unique()}", T0);
        var active2 = NewSubject(environment, $"acme.b.S{Unique()}", T0);
        var deprecated = NewSubject(environment, $"acme.c.S{Unique()}", T0);
        var retired = NewSubject(environment, $"acme.d.S{Unique()}", T0);
        Assert.True(deprecated.Deprecate().IsSuccess);
        Assert.True(retired.Retire().IsSuccess);

        await SaveAsync(tenant, schemas, active1, active2, deprecated, retired);

        var summary = await ReadAsync(tenant, environment);

        // Retired subjects are still listed by the subject list, so they are still counted: a
        // Subjects card that disagreed with the list it links to would be the first thing anyone
        // checked.
        Assert.Equal(new SubjectTotals(2, 1, 1), summary.Subjects);
        Assert.Equal(4, summary.Subjects.Total);
    }

    // ------------------------------------------------------------------------ version counts

    [Fact]
    public async Task VersionsAreCountedByStatus_AllFourOfThem()
    {
        var tenant = TenantId.New();
        var environment = EnvironmentId.New();
        var schemas = new List<Schema>();

        // One subject carrying every status: active (v1), rejected (v2), approved-then-active (v3),
        // dismissed (v4), awaiting (v5).
        var subject = NewSubject(environment, $"acme.all.S{Unique()}", T0);
        Register(subject, schemas, T0.AddHours(1));
        var rejected = Register(subject, schemas, T0.AddHours(2), breaking: true);
        Assert.True(subject.Reject(rejected, Actor("bob"), T0.AddHours(3)).IsSuccess);
        var approved = Register(subject, schemas, T0.AddHours(4), breaking: true);
        Assert.True(subject.Approve(approved, Actor("bob"), T0.AddHours(5)).IsSuccess);
        Register(subject, schemas, T0.AddHours(6), breaking: true);
        Assert.Equal([4], subject.DismissPending(T0.AddHours(7)));
        Register(subject, schemas, T0.AddHours(8), breaking: true);

        var other = NewSubject(environment, $"acme.one.S{Unique()}", T0);
        Register(other, schemas, T0.AddHours(1));

        await SaveAsync(tenant, schemas, subject, other);

        var summary = await ReadAsync(tenant, environment);

        Assert.Equal(new VersionTotals(Active: 3, AwaitingApproval: 1, Rejected: 1, Dismissed: 1), summary.Versions);
        Assert.Equal(6, summary.Versions.Total);
    }

    // ------------------------------------------------------------------------ newest subjects

    [Fact]
    public async Task NewestSubjects_AreOrderedByCreationAndBoundedByTheLimit()
    {
        var tenant = TenantId.New();
        var environment = EnvironmentId.New();
        var schemas = new List<Schema>();

        var names = Enumerable.Range(0, 5).Select(i => $"acme.n{i}.S{Unique()}").ToList();
        var subjects = names
            .Select((name, i) => NewSubject(environment, name, T0.AddHours(i)))
            .ToList();

        // Saved in a scrambled order, so insertion order cannot be what the answer reflects.
        await SaveAsync(tenant, schemas, subjects[2], subjects[0], subjects[4], subjects[1], subjects[3]);

        var summary = await ReadAsync(tenant, environment, newest: 3);

        Assert.Equal([names[4], names[3], names[2]], summary.NewestSubjects.Select(s => s.Name));
    }

    [Fact]
    public async Task ASubjectWithNoVersionsIsNewestInItsPlace_NotSortedLast()
    {
        // The dashboard used to rank by the registration time of what `latest` pointed at, which
        // left a subject somebody had just created, and not yet registered a version for, at the
        // bottom of "recent" or off it. It is the newest thing in the registry.
        var tenant = TenantId.New();
        var environment = EnvironmentId.New();
        var schemas = new List<Schema>();

        var older = NewSubject(environment, $"acme.older.S{Unique()}", T0);
        Register(older, schemas, T0.AddHours(1));
        var brandNew = NewSubject(environment, $"acme.brandnew.S{Unique()}", T0.AddHours(2));

        await SaveAsync(tenant, schemas, older, brandNew);

        var summary = await ReadAsync(tenant, environment);

        Assert.Equal(
            [brandNew.Name.Value, older.Name.Value], summary.NewestSubjects.Select(s => s.Name));

        var fresh = summary.NewestSubjects[0];
        Assert.Null(fresh.Latest);
        Assert.Equal(0, fresh.Versions);
        Assert.Equal(0, fresh.AwaitingApproval);
    }

    [Fact]
    public async Task ANewestSubjectCarriesWhatItsCardNeeds()
    {
        var tenant = TenantId.New();
        var environment = EnvironmentId.New();
        var schemas = new List<Schema>();

        var subject = NewSubject(environment, $"acme.card.S{Unique()}", T0);
        Register(subject, schemas, T0.AddHours(1));
        Register(subject, schemas, T0.AddHours(2));
        Register(subject, schemas, T0.AddHours(3), breaking: true);
        Register(subject, schemas, T0.AddHours(4), breaking: true);
        Assert.True(subject.Deprecate().IsSuccess);

        await SaveAsync(tenant, schemas, subject);

        var card = Assert.Single((await ReadAsync(tenant, environment)).NewestSubjects);

        Assert.Equal(subject.Name.Value, card.Name);
        Assert.Equal(SchemaFormat.Json, card.Format);
        Assert.Equal(subject.Owner.Value, card.Owner);
        Assert.Equal(SubjectLifecycle.Deprecated, card.Lifecycle);

        // The gated pointer, not the highest ordinal: two versions are held above it.
        Assert.Equal(2, card.Latest);
        Assert.Equal(4, card.Versions);
        Assert.Equal(2, card.AwaitingApproval);
        Assert.Equal(T0, card.CreatedAt);
    }

    // ----------------------------------------------------------------------- breaking changes

    [Fact]
    public async Task BreakingChanges_AreHeldAndRejectedVersionsOnly_NewestFirst()
    {
        var tenant = TenantId.New();
        var environment = EnvironmentId.New();
        var schemas = new List<Schema>();

        var a = NewSubject(environment, $"acme.a.S{Unique()}", T0);
        Register(a, schemas, T0.AddHours(1));
        var aHeld = Register(a, schemas, T0.AddHours(10), breaking: true);

        var b = NewSubject(environment, $"acme.b.S{Unique()}", T0);
        Register(b, schemas, T0.AddHours(1));
        var bRejected = Register(b, schemas, T0.AddHours(12), breaking: true, by: "carol", semver: "2.0.0");
        Assert.True(b.Reject(bRejected, Actor("bob"), T0.AddHours(13)).IsSuccess);

        // None of these is in the list. An approved breaking change is live, a dismissed proposal
        // was reverted before anyone judged it, and an ordinary version is not breaking.
        var c = NewSubject(environment, $"acme.c.S{Unique()}", T0);
        Register(c, schemas, T0.AddHours(1));
        var cApproved = Register(c, schemas, T0.AddHours(14), breaking: true);
        Assert.True(c.Approve(cApproved, Actor("bob"), T0.AddHours(15)).IsSuccess);
        Register(c, schemas, T0.AddHours(16), breaking: true);
        Assert.Equal([3], c.DismissPending(T0.AddHours(17)));
        Register(c, schemas, T0.AddHours(18));

        await SaveAsync(tenant, schemas, a, b, c);

        var summary = await ReadAsync(tenant, environment);

        Assert.Collection(
            summary.BreakingChanges,
            first =>
            {
                Assert.Equal(b.Name.Value, first.Subject);
                Assert.Equal(bRejected, first.Ordinal);
                Assert.Equal(VersionStatus.Rejected, first.Status);
                Assert.Equal("2.0.0", first.SemanticVersion);
                Assert.Equal(T0.AddHours(12), first.RegisteredAt);
                Assert.Equal("carol", first.RegisteredBy);
                Assert.Equal(T0.AddHours(13), first.DecidedAt);
                Assert.Equal("bob", first.DecidedBy);
            },
            second =>
            {
                Assert.Equal(a.Name.Value, second.Subject);
                Assert.Equal(aHeld, second.Ordinal);
                Assert.Equal(VersionStatus.AwaitingApproval, second.Status);
                Assert.Null(second.SemanticVersion);
                Assert.Equal(T0.AddHours(10), second.RegisteredAt);
                Assert.Null(second.DecidedAt);
                Assert.Null(second.DecidedBy);
            });
    }

    [Fact]
    public async Task BreakingChanges_AreBoundedByTheLimit_KeepingTheNewest()
    {
        var tenant = TenantId.New();
        var environment = EnvironmentId.New();
        var schemas = new List<Schema>();

        var subject = NewSubject(environment, $"acme.many.S{Unique()}", T0);
        Register(subject, schemas, T0.AddHours(1));
        var held = Enumerable.Range(0, 4)
            .Select(i => Register(subject, schemas, T0.AddHours(10 + i), breaking: true))
            .ToList();

        await SaveAsync(tenant, schemas, subject);

        var summary = await ReadAsync(tenant, environment, breaking: 2);

        Assert.Equal([held[3], held[2]], summary.BreakingChanges.Select(v => v.Ordinal));

        // The count is not the list's length: four are held, two are shown.
        Assert.Equal(4, summary.Versions.AwaitingApproval);
    }

    // ------------------------------------------------------------------------- enforcement

    [Fact]
    public async Task EnforcementCoverage_CountsContractsAndBindingsPerMode()
    {
        var tenant = TenantId.New();
        var environment = EnvironmentId.New();

        await SaveAsync(
            tenant,
            [],
            NewContract(environment, EnforcementMode.Enforce, publishes: 3, consumes: 1),
            NewContract(environment, EnforcementMode.Enforce, publishes: 1, consumes: 0),
            NewContract(environment, EnforcementMode.Monitor, publishes: 2, consumes: 2),

            // A contract with no bindings is a contract: it counts once and adds no bindings.
            NewContract(environment, EnforcementMode.Monitor, publishes: 0, consumes: 0));

        var summary = await ReadAsync(tenant, environment);

        Assert.Equal(
            [
                new EnforcementTier(EnforcementMode.Off, 0, 0, 0),
                new EnforcementTier(EnforcementMode.Monitor, 2, 2, 2),
                new EnforcementTier(EnforcementMode.Enforce, 2, 4, 1),
            ],
            summary.Enforcement);
    }

    [Fact]
    public async Task AContractSetToOff_IsCountedUnderOff()
    {
        var tenant = TenantId.New();
        var environment = EnvironmentId.New();

        await SaveAsync(tenant, [], NewContract(environment, EnforcementMode.Off, 1, 1));

        var summary = await ReadAsync(tenant, environment);

        Assert.Equal(new EnforcementTier(EnforcementMode.Off, 1, 1, 1), summary.Enforcement[0]);
        Assert.Equal(0, summary.Enforcement[1].Contracts + summary.Enforcement[2].Contracts);
    }

    // -------------------------------------------------------------------------- violations

    [Fact]
    public async Task Violations_AreCountedDistinctAndRecent_TheWindowEdgeIsRecent()
    {
        var tenant = TenantId.New();
        var environment = EnvironmentId.New();

        await SaveAsync(
            tenant,
            [],
            NewViolation(environment, Since.AddSeconds(-1)),
            NewViolation(environment, Since),
            NewViolation(environment, Since.AddHours(3)),
            NewViolation(environment, Since.AddDays(-30)));

        var summary = await ReadAsync(tenant, environment);

        // Distinct is every row (a row is one violation however many messages hit it); recent is
        // last seen at or after the window's start, so a violation seen exactly then is recent.
        Assert.Equal(new ViolationTotals(Distinct: 4, Recent: 2), summary.Violations);
    }

    // --------------------------------------------------------------------------- isolation

    [Fact]
    public async Task NothingLeaksFromAnotherEnvironmentInTheSameTenant()
    {
        var tenant = TenantId.New();
        var mine = EnvironmentId.New();
        var theirs = EnvironmentId.New();
        var schemas = new List<Schema>();

        var visible = NewSubject(mine, $"acme.mine.S{Unique()}", T0);
        Register(visible, schemas, T0.AddHours(1));

        var hidden = NewSubject(theirs, $"acme.theirs.S{Unique()}", T0.AddHours(5));
        Register(hidden, schemas, T0.AddHours(6));
        Register(hidden, schemas, T0.AddHours(7), breaking: true);

        await SaveAsync(
            tenant,
            schemas,
            visible,
            hidden,
            NewContract(theirs, EnforcementMode.Enforce, 2, 2),
            NewViolation(theirs, Since.AddHours(1)));

        var summary = await ReadAsync(tenant, mine);

        Assert.Equal(new SubjectTotals(1, 0, 0), summary.Subjects);
        Assert.Equal(new VersionTotals(1, 0, 0, 0), summary.Versions);
        Assert.Equal([visible.Name.Value], summary.NewestSubjects.Select(s => s.Name));
        Assert.Empty(summary.BreakingChanges);
        Assert.All(summary.Enforcement, t => Assert.Equal(0, t.Contracts));
        Assert.Equal(new ViolationTotals(0, 0), summary.Violations);
    }

    [Fact]
    public async Task NothingLeaksFromAnotherTenant_EvenWithTheSameEnvironmentId()
    {
        // The same EnvironmentId in two tenants: production derives ids from the tenant too, so it
        // cannot happen there, which is why it is the sharpest way to ask whether the tenant
        // filter holds on every one of the six queries rather than the environment predicate
        // happening to separate them.
        var first = TenantId.New();
        var second = TenantId.New();
        var environment = EnvironmentId.New();

        var firstSchemas = new List<Schema>();
        var firstSubject = NewSubject(environment, $"acme.first.S{Unique()}", T0);
        Register(firstSubject, firstSchemas, T0.AddHours(1));
        Register(firstSubject, firstSchemas, T0.AddHours(2), breaking: true);

        await SaveAsync(
            first,
            firstSchemas,
            firstSubject,
            NewContract(environment, EnforcementMode.Enforce, 2, 1),
            NewViolation(environment, Since.AddHours(1)));

        var secondSchemas = new List<Schema>();
        var secondSubject = NewSubject(environment, $"acme.second.S{Unique()}", T0);
        Register(secondSubject, secondSchemas, T0.AddHours(1));

        await SaveAsync(second, secondSchemas, secondSubject);

        var theirs = await ReadAsync(second, environment);

        Assert.Equal(new SubjectTotals(1, 0, 0), theirs.Subjects);
        Assert.Equal(new VersionTotals(1, 0, 0, 0), theirs.Versions);
        Assert.Equal([secondSubject.Name.Value], theirs.NewestSubjects.Select(s => s.Name));
        Assert.Empty(theirs.BreakingChanges);
        Assert.All(theirs.Enforcement, t => Assert.Equal(0, t.Contracts + t.PublishBindings));
        Assert.Equal(new ViolationTotals(0, 0), theirs.Violations);

        var mine = await ReadAsync(first, environment);

        Assert.Equal(new SubjectTotals(1, 0, 0), mine.Subjects);
        Assert.Equal(1, mine.Versions.AwaitingApproval);
        Assert.Equal(new EnforcementTier(EnforcementMode.Enforce, 1, 2, 1), mine.Enforcement[2]);
        Assert.Equal(new ViolationTotals(1, 1), mine.Violations);
    }

    // ---------------------------------------------------------------------------------- cost

    [Fact]
    public async Task TheNumberOfQueriesDoesNotGrowWithTheData()
    {
        // The property ADR-037 exists for: a bounded number of aggregate queries and no
        // per-subject loop. The client-side derivation this replaces was one request that
        // materialised every subject and every version; a server-side loop would have kept that
        // cost and moved it. Counted, not timed: a timing assertion on a shared CI runner is a
        // flake with a lie attached.
        async Task<int> CommandsFor(int subjects)
        {
            var tenant = TenantId.New();
            var environment = EnvironmentId.New();
            var schemas = new List<Schema>();
            var aggregates = new List<object>();

            for (var i = 0; i < subjects; i++)
            {
                var subject = NewSubject(environment, $"acme.cost.S{Unique()}", T0.AddMinutes(i));
                Register(subject, schemas, T0.AddMinutes(i));
                Register(subject, schemas, T0.AddMinutes(i + 1), breaking: i % 2 == 0);
                aggregates.Add(subject);
                aggregates.Add(NewContract(environment, EnforcementMode.Monitor, 1, 1));
                aggregates.Add(NewViolation(environment, Since.AddMinutes(i)));
            }

            await SaveAsync(tenant, schemas, [.. aggregates]);

            var counter = new CommandCounter();
            await using var context = fixture.NewObservedContext(tenant, counter);

            await new EnvironmentSummaryReader(context).ReadAsync(
                environment, 6, 5, Since, CancellationToken.None);

            return counter.Count;
        }

        var few = await CommandsFor(2);
        var many = await CommandsFor(60);

        Assert.Equal(few, many);
        Assert.InRange(many, 1, 6);
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        private int _count;

        public int Count => _count;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
