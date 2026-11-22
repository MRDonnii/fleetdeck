using Fleetdeck.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Server.Services;

/// <summary>
/// Programmer, der aldrig må opdateres: på alle maskiner (indstillingen "upgrade_exclude"), på én maskine (Machine.UpgradeExclude)
/// eller i en profil (Profile.UpgradeExclude). "Opdatér alt" springer dem over, og de tæller ikke med som ventende.
/// </summary>
public static class UpgradeLocks
{
    public const string SettingKey = "upgrade_exclude";

    public static async Task<List<string>> Global(AppDb db) =>
        Json.Des<List<string>>(await db.Settings.AsNoTracking().Where(s => s.Key == SettingKey).Select(s => s.Value).FirstOrDefaultAsync());

    public static async Task SetGlobal(AppDb db, List<string> list)
    {
        var row = await db.Settings.FindAsync(SettingKey);
        if (row is null) db.Settings.Add(row = new Setting { Key = SettingKey });
        row.Value = Json.Ser(list.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>Alle låse for én maskine: id → hvor låsen kommer fra ("alle" | "maskine" | "profil").</summary>
    public static async Task<Dictionary<string, string>> For(AppDb db, int machineId, List<string>? global = null)
    {
        var m = await db.Machines.AsNoTracking().Where(x => x.Id == machineId).Select(x => new { x.UpgradeExclude, x.ProfileId }).FirstOrDefaultAsync();
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (m is null) return d;
        if (m.ProfileId is int pid)
            foreach (var x in await db.Profiles.AsNoTracking().Where(p => p.Id == pid).Select(p => p.UpgradeExclude).FirstOrDefaultAsync() ?? []) d[x] = "profil";
        foreach (var x in m.UpgradeExclude) d[x] = "maskine";
        foreach (var x in global ?? await Global(db)) d[x] = "alle";
        return d;
    }

    /// <summary>Ventende opdateringer, der ikke er låst.</summary>
    public static async Task<int> OpenCount(AppDb db, int machineId)
    {
        var locks = await For(db, machineId);
        return (await db.Upgrades.AsNoTracking().Where(u => u.MachineId == machineId).Select(u => u.WingetId).ToListAsync()).Count(id => !locks.ContainsKey(id));
    }

    /// <summary>
    /// Det, opdateringsmotoren faktisk vil installere (til "skal den slukkede pc tændes?"): ikke låste, ikke drivere, Fleetdeck
    /// fraråder (gamle, nedgraderinger), ikke BIOS – og drivere/firmware kun, når profilen har dem med.
    /// </summary>
    public static async Task<int> EngineCount(AppDb db, int machineId, bool drivers = true, bool firmware = false)
    {
        var locks = await For(db, machineId);
        var rows = await db.Upgrades.AsNoTracking().Where(u => u.MachineId == machineId).Select(u => new { u.WingetId, u.Name, u.Driver, u.Advice }).ToListAsync();
        return rows.Count(u =>
        {
            if (locks.ContainsKey(u.WingetId)) return false;
            var fw = u.WingetId.StartsWith("fwupd:", StringComparison.Ordinal) || u.Driver && Fleetdeck.Shared.UpdatePlanner.LooksLikeFirmware(u.Name);
            // Windows Update-firmware kan være BIOS uden at hedde det ("Dell Inc. - Firmware - 1.21.0") – agenten afgør det via ESRT,
            // så den tæller ikke som grund til at tænde pc'en
            if (fw) return firmware && !u.WingetId.StartsWith("wu:", StringComparison.Ordinal) && !Fleetdeck.Shared.UpdatePlanner.LooksLikeBios(u.Name);
            if (u.Driver) return drivers && string.IsNullOrEmpty(u.Advice);
            return true;
        });
    }
}
