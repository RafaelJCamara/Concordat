using Concordat.Application.Registry;
using Concordat.Application.Tests.TestSupport;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;

namespace Concordat.Application.Tests;

/// <summary>The plain reads: subjects, schemas, and who is using a schema.</summary>
/// <remarks>
/// <c>SchemaAuthorisationTests</c> already proves the reachability gate against real
/// PostgreSQL. What is left for a unit test is the ordering — that the gate runs
/// <em>before</em> the read, so an unauthorised caller's id never reaches a lookup.
/// </remarks>
public class ReadHandlerTests
{
    private const string Name = "acme.orders.OrderCreated";

    private readonly EnvironmentId _environment = EnvironmentId.New();
    private readonly FakeSubjects _subjects = new();
    private readonly FakeSchemas _schemas = new();

    private Task<Result<Subject>> GetSubjectAsync(string name = Name, EnvironmentId? environment = null) =>
        new GetSubjectHandler(_subjects).HandleAsync(
            new GetSubjectQuery(environment ?? _environment, name), CancellationToken.None);

    private Task<Result<IReadOnlyList<Subject>>> ListAsync(EnvironmentId? environment = null) =>
        new ListSubjectsHandler(_subjects).HandleAsync(
            new ListSubjectsQuery(environment ?? _environment), CancellationToken.None);

    private Task<Result<Schema>> GetSchemaAsync(string id) =>
        new GetSchemaHandler(_schemas).HandleAsync(
            new GetSchemaQuery(id), CancellationToken.None);

    private Task<Result<IReadOnlyList<SchemaUsage>>> UsagesAsync(string id) =>
        new GetSchemaUsagesHandler(_schemas).HandleAsync(
            new GetSchemaUsagesQuery(id), CancellationToken.None);

    // ------------------------------------------------------------- get subject

    [Fact]
    public async Task AnInvalidSubjectName_IsRefusedBeforeTheRepositoryIsTouched()
    {
        var result = await GetSubjectAsync("acme.orders.");

        Assert.Equal(ConcordatCodes.SubjectNameInvalid, result.Error!.Code);
        Assert.Equal(0, _subjects.Finds);
    }

    [Fact]
    public async Task AnUnknownSubject_IsSubjectNotFound()
    {
        var result = await GetSubjectAsync();

        Assert.Equal(ConcordatCodes.SubjectNotFound, result.Error!.Code);
    }

    [Fact]
    public async Task ASubjectInAnotherEnvironment_IsNotVisible()
    {
        // Environments scope every read (ADR-012), so the same name in staging is a different
        // subject rather than the same one seen from elsewhere.
        _subjects.Seed(Build.Subject(EnvironmentId.New()));

        var result = await GetSubjectAsync();

        Assert.Equal(ConcordatCodes.SubjectNotFound, result.Error!.Code);
    }

    [Fact]
    public async Task ASubjectNameIsMatchedAfterTrimmingJustAsItWasStored()
    {
        // SubjectName trims on the way in, so a name that arrives with whitespace from a URL or
        // a config file still resolves rather than looking absent.
        _subjects.Seed(Build.Subject(_environment));

        var result = await GetSubjectAsync($"  {Name}  ");

        Assert.True(result.IsSuccess, result.Error?.Message);
    }

    // ----------------------------------------------------------- list subjects

    [Fact]
    public async Task AnEmptyEnvironment_ListsNothingRatherThanFailing()
    {
        // A fresh environment is a legitimate state. Reporting it as not-found would make a
        // correctly provisioned environment look broken on its first request.
        var result = await ListAsync();

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Empty(result.Value);
    }

    [Fact]
    public async Task ListingIsScopedToTheEnvironmentAsked()
    {
        _subjects.Seed(Build.Subject(_environment, "acme.a.One"));
        _subjects.Seed(Build.Subject(EnvironmentId.New(), "acme.b.Two"));

        var result = await ListAsync();

        Assert.Equal("acme.a.One", Assert.Single(result.Value).Name.Value);
    }

    // -------------------------------------------------------------- get schema

    [Fact]
    public async Task AMalformedSchemaId_IsRefusedBeforeTheStoreIsTouched()
    {
        var result = await GetSchemaAsync("not-a-schema-id");

        Assert.Equal(ConcordatCodes.SchemaIdMalformed, result.Error!.Code);
        Assert.Equal(0, _schemas.Finds);
    }

    [Fact]
    public async Task AnUppercaseSchemaId_IsMalformedRatherThanNormalised()
    {
        // Two spellings of one id would defeat content addressing: the same schema would be
        // cacheable under two keys and comparable as unequal.
        var stored = Build.JsonSchema("""{"type":"object"}""");
        _schemas.Seed(stored);

        var result = await GetSchemaAsync(stored.Id.Value.ToUpperInvariant());

        Assert.Equal(ConcordatCodes.SchemaIdMalformed, result.Error!.Code);
    }

    [Fact]
    public async Task TheReachabilityGateIsConsultedBeforeTheSchemaIsRead()
    {
        // The gate is the only authorisation the global schema table has — there is no tenant
        // column to filter on. Reading first and gating afterwards would work today and would
        // leak the moment someone adds logging, caching or an early return to the read path.
        var hidden = Build.JsonSchema("""{"type":"object","x":"someone-elses"}""");
        _schemas.Seed(hidden).Hide(hidden.Id);

        var result = await GetSchemaAsync(hidden.Id.Value);

        Assert.Equal(ConcordatCodes.SchemaNotFound, result.Error!.Code);
        Assert.Equal(0, _schemas.Finds);
    }

    [Fact]
    public async Task AReachableSchemaIsReturnedWithItsBodyAndReferences()
    {
        var stored = Build.Referring("concordat://prod/acme.Address/2");
        _schemas.Seed(stored);

        var result = await GetSchemaAsync(stored.Id.Value);

        Assert.Equal(stored.Body, result.Value.Body);
        Assert.Equal("acme.Address", Assert.Single(result.Value.References).Subject.Value);
    }

    // ------------------------------------------------------------------ usages

    [Fact]
    public async Task AMalformedSchemaId_IsRefusedByTheUsagesQueryToo()
    {
        var result = await UsagesAsync(new string('z', 32));

        Assert.Equal(ConcordatCodes.SchemaIdMalformed, result.Error!.Code);
    }

    [Fact]
    public async Task ASchemaNobodyUses_AnswersWithAnEmptyListRatherThanNotFound()
    {
        // "Who depends on this" has a correct answer of "nobody", and it is the answer a caller
        // needs before retiring something. A 404 would read as "the schema is gone".
        var result = await UsagesAsync(new string('a', 32));

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Empty(result.Value);
    }

    [Fact]
    public async Task UsagesCarryTheSubjectNameAndOrdinalOfEveryPlaceTheSchemaIsBound()
    {
        var schema = Build.JsonSchema("""{"type":"object"}""");
        _schemas.Seed(schema)
            .Used(schema.Id, Build.Name("acme.a.One"), 3)
            .Used(schema.Id, Build.Name("acme.b.Two"), 1);

        var result = await UsagesAsync(schema.Id.Value);

        Assert.Equal(
            [("acme.a.One", 3), ("acme.b.Two", 1)],
            result.Value.Select(u => (u.Subject, u.Version)));
    }
}
