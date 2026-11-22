using System.Security.Claims;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Hubs;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Server.Endpoints;

/// <param name="Route">"mac" (Apple Configurator på en Mac) eller "usb" (en pc med udvidelsen "iPad via USB").</param>
/// <param name="Store">Lageret for det udtrykkeligt valgte sæt ("mac"/"usb"); tom = agenten vælger efter reglerne.</param>
public sealed record NoWipeInput(string Route, int MachineId, string? Udid = null, string? Method = null, bool? FreshBackup = null,
    string? Store = null, string? Set = null, string? Confirm = null);
public sealed record NoWipeChosenInput(string Store, string Set);

/// <summary>"Overvåg uden datatab" (Opsætning, kun administrator): backupperne, vejene (Mac/pc) og jobbet – og agentens hentning af det valgte sæt.</summary>
public static class IpadNoWipeEndpoints
{
    public const int TimeoutMin = 235;   // serveren opgiver kørende jobs efter 4 timer

    public static void MapIpadNoWipeEndpoints(this WebApplication app)
    {
        var adm = app.MapGroup("/api").RequireAuthorization("admin");

        adm.MapGet("/ipads/{id:int}/nowipe", async (int id, ClaimsPrincipal u, AppDb db, Paths paths, UsbIpadStore store, SettingsService settings) =>
        {
            var sc = await FamilyScope.Of(u, db);
            var p = await db.Ipads.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (p is null || !sc.Sees(id)) return Results.NotFound();
            if (!sc.Admin || !sc.Can(FamilyPerms.Erase)) return Results.Problem("Kun en administrator kan overvåge en iPad", statusCode: 403);
            var poll = await settings.GetNum("poll_seconds", 30);
            var backups = NoWipeBackups.Candidates(paths, id).Select(c => new
            {
                c.Store, c.Set, c.Created, c.Bytes, c.Parts, c.Encrypted, c.Verified, c.SourceEmpty, c.DeviceName, c.Pinned,
                // reglerne uden iPad'ens forbrug (det kendes først på agenten, som også sammenligner med den brugte plads)
                ineligible = NoWipeSafety.Ineligible(c, null),
            });
            var macs = (await db.Machines.AsNoTracking().Where(m => m.Platform == Platforms.Mac).ToListAsync())
                .Select(m => new { m.Id, name = m.Label, online = MachineViews.Online(m, poll), seen = p.Ecid is { Length: > 0 } e && IosInventory.Devices(m).Any(d => Cfgutil.NormEcid(d.Ecid) == e) });
            var usbIds = store.All().Select(x => x.MachineId).ToList();
            var usbMachines = await db.Machines.AsNoTracking().Where(m => usbIds.Contains(m.Id)).ToListAsync();
            var usb = store.All().SelectMany(x => x.Report.Devices.Where(d => p.Ecid is { Length: > 0 } e && Cfgutil.NormEcid(d.Ecid) == e)
                    .Select(d => (x.MachineId, d)))
                .Select(x => usbMachines.FirstOrDefault(m => m.Id == x.MachineId) is { } m
                    ? new { machineId = m.Id, name = m.Label, platform = m.Platform, online = MachineViews.Online(m, poll), udid = x.d.Udid, deviceName = x.d.Name, supervised = x.d.Supervised, firmware = x.d.Firmware }
                    : null)
                .Where(x => x is not null).ToList();
            var jobs = await db.Jobs.AsNoTracking().Where(j => j.Type == JobTypes.Ios || j.Type == JobTypes.UsbIpad).OrderByDescending(j => j.Id).Take(100).ToListAsync();
            var job = jobs.Select(j => (j, prm: Json.Des<JobParams>(j.ParamsJson))).FirstOrDefault(x => x.prm.Action == IosActions.SuperviseNoWipe && x.prm.IpadId == id);
            object? jobRow = job.j is null ? null : new
            {
                job.j.Id, job.j.Title, job.j.Status, job.j.Created, job.j.Finished, job.j.Step, job.j.Progress, job.j.StepSince,
                method = NoWipeMethods.Norm(job.prm.Method), freshBackup = job.prm.FreshBackup == true, route = job.j.Type == JobTypes.Ios ? "mac" : "usb",
                output = job.j.Output.Length > 6000 ? job.j.Output[^6000..] : job.j.Output,
            };
            return Results.Ok(new { name = p.Name, linked = p.Ecid is { Length: > 0 }, backups, macs, usb, job = jobRow });
        });

        adm.MapPost("/ipads/{id:int}/nowipe", async (int id, NoWipeInput i, ClaimsPrincipal u, AppDb db, Paths paths, UsbIpadStore store, JobService jobs,
            LockedProfiles lp, IpadSupervision sv, AuditService audit, SettingsService settings) =>
        {
            var sc = await FamilyScope.Of(u, db);
            var p = await db.Ipads.FindAsync(id);
            if (p is null || !sc.Sees(id)) return Results.NotFound();
            if (!sc.Admin || !sc.Can(FamilyPerms.Erase)) return Results.Problem("Kun en administrator kan slette og overvåge en iPad", statusCode: 403);
            if (!IpadEndpoints.ConfirmMatches(i.Confirm, p.Name))
                return Results.Problem($"Skriv iPad'ens navn (\"{p.Name}\") for at bekræfte – iPad'en bliver slettet", statusCode: 400);
            if (p.Ecid is not { Length: > 0 } ecid) return Results.Problem("Link først iPad'en til en tilsluttet enhed", statusCode: 400);
            if (i.Method is { Length: > 0 } mm && !NoWipeMethods.All.Contains(mm)) return Results.Problem("Ukendt metode", statusCode: 400);
            var method = NoWipeMethods.Norm(i.Method);
            var candidates = NoWipeBackups.Candidates(paths, id);
            NoWipeCandidate? chosen = null;
            if (i.Set is { Length: > 0 } set)
            {
                chosen = candidates.FirstOrDefault(c => c.Set == set && (i.Store is not { Length: > 0 } st || c.Store == st));
                if (chosen is null) return Results.Problem("Den valgte backup findes ikke i Fleetdeck", statusCode: 404);
            }
            if (candidates.Count == 0 && i.FreshBackup != true)
                return Results.Problem("Der er ingen backup af iPad'en i Fleetdeck. Sæt flueben i \"Tag en ny backup først\".", statusCode: 400);
            var m = await db.Machines.FindAsync(i.MachineId);
            if (m is null) return Results.Problem("Maskinen findes ikke", statusCode: 404);
            var prm = new JobParams
            {
                Action = IosActions.SuperviseNoWipe, Ecid = ecid, IpadId = p.Id, TimeoutMin = TimeoutMin, Method = method, FreshBackup = i.FreshBackup == true,
                BackupStore = chosen?.Store, BackupSet = chosen?.Set, Candidates = candidates,
                ProfileIdentifier = LockedProfile.Identifier(p), ReplaceIds = [LockedProfile.DnsLinkIdentifier(p)], 
            };
            (prm.Language, prm.Region) = (await InstallLocale.Load(settings)).Apple;   // Indstillinger → Netværksinstallationer
            if (await lp.Sync(p)) await db.SaveChangesAsync();
            try { await sv.Identity(); await sv.BackupPassword(); }   // laves nu, ikke midt i jobbet
            catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: 500); }
            var title = $"iPad: overvåg {p.Name} uden datatab ({NoWipeMethods.Title(method)})";
            Job j;
            if (i.Route == "mac")
            {
                if (m.Platform != Platforms.Mac) return Results.Problem("Vælg en Mac med Fleetdeck-agenten", statusCode: 400);
                var active = (await db.Jobs.Where(x => x.MachineId == m.Id && x.Type == JobTypes.Ios && JobStatus.Active.Contains(x.Status)).Select(x => x.ParamsJson).ToListAsync())
                    .Select(Json.Des<JobParams>);
                if (active.Any(x => x.Ecid == ecid)) return Results.Problem("Der kører allerede et job på iPad'en – vent, til det er færdigt", statusCode: 409);
                var nonce = IpadSupervision.NewNonceValue();
                prm.SecretNonce = nonce;
                j = await jobs.Create(m.Id, JobTypes.Ios, title, prm, RunAs.System, null, "manual", u.UserName(), save: false);
                await db.SaveChangesAsync();
                await sv.StoreNonce(j.Id, nonce);
                await jobs.Commit(m.Id);
                await sv.PruneNonces();
            }
            else if (i.Route == "usb")
            {
                if (store.Device(m.Id, i.Udid) is not { } dev || Cfgutil.NormEcid(dev.Ecid) != ecid)
                    return Results.Problem($"iPad'en er ikke sat i {m.Label} lige nu", statusCode: 409);
                if (await db.Jobs.AnyAsync(x => x.MachineId == m.Id && x.Type == JobTypes.UsbIpad && JobStatus.Active.Contains(x.Status)))
                    return Results.Problem($"Der kører allerede et iPad-job på {m.Label}", statusCode: 409);
                prm.Udid = dev.Udid; prm.Ticket = Crypto.Token(24); prm.Org = await sv.Organization();
                await sv.Keybag();
                j = await jobs.Create(m.Id, JobTypes.UsbIpad, title, prm, RunAs.System, null, "manual", u.UserName());
            }
            else return Results.Problem("Vælg Mac eller pc", statusCode: 400);
            if (chosen is not null) NoWipeBackups.Pin(paths, id, chosen.Store, chosen.Set);
            await audit.Log(u.UserName(), "ipad.nowipe", $"{p.Name}: {method}{(i.FreshBackup == true ? " + ny backup" : "")}{(chosen is null ? "" : $", backup {chosen.Set}")} på {m.Label}");
            return Results.Ok(new { jobId = j.Id, msg = $"Sendt til {m.Label}" });
        });

        // ------------------------------------------------------------------ agenten
        // En del af det sæt, jobbet må bruge (fra Mac- eller USB-lageret)
        app.MapGet("/api/agent/nowipe/{jobId:int}/backup", async (int jobId, string store, string set, int part, HttpContext ctx, AppDb db, Paths paths) =>
        {
            if (await Running(ctx, db, jobId) is not { } prm || prm.IpadId is not int iid) return Results.NotFound();
            if (!(prm.Candidates ?? []).Any(c => c.Store == store && c.Set == set) && !NoWipeBackups.Candidates(paths, iid).Any(c => c.Store == store && c.Set == set))
                return Results.NotFound();
            var f = NoWipeBackups.PartFile(paths, iid, store, set, part);
            return f is not null && File.Exists(f) ? Results.File(f, "application/octet-stream", $"{set}.p{part:000}") : Results.NotFound();
        });

        // Agenten melder, hvilket sæt den bruger: det fastholdes (ryddes aldrig op)
        app.MapPost("/api/agent/nowipe/{jobId:int}/chosen", async (int jobId, NoWipeChosenInput i, HttpContext ctx, AppDb db, Paths paths, Live live) =>
        {
            if (await Running(ctx, db, jobId) is not { } prm || prm.IpadId is not int iid) return Results.NotFound();
            if (!NoWipeBackups.Candidates(paths, iid).Any(c => c.Store == i.Store && c.Set == i.Set)) return Results.NotFound();
            NoWipeBackups.Pin(paths, iid, i.Store, i.Set);
            await live.Changed("ipads");
            return Results.Ok();
        });
    }

    /// <summary>Parametrene for agentens kørende supervise_nowipe-job (null = ikke agentens, ikke kørende eller en anden handling).</summary>
    private static async Task<JobParams?> Running(HttpContext ctx, AppDb db, int jobId)
    {
        var h = ctx.Request.Headers.Authorization.ToString();
        if ((h.StartsWith("Bearer ", StringComparison.Ordinal) ? await AgentAuth.Resolve(db, h[7..].Trim()) : null) is not { } m) return null;
        var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId && j.MachineId == m.Id && (j.Type == JobTypes.Ios || j.Type == JobTypes.UsbIpad)
                                                                         && j.Status == JobStatus.Running);
        if (job is null) return null;
        var prm = Json.Des<JobParams>(job.ParamsJson);
        return prm.Action == IosActions.SuperviseNoWipe ? prm : null;
    }
}
