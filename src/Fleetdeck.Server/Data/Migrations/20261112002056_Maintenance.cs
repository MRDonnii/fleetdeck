using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleetdeck.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Maintenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MaintAfter",
                table: "Profiles",
                type: "TEXT",
                nullable: false,
                defaultValue: "shutdown_if_woken");

            migrationBuilder.AddColumn<int>(
                name: "MaintDay",
                table: "Profiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 6);

            migrationBuilder.AddColumn<string>(
                name: "MaintMode",
                table: "Profiles",
                type: "TEXT",
                nullable: false,
                defaultValue: "off");

            migrationBuilder.AddColumn<bool>(
                name: "MaintReport",
                table: "Profiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "MaintTasks",
                table: "Profiles",
                type: "TEXT",
                nullable: false,
                defaultValue: "[\"windows_update\",\"upgrade\"]");

            migrationBuilder.AddColumn<string>(
                name: "MaintTime",
                table: "Profiles",
                type: "TEXT",
                nullable: false,
                defaultValue: "02:30");

            migrationBuilder.AddColumn<bool>(
                name: "MaintWake",
                table: "Profiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "MaintenanceRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MachineId = table.Column<int>(type: "INTEGER", nullable: false),
                    Batch = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Created = table.Column<DateTime>(type: "TEXT", nullable: false),
                    WasOffline = table.Column<bool>(type: "INTEGER", nullable: false),
                    WakeSent = table.Column<bool>(type: "INTEGER", nullable: false),
                    JobIds = table.Column<string>(type: "TEXT", nullable: false),
                    Finished = table.Column<DateTime>(type: "TEXT", nullable: true),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Summary = table.Column<string>(type: "TEXT", nullable: false),
                    Reported = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MaintenanceRuns_Machines_MachineId",
                        column: x => x.MachineId,
                        principalTable: "Machines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceRuns_MachineId_Created",
                table: "MaintenanceRuns",
                columns: new[] { "MachineId", "Created" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MaintenanceRuns");

            migrationBuilder.DropColumn(
                name: "MaintAfter",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "MaintDay",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "MaintMode",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "MaintReport",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "MaintTasks",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "MaintTime",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "MaintWake",
                table: "Profiles");
        }
    }
}
