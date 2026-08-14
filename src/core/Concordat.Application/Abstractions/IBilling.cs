using Concordat.Domain.Billing;
using Concordat.Domain.Registry;

namespace Concordat.Application.Abstractions;

/// <summary>What a tenant is currently using (M9.3).</summary>
/// <param name="Environments">How many environments exist.</param>
/// <param name="Subjects">How many subjects exist, retired ones included.</param>
/// <param name="Seats">How many members the organisation has.</param>
/// <param name="VersionsThisMonth">Versions registered since the start of the calendar month.</param>
/// <param name="MeasuredAt">When the counts were taken.</param>
public sealed record UsageReport(
    int Environments,
    int Subjects,
    int Seats,
    int VersionsThisMonth,
    DateTimeOffset MeasuredAt);

/// <summary>
/// Counts what a tenant is using (M9.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Measured by query, not by counter.</b> Every figure here is derived from rows that
/// already exist, so there is no meter to increment, nothing to drift, and no reconciliation
/// job. A counter would have to be correct across a failed transaction, a restore from backup
/// and a retried request; a <c>COUNT(*)</c> is correct by construction and cheap at the sizes
/// these limits describe.
/// </para>
/// <para>
/// <b>API requests are deliberately absent</b>, and DESIGN §10 lists them. Counting a request
/// per request is a write on the read path — the SDK's schema lookups are the highest-volume
/// traffic this registry sees, and turning each into a row would cost more than the thing being
/// measured. It needs sampling or an aggregation pipeline, which is a different design, so it
/// is recorded as not done rather than approximated badly.
/// </para>
/// </remarks>
public interface IUsageMeter
{
    /// <summary>Counts what a tenant is using right now.</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The counts.</returns>
    Task<UsageReport> MeasureAsync(TenantId tenantId, CancellationToken cancellationToken);
}

/// <summary>What a tenant is allowed to create more of.</summary>
public enum Meter
{
    /// <summary>Environments.</summary>
    Environments,

    /// <summary>Subjects.</summary>
    Subjects,

    /// <summary>Members.</summary>
    Seats,

    /// <summary>Versions registered this calendar month.</summary>
    VersionsPerMonth,
}

/// <summary>
/// Decides whether a tenant may create one more of something (M9.1, M9.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>It gates creation and nothing else.</b> Reads are never refused, existing resources never
/// stop working, and no message is ever rejected for a billing reason. The registry sits on the
/// delivery path: a plan limit that could stop a consumer resolving a schema would turn a
/// billing dispute into a production outage, which is a far larger event than an unpaid
/// invoice and one no customer would forgive.
/// </para>
/// <para>
/// <b>Self-hosted allows everything.</b> Not by a flag checked here, but because the
/// self-hosted registration is an implementation that says yes — ADR-009 puts every feature
/// under Apache-2.0, and a self-hosted deployment has nobody to bill.
/// </para>
/// </remarks>
public interface IBillingGate
{
    /// <summary>Whether one more of something may be created.</summary>
    /// <param name="meter">What is being created.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The verdict, and what to tell the caller when it is no.</returns>
    Task<BillingVerdict> MayCreateAsync(Meter meter, CancellationToken cancellationToken);
}

/// <summary>Whether something may be created, and why not.</summary>
/// <param name="Allowed">Whether to proceed.</param>
/// <param name="Limit">The limit that was reached, when one was.</param>
/// <param name="Tier">The tier in force, when there is a subscription.</param>
public sealed record BillingVerdict(bool Allowed, int? Limit = null, Tier? Tier = null)
{
    /// <summary>The answer a deployment that does not bill always gives.</summary>
    public static BillingVerdict Yes { get; } = new(true);
}

/// <summary>
/// Reads and writes billing subscriptions (M9.3).
/// </summary>
/// <remarks>
/// Named for the billing context rather than shortened to <c>ISubscriptionRepository</c>,
/// which M7.5 already uses for notification subscriptions. Two unrelated things called
/// "subscription" is the sort of collision that gets the wrong one injected.
/// </remarks>
public interface IBillingSubscriptionRepository
{
    /// <summary>Finds a tenant's subscription.</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The subscription, or <see langword="null"/> when nobody is billing them.</returns>
    Task<Subscription?> FindAsync(TenantId tenantId, CancellationToken cancellationToken);

    /// <summary>Stages a new subscription for insert.</summary>
    /// <param name="subscription">The subscription.</param>
    void Add(Subscription subscription);
}
