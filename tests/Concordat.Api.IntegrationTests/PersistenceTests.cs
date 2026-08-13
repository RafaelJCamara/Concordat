using Concordat.Domain.Registry;
using Concordat.Formats.Abstractions;
using Concordat.Formats.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Concordat.Api.IntegrationTests;

[Collection(PostgresCollection.Name)]
public class PersistenceTests(PostgresFixture fixture)
{
    private static readonly JsonSchemaCanonicalizer Canonicalizer = new();

    private static ActorId Actor(string name = "alice") => ActorId.Create(name).Value;

    private static Schema NewSchema(string body, params Reference[] references)
    {
        var canonical = Canonicalizer.Canonicalize(body).Value;
        var id = SchemaIdComputer.Compute(SchemaFormat.Json, canonical, references);
        return Schema.Create(id, SchemaFormat.Json, canonical, references).Value;
    }

    private static Subject NewSubject(string name, EnvironmentId environment) =>
        Subject.Create(
            environment,
            SubjectName.Create(name).Value,
            SchemaFormat.Json,
            Actor(),
            CompatibilityPolicy.Default,
            DateTimeOffset.UtcNow).Value;

    [Fact]
    public async Task ASubjectWithVersions_RoundTrips()
    {
        var environment = EnvironmentId.New();
        var schema = NewSchema("""{"type":"object","properties":{"id":{"type":"string"}}}""");
        var subject = NewSubject("acme.roundtrip.Order", environment);

        var registered = subject.RegisterVersion(
            schema,
            CompatibilityVerdict.Compatible(CompatibilityPolicy.Default),
            SemanticVersion.Create("1.0.0").Value,
            "initial",
            Actor(),
            DateTimeOffset.UtcNow);
        Assert.True(registered.IsSuccess, registered.Error?.Message);

        await using (var write = fixture.NewContext())
        {
            write.Schemas.Add(schema);
            write.Subjects.Add(subject);
            await write.SaveChangesAsync();
        }

        await using var read = fixture.NewContext();
        var loaded = await read.Subjects.SingleAsync(s => s.Id == subject.Id);

        Assert.Equal("acme.roundtrip.Order", loaded.Name.Value);
        Assert.Equal(SchemaFormat.Json, loaded.Format);
        Assert.Equal(CompatibilityPolicy.Default, loaded.CompatibilityPolicy);
        Assert.Equal(ContentModel.Open, loaded.ContentModel);
        Assert.Equal(SubjectLifecycle.Active, loaded.Lifecycle);

        var version = Assert.Single(loaded.Versions);
        Assert.Equal(1, version.Ordinal);
        Assert.Equal(schema.Id, version.SchemaId);
        Assert.Equal(new SemanticVersion(1, 0, 0), version.SemanticVersion);
        Assert.Equal("initial", version.Changelog);
        Assert.Equal(VersionStatus.Active, version.Status);

        Assert.NotNull(loaded.Latest);
        Assert.Equal(1, loaded.Latest.Ordinal);
    }

    [Fact]
    public async Task AnInheritingSubject_RoundTripsAsNullNotAsTheDefault()
    {
        // The distinction that cannot be retrofitted: "inheriting" must not collapse into
        // "explicitly configured to whatever the default happens to be".
        var subject = Subject.Create(
            EnvironmentId.New(),
            SubjectName.Create("acme.inherit.Order").Value,
            SchemaFormat.Json,
            Actor(),
            compatibilityPolicy: null,
            DateTimeOffset.UtcNow).Value;

        await using (var write = fixture.NewContext())
        {
            write.Subjects.Add(subject);
            await write.SaveChangesAsync();
        }

        await using var read = fixture.NewContext();
        var loaded = await read.Subjects.SingleAsync(s => s.Id == subject.Id);

        Assert.Null(loaded.CompatibilityPolicy);
    }

    [Fact]
    public async Task RegisteringTheSameSchemaContentTwice_CollidesOnThePrimaryKey()
    {
        // Idempotency without a counter or a lock: the content-addressed id IS the key, so a
        // concurrent duplicate loses the insert and reads the winner (ADR-015).
        var schema = NewSchema("""{"type":"object","x":"duplicate-content"}""");
        var same = NewSchema("""{"type":"object","x":"duplicate-content"}""");
        Assert.Equal(schema.Id, same.Id);

        await using (var first = fixture.NewContext())
        {
            first.Schemas.Add(schema);
            await first.SaveChangesAsync();
        }

        await using var second = fixture.NewContext();
        second.Schemas.Add(same);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
        Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal(
            PostgresErrorCodes.UniqueViolation,
            ((PostgresException)ex.InnerException!).SqlState);
    }

    [Fact]
    public async Task TwoSubjectsWithTheSameNameInOneEnvironment_AreRejected()
    {
        var environment = EnvironmentId.New();

        await using (var first = fixture.NewContext())
        {
            first.Subjects.Add(NewSubject("acme.dup.Order", environment));
            await first.SaveChangesAsync();
        }

        await using var second = fixture.NewContext();
        second.Subjects.Add(NewSubject("acme.dup.Order", environment));

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
        Assert.Equal(
            PostgresErrorCodes.UniqueViolation,
            ((PostgresException)ex.InnerException!).SqlState);
    }

    [Fact]
    public async Task TheSameNameInADifferentEnvironment_IsAllowed()
    {
        // The environment is a real isolation boundary, not a naming convention (ADR-012).
        await using var context = fixture.NewContext();
        context.Subjects.Add(NewSubject("acme.shared.Order", EnvironmentId.New()));
        context.Subjects.Add(NewSubject("acme.shared.Order", EnvironmentId.New()));

        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task ReferencesRoundTripAndKeepTheirCanonicalOrder()
    {
        var zeta = Reference.Create(
            "concordat://prod/acme.Zeta/1", SubjectName.Create("acme.Zeta").Value, 1).Value;
        var alpha = Reference.Create(
            "concordat://prod/acme.Alpha/2", SubjectName.Create("acme.Alpha").Value, 2).Value;

        var schema = NewSchema("""{"type":"object","x":"with-refs"}""", zeta, alpha);

        await using (var write = fixture.NewContext())
        {
            write.Schemas.Add(schema);
            await write.SaveChangesAsync();
        }

        await using var read = fixture.NewContext();
        var loaded = await read.Schemas.SingleAsync(s => s.Id == schema.Id);

        Assert.Equal(2, loaded.References.Count);
        Assert.Equal("acme.Alpha", loaded.References[0].Subject.Value);
        Assert.Equal(2, loaded.References[0].Version);
        Assert.Equal("acme.Zeta", loaded.References[1].Subject.Value);
    }

    [Fact]
    public async Task OrdinalsAreDomainAssignedNotDatabaseGenerated()
    {
        // If the column were an identity column, PostgreSQL would allocate the ordinal and the
        // aggregate's contiguous-from-1 invariant, the approval gate and the latest pointer
        // would all be built on a number the domain does not control.
        var environment = EnvironmentId.New();
        var subject = NewSubject("acme.ordinals.Order", environment);
        var verdict = CompatibilityVerdict.Compatible(CompatibilityPolicy.Default);

        for (var i = 1; i <= 3; i++)
        {
            var schema = NewSchema($$"""{"type":"object","v":{{i}}}""");
            await using var ctx = fixture.NewContext();
            ctx.Schemas.Add(schema);
            await ctx.SaveChangesAsync();

            var result = subject.RegisterVersion(
                schema, verdict, null, null, Actor(), DateTimeOffset.UtcNow);
            Assert.True(result.IsSuccess, result.Error?.Message);
        }

        await using (var write = fixture.NewContext())
        {
            write.Subjects.Add(subject);
            await write.SaveChangesAsync();
        }

        await using var read = fixture.NewContext();
        var loaded = await read.Subjects.SingleAsync(s => s.Id == subject.Id);

        Assert.Equal([1, 2, 3], loaded.Versions.Select(v => v.Ordinal));
    }

    [Fact]
    public async Task ConcurrentUpdatesToOneSubject_AreDetected()
    {
        // xmin as the concurrency token. Without it, two registrations racing on one subject
        // would both allocate the same ordinal and one would silently win.
        var subject = NewSubject("acme.concurrency.Order", EnvironmentId.New());

        await using (var seed = fixture.NewContext())
        {
            seed.Subjects.Add(subject);
            await seed.SaveChangesAsync();
        }

        await using var first = fixture.NewContext();
        await using var second = fixture.NewContext();

        var a = await first.Subjects.SingleAsync(s => s.Id == subject.Id);
        var b = await second.Subjects.SingleAsync(s => s.Id == subject.Id);

        a.Deprecate();
        await first.SaveChangesAsync();

        b.Retire();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }
}
