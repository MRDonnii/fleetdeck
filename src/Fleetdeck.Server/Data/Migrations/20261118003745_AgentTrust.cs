using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleetdeck.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AgentTrust : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EnrollMethod",
                table: "Machines",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "JobTrust",
                table: "Machines",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PendingProvision",
                table: "Machines",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ReplacesId",
                table: "Machines",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TlsPinned",
                table: "Machines",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "TokenAlias",
                table: "Machines",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Transport",
                table: "Machines",
                type: "TEXT",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TrustError",
                table: "Machines",
                type: "TEXT",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TrustErrorAt",
                table: "Machines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EnrollCodes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CodeHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Hint = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ReplaceMachineId = table.Column<int>(type: "INTEGER", nullable: true),
                    ProfileId = table.Column<int>(type: "INTEGER", nullable: true),
                    Created = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Expires = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UsedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UsedByMachineId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnrollCodes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EnrollCodes_CodeHash",
                table: "EnrollCodes",
                column: "CodeHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EnrollCodes");

            migrationBuilder.DropColumn(
                name: "EnrollMethod",
                table: "Machines");

            migrationBuilder.DropColumn(
                name: "JobTrust",
                table: "Machines");

            migrationBuilder.DropColumn(
                name: "PendingProvision",
                table: "Machines");

            migrationBuilder.DropColumn(
                name: "ReplacesId",
                table: "Machines");

            migrationBuilder.DropColumn(
                name: "TlsPinned",
                table: "Machines");

            migrationBuilder.DropColumn(
                name: "TokenAlias",
                table: "Machines");

            migrationBuilder.DropColumn(
                name: "Transport",
                table: "Machines");

            migrationBuilder.DropColumn(
                name: "TrustError",
                table: "Machines");

            migrationBuilder.DropColumn(
                name: "TrustErrorAt",
                table: "Machines");
        }
    }
}
