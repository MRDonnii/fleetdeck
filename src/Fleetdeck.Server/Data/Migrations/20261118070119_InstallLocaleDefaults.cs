using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleetdeck.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class InstallLocaleDefaults : Migration
    {
        /// <summary>
        /// Netværksinstallationer brugte før faste danske værdier. Eksisterende installationer (der findes brugere) får dem
        /// skrevet ind som indstillinger, så intet ændrer sig. En ny installation får i stedet værdierne fra opsætningsguiden.
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var (key, value) in Defaults)
                migrationBuilder.Sql($"INSERT OR IGNORE INTO \"Settings\" (\"Key\", \"Value\") SELECT '{key}', '{value}' WHERE EXISTS (SELECT 1 FROM \"Users\");");
        }

        internal static readonly (string Key, string Value)[] Defaults =
        [
            ("install_language", "da-DK"), ("install_keyboard", "dk"), ("install_timezone", "Europe/Copenhagen"),
        ];

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var (key, _) in Defaults)
                migrationBuilder.Sql($"DELETE FROM \"Settings\" WHERE \"Key\" = '{key}';");
        }
    }
}
