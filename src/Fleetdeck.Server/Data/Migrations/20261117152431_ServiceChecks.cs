using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleetdeck.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ServiceChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CheckBeats",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CheckId = table.Column<int>(type: "INTEGER", nullable: false),
                    Ts = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Up = table.Column<bool>(type: "INTEGER", nullable: false),
                    Ms = table.Column<int>(type: "INTEGER", nullable: true),
                    Message = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CheckBeats", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Checks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Target = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    MachineId = table.Column<int>(type: "INTEGER", nullable: true),
                    Group = table.Column<string>(type: "TEXT", maxLength: 60, nullable: true),
                    Keyword = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    KeywordInvert = table.Column<bool>(type: "INTEGER", nullable: false),
                    ExpectStatus = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    IgnoreTls = table.Column<bool>(type: "INTEGER", nullable: false),
                    CertWarnDays = table.Column<int>(type: "INTEGER", nullable: false),
                    Expect = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    PushToken = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    IntervalSec = table.Column<int>(type: "INTEGER", nullable: false),
                    TimeoutSec = table.Column<int>(type: "INTEGER", nullable: false),
                    FailThreshold = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Notify = table.Column<bool>(type: "INTEGER", nullable: false),
                    Created = table.Column<DateTime>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    FailCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastCheck = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastChange = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastMs = table.Column<int>(type: "INTEGER", nullable: true),
                    LastMessage = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    CertExpires = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CertWarned = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastPush = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Checks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CheckBeats_CheckId_Ts",
                table: "CheckBeats",
                columns: new[] { "CheckId", "Ts" });

            migrationBuilder.CreateIndex(
                name: "IX_Checks_PushToken",
                table: "Checks",
                column: "PushToken");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CheckBeats");

            migrationBuilder.DropTable(
                name: "Checks");
        }
    }
}
