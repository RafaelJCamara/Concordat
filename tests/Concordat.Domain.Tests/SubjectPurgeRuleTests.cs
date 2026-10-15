using Concordat.Domain.Registry;
using Concordat.Domain.Results;
using Concordat.Domain.Tests.TestSupport;

namespace Concordat.Domain.Tests;

/// <summary>
/// Only a retired subject may be deleted for good (ADR-046). The rule is the aggregate's, so no handler
/// can forget it.
/// </summary>
public class SubjectPurgeRuleTests
{
    [Fact]
    public void ARetiredSubjectMayBePurged()
    {
        var subject = Build.Subject().WithVersion(1);
        Assert.True(subject.Retire().IsSuccess);

        Assert.True(subject.CheckPurgeable().IsSuccess);
    }

    [Fact]
    public void AnActiveSubjectMayNotBePurged_AndTheRefusalSaysToRetireItFirst()
    {
        var subject = Build.Subject().WithVersion(1);

        var result = subject.CheckPurgeable();

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.SubjectNotRetired, result.Error!.Code);
        Assert.Contains("Retire it first", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("active", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeprecatedSubjectMayNotBePurged_BecauseDeprecationIsAdvisoryAndItsProducersAreStillThere()
    {
        var subject = Build.Subject().WithVersion(1);
        Assert.True(subject.Deprecate().IsSuccess);

        var result = subject.CheckPurgeable();

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.SubjectNotRetired, result.Error!.Code);
        Assert.Contains("deprecated", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AskingChangesNothing()
    {
        var subject = Build.Subject().WithVersion(1);
        var revision = subject.Revision;

        _ = subject.CheckPurgeable();

        Assert.Equal(SubjectLifecycle.Active, subject.Lifecycle);
        Assert.Equal(revision, subject.Revision);
        Assert.Single(subject.Versions);
    }
}
