using System.Globalization;
using System.Text.RegularExpressions;

namespace Fleetdeck.Server.Services.Bios;

/// <summary>
/// En Lenovo-BIOS-version. Lenovo skriver den på tre måder:
/// <list type="bullet">
/// <item>som byggekode "M1UKT79A": billedpræfiks (4 tegn, "m1uk") + bogstav + niveau (2 hex-cifre, "79", "2B", "5C") + bogstav;</item>
/// <item>som Windows Update-version "1.0.0.121" eller "1.100" (major og minor; minor er niveauet som tal);</item>
/// <item>samlet i kataloget: "M1UKT79A_1.0.0.121", eller kun det ene af dem ("1.50" for ThinkPads, "M5QKT31A").</item>
/// </list>
/// Agentens SMBIOS-version er byggekoden, på ThinkPads med versionen i parentes ("N2HET77W (1.60 )"), og "BIOS release" er "1.117".
/// </summary>
public sealed partial record LenovoBiosVersion(string? Code, string? Image, int? Level, int? Major, int? Minor)
{
    public string? Numeric => Major is { } a && Minor is { } b ? $"{a}.{b}" : null;

    /// <summary>Kataloget/pakken: "M1UKT79A_1.0.0.121", "1.50", "M5QKT31A".</summary>
    public static LenovoBiosVersion FromCatalog(string? s)
    {
        s = s?.Trim() ?? "";
        var parts = s.Split('_', 2);
        var code = MatchCode(parts[0]);
        var num = code is null ? parts[0] : parts.Length > 1 ? parts[1] : null;
        return With(code, Number(num));
    }

    /// <summary>Agenten: SMBIOS-versionen (evt. med "(1.60 )") og "BIOS release" ("1.117").</summary>
    public static LenovoBiosVersion FromMachine(string? smbiosVersion, string? biosRelease)
    {
        var s = smbiosVersion?.Trim() ?? "";
        var paren = Paren().Match(s);
        var code = MatchCode(paren.Success ? s[..paren.Index].Trim() : s.Split(' ', 2)[0]);
        var num = paren.Success ? Number(paren.Groups[1].Value) : null;
        num ??= Number(biosRelease);
        if (code is null && num is null) num = Number(s);
        return With(code, num);
    }

    private static LenovoBiosVersion With(Match? code, (int, int)? num) => new(
        code?.Value.ToUpperInvariant(), code?.Groups[1].Value.ToLowerInvariant(),
        code is null ? null : int.Parse(code.Groups[3].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        num?.Item1, num?.Item2);

    private static Match? MatchCode(string? s) => s is null ? null : CodeRx().Match(s.Trim()) is { Success: true } m ? m : null;

    /// <summary>"1.0.0.121" → (1, 121); "1.117" → (1, 117); "1.60" → (1, 60). Alle dele skal være decimale tal ("1.0.0.3F" giver null).</summary>
    public static (int, int)? Number(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var p = s.Trim().Split('.');
        if (p.Length < 2) return null;
        var n = new int[p.Length];
        for (var i = 0; i < p.Length; i++)
            if (!int.TryParse(p[i], NumberStyles.None, CultureInfo.InvariantCulture, out n[i])) return null;
        return (n[0], n[^1]);
    }

    [GeneratedRegex(@"^([A-Z0-9]{4})([A-Z])([0-9A-F]{2})([A-Z0-9])$", RegexOptions.IgnoreCase)]
    private static partial Regex CodeRx();
    [GeneratedRegex(@"\(\s*([0-9.]+)\s*\)")]
    private static partial Regex Paren();
}

public enum VersionOrder { Older, Same, Newer, Unknown, OtherFamily, Conflict }

public static class BiosVersions
{
    /// <summary>
    /// Er <paramref name="candidate"/> nyere end <paramref name="current"/>? Kun i samme familie (samme billedpræfiks). Niveauet i
    /// byggekoden og Windows Update-versionen krydstjekkes: peger de i hver sin retning, er svaret <see cref="VersionOrder.Conflict"/>.
    /// </summary>
    public static VersionOrder Compare(LenovoBiosVersion current, LenovoBiosVersion candidate)
    {
        if (current.Image is { } a && candidate.Image is { } b && a != b) return VersionOrder.OtherFamily;
        int? byLevel = current.Level is { } l1 && candidate.Level is { } l2 ? l2.CompareTo(l1) : null;
        int? byNumber = current.Major is { } m1 && current.Minor is { } n1 && candidate.Major is { } m2 && candidate.Minor is { } n2
            ? (m2, n2).CompareTo((m1, n1)) : null;
        if (byLevel is { } x && byNumber is { } y && Math.Sign(x) != Math.Sign(y)) return VersionOrder.Conflict;
        return (byLevel ?? byNumber) switch
        {
            null => VersionOrder.Unknown,
            > 0 => VersionOrder.Newer,
            0 => VersionOrder.Same,
            _ => VersionOrder.Older,
        };
    }

    /// <summary>Lenovos &lt;Level&gt;-regler: "M1UKT7*" eller "M1UKT79*" (stjerne til sidst, ingen andre jokertegn).</summary>
    public static bool LevelMatches(string pattern, string? code)
    {
        if (string.IsNullOrEmpty(code) || string.IsNullOrWhiteSpace(pattern)) return false;
        pattern = pattern.Trim();
        return pattern.EndsWith('*')
            ? code.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(code, pattern, StringComparison.OrdinalIgnoreCase);
    }
}
