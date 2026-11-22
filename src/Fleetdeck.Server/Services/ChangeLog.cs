using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Shared;

namespace Fleetdeck.Server.Services;

public static class ChangeKinds
{
    public const string Program = "program", Driver = "driver";
}

public static class ChangeActions
{
    public const string Installed = "installed", Removed = "removed", Updated = "updated";
}

public sealed record ChangeItem(string Name, string? Version, string? Publisher = null);
public sealed record ChangeEntry(string Action, string Name, string? From, string? To, string? Publisher);

/// <summary>
/// Forskellen mellem to lister (programmer eller drivere): hvad er installeret, fjernet eller opdateret. Navnet er nøglen (uden forskel på
/// store og små bogstaver); samme program i flere versioner (fx 32- og 64-bit) sammenlignes som en samling af versioner.
/// </summary>
public static class ChangeDiff
{
    public const int MaxPerRun = 300;

    /// <summary>
    /// Null = ingen sammenligning: den første liste (ingen tidligere at sammenligne med), en tom ny liste, eller så mange fjernede, at
    /// indsamlingen nok fejlede (over halvdelen af mindst 20). Ellers ændringerne (installeret, opdateret, fjernet), højst <see cref="MaxPerRun"/>.
    /// </summary>
    public static List<ChangeEntry>? Diff(IReadOnlyCollection<ChangeItem> before, IReadOnlyCollection<ChangeItem> after)
    {
        if (before.Count == 0 || after.Count == 0) return null;
        var b = Group(before); var a = Group(after);
        var removed = b.Keys.Where(k => !a.ContainsKey(k)).ToList();
        if (b.Count >= 20 && removed.Count * 2 > b.Count) return null;
        var list = new List<ChangeEntry>();
        foreach (var (k, now) in a.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            if (!b.TryGetValue(k, out var was)) { list.Add(new(ChangeActions.Installed, now.Name, null, Join(now.Versions), now.Publisher)); continue; }
            // kun en rigtig versionsændring: mangler versionen på den ene side, er det indsamlingen, ikke programmet, der har ændret sig
            if (was.Versions.Count == 0 || now.Versions.Count == 0 || was.Versions.SetEquals(now.Versions)) continue;
            list.Add(new(ChangeActions.Updated, now.Name, Join(was.Versions.Except(now.Versions)) ?? Join(was.Versions),
                Join(now.Versions.Except(was.Versions)) ?? Join(now.Versions), now.Publisher ?? was.Publisher));
        }
        foreach (var k in removed.OrderBy(k => k, StringComparer.Ordinal))
            list.Add(new(ChangeActions.Removed, b[k].Name, Join(b[k].Versions), null, b[k].Publisher));
        return list.Take(MaxPerRun).ToList();
    }

    private sealed record Bucket(string Name, HashSet<string> Versions, string? Publisher);

    private static Dictionary<string, Bucket> Group(IEnumerable<ChangeItem> items)
    {
        var d = new Dictionary<string, Bucket>(StringComparer.Ordinal);
        foreach (var i in items)
        {
            var name = i.Name.Trim();
            if (name.Length == 0) continue;
            var key = name.ToLowerInvariant();
            if (!d.TryGetValue(key, out var g)) d[key] = g = new Bucket(name, new HashSet<string>(StringComparer.Ordinal), i.Publisher);
            if (i.Version?.Trim() is { Length: > 0 } v) g.Versions.Add(v);
        }
        return d;
    }

    private static string? Join(IEnumerable<string> versions)
    {
        var s = string.Join(", ", versions.OrderBy(v => v, StringComparer.Ordinal));
        return s.Length == 0 ? null : s.Length <= 100 ? s : s[..99] + "…";
    }
}

/// <summary>Ændringsloggen pr. maskine: gemmes ved hvert inventar, 180 dage og højst 3000 rækker pr. maskine.</summary>
public static class ChangeLogStore
{
    public const int RetentionDays = 180, MaxPerMachine = 3000;

    /// <summary>Tilføjer ændringerne (uden at gemme). Giver antallet.</summary>
    public static int Add(AppDb db, int machineId, string kind, IReadOnlyCollection<ChangeItem> before, IReadOnlyCollection<ChangeItem> after, DateTime nowUtc)
    {
        if (ChangeDiff.Diff(before, after) is not { Count: > 0 } diff) return 0;
        db.ChangeLog.AddRange(diff.Select(c => new ChangeLogRow
        {
            MachineId = machineId, Time = nowUtc, Kind = kind, Action = c.Action, Name = Cut(c.Name, 200)!,
            From = Cut(c.From, 100), To = Cut(c.To, 100), Publisher = Cut(c.Publisher, 100),
        }));
        return diff.Count;
    }

    /// <summary>
    /// Driverne fra inventaret: sammenlign med de sidst kendte og gem den nye liste. Null (ældre agent, Linux/Mac eller WMI svarede ikke)
    /// rører intet. Første gang er grundlaget, så der kommer ingen ændringer.
    /// </summary>
    public static async Task<int> Drivers(AppDb db, int machineId, List<DriverEntry>? drivers, DateTime nowUtc)
    {
        if (drivers is null) return 0;
        var clean = DriverList.Clean(drivers);
        if (clean.Count == 0) return 0;
        var old = await db.Drivers.AsNoTracking().Where(d => d.MachineId == machineId).ToListAsync();
        var n = Add(db, machineId, ChangeKinds.Driver, old.Select(d => new ChangeItem(d.Name, d.Version, d.Provider)).ToList(),
            clean.Select(d => new ChangeItem(d.Name, d.Version, d.Provider)).ToList(), nowUtc);
        await db.Drivers.Where(d => d.MachineId == machineId).ExecuteDeleteAsync();
        db.Drivers.AddRange(clean.Select(d => new DriverItem { MachineId = machineId, Name = d.Name, Version = d.Version, Provider = d.Provider, Class = d.Class, Date = Cut(d.Date, 10) }));
        return n;
    }

    public static async Task Prune(AppDb db, DateTime nowUtc)
    {
        var cut = nowUtc.AddDays(-RetentionDays);
        await db.ChangeLog.Where(x => x.Time < cut).ExecuteDeleteAsync();
        var over = await db.ChangeLog.GroupBy(x => x.MachineId).Where(g => g.Count() > MaxPerMachine).Select(g => g.Key).ToListAsync();
        foreach (var mid in over)
        {
            var keepFrom = await db.ChangeLog.Where(x => x.MachineId == mid).OrderByDescending(x => x.Id).Skip(MaxPerMachine).Select(x => x.Id).FirstAsync();
            await db.ChangeLog.Where(x => x.MachineId == mid && x.Id <= keepFrom).ExecuteDeleteAsync();
        }
    }

    private static string? Cut(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}
