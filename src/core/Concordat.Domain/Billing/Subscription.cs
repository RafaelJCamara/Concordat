using Concordat.Domain.Registry;
using Concordat.Domain.Results;

namespace Concordat.Domain.Billing;

/// <summary>Where a subscription stands with the payment provider (M9.3).</summary>
public enum SubscriptionStatus
{
    /// <summary>Paid up, or on a tier that costs nothing.</summary>
    Active = 1,

    /// <summary>
    /// A payment failed and the provider is retrying.
    /// </summary>
    /// <remarks>
    /// <b>Still allows everything an active subscription does.</b> A card that expired over a
    /// weekend must not stop a team registering a schema — the registry sits on the delivery
    /// path, and a billing problem that becomes a production problem is a much larger incident
    /// than an unpaid invoice.
    /// </remarks>
    PastDue = 2,

    /// <summary>Ended. New resources are refused; everything already there keeps working.</summary>
    Cancelled = 3,
}

/// <summary>
/// What a tenant is entitled to (M9.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Self-hosted has none, and that is not a gap.</b> ADR-009 puts every feature under
/// Apache-2.0; the subscription exists to answer "what has this organisation paid for" and a
/// deployment nobody is billing has no answer to give. <c>IBillingGate</c> allows everything
/// when there is no subscription, rather than falling back to the most restrictive plan.
/// </para>
/// <para>
/// The provider's own identifiers live here so that a webhook can find the subscription it is
/// about without a second lookup table, and so that reconciling an invoice against a tenant is
/// a join rather than an investigation.
/// </para>
/// </remarks>
public sealed class Subscription
{
    private Subscription(
        Guid id,
        TenantId tenantId,
        Tier tier,
        SubscriptionStatus status,
        string? providerCustomerId,
        string? providerSubscriptionId,
        DateTimeOffset startedAt)
    {
        Id = id;
        TenantId = tenantId;
        Tier = tier;
        Status = status;
        ProviderCustomerId = providerCustomerId;
        ProviderSubscriptionId = providerSubscriptionId;
        StartedAt = startedAt;
    }

    // Materialisation only.
    private Subscription()
    {
    }

    /// <summary>The subscription's identity.</summary>
    public Guid Id { get; }

    /// <summary>Which organisation.</summary>
    public TenantId TenantId { get; }

    /// <summary>What they are paying for.</summary>
    public Tier Tier { get; private set; }

    /// <summary>Where it stands with the provider.</summary>
    public SubscriptionStatus Status { get; private set; }

    /// <summary>The payment provider's customer identifier, when there is one.</summary>
    public string? ProviderCustomerId { get; private set; }

    /// <summary>The payment provider's subscription identifier, when there is one.</summary>
    public string? ProviderSubscriptionId { get; private set; }

    /// <summary>When it began.</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>When it was last changed.</summary>
    public DateTimeOffset? ChangedAt { get; private set; }

    /// <summary>What this subscription allows.</summary>
    public PlanLimits Limits => PlanLimits.For(Tier);

    /// <summary>
    /// Whether new resources may be created.
    /// </summary>
    /// <remarks>
    /// <see cref="SubscriptionStatus.PastDue"/> deliberately still allows them. See the remarks
    /// on that member: the registry is on the delivery path, and turning a failed card into a
    /// production incident is not a proportionate response to an unpaid invoice.
    /// </remarks>
    public bool AllowsCreation => Status is not SubscriptionStatus.Cancelled;

    /// <summary>Starts a subscription.</summary>
    /// <param name="tenantId">Which organisation.</param>
    /// <param name="tier">What they are paying for.</param>
    /// <param name="startedAt">When.</param>
    /// <param name="providerCustomerId">The provider's customer identifier.</param>
    /// <param name="providerSubscriptionId">The provider's subscription identifier.</param>
    /// <returns>The subscription.</returns>
    public static Subscription Start(
        TenantId tenantId,
        Tier tier,
        DateTimeOffset startedAt,
        string? providerCustomerId = null,
        string? providerSubscriptionId = null) =>
        new(Guid.CreateVersion7(),
            tenantId,
            tier,
            SubscriptionStatus.Active,
            providerCustomerId,
            providerSubscriptionId,
            startedAt.ToUniversalTime());

    /// <summary>Moves to another tier.</summary>
    /// <param name="tier">The new tier.</param>
    /// <param name="at">When.</param>
    /// <returns>Success. Downgrading below current usage is allowed — see remarks.</returns>
    /// <remarks>
    /// <b>A downgrade is never refused for being over the new limit.</b> Refusing would leave
    /// an organisation stuck on a plan they no longer want to pay for, with the only way out
    /// being to delete things — and deletion in this product means retiring subjects other
    /// teams depend on. Being over a limit stops you creating more; it never takes away what
    /// you have.
    /// </remarks>
    public Result ChangeTier(Tier tier, DateTimeOffset at)
    {
        Tier = tier;
        ChangedAt = at.ToUniversalTime();
        return Result.Success();
    }

    /// <summary>Records what the payment provider says.</summary>
    /// <param name="status">The new status.</param>
    /// <param name="at">When.</param>
    public void SetStatus(SubscriptionStatus status, DateTimeOffset at)
    {
        Status = status;
        ChangedAt = at.ToUniversalTime();
    }

    /// <summary>Links the payment provider's identifiers.</summary>
    /// <param name="customerId">The provider's customer identifier.</param>
    /// <param name="subscriptionId">The provider's subscription identifier.</param>
    public void LinkProvider(string? customerId, string? subscriptionId)
    {
        ProviderCustomerId = customerId;
        ProviderSubscriptionId = subscriptionId;
    }
}
