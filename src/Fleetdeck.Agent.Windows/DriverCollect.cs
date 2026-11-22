using System.Management;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Windows;

/// <summary>Installerede drivere til ændringsloggen (Win32_PnPSignedDriver, uden Microsofts egne). Kun med inventaret; en fejl giver null.</summary>
public static class DriverCollect
{
    public static List<DriverEntry>? Collect()
    {
        var all = Collectors.Wmi(@"root\cimv2", "SELECT DeviceName, DriverVersion, DriverProviderName, DeviceClass, DriverDate FROM Win32_PnPSignedDriver");
        if (all.Count == 0) return null;   // WMI svarede ikke: hellere ingen liste end en tom (så ser alt ud til at være fjernet)
        var list = new List<DriverEntry>();
        foreach (var o in all)
            using (o)
            {
                string? date = null;
                if (Collectors.Prop<string>(o, "DriverDate") is { Length: >= 8 } d)
                    try { date = ManagementDateTimeConverter.ToDateTime(d).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture); } catch (ArgumentException) { }
                list.Add(new DriverEntry
                {
                    Name = Collectors.Prop<string>(o, "DeviceName") ?? "", Version = Collectors.Prop<string>(o, "DriverVersion"),
                    Provider = Collectors.Prop<string>(o, "DriverProviderName"), Class = Collectors.Prop<string>(o, "DeviceClass"), Date = date,
                });
            }
        return DriverList.Clean(list);
    }
}
