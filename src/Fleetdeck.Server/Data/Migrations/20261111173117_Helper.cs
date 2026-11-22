using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleetdeck.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Helper : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AutoFix",
                table: "Profiles",
                type: "TEXT",
                nullable: false,
                defaultValue: "[\"defender_signatures\",\"crash_analysis\",\"retry_failed\"]");

            migrationBuilder.AddColumn<int>(
                name: "FixAttempts",
                table: "Alerts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "FixJobId",
                table: "Alerts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastFixAt",
                table: "Alerts",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoFix",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "FixAttempts",
                table: "Alerts");

            migrationBuilder.DropColumn(
                name: "FixJobId",
                table: "Alerts");

            migrationBuilder.DropColumn(
                name: "LastFixAt",
                table: "Alerts");
        }
    }
}
