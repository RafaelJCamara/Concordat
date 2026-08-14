using Concordat.Application.Abstractions;
using Concordat.Domain.Billing;
using Concordat.Domain.Contracts;
using Concordat.Domain.Governance;
using Concordat.Domain.Identity;
using Concordat.Domain.Registry;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

using Environment = Concordat.Domain.Registry.Environment;

namespace Concordat.Infrastructure.Persistence;

/// <summary>
/// The registry's PostgreSQL context (ADR-007).
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not the Confluent/Karapace/Redpanda pattern of storing schemas in a broker
/// log: RabbitMQ is the thing being governed, and putting registry state inside it creates a
/// circular failure mode where a broker outage takes down the thing that says the broker's
/// traffic is valid.
/// </para>
/// <para>
/// Tenant isolation is enforced by global query filters rather than by remembering to add a
/// predicate. <see cref="Schema"/> is the deliberate exception — it is global and
/// deduplicated by content (ADR-015).
/// </para>
/// </remarks>
public sealed class ConcordatDbContext : DbContext, IDataProtectionKeyContext
{
    private readonly ITenantContext _tenantContext;

    /// <summary>Creates the context.</summary>
    /// <param name="options">Provider and connection configuration.</param>
    /// <param name="tenantContext">Supplies the tenant every filtered query is scoped to.</param>
    public ConcordatDbContext(DbContextOptions<ConcordatDbContext> options, ITenantContext tenantContext)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(tenantContext);
        _tenantContext = tenantContext;
    }

    /// <summary>Subjects, scoped to the current tenant.</summary>
    public DbSet<Subject> Subjects => Set<Subject>();

    /// <summary>
    /// Schemas. <b>Global, not tenant-scoped</b> — the same content is one row for everyone.
    /// </summary>
    /// <remarks>
    /// Because there is no tenant column to filter on, reads of a schema by id must be
    /// authorised by <em>reachability</em> from a subject in the caller's tenant. That is
    /// M1.6's obligation and is not enforced here.
    /// </remarks>
    public DbSet<Schema> Schemas => Set<Schema>();

    /// <summary>Environments and their brokers, scoped to the current tenant (M7.1).</summary>
    public DbSet<Environment> Environments => Set<Environment>();

    /// <summary>
    /// The Data Protection key ring (M7.2).
    /// </summary>
    /// <remarks>
    /// In the database rather than on disk, so a second API instance can decrypt what the
    /// first one wrote. A disk ring is the framework default and would be the wrong default
    /// here: a containerised registry restarting without a mounted volume would generate a
    /// fresh key, and every stored broker credential would become permanently unreadable with
    /// nothing to say why.
    /// </remarks>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    /// <summary>Contracts, scoped to the current tenant (M7.3).</summary>
    public DbSet<Contract> Contracts => Set<Contract>();

    /// <summary>Declared producer and consumer intent, scoped to the current tenant (M7.4).</summary>
    public DbSet<ServiceRegistration> Services => Set<ServiceRegistration>();

    /// <summary>The audit trail, scoped to the current tenant (M7.4).</summary>
    /// <remarks>Append-only: nothing in the model or the repository issues an update or a delete.</remarks>
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    /// <summary>
    /// The deployment-level trail, deliberately <b>not</b> scoped to a tenant (decision 29).
    /// </summary>
    /// <remarks>
    /// The one table here with no tenant filter, because its rows record things that happened
    /// above a tenant or before one existed — a signup has no authenticated caller to take a
    /// scope from. Nothing in a request handler reads it; see <c>IDeploymentLog</c>.
    /// </remarks>
    public DbSet<DeploymentEvent> DeploymentEvents => Set<DeploymentEvent>();

    /// <summary>Notifications staged for delivery, scoped to the current tenant (M7.5).</summary>
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    /// <summary>Who wants to hear about what, scoped to the current tenant (M7.5).</summary>
    public DbSet<NotificationSubscription> Subscriptions => Set<NotificationSubscription>();

    /// <summary>
    /// Local accounts (M8.1). <b>Global, not tenant-scoped</b> — a login belongs to a person,
    /// and which tenants they may act in is what <see cref="Memberships"/> answers. Filtering
    /// these by tenant would make sign-in circular: the request has to find the account before
    /// it can know which tenant to filter by.
    /// </summary>
    public DbSet<User> Users => Set<User>();

    /// <summary>
    /// Organisations (M9.1). Global by necessity — this table is what everything else is
    /// filtered <em>by</em>, so a filter here would stop an organisation reading its own row.
    /// </summary>
    public DbSet<Tenant> Tenants => Set<Tenant>();

    /// <summary>
    /// Billing subscriptions (M9.3). Named apart from <see cref="Subscriptions"/>, which is
    /// M7.5's notification subscriptions — two unrelated things called "subscription" is the
    /// sort of collision that gets the wrong one queried.
    /// </summary>
    public DbSet<Subscription> BillingSubscriptions => Set<Subscription>();

    /// <summary>Which tenants a user belongs to, and as what (M8.1).</summary>
    public DbSet<Membership> Memberships => Set<Membership>();

    /// <summary>
    /// API keys (M8.1). Global for the same reason as <see cref="Users"/>: authentication is
    /// what establishes the tenant, so a filter here would be circular.
    /// </summary>
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    /// <summary>The tenant this context instance is bound to.</summary>
    internal TenantId CurrentTenant => _tenantContext.Current;

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfiguration(new SchemaConfiguration());
        modelBuilder.ApplyConfiguration(new SubjectConfiguration());
        modelBuilder.ApplyConfiguration(new EnvironmentConfiguration());
        modelBuilder.ApplyConfiguration(new StoredCredentialConfiguration());
        modelBuilder.ApplyConfiguration(new ContractConfiguration());
        modelBuilder.ApplyConfiguration(new ServiceRegistrationConfiguration());
        modelBuilder.ApplyConfiguration(new AuditEntryConfiguration());
        modelBuilder.ApplyConfiguration(new DeploymentEventConfiguration());
        modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
        modelBuilder.ApplyConfiguration(new NotificationSubscriptionConfiguration());
        modelBuilder.ApplyConfiguration(new TenantConfiguration());
        modelBuilder.ApplyConfiguration(new SubscriptionConfiguration());
        modelBuilder.ApplyConfiguration(new UserConfiguration());
        modelBuilder.ApplyConfiguration(new MembershipConfiguration());
        modelBuilder.ApplyConfiguration(new ApiKeyConfiguration());

        // Isolation by construction rather than by remembering a predicate. Every query
        // against Subjects is filtered whether or not the author thought about tenancy, which
        // is the whole point — the failure mode of the alternative is silently returning
        // another tenant's data.
        //
        // Schema carries no filter on purpose (ADR-015): it is global and deduplicated.
        modelBuilder.Entity<Subject>().HasQueryFilter(
            s => EF.Property<Guid>(s, SubjectConfiguration.TenantIdProperty) == CurrentTenant.Value);

        modelBuilder.Entity<Environment>().HasQueryFilter(
            e => EF.Property<Guid>(e, EnvironmentConfiguration.TenantIdProperty) == CurrentTenant.Value);

        modelBuilder.Entity<StoredCredential>().HasQueryFilter(
            c => EF.Property<Guid>(c, SubjectConfiguration.TenantIdProperty) == CurrentTenant.Value);

        modelBuilder.Entity<Contract>().HasQueryFilter(
            c => EF.Property<Guid>(c, ContractConfiguration.TenantIdProperty) == CurrentTenant.Value);

        modelBuilder.Entity<ServiceRegistration>().HasQueryFilter(
            s => EF.Property<Guid>(s, ServiceRegistrationConfiguration.TenantIdProperty) ==
                 CurrentTenant.Value);

        modelBuilder.Entity<AuditEntry>().HasQueryFilter(
            e => EF.Property<Guid>(e, AuditEntryConfiguration.TenantIdProperty) ==
                 CurrentTenant.Value);

        modelBuilder.Entity<OutboxMessage>().HasQueryFilter(
            m => EF.Property<Guid>(m, OutboxMessageConfiguration.TenantIdProperty) ==
                 CurrentTenant.Value);

        modelBuilder.Entity<NotificationSubscription>().HasQueryFilter(
            s => EF.Property<Guid>(s, NotificationSubscriptionConfiguration.TenantIdProperty) ==
                 CurrentTenant.Value);

        base.OnModelCreating(modelBuilder);
    }

    /// <inheritdoc />
    public override int SaveChanges()
    {
        StampTenant();
        return base.SaveChanges();
    }

    /// <inheritdoc />
    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampTenant();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Writes the current tenant onto every newly added tenant-scoped entity.
    /// </summary>
    /// <remarks>
    /// Paired with the query filter: the filter stops you reading another tenant's rows, and
    /// this stops you writing a row with no tenant — which the filter would then hide from
    /// everyone, including its author.
    /// </remarks>
    private void StampTenant()
    {
        var tenant = _tenantContext.Current.Value;

        // Every added entity that declares the tenant shadow property, rather than a loop per
        // aggregate. The per-aggregate version was already wrong once: M7.1's Environment was
        // written with an empty tenant and then hidden by its own query filter — precisely the
        // failure described above, and invisible until a read came back empty. A third
        // aggregate arriving in M7.3 would have repeated it.
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State is not EntityState.Added)
            {
                continue;
            }

            // Shadow properties only, and the qualifier is load-bearing. M8's Membership and
            // ApiKey carry a real, strongly-typed TenantId property with exactly this name --
            // matching on the name alone assigned a Guid to a TenantId and threw
            // InvalidCastException on the first sign-in. A shadow property is what "this table
            // is tenant-isolated" has always meant here; an aggregate that owns its tenant as
            // domain state sets it itself.
            if (entry.Metadata.FindProperty(SubjectConfiguration.TenantIdProperty)
                is { } property && property.IsShadowProperty())
            {
                entry.Property(SubjectConfiguration.TenantIdProperty).CurrentValue = tenant;
            }
        }
    }
}
