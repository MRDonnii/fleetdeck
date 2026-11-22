using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleetdeck.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class HelpdeskBeskeder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HelpdeskConversations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MachineId = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Created = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastActivity = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Preview = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    LastFromMachine = table.Column<bool>(type: "INTEGER", nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ClosedBy = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HelpdeskConversations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HelpdeskConversations_Machines_MachineId",
                        column: x => x.MachineId,
                        principalTable: "Machines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HelpdeskMessages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ConversationId = table.Column<int>(type: "INTEGER", nullable: false),
                    MachineId = table.Column<int>(type: "INTEGER", nullable: false),
                    FromMachine = table.Column<bool>(type: "INTEGER", nullable: false),
                    Author = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Text = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    ButtonsJson = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "[]"),
                    Answer = table.Column<int>(type: "INTEGER", nullable: true),
                    AnswerAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReplyToId = table.Column<int>(type: "INTEGER", nullable: true),
                    Created = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DeliveredAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReadAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Info = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    HasScreenshot = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HelpdeskMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HelpdeskMessages_HelpdeskConversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "HelpdeskConversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HelpdeskScreenshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MessageId = table.Column<int>(type: "INTEGER", nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Data = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HelpdeskScreenshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HelpdeskScreenshots_HelpdeskMessages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "HelpdeskMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HelpdeskConversations_MachineId",
                table: "HelpdeskConversations",
                column: "MachineId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HelpdeskConversations_Status_LastActivity",
                table: "HelpdeskConversations",
                columns: new[] { "Status", "LastActivity" });

            migrationBuilder.CreateIndex(
                name: "IX_HelpdeskMessages_ConversationId",
                table: "HelpdeskMessages",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_HelpdeskMessages_MachineId_FromMachine_DeliveredAt",
                table: "HelpdeskMessages",
                columns: new[] { "MachineId", "FromMachine", "DeliveredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_HelpdeskScreenshots_MessageId",
                table: "HelpdeskScreenshots",
                column: "MessageId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HelpdeskScreenshots");

            migrationBuilder.DropTable(
                name: "HelpdeskMessages");

            migrationBuilder.DropTable(
                name: "HelpdeskConversations");
        }
    }
}
