using Concordat.Domain.Contracts;
using Concordat.Domain.Governance;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;

namespace Concordat.Domain.Tests;

/// <summary>M7.4's service registrations — the data impact analysis is computed from.</summary>
public class ServiceRegistrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private static SubjectRef Ref(string subject, string selector = "latest") =>
        new(SubjectName.Create(subject).Value, VersionSelector.Parse(selector).Value);

    private static ServiceRegistration New(
        IReadOnlyList<SubjectRef>? produces = null,
        IReadOnlyList<SubjectRef>? consumes = null,
        DateTimeOffset? at = null) =>
        ServiceRegistration.Create(
            EnvironmentId.New(), "orders-api", produces ?? [], consumes ?? [], at ?? Now).Value;

    [Fact]
    public void ADeclarationKeepsWhatItWasGiven()
    {
        var service = New([Ref("acme.Created")], [Ref("acme.Shipped", ">=2")]);

        Assert.Equal("orders-api", service.Name);
        Assert.Equal("acme.Created", Assert.Single(service.Produces).Subject.Value);
        Assert.Equal(">=2", Assert.Single(service.Consumes).Selector.ToString());
        Assert.Equal(Now, service.FirstSeenAt);
        Assert.Equal(Now, service.LastSeenAt);
    }

    [Fact]
    public void AServiceThatProducesAndConsumesNothingIsStillLegal()
    {
        // A service that has not been instrumented yet reports empty lists. Refusing that would
        // make partial adoption impossible, which is the state every brownfield estate is in.
        var service = New();

        Assert.Empty(service.Produces);
        Assert.Empty(service.Consumes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("orders api")]
    [InlineData("orders/api")]
    [InlineData("-leading-hyphen")]
    public void AnUnusableNameIsRefused(string? name)
    {
        var result = ServiceRegistration.Create(EnvironmentId.New(), name, [], [], Now);

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.ServiceNameInvalid, result.Error!.Code);
    }

    [Theory]
    [InlineData("orders-api")]
    [InlineData("orders.api")]
    [InlineData("orders_api")]
    [InlineData("Orders2")]
    public void AUsableNameIsAccepted(string name) =>
        Assert.True(ServiceRegistration.Create(EnvironmentId.New(), name, [], [], Now).IsSuccess);

    [Fact]
    public void ANameIsTrimmedRatherThanRefused()
    {
        var service = ServiceRegistration.Create(
            EnvironmentId.New(), "  orders-api  ", [], [], Now).Value;

        Assert.Equal("orders-api", service.Name);
    }

    [Fact]
    public void ReportingReplacesIntentRatherThanAccumulatingIt()
    {
        // A service that stopped consuming a subject has no other way to say so, and a stale
        // entry nobody can remove is what makes impact analysis stop being believed.
        var service = New(consumes: [Ref("acme.Old"), Ref("acme.Shipped")]);

        service.Report([], [Ref("acme.Shipped")], Now.AddHours(1));

        Assert.Equal("acme.Shipped", Assert.Single(service.Consumes).Subject.Value);
        Assert.Equal(Now.AddHours(1), service.LastSeenAt);
    }

    [Fact]
    public void ReportingDoesNotMoveTheFirstSeenTimestamp()
    {
        var service = New();

        service.Report([], [], Now.AddDays(90));

        Assert.Equal(Now, service.FirstSeenAt);
        Assert.Equal(Now.AddDays(90), service.LastSeenAt);
    }

    [Fact]
    public void AServiceGoesStaleAfterThirtyDaysAndComesBackWhenItReports()
    {
        var service = New();

        Assert.False(service.IsStale(Now.AddDays(29)));
        Assert.True(service.IsStale(Now.AddDays(31)));

        service.Report([], [], Now.AddDays(31));

        Assert.False(service.IsStale(Now.AddDays(31)));
    }

    [Fact]
    public void ConsumerOfFindsTheDeclaredReferenceAndNothingElse()
    {
        var service = New(
            produces: [Ref("acme.Created")],
            consumes: [Ref("acme.Shipped", "3")]);

        Assert.Equal("3", service.ConsumerOf(SubjectName.Create("acme.Shipped").Value)!.Selector.ToString());

        // Producing a subject is not consuming it. Conflating the two would report a publisher
        // as broken by its own change.
        Assert.Null(service.ConsumerOf(SubjectName.Create("acme.Created").Value));
        Assert.Null(service.ConsumerOf(SubjectName.Create("acme.Unknown").Value));
    }

    [Fact]
    public void TimestampsAreNormalisedToUtc()
    {
        var local = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.FromHours(5));
        var service = ServiceRegistration.Create(EnvironmentId.New(), "svc", [], [], local).Value;

        Assert.Equal(TimeSpan.Zero, service.FirstSeenAt.Offset);
        Assert.Equal(local.UtcDateTime, service.FirstSeenAt.UtcDateTime);
    }
}

/// <summary>M7.4's audit entries.</summary>
public class AuditEntryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private static ActorId Actor => ActorId.Create("someone@example.com").Value;

    [Fact]
    public void AnEntryKeepsWhatItWasGiven()
    {
        var environment = EnvironmentId.New();

        var entry = AuditEntry.Record(
            environment, AuditAction.VersionApproved, Actor, "acme.Created", Now, "version 3");

        Assert.Equal(environment, entry.EnvironmentId);
        Assert.Equal(AuditAction.VersionApproved, entry.Action);
        Assert.Equal("acme.Created", entry.Target);
        Assert.Equal("version 3", entry.Detail);
        Assert.Equal(Now, entry.At);
        Assert.NotEqual(Guid.Empty, entry.Id);
    }

    [Fact]
    public void AnEntryNeedNotBelongToAnEnvironment()
    {
        var entry = AuditEntry.Record(null, AuditAction.EnvironmentCreated, Actor, "prod", Now);

        Assert.Null(entry.EnvironmentId);
        Assert.Null(entry.Detail);
    }

    [Fact]
    public void AnOverlongTargetIsTruncatedRatherThanRefused()
    {
        // Auditing is a side effect of a change the domain already allowed. Failing the change
        // because its description does not fit would make the log a source of outages.
        var entry = AuditEntry.Record(
            null,
            AuditAction.SubjectUpdated,
            Actor,
            new string('x', AuditEntry.MaxTargetLength + 100),
            Now,
            new string('y', AuditEntry.MaxDetailLength + 100));

        Assert.Equal(AuditEntry.MaxTargetLength, entry.Target.Length);
        Assert.Equal(AuditEntry.MaxDetailLength, entry.Detail!.Length);
    }

    [Fact]
    public void AMissingTargetBecomesEmptyRatherThanNull()
    {
        var entry = AuditEntry.Record(null, AuditAction.SubjectCreated, Actor, null, Now);

        Assert.Equal(string.Empty, entry.Target);
    }

    [Fact]
    public void TheTimestampIsNormalisedToUtc()
    {
        var local = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.FromHours(-3));

        var entry = AuditEntry.Record(null, AuditAction.SubjectCreated, Actor, "x", local);

        Assert.Equal(TimeSpan.Zero, entry.At.Offset);
        Assert.Equal(local.UtcDateTime, entry.At.UtcDateTime);
    }

    [Fact]
    public async Task IdentifiersAreTimeOrderedSoInsertsStayLocalInTheIndex()
    {
        // The audit log only grows and is read newest-first, so a v4 GUID key would scatter
        // every insert across the index for no benefit.
        //
        // This test was wrong twice before it was right, and both mistakes are easy to repeat.
        // A UUIDv7 encodes a *millisecond* timestamp and fills the rest with randomness, so two
        // ids minted in the same millisecond have no defined order — asserting one was a coin
        // flip that passed roughly half the time. And Guid.CompareTo orders by its internal
        // fields, not by the byte order PostgreSQL's uuid type sorts on, so it would not have
        // measured index locality even when it passed.
        var first = AuditEntry.Record(null, AuditAction.SubjectCreated, Actor, "a", Now);
        await Task.Delay(5);
        var second = AuditEntry.Record(null, AuditAction.SubjectCreated, Actor, "b", Now);

        var earlier = first.Id.ToByteArray(bigEndian: true);
        var later = second.Id.ToByteArray(bigEndian: true);

        Assert.True(
            earlier.AsSpan().SequenceCompareTo(later) < 0,
            "a later id should sort after an earlier one in byte order");

        // And the ordering comes from the timestamp, not from luck: the first 48 bits are
        // milliseconds since the epoch, and they are what makes consecutive inserts adjacent.
        Assert.True(earlier.AsSpan(0, 6).SequenceCompareTo(later.AsSpan(0, 6)) < 0);
    }
}
