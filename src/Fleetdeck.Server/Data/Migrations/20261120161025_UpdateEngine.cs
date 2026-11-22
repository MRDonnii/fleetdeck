using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleetdeck.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class UpdateEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "UpdCatchUpDays",
                table: "Profiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<bool>(
                name: "UpdDrivers",
                table: "Profiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "UpdFeatureUpdates",
                table: "Profiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "UpdFirmware",
                table: "Profiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "UpdMaxRounds",
                table: "Profiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 4);

            migrationBuilder.AddColumn<bool>(
                name: "UpdNeverAuto",
                table: "Profiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "UpdQuietFrom",
                table: "Profiles",
                type: "TEXT",
                maxLength: 5,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "UpdQuietTo",
                table: "Profiles",
                type: "TEXT",
                maxLength: 5,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "UpdRebootNoWarning",
                table: "Profiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CatchUp",
                table: "MaintenanceRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "CatchUpUntil",
                table: "MaintenanceRuns",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<int>(
                name: "CurrentJobId",
                table: "MaintenanceRuns",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Engine",
                table: "MaintenanceRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "MaintenanceRuns",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "maintenance");

            migrationBuilder.AddColumn<string>(
                name: "LastLeft",
                table: "MaintenanceRuns",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<int>(
                name: "MaxRounds",
                table: "MaintenanceRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: 4);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextAt",
                table: "MaintenanceRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NotBefore",
                table: "MaintenanceRuns",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "PlanJson",
                table: "MaintenanceRuns",
                type: "TEXT",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<int>(
                name: "RebootTries",
                table: "MaintenanceRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Reboots",
                table: "MaintenanceRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ReportJson",
                table: "MaintenanceRuns",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "Round",
                table: "MaintenanceRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Step",
                table: "MaintenanceRuns",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "StopReason",
                table: "MaintenanceRuns",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ResultJson",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            // Ventilationsstyringens profil: aldrig automatisk opdatering eller genstart. Beskyttede maskiner er allerede spærret hver for sig
            // (AutoUpdateGuard); deres profil røres ikke, så de andre maskiner på samme profil beholder deres natlige opdatering.
            migrationBuilder.Sql("UPDATE Profiles SET UpdNeverAuto = 1 WHERE lower(Name) LIKE '%dantherm%';");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceRuns_MachineId_Finished",
                table: "MaintenanceRuns",
                columns: new[] { "MachineId", "Finished" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MaintenanceRuns_MachineId_Finished",
                table: "MaintenanceRuns");

            migrationBuilder.DropColumn(
                name: "UpdCatchUpDays",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "UpdDrivers",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "UpdFeatureUpdates",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "UpdFirmware",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "UpdMaxRounds",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "UpdNeverAuto",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "UpdQuietFrom",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "UpdQuietTo",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "UpdRebootNoWarning",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "CatchUp",
                table: "MaintenanceRuns");

            migrationBuilder.DropColumn(
                name: "CatchUpUntil",
                table: "MaintenanceRuns");

            migrationBuilder.DropColumn(
                name: "CurrentJobId",
                table: "MaintenanceRuns");

            migrationBuilder.DropColumn(
                name: "Engine",
                table: "MaintenanceRuns");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "MaintenanceRuns");

            migrationBuilder.DropColumn(
                name: "LastLeft",
                table: "MaintenanceRuns");

            migrationBuilder.DropColumn(
                name: "MaxRounds",
                table: "MaintenanceRuns");

            migrationBuilder.DropColumn(
                name: "NextAt",
                table: "MaintenanceRuns");

            migrationBuilder.DropColumn(
                name: "NotBefore",
                table: "MaintenanceRuns");

            migrationBuilder.DropColumn(
                name: "PlanJson",
                table: "MaintenanceRuns");

            migrationBuilder.DropColumn(
                name: "RebootTries",
                table: "MaintenanceRuns");

            migrationBuilder.DropColumn(
                name: "Reboots",
                table: "MaintenanceRuns");

            migrationBuilder.DropColumn(
                name: "ReportJson",
                table: "MaintenanceRuns");

            migrationBuilder.DropColumn(
                name: "Round",
                table: "MaintenanceRuns");

            migrationBuilder.DropColumn(
                name: "Step",
                table: "MaintenanceRuns");

            migrationBuilder.DropColumn(
                name: "StopReason",
                table: "MaintenanceRuns");

            migrationBuilder.DropColumn(
                name: "ResultJson",
                table: "Jobs");
        }
    }
}
