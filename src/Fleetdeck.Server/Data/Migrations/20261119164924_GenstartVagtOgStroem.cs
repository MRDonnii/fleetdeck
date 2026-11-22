using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleetdeck.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class GenstartVagtOgStroem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PowerMinWatts",
                table: "Machines",
                type: "INTEGER",
                nullable: false,
                defaultValue: 15);   // eksisterende maskiner: over 15 W = tændt

            migrationBuilder.AddColumn<string>(
                name: "PowerSensor",
                table: "Machines",
                type: "TEXT",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PowerSwitch",
                table: "Machines",
                type: "TEXT",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "BootBefore",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "BootBeforePrecise",
                table: "Jobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "HungAt",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PowerAt",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "WakeSentAt",
                table: "Jobs",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PowerMinWatts",
                table: "Machines");

            migrationBuilder.DropColumn(
                name: "PowerSensor",
                table: "Machines");

            migrationBuilder.DropColumn(
                name: "PowerSwitch",
                table: "Machines");

            migrationBuilder.DropColumn(
                name: "BootBefore",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "BootBeforePrecise",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "HungAt",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "PowerAt",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "WakeSentAt",
                table: "Jobs");
        }
    }
}
