using Concordat.Application.Registry;
using Concordat.Application.Tests.TestSupport;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;
using Microsoft.Extensions.Time.Testing;

namespace Concordat.Application.Tests;

/// <summary>The approval gate (ADR-017), seen from the handler that operates it.</summary>
public class DecideVersionHandlerTests
{
    private const string Name = "acme.orders.OrderCreated";
    private const string V1 = """{"type":"object","properties":{"id":{"type":"string"}}}""";

    private const string V1Required =
        """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}""";

    private readonly EnvironmentId _environment = EnvironmentId.New();
    private readonly FakeSubjects _subjects = new();
    private readonly FakeSchemas _schemas = new();
    private readonly RecordingUnitOfWork _unitOfWork = new();
    private readonly RecordingAuditLog _audit = new();
    private readonly FakeTimeProvider _clock = new(Build.At);

    private Task<Result<Subject>> DecideAsync(
        int ordinal, bool approve, string name = Name, string decidedBy = "bob") =>
        new DecideVersionHandler(_subjects, _audit, _unitOfWork, _clock).HandleAsync(
            new DecideVersionCommand(_environment, name, ordinal, decidedBy, approve),
            CancellationToken.None);

    /// <summary>A subject with an active version 1 and a breaking version 2 awaiting review.</summary>
    private Subject WithPendingSecondVersion()
    {
        var subject = Build.Subject(_environment);
        subject.Register(_schemas, V1);
        subject.Register(_schemas, V1Required, breaking: true);
        _subjects.Seed(subject);

        return subject;
    }

    [Fact]
    public async Task AnInvalidSubjectName_IsRefusedBeforeTheRepositoryIsTouched()
    {
        var result = await DecideAsync(1, approve: true, name: "acme orders");

        Assert.Equal(ConcordatCodes.SubjectNameInvalid, result.Error!.Code);
        Assert.Equal(0, _subjects.Finds);
    }

    [Fact]
    public async Task AnEmptyDecidedBy_IsRefusedBeforeTheRepositoryIsTouched()
    {
        // The gate exists so that a breaking change is somebody's decision. An approval with no
        // approver records the outcome and loses the only thing the gate was for.
        var result = await DecideAsync(2, approve: true, decidedBy: "");

        Assert.Equal(ConcordatCodes.ActorIdInvalid, result.Error!.Code);
        Assert.Equal(0, _subjects.Finds);
    }

    [Fact]
    public async Task AnUnknownSubject_IsSubjectNotFound()
    {
        var result = await DecideAsync(1, approve: true);

        Assert.Equal(ConcordatCodes.SubjectNotFound, result.Error!.Code);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task AnUnknownOrdinal_IsVersionNotFoundAndCommitsNothing()
    {
        WithPendingSecondVersion();

        var result = await DecideAsync(99, approve: true);

        Assert.Equal(ConcordatCodes.VersionNotFound, result.Error!.Code);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task ApprovingAnAlreadyActiveVersion_IsVersionNotAwaitingApproval()
    {
        // A distinct code from version_not_found: the version is there, it simply was never
        // gated. Collapsing the two would send an operator looking for a missing row.
        WithPendingSecondVersion();

        var result = await DecideAsync(1, approve: true);

        Assert.Equal(ConcordatCodes.VersionNotAwaitingApproval, result.Error!.Code);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task ApprovingTwice_IsRefusedTheSecondTime()
    {
        var subject = WithPendingSecondVersion();

        Assert.True((await DecideAsync(2, approve: true)).IsSuccess);
        var second = await DecideAsync(2, approve: true);

        Assert.Equal(ConcordatCodes.VersionNotAwaitingApproval, second.Error!.Code);
        Assert.Equal(2, subject.Latest!.Ordinal);
        Assert.Equal(1, _unitOfWork.Saves);
    }

    [Fact]
    public async Task ARejectedVersionCannotThenBeApproved()
    {
        // The reviewer's decision is final for that proposal. Re-opening it would let a
        // breaking change reach the latest pointer through a route nobody is watching.
        var subject = WithPendingSecondVersion();

        Assert.True((await DecideAsync(2, approve: false)).IsSuccess);
        var approve = await DecideAsync(2, approve: true);

        Assert.Equal(ConcordatCodes.VersionNotAwaitingApproval, approve.Error!.Code);
        Assert.Equal(VersionStatus.Rejected, subject.Versions[1].Status);
    }

    [Fact]
    public async Task RejectionLeavesTheLatestPointerWhereItWas()
    {
        var subject = WithPendingSecondVersion();

        await DecideAsync(2, approve: false);

        Assert.Equal(1, subject.Latest!.Ordinal);
    }

    [Fact]
    public async Task TheDecisionIsAttributedToTheCallerAndStampedFromTheInjectedClock()
    {
        // Who and when are the entire record of the gate having been operated. A wall-clock
        // read here would make the audit trail untestable.
        var moment = new DateTimeOffset(2030, 5, 6, 7, 8, 9, TimeSpan.FromHours(3));
        _clock.SetUtcNow(moment);
        var subject = WithPendingSecondVersion();

        await DecideAsync(2, approve: true, decidedBy: "carol");

        var version = subject.Versions[1];
        Assert.Equal(VersionStatus.Active, version.Status);
        Assert.Equal("carol", version.DecidedBy!.Value);
        Assert.Equal(moment.ToUniversalTime(), version.DecidedAt);
    }

    [Fact]
    public async Task ApprovalDoesNotDragTheLatestPointerBackwards()
    {
        // A compatible version registered while the breaking one sat in review is already the
        // pointer's target. Approving the older proposal must not reverse that — consumers
        // tracking latest would silently move back to a narrower contract.
        var subject = Build.Subject(_environment);
        subject.Register(_schemas, V1);
        subject.Register(_schemas, V1Required, breaking: true);
        subject.Register(
            _schemas, """{"type":"object","properties":{"id":{"type":"string"},"n":{"type":"string"}}}""");
        _subjects.Seed(subject);

        Assert.Equal(3, subject.Latest!.Ordinal);

        await DecideAsync(2, approve: true);

        Assert.Equal(3, subject.Latest.Ordinal);
    }
}
