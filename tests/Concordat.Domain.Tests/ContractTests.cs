using Concordat.Domain.Contracts;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;

namespace Concordat.Domain.Tests;

/// <summary>M7.3's contract invariants, and the pattern algebra they rest on.</summary>
public class ContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly EnvironmentId Env = EnvironmentId.New();

    private static RoutingKeyPattern Pattern(string value) =>
        RoutingKeyPattern.Create(value).Value;

    private static SubjectRef Ref(string subject, string selector = "latest") =>
        new(SubjectName.Create(subject).Value, VersionSelector.Parse(selector).Value);

    private static Contract NewContract() => Contract.Create(Env, "orders", Now).Value;

    private static PublishBinding Publish(
        string pattern, string subject, int? precedence = null, string exchange = "orders") =>
        new(TopologyScope.Default, exchange, Pattern(pattern), [Ref(subject)], precedence);

    // ------------------------------------------------------------ pattern grammar

    [Theory]
    [InlineData("orders")]
    [InlineData("orders.created")]
    [InlineData("orders.*")]
    [InlineData("orders.#")]
    [InlineData("*")]
    [InlineData("#")]
    [InlineData("orders.*.created")]
    [InlineData("a-b_c.d")]
    public void ValidPatternsAreAccepted(string pattern) =>
        Assert.True(RoutingKeyPattern.Create(pattern).IsSuccess, pattern);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("orders..created")]
    [InlineData(".orders")]
    [InlineData("orders.")]
    [InlineData("orders.**")]
    [InlineData("orders.a#")]
    [InlineData("orders/created")]
    [InlineData("orders created")]
    public void InvalidPatternsAreRefused(string? pattern)
    {
        var result = RoutingKeyPattern.Create(pattern);

        Assert.True(result.IsFailure, pattern);
        Assert.Equal(ConcordatCodes.RoutingKeyPatternInvalid, result.Error!.Code);
    }

    [Fact]
    public void ARefusalShowsAValidExample() =>
        Assert.Contains(
            "orders.*.created",
            RoutingKeyPattern.Create("orders..created").Error!.Message,
            StringComparison.Ordinal);

    // ------------------------------------------------------------------ matching

    [Theory]
    [InlineData("orders.created", "orders.created", true)]
    [InlineData("orders.*", "orders.created", true)]
    [InlineData("orders.*", "orders.created.eu", false)]
    [InlineData("orders.#", "orders.created.eu", true)]
    [InlineData("orders.#", "orders", true)]
    [InlineData("#", "anything.at.all", true)]
    [InlineData("#", "", true)]
    [InlineData("*", "orders", true)]
    [InlineData("*", "orders.created", false)]
    [InlineData("orders.created", "orders.updated", false)]
    [InlineData("*.created", "orders.created", true)]
    public void MatchingFollowsTheAmqpRules(string pattern, string key, bool matches) =>
        Assert.Equal(matches, Pattern(pattern).Matches(key));

    [Fact]
    public void HashMatchesZeroWords() =>
        // The case people get wrong. 'orders.#' matches the bare key 'orders', so the
        // recursion has to try consuming nothing before it tries consuming words.
        Assert.True(Pattern("orders.#").Matches("orders"));

    // ------------------------------------------------------------------ overlap

    [Theory]
    [InlineData("orders.*", "*.created", true)]
    [InlineData("orders.#", "orders.created", true)]
    [InlineData("orders.created", "orders.updated", false)]
    [InlineData("orders.*", "orders.*.eu", false)]
    [InlineData("#", "anything.here", true)]
    [InlineData("orders.#", "billing.#", false)]
    [InlineData("*.*", "orders.created", true)]
    [InlineData("*.*", "orders", false)]
    [InlineData("orders.#", "orders", true)]
    [InlineData("a.#.b", "a.b", true)]
    public void OverlapIsIntersectionNotResemblance(string left, string right, bool overlaps)
    {
        // The property the whole conflict invariant rests on: 'orders.*' and '*.created' look
        // nothing alike and both match 'orders.created'.
        Assert.Equal(overlaps, Pattern(left).Overlaps(Pattern(right)));
        Assert.Equal(overlaps, Pattern(right).Overlaps(Pattern(left)));
    }

    [Fact]
    public void OverlapIsReflexive() =>
        Assert.True(Pattern("orders.*.created").Overlaps(Pattern("orders.*.created")));

    // -------------------------------------------------------------- version selectors

    [Theory]
    [InlineData("latest", VersionSelectorKind.Latest)]
    [InlineData("3", VersionSelectorKind.Pinned)]
    [InlineData(">=2", VersionSelectorKind.Range)]
    [InlineData("LATEST", VersionSelectorKind.Latest)]
    [InlineData(">= 2", VersionSelectorKind.Range)]
    public void SelectorsParse(string value, VersionSelectorKind kind) =>
        Assert.Equal(kind, VersionSelector.Parse(value).Value.Kind);

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("newest")]
    [InlineData("0")]
    [InlineData(">=0")]
    [InlineData("-1")]
    [InlineData(">")]
    public void InvalidSelectorsAreRefused(string? value) =>
        Assert.True(VersionSelector.Parse(value).IsFailure, value);

    [Theory]
    [InlineData("latest", 5, 5, true)]
    [InlineData("latest", 4, 5, false)]
    [InlineData("3", 3, 9, true)]
    [InlineData("3", 4, 9, false)]
    [InlineData(">=2", 2, 9, true)]
    [InlineData(">=2", 9, 9, true)]
    [InlineData(">=2", 1, 9, false)]
    public void SelectorsAcceptTheRightVersions(
        string selector, int ordinal, int? latest, bool accepted) =>
        Assert.Equal(accepted, VersionSelector.Parse(selector).Value.Accepts(ordinal, latest));

    [Fact]
    public void SelectorsRoundTripThroughTheirWireSpelling()
    {
        foreach (var spelling in new[] { "latest", "3", ">=2" })
        {
            Assert.Equal(spelling, VersionSelector.Parse(spelling).Value.ToString());
        }
    }

    // ------------------------------------------------------------ the invariant

    [Fact]
    public void NonOverlappingBindingsCoexist()
    {
        var contract = NewContract();

        Assert.True(contract.AddPublish(Publish("orders.created", "acme.Created")).IsSuccess);
        Assert.True(contract.AddPublish(Publish("orders.updated", "acme.Updated")).IsSuccess);
        Assert.Equal(2, contract.Publishes.Count);
    }

    [Fact]
    public void OverlappingBindingsWithDifferentSubjectsAreRefused()
    {
        var contract = NewContract();
        contract.AddPublish(Publish("orders.*", "acme.Created"));

        var conflict = contract.AddPublish(Publish("*.created", "acme.Other"));

        Assert.True(conflict.IsFailure);
        Assert.Equal(ConcordatCodes.BindingConflict, conflict.Error!.Code);
    }

    [Fact]
    public void TheConflictMessageNamesAKeyThatMatchesBoth()
    {
        // A message that only says "these overlap" leaves the reader to work out why two
        // unlike-looking patterns collide, which is the hard part.
        var contract = NewContract();
        contract.AddPublish(Publish("orders.*", "acme.Created"));

        var conflict = contract.AddPublish(Publish("*.created", "acme.Other"));

        Assert.Contains("orders.created", conflict.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OverlappingBindingsCarryingTheSameSubjectsAreAllowed() =>
        // They cannot disagree, so there is nothing for a precedence to resolve.
        Assert.True(Bind(
            Publish("orders.*", "acme.Created"),
            Publish("*.created", "acme.Created")).IsSuccess);

    [Fact]
    public void ExplicitPrecedenceSeparatesOverlappingBindings() =>
        // Specific-beats-general is a normal thing to want; it just has to be said out loud.
        Assert.True(Bind(
            Publish("orders.#", "acme.Fallback", precedence: 1),
            Publish("orders.created", "acme.Created", precedence: 10)).IsSuccess);

    [Fact]
    public void EqualPrecedenceDoesNotSeparateAnything() =>
        // Two bindings both claiming priority 5 have not decided anything.
        Assert.True(Bind(
            Publish("orders.#", "acme.Fallback", precedence: 5),
            Publish("orders.created", "acme.Created", precedence: 5)).IsFailure);

    [Fact]
    public void PrecedenceOnOnlyOneSideDoesNotSeparateAnything() =>
        Assert.True(Bind(
            Publish("orders.#", "acme.Fallback"),
            Publish("orders.created", "acme.Created", precedence: 10)).IsFailure);

    [Fact]
    public void BindingsOnDifferentExchangesDoNotConflict() =>
        Assert.True(Bind(
            Publish("orders.*", "acme.Created", exchange: "orders"),
            Publish("orders.*", "acme.Other", exchange: "billing")).IsSuccess);

    [Fact]
    public void BindingsOnDifferentVirtualHostsDoNotConflict()
    {
        var contract = NewContract();
        contract.AddPublish(new PublishBinding(
            new TopologyScope(null, "/orders"), "x", Pattern("#"), [Ref("acme.A")]));

        var second = contract.AddPublish(new PublishBinding(
            new TopologyScope(null, "/billing"), "x", Pattern("#"), [Ref("acme.B")]));

        Assert.True(second.IsSuccess, second.Error?.Message);
    }

    [Fact]
    public void AScopeWithoutABrokerOverlapsOneWithABroker()
    {
        // Null broker means "every broker in this environment", so it necessarily covers the
        // one a narrower binding names. Treating them as disjoint would let a wildcard binding
        // and a specific one disagree silently.
        var contract = NewContract();
        var broker = Guid.CreateVersion7();

        contract.AddPublish(new PublishBinding(
            TopologyScope.Default, "x", Pattern("#"), [Ref("acme.A")]));

        var second = contract.AddPublish(new PublishBinding(
            new TopologyScope(broker, "/"), "x", Pattern("#"), [Ref("acme.B")]));

        Assert.True(second.IsFailure);
    }

    // ---------------------------------------------------------------- resolution

    [Fact]
    public void ResolvingAPublishReturnsHighestPrecedenceFirst()
    {
        var contract = NewContract();
        contract.AddPublish(Publish("orders.#", "acme.Fallback", precedence: 1));
        contract.AddPublish(Publish("orders.created", "acme.Created", precedence: 10));

        var matched = contract.ResolvePublish(
            Guid.CreateVersion7(), "/", "orders", "orders.created");

        Assert.Equal(2, matched.Count);
        Assert.Equal("acme.Created", matched[0].Subjects[0].Subject.Value);
    }

    [Fact]
    public void ResolvingAPublishIgnoresAnotherVirtualHost()
    {
        var contract = NewContract();
        contract.AddPublish(new PublishBinding(
            new TopologyScope(null, "/orders"), "x", Pattern("#"), [Ref("acme.A")]));

        Assert.Empty(contract.ResolvePublish(Guid.CreateVersion7(), "/", "x", "anything"));
    }

    [Fact]
    public void ResolvingAPublishIgnoresAnotherExchange()
    {
        var contract = NewContract();
        contract.AddPublish(Publish("#", "acme.A", exchange: "orders"));

        Assert.Empty(contract.ResolvePublish(Guid.CreateVersion7(), "/", "billing", "anything"));
    }

    // ------------------------------------------------------------------ consume

    [Fact]
    public void AQueueCanBeBound()
    {
        var contract = NewContract();

        var added = contract.AddConsume(
            new ConsumeBinding(TopologyScope.Default, "orders.q", [Ref("acme.Created")]));

        Assert.True(added.IsSuccess);
        Assert.NotNull(contract.ResolveConsume(Guid.CreateVersion7(), "/", "orders.q"));
    }

    [Fact]
    public void OneQueueBoundToDifferentSubjectsIsRefused()
    {
        // No pattern algebra on the consume side: a queue name is a literal, so two bindings
        // either name the same queue or they do not, and disagreement is just a duplicate.
        var contract = NewContract();
        contract.AddConsume(
            new ConsumeBinding(TopologyScope.Default, "orders.q", [Ref("acme.A")]));

        var clash = contract.AddConsume(
            new ConsumeBinding(TopologyScope.Default, "orders.q", [Ref("acme.B")]));

        Assert.True(clash.IsFailure);
        Assert.Equal(ConcordatCodes.BindingConflict, clash.Error!.Code);
    }

    [Fact]
    public void TheSameQueueWithTheSameSubjectsIsNotAConflict() =>
        Assert.True(BindConsume(
            new ConsumeBinding(TopologyScope.Default, "q", [Ref("acme.A")]),
            new ConsumeBinding(TopologyScope.Default, "q", [Ref("acme.A")])).IsSuccess);

    // ----------------------------------------------------------------- lifecycle

    [Fact]
    public void ANewContractMonitorsRatherThanEnforces() =>
        // A contract that started blocking the moment it was written would be authored by
        // guessing and discovered in production.
        Assert.Equal(EnforcementMode.Monitor, NewContract().Enforcement);

    [Fact]
    public void EnforcementCanBeChanged()
    {
        var contract = NewContract();
        contract.SetEnforcement(EnforcementMode.Enforce);

        Assert.Equal(EnforcementMode.Enforce, contract.Enforcement);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void AContractNeedsAName(string? name) =>
        Assert.Equal(
            ConcordatCodes.ContractNameInvalid,
            Contract.Create(Env, name, Now).Error!.Code);

    [Fact]
    public void TheBindingListsCannotBeMutatedThroughTheProperties()
    {
        var contract = NewContract();

        Assert.False(contract.Publishes is ICollection<PublishBinding> { IsReadOnly: false });
        Assert.False(contract.Consumes is ICollection<ConsumeBinding> { IsReadOnly: false });
    }

    private static Result Bind(PublishBinding first, PublishBinding second)
    {
        var contract = NewContract();
        contract.AddPublish(first);
        return contract.AddPublish(second);
    }

    private static Result BindConsume(ConsumeBinding first, ConsumeBinding second)
    {
        var contract = NewContract();
        contract.AddConsume(first);
        return contract.AddConsume(second);
    }
}
