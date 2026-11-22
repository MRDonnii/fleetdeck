using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleetdeck.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class SpareCards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SpareCards",
                columns: table => new
                {
                    MachineId = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Time = table.Column<string>(type: "TEXT", maxLength: 5, nullable: false),
                    Netboot = table.Column<bool>(type: "INTEGER", nullable: false),
                    DiskId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    DiskLabel = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Serial = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    ConfirmedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ConfirmedBy = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    KnownDiskId = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    LastRun = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastOk = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    Kernel = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    Bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    JobId = table.Column<int>(type: "INTEGER", nullable: true),
                    NetbootSignature = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    NetbootStatus = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    NetbootMessage = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    NetbootAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpareCards", x => x.MachineId);
                    table.ForeignKey(
                        name: "FK_SpareCards_Machines_MachineId",
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
                name: "SpareCards");
        }
    }
}
