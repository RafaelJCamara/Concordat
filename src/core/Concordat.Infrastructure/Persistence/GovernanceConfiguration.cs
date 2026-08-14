using Concordat.Domain.Governance;
using Concordat.Domain.Registry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Concordat.Infrastructure.Persistence;

/// <summary>Maps <see cref="ServiceRegistration"/> (M7.4).</summary>
internal sealed class ServiceRegistrationConfiguration
    : IEntityTypeConfiguration<ServiceRegistration>
{
    /// <summary>The shadow column carrying tenant ownership.</summary>
    internal const string TenantIdProperty = "TenantId";

    public void Configure(EntityTypeBuilder<ServiceRegistration> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("service_registration");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Id).HasColumnName("service_id").ValueGeneratedNever();

        builder.Property(s => s.EnvironmentId)
            .HasColumnName("environment_id")
            .HasConversion(id => id.Value, value => new EnvironmentId(value));

        builder.Property(s => s.Name)
            .HasColumnName("name")
            .HasMaxLength(ServiceRegistration.MaxNameLength)
            .IsRequired();

        builder.Property(s => s.FirstSeenAt).HasColumnName("first_seen_at");
        builder.Property(s => s.LastSeenAt).HasColumnName("last_seen_at");

        SubjectRefColumn.Configure(builder.Property(s => s.Produces), "produces");
        SubjectRefColumn.Configure(builder.Property(s => s.Consumes), "consumes");

        builder.Property<Guid>(TenantIdProperty).HasColumnName("tenant_id");

        // The upsert key. Enforced by the database rather than a read-then-write, because a
        // fleet-wide restart reports fifty times at once and a race would leave duplicate rows
        // that impact analysis would then count as separate consumers.
        builder.HasIndex(
                TenantIdProperty,
                nameof(ServiceRegistration.EnvironmentId),
                nameof(ServiceRegistration.Name))
            .IsUnique()
            .HasDatabaseName("ux_service_tenant_environment_name");
    }
}

/// <summary>
/// Maps <see cref="AuditEntry"/> (M7.4).
/// </summary>
/// <remarks>
/// Append-only in practice as well as by interface: nothing in the model gives EF a reason to
/// issue an UPDATE or a DELETE against this table, and the repository exposes no method that
/// would.
/// </remarks>
internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    /// <summary>The shadow column carrying tenant ownership.</summary>
    internal const string TenantIdProperty = "TenantId";

    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("audit_entry");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).HasColumnName("audit_id").ValueGeneratedNever();

        // Nullable: an action that is not scoped to one environment still belongs in the trail.
        // The converter is declared over the non-nullable pair and EF lifts it, which is the
        // only form that survives a null without a cast that throws on read.
        builder.Property(e => e.EnvironmentId)
            .HasColumnName("environment_id")
            .HasConversion(
                new ValueConverter<EnvironmentId, Guid>(
                    id => id.Value,
                    value => new EnvironmentId(value)));

        builder.Property(e => e.Action)
            .HasColumnName("action")
            .HasConversion(
                action => AuditTokens.For(action),
                token => Parse(token))
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(e => e.Actor)
            .HasColumnName("actor")
            .HasConversion(a => a.Value, value => ActorId.Create(value).Value)
            .HasMaxLength(ActorId.MaxLength)
            .IsRequired();

        builder.Property(e => e.Target)
            .HasColumnName("target")
            .HasMaxLength(AuditEntry.MaxTargetLength)
            .IsRequired();

        builder.Property(e => e.Detail)
            .HasColumnName("detail")
            .HasMaxLength(AuditEntry.MaxDetailLength);

        builder.Property(e => e.At).HasColumnName("at");
        builder.Property<Guid>(TenantIdProperty).HasColumnName("tenant_id");

        // Newest-first within a tenant is how the trail is read essentially every time. The
        // environment and target indexes carry the same descending timestamp because those
        // filters are always combined with it, never used alone.
        builder.HasIndex(TenantIdProperty, nameof(AuditEntry.At))
            .IsDescending(false, true)
            .HasDatabaseName("ix_audit_tenant_at");

        builder.HasIndex(TenantIdProperty, nameof(AuditEntry.EnvironmentId), nameof(AuditEntry.At))
            .IsDescending(false, false, true)
            .HasDatabaseName("ix_audit_tenant_environment_at");

        builder.HasIndex(TenantIdProperty, nameof(AuditEntry.Target), nameof(AuditEntry.At))
            .IsDescending(false, false, true)
            .HasDatabaseName("ix_audit_tenant_target_at");
    }

    // Stored tokens are trusted, but a token that no longer maps is a schema the code has moved
    // past. Throwing names the row instead of silently reading it as the first enum member.
    private static AuditAction Parse(string token)
    {
        var parsed = AuditTokens.Parse(token, out var action);

        return parsed.IsSuccess && action is { } known
            ? known
            : throw new InvalidOperationException(
                $"The audit trail contains action '{token}', which this build does not know.");
    }
}

/// <summary>
/// Maps <see cref="DeploymentEvent"/> (decision 29).
/// </summary>
/// <remarks>
/// <b>No tenant shadow property and no global query filter</b>, unlike every other table here.
/// That is the point of the table: these rows describe things that happened above a tenant or
/// before one existed, so scoping them to one would either hide them or file them under the
/// wrong organisation. <c>TenantId</c> is a real, nullable column carrying <em>which</em>
/// organisation the event concerns, as data.
/// </remarks>
internal sealed class DeploymentEventConfiguration : IEntityTypeConfiguration<DeploymentEvent>
{
    public void Configure(EntityTypeBuilder<DeploymentEvent> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("deployment_event");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).HasColumnName("deployment_event_id").ValueGeneratedNever();

        builder.Property(e => e.Action)
            .HasColumnName("action")
            .HasConversion(action => DeploymentTokens.For(action), token => Parse(token))
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(e => e.Actor)
            .HasColumnName("actor")
            .HasMaxLength(DeploymentEvent.MaxActorLength)
            .IsRequired();

        // Declared over the non-nullable pair and lifted by EF, which is the only form that
        // survives a null without a cast that throws on read -- the same shape AuditEntry uses
        // for its nullable EnvironmentId.
        builder.Property(e => e.TenantId)
            .HasColumnName("tenant_id")
            .HasConversion(
                new ValueConverter<TenantId, Guid>(id => id.Value, value => new TenantId(value)));

        builder.Property(e => e.Detail)
            .HasColumnName("detail")
            .HasMaxLength(DeploymentEvent.MaxDetailLength)
            .IsRequired();

        builder.Property(e => e.OccurredAt).HasColumnName("occurred_at");

        builder.HasIndex(nameof(DeploymentEvent.OccurredAt))
            .IsDescending(true)
            .HasDatabaseName("ix_deployment_event_at");
    }

    private static DeploymentAction Parse(string token) => token switch
    {
        "ORGANISATION_CREATED" => DeploymentAction.OrganisationCreated,
        "INSTANCE_CLAIMED" => DeploymentAction.InstanceClaimed,
        _ => throw new InvalidOperationException(
            $"'{token}' is not a deployment action this build knows."),
    };
}

/// <summary>
/// Maps <see cref="EnforcementViolation"/> (decision 25).
/// </summary>
/// <remarks>
/// Tenant-scoped, unlike <see cref="DeploymentEvent"/>: a violation is something that happened
/// inside one organisation's estate and belongs to that organisation's view of itself.
/// </remarks>
internal sealed class EnforcementViolationConfiguration
    : IEntityTypeConfiguration<EnforcementViolation>
{
    /// <summary>The shadow column carrying tenant ownership.</summary>
    internal const string TenantIdProperty = "TenantId";

    public void Configure(EntityTypeBuilder<EnforcementViolation> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("enforcement_violation");
        builder.HasKey(v => v.Id);

        builder.Property(v => v.Id).HasColumnName("violation_id").ValueGeneratedNever();

        builder.Property(v => v.EnvironmentId)
            .HasColumnName("environment_id")
            .HasConversion(id => id.Value, value => new EnvironmentId(value));

        builder.Property(v => v.Side)
            .HasColumnName("side")
            .HasConversion(side => ViolationTokens.For(side), token => ParseSide(token))
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(v => v.Route)
            .HasColumnName("route")
            .HasMaxLength(EnforcementViolation.MaxRouteLength)
            .IsRequired();

        builder.Property(v => v.Subject).HasColumnName("subject").HasMaxLength(256);

        builder.Property(v => v.Code).HasColumnName("code").HasMaxLength(64).IsRequired();

        builder.Property(v => v.Detail)
            .HasColumnName("detail")
            .HasMaxLength(EnforcementViolation.MaxDetailLength)
            .IsRequired();

        builder.Property(v => v.ReportedBy)
            .HasColumnName("reported_by")
            .HasMaxLength(EnforcementViolation.MaxReportedByLength)
            .IsRequired();

        builder.Property(v => v.FirstSeenAt).HasColumnName("first_seen_at");
        builder.Property(v => v.LastSeenAt).HasColumnName("last_seen_at");
        builder.Property(v => v.Occurrences).HasColumnName("occurrences");

        builder.Property(v => v.Fingerprint)
            .HasColumnName("fingerprint")
            .HasMaxLength(1024)
            .IsRequired();

        builder.Property<Guid>(TenantIdProperty).HasColumnName("tenant_id");

        // The upsert key, enforced by the database rather than by a read-then-write. Every
        // replica of a broken service reports the same fingerprint at the same moment, and a race
        // would leave duplicate rows -- which would then each fire their own "first sight"
        // notification, turning one fault into an alert storm.
        builder.HasIndex(TenantIdProperty, nameof(EnforcementViolation.Fingerprint))
            .IsUnique()
            .HasDatabaseName("ux_violation_tenant_fingerprint");

        builder.HasIndex(
                TenantIdProperty,
                nameof(EnforcementViolation.EnvironmentId),
                nameof(EnforcementViolation.LastSeenAt))
            .IsDescending(false, false, true)
            .HasDatabaseName("ix_violation_tenant_environment_last_seen");
    }

    private static ViolationSide ParseSide(string token)
    {
        if (ViolationTokens.Parse(token, out var side).IsFailure)
        {
            throw new InvalidOperationException($"'{token}' is not a violation side.");
        }

        return side;
    }
}
