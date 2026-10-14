using Concordat.Application.Registry;
using Concordat.Application.Tests.TestSupport;
using Concordat.Domain.Governance;
using Concordat.Domain.Identity;
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
    private readonly RecordingAuditLog _audit = new();
    private readonly RecordingOutbox _outbox = new();
    private readonly SettableBillingGate _billing = new();
    private readonly FakeTimeProvider _clock = new(Build.At);
    private readonly FakeEnvironmentStore _environments = new();
    private readonly SettableCaller _caller = new();

    private Task<Result<Subject>> CreateAsync(
        string name = Name,
        string owner = "alice",
        CompatibilityPolicy? policy = null,
        ContentModel contentModel = ContentModel.Open) =>
        new CreateSubjectHandler(
            _subjects, _environments, _caller, _audit, _billing, _unitOfWork, _clock).HandleAsync(
            new CreateSubjectCommand(
                _environment, name, SchemaFormat.Json, owner, policy, contentModel),
            CancellationToken.None);

    private Task<Result<Subject>> UpdateAsync(
        string name = Name, string? owner = null, bool deprecate = false) =>
        new UpdateSubjectHandler(_subjects, _caller, _audit, _outbox, _unitOfWork, _clock).HandleAsync(
            new UpdateSubjectCommand(_environment, name, owner, deprecate),
            CancellationToken.None);

    private Task<Result<Subject>> SetPolicyAsync(
        CompatibilityPolicy? policy, string name = Name) =>
        new SetCompatibilityPolicyHandler(
            _subjects, _caller, _audit, _outbox, _unitOfWork, _clock).HandleAsync(
            new SetCompatibilityPolicyCommand(_environment, name, policy),
            CancellationToken.None);

    private Task<Result<Subject>> RetireAsync(string name = Name) =>
        new RetireSubjectHandler(_subjects, _caller, _audit, _unitOfWork, _clock).HandleAsync(
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

    [Fact]
    public async Task APlanLimitRefusesCreationAndStagesNothing()
    {
        // The check has to happen before anything is staged. A refused creation that still
        // left a row behind would let an organisation over its limit one subject at a time.
        _billing.Allowed = false;

        var result = await CreateAsync();

        Assert.Equal(ConcordatCodes.PlanLimitReached, result.Error!.Code);
        Assert.Empty(_subjects.Added);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task AClosedEnvironmentRefusesSubjectCreationTooNotJustRegistration()
    {
        // Gating only version registration would let a misconfigured producer fill a closed
        // production environment with empty subjects — permanent clutter, arriving through the
        // exact door the policy exists to shut, reported as a success every time.
        _environments.Seed(_environment, "prod", RegistrationPolicy.Closed);
        _caller.Holding(Scope.SubjectWrite, Scope.Ci);

        var result = await CreateAsync();

        Assert.Equal(ConcordatCodes.RegistrationPolicyForbids, result.Error!.Code);
        Assert.Empty(_subjects.Added);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task ACiOnlyEnvironmentAdmitsAPipelineCreatingASubject()
    {
        _environments.Seed(_environment, "prod", RegistrationPolicy.CiOnly);
        _caller.Holding(Scope.SubjectWrite, Scope.Ci);

        var result = await CreateAsync();

        Assert.True(result.IsSuccess, result.Error?.Message);
    }

    [Fact]
    public async Task APlanLimitIsCheckedAfterTheCheapRefusals()
    {
        // An invalid name should not cost a round trip to the meter, which counts four things.
        await CreateAsync(name: "acme..orders");

        Assert.Equal(0, _billing.Calls);
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
    public async Task AnOwnerChange_IsAuditedAsTheCallerAndNotAsTheNewOwner()
    {
        // The row used to name whoever was *becoming* the owner as the actor, so the trail said
        // that bob had changed the owner to bob. The screen that edits an owner tells the person
        // what is recorded, and that sentence has to be true of this row.
        _caller.Current = _caller.Current with { Actor = ActorId.Create("carol").Value };
        _subjects.Seed(Build.Subject(_environment));

        var result = await UpdateAsync(owner: "bob");

        Assert.True(result.IsSuccess, result.Error?.Message);
        var entry = Assert.Single(_audit.Entries);
        Assert.Equal(AuditAction.SubjectUpdated, entry.Action);
        Assert.Equal("carol", entry.Actor.Value);
        Assert.Equal("owner alice -> bob", entry.Detail);
    }

    [Fact]
    public async Task AnOwnerChange_RecordsTheNewOwnerAsTheRegistryStoredIt()
    {
        // After its trim, and not as typed: the row has to agree with the subject it describes.
        _subjects.Seed(Build.Subject(_environment));

        await UpdateAsync(owner: "  team-after  ");

        Assert.Equal("owner alice -> team-after", Assert.Single(_audit.Entries).Detail);
    }

    [Fact]
    public async Task ChangingTheOwnerAndDeprecating_RecordsBothInOneRow()
    {
        // The row used to say only "deprecated" and lose the owner change made in the same call.
        _subjects.Seed(Build.Subject(_environment));

        await UpdateAsync(owner: "bob", deprecate: true);

        Assert.Equal("owner alice -> bob; deprecated", Assert.Single(_audit.Entries).Detail);
    }

    [Fact]
    public async Task Deprecating_StillReadsDeprecatedAndNamesNoOwner()
    {
        _subjects.Seed(Build.Subject(_environment));

        await UpdateAsync(deprecate: true);

        Assert.Equal("deprecated", Assert.Single(_audit.Entries).Detail);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t \n")]
    public async Task APatchThatChangesNothing_WritesNothingAndSavesNothing(string? owner)
    {
        // It used to write a row reading "owner changed" for `{}`, a null owner and a blank one,
        // none of which changed anything: the trail was told something happened that did not, and
        // an auditor reading it for who changed an owner would find a change nobody made. The same
        // rule a policy change and an idempotent registration already follow. The request is still
        // a success (200), and nothing is staged or saved.
        var subject = Build.Subject(_environment);
        _subjects.Seed(subject);

        var result = await UpdateAsync(owner: owner);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("alice", subject.Owner.Value);
        Assert.Empty(_audit.Entries);
        Assert.Empty(_outbox.Staged);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task AnOwnerChangeAlongsideABlankOwner_IsStillOneEntryForTheDeprecation()
    {
        // The line that must not move: a request that does something is still recorded. Blank
        // owner plus deprecate deprecates, and says so, without inventing an owner change.
        _subjects.Seed(Build.Subject(_environment));

        await UpdateAsync(owner: "  ", deprecate: true);

        Assert.Equal("deprecated", Assert.Single(_audit.Entries).Detail);
    }

    [Fact]
    public async Task SettingTheOwnerASubjectAlreadyHas_StillReadsAsTheRequestThatWasMade()
    {
        // Not part of the no-op rule above, on purpose: an explicit owner is a request, the entry
        // says what was asked (`owner a -> a`, ADR-033), and the form already blocks it. Pinned so
        // that widening "changes nothing" to cover it is a decision and not an accident.
        _subjects.Seed(Build.Subject(_environment));

        await UpdateAsync(owner: "alice");

        Assert.Equal("owner alice -> alice", Assert.Single(_audit.Entries).Detail);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t \n")]
    public async Task ABlankOwner_ChangesNothingAndIsNotACrash(string owner)
    {
        // Whitespace is what the owner rule declines to apply, and an audit row used to be built
        // from that same text: `ActorId.Create("   ")` failed, `.Value` threw, and the endpoint
        // answered 500 for a request it should simply have found nothing to do in.
        var subject = Build.Subject(_environment);
        _subjects.Seed(subject);

        var result = await UpdateAsync(owner: owner);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("alice", subject.Owner.Value);
    }

    [Fact]
    public async Task AnOwnerChange_NotifiesNobody()
    {
        // Only deprecation is staged: it is a signal to every team consuming the subject. The
        // owner-editing screen says nobody is told, and this is what makes that a measured
        // claim and not a reading of the handler.
        _subjects.Seed(Build.Subject(_environment));

        var result = await UpdateAsync(owner: "bob");

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Empty(_outbox.Staged);
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

    // ------------------------------------------------------------------ policy

    private static readonly CompatibilityPolicy FullSource =
        new(CompatibilityMode.Full, CompatibilitySurface.Source);

    private static readonly CompatibilityPolicy BackwardWire =
        new(CompatibilityMode.Backward, CompatibilitySurface.Wire);

    [Fact]
    public async Task APolicyChange_IsAuditedAsTheCallerWithTheOldAndNewPolicy()
    {
        // The subject page's policy confirmation says exactly this, so it is pinned here and not
        // left to a reading of the handler. The trail answers "who made this stricter, and when"
        // only if it names the caller (never anything in the request) and both policies.
        _caller.Current = _caller.Current with { Actor = ActorId.Create("carol").Value };
        _subjects.Seed(Build.Subject(_environment, policy: BackwardWire));

        var result = await SetPolicyAsync(FullSource);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(FullSource, result.Value.CompatibilityPolicy);

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal(AuditAction.SubjectPolicyChanged, entry.Action);
        Assert.Equal("carol", entry.Actor.Value);
        Assert.Equal(Name, entry.Target);
        Assert.Equal(_environment, entry.EnvironmentId);
        Assert.Equal("policy BACKWARD/WIRE -> FULL/SOURCE", entry.Detail);
        Assert.Equal(1, _unitOfWork.Saves);
    }

    [Fact]
    public async Task PinningASubjectThatInherits_SaysInheritedForTheOldPolicy()
    {
        _subjects.Seed(Build.Subject(_environment));

        await SetPolicyAsync(FullSource);

        Assert.Equal("policy inherited -> FULL/SOURCE", Assert.Single(_audit.Entries).Detail);
    }

    [Fact]
    public async Task ClearingAPolicy_SaysInheritedForTheNewOne()
    {
        _subjects.Seed(Build.Subject(_environment, policy: FullSource));

        var result = await SetPolicyAsync(null);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Null(result.Value.CompatibilityPolicy);
        Assert.Equal("policy FULL/SOURCE -> inherited", Assert.Single(_audit.Entries).Detail);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettingThePolicyTheSubjectAlreadyHas_WritesNothingToTheTrail(bool inheriting)
    {
        // Answered as before (200, the policy), but the trail is not told that something happened
        // which did not: an entry reading "FULL/SOURCE -> FULL/SOURCE" would bury the changes
        // somebody opens the log to find.
        CompatibilityPolicy? current = inheriting ? null : FullSource;
        var subject = Build.Subject(_environment, policy: current);
        _subjects.Seed(subject);

        var result = await SetPolicyAsync(current);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Empty(_audit.Entries);
        Assert.Equal(current, subject.CompatibilityPolicy);
    }

    [Fact]
    public async Task APolicyChange_IsRecordedInTheSameUnitOfWorkAsTheChange()
    {
        // One SaveChanges carries both, so the entry cannot outlive a rolled-back change or be
        // lost while the change survives. The entry is appended before the save and not after.
        _subjects.Seed(Build.Subject(_environment));
        var appendedAtSave = -1;
        _unitOfWork.OnSave = () => appendedAtSave = _audit.Entries.Count;

        await SetPolicyAsync(FullSource);

        Assert.Equal(1, appendedAtSave);
    }

    [Fact]
    public async Task ARetiredSubject_RefusesAPolicyChangeAndRecordsNothing()
    {
        // Refused before anything is recorded: a refused request never reaches a commit, and the
        // trail "records what happened, not what was attempted".
        var subject = Build.Subject(_environment, policy: BackwardWire);
        Assert.True(subject.Retire().IsSuccess);
        _subjects.Seed(subject);

        var result = await SetPolicyAsync(FullSource);

        Assert.Equal(ConcordatCodes.LifecycleTransitionInvalid, result.Error!.Code);
        Assert.Equal(BackwardWire, subject.CompatibilityPolicy);
        Assert.Empty(_audit.Entries);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task ADeprecatedSubject_TakesAPolicyChange()
    {
        var subject = Build.Subject(_environment);
        Assert.True(subject.Deprecate().IsSuccess);
        _subjects.Seed(subject);

        var result = await SetPolicyAsync(FullSource);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(SubjectLifecycle.Deprecated, subject.Lifecycle);
        Assert.Single(_audit.Entries);
    }

    [Fact]
    public async Task ChangingThePolicyOfAnUnknownSubject_IsSubjectNotFoundAndRecordsNothing()
    {
        var result = await SetPolicyAsync(FullSource);

        Assert.Equal(ConcordatCodes.SubjectNotFound, result.Error!.Code);
        Assert.Empty(_audit.Entries);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task ChangingThePolicyOfAnInvalidName_IsRefusedBeforeTheRepositoryIsTouched()
    {
        var result = await SetPolicyAsync(FullSource, name: "acme.orders.Order-Created");

        Assert.Equal(ConcordatCodes.SubjectNameInvalid, result.Error!.Code);
        Assert.Equal(0, _subjects.Finds);
    }

    [Fact]
    public async Task APolicyChange_IsNotAnOwnerChangeOrADeprecation()
    {
        // SUBJECT_UPDATED stays the row for an owner change and a deprecation. Reusing it for a
        // policy change would make a filter on it return all three, which is the whole reason the
        // policy has an action of its own (ADR-033).
        _subjects.Seed(Build.Subject(_environment));

        await SetPolicyAsync(FullSource);

        Assert.DoesNotContain(AuditAction.SubjectUpdated, _audit.Actions);
    }

    // ------------------------------------------------- policy: the notification (ADR-034)

    [Fact]
    public async Task APolicyChange_StagesOneNotificationForTheSubjectInItsEnvironment()
    {
        // The reversal of ADR-033's "No notification": a team reading this subject may now be
        // judged more laxly or strictly than the one it built against, which is what an alert is
        // for. One message, about this subject, in this environment, stamped by the injected clock.
        var moment = new DateTimeOffset(2029, 11, 4, 6, 7, 8, TimeSpan.FromHours(-5));
        _clock.SetUtcNow(moment);
        _subjects.Seed(Build.Subject(_environment, policy: BackwardWire));

        var result = await SetPolicyAsync(FullSource);

        Assert.True(result.IsSuccess, result.Error?.Message);

        var message = Assert.Single(_outbox.Staged);
        Assert.Equal(NotificationEvent.SubjectPolicyChanged, message.Event);
        Assert.Equal(Name, message.Target);
        Assert.Equal(_environment, message.EnvironmentId);
        Assert.Equal(moment.ToUniversalTime(), message.OccurredAt);
    }

    [Fact]
    public async Task ThePolicyChangeNotification_SaysWhichSubjectWhoAndTheOldAndNewPolicy()
    {
        _caller.Current = _caller.Current with { Actor = ActorId.Create("carol").Value };
        _subjects.Seed(Build.Subject(_environment, policy: BackwardWire));

        await SetPolicyAsync(FullSource);

        Assert.Equal(
            "The compatibility policy of acme.orders.OrderCreated was changed by carol: " +
            "BACKWARD/WIRE -> FULL/SOURCE. " +
            "(\"inherited\" means the environment's default applies.) " +
            "Versions already registered are not judged again.",
            Assert.Single(_outbox.Staged).Body);
    }

    [Theory]
    [InlineData(false, "inherited -> FULL/SOURCE")]
    [InlineData(true, "FULL/SOURCE -> inherited")]
    public async Task ThePolicyChangeNotification_SpellsInheritedAsTheAuditEntryDoes(
        bool clearing, string expected)
    {
        // One spelling in two places: the message reuses the audit detail's own function, so a
        // subscriber and an auditor read the same words for the same change.
        _subjects.Seed(Build.Subject(_environment, policy: clearing ? FullSource : null));

        await SetPolicyAsync(clearing ? null : FullSource);

        var entry = Assert.Single(_audit.Entries);
        var message = Assert.Single(_outbox.Staged);

        Assert.Equal($"policy {expected}", entry.Detail);
        Assert.Contains($": {expected}.", message.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePolicyChangeNotification_NamesTheCallerAndNeverAnythingInTheRequest()
    {
        // The approval, rejection, promotion and breaking-registration messages end "by <actor>",
        // and a team told its contract is now judged differently asks who. The deprecation message
        // is the one that names nobody, and it is advisory. The name is the audit entry's own: the
        // authenticated caller (the command has no actor to take another from), so the two cannot
        // disagree about who did it (ADR-034).
        _caller.Current = _caller.Current with { Actor = ActorId.Create("carol").Value };
        _subjects.Seed(Build.Subject(_environment));

        await SetPolicyAsync(FullSource);

        var entry = Assert.Single(_audit.Entries);
        Assert.Contains(
            $"changed by {entry.Actor.Value}:", Assert.Single(_outbox.Staged).Body, StringComparison.Ordinal);
        Assert.Equal("carol", entry.Actor.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettingThePolicyTheSubjectAlreadyHas_StagesNothing(bool inheriting)
    {
        // A change that changes nothing tells nobody anything: the guard that keeps the audit
        // trail honest is the one that keeps a subscriber from being alerted to a non-event.
        CompatibilityPolicy? current = inheriting ? null : FullSource;
        _subjects.Seed(Build.Subject(_environment, policy: current));

        var result = await SetPolicyAsync(current);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Empty(_outbox.Staged);
    }

    [Fact]
    public async Task APolicyChange_StagesItsNotificationInTheSameUnitOfWorkAsTheAuditEntry()
    {
        // The outbox's whole point: the notification rides the same SaveChanges as the change and
        // its entry, so there is no "your policy changed" for a change that rolled back and no
        // change whose notification was lost.
        _subjects.Seed(Build.Subject(_environment));
        var auditedAtSave = -1;
        var stagedAtSave = -1;
        _unitOfWork.OnSave = () =>
        {
            auditedAtSave = _audit.Entries.Count;
            stagedAtSave = _outbox.Staged.Count;
        };

        await SetPolicyAsync(FullSource);

        Assert.Equal(1, auditedAtSave);
        Assert.Equal(1, stagedAtSave);
        Assert.Equal(1, _unitOfWork.Saves);
    }

    [Fact]
    public async Task ARetiredSubject_RefusesAPolicyChangeAndStagesNothing()
    {
        var subject = Build.Subject(_environment, policy: BackwardWire);
        Assert.True(subject.Retire().IsSuccess);
        _subjects.Seed(subject);

        var result = await SetPolicyAsync(FullSource);

        Assert.Equal(ConcordatCodes.LifecycleTransitionInvalid, result.Error!.Code);
        Assert.Empty(_outbox.Staged);
    }

    [Fact]
    public async Task APolicyChangeThatIsRefusedForAnUnknownOrInvalidSubject_StagesNothing()
    {
        await SetPolicyAsync(FullSource);
        await SetPolicyAsync(FullSource, name: "acme.orders.Order-Created");

        Assert.Empty(_outbox.Staged);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task ADeprecatedSubject_StagesAPolicyChangeNotificationAndNotADeprecationOne()
    {
        // The two events stay separate: a policy change on a deprecated subject is a policy
        // change, and must not be mistaken for (or suppressed by) the deprecation's own event.
        var subject = Build.Subject(_environment);
        Assert.True(subject.Deprecate().IsSuccess);
        _subjects.Seed(subject);

        await SetPolicyAsync(FullSource);

        Assert.Equal([NotificationEvent.SubjectPolicyChanged], _outbox.Events);
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
    public async Task ARetirement_IsRecordedAsTheCaller_NotAsTheOwnerOfTheSubjectItEnds()
    {
        // The defect UpdateSubjectHandler had until ADR-033: the row's actor was the subject's
        // *owner*, so retiring someone else's contract was attributed to them. An audit trail that
        // names the wrong person for the one change that cannot be undone is worse than none.
        _caller.Current = _caller.Current with { Actor = ActorId.Create("carol").Value };
        _subjects.Seed(Build.Subject(_environment));

        var result = await RetireAsync();

        Assert.True(result.IsSuccess, result.Error?.Message);

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal(AuditAction.SubjectRetired, entry.Action);
        Assert.Equal("carol", entry.Actor.Value);
        Assert.NotEqual(result.Value.Owner.Value, entry.Actor.Value);
        Assert.Equal(Name, entry.Target);
        Assert.Equal(1, _unitOfWork.Saves);
    }

    [Fact]
    public async Task ARefusedRetirement_RecordsNothing()
    {
        // Records what happened, not what was attempted: the second retirement is refused and
        // never reaches a commit, so the trail holds exactly one row.
        _subjects.Seed(Build.Subject(_environment));

        Assert.True((await RetireAsync()).IsSuccess);
        Assert.False((await RetireAsync()).IsSuccess);

        Assert.Single(_audit.Entries);
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
