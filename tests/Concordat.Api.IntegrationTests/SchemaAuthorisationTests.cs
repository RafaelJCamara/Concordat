using Concordat.Application.Registry;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;
using Concordat.Formats.Abstractions;
using Concordat.Formats.Json;
using Concordat.Infrastructure.Persistence;

namespace Concordat.Api.IntegrationTests;

/// <summary>
/// Discharges the M1.6 commitment the global schema table created: with no tenant column to
/// filter on, a schema read must be authorised by <em>reachability</em> from a subject in the
/// caller's tenant.
/// </summary>
[Collection(PostgresCollection.Name)]
public class SchemaAuthorisationTests(PostgresFixture fixture)
{
    private static readonly JsonSchemaCanonicalizer Canonicalizer = new();

    private static ActorId Actor() => ActorId.Create("alice").Value;

    private async Task<(SchemaId Id, TenantId Owner)> SeedReachableSchemaAsync(string body)
    {
        var owner = TenantId.New();
        var canonical = Canonicalizer.Canonicalize(body).Value;
        var id = SchemaIdComputer.Compute(SchemaFormat.Json, canonical);
        var schema = Schema.Create(id, SchemaFormat.Json, canonical).Value;

        var subject = Subject.Create(
            EnvironmentId.New(),
            SubjectName.Create("acme.auth.Order").Value,
            SchemaFormat.Json,
            Actor(),
            CompatibilityPolicy.Default,
            DateTimeOffset.UtcNow).Value;

        var registered = subject.RegisterVersion(
            schema,
            CompatibilityVerdict.Compatible(CompatibilityPolicy.Default),
            null,
            null,
            Actor(),
            DateTimeOffset.UtcNow);
        Assert.True(registered.IsSuccess, registered.Error?.Message);

        await using var context = fixture.NewContext(owner);
        context.Schemas.Add(schema);
        context.Subjects.Add(subject);
        await context.SaveChangesAsync();

        return (id, owner);
    }

    private static GetSchemaHandler HandlerFor(ConcordatDbContext context) =>
        new(new SchemaRepository(context));

    [Fact]
    public async Task TheOwningTenantCanReadItsSchema()
    {
        var (id, owner) = await SeedReachableSchemaAsync("""{"type":"object","x":"owned"}""");

        await using var context = fixture.NewContext(owner);
        var result = await HandlerFor(context)
            .HandleAsync(new GetSchemaQuery(id.Value), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(id, result.Value.Id);
    }

    [Fact]
    public async Task AForeignTenantIsRefused()
    {
        // The naive implementation - a lookup by primary key - returns 200 here and leaks any
        // schema to anyone who can guess a 128-bit hash. It also looks entirely correct in
        // review, which is why this test exists.
        var (id, _) = await SeedReachableSchemaAsync("""{"type":"object","x":"not-yours"}""");

        await using var context = fixture.NewContext(TenantId.New());
        var result = await HandlerFor(context)
            .HandleAsync(new GetSchemaQuery(id.Value), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.SchemaNotFound, result.Error!.Code);
    }

    [Fact]
    public async Task AnUnreachableSchemaIsRefusedEvenToTheTenantThatStoredIt()
    {
        // Stored but referenced by nothing. Reachability, not authorship, is the test - there
        // is no "who uploaded it" column, and content-addressed storage means there could not
        // meaningfully be one.
        var tenant = TenantId.New();
        var canonical = Canonicalizer.Canonicalize("""{"type":"object","x":"orphan"}""").Value;
        var id = SchemaIdComputer.Compute(SchemaFormat.Json, canonical);

        await using (var seed = fixture.NewContext(tenant))
        {
            seed.Schemas.Add(Schema.Create(id, SchemaFormat.Json, canonical).Value);
            await seed.SaveChangesAsync();
        }

        await using var context = fixture.NewContext(tenant);
        var result = await HandlerFor(context)
            .HandleAsync(new GetSchemaQuery(id.Value), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.SchemaNotFound, result.Error!.Code);
    }

    [Fact]
    public async Task RefusalIsIndistinguishableFromAbsence()
    {
        // Both must be schema_not_found. A distinct "forbidden" would confirm the existence of
        // another tenant's schema, which is most of what an attacker wants.
        var (existing, _) = await SeedReachableSchemaAsync("""{"type":"object","x":"probe"}""");
        var absent = SchemaId.Create(new string('a', 32)).Value;

        await using var context = fixture.NewContext(TenantId.New());
        var handler = HandlerFor(context);

        var forbidden = await handler.HandleAsync(
            new GetSchemaQuery(existing.Value), CancellationToken.None);
        var missing = await handler.HandleAsync(
            new GetSchemaQuery(absent.Value), CancellationToken.None);

        Assert.Equal(missing.Error!.Code, forbidden.Error!.Code);
    }
}
