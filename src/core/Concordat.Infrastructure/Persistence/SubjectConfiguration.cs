using Concordat.Domain.Registry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Concordat.Infrastructure.Persistence;

/// <summary>
/// Maps the <see cref="Subject"/> aggregate and its versions.
/// </summary>
internal sealed class SubjectConfiguration : IEntityTypeConfiguration<Subject>
{
    /// <summary>The shadow column carrying tenant ownership.</summary>
    internal const string TenantIdProperty = "TenantId";

    public void Configure(EntityTypeBuilder<Subject> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("subject");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Id)
            .HasColumnName("subject_id")
            .HasConversion(id => id.Value, value => new SubjectId(value))
            .ValueGeneratedNever();

        builder.Property(s => s.EnvironmentId)
            .HasColumnName("environment_id")
            .HasConversion(id => id.Value, value => new EnvironmentId(value));

        builder.Property(s => s.Name)
            .HasColumnName("name")
            .HasConversion(n => n.Value, value => SubjectName.Create(value).Value)
            .HasMaxLength(SubjectName.MaxLength)
            .IsRequired();

        builder.Property(s => s.Format)
            .HasColumnName("format")
            .HasConversion(f => WireTokens.For(f), token => ParseFormat(token))
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(s => s.Owner)
            .HasColumnName("owner")
            .HasConversion(a => a.Value, value => ActorId.Create(value).Value)
            .HasMaxLength(ActorId.MaxLength)
            .IsRequired();

        // Enums as text, never Enum.ToString(): a C# rename must not silently rewrite stored
        // data or the wire format (ADR-019).
        builder.Property(s => s.Lifecycle)
            .HasColumnName("lifecycle")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(s => s.ContentModel)
            .HasColumnName("content_model")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        // Nullable on purpose: null means "inherit the environment default", and that is not
        // the same as "explicitly configured to whatever the default currently is". Flattening
        // it would destroy the distinction with no data to rebuild it from.
        builder.ComplexProperty(s => s.CompatibilityPolicy, policy =>
        {
            policy.Property(p => p.Mode).HasColumnName("compatibility_mode")
                .HasConversion<string>().HasMaxLength(24);
            policy.Property(p => p.Surface).HasColumnName("compatibility_surface")
                .HasConversion<string>().HasMaxLength(16);
        });

        builder.Property(s => s.CreatedAt).HasColumnName("created_at");

        builder.ComplexProperty(s => s.Latest, latest =>
        {
            latest.Property(p => p.Ordinal).HasColumnName("latest_ordinal");
            latest.Property(p => p.MovedAt).HasColumnName("latest_moved_at");
            latest.Property(p => p.MovedBy)
                .HasColumnName("latest_moved_by")
                .HasConversion(a => a!.Value, value => ActorId.Create(value).Value)
                .HasMaxLength(ActorId.MaxLength);
        });

        builder.Property<Guid>(TenantIdProperty).HasColumnName("tenant_id");

        // Optimistic concurrency via PostgreSQL's system xmin column. Two concurrent
        // registrations against one subject would otherwise both allocate the same ordinal and
        // one would silently win.
        //
        // The sharp edge this used to carry: xmin only changes when the SUBJECT ROW is updated,
        // and inserting or editing a child schema_version row alone does not bump it. That made
        // RegisterVersion safe only by accident (it moves LatestPointer) and left Reject
        // unguarded entirely. M7.4 closed it — Subject.Revision is incremented by every mutator,
        // so there is always a root UPDATE for the token to ride on.
        builder.Property(s => s.Revision).HasColumnName("revision");

        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        // Subject names are unique per (tenant, environment) — the environment is a genuine
        // isolation boundary, not a naming convention (ADR-012).
        builder.HasIndex(TenantIdProperty, nameof(Subject.EnvironmentId), nameof(Subject.Name))
            .IsUnique()
            .HasDatabaseName("ux_subject_name_per_environment");

        builder.OwnsMany(s => s.Versions, version =>
        {
            version.ToTable("schema_version");
            version.WithOwner().HasForeignKey("subject_id");
            version.HasKey("subject_id", nameof(SchemaVersion.Ordinal));

            // ValueGeneratedNever is essential, not cosmetic. EF treats an integer key as
            // database-generated by default, which would make PostgreSQL allocate the ordinal
            // — and the aggregate's contiguous-from-1 invariant, the approval gate and the
            // latest pointer all depend on the DOMAIN owning that number.
            version.Property(v => v.Ordinal).HasColumnName("ordinal").ValueGeneratedNever();

            version.Property(v => v.SchemaId)
                .HasColumnName("schema_id")
                .HasConversion(id => id.Value, value => SchemaId.FromTrusted(value))
                .HasMaxLength(32)
                .IsFixedLength()
                .IsRequired();

            // Stored as its canonical MAJOR.MINOR.PATCH text rather than three columns:
            // complex types are not available inside an owned collection, and the label is
            // never ordered in SQL — ordering is by Ordinal, which is the canonical identity
            // (ADR-004). If it ever needs range querying, split it then.
            version.Property(v => v.SemanticVersion)
                .HasColumnName("semver")
                .HasConversion(
                    v => v == null ? null : v.Value.ToString(),
                    s => s == null ? null : SemanticVersion.Create(s).Value)
                .HasMaxLength(32);

            version.Property(v => v.Changelog)
                .HasColumnName("changelog")
                .HasMaxLength(Subject.MaxChangelogLength);

            version.Property(v => v.Status)
                .HasColumnName("status").HasConversion<string>().HasMaxLength(24).IsRequired();

            version.Property(v => v.Deprecated).HasColumnName("deprecated");
            version.Property(v => v.RegisteredAt).HasColumnName("registered_at");
            version.Property(v => v.RegisteredBy)
                .HasColumnName("registered_by")
                .HasConversion(a => a.Value, value => ActorId.Create(value).Value)
                .HasMaxLength(ActorId.MaxLength)
                .IsRequired();

            version.Property(v => v.DecidedAt).HasColumnName("decided_at");
            version.Property(v => v.DecidedBy)
                .HasColumnName("decided_by")
                .HasConversion(a => a!.Value, value => ActorId.Create(value).Value)
                .HasMaxLength(ActorId.MaxLength);

            // No navigation to Schema in either direction, and Restrict rather than Cascade:
            // schemas are immutable and never deleted (ADR-015), so a cascade would be a bug
            // waiting for someone to write a DELETE.
            version.HasIndex(nameof(SchemaVersion.SchemaId))
                .HasDatabaseName("ix_schema_version_schema_id");

            version.ToTable(t =>
                t.HasCheckConstraint("ck_schema_version_ordinal_positive", "ordinal >= 1"));
        });

        // Reconsidered for Q1 and kept, deliberately: SubjectResponse (the wire contract for
        // both GET /subjects and GET /subjects/{subject}) embeds each subject's full Versions
        // array, not a summary — removing AutoInclude would silently null that out for every
        // caller of ListSubjectsQuery unless every read path added an explicit .Include(), and
        // a forgotten one is a production bug (an empty Versions array), not a build error. The
        // actual "unbounded" cost this leaves on the table is the N+1 the *other* Q1 fixes
        // address instead: loading each prior schema's body one row at a time while walking a
        // subject's history, not the Versions collection itself, which the wire contract needs
        // loaded either way. If a subject's version count ever grows large enough that this
        // join becomes the bottleneck, the fix is splitting the wire contract (a summary list
        // endpoint plus the existing full-history get), not quietly dropping AutoInclude here.
        builder.Navigation(s => s.Versions).AutoInclude();
    }

    private static SchemaFormat ParseFormat(string token) => token switch
    {
        WireTokens.FormatJson => SchemaFormat.Json,
        WireTokens.FormatAvro => SchemaFormat.Avro,
        WireTokens.FormatProtobuf => SchemaFormat.Protobuf,
        _ => throw new InvalidOperationException($"Unknown schema format token '{token}'."),
    };
}
