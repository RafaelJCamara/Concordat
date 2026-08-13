using Concordat.Domain.Registry;
using Concordat.Domain.Results;
using Concordat.Domain.Tests.TestSupport;

namespace Concordat.Domain.Tests;

public class RegisterVersionTests
{
    [Fact]
    public void FirstVersion_IsActiveAndMovesLatestToOrdinalOne()
    {
        var subject = Build.Subject();

        var outcome = subject.RegisterVersion(
            Build.Schema(1), Build.Ok(), null, null, Build.Actor(), Build.At);

        Assert.True(outcome.IsSuccess);
        Assert.True(outcome.Value.Created);
        Assert.Equal(1, outcome.Value.Version.Ordinal);
        Assert.Equal(VersionStatus.Active, outcome.Value.Version.Status);
        Assert.Equal(1, subject.Latest!.Ordinal);
    }

    [Fact]
    public void FirstVersion_ReportedBreaking_IsRejected()
    {
        // There is no predecessor, so "breaking" is meaningless and signals a caller bug.
        var subject = Build.Subject();

        var outcome = subject.RegisterVersion(
            Build.Schema(1), Build.Breaks(), null, null, Build.Actor(), Build.At);

        Assert.True(outcome.IsFailure);
        Assert.Equal(ConcordatCodes.FirstVersionCannotBreak, outcome.Error!.Code);
        Assert.Empty(subject.Versions);
        Assert.Null(subject.Latest);
    }

    [Fact]
    public void Ordinals_AreContiguousAndMonotonicFromOne()
    {
        var subject = Build.Subject().WithVersion(1).WithVersion(2).WithVersion(3);

        Assert.Equal([1, 2, 3], subject.Versions.Select(v => v.Ordinal));
    }

    [Fact]
    public void DifferentFormat_IsRejected()
    {
        var subject = Build.Subject(format: SchemaFormat.Json).WithVersion(1);

        var outcome = subject.RegisterVersion(
            Build.Schema(2, SchemaFormat.Avro), Build.Ok(), null, null, Build.Actor(), Build.At);

        Assert.True(outcome.IsFailure);
        Assert.Equal(ConcordatCodes.FormatMismatch, outcome.Error!.Code);
        Assert.Single(subject.Versions);
    }

    [Fact]
    public void ReRegisteringTheTipSchema_IsIdempotentAndAllocatesNoOrdinal()
    {
        var subject = Build.Subject().WithVersion(1);

        var outcome = subject.RegisterVersion(
            Build.Schema(1), Build.Ok(), null, null, Build.Actor(), Build.At);

        Assert.True(outcome.IsSuccess);
        Assert.False(outcome.Value.Created);
        Assert.Single(subject.Versions);
        Assert.Equal(1, subject.Latest!.Ordinal);
    }

    [Fact]
    public void VerdictEvaluatedUnderADifferentPolicy_IsRejected()
    {
        // The aggregate cannot recompute the verdict, but it can refuse one computed against a
        // policy that is not this subject's.
        var subject = Build.Subject(Build.BackwardWireJson).WithVersion(1);
        var wrongPolicy = new CompatibilityPolicy(
            CompatibilityMode.Full, CompatibilitySurface.Source);

        var outcome = subject.RegisterVersion(
            Build.Schema(2), Build.Ok(wrongPolicy), null, null, Build.Actor(), Build.At);

        Assert.True(outcome.IsFailure);
        Assert.Equal(ConcordatCodes.VerdictPolicyMismatch, outcome.Error!.Code);
    }

    [Fact]
    public void Changelog_IsTrimmedAndBounded()
    {
        var subject = Build.Subject();

        var ok = subject.RegisterVersion(
            Build.Schema(1), Build.Ok(), null, "  initial  ", Build.Actor(), Build.At);
        Assert.Equal("initial", ok.Value.Version.Changelog);

        var tooLong = new string('x', Subject.MaxChangelogLength + 1);
        var bad = subject.RegisterVersion(
            Build.Schema(2), Build.Ok(), null, tooLong, Build.Actor(), Build.At);

        Assert.True(bad.IsFailure);
        Assert.Equal(ConcordatCodes.ChangelogTooLong, bad.Error!.Code);
    }

    [Fact]
    public void RegisteredAt_IsNormalisedToUtc()
    {
        var subject = Build.Subject();
        var offset = new DateTimeOffset(2026, 8, 13, 11, 0, 0, TimeSpan.FromHours(2));

        var outcome = subject.RegisterVersion(
            Build.Schema(1), Build.Ok(), null, null, Build.Actor(), offset);

        Assert.Equal(TimeSpan.Zero, outcome.Value.Version.RegisteredAt.Offset);
    }

    [Fact]
    public void Versions_CannotBeMutatedThroughTheExposedCollection()
    {
        var subject = Build.Subject().WithVersion(1);

        Assert.False(subject.Versions is System.Collections.Generic.ICollection<SchemaVersion>
        {
            IsReadOnly: false,
        });
    }
}

public class ApprovalGateTests
{
    [Fact]
    public void BreakingChange_RegistersAwaitingApprovalAndDoesNotMoveLatest()
    {
        var subject = Build.Subject().WithVersion(1);

        var outcome = subject.RegisterVersion(
            Build.Schema(2), Build.Breaks(), null, null, Build.Actor(), Build.At);

        Assert.True(outcome.IsSuccess);
        Assert.Equal(VersionStatus.AwaitingApproval, outcome.Value.Version.Status);
        Assert.Equal(2, outcome.Value.Version.Ordinal);
        Assert.Equal(1, subject.Latest!.Ordinal);
    }

    [Fact]
    public void Approve_MakesVersionActiveAndMovesLatest()
    {
        var subject = Build.Subject().WithVersion(1);
        subject.RegisterVersion(Build.Schema(2), Build.Breaks(), null, null, Build.Actor(), Build.At);

        var result = subject.Approve(2, Build.Actor("bob"), Build.At);

        Assert.True(result.IsSuccess);
        Assert.Equal(VersionStatus.Active, subject.Versions[1].Status);
        Assert.Equal(2, subject.Latest!.Ordinal);
        Assert.Equal("bob", subject.Latest.MovedBy.Value);
    }

    [Fact]
    public void Reject_MarksRejectedAndLeavesLatestWhereItWas()
    {
        var subject = Build.Subject().WithVersion(1);
        subject.RegisterVersion(Build.Schema(2), Build.Breaks(), null, null, Build.Actor(), Build.At);

        var result = subject.Reject(2, Build.Actor("bob"), Build.At);

        Assert.True(result.IsSuccess);
        Assert.Equal(VersionStatus.Rejected, subject.Versions[1].Status);
        Assert.Equal(1, subject.Latest!.Ordinal);
    }

    [Fact]
    public void Approve_DoesNotRegressLatest()
    {
        // v2 breaks and sits pending; a compatible v3 registers and takes latest. Approving v2
        // afterwards must not drag the pointer backwards.
        var subject = Build.Subject().WithVersion(1);
        subject.RegisterVersion(Build.Schema(2), Build.Breaks(), null, null, Build.Actor(), Build.At);
        subject.RegisterVersion(Build.Schema(3), Build.Ok(), null, null, Build.Actor(), Build.At);
        Assert.Equal(3, subject.Latest!.Ordinal);

        subject.Approve(2, Build.Actor(), Build.At);

        Assert.Equal(VersionStatus.Active, subject.Versions[1].Status);
        Assert.Equal(3, subject.Latest.Ordinal);
    }

    [Fact]
    public void Approve_AnAlreadyActiveVersion_Fails()
    {
        var subject = Build.Subject().WithVersion(1);

        var result = subject.Approve(1, Build.Actor(), Build.At);

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.VersionNotAwaitingApproval, result.Error!.Code);
    }

    [Fact]
    public void Approve_Twice_FailsTheSecondTime()
    {
        var subject = Build.Subject().WithVersion(1);
        subject.RegisterVersion(Build.Schema(2), Build.Breaks(), null, null, Build.Actor(), Build.At);
        subject.Approve(2, Build.Actor(), Build.At);

        var second = subject.Approve(2, Build.Actor(), Build.At);

        Assert.True(second.IsFailure);
        Assert.Equal(ConcordatCodes.VersionNotAwaitingApproval, second.Error!.Code);
    }

    [Fact]
    public void Approve_AnUnknownOrdinal_Fails()
    {
        var subject = Build.Subject().WithVersion(1);

        var result = subject.Approve(99, Build.Actor(), Build.At);

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.VersionNotFound, result.Error!.Code);
    }

    [Fact]
    public void RejectedVersion_CannotThenBeApproved()
    {
        var subject = Build.Subject().WithVersion(1);
        subject.RegisterVersion(Build.Schema(2), Build.Breaks(), null, null, Build.Actor(), Build.At);
        subject.Reject(2, Build.Actor(), Build.At);

        Assert.True(subject.Approve(2, Build.Actor(), Build.At).IsFailure);
    }
}

public class SemverVerificationTests
{
    [Fact]
    public void BreakingChange_LabelledMinor_IsRejected()
    {
        var subject = Build.Subject().WithVersion(1, "1.0.0");
        var minor = SemanticVersion.Create("1.1.0").Value;

        var outcome = subject.RegisterVersion(
            Build.Schema(2), Build.Breaks(), minor, null, Build.Actor(), Build.At);

        Assert.True(outcome.IsFailure);
        Assert.Equal(ConcordatCodes.SemverLabelUnderstatesBreakage, outcome.Error!.Code);
    }

    [Fact]
    public void BreakingChange_LabelledPatch_IsRejected()
    {
        var subject = Build.Subject().WithVersion(1, "1.0.0");
        var patch = SemanticVersion.Create("1.0.1").Value;

        var outcome = subject.RegisterVersion(
            Build.Schema(2), Build.Breaks(), patch, null, Build.Actor(), Build.At);

        Assert.True(outcome.IsFailure);
        Assert.Equal(ConcordatCodes.SemverLabelUnderstatesBreakage, outcome.Error!.Code);
    }

    [Fact]
    public void BreakingChange_LabelledMajor_IsAccepted()
    {
        var subject = Build.Subject().WithVersion(1, "1.0.0");
        var major = SemanticVersion.Create("2.0.0").Value;

        var outcome = subject.RegisterVersion(
            Build.Schema(2), Build.Breaks(), major, null, Build.Actor(), Build.At);

        Assert.True(outcome.IsSuccess, outcome.Error?.Message);
        Assert.Equal(VersionStatus.AwaitingApproval, outcome.Value.Version.Status);
    }

    [Fact]
    public void NonBreakingChange_LabelledMajor_IsAccepted()
    {
        // Over-claiming is allowed; ADR-004 only forbids under-claiming.
        var subject = Build.Subject().WithVersion(1, "1.0.0");
        var major = SemanticVersion.Create("2.0.0").Value;

        Assert.True(subject.RegisterVersion(
            Build.Schema(2), Build.Ok(), major, null, Build.Actor(), Build.At).IsSuccess);
    }

    [Fact]
    public void Label_ThatDoesNotIncrease_IsRejected()
    {
        var subject = Build.Subject().WithVersion(1, "2.0.0");
        var lower = SemanticVersion.Create("1.9.9").Value;

        var outcome = subject.RegisterVersion(
            Build.Schema(2), Build.Ok(), lower, null, Build.Actor(), Build.At);

        Assert.True(outcome.IsFailure);
        Assert.Equal(ConcordatCodes.SemverNotIncreasing, outcome.Error!.Code);
    }

    [Fact]
    public void NoLabel_IsAlwaysAllowed()
    {
        var subject = Build.Subject().WithVersion(1, "1.0.0");

        Assert.True(subject.RegisterVersion(
            Build.Schema(2), Build.Breaks(), null, null, Build.Actor(), Build.At).IsSuccess);
    }
}

public class LifecycleTests
{
    [Fact]
    public void NewSubject_IsActiveWithNoVersionsAndNoLatest()
    {
        var subject = Build.Subject();

        Assert.Equal(SubjectLifecycle.Active, subject.Lifecycle);
        Assert.Empty(subject.Versions);
        Assert.Null(subject.Latest);
        Assert.Null(subject.LatestVersion());
    }

    [Fact]
    public void DeprecatedSubject_StillAcceptsVersions()
    {
        // Deprecated is advisory: existing producers still need to patch their contract.
        var subject = Build.Subject().WithVersion(1);
        Assert.True(subject.Deprecate().IsSuccess);

        var outcome = subject.RegisterVersion(
            Build.Schema(2), Build.Ok(), null, null, Build.Actor(), Build.At);

        Assert.True(outcome.IsSuccess);
        Assert.Equal(SubjectLifecycle.Deprecated, subject.Lifecycle);
    }

    [Fact]
    public void RetiredSubject_AcceptsNoVersions()
    {
        var subject = Build.Subject().WithVersion(1);
        subject.Retire();

        var outcome = subject.RegisterVersion(
            Build.Schema(2), Build.Ok(), null, null, Build.Actor(), Build.At);

        Assert.True(outcome.IsFailure);
        Assert.Equal(ConcordatCodes.SubjectRetired, outcome.Error!.Code);
    }

    [Fact]
    public void Retire_IsTerminal()
    {
        var subject = Build.Subject();
        subject.Retire();

        Assert.True(subject.Retire().IsFailure);
        Assert.True(subject.Deprecate().IsFailure);
        Assert.Equal(SubjectLifecycle.Retired, subject.Lifecycle);
    }

    [Fact]
    public void EffectivePolicy_FallsBackToTheEnvironmentDefault()
    {
        var envDefault = new CompatibilityPolicy(
            CompatibilityMode.Full, CompatibilitySurface.Source);

        var inheriting = Build.Subject();
        Assert.Null(inheriting.CompatibilityPolicy);
        Assert.Equal(envDefault, inheriting.EffectivePolicy(envDefault));

        var explicitly = Build.Subject(Build.BackwardWireJson);
        Assert.Equal(Build.BackwardWireJson, explicitly.EffectivePolicy(envDefault));
    }
}

public class DependencyRuleTests
{
    [Fact]
    public void Domain_ReferencesNothingButTheBaseClassLibrary()
    {
        // DESIGN §8: Domain is the base of the dependency rule. src/README.md flags this as
        // review-enforced; this makes it a build failure instead.
        var referenced = typeof(Subject).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => !n.StartsWith("System", StringComparison.Ordinal)
                        && !n.Equals("netstandard", StringComparison.Ordinal)
                        && !n.Equals("mscorlib", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(referenced);
    }
}
