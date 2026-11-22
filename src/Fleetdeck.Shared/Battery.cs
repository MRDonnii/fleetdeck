using System.Globalization;
using System.Text.RegularExpressions;

namespace Fleetdeck.Shared;

/// <summary>Et batteri i en bærbar: kapacitet nu i forhold til som ny, antal opladninger og status.</summary>
public sealed class BatteryInfo
{
    public string Name { get; set; } = "Batteri";
    public string? Manufacturer { get; set; }
    public string? Chemistry { get; set; }
    /// <summary>Kapacitet som ny og ved fuld opladning nu, i <see cref="Unit"/> (mWh eller mAh).</summary>
    public double? Design { get; set; }
    public double? Full { get; set; }
    public string? Unit { get; set; }
    /// <summary>Fuld opladning i forhold til som ny, i procent (Mac: Apples egen "Maksimal kapacitet", når den findes).</summary>
    public double? HealthPct { get; set; }
    public int? Cycles { get; set; }
    /// <summary>charging, discharging, full, ac eller null.</summary>
    public string? Status { get; set; }
    /// <summary>Opladet lige nu i procent.</summary>
    public double? ChargePct { get; set; }
    /// <summary>Mac: Apples vurdering ("Normal", "Service Recommended" …).</summary>
    public string? Condition { get; set; }
    public string? Source { get; set; }
}

/// <summary>Fortolkning af batteridata pr. platform (testbar uden maskinen).</summary>
public static class BatteryParse
{
    public static double? Pct(double? full, double? design) =>
        full is > 0 && design is > 0 ? Math.Round(Math.Clamp(full.Value / design.Value * 100, 0, 150), 0) : null;

    /// <summary>Linux: filerne i /sys/class/power_supply/BATx (navn → indhold). Null = ikke et batteri i maskinen (fx en trådløs mus).</summary>
    public static BatteryInfo? FromSysfs(string name, IReadOnlyDictionary<string, string> f)
    {
        string? V(string k) => f.TryGetValue(k, out var v) && v.Trim().Length > 0 ? v.Trim() : null;
        if (!string.Equals(V("type"), "Battery", StringComparison.OrdinalIgnoreCase)) return null;
        if (string.Equals(V("scope"), "Device", StringComparison.OrdinalIgnoreCase) || V("present") == "0") return null;
        double? N(string k) => double.TryParse(V(k), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        var b = new BatteryInfo { Name = name, Manufacturer = V("manufacturer"), Chemistry = V("technology"), Source = "sysfs" };
        if (V("model_name") is { } model) b.Name = model;
        // µWh (energy_*) eller µAh (charge_*)
        if (N("energy_full_design") is { } ed) { b.Design = Math.Round(ed / 1000); b.Full = N("energy_full") is { } ef ? Math.Round(ef / 1000) : null; b.Unit = "mWh"; }
        else if (N("charge_full_design") is { } cd) { b.Design = Math.Round(cd / 1000); b.Full = N("charge_full") is { } cf ? Math.Round(cf / 1000) : null; b.Unit = "mAh"; }
        b.HealthPct = Pct(b.Full, b.Design);
        b.Cycles = N("cycle_count") is { } c and > 0 ? (int)c : null;   // 0 = driveren kender det ikke
        b.ChargePct = N("capacity");
        b.Status = V("status")?.ToLowerInvariant() switch
        {
            "charging" => "charging", "discharging" => "discharging", "full" => "full", "not charging" => "ac", _ => null,
        };
        return b;
    }

    /// <summary>
    /// Mac: <c>ioreg -rn AppleSmartBattery</c> og (valgfrit) <c>system_profiler SPPowerDataType</c>. Null = intet batteri (stationær Mac).
    /// Apple Silicon giver MaxCapacity i procent og den rigtige kapacitet i AppleRawMaxCapacity; Intel giver MaxCapacity i mAh.
    /// </summary>
    public static BatteryInfo? FromMac(string ioreg, string? profiler = null)
    {
        if (string.IsNullOrWhiteSpace(ioreg) || !ioreg.Contains("DesignCapacity", StringComparison.Ordinal)) return null;
        double? I(string k) => Regex.Match(ioreg, "\"" + Regex.Escape(k) + "\"\\s*=\\s*(\\d+)") is { Success: true } m
            ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
        bool? B(string k) => Regex.Match(ioreg, "\"" + Regex.Escape(k) + "\"\\s*=\\s*(Yes|No)") is { Success: true } m ? m.Groups[1].Value == "Yes" : null;
        var design = I("DesignCapacity");
        var max = I("MaxCapacity");
        var full = I("AppleRawMaxCapacity") ?? (max > 100 ? max : null);
        var cur = I("CurrentCapacity");
        var b = new BatteryInfo
        {
            Name = Regex.Match(ioreg, "\"DeviceName\"\\s*=\\s*\"([^\"]+)\"") is { Success: true } dn ? dn.Groups[1].Value : "Batteri",
            Design = design, Full = full, Unit = "mAh", Cycles = I("CycleCount") is { } c ? (int)c : null, Source = "ioreg",
            ChargePct = cur is null ? null : max is > 0 and <= 100 && cur <= 100 ? cur : max is > 100 ? Math.Round(cur.Value / max.Value * 100) : null,
        };
        b.HealthPct = Pct(full, design);
        b.Status = B("FullyCharged") == true ? "full" : B("IsCharging") == true ? "charging" : B("ExternalConnected") == true ? "ac" : B("ExternalConnected") == false ? "discharging" : null;
        if (!string.IsNullOrWhiteSpace(profiler))
        {
            string P(string k) => Regex.Match(profiler, "^\\s*" + Regex.Escape(k) + @":\s*(.+)$", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value.Trim() : "";
            if (P("Condition") is { Length: > 0 } cond) b.Condition = cond;
            if (Regex.Match(P("Maximum Capacity"), @"(\d+)\s*%") is { Success: true } mc) b.HealthPct = double.Parse(mc.Groups[1].Value, CultureInfo.InvariantCulture);
            if (b.Cycles is null && int.TryParse(P("Cycle Count"), out var cc)) b.Cycles = cc;
        }
        return b;
    }

    /// <summary>
    /// Windows: root\wmi BatteryStaticData.DesignedCapacity, BatteryFullChargedCapacity.FullChargedCapacity og BatteryCycleCount.CycleCount (mWh),
    /// plus Win32_Battery (navn, kemi, status og opladning). Cyklus 0 betyder på Windows næsten altid "ukendt".
    /// </summary>
    public static BatteryInfo FromWindows(string? name, double? designed, double? fullCharged, double? cycles, int? status, double? charge, int? chemistry)
    {
        var b = new BatteryInfo
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Batteri" : name.Trim(),
            Design = designed is > 0 ? designed : null, Full = fullCharged is > 0 ? fullCharged : null, Unit = "mWh",
            Cycles = cycles is > 0 and < 100_000 ? (int)cycles : null,
            ChargePct = charge is >= 0 and <= 100 ? charge : null,
            Source = "wmi",
            Status = status switch { 1 or 4 or 5 => "discharging", 2 => "ac", 3 => "full", 6 or 7 or 8 or 9 => "charging", _ => null },
            Chemistry = chemistry switch { 3 => "Lead Acid", 4 => "NiCd", 5 => "NiMH", 6 => "Li-ion", 7 => "Zinc air", 8 => "Li-polymer", _ => null },
        };
        b.HealthPct = Pct(b.Full, b.Design);
        return b;
    }

    /// <summary>Batteriet bør skiftes: under <paramref name="limitPct"/> af den oprindelige kapacitet, eller Apple anbefaler service.</summary>
    public static string? Problem(BatteryInfo b, double limitPct)
    {
        if (b.Condition is { } c && Regex.IsMatch(c, "service|replace|poor|check", RegexOptions.IgnoreCase))
            return $"Mac'en anbefaler service af batteriet ({c})";
        if (b.HealthPct is { } p && p < limitPct)
            return $"Batteriet kan kun rumme {p:0} % af det, det kunne som nyt" + (b.Cycles is { } cy ? $" ({cy} opladninger)" : "");
        return null;
    }
}
