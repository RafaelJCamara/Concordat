using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Concordat.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SubjectReviewers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "reviewers",
                table: "subject",
                type: "character varying(8192)",
                maxLength: 8192,
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "reviewers",
                table: "subject");
        }
    }
}
