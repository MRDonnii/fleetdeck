using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleetdeck.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class MacKeepAwake : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "KeepAwakeAt",
                table: "Machines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KeepAwakeBy",
                table: "Machines",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "KeepAwakeOnAc",
                table: "Machines",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "KeepAwakeSource",
                table: "Machines",
                type: "TEXT",
                maxLength: 8,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "KeepAwakeAt",
                table: "Machines");

            migrationBuilder.DropColumn(
                name: "KeepAwakeBy",
                table: "Machines");

            migrationBuilder.DropColumn(
                name: "KeepAwakeOnAc",
                table: "Machines");

            migrationBuilder.DropColumn(
                name: "KeepAwakeSource",
                table: "Machines");
        }
    }
}
