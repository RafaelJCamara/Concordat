using Concordat.Domain.Registry;
using Concordat.Domain.Tests.TestSupport;

namespace Concordat.Domain.Tests;

/// <summary>
/// M7.4's auto-dismiss: a proposal nobody is proposing any more.
/// </summary>
/// <remarks>
/// The scenario is a CI job, not a person. A team pushes a breaking change, it lands as
/// <see cref="VersionStatus.AwaitingApproval"/>, they think better of it and revert the file.
/// The next pipeline run re-registers the schema that is already deployed — and before this,
/// that request returned early on the idempotency branch without touching anything, leaving a
/// reviewer holding a proposal no repository contained.
/// </remarks>
public class RevertTests
{
    private static (Subject Subject, int Pending) WithPendingProposal()
    {
        var subject = Build.Subject().WithVersion(1).WithVersion(2);

        var breaking = subject.RegisterVersion(
            Build.Schema(3), Build.Breaks(), null, null, Build.Actor(), Build.At);

        Assert.Equal(VersionStatus.AwaitingApproval, breaking.Value.Version.Status);
        Assert.Equal(2, subject.Latest!.Ordinal);

        return (subject, breaking.Value.Version.Ordinal);
    }

    [Fact]
    public void ReRegisteringTheActiveTipDismissesThePendingProposal()
    {
        var (subject, pending) = WithPendingProposal();

        var revert = subject.RegisterVersion(
            Build.Schema(2), Build.Ok(), null, null, Build.Actor(), Build.At.AddHours(1));

        Assert.True(revert.IsSuccess);
        Assert.False(revert.Value.Created);
        Assert.Equal([pending], revert.Value.Dismissed);

        var proposal = subject.Versions.Single(v => v.Ordinal == pending);
        Assert.Equal(VersionStatus.Dismissed, proposal.Status);
        Assert.Equal(Build.At.AddHours(1), proposal.DecidedAt);
    }

    [Fact]
    public void ADismissalNamesNobodyAsTheDecider()
    {
        // The difference that matters between this and a rejection: no reviewer made a
        // judgement, so attributing one would be a lie the audit trail then repeats.
        var (subject, pending) = WithPendingProposal();

        subject.RegisterVersion(Build.Schema(2), Build.Ok(), null, null, Build.Actor(), Build.At);

        var proposal = subject.Versions.Single(v => v.Ordinal == pending);
        Assert.Equal(VersionStatus.Dismissed, proposal.Status);
        Assert.Null(proposal.DecidedBy);
    }

    [Fact]
    public void AnOrdinaryRetryOfTheTipDismissesNothingWhenNothingIsPending()
    {
        // The common case by far. Re-registering the tip must stay a cheap no-op.
        var subject = Build.Subject().WithVersion(1).WithVersion(2);

        var retry = subject.RegisterVersion(
            Build.Schema(2), Build.Ok(), null, null, Build.Actor(), Build.At);

        Assert.True(retry.IsSuccess);
        Assert.False(retry.Value.Created);
        Assert.Empty(retry.Value.Dismissed);
        Assert.Equal(2, subject.Versions.Count);
    }

    [Fact]
    public void AnUnrelatedCompatibleChangeLeavesThePendingProposalAlone()
    {
        // The boundary of the rule. A second, different change is not a withdrawal of the
        // first; discarding a reviewer's queue on an ordinary registration would be worse than
        // leaving it stale.
        var (subject, pending) = WithPendingProposal();

        var next = subject.RegisterVersion(
            Build.Schema(4), Build.Ok(), null, null, Build.Actor(), Build.At.AddHours(1));

        Assert.True(next.Value.Created);
        Assert.Empty(next.Value.Dismissed);
        Assert.Equal(
            VersionStatus.AwaitingApproval,
            subject.Versions.Single(v => v.Ordinal == pending).Status);
    }

    [Fact]
    public void EveryPendingProposalIsDismissedTogether()
    {
        var subject = Build.Subject().WithVersion(1);

        subject.RegisterVersion(Build.Schema(2), Build.Breaks(), null, null, Build.Actor(), Build.At);
        subject.RegisterVersion(Build.Schema(3), Build.Breaks(), null, null, Build.Actor(), Build.At);

        var revert = subject.RegisterVersion(
            Build.Schema(1), Build.Ok(), null, null, Build.Actor(), Build.At);

        Assert.Equal([2, 3], revert.Value.Dismissed);
        Assert.All(
            subject.Versions.Where(v => v.Ordinal > 1),
            v => Assert.Equal(VersionStatus.Dismissed, v.Status));
    }

    [Fact]
    public void ADismissedVersionCanNoLongerBeApprovedOrRejected()
    {
        var (subject, pending) = WithPendingProposal();
        subject.RegisterVersion(Build.Schema(2), Build.Ok(), null, null, Build.Actor(), Build.At);

        var approve = subject.Approve(pending, Build.Actor(), Build.At);
        var reject = subject.Reject(pending, Build.Actor(), Build.At);

        Assert.True(approve.IsFailure);
        Assert.True(reject.IsFailure);
        Assert.Contains("DISMISSED", approve.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADismissedLabelDoesNotBlockTheNextRealOne()
    {
        // A 2.0.0 that was withdrawn must not force every later change past it. Without this,
        // reverting a breaking change would permanently strand the subject's version line.
        var subject = Build.Subject().WithVersion(1, "1.0.0").WithVersion(2, "1.1.0");

        subject.RegisterVersion(
            Build.Schema(3),
            Build.Breaks(),
            SemanticVersion.Create("2.0.0").Value,
            null,
            Build.Actor(),
            Build.At);

        subject.RegisterVersion(Build.Schema(2), Build.Ok(), null, null, Build.Actor(), Build.At);

        var next = subject.RegisterVersion(
            Build.Schema(5),
            Build.Ok(),
            SemanticVersion.Create("1.2.0").Value,
            null,
            Build.Actor(),
            Build.At);

        Assert.True(next.IsSuccess, next.Error?.Message);
    }

    [Fact]
    public void APendingLabelStillBlocksTheNextOne()
    {
        // The counterpart, and the reason the rule is written as "what counts" rather than
        // "what does not": a proposal still under review is still being asked for.
        var subject = Build.Subject().WithVersion(1, "1.0.0");

        subject.RegisterVersion(
            Build.Schema(2),
            Build.Breaks(),
            SemanticVersion.Create("2.0.0").Value,
            null,
            Build.Actor(),
            Build.At);

        var next = subject.RegisterVersion(
            Build.Schema(3),
            Build.Ok(),
            SemanticVersion.Create("1.1.0").Value,
            null,
            Build.Actor(),
            Build.At);

        Assert.True(next.IsFailure);
    }

    [Fact]
    public void DismissPendingIsIdempotent()
    {
        var (subject, _) = WithPendingProposal();

        Assert.Single(subject.DismissPending(Build.At));
        Assert.Empty(subject.DismissPending(Build.At));
    }
}

/// <summary>
/// The revision counter that makes the optimistic-concurrency token engage.
/// </summary>
/// <remarks>
/// PostgreSQL's <c>xmin</c> only changes when the <em>subject</em> row is updated. Editing a
/// child <c>schema_version</c> row alone does not bump it, so before this counter existed
/// <c>Reject</c> — and M7.4's dismissal — would have slipped past the guard entirely and let
/// two concurrent decisions on one subject both commit.
/// </remarks>
public class SubjectRevisionTests
{
    [Fact]
    public void ANewSubjectStartsAtZero() => Assert.Equal(0, Build.Subject().Revision);

    [Fact]
    public void RegisteringAVersionAdvancesIt()
    {
        var subject = Build.Subject();
        subject.WithVersion(1);

        Assert.True(subject.Revision > 0);
    }

    [Fact]
    public void DecidingAProposalAdvancesItEvenThoughOnlyAChildRowChanges()
    {
        // Reject is the case that was silently unguarded: it moves no pointer, so nothing on
        // the root row changed and EF issued no UPDATE to carry the token.
        var subject = Build.Subject().WithVersion(1);
        subject.RegisterVersion(Build.Schema(2), Build.Breaks(), null, null, Build.Actor(), Build.At);

        var before = subject.Revision;
        subject.Reject(2, Build.Actor(), Build.At);

        Assert.True(subject.Revision > before);
    }

    [Fact]
    public void DismissingAdvancesIt()
    {
        var subject = Build.Subject().WithVersion(1);
        subject.RegisterVersion(Build.Schema(2), Build.Breaks(), null, null, Build.Actor(), Build.At);

        var before = subject.Revision;
        subject.DismissPending(Build.At);

        Assert.True(subject.Revision > before);
    }

    [Fact]
    public void EveryOtherMutatorAdvancesItToo()
    {
        var subject = Build.Subject();

        var seen = new List<int>();

        subject.ChangeOwner(Build.Actor("bob"));
        seen.Add(subject.Revision);

        subject.SetContentModel(ContentModel.Closed);
        seen.Add(subject.Revision);

        subject.SetCompatibilityPolicy(Build.BackwardWireJson);
        seen.Add(subject.Revision);

        subject.Deprecate();
        seen.Add(subject.Revision);

        subject.Retire();
        seen.Add(subject.Revision);

        Assert.Equal(seen.Order(), seen);
        Assert.Equal(seen.Distinct().Count(), seen.Count);
    }

    [Fact]
    public void ARefusedMutationDoesNotAdvanceIt()
    {
        // A no-op must not look like a change, or a concurrent caller would be told its read
        // was stale when nothing happened.
        var subject = Build.Subject();
        subject.Retire();

        var after = subject.Revision;
        Assert.True(subject.Deprecate().IsFailure);

        Assert.Equal(after, subject.Revision);
    }
}
