using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleetdeck.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class YoutubeKidsSeparate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "KidsDailyMin",
                table: "Ipads",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "KidsLinked",
                table: "Ipads",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "KidsMode",
                table: "Ipads",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "allowed");

            migrationBuilder.AddColumn<string>(
                name: "KidsWindows",
                table: "Ipads",
                type: "TEXT",
                maxLength: 336,
                nullable: true);

            // Samme adfærd som før: "Spærret" ramte også YouTube Kids, og "Kun YouTube Kids" er nu YouTube spærret med YouTube Kids tilladt.
            migrationBuilder.Sql("UPDATE Ipads SET KidsMode = 'blocked' WHERE YtMode = 'blocked';");
            migrationBuilder.Sql("UPDATE Ipads SET YtMode = 'blocked', KidsMode = 'allowed' WHERE YtMode = 'kids';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "KidsDailyMin",
                table: "Ipads");

            migrationBuilder.DropColumn(
                name: "KidsLinked",
                table: "Ipads");

            migrationBuilder.DropColumn(
                name: "KidsMode",
                table: "Ipads");

            migrationBuilder.DropColumn(
                name: "KidsWindows",
                table: "Ipads");
        }
    }
}
