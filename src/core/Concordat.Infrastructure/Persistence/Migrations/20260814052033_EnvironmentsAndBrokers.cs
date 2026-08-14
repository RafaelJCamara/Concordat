using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Concordat.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnvironmentsAndBrokers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "environment",
                columns: table => new
                {
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    registration_policy = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    default_compatibility_mode = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    default_compatibility_surface = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_environment", x => x.environment_id);
                });

            migrationBuilder.CreateTable(
                name: "broker_connection",
                columns: table => new
                {
                    broker_id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    uri = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    virtual_host = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    credential_ref = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    use_tls = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    last_checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_broker_connection", x => x.broker_id);
                    table.ForeignKey(
                        name: "FK_broker_connection_environment_environment_id",
                        column: x => x.environment_id,
                        principalTable: "environment",
                        principalColumn: "environment_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_broker_connection_environment_id",
                table: "broker_connection",
                column: "environment_id");

            migrationBuilder.CreateIndex(
                name: "ix_environment_tenant_name",
                table: "environment",
                columns: new[] { "tenant_id", "name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "broker_connection");

            migrationBuilder.DropTable(
                name: "environment");
        }
    }
}
