using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Hubs;
using Fleetdeck.Server.Services;
using Fleetdeck.Server.Services.Bios;

namespace Fleetdeck.Server.Endpoints;

/// <summary>
/// BIOS: status pr. maskine og for flåden (kun læsning, også for læsere), katalogstatus, og pakkecachen (kun administratorer; hentning
/// sker kun på et tryk og skrives i revisionsloggen). Ingen af endpointene ændrer noget på maskinerne.
/// </summary>
public static class BiosEndpoints
{
    public sealed record PackageRequest(int MachineId, string? Kind);
    public sealed record ReadRequest(string? Reason);

    public static void MapBiosEndpoints(this WebApplication app)
    {
        var read = app.MapGroup("/api/bios").RequireAuthorization("user");
        var adm = app.MapGroup("/api/bios").RequireAuthorization("admin");

        read.MapGet("/machines/{id:int}", async (int id, AppDb db, BiosCatalog catalog) =>
        {
            if (!await db.Machines.AnyAsync(m => m.Id == id)) return Results.NotFound();
            var s = await db.BiosStatus.AsNoTracking().FirstOrDefaultAsync(x => x.MachineId == id);
            if (s is null) { await catalog.Rematch(db, [id]); s = await db.BiosStatus.AsNoTracking().FirstOrDefaultAsync(x => x.MachineId == id); }
            var snapshots = await db.BiosSnapshots.AsNoTracking().Where(x => x.MachineId == id).OrderByDescending(x => x.TakenAt).Take(20)
                .Select(x => new { x.Id, x.TakenAt, x.Reason, x.Driver, x.BiosVersion, x.By }).ToListAsync();
            var settings = Json.Des<Fleetdeck.Shared.SystemFacts>((await db.Machines.AsNoTracking().FirstAsync(m => m.Id == id)).FactsJson).BiosSettings;
            return Results.Ok(new { status = s is null ? null : View(s), snapshots, settings, catalog = (await catalog.Vendors(db)).FirstOrDefault(v => v.Vendor == s?.Vendor),
                localPort = Fleetdeck.Shared.LocalWeb.ServerPort() });
        });

        read.MapGet("/fleet", async (AppDb db) =>
        {
            var rows = await (from m in db.Machines.AsNoTracking()
                              join s in db.BiosStatus.AsNoTracking() on m.Id equals s.MachineId into g
                              from s in g.DefaultIfEmpty()
                              select new { m.Id, m.Name, m.DisplayName, m.Platform, m.FactsJson, s }).ToListAsync();
            // seneste backup af BIOS-indstillingerne pr. maskine (kolonnen "Backup")
            var snaps = await db.BiosSnapshots.AsNoTracking().GroupBy(x => x.MachineId).Select(g => new { g.Key, At = g.Max(x => x.TakenAt) })
                .ToDictionaryAsync(x => x.Key, x => x.At);
            // hentede USB-pakker: rigtig UEFI-kapsel eller rå ROM (vejen på Linux afhænger af det)
            var capsules = (await db.BiosPackages.AsNoTracking().Where(p => p.Kind == BiosPackages.UsbZip && p.State == BiosPackages.Ready)
                .OrderBy(p => p.Id).Select(p => new { p.Vendor, p.CatalogKey, p.Version, p.Capsule }).ToListAsync())
                .GroupBy(p => (p.Vendor, p.CatalogKey, p.Version)).ToDictionary(g => g.Key, g => g.Last().Capsule);
            return Results.Ok(rows.Select(r =>
            {
                var f = Json.Des<Fleetdeck.Shared.SystemFacts>(r.FactsJson);
                return new
                {
                    machineId = r.Id, machine = r.DisplayName ?? r.Name, r.Platform,
                    model = f.ProductVersion is { Length: > 0 } pv && BiosMatcher.VendorOf(f) == BiosMatcher.Lenovo ? pv : f.Model,
                    status = r.s is null ? null : View(r.s),
                    backupAt = snaps.TryGetValue(r.Id, out var at) ? at : (DateTime?)null,
                    route = BiosUpdateService.RouteFor(new Machine { Platform = r.Platform }, f, r.s,
                        r.s is { Latest: { } l, CatalogKey: { } k } && capsules.TryGetValue((r.s.Vendor, k, l), out var cap) ? cap : null).Route,
                };
            }).OrderBy(x => Order(x.status?.state)).ThenBy(x => x.machine, StringComparer.CurrentCultureIgnoreCase));
        });

        read.MapGet("/catalog", async (AppDb db, BiosCatalog catalog) => Results.Ok(await catalog.Vendors(db)));

        adm.MapPost("/check", async (HttpContext ctx, AuditService audit, BiosCatalog catalog) =>
        {
            catalog.Wake(force: true);
            await audit.Log(ctx.User.UserName(), "bios.check", "BIOS-katalogerne tjekkes nu");
            return Results.Ok();
        });

        adm.MapGet("/packages", async (AppDb db, BiosPackages pk) =>
            Results.Ok((await db.BiosPackages.AsNoTracking().OrderByDescending(p => p.Created).ToListAsync()).Select(pk.View)));

        adm.MapPost("/packages", async (PackageRequest r, HttpContext ctx, AppDb db, BiosPackages pk) =>
        {
            var (p, err) = await pk.Request(db, r.MachineId, r.Kind ?? BiosPackages.UsbZip, ctx.User.UserName());
            return p is null ? Results.Problem(err, statusCode: 400) : Results.Ok(pk.View(p));
        });

        adm.MapGet("/packages/{id:int}", async (int id, AppDb db, BiosPackages pk) =>
            await db.BiosPackages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id) is { } p ? Results.Ok(pk.View(p)) : Results.NotFound());

        // "Hent pakke": USB-pakken som zip med LÆS-MIG.txt (raw=true giver leverandørens fil uændret, med range-understøttelse)
        adm.MapGet("/packages/{id:int}/download", async (int id, bool? raw, int? machineId, HttpContext ctx, AppDb db, BiosPackages pk) =>
        {
            var p = await db.BiosPackages.FirstOrDefaultAsync(x => x.Id == id);
            if (p is null) return Results.NotFound();
            if (await pk.Verify(db, p) is { } err) return Results.Problem(err, statusCode: 409);
            await new AuditService(db).Log(ctx.User.UserName(), "bios.package.get", $"BIOS-pakke hentet fra Fleetdeck: {p.FileName} ({p.Version}) · SHA-256 {p.Sha256}", machineId);
            if (raw == true || p.Kind != BiosPackages.UsbZip)
                return Results.File(pk.FullPath(p), "application/octet-stream", p.FileName, enableRangeProcessing: true);
            var model = machineId is { } mid && await db.Machines.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mid) is { } mm
                ? Json.Des<Fleetdeck.Shared.SystemFacts>(mm.FactsJson).ProductVersion ?? p.CatalogKey : p.CatalogKey;
            var readme = BiosPackages.UsbReadme(model, p.Version, p.FileName, p.Sha256, p.ShaSource);
            var path = pk.FullPath(p);
            return Results.Stream(async s => await BiosPackages.WriteUsbZip(path, s, readme, ctx.RequestAborted), "application/zip",
                Path.GetFileNameWithoutExtension(p.FileName) + "-usb.zip");
        });

        // Indstillinger → BIOS: slet en pakke fra cachen – ikke mens den hentes, eller mens en BIOS-opdatering bruger den
        adm.MapDelete("/packages/{id:int}", async (int id, HttpContext ctx, AppDb db, BiosPackages pk) =>
        {
            if (await db.BiosPackages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id) is not { } p) return Results.NotFound();
            if (pk.InProgress(p.Id))
                return Results.Problem("Pakken er ved at blive hentet. Vent, til den er færdig.", statusCode: 409);
            if (await db.BiosUpdateRuns.AnyAsync(r => r.PackageId == id && BiosUpdateStates.Active.Contains(r.State)))
                return Results.Problem("En BIOS-opdatering bruger pakken lige nu.", statusCode: 409);
            return await pk.Delete(db, id, ctx.User.UserName()) ? Results.Ok() : Results.NotFound();
        });

        MapSettings(app, read, adm);
        MapUpdate(app, read, adm);
        MapUsb(app, adm);
    }

    public sealed record UsbWriteRequest(string DiskId, string Serial, long Size, string ConfirmHash, bool? AllowLarge);

    /// <summary>
    /// "Lav USB-stik" (design afsnit 7): guiden på den pc, brugeren sidder ved. Kun administratorer. Intet skrives, før brugeren har valgt
    /// et stik, der blev sat i under guiden, og bekræftet det lokalt på pc'en; agenten tjekker alt igen lige før.
    /// </summary>
    private static void MapUsb(WebApplication app, RouteGroupBuilder adm)
    {
        adm.MapPost("/machines/{id:int}/usb", async (int id, HttpContext ctx, AppDb db, BiosPackages pk, BiosUsbSessions usb, AuditService audit) =>
        {
            if (await db.Machines.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id) is not { } m) return Results.NotFound();
            var s = await db.BiosStatus.AsNoTracking().FirstOrDefaultAsync(x => x.MachineId == id);
            if (s?.Latest is not { } latest || s.Vendor is not { } vendor) return Results.Problem("Fleetdeck kender ikke en BIOS-pakke til maskinen.", statusCode: 409);
            if (Fleetdeck.Shared.BiosUsb.LayoutFor(vendor, BiosPackages.UsbZip) is not { } layout || vendor != BiosMatcher.Lenovo)
                return Results.Problem("USB-stik kan endnu kun laves til Lenovo. Brug \"Hent pakke\".", statusCode: 409);
            var by = ctx.User.UserName();
            var (pkg, err) = await pk.Request(db, id, BiosPackages.UsbZip, by);
            if (pkg is null) return Results.Problem(err, statusCode: 409);
            var ses = usb.Create(id, m.Label, vendor, layout, latest, pkg.Id, by);
            await audit.Log(by, "bios.usb.start", $"Guiden \"Lav USB-stik\" startet for {m.Label} ({latest})", id);
            return Results.Ok(new { sessionId = ses.Id, nonce = ses.Nonce });
        });

        adm.MapGet("/usb/{sid}", async (string sid, AppDb db, BiosPackages pk, BiosUsbSessions usb) =>
        {
            if (usb.Get(sid) is not { } s) return Results.NotFound();
            var pkg = await db.BiosPackages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == s.PackageId);
            var target = await db.Machines.AsNoTracking().FirstOrDefaultAsync(m => m.Id == s.TargetId);
            var list = await BiosUsbSessions.ActiveJob(db, s.ListJobId);
            var write = await BiosUsbSessions.ActiveJob(db, s.WriteJobId);
            usb.Settle(s, list, write);
            object? J(Job? j) => j is null ? null : new { j.Id, j.Status, j.Progress, j.Step, j.StepSince, j.Created, j.Finished };
            return Results.Ok(new
            {
                sessionId = s.Id, targetId = s.TargetId, target = s.TargetName, s.Vendor, s.Layout, s.Version, created = s.Created,
                nonceValid = s.ThisId is null && s.Nonce.Length > 0 && DateTime.UtcNow < s.NonceUntil,
                thisMachine = s.ThisId is null ? null : new { id = s.ThisId, name = s.ThisName, platform = s.ThisPlatform },
                package = pkg is null ? null : pk.View(pkg),
                listJob = J(list), baseline = s.Baseline, disks = s.Disks, disksAt = s.DisksAt, listMessage = s.ListMessage,
                chosen = s.Chosen, writeJob = J(write), result = s.Result, maxBytes = s.MaxBytes,
                steps = BiosUsbSessions.TargetSteps(s.Vendor, target?.Platform ?? "linux"),
            });
        });

        adm.MapPost("/usb/{sid}/renew", (string sid, BiosUsbSessions usb) =>
            usb.Renew(sid) is { } n ? Results.Ok(new { nonce = n }) : Results.NotFound());

        // lukket guide: sessionen fjernes – men ikke mens stikket skrives, så resultatet stadig når revisionsloggen
        adm.MapDelete("/usb/{sid}", async (string sid, AppDb db, BiosUsbSessions usb) =>
        {
            if (usb.Get(sid) is { } cur) usb.Settle(cur, null, await BiosUsbSessions.ActiveJob(db, cur.WriteJobId));
            if (usb.Get(sid) is { WriteJobId: not null, Result: null }) return Results.Ok();
            usb.Remove(sid);
            return Results.Ok();
        });

        // øjebliksbillede (første gang) og derefter: vent på et nyt stik. Kun læsning på pc'en.
        adm.MapPost("/usb/{sid}/disks", async (string sid, AppDb db, JobService jobs, BiosUsbSessions usb) =>
        {
            if (usb.Get(sid) is not { } s) return Results.NotFound();
            if (s.ThisId is not int me) return Results.Problem("Denne pc er ikke genkendt endnu.", statusCode: 409);
            if (s.ThisPlatform is not ("linux" or "windows" or "mac"))   // Linux: BiosUsb, Windows: BiosUsbWin, Mac: BiosUsbMac (diskutil)
                return Results.Problem(Fleetdeck.Shared.BiosUsb.PlatformLater, statusCode: 409);
            if (await BiosUsbSessions.ActiveJob(db, s.ListJobId) is { } cur && Fleetdeck.Shared.JobStatus.Active.Contains(cur.Status))
                return Results.Ok(new { jobId = cur.Id });
            var j = await jobs.Create(me, Fleetdeck.Shared.JobTypes.UsbDisks, s.Baseline ? "Venter på USB-stik til BIOS" : "USB-diske før stikket sættes i",
                new Fleetdeck.Shared.JobParams { BiosUsb = new() { SessionId = s.Id, Phase = Fleetdeck.Shared.BiosUsbPhases.List, MaxBytes = s.MaxBytes } },
                Fleetdeck.Shared.RunAs.System, null, "manual", s.By);
            s.ListJobId = j.Id;
            return Results.Ok(new { jobId = j.Id });
        });

        // skriv: kun et stik fra pc'ens egen liste, med pc'ens lokale kvittering. Pakken kontrolleres mod SHA-256 lige før.
        adm.MapPost("/usb/{sid}/write", async (string sid, UsbWriteRequest r, HttpContext ctx, AppDb db, JobService jobs, BiosPackages pk, BiosUsbSessions usb, AuditService audit) =>
        {
            if (usb.Get(sid) is not { } s) return Results.NotFound();
            usb.Settle(s, null, await BiosUsbSessions.ActiveJob(db, s.WriteJobId));
            if (BiosUsbSessions.CannotWrite(s, r.DiskId, r.Serial, r.Size, r.AllowLarge == true) is { } why) return Results.Problem(why, statusCode: 409);
            if (r.ConfirmHash is not { Length: 64 }) return Results.Problem("Stikket er ikke bekræftet på pc'en.", statusCode: 409);
            var pkg = await db.BiosPackages.FirstOrDefaultAsync(p => p.Id == s.PackageId);
            if (pkg is null || pkg.State != BiosPackages.Ready) return Results.Problem("Pakken er ikke hentet endnu.", statusCode: 409);
            if (await pk.Verify(db, pkg) is { } verr) return Results.Problem(verr, statusCode: 409);
            var target = await db.Machines.AsNoTracking().FirstAsync(m => m.Id == s.TargetId);
            var model = Json.Des<Fleetdeck.Shared.SystemFacts>(target.FactsJson).ProductVersion ?? pkg.CatalogKey;
            var disk = s.Disks.First(d => d.Id == r.DiskId);
            s.Chosen = disk;
            s.Result = null;
            var j = await jobs.Create(s.ThisId!.Value, Fleetdeck.Shared.JobTypes.BiosUsbWrite, $"USB-stik med BIOS {s.Version} til {s.TargetName}",
                new Fleetdeck.Shared.JobParams
                {
                    BiosUsb = new()
                    {
                        SessionId = s.Id, Phase = Fleetdeck.Shared.BiosUsbPhases.Write, MaxBytes = s.MaxBytes, AllowLarge = r.AllowLarge == true,
                        DiskId = r.DiskId, Serial = r.Serial, Size = r.Size, ConfirmHash = r.ConfirmHash, Layout = s.Layout, FileName = pkg.FileName,
                        Sha256 = pkg.Sha256, PackageSize = pkg.Size, Version = pkg.Version,
                        Readme = BiosPackages.UsbReadme(model, pkg.Version, pkg.FileName, pkg.Sha256, pkg.ShaSource),
                    },
                }, Fleetdeck.Shared.RunAs.System, null, "manual", ctx.User.UserName());
            s.WriteJobId = j.Id;
            await audit.Log(ctx.User.UserName(), "bios.usb.write",
                $"USB-stik skrives på {s.ThisName}: {disk.Vendor} {disk.Model} · serie {disk.Serial} · {disk.Size} B · pakke {pkg.FileName} SHA-256 {pkg.Sha256}", s.TargetId);
            return Results.Ok(new { jobId = j.Id });
        });

        // agenten: engangskoden fra browseren på samme pc (via /_fd/claim)
        app.MapPost("/api/agent/bios/usb/claim", async (Fleetdeck.Shared.BiosUsbClaimRequest r, HttpContext ctx, AppDb db, BiosUsbSessions usb, AuditService audit) =>
        {
            if (await AgentMachine(ctx, db) is not { } m) return Results.Unauthorized();
            if (usb.Claim(r.Nonce ?? "", m) is not { } s) return Results.NotFound();
            await audit.Log($"agent:{m.Label}", "bios.usb.claim", $"Guiden \"Lav USB-stik\" til {s.TargetName} kører på {m.Label}", s.TargetId);
            return Results.Ok(new Fleetdeck.Shared.BiosUsbClaimResponse { SessionId = s.Id, Machine = m.Label });
        });

        // agenten: pakken til netop denne skrivning, kun mens jobbet kører, og kontrolleret mod SHA-256 lige før
        app.MapGet("/api/agent/bios/usb/{sid}/package", async (string sid, HttpContext ctx, AppDb db, BiosPackages pk, BiosUsbSessions usb, AuditService audit) =>
        {
            if (await AgentMachine(ctx, db) is not { } m) return Results.Unauthorized();
            if (usb.Get(sid) is not { } s || s.ThisId != m.Id || s.WriteJobId is not int wj
                || !await db.Jobs.AnyAsync(j => j.Id == wj && j.MachineId == m.Id && j.Status == Fleetdeck.Shared.JobStatus.Running)) return Results.NotFound();
            if (await db.BiosPackages.FirstOrDefaultAsync(p => p.Id == s.PackageId) is not { } pkg) return Results.NotFound();
            if (await pk.Verify(db, pkg) is { } err) return Results.Problem(err, statusCode: 409);
            await audit.Log($"agent:{m.Label}", "bios.usb.package", $"BIOS-pakke hentet til USB-stik: {pkg.FileName} ({pkg.Version})", s.TargetId);
            return Results.File(pk.FullPath(pkg), "application/octet-stream", pkg.FileName);
        });
    }

    /// <summary>
    /// BIOS-opdatering inde fra styresystemet: kun på en administrators udtrykkelige tryk (aldrig "Opdatér alt"), altid med en kopi af
    /// indstillingerne først, og genstarten er et separat valg. Status må alle brugere se.
    /// </summary>
    private static void MapUpdate(WebApplication app, RouteGroupBuilder read, RouteGroupBuilder adm)
    {
        read.MapGet("/machines/{id:int}/update", async (int id, AppDb db) =>
        {
            if (await db.Machines.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id) is not { } m) return Results.NotFound();
            var s = await db.BiosStatus.AsNoTracking().FirstOrDefaultAsync(x => x.MachineId == id);
            var capsule = m.Platform == Fleetdeck.Shared.Platforms.Linux ? await BiosUpdateService.CapsuleKnown(db, s) : null;
            var (route, routeWhy) = BiosUpdateService.RouteFor(m, Json.Des<Fleetdeck.Shared.SystemFacts>(m.FactsJson), s, capsule);
            BiosPackage? pkg = null;
            if (route is not null && s?.Latest is { } latest)
            {
                var kind = BiosUpdateService.PackageKind(route);
                pkg = await db.BiosPackages.AsNoTracking().FirstOrDefaultAsync(p => p.Vendor == s.Vendor && p.Version == latest && p.Kind == kind
                    && s.CatalogKey != null && p.CatalogKey == s.CatalogKey);
            }
            await BiosUpdateService.CloseOrphans(db, id, DateTime.UtcNow);
            var run = await BiosUpdateService.Latest(db, id);
            var active = run is not null && BiosUpdateStates.Active.Contains(run.State) ? run : null;
            var busy = await db.Jobs.AnyAsync(j => j.MachineId == id && (j.Type == Fleetdeck.Shared.JobTypes.BiosUpdate || j.Type == Fleetdeck.Shared.JobTypes.BiosSettingsRead
                || j.Type == Fleetdeck.Shared.JobTypes.BiosSettingsApply) && Fleetdeck.Shared.JobStatus.Active.Contains(j.Status));
            var now = DateTime.UtcNow;
            var checks = BiosUpdateService.ServerChecks(m, s, pkg, active, busy, now, capsule, await BiosUpdateService.PowerPending(db, id));
            var jobStatus = run?.JobId is int jid ? await db.Jobs.AsNoTracking().Where(j => j.Id == jid).Select(j => j.Status).FirstOrDefaultAsync() : null;
            var bootAt = Json.Des<Fleetdeck.Shared.HealthReport>(m.HealthJson).Live.BootAt;
            return Results.Ok(new
            {
                route, routeWhy, capsule, target = s?.Latest, current = s?.Current,
                serverChecks = checks,
                canCheck = route is not null && checks.All(c => c.Id is "package" or "online" or "power" || c.State != Fleetdeck.Shared.BiosCheckStates.Fail),
                canStart = !Fleetdeck.Shared.BiosPreconditions.Blocked(checks),
                package = pkg is null ? null : new { pkg.Id, pkg.FileName, pkg.Size, pkg.State, pkg.ShaSource, pkg.Error },
                run = run is null ? null : BiosUpdateService.View(run, jobStatus, bootAt, now, await BiosUpdateService.RebootAborted(db, run)),
            });
        });

        adm.MapPost("/machines/{id:int}/update/check", async (int id, HttpContext ctx, AppDb db, JobService jobs) =>
        {
            if (await db.Machines.FindAsync(id) is not { } m) return Results.NotFound();
            var (run, job, err) = await BiosUpdateService.Check(db, jobs, m, ctx.User.UserName(), DateTime.UtcNow);
            return run is null ? Results.Problem(err, statusCode: 409) : Results.Ok(new { runId = run.Id, jobId = job!.Id });
        });

        // Start: hent pakken (et tryk), kontrollér SHA-256 lige før, og læg jobbet. Er pakken ikke hentet, startes hentningen, og brugeren prøver igen.
        adm.MapPost("/machines/{id:int}/update/start", async (int id, BiosUpdateService.StartRequest r, HttpContext ctx, AppDb db, JobService jobs, BiosPackages pk) =>
        {
            if (await db.Machines.FindAsync(id) is not { } m) return Results.NotFound();
            var s = await db.BiosStatus.AsNoTracking().FirstOrDefaultAsync(x => x.MachineId == id);
            var (route, why) = await BiosUpdateService.Route(db, m, Json.Des<Fleetdeck.Shared.SystemFacts>(m.FactsJson), s);
            if (route is null) return Results.Problem(why, statusCode: 409);
            if (!r.Confirm || !string.Equals(r.Target?.Trim(), s?.Latest, StringComparison.OrdinalIgnoreCase))
                return Results.Problem($"Bekræft opdateringen til {s?.Latest ?? "den nye version"}.", statusCode: 409);
            var by = ctx.User.UserName();
            var (pkg, perr) = await pk.Request(db, id, BiosUpdateService.PackageKind(route), by);
            if (pkg is null) return Results.Problem(perr, statusCode: 409);
            if (pkg.State != BiosPackages.Ready)
                return Results.Problem("Pakken hentes nu fra leverandøren. Start opdateringen igen, når den er hentet.", statusCode: 409);
            if (await pk.Verify(db, pkg) is { } verr) return Results.Problem(verr, statusCode: 409);
            var (run, job, err) = await BiosUpdateService.Start(db, jobs, m, pkg, r, by, DateTime.UtcNow);
            return run is null ? Results.Problem(err, statusCode: 409) : Results.Ok(new { runId = run.Id, jobId = job!.Id });
        });

        adm.MapPost("/machines/{id:int}/update/reboot", async (int id, HttpContext ctx, AppDb db, JobService jobs) =>
        {
            if (await db.Machines.FindAsync(id) is not { } m) return Results.NotFound();
            var (job, err) = await BiosUpdateService.Reboot(db, jobs, m, ctx.User.UserName(), DateTime.UtcNow);
            return job is null ? Results.Problem(err, statusCode: 409) : Results.Ok(new { jobId = job.Id });
        });

        adm.MapPost("/machines/{id:int}/update/verify", async (int id, HttpContext ctx, AppDb db, JobService jobs) =>
        {
            if (await db.Machines.FindAsync(id) is not { } m) return Results.NotFound();
            var (job, err) = await BiosUpdateService.Verify(db, jobs, m, ctx.User.UserName());
            return job is null ? Results.Problem(err, statusCode: 409) : Results.Ok(new { jobId = job.Id });
        });

        // agenten: kopien før opdateringen og meldinger (også efter genstart og efter en genstart af agenten)
        app.MapPost("/api/agent/bios/update", async (Fleetdeck.Shared.BiosUpdatePost p, HttpContext ctx, AppDb db, Live live) =>
        {
            if (await AgentMachine(ctx, db) is not { } m) return Results.Unauthorized();
            var res = await BiosUpdateService.OnAgentPost(db, m, p, DateTime.UtcNow);
            await live.Changed("machines", m.Id);
            return Results.Ok(res);
        });

        // agenten: pakken til netop denne kørsel, kun mens den gøres klar, og kontrolleret mod SHA-256 lige før
        app.MapGet("/api/agent/bios/update/{runId:int}/package", async (int runId, HttpContext ctx, AppDb db, BiosPackages pk, AuditService audit) =>
        {
            if (await AgentMachine(ctx, db) is not { } m) return Results.Unauthorized();
            var run = await db.BiosUpdateRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId && r.MachineId == m.Id && r.State == BiosUpdateStates.Staging);
            if (run?.PackageId is not int pid || await db.BiosPackages.FirstOrDefaultAsync(p => p.Id == pid) is not { } pkg) return Results.NotFound();
            // kun kørslens eget, kørende job (ikke et kopieret "Prøv igen"-job eller et annulleret job)
            if (!await BiosUpdateService.OwnJobRunning(db, run, m.Id, null)) return Results.NotFound();
            if (await pk.Verify(db, pkg) is { } err) return Results.Problem(err, statusCode: 409);
            await audit.Log($"agent:{m.Label}", "bios.update.package", $"BIOS-pakke hentet af agenten: {pkg.FileName} ({pkg.Version})", m.Id);
            return Results.File(pk.FullPath(pkg), "application/octet-stream", pkg.FileName);
        });
    }

    private static async Task<Machine?> AgentMachine(HttpContext ctx, AppDb db)
    {
        var h = ctx.Request.Headers.Authorization.ToString();
        return h.StartsWith("Bearer ", StringComparison.Ordinal) ? await AgentAuth.Resolve(db, h[7..].Trim()) : null;
    }

    /// <summary>
    /// BIOS-indstillinger: læs (kopi), se en kopi, sammenlign to kopier, gendan valgte værdier og kontrollér bagefter.
    /// Læsning og sammenligning må alle brugere se; at læse og gendanne kræver administrator.
    /// </summary>
    private static void MapSettings(WebApplication app, RouteGroupBuilder read, RouteGroupBuilder adm)
    {
        adm.MapPost("/machines/{id:int}/settings/read", async (int id, ReadRequest? r, HttpContext ctx, AppDb db, JobService jobs) =>
        {
            if (await db.Machines.FindAsync(id) is not { } m) return Results.NotFound();
            var reason = r?.Reason is { } x && BiosSnapshotReasons.ByUser.Contains(x) ? x : BiosSnapshotReasons.Manual;
            var (job, err) = await BiosSettingsService.RequestRead(db, jobs, m, reason, ctx.User.UserName());
            return job is null ? Results.Problem(err, statusCode: 409) : Results.Ok(new { jobId = job.Id });
        });

        read.MapGet("/snapshots/{sid:int}", async (int sid, AppDb db) =>
            await db.BiosSnapshots.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sid) is { } s
                ? Results.Ok(new { snapshot = BiosSettingsService.Meta(s), items = BiosSettingsService.Items(s) }) : Results.NotFound());

        // sammenlign: standard er den nyeste kopi mod den seneste "før opdatering" (ellers den forrige med andre værdier)
        read.MapGet("/machines/{id:int}/settings/diff", async (int id, int? from, int? to, AppDb db) =>
        {
            if (await db.Machines.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id) is not { } m) return Results.NotFound();
            var all = await db.BiosSnapshots.AsNoTracking().Where(x => x.MachineId == id).OrderByDescending(x => x.TakenAt).ThenByDescending(x => x.Id).ToListAsync();
            var (df, dt) = BiosSettingsService.DefaultPair(all);
            var a = from is int f ? all.FirstOrDefault(x => x.Id == f) : df;
            var b = to is int t ? all.FirstOrDefault(x => x.Id == t) : dt;
            if (a is null || b is null) return Results.Problem("Der skal være to kopier at sammenligne.", statusCode: 404);
            var rows = BiosRestorePlanner.Diff(BiosSettingsService.Items(a), BiosSettingsService.Items(b), b.Driver);
            var info = Json.Des<Fleetdeck.Shared.SystemFacts>(m.FactsJson).BiosSettings;
            var latest = all[0];
            var updating = await BiosUpdateService.UpdateBusy(db, id);
            string? cannot = b.Id != latest.Id ? "Gendannelse sker altid mod den nyeste kopi."
                : updating ? BiosUpdateService.UpdateBusyText
                : DateTime.UtcNow - b.TakenAt > BiosSettingsService.FreshFor ? "Den nyeste kopi er for gammel. Læs indstillingerne igen først."
                : b.PendingReboot == true || info?.PendingReboot == true ? "Der venter allerede BIOS-ændringer. Genstart maskinen først."
                : a.Driver != b.Driver ? "Kopierne er læst med forskellige drivere."
                : !Fleetdeck.Shared.BiosSettingsDrivers.Writable(b.Driver) ? $"Fleetdeck kan endnu kun læse BIOS-indstillinger med {b.Driver}."
                : null;
            return Results.Ok(new
            {
                from = BiosSettingsService.Meta(a), to = BiosSettingsService.Meta(b), rows,
                counts = new
                {
                    all = rows.Count, changed = rows.Count(x => x.State == BiosDiffStates.Changed), preselected = rows.Count(x => x.Preselected),
                    ask = rows.Count(x => x.State == BiosDiffStates.Changed && x.Selectable && x.Rule == BiosRuleClasses.Ask),
                    never = rows.Count(x => x.State == BiosDiffStates.Changed && x.Rule == BiosRuleClasses.Never),
                    invalid = rows.Count(x => x.State == BiosDiffStates.Changed && !x.Valid),
                    added = rows.Count(x => x.State == BiosDiffStates.New), gone = rows.Count(x => x.State == BiosDiffStates.Gone),
                },
                canRestore = cannot is null, cannotRestore = cannot, adminPassword = b.AdminPassword || info?.AdminPassword == true,
            });
        });

        // gendan: én skrivning med én gemning; adgangskoden bliver kun i serverens hukommelse (BiosSecrets)
        adm.MapPost("/machines/{id:int}/settings/restore", async (int id, BiosSettingsService.RestoreRequest r, HttpContext ctx, AppDb db, JobService jobs, BiosSecrets secrets) =>
        {
            if (await db.Machines.FindAsync(id) is not { } m) return Results.NotFound();
            var (job, errors) = await BiosSettingsService.RequestRestore(db, jobs, secrets, m, r, ctx.User.UserName(), DateTime.UtcNow);
            return job is null ? Results.Json(new { errors }, statusCode: 409) : Results.Ok(new { jobId = job.Id });
        });

        // efter genstarten: grøn/gul/rød for den seneste gendannelse
        read.MapGet("/machines/{id:int}/settings/check", async (int id, AppDb db) =>
        {
            var apply = await db.Jobs.AsNoTracking().Where(j => j.MachineId == id && j.Type == Fleetdeck.Shared.JobTypes.BiosSettingsApply)
                .OrderByDescending(j => j.Id).FirstOrDefaultAsync();
            if (apply is null) return Results.NotFound();
            var expected = Json.Des<Fleetdeck.Shared.JobParams>(apply.ParamsJson).BiosSettings ?? [];
            var after = apply.Finished is DateTime done
                ? await db.BiosSnapshots.AsNoTracking().Where(s => s.MachineId == id && s.Reason == BiosSnapshotReasons.PostRestore && s.TakenAt > done)
                    .OrderByDescending(s => s.TakenAt).FirstOrDefaultAsync()
                : null;
            return Results.Ok(new
            {
                jobId = apply.Id, apply.Status, apply.Finished, expected = expected.Count,
                snapshot = after is null ? null : BiosSettingsService.Meta(after),
                rows = after is null ? null : BiosRestorePlanner.Check(expected, BiosSettingsService.Items(after)),
            });
        });

        // agenten: byt engangsbilletten til adgangskoden – kun til det kørende gendannelsesjob, kun én gang
        app.MapPost("/api/agent/bios/secret", async (Fleetdeck.Shared.BiosSecretRequest r, HttpContext ctx, AppDb db, BiosSecrets secrets, AuditService audit) =>
        {
            var h = ctx.Request.Headers.Authorization.ToString();
            if ((h.StartsWith("Bearer ", StringComparison.Ordinal) ? await AgentAuth.Resolve(db, h[7..].Trim()) : null) is not { } m) return Results.Unauthorized();
            var job = await db.Jobs.FirstOrDefaultAsync(x => x.Id == r.JobId && x.MachineId == m.Id && x.Type == Fleetdeck.Shared.JobTypes.BiosSettingsApply
                && x.Status == Fleetdeck.Shared.JobStatus.Running);
            var prm = job is null ? null : Json.Des<Fleetdeck.Shared.JobParams>(job.ParamsJson);
            if (job is null || prm?.Ticket is not { Length: > 0 } t || !Crypto.FixedEquals(t, r.Ticket ?? "") || secrets.Take(job.Id, t) is not { } pw)
            {
                await audit.Log("agent", "bios.settings.secret_denied", $"job {r.JobId} på {m.Label}", m.Id);
                return Results.NotFound();
            }
            prm.Ticket = null;   // brugt
            job.ParamsJson = Json.Ser(prm);
            await db.SaveChangesAsync();
            await audit.Log("agent", "bios.settings.secret", $"BIOS-adgangskode til job {job.Id} på {m.Label}", m.Id);
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new Fleetdeck.Shared.BiosSecretResponse { Value = pw });
        });
    }

    /// <summary>Bagud først, derefter usikre, nyeste, og til sidst dem uden BIOS-funktion.</summary>
    private static int Order(string? state) => state switch
    {
        BiosStates.Newer => 0, BiosStates.Fwupd => 1, BiosStates.Uncertain => 2, BiosStates.CatalogError => 3, BiosStates.Unchecked => 4,
        BiosStates.Newest => 5, BiosStates.Unsupported => 6, _ => 7,
    };

    public sealed record ChangeLineView(string text, bool important);
    public sealed record ChangeView(string version, string? wu, List<ChangeLineView> lines);
    public sealed record StatusView(int machineId, string vendor, string? catalogKey, string? matchQuality, string? current, string? currentNumeric,
        string? currentDate, string? latest, string? latestNumeric, string? latestDate, string state, bool critical, List<ChangeView> changes,
        string? detail, string? fwupdVersion, DateTime checkedAt, string source);

    public static StatusView View(MachineBiosStatus s)
    {
        List<ChangeView> changes;
        try
        {
            changes = (JsonSerializer.Deserialize<List<BiosChange>>(s.ChangeSummaryJson, Json.Web) ?? [])
                .Select(c => new ChangeView(c.Version, c.Wu, c.Lines.Select(l => new ChangeLineView(l.Text, l.Important)).ToList())).ToList();
        }
        catch (JsonException) { changes = []; }
        return new(s.MachineId, s.Vendor, s.CatalogKey, s.MatchQuality, s.Current, s.CurrentNumeric, s.CurrentDate, s.Latest, s.LatestNumeric,
            s.LatestDate, s.State, s.Critical, changes, s.Detail, s.FwupdVersion, s.CheckedAt, s.Source);
    }
}
