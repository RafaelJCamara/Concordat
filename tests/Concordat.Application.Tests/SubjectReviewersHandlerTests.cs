using Concordat.Application.Registry;
using Concordat.Application.Tests.TestSupport;
using Concordat.Domain.Governance;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;
using Microsoft.Extensions.Time.Testing;

namespace Concordat.Application.Tests;

/// <summary>
/// Naming who may decide a subject's held versions, and the gate that honours it (ADR-043).
/// </summary>
public class SubjectReviewersHandlerTests
{
    private const string Name = "acme.orders.OrderCreated";
    private const string V1 = """{"type":"object","properties":{"id":{"type":"string"}}}""";

    private const string V1Required =
        """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}""";

    private readonly EnvironmentId _environment = EnvironmentId.New();
    private readonly FakeSubjects _subjects = new();
    private readonly FakeSchemas _schemas = new();
    private readonly SettableCaller _caller = new();
    private readonly RecordingUnitOfWork _unitOfWork = new();
    private readonly RecordingAuditLog _audit = new();
    private readonly RecordingOutbox _outbox = new();
    private readonly FakeTimeProvider _clock = new(Build.At);

    private void CallerIs(string actor) =>
        _caller.Current = _caller.Current with { Actor = ActorId.Create(actor).Value };

    private Task<Result<Subject>> SetAsync(string[] reviewers, string name = Name) =>
        new SetSubjectReviewersHandler(_subjects, _caller, _audit, _unitOfWork, _clock).HandleAsync(
            new SetSubjectReviewersCommand(_environment, name, reviewers), CancellationToken.None);

    private Task<Result<Subject>> DecideAsync(int ordinal, bool approve) =>
        new DecideVersionHandler(_subjects, _caller, _audit, _outbox, _unitOfWork, _clock)
            .HandleAsync(
                new DecideVersionCommand(_environment, Name, ordinal, "ignored", approve),
                CancellationToken.None);

    private Subject SeedWithHeldSecondVersion()
    {
        var subject = Build.Subject(_environment);
        subject.Register(_schemas, V1);
        subject.Register(_schemas, V1Required, breaking: true);
        _subjects.Seed(subject);

        return subject;
    }

    // -------------------------------------------------------------------------- setting

    [Fact]
    public async Task ASet_IsAuditedAsTheCallerWithTheOldAndNewSet()
    {
        // The row answers "who could decide this, and who made it so", so it names the caller (never
        // anything in the request) and both sets.
        CallerIs("carol");
        _subjects.Seed(Build.Subject(_environment));
        await SetAsync(["alice@example.com"]);
        _audit.Clear();

        var result = await SetAsync(["bob@example.com", "dana@example.com"]);

        Assert.True(result.IsSuccess, result.Error?.Message);

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal(AuditAction.SubjectReviewersChanged, entry.Action);
        Assert.Equal("carol", entry.Actor.Value);
        Assert.Equal(Name, entry.Target);
        Assert.Equal(_environment, entry.EnvironmentId);
        Assert.Equal("reviewers alice@example.com -> bob@example.com, dana@example.com", entry.Detail);
    }

    [Fact]
    public async Task TheFirstSet_SaysAnyoneWithTheScopeForTheOldOne()
    {
        _subjects.Seed(Build.Subject(_environment));

        await SetAsync(["alice@example.com"]);

        Assert.Equal(
            "reviewers anyone with the scope -> alice@example.com", Assert.Single(_audit.Entries).Detail);
    }

    [Fact]
    public async Task ClearingTheSet_SaysAnyoneWithTheScopeForTheNewOne()
    {
        _subjects.Seed(Build.Subject(_environment));
        await SetAsync(["alice@example.com"]);
        _audit.Clear();

        var result = await SetAsync([]);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Empty(result.Value.Reviewers);
        Assert.Equal(
            "reviewers alice@example.com -> anyone with the scope", Assert.Single(_audit.Entries).Detail);
    }

    [Fact]
    public async Task TheEntryShowsTheSetAsStored_AfterTheAggregateDropsARepeat()
    {
        _subjects.Seed(Build.Subject(_environment));

        await SetAsync(["alice@example.com", "ALICE@example.com", "bob@example.com"]);

        Assert.Equal(
            "reviewers anyone with the scope -> alice@example.com, bob@example.com",
            Assert.Single(_audit.Entries).Detail);
    }

    [Fact]
    public async Task ASet_IsRecordedInTheSameUnitOfWorkAsTheChange()
    {
        _subjects.Seed(Build.Subject(_environment));

        await SetAsync(["alice@example.com"]);

        Assert.Equal(1, _unitOfWork.Saves);
        Assert.Single(_audit.Entries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettingTheSetTheSubjectAlreadyHas_WritesAndSavesNothing(bool empty)
    {
        var subject = Build.Subject(_environment);
        _subjects.Seed(subject);
        string[] current = empty ? [] : ["alice@example.com"];

        if (!empty)
        {
            await SetAsync(current);
            _audit.Clear();
        }

        var savesBefore = _unitOfWork.Saves;
        var result = await SetAsync(current);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Empty(_audit.Entries);
        Assert.Equal(savesBefore, _unitOfWork.Saves);
    }

    [Fact]
    public async Task NobodyIsNotified()
    {
        // Decided (ADR-043): a new notification event is a new vocabulary and, since an empty filter
        // means every event, a message to every subscription that has one. The trail is the record.
        _subjects.Seed(Build.Subject(_environment));

        await SetAsync(["alice@example.com"]);

        Assert.Empty(_outbox.Events);
    }

    [Fact]
    public async Task ABlankEntry_IsRefusedByPositionAndNothingIsRecorded()
    {
        // A form with twenty lines has to be told which one is wrong.
        var subject = Build.Subject(_environment);
        _subjects.Seed(subject);

        var result = await SetAsync(["alice@example.com", "  ", "bob@example.com"]);

        Assert.Equal(ConcordatCodes.ReviewersInvalid, result.Error!.Code);
        Assert.Contains("Reviewer 2", result.Error.Message, StringComparison.Ordinal);
        Assert.Empty(subject.Reviewers);
        Assert.Empty(_audit.Entries);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task AnEntryLongerThanAnActorIdMayBe_IsRefused()
    {
        _subjects.Seed(Build.Subject(_environment));

        var result = await SetAsync([new string('a', ActorId.MaxLength + 1)]);

        Assert.Equal(ConcordatCodes.ReviewersInvalid, result.Error!.Code);
    }

    [Fact]
    public async Task TooManyReviewers_IsRefusedAndNothingIsRecorded()
    {
        _subjects.Seed(Build.Subject(_environment));

        var result = await SetAsync(
            [.. Enumerable.Range(0, Subject.MaxReviewers + 1).Select(i => $"r{i}@example.com")]);

        Assert.Equal(ConcordatCodes.ReviewersInvalid, result.Error!.Code);
        Assert.Empty(_audit.Entries);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task AnUnknownSubject_IsSubjectNotFound()
    {
        var result = await SetAsync(["alice@example.com"]);

        Assert.Equal(ConcordatCodes.SubjectNotFound, result.Error!.Code);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task AnInvalidSubjectName_IsRefusedBeforeTheRepositoryIsTouched()
    {
        var result = await SetAsync(["alice@example.com"], name: "acme..orders");

        Assert.Equal(ConcordatCodes.SubjectNameInvalid, result.Error!.Code);
        Assert.Equal(0, _subjects.Finds);
    }

    [Fact]
    public async Task ARetiredSubject_RefusesItAndRecordsNothing()
    {
        var subject = Build.Subject(_environment);
        Assert.True(subject.Retire().IsSuccess);
        _subjects.Seed(subject);

        var result = await SetAsync(["alice@example.com"]);

        Assert.Equal(ConcordatCodes.LifecycleTransitionInvalid, result.Error!.Code);
        Assert.Empty(_audit.Entries);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    // ------------------------------------------------------------------------- the gate

    [Fact]
    public async Task ANamedReviewer_MayApprove_AsTheCaller()
    {
        SeedWithHeldSecondVersion();
        await SetAsync(["alice@example.com"]);
        _audit.Clear();
        CallerIs("alice@example.com");

        var result = await DecideAsync(2, approve: true);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(2, result.Value.Latest!.Ordinal);
        Assert.Equal(AuditAction.VersionApproved, Assert.Single(_audit.Entries).Action);
    }

    [Fact]
    public async Task AnAdministratorWhoIsNotNamed_IsRefused_AndTheGateRecordsNothing()
    {
        // The credential is sufficient and the rule is the subject's, which is why this is not
        // insufficient_scope: a client that retried with a stronger one would be refused again.
        SeedWithHeldSecondVersion();
        await SetAsync(["alice@example.com"]);
        _audit.Clear();
        var saves = _unitOfWork.Saves;
        CallerIs("mallory@example.com");

        var result = await DecideAsync(2, approve: true);

        Assert.Equal(ConcordatCodes.NotAReviewer, result.Error!.Code);
        Assert.Empty(_audit.Entries);
        Assert.Empty(_outbox.Events);
        Assert.Equal(saves, _unitOfWork.Saves);
    }

    [Fact]
    public async Task ARejection_IsRefusedTheSameWay_AndTheVersionStaysHeld()
    {
        var subject = SeedWithHeldSecondVersion();
        await SetAsync(["alice@example.com"]);
        CallerIs("mallory@example.com");

        var result = await DecideAsync(2, approve: false);

        Assert.Equal(ConcordatCodes.NotAReviewer, result.Error!.Code);
        Assert.Equal(VersionStatus.AwaitingApproval, subject.Versions[1].Status);
    }

    [Fact]
    public async Task WithNoReviewersNamed_TheGateIsAsItWas()
    {
        SeedWithHeldSecondVersion();
        CallerIs("anyone@example.com");

        var result = await DecideAsync(2, approve: true);

        Assert.True(result.IsSuccess, result.Error?.Message);
    }

    [Fact]
    public async Task ClearingTheSet_OpensTheGateAgain()
    {
        SeedWithHeldSecondVersion();
        await SetAsync(["alice@example.com"]);
        await SetAsync([]);
        CallerIs("anyone@example.com");

        var result = await DecideAsync(2, approve: true);

        Assert.True(result.IsSuccess, result.Error?.Message);
    }
}
