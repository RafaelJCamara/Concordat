using Concordat.Application.Abstractions;
using Concordat.Domain.Registry;
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
public sealed class ConcordatDbContext : DbContext
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

    /// <summary>The tenant this context instance is bound to.</summary>
    internal TenantId CurrentTenant => _tenantContext.Current;

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfiguration(new SchemaConfiguration());
        modelBuilder.ApplyConfiguration(new SubjectConfiguration());
        modelBuilder.ApplyConfiguration(new EnvironmentConfiguration());

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

        foreach (var entry in ChangeTracker.Entries<Subject>())
        {
            if (entry.State is EntityState.Added)
            {
                entry.Property<Guid>(SubjectConfiguration.TenantIdProperty).CurrentValue = tenant;
            }
        }
    }
}
