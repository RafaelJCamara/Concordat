using Concordat.Domain.Contracts;
using Concordat.Domain.Registry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Concordat.Infrastructure.Persistence;

/// <summary>
/// Maps the <see cref="Contract"/> aggregate and its bindings (M7.3).
/// </summary>
/// <remarks>
/// A binding's subjects are stored as one text column rather than a child table; see
/// <see cref="SubjectRefColumn"/> for why, and for the value comparer that makes edits save.
/// </remarks>
internal sealed class ContractConfiguration : IEntityTypeConfiguration<Contract>
{
    /// <summary>The shadow column carrying tenant ownership.</summary>
    internal const string TenantIdProperty = "TenantId";

    public void Configure(EntityTypeBuilder<Contract> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("contract");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Id).HasColumnName("contract_id").ValueGeneratedNever();

        builder.Property(c => c.EnvironmentId)
            .HasColumnName("environment_id")
            .HasConversion(id => id.Value, value => new EnvironmentId(value));

        builder.Property(c => c.Name).HasColumnName("name").HasMaxLength(128).IsRequired();

        builder.Property(c => c.Enforcement)
            .HasColumnName("enforcement")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(c => c.CreatedAt).HasColumnName("created_at");
        builder.Property<Guid>(TenantIdProperty).HasColumnName("tenant_id");

        builder.HasIndex(TenantIdProperty, nameof(Contract.EnvironmentId), nameof(Contract.Name))
            .IsUnique()
            .HasDatabaseName("ix_contract_tenant_environment_name");

        builder.OwnsMany(c => c.Publishes, publish =>
        {
            publish.ToTable("publish_binding");
            publish.WithOwner().HasForeignKey("contract_id");

            publish.Property(b => b.Exchange).HasColumnName("exchange").HasMaxLength(255)
                .IsRequired();

            publish.Property(b => b.RoutingKeyPattern)
                .HasColumnName("routing_key_pattern")
                .HasConversion(p => p.Value, value => RoutingKeyPattern.FromTrusted(value))
                .HasMaxLength(RoutingKeyPattern.MaxLength)
                .IsRequired();

            publish.Property(b => b.Precedence).HasColumnName("precedence");

            publish.OwnsOne(b => b.Scope, scope =>
            {
                scope.Property(s => s.BrokerId).HasColumnName("broker_id");
                scope.Property(s => s.VirtualHost).HasColumnName("virtual_host")
                    .HasMaxLength(256).IsRequired();
            });

            SubjectRefColumn.Configure(publish.Property(b => b.Subjects));
        });

        builder.OwnsMany(c => c.Consumes, consume =>
        {
            consume.ToTable("consume_binding");
            consume.WithOwner().HasForeignKey("contract_id");

            consume.Property(b => b.Queue).HasColumnName("queue").HasMaxLength(255).IsRequired();

            consume.OwnsOne(b => b.Scope, scope =>
            {
                scope.Property(s => s.BrokerId).HasColumnName("broker_id");
                scope.Property(s => s.VirtualHost).HasColumnName("virtual_host")
                    .HasMaxLength(256).IsRequired();
            });

            SubjectRefColumn.Configure(consume.Property(b => b.Subjects));
        });

        builder.Navigation(c => c.Publishes).AutoInclude();
        builder.Navigation(c => c.Consumes).AutoInclude();
    }
}
