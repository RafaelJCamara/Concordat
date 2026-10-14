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

    // ------------------------------------------------------- the name grammar (decision 22)

    [Theory]
    [InlineData("orders")]
    [InlineData("orders-v1")]
    [InlineData("payments.eu")]
    [InlineData("payments_eu_west")]
    [InlineData("v2")]
    public void AContractNameTakesTheSameShapeAnEnvironmentNameDoes(string name) =>
        Assert.True(Contract.Create(Env, name, Now).IsSuccess, name);

    [Theory]
    [InlineData("my contract")]        // a space
    [InlineData("orders/v1")]          // splits the path segment it is addressed by
    [InlineData("orders%2Fv1")]        // and so does its escaped form
    [InlineData("Orders — EU")]        // an em dash, which was legal until now
    [InlineData("-orders")]            // leading separator
    [InlineData("orders-")]            // trailing separator
    [InlineData("orders--v1")]         // repeated separator
    public void AContractNameOutsideTheGrammarIsRefused(string name)
    {
        // A contract is addressed at /v1/environments/{env}/contracts/{contract}, so a name
        // carrying '/' or '%' is not reliably addressable. This was legal until decision 22.
        var refused = Contract.Create(Env, name, Now);

        Assert.Equal(ConcordatCodes.ContractNameInvalid, refused.Error!.Code);
        Assert.Contains("lowercase", refused.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AContractNameIsFoldedNotPreserved()
    {
        // Folded like an environment name, unlike a subject name. A subject comes from a message
        // type where OrderCreated and ordercreated are different types; a contract name is typed
        // by a human into a URL.
        Assert.Equal("orders-v1", Contract.Create(Env, "Orders-V1", Now).Value.Name);
    }

    [Fact]
    public void AnOverlongContractNameIsRefused() =>
        Assert.Equal(
            ConcordatCodes.ContractNameInvalid,
            Contract.Create(Env, new string('a', Contract.MaxNameLength + 1), Now).Error!.Code);

    [Fact]
    public void TheBindingListsCannotBeMutatedThroughTheProperties()
    {
        var contract = NewContract();

        Assert.False(contract.Publishes is ICollection<PublishBinding> { IsReadOnly: false });
        Assert.False(contract.Consumes is ICollection<ConsumeBinding> { IsReadOnly: false });
    }

    // ------------------------------------------------- editing and removing a binding

    [Fact]
    public void EveryBindingHasItsOwnIdentity()
    {
        // What an edit or a removal addresses. A binding used to have none, so it could not be
        // named after it was added, and a binding added without a precedence could never be
        // separated from a later overlapping one.
        var contract = NewContract();
        contract.AddPublish(Publish("orders.created", "acme.A"));
        contract.AddPublish(Publish("orders.updated", "acme.A"));
        contract.AddConsume(new ConsumeBinding(TopologyScope.Default, "q", [Ref("acme.A")]));

        var ids = contract.Publishes.Select(b => b.Id)
            .Concat(contract.Consumes.Select(b => b.Id)).ToList();

        Assert.DoesNotContain(Guid.Empty, ids);
        Assert.Equal(3, ids.Distinct().Count());
    }

    [Fact]
    public void ThePrecedenceTrap_IsClosedByGivingTheFirstBindingOneAfterTheFact()
    {
        // The hole this closes, step by step. A is added with no precedence. B overlaps it and
        // carries other subjects, so it is refused with advice to "give the two bindings different
        // precedence values", and A has none to change.
        var contract = NewContract();
        contract.AddPublish(Publish("orders.#", "acme.Fallback"));
        var specific = Publish("orders.created", "acme.Created", precedence: 10);

        Assert.Equal(ConcordatCodes.BindingConflict, contract.AddPublish(specific).Error!.Code);

        var first = contract.Publishes.Single();
        var changed = contract.ReplacePublish(first.Id, first.Subjects, precedence: 1);

        Assert.True(changed.IsSuccess, changed.Error?.Message);
        Assert.True(changed.Value);
        Assert.Equal(1, first.Precedence);
        Assert.True(contract.AddPublish(specific).IsSuccess);
    }

    [Fact]
    public void ReplacingABindingChangesItsSubjectsAndPrecedenceAndNothingElse()
    {
        var contract = NewContract();
        contract.AddPublish(new PublishBinding(
            new TopologyScope(Guid.CreateVersion7(), "/v"), "ex", Pattern("a.*"), [Ref("acme.A")], 3));
        contract.AddPublish(Publish("zzz", "acme.Z"));
        var binding = contract.Publishes[0];
        var untouched = contract.Publishes[1];
        var id = binding.Id;
        var scope = binding.Scope;

        var result = contract.ReplacePublish(id, [Ref("acme.B", "2"), Ref("acme.C")], precedence: null);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(id, binding.Id);
        Assert.Equal(scope, binding.Scope);
        Assert.Equal("ex", binding.Exchange);
        Assert.Equal("a.*", binding.RoutingKeyPattern.Value);
        Assert.Null(binding.Precedence);
        Assert.Equal(["acme.B", "acme.C"], binding.Subjects.Select(s => s.Subject.Value));
        Assert.Equal("acme.Z", Assert.Single(untouched.Subjects).Subject.Value);
    }

    [Fact]
    public void ReplacingABindingWithWhatItAlreadyHasChangesNothing()
    {
        // Reported, so a caller can say so and write nothing, the rule a policy change follows.
        var contract = NewContract();
        contract.AddPublish(Publish("orders.created", "acme.A", precedence: 4));
        var binding = contract.Publishes.Single();

        var result = contract.ReplacePublish(binding.Id, [Ref("acme.A")], precedence: 4);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.False(result.Value);
    }

    [Fact]
    public void ReorderingSubjectsIsAChange()
    {
        // The order is stored and shown, so it is not nothing; only an identical request is.
        var contract = NewContract();
        contract.AddPublish(new PublishBinding(
            TopologyScope.Default, "ex", Pattern("a"), [Ref("acme.A"), Ref("acme.B")]));
        var binding = contract.Publishes.Single();

        var result = contract.ReplacePublish(binding.Id, [Ref("acme.B"), Ref("acme.A")], null);

        Assert.True(result.Value);
        Assert.Equal(["acme.B", "acme.A"], binding.Subjects.Select(s => s.Subject.Value));
    }

    [Fact]
    public void EqualisingPrecedenceWithAnOverlappingBindingIsRefusedAndChangesNothing()
    {
        // A deliberate ordering is the only thing that lets two overlapping bindings carry
        // different subjects, so an edit may not quietly destroy it.
        var contract = NewContract();
        contract.AddPublish(Publish("orders.#", "acme.Fallback", precedence: 1));
        contract.AddPublish(Publish("orders.created", "acme.Created", precedence: 10));
        var fallback = contract.Publishes[0];

        var result = contract.ReplacePublish(fallback.Id, fallback.Subjects, precedence: 10);

        Assert.Equal(ConcordatCodes.BindingConflict, result.Error!.Code);
        Assert.Equal(1, fallback.Precedence);
    }

    [Fact]
    public void ClearingThePrecedenceThatSeparatedTwoBindingsIsRefused()
    {
        var contract = NewContract();
        contract.AddPublish(Publish("orders.#", "acme.Fallback", precedence: 1));
        contract.AddPublish(Publish("orders.created", "acme.Created", precedence: 10));
        var specific = contract.Publishes[1];

        var result = contract.ReplacePublish(specific.Id, specific.Subjects, precedence: null);

        Assert.Equal(ConcordatCodes.BindingConflict, result.Error!.Code);
        Assert.Equal(10, specific.Precedence);
    }

    [Fact]
    public void ChangingSubjectsSoThatTwoOverlappingBindingsDisagreeIsRefusedAndChangesNothing()
    {
        var contract = NewContract();
        contract.AddPublish(Publish("orders.*", "acme.Same"));
        contract.AddPublish(Publish("*.created", "acme.Same"));
        var second = contract.Publishes[1];

        var result = contract.ReplacePublish(second.Id, [Ref("acme.Different")], precedence: null);

        Assert.Equal(ConcordatCodes.BindingConflict, result.Error!.Code);
        Assert.Contains("orders.created", result.Error.Message, StringComparison.Ordinal);
        Assert.Equal("acme.Same", Assert.Single(second.Subjects).Subject.Value);
    }

    [Fact]
    public void ABindingIsNotInConflictWithItself()
    {
        // The check skips the binding being changed: it overlaps itself by definition.
        var contract = NewContract();
        contract.AddPublish(Publish("orders.*", "acme.A"));
        var only = contract.Publishes.Single();

        Assert.True(contract.ReplacePublish(only.Id, [Ref("acme.B")], precedence: null).IsSuccess);
    }

    [Fact]
    public void ReplacingAnUnknownPublishBindingIsBindingNotFound()
    {
        var contract = NewContract();
        contract.AddPublish(Publish("orders.created", "acme.A"));

        var result = contract.ReplacePublish(Guid.CreateVersion7(), [Ref("acme.A")], null);

        Assert.Equal(ConcordatCodes.BindingNotFound, result.Error!.Code);
    }

    [Fact]
    public void ARemovedPublishBindingIsGoneAndTheOthersAreUntouched()
    {
        var contract = NewContract();
        contract.AddPublish(Publish("orders.created", "acme.A"));
        contract.AddPublish(Publish("orders.updated", "acme.B"));
        var gone = contract.Publishes[0].Id;
        var kept = contract.Publishes[1];

        var result = contract.RemovePublish(gone);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Same(kept, Assert.Single(contract.Publishes));
        Assert.Empty(contract.ResolvePublish(Guid.CreateVersion7(), "/", "orders", "orders.created"));
    }

    [Fact]
    public void RemovingAnUnknownOrAlreadyRemovedPublishBindingIsBindingNotFound()
    {
        var contract = NewContract();
        contract.AddPublish(Publish("orders.created", "acme.A"));
        var id = contract.Publishes.Single().Id;
        contract.RemovePublish(id);

        Assert.Equal(ConcordatCodes.BindingNotFound, contract.RemovePublish(id).Error!.Code);
        Assert.Equal(
            ConcordatCodes.BindingNotFound,
            contract.RemovePublish(Guid.CreateVersion7()).Error!.Code);
    }

    [Fact]
    public void ABindingCannotBeRemovedThroughTheOtherKindsId()
    {
        // A publish id is not a consume id: removing by the wrong kind must not find anything.
        var contract = NewContract();
        contract.AddPublish(Publish("orders.created", "acme.A"));
        contract.AddConsume(new ConsumeBinding(TopologyScope.Default, "q", [Ref("acme.A")]));

        Assert.Equal(
            ConcordatCodes.BindingNotFound,
            contract.RemoveConsume(contract.Publishes.Single().Id).Error!.Code);
        Assert.Equal(
            ConcordatCodes.BindingNotFound,
            contract.RemovePublish(contract.Consumes.Single().Id).Error!.Code);
        Assert.Single(contract.Publishes);
        Assert.Single(contract.Consumes);
    }

    [Fact]
    public void ReplacingAConsumeBindingChangesItsSubjectsAndNothingElse()
    {
        var contract = NewContract();
        contract.AddConsume(new ConsumeBinding(TopologyScope.Default, "q", [Ref("acme.A")]));
        var binding = contract.Consumes.Single();

        var result = contract.ReplaceConsume(binding.Id, [Ref("acme.B", "3")]);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(result.Value);
        Assert.Equal("q", binding.Queue);
        Assert.Equal("acme.B", Assert.Single(binding.Subjects).Subject.Value);
        Assert.False(contract.ReplaceConsume(binding.Id, [Ref("acme.B", "3")]).Value);
    }

    [Fact]
    public void ChangingAConsumeBindingSoThatTheQueueDisagreesWithAnotherIsRefused()
    {
        // Two bindings for one queue that overlap in scope must agree, as when they were added.
        var contract = NewContract();
        contract.AddConsume(new ConsumeBinding(TopologyScope.Default, "q", [Ref("acme.A")]));
        var second = new ConsumeBinding(TopologyScope.Default, "q", [Ref("acme.A")]);
        contract.AddConsume(second);

        var clash = contract.ReplaceConsume(second.Id, [Ref("acme.D")]);

        Assert.Equal(ConcordatCodes.BindingConflict, clash.Error!.Code);
        Assert.Equal("acme.A", Assert.Single(second.Subjects).Subject.Value);
    }

    [Fact]
    public void ARemovedConsumeBindingIsGone()
    {
        var contract = NewContract();
        contract.AddConsume(new ConsumeBinding(TopologyScope.Default, "q", [Ref("acme.A")]));

        Assert.True(contract.RemoveConsume(contract.Consumes.Single().Id).IsSuccess);
        Assert.Null(contract.ResolveConsume(Guid.CreateVersion7(), "/", "q"));
        Assert.Equal(
            ConcordatCodes.BindingNotFound,
            contract.RemoveConsume(Guid.CreateVersion7()).Error!.Code);
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
