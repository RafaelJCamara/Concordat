using Concordat.Application.Abstractions;
using Concordat.Domain.Billing;
using Concordat.Domain.Registry;
using Concordat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Concordat.Infrastructure.Billing;

/// <summary>
/// Counts what a tenant is using, straight from the rows (M9.3).
/// </summary>
/// <remarks>
/// The counts are taken with the tenant supplied explicitly rather than relying on the global
/// query filters. Both would work here, but a meter that silently returned the *caller's*
/// usage when asked about somebody else is the kind of thing that produces a plausible invoice
/// for the wrong organisation.
/// </remarks>
internal sealed class UsageMeter(ConcordatDbContext context, TimeProvider clock) : IUsageMeter
{
    /// <inheritdoc />
    public async Task<UsageReport> MeasureAsync(
        TenantId tenantId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);

        var environments = await context.Environments
            .IgnoreQueryFilters()
            .CountAsync(
                e => EF.Property<Guid>(e, EnvironmentConfiguration.TenantIdProperty) ==
                     tenantId.Value,
                cancellationToken)
            .ConfigureAwait(false);

        var subjects = await context.Subjects
            .IgnoreQueryFilters()
            .CountAsync(
                s => EF.Property<Guid>(s, SubjectConfiguration.TenantIdProperty) == tenantId.Value,
                cancellationToken)
            .ConfigureAwait(false);

        var seats = await context.Memberships
            .CountAsync(m => m.TenantId == tenantId, cancellationToken)
            .ConfigureAwait(false);

        // Every version this tenant registered since the first of the month. Counted through
        // the owning subject because a version has no tenant column of its own — it is a child
        // of an aggregate that does.
        var versions = await context.Subjects
            .IgnoreQueryFilters()
            .Where(s => EF.Property<Guid>(s, SubjectConfiguration.TenantIdProperty) == tenantId.Value)
            .SelectMany(s => s.Versions)
            .CountAsync(v => v.RegisteredAt >= monthStart, cancellationToken)
            .ConfigureAwait(false);

        return new UsageReport(environments, subjects, seats, versions, now);
    }
}

/// <summary>
/// The gate a deployment that does not bill uses (M9.3).
/// </summary>
/// <remarks>
/// Says yes to everything, and is registered by the self-hosted profile. ADR-009 puts every
/// feature under Apache-2.0 and a self-hosted install has nobody to bill, so this is the
/// honest implementation rather than a stub — there is no plan to consult and no limit to
/// reach.
/// </remarks>
public sealed class UnmeteredBillingGate : IBillingGate
{
    /// <inheritdoc />
    public Task<BillingVerdict> MayCreateAsync(
        Meter meter, CancellationToken cancellationToken) =>
        Task.FromResult(BillingVerdict.Yes);
}

/// <summary>
/// The gate a Cloud deployment uses (M9.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>An organisation with no subscription row is allowed everything, not restricted to Free.</b>
/// The row is created at signup; a missing one means something went wrong in provisioning, and
/// the response to an internal inconsistency should not be to silently downgrade a paying
/// customer. It fails open on purpose, and it is a narrow opening: creating a subscription is
/// part of the same transaction as creating the organisation.
/// </para>
/// <para>
/// A cancelled subscription refuses creation. A past-due one does not — see
/// <see cref="SubscriptionStatus.PastDue"/>.
/// </para>
/// </remarks>
internal sealed class MeteredBillingGate(
    IBillingSubscriptionRepository subscriptions,
    IUsageMeter meter,
    ITenantContext tenant) : IBillingGate
{
    /// <inheritdoc />
    public async Task<BillingVerdict> MayCreateAsync(
        Meter what, CancellationToken cancellationToken)
    {
        var tenantId = tenant.Current;

        var subscription = await subscriptions.FindAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (subscription is null)
        {
            return BillingVerdict.Yes;
        }

        if (!subscription.AllowsCreation)
        {
            return new BillingVerdict(false, Limit: 0, Tier: subscription.Tier);
        }

        var limits = subscription.Limits;

        var limit = what switch
        {
            Meter.Environments => limits.Environments,
            Meter.Subjects => limits.Subjects,
            Meter.Seats => limits.Seats,
            Meter.VersionsPerMonth => limits.VersionsPerMonth,
            _ => throw new ArgumentOutOfRangeException(nameof(what), what, "Unknown meter."),
        };

        if (limit is null)
        {
            return BillingVerdict.Yes;
        }

        // Measured only once a limit is known to exist. An unlimited tier is the common case in
        // Cloud's paying customers and should not pay for four COUNT(*) queries per create.
        var usage = await meter.MeasureAsync(tenantId, cancellationToken).ConfigureAwait(false);

        var current = what switch
        {
            Meter.Environments => usage.Environments,
            Meter.Subjects => usage.Subjects,
            Meter.Seats => usage.Seats,
            Meter.VersionsPerMonth => usage.VersionsThisMonth,
            _ => 0,
        };

        return current < limit
            ? BillingVerdict.Yes
            : new BillingVerdict(false, limit, subscription.Tier);
    }
}
