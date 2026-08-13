using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Concordat.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "schema",
                columns: table => new
                {
                    schema_id = table.Column<string>(type: "character(32)", fixedLength: true, maxLength: 32, nullable: false),
                    format = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    body = table.Column<string>(type: "character varying(524288)", maxLength: 524288, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_schema", x => x.schema_id);
                    table.CheckConstraint("ck_schema_id_is_lower_hex", "schema_id ~ '^[0-9a-f]{32}$'");
                });

            migrationBuilder.CreateTable(
                name: "subject",
                columns: table => new
                {
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    format = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    owner = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    content_model = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    lifecycle = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    compatibility_mode = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    compatibility_surface = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    latest_moved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    latest_moved_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    latest_ordinal = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_subject", x => x.subject_id);
                });

            migrationBuilder.CreateTable(
                name: "schema_reference",
                columns: table => new
                {
                    name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    schema_id = table.Column<string>(type: "character(32)", nullable: false),
                    subject = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_schema_reference", x => new { x.schema_id, x.name });
                    table.ForeignKey(
                        name: "FK_schema_reference_schema_schema_id",
                        column: x => x.schema_id,
                        principalTable: "schema",
                        principalColumn: "schema_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "schema_version",
                columns: table => new
                {
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    schema_id = table.Column<string>(type: "character(32)", fixedLength: true, maxLength: 32, nullable: false),
                    semver = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    changelog = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    deprecated = table.Column<bool>(type: "boolean", nullable: false),
                    registered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    registered_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decided_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_schema_version", x => new { x.subject_id, x.ordinal });
                    table.CheckConstraint("ck_schema_version_ordinal_positive", "ordinal >= 1");
                    table.ForeignKey(
                        name: "FK_schema_version_subject_subject_id",
                        column: x => x.subject_id,
                        principalTable: "subject",
                        principalColumn: "subject_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_schema_reference_target",
                table: "schema_reference",
                columns: new[] { "subject", "version" });

            migrationBuilder.CreateIndex(
                name: "ix_schema_version_schema_id",
                table: "schema_version",
                column: "schema_id");

            migrationBuilder.CreateIndex(
                name: "ux_subject_name_per_environment",
                table: "subject",
                columns: new[] { "tenant_id", "environment_id", "name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "schema_reference");

            migrationBuilder.DropTable(
                name: "schema_version");

            migrationBuilder.DropTable(
                name: "schema");

            migrationBuilder.DropTable(
                name: "subject");
        }
    }
}
