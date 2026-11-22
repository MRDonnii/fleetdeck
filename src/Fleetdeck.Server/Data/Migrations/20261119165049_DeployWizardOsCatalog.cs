using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleetdeck.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class DeployWizardOsCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AskFields",
                table: "Deployments",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<bool>(
                name: "Encrypt",
                table: "Deployments",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Keyboard",
                table: "Deployments",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Language",
                table: "Deployments",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "NoAdmin",
                table: "Deployments",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "OsId",
                table: "Deployments",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SshKey",
                table: "Deployments",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TimeZone",
                table: "Deployments",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AskFields",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "Encrypt",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "Keyboard",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "Language",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "NoAdmin",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "OsId",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "SshKey",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "TimeZone",
                table: "Deployments");
        }
    }
}
