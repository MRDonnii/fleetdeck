using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleetdeck.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class IpadApps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppStoreApps",
                columns: table => new
                {
                    BundleId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Found = table.Column<bool>(type: "INTEGER", nullable: false),
                    TrackId = table.Column<long>(type: "INTEGER", nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Version = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    IconUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    StoreUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Released = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Checked = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppStoreApps", x => x.BundleId);
                });

            migrationBuilder.CreateTable(
                name: "IpadApps",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    IpadId = table.Column<int>(type: "INTEGER", nullable: false),
                    BundleId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    Version = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Build = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Outdated = table.Column<bool>(type: "INTEGER", nullable: false),
                    Seen = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IpadApps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IpadApps_Ipads_IpadId",
                        column: x => x.IpadId,
                        principalTable: "Ipads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IpadAppScans",
                columns: table => new
                {
                    IpadId = table.Column<int>(type: "INTEGER", nullable: false),
                    ScannedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                    Requested = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RequestedVia = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IpadAppScans", x => x.IpadId);
                    table.ForeignKey(
                        name: "FK_IpadAppScans_Ipads_IpadId",
                        column: x => x.IpadId,
                        principalTable: "Ipads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IpadApps_BundleId",
                table: "IpadApps",
                column: "BundleId");

            migrationBuilder.CreateIndex(
                name: "IX_IpadApps_IpadId_BundleId",
                table: "IpadApps",
                columns: new[] { "IpadId", "BundleId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppStoreApps");

            migrationBuilder.DropTable(
                name: "IpadApps");

            migrationBuilder.DropTable(
                name: "IpadAppScans");
        }
    }
}
