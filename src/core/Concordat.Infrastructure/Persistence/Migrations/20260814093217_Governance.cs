using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Concordat.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Governance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "revision",
                table: "subject",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "audit_entry",
                columns: table => new
                {
                    audit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    actor = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    target = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    detail = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_entry", x => x.audit_id);
                });

            migrationBuilder.CreateTable(
                name: "service_registration",
                columns: table => new
                {
                    service_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    produces = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    consumes = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_registration", x => x.service_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_tenant_at",
                table: "audit_entry",
                columns: new[] { "tenant_id", "at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_audit_tenant_environment_at",
                table: "audit_entry",
                columns: new[] { "tenant_id", "environment_id", "at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_audit_tenant_target_at",
                table: "audit_entry",
                columns: new[] { "tenant_id", "target", "at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ux_service_tenant_environment_name",
                table: "service_registration",
                columns: new[] { "tenant_id", "environment_id", "name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_entry");

            migrationBuilder.DropTable(
                name: "service_registration");

            migrationBuilder.DropColumn(
                name: "revision",
                table: "subject");
        }
    }
}
