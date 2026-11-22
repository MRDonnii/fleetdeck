using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Unix;

/// <summary>
/// Batterihelbred: Linux læser /sys/class/power_supply (kun batterier i selve maskinen, ikke trådløse mus), Mac læser
/// ioreg (AppleSmartBattery) og – højst én gang i timen – system_profiler for Apples egen vurdering. Ingen batterier = tom liste.
/// </summary>
public static class BatteryCollect
{
    private const string Dir = "/sys/class/power_supply";
    private static readonly string[] Files =
        ["type", "scope", "present", "status", "capacity", "cycle_count", "energy_full_design", "energy_full", "charge_full_design", "charge_full", "manufacturer", "model_name", "technology"];
    private static (DateTime At, string Text)? profiler;

    public static List<BatteryInfo> Status()
    {
        if (Mac.Is)
        {
            var ioreg = Shell.Out("/usr/sbin/ioreg -rn AppleSmartBattery 2>/dev/null", 10);
            if (!ioreg.Contains("DesignCapacity", StringComparison.Ordinal)) return [];   // stationær Mac
            if (profiler is not { } p || p.At < DateTime.UtcNow.AddHours(-1))
                profiler = (DateTime.UtcNow, Shell.Out("/usr/sbin/system_profiler SPPowerDataType 2>/dev/null", 20));
            return BatteryParse.FromMac(ioreg, profiler?.Text) is { } b ? [b] : [];
        }
        if (!Directory.Exists(Dir)) return [];
        var list = new List<BatteryInfo>();
        foreach (var dir in Directory.GetDirectories(Dir).OrderBy(d => d, StringComparer.Ordinal))
        {
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var f in Files)
                try { var path = Path.Combine(dir, f); if (File.Exists(path)) files[f] = File.ReadAllText(path); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            if (BatteryParse.FromSysfs(Path.GetFileName(dir), files) is { } b) list.Add(b);
        }
        return list.Take(4).ToList();
    }
}
