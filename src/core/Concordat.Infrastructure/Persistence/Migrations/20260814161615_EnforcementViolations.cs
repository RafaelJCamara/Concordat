using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Concordat.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnforcementViolations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "enforcement_violation",
                columns: table => new
                {
                    violation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    side = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    route = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    detail = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    reported_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    occurrences = table.Column<long>(type: "bigint", nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_enforcement_violation", x => x.violation_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_violation_tenant_environment_last_seen",
                table: "enforcement_violation",
                columns: new[] { "tenant_id", "environment_id", "last_seen_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ux_violation_tenant_fingerprint",
                table: "enforcement_violation",
                columns: new[] { "tenant_id", "fingerprint" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "enforcement_violation");
        }
    }
}
