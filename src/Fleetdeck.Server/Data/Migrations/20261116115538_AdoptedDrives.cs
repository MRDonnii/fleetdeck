using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleetdeck.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AdoptedDrives : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Adopted",
                table: "NetworkDrives",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Ignored",
                table: "NetworkDrives",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Adopted",
                table: "NetworkDrives");

            migrationBuilder.DropColumn(
                name: "Ignored",
                table: "NetworkDrives");
        }
    }
}
