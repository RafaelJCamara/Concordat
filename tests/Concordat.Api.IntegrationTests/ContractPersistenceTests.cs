using Concordat.Domain.Contracts;
using Concordat.Domain.Registry;
using Microsoft.EntityFrameworkCore;

namespace Concordat.Api.IntegrationTests;

/// <summary>
/// M7.3 contracts survive a round trip through PostgreSQL.
/// </summary>
/// <remarks>
/// The domain tests prove the invariants; these prove the mapping, which is where a value-object
/// list squeezed into one column can go wrong without any of them noticing.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class ContractPersistenceTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private static RoutingKeyPattern Pattern(string value) =>
        RoutingKeyPattern.Create(value).Value;

    private static SubjectRef Ref(string subject, string selector) =>
        new(SubjectName.Create(subject).Value, VersionSelector.Parse(selector).Value);

    private static Contract NewContract(EnvironmentId environment) =>
        Contract.Create(environment, $"c-{Guid.CreateVersion7():N}"[..12], Now).Value;

    [Fact]
    public async Task ABindingRoundTripsIncludingItsSubjectsAndSelectors()
    {
        // The subject list is stored as one text column. Every selector spelling has to survive
        // that, because a selector that comes back as 'latest' when it was pinned would quietly
        // widen a contract.
        var environment = EnvironmentId.New();
        var contract = NewContract(environment);
        var broker = Guid.CreateVersion7();

        contract.AddPublish(new PublishBinding(
            new TopologyScope(broker, "/orders"),
            "orders",
            Pattern("orders.*.created"),
            [Ref("acme.Created", "latest"), Ref("acme.Other", ">=2"), Ref("acme.Pinned", "3")],
            precedence: 7));

        contract.AddConsume(new ConsumeBinding(
            TopologyScope.Default, "orders.q", [Ref("acme.Created", "5")]));

        await using (var write = postgres.NewContext())
        {
            write.Contracts.Add(contract);
            await write.SaveChangesAsync(CancellationToken.None);
        }

        await using var read = postgres.NewContext();
        var loaded = await read.Contracts.SingleAsync(
            c => c.Id == contract.Id, CancellationToken.None);

        var publish = Assert.Single(loaded.Publishes);
        Assert.Equal("orders", publish.Exchange);
        Assert.Equal("orders.*.created", publish.RoutingKeyPattern.Value);
        Assert.Equal(7, publish.Precedence);
        Assert.Equal(broker, publish.Scope.BrokerId);
        Assert.Equal("/orders", publish.Scope.VirtualHost);

        Assert.Equal(
            ["acme.Created@latest", "acme.Other@>=2", "acme.Pinned@3"],
            publish.Subjects.Select(r => $"{r.Subject.Value}@{r.Selector}"));

        var consume = Assert.Single(loaded.Consumes);
        Assert.Equal("orders.q", consume.Queue);
        Assert.Null(consume.Scope.BrokerId);
        Assert.Equal("acme.Created@5", $"{consume.Subjects[0].Subject.Value}@{consume.Subjects[0].Selector}");
    }

    [Fact]
    public async Task ThePatternStillComputesOverlapAfterReloading()
    {
        // A pattern rehydrated through FromTrusted must behave identically to one that was
        // validated, or the conflict invariant would hold on write and not on read.
        var environment = EnvironmentId.New();
        var contract = NewContract(environment);

        contract.AddPublish(new PublishBinding(
            TopologyScope.Default, "orders", Pattern("orders.#"), [Ref("acme.A", "latest")]));

        await using (var write = postgres.NewContext())
        {
            write.Contracts.Add(contract);
            await write.SaveChangesAsync(CancellationToken.None);
        }

        await using var read = postgres.NewContext();
        var loaded = await read.Contracts.SingleAsync(
            c => c.Id == contract.Id, CancellationToken.None);

        var conflict = loaded.AddPublish(new PublishBinding(
            TopologyScope.Default, "orders", Pattern("orders.created"), [Ref("acme.B", "latest")]));

        Assert.True(conflict.IsFailure);
    }

    [Fact]
    public async Task ChangingASubjectListIsActuallyWritten()
    {
        // Without a value comparer EF compares the converted collection by reference, so an
        // edited binding is considered unmodified and silently never saved. That failure only
        // appears on the second save, which is exactly when nobody is looking.
        var environment = EnvironmentId.New();
        var contract = NewContract(environment);

        contract.AddPublish(new PublishBinding(
            TopologyScope.Default, "orders", Pattern("a"), [Ref("acme.A", "latest")]));

        await using (var write = postgres.NewContext())
        {
            write.Contracts.Add(contract);
            await write.SaveChangesAsync(CancellationToken.None);
        }

        await using (var edit = postgres.NewContext())
        {
            var loaded = await edit.Contracts.SingleAsync(
                c => c.Id == contract.Id, CancellationToken.None);

            loaded.AddPublish(new PublishBinding(
                TopologyScope.Default, "orders", Pattern("b"), [Ref("acme.B", ">=4")]));

            await edit.SaveChangesAsync(CancellationToken.None);
        }

        await using var read = postgres.NewContext();
        var reloaded = await read.Contracts.SingleAsync(
            c => c.Id == contract.Id, CancellationToken.None);

        Assert.Equal(2, reloaded.Publishes.Count);
        Assert.Contains(reloaded.Publishes, p => p.Subjects[0].Selector.ToString() == ">=4");
    }

    [Fact]
    public async Task TwoContractsWithOneNameInAnEnvironmentAreRejected()
    {
        // Enforced by the database rather than a read-then-write, which races.
        var environment = EnvironmentId.New();
        var name = $"dup-{Guid.CreateVersion7():N}"[..12];

        await using var context = postgres.NewContext();
        context.Contracts.Add(Contract.Create(environment, name, Now).Value);
        context.Contracts.Add(Contract.Create(environment, name, Now).Value);

        await Assert.ThrowsAsync<DbUpdateException>(
            () => context.SaveChangesAsync(CancellationToken.None));
    }
}
