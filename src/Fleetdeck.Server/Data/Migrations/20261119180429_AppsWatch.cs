using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleetdeck.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AppsWatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WatchedApps",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Repo = table.Column<string>(type: "TEXT", maxLength: 140, nullable: false),
                    Channel = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    MachineId = table.Column<int>(type: "INTEGER", nullable: true),
                    PlaceKind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    PlaceName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ReadKind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ReadTarget = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    ReadField = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    UpdateCommand = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    AutoUpdate = table.Column<bool>(type: "INTEGER", nullable: false),
                    NeverAuto = table.Column<bool>(type: "INTEGER", nullable: false),
                    Notify = table.Column<bool>(type: "INTEGER", nullable: false),
                    Created = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Running = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    RunningAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReadError = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    ReadJobId = table.Column<int>(type: "INTEGER", nullable: true),
                    ReleasesJson = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "[]"),
                    ETag = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Checked = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CheckError = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    NotifiedVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    AutoLastVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    UpdateJobId = table.Column<int>(type: "INTEGER", nullable: true),
                    UpdateTo = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WatchedApps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WatchedApps_Machines_MachineId",
                        column: x => x.MachineId,
                        principalTable: "Machines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WatchedApps_MachineId",
                table: "WatchedApps",
                column: "MachineId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WatchedApps");
        }
    }
}
