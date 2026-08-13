using Concordat.Application.Registry;
using Concordat.Application.Tests.TestSupport;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;
using Microsoft.Extensions.Time.Testing;

namespace Concordat.Application.Tests;

/// <summary>Creating a subject, changing it, and retiring it — the refusals in each.</summary>
public class SubjectCommandHandlerTests
{
    private const string Name = "acme.orders.OrderCreated";

    private readonly EnvironmentId _environment = EnvironmentId.New();
    private readonly FakeSubjects _subjects = new();
    private readonly FakeSchemas _schemas = new();
    private readonly RecordingUnitOfWork _unitOfWork = new();
    private readonly FakeTimeProvider _clock = new(Build.At);

    private Task<Result<Subject>> CreateAsync(
        string name = Name,
        string owner = "alice",
        CompatibilityPolicy? policy = null,
        ContentModel contentModel = ContentModel.Open) =>
        new CreateSubjectHandler(_subjects, _unitOfWork, _clock).HandleAsync(
            new CreateSubjectCommand(
                _environment, name, SchemaFormat.Json, owner, policy, contentModel),
            CancellationToken.None);

    private Task<Result<Subject>> UpdateAsync(
        string name = Name, string? owner = null, bool deprecate = false) =>
        new UpdateSubjectHandler(_subjects, _unitOfWork).HandleAsync(
            new UpdateSubjectCommand(_environment, name, owner, deprecate),
            CancellationToken.None);

    private Task<Result<Subject>> RetireAsync(string name = Name) =>
        new RetireSubjectHandler(_subjects, _unitOfWork).HandleAsync(
            new RetireSubjectCommand(_environment, name), CancellationToken.None);

    // ------------------------------------------------------------------ create

    [Fact]
    public async Task AnInvalidName_IsRefusedBeforeTheRepositoryIsTouched()
    {
        var result = await CreateAsync(name: "acme..orders");

        Assert.Equal(ConcordatCodes.SubjectNameInvalid, result.Error!.Code);
        Assert.Equal(0, _subjects.Finds);
        Assert.Empty(_subjects.Added);
    }

    [Fact]
    public async Task AnEmptyOwner_IsRefusedBeforeTheRepositoryIsTouched()
    {
        // The owner is who gets paged when this contract breaks. A subject with a blank one is
        // a contract nobody is accountable for.
        var result = await CreateAsync(owner: "  ");

        Assert.Equal(ConcordatCodes.ActorIdInvalid, result.Error!.Code);
        Assert.Equal(0, _subjects.Finds);
    }

    [Fact]
    public async Task ADuplicateName_IsSubjectAlreadyExistsAndStagesNothing()
    {
        _subjects.Seed(Build.Subject(_environment));

        var result = await CreateAsync();

        Assert.Equal(ConcordatCodes.SubjectAlreadyExists, result.Error!.Code);
        Assert.Empty(_subjects.Added);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task TheSameNameInAnotherEnvironment_IsNotADuplicate()
    {
        // Environments are isolation, not decoration (ADR-012). Two teams running the same
        // service in staging and production must not collide on subject names.
        _subjects.Seed(Build.Subject(EnvironmentId.New()));

        var result = await CreateAsync();

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Single(_subjects.Added);
    }

    [Fact]
    public async Task ANullPolicyIsStoredAsNullSoTheEnvironmentDefaultKeepsApplying()
    {
        // Copying the default in at creation would permanently destroy the distinction between
        // "explicitly configured this way" and "following the environment", and there would be
        // no data left from which to reconstruct it.
        var result = await CreateAsync(policy: null);

        Assert.Null(result.Value.CompatibilityPolicy);

        var environmentDefault =
            new CompatibilityPolicy(CompatibilityMode.Full, CompatibilitySurface.Source);
        Assert.Equal(environmentDefault, result.Value.EffectivePolicy(environmentDefault));
    }

    [Fact]
    public async Task AnExplicitPolicySurvivesAChangeToTheEnvironmentDefault()
    {
        var explicitPolicy =
            new CompatibilityPolicy(CompatibilityMode.Forward, CompatibilitySurface.Wire);

        var result = await CreateAsync(policy: explicitPolicy);

        Assert.Equal(
            explicitPolicy, result.Value.EffectivePolicy(CompatibilityPolicy.Default));
    }

    [Fact]
    public async Task TheCreationTimestampComesFromTheInjectedClock()
    {
        var moment = new DateTimeOffset(2029, 11, 4, 6, 7, 8, TimeSpan.FromHours(-5));
        _clock.SetUtcNow(moment);

        var result = await CreateAsync();

        Assert.Equal(moment.ToUniversalTime(), result.Value.CreatedAt);
    }

    [Fact]
    public async Task TheContentModelIsWhateverTheCallerAskedForNotInferred()
    {
        var result = await CreateAsync(contentModel: ContentModel.Closed);

        Assert.Equal(ContentModel.Closed, result.Value.ContentModel);
    }

    // ------------------------------------------------------------------ update

    [Fact]
    public async Task UpdatingAnInvalidName_IsRefusedBeforeTheRepositoryIsTouched()
    {
        var result = await UpdateAsync(name: "acme.orders.Order-Created");

        Assert.Equal(ConcordatCodes.SubjectNameInvalid, result.Error!.Code);
        Assert.Equal(0, _subjects.Finds);
    }

    [Fact]
    public async Task UpdatingAnUnknownSubject_IsSubjectNotFound()
    {
        var result = await UpdateAsync(owner: "bob");

        Assert.Equal(ConcordatCodes.SubjectNotFound, result.Error!.Code);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task AnOverlongOwner_IsRefusedAndCommitsNothing()
    {
        var subject = Build.Subject(_environment);
        _subjects.Seed(subject);

        var result = await UpdateAsync(owner: new string('o', ActorId.MaxLength + 1));

        Assert.Equal(ConcordatCodes.ActorIdInvalid, result.Error!.Code);
        Assert.Equal("alice", subject.Owner.Value);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task AnOmittedOwnerLeavesTheCurrentOneAlone()
    {
        // A PATCH that only deprecates must not blank the owner, and null is the only way the
        // command can say "not changing this".
        var subject = Build.Subject(_environment);
        _subjects.Seed(subject);

        var result = await UpdateAsync(owner: null, deprecate: true);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("alice", subject.Owner.Value);
        Assert.Equal(SubjectLifecycle.Deprecated, subject.Lifecycle);
    }

    [Fact]
    public async Task ARetiredSubject_RefusesAnOwnerChange()
    {
        var subject = Build.Subject(_environment);
        Assert.True(subject.Retire().IsSuccess);
        _subjects.Seed(subject);

        var result = await UpdateAsync(owner: "bob");

        Assert.Equal(ConcordatCodes.LifecycleTransitionInvalid, result.Error!.Code);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task ARetiredSubject_RefusesDeprecation()
    {
        // Retired is terminal. Letting it slide back to Deprecated would make the soft delete
        // reversible by accident, and the whole point of a terminal state is that consumers
        // can trust it.
        var subject = Build.Subject(_environment);
        Assert.True(subject.Retire().IsSuccess);
        _subjects.Seed(subject);

        var result = await UpdateAsync(deprecate: true);

        Assert.Equal(ConcordatCodes.LifecycleTransitionInvalid, result.Error!.Code);
        Assert.Equal(SubjectLifecycle.Retired, subject.Lifecycle);
    }

    [Fact]
    public async Task DeprecationIsAdvisoryAndDoesNotBlockRegistration()
    {
        // A deprecated subject still accepts versions so existing producers can patch their
        // contract while they migrate. Only retirement stops registration.
        var subject = Build.Subject(_environment);
        _subjects.Seed(subject);

        await UpdateAsync(deprecate: true);
        subject.Register(_schemas, """{"type":"object"}""");

        Assert.Equal(SubjectLifecycle.Deprecated, subject.Lifecycle);
        Assert.Single(subject.Versions);
    }

    // ------------------------------------------------------------------ retire

    [Fact]
    public async Task RetiringAnInvalidName_IsRefusedBeforeTheRepositoryIsTouched()
    {
        var result = await RetireAsync(name: "");

        Assert.Equal(ConcordatCodes.SubjectNameInvalid, result.Error!.Code);
        Assert.Equal(0, _subjects.Finds);
    }

    [Fact]
    public async Task RetiringAnUnknownSubject_IsSubjectNotFound()
    {
        var result = await RetireAsync();

        Assert.Equal(ConcordatCodes.SubjectNotFound, result.Error!.Code);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task RetiringTwice_IsRefusedRatherThanTreatedAsIdempotent()
    {
        // Not a harmless no-op: a second retirement means the caller believes the subject was
        // still live, and telling them it worked hides whatever led them there.
        var subject = Build.Subject(_environment);
        _subjects.Seed(subject);

        Assert.True((await RetireAsync()).IsSuccess);
        var second = await RetireAsync();

        Assert.Equal(ConcordatCodes.LifecycleTransitionInvalid, second.Error!.Code);
        Assert.Equal(1, _unitOfWork.Saves);
    }

    [Fact]
    public async Task RetirementKeepsTheVersionsRatherThanDeletingThem()
    {
        // There is no hard delete (ADR-015). A consumer holding a schema id from last week must
        // still be able to resolve it, or retiring a subject becomes an outage.
        var subject = Build.Subject(_environment);
        subject.Register(_schemas, """{"type":"object"}""");
        _subjects.Seed(subject);

        var result = await RetireAsync();

        Assert.Equal(SubjectLifecycle.Retired, result.Value.Lifecycle);
        Assert.Single(result.Value.Versions);
        Assert.NotNull(result.Value.Latest);
    }
}
