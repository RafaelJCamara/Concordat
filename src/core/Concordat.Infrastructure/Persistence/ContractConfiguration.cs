using Concordat.Domain.Contracts;
using Concordat.Domain.Registry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Concordat.Infrastructure.Persistence;

/// <summary>
/// Maps the <see cref="Contract"/> aggregate and its bindings (M7.3).
/// </summary>
/// <remarks>
/// <b>A binding's subjects are stored as one text column, not a child table.</b> They are a
/// value-object list owned entirely by the binding: nothing queries them independently, they
/// have no identity, and they are always read and written together. A child table would add a
/// join and two more shadow keys to model something that is conceptually one field. The format
/// is <c>subject@selector</c> joined by commas, which is unambiguous because a subject name
/// cannot contain <c>@</c> or <c>,</c> under its own grammar, and readable in a database
/// client — which matters more than it sounds for a table operators will inspect when a
/// contract behaves unexpectedly.
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

            SubjectList(publish.Property(b => b.Subjects));
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

            SubjectList(consume.Property(b => b.Subjects));
        });

        builder.Navigation(c => c.Publishes).AutoInclude();
        builder.Navigation(c => c.Consumes).AutoInclude();
    }

    /// <summary>Maps a subject list to one text column.</summary>
    /// <remarks>
    /// The comparer is not optional. Without it EF compares the collection by reference, so a
    /// binding whose subjects changed would be silently considered unmodified and never
    /// written — the kind of bug that only appears on the second save.
    /// </remarks>
    private static void SubjectList(PropertyBuilder<IReadOnlyList<SubjectRef>> property) =>
        property
            .HasColumnName("subjects")
            .HasMaxLength(4096)
            .IsRequired()
            .HasConversion(
                refs => Serialise(refs),
                text => Deserialise(text),
                new Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<IReadOnlyList<SubjectRef>>(
                    (left, right) => Serialise(left!) == Serialise(right!),
                    refs => Serialise(refs).GetHashCode(StringComparison.Ordinal),
                    refs => Deserialise(Serialise(refs))));

    private static string Serialise(IReadOnlyList<SubjectRef> refs) =>
        string.Join(',', refs.Select(r => $"{r.Subject.Value}@{r.Selector}"));

    private static IReadOnlyList<SubjectRef> Deserialise(string text) =>
        text.Length is 0
            ? []
            : [.. text.Split(',').Select(entry =>
            {
                var at = entry.LastIndexOf('@');
                return new SubjectRef(
                    SubjectName.Create(entry[..at]).Value,
                    VersionSelector.Parse(entry[(at + 1)..]).Value);
            })];
}
