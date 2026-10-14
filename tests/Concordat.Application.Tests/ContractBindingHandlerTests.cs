using Concordat.Application.Abstractions;
using Concordat.Application.Registry;
using Concordat.Application.Tests.TestSupport;
using Concordat.Domain.Contracts;
using Concordat.Domain.Governance;
using Concordat.Domain.Identity;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;
using Microsoft.Extensions.Time.Testing;

namespace Concordat.Application.Tests;

/// <summary>
/// Editing and removing a contract's bindings: what is recorded, and what is not.
/// </summary>
/// <remarks>
/// A binding used to have no identity, so it could not be changed or removed once added, and a
/// binding added without a precedence could never be separated from a later overlapping one.
/// These pin the handlers that address a binding by id: one audit entry of its own action as the
/// authenticated caller, in the same unit of work as the change; nothing written for a request
/// that changes nothing or is refused.
/// </remarks>
public class ContractBindingHandlerTests
{
    private const string ContractName = "orders-v1";

    private readonly EnvironmentId _environment = EnvironmentId.New();
    private readonly FakeEnvironmentStore _environments = new();
    private readonly FakeContracts _contracts = new();
    private readonly RecordingAuditLog _audit = new();
    private readonly RecordingUnitOfWork _unitOfWork = new();
    private readonly SettableCaller _caller = new();
    private readonly FakeTimeProvider _clock = new(Build.At);

    private readonly Contract _contract;

    public ContractBindingHandlerTests()
    {
        _environments.Seed(_environment, "prod", RegistrationPolicy.Open);
        _contract = Contract.Create(_environment, ContractName, Build.At).Value;
        _contracts.Seed(_contract);
        _caller.Current = _caller.Current with { Actor = ActorId.Create("carol").Value };
    }

    private static SubjectRefInput Input(string subject, string selector = "latest") =>
        new(subject, selector);

    private static SubjectRef Ref(string subject, string selector = "latest") =>
        new(SubjectName.Create(subject).Value, VersionSelector.Parse(selector).Value);

    private PublishBinding SeedPublish(
        string pattern, string subject, int? precedence = null, string exchange = "orders")
    {
        var binding = new PublishBinding(
            TopologyScope.Default,
            exchange,
            RoutingKeyPattern.Create(pattern).Value,
            [Ref(subject)],
            precedence);

        Assert.True(_contract.AddPublish(binding).IsSuccess);
        return binding;
    }

    private ConsumeBinding SeedConsume(string queue, string subject)
    {
        var binding = new ConsumeBinding(TopologyScope.Default, queue, [Ref(subject)]);

        Assert.True(_contract.AddConsume(binding).IsSuccess);
        return binding;
    }

    private Task<Result<Contract>> ReplacePublishAsync(
        Guid id,
        int? precedence,
        string contract = ContractName,
        SubjectRefInput[]? subjects = null) =>
        new ReplacePublishBindingHandler(
            _environments, _contracts, _audit, _caller, _unitOfWork, _clock).HandleAsync(
            new ReplacePublishBindingCommand("prod", contract, id, subjects, precedence),
            CancellationToken.None);

    private Task<Result<Contract>> RemovePublishAsync(Guid id, string contract = ContractName) =>
        new RemovePublishBindingHandler(
            _environments, _contracts, _audit, _caller, _unitOfWork, _clock).HandleAsync(
            new RemovePublishBindingCommand("prod", contract, id), CancellationToken.None);

    private Task<Result<Contract>> ReplaceConsumeAsync(
        Guid id, params SubjectRefInput[] subjects) =>
        new ReplaceConsumeBindingHandler(
            _environments, _contracts, _audit, _caller, _unitOfWork, _clock).HandleAsync(
            new ReplaceConsumeBindingCommand("prod", ContractName, id, subjects),
            CancellationToken.None);

    private Task<Result<Contract>> RemoveConsumeAsync(Guid id) =>
        new RemoveConsumeBindingHandler(
            _environments, _contracts, _audit, _caller, _unitOfWork, _clock).HandleAsync(
            new RemoveConsumeBindingCommand("prod", ContractName, id), CancellationToken.None);

    // ------------------------------------------------------------ change a publish binding

    [Fact]
    public async Task ChangingAPrecedence_IsAuditedAsTheCallerAndSaysWhatChanged()
    {
        var binding = SeedPublish("orders.#", "acme.A");

        var result = await ReplacePublishAsync(binding.Id, 1, subjects: [Input("acme.A")]);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(1, binding.Precedence);

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal(AuditAction.ContractBindingChanged, entry.Action);
        Assert.Equal("carol", entry.Actor.Value);
        Assert.Equal(ContractName, entry.Target);
        Assert.Equal(_environment, entry.EnvironmentId);
        Assert.Equal("publish orders / orders.#: precedence none -> 1", entry.Detail);
        Assert.Equal(1, _unitOfWork.Saves);
    }

    [Fact]
    public async Task ChangingSubjectsAndPrecedenceTogether_IsOneEntryNamingBoth()
    {
        var binding = SeedPublish("orders.#", "acme.A", precedence: 2);

        await ReplacePublishAsync(
            binding.Id, null, subjects: [Input("acme.B", "3"), Input("acme.C")]);

        Assert.Equal(
            "publish orders / orders.#: precedence 2 -> none; " +
            "subjects acme.A@latest -> acme.B@3, acme.C@latest",
            Assert.Single(_audit.Entries).Detail);
    }

    [Fact]
    public async Task ChangingOnlySubjects_DoesNotMentionPrecedence()
    {
        var binding = SeedPublish("orders.#", "acme.A", precedence: 2);

        await ReplacePublishAsync(binding.Id, 2, subjects: [Input("acme.B")]);

        Assert.Equal(
            "publish orders / orders.#: subjects acme.A@latest -> acme.B@latest",
            Assert.Single(_audit.Entries).Detail);
    }

    [Fact]
    public async Task AChangeThatChangesNothing_WritesNothingAndSavesNothing()
    {
        // Answered as the success it is, with the contract as it stands. The rule a policy change
        // and an idempotent registration already follow: the trail is not told that something
        // happened which did not.
        var binding = SeedPublish("orders.#", "acme.A", precedence: 4);

        var result = await ReplacePublishAsync(binding.Id, 4, subjects: [Input("acme.A")]);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Empty(_audit.Entries);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task AChange_IsRecordedInTheSameUnitOfWorkAsTheChange()
    {
        var binding = SeedPublish("orders.#", "acme.A");
        var appendedAtSave = -1;
        _unitOfWork.OnSave = () => appendedAtSave = _audit.Entries.Count;

        await ReplacePublishAsync(binding.Id, 1, subjects: [Input("acme.A")]);

        Assert.Equal(1, appendedAtSave);
    }

    [Fact]
    public async Task AChangeThatWouldConflict_IsRefusedAndRecordsAndSavesNothing()
    {
        SeedPublish("orders.#", "acme.Fallback", precedence: 1);
        var specific = SeedPublish("orders.created", "acme.Created", precedence: 10);

        var result = await ReplacePublishAsync(specific.Id, 1, subjects: [Input("acme.Created")]);

        Assert.Equal(ConcordatCodes.BindingConflict, result.Error!.Code);
        Assert.Equal(10, specific.Precedence);
        Assert.Empty(_audit.Entries);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task ChangingAnUnknownBinding_IsBindingNotFoundAndRecordsNothing()
    {
        SeedPublish("orders.#", "acme.A");

        var result = await ReplacePublishAsync(
            Guid.CreateVersion7(), 1, subjects: [Input("acme.A")]);

        Assert.Equal(ConcordatCodes.BindingNotFound, result.Error!.Code);
        Assert.Empty(_audit.Entries);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task ChangingABindingOfAnUnknownContract_IsContractNotFound()
    {
        var binding = SeedPublish("orders.#", "acme.A");

        var result = await ReplacePublishAsync(
            binding.Id, 1, contract: "nope", subjects: [Input("acme.A")]);

        Assert.Equal(ConcordatCodes.ContractNotFound, result.Error!.Code);
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public async Task ChangingToNoSubjects_IsRefusedBeforeAnythingIsRecorded()
    {
        // A binding that carries nothing says nothing, which is what adding one already refuses.
        var binding = SeedPublish("orders.#", "acme.A");

        var result = await ReplacePublishAsync(binding.Id, 1);

        Assert.True(result.IsFailure);
        Assert.Equal("acme.A", Assert.Single(binding.Subjects).Subject.Value);
        Assert.Empty(_audit.Entries);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task ChangingToAnInvalidSubjectName_IsRefusedAndChangesNothing()
    {
        var binding = SeedPublish("orders.#", "acme.A");

        var result = await ReplacePublishAsync(
            binding.Id, 1, subjects: [Input("acme..bad")]);

        Assert.Equal(ConcordatCodes.SubjectNameInvalid, result.Error!.Code);
        Assert.Null(binding.Precedence);
        Assert.Empty(_audit.Entries);
    }

    // ------------------------------------------------------------ remove a publish binding

    [Fact]
    public async Task RemovingAPublishBinding_IsAuditedAsTheCallerAndMirrorsTheAddedEntry()
    {
        var kept = SeedPublish("orders.updated", "acme.B");
        var gone = SeedPublish("orders.created", "acme.A");

        var result = await RemovePublishAsync(gone.Id);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Same(kept, Assert.Single(_contract.Publishes));

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal(AuditAction.ContractBindingRemoved, entry.Action);
        Assert.Equal("carol", entry.Actor.Value);
        Assert.Equal(ContractName, entry.Target);
        Assert.Equal("publish orders / orders.created -> acme.A@latest", entry.Detail);
        Assert.Equal(1, _unitOfWork.Saves);
    }

    [Fact]
    public async Task RemovingTwice_IsBindingNotFoundTheSecondTimeAndRecordsOneEntry()
    {
        var binding = SeedPublish("orders.created", "acme.A");

        Assert.True((await RemovePublishAsync(binding.Id)).IsSuccess);
        var second = await RemovePublishAsync(binding.Id);

        Assert.Equal(ConcordatCodes.BindingNotFound, second.Error!.Code);
        Assert.Single(_audit.Entries);
        Assert.Equal(1, _unitOfWork.Saves);
    }

    [Fact]
    public async Task ARemoval_IsRecordedInTheSameUnitOfWorkAsTheRemoval()
    {
        var binding = SeedPublish("orders.created", "acme.A");
        var appendedAtSave = -1;
        var remainingAtSave = -1;
        _unitOfWork.OnSave = () =>
        {
            appendedAtSave = _audit.Entries.Count;
            remainingAtSave = _contract.Publishes.Count;
        };

        await RemovePublishAsync(binding.Id);

        Assert.Equal(1, appendedAtSave);
        Assert.Equal(0, remainingAtSave);
    }

    [Fact]
    public async Task RemovingFromAnUnknownContract_IsContractNotFoundAndRecordsNothing()
    {
        var binding = SeedPublish("orders.created", "acme.A");

        var result = await RemovePublishAsync(binding.Id, contract: "nope");

        Assert.Equal(ConcordatCodes.ContractNotFound, result.Error!.Code);
        Assert.Single(_contract.Publishes);
        Assert.Empty(_audit.Entries);
    }

    // ------------------------------------------------------------------- consume bindings

    [Fact]
    public async Task ChangingAConsumeBinding_IsAuditedAsTheCallerAndSaysWhatChanged()
    {
        var binding = SeedConsume("orders.q", "acme.A");

        var result = await ReplaceConsumeAsync(binding.Id, Input("acme.B", "2"));

        Assert.True(result.IsSuccess, result.Error?.Message);

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal(AuditAction.ContractBindingChanged, entry.Action);
        Assert.Equal("carol", entry.Actor.Value);
        Assert.Equal("consume orders.q: subjects acme.A@latest -> acme.B@2", entry.Detail);
        Assert.Equal(1, _unitOfWork.Saves);
    }

    [Fact]
    public async Task ChangingAConsumeBindingToWhatItHas_WritesNothing()
    {
        var binding = SeedConsume("orders.q", "acme.A");

        var result = await ReplaceConsumeAsync(binding.Id, Input("acme.A"));

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Empty(_audit.Entries);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task ChangingAConsumeBindingIntoAConflict_IsRefusedAndRecordsNothing()
    {
        SeedConsume("orders.q", "acme.A");
        var second = SeedConsume("orders.q", "acme.A");

        var result = await ReplaceConsumeAsync(second.Id, Input("acme.Other"));

        Assert.Equal(ConcordatCodes.BindingConflict, result.Error!.Code);
        Assert.Empty(_audit.Entries);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task RemovingAConsumeBinding_IsAuditedAsTheCaller()
    {
        var binding = SeedConsume("orders.q", "acme.A");

        var result = await RemoveConsumeAsync(binding.Id);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Empty(_contract.Consumes);

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal(AuditAction.ContractBindingRemoved, entry.Action);
        Assert.Equal("carol", entry.Actor.Value);
        Assert.Equal("consume orders.q -> acme.A@latest", entry.Detail);
        Assert.Equal(1, _unitOfWork.Saves);
    }

    [Fact]
    public async Task RemovingAnUnknownConsumeBinding_IsBindingNotFoundAndRecordsNothing()
    {
        SeedConsume("orders.q", "acme.A");

        var result = await RemoveConsumeAsync(Guid.CreateVersion7());

        Assert.Equal(ConcordatCodes.BindingNotFound, result.Error!.Code);
        Assert.Single(_contract.Consumes);
        Assert.Empty(_audit.Entries);
    }

    private sealed class FakeContracts : IContractRepository
    {
        private readonly List<Contract> _all = [];

        public void Seed(Contract contract) => _all.Add(contract);

        public Task<Contract?> FindAsync(
            EnvironmentId environmentId, string name, CancellationToken cancellationToken) =>
            Task.FromResult(_all.FirstOrDefault(
                c => c.EnvironmentId == environmentId && c.Name == name));

        public Task<IReadOnlyList<Contract>> ListAsync(
            EnvironmentId environmentId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Contract>>(
                [.. _all.Where(c => c.EnvironmentId == environmentId)]);

        public void Add(Contract contract) => _all.Add(contract);
    }
}
