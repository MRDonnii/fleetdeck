using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleetdeck.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class BeskederLogfiler : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HelpdeskAttachments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MachineId = table.Column<int>(type: "INTEGER", nullable: false),
                    MessageId = table.Column<int>(type: "INTEGER", nullable: true),
                    JobId = table.Column<int>(type: "INTEGER", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    EntriesJson = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "[]"),
                    By = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Created = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Data = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HelpdeskAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HelpdeskAttachments_HelpdeskMessages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "HelpdeskMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_HelpdeskAttachments_Machines_MachineId",
                        column: x => x.MachineId,
                        principalTable: "Machines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HelpdeskAttachments_MachineId_Created",
                table: "HelpdeskAttachments",
                columns: new[] { "MachineId", "Created" });

            migrationBuilder.CreateIndex(
                name: "IX_HelpdeskAttachments_MessageId",
                table: "HelpdeskAttachments",
                column: "MessageId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HelpdeskAttachments");
        }
    }
}
