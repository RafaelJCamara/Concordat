using Concordat.Domain.Registry;
using Concordat.Domain.Results;
using Concordat.Domain.Tests.TestSupport;

namespace Concordat.Domain.Tests;

/// <summary>
/// A subject can name who may decide its held versions (ADR-043). An empty set is what every
/// subject had before and must keep meaning "the scope alone decides".
/// </summary>
public class SubjectReviewerTests
{
    private static ActorId Actor(string value) => ActorId.Create(value).Value;

    private static IReadOnlyList<ActorId> Set(params string[] values) =>
        [.. values.Select(Actor)];

    /// <summary>A subject holding version 2 at the gate, with the given reviewers named.</summary>
    private static Subject Held(params string[] reviewers)
    {
        var subject = Build.Subject().WithVersion(1);

        var proposed = subject.RegisterVersion(
            Build.Schema(2), Build.Breaks(), null, null, Build.Actor("proposer"), Build.At);
        Assert.Equal(VersionStatus.AwaitingApproval, proposed.Value.Version.Status);

        if (reviewers.Length > 0)
        {
            Assert.True(subject.SetReviewers(Set(reviewers)).IsSuccess);
        }

        return subject;
    }

    // -------------------------------------------------------------------------- the default

    [Fact]
    public void ANewSubjectNamesNoReviewers()
    {
        Assert.Empty(Build.Subject().Reviewers);
    }

    [Fact]
    public void WithNoReviewers_AnyoneMayDecide_AsEveryAdministratorAlwaysCould()
    {
        var subject = Held();

        Assert.True(subject.MayDecide(Actor("anyone@example.com")));
        Assert.True(subject.Approve(2, Actor("anyone@example.com"), Build.At).IsSuccess);
    }

    // ---------------------------------------------------------------------------- deciding

    [Fact]
    public void WithReviewers_ANamedActorMayApprove()
    {
        var subject = Held("alice@example.com");

        var approved = subject.Approve(2, Actor("alice@example.com"), Build.At);

        Assert.True(approved.IsSuccess, approved.Error?.Message);
        Assert.Equal(2, subject.Latest!.Ordinal);
    }

    [Fact]
    public void WithReviewers_ANamedActorMayReject()
    {
        var subject = Held("alice@example.com");

        var rejected = subject.Reject(2, Actor("alice@example.com"), Build.At);

        Assert.True(rejected.IsSuccess, rejected.Error?.Message);
        Assert.Equal(VersionStatus.Rejected, subject.Versions[1].Status);
    }

    [Fact]
    public void WithReviewers_AnyoneElseMayNotApprove_AndNothingMoves()
    {
        var subject = Held("alice@example.com");
        var revision = subject.Revision;

        var approved = subject.Approve(2, Actor("mallory@example.com"), Build.At);

        Assert.True(approved.IsFailure);
        Assert.Equal(ConcordatCodes.NotAReviewer, approved.Error!.Code);
        Assert.Equal(1, subject.Latest!.Ordinal);
        Assert.Equal(VersionStatus.AwaitingApproval, subject.Versions[1].Status);
        Assert.Equal(revision, subject.Revision);
    }

    [Fact]
    public void WithReviewers_AnyoneElseMayNotReject_AndNothingMoves()
    {
        var subject = Held("alice@example.com");

        var rejected = subject.Reject(2, Actor("mallory@example.com"), Build.At);

        Assert.True(rejected.IsFailure);
        Assert.Equal(ConcordatCodes.NotAReviewer, rejected.Error!.Code);
        Assert.Equal(VersionStatus.AwaitingApproval, subject.Versions[1].Status);
    }

    [Fact]
    public void TheProposerIsNotExcluded_WhenTheyAreAmongTheReviewers()
    {
        // Decided, not forgotten (ADR-043): a one-person team that names itself as the reviewer
        // would otherwise lock itself out. Independence is a policy to add on top, not a default.
        var subject = Held("proposer");

        Assert.True(subject.Approve(2, Actor("proposer"), Build.At).IsSuccess);
    }

    [Fact]
    public void ARefusedCallerLearnsNothingAboutTheVersion()
    {
        // Version 9 does not exist. A caller who may not decide is told that, and not which of the
        // two things was wrong, so the rule cannot be used to probe for held versions.
        var subject = Held("alice@example.com");

        var outcome = subject.Approve(9, Actor("mallory@example.com"), Build.At);

        Assert.Equal(ConcordatCodes.NotAReviewer, outcome.Error!.Code);
    }

    [Fact]
    public void ANamedActorStillGetsTheOrdinaryErrorForAVersionThatIsNotHeld()
    {
        var subject = Held("alice@example.com");

        var outcome = subject.Approve(1, Actor("alice@example.com"), Build.At);

        Assert.Equal(ConcordatCodes.VersionNotAwaitingApproval, outcome.Error!.Code);
    }

    // --------------------------------------------------------------------------- comparison

    [Theory]
    [InlineData("alice@example.com", "ALICE@EXAMPLE.COM")]
    [InlineData("Alice@Example.com", "alice@example.com")]
    public void AReviewerIsComparedWithoutRegardToCase_BecauseAnAddressIsTheSamePerson(
        string named, string deciding)
    {
        var subject = Held(named);

        Assert.True(subject.MayDecide(Actor(deciding)));
    }

    [Fact]
    public void ADifferentActorWhoSharesAPrefixIsNotAReviewer()
    {
        var subject = Held("alice@example.com");

        Assert.False(subject.MayDecide(Actor("alice@example.co")));
        Assert.False(subject.MayDecide(Actor("malice@example.com")));

        // The direction that matters: an actor whose name merely begins with a reviewer's.
        Assert.False(subject.MayDecide(Actor("alice@example.com.evil.net")));
        Assert.False(subject.MayDecide(Actor("alice@example.comx")));
    }

    // ---------------------------------------------------------------------------- setting

    [Fact]
    public void ASetIsKeptAsWritten_InTheOrderGiven()
    {
        var subject = Build.Subject();

        var changed = subject.SetReviewers(Set("Bob@Example.com", "alice@example.com"));

        Assert.True(changed.IsSuccess);
        Assert.True(changed.Value);
        Assert.Equal(["Bob@Example.com", "alice@example.com"], subject.Reviewers.Select(r => r.Value));
    }

    [Fact]
    public void ARepeatInAnyCaseIsDropped_AndTheFirstSpellingStays()
    {
        var subject = Build.Subject();

        subject.SetReviewers(Set("alice@example.com", "bob@example.com", "ALICE@example.com"));

        Assert.Equal(["alice@example.com", "bob@example.com"], subject.Reviewers.Select(r => r.Value));
    }

    [Fact]
    public void ASetChangesTheRevision_SoTheRootRowIsWrittenAndTheConcurrencyTokenMoves()
    {
        var subject = Build.Subject();
        var before = subject.Revision;

        subject.SetReviewers(Set("alice@example.com"));

        Assert.Equal(before + 1, subject.Revision);
    }

    [Fact]
    public void TheSameSetAgain_ChangesNothing_AndSaysSo()
    {
        var subject = Build.Subject();
        subject.SetReviewers(Set("alice@example.com", "bob@example.com"));
        var revision = subject.Revision;

        var again = subject.SetReviewers(Set("alice@example.com", "bob@example.com"));

        Assert.True(again.IsSuccess);
        Assert.False(again.Value);
        Assert.Equal(revision, subject.Revision);
    }

    [Fact]
    public void TheSameNamesInAnotherOrder_AreAChange()
    {
        // Order is what the page shows, so it is part of what was set.
        var subject = Build.Subject();
        subject.SetReviewers(Set("alice@example.com", "bob@example.com"));

        var reordered = subject.SetReviewers(Set("bob@example.com", "alice@example.com"));

        Assert.True(reordered.Value);
    }

    [Fact]
    public void AnEmptySetClearsIt_SoAnyAdministratorMayDecideAgain()
    {
        var subject = Held("alice@example.com");

        var cleared = subject.SetReviewers([]);

        Assert.True(cleared.Value);
        Assert.Empty(subject.Reviewers);
        Assert.True(subject.MayDecide(Actor("mallory@example.com")));
    }

    [Fact]
    public void ClearingAnEmptySet_ChangesNothing()
    {
        var subject = Build.Subject();

        Assert.False(subject.SetReviewers([]).Value);
    }

    [Fact]
    public void ASetOfTwentyIsAccepted_AndOneMoreIsNot()
    {
        var subject = Build.Subject();
        var twenty = Enumerable.Range(0, Subject.MaxReviewers).Select(i => $"r{i}@example.com").ToArray();

        Assert.True(subject.SetReviewers(Set(twenty)).IsSuccess);

        var tooMany = subject.SetReviewers(Set([.. twenty, "extra@example.com"]));

        Assert.True(tooMany.IsFailure);
        Assert.Equal(ConcordatCodes.ReviewersInvalid, tooMany.Error!.Code);
        Assert.Equal(Subject.MaxReviewers, subject.Reviewers.Count);
    }

    [Fact]
    public void TheLimitCountsDistinctReviewers_NotRepeats()
    {
        var subject = Build.Subject();
        var repeated = Enumerable.Range(0, Subject.MaxReviewers * 2).Select(_ => "alice@example.com").ToArray();

        Assert.True(subject.SetReviewers(Set(repeated)).IsSuccess);
        Assert.Single(subject.Reviewers);
    }

    [Fact]
    public void ARetiredSubjectCannotHaveItsReviewersChanged()
    {
        var subject = Build.Subject();
        Assert.True(subject.Retire().IsSuccess);

        var outcome = subject.SetReviewers(Set("alice@example.com"));

        Assert.True(outcome.IsFailure);
        Assert.Equal(ConcordatCodes.LifecycleTransitionInvalid, outcome.Error!.Code);
        Assert.Empty(subject.Reviewers);
    }

    [Fact]
    public void AFailedSet_LeavesTheSetAsItWas()
    {
        var subject = Build.Subject();
        subject.SetReviewers(Set("alice@example.com"));

        var tooMany = subject.SetReviewers(
            Set(Enumerable.Range(0, Subject.MaxReviewers + 1).Select(i => $"r{i}@example.com").ToArray()));

        Assert.True(tooMany.IsFailure);
        Assert.Equal(["alice@example.com"], subject.Reviewers.Select(r => r.Value));
    }
}
