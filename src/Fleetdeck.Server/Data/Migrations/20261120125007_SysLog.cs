using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleetdeck.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class SysLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "LiveLogCapable",
                table: "Machines",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "SysLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MachineId = table.Column<int>(type: "INTEGER", nullable: false),
                    Time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Level = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    Log = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    EventId = table.Column<int>(type: "INTEGER", nullable: true),
                    Message = table.Column<string>(type: "TEXT", maxLength: 700, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    GroupKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Hash = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Received = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SysLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SysLog_Machines_MachineId",
                        column: x => x.MachineId,
                        principalTable: "Machines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SysLog_MachineId_Hash",
                table: "SysLog",
                columns: new[] { "MachineId", "Hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SysLog_MachineId_Time",
                table: "SysLog",
                columns: new[] { "MachineId", "Time" });

            migrationBuilder.CreateIndex(
                name: "IX_SysLog_Time",
                table: "SysLog",
                column: "Time");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SysLog");

            migrationBuilder.DropColumn(
                name: "LiveLogCapable",
                table: "Machines");
        }
    }
}
