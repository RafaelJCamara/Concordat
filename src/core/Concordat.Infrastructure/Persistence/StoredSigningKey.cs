using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Concordat.Infrastructure.Persistence;

/// <summary>
/// One encrypted webhook signing secret (M7.5).
/// </summary>
/// <remarks>
/// Same shape as <see cref="StoredCredential"/> and deliberately a separate table rather than
/// reusing it: <c>broker_credential</c> is a name that promises one kind of secret, and a
/// signing key sharing it would be the first thing a future reader had to explain away.
/// </remarks>
internal sealed class StoredSigningKey
{
    /// <summary>The opaque reference recorded on the subscription.</summary>
    public string Reference { get; set; } = null!;

    /// <summary>The protected payload.</summary>
    public string Ciphertext { get; set; } = null!;

    /// <summary>When it was last written, for operational forensics only.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Maps <see cref="StoredSigningKey"/>.</summary>
internal sealed class StoredSigningKeyConfiguration : IEntityTypeConfiguration<StoredSigningKey>
{
    public void Configure(EntityTypeBuilder<StoredSigningKey> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("webhook_signing_key");
        builder.HasKey(c => c.Reference);

        builder.Property(c => c.Reference).HasColumnName("signing_key_ref").HasMaxLength(64);
        builder.Property(c => c.Ciphertext).HasColumnName("ciphertext").HasMaxLength(4096)
            .IsRequired();
        builder.Property(c => c.UpdatedAt).HasColumnName("updated_at");

        // Tenant-scoped like StoredCredential, and for the same reason: defence in depth on
        // top of the reference being reachable only through a subscription that is itself
        // filtered.
        builder.Property<Guid>(SubjectConfiguration.TenantIdProperty).HasColumnName("tenant_id");
    }
}
