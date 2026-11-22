using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;

namespace Fleetdeck.Server.Endpoints;

public sealed record KontrolrumSsoUser(bool Allowed);

/// <summary>
/// Status til et dashboard (fx Kontrolrum) med en API-nøgle med rettigheden "kun læse" (Indstillinger → Adgang for programmer),
/// sendt som <c>Authorization: Bearer</c> eller <c>X-Api-Key</c>. Den gamle enkelt-nøgle er flyttet til nøglen "Kontrolrum" (ApiKeyMigration).
/// </summary>
public static class KontrolrumEndpoints
{
    private static DateTime? Utc(DateTime? d) => d is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;

    public static void MapKontrolrumEndpoints(this WebApplication app)
    {
        app.MapGet("/api/kontrolrum/status", async (AppDb db, SettingsService s) =>
        {
            var now = DateTime.UtcNow;
            var poll = await s.GetNum("poll_seconds", 30);
            var machines = await db.Machines.AsNoTracking().Include(m => m.Profile).ToListAsync();
            var running = await db.Jobs.AsNoTracking().Where(j => j.Status == JobStatus.Running).Select(j => j.MachineId).ToListAsync();
            var online = machines.Count(m => MachineViews.Online(m, poll));

            // Udrulninger: lagt klar/installerer (netværksboot) + nyinstallationer der er i gang (provision-jobs)
            var deployments = new List<object>();
            foreach (var d in await db.Deployments.AsNoTracking().Where(d => DeployStatus.Active.Contains(d.Status)).OrderBy(d => d.Id).ToListAsync())
                deployments.Add(new
                {
                    machine = d.PcName.ToUpperInvariant(),
                    image = DeployService.Describe(d),
                    state = d.Status == DeployStatus.Installing ? "running" : "queued",
                    progress = (int?)null, eta = (DateTime?)null,
                });
            var prov = await db.Jobs.AsNoTracking().Where(j => j.Source == "provision").Include(j => j.Machine)
                .Select(j => new { j.MachineId, Machine = j.Machine!.DisplayName ?? j.Machine.Name, Profile = j.Machine.Profile!.Name, j.Status, j.Created, Enrolled = j.Machine.Enrolled })
                .ToListAsync();
            foreach (var g in prov.Where(j => j.Enrolled == null || j.Created >= j.Enrolled.Value.AddMinutes(-5)).GroupBy(j => j.MachineId))
            {
                var total = g.Count(); var done = g.Count(j => !JobStatus.Active.Contains(j.Status));
                if (done == total) continue;   // færdig
                deployments.Add(new
                {
                    machine = g.First().Machine, image = $"Nyinstallation{(g.First().Profile is { } p ? $" ({p})" : "")}",
                    state = "running", progress = (int?)(int)Math.Round(100.0 * done / total), eta = (DateTime?)null,
                });
            }

            var alerts = await db.Alerts.AsNoTracking().Where(a => a.Resolved == null && a.Acknowledged == null && (a.Severity == "warning" || a.Severity == "critical"))
                .Include(a => a.Machine).OrderByDescending(a => a.Severity == "critical").ThenBy(a => a.FirstSeen)
                .Select(a => new { level = a.Severity, title = (a.Machine!.DisplayName ?? a.Machine.Name) + ": " + a.Title, since = a.FirstSeen }).ToListAsync();

            // Det der sker lige nu: kørende jobs med fremdrift og trin, derefter køen
            var activity = await db.Jobs.AsNoTracking().Where(j => JobStatus.Active.Contains(j.Status))
                .OrderBy(j => j.Status == JobStatus.Running ? 0 : 1).ThenBy(j => j.Id).Take(50)
                .Select(j => new { machine = j.Machine!.DisplayName ?? j.Machine.Name, j.Type, j.Title, j.Status, j.Progress, j.Step, j.Started, j.Created }).ToListAsync();

            return Results.Ok(new
            {
                version = ThisVersion.Value,
                machines = new { total = machines.Count, online, offline = machines.Count - online, busy = running.Distinct().Count() },
                jobs = new
                {
                    running = running.Count,
                    queued = await db.Jobs.CountAsync(j => j.Status == JobStatus.Pending || j.Status == JobStatus.WaitingUser),
                    failed24h = await db.Jobs.CountAsync(j => j.Status == JobStatus.Failed && j.Finished > now.AddHours(-24)),
                },
                deployments,
                alerts = alerts.Select(a => new { a.level, a.title, since = Utc(a.since) }),
                activity = activity.Select(a => new
                {
                    a.machine, type = a.Type, title = a.Title,
                    state = a.Status == JobStatus.Running ? "running" : a.Status == JobStatus.WaitingUser ? "waiting" : "queued",
                    progress = a.Progress, step = a.Step.Length > 0 ? a.Step : null,
                    since = Utc(a.Status == JobStatus.Running ? a.Started ?? a.Created : a.Created),
                }),
                updated = now,
            });
        }).RequireApiKey(ApiScopes.Read, "Status til et dashboard: maskiner, jobs, udrulninger, advarsler og det der kører lige nu");

        // SSO-opsætningen under Indstillinger → Adgang for programmer (kun administratorer). API-nøglerne: ApiKeyEndpoints.
        var adm = app.MapGroup("/api").RequireAuthorization("admin");
        adm.MapGet("/settings/kontrolrum", async (SettingsService s, HttpContext ctx, KontrolrumSso sso, AppDb db) =>
        {
            var baseUrl = (await s.Get("server_url")) is { Length: > 0 } u ? u.TrimEnd('/') : $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            var stored = await s.Get(KontrolrumSso.KeySetting);
            var ssoKey = sso.ReadKey(stored);
            // Sidst brugt: seneste login fra Kontrolrum i revisionsloggen (detaljen starter med klientens IP)
            var last = await db.Audit.AsNoTracking().Where(a => a.Action == "login.kontrolrum").OrderByDescending(a => a.Id)
                .Select(a => new { a.Ts, a.User, a.Detail }).FirstOrDefaultAsync();
            var made = ssoKey is null ? null : await db.Audit.AsNoTracking().Where(a => a.Action == "settings.kontrolrum_sso" && a.Detail != "slået fra")
                .OrderByDescending(a => a.Id).Select(a => a.User).FirstOrDefaultAsync();
            return new
            {
                endpoint = $"{baseUrl}/api/kontrolrum/status",
                // SSO-nøglen selv vises kun én gang, når den laves – her kun om den findes og dens fingeraftryk
                sso = new
                {
                    enabled = ssoKey is not null,
                    unreadable = ssoKey is null && stored.Length > 0,
                    fingerprint = ssoKey is null ? null : KontrolrumSso.Fingerprint(ssoKey),
                    created = (await s.Get(KontrolrumSso.CreatedSetting)) is { Length: > 0 } c ? c : null,
                    createdBy = made,
                    lastUsed = last is null ? null : new { at = Utc(last.Ts), user = last.User, ip = last.Detail.Split(" · ")[0] },
                    endpoint = $"{baseUrl}/api/auth/kontrolrum",
                    users = await db.Users.AsNoTracking().OrderBy(x => x.Username)
                        .Select(x => new { x.Id, x.Username, x.Role, twoFactor = x.TotpSecret != null, allowed = x.KontrolrumSso }).ToListAsync(),
                },
            };
        });
        // Single sign-on fra Kontrolrum: lav/skift nøglen (vises kun i dette svar), slå fra, og flueben pr. bruger
        adm.MapPost("/settings/kontrolrum-sso", async (SettingsService s, AuditService audit, ClaimsPrincipal u, KontrolrumSso sso) =>
        {
            var had = (await s.Get(KontrolrumSso.KeySetting)).Length > 0;
            var key = KontrolrumSso.NewKey();
            await s.Set(KontrolrumSso.KeySetting, sso.Protect(key));
            await s.Set(KontrolrumSso.CreatedSetting, DateTime.UtcNow.ToString("O"));
            await audit.Log(u.UserName(), "settings.kontrolrum_sso", $"{(had ? "ny nøgle" : "slået til")} · {KontrolrumSso.Fingerprint(key)}");
            return Results.Ok(new { key, fingerprint = KontrolrumSso.Fingerprint(key) });
        });
        adm.MapDelete("/settings/kontrolrum-sso", async (SettingsService s, AuditService audit, ClaimsPrincipal u) =>
        {
            await s.Set(KontrolrumSso.KeySetting, "");
            await s.Set(KontrolrumSso.CreatedSetting, "");
            await audit.Log(u.UserName(), "settings.kontrolrum_sso", "slået fra");
            return Results.Ok();
        });
        adm.MapPut("/settings/kontrolrum-sso/users/{id:int}", async (int id, KontrolrumSsoUser d, AppDb db, AuditService audit, ClaimsPrincipal u) =>
        {
            var x = await db.Users.FindAsync(id);
            if (x is null) return Results.NotFound();
            if (x.KontrolrumSso == d.Allowed) return Results.Ok();
            x.KontrolrumSso = d.Allowed;
            await db.SaveChangesAsync();
            await audit.Log(u.UserName(), "user.kontrolrum_sso", $"{x.Username}: {(d.Allowed ? "må logges ind fra Kontrolrum" : "må ikke logges ind fra Kontrolrum")}");
            return Results.Ok();
        });
    }
}
