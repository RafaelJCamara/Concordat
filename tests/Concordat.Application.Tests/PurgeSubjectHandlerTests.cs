using Concordat.Application.Abstractions;
using Concordat.Application.Registry;
using Concordat.Application.Tests.TestSupport;
using Concordat.Domain.Contracts;
using Concordat.Domain.Governance;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;
using Microsoft.Extensions.Time.Testing;

namespace Concordat.Application.Tests;

/// <summary>
/// Deleting a retired subject for good, and everything that must stop it (ADR-046).
/// </summary>
public class PurgeSubjectHandlerTests
{
    private const string Name = "acme.orders.OrderCreated";
    private const string V1 = """{"type":"object","properties":{"id":{"type":"string"}}}""";

    private readonly FakeEnvironments _resolver = new();
    private readonly EnvironmentId _environment;
    private readonly FakeSubjects _subjects = new();
    private readonly FakeSchemas _schemas = new();
    private readonly FakeContracts _contracts;
    private readonly FakeServices _services;
    private readonly SettableCaller _caller = new();
    private readonly RecordingUnitOfWork _unitOfWork = new();
    private readonly RecordingAuditLog _audit = new();
    private readonly FakeTimeProvider _clock = new(Build.At);

    public PurgeSubjectHandlerTests()
    {
        _environment = _resolver.Resolve("dev");
        _contracts = new FakeContracts { EnvironmentIdFor = _environment };
        _services = new FakeServices { EnvironmentIdFor = _environment };
    }

    private Task<Result<PurgedSubject>> PurgeAsync(string name = Name, bool force = true) =>
        new PurgeSubjectHandler(_subjects, _contracts, _services, _resolver, _caller, _audit, _unitOfWork, _clock)
            .HandleAsync(new PurgeSubjectCommand(_environment, name, force), CancellationToken.None);

    private Subject SeedRetired(string name = Name, int versions = 1)
    {
        var subject = Build.Subject(_environment, name);

        for (var i = 0; i < versions; i++)
        {
            subject.Register(_schemas, "{\"type\":\"object\",\"properties\":{\"f" + i + "\":{\"type\":\"string\"}}}");
        }

        Assert.True(subject.Retire().IsSuccess);
        _subjects.Seed(subject);

        return subject;
    }

    private static SubjectRef Ref(string subject) =>
        new(SubjectName.Create(subject).Value, VersionSelector.Latest);

    // --------------------------------------------------------------------------- the happy path

    [Fact]
    public async Task ARetiredSubjectNothingUses_IsRemovedWithItsVersions_AndSaidSo()
    {
        var subject = SeedRetired(versions: 3);

        var result = await PurgeAsync();

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(new PurgedSubject(Name, 3), result.Value);
        Assert.Same(subject, Assert.Single(_subjects.Removed));
        Assert.Equal(1, _unitOfWork.Saves);
    }

    [Fact]
    public async Task TheDeletionIsAuditedAsTheCaller_NamingHowManyVersionsWent()
    {
        _caller.Current = _caller.Current with { Actor = ActorId.Create("carol").Value };
        SeedRetired(versions: 2);

        await PurgeAsync();

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal(AuditAction.SubjectPurged, entry.Action);
        Assert.Equal("carol", entry.Actor.Value);
        Assert.Equal(Name, entry.Target);
        Assert.Equal(_environment, entry.EnvironmentId);
        Assert.Equal("2 versions removed", entry.Detail);
    }

    [Fact]
    public async Task OneVersionIsNotPluralised()
    {
        SeedRetired(versions: 1);

        await PurgeAsync();

        Assert.Equal("1 version removed", Assert.Single(_audit.Entries).Detail);
    }

    [Fact]
    public async Task AContractOrAServiceForAnotherSubjectDoesNotStopIt()
    {
        SeedRetired();
        _contracts.Bind("orders-contract", Ref("acme.billing.InvoiceRaised"));
        _services.Declare("billing", consumes: [Ref("acme.billing.InvoiceRaised")], produces: []);

        var result = await PurgeAsync();

        Assert.True(result.IsSuccess, result.Error?.Message);
    }

    // ---------------------------------------------------------------------------- the refusals

    [Fact]
    public async Task WithoutForce_NothingIsReadAndNothingIsRemoved()
    {
        SeedRetired();

        var result = await PurgeAsync(force: false);

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.PurgeRequiresForce, result.Error!.Code);

        // Before the subject is even looked up: a request that did not say it means it learns nothing
        // about whether the subject exists.
        Assert.Equal(0, _subjects.Finds);
        Assert.Empty(_subjects.Removed);
        Assert.Empty(_audit.Entries);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task ASubjectThatDoesNotExist_IsNotFound()
    {
        var result = await PurgeAsync();

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.SubjectNotFound, result.Error!.Code);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task AnotherEnvironmentsSubjectIsNotFound()
    {
        SeedRetired();
        var other = EnvironmentId.New();

        var result = await new PurgeSubjectHandler(
                _subjects, _contracts, _services, _resolver, _caller, _audit, _unitOfWork, _clock)
            .HandleAsync(new PurgeSubjectCommand(other, Name, true), CancellationToken.None);

        Assert.Equal(ConcordatCodes.SubjectNotFound, result.Error!.Code);
        Assert.Empty(_subjects.Removed);
    }

    [Fact]
    public async Task ASubjectThatIsNotRetired_IsRefusedAndKept()
    {
        var subject = Build.Subject(_environment);
        subject.Register(_schemas, V1);
        _subjects.Seed(subject);

        var result = await PurgeAsync();

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.SubjectNotRetired, result.Error!.Code);
        Assert.Empty(_subjects.Removed);
        Assert.Empty(_audit.Entries);
        Assert.Equal(0, _unitOfWork.Saves);
        // The contract and reference checks cost reads: not spent on a subject that was refused for its state.
        Assert.Equal(0, _contracts.Lists);
        Assert.Equal(0, _subjects.ReferrerLookups);
    }

    [Fact]
    public async Task AContractThatBindsItForPublishing_StopsItAndIsNamed()
    {
        SeedRetired();
        _contracts.Bind("orders-contract", Ref(Name));

        var result = await PurgeAsync();

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.SubjectInUse, result.Error!.Code);
        Assert.Contains("contract 'orders-contract'", result.Error.Message, StringComparison.Ordinal);
        Assert.Empty(_subjects.Removed);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task AContractThatConsumesIt_StopsItToo()
    {
        SeedRetired();
        _contracts.Consume("billing-contract", Ref(Name));

        var result = await PurgeAsync();

        Assert.Equal(ConcordatCodes.SubjectInUse, result.Error!.Code);
        Assert.Contains("contract 'billing-contract'", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AServiceThatConsumesIt_StopsItAndIsNamed()
    {
        SeedRetired();
        _services.Declare("billing", consumes: [Ref(Name)], produces: []);

        var result = await PurgeAsync();

        Assert.Equal(ConcordatCodes.SubjectInUse, result.Error!.Code);
        Assert.Contains("service 'billing'", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AServiceThatProducesIt_StopsItToo()
    {
        SeedRetired();
        _services.Declare("orders", consumes: [], produces: [Ref(Name)]);

        var result = await PurgeAsync();

        Assert.Equal(ConcordatCodes.SubjectInUse, result.Error!.Code);
        Assert.Contains("service 'orders'", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManyUsers_AreListedUpToTheLimitAndThenCounted()
    {
        SeedRetired();

        for (var i = 1; i <= 8; i++)
        {
            _contracts.Bind($"contract-{i}", Ref(Name));
        }

        var result = await PurgeAsync();

        Assert.Equal(ConcordatCodes.SubjectInUse, result.Error!.Code);
        Assert.Contains("contract 'contract-5'", result.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("contract-6", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("and 3 more", result.Error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(5, "contract-5", false)]
    [InlineData(6, "contract-5", true)]
    public async Task TheLimitIsExact_FiveAreAllListed_SixIsFiveAndOneMore(int users, string last, bool more)
    {
        SeedRetired();

        for (var i = 1; i <= users; i++)
        {
            _contracts.Bind($"contract-{i}", Ref(Name));
        }

        var result = await PurgeAsync();

        Assert.Contains($"contract '{last}'", result.Error!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("contract-6", result.Error.Message, StringComparison.Ordinal);
        Assert.Equal(more, result.Error.Message.Contains(" and 1 more", StringComparison.Ordinal));
    }
    [Fact]
    public async Task AContractInAnotherEnvironment_DoesNotStopIt()
    {
        SeedRetired();
        _contracts.Bind("elsewhere", Ref(Name), environment: EnvironmentId.New());

        var result = await PurgeAsync();

        Assert.True(result.IsSuccess, result.Error?.Message);
    }

    private static string Uri(string environment, string subject, int version) =>
        $"concordat://{environment}/{subject}/{version}";

    [Fact]
    public async Task ASchemaOfAnotherSubjectThatReferencesIt_StopsItAndNamesTheVersion()
    {
        SeedRetired();
        SeedRetired("acme.billing.Invoice");
        _subjects.Refers("acme.billing.Invoice", _environment, 1, Uri("dev", Name, 1));

        var result = await PurgeAsync();

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.SubjectReferenced, result.Error!.Code);
        Assert.Contains("acme.billing.Invoice@1", result.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("another environment", result.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_subjects.Removed, s => s.Name.Value == Name);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task AVersionInAnotherEnvironmentThatReferencesThisOne_StopsItAndSaysItIsElsewhere()
    {
        // A reference carries an environment, so staging can hold a reference to a subject in dev.
        SeedRetired();
        _subjects.Refers("acme.billing.Invoice", _resolver.Resolve("staging"), 2, Uri("dev", Name, 1));

        var result = await PurgeAsync();

        Assert.Equal(ConcordatCodes.SubjectReferenced, result.Error!.Code);
        Assert.Contains(
            "acme.billing.Invoice@2 (in another environment)", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReferenceToTheSameNameInAnotherEnvironment_IsNotAReferenceToThisSubject()
    {
        SeedRetired();
        // Naming `acme.orders.OrderCreated` in staging is naming staging's subject, not dev's.
        _subjects.Refers("acme.billing.Invoice", _environment, 1, Uri("staging", Name, 1));

        var result = await PurgeAsync();

        Assert.True(result.IsSuccess, result.Error?.Message);
    }

    [Fact]
    public async Task AReferenceThatIsNotAWellFormedUri_IsNotAReference()
    {
        // Never stored by registration, which refuses it; a row that somehow held one must not throw here.
        SeedRetired();
        _subjects.Refers("acme.billing.Invoice", _environment, 1, "concordat:///" + Name + "/not-a-number");

        var result = await PurgeAsync();

        Assert.True(result.IsSuccess, result.Error?.Message);
    }

    [Fact]
    public async Task ASubjectIsNotStoppedByItsOwnReferences()
    {
        SeedRetired();
        _subjects.Refers(Name, _environment, 1, Uri("dev", Name, 1));

        var result = await PurgeAsync();

        Assert.True(result.IsSuccess, result.Error?.Message);
    }

    [Fact]
    public async Task ASameNamedSubjectInAnotherEnvironmentReferencingItselfIsNotAReferrer_ButOneReferencingThisIs()
    {
        SeedRetired();
        var staging = _resolver.Resolve("staging");

        // Staging's own subject of this name refers to dev's: that is a referrer, because it is not this one.
        _subjects.Refers(Name, staging, 3, Uri("dev", Name, 1));

        var result = await PurgeAsync();

        Assert.Equal(ConcordatCodes.SubjectReferenced, result.Error!.Code);
        Assert.Contains($"{Name}@3 (in another environment)", result.Error.Message, StringComparison.Ordinal);
    }
    [Fact]
    public async Task TheFirstRefusalIsTheOneReported_ContractsBeforeReferences()
    {
        SeedRetired();
        SeedRetired("acme.billing.Invoice");
        _contracts.Bind("orders-contract", Ref(Name));
        _subjects.Refers("acme.billing.Invoice", _environment, 1, Uri("dev", Name, 1));

        var result = await PurgeAsync();

        Assert.Equal(ConcordatCodes.SubjectInUse, result.Error!.Code);
        Assert.Equal(0, _subjects.ReferrerLookups);
    }

    [Fact]
    public async Task ASuccessfulDeletionTouchesNeitherASchemaNorAContract()
    {
        // Schemas are content-addressed and never deleted (ADR-015), and a deletion that was allowed has
        // nothing bound to it, so the handler has nothing to change anywhere else.
        SeedRetired(versions: 2);
        _contracts.Bind("unrelated", Ref("acme.billing.InvoiceRaised"));

        await PurgeAsync();

        Assert.Equal(0, _schemas.Staged);
        Assert.Equal(0, _contracts.Writes);
    }
}

/// <summary>An in-memory <see cref="IContractRepository"/> that can be told what is bound.</summary>
internal sealed class FakeContracts : IContractRepository
{
    private readonly List<Contract> _contracts = [];

    /// <summary>How many times a handler listed the contracts.</summary>
    public int Lists { get; private set; }

    /// <summary>How many contracts a handler staged.</summary>
    public int Writes { get; private set; }

    private static readonly TopologyScope Scope = new(null, "/");

    /// <summary>A contract that publishes the subject.</summary>
    public void Bind(string name, SubjectRef subject, EnvironmentId? environment = null)
    {
        var contract = Create(name, environment);
        Assert.True(contract.AddPublish(new PublishBinding(
            Scope, "orders", RoutingKeyPattern.Create("orders.created").Value, [subject])).IsSuccess);
    }

    /// <summary>A contract that consumes the subject.</summary>
    public void Consume(string name, SubjectRef subject, EnvironmentId? environment = null)
    {
        var contract = Create(name, environment);
        Assert.True(contract.AddConsume(new ConsumeBinding(Scope, "orders-queue", [subject])).IsSuccess);
    }

    private Contract Create(string name, EnvironmentId? environment)
    {
        var contract = Contract.Create(environment ?? EnvironmentIdFor, name, Build.At).Value;
        _contracts.Add(contract);

        return contract;
    }

    /// <summary>The environment contracts are made in unless one is named.</summary>
    public EnvironmentId EnvironmentIdFor { get; set; }

    public Task<Contract?> FindAsync(EnvironmentId environmentId, string name, CancellationToken cancellationToken) =>
        Task.FromResult(_contracts.Find(c => c.EnvironmentId == environmentId && c.Name == name));

    public Task<IReadOnlyList<Contract>> ListAsync(EnvironmentId environmentId, CancellationToken cancellationToken)
    {
        Lists++;

        return Task.FromResult<IReadOnlyList<Contract>>(
            [.. _contracts.Where(c => c.EnvironmentId == environmentId).OrderBy(c => c.Name, StringComparer.Ordinal)]);
    }

    public void Add(Contract contract)
    {
        Writes++;
        _contracts.Add(contract);
    }
}

/// <summary>An in-memory <see cref="IServiceRegistrationRepository"/> that can be told what is declared.</summary>
internal sealed class FakeServices : IServiceRegistrationRepository
{
    private readonly List<ServiceRegistration> _services = [];

    /// <summary>The environment services are declared in.</summary>
    public EnvironmentId EnvironmentIdFor { get; set; }

    /// <summary>A service declaring what it produces and consumes.</summary>
    public void Declare(string name, IReadOnlyList<SubjectRef> consumes, IReadOnlyList<SubjectRef> produces) =>
        _services.Add(ServiceRegistration.Create(EnvironmentIdFor, name, produces, consumes, Build.At).Value);

    public Task<ServiceRegistration?> FindAsync(
        EnvironmentId environmentId, string name, CancellationToken cancellationToken) =>
        Task.FromResult(_services.Find(s => s.EnvironmentId == environmentId && s.Name == name));

    public Task<IReadOnlyList<ServiceRegistration>> ListAsync(
        EnvironmentId environmentId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ServiceRegistration>>(
            [.. _services.Where(s => s.EnvironmentId == environmentId).OrderBy(s => s.Name, StringComparer.Ordinal)]);

    public void Add(ServiceRegistration registration) => _services.Add(registration);
}
