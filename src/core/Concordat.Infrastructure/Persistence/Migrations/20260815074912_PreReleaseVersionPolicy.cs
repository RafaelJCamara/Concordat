using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Concordat.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PreReleaseVersionPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "allow_prerelease_versions",
                table: "environment",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "allow_prerelease_versions",
                table: "environment");
        }
    }
}
