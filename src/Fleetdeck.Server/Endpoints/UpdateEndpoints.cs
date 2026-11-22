using System.Security.Claims;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Server.Endpoints;

public sealed record UpdateConfigInput(string Source, string? Repo, string Channel, string? Image, string? LocalDir, bool Auto, int AutoHour,
    int? MachineId, string? Container, int HealthMinutes, string? Token);
public sealed record UpdateStartInput(string Version, bool AllowOlder);

/// <summary>Opdatering af selve Fleetdeck fra brugerfladen (Indstillinger → Generelt). Kun admin.</summary>
public static class UpdateEndpoints
{
    private static IResult Bad(string msg) => Results.Problem(msg, statusCode: 400);

    public static void MapUpdateEndpoints(this WebApplication app)
    {
        var adm = app.MapGroup("/api/update").RequireAuthorization("admin");

        adm.MapGet("", async (UpdateService up, SettingsService s, AppDb db) =>
        {
            await up.Settle();
            var cfg = await up.Config();
            var cache = await up.Cached();
            var cur = SemVer.Parse(UpdateService.Current) ?? default;
            var latest = ReleaseFeed.Latest(cache.Releases, cfg.Channel);
            var t = await up.Target(cfg);
            var poll = await s.GetNum("poll_seconds", 30);
            var linux = await db.Machines.AsNoTracking().Where(m => m.Platform == "linux").OrderBy(m => m.DisplayName ?? m.Name).ToListAsync();
            var state = await up.CurrentState();
            var job = state is null ? null : await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == state.JobId);
            object Rel(ReleaseInfo r)
            {
                var image = UpdateScript.ImageFor(r, cfg.ImageTemplate);
                return new
                {
                    r.Version, r.Name, r.Notes, r.Prerelease, r.Published, r.Url,
                    newer = r.Sem > cur, same = r.Sem.CompareTo(cur) == 0,
                    image = r.LocalDir is not null ? $"bygges fra {r.LocalDir}" : image ?? (UpdateScript.AssetFor(r, cfg.Repo) is not null ? "billedfil i udgivelsen" : null),
                };
            }
            return Results.Ok(new
            {
                current = UpdateService.Current, inDocker = UpdateService.InDocker,
                config = new
                {
                    cfg.Source, cfg.Repo, cfg.Channel, image = cfg.ImageTemplate, cfg.LocalDir, cfg.Auto, cfg.AutoHour, cfg.MachineId, cfg.Container, cfg.HealthMinutes,
                    hasToken = (await s.Get("update_token")).Length > 0,
                },
                checkedAt = cache.Checked, cache.Error,
                latest = latest is null ? null : Rel(latest),
                updateAvailable = latest is not null && latest.Sem > cur,
                releases = cache.Releases.Where(r => cfg.Channel == ReleaseFeed.Beta || !r.Prerelease).Take(15).Select(Rel),
                target = t.Machine is null ? null : new { machineId = t.Machine.Id, machine = t.Machine.Label, t.Container, online = MachineViews.Online(t.Machine, poll), t.Detected, t.OnHost, t.Note },
                problem = t.Problem,
                t.Candidates,
                machines = linux.Select(m => new { m.Id, name = m.Label, online = MachineViews.Online(m, poll), unraid = Json.Des<HealthReport>(m.HealthJson).Unraid is not null }),
                state,
                job = job is null ? null : new
                {
                    job.Id, job.Title, job.Status, job.Progress, job.Step, job.Created, job.Started, job.Finished,
                    output = job.Output.Length > 6000 ? job.Output[^6000..] : job.Output,
                    machine = linux.FirstOrDefault(m => m.Id == job.MachineId)?.Label,
                },
            });
        });

        adm.MapPut("/config", async (UpdateConfigInput i, UpdateService up, ClaimsPrincipal u) =>
        {
            var cfg = new UpdateConfig(i.Source, (i.Repo ?? "").Trim(), i.Channel, (i.Image ?? "").Trim(), (i.LocalDir ?? "").Trim(), i.Auto, i.AutoHour,
                i.MachineId is > 0 ? i.MachineId : null, (i.Container ?? "").Trim(), i.HealthMinutes);
            // GitHub-nøglen: null/tom = uændret, "-" = fjern
            var token = i.Token switch { null or "" => null, "-" => "", var t => t.Trim() };
            return await up.Save(cfg, token, u.UserName()) is { } err ? Bad(err) : Results.Ok();
        });

        adm.MapPost("/check", async (UpdateService up, CancellationToken ct) =>
        {
            var c = await up.Check(ct);
            return c.Error is { } e ? Bad(e) : Results.Ok(new { count = c.Releases.Count });
        });

        adm.MapPost("/start", async (UpdateStartInput i, UpdateService up, ClaimsPrincipal u) =>
        {
            var (jobId, err) = await up.Start(i.Version.Trim(), i.AllowOlder, u.UserName());
            return err is not null ? Bad(err) : Results.Ok(new { jobId });
        });
    }
}
