using System.Security.Claims;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Hubs;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Server.Endpoints;

public sealed record ReserveDnsInput(int? MachineId, string? Network, string? Ip, string? Container, string? TunnelContainer, string? DataDir, string? ApiUrl);

/// <summary>Reserve-DNS: en ekstra AdGuard + tunnel-connector på en anden Docker-vært (Indstillinger → iPads).</summary>
public static class ReserveDnsEndpoints
{
    private static IResult Bad(string msg) => Results.Problem(msg, statusCode: 400);

    public static void MapReserveDnsEndpoints(this WebApplication app)
    {
        var adm = app.MapGroup("/api/reserve-dns").RequireAuthorization("admin");

        adm.MapGet("", async (ReserveDnsSettings rd, PublicAccessSettings pa, SettingsService s, AppDb db) =>
        {
            var c = await rd.Config();
            var poll = await s.GetNum("poll_seconds", 30);
            var machines = await db.Machines.AsNoTracking().Where(m => m.Platform == "linux").OrderBy(m => m.DisplayName ?? m.Name).ToListAsync();
            var adguard = await s.Get("adguard_url");
            var port = ReserveDns.PortOf(adguard);
            return Results.Ok(new
            {
                machineId = c.MachineId, network = c.Network, ip = c.Ip, container = c.Container, tunnelContainer = c.TunnelContainer, dataDir = c.DataDir, apiUrl = c.ApiUrl,
                enabled = await rd.Enabled(), image = ReserveDns.Image, tunnelImage = PublicAccess.Image, port, service = ReserveDns.Service(port), alias = ReserveDns.Alias,
                primaryHttps = adguard.StartsWith("https:", StringComparison.OrdinalIgnoreCase), hasToken = await pa.HasToken(), tunnelMachineId = (await pa.Config()).MachineId,
                machines = machines.Select(m => new { m.Id, Name = m.Label, online = MachineViews.Online(m, poll), unraid = Json.Des<HealthReport>(m.HealthJson).Unraid is not null, ip = m.Ip }),
            });
        });

        adm.MapPut("", async (ReserveDnsInput i, ReserveDnsSettings rd, AppDb db, AuditService audit, ClaimsPrincipal u, IpadEngine engine) =>
        {
            var c = await rd.Config();
            if (i.MachineId is { } mid)
            {
                if (mid <= 0) c = c with { MachineId = null };
                else if (await db.Machines.AsNoTracking().AnyAsync(m => m.Id == mid && m.Platform == "linux")) c = c with { MachineId = mid };
                else return Bad("Maskinen findes ikke eller er ikke en Linux/Unraid-maskine");
            }
            c = c with
            {
                Network = i.Network?.Trim() ?? c.Network,
                Ip = i.Ip?.Trim() ?? c.Ip,
                Container = i.Container?.Trim() ?? c.Container,
                TunnelContainer = i.TunnelContainer?.Trim() ?? c.TunnelContainer,
                DataDir = i.DataDir?.Trim() ?? c.DataDir,
                ApiUrl = i.ApiUrl?.Trim().TrimEnd('/') ?? c.ApiUrl,
            };
            if (ReserveDns.Validate(c) is { } err) return Bad(err);
            await rd.Save(c);
            if (c.MachineId is null) { await rd.SetEnabled(false); engine.Kick(); }
            await audit.Log(u.UserName(), "settings.reserve_dns",
                $"maskine {c.MachineId?.ToString() ?? "-"}, netværk {c.Network}{(c.Ip.Length > 0 ? $", IP {c.Ip}" : "")}, containere {c.Container}/{c.TunnelContainer}");
            return Results.Ok();
        });

        adm.MapPost("/start", async (ReserveDnsSettings rd, PublicAccessSettings pa, SettingsService s, AppDb db, JobService jobs, AuditService audit, ClaimsPrincipal u, IpadEngine engine) =>
        {
            var c = await rd.Config();
            if (await Host(db, c) is not { } m) return Bad("Vælg den Linux-maskine med Docker, reserven skal køre på, og gem");
            if (ReserveDns.Validate(c) is { } err) return Bad(err);
            if ((await pa.Config()).MachineId == m.Id) return Bad("Reserven skal køre på en anden maskine end tunnelen på hovedserveren – ellers hjælper den ikke, når den går ned");
            if (await pa.Token() is null) return Bad("Sæt først den offentlige adgang (Cloudflare-tunnelen) op – reserven bruger den samme tunnel-nøgle");
            if ((await s.Get("adguard_url")).Length == 0) return Bad("Sæt først forbindelsen til hoved-AdGuard op (iPads – AdGuard og krypteret DNS)");
            if (await db.Jobs.AnyAsync(j => j.MachineId == m.Id && j.Source == ReserveDns.Source && j.Status == JobStatus.Running && j.Title != ReserveDns.SuggestTitle))
                return Bad($"{m.Label} er i gang med reserven – vent til jobbet er færdigt");
            await rd.EnsurePassword();
            await CancelPending(db, m.Id);
            int jobId;
            var nonce = Crypto.Token();
            await using (var tx = await db.Database.BeginTransactionAsync())
            {
                var job = await jobs.Create(m.Id, JobTypes.Script, ReserveDns.StartTitle, ReserveDns.StartJob(c, nonce, await rd.Port()), RunAs.System, null, ReserveDns.Source, u.UserName(), save: false);
                await db.SaveChangesAsync();
                await rd.SetNonce(job.Id, nonce);
                await tx.CommitAsync();
                jobId = job.Id;
            }
            await jobs.Commit(m.Id);
            await rd.SetEnabled(true);
            engine.Kick();
            await audit.Log(u.UserName(), "reserve_dns.start", $"{m.Label}: {c.Container} + {c.TunnelContainer} på {c.Network}{(c.Ip.Length > 0 ? $" ({c.Ip})" : "")}");
            return Results.Ok(new { jobId });
        });

        adm.MapPost("/stop", async (ReserveDnsSettings rd, AppDb db, JobService jobs, AuditService audit, ClaimsPrincipal u, IpadEngine engine) =>
        {
            var c = await rd.Config();
            if (await Host(db, c) is not { } m) return Bad("Vælg den maskine, reserven kører på");
            if ((PublicAccess.ValidateContainer(c.Container) ?? PublicAccess.ValidateContainer(c.TunnelContainer)) is { } err) return Bad(err);
            await CancelPending(db, m.Id);
            await rd.ClearNonce();
            await rd.SetEnabled(false);
            engine.Kick();
            var j = await jobs.Create(m.Id, JobTypes.Script, ReserveDns.StopTitle, ReserveDns.StopJob(c), RunAs.System, null, ReserveDns.Source, u.UserName());
            await audit.Log(u.UserName(), "reserve_dns.stop", $"{m.Label}: {c.Container} + {c.TunnelContainer}");
            return Results.Ok(new { jobId = j.Id });
        });

        adm.MapPost("/suggest-ip", async (ReserveDnsSettings rd, AppDb db, JobService jobs, ClaimsPrincipal u) =>
        {
            var c = await rd.Config();
            if (await Host(db, c) is not { } m) return Bad("Vælg maskinen og gem først");
            if (PublicAccess.ValidateNetwork(c.Network) is { } err) return Bad(err);
            if (c.Network is "host" or "bridge") return Bad($"På \"{c.Network}\" bruges ingen fast IP – lad feltet være tomt");
            var j = await jobs.Create(m.Id, JobTypes.Script, ReserveDns.SuggestTitle, PublicAccess.SuggestJob(new TunnelConfig(m.Id, c.Network, "", c.Container)),
                RunAs.System, null, ReserveDns.Source, u.UserName());
            return Results.Ok(new { jobId = j.Id });
        });

        // Synkronisér nu (ellers sker det af sig selv hvert halve minut)
        adm.MapPost("/sync", (IpadEngine engine) => { engine.Kick(); return Results.Ok(); });

        // Status: seneste job, containerne (som agenten melder dem – Unraid) og reservens tilstand set fra Fleetdeck
        adm.MapGet("/status", async (ReserveDnsSettings rd, PublicAccessSettings pa, AppDb db, IpadEngine engine, CancellationToken ct) =>
        {
            var c = await rd.Config();
            var token = await pa.Token();
            var host = c.MachineId is int mid ? await db.Machines.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mid, ct) : null;
            object? job = null, suggest = null;
            List<object> containers = [];
            if (host is not null)
            {
                var recent = await db.Jobs.AsNoTracking().Where(j => j.MachineId == host.Id && j.Source == ReserveDns.Source)
                    .OrderByDescending(j => j.Id).Take(20).ToListAsync(ct);
                if (recent.FirstOrDefault(j => j.Title != ReserveDns.SuggestTitle) is { } lj)
                    job = new
                    {
                        lj.Id, lj.Title, lj.Status, lj.Created, lj.Finished, lj.Step,
                        output = ReserveDns.Mask(PublicAccess.Mask(lj.Output.Length > 3000 ? lj.Output[^3000..] : lj.Output, token)),
                        stop = lj.Title == ReserveDns.StopTitle,
                    };
                if (recent.FirstOrDefault(j => j.Title == ReserveDns.SuggestTitle) is { } sj)
                    suggest = new { sj.Id, sj.Status, ip = PublicAccess.SuggestedIp(sj.Output) };
                if (Json.Des<HealthReport>(host.HealthJson).Unraid is { } ur)
                    foreach (var (name, role) in new[] { (c.Container, "AdGuard"), (c.TunnelContainer, "Tunnel-connector") })
                    {
                        var ctr = ur.Containers.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                        containers.Add(new { name, role, found = ctr is not null, running = ctr?.State == "running", state = ctr?.State, ip = ctr?.Ip, at = host.LastHealth });
                    }
            }
            var h = ReserveDnsStatus.Current;
            var enabled = await rd.Enabled();
            return Results.Ok(new
            {
                machine = host is null ? null : new { host.Id, Name = host.Label },
                enabled, job, suggest, containers, apiUrl = ReserveDnsSettings.ApiUrl(c, host?.Ip, await rd.Port()),
                health = enabled && h is not null && h.MachineId == c.MachineId ? new
                {
                    h.Up, h.LastAttempt, h.LastOk, h.DownSince, h.Error, h.LastSync, h.SyncError, h.LastChanges, h.LastChange, h.PrimaryOk, h.Version,
                    alarm = ReserveDnsStatus.Alarm(h, DateTime.UtcNow) is not null,
                } : null,
                primaryError = engine.LastError,
            });
        });

        // Start-jobbet henter tunnel-nøglen og AdGuards opsætningsfil: kun den valgte maskine, kun med engangskoden, og kun mens jobbet kører
        app.MapGet(ReserveDns.SecretsPath, async (string? nonce, HttpContext ctx, AppDb db, ReserveDnsSettings rd, PublicAccessSettings pa, AuditService audit) =>
        {
            var h = ctx.Request.Headers.Authorization.ToString();
            if (!h.StartsWith("Bearer ", StringComparison.Ordinal) || await AgentAuth.Resolve(db, h[7..].Trim()) is not { } m) return Results.Unauthorized();
            ctx.Response.Headers.CacheControl = "no-store";
            var c = await rd.Config();
            if (c.MachineId != m.Id) return Results.Text("Reserve-DNS er ikke sat op til at køre på denne maskine", statusCode: 403);
            if (await rd.TakeNonce(nonce) is not int jobId
                || !await db.Jobs.AnyAsync(j => j.Id == jobId && j.MachineId == m.Id && j.Source == ReserveDns.Source && j.Title == ReserveDns.StartTitle
                    && (j.Status == JobStatus.Running || j.Status == JobStatus.Pending)))
            {
                await audit.Log($"agent:{m.Label}", "reserve_dns.secrets_denied", "ugyldig eller brugt engangskode");
                return Results.Text("Engangskoden er ugyldig eller allerede brugt – start reserven igen fra Fleetdeck", statusCode: 403);
            }
            if (await pa.Token() is not { } token) return Results.Text("Der er ingen tunnel-nøgle gemt i Fleetdeck", statusCode: 404);
            if (await rd.Password() is not { } pw) return Results.Text("Reservens adgangskode mangler – start reserven igen fra Fleetdeck", statusCode: 404);
            var yaml = ReserveDns.BootstrapYaml(await rd.Port(), ReserveDnsSettings.Hash(pw), c.Network == "host" ? m.Ip : null);
            await audit.Log($"agent:{m.Label}", "reserve_dns.secrets", $"tunnel-nøgle og AdGuard-opsætning er hentet af job {jobId}");
            return Results.Text(ReserveDnsSettings.Secrets(token, yaml), "text/plain");
        });
    }

    private static async Task<Machine?> Host(AppDb db, ReserveDnsConfig c) =>
        c.MachineId is int mid ? await db.Machines.FirstOrDefaultAsync(m => m.Id == mid && m.Platform == "linux") : null;

    /// <summary>Ventende reserve-jobs (fx til en slukket maskine) erstattes af det nye.</summary>
    private static Task<int> CancelPending(AppDb db, int machineId) =>
        db.Jobs.Where(j => j.MachineId == machineId && j.Source == ReserveDns.Source && j.Status == JobStatus.Pending && j.Title != ReserveDns.SuggestTitle)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Cancelled).SetProperty(j => j.Finished, DateTime.UtcNow));
}
