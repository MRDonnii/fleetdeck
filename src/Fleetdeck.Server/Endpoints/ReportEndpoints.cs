using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services;
using Fleetdeck.Server.Services.Reports;

namespace Fleetdeck.Server.Endpoints;

public sealed record ReportScheduleRequest(List<ReportSchedule> Schedules, WeeklySummary Summary);

/// <summary>
/// Rapporter (Drift → Rapporter): kun administratorer, og hver eksport skrives i revisionsloggen ("report.export").
/// GET /api/reports/{navn}?format=json|csv|pdf&amp;machines=1,2&amp;profile=3&amp;days=30&amp;q=…&amp;section=…&amp;page=1&amp;pageSize=200
/// </summary>
public static class ReportEndpoints
{
    /// <summary>Forhåndsvisningen henter JSON igen ved hvert filterskift; det logges højst én gang i kvarteret pr. bruger og rapport.</summary>
    private static readonly ConcurrentDictionary<(string User, string Report), DateTime> lastJsonAudit = new();
    public static readonly TimeSpan JsonAuditWindow = TimeSpan.FromMinutes(15);

    public static void MapReportEndpoints(this WebApplication app)
    {
        var adm = app.MapGroup("/api/reports").RequireAuthorization("admin");

        adm.MapGet("", async (AppDb db) => Results.Ok(new
        {
            reports = ReportCatalog.All.Select(d => new { d.Name, d.Title, d.Description, d.Group, machines = d.Machines, period = d.Period, search = d.Search, d.DefaultDays, d.DayChoices }),
            machines = (await db.Machines.AsNoTracking().Select(m => new { m.Id, m.Name, m.DisplayName, m.ProfileId }).ToListAsync())
                .Select(m => new { m.Id, label = m.DisplayName ?? m.Name, m.ProfileId }).OrderBy(m => m.label),
            profiles = await db.Profiles.AsNoTracking().OrderBy(p => p.Name).Select(p => new { p.Id, p.Name }).ToListAsync(),
        }));

        adm.MapGet("/schedules", async (SettingsService s) =>
        {
            var cfg = await ReportScheduler.Load(s);
            var mqtt = (await s.Get("mqtt_host")).Trim().Length > 0;
            var webhook = (await s.Get("webhook_url")).Trim().Length > 0;
            return Results.Ok(new { cfg.Schedules, cfg.Summary, channels = new { push = mqtt || webhook, mqtt, webhook, email = false }, serverUrl = await s.Get("server_url") });
        });

        adm.MapPut("/schedules", async (ReportScheduleRequest r, SettingsService s, AuditService audit, ClaimsPrincipal u) =>
        {
            if (Validate(r) is { } err) return Results.BadRequest(new { error = err });
            var before = await ReportScheduler.Load(s);
            var now = DateTime.UtcNow;
            foreach (var sc in r.Schedules)
            {
                if (sc.Id.Length == 0) sc.Id = Guid.NewGuid().ToString("N")[..12];
                var old = before.Schedules.FirstOrDefault(x => x.Id == sc.Id);
                // en ny eller ændret plan venter til næste planlagte tid (sender ikke straks for en tid, der lige er gået)
                sc.LastRun = old is not null && old.Frequency == sc.Frequency && old.Day == sc.Day && old.Time == sc.Time ? old.LastRun : now;
                sc.LastResult = old?.LastResult;
                sc.Reports = sc.Reports.Where(n => ReportCatalog.Find(n) is not null).Distinct().ToList();
            }
            var sum = r.Summary;
            sum.LastRun = before.Summary.Enabled && sum.Enabled && before.Summary.Day == sum.Day && before.Summary.Time == sum.Time ? before.Summary.LastRun : now;
            sum.LastResult = before.Summary.LastResult;
            await ReportScheduler.Save(s, new ReportScheduleConfig { Schedules = r.Schedules, Summary = sum });
            await audit.Log(u.UserName(), "report.schedule", $"{r.Schedules.Count} plan(er), {r.Schedules.Count(x => x.Enabled)} slået til · ugentlig opsummering {(sum.Enabled ? "til" : "fra")}");
            return Results.Ok(new { ok = true });
        });

        // "Send nu": samme besked som planen ville sende (til at prøve kanalen)
        adm.MapPost("/schedules/{id}/send", async (string id, AppDb db, SettingsService s, Notifier notifier, AuditService audit, ClaimsPrincipal u) =>
        {
            var cfg = await ReportScheduler.Load(s);
            var link = (await s.Get("server_url")).Trim().TrimEnd('/') is { Length: > 0 } x ? x + "/rapporter" : "";
            string text;
            if (id == "summary") text = await ReportScheduler.ComposeSummary(db, cfg.Summary, link, DateTime.UtcNow);
            else if (cfg.Schedules.FirstOrDefault(x => x.Id == id) is { Reports.Count: > 0 } sc) text = await ReportScheduler.Compose(db, sc, link, DateTime.UtcNow);
            else return Results.NotFound();
            notifier.Send(text, "report");
            await audit.Log(u.UserName(), "report.send", id == "summary" ? "Ugentlig opsummering" : cfg.Schedules.First(x => x.Id == id).Name);
            return Results.Ok(new { text });
        });

        adm.MapGet("/{name}", async (string name, string? format, string? machines, int? profile, int? days, string? q, string? section, int? page, int? pageSize,
            HttpContext ctx, AppDb db, AuditService audit, ClaimsPrincipal u) =>
        {
            if (ReportCatalog.Find(name) is not { } def) return Results.NotFound();
            var fmt = (format ?? "json").ToLowerInvariant();
            if (fmt is not ("json" or "csv" or "pdf")) return Results.BadRequest(new { error = "format skal være json, csv eller pdf" });
            var f = ReportFilter.Parse(machines, profile, days, q);
            var now = DateTime.UtcNow;
            var r = await ReportCatalog.Run(db, def, f, now);
            var user = u.UserName();
            var detail = $"{def.Title} ({fmt}) · {r.RowCount} rækker · {r.Filter}{(section is { Length: > 0 } ? $" · {section}" : "")}";
            var file = $"rapport-{def.Name}-{ReportFormat.Local(now):yyyyMMdd-HHmm}";
            switch (fmt)
            {
                case "csv":
                    await audit.Log(user, "report.export", detail);
                    return Results.Stream(async stream =>
                    {
                        await using var w = new StreamWriter(stream, new UTF8Encoding(false), 16 * 1024, leaveOpen: true);
                        await ReportExport.WriteCsv(w, r, section);
                    }, "text/csv; charset=utf-8", file + ".csv");
                case "pdf":
                    await audit.Log(user, "report.export", detail);
                    return Results.File(ReportExport.Pdf(r, section), "application/pdf", file + ".pdf");
                default:
                    if (ShouldAuditJson(user, def.Name, now)) await audit.Log(user, "report.export", detail);
                    return Results.Ok(ReportExport.Page(r, section, page ?? 1, pageSize ?? 200));
            }
        });
    }

    public static bool ShouldAuditJson(string user, string report, DateTime now)
    {
        var key = (user, report);
        if (lastJsonAudit.TryGetValue(key, out var last) && now - last < JsonAuditWindow) return false;
        lastJsonAudit[key] = now;
        return true;
    }

    public static string? Validate(ReportScheduleRequest r)
    {
        if (r.Schedules.Count > 20) return "Højst 20 planer";
        foreach (var s in r.Schedules.Append(null))
        {
            var (freq, day, time) = s is null ? ("weekly", r.Summary.Day, r.Summary.Time) : (s.Frequency, s.Day, s.Time);
            if (freq is not ("weekly" or "monthly")) return "Hyppighed skal være ugentlig eller månedlig";
            if (freq == "weekly" ? day is < 0 or > 6 : day is < 1 or > 28) return "Ugyldig dag";
            if (!TimeSpan.TryParseExact(time, @"hh\:mm", null, out var t) || t >= TimeSpan.FromDays(1)) return "Tidspunkt skal være TT:MM";
            if (s is not null && s.Name.Length > 80) return "Navnet er for langt";
        }
        return null;
    }
}
