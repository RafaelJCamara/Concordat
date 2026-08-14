using Concordat.Domain.Governance;
using Concordat.Domain.Registry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Concordat.Infrastructure.Persistence;

/// <summary>Maps <see cref="OutboxMessage"/> (M7.5).</summary>
internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    /// <summary>The shadow column carrying tenant ownership.</summary>
    internal const string TenantIdProperty = "TenantId";

    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("outbox_message");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.Id).HasColumnName("outbox_id").ValueGeneratedNever();

        builder.Property(m => m.EnvironmentId)
            .HasColumnName("environment_id")
            .HasConversion(id => id.Value, value => new EnvironmentId(value));

        builder.Property(m => m.Event)
            .HasColumnName("event")
            .HasConversion(
                value => NotificationTokens.For(value),
                token => Parse(token))
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(m => m.Target)
            .HasColumnName("target")
            .HasMaxLength(OutboxMessage.MaxTargetLength)
            .IsRequired();

        builder.Property(m => m.Body)
            .HasColumnName("body")
            .HasMaxLength(OutboxMessage.MaxBodyLength)
            .IsRequired();

        builder.Property(m => m.OccurredAt).HasColumnName("occurred_at");
        builder.Property(m => m.DeliveredAt).HasColumnName("delivered_at");
        builder.Property(m => m.Attempts).HasColumnName("attempts");
        builder.Property(m => m.NextAttemptAt).HasColumnName("next_attempt_at");
        builder.Property(m => m.Parked).HasColumnName("parked");

        builder.Property(m => m.LastError)
            .HasColumnName("last_error")
            .HasMaxLength(OutboxMessage.MaxBodyLength);

        builder.Property<Guid>(TenantIdProperty).HasColumnName("tenant_id");

        // The pump's only query: undelivered, unparked, due now, oldest first. A filtered index
        // keeps it proportional to the backlog rather than to everything ever notified, which
        // matters because this table only grows until retention exists.
        builder.HasIndex(TenantIdProperty, nameof(OutboxMessage.NextAttemptAt))
            .HasFilter("delivered_at IS NULL AND parked = false")
            .HasDatabaseName("ix_outbox_due");
    }

    private static NotificationEvent Parse(string token) =>
        NotificationTokens.TryParse(token, out var parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"The outbox contains event '{token}', which this build does not know.");
}

/// <summary>Maps <see cref="NotificationSubscription"/> (M7.5).</summary>
internal sealed class NotificationSubscriptionConfiguration
    : IEntityTypeConfiguration<NotificationSubscription>
{
    /// <summary>The shadow column carrying tenant ownership.</summary>
    internal const string TenantIdProperty = "TenantId";

    public void Configure(EntityTypeBuilder<NotificationSubscription> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("notification_subscription");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Id).HasColumnName("subscription_id").ValueGeneratedNever();

        builder.Property(s => s.EnvironmentId)
            .HasColumnName("environment_id")
            .HasConversion(id => id.Value, value => new EnvironmentId(value));

        builder.Property(s => s.Channel)
            .HasColumnName("channel")
            .HasConversion(
                channel => ChannelTokens.For(channel),
                token => ParseChannel(token))
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(s => s.Endpoint)
            .HasColumnName("endpoint")
            .HasMaxLength(NotificationSubscription.MaxEndpointLength)
            .IsRequired();

        // One text column for the same reason a binding's subjects are: a value list with no
        // identity, always read and written together. Empty means every event, which is why the
        // column is required rather than nullable -- two spellings of "all" would be one too
        // many.
        builder.Property(s => s.Events)
            .HasColumnName("events")
            .HasMaxLength(1024)
            .IsRequired()
            .HasConversion(
                events => Serialise(events),
                text => Deserialise(text),
                new ValueComparer<IReadOnlyList<NotificationEvent>>(
                    (left, right) => Serialise(left!) == Serialise(right!),
                    events => Serialise(events).GetHashCode(StringComparison.Ordinal),
                    events => Deserialise(Serialise(events))));

        builder.Property(s => s.Enabled).HasColumnName("enabled");
        builder.Property(s => s.CreatedAt).HasColumnName("created_at");
        builder.Property<Guid>(TenantIdProperty).HasColumnName("tenant_id");

        builder.HasIndex(TenantIdProperty, nameof(NotificationSubscription.EnvironmentId))
            .HasDatabaseName("ix_subscription_tenant_environment");
    }

    private static string Serialise(IReadOnlyList<NotificationEvent> events) =>
        string.Join(',', events.Select(NotificationTokens.For));

    private static IReadOnlyList<NotificationEvent> Deserialise(string text) =>
        text.Length is 0
            ? []
            : [.. text.Split(',').Select(token =>
                NotificationTokens.TryParse(token, out var parsed)
                    ? parsed
                    : throw new InvalidOperationException(
                        $"A subscription names event '{token}', which this build does not know."))];

    private static NotificationChannel ParseChannel(string token) =>
        ChannelTokens.TryParse(token, out var parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"A subscription names channel '{token}', which this build does not know.");
}
