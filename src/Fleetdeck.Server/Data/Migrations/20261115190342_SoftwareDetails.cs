using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleetdeck.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class SoftwareDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "Software",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Exe",
                table: "Software",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IconHash",
                table: "Software",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InstallPath",
                table: "Software",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SoftwareIcons",
                columns: table => new
                {
                    Hash = table.Column<string>(type: "TEXT", nullable: false),
                    Png = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Created = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SoftwareIcons", x => x.Hash);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SoftwareIcons");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "Software");

            migrationBuilder.DropColumn(
                name: "Exe",
                table: "Software");

            migrationBuilder.DropColumn(
                name: "IconHash",
                table: "Software");

            migrationBuilder.DropColumn(
                name: "InstallPath",
                table: "Software");
        }
    }
}
