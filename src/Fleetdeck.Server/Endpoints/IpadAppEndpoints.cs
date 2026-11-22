using System.Security.Claims;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Hubs;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Server.Endpoints;

public sealed record AppScanInput(string? Via, int? MacId);

/// <summary>Apps på iPad'en: liste med App Stores nyeste version, og "Scan apps nu" via Mac'en (kabel) eller Apple-styring (MDM).</summary>
public static partial class IpadEndpoints
{
    private static void MapApps(RouteGroupBuilder fam, RouteGroupBuilder adm)
    {
        fam.MapGet("/ipads/{id:int}/apps", async (int id, ClaimsPrincipal u, AppDb db, SettingsService settings, FamilySettings fs, IpadEngine engine, CancellationToken ct) =>
        {
            var sc = await FamilyScope.Of(u, db);
            var p = await db.Ipads.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (p is null || !sc.Sees(id)) return Results.NotFound();
            var apps = await db.IpadApps.AsNoTracking().Where(x => x.IpadId == id).ToListAsync(ct);
            var ids = apps.Select(a => a.BundleId).ToList();
            var store = await db.AppStoreApps.AsNoTracking().Where(s => ids.Contains(s.BundleId)).ToDictionaryAsync(s => s.BundleId, StringComparer.Ordinal, ct);
            var scan = await db.IpadAppScans.AsNoTracking().FirstOrDefaultAsync(x => x.IpadId == id, ct);
            var md = await db.MdmDevices.AsNoTracking().FirstOrDefaultAsync(x => x.IpadId == id, ct);
            var mdm = md is { State: MdmStates.Enrolled, Approved: true, EnrollmentId: not null };
            var mdmPending = await db.MdmCommands.AsNoTracking()
                .AnyAsync(c => c.IpadId == id && c.RequestType == AppStore.MdmRequestType && MdmCommandStates.Open.Contains(c.Status) && c.Created > DateTime.UtcNow.AddDays(-2), ct);

            // Mac'er, der har set iPad'en via kabel (nyeste først)
            var poll = await settings.GetNum("poll_seconds", 30);
            var macs = p.Ecid is { Length: > 0 } ecid
                ? (await db.Machines.AsNoTracking().Where(m => m.Platform == Platforms.Mac).ToListAsync(ct))
                    .Where(m => IosInventory.Devices(m).Any(d => Cfgutil.NormEcid(d.Ecid) == ecid))
                    .OrderByDescending(m => MachineViews.Online(m, poll)).ThenByDescending(m => m.IosDevicesAt)
                    .Select(m => new { m.Id, name = m.Label, online = MachineViews.Online(m, poll), seenAt = m.IosDevicesAt }).ToList()
                : [];
            var job = (await db.Jobs.AsNoTracking().Where(j => j.Type == JobTypes.IosApps).OrderByDescending(j => j.Id).Take(30).ToListAsync(ct))
                .FirstOrDefault(j => Json.Des<JobParams>(j.ParamsJson).IpadId == id);

            // AdGuard-tjenester, som apps kan kobles til ("Spær tjeneste")
            var services = new List<(string Id, string Name, string[] Domains)>();
            if (await fs.Client() is { } ag)
                try { services = (await engine.Catalog(ag, ct))?.Items.Select(s => (s.Id, s.Name, s.Domains)).ToList() ?? []; }
                catch (Exception e) when (e is InvalidOperationException or HttpRequestException or TaskCanceledException) { }

            var hidden = p.HiddenApps.ToHashSet(StringComparer.Ordinal);
            var rows = apps.Select(a =>
            {
                var s = store.GetValueOrDefault(a.BundleId);
                var svc = AppStore.MatchService(a.BundleId, s?.Name ?? a.Name, services);
                return new
                {
                    a.BundleId, name = a.Name ?? s?.Name ?? a.BundleId, a.Version, a.Build, a.Outdated, a.Seen,
                    storeVersion = s?.Found == true ? s.Version : null, storeName = s?.Found == true ? s.Name : null,
                    iconUrl = s?.Found == true ? s.IconUrl : null, storeUrl = s?.Found == true ? s.StoreUrl : null, released = s?.Found == true ? s.Released : null,
                    store = s is null ? "pending" : s.Error is not null && !s.Found ? "error" : s.Found ? "found" : "missing", storeChecked = s?.Checked,
                    hidden = hidden.Contains(a.BundleId),
                    service = svc, serviceName = svc is null ? null : services.FirstOrDefault(x => x.Id == svc).Name, serviceBlocked = svc is not null && p.BlockedServices.Contains(svc),
                };
            }).OrderByDescending(a => a.Outdated).ThenBy(a => a.name, StringComparer.CurrentCultureIgnoreCase).ToList();

            return Results.Ok(new
            {
                scannedAt = scan?.ScannedAt, source = scan?.Source, error = scan?.Error, requested = scan?.Requested, requestedVia = scan?.RequestedVia,
                ecid = sc.Admin ? p.Ecid : null, mdm, mdmPending, macs = sc.Admin ? macs : [],
                job = job is null ? null : new { job.Id, job.Title, job.Status, job.Created, job.Finished, job.Progress, job.Step, job.StepSince },
                outdated = rows.Count(a => a.Outdated), pendingLookups = rows.Count(a => a.store == "pending"),
                apps = rows, can = sc.PermList,
            });
        });

        // Scan apps nu: MDM-kommando (hvis iPad'en er tilmeldt Apple-styring) eller et job på Mac'en, iPad'en er tilsluttet
        adm.MapPost("/ipads/{id:int}/apps/scan", async (int id, AppScanInput i, ClaimsPrincipal u, AppDb db, SettingsService settings, JobService jobs, MdmService mdmSvc,
            AuditService audit, Live live) =>
        {
            var sc = await FamilyScope.Of(u, db);
            var p = await db.Ipads.FindAsync(id);
            if (p is null || !sc.Sees(id)) return Results.NotFound();
            if (!sc.Can(FamilyPerms.Update)) return Results.Problem("Det har du ikke adgang til", statusCode: 403);
            var md = await db.MdmDevices.FirstOrDefaultAsync(x => x.IpadId == id);
            var mdmOk = md is { State: MdmStates.Enrolled, Approved: true, EnrollmentId: not null };
            var via = i.Via is IpadAppStore.ViaMac or IpadAppStore.ViaMdm ? i.Via : mdmOk ? IpadAppStore.ViaMdm : IpadAppStore.ViaMac;
            var scan = await IpadAppStore.Scan(db, id);
            string msg;
            if (via == IpadAppStore.ViaMdm)
            {
                if (!mdmOk) return Results.Problem("iPad'en er ikke tilmeldt Apple-styring", statusCode: 400);
                try
                {
                    var c = await mdmSvc.Send(p, md!, AppStore.MdmListCommand(), u.UserName(), "Scan apps");
                    if (c.Status == MdmCommandStates.Failed) return Results.Problem(c.Error ?? "Kommandoen kunne ikke sendes", statusCode: 502);
                }
                catch (MdmException e) { return Results.Problem(e.Message, statusCode: 400); }
                msg = "Sendt til iPad'en via Apple-styring – listen kommer, når iPad'en svarer";
            }
            else
            {
                if (p.Ecid is not { Length: > 0 } ecid)
                    return Results.Problem("Link først iPad'en til en tilsluttet enhed (Opsætning → Via Mac'en → \"Find tilsluttede iPads\")", statusCode: 400);
                var poll = await settings.GetNum("poll_seconds", 30);
                var all = await db.Machines.Where(m => m.Platform == Platforms.Mac).ToListAsync();
                var m = i.MacId is int mid ? all.FirstOrDefault(x => x.Id == mid)
                    : all.Where(x => IosInventory.Devices(x).Any(d => Cfgutil.NormEcid(d.Ecid) == ecid))
                        .OrderByDescending(x => MachineViews.Online(x, poll)).ThenByDescending(x => x.IosDevicesAt).FirstOrDefault()
                      ?? all.Where(x => MachineViews.Online(x, poll)).OrderByDescending(x => x.LastSeen).FirstOrDefault();
                if (m is null) return Results.Problem("Ingen Mac med Fleetdeck-agenten har set iPad'en", statusCode: 400);
                var active = await db.Jobs.Where(j => j.MachineId == m.Id && j.Type == JobTypes.IosApps && JobStatus.Active.Contains(j.Status)).Select(j => j.ParamsJson).ToListAsync();
                if (active.Select(Json.Des<JobParams>).Any(x => x.Ecid == ecid)) return Results.Problem("Der scannes allerede på Mac'en", statusCode: 409);
                await jobs.Create(m.Id, JobTypes.IosApps, $"iPad: scan apps på {p.Name}", new JobParams { Ecid = ecid, IpadId = p.Id, TimeoutMin = 5 },
                    RunAs.System, null, "manual", u.UserName());
                msg = $"Sendt til {m.Label} – iPad'en skal være tilsluttet med kabel og låst op";
            }
            scan.Requested = DateTime.UtcNow; scan.RequestedVia = via; scan.Error = null;
            await db.SaveChangesAsync();
            await audit.Log(u.UserName(), "ipad.apps_scan", $"{p.Name} via {via}");
            await live.Changed("ipads");
            return Results.Ok(new { msg });
        });
    }
}
