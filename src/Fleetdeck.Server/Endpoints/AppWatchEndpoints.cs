using System.Globalization;
using System.Security.Claims;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Server.Endpoints;

public sealed record AppWatchInput(string Name, string Repo, string? Channel, int? MachineId, string? PlaceKind, string? PlaceName, string? ReadKind,
    string? ReadTarget, string? ReadField, string? UpdateCommand, bool? AutoUpdate, bool? NeverAuto, bool? Notify);
public sealed record AppWatchUpdateInput(string? Version, bool Confirm);
public sealed record AppWatchSettingsInput(int? Hours, int? AutoHour, string? Token);

/// <summary>Siden Apps: brugerens egne apps fra GitHub, deres kørende version og en Opdatér-knap (Services/AppWatch.cs).</summary>
public static class AppWatchEndpoints
{
    private static IResult Bad(string msg) => Results.Problem(msg, statusCode: 400);

    public static void MapAppWatchEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/apps").RequireAuthorization("user");
        var adm = app.MapGroup("/api/apps").RequireAuthorization("admin");

        api.MapGet("", async (HttpContext ctx, AppDb db, AppWatchService svc, SettingsService s) =>
        {
            var admin = ctx.User.IsInRole(Roles.Admin);
            var apps = await db.WatchedApps.AsNoTracking().OrderBy(a => a.Name).ToListAsync();
            var poll = await s.GetNum("poll_seconds", 30);
            var machines = await db.Machines.AsNoTracking().OrderBy(m => m.DisplayName ?? m.Name).ToListAsync();
            var jobIds = apps.SelectMany(a => new[] { a.ReadJobId, a.UpdateJobId }).OfType<int>().ToList();
            var jobs = await db.Jobs.AsNoTracking().Where(j => jobIds.Contains(j.Id)).Select(j => new { j.Id, j.Status, j.Progress, j.Step }).ToDictionaryAsync(j => j.Id);
            return Results.Ok(new
            {
                apps = apps.Select(a =>
                {
                    var rels = AppWatchService.Releases(a);
                    var cmp = AppWatch.Compare(a.Running, rels, a.Channel);
                    var m = machines.FirstOrDefault(x => x.Id == a.MachineId);
                    return new
                    {
                        a.Id, a.Name, a.Repo, a.Channel, a.MachineId, machine = m?.Label, online = m is not null && MachineViews.Online(m, poll), platform = m?.Platform,
                        a.PlaceKind, a.PlaceName, a.ReadKind, readTarget = admin ? a.ReadTarget : "", a.ReadField,
                        updateCommand = admin ? a.UpdateCommand : "", hasUpdateCommand = a.UpdateCommand.Length > 0,
                        a.AutoUpdate, a.NeverAuto, a.Notify,
                        a.Running, a.RunningAt, a.ReadError, a.Checked, a.CheckError,
                        status = cmp.Status, latest = cmp.Latest is { } l ? Rel(l) : null, newer = cmp.Newer.Select(Rel),
                        releases = rels.Where(r => a.Channel == ReleaseFeed.Beta || !r.Prerelease).Take(8).Select(r => new { r.Version, r.Prerelease, r.Published }),
                        reading = a.ReadJobId is { } rj && jobs.ContainsKey(rj),
                        updateJob = a.UpdateJobId is { } uj && jobs.TryGetValue(uj, out var j) ? new { j.Id, j.Status, j.Progress, j.Step, to = a.UpdateTo } : null,
                    };
                }),
                settings = new
                {
                    hours = (int)await s.GetNum(AppWatch.HoursKey, 6), autoHour = (int)await s.GetNum(AppWatch.AutoHourKey, 3),
                    hasToken = await svc.HasToken(), rate = await svc.Rate(),
                },
                machines = admin ? machines.Select(m => new { m.Id, name = m.Label, m.Platform, online = MachineViews.Online(m, poll) }) : null,
            });
        });

        // forsiden: kun antallet og navnene på apps med en ny version
        api.MapGet("/summary", async (AppDb db) =>
        {
            var apps = await db.WatchedApps.AsNoTracking().ToListAsync();
            var fresh = apps.Select(a => (a, cmp: AppWatch.Compare(a.Running, AppWatchService.Releases(a), a.Channel)))
                .Where(x => x.cmp.Status == AppStatus.New).Select(x => new { x.a.Id, x.a.Name, x.a.Running, latest = x.cmp.Latest!.Version, x.a.NeverAuto }).ToList();
            return Results.Ok(new { total = apps.Count, updates = fresh });
        });

        adm.MapPost("", async (AppWatchInput i, HttpContext ctx, AppDb db, AppWatchService svc, AuditService audit) =>
        {
            var a = new WatchedApp();
            if (await Apply(a, i, db) is { } err) return Bad(err);
            db.WatchedApps.Add(a);
            await db.SaveChangesAsync();
            await audit.Log(ctx.User.UserName(), "apps.create", $"{a.Name} ({a.Repo}, {a.Channel})", a.MachineId);
            await svc.Check(a); await svc.Read(a);
            return Results.Ok(new { a.Id });
        });

        adm.MapPut("/{id:int}", async (int id, AppWatchInput i, HttpContext ctx, AppDb db, AppWatchService svc, AuditService audit) =>
        {
            if (await db.WatchedApps.FindAsync(id) is not { } a) return Results.NotFound();
            var (repo, channel, kind, target, field, mid) = (a.Repo, a.Channel, a.ReadKind, a.ReadTarget, a.ReadField, a.MachineId);
            if (await Apply(a, i, db) is { } err) return Bad(err);
            if (repo != a.Repo) { a.ReleasesJson = "[]"; a.ETag = null; a.Checked = null; a.NotifiedVersion = null; }
            if (kind != a.ReadKind || target != a.ReadTarget || field != a.ReadField || mid != a.MachineId) { a.Running = null; a.RunningAt = null; a.ReadError = null; a.ReadJobId = null; }
            await db.SaveChangesAsync();
            await audit.Log(ctx.User.UserName(), "apps.edit", $"{a.Name} ({a.Repo}, {a.Channel}){(a.NeverAuto ? ", aldrig automatisk" : a.AutoUpdate ? ", automatisk" : "")}", a.MachineId);
            if (a.Checked is null) await svc.Check(a);
            if (a.RunningAt is null) await svc.Read(a);
            return Results.Ok();
        });

        adm.MapDelete("/{id:int}", async (int id, HttpContext ctx, AppDb db, AuditService audit) =>
        {
            if (await db.WatchedApps.FindAsync(id) is not { } a) return Results.NotFound();
            db.WatchedApps.Remove(a);
            await db.SaveChangesAsync();
            await audit.Log(ctx.User.UserName(), "apps.delete", a.Name, a.MachineId);
            return Results.Ok();
        });

        // Tjek nu: én app eller alle (GitHub og den kørende version)
        adm.MapPost("/check", async (int? id, AppDb db, AppWatchService svc) =>
        {
            var apps = await db.WatchedApps.Where(a => id == null || a.Id == id).ToListAsync();
            var errors = new List<string>();
            foreach (var a in apps)
            {
                await svc.Settle(a);
                if ((await svc.Check(a)).Error is { } e) errors.Add($"{a.Name}: {e}");
                if (a.ReadJobId is null && a.UpdateJobId is null) await svc.Read(a);
            }
            return Results.Ok(new { count = apps.Count, errors });
        });

        adm.MapPost("/{id:int}/update", async (int id, AppWatchUpdateInput i, HttpContext ctx, AppDb db, AppWatchService svc) =>
        {
            if (await db.WatchedApps.FindAsync(id) is not { } a) return Results.NotFound();
            var (job, err) = await svc.Update(a, i.Version, i.Confirm, ctx.User.UserName());
            return err is null ? Results.Ok(new { jobId = job }) : Bad(err);
        });

        adm.MapPut("/settings", async (AppWatchSettingsInput i, HttpContext ctx, SettingsService s, AppWatchService svc, AuditService audit) =>
        {
            if (i.Hours is { } h && h is < 1 or > 168) return Bad("Tjek GitHub hver 1.–168. time");
            if (i.AutoHour is { } ah && ah is < 0 or > 23) return Bad("Tidspunktet skal være 0-23");
            if (i.Token is { Length: > 200 }) return Bad("GitHub-nøglen er for lang");
            if (i.Hours is { } hh) await s.Set(AppWatch.HoursKey, hh.ToString(CultureInfo.InvariantCulture));
            if (i.AutoHour is { } aa) await s.Set(AppWatch.AutoHourKey, aa.ToString(CultureInfo.InvariantCulture));
            await svc.SetToken(i.Token, ctx.User.UserName());
            await audit.Log(ctx.User.UserName(), "apps.settings", $"hver {i.Hours?.ToString(CultureInfo.InvariantCulture) ?? "uændret"}. time, automatisk kl. {i.AutoHour?.ToString(CultureInfo.InvariantCulture) ?? "uændret"}");
            return Results.Ok();
        });
    }

    private static object Rel(ReleaseInfo r) => new { r.Version, r.Tag, r.Name, r.Notes, r.Prerelease, r.Published, r.Url };

    private static async Task<string?> Apply(WatchedApp a, AppWatchInput i, AppDb db)
    {
        a.Name = i.Name ?? ""; a.Repo = i.Repo ?? ""; a.Channel = i.Channel ?? ReleaseFeed.Stable; a.MachineId = i.MachineId;
        a.PlaceKind = i.PlaceKind ?? "container"; a.PlaceName = i.PlaceName ?? ""; a.ReadKind = i.ReadKind ?? "none";
        a.ReadTarget = i.ReadTarget ?? ""; a.ReadField = i.ReadField ?? ""; a.UpdateCommand = i.UpdateCommand ?? "";
        a.AutoUpdate = i.AutoUpdate ?? false; a.NeverAuto = i.NeverAuto ?? false; a.Notify = i.Notify ?? true;
        AppWatch.Clean(a);
        if (AppWatch.Validate(a) is { } err) return err;
        if (a.MachineId is { } mid && !await db.Machines.AnyAsync(m => m.Id == mid)) return "Maskinen findes ikke";
        return null;
    }
}
