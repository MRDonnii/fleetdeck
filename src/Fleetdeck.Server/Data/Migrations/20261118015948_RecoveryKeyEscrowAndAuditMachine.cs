using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleetdeck.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class RecoveryKeyEscrowAndAuditMachine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MachineId",
                table: "Audit",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RecoveryKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MachineId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Volume = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ProtectorId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Protected = table.Column<string>(type: "TEXT", nullable: false),
                    Created = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeen = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Superseded = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    JobId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecoveryKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecoveryKeys_Machines_MachineId",
                        column: x => x.MachineId,
                        principalTable: "Machines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Audit_MachineId",
                table: "Audit",
                column: "MachineId");

            migrationBuilder.CreateIndex(
                name: "IX_Audit_Ts",
                table: "Audit",
                column: "Ts");

            migrationBuilder.CreateIndex(
                name: "IX_RecoveryKeys_MachineId_Superseded",
                table: "RecoveryKeys",
                columns: new[] { "MachineId", "Superseded" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RecoveryKeys");

            migrationBuilder.DropIndex(
                name: "IX_Audit_MachineId",
                table: "Audit");

            migrationBuilder.DropIndex(
                name: "IX_Audit_Ts",
                table: "Audit");

            migrationBuilder.DropColumn(
                name: "MachineId",
                table: "Audit");
        }
    }
}
