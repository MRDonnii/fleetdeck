using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleetdeck.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Ipads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IpadAll",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Ipads",
                table: "Users",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<DateTime>(
                name: "LastLogin",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Perms",
                table: "Users",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.CreateTable(
                name: "Ipads",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Owner = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    Model = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    ClientId = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Schedule = table.Column<string>(type: "TEXT", maxLength: 336, nullable: false),
                    BlockedServices = table.Column<string>(type: "TEXT", nullable: false),
                    AllowedDomains = table.Column<string>(type: "TEXT", nullable: false),
                    BlockedDomains = table.Column<string>(type: "TEXT", nullable: false),
                    FilterAdult = table.Column<bool>(type: "INTEGER", nullable: false),
                    SafeSearch = table.Column<bool>(type: "INTEGER", nullable: false),
                    YtMode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    YtWindows = table.Column<string>(type: "TEXT", maxLength: 336, nullable: true),
                    YtDailyMin = table.Column<int>(type: "INTEGER", nullable: false),
                    YtBonusMin = table.Column<int>(type: "INTEGER", nullable: false),
                    YtBonusDay = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    PauseUntil = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FreeUntil = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSeen = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Applied = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ApplyError = table.Column<string>(type: "TEXT", nullable: true),
                    ProfileToken = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    ProfileTokenExpires = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Created = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Ipads", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IpadSlots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    IpadId = table.Column<int>(type: "INTEGER", nullable: false),
                    Service = table.Column<string>(type: "TEXT", maxLength: 48, nullable: false),
                    Slot = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IpadSlots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Ipads_ClientId",
                table: "Ipads",
                column: "ClientId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IpadSlots_IpadId_Service_Slot",
                table: "IpadSlots",
                columns: new[] { "IpadId", "Service", "Slot" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Ipads");

            migrationBuilder.DropTable(
                name: "IpadSlots");

            migrationBuilder.DropColumn(
                name: "IpadAll",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Ipads",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LastLogin",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Perms",
                table: "Users");
        }
    }
}
