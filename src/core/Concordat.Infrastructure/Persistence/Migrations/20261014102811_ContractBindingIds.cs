using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Concordat.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Gives every contract binding an identity of its own, so it can be changed or removed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A binding used to be keyed by <c>(contract_id, Id)</c> where <c>Id</c> was an integer EF
    /// generated and nothing outside the table could name. The scaffolded migration altered that
    /// integer column to <c>uuid</c>, which PostgreSQL refuses for any table that has a row; this
    /// is written by hand instead. It adds the new column empty, <b>gives every existing binding a
    /// fresh random id</b> (<c>gen_random_uuid()</c>, built in since PostgreSQL 13; the images in
    /// this repository are 17), makes it the key, and only then drops the integer. Nothing is lost
    /// but a number nobody could see.
    /// </para>
    /// <para>
    /// The ids a binding had before this migration are not meaningful and are not preserved,
    /// because it had none: they are generated here, once, and are stable from then on.
    /// </para>
    /// </remarks>
    public partial class ContractBindingIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[] { "publish_binding", "consume_binding" })
            {
                migrationBuilder.DropPrimaryKey(
                    name: $"PK_{table}",
                    table: table);

                migrationBuilder.AddColumn<Guid>(
                    name: "binding_id",
                    table: table,
                    type: "uuid",
                    nullable: true);

                migrationBuilder.Sql($"UPDATE {table} SET binding_id = gen_random_uuid();");

                migrationBuilder.AlterColumn<Guid>(
                    name: "binding_id",
                    table: table,
                    type: "uuid",
                    nullable: false,
                    oldClrType: typeof(Guid),
                    oldType: "uuid",
                    oldNullable: true);

                migrationBuilder.DropColumn(
                    name: "Id",
                    table: table);

                migrationBuilder.AddPrimaryKey(
                    name: $"PK_{table}",
                    table: table,
                    column: "binding_id");

                migrationBuilder.CreateIndex(
                    name: $"IX_{table}_contract_id",
                    table: table,
                    column: "contract_id");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[] { "publish_binding", "consume_binding" })
            {
                migrationBuilder.DropPrimaryKey(
                    name: $"PK_{table}",
                    table: table);

                migrationBuilder.DropIndex(
                    name: $"IX_{table}_contract_id",
                    table: table);

                migrationBuilder.AddColumn<int>(
                    name: "Id",
                    table: table,
                    type: "integer",
                    nullable: false,
                    defaultValue: 0)
                    .Annotation(
                        "Npgsql:ValueGenerationStrategy",
                        NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

                migrationBuilder.DropColumn(
                    name: "binding_id",
                    table: table);

                migrationBuilder.AddPrimaryKey(
                    name: $"PK_{table}",
                    table: table,
                    columns: new[] { "contract_id", "Id" });
            }
        }
    }
}
