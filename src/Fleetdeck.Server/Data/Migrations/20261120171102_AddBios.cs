using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleetdeck.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBios : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BiosPackages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Vendor = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CatalogKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Version = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    SourceUrl = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ShaSource = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Path = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    RequestedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Created = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DownloadedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastVerifiedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastUsedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Capsule = table.Column<bool>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BiosPackages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BiosSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MachineId = table.Column<int>(type: "INTEGER", nullable: false),
                    TakenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Driver = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    BiosVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    EsrtVersion = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    SecureBoot = table.Column<bool>(type: "INTEGER", nullable: true),
                    TpmState = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    DiskEncryption = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    ItemsJson = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "[]"),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    By = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Count = table.Column<int>(type: "INTEGER", nullable: false),
                    AdminPassword = table.Column<bool>(type: "INTEGER", nullable: false),
                    PendingReboot = table.Column<bool>(type: "INTEGER", nullable: true),
                    JobId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BiosSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BiosSnapshots_Machines_MachineId",
                        column: x => x.MachineId,
                        principalTable: "Machines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BiosStatus",
                columns: table => new
                {
                    MachineId = table.Column<int>(type: "INTEGER", nullable: false),
                    Vendor = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CatalogKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    MatchQuality = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    Current = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CurrentNumeric = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    CurrentDate = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    Latest = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    LatestNumeric = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    LatestDate = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Critical = table.Column<bool>(type: "INTEGER", nullable: false),
                    ChangeSummaryJson = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "[]"),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    FwupdVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CheckedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BiosStatus", x => x.MachineId);
                    table.ForeignKey(
                        name: "FK_BiosStatus_Machines_MachineId",
                        column: x => x.MachineId,
                        principalTable: "Machines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BiosUpdateRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MachineId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Route = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Vendor = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Family = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    FromVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    TargetVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TargetNumeric = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    PackageId = table.Column<int>(type: "INTEGER", nullable: true),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    JobId = table.Column<int>(type: "INTEGER", nullable: true),
                    PreSnapshotId = table.Column<int>(type: "INTEGER", nullable: true),
                    ChecksJson = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "[]"),
                    ReportJson = table.Column<string>(type: "TEXT", nullable: true),
                    Message = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    StagedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RebootRequestedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BiosUpdateRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BiosUpdateRuns_Machines_MachineId",
                        column: x => x.MachineId,
                        principalTable: "Machines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BiosPackages_Vendor_CatalogKey_Version_Kind",
                table: "BiosPackages",
                columns: new[] { "Vendor", "CatalogKey", "Version", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BiosSnapshots_MachineId_TakenAt",
                table: "BiosSnapshots",
                columns: new[] { "MachineId", "TakenAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BiosUpdateRuns_MachineId_CreatedAt",
                table: "BiosUpdateRuns",
                columns: new[] { "MachineId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BiosPackages");

            migrationBuilder.DropTable(
                name: "BiosSnapshots");

            migrationBuilder.DropTable(
                name: "BiosStatus");

            migrationBuilder.DropTable(
                name: "BiosUpdateRuns");
        }
    }
}
