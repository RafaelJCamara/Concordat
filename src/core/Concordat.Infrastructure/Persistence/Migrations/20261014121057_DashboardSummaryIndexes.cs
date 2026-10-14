using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Concordat.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DashboardSummaryIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_subject_tenant_environment_created",
                table: "subject",
                columns: new[] { "tenant_id", "environment_id", "created_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_schema_version_held",
                table: "schema_version",
                column: "registered_at",
                descending: new bool[0],
                filter: "status IN ('AwaitingApproval', 'Rejected')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_subject_tenant_environment_created",
                table: "subject");

            migrationBuilder.DropIndex(
                name: "ix_schema_version_held",
                table: "schema_version");
        }
    }
}
