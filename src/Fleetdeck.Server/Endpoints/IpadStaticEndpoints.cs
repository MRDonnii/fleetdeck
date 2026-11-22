using System.Security.Claims;
using System.Text;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Hubs;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Server.Endpoints;

/// <summary>
/// Statisk tilstand: den låste profil (download, fjernelseskode) og iPads tilsluttet en Mac med Apple Configurator (cfgutil).
/// Installation af den låste profil og koden kræver administrator; backup og iPadOS-opdatering kræver tilladelsen "update".
/// </summary>
public static partial class IpadEndpoints
{
    private const string MobileConfig = "application/x-apple-aspen-config";

    private static void MapStatic(WebApplication app, RouteGroupBuilder fam, RouteGroupBuilder adm)
    {
        // Den låste profil som fil (til Apple Configurator: Tilføj → Profiler). Indeholder fjernelseskoden i klartekst.
        adm.MapGet("/ipads/{id:int}/locked-profile", async (int id, AppDb db, LockedProfiles lp, AuditService audit, ClaimsPrincipal u, HttpContext ctx) =>
        {
            var p = await db.Ipads.FindAsync(id);
            if (p is null) return Results.NotFound();
            var xml = await lp.Build(p);
            await db.SaveChangesAsync();
            await audit.Log(u.UserName(), "ipad.locked_profile", $"{p.Name} v{p.LockedProfileVersion}");
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.File(Encoding.UTF8.GetBytes(xml), MobileConfig, $"Fleetdeck-{p.Id}-laast-v{p.LockedProfileVersion}.mobileconfig");
        });

        adm.MapGet("/ipads/{id:int}/removal-password", async (int id, AppDb db, LockedProfiles lp, AuditService audit, ClaimsPrincipal u, HttpContext ctx) =>
        {
            var p = await db.Ipads.FindAsync(id);
            if (p is null) return Results.NotFound();
            var pw = lp.Password(p);
            await lp.Sync(p);
            await db.SaveChangesAsync();
            await audit.Log(u.UserName(), "ipad.removal_password", p.Name);
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { password = pw });
        });

        // Ny fjernelseskode: giver en ny version af profilen. Den gamle kode gælder, indtil den nye profil er installeret.
        adm.MapPost("/ipads/{id:int}/locked-profile/rotate-password", async (int id, AppDb db, LockedProfiles lp, AuditService audit, ClaimsPrincipal u, Live live) =>
        {
            var p = await db.Ipads.FindAsync(id);
            if (p is null) return Results.NotFound();
            var pw = lp.Rotate(p);
            await lp.Sync(p);
            await db.SaveChangesAsync();
            await audit.Log(u.UserName(), "ipad.removal_password.rotate", $"{p.Name} → v{p.LockedProfileVersion}");
            await live.Changed("ipads");
            return Results.Ok(new { password = pw, version = p.LockedProfileVersion });
        });

        // Kobl iPad'en til en enhed, en Mac har set (tom ECID = fjern koblingen)
        adm.MapPost("/ipads/{id:int}/link", async (int id, IpadLinkInput i, AppDb db, AuditService audit, ClaimsPrincipal u, Live live) =>
        {
            var p = await db.Ipads.FindAsync(id);
            if (p is null) return Results.NotFound();
            var ecid = Cfgutil.NormEcid(i.Ecid);
            if (ecid.Length == 0)
            {
                p.Ecid = null; p.Serial = null; p.Supervised = false; p.LockedProfileApplied = null; p.DeviceSeen = null;
                await db.SaveChangesAsync();
                await audit.Log(u.UserName(), "ipad.unlink", p.Name);
                await live.Changed("ipads");
                return Results.Ok(new { msg = "Koblingen er fjernet" });
            }
            var seen = (await db.Machines.Where(m => m.Platform == Platforms.Mac).ToListAsync())
                .SelectMany(m => IosInventory.Devices(m).Select(d => (d, at: m.IosDevicesAt ?? DateTime.UtcNow)))
                .Where(x => Cfgutil.NormEcid(x.d.Ecid) == ecid).OrderByDescending(x => x.at).FirstOrDefault();
            if (seen.d is null) return Results.Problem("Ingen Mac har set den iPad – tryk \"Find tilsluttede iPads\" først", statusCode: 404);
            foreach (var other in await db.Ipads.Where(x => x.Id != id && x.Ecid == ecid).ToListAsync())
            {
                other.Ecid = null; other.Serial = null; other.Supervised = false; other.LockedProfileApplied = null;
            }
            IosInventory.Link(p, seen.d, seen.at);
            await db.SaveChangesAsync();
            await audit.Log(u.UserName(), "ipad.link", $"{p.Name} ↔ {seen.d.Name ?? ecid}");
            await live.Changed("ipads");
            return Results.Ok(new { msg = $"{p.Name} er linket til {seen.d.Name ?? "enheden"}" });
        });

        // Mac'er med Fleetdeck-agenten og de iPads, de sidst så via kabel
        adm.MapGet("/ipads/macs", async (ClaimsPrincipal u, AppDb db, SettingsService settings) =>
        {
            var sc = await FamilyScope.Of(u, db);
            if (!sc.Can(FamilyPerms.Update)) return Results.Problem("Det har du ikke adgang til", statusCode: 403);
            var poll = await settings.GetNum("poll_seconds", 30);
            var macs = await db.Machines.AsNoTracking().Where(m => m.Platform == Platforms.Mac).OrderBy(m => m.DisplayName ?? m.Name).ToListAsync();
            var ipads = await db.Ipads.AsNoTracking().Where(i => i.Ecid != null).Select(i => new { i.Id, i.Name, i.Ecid }).ToListAsync();
            var rows = new List<object>();
            foreach (var m in macs)
            {
                var devices = IosInventory.Devices(m).Select(d => new { d, ipad = ipads.FirstOrDefault(i => i.Ecid == Cfgutil.NormEcid(d.Ecid)) })
                    // forældre ser kun de iPads, de har adgang til
                    .Where(x => sc.Admin || (x.ipad is not null && sc.Sees(x.ipad.Id)))
                    .Select(x => new
                    {
                        ecid = x.d.Ecid, name = x.d.Name, deviceType = x.d.DeviceType, deviceClass = x.d.DeviceClass, serial = x.d.Serial, firmware = x.d.Firmware,
                        supervised = x.d.Supervised, paired = x.d.Paired, battery = x.d.Battery, charging = x.d.Charging, passcode = x.d.Passcode,
                        profiles = x.d.Profiles?.Select(pr => new { identifier = pr.Identifier, name = pr.Name, version = pr.Version }),
                        ipadId = x.ipad?.Id, ipadName = x.ipad?.Name,
                    }).ToList();
                var job = await db.Jobs.AsNoTracking().Where(j => j.MachineId == m.Id && j.Type == JobTypes.Ios).OrderByDescending(j => j.Id).FirstOrDefaultAsync();
                object? jobRow = null;
                if (job is not null)
                {
                    var prm = Json.Des<JobParams>(job.ParamsJson);
                    var mine = sc.Admin || (prm.IpadId is int iid && sc.Sees(iid));
                    jobRow = new
                    {
                        job.Id, title = mine ? job.Title : "iPad-job", action = prm.Action, order = prm.Order, ipadId = prm.IpadId, job.Status, job.Created, job.Finished, job.Step, job.Progress, job.StepSince,
                        output = mine ? (job.Output.Length > 4000 ? job.Output[^4000..] : job.Output) : "",
                    };
                }
                rows.Add(new { m.Id, Name = m.Label, online = MachineViews.Online(m, poll), m.LastSeen, devicesAt = m.IosDevicesAt, devices, job = jobRow });
            }
            return Results.Ok(rows);
        });

        // Opret et ios-job på en Mac: find iPads, installér den låste profil, tag backup, opdatér iPadOS, gør overvåget eller gendan
        adm.MapPost("/ipads/{id:int}/mac-action", async (int id, MacActionInput i, ClaimsPrincipal u, AppDb db, JobService jobs, LockedProfiles lp,
            IpadSupervision sv, Paths paths, AuditService audit, SettingsService settings) =>
        {
            var sc = await FamilyScope.Of(u, db);
            var p = await db.Ipads.FindAsync(id);
            if (p is null || !sc.Sees(id)) return Results.NotFound();
            var action = i.Action;
            if (!IosActions.All.Contains(action)) return Results.Problem("Ukendt handling", statusCode: 400);
            if (action is IosActions.RemoveProfile or IosActions.Release) return Results.Problem("Brug \"Frigiv iPad\" under Opsætning", statusCode: 400);
            if (action == IosActions.SuperviseNoWipe) return Results.Problem("Brug \"Overvåg uden datatab\" under Opsætning", statusCode: 400);
            var erasing = IosActions.Erasing.Contains(action);
            var allowed = action is IosActions.InstallProfile or IosActions.Help ? sc.Admin
                : erasing ? sc.Admin && sc.Can(FamilyPerms.Erase)
                : sc.Can(FamilyPerms.Update);
            if (!allowed)
                return Results.Problem(action == IosActions.InstallProfile ? "Kun en administrator kan installere den låste profil"
                    : erasing ? "Kun en administrator kan slette og overvåge en iPad" : "Det har du ikke adgang til", statusCode: 403);
            var m = await db.Machines.FindAsync(i.MacId);
            if (m is null || m.Platform != Platforms.Mac) return Results.Problem("Vælg en Mac med Fleetdeck-agenten", statusCode: 400);
            string? ecid = null;
            if (action is not (IosActions.List or IosActions.Help))
            {
                if (p.Ecid is not { Length: > 0 } e) return Results.Problem("Link først iPad'en til en tilsluttet enhed (\"Find tilsluttede iPads\")", statusCode: 400);
                ecid = e;
            }
            if (erasing && !ConfirmMatches(i.Confirm, p.Name))
                return Results.Problem($"Skriv iPad'ens navn (\"{p.Name}\") for at bekræfte – iPad'en bliver slettet", statusCode: 400);
            string? set = null;
            if (action == IosActions.Restore && i.BackupSet is { Length: > 0 } bs)
            {
                if (IpadBackups.ParseSet(bs) is not { } ps || ps.IpadId != p.Id || !IpadBackups.List(paths, p.Id).Any(x => x.Set == bs && x.Complete))
                    return Results.Problem("Backuppen findes ikke i Fleetdeck", statusCode: 404);
                set = bs;
            }
            var active = await db.Jobs.Where(j => j.MachineId == m.Id && j.Type == JobTypes.Ios && JobStatus.Active.Contains(j.Status)).Select(j => j.ParamsJson).ToListAsync();
            var activeP = active.Select(Json.Des<JobParams>).ToList();
            if (activeP.Any(x => x.Action == action && (action is IosActions.List or IosActions.Help || x.Ecid == ecid)))
                return Results.Problem("Det er allerede i gang på Mac'en", statusCode: 409);
            // ingen andre handlinger på iPad'en, mens den slettes og overvåges (og omvendt)
            if (ecid is not null && activeP.Any(x => x.Ecid == ecid && (erasing || IosActions.Erasing.Contains(x.Action))))
                return Results.Problem("Der kører allerede et job på iPad'en – vent, til det er færdigt", statusCode: 409);
            if (action is IosActions.InstallProfile or IosActions.Supervise)
            {
                if (await lp.Sync(p)) await db.SaveChangesAsync();
                try { await sv.Identity(); }   // laves nu (ikke midt i jobbet), og fejler her, hvis nøglen ikke kan læses
                catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: 500); }
            }
            if (IosActions.NeedSecrets.Contains(action) && action != IosActions.InstallProfile)
            {
                try { await sv.BackupPassword(); }
                catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: 500); }
            }
            // engangskoden står i jobbet; dens SHA-256 gemmes under jobbets id, før agenten vækkes (Commit)
            var nonce = IosActions.NeedSecrets.Contains(action) ? IpadSupervision.NewNonceValue() : null;
            var prm = IosJobs.Params(action, ecid, p, i.Order, set, nonce, await InstallLocale.Load(settings));
            var title = action switch
            {
                IosActions.List => "iPads: find tilsluttede iPads",
                IosActions.Help => "iPads: vis cfgutil-hjælp",
                IosActions.InstallProfile => $"iPad: installér låst profil v{p.LockedProfileVersion} på {p.Name}",
                IosActions.Backup => $"iPad: tag krypteret backup af {p.Name}",
                IosActions.Supervise => $"iPad: gør {p.Name} overvåget (sletter og gendanner)",
                IosActions.Restore => $"iPad: gendan backup på {p.Name} (sletter iPad'en)",
                _ => $"iPad: opdatér iPadOS på {p.Name}",
            };
            var j = await jobs.Create(m.Id, JobTypes.Ios, title, prm, RunAs.System, null, "manual", u.UserName(), save: false);
            await db.SaveChangesAsync();
            if (nonce is not null) await sv.StoreNonce(j.Id, nonce);
            await jobs.Commit(m.Id);
            await sv.PruneNonces();
            await audit.Log(u.UserName(), erasing ? "ipad.mac_action.erase" : "ipad.mac_action", $"{p.Name}: {action} på {m.Label}");
            return Results.Ok(new { jobId = j.Id, msg = $"Sendt til {m.Label}" });
        });

        // Agenten henter hemmelighederne til et kørende ios-job: kun med jobbets engangskode, kun én gang, kun det handlingen skal bruge
        app.MapGet("/api/agent/ios-secrets/{jobId:int}", async (int jobId, string? nonce, HttpContext ctx, AppDb db, IpadSupervision sv, AuditService audit) =>
        {
            var h = ctx.Request.Headers.Authorization.ToString();
            if ((h.StartsWith("Bearer ", StringComparison.Ordinal) ? await AgentAuth.Resolve(db, h[7..].Trim()) : null) is not { } m) return Results.Unauthorized();
            ctx.Response.Headers.CacheControl = "no-store";
            var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId && j.MachineId == m.Id && j.Type == JobTypes.Ios && j.Status == JobStatus.Running);
            if (job is null || m.Platform != Platforms.Mac) return Results.NotFound();
            var prm = Json.Des<JobParams>(job.ParamsJson);
            if (!IosActions.NeedSecrets.Contains(prm.Action) || !await sv.TakeNonce(jobId, nonce))
            {
                await audit.Log($"agent:{m.Name}", "ipad.secrets_denied", $"job {jobId}: ugyldig eller brugt engangskode");
                return Results.Text("Engangskoden er ugyldig eller allerede brugt – start handlingen igen fra Fleetdeck", statusCode: 403);
            }
            try
            {
                var secrets = await sv.SecretsFor(prm.Action);
                await audit.Log($"agent:{m.Name}", "ipad.secrets", $"job {jobId} ({prm.Action})");
                return Results.Json(secrets, Json.Web);
            }
            catch (InvalidOperationException ex) { return Results.Text(ex.Message, statusCode: 500); }
        });

        // Agenten melder, at alle dele af en iPad-backup er sendt
        app.MapPost("/api/agent/ios-backup/{jobId:int}/complete", async (int jobId, IosBackupComplete c, HttpContext ctx, AppDb db, Paths paths, Live live) =>
        {
            var h = ctx.Request.Headers.Authorization.ToString();
            if ((h.StartsWith("Bearer ", StringComparison.Ordinal) ? await AgentAuth.Resolve(db, h[7..].Trim()) : null) is not { } m) return Results.Unauthorized();
            var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId && j.MachineId == m.Id && j.Type == JobTypes.Ios && j.Status == JobStatus.Running);
            if (job is null) return Results.NotFound();
            var prm = Json.Des<JobParams>(job.ParamsJson);
            if (prm.IpadId is not int iid || prm.Action is not (IosActions.Backup or IosActions.Supervise or IosActions.Release or IosActions.SuperviseNoWipe)) return Results.NotFound();
            if (IpadBackups.Complete(paths, iid, c, m, await NoWipeBackups.Protected(db, iid)) is { } err) return Results.Problem(err, statusCode: 400);
            await live.Changed("ipads");
            return Results.Ok();
        });

        // Backups i Fleetdeck og den seneste backup-handling
        adm.MapGet("/ipads/{id:int}/backups", async (int id, ClaimsPrincipal u, AppDb db, Paths paths) =>
        {
            var sc = await FamilyScope.Of(u, db);
            if (!sc.Sees(id)) return Results.NotFound();
            if (!sc.Can(FamilyPerms.Update)) return Results.Problem("Det har du ikke adgang til", statusCode: 403);
            return Results.Ok(IpadBackups.List(paths, id));
        });

        // Overvågningen: organisationens navn og husstandens identitet (Mac og USB bruger den samme – nøglen forlader kun serveren til et kørende job)
        adm.MapGet("/ipads/supervision", async (IpadSupervision sv) =>
            Results.Ok(new
            {
                organization = await sv.Organization(), identity = await sv.Info(), backupPassword = await sv.HasBackupPassword(),
                // "iPad via USB"s gamle identitet fra før sammenlægningen, hvis den er en anden (husstandens vinder altid)
                previousUsbIdentity = await sv.PreviousUsbIdentity(),
            }));
        adm.MapPost("/ipads/supervision/organization", async (SupervisionOrgInput i, IpadSupervision sv, AuditService audit, ClaimsPrincipal u) =>
        {
            await sv.SetOrganization(i.Organization ?? "");
            await audit.Log(u.UserName(), "ipad.supervision_org", await sv.Organization());
            return Results.Ok(new { organization = await sv.Organization() });
        });
        // Vis backup-adgangskoden (fx til at gendanne en backup i Finder)
        adm.MapGet("/ipads/backup-password", async (IpadSupervision sv, AuditService audit, ClaimsPrincipal u, HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            try
            {
                var pw = await sv.BackupPassword();
                await audit.Log(u.UserName(), "ipad.backup_password", "vist");
                return Results.Ok(new { password = pw });
            }
            catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: 500); }
        });

        // Agenten på Mac'en henter den låste profil til et kørende install_profile- eller supervise-job (agentens eget login)
        app.MapGet("/api/agent/ios-profile/{jobId:int}", async (int jobId, HttpContext ctx, AppDb db, LockedProfiles lp, AuditService audit) =>
        {
            var h = ctx.Request.Headers.Authorization.ToString();
            if ((h.StartsWith("Bearer ", StringComparison.Ordinal) ? await AgentAuth.Resolve(db, h[7..].Trim()) : null) is not { } m) return Results.Unauthorized();
            var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId && j.MachineId == m.Id && j.Type == JobTypes.Ios && j.Status == JobStatus.Running);
            if (job is null || m.Platform != Platforms.Mac) return Results.NotFound();
            var prm = Json.Des<JobParams>(job.ParamsJson);
            if (prm.Action is not (IosActions.InstallProfile or IosActions.Supervise or IosActions.SuperviseNoWipe) || prm.IpadId is not int iid || await db.Ipads.FindAsync(iid) is not { } p) return Results.NotFound();
            var xml = await lp.Build(p);
            await db.SaveChangesAsync();
            await audit.Log("agent", "ipad.locked_profile.mac", $"{p.Name} v{p.LockedProfileVersion} → {m.Label}");
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers["X-Fleetdeck-Profile-Version"] = p.LockedProfileVersion.ToString();
            return Results.File(Encoding.UTF8.GetBytes(xml), MobileConfig);
        });
    }

    /// <summary>Den skrevne bekræftelse skal være iPad'ens navn (uden hensyn til store/små bogstaver og mellemrum i enderne).</summary>
    public static bool ConfirmMatches(string? typed, string name) =>
        typed is { Length: > 0 } t && name.Trim().Length > 0 && string.Equals(t.Trim(), name.Trim(), StringComparison.CurrentCultureIgnoreCase);
}
