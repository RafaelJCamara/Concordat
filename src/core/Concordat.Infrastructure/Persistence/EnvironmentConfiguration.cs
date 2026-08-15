using Concordat.Domain.Registry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Environment = Concordat.Domain.Registry.Environment;

namespace Concordat.Infrastructure.Persistence;

/// <summary>
/// Maps the <see cref="Environment"/> aggregate and its brokers (M7.1).
/// </summary>
internal sealed class EnvironmentConfiguration : IEntityTypeConfiguration<Environment>
{
    /// <summary>The shadow column carrying tenant ownership.</summary>
    internal const string TenantIdProperty = "TenantId";

    public void Configure(EntityTypeBuilder<Environment> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("environment");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id)
            .HasColumnName("environment_id")
            .HasConversion(id => id.Value, value => new EnvironmentId(value))
            .ValueGeneratedNever();

        builder.Property(e => e.Name)
            .HasColumnName("name")
            .HasConversion(n => n.Value, value => EnvironmentName.FromTrusted(value))
            .HasMaxLength(EnvironmentName.MaxLength)
            .IsRequired();

        builder.Property(e => e.Description)
            .HasColumnName("description")
            .HasMaxLength(512);

        builder.ComplexProperty(e => e.DefaultCompatibilityPolicy, policy =>
        {
            policy.Property(p => p.Mode).HasColumnName("default_compatibility_mode")
                .HasConversion<string>().HasMaxLength(24).IsRequired();
            policy.Property(p => p.Surface).HasColumnName("default_compatibility_surface")
                .HasConversion<string>().HasMaxLength(16).IsRequired();
        });

        builder.Property(e => e.RegistrationPolicy)
            .HasColumnName("registration_policy")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(e => e.AllowPreReleaseVersions).HasColumnName("allow_prerelease_versions");

        builder.Property(e => e.CreatedAt).HasColumnName("created_at");

        builder.Property<Guid>(TenantIdProperty).HasColumnName("tenant_id");

        // Names are how pipelines address environments, so two with one name inside a tenant
        // would make every /v1/environments/{env}/... route ambiguous. Enforced by the
        // database rather than by a read-then-write, which races.
        builder.HasIndex(TenantIdProperty, nameof(Environment.Name))
            .IsUnique()
            .HasDatabaseName("ix_environment_tenant_name");

        builder.OwnsMany(e => e.Brokers, brokers =>
        {
            brokers.ToTable("broker_connection");
            brokers.WithOwner().HasForeignKey("environment_id");
            brokers.HasKey(b => b.Id);

            brokers.Property(b => b.Id).HasColumnName("broker_id").ValueGeneratedNever();
            brokers.Property(b => b.DisplayName).HasColumnName("display_name")
                .HasMaxLength(128).IsRequired();

            // Stored as text rather than as a set of parts. The URI is what an operator typed
            // and what a connection factory needs back; decomposing it would mean reassembling
            // it identically on every read, which is a second implementation of Uri.
            brokers.Property(b => b.Uri).HasColumnName("uri")
                .HasConversion(u => u.ToString(), value => new Uri(value))
                .HasMaxLength(512).IsRequired();

            brokers.Property(b => b.VirtualHost).HasColumnName("virtual_host")
                .HasMaxLength(256).IsRequired();

            // A reference, never a secret. The credential itself is encrypted at rest
            // elsewhere (M7.2) and never enters this table.
            brokers.Property(b => b.CredentialRef).HasColumnName("credential_ref")
                .HasMaxLength(256);

            brokers.Property(b => b.UseTls).HasColumnName("use_tls").IsRequired();
            brokers.Property(b => b.Status).HasColumnName("status")
                .HasConversion<string>().HasMaxLength(16).IsRequired();
            brokers.Property(b => b.LastCheckedAt).HasColumnName("last_checked_at");
            brokers.Property(b => b.LastError).HasColumnName("last_error").HasMaxLength(1024);
        });

        builder.Navigation(e => e.Brokers).AutoInclude();
    }
}
