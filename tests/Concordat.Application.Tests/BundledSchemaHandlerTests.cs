using Concordat.Application.Registry;
using Concordat.Application.Tests.TestSupport;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;

namespace Concordat.Application.Tests;

/// <summary>
/// Bundling walks the reference graph on a read path, so its failure modes are resolution
/// failures and its hazard is a graph that never terminates.
/// </summary>
public class BundledSchemaHandlerTests
{
    private readonly FakeSubjects _subjects = new();
    private readonly FakeSchemas _schemas = new();
    private readonly FakeEnvironments _environments = new();

    private Task<Result<BundledSchema>> BundleAsync(string id) =>
        new GetBundledSchemaHandler(_schemas, _subjects, _environments, new JsonBundlers())
            .HandleAsync(new GetBundledSchemaQuery(id), CancellationToken.None);

    /// <summary>Stores a schema and binds it to version 1 of a subject.</summary>
    private Schema Publish(string subjectName, Schema schema, string environment = "prod")
    {
        var subject = Build.Subject(_environments.Resolve(environment), subjectName);

        var registered = subject.RegisterVersion(
            schema,
            CompatibilityVerdict.Compatible(CompatibilityPolicy.Default),
            null,
            null,
            Build.Actor(),
            Build.At);
        Assert.True(registered.IsSuccess, registered.Error?.Message);

        _subjects.Seed(subject);
        _schemas.Seed(schema);

        return schema;
    }

    [Fact]
    public async Task AMalformedSchemaId_IsRefusedBeforeTheStoreIsTouched()
    {
        var result = await BundleAsync("0123");

        Assert.Equal(ConcordatCodes.SchemaIdMalformed, result.Error!.Code);
        Assert.Equal(0, _schemas.Finds);
    }

    [Fact]
    public async Task AnUnreachableSchema_IsNotFoundSoBundlingIsNotAWayRoundTheGate()
    {
        // Two read endpoints serve the same bytes. Gating only the plain one would leave the
        // bundle as an unauthenticated mirror of every schema in the installation.
        var hidden = Build.JsonSchema("""{"type":"object","x":"someone-elses"}""");
        _schemas.Seed(hidden).Hide(hidden.Id);

        var result = await BundleAsync(hidden.Id.Value);

        Assert.Equal(ConcordatCodes.SchemaNotFound, result.Error!.Code);
        Assert.Equal(0, _schemas.Finds);
    }

    [Fact]
    public async Task AnAbsentSchema_IsNotFound()
    {
        var result = await BundleAsync(new string('b', 32));

        Assert.Equal(ConcordatCodes.SchemaNotFound, result.Error!.Code);
    }

    [Fact]
    public async Task AReferenceToASubjectThisTenantCannotSee_IsSubjectNotFound()
    {
        // Resolution runs through the subject repository, which the tenant filter protects. A
        // reference is not a way to reach across tenants, and the message says so rather than
        // reporting a corrupt schema.
        var root = Build.Referring("concordat://prod/acme.Missing/1");
        _schemas.Seed(root);

        var result = await BundleAsync(root.Id.Value);

        Assert.Equal(ConcordatCodes.SubjectNotFound, result.Error!.Code);
        Assert.Contains("acme.Missing", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReferenceToAVersionThatDoesNotExist_IsVersionNotFound()
    {
        // References pin an ordinal on purpose. Falling back to whatever version happens to
        // exist would make the bundle depend on when it was requested.
        Publish("acme.Address", Build.JsonSchema("""{"type":"object","x":"address"}"""));
        var root = Build.Referring("concordat://prod/acme.Address/9");
        _schemas.Seed(root);

        var result = await BundleAsync(root.Id.Value);

        Assert.Equal(ConcordatCodes.VersionNotFound, result.Error!.Code);
    }

    [Fact]
    public async Task AReferenceResolvingToAnUnstoredSchema_IsSchemaNotFound()
    {
        var target = Build.JsonSchema("""{"type":"object","x":"address"}""");
        var subject = Build.Subject(_environments.Resolve("prod"), "acme.Address");
        var registered = subject.RegisterVersion(
            target,
            CompatibilityVerdict.Compatible(CompatibilityPolicy.Default),
            null,
            null,
            Build.Actor(),
            Build.At);
        Assert.True(registered.IsSuccess, registered.Error?.Message);
        _subjects.Seed(subject);

        var root = Build.Referring("concordat://prod/acme.Address/1");
        _schemas.Seed(root);

        var result = await BundleAsync(root.Id.Value);

        Assert.Equal(ConcordatCodes.SchemaNotFound, result.Error!.Code);
    }

    [Fact]
    public async Task ReferencesAreResolvedTransitivelySoTheBundleValidatesStandalone()
    {
        // A bundle that inlined only the direct references would still need a second fetch,
        // which is the whole thing bundling exists to remove.
        Publish("acme.City", Build.JsonSchema("""{"type":"string","x":"city"}"""));
        Publish("acme.Address", Build.Referring("concordat://prod/acme.City/1"));

        var root = Build.Referring("concordat://prod/acme.Address/1");
        _schemas.Seed(root);

        var result = await BundleAsync(root.Id.Value);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(
            ["concordat://prod/acme.Address/1", "concordat://prod/acme.City/1"],
            result.Value.Inlined);
        Assert.DoesNotContain("concordat://", result.Value.Bundled, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACyclicReferenceGraphTerminates()
    {
        // Cycles are rejected at registration, but a read path that trusts that assumption is
        // one bug away from hanging the request thread. This test fails by never finishing.
        var address = Build.Referring("concordat://prod/acme.Order/1");
        Publish("acme.Address", address);

        var order = Build.Referring("concordat://prod/acme.Address/1");
        Publish("acme.Order", order);

        var result = await BundleAsync(order.Id.Value);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(
            ["concordat://prod/acme.Address/1", "concordat://prod/acme.Order/1"],
            result.Value.Inlined);
    }

    [Fact]
    public async Task TheInlinedListIsOrderedRatherThanInDiscoveryOrder()
    {
        // It is part of the response body. Leaving it in whatever order the queue happened to
        // produce would make two identical bundles compare unequal.
        Publish("acme.Zeta", Build.JsonSchema("""{"type":"string","x":"z"}"""));
        Publish("acme.Alpha", Build.JsonSchema("""{"type":"string","x":"a"}"""));

        var root = Build.Referring(
            "concordat://prod/acme.Zeta/1", "concordat://prod/acme.Alpha/1");
        _schemas.Seed(root);

        var result = await BundleAsync(root.Id.Value);

        Assert.Equal(
            ["concordat://prod/acme.Alpha/1", "concordat://prod/acme.Zeta/1"],
            result.Value.Inlined);
    }

    [Fact]
    public async Task ASchemaWithNoReferences_BundlesToItselfWithNothingInlined()
    {
        var root = Build.JsonSchema("""{"type":"object","properties":{"id":{"type":"string"}}}""");
        _schemas.Seed(root);

        var result = await BundleAsync(root.Id.Value);

        Assert.Empty(result.Value.Inlined);
        Assert.Equal(root.Body, result.Value.Bundled);
        Assert.Equal(SchemaFormat.Json, result.Value.Format);
    }
}
