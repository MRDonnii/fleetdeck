using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services;

namespace Fleetdeck.Server.Endpoints;

public static class DeployEndpoints
{
    public static void MapDeployEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization("user");
        var adm = app.MapGroup("/api").RequireAuthorization("admin");

        // -------------------------------------------------------------- udrulning (webgrænsefladen)
        api.MapGet("/deploy", async (AppDb db, OsLibrary os, SettingsService s) =>
        {
            var profiles = await db.Profiles.AsNoTracking().OrderBy(p => p.Name).Select(p => new { p.Id, p.Name }).ToListAsync();
            var names = profiles.ToDictionary(p => p.Id, p => p.Name);
            var list = await db.Deployments.AsNoTracking().OrderByDescending(d => d.Id).Take(50).ToListAsync();
            return new
            {
                deployments = list.Select(d => new
                {
                    d.Id, d.Mac, d.Os, d.Series, d.Variant, d.Mode, d.ProfileId, profile = d.ProfileId is int p ? names.GetValueOrDefault(p) : null,
                    d.PcName, d.UserName, d.FullName, d.Packages, d.AutoDisk, d.MachineId, d.Status, d.Created, d.CreatedBy, d.Started, d.Finished, d.Notes,
                    osId = DeployService.OsIdOf(d), title = DeployService.Describe(d), d.AskFields, d.Encrypt, admin = !d.NoAdmin, hasSshKey = d.SshKey.Length > 0,
                }),
                untested = await s.Get(DeployService.KUntested) == "1",
                os = os.Status(),
                profiles,
                defaultProfileId = await s.DefaultProfileId(),
                defaultUser = await s.Get("pxe_default_user"),
                machines = await db.Machines.AsNoTracking().OrderBy(m => m.DisplayName ?? m.Name)
                    .Select(m => new { m.Id, m.Name, label = m.DisplayName ?? m.Name, m.Mac, m.User, m.ProfileId, m.LastSeen }).ToListAsync(),
            };
        });

        adm.MapPost("/deploy", async (DeployRequest r, DeployService deploy, AuditService audit, Live live, ClaimsPrincipal u) =>
        {
            var (d, err) = await deploy.Create(r, u.UserName());
            if (d is null) return Results.Problem(err, statusCode: 400);
            await audit.Log(u.UserName(), "deploy.create", $"{d.Os} {d.Series} {d.Mode} {d.PcName} ({d.Mac})");
            await live.Changed("deploy");
            return Results.Ok(new { d.Id });
        });

        adm.MapPost("/deploy/{id:int}/{action}", async (int id, string action, AppDb db, DeployService deploy, AuditService audit, Live live, ClaimsPrincipal u) =>
        {
            var d = await db.Deployments.FindAsync(id);
            if (d is null) return Results.NotFound();
            switch (action)
            {
                case "cancel" when DeployStatus.Active.Contains(d.Status):
                    d.Status = DeployStatus.Cancelled; d.Finished = DateTime.UtcNow; break;
                case "again":   // prøv igen: læg den klar til næste netværksboot
                    await db.Deployments.Where(x => x.Mac == d.Mac && x.Id != d.Id && DeployStatus.Active.Contains(x.Status))
                        .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, DeployStatus.Cancelled));
                    d.Status = DeployStatus.Ready; d.Started = null; d.Finished = null; d.Created = DateTime.UtcNow; break;
                case "delete" when !DeployStatus.Active.Contains(d.Status):
                    db.Deployments.Remove(d); break;
                default: return Results.Problem("Kan ikke lade sig gøre", statusCode: 400);
            }
            await db.SaveChangesAsync();
            await deploy.Sync();
            await audit.Log(u.UserName(), $"deploy.{action}", $"{d.PcName} ({d.Mac})");
            await live.Changed("deploy");
            return Results.Ok();
        });

        // Afprøvning: styresystemer, der ikke er afprøvet til ende endnu, kan vælges (kun til test i en VM)
        adm.MapPut("/deploy/untested", async (UntestedRequest r, SettingsService s, AuditService audit, Live live, ClaimsPrincipal u) =>
        {
            await s.Set(DeployService.KUntested, r.On ? "1" : "");
            await audit.Log(u.UserName(), "deploy.untested", r.On ? "til" : "fra");
            await live.Changed("deploy");
            return Results.Ok();
        });

        adm.MapPost("/os/{id}/download", async (string id, OsLibrary os, AuditService audit, ClaimsPrincipal u) =>
        {
            if (!os.Start(id)) return Results.Problem("Hentningen kører allerede, eller det kan ikke hentes", statusCode: 400);
            await audit.Log(u.UserName(), "os.download", id);
            return Results.Ok();
        });

        adm.MapDelete("/os/{id}", async (string id, OsLibrary os, AuditService audit, ClaimsPrincipal u) =>
        {
            os.Remove(id);
            await audit.Log(u.UserName(), "os.delete", id);
            return Results.Ok();
        });

        // Programkataloget (de populære gratis programmer med beskrivelse) – tilføjer kun dem der mangler
        adm.MapPost("/packages/catalog", async (AppDb db, AuditService audit, ClaimsPrincipal u) =>
            Results.Ok(new { added = await AppCatalog.Import(db, audit, u.UserName()) }));

        // -------------------------------------------------------------- netværksinstallationen (uden login)
        // Ubuntu-installeren henter sin opsætning her. Kun en pc med en aktiv udrulning får svar.
        app.MapGet("/pxe/ubuntu/{mac}/{file}", async (string mac, string file, DeployService deploy) =>
        {
            var d = await deploy.ActiveUbuntu(mac);
            if (d is null) return Results.NotFound();
            switch (file)
            {
                case "meta-data":
                    return Results.Text($"instance-id: fleetdeck-{d.Id}-{d.Created:yyyyMMddHHmmss}\nlocal-hostname: {d.PcName}\n", "text/plain");
                case "user-data":
                    await deploy.MarkStarted(d);
                    return Results.Text(await deploy.UserData(d), "text/plain");
                case "vendor-data":
                case "network-config":
                    return Results.Text("", "text/plain");
                default: return Results.NotFound();
            }
        });

        app.MapPost("/pxe/ubuntu/{mac}/done", async (string mac, DeployService deploy, AuditService audit, Live live, Notifier notifier) =>
        {
            var d = await deploy.ActiveUbuntu(mac);
            if (d is null) return Results.NotFound();
            await deploy.MarkDone(d);
            await audit.Log("pxe", "deploy.done", $"Ubuntu {d.Series} på {d.PcName} ({d.Mac})");
            notifier.Send($"🐧 {d.PcName}: Ubuntu {d.Series} er installeret");
            await live.Changed("deploy");
            return Results.Ok();
        });

        // Debian (preseed.cfg) og Fedora/Rocky/Alma (ks.cfg) henter deres svarfil her. Kun en pc med en aktiv udrulning får svar.
        app.MapGet("/pxe/linux/{mac}/{file}", async (string mac, string file, DeployService deploy) =>
        {
            var d = await deploy.ActiveLinux(mac);
            if (d is null || d.Os == DeployOs.Ubuntu) return Results.NotFound();
            var text = await deploy.LinuxAnswer(d, file);
            if (text is null) return Results.NotFound();
            await deploy.MarkStarted(d);
            return Results.Text(text, "text/plain");
        });

        app.MapPost("/pxe/linux/{mac}/done", async (string mac, DeployService deploy, AuditService audit, Live live, Notifier notifier) =>
        {
            var d = await deploy.ActiveLinux(mac);
            if (d is null || d.Os == DeployOs.Ubuntu) return Results.NotFound();
            await deploy.MarkDone(d);
            var what = DeployService.Describe(d);
            await audit.Log("pxe", "deploy.done", $"{what} på {d.PcName} ({d.Mac})");
            notifier.Send($"🐧 {(d.PcName.Length > 0 ? d.PcName : d.Mac)}: {what} er installeret");
            await live.Changed("deploy");
            return Results.Ok();
        });

        // ISO'en til Ubuntu-installeren (stor fil, understøtter Range)
        app.MapGet("/pxe/files/{id}/{file}", (string id, string file, OsLibrary os) =>
        {
            var info = os.Ready(id);
            if (info is null || file != info.Iso) return Results.NotFound();
            return Results.File(Path.Combine(os.OsDir(id), info.Iso), "application/octet-stream", enableRangeProcessing: true);
        });
    }
}

public sealed record UntestedRequest(bool On);
