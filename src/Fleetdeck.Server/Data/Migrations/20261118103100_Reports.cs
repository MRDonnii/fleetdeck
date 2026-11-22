using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleetdeck.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Reports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FirstSeen",
                table: "Upgrades",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "OsUpdatesSince",
                table: "Machines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "HealthDaily",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MachineId = table.Column<int>(type: "INTEGER", nullable: false),
                    Day = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Score = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Samples = table.Column<int>(type: "INTEGER", nullable: false),
                    ScoreSum = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HealthDaily", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HealthDaily_MachineId_Day",
                table: "HealthDaily",
                columns: new[] { "MachineId", "Day" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HealthDaily");

            migrationBuilder.DropColumn(
                name: "FirstSeen",
                table: "Upgrades");

            migrationBuilder.DropColumn(
                name: "OsUpdatesSince",
                table: "Machines");
        }
    }
}
