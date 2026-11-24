using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleetdeck.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class PackageOutcomes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PackageOutcomes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MachineId = table.Column<int>(type: "INTEGER", nullable: false),
                    PackageId = table.Column<int>(type: "INTEGER", nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    JobId = table.Column<int>(type: "INTEGER", nullable: false),
                    Finished = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PackageOutcomes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PackageOutcomes_MachineId_PackageId",
                table: "PackageOutcomes",
                columns: new[] { "MachineId", "PackageId" },
                unique: true);

            // fyld ud fra jobhistorikken, før "Ryd op" kan nå at slette noget: seneste lykkede (af)installation pr. maskine og pakke
            migrationBuilder.Sql("""
                INSERT INTO PackageOutcomes (MachineId, PackageId, Type, JobId, Finished)
                SELECT j.MachineId, j.PackageId, j.Type, j.Id, COALESCE(j.Finished, j.Created) FROM Jobs j
                WHERE j.Status = 'success' AND j.Type IN ('install', 'uninstall') AND j.PackageId IS NOT NULL
                  AND j.MachineId IN (SELECT Id FROM Machines)
                  AND j.Id = (SELECT MAX(k.Id) FROM Jobs k WHERE k.MachineId = j.MachineId AND k.PackageId = j.PackageId
                              AND k.Status = 'success' AND k.Type IN ('install', 'uninstall'));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PackageOutcomes");
        }
    }
}
