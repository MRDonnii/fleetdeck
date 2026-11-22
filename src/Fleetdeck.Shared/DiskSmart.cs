using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fleetdeck.Shared;

public static class DiskKinds
{
    public const string Nvme = "NVMe", Ssd = "SSD", Hdd = "HDD";
}

public static class DiskStates
{
    public const string Ok = "ok", Warning = "warning", Failing = "failing";
}

/// <summary>Rå værdier fra Windows (MSFT_PhysicalDisk og MSFT_StorageReliabilityCounter), så fortolkningen kan testes uden WMI.</summary>
public sealed record WinDiskRaw(string? FriendlyName, string? Model, string? Serial, int MediaType, int BusType, double Size, int HealthStatus,
    IReadOnlyList<ushort>? OperationalStatus, IReadOnlyDictionary<string, object?>? Reliability);

/// <summary>
/// Diskhelbred (SMART) pr. fysisk disk: fortolkning af smartctl -j (Linux, Unraid og Mac med smartmontools), Windows' Storage-WMI og
/// diskutil på Mac. Fælles for agenterne og serveren, så fortolkningen kan testes med eksempel-uddata.
/// </summary>
public static class DiskSmart
{
    /// <summary>
    /// Kan disken overhovedet give SMART-data? SD-kort og eMMC (mmcblk – fx en Raspberry Pi), RAM- og loop-enheder,
    /// software-RAID/LVM og optiske drev kan ikke, så for dem foreslås værktøjet til diskhelbred aldrig.
    /// </summary>
    public static bool CanHaveSmart(string? name)
    {
        var n = (name ?? "").Trim();
        if (n.StartsWith("/dev/", StringComparison.Ordinal)) n = n[5..];
        if (n.Length == 0) return false;
        string[] never = ["mmcblk", "zram", "loop", "ram", "md", "dm-", "nbd", "sr", "fd", "zd"];
        return !never.Any(p => n.StartsWith(p, StringComparison.Ordinal));
    }

    /// <summary>Kort id ud fra serienummeret (så en disk kan følges over tid), uden at serienummeret selv forlader maskinen.</summary>
    public static string? IdOf(string? serial)
    {
        var s = serial?.Trim();
        if (string.IsNullOrEmpty(s) || s.All(c => c is '0' or ' ' or '_' or '-' or '.')) return null;
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..12];
    }

    // ------------------------------------------------------------------ smartctl
    /// <summary>
    /// Fortolker <c>smartctl -a -j</c>. <paramref name="fallback"/> er det, agenten allerede ved (fx fra lsblk); dens felter beholdes,
    /// hvor smartctl ikke siger noget. Kører smartctl med <c>-n standby</c>, og disken sover, markeres den med <see cref="DiskHealth.Standby"/>.
    /// </summary>
    public static DiskHealth FromSmartctl(string json, DiskHealth? fallback = null)
    {
        var d = fallback ?? new DiskHealth();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return d; }
        using (doc)
        {
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return d;
            if (r.TryGetProperty("smartctl", out var sc) && sc.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array)
                foreach (var m in msgs.EnumerateArray())
                    if (m.TryGetProperty("string", out var ms) && ms.GetString() is { } text && Regex.IsMatch(text, @"\b(STANDBY|SLEEP)\b", RegexOptions.IgnoreCase))
                        d.Standby = true;
            d.Source = "smartctl";
            if (Str(r, "model_name") is { Length: > 0 } model) d.Name = model;
            else if (string.IsNullOrEmpty(d.Name) && Str(r, "device", "name") is { } dev) d.Name = dev;
            d.Id = IdOf(Str(r, "serial_number")) ?? d.Id;
            if (Num(r, "user_capacity", "bytes") is { } bytes && bytes > 0) d.SizeGb = Math.Round(bytes / 1e9);
            var protocol = Str(r, "device", "protocol");
            var rotation = Num(r, "rotation_rate");
            d.Kind = protocol?.Equals("NVMe", StringComparison.OrdinalIgnoreCase) == true ? DiskKinds.Nvme
                : rotation is > 0 ? DiskKinds.Hdd
                : rotation is 0 ? DiskKinds.Ssd
                : d.Kind ?? KindOf(d.Media, d.Bus);
            if (d.Kind == DiskKinds.Nvme) d.Bus ??= "NVMe";
            d.Media = d.Kind == DiskKinds.Hdd ? "HDD" : d.Kind is null ? d.Media : "SSD";
            if (d.Standby) return d;   // sover: kun identitet – serveren beholder de sidst kendte tal

            if (r.TryGetProperty("smart_status", out var st) && st.TryGetProperty("passed", out var pa) && pa.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                d.Health = pa.GetBoolean() ? "Healthy" : "Unhealthy";
                d.Status = pa.GetBoolean() ? "SMART OK" : "SMART-test fejlet";
            }
            if (Num(r, "temperature", "current") is { } t && t is > 0 and < 150) d.Temp = t;
            if (Num(r, "power_on_time", "hours") is { } h && h >= 0) d.PowerOnHours = (long)h;

            if (r.TryGetProperty("nvme_smart_health_information_log", out var nv) && nv.ValueKind == JsonValueKind.Object)
            {
                if (Num(nv, "percentage_used") is { } pu) d.Wear = pu;
                if (Num(nv, "media_errors") is { } me) d.MediaErrors = (long)me;
                if (Num(nv, "critical_warning") is { } cw) d.CriticalWarning = (int)cw;
                if (Num(nv, "available_spare") is { } sp) d.Spare = sp;
                if (d.Temp is null && Num(nv, "temperature") is { } nt && nt is > 0 and < 150) d.Temp = nt;
                if (d.PowerOnHours is null && Num(nv, "power_on_hours") is { } ph) d.PowerOnHours = (long)ph;
            }
            if (r.TryGetProperty("ata_smart_attributes", out var at) && at.TryGetProperty("table", out var table) && table.ValueKind == JsonValueKind.Array)
            {
                var attrs = new Dictionary<int, (double Value, double Raw)>();
                foreach (var a in table.EnumerateArray())
                    if (Num(a, "id") is { } id && Num(a, "value") is { } v)
                        attrs.TryAdd((int)id, (v, Num(a, "raw", "value") ?? 0));
                if (attrs.TryGetValue(5, out var re)) d.Reallocated = (long)re.Raw;
                long? pending = null;
                if (attrs.TryGetValue(197, out var pe)) pending = (long)pe.Raw;
                if (attrs.TryGetValue(198, out var un)) pending = (pending ?? 0) + (long)un.Raw;
                d.Pending = pending;
                if (d.Temp is null && attrs.TryGetValue(194, out var te) && (te.Raw % 256) is > 0 and < 150) d.Temp = te.Raw % 256;
                // SSD-levetid: producentens egen procent (normaliseret værdi = tilbage)
                if (d.Kind != DiskKinds.Hdd)
                    foreach (var id in new[] { 231, 233, 177, 202, 169 })
                        if (attrs.TryGetValue(id, out var life) && life.Value is >= 0 and <= 100) { d.LifeLeft = life.Value; break; }
            }
            Complete(d);
            return d;
        }
    }

    // ------------------------------------------------------------------ Windows
    /// <summary>Windows: MSFT_PhysicalDisk og (som Get-StorageReliabilityCounter) MSFT_StorageReliabilityCounter.</summary>
    public static DiskHealth FromWindows(WinDiskRaw w)
    {
        var d = new DiskHealth
        {
            Name = (w.FriendlyName ?? w.Model)?.Trim() is { Length: > 0 } n ? n : "Disk",
            Id = IdOf(w.Serial),
            Media = w.MediaType switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "?" },
            Bus = w.BusType switch { 7 => "USB", 11 => "SATA", 17 => "NVMe", 8 => "RAID", 10 => "SAS", 12 => "SD", 13 => "MMC", 15 => "Fil", 16 => "Storage Spaces", _ => "?" },
            SizeGb = w.Size > 0 ? Math.Round(w.Size / 1e9) : null,
            Health = w.HealthStatus switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unknown" },
            Status = w.OperationalStatus is { Count: > 0 } st
                ? string.Join(", ", st.Select(x => x switch { 2 => "OK", 3 => "Degraded", 5 => "Predictive Failure", 0xD00D => "In Service", _ => x.ToString(CultureInfo.InvariantCulture) }))
                : null,
            Source = "wmi",
        };
        // en disk, der forudsiger fejl, er ikke rask, selv om HealthStatus siger det
        if (w.OperationalStatus?.Contains((ushort)5) == true && d.Health == "Healthy") d.Health = "Warning";
        d.Kind = KindOf(d.Media, d.Bus);
        if (w.Reliability is { } r)
        {
            d.Wear = Dbl(r, "Wear");
            if (Dbl(r, "Temperature") is { } t && t is > 0 and < 150) d.Temp = t;
            if (Dbl(r, "TemperatureMax") is { } tm && tm is > 0 and < 150) d.TempMax = tm;
            var read = Lng(r, "ReadErrorsUncorrected"); var write = Lng(r, "WriteErrorsUncorrected");
            d.ReadErrors = read;
            if (read is not null || write is not null) d.MediaErrors = (read ?? 0) + (write ?? 0);
            d.PowerOnHours = Lng(r, "PowerOnHours");
            // Windows giver slid 0 for diske, der ikke melder det: kun en SSD/NVMe med driftstimer tæller som "0 % slidt"
            if (d.Wear == 0 && (d.Kind == DiskKinds.Hdd || d.PowerOnHours is null or 0)) d.Wear = null;
        }
        Complete(d);
        return d;
    }

    // ------------------------------------------------------------------ Mac
    /// <summary>Mac: <c>diskutil info disk0</c> (SMART-status, Solid State, Protocol, størrelse).</summary>
    public static DiskHealth? FromDiskutil(string info, string device = "disk0")
    {
        if (string.IsNullOrWhiteSpace(info)) return null;
        string F(string k) => Regex.Match(info, "^\\s*" + Regex.Escape(k) + @":\s+(.+)$", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value.Trim() : "";
        var smart = F("SMART Status");
        var protocol = F("Protocol");
        var ssd = F("Solid State") == "Yes";
        var d = new DiskHealth
        {
            Name = F("Device / Media Name") is { Length: > 0 } n ? n : device,
            Media = ssd ? "SSD" : "HDD", Bus = protocol.Length > 0 ? protocol : null,
            SizeGb = Regex.Match(F("Disk Size"), @"\((\d+) Bytes\)") is { Success: true } sz ? Math.Round(long.Parse(sz.Groups[1].Value, CultureInfo.InvariantCulture) / 1e9, 1) : null,
            Health = smart switch { "Verified" => "Healthy", "Failing" => "Unhealthy", "" or "Not Supported" => null, _ => smart },
            Status = smart.Length > 0 ? smart : null,
            Kind = !ssd ? DiskKinds.Hdd : Regex.IsMatch(protocol, "PCI|Fabric|NVMe", RegexOptions.IgnoreCase) ? DiskKinds.Nvme : DiskKinds.Ssd,
            Source = "diskutil",
        };
        return d;
    }

    /// <summary>NVMe, SSD eller HDD ud fra medie og bus (Windows, lsblk).</summary>
    public static string? KindOf(string? media, string? bus) =>
        bus?.Equals("NVMe", StringComparison.OrdinalIgnoreCase) == true ? DiskKinds.Nvme
        : media is "SSD" or "SCM" ? DiskKinds.Ssd
        : media == "HDD" ? DiskKinds.Hdd
        : null;

    /// <summary>Slid og resterende levetid er to sider af samme tal: udfyld den, der mangler.</summary>
    public static void Complete(DiskHealth d)
    {
        if (d.Wear is { } w && d.LifeLeft is null && d.Kind != DiskKinds.Hdd) d.LifeLeft = Math.Max(0, 100 - w);
        else if (d.LifeLeft is { } l && d.Wear is null) d.Wear = Math.Max(0, 100 - l);
    }

    // ------------------------------------------------------------------ vurdering og historik (serveren)
    /// <summary>NVMe critical warning: bit 2 = pålideligheden er nedsat, bit 3 = disken er skrivebeskyttet. Begge betyder, at disken svigter.</summary>
    public static bool NvmeFailing(int? cw) => cw is { } c && (c & 0x0C) != 0;

    /// <summary>Tekst for NVMe-advarselsbits (dansk).</summary>
    public static string NvmeWarningText(int cw)
    {
        var parts = new List<string>();
        if ((cw & 0x01) != 0) parts.Add("få reserveblokke tilbage");
        if ((cw & 0x02) != 0) parts.Add("temperaturen er uden for grænsen");
        if ((cw & 0x04) != 0) parts.Add("pålideligheden er nedsat");
        if ((cw & 0x08) != 0) parts.Add("disken er skrivebeskyttet");
        if ((cw & 0x10) != 0) parts.Add("backup-hukommelsen svigter");
        return parts.Count > 0 ? string.Join(", ", parts) : $"advarsel {cw}";
    }

    /// <summary>Er tallet steget inden for de sidste <paramref name="days"/> dage?</summary>
    public static bool RoseWithin(DateTime? at, DateTime nowUtc, int days = 7) => at is { } t && t > nowUtc.AddDays(-days);

    /// <summary>Samlet vurdering: failing (SMART fejlet / NVMe svigter), warning (tegn på slid eller fejl), ok, eller null (ingen data).</summary>
    public static string? Classify(DiskHealth d, double tempLimit, DateTime nowUtc)
    {
        if (IsUnhealthy(d.Health) || NvmeFailing(d.CriticalWarning)) return DiskStates.Failing;
        var known = d.Health is not null || d.Wear is not null || d.Temp is not null || d.Reallocated is not null || d.CriticalWarning is not null;
        if (!known) return null;
        if (d.Health?.Equals("Warning", StringComparison.OrdinalIgnoreCase) == true || d.CriticalWarning is > 0 || d.Pending > 0
            || RoseWithin(d.ReallocatedRoseAt, nowUtc) || RoseWithin(d.MediaErrorsRoseAt, nowUtc) || d.Wear >= 80 || d.Temp >= tempLimit)
            return DiskStates.Warning;
        return DiskStates.Ok;
    }

    public static bool IsUnhealthy(string? health) => health is not null
        && (health.Equals("Unhealthy", StringComparison.OrdinalIgnoreCase) || health.Equals("Failing", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Serveren: følg diskene fra gang til gang. En sovende disk (standby) beholder de sidst kendte SMART-tal; reallokerede sektorer og
    /// mediefejl får tidspunktet for, hvornår de sidst steg (bruges af advarslen "stigende"). Til sidst sættes <see cref="DiskHealth.State"/>.
    /// </summary>
    public static void Track(List<DiskHealth> now, IReadOnlyList<DiskHealth>? before, DateTime nowUtc, double tempLimit = 70)
    {
        foreach (var d in now)
        {
            // efter navn kun, når navnet er entydigt: ellers ville en sovende disk i et array få tal (og advarsler) fra en anden disk af samme model
            var old = before?.FirstOrDefault(b => d.Id is not null && b.Id == d.Id)
                ?? (now.Count(x => x.Name == d.Name) == 1 && before?.Count(b => b.Name == d.Name) == 1
                    ? before.FirstOrDefault(b => (d.Id is null || b.Id is null) && b.Name == d.Name) : null);
            if (old is not null)
            {
                if (d.Standby)
                {
                    d.Health ??= old.Health; d.Status ??= old.Status; d.Wear ??= old.Wear; d.LifeLeft ??= old.LifeLeft; d.Reallocated ??= old.Reallocated;
                    d.Pending ??= old.Pending; d.MediaErrors ??= old.MediaErrors; d.CriticalWarning ??= old.CriticalWarning; d.Spare ??= old.Spare;
                    d.PowerOnHours ??= old.PowerOnHours; d.ReadErrors ??= old.ReadErrors; d.Kind ??= old.Kind;
                }
                d.ReallocatedFirst = old.ReallocatedFirst ?? old.Reallocated ?? d.Reallocated;
                d.ReallocatedRoseAt = d.Reallocated > old.Reallocated ? nowUtc : old.ReallocatedRoseAt;
                d.MediaErrorsRoseAt = d.MediaErrors > old.MediaErrors ? nowUtc : old.MediaErrorsRoseAt;
            }
            else d.ReallocatedFirst = d.Reallocated;
            d.State = Classify(d, tempLimit, nowUtc);
        }
    }

    // ------------------------------------------------------------------ hjælpere
    private static JsonElement? Get(JsonElement e, params string[] path)
    {
        foreach (var p in path)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out var next)) return null;
            e = next;
        }
        return e;
    }
    private static string? Str(JsonElement e, params string[] path) => Get(e, path) is { ValueKind: JsonValueKind.String } s ? s.GetString()?.Trim() : null;
    private static double? Num(JsonElement e, params string[] path) => Get(e, path) is { ValueKind: JsonValueKind.Number } n && n.TryGetDouble(out var v) ? v : null;
    private static double? Dbl(IReadOnlyDictionary<string, object?> r, string k) =>
        r.TryGetValue(k, out var v) && v is not null && double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    private static long? Lng(IReadOnlyDictionary<string, object?> r, string k) => Dbl(r, k) is { } d ? (long)d : null;
}
