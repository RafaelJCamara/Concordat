using Concordat.Domain.Registry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Concordat.Infrastructure.Persistence;

/// <summary>
/// Maps the immutable, content-addressed <see cref="Schema"/> aggregate.
/// </summary>
internal sealed class SchemaConfiguration : IEntityTypeConfiguration<Schema>
{
    public void Configure(EntityTypeBuilder<Schema> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("schema");

        // The content-addressed id IS the primary key. That is what makes registration
        // idempotent with no application-level locking and no single-writer counter: a
        // concurrent duplicate registration collides on insert and the loser reads the winner
        // (ADR-015).
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Id)
            .HasColumnName("schema_id")
            .HasConversion(id => id.Value, value => SchemaId.FromTrusted(value))
            .HasMaxLength(32)
            .IsFixedLength()
            .ValueGeneratedNever();

        builder.Property(s => s.Format)
            .HasColumnName("format")
            .HasConversion(f => WireTokens.For(f), token => ParseFormat(token))
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(s => s.Body)
            .HasColumnName("body")
            .HasMaxLength(Schema.MaxBodyBytes)
            .IsRequired();

        // An explicit key of (schema_id, name), not .ToJson(). A JSON column cannot be indexed
        // for the reference queries M1.6 needs, and cannot enforce the name uniqueness the
        // aggregate already guarantees in memory.
        builder.OwnsMany(s => s.References, reference =>
        {
            reference.ToTable("schema_reference");
            reference.WithOwner().HasForeignKey("schema_id");
            reference.HasKey("schema_id", nameof(Reference.Name));

            reference.Property(r => r.Name).HasColumnName("name").HasMaxLength(512);
            reference.Property(r => r.Subject)
                .HasColumnName("subject")
                .HasConversion(n => n.Value, value => SubjectName.Create(value).Value)
                .HasMaxLength(SubjectName.MaxLength)
                .IsRequired();
            reference.Property(r => r.Version).HasColumnName("version");

            // Answers "what references this subject?" for the transitive re-check in M1.6.
            reference.HasIndex(nameof(Reference.Subject), nameof(Reference.Version))
                .HasDatabaseName("ix_schema_reference_target");
        });

        builder.Navigation(s => s.References).AutoInclude();

        builder.ToTable(t =>
        {
            // Belt and braces behind SchemaId.Create: the domain validates, but FromTrusted
            // exists for materialisation and this is what stops a bad row entering by any
            // other route.
            t.HasCheckConstraint("ck_schema_id_is_lower_hex", "schema_id ~ '^[0-9a-f]{32}$'");
        });
    }

    private static SchemaFormat ParseFormat(string token) => token switch
    {
        WireTokens.FormatJson => SchemaFormat.Json,
        WireTokens.FormatAvro => SchemaFormat.Avro,
        WireTokens.FormatProtobuf => SchemaFormat.Protobuf,
        _ => throw new InvalidOperationException($"Unknown schema format token '{token}'."),
    };
}
