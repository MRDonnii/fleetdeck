using System.Security.Claims;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Hubs;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Server.Endpoints;

public sealed record PublicAccessInput(string? Token, int? MachineId, string? Network, string? Ip, string? Container);

/// <summary>Offentlig adgang: Cloudflare-tunnel sat op fra Fleetdeck (Indstillinger → iPads).</summary>
public static class PublicAccessEndpoints
{
    private static IResult Bad(string msg) => Results.Problem(msg, statusCode: 400);

    public static void MapPublicAccessEndpoints(this WebApplication app)
    {
        var adm = app.MapGroup("/api/public-access").RequireAuthorization("admin");

        adm.MapGet("", async (PublicAccessSettings pa, FamilySettings fs, SettingsService s, ReserveDnsSettings rd, AppDb db) =>
        {
            var c = await pa.Config();
            var poll = await s.GetNum("poll_seconds", 30);
            var machines = await db.Machines.AsNoTracking().Where(m => m.Platform == "linux").OrderBy(m => m.DisplayName ?? m.Name).ToListAsync();
            var adguard = await s.Get("adguard_url");
            return Results.Ok(new
            {
                hasToken = await pa.HasToken(), machineId = c.MachineId, network = c.Network, ip = c.Ip, container = c.Container, image = PublicAccess.Image,
                dohHost = await fs.DohHost(),
                // det, tunnelen skal sende videre til i Cloudflare: AdGuards egen adresse (den, Fleetdeck også bruger)
                service = Uri.TryCreate(adguard, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? $"{u.Scheme}://{u.Host}:{u.Port}" : null,
                // med Reserve-DNS: begge connectorer sender til navnet fleetdeck-dns, som hver vært peger på sin egen AdGuard
                reserve = (await rd.Config()).MachineId is not null, reserveService = ReserveDns.Service(await rd.Port()),
                machines = machines.Select(m => new { m.Id, Name = m.Label, online = MachineViews.Online(m, poll), unraid = Json.Des<HealthReport>(m.HealthJson).Unraid is not null }),
            });
        });

        adm.MapPut("", async (PublicAccessInput i, PublicAccessSettings pa, AppDb db, AuditService audit, ClaimsPrincipal u) =>
        {
            var c = await pa.Config();
            string? token = null;
            if (i.Token?.Trim() is { Length: > 0 } t && (token = PublicAccess.CleanToken(t)) is null)
                return Bad("Tunnel-nøglen ser ikke rigtig ud. Kopiér den lange nøgle fra Cloudflare (den starter typisk med eyJ) – hele kommandoen kan også indsættes.");
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
            };
            if (PublicAccess.Validate(c) is { } err) return Bad(err);
            await pa.Save(c);
            if (token is not null) await pa.SetToken(token);
            await audit.Log(u.UserName(), "settings.public_access",
                $"maskine {c.MachineId?.ToString() ?? "-"}, netværk {c.Network}{(c.Ip.Length > 0 ? $", IP {c.Ip}" : "")}, container {c.Container}{(token is not null ? ", ny tunnel-nøgle" : "")}");
            return Results.Ok();
        });

        // Kør tunnelen i selve Fleetdeck-containeren (cloudflared som underproces) i stedet for i en container på en anden vært
        adm.MapPut("/inside", async (InsideInput i, SettingsService s, PublicAccessSettings pa, AuditService audit, ClaimsPrincipal u) =>
        {
            if (i.On && !TunnelInside.Available) return Bad("Denne Fleetdeck-container har ikke cloudflared. Opdatér Fleetdeck først.");
            if (i.On && await pa.Token() is null) return Bad("Indsæt tunnel-nøglen fra Cloudflare og gem først");
            await s.Set(TunnelInside.Setting, i.On ? "1" : "0");
            await audit.Log(u.UserName(), "settings.public_access", i.On ? "tunnelen kører i Fleetdeck" : "tunnelen kører ikke længere i Fleetdeck");
            return Results.Ok();
        });
        adm.MapGet("/inside", async (SettingsService s, TunnelInside t) => Results.Ok(new { on = await s.Get(TunnelInside.Setting) == "1", status = t.Status() }));

        adm.MapPost("/start", async (PublicAccessSettings pa, SettingsService s, AppDb db, JobService jobs, AuditService audit, ClaimsPrincipal u) =>
        {
            var c = await pa.Config();
            if (!await pa.HasToken() || await pa.Token() is null) return Bad("Indsæt tunnel-nøglen fra Cloudflare og gem først");
            if (await Host(db, c) is not { } m) return Bad("Vælg den Linux/Unraid-maskine, tunnelen skal køre på, og gem");
            if (PublicAccess.Validate(c) is { } err) return Bad(err);
            if (await db.Jobs.AnyAsync(j => j.MachineId == m.Id && j.Source == PublicAccess.Source && j.Status == JobStatus.Running && j.Title != PublicAccess.SuggestTitle))
                return Bad($"{m.Label} er i gang med tunnelen – vent til jobbet er færdigt");
            await CancelPending(db, m.Id);
            int jobId;
            var nonce = Crypto.Token();
            // jobbet og engangskoden gemmes samlet, så agenten aldrig ser jobbet før koden findes
            await using (var tx = await db.Database.BeginTransactionAsync())
            {
                var job = await jobs.Create(m.Id, JobTypes.Script, PublicAccess.StartTitle, PublicAccess.StartJob(c, nonce, ReserveDns.OriginHost(await s.Get("adguard_url"))), RunAs.System, null, PublicAccess.Source, u.UserName(), save: false);
                await db.SaveChangesAsync();
                await pa.SetNonce(job.Id, nonce);
                await tx.CommitAsync();
                jobId = job.Id;
            }
            await jobs.Commit(m.Id);
            await audit.Log(u.UserName(), "public_access.start", $"{m.Label}: {c.Container} på {c.Network}{(c.Ip.Length > 0 ? $" ({c.Ip})" : "")}");
            return Results.Ok(new { jobId });
        });

        adm.MapPost("/stop", async (PublicAccessSettings pa, AppDb db, JobService jobs, AuditService audit, ClaimsPrincipal u) =>
        {
            var c = await pa.Config();
            if (await Host(db, c) is not { } m) return Bad("Vælg den maskine, tunnelen kører på");
            if (PublicAccess.ValidateContainer(c.Container) is { } err) return Bad(err);
            await CancelPending(db, m.Id);
            await pa.ClearNonce();
            var j = await jobs.Create(m.Id, JobTypes.Script, PublicAccess.StopTitle, PublicAccess.StopJob(c), RunAs.System, null, PublicAccess.Source, u.UserName());
            await audit.Log(u.UserName(), "public_access.stop", $"{m.Label}: {c.Container}");
            return Results.Ok(new { jobId = j.Id });
        });

        // Forslag til en ledig fast IP på Docker-netværket (maskinen pinger kandidater)
        adm.MapPost("/suggest-ip", async (PublicAccessSettings pa, AppDb db, JobService jobs, ClaimsPrincipal u) =>
        {
            var c = await pa.Config();
            if (await Host(db, c) is not { } m) return Bad("Vælg maskinen og gem først");
            if (PublicAccess.ValidateNetwork(c.Network) is { } err) return Bad(err);
            if (c.Network is "host" or "bridge") return Bad($"På \"{c.Network}\" bruges ingen fast IP – lad feltet være tomt");
            var j = await jobs.Create(m.Id, JobTypes.Script, PublicAccess.SuggestTitle, PublicAccess.SuggestJob(c), RunAs.System, null, PublicAccess.Source, u.UserName());
            return Results.Ok(new { jobId = j.Id });
        });

        // Status: seneste tunnel-job, containeren (som agenten melder den – Unraid) og om krypteret DNS svarer udefra
        adm.MapGet("/status", async (bool? probe, PublicAccessSettings pa, FamilySettings fs, AppDb db, CancellationToken ct) =>
        {
            var c = await pa.Config();
            var token = await pa.Token();
            var host = c.MachineId is int mid ? await db.Machines.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mid, ct) : null;
            object? job = null, suggest = null, container = null;
            if (host is not null)
            {
                var recent = await db.Jobs.AsNoTracking().Where(j => j.MachineId == host.Id && j.Source == PublicAccess.Source)
                    .OrderByDescending(j => j.Id).Take(20).ToListAsync(ct);
                if (recent.FirstOrDefault(j => j.Title != PublicAccess.SuggestTitle) is { } lj)
                    job = new
                    {
                        lj.Id, lj.Title, lj.Status, lj.Created, lj.Finished, lj.Step,
                        output = PublicAccess.Mask(lj.Output.Length > 3000 ? lj.Output[^3000..] : lj.Output, token),
                        stop = lj.Title == PublicAccess.StopTitle,
                    };
                if (recent.FirstOrDefault(j => j.Title == PublicAccess.SuggestTitle) is { } sj)
                    suggest = new { sj.Id, sj.Status, ip = PublicAccess.SuggestedIp(sj.Output), output = PublicAccess.Mask(sj.Output.Length > 600 ? sj.Output[^600..] : sj.Output, token) };
                if (Json.Des<HealthReport>(host.HealthJson).Unraid is { } ur)
                {
                    var ctr = ur.Containers.FirstOrDefault(x => x.Name.Equals(c.Container, StringComparison.OrdinalIgnoreCase));
                    container = new { reported = true, found = ctr is not null, running = ctr?.State == "running", state = ctr?.State, ip = ctr?.Ip, network = ctr?.Network, started = ctr?.Started, at = host.LastHealth };
                }
            }
            object? doh = null;
            if (probe != false)
            {
                var (ok, text) = await DohProbe.Run(await fs.DohHost(), ct);
                doh = new { ok, text };
            }
            return Results.Ok(new { machine = host is null ? null : new { host.Id, Name = host.Label }, job, suggest, container, doh });
        });

        // Agenten (tunnel-jobbet) henter nøglen: kun den valgte maskine, kun med engangskoden, og kun mens start-jobbet kører
        app.MapGet(PublicAccess.TokenPath, async (string? nonce, HttpContext ctx, AppDb db, PublicAccessSettings pa, AuditService audit) =>
        {
            var h = ctx.Request.Headers.Authorization.ToString();
            if (!h.StartsWith("Bearer ", StringComparison.Ordinal) || await AgentAuth.Resolve(db, h[7..].Trim()) is not { } m) return Results.Unauthorized();
            ctx.Response.Headers.CacheControl = "no-store";
            var c = await pa.Config();
            if (c.MachineId != m.Id) return Results.Text("Tunnelen er ikke sat op til at køre på denne maskine", statusCode: 403);
            if (await pa.TakeNonce(nonce) is not int jobId
                || !await db.Jobs.AnyAsync(j => j.Id == jobId && j.MachineId == m.Id && j.Source == PublicAccess.Source && j.Title == PublicAccess.StartTitle
                    && (j.Status == JobStatus.Running || j.Status == JobStatus.Pending)))
            {
                await audit.Log($"agent:{m.Label}", "public_access.token_denied", "ugyldig eller brugt engangskode");
                return Results.Text("Engangskoden er ugyldig eller allerede brugt – start tunnelen igen fra Fleetdeck", statusCode: 403);
            }
            if (await pa.Token() is not { } token) return Results.Text("Der er ingen tunnel-nøgle gemt i Fleetdeck", statusCode: 404);
            await audit.Log($"agent:{m.Label}", "public_access.token", $"tunnel-nøglen er hentet af job {jobId}");
            return Results.Text(token, "text/plain");
        });
    }

    private static async Task<Machine?> Host(AppDb db, TunnelConfig c) =>
        c.MachineId is int mid ? await db.Machines.FirstOrDefaultAsync(m => m.Id == mid && m.Platform == "linux") : null;

    /// <summary>Ventende tunnel-jobs (fx til en slukket maskine) erstattes af det nye.</summary>
    private static Task<int> CancelPending(AppDb db, int machineId) =>
        db.Jobs.Where(j => j.MachineId == machineId && j.Source == PublicAccess.Source && j.Status == JobStatus.Pending && j.Title != PublicAccess.SuggestTitle)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Cancelled).SetProperty(j => j.Finished, DateTime.UtcNow));
}

public sealed record InsideInput(bool On);
