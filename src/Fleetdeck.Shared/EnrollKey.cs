using System.Text.RegularExpressions;

namespace Fleetdeck.Shared;

/// <summary>
/// Finder serveradresse og tilmeldingsnøgle i det, brugeren indsætter: selve nøglen, kommandoen fra Fleetdeck
/// (install.ps1/install.sh?key=…), linket med nøgle (/hent#k=…) – også med anførselstegn, mellemrum eller usynlige tegn omkring.
/// </summary>
public static partial class EnrollKey
{
    [GeneratedRegex(@"(https?://[^\s""'/#?]+)?[^\s""']*?[?&#](?:key|k)=([A-Za-z0-9_\-]+)")]
    private static partial Regex WithKey();

    public static (string? Server, string Key) Parse(string? text)
    {
        var t = new string((text ?? "").Where(c => !char.IsControl(c) && c is not ('​' or '‌' or '‍' or '﻿')).ToArray()).Trim();
        if (WithKey().Match(t) is { Success: true } m)
            return (m.Groups[1].Success ? m.Groups[1].Value : null, m.Groups[2].Value);
        return (null, t.Trim('"', '\'', '`', ' ', '<', '>'));
    }
}
