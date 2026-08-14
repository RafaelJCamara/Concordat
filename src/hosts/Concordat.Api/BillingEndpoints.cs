using Concordat.Application.Abstractions;
using Concordat.Domain.Billing;
using Concordat.Domain.Identity;

namespace Concordat.Api;

/// <summary>M9.3's usage and plan routes.</summary>
public static class BillingEndpoints
{
    /// <summary>Maps the billing routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapBillingEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/v1/usage", Usage)
            .WithTags("Billing")
            .WithSummary("What this organisation is using, and what its plan allows")
            .WithDescription(
                "Counts are measured from the rows themselves rather than from a meter, so " +
                "there is nothing to drift. A self-hosted deployment has no plan and reports " +
                "no limits.")
            .RequireScope(Scope.OrgAdmin)
            .Produces<UsageResponse>();

        return app;
    }

    private static async Task<IResult> Usage(
        IUsageMeter meter,
        IBillingSubscriptionRepository subscriptions,
        ITenantContext tenant,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(meter);
        ArgumentNullException.ThrowIfNull(subscriptions);
        ArgumentNullException.ThrowIfNull(tenant);

        var tenantId = tenant.Current;

        var usage = await meter.MeasureAsync(tenantId, cancellationToken).ConfigureAwait(false);

        var subscription = await subscriptions.FindAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(UsageResponse.From(usage, subscription));
    }
}

/// <summary>One thing being counted, and what the plan allows of it.</summary>
/// <param name="Used">How many exist now.</param>
/// <param name="Limit">The most allowed, or null when unlimited.</param>
public sealed record MeterResponse(int Used, int? Limit);

/// <summary>What an organisation is using.</summary>
/// <param name="Tier">The tier in force, or null on a deployment that does not bill.</param>
/// <param name="Status">The subscription's status, or null.</param>
/// <param name="Environments">Environments.</param>
/// <param name="Subjects">Subjects, retired ones included.</param>
/// <param name="Seats">Members.</param>
/// <param name="VersionsThisMonth">Versions registered since the first of the month.</param>
/// <param name="MeasuredAt">When the counts were taken.</param>
public sealed record UsageResponse(
    string? Tier,
    string? Status,
    MeterResponse Environments,
    MeterResponse Subjects,
    MeterResponse Seats,
    MeterResponse VersionsThisMonth,
    DateTimeOffset MeasuredAt)
{
    /// <summary>Projects a measurement against whatever plan is in force.</summary>
    /// <param name="usage">The counts.</param>
    /// <param name="subscription">The subscription, or null when nobody is billing.</param>
    /// <returns>The wire form.</returns>
    public static UsageResponse From(UsageReport usage, Subscription? subscription)
    {
        ArgumentNullException.ThrowIfNull(usage);

        // No subscription means no limits reported, rather than the Free tier's. A self-hosted
        // install is not on a plan, and showing one would imply a ceiling that does not exist.
        var limits = subscription?.Limits;

        return new UsageResponse(
            subscription is null ? null : Tiers.For(subscription.Tier),
            subscription?.Status.ToString().ToUpperInvariant(),
            new MeterResponse(usage.Environments, limits?.Environments),
            new MeterResponse(usage.Subjects, limits?.Subjects),
            new MeterResponse(usage.Seats, limits?.Seats),
            new MeterResponse(usage.VersionsThisMonth, limits?.VersionsPerMonth),
            usage.MeasuredAt);
    }
}
