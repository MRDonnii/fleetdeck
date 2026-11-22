using System.Security.Claims;
using System.Text;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Hubs;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Server.Endpoints;

public sealed record UsbLinkInput(int MachineId, string Udid);
public sealed record UsbActionInput(int MachineId, string Udid, string Action, string? Backup = null, string? Confirm = null);
public sealed record UsbKeybagInput(string Pem, bool? Replace = null);

/// <summary>
/// iPad via USB: en iPad i kablet på en hvilken som helst pc (Windows, Linux, Mac) med udvidelsen "iPad via USB". Agentens vagt melder,
/// hvad der er sat i; herfra oprettes usb_ipad-jobs. Hemmeligheder (backup-kode, overvågningsnøgle) udleveres kun til det kørende job
/// mod jobbets engangsbillet og står aldrig i jobbets parametre eller output.
/// </summary>
public static class UsbIpadEndpoints
{
    private const string MobileConfig = "application/x-apple-aspen-config";

    private static async Task<Machine?> Agent(HttpContext ctx, AppDb db)
    {
        var h = ctx.Request.Headers.Authorization.ToString();
        return h.StartsWith("Bearer ", StringComparison.Ordinal) ? await AgentAuth.Resolve(db, h[7..].Trim()) : null;
    }

    /// <summary>Agentens kørende usb_ipad-job (null hvis jobbet ikke findes, ikke kører eller hører til en anden maskine).</summary>
    private static async Task<(Job Job, JobParams Params)?> Running(AppDb db, Machine m, int jobId, bool track = false)
    {
        var q = track ? db.Jobs : db.Jobs.AsNoTracking();
        var j = await q.FirstOrDefaultAsync(x => x.Id == jobId && x.MachineId == m.Id && x.Type == JobTypes.UsbIpad && x.Status == JobStatus.Running);
        return j is null ? null : (j, Json.Des<JobParams>(j.ParamsJson));
    }

    /// <summary>Hvilken hemmelighed en handling må hente (den første, hvis den må hente flere – se <see cref="SecretKinds"/>).</summary>
    public static string? SecretFor(string? action) => SecretKinds(action).FirstOrDefault();

    /// <summary>De hemmeligheder, en handling må hente – hver højst én gang pr. job.</summary>
    public static string[] SecretKinds(string? action) => action switch
    {
        UsbIpadActions.InstallProfile or UsbIpadActions.Supervise or UsbIpadActions.RemoveProfile => [UsbIpadSecretKinds.Keybag],
        UsbIpadActions.Backup or UsbIpadActions.Restore => [UsbIpadSecretKinds.BackupPassword],
        UsbIpadActions.Release or UsbIpadActions.SuperviseNoWipe => [UsbIpadSecretKinds.Keybag, UsbIpadSecretKinds.BackupPassword],
        _ => [],
    };

    /// <summary>Må billetten byttes til denne slags nu? Ja, hvis handlingen bruger den, og den ikke er hentet før i jobbet.</summary>
    public static bool TicketAllows(JobParams prm, string? kind) =>
        kind is { Length: > 0 } k && SecretKinds(prm.Action).Contains(k) && prm.TicketUsed?.Contains(k) != true;

    /// <summary>Navnet skal skrives som bekræftelse ved handlinger, der afprøves (overvågning og sletning).</summary>
    public static bool Confirmed(string ipadName, string? typed) =>
        typed is { } t && t.Trim().Length > 0 && string.Equals(t.Trim(), ipadName.Trim(), StringComparison.CurrentCultureIgnoreCase);

    public static void MapUsbIpadEndpoints(this WebApplication app)
    {
        var fam = app.MapGroup("/api").RequireAuthorization("family");
        var adm = app.MapGroup("/api").RequireAuthorization("admin");

        // ------------------------------------------------------------------ agenten
        // Vagten: hvad er sat i lige nu (hvert minut og straks ved ændringer)
        app.MapPost("/api/agent/usb-ipads", async (UsbIpadReport r, HttpContext ctx, AppDb db, UsbIpadStore store, Live live) =>
        {
            if (await Agent(ctx, db) is not { } m) return Results.Unauthorized();
            var changed = store.Put(m.Id, r);
            var linked = false;
            if (store.LinkDue(m.Id, changed))
            {
                linked = await UsbIpadLink.Apply(db, r.Devices, DateTime.UtcNow);
                if (linked) await db.SaveChangesAsync();
            }
            if (changed || linked) await live.Changed("ipads");
            return Results.Ok();
        });

        // Engangsbilletten byttes til én hemmelighed – kun til det kørende job og kun den slags, handlingen skal bruge
        app.MapPost("/api/agent/usb-ipad/secret", async (UsbIpadSecretRequest r, HttpContext ctx, AppDb db, IpadSupervision sv, AuditService audit) =>
        {
            if (await Agent(ctx, db) is not { } m) return Results.Unauthorized();
            if (await Running(db, m, r.JobId, track: true) is not { } run) return Results.NotFound();
            var (job, prm) = run;
            if (prm.Ticket is not { Length: > 0 } t || !Crypto.FixedEquals(t, r.Ticket ?? "") || !TicketAllows(prm, r.Kind))
            {
                await audit.Log("agent", "ipad.usb.secret_denied", $"job {job.Id} på {m.Label}");
                return Results.NotFound();
            }
            prm.TicketUsed = [.. prm.TicketUsed ?? [], r.Kind];
            if (SecretKinds(prm.Action).All(prm.TicketUsed.Contains)) prm.Ticket = null;   // brugt
            job.ParamsJson = Json.Ser(prm);
            await db.SaveChangesAsync();
            string value;
            try { value = r.Kind == UsbIpadSecretKinds.Keybag ? await sv.Keybag() : await sv.BackupPassword(); }
            catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: 500); }
            await audit.Log("agent", "ipad.usb.secret", $"{r.Kind} til job {job.Id} på {m.Label}");
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new UsbIpadSecretResponse { Value = value });
        });

        // Den låste profil til et kørende install_profile-job
        app.MapGet("/api/agent/usb-ipad/profile/{jobId:int}", async (int jobId, HttpContext ctx, AppDb db, LockedProfiles lp, AuditService audit) =>
        {
            if (await Agent(ctx, db) is not { } m) return Results.Unauthorized();
            if (await Running(db, m, jobId) is not { } run || run.Params.Action is not (UsbIpadActions.InstallProfile or UsbIpadActions.SuperviseNoWipe)
                || run.Params.IpadId is not int iid || await db.Ipads.FindAsync(iid) is not { } p) return Results.NotFound();
            var xml = await lp.Build(p);
            await db.SaveChangesAsync();
            await audit.Log("agent", "ipad.locked_profile.usb", $"{p.Name} v{p.LockedProfileVersion} → {m.Label}");
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.File(Encoding.UTF8.GetBytes(xml), MobileConfig);
        });

        // Backup-dele (streamet til disk som gendannelsespunkterne; højst 1,5 GB pr. del)
        app.MapPut("/api/agent/usb-ipad/backup", async (int job, string set, int part, int parts, string? udid, bool? enc, bool? verified, bool? empty, long? used,
            HttpContext ctx, AppDb db, Paths paths, Live live, CancellationToken ct) =>
        {
            if (await Agent(ctx, db) is not { } m) return Results.Unauthorized();
            if (await Running(db, m, job) is not { } run || run.Params.Action is not (UsbIpadActions.Backup or UsbIpadActions.Release or UsbIpadActions.SuperviseNoWipe)
                || run.Params.IpadId is not int iid) return Results.NotFound();
            if (UsbIpadBackups.SafeSet(set) is not { } name || parts is < 1 or > UsbIpadBackups.MaxParts || part < 1 || part > parts)
                return Results.BadRequest("Ugyldig backup-del");
            var dir = UsbIpadBackups.Dir(paths, iid); Directory.CreateDirectory(dir);
            var file = UsbIpadBackups.Part(dir, name, part);
            var tmp = file + ".upload";
            await using (var f = File.Create(tmp)) await ctx.Request.Body.CopyToAsync(f, ct);
            File.Move(tmp, file, true);
            if (part == parts)
            {
                var ipad = await db.Ipads.AsNoTracking().FirstOrDefaultAsync(x => x.Id == iid, ct);
                if (UsbIpadBackups.Finish(dir, name, parts, udid ?? run.Params.Udid ?? "", m.Label, ipad?.Name, enc, verified, empty, used) is null)
                    return Results.BadRequest("Der mangler dele af backuppen");
                UsbIpadBackups.Prune(dir, protect: await NoWipeBackups.Protected(db, iid));
                await live.Changed("ipads");
            }
            return Results.Ok();
        }).DisableAntiforgery().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(2L * 1024 * 1024 * 1024));

        app.MapGet("/api/agent/usb-ipad/backup/{set}/{part:int}", async (string set, int part, int job, HttpContext ctx, AppDb db, Paths paths) =>
        {
            if (await Agent(ctx, db) is not { } m) return Results.Unauthorized();
            if (await Running(db, m, job) is not { } run || run.Params.Action != UsbIpadActions.Restore || run.Params.Backup != set
                || run.Params.IpadId is not int iid || UsbIpadBackups.SafeSet(set) is not { } name) return Results.NotFound();
            var f = UsbIpadBackups.Part(UsbIpadBackups.Dir(paths, iid), name, part);
            return File.Exists(f) ? Results.File(f, "application/octet-stream", Path.GetFileName(f)) : Results.NotFound();
        });

        // ------------------------------------------------------------------ brugerfladen
        // Pc'er med udvidelsen og de iPads, der er sat i dem lige nu
        adm.MapGet("/ipads/usb", async (ClaimsPrincipal u, AppDb db, UsbIpadStore store, SettingsService settings) =>
        {
            var sc = await FamilyScope.Of(u, db);
            var poll = await settings.GetNum("poll_seconds", 30);
            var all = store.All();
            var ids = all.Select(x => x.MachineId).ToList();
            var machines = await db.Machines.AsNoTracking().Where(m => ids.Contains(m.Id)).ToListAsync();
            var ipads = await db.Ipads.AsNoTracking().Where(i => i.Ecid != null).Select(i => new { i.Id, i.Name, i.Ecid, i.Serial }).ToListAsync();
            var rows = new List<object>();
            foreach (var (mid, r, at) in all.OrderBy(x => machines.FirstOrDefault(m => m.Id == x.MachineId)?.Label))
            {
                if (machines.FirstOrDefault(x => x.Id == mid) is not { } m) continue;
                var devices = r.Devices.Select(d => new
                    {
                        d, ipad = ipads.FirstOrDefault(i => d.Ecid.Length > 0 && i.Ecid == d.Ecid) ?? (d.Serial is { Length: > 0 } sn ? ipads.FirstOrDefault(i => i.Serial == sn) : null),
                    })
                    .Where(x => sc.Admin || (x.ipad is not null && sc.Sees(x.ipad.Id)))
                    .Select(x => new
                    {
                        udid = x.d.Udid, ecid = x.d.Ecid.Length > 0 ? x.d.Ecid : null, name = x.d.Name, deviceType = x.d.DeviceType, serial = x.d.Serial, firmware = x.d.Firmware,
                        supervised = x.d.Supervised, paired = x.d.Paired, battery = x.d.Battery, charging = x.d.Charging, activation = x.d.ActivationState,
                        profiles = x.d.Profiles?.Select(pr => new { identifier = pr.Identifier, name = pr.Name, version = pr.Version }),
                        ipadId = x.ipad?.Id, ipadName = x.ipad?.Name,
                    }).ToList();
                if (!sc.Admin && devices.Count == 0) continue;   // forældre ser kun pc'er med deres egne iPads
                var job = await db.Jobs.AsNoTracking().Where(j => j.MachineId == m.Id && j.Type == JobTypes.UsbIpad).OrderByDescending(j => j.Id).FirstOrDefaultAsync();
                object? jobRow = null;
                if (job is not null)
                {
                    var prm = Json.Des<JobParams>(job.ParamsJson);
                    var mine = sc.Admin || (prm.IpadId is int iid && sc.Sees(iid));
                    if (mine)
                        jobRow = new
                        {
                            job.Id, job.Title, action = prm.Action, ipadId = prm.IpadId, udid = prm.Udid, job.Status, job.Created, job.Finished, job.Step, job.Progress, job.StepSince,
                            output = job.Output.Length > 4000 ? job.Output[^4000..] : job.Output,
                        };
                }
                rows.Add(new { m.Id, Name = m.Label, m.Platform, online = MachineViews.Online(m, poll), ready = r.Ready, problem = r.Problem, at, devices, job = jobRow });
            }
            return Results.Ok(rows);
        });

        // Kobl iPad'en til en enhed, der er sat i en pc (samme kobling som via Mac'en: ECID og serienummer)
        adm.MapPost("/ipads/{id:int}/usb/link", async (int id, UsbLinkInput i, AppDb db, UsbIpadStore store, AuditService audit, ClaimsPrincipal u, Live live) =>
        {
            var p = await db.Ipads.FindAsync(id);
            if (p is null) return Results.NotFound();
            if (store.Device(i.MachineId, i.Udid) is not { } d) return Results.Problem("iPad'en er ikke sat i den pc lige nu", statusCode: 404);
            var ecid = Cfgutil.NormEcid(d.Ecid);
            if (ecid.Length == 0) return Results.Problem("iPad'ens id kendes ikke endnu – lås iPad'en op og tryk \"Stol på\", og prøv igen om lidt", statusCode: 409);
            foreach (var other in await db.Ipads.Where(x => x.Id != id && x.Ecid == ecid).ToListAsync())
            {
                other.Ecid = null; other.Serial = null; other.Supervised = false; other.LockedProfileApplied = null;
            }
            d.Ecid = ecid;
            IosInventory.Link(p, d, DateTime.UtcNow);
            await db.SaveChangesAsync();
            await audit.Log(u.UserName(), "ipad.link.usb", $"{p.Name} ↔ {d.Name ?? ecid}");
            await live.Changed("ipads");
            return Results.Ok(new { msg = $"{p.Name} er linket til {d.Name ?? "enheden"}" });
        });

        // Opret et usb_ipad-job på pc'en, iPad'en er sat i
        adm.MapPost("/ipads/{id:int}/usb/action", async (int id, UsbActionInput i, ClaimsPrincipal u, AppDb db, UsbIpadStore store, JobService jobs, LockedProfiles lp,
            IpadSupervision sv, Paths paths, AuditService audit) =>
        {
            var sc = await FamilyScope.Of(u, db);
            var p = await db.Ipads.FindAsync(id);
            if (p is null || !sc.Sees(id)) return Results.NotFound();
            var action = i.Action;
            if (!UsbIpadActions.All.Contains(action)) return Results.Problem("Ukendt handling", statusCode: 400);
            if (UsbIpadActions.ReleaseOnly.Contains(action)) return Results.Problem("Brug \"Frigiv iPad\" under Opsætning", statusCode: 400);
            if (UsbIpadActions.NoWipeOnly.Contains(action)) return Results.Problem("Brug \"Overvåg uden datatab\" under Opsætning", statusCode: 400);
            var adminOnly = action is not (UsbIpadActions.Info or UsbIpadActions.Backup);
            if (adminOnly ? !sc.Admin : !sc.Can(FamilyPerms.Update))
                return Results.Problem(adminOnly ? "Kun en administrator kan det" : "Det har du ikke adgang til", statusCode: 403);
            if (UsbIpadActions.Experimental.Contains(action) && !Confirmed(p.Name, i.Confirm))
                return Results.Problem($"Skriv iPad'ens navn ({p.Name}) for at bekræfte", statusCode: 400);
            var m = await db.Machines.FindAsync(i.MachineId);
            if (m is null) return Results.Problem("Pc'en findes ikke", statusCode: 404);
            if (store.Device(m.Id, i.Udid) is not { } dev) return Results.Problem($"iPad'en er ikke sat i {m.Label} lige nu", statusCode: 409);
            if (p.Ecid is not { Length: > 0 } ecid || Cfgutil.NormEcid(dev.Ecid) != ecid)
                return Results.Problem("Link først enheden til denne iPad", statusCode: 400);
            if (await db.Jobs.AnyAsync(j => j.MachineId == m.Id && j.Type == JobTypes.UsbIpad && JobStatus.Active.Contains(j.Status)))
                return Results.Problem($"Der kører allerede et iPad-job på {m.Label}", statusCode: 409);

            var prm = new JobParams
            {
                Action = action, Udid = dev.Udid, Ecid = ecid, IpadId = p.Id,
                TimeoutMin = action is UsbIpadActions.Backup or UsbIpadActions.Restore ? 180 : 15,   // serveren opgiver kørende jobs efter 4 timer
            };
            if (SecretKinds(action).Length > 0) prm.Ticket = Crypto.Token(24);
            switch (action)
            {
                case UsbIpadActions.InstallProfile:
                    prm.ProfileIdentifier = LockedProfile.Identifier(p);
                    prm.ReplaceIds = [LockedProfile.DnsLinkIdentifier(p)];
                    if (await lp.Sync(p)) await db.SaveChangesAsync();
                    if (dev.Supervised == true) await sv.Keybag();   // husstandens identitet (den samme som Mac'en bruger)
                    break;
                case UsbIpadActions.Restore:
                    var b = UsbIpadBackups.List(paths, p.Id).FirstOrDefault(x => x.Set == i.Backup);
                    if (b is null) return Results.Problem("Vælg en backup at gendanne", statusCode: 400);
                    prm.Backup = b.Set; prm.Parts = b.Parts; prm.SourceUdid = b.Udid;
                    break;
                case UsbIpadActions.Supervise:
                    prm.Org = await sv.Organization();
                    await sv.Keybag();   // laves nu (eller overtages), så den er den samme for alle iPads – og for Mac'en
                    break;
            }
            var title = action switch
            {
                UsbIpadActions.Info => $"iPad (USB): læs {p.Name}",
                UsbIpadActions.InstallProfile => $"iPad (USB): installér låst profil v{p.LockedProfileVersion} på {p.Name}",
                UsbIpadActions.Backup => $"iPad (USB): tag krypteret backup af {p.Name}",
                UsbIpadActions.Restore => $"iPad (USB): gendan {p.Name} fra {prm.Backup}",
                UsbIpadActions.Supervise => $"iPad (USB): gør {p.Name} overvåget (afprøves)",
                _ => $"iPad (USB): slet {p.Name} (afprøves)",
            };
            var j = await jobs.Create(m.Id, JobTypes.UsbIpad, title, prm, RunAs.System, null, "manual", u.UserName());
            await audit.Log(u.UserName(), "ipad.usb_action", $"{p.Name}: {action} på {m.Label}");
            return Results.Ok(new { jobId = j.Id, msg = $"Sendt til {m.Label}" });
        });

        adm.MapGet("/ipads/{id:int}/usb/backups", async (int id, ClaimsPrincipal u, AppDb db, Paths paths) =>
        {
            var sc = await FamilyScope.Of(u, db);
            if (!sc.Sees(id) || !sc.Can(FamilyPerms.Update)) return Results.NotFound();
            return Results.Ok(UsbIpadBackups.List(paths, id).Select(b => new { set = b.Set, b.Parts, b.Bytes, b.Created, b.Machine, b.DeviceName }));
        });

        // Husstandens backup-kode (til gendannelse uden for Fleetdeck, fx i Finder eller Apple Devices)
        adm.MapGet("/ipads/usb/backup-password", async (IpadSupervision sv, AuditService audit, ClaimsPrincipal u, HttpContext ctx) =>
        {
            string pw;
            try { pw = await sv.BackupPassword(); }   // husstandens ene backup-kode (den samme som Mac'en bruger)
            catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: 500); }
            await audit.Log(u.UserName(), "ipad.usb.backup_password");
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { password = pw });
        });

        // Brug en anden overvågningsidentitet (PEM med certifikat og privat nøgle). Husstandens eksisterende erstattes kun med Replace = true.
        adm.MapPut("/ipads/usb/keybag", async (UsbKeybagInput i, IpadSupervision sv, AuditService audit, ClaimsPrincipal u) =>
        {
            string msg;
            try { msg = await sv.SetIdentity(i.Pem, i.Replace == true); }
            catch (ArgumentException e) { return Results.Problem(e.Message, statusCode: 400); }
            catch (InvalidOperationException e) { return Results.Problem(e.Message, statusCode: 409); }
            await audit.Log(u.UserName(), "ipad.supervision_identity", msg);
            return Results.Ok(new { msg });
        });
    }
}
