using System.Security.Claims;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Server.Endpoints;

public sealed record IpadLoosenInput(string? Via = null, int? MachineId = null, string? Udid = null);
public sealed record IpadReleaseInput(string? Confirm, string Via, int MachineId, string? Udid = null);

/// <summary>
/// "Frigiv iPad" (Opsætning, kun administrator) – når barnet er gammelt nok. To niveauer:
/// <list type="number">
/// <item>"Løsn styring": den låste profil fjernes (via Apple-styring, en Mac med Apple Configurator, en pc med "iPad via USB" – eller
/// i hånden med fjernelseskoden), iPad'en bliver en familie-iPad, og skemaer og spærrede tjenester ryddes. Intet slettes.</item>
/// <item>"Frigiv helt": krypteret backup → slet → gendan backuppen UDEN overvågning → Fleetdeck glemmer iPad'en (Apple-styring,
/// AdGuard-klienten og selve iPad'en). Kræver, at iPad'ens navn skrives som bekræftelse. Indholdet kommer tilbage, overvågningen gør ikke.</item>
/// </list>
/// </summary>
public static class IpadRelease
{
    /// <summary>Barnets regler væk: familie-iPad uden skema, spærrede tjenester, YouTube-tider eller pause.</summary>
    public static void Loosen(Ipad p)
    {
        p.Kind = IpadKinds.Family;
        p.Schedule = new string('0', IpadPolicy.Slots);
        p.BlockedServices = [];
        p.YtWindows = null; p.YtDailyMin = 0; p.YtBonusMin = 0; p.YtBonusDay = null;
        p.KidsMode = KidsModes.Allowed; p.KidsLinked = true; p.KidsWindows = null; p.KidsDailyMin = 0;
        p.PauseUntil = null; p.FreeUntil = null;
    }

    /// <summary>
    /// Er den låste profil (måske) lagt på uden om Apple-styring? Så skal den fjernes med Mac/USB eller i hånden. En Mac/pc har set den (applied),
    /// eller der findes en fjernelseskode (profilen kan være hentet som fil) og den ligger ikke via Apple-styring.
    /// </summary>
    public static bool StaticLocked(Ipad p, string? removalPassword, bool mdmLocked) => p.LockedProfileApplied is not null || (removalPassword is not null && !mdmLocked);

    /// <summary>Er den låste profil lagt på via Apple-styring (eller på vej)?</summary>
    public static bool MdmLocked(MdmDevice? d) => d is not null && (d.LockedViaMdm || d.LockedInstalledHash is not null);

    /// <summary>Trinene på iPad'en, når profilen fjernes i hånden.</summary>
    public static List<string> ManualSteps(Ipad p, string removalPassword) =>
    [
        "På iPad'en: Indstillinger → Generelt → VPN og enhedsadministration.",
        $"Vælg \"Fleetdeck – {p.Name} (låst …)\" → Fjern profil.",
        "Skriv iPad'ens egen kode, og derefter fjernelseskoden: " + removalPassword,
        "Har iPad'en også \"Fleetdeck – sikker DNS\", kan den fjernes samme sted (uden kode).",
    ];

    /// <summary>Fleetdeck glemmer iPad'en (Apple-styring, apps og aktivitet følger med; AdGuard-klienten fjernes af motoren).</summary>
    public static async Task Remove(AppDb db, Ipad p)
    {
        await db.IpadSlots.Where(s => s.IpadId == p.Id).ExecuteDeleteAsync();
        db.Ipads.Remove(p);
        foreach (var usr in await db.Users.Where(x => x.Role == Roles.Parent).ToListAsync()) usr.Ipads = usr.Ipads.Where(x => x != p.Id).ToList();
        await db.SaveChangesAsync();
    }

    /// <summary>Kaldes, når et release-job er lykkedes: Apple-styring, AdGuard og Fleetdeck glemmer iPad'en.</summary>
    public static async Task Finish(IServiceProvider sp, AppDb db, int ipadId, string machine)
    {
        if (await db.Ipads.FindAsync(ipadId) is not { } p) return;
        var hadMdm = await db.MdmDevices.AnyAsync(d => d.IpadId == ipadId);
        // iPad'en er slettet, så dens tilmelding er væk – her glemmes den også i Fleetdeck (cascade: enhed, kommandoer, links)
        await db.MdmEnrollTokens.Where(t => t.IpadId == ipadId).ExecuteDeleteAsync();
        await db.MdmCommands.Where(c => c.IpadId == ipadId).ExecuteDeleteAsync();
        await db.MdmDevices.Where(d => d.IpadId == ipadId).ExecuteDeleteAsync();
        await Remove(db, p);
        await sp.GetRequiredService<AuditService>().Log("agent", "ipad.release.done",
            $"{p.Name} er frigivet via {machine}{(hadMdm ? " (afmeldt Apple-styring)" : "")} og fjernet fra AdGuard og Fleetdeck");
        sp.GetRequiredService<IpadEngine>().Kick();
        sp.GetRequiredService<MdmEngine>().Kick();
    }
}

public static partial class IpadEndpoints
{
    private static void MapRelease(RouteGroupBuilder adm)
    {
        // Hvad kan "Frigiv iPad" bruge lige nu: Apple-styring, Mac'er og pc'er, iPad'en sidder i, og fjernelseskoden
        adm.MapGet("/ipads/{id:int}/release", async (int id, AppDb db, UsbIpadStore store, SettingsService settings, LockedProfiles lp) =>
        {
            var p = await db.Ipads.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (p is null) return Results.NotFound();
            var d = await db.MdmDevices.AsNoTracking().FirstOrDefaultAsync(x => x.IpadId == id);
            var poll = await settings.GetNum("poll_seconds", 30);
            var ecid = p.Ecid;
            var macs = ecid is null ? [] : (await db.Machines.AsNoTracking().Where(m => m.Platform == Platforms.Mac).ToListAsync())
                .Where(m => IosInventory.Devices(m).Any(x => Cfgutil.NormEcid(x.Ecid) == ecid))
                .Select(m => new { id = m.Id, name = m.Label, online = MachineViews.Online(m, poll), seen = m.IosDevicesAt }).ToList();
            var usbRows = ecid is null ? [] : store.All()
                .Select(x => (x.MachineId, x.Report, dev: x.Report.Devices.FirstOrDefault(dv => Cfgutil.NormEcid(dv.Ecid) == ecid)))
                .Where(x => x.dev is not null && x.Report.Ready).ToList();
            var usbIds = usbRows.Select(x => x.MachineId).ToList();
            var names = await db.Machines.AsNoTracking().Where(m => usbIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.DisplayName ?? m.Name);
            var usb = usbRows.Select(x => new { machineId = x.MachineId, name = names.GetValueOrDefault(x.MachineId, "?"), udid = x.dev!.Udid, supervised = x.dev.Supervised }).ToList();

            // seneste "Løsn styring"/"Frigiv helt"-job for iPad'en
            var recent = await db.Jobs.AsNoTracking().Where(j => j.Type == JobTypes.Ios || j.Type == JobTypes.UsbIpad).OrderByDescending(j => j.Id).Take(100).ToListAsync();
            var job = recent.Select(j => (j, prm: Json.Des<JobParams>(j.ParamsJson)))
                .Where(x => x.prm.IpadId == id && x.prm.Action is IosActions.RemoveProfile or IosActions.Release)
                .Select(x => new
                {
                    x.j.Id, x.j.Title, action = x.prm.Action, x.j.Status, x.j.Step, x.j.Progress, x.j.Created, x.j.Finished,
                    output = x.j.Output.Length > 3000 ? x.j.Output[^3000..] : x.j.Output,
                }).FirstOrDefault();

            return Results.Ok(new
            {
                p.Kind, p.Supervised, staticLocked = IpadRelease.StaticLocked(p, lp.Existing(p), IpadRelease.MdmLocked(d)), lockedApplied = p.LockedProfileApplied,
                mdm = d is null ? null : new { d.State, locked = IpadRelease.MdmLocked(d) },
                macs, usb, job,
            });
        });

        // Niveau 1: løsn styringen (intet slettes)
        adm.MapPost("/ipads/{id:int}/release/loosen", async (int id, IpadLoosenInput i, ClaimsPrincipal u, AppDb db, LockedProfiles lp, IpadSupervision sv,
            UsbIpadStore store, JobService jobs, AuditService audit, IpadEngine engine, MdmEngine mdmEngine, Live live) =>
        {
            var p = await db.Ipads.FindAsync(id);
            if (p is null) return Results.NotFound();
            var d = await db.MdmDevices.FirstOrDefaultAsync(x => x.IpadId == id);
            var steps = new List<string>();
            int? jobId = null; string? removal = null;

            var mdmLocked = IpadRelease.MdmLocked(d);
            if (mdmLocked)
            {
                d!.LockedViaMdm = false;   // motoren sender RemoveProfile ved næste runde
                steps.Add("Apple-styring fjerner den låste profil ved iPad'ens næste kontakt (som regel inden for få minutter, når den er tændt og på nettet).");
            }
            var existing = lp.Existing(p);
            if (IpadRelease.StaticLocked(p, existing, mdmLocked) || i.Via is "mac" or "usb")
            {
                var via = i.Via ?? "manual";
                if (via is "mac" or "usb")
                {
                    var (job, err) = via == "mac"
                        ? await MacReleaseJob(db, jobs, sv, p, i.MachineId, IosActions.RemoveProfile, u.UserName())
                        : await UsbReleaseJob(db, jobs, store, p, i.MachineId, i.Udid, UsbIpadActions.RemoveProfile, u.UserName());
                    if (job is null) return Results.Problem(err, statusCode: 409);
                    jobId = job.Id;
                    steps.Add($"Den låste profil fjernes nu fra {(await db.Machines.FindAsync(job.MachineId))?.Label ?? "maskinen"} – følg jobbet nedenfor.");
                }
                else if (existing is { } pw)
                {
                    removal = pw;
                    steps.AddRange(IpadRelease.ManualSteps(p, pw));
                }
            }
            if (steps.Count == 0) steps.Add("Der er ingen låst profil at fjerne.");

            IpadRelease.Loosen(p);
            await lp.Sync(p);
            await db.SaveChangesAsync();
            steps.Add("iPad'en er nu en familie-iPad: skemaer (skoletid, sengetid), spærrede tjenester og YouTube-tider er fjernet. Børnefilteret (voksenindhold) gælder stadig, " +
                      "så længe iPad'en bruger hjemmets DNS – det kan slås fra under Regler.");
            await audit.Log(u.UserName(), "ipad.release.loosen", $"{p.Name}{(jobId is int jid ? $" (job {jid})" : removal is not null ? " (fjernelseskoden vist)" : "")}");
            engine.Kick(); mdmEngine.Kick(); await live.Changed("ipads");
            return Results.Ok(new { msg = $"Styringen af {p.Name} er løsnet", steps, removalPassword = removal, jobId });
        });

        // Niveau 2: frigiv helt (backup → slet → gendan uden overvågning → glem iPad'en). Navnet skal skrives som bekræftelse.
        adm.MapPost("/ipads/{id:int}/release/full", async (int id, IpadReleaseInput i, ClaimsPrincipal u, AppDb db, IpadSupervision sv, UsbIpadStore store,
            JobService jobs, AuditService audit) =>
        {
            var p = await db.Ipads.FindAsync(id);
            if (p is null) return Results.NotFound();
            if (!ConfirmMatches(i.Confirm, p.Name)) return Results.Problem($"Skriv iPad'ens navn (\"{p.Name}\") for at bekræfte – iPad'en bliver slettet og gendannet", statusCode: 400);
            var (job, err) = i.Via switch
            {
                "mac" => await MacReleaseJob(db, jobs, sv, p, i.MachineId, IosActions.Release, u.UserName()),
                "usb" => await UsbReleaseJob(db, jobs, store, p, i.MachineId, i.Udid, UsbIpadActions.Release, u.UserName()),
                _ => (null, "Vælg en Mac eller pc, iPad'en sidder i"),
            };
            if (job is null) return Results.Problem(err, statusCode: 409);
            await audit.Log(u.UserName(), "ipad.release.full", $"{p.Name}: job {job.Id}");
            return Results.Ok(new { jobId = job.Id, msg = "Frigivelsen er startet – lad iPad'en sidde i kablet, til den er færdig" });
        });
    }

    /// <summary>ios-job på en Mac (remove_profile eller release) med engangskode til hemmelighederne.</summary>
    private static async Task<(Job? Job, string? Error)> MacReleaseJob(AppDb db, JobService jobs, IpadSupervision sv, Ipad p, int? macId, string action, string user)
    {
        var m = macId is int mid ? await db.Machines.FindAsync(mid) : null;
        if (m is null || m.Platform != Platforms.Mac) return (null, "Vælg en Mac med Fleetdeck-agenten");
        if (p.Ecid is not { Length: > 0 } ecid) return (null, "Link først iPad'en til en tilsluttet enhed (\"Find tilsluttede iPads\")");
        var active = (await db.Jobs.Where(j => j.MachineId == m.Id && j.Type == JobTypes.Ios && JobStatus.Active.Contains(j.Status)).Select(j => j.ParamsJson).ToListAsync())
            .Select(Json.Des<JobParams>);
        if (active.Any(x => x.Ecid == ecid)) return (null, "Der kører allerede et job på iPad'en – vent, til det er færdigt");
        try
        {
            await sv.Identity();
            if (IosJobs.NeedsBackupPassword(action)) await sv.BackupPassword();
        }
        catch (InvalidOperationException ex) { return (null, ex.Message); }
        var nonce = IpadSupervision.NewNonceValue();
        var prm = IosJobs.Params(action, ecid, p, null, null, nonce);
        var title = action == IosActions.Release ? $"iPad: frigiv {p.Name} (backup → slet → gendan uden overvågning)" : $"iPad: fjern den låste profil fra {p.Name}";
        var j = await jobs.Create(m.Id, JobTypes.Ios, title, prm, RunAs.System, null, "manual", user, save: false);
        await db.SaveChangesAsync();
        await sv.StoreNonce(j.Id, nonce);
        await jobs.Commit(m.Id);
        await sv.PruneNonces();
        return (j, null);
    }

    /// <summary>usb_ipad-job på en pc med "iPad via USB" (remove_profile eller release) med engangsbillet til hemmelighederne.</summary>
    private static async Task<(Job? Job, string? Error)> UsbReleaseJob(AppDb db, JobService jobs, UsbIpadStore store, Ipad p, int? machineId, string? udid,
        string action, string user)
    {
        var m = machineId is int mid ? await db.Machines.FindAsync(mid) : null;
        if (m is null) return (null, "Pc'en findes ikke");
        if (store.Device(m.Id, udid) is not { } dev) return (null, $"iPad'en er ikke sat i {m.Label} lige nu");
        if (p.Ecid is not { Length: > 0 } ecid || Cfgutil.NormEcid(dev.Ecid) != ecid) return (null, "Link først enheden til denne iPad");
        if (await db.Jobs.AnyAsync(j => j.MachineId == m.Id && j.Type == JobTypes.UsbIpad && JobStatus.Active.Contains(j.Status)))
            return (null, $"Der kører allerede et iPad-job på {m.Label}");
        var prm = new JobParams
        {
            Action = action, Udid = dev.Udid, Ecid = ecid, IpadId = p.Id, Ticket = Crypto.Token(24),
            ProfileIdentifier = LockedProfile.Identifier(p), ReplaceIds = [LockedProfile.DnsLinkIdentifier(p)],
            TimeoutMin = action == UsbIpadActions.Release ? 235 : 15,
        };
        var title = action == UsbIpadActions.Release ? $"iPad (USB): frigiv {p.Name} (backup → slet → gendan uden overvågning – afprøves)"
            : $"iPad (USB): fjern den låste profil fra {p.Name}";
        return (await jobs.Create(m.Id, JobTypes.UsbIpad, title, prm, RunAs.System, null, "manual", user), null);
    }
}
