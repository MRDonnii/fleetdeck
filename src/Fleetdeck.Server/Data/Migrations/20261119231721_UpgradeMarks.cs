using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleetdeck.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class UpgradeMarks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Blocked",
                table: "Upgrades",
                type: "TEXT",
                maxLength: 300,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "UpgradeMarks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MachineId = table.Column<int>(type: "INTEGER", nullable: false),
                    WingetId = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Available = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Hidden = table.Column<bool>(type: "INTEGER", nullable: false),
                    Blocked = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Created = table.Column<DateTime>(type: "TEXT", nullable: false),
                    By = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UpgradeMarks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UpgradeMarks_Machines_MachineId",
                        column: x => x.MachineId,
                        principalTable: "Machines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UpgradeMarks_MachineId_WingetId",
                table: "UpgradeMarks",
                columns: new[] { "MachineId", "WingetId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UpgradeMarks");

            migrationBuilder.DropColumn(
                name: "Blocked",
                table: "Upgrades");
        }
    }
}
