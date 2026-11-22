using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;

namespace Fleetdeck.Server.Services.Reports;

/// <summary>En planlagt rapport: et udvalg af rapporter, der sendes som en kort push-besked med link (HA/MQTT og webhook).</summary>
public sealed class ReportSchedule
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Reports { get; set; } = [];
    /// <summary>weekly | monthly</summary>
    public string Frequency { get; set; } = "weekly";
    /// <summary>Ugentligt: 0 = mandag … 6 = søndag. Månedligt: dag 1–28.</summary>
    public int Day { get; set; }
    public string Time { get; set; } = "07:00";
    public bool Enabled { get; set; } = true;
    public List<int> Machines { get; set; } = [];
    public int? Profile { get; set; }
    public DateTime? LastRun { get; set; }
    public string? LastResult { get; set; }
}

/// <summary>"Ugentlig opsummering": børnenes iPad-forbrug den seneste uge og flådens helbred i én besked. Slået fra som standard.</summary>
public sealed class WeeklySummary
{
    public bool Enabled { get; set; }
    public int Day { get; set; } = 6;
    public string Time { get; set; } = "18:00";
    public bool Ipads { get; set; } = true;
    public bool Fleet { get; set; } = true;
    public DateTime? LastRun { get; set; }
    public string? LastResult { get; set; }
}

public sealed class ReportScheduleConfig
{
    public List<ReportSchedule> Schedules { get; set; } = [];
    public WeeklySummary Summary { get; set; } = new();
}

/// <summary>
/// Planlagte rapporter og ugentlig opsummering. Køres af baggrundstråden hvert halve minut; kanalen er Fleetdecks push
/// (Home Assistant via MQTT og webhook). E-mail findes ikke, fordi Fleetdeck ikke har SMTP-indstillinger.
/// </summary>
public sealed class ReportScheduler(AppDb db, SettingsService settings, Notifier notifier)
{
    public const string Key = "report_schedules";
    /// <summary>En planlagt tid, der er gået for mere end dette, indhentes ikke (fx efter en lang nedetid).</summary>
    public static readonly TimeSpan CatchUp = TimeSpan.FromHours(6);

    public static async Task<ReportScheduleConfig> Load(SettingsService s) => Json.Des<ReportScheduleConfig>(await s.Get(Key));
    public static Task Save(SettingsService s, ReportScheduleConfig c) => s.Set(Key, Json.Ser(c));

    public Task Tick() => RunDue(db, settings, (text, kind) => notifier.Send(text, kind), DateTime.UtcNow);

    /// <summary>Sidste planlagte tidspunkt (UTC) på eller før nu, efter lokal tid.</summary>
    public static DateTime Previous(string frequency, int day, string time, DateTime nowUtc)
    {
        var tz = IpadEngine.Tz;
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
        var tod = TimeSpan.TryParseExact(time, @"hh\:mm", CultureInfo.InvariantCulture, out var t) ? t : TimeSpan.FromHours(7);
        DateTime cand;
        if (frequency == "monthly")
        {
            var d = Math.Clamp(day, 1, 28);
            cand = new DateTime(local.Year, local.Month, d).Add(tod);
            if (cand > local) { var pm = local.AddMonths(-1); cand = new DateTime(pm.Year, pm.Month, d).Add(tod); }
        }
        else
        {
            var want = (DayOfWeek)((Math.Clamp(day, 0, 6) + 1) % 7);   // 0 = mandag
            cand = local.Date.Add(tod);
            while (cand.DayOfWeek != want || cand > local) cand = cand.AddDays(-1);
        }
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(cand, DateTimeKind.Unspecified), tz);
    }

    public static bool IsDue(string frequency, int day, string time, DateTime? lastRun, DateTime nowUtc)
    {
        var prev = Previous(frequency, day, time, nowUtc);
        return nowUtc - prev <= CatchUp && (lastRun is null || lastRun < prev);
    }

    /// <summary>Sender det, der er forfaldent, og gemmer hvornår. Returnerer de sendte beskeder (til test og "Send nu").</summary>
    public static async Task<List<string>> RunDue(AppDb db, SettingsService s, Action<string, string> send, DateTime nowUtc)
    {
        var cfg = await Load(s);
        var sent = new List<string>();
        var link = await Link(s);
        foreach (var sc in cfg.Schedules.Where(x => x.Enabled && x.Reports.Count > 0 && IsDue(x.Frequency, x.Day, x.Time, x.LastRun, nowUtc)))
        {
            var text = await Compose(db, sc, link, nowUtc);
            send(text, "report"); sent.Add(text);
            sc.LastRun = nowUtc; sc.LastResult = "sendt";
        }
        var sum = cfg.Summary;
        if (sum.Enabled && (sum.Ipads || sum.Fleet) && IsDue("weekly", sum.Day, sum.Time, sum.LastRun, nowUtc))
        {
            var text = await ComposeSummary(db, sum, link, nowUtc);
            send(text, "report"); sent.Add(text);
            sum.LastRun = nowUtc; sum.LastResult = "sendt";
        }
        if (sent.Count > 0)
        {
            await Save(s, cfg);
            db.Audit.Add(AuditService.Entry("system", "report.scheduled", $"{sent.Count} besked(er) sendt"));
            await db.SaveChangesAsync();
        }
        return sent;
    }

    private static async Task<string> Link(SettingsService s) =>
        (await s.Get("server_url")).Trim().TrimEnd('/') is { Length: > 0 } u ? u + "/rapporter" : "";

    public static async Task<string> Compose(AppDb db, ReportSchedule sc, string link, DateTime nowUtc)
    {
        var lines = new List<string> { $"📊 {(sc.Name.Length > 0 ? sc.Name : "Rapport")}" };
        var f = new ReportFilter(sc.Machines.Count > 0 ? sc.Machines : null, sc.Profile);
        foreach (var name in sc.Reports)
            if (ReportCatalog.Find(name) is { } def)
                lines.Add((await ReportCatalog.Run(db, def, f, nowUtc)).Summary);
        if (link.Length > 0) lines.Add($"{link}?r={Uri.EscapeDataString(sc.Reports[0])}");
        return string.Join("\n", lines);
    }

    public static async Task<string> ComposeSummary(AppDb db, WeeklySummary sum, string link, DateTime nowUtc)
    {
        var lines = new List<string> { "📅 Ugentlig opsummering" };
        if (sum.Ipads)
        {
            var from = nowUtc.AddDays(-7);
            var ipads = await db.Ipads.AsNoTracking().Where(p => p.Kind == IpadKinds.Child).ToListAsync();
            var ids = ipads.Select(p => p.Id).ToList();
            var slots = await db.IpadSlots.AsNoTracking().Where(x => ids.Contains(x.IpadId) && x.Slot >= from && !x.Service.StartsWith(IpadPolicy.AppPrefix)).Select(x => new { x.IpadId, x.Service }).ToListAsync();
            var parts = ipads.OrderBy(p => ReportCatalog.IpadLabel(p), StringComparer.Create(ReportFormat.Da, true)).Select(p =>
            {
                var mine = slots.Where(x => x.IpadId == p.Id).ToList();
                var total = mine.Count(x => x.Service == "*") * 5;
                var top = mine.Where(x => x.Service != "*").GroupBy(x => x.Service).Select(g => (g.Key, M: g.Count() * 5)).OrderByDescending(x => x.M).FirstOrDefault();
                return $"{p.Owner ?? p.Name} {ReportFormat.Minutes(total)}" + (top.Key is { } k ? $" (mest {k} {ReportFormat.Minutes(top.M)})" : "");
            }).ToList();
            lines.Add(parts.Count > 0 ? "iPads (7 dage): " + string.Join(", ", parts) : "iPads: ingen børne-iPads");
        }
        if (sum.Fleet)
        {
            var ms = await db.Machines.AsNoTracking().Select(m => new { m.HealthScore, m.HealthState }).ToListAsync();
            var scored = ms.Where(m => m.HealthScore is not null).ToList();
            var alerts = await db.Alerts.AsNoTracking().Where(a => a.Resolved == null).GroupBy(a => a.Severity).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
            var ups = await db.Upgrades.AsNoTracking().CountAsync();
            lines.Add($"Flåden: {ms.Count} maskiner" + (scored.Count > 0 ? $" · Ø {scored.Average(m => m.HealthScore!.Value):0}/100" : "") +
                $" · {alerts.GetValueOrDefault("critical")} kritiske og {alerts.GetValueOrDefault("warning")} advarsler · {ups} programopdateringer venter");
        }
        if (link.Length > 0) lines.Add(link);
        return string.Join("\n", lines);
    }
}
