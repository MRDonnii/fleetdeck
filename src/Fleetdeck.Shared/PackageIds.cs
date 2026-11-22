using System.Text.RegularExpressions;

namespace Fleetdeck.Shared;

/// <summary>
/// Værdier fra en pakke, der ender i anførselstegn på en kommandolinje (cmd /c start "" "…"): ingen anførselstegn, mellemrum
/// eller kontroltegn, så værdien aldrig kan afslutte anførselstegnene og starte en anden kommando (&amp;, | og &lt; er harmløse inden i dem).
/// </summary>
public static partial class PackageIds
{
    [GeneratedRegex(@"^[0-9]{1,12}\z")] private static partial Regex SteamRx();
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.-]{1,30}:[^\s""\p{C}]{1,2000}\z")] private static partial Regex UriRx();
    [GeneratedRegex(@"^[A-Za-z0-9 ._()+-]{1,120}\.(exe|msi)\z", RegexOptions.IgnoreCase)] private static partial Regex FileRx();

    /// <summary>Et Steam-app-id: kun cifre.</summary>
    public static bool SteamId(string? id) => id is not null && SteamRx().IsMatch(id);

    /// <summary>En adresse (fx ms-windows-store://… eller https://…) uden anførselstegn, mellemrum og kontroltegn.</summary>
    public static bool SafeUri(string? uri) => uri is not null && UriRx().IsMatch(uri);

    /// <summary>Filnavnet til et hentet installationsprogram: navnet fra pakken, hvis det er pænt, ellers setup.exe/setup.msi.</summary>
    public static string FileName(string? identifier)
    {
        var name = Path.GetFileName((identifier ?? "").Replace('\\', '/'));
        if (FileRx().IsMatch(name) && !name.StartsWith('.')) return name;
        return name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ? "setup.msi" : "setup.exe";
    }
}
