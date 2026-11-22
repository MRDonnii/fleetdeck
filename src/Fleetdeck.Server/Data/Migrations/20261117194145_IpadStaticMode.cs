using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleetdeck.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class IpadStaticMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "IosDevicesAt",
                table: "Machines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IosDevicesJson",
                table: "Machines",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<int>(
                name: "AgeRatingApps",
                table: "Ipads",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AllowAccountChanges",
                table: "Ipads",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AllowAppInstall",
                table: "Ipads",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AllowInAppPurchases",
                table: "Ipads",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeviceSeen",
                table: "Ipads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Ecid",
                table: "Ipads",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HiddenApps",
                table: "Ipads",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<int>(
                name: "LockedProfileApplied",
                table: "Ipads",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LockedProfileHash",
                table: "Ipads",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LockedProfileVersion",
                table: "Ipads",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "OsVersion",
                table: "Ipads",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RemovalPassword",
                table: "Ipads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Serial",
                table: "Ipads",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Supervised",
                table: "Ipads",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // familie-iPads er ikke begrænsede som standard (børne-iPads: App Store skjult, ingen køb i apps, konti låst)
            migrationBuilder.Sql("UPDATE Ipads SET AllowAppInstall = 1, AllowInAppPurchases = 1, AllowAccountChanges = 1 WHERE Kind = 'family'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IosDevicesAt",
                table: "Machines");

            migrationBuilder.DropColumn(
                name: "IosDevicesJson",
                table: "Machines");

            migrationBuilder.DropColumn(
                name: "AgeRatingApps",
                table: "Ipads");

            migrationBuilder.DropColumn(
                name: "AllowAccountChanges",
                table: "Ipads");

            migrationBuilder.DropColumn(
                name: "AllowAppInstall",
                table: "Ipads");

            migrationBuilder.DropColumn(
                name: "AllowInAppPurchases",
                table: "Ipads");

            migrationBuilder.DropColumn(
                name: "DeviceSeen",
                table: "Ipads");

            migrationBuilder.DropColumn(
                name: "Ecid",
                table: "Ipads");

            migrationBuilder.DropColumn(
                name: "HiddenApps",
                table: "Ipads");

            migrationBuilder.DropColumn(
                name: "LockedProfileApplied",
                table: "Ipads");

            migrationBuilder.DropColumn(
                name: "LockedProfileHash",
                table: "Ipads");

            migrationBuilder.DropColumn(
                name: "LockedProfileVersion",
                table: "Ipads");

            migrationBuilder.DropColumn(
                name: "OsVersion",
                table: "Ipads");

            migrationBuilder.DropColumn(
                name: "RemovalPassword",
                table: "Ipads");

            migrationBuilder.DropColumn(
                name: "Serial",
                table: "Ipads");

            migrationBuilder.DropColumn(
                name: "Supervised",
                table: "Ipads");
        }
    }
}
