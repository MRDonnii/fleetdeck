using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Shared;

namespace Fleetdeck.Server.Services;

public static class TimelineKinds
{
    /// <summary>Gemte rækker (<see cref="TimelineMark"/>).</summary>
    public const string Offline = "offline", Boot = "boot";
    /// <summary>Markører, der hentes fra andre tabeller.</summary>
    public const string Update = "update", Event = "event", Change = "change";
}

public static class TimelineStates
{
    public const string Online = "online", Offline = "offline", Unknown = "unknown";
}

public sealed record TimelineSegment(DateTime From, DateTime To, string State);
public sealed record TimelineMarker(DateTime Time, string Kind, string Severity, string Title, string? Detail = null, string? Ref = null, int Count = 1);
public sealed record TimelineView(DateTime From, DateTime To, List<TimelineSegment> Segments, List<TimelineMarker> Markers, double? OnlinePct,
    DateTime? KnownSince, int Boots, int Updates, int Events, int Changes, int OfflineCount);

/// <summary>
/// Tidslinjen pr. maskine (Helbred-fanen): et statusbånd (online/offline/ukendt) og markører for opstarter, opdateringer, alvorlige
/// hændelser fra fejlloggen og ændringer i programmer og drivere. Offline-perioder og opstarter gemmes ved check-in (billigt: kun en række,
/// når noget sker); resten bygges ud fra de tabeller, der findes i forvejen.
/// </summary>
public static class Timeline
{
    public const int RetentionDays = 35, MaxMarkers = 400;
    /// <summary>Så længe uden check-in tæller som offline (samme grænse som "var væk" ved check-in).</summary>
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan BootTolerance = TimeSpan.FromMinutes(10);

    /// <summary>Opstartstidspunktet: agentens eget, ellers tidspunktet minus oppetiden.</summary>
    public static DateTime? BootOf(LiveFacts? f, DateTime? at) =>
        f?.BootAt ?? (f?.UptimeH is double up && up >= 0 && at is { } t ? t.AddHours(-up) : null);

    /// <summary>
    /// Ved check-in (før LastSeen opdateres): var maskinen væk mere end <see cref="OfflineAfter"/>, gemmes perioden; er opstartstidspunktet
    /// flyttet frem, gemmes en opstart. Første gang begynder tidslinjen bare. Gemmer ikke selv.
    /// </summary>
    public static void OnCheckin(AppDb db, Machine m, DateTime? prevLastSeen, DateTime? prevBoot, DateTime? newBoot, DateTime nowUtc)
    {
        if (m.TimelineSince is null) { m.TimelineSince = nowUtc; return; }
        if (prevLastSeen is { } ls && ls < nowUtc - OfflineAfter && ls >= m.TimelineSince.Value.AddDays(-1))
            db.Timeline.Add(new TimelineMark { MachineId = m.Id, Kind = TimelineKinds.Offline, Start = ls, End = nowUtc });
        if (newBoot is { } nb && prevBoot is { } pb && nb - pb > BootTolerance && nb <= nowUtc.AddMinutes(5))
            db.Timeline.Add(new TimelineMark { MachineId = m.Id, Kind = TimelineKinds.Boot, Start = nb });
    }

    /// <summary>
    /// Statusbåndet: ukendt før <paramref name="knownSince"/> (før tidslinjen begyndte), offline i de gemte perioder og lige nu, hvis
    /// maskinen ikke har meldt sig i <see cref="OfflineAfter"/>; ellers online. Tilstødende stykker med samme tilstand lægges sammen.
    /// </summary>
    public static List<TimelineSegment> Segments(DateTime from, DateTime to, DateTime? knownSince, DateTime? lastSeen, IEnumerable<(DateTime Start, DateTime End)> offline)
    {
        if (to <= from) return [];
        var off = offline.Select(o => (Start: Max(o.Start, from), End: Min(o.End, to))).Where(o => o.End > o.Start).ToList();
        if (lastSeen is { } ls && ls < to - OfflineAfter && Max(ls, from) < to) off.Add((Max(ls, from), to));
        var cuts = new SortedSet<DateTime> { from, to };
        if (knownSince is { } k && k > from && k < to) cuts.Add(k);
        foreach (var o in off) { cuts.Add(o.Start); cuts.Add(o.End); }
        var points = cuts.ToList();
        var list = new List<TimelineSegment>();
        for (var i = 0; i + 1 < points.Count; i++)
        {
            var (a, b) = (points[i], points[i + 1]);
            var mid = a + (b - a) / 2;
            var state = off.Any(o => o.Start <= mid && mid < o.End) ? TimelineStates.Offline
                : knownSince is { } ks && mid >= ks ? TimelineStates.Online
                : TimelineStates.Unknown;
            if (list.Count > 0 && list[^1].State == state) list[^1] = list[^1] with { To = b };
            else list.Add(new(a, b, state));
        }
        return list;
    }

    /// <summary>Andel af den kendte tid, maskinen var online (null, hvis intet er kendt).</summary>
    public static double? OnlinePct(IEnumerable<TimelineSegment> segments)
    {
        double on = 0, off = 0;
        foreach (var s in segments)
            if (s.State == TimelineStates.Online) on += (s.To - s.From).TotalSeconds;
            else if (s.State == TimelineStates.Offline) off += (s.To - s.From).TotalSeconds;
        return on + off > 0 ? Math.Round(on / (on + off) * 100, 1) : null;
    }

    /// <summary>Ændringer fra samme inventar bliver til én markør ("2 installeret, 1 opdateret").</summary>
    public static List<TimelineMarker> ChangeMarkers(IEnumerable<ChangeLogRow> rows) => rows
        .GroupBy(r => r.Time)
        .Select(g =>
        {
            var parts = new List<string>();
            int Count(string a) => g.Count(r => r.Action == a);
            if (Count(ChangeActions.Installed) is > 0 and var i) parts.Add($"{i} installeret");
            if (Count(ChangeActions.Updated) is > 0 and var u) parts.Add($"{u} opdateret");
            if (Count(ChangeActions.Removed) is > 0 and var r) parts.Add($"{r} fjernet");
            var names = g.OrderBy(x => x.Action == ChangeActions.Updated ? 1 : 0).Take(5).Select(x => $"{Verb(x.Action)} {x.Name}").ToList();
            if (g.Count() > 5) names.Add($"… og {g.Count() - 5} mere");
            var drivers = g.All(x => x.Kind == ChangeKinds.Driver) ? "Drivere" : g.Any(x => x.Kind == ChangeKinds.Driver) ? "Programmer og drivere" : "Programmer";
            return new TimelineMarker(g.Key, TimelineKinds.Change, "info", $"{drivers}: {string.Join(", ", parts)}", string.Join("\n", names), Count: g.Count());
        })
        .ToList();

    /// <summary>Alvorlige hændelser af samme type inden for samme time bliver til én markør med antal.</summary>
    public static List<TimelineMarker> EventMarkers(IEnumerable<SysLogRow> rows) => rows
        .Where(r => r.Kind is not null && SysLogKinds.Serious.Contains(r.Kind))
        .GroupBy(r => (r.Kind, Hour: new DateTime(r.Time.Year, r.Time.Month, r.Time.Day, r.Time.Hour, 0, 0, DateTimeKind.Utc)))
        .Select(g =>
        {
            var first = g.OrderBy(r => r.Time).First();
            var severity = g.Any(r => r.Level == SysLogLevels.Critical) || g.Key.Kind is SysLogKinds.BugCheck or SysLogKinds.KernelPanic or SysLogKinds.MacPanic or SysLogKinds.WheaFatal
                ? "critical" : "warning";
            var detail = first.Message.Length <= 200 ? first.Message : first.Message[..199] + "…";
            return new TimelineMarker(first.Time, TimelineKinds.Event, severity, first.EventId is int id ? $"{first.Source} {id}" : first.Source, detail, g.Key.Kind, g.Count());
        })
        .ToList();

    private static string Verb(string action) => action switch { ChangeActions.Installed => "+", ChangeActions.Removed => "−", _ => "↑" };
    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    /// <summary>Hele tidslinjen for de sidste <paramref name="days"/> dage.</summary>
    public static async Task<TimelineView> For(AppDb db, Machine m, int days, DateTime nowUtc)
    {
        var from = nowUtc.AddDays(-days);
        var marks = await db.Timeline.AsNoTracking().Where(t => t.MachineId == m.Id && (t.End ?? t.Start) >= from && t.Start <= nowUtc).ToListAsync();
        var knownSince = m.TimelineSince is { } ts ? (m.Enrolled is { } en && en > ts ? en : ts) : (DateTime?)null;
        var segments = Segments(from, nowUtc, knownSince, m.LastSeen,
            marks.Where(t => t.Kind == TimelineKinds.Offline && t.End is not null).Select(t => (t.Start, t.End!.Value)));
        var markers = new List<TimelineMarker>();
        var boots = marks.Where(t => t.Kind == TimelineKinds.Boot).OrderBy(t => t.Start).ToList();
        markers.AddRange(boots.Select(b => new TimelineMarker(b.Start, TimelineKinds.Boot, "info", "Opstart")));
        var updateTypes = new[] { JobTypes.Upgrade, JobTypes.WindowsUpdate, JobTypes.Install };
        var jobs = await db.Jobs.AsNoTracking().Where(j => j.MachineId == m.Id && updateTypes.Contains(j.Type) && j.Status == JobStatus.Success && j.Finished >= from)
            .OrderByDescending(j => j.Finished).Take(100).Select(j => new { j.Finished, j.Title, j.Type }).ToListAsync();
        markers.AddRange(jobs.Select(j => new TimelineMarker(j.Finished!.Value, TimelineKinds.Update, "info", j.Title, j.Type == JobTypes.Install ? "Installeret af Fleetdeck" : "Opdateret af Fleetdeck")));
        var serious = SysLogKinds.Serious.ToList();
        var events = await db.SysLog.AsNoTracking().Where(x => x.MachineId == m.Id && x.Time >= from && x.Kind != null && serious.Contains(x.Kind))
            .OrderByDescending(x => x.Time).Take(1000).ToListAsync();
        markers.AddRange(EventMarkers(events));
        var changes = await db.ChangeLog.AsNoTracking().Where(x => x.MachineId == m.Id && x.Time >= from).OrderByDescending(x => x.Time).Take(2000).ToListAsync();
        markers.AddRange(ChangeMarkers(changes));
        markers = markers.OrderByDescending(x => x.Time).Take(MaxMarkers).OrderBy(x => x.Time).ToList();
        return new TimelineView(from, nowUtc, segments, markers, OnlinePct(segments), knownSince,
            Boots: boots.Count, Updates: jobs.Count, Events: events.Count, Changes: changes.Count,
            OfflineCount: segments.Count(s => s.State == TimelineStates.Offline));
    }

    public static Task Prune(AppDb db, DateTime nowUtc)
    {
        var cut = nowUtc.AddDays(-RetentionDays);
        return db.Timeline.Where(t => (t.End ?? t.Start) < cut).ExecuteDeleteAsync();
    }
}
