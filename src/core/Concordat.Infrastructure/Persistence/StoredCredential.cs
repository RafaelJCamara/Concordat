using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Concordat.Infrastructure.Persistence;

/// <summary>
/// One encrypted broker credential (M7.2).
/// </summary>
/// <remarks>
/// <para>
/// <b>An infrastructure row, deliberately not a domain entity.</b> It exists in this namespace
/// and nowhere else so that no aggregate, no query handler and no response projection can
/// reach it. <c>BrokerConnection</c> holds only the reference.
/// </para>
/// <para>
/// <b>The ciphertext is opaque to the database.</b> Data Protection produces a
/// base64url payload that carries its own key identifier, so rotating the key ring does not
/// require rewriting these rows — the old key stays in the ring and old payloads keep
/// decrypting.
/// </para>
/// </remarks>
internal sealed class StoredCredential
{
    /// <summary>The opaque reference recorded on the broker.</summary>
    public string Reference { get; set; } = null!;

    /// <summary>The protected payload.</summary>
    public string Ciphertext { get; set; } = null!;

    /// <summary>When it was last written, for operational forensics only.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Maps <see cref="StoredCredential"/>.</summary>
internal sealed class StoredCredentialConfiguration : IEntityTypeConfiguration<StoredCredential>
{
    public void Configure(EntityTypeBuilder<StoredCredential> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("broker_credential");
        builder.HasKey(c => c.Reference);

        builder.Property(c => c.Reference).HasColumnName("credential_ref").HasMaxLength(64);
        builder.Property(c => c.Ciphertext).HasColumnName("ciphertext").HasMaxLength(4096)
            .IsRequired();
        builder.Property(c => c.UpdatedAt).HasColumnName("updated_at");

        // Tenant-scoped like every other row that belongs to someone. The reference is a
        // random GUID and is only reachable through a broker that is itself filtered, so this
        // is defence in depth rather than the primary control — but a secret store is exactly
        // where defence in depth is worth the column.
        builder.Property<Guid>(SubjectConfiguration.TenantIdProperty).HasColumnName("tenant_id");
    }
}
