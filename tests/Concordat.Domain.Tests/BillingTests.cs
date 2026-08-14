using Concordat.Domain.Billing;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;

namespace Concordat.Domain.Tests;

/// <summary>M9.3's plans and what they allow.</summary>
public class PlanLimitsTests
{
    [Fact]
    public void FreeMatchesWhatThePlanDocumentPromises()
    {
        // DESIGN §10 and the M9 plan both say "Free (1 env, 10 subjects)". A tier whose limits
        // drift from the published ones is a pricing page that lies.
        var free = PlanLimits.For(Tier.Free);

        Assert.Equal(1, free.Environments);
        Assert.Equal(10, free.Subjects);
    }

    [Fact]
    public void LimitsAreMonotonicAcrossTiers()
    {
        // A tier that allowed less than a cheaper one would be an upgrade nobody should buy,
        // and the kind of thing a typo produces silently.
        var tiers = new[] { Tier.Free, Tier.Team, Tier.Business };

        for (var i = 1; i < tiers.Length; i++)
        {
            var cheaper = PlanLimits.For(tiers[i - 1]);
            var dearer = PlanLimits.For(tiers[i]);

            Assert.True(dearer.Environments > cheaper.Environments, $"{tiers[i]} environments");
            Assert.True(dearer.Subjects > cheaper.Subjects, $"{tiers[i]} subjects");
            Assert.True(dearer.Seats > cheaper.Seats, $"{tiers[i]} seats");
            Assert.True(
                dearer.VersionsPerMonth > cheaper.VersionsPerMonth, $"{tiers[i]} versions");
        }
    }

    [Fact]
    public void EnterpriseIsUnlimitedRatherThanVeryLarge()
    {
        // Null, not int.MaxValue. "Negotiated" should not mean "somebody picked a big number",
        // and a very large limit is still a limit somebody eventually hits at 3am.
        var enterprise = PlanLimits.For(Tier.Enterprise);

        Assert.Null(enterprise.Environments);
        Assert.Null(enterprise.Subjects);
        Assert.Null(enterprise.Seats);
        Assert.Null(enterprise.VersionsPerMonth);
    }

    [Fact]
    public void EveryTierResolves() =>
        Assert.All(Enum.GetValues<Tier>(), t => Assert.Equal(t, PlanLimits.For(t).Tier));

    [Theory]
    [InlineData(Tier.Free, "FREE")]
    [InlineData(Tier.Team, "TEAM")]
    [InlineData(Tier.Business, "BUSINESS")]
    [InlineData(Tier.Enterprise, "ENTERPRISE")]
    public void TierTokensRoundTrip(Tier tier, string expected)
    {
        Assert.Equal(expected, Tiers.For(tier));

        Assert.True(Tiers.Parse(expected, out var parsed).IsSuccess);
        Assert.Equal(tier, parsed);
    }

    [Fact]
    public void AnUnknownTierFallsBackToTheLeastGenerousOne()
    {
        // The same rule as Roles.Parse: a caller that ignores the Result must not be handed the
        // best plan.
        var result = Tiers.Parse("PLATINUM", out var tier);

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.TierInvalid, result.Error!.Code);
        Assert.Equal(Tier.Free, tier);
    }
}

/// <summary>M9.3's subscriptions.</summary>
public class SubscriptionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private static Subscription New(Tier tier = Tier.Team) =>
        Subscription.Start(TenantId.New(), tier, Now);

    [Fact]
    public void ANewSubscriptionIsActiveAndCarriesItsTiersLimits()
    {
        var subscription = New(Tier.Business);

        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
        Assert.Equal(PlanLimits.For(Tier.Business), subscription.Limits);
        Assert.True(subscription.AllowsCreation);
    }

    [Fact]
    public void APastDueSubscriptionStillAllowsEverything()
    {
        // The decision that matters most in this file. A card that expired over a weekend must
        // not stop a team registering a schema: the registry sits on the delivery path, and a
        // billing problem that becomes a production problem is a far larger incident than an
        // unpaid invoice.
        var subscription = New();

        subscription.SetStatus(SubscriptionStatus.PastDue, Now);

        Assert.True(subscription.AllowsCreation);
    }

    [Fact]
    public void ACancelledSubscriptionStopsCreation()
    {
        var subscription = New();

        subscription.SetStatus(SubscriptionStatus.Cancelled, Now);

        Assert.False(subscription.AllowsCreation);
    }

    [Fact]
    public void ADowngradeIsNeverRefusedForBeingOverTheNewLimit()
    {
        // Refusing would strand an organisation on a plan they no longer want to pay for, with
        // the only way out being to delete things — and deletion here means retiring subjects
        // other teams depend on. Being over a limit stops you creating more; it never takes
        // away what you have.
        var subscription = New(Tier.Business);

        var result = subscription.ChangeTier(Tier.Free, Now);

        Assert.True(result.IsSuccess);
        Assert.Equal(Tier.Free, subscription.Tier);
    }

    [Fact]
    public void ProviderIdentifiersCanBeLinkedAfterTheFact()
    {
        // The subscription row is created with the organisation; the provider's ids arrive when
        // checkout completes, which is a different request and possibly a much later one.
        var subscription = New();
        Assert.Null(subscription.ProviderCustomerId);

        subscription.LinkProvider("cus_123", "sub_456");

        Assert.Equal("cus_123", subscription.ProviderCustomerId);
        Assert.Equal("sub_456", subscription.ProviderSubscriptionId);
    }

    [Fact]
    public void TimestampsAreNormalisedToUtc()
    {
        var local = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.FromHours(9));

        var subscription = Subscription.Start(TenantId.New(), Tier.Team, local);

        Assert.Equal(TimeSpan.Zero, subscription.StartedAt.Offset);
        Assert.Equal(local.UtcDateTime, subscription.StartedAt.UtcDateTime);
    }
}
