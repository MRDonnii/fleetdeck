using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleetdeck.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Printere : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Printers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Uri = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    Location = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    MakeModel = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Paper = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Duplex = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    Color = table.Column<bool>(type: "INTEGER", nullable: false),
                    NeedsDriver = table.Column<bool>(type: "INTEGER", nullable: false),
                    Driver = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    DriverPackageId = table.Column<int>(type: "INTEGER", nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    StatusJson = table.Column<string>(type: "TEXT", nullable: true),
                    LastPolled = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Created = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Printers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PrinterAssignments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PrinterId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProfileId = table.Column<int>(type: "INTEGER", nullable: true),
                    MachineId = table.Column<int>(type: "INTEGER", nullable: true),
                    MakeDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    Keep = table.Column<bool>(type: "INTEGER", nullable: false),
                    Created = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrinterAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrinterAssignments_Printers_PrinterId",
                        column: x => x.PrinterId,
                        principalTable: "Printers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PrinterAssignments_PrinterId_ProfileId_MachineId",
                table: "PrinterAssignments",
                columns: new[] { "PrinterId", "ProfileId", "MachineId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PrinterAssignments");

            migrationBuilder.DropTable(
                name: "Printers");
        }
    }
}
