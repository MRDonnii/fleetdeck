using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleetdeck.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AppleMdm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MdmCommands",
                columns: table => new
                {
                    CommandUuid = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    IpadId = table.Column<int>(type: "INTEGER", nullable: false),
                    RequestType = table.Column<string>(type: "TEXT", maxLength: 48, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    RequestedBy = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    PayloadHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Created = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Updated = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Completed = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MdmCommands", x => x.CommandUuid);
                    table.ForeignKey(
                        name: "FK_MdmCommands_Ipads_IpadId",
                        column: x => x.IpadId,
                        principalTable: "Ipads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MdmDevices",
                columns: table => new
                {
                    IpadId = table.Column<int>(type: "INTEGER", nullable: false),
                    EnrollKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Udid = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    EnrollmentId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ExpectedSerial = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    Approved = table.Column<bool>(type: "INTEGER", nullable: false),
                    ApprovalNote = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Serial = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    DeviceName = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    ProductName = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    ModelName = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    OsVersion = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    BuildVersion = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    Supervised = table.Column<bool>(type: "INTEGER", nullable: true),
                    ActivationLock = table.Column<bool>(type: "INTEGER", nullable: true),
                    BatteryLevel = table.Column<double>(type: "REAL", nullable: true),
                    StorageTotalGb = table.Column<double>(type: "REAL", nullable: true),
                    StorageFreeGb = table.Column<double>(type: "REAL", nullable: true),
                    LastCheckin = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastInventory = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EnrolledAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CheckedOutAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Created = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IdentitySerial = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    IdentityExpires = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LockedViaMdm = table.Column<bool>(type: "INTEGER", nullable: false),
                    LockedInstalledHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    LockedInstalledVersion = table.Column<int>(type: "INTEGER", nullable: true),
                    OsTargetVersion = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    OsTargetBuild = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    OsTargetLocal = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    PushError = table.Column<string>(type: "TEXT", nullable: true),
                    NotSeenWarned = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MdmDevices", x => x.IpadId);
                    table.ForeignKey(
                        name: "FK_MdmDevices_Ipads_IpadId",
                        column: x => x.IpadId,
                        principalTable: "Ipads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MdmEnrollTokens",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    IpadId = table.Column<int>(type: "INTEGER", nullable: false),
                    Expires = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UsedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Fetches = table.Column<int>(type: "INTEGER", nullable: false),
                    ProfileEnc = table.Column<string>(type: "TEXT", nullable: false),
                    Created = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MdmEnrollTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MdmEnrollTokens_Ipads_IpadId",
                        column: x => x.IpadId,
                        principalTable: "Ipads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MdmCommands_IpadId_Created",
                table: "MdmCommands",
                columns: new[] { "IpadId", "Created" });

            migrationBuilder.CreateIndex(
                name: "IX_MdmDevices_EnrollKey",
                table: "MdmDevices",
                column: "EnrollKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MdmDevices_Udid",
                table: "MdmDevices",
                column: "Udid");

            migrationBuilder.CreateIndex(
                name: "IX_MdmEnrollTokens_IpadId",
                table: "MdmEnrollTokens",
                column: "IpadId");

            migrationBuilder.CreateIndex(
                name: "IX_MdmEnrollTokens_TokenHash",
                table: "MdmEnrollTokens",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MdmCommands");

            migrationBuilder.DropTable(
                name: "MdmDevices");

            migrationBuilder.DropTable(
                name: "MdmEnrollTokens");
        }
    }
}
