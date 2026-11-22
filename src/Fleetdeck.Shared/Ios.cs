using System.Globalization;
using System.Text.Json;

namespace Fleetdeck.Shared;

/// <summary>Hvad et ios-job (iPad via Mac'en) kan gøre.</summary>
public static class IosActions
{
    public const string List = "list", InstallProfile = "install_profile", Backup = "backup", Update = "update",
        Supervise = "supervise", Restore = "restore_backup", Help = "cfgutil_help",
        /// <summary>Fjern den låste profil (og den gamle DNS-profil) med husstandens overvågningsidentitet – "Løsn styring".</summary>
        RemoveProfile = "remove_profile",
        /// <summary>"Frigiv helt": fjern profilen → krypteret backup (også til Fleetdeck) → slet → gendan backuppen UDEN at overvåge igen.</summary>
        Release = "release",
        /// <summary>"Overvåg uden datatab" (<see cref="NoWipeFlow"/>) – startes kun fra Opsætning → Overvåg uden datatab.</summary>
        SuperviseNoWipe = "supervise_nowipe";
    public static readonly string[] All = [List, InstallProfile, Backup, Update, Supervise, Restore, Help, RemoveProfile, Release, SuperviseNoWipe];
    /// <summary>Handlinger, der sletter iPad'en: kun administrator, og navnet skal skrives som bekræftelse.</summary>
    public static readonly string[] Erasing = [Supervise, Restore, Release, SuperviseNoWipe];
    /// <summary>Handlinger, der henter hemmeligheder (overvågningsidentitet, backup-adgangskode) med en engangskode.</summary>
    public static readonly string[] NeedSecrets = [InstallProfile, Backup, Supervise, Restore, RemoveProfile, Release, SuperviseNoWipe];
}

/// <summary>En installeret konfigurationsprofil på en iPad (fra cfgutil get configurationProfiles).</summary>
public sealed class IosProfileInfo
{
    public string Identifier { get; set; } = "";
    public string? Name { get; set; }
    public int? Version { get; set; }
}

/// <summary>En iPad, som Mac'en kan se via kabel.</summary>
public sealed class IosDeviceInfo
{
    public string Ecid { get; set; } = "";
    public string? Udid { get; set; }
    public string? Name { get; set; }
    public string? DeviceType { get; set; }
    public string? DeviceClass { get; set; }
    public string? Serial { get; set; }
    public string? Firmware { get; set; }
    public string? Build { get; set; }
    public bool? Supervised { get; set; }
    public bool? Paired { get; set; }
    public int? Battery { get; set; }
    public bool? Charging { get; set; }
    public bool? Passcode { get; set; }
    public string? BootedState { get; set; }
    public string? ActivationState { get; set; }
    /// <summary>null = ukendt (iPad'en svarede ikke, fx fordi den ikke er parret).</summary>
    public List<IosProfileInfo>? Profiles { get; set; }
}

/// <summary>Resultatet af et ios-job. Full = alle tilsluttede iPads (list); ellers kun den ene iPad, jobbet handlede om.</summary>
public sealed class IosReport
{
    public bool Full { get; set; }
    public List<IosDeviceInfo> Devices { get; set; } = [];
}

/// <summary>
/// Læser JSON-svar fra Apple Configurators cfgutil (--format JSON). Formatet er kun løst dokumenteret, så alt læses forsigtigt:
/// fejl kan komme med exit-kode 0 (Type = "Error"), og "get" lægger fejl pr. egenskab under "Errors" (eller "Error") inde i Output.
/// </summary>
public static class Cfgutil
{
    /// <summary>Egenskaber, der hentes pr. iPad med "get" (dem, alle iPads har – ICCID/IMEI giver fejl på wifi-modeller).</summary>
    public static readonly string[] GetProps = ["ECID", "UDID", "name", "serialNumber", "deviceType", "deviceClass", "firmwareVersion", "buildVersion",
        "isSupervised", "isPaired", "batteryCurrentCapacity", "batteryIsCharging", "passcodeProtected", "bootedState", "activationState", "configurationProfiles"];

    /// <summary>Kendte fejlkoder.</summary>
    public const int ProfileAlreadyInstalled = 118;

    /// <summary>ECID som "0x" + store hex-cifre, så værdier fra list, get og brugeren kan sammenlignes.</summary>
    public static string NormEcid(string? s)
    {
        var t = (s ?? "").Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        if (t.Length == 0) return "";
        if (!t.All(char.IsAsciiHexDigit)) return "";
        var h = t.TrimStart('0').ToUpperInvariant();
        return "0x" + (h.Length == 0 ? "0" : h);
    }

    /// <summary>ECID fra et JSON-felt: tekst er hex (0x…), et tal er decimalt.</summary>
    private static string Ecid(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => NormEcid(v.GetString()),
            JsonValueKind.Number when v.TryGetUInt64(out var n) => "0x" + n.ToString("X", CultureInfo.InvariantCulture),
            _ => "",
        } : "";

    /// <summary>Det JSON-objekt, cfgutil skrev (der kan stå andet før/efter, fx advarsler). null hvis intet kunne læses.</summary>
    public static JsonElement? ExtractJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        if (TryParse(t) is { } whole) return whole;
        // flere JSON-beskeder (--progress) eller tekst omkring: tag den sidste hele linje, der er et objekt, ellers fra første { til sidste }
        foreach (var line in Enumerable.Reverse(t.Split('\n')))
            if (line.TrimStart().StartsWith('{') && TryParse(line.Trim()) is { } l) return l;
        var a = t.IndexOf('{'); var b = t.LastIndexOf('}');
        return a >= 0 && b > a ? TryParse(t[a..(b + 1)]) : null;
    }

    private static JsonElement? TryParse(string s)
    {
        try
        {
            using var d = JsonDocument.Parse(s);
            return d.RootElement.ValueKind == JsonValueKind.Object ? d.RootElement.Clone() : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Fejlbesked fra et Type=Error-svar (med kode), ellers null.</summary>
    public static (string Message, int? Code)? Error(JsonElement root)
    {
        if (Str(root, "Type") != "Error") return null;
        var parts = new[] { Str(root, "Message"), Str(root, "FailureReason"), Str(root, "Detail") }
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()).Distinct().ToList();
        return (parts.Count > 0 ? string.Join(" – ", parts) : "cfgutil meldte en fejl", Int(root, "Code"));
    }

    /// <summary>"cfgutil --format JSON list": alle tilsluttede enheder (kun de grundlæggende oplysninger).</summary>
    public static List<IosDeviceInfo> ParseList(JsonElement root)
    {
        var list = new List<IosDeviceInfo>();
        if (!root.TryGetProperty("Output", out var output) || output.ValueKind != JsonValueKind.Object) return list;
        foreach (var dev in output.EnumerateObject())
        {
            if (dev.Name is "Errors" or "Error" || dev.Value.ValueKind != JsonValueKind.Object) continue;
            var d = dev.Value;
            var ecid = Ecid(d, "ECID") is { Length: > 0 } x ? x : NormEcid(dev.Name);
            if (ecid.Length == 0) continue;
            list.Add(new IosDeviceInfo
            {
                Ecid = ecid, Udid = Str(d, "UDID"), Name = Str(d, "name"), DeviceType = Str(d, "deviceType"), DeviceClass = Str(d, "deviceClass"),
            });
        }
        return list;
    }

    /// <summary>"cfgutil --format JSON -e ECID get …": oplysningerne om én iPad (null hvis den ikke er i svaret).</summary>
    public static IosDeviceInfo? ParseGet(JsonElement root, string ecid)
    {
        if (!root.TryGetProperty("Output", out var output) || output.ValueKind != JsonValueKind.Object) return null;
        var want = NormEcid(ecid);
        JsonElement? hit = null; var others = 0;
        foreach (var dev in output.EnumerateObject())
        {
            if (dev.Name is "Errors" or "Error" || dev.Value.ValueKind != JsonValueKind.Object) continue;
            others++;
            if (NormEcid(dev.Name) == want || Ecid(dev.Value, "ECID") == want) hit = dev.Value;
        }
        // kun én enhed i svaret, men nøglen har et andet format: brug den
        if (hit is null && others == 1) hit = output.EnumerateObject().First(x => x.Name is not ("Errors" or "Error") && x.Value.ValueKind == JsonValueKind.Object).Value;
        if (hit is not { } d) return null;
        return new IosDeviceInfo
        {
            Ecid = Ecid(d, "ECID") is { Length: > 0 } e ? e : want,
            Udid = Str(d, "UDID"), Name = Str(d, "name"), DeviceType = Str(d, "deviceType"), DeviceClass = Str(d, "deviceClass"),
            Serial = Str(d, "serialNumber"), Firmware = Str(d, "firmwareVersion"), Build = Str(d, "buildVersion"),
            Supervised = Bool(d, "isSupervised"), Paired = Bool(d, "isPaired"), Battery = Int(d, "batteryCurrentCapacity"), Charging = Bool(d, "batteryIsCharging"),
            Passcode = Bool(d, "passcodeProtected"), BootedState = Str(d, "bootedState"), ActivationState = Str(d, "activationState"),
            Profiles = Profiles(d),
        };
    }

    private static List<IosProfileInfo>? Profiles(JsonElement d)
    {
        if (!d.TryGetProperty("configurationProfiles", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        var list = new List<IosProfileInfo>();
        foreach (var p in arr.EnumerateArray())
        {
            if (p.ValueKind != JsonValueKind.Object) continue;
            var id = Str(p, "identifier") ?? Str(p, "PayloadIdentifier");
            if (string.IsNullOrEmpty(id)) continue;
            list.Add(new IosProfileInfo { Identifier = id, Name = Str(p, "displayName") ?? Str(p, "PayloadDisplayName"), Version = Int(p, "version") ?? Int(p, "PayloadVersion") });
        }
        return list;
    }

    /// <summary>Lægger nye oplysninger oven i de gamle (ukendte felter i den nye bevarer den gamle værdi).</summary>
    public static IosDeviceInfo Merge(IosDeviceInfo old, IosDeviceInfo n) => new()
    {
        Ecid = n.Ecid.Length > 0 ? n.Ecid : old.Ecid, Udid = n.Udid ?? old.Udid, Name = n.Name ?? old.Name, DeviceType = n.DeviceType ?? old.DeviceType,
        DeviceClass = n.DeviceClass ?? old.DeviceClass, Serial = n.Serial ?? old.Serial, Firmware = n.Firmware ?? old.Firmware, Build = n.Build ?? old.Build,
        Supervised = n.Supervised ?? old.Supervised, Paired = n.Paired ?? old.Paired, Battery = n.Battery ?? old.Battery, Charging = n.Charging ?? old.Charging,
        Passcode = n.Passcode ?? old.Passcode, BootedState = n.BootedState ?? old.BootedState, ActivationState = n.ActivationState ?? old.ActivationState,
        Profiles = n.Profiles ?? old.Profiles,
    };

    private static string? Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
            _ => null,
        } : null;

    private static int? Int(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) ? v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt64(out var n) => (int)Math.Clamp(n, int.MinValue, int.MaxValue),
            JsonValueKind.Number => (int)Math.Round(v.GetDouble()),
            JsonValueKind.String when int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
            _ => null,
        } : null;

    private static bool? Bool(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) ? v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.TryGetInt32(out var n) ? n != 0 : null,
            JsonValueKind.String => v.GetString()?.Trim().ToLowerInvariant() switch { "yes" or "true" or "1" => true, "no" or "false" or "0" => false, _ => null },
            _ => null,
        } : null;
}
