using System.Management;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Windows;

/// <summary>
/// Batterihelbred på bærbare fra WMI: root\wmi (BatteryStaticData, BatteryFullChargedCapacity, BatteryCycleCount – kræver SYSTEM)
/// og Win32_Battery (navn, status, opladning). Ingen batterier = tom liste. En fejl vælter aldrig agenten.
/// </summary>
public static class BatteryCollect
{
    public static List<BatteryInfo>? Collect()
    {
        if (!AgentOptions.IsSystem) return null;
        var win = Collectors.Wmi(@"root\cimv2", "SELECT Name, BatteryStatus, EstimatedChargeRemaining, Chemistry FROM Win32_Battery");
        if (win.Count == 0) return [];   // stationær pc
        var design = ByInstance("SELECT InstanceName, DesignedCapacity FROM BatteryStaticData", "DesignedCapacity");
        var full = ByInstance("SELECT InstanceName, FullChargedCapacity FROM BatteryFullChargedCapacity", "FullChargedCapacity");
        var cycles = ByInstance("SELECT InstanceName, CycleCount FROM BatteryCycleCount", "CycleCount");
        var keys = design.Keys.Concat(full.Keys).Distinct().OrderBy(k => k, StringComparer.Ordinal).ToList();
        var list = new List<BatteryInfo>();
        for (var i = 0; i < Math.Max(keys.Count, win.Count); i++)
        {
            var key = i < keys.Count ? keys[i] : null;
            var w = i < win.Count ? win[i] : null;
            list.Add(BatteryParse.FromWindows(
                w is null ? null : Collectors.Prop<string>(w, "Name"),
                key is not null && design.TryGetValue(key, out var d) ? d : null,
                key is not null && full.TryGetValue(key, out var f) ? f : null,
                key is not null && cycles.TryGetValue(key, out var c) ? c : null,
                w is null ? null : Collectors.Prop<int?>(w, "BatteryStatus"),
                w is null ? null : Collectors.Prop<double?>(w, "EstimatedChargeRemaining"),
                w is null ? null : Collectors.Prop<int?>(w, "Chemistry")));
        }
        foreach (var o in win) o.Dispose();
        return list.Take(4).ToList();
    }

    private static Dictionary<string, double> ByInstance(string query, string prop)
    {
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var o in Collectors.Wmi(@"root\wmi", query))
            using (o)
                if (Collectors.Prop<string>(o, "InstanceName") is { } k && Collectors.Prop<double?>(o, prop) is { } v)
                    map.TryAdd(k, v);
        return map;
    }
}
