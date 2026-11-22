using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleetdeck.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Debloat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MachineDebloats",
                columns: table => new
                {
                    MachineId = table.Column<int>(type: "INTEGER", nullable: false),
                    Profile = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Include = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "'[]'"),
                    Exclude = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "'[]'"),
                    Enforce = table.Column<bool>(type: "INTEGER", nullable: false),
                    EnforceMode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Applied = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "'[]'"),
                    Drift = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "'[]'"),
                    StateJson = table.Column<string>(type: "TEXT", nullable: false),
                    BeforeJson = table.Column<string>(type: "TEXT", nullable: false),
                    Build = table.Column<int>(type: "INTEGER", nullable: true),
                    Edition = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    LastScan = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastApplied = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastReapply = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastJobId = table.Column<int>(type: "INTEGER", nullable: true),
                    RestorePoint = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MachineDebloats", x => x.MachineId);
                    table.ForeignKey(
                        name: "FK_MachineDebloats_Machines_MachineId",
                        column: x => x.MachineId,
                        principalTable: "Machines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MachineDebloats");
        }
    }
}
