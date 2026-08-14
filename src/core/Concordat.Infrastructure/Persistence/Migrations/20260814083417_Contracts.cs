using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Concordat.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Contracts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "contract",
                columns: table => new
                {
                    contract_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    enforcement = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contract", x => x.contract_id);
                });

            migrationBuilder.CreateTable(
                name: "consume_binding",
                columns: table => new
                {
                    contract_id = table.Column<Guid>(type: "uuid", nullable: false),
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    broker_id = table.Column<Guid>(type: "uuid", nullable: true),
                    virtual_host = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    queue = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    subjects = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_consume_binding", x => new { x.contract_id, x.Id });
                    table.ForeignKey(
                        name: "FK_consume_binding_contract_contract_id",
                        column: x => x.contract_id,
                        principalTable: "contract",
                        principalColumn: "contract_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "publish_binding",
                columns: table => new
                {
                    contract_id = table.Column<Guid>(type: "uuid", nullable: false),
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    broker_id = table.Column<Guid>(type: "uuid", nullable: true),
                    virtual_host = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    exchange = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    routing_key_pattern = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    subjects = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    precedence = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_publish_binding", x => new { x.contract_id, x.Id });
                    table.ForeignKey(
                        name: "FK_publish_binding_contract_contract_id",
                        column: x => x.contract_id,
                        principalTable: "contract",
                        principalColumn: "contract_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_contract_tenant_environment_name",
                table: "contract",
                columns: new[] { "tenant_id", "environment_id", "name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "consume_binding");

            migrationBuilder.DropTable(
                name: "publish_binding");

            migrationBuilder.DropTable(
                name: "contract");
        }
    }
}
