using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleetdeck.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AccessRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PendingProfileId",
                table: "Machines",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AccessRequestId",
                table: "EnrollCodes",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "EnrollCodes",
                type: "TEXT",
                maxLength: 31,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AccessRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SecretHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 7, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 31, nullable: false),
                    Platform = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    FactsJson = table.Column<string>(type: "TEXT", nullable: true),
                    Os = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    Model = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    RequestedBy = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Message = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Ip = table.Column<string>(type: "TEXT", maxLength: 45, nullable: false),
                    ObservedPin = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    PinVerified = table.Column<bool>(type: "INTEGER", nullable: false),
                    Created = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Expires = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastPoll = table.Column<DateTime>(type: "TEXT", nullable: true),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ProfileId = table.Column<int>(type: "INTEGER", nullable: true),
                    ApprovedName = table.Column<string>(type: "TEXT", maxLength: 31, nullable: true),
                    DecidedBy = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RejectReason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    EnrollCodeId = table.Column<int>(type: "INTEGER", nullable: true),
                    MachineId = table.Column<int>(type: "INTEGER", nullable: true),
                    PublicKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    SealedKey = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessRequests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccessRequests_SecretHash",
                table: "AccessRequests",
                column: "SecretHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccessRequests_State_Ip",
                table: "AccessRequests",
                columns: new[] { "State", "Ip" });

            // "Anmod om adgang" er slået fra på installationer, der opgraderes (der findes maskiner): administratoren slår det selv til.
            // Nye installationer får standarden "kun hjemmenet og VPN".
            migrationBuilder.Sql("INSERT OR IGNORE INTO Settings (Key, Value) SELECT 'access_requests', 'off' WHERE EXISTS (SELECT 1 FROM Machines);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccessRequests");

            migrationBuilder.DropColumn(
                name: "PendingProfileId",
                table: "Machines");

            migrationBuilder.DropColumn(
                name: "AccessRequestId",
                table: "EnrollCodes");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "EnrollCodes");
        }
    }
}
