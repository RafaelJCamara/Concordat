using Concordat.Domain.Registry;
using Concordat.Domain.Results;
using Concordat.Domain.Tests.TestSupport;

namespace Concordat.Domain.Tests;

public class ConcordatRefTests
{
    [Fact]
    public void Create_ParsesEnvironmentSubjectAndVersion()
    {
        var result = ConcordatRef.Create("concordat://prod/acme.orders.OrderCreated/3");

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("prod", result.Value.Environment);
        Assert.Equal("acme.orders.OrderCreated", result.Value.Subject.Value);
        Assert.Equal(3, result.Value.Version);
    }

    [Fact]
    public void ToString_RoundTrips()
    {
        const string text = "concordat://prod/acme.Common/1";

        Assert.Equal(text, ConcordatRef.Create(text).Value.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#/$defs/Address")]
    [InlineData("https://example.com/schema.json")]
    [InlineData("concordat://prod/acme.Common")]
    [InlineData("concordat://prod/acme.Common/1/extra")]
    [InlineData("concordat://prod/acme.Common/0")]
    [InlineData("concordat://prod/acme.Common/-1")]
    [InlineData("concordat://prod/acme.Common/abc")]
    [InlineData("concordat:///acme.Common/1")]
    [InlineData("concordat://prod/not a name/1")]
    public void Create_RejectsMalformedOrForeignReferences(string? value)
    {
        var result = ConcordatRef.Create(value);

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.ReferenceInvalid, result.Error!.Code);
    }

    [Theory]
    [InlineData("concordat://prod/a/1", true)]
    [InlineData("CONCORDAT://prod/a/1", true)]
    [InlineData("concordat://garbage", true)]
    [InlineData("#/$defs/Address", false)]
    [InlineData("https://example.com/s.json", false)]
    [InlineData(null, false)]
    public void IsConcordatRef_DistinguishesOursFromForeign(string? value, bool expected) =>
        Assert.Equal(expected, ConcordatRef.IsConcordatRef(value));

    [Fact]
    public void IsConcordatRef_IsTrueForAMalformedConcordatRef()
    {
        // The distinction that matters: a typo in our own scheme must be reported, not
        // silently skipped as "somebody else's reference".
        const string typo = "concordat://prod/acme.Common";

        Assert.True(ConcordatRef.IsConcordatRef(typo));
        Assert.True(ConcordatRef.Create(typo).IsFailure);
    }
}

public class ReferenceGraphTests
{
    private static SchemaNode Node(string subject, int version) =>
        new(SubjectName.Create(subject).Value, version);

    private static Dictionary<SchemaNode, IReadOnlyList<SchemaNode>> Edges(
        params (SchemaNode From, SchemaNode[] To)[] entries) =>
        entries.ToDictionary(e => e.From, e => (IReadOnlyList<SchemaNode>)e.To);

    [Fact]
    public void AnAcyclicGraph_Passes()
    {
        var a = Node("acme.A", 1);
        var b = Node("acme.B", 1);
        var c = Node("acme.C", 1);

        var result = ReferenceGraph.DetectCycle(a, Edges((a, [b]), (b, [c])));

        Assert.True(result.IsSuccess, result.Error?.Message);
    }

    [Fact]
    public void ADirectCycle_IsDetected()
    {
        var a = Node("acme.A", 1);
        var b = Node("acme.B", 1);

        var result = ReferenceGraph.DetectCycle(a, Edges((a, [b]), (b, [a])));

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.ReferenceCycle, result.Error!.Code);
        Assert.Contains("acme.A@1", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASelfReference_IsDetected()
    {
        // Registration order prevents most cycles, but not this one.
        var a = Node("acme.A", 1);

        var result = ReferenceGraph.DetectCycle(a, Edges((a, [a])));

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.ReferenceCycle, result.Error!.Code);
    }

    [Fact]
    public void AnIndirectCycle_IsDetected()
    {
        var a = Node("acme.A", 1);
        var b = Node("acme.B", 1);
        var c = Node("acme.C", 1);

        var result = ReferenceGraph.DetectCycle(a, Edges((a, [b]), (b, [c]), (c, [a])));

        Assert.True(result.IsFailure);
    }

    [Fact]
    public void DifferentVersionsOfOneSubject_AreNotACycle()
    {
        // A@2 -> B@1 -> A@1 is a valid DAG: A@1 existed before B@1 referenced it. A
        // subject-level graph would wrongly reject this.
        var a1 = Node("acme.A", 1);
        var a2 = Node("acme.A", 2);
        var b1 = Node("acme.B", 1);

        var result = ReferenceGraph.DetectCycle(a2, Edges((a2, [b1]), (b1, [a1])));

        Assert.True(result.IsSuccess, result.Error?.Message);
    }

    [Fact]
    public void ADiamond_IsNotACycle()
    {
        var a = Node("acme.A", 1);
        var b = Node("acme.B", 1);
        var c = Node("acme.C", 1);
        var d = Node("acme.D", 1);

        var result = ReferenceGraph.DetectCycle(a, Edges((a, [b, c]), (b, [d]), (c, [d])));

        Assert.True(result.IsSuccess, result.Error?.Message);
    }

    [Fact]
    public void ReferrersOf_FindsDirectAndTransitiveDependants()
    {
        // A breaking change in D breaks B and C directly, and A through them.
        var a = Node("acme.A", 1);
        var b = Node("acme.B", 1);
        var c = Node("acme.C", 1);
        var d = Node("acme.D", 1);

        var referrers = ReferenceGraph.ReferrersOf(d, Edges((a, [b, c]), (b, [d]), (c, [d])));

        Assert.Equal(3, referrers.Count);
        Assert.Contains(a, referrers);
        Assert.Contains(b, referrers);
        Assert.Contains(c, referrers);
    }

    [Fact]
    public void ReferrersOf_IsEmptyForALeaf()
    {
        var a = Node("acme.A", 1);
        var b = Node("acme.B", 1);

        Assert.Empty(ReferenceGraph.ReferrersOf(a, Edges((a, [b]))));
    }

    [Fact]
    public void ReferrersOf_TerminatesOnACyclicEdgeSet()
    {
        var a = Node("acme.A", 1);
        var b = Node("acme.B", 1);

        var referrers = ReferenceGraph.ReferrersOf(a, Edges((a, [b]), (b, [a])));

        Assert.Equal(2, referrers.Count);
    }
}
