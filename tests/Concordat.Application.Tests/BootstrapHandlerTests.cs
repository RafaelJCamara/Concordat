using Concordat.Application.Registry;
using Concordat.Application.Tests.TestSupport;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;

namespace Concordat.Application.Tests;

/// <summary>
/// Cold start is the load pattern this endpoint exists for: a fleet-wide restart empties every
/// cache at once and stampedes a registry that sits on the deserialise path. One request has to
/// answer everything, so what it leaves out matters as much as what it includes.
/// </summary>
public class BootstrapHandlerTests
{
    private const string Simple = """{"type":"object","properties":{"id":{"type":"string"}}}""";

    private const string SimpleRequired =
        """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}""";

    private readonly FakeSubjects _subjects = new();
    private readonly FakeSchemas _schemas = new();
    private readonly FakeEnvironments _environments = new();

    private EnvironmentId Prod => _environments.Resolve("prod");

    private Task<Result<BootstrapResult>> BootstrapAsync(EnvironmentId? environment = null) =>
        new BootstrapHandler(_subjects, _schemas, _environments).HandleAsync(
            new BootstrapQuery(environment ?? Prod), CancellationToken.None);

    /// <summary>Seeds a subject with no versions.</summary>
    private Subject Empty(string name, string environment = "prod")
    {
        var subject = Build.Subject(_environments.Resolve(environment), name);
        _subjects.Seed(subject);

        return subject;
    }

    /// <summary>Seeds a subject whose version 1 is the given schema.</summary>
    private Subject Publish(string name, Schema schema, string environment = "prod")
    {
        var subject = Build.Subject(_environments.Resolve(environment), name);

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

        return subject;
    }

    [Fact]
    public async Task AnEmptyEnvironment_AnswersWithAnEmptyPayloadRatherThanFailing()
    {
        var result = await BootstrapAsync();

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Empty(result.Value.Subjects);
        Assert.Empty(result.Value.Schemas);
    }

    [Fact]
    public async Task ARetiredSubjectIsExcludedAndItsSchemaIsNotCollectedEither()
    {
        // A client warming a cache should not be primed with soft-deleted contracts, and
        // shipping the schema anyway would leave the exclusion cosmetic.
        var schema = Build.JsonSchema(Simple);
        var subject = Publish("acme.orders.Old", schema);
        Assert.True(subject.Retire().IsSuccess);

        var result = await BootstrapAsync();

        Assert.Empty(result.Value.Subjects);
        Assert.Empty(result.Value.Schemas);
    }

    [Fact]
    public async Task ADeprecatedSubjectIsStillIncluded()
    {
        // Deprecation is advisory. Consumers of a deprecated contract are exactly the ones who
        // have not migrated yet, so dropping it from the payload would break them first.
        var subject = Publish("acme.orders.Legacy", Build.JsonSchema(Simple));
        Assert.True(subject.Deprecate().IsSuccess);

        var result = await BootstrapAsync();

        Assert.Single(result.Value.Subjects);
    }

    [Fact]
    public async Task ASubjectWithNoVersionsIsListedWithNoPointer()
    {
        // Created but never registered against. Omitting it would tell a client the subject
        // does not exist, which is a different thing from having nothing to serialise with.
        Empty("acme.orders.Planned");

        var result = await BootstrapAsync();

        var listed = Assert.Single(result.Value.Subjects);
        Assert.Equal("acme.orders.Planned", listed.Name);
        Assert.Null(listed.LatestOrdinal);
        Assert.Null(listed.LatestSchemaId);
        Assert.Null(listed.LatestSemver);
    }

    [Fact]
    public async Task AVersionAwaitingApprovalIsNotWhatClientsAreWarmedWith()
    {
        // The pointer is gated, not "whichever ordinal is highest" (ADR-017). Priming caches
        // with an unapproved breaking proposal would let a third party change what every
        // producer serialises with, at runtime, with no deploy — the failure Confluent's
        // mutable latest has.
        var subject = Publish("acme.orders.OrderCreated", Build.JsonSchema(Simple));
        var pending = subject.RegisterVersion(
            Build.JsonSchema(SimpleRequired),
            CompatibilityVerdict.Breaking(CompatibilityPolicy.Default),
            null,
            null,
            Build.Actor(),
            Build.At);
        Assert.True(pending.IsSuccess, pending.Error?.Message);
        _schemas.Seed(Build.JsonSchema(SimpleRequired));

        var result = await BootstrapAsync();

        var listed = Assert.Single(result.Value.Subjects);
        Assert.Equal(1, listed.LatestOrdinal);
        Assert.Equal(Build.JsonSchema(Simple).Id.Value, listed.LatestSchemaId);
        Assert.Single(result.Value.Schemas);
    }

    [Fact]
    public async Task TheSemanticVersionLabelOnThePointedAtVersionIsCarried()
    {
        var subject = Build.Subject(Prod, "acme.orders.OrderCreated");
        subject.Register(_schemas, Simple, semver: "2.5.1");
        _subjects.Seed(subject);

        var result = await BootstrapAsync();

        Assert.Equal("2.5.1", Assert.Single(result.Value.Subjects).LatestSemver);
    }

    [Fact]
    public async Task ReferencedSchemasAreIncludedTransitively()
    {
        // The payload has to be self-sufficient. A client that has to follow a reference with a
        // second call it did not plan for is back in the stampede this endpoint prevents.
        var city = Build.JsonSchema("""{"type":"string","x":"city"}""");
        Publish("acme.City", city);

        var address = Build.Referring("concordat://prod/acme.City/1");
        Publish("acme.Address", address);

        var order = Build.Referring("concordat://prod/acme.Address/1");
        Publish("acme.orders.OrderCreated", order);

        var result = await BootstrapAsync();

        // City is reached only through Address, which is reached only through Order.
        Assert.Contains(city.Id.Value, result.Value.Schemas.Keys, StringComparer.Ordinal);
        Assert.Equal(3, result.Value.Schemas.Count);
    }

    [Fact]
    public async Task ASchemaTwoSubjectsShareAppearsOnce()
    {
        // Deduplication is why the payload is keyed by id rather than nested under each
        // subject: a shared envelope schema across fifty subjects would otherwise be sent
        // fifty times, on the request that is already the largest one a client makes.
        var shared = Build.JsonSchema(Simple);
        Publish("acme.a.One", shared);
        Publish("acme.b.Two", shared);

        var result = await BootstrapAsync();

        Assert.Equal(2, result.Value.Subjects.Count);
        Assert.Single(result.Value.Schemas);
    }

    [Fact]
    public async Task AnUnresolvableReferenceIsSkippedRatherThanFailingTheWholePayload()
    {
        // Documented rather than endorsed: the payload silently stops being self-sufficient,
        // and the client falls back to fetching that one schema itself. The alternative is
        // worse — one dangling edge would take out cache warm-up for every subject in the
        // environment at once.
        var order = Build.Referring("concordat://prod/acme.Gone/1");
        Publish("acme.orders.OrderCreated", order);

        var result = await BootstrapAsync();

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Single(result.Value.Subjects);
        Assert.Single(result.Value.Schemas);
    }

    [Fact]
    public async Task ACyclicReferenceGraphTerminates()
    {
        // The visited set is what stops it, and the cycle is only reachable through a schema
        // outside the listed environment — so the queue, not the subject list, is what has to
        // hold the line. This test fails by never finishing.
        var external = Build.Referring("concordat://prod/acme.orders.OrderCreated/1");
        Publish("acme.Echo", external, environment: "staging");

        var order = Build.Referring("concordat://staging/acme.Echo/1");
        Publish("acme.orders.OrderCreated", order);

        var result = await BootstrapAsync();

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(2, result.Value.Schemas.Count);
    }

    [Fact]
    public async Task ASubjectWhosePointedAtSchemaIsMissingIsStillListed()
    {
        // The pointer is reported from the aggregate and the body from the store, so they can
        // disagree. Listing the subject with an id the payload does not carry is the honest
        // answer; dropping the subject would look like it had been deleted.
        var subject = Build.Subject(Prod, "acme.orders.OrderCreated");
        var registered = subject.RegisterVersion(
            Build.JsonSchema(Simple),
            CompatibilityVerdict.Compatible(CompatibilityPolicy.Default),
            null,
            null,
            Build.Actor(),
            Build.At);
        Assert.True(registered.IsSuccess, registered.Error?.Message);
        _subjects.Seed(subject);

        var result = await BootstrapAsync();

        Assert.Equal(1, Assert.Single(result.Value.Subjects).LatestOrdinal);
        Assert.Empty(result.Value.Schemas);
    }

    [Fact]
    public async Task SubjectsFromAnotherEnvironmentAreNotListed()
    {
        Publish("acme.orders.OrderCreated", Build.JsonSchema(Simple), environment: "staging");

        var result = await BootstrapAsync();

        Assert.Empty(result.Value.Subjects);
    }

    [Fact]
    public async Task LatestSchemasAreLoadedInOneBatchRegardlessOfSubjectCount()
    {
        // The doc comment on this handler promises "one request instead of N" to the client.
        // That promise is broken from the inside if answering it costs one schema lookup per
        // subject — this is the regression test for that specific N+1 (Q1).
        for (var i = 0; i < 5; i++)
        {
            Publish($"acme.orders.Order{i}", Build.JsonSchema($$"""{"type":"object","x":{{i}}}"""));
        }

        var result = await BootstrapAsync();

        Assert.Equal(5, result.Value.Subjects.Count);
        Assert.Equal(1, _schemas.BatchFinds);
        Assert.Equal(0, _schemas.Finds);
    }
}
