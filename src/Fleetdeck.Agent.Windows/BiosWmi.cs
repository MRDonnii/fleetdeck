using System.Management;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Windows;

/// <summary>
/// Lenovos BIOS-WMI i root\wmi (Lenovo_BiosSetting, Lenovo_GetBiosSelections, Lenovo_SetBiosSetting, Lenovo_SaveBiosSettings,
/// Lenovo_WmiOpcodeInterface, Lenovo_DiscardBiosSettings, Lenovo_BiosPasswordSettings). Logikken ligger i <see cref="LenovoWmiSettings"/>;
/// her er kun selve WMI-kaldene. Parametrene kan indeholde BIOS-adgangskoden, så de logges aldrig.
/// </summary>
public sealed class LenovoBiosWmi : ILenovoWmi
{
    private readonly ManagementScope scope = new(@"\\.\root\wmi");

    private LenovoBiosWmi() { }

    /// <summary>Null, hvis maskinen ikke har Lenovos BIOS-WMI (andre mærker, eller ældre Lenovo uden klasserne).</summary>
    public static LenovoBiosWmi? TryOpen()
    {
        var w = new LenovoBiosWmi();
        try
        {
            w.scope.Connect();
            using var s = new ManagementObjectSearcher(w.scope, new ObjectQuery("SELECT CurrentSetting FROM Lenovo_BiosSetting"));
            using var all = s.Get();
            return all.Count > 0 ? w : null;
        }
        catch (Exception e) when (e is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { return null; }
    }

    public IReadOnlyList<string> CurrentSettings()
    {
        using var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT CurrentSetting FROM Lenovo_BiosSetting"));
        using var all = s.Get();
        var list = new List<string>();
        foreach (ManagementObject o in all)
            using (o)
                if (o["CurrentSetting"] as string is { Length: > 0 } v) list.Add(v);
        return list;
    }

    public string? Selections(string name)
    {
        try { return Call("Lenovo_GetBiosSelections", "GetBiosSelections", name); }
        catch (Exception e) when (e is ManagementException or InvalidOperationException) { return null; }
    }

    public int? PasswordState()
    {
        try
        {
            using var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT PasswordState FROM Lenovo_BiosPasswordSettings"));
            using var all = s.Get();
            foreach (ManagementObject o in all)
                using (o)
                    return o["PasswordState"] is { } v ? Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture) : null;
        }
        catch (ManagementException) { }
        return null;
    }

    public bool HasOpcode
    {
        get
        {
            try { using var c = new ManagementClass(scope, new ManagementPath("Lenovo_WmiOpcodeInterface"), null); c.Get(); return true; }
            catch (ManagementException) { return false; }
        }
    }

    public string Opcode(string parameter) => Call("Lenovo_WmiOpcodeInterface", "WmiOpcodeInterface", parameter) ?? "";
    public string Set(string parameter) => Call("Lenovo_SetBiosSetting", "SetBiosSetting", parameter) ?? "";
    public string Save(string? parameter) => Call("Lenovo_SaveBiosSettings", "SaveBiosSettings", parameter) ?? "";
    public string? Discard(string? parameter)
    {
        try { return Call("Lenovo_DiscardBiosSettings", "DiscardBiosSettings", parameter); }
        catch (ManagementException) { return null; }
    }

    /// <summary>Kald metoden på klassens (eneste) instans. Ind- og udparametrenes navne varierer, så den første af hver bruges.</summary>
    private string? Call(string cls, string method, string? parameter)
    {
        using var s = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT * FROM {cls}"));
        using var all = s.Get();
        foreach (ManagementObject o in all)
            using (o)
            {
                using var inp = o.GetMethodParameters(method);
                if (parameter is not null)
                    foreach (PropertyData p in inp.Properties) { inp[p.Name] = parameter; break; }
                using var outp = o.InvokeMethod(method, inp, null);
                if (outp is null) return null;
                foreach (PropertyData p in outp.Properties)
                    if (p.Name != "ReturnValue" && p.Value is string v) return v;
                return null;
            }
        throw new InvalidOperationException($"{cls} findes ikke på denne maskine.");
    }
}
