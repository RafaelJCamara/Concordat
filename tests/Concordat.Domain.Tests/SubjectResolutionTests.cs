using Concordat.Domain.Messaging;
using Concordat.Domain.Registry;

namespace Concordat.Domain.Tests;

/// <summary>
/// What the corpus cannot express: the seam's contract, and the quality of a refusal.
/// </summary>
/// <remarks>
/// The normalisation rules themselves live in <c>corpus/subject-resolution</c>, because those
/// are the ones every SDK must reproduce. These are .NET-side obligations.
/// </remarks>
public class SubjectResolutionTests
{
    private static SubjectResolution Resolve(string? type) =>
        MessageTypeSubjectResolver.Instance.Resolve(new PublishContext { MessageType = type });

    [Fact]
    public void TheResolverNeverThrowsOnAMessage()
    {
        // It runs on the publish path for every message. A resolver that throws on malformed
        // input turns a naming mistake into an outage, and the caller loses the ability to
        // choose between blocking the publish and letting it through unenforced.
        foreach (var hostile in new[] { "", "   ", ".", "..", "a..b", "\0", "😀", new string('x', 5000) })
        {
            var resolution = Resolve(hostile);
            Assert.False(resolution.IsResolved && resolution.IsUnusable);
        }
    }

    [Fact]
    public void ANullContextIsAProgrammingErrorNotAMessageProblem()
    {
        Assert.Throws<ArgumentNullException>(
            () => MessageTypeSubjectResolver.Instance.Resolve(null!));
    }

    [Fact]
    public void AClosedGenericIsSpelledRatherThanRefused()
    {
        // ADR-025. The spelling is defined over the outer and argument NAMES in order -- which
        // every language with generics can produce -- rather than over CLR syntax, which only
        // .NET can. Refusing generics was refusing the wrong thing: what had to be refused was
        // deriving the spelling from one language's type system, because each SDK inventing its
        // own would give the same logical contract a different subject per language.
        var resolution = Resolve("Acme.Envelope`1[[Acme.Order, Acme]]");

        Assert.True(resolution.IsResolved);
        Assert.Equal("Acme.Envelope_of_Acme.Order", resolution.Subject!.Value);
    }

    [Fact]
    public void AnOpenGenericIsToldWhatToDoInstead()
    {
        // An open generic names no contract -- there is nothing to validate a payload against.
        var resolution = Resolve("Acme.Envelope`1");

        Assert.True(resolution.IsUnusable);
        Assert.Contains("generic type", resolution.Error!.Message, StringComparison.Ordinal);
        Assert.Contains("Envelope_of_OrderCreated", resolution.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusalQuotesWhatThePublisherActuallyWrote()
    {
        // Not the normalised form. Someone reading the log has to find this string in their
        // code, and the normalised version may appear nowhere.
        var resolution = Resolve("Acme.Orders+Order-Created, Acme.Contracts");

        Assert.True(resolution.IsUnusable);
        Assert.Contains(
            "Acme.Orders+Order-Created, Acme.Contracts",
            resolution.Error!.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AbsentAndUnusableAreDistinguishable()
    {
        // The distinction the whole three-outcome shape exists for. An adopter with legacy
        // publishers must be able to count "no type set" separately from "type set wrongly",
        // because the first is a rollout backlog and the second is a bug.
        var absent = Resolve(null);
        var unusable = Resolve("not a subject");

        Assert.False(absent.IsResolved);
        Assert.False(absent.IsUnusable);
        Assert.Null(absent.Error);

        Assert.False(unusable.IsResolved);
        Assert.True(unusable.IsUnusable);
        Assert.NotNull(unusable.Error);
    }

    [Fact]
    public void TheContextCarriesHeadersForResolversThatNeedThem()
    {
        // v1's resolver ignores them. The seam admits them because a framework adapter reading
        // its own convention out of a header is the obvious next resolver, and widening a
        // published interface later is worse than carrying an unused property now.
        var context = new PublishContext
        {
            MessageType = "acme.Order",
            Headers = new Dictionary<string, object?>(StringComparer.Ordinal) { ["x-whatever"] = "v" },
        };

        Assert.True(MessageTypeSubjectResolver.Instance.Resolve(context).IsResolved);
    }

    [Fact]
    public void TheSharedInstanceHoldsNoState()
    {
        // Published as a singleton, so a future resolver caching per-type results would be a
        // silently shared mutable cache across every publisher in the process.
        Assert.Same(MessageTypeSubjectResolver.Instance, MessageTypeSubjectResolver.Instance);

        Assert.Equal("acme.A", Resolve("acme.A").Subject!.Value);
        Assert.True(Resolve("bad-name").IsUnusable);
        Assert.Equal("acme.A", Resolve("acme.A").Subject!.Value);
    }

    [Fact]
    public void NormalizationIsSeparableFromValidation()
    {
        // The normaliser deliberately returns text that may still be invalid, so an SDK can
        // report what the rewrite produced when validation then fails. Folding the two
        // together would leave a refusal that cannot say what it tried.
        Assert.Equal("Acme.Orders.Order-Created", SubjectNormalizer.Normalize("Acme.Orders+Order-Created, Acme"));
        Assert.Equal(string.Empty, SubjectNormalizer.Normalize(null));
        Assert.Equal(string.Empty, SubjectNormalizer.Normalize("   "));
    }

    [Fact]
    public void OnlyTheFirstCommaSplitsOffTheAssembly()
    {
        // Version=..., Culture=..., PublicKeyToken=... all contain commas between them. A
        // last-comma or split-and-rejoin implementation would keep part of the assembly name.
        Assert.Equal(
            "Acme.Order",
            SubjectNormalizer.Normalize("Acme.Order, Acme, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"));
    }

    [Fact]
    public void ResolvedRequiresASubject()
    {
        Assert.Throws<ArgumentNullException>(() => SubjectResolution.Resolved(null!));
    }
}
