using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleetdeck.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class IpadControlWatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ProfileSeen",
                table: "Ipads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SetupDone",
                table: "Ipads",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<DateTime>(
                name: "WatchAlertSince",
                table: "Ipads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WatchEnabled",
                table: "Ipads",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "WatchSnoozeUntil",
                table: "Ipads",
                type: "TEXT",
                nullable: true);

            // Uden fast IP-adresse kan LastSeen kun stamme fra klient-id'et. Med fast IP-adresse vides det ikke, så de venter på næste opslag via profilen.
            migrationBuilder.Sql("UPDATE Ipads SET ProfileSeen = LastSeen WHERE LanIp IS NULL AND DeviceType IN ('ipad', 'iphone');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProfileSeen",
                table: "Ipads");

            migrationBuilder.DropColumn(
                name: "SetupDone",
                table: "Ipads");

            migrationBuilder.DropColumn(
                name: "WatchAlertSince",
                table: "Ipads");

            migrationBuilder.DropColumn(
                name: "WatchEnabled",
                table: "Ipads");

            migrationBuilder.DropColumn(
                name: "WatchSnoozeUntil",
                table: "Ipads");
        }
    }
}
