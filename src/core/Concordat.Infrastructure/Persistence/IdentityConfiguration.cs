using Concordat.Domain.Identity;
using Concordat.Domain.Registry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Concordat.Infrastructure.Persistence;

/// <summary>
/// Maps <see cref="User"/> (M8.1).
/// </summary>
/// <remarks>
/// <b>Not tenant-scoped, and that is deliberate.</b> An account is global — Cloud (M9) has one
/// login belonging to several organisations, and self-hosted has one tenant. What is
/// tenant-scoped is the <see cref="Membership"/>, which is where the authorisation answer
/// actually lives. Filtering users by tenant would make sign-in circular: the request has to
/// find the account before it can know which tenant to filter by.
/// </remarks>
internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("app_user");
        builder.HasKey(u => u.Id);

        builder.Property(u => u.Id)
            .HasColumnName("user_id")
            .HasConversion(id => id.Value, value => new UserId(value))
            .ValueGeneratedNever();

        builder.Property(u => u.Email)
            .HasColumnName("email")
            .HasConversion(e => e.Value, value => EmailAddress.FromTrusted(value))
            .HasMaxLength(EmailAddress.MaxLength)
            .IsRequired();

        builder.Property(u => u.DisplayName)
            .HasColumnName("display_name")
            .HasMaxLength(User.MaxDisplayNameLength)
            .IsRequired();

        // Long enough for a PBKDF2 v3 hash today and for whatever replaces it. A column that
        // silently truncates a hash produces an account nobody can ever sign in to.
        builder.Property(u => u.PasswordHash)
            .HasColumnName("password_hash")
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(u => u.Disabled).HasColumnName("disabled");
        builder.Property(u => u.CreatedAt).HasColumnName("created_at");
        builder.Property(u => u.LastSignedInAt).HasColumnName("last_signed_in_at");

        // One account per address, enforced by the database. A read-then-write races, and the
        // loser of that race is a duplicate account with the same login.
        builder.HasIndex(u => u.Email).IsUnique().HasDatabaseName("ux_user_email");
    }
}

/// <summary>Maps <see cref="Membership"/> (M8.1).</summary>
internal sealed class MembershipConfiguration : IEntityTypeConfiguration<Membership>
{
    public void Configure(EntityTypeBuilder<Membership> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("membership");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.Id).HasColumnName("membership_id").ValueGeneratedNever();

        builder.Property(m => m.TenantId)
            .HasColumnName("tenant_id")
            .HasConversion(id => id.Value, value => new TenantId(value));

        builder.Property(m => m.UserId)
            .HasColumnName("user_id")
            .HasConversion(id => id.Value, value => new UserId(value));

        builder.Property(m => m.Role)
            .HasColumnName("role")
            .HasConversion(role => Roles.For(role), token => ParseRole(token))
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(m => m.CreatedAt).HasColumnName("created_at");

        builder.HasIndex(m => new { m.TenantId, m.UserId })
            .IsUnique()
            .HasDatabaseName("ux_membership_tenant_user");
    }

    private static Role ParseRole(string token) =>
        Roles.Parse(token, out var role).IsSuccess
            ? role
            : throw new InvalidOperationException(
                $"A membership names role '{token}', which this build does not know.");
}

/// <summary>
/// Maps <see cref="ApiKey"/> (M8.1).
/// </summary>
/// <remarks>
/// Like <see cref="UserConfiguration"/>, no tenant query filter: authentication is what
/// establishes the tenant, so a filter here would be circular. The tenant is a column the
/// repository compares explicitly for the management endpoints, and ignores for the lookup.
/// </remarks>
internal sealed class ApiKeyConfiguration : IEntityTypeConfiguration<ApiKey>
{
    public void Configure(EntityTypeBuilder<ApiKey> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("api_key");
        builder.HasKey(k => k.Id);

        builder.Property(k => k.Id).HasColumnName("api_key_id").ValueGeneratedNever();

        builder.Property(k => k.TenantId)
            .HasColumnName("tenant_id")
            .HasConversion(id => id.Value, value => new TenantId(value));

        builder.Property(k => k.UserId)
            .HasColumnName("user_id")
            .HasConversion(
                new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<UserId, Guid>(
                    id => id.Value,
                    value => new UserId(value)));

        builder.Property(k => k.KeyId)
            .HasColumnName("key_id")
            .HasMaxLength(ApiKey.KeyIdLength)
            .IsRequired();

        builder.Property(k => k.SecretHash)
            .HasColumnName("secret_hash")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(k => k.Label)
            .HasColumnName("label")
            .HasMaxLength(ApiKey.MaxLabelLength)
            .IsRequired();

        builder.Property(k => k.Kind)
            .HasColumnName("kind")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(k => k.Actor)
            .HasColumnName("actor")
            .HasConversion(a => a.Value, value => ActorId.Create(value).Value)
            .HasMaxLength(ActorId.MaxLength)
            .IsRequired();

        builder.Property(k => k.Scopes)
            .HasColumnName("scopes")
            .HasMaxLength(512)
            .IsRequired()
            .HasConversion(
                scopes => string.Join(' ', scopes),
                text => Split(text),
                new ValueComparer<IReadOnlyList<string>>(
                    (left, right) => string.Join(' ', left!) == string.Join(' ', right!),
                    scopes => string.Join(' ', scopes).GetHashCode(StringComparison.Ordinal),
                    scopes => Split(string.Join(' ', scopes))));

        builder.Property(k => k.CreatedAt).HasColumnName("created_at");
        builder.Property(k => k.ExpiresAt).HasColumnName("expires_at");
        builder.Property(k => k.LastUsedAt).HasColumnName("last_used_at");
        builder.Property(k => k.RevokedAt).HasColumnName("revoked_at");

        // The authentication lookup, and the only query on the hot path. Unique because the id
        // is what a presented credential is resolved by: two rows would make authentication
        // depend on which one the database returned first.
        builder.HasIndex(k => k.KeyId).IsUnique().HasDatabaseName("ux_api_key_key_id");

        builder.HasIndex(k => k.TenantId).HasDatabaseName("ix_api_key_tenant");
    }

    private static IReadOnlyList<string> Split(string text) =>
        text.Length is 0 ? [] : [.. text.Split(' ', StringSplitOptions.RemoveEmptyEntries)];
}
