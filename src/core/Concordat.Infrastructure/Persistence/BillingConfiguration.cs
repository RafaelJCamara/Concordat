using Concordat.Domain.Billing;
using Concordat.Domain.Registry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Concordat.Infrastructure.Persistence;

/// <summary>
/// Maps <see cref="Subscription"/> (M9.3).
/// </summary>
/// <remarks>
/// No query filter, for the same reason <see cref="TenantConfiguration"/> has none: the billing
/// gate has to read a tenant's subscription while establishing what that tenant may do, and a
/// filter would make the answer depend on the question. The tenant is a column the repository
/// compares explicitly.
/// </remarks>
internal sealed class SubscriptionConfiguration : IEntityTypeConfiguration<Subscription>
{
    public void Configure(EntityTypeBuilder<Subscription> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("subscription");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Id).HasColumnName("subscription_id").ValueGeneratedNever();

        builder.Property(s => s.TenantId)
            .HasColumnName("tenant_id")
            .HasConversion(id => id.Value, value => new TenantId(value));

        builder.Property(s => s.Tier)
            .HasColumnName("tier")
            .HasConversion(tier => Tiers.For(tier), token => ParseTier(token))
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(s => s.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(s => s.ProviderCustomerId)
            .HasColumnName("provider_customer_id")
            .HasMaxLength(128);

        builder.Property(s => s.ProviderSubscriptionId)
            .HasColumnName("provider_subscription_id")
            .HasMaxLength(128);

        builder.Property(s => s.StartedAt).HasColumnName("started_at");
        builder.Property(s => s.ChangedAt).HasColumnName("changed_at");

        // One subscription per organisation. Two would make "what is this tenant paying for" a
        // question with more than one answer, and the gate would take whichever came back first.
        builder.HasIndex(s => s.TenantId)
            .IsUnique()
            .HasDatabaseName("ux_subscription_tenant");

        // A provider webhook arrives naming its own subscription id and nothing else, so this
        // is the lookup that has to be fast on the path that matters least to us and most to
        // the customer whose payment just went through.
        builder.HasIndex(s => s.ProviderSubscriptionId)
            .HasDatabaseName("ix_subscription_provider");
    }

    private static Tier ParseTier(string token) =>
        Tiers.Parse(token, out var tier).IsSuccess
            ? tier
            : throw new InvalidOperationException(
                $"A subscription names tier '{token}', which this build does not know.");
}
