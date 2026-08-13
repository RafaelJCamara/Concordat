using Concordat.Domain.Registry;
using Concordat.Domain.Tests.TestSupport;

namespace Concordat.Domain.Tests;

public class DefaultPolicyTests
{
    [Fact]
    public void Default_IsBackwardTimesWireJson()
    {
        // Pinned deliberately. Backward × Source would block int32 -> int64, the exact change
        // ADR-016 exists to permit; Backward × Wire is a no-op for JSON Schema and gets
        // permissive once Avro and Protobuf land. Changing this is a product decision, so it
        // should break a test and force the conversation.
        Assert.Equal(CompatibilityMode.Backward, CompatibilityPolicy.Default.Mode);
        Assert.Equal(CompatibilitySurface.WireJson, CompatibilityPolicy.Default.Surface);
    }

    [Fact]
    public void Default_PermitsASourceOnlyBreak()
    {
        // int32 -> int64 is source-breaking but wire- and JSON-safe.
        Assert.False(CompatibilityPolicy.Default.IsViolatedBy(CompatibilitySurface.Source));
    }

    [Fact]
    public void Default_BlocksAWireBreak() =>
        Assert.True(CompatibilityPolicy.Default.IsViolatedBy(CompatibilitySurface.Wire));

    [Fact]
    public void ASubjectWithNoExplicitPolicy_InheritsTheEnvironmentDefault()
    {
        var subject = Build.Subject();

        Assert.Null(subject.CompatibilityPolicy);
        Assert.Equal(
            CompatibilityPolicy.Default,
            subject.EffectivePolicy(CompatibilityPolicy.Default));
    }
}
