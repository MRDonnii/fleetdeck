using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleetdeck.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class TerminalPhase1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Protected",
                table: "Machines",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "TerminalCapable",
                table: "Machines",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "TerminalFavorite",
                table: "Machines",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "TerminalHosts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Address = table.Column<string>(type: "TEXT", maxLength: 253, nullable: false),
                    Port = table.Column<int>(type: "INTEGER", nullable: false),
                    Username = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Group = table.Column<string>(type: "TEXT", maxLength: 60, nullable: true),
                    Favorite = table.Column<bool>(type: "INTEGER", nullable: false),
                    Protected = table.Column<bool>(type: "INTEGER", nullable: false),
                    CredentialId = table.Column<int>(type: "INTEGER", nullable: true),
                    HostKeyType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    HostKeyFingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    PendingKeyType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    PendingKeyFingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Created = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastConnected = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TerminalHosts", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TerminalHosts");

            migrationBuilder.DropColumn(
                name: "Protected",
                table: "Machines");

            migrationBuilder.DropColumn(
                name: "TerminalCapable",
                table: "Machines");

            migrationBuilder.DropColumn(
                name: "TerminalFavorite",
                table: "Machines");
        }
    }
}
