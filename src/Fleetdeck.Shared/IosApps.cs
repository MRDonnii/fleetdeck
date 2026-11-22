using System.Text.Json;

namespace Fleetdeck.Shared;

/// <summary>En installeret app på en iPad (bundle-id, navn og version).</summary>
public sealed class IosAppInfo
{
    public string BundleId { get; set; } = "";
    public string? Name { get; set; }
    /// <summary>Versionen som i App Store (CFBundleShortVersionString), ellers bundle-versionen.</summary>
    public string? Version { get; set; }
    /// <summary>Bundle-versionen (CFBundleVersion), hvis den kendes.</summary>
    public string? Build { get; set; }
}

/// <summary>Resultatet af et ios_apps-job: de apps, Mac'en så på én iPad.</summary>
public sealed class IosAppsReport
{
    public string Ecid { get; set; } = "";
    public string? DeviceName { get; set; }
    public List<IosAppInfo> Apps { get; set; } = [];
}

/// <summary>
/// Læser apps fra "cfgutil --format JSON -e ECID get installedApps" (eller "get all"). Formatet er kun løst dokumenteret, så både en liste
/// af objekter og et objekt med bundle-id som nøgle godtages, og flere navne pr. felt prøves.
/// </summary>
public static class CfgutilApps
{
    /// <summary>Egenskaben i cfgutil get, der giver de installerede apps.</summary>
    public const string Property = "installedApps";

    private static readonly string[] IdKeys = ["bundleIdentifier", "BundleIdentifier", "CFBundleIdentifier", "identifier", "Identifier", "bundleID", "bundleId"];
    private static readonly string[] NameKeys = ["displayName", "DisplayName", "itunesName", "CFBundleDisplayName", "bundleDisplayName", "CFBundleName", "name", "Name"];
    private static readonly string[] VersionKeys = ["bundleShortVersion", "shortVersion", "ShortVersion", "CFBundleShortVersionString", "bundleShortVersionString", "version", "Version"];
    private static readonly string[] BuildKeys = ["bundleVersion", "BundleVersion", "CFBundleVersion"];

    /// <summary>Apps for én iPad i svaret; null hvis svaret ikke indeholder nogen app-liste for den.</summary>
    public static List<IosAppInfo>? Parse(JsonElement root, string ecid)
    {
        if (!root.TryGetProperty("Output", out var output) || output.ValueKind != JsonValueKind.Object) return null;
        var want = Cfgutil.NormEcid(ecid);
        JsonElement? hit = null; JsonElement? only = null; var count = 0;
        foreach (var dev in output.EnumerateObject())
        {
            if (dev.Name is "Errors" or "Error" || dev.Value.ValueKind != JsonValueKind.Object) continue;
            count++; only = dev.Value;
            if (Cfgutil.NormEcid(dev.Name) == want) hit = dev.Value;
        }
        if (hit is null && count == 1) hit = only;
        if (hit is not { } d) return null;
        foreach (var p in d.EnumerateObject())
            if (p.Name.Equals(Property, StringComparison.OrdinalIgnoreCase)) return Apps(p.Value);
        return null;
    }

    /// <summary>En app-liste i et af de kendte formater.</summary>
    public static List<IosAppInfo> Apps(JsonElement list)
    {
        var res = new List<IosAppInfo>();
        if (list.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in list.EnumerateArray())
                if (a.ValueKind == JsonValueKind.Object && One(a, null) is { } x) res.Add(x);
                else if (a.ValueKind == JsonValueKind.String && a.GetString() is { Length: > 0 } s) res.Add(new IosAppInfo { BundleId = s.Trim() });
        }
        else if (list.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in list.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Object && One(p.Value, p.Name) is { } x) res.Add(x);
        }
        return res.Where(a => a.BundleId.Length is > 0 and <= 200).GroupBy(a => a.BundleId, StringComparer.Ordinal).Select(g => g.First())
            .OrderBy(a => a.Name ?? a.BundleId, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static IosAppInfo? One(JsonElement a, string? key)
    {
        var id = First(a, IdKeys) ?? key;
        if (string.IsNullOrWhiteSpace(id)) return null;
        var build = First(a, BuildKeys);
        var version = First(a, VersionKeys) ?? build;
        return new IosAppInfo { BundleId = id.Trim(), Name = First(a, NameKeys)?.Trim(), Version = version?.Trim(), Build = build?.Trim() };
    }

    private static string? First(JsonElement e, string[] keys)
    {
        foreach (var k in keys)
            if (e.TryGetProperty(k, out var v))
                switch (v.ValueKind)
                {
                    case JsonValueKind.String when v.GetString() is { Length: > 0 } s: return s;
                    case JsonValueKind.Number: return v.GetRawText();
                }
        return null;
    }
}
