using Concordat.Domain.Registry;
using Concordat.Formats.Abstractions;
using Concordat.Formats.Json;
using Microsoft.EntityFrameworkCore;

namespace Concordat.Api.IntegrationTests;

/// <summary>
/// Tenant isolation, wired from M1.5 with a single implicit tenant so that M9 is a
/// configuration swap rather than a data migration plus an audit of every query.
/// </summary>
[Collection(PostgresCollection.Name)]
public class TenantIsolationTests(PostgresFixture fixture)
{
    private static readonly JsonSchemaCanonicalizer Canonicalizer = new();

    private static Subject NewSubject(string name) =>
        Subject.Create(
            EnvironmentId.New(),
            SubjectName.Create(name).Value,
            SchemaFormat.Json,
            ActorId.Create("alice").Value,
            CompatibilityPolicy.Default,
            DateTimeOffset.UtcNow).Value;

    [Fact]
    public async Task ASubjectIsInvisibleToAnotherTenant()
    {
        var mine = TenantId.New();
        var theirs = TenantId.New();
        var subject = NewSubject("acme.isolation.Order");

        await using (var write = fixture.NewContext(mine))
        {
            write.Subjects.Add(subject);
            await write.SaveChangesAsync();
        }

        await using var asOwner = fixture.NewContext(mine);
        Assert.NotNull(await asOwner.Subjects.SingleOrDefaultAsync(s => s.Id == subject.Id));

        // Not "throws" — invisible. A filtered query returns nothing, which is the correct
        // shape: another tenant's subject must not even be known to exist.
        await using var asStranger = fixture.NewContext(theirs);
        Assert.Null(await asStranger.Subjects.SingleOrDefaultAsync(s => s.Id == subject.Id));
    }

    [Fact]
    public async Task TheTenantIsStampedOnWriteWithoutTheCallerSupplyingIt()
    {
        // Paired with the query filter: the filter stops you reading another tenant's rows,
        // stamping stops you writing a row with no tenant, which the filter would then hide
        // from everyone including its author.
        var tenant = TenantId.New();
        var subject = NewSubject("acme.stamp.Order");

        await using (var write = fixture.NewContext(tenant))
        {
            write.Subjects.Add(subject);
            await write.SaveChangesAsync();
        }

        await using var read = fixture.NewContext(tenant);
        var loaded = await read.Subjects.SingleAsync(s => s.Id == subject.Id);

        Assert.Equal(tenant.Value, read.Entry(loaded).Property<Guid>("TenantId").CurrentValue);
    }

    [Fact]
    public async Task SchemasAreGlobalAndSharedAcrossTenants()
    {
        // ADR-015 at the storage layer: "same content implies same id everywhere" is only
        // literally true if the same content is also ONE ROW everywhere. Schema deliberately
        // carries no tenant and no query filter.
        var canonical = Canonicalizer.Canonicalize("""{"type":"object","x":"shared"}""").Value;
        var id = SchemaIdComputer.Compute(SchemaFormat.Json, canonical);
        var schema = Schema.Create(id, SchemaFormat.Json, canonical).Value;

        await using (var write = fixture.NewContext(TenantId.New()))
        {
            write.Schemas.Add(schema);
            await write.SaveChangesAsync();
        }

        await using var other = fixture.NewContext(TenantId.New());
        Assert.NotNull(await other.Schemas.SingleOrDefaultAsync(s => s.Id == id));
    }

    [Fact]
    public async Task GlobalSchemasAreWhyReadsMustBeAuthorisedByReachability()
    {
        // Documents the M1.6 obligation as an executable statement rather than a note: with
        // no tenant column there is nothing to filter on, so GET /schemas/{id} must check
        // that some subject in the caller's tenant references the schema. The naive
        // implementation leaks any schema to anyone who can guess a 128-bit hash.
        var canonical = Canonicalizer.Canonicalize("""{"type":"object","x":"leaky"}""").Value;
        var id = SchemaIdComputer.Compute(SchemaFormat.Json, canonical);

        await using (var write = fixture.NewContext(TenantId.New()))
        {
            write.Schemas.Add(Schema.Create(id, SchemaFormat.Json, canonical).Value);
            await write.SaveChangesAsync();
        }

        await using var stranger = fixture.NewContext(TenantId.New());

        // Reachable at the data layer by design. M1.6 must add the authorisation the storage
        // model deliberately does not provide.
        Assert.NotNull(await stranger.Schemas.SingleOrDefaultAsync(s => s.Id == id));
    }
}
