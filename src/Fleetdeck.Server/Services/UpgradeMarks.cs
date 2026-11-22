using Fleetdeck.Server.Data;
using Fleetdeck.Shared;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Server.Services;

/// <summary>
/// Skjul i Fleetdeck og "kan ikke installeres" (UpgradeMark): det, serveren selv husker om en opdatering, så det overlever et nyt
/// inventar. Windows Update skjules stadig på pc'en (wu_hide); alt andet (firmware, macOS, Linux-pakker …) skjules her.
/// </summary>
public static class UpgradeMarks
{
    public const int Max = 50;

    /// <summary>Gyldige id'er til at skjule i Fleetdeck: alt andet end Windows Update, uden mærkelige tegn.</summary>
    public static List<string> Valid(IEnumerable<string>? ids) =>
        (ids ?? []).Select(x => (x ?? "").Trim()).Where(x => x.Length is > 0 and <= 400 && !x.StartsWith("wu:", StringComparison.Ordinal) && !x.Any(char.IsControl))
            .Distinct(StringComparer.Ordinal).Take(Max).ToList();

    /// <summary>Gælder mærket denne version? (null/tom = alle versioner.)</summary>
    public static bool Matches(UpgradeMark k, string? available) => string.IsNullOrEmpty(k.Available) || k.Available == (available ?? "");

    /// <summary>
    /// Nyt inventar: sæt Hidden/Blocked fra mærkerne på de nye linjer. Mærker for en version, der er afløst af en ny, returneres
    /// (de skal slettes); mærker for opdateringer, der ikke er på listen lige nu, bliver stående (fx hvis fwupd ikke svarede).
    /// </summary>
    public static List<UpgradeMark> Apply(IEnumerable<UpgradeItem> fresh, IEnumerable<UpgradeMark> marks)
    {
        var byId = marks.ToDictionary(k => k.WingetId, StringComparer.Ordinal);
        var stale = new List<UpgradeMark>();
        foreach (var u in fresh)
        {
            if (!byId.TryGetValue(u.WingetId, out var k)) continue;
            if (!Matches(k, u.Available)) { stale.Add(k); byId.Remove(u.WingetId); continue; }
            u.Hidden |= k.Hidden;
            u.Blocked ??= k.Blocked;
        }
        return stale;
    }

    /// <summary>Kør <see cref="Apply"/> mod databasen (før de nye linjer gemmes).</summary>
    public static async Task ApplyInventory(AppDb db, int machineId, List<UpgradeItem> fresh)
    {
        var marks = await db.UpgradeMarks.Where(k => k.MachineId == machineId).ToListAsync();
        if (marks.Count == 0) return;
        db.UpgradeMarks.RemoveRange(Apply(fresh, marks));
    }

    /// <summary>Skjul (eller vis igen) i Fleetdeck. Kun opdateringer, maskinen har meldt (eller som allerede er mærket). Returnerer navnene.</summary>
    public static async Task<List<string>> SetHidden(AppDb db, int machineId, List<string> ids, bool hide, string by, DateTime now)
    {
        var items = await db.Upgrades.IgnoreQueryFilters().Where(u => u.MachineId == machineId && ids.Contains(u.WingetId)).ToListAsync();
        var marks = await db.UpgradeMarks.Where(k => k.MachineId == machineId && ids.Contains(k.WingetId)).ToListAsync();
        var names = new List<string>();
        foreach (var id in ids)
        {
            var u = items.FirstOrDefault(x => x.WingetId == id);
            var k = marks.FirstOrDefault(x => x.WingetId == id);
            if (u is null && k is null) continue;
            names.Add(u?.Name ?? k!.Name);
            if (hide)
            {
                if (k is null) db.UpgradeMarks.Add(k = new UpgradeMark { MachineId = machineId, WingetId = id, Name = u!.Name, Available = u.Available });
                else if (u is not null && !Matches(k, u.Available)) { k.Available = u.Available; k.Blocked = null; }
                k.Hidden = true; k.Created = now; k.By = by;
            }
            else if (k is not null)
            {
                k.Hidden = false;
                if (k.Blocked is null) db.UpgradeMarks.Remove(k);
            }
            if (u is not null) u.Hidden = hide;
        }
        await db.SaveChangesAsync();
        return names;
    }

    /// <summary>Agenten meldte, at opdateringen ikke kan installeres: husk det for den version, maskinen har nu.</summary>
    public static async Task<bool> Block(AppDb db, int machineId, string wingetId, string text, DateTime now)
    {
        var u = await db.Upgrades.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.MachineId == machineId && x.WingetId == wingetId);
        if (u is null) return false;
        var k = await db.UpgradeMarks.FirstOrDefaultAsync(x => x.MachineId == machineId && x.WingetId == wingetId);
        if (k is null) db.UpgradeMarks.Add(k = new UpgradeMark { MachineId = machineId, WingetId = wingetId, Name = u.Name, Available = u.Available });
        else if (!Matches(k, u.Available)) { k.Available = u.Available; k.Hidden = false; }
        k.Blocked = text.Length > 300 ? text[..300] : text; k.Created = now; k.By = "agent";
        u.Blocked = k.Blocked;
        return true;
    }

    /// <summary>Lykkedes den alligevel (fx efter en BIOS-opdatering): glem "kan ikke installeres".</summary>
    public static async Task Unblock(AppDb db, int machineId, string wingetId)
    {
        if (await db.UpgradeMarks.FirstOrDefaultAsync(x => x.MachineId == machineId && x.WingetId == wingetId) is { Blocked: not null } k)
        {
            k.Blocked = null;
            if (!k.Hidden) db.UpgradeMarks.Remove(k);
        }
        await db.Upgrades.IgnoreQueryFilters().Where(x => x.MachineId == machineId && x.WingetId == wingetId && x.Blocked != null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Blocked, (string?)null));
    }

    /// <summary>Det, "Opdatér alt" skal lade være (skjult i Fleetdeck eller kan ikke installeres) – lægges til jobbets Exclude.</summary>
    public static async Task<List<string>> Exclude(AppDb db, int machineId) =>
        await db.UpgradeMarks.AsNoTracking().Where(k => k.MachineId == machineId && (k.Hidden || k.Blocked != null)).Select(k => k.WingetId).ToListAsync();

    /// <summary>Et fejlet opdateringsjob: en særlig grund fra agenten, eller (ældre agenter) en fwupd-fejl, serveren selv genkender.</summary>
    public static string? CannotInstallReason(string? wingetId, string? reason, string? output)
    {
        if (JobReasons.IsCannotInstall(reason)) return reason;
        return wingetId?.StartsWith("fwupd:", StringComparison.Ordinal) == true ? FwupdErrors.Classify(output) : null;
    }
}
