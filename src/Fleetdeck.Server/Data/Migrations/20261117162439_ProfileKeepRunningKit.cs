using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleetdeck.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProfileKeepRunningKit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "KeepRunning",
                table: "Profiles",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<int>(
                name: "KitDay",
                table: "Profiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "KitMode",
                table: "Profiles",
                type: "TEXT",
                nullable: false,
                defaultValue: "off");

            migrationBuilder.AddColumn<string>(
                name: "KitPaths",
                table: "Profiles",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "KitTime",
                table: "Profiles",
                type: "TEXT",
                nullable: false,
                defaultValue: "01:30");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "KeepRunning",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "KitDay",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "KitMode",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "KitPaths",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "KitTime",
                table: "Profiles");
        }
    }
}
