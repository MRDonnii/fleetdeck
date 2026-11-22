using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Shared;

namespace Fleetdeck.Server.Services.Reports;

/// <summary>Små historik-spor, rapporterne bruger: helbredsscore pr. dag og hvor længe opdateringer har ventet.</summary>
public static class ReportTracking
{
    public const int HealthDays = 400;

    private static readonly Dictionary<string, int> Rank = new() { ["unknown"] = 0, ["ok"] = 1, ["warning"] = 2, ["critical"] = 3 };

    /// <summary>Dagens række (lokal dato): laveste score, værste tilstand og gennemsnit. Gemmes af kalderen.</summary>
    public static async Task RecordHealth(AppDb db, int machineId, int score, string state, DateTime nowUtc)
    {
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, IpadEngine.Tz));
        var row = db.HealthDaily.Local.FirstOrDefault(h => h.MachineId == machineId && h.Day == day)
            ?? await db.HealthDaily.FirstOrDefaultAsync(h => h.MachineId == machineId && h.Day == day);
        if (row is null) { db.HealthDaily.Add(new HealthDaily { MachineId = machineId, Day = day, Score = score, State = state, Samples = 1, ScoreSum = score }); return; }
        row.Score = Math.Min(row.Score, score);
        if (Rank.GetValueOrDefault(state) > Rank.GetValueOrDefault(row.State)) row.State = state;
        row.Samples++; row.ScoreSum += score;
    }

    /// <summary>System-opdateringer: husk hvornår de begyndte at vente, glem det når der ikke venter nogen.</summary>
    public static void TrackOsUpdates(Machine m, HealthReport h, DateTime nowUtc)
    {
        if (h.Updates is null) return;   // ukendt (fx ikke læst denne gang): rør ikke
        if (h.Updates.Pending > 0) m.OsUpdatesSince ??= nowUtc;
        else m.OsUpdatesSince = null;
    }

    /// <summary>Programopdateringer: behold "først set" for samme program og version på tværs af inventarer.</summary>
    public static void CarryFirstSeen(IEnumerable<UpgradeItem> fresh, IEnumerable<UpgradeItem> old, DateTime nowUtc)
    {
        var seen = old.GroupBy(o => (o.WingetId, o.Available ?? "")).ToDictionary(g => g.Key, g => g.Min(x => x.FirstSeen ?? nowUtc));
        foreach (var u in fresh) u.FirstSeen = seen.TryGetValue((u.WingetId, u.Available ?? ""), out var f) ? f : nowUtc;
    }
}
