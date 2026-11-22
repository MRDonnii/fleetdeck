using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleetdeck.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class PiCards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PiCmdlineFlags",
                table: "Profiles",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PiConfigLines",
                table: "Profiles",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PiExclude",
                table: "Profiles",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");   // en tom liste (JSON) – "" kan ikke læses som liste

            migrationBuilder.Sql("UPDATE Profiles SET PiExclude = '[]' WHERE PiExclude = '' OR PiExclude IS NULL;");

            migrationBuilder.CreateTable(
                name: "PiCards",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MachineId = table.Column<int>(type: "INTEGER", nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 63, nullable: false),
                    Mode = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    KitName = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    ImageId = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    EnrollCodeId = table.Column<int>(type: "INTEGER", nullable: true),
                    ProfileId = table.Column<int>(type: "INTEGER", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Progress = table.Column<int>(type: "INTEGER", nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    File = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    SettingsJson = table.Column<string>(type: "TEXT", nullable: false),
                    EventsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Created = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Expires = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Downloaded = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PiCards", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PiCards");

            migrationBuilder.DropColumn(
                name: "PiCmdlineFlags",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "PiConfigLines",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "PiExclude",
                table: "Profiles");
        }
    }
}
