using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleetdeck.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class DeviceType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeviceType",
                table: "Ipads",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "ipad");

            // eksisterende rækker: Apple TV'er (modelnavn "Apple TV …" eller identifier "AppleTV5,3") og iPhones får deres egen type
            migrationBuilder.Sql("UPDATE Ipads SET DeviceType = 'appletv' WHERE REPLACE(LOWER(COALESCE(Model, '')), ' ', '') LIKE '%appletv%';");
            migrationBuilder.Sql("UPDATE Ipads SET DeviceType = 'iphone' WHERE DeviceType = 'ipad' AND LOWER(COALESCE(Model, '')) LIKE '%iphone%';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeviceType",
                table: "Ipads");
        }
    }
}
