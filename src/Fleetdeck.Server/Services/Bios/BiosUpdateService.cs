using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Shared;

namespace Fleetdeck.Server.Services.Bios;

/// <summary>
/// Tilstandsmaskinen for én BIOS-opdatering. Rene funktioner (testbare uden database):
/// checking → checked/blocked/failed; staging → staged/blocked/failed; staged → updated/rejected/not_applied/unknown efter genstarten.
/// Agentens melding vinder over serverens gæt (fx et job, der blev lukket ved en genstart).
/// </summary>
public static class BiosUpdateFlow
{
    /// <summary>Så længe efter genstarten venter vi på agentens melding, før vi siger "ingen melding".</summary>
    public static readonly TimeSpan ReportGrace = TimeSpan.FromMinutes(30);
    /// <summary>En tjek- eller klargøringskørsel uden svar i så lang tid regnes som gået i stå.</summary>
    public static readonly TimeSpan Stale = TimeSpan.FromHours(3);

    /// <summary>Den nye tilstand for en melding, eller null når meldingen ikke passer (den ignoreres).</summary>
    public static string? Next(string state, string outcome) => (state, outcome) switch
    {
        (BiosUpdateStates.Checking, BiosUpdateOutcomes.Checked) => BiosUpdateStates.Checked,
        (BiosUpdateStates.Checking or BiosUpdateStates.Staging, BiosUpdateOutcomes.Blocked) => BiosUpdateStates.Blocked,
        (BiosUpdateStates.Checking or BiosUpdateStates.Staging, BiosUpdateOutcomes.Failed) => BiosUpdateStates.Failed,
        (BiosUpdateStates.Staging or BiosUpdateStates.Staged or BiosUpdateStates.Failed, BiosUpdateOutcomes.Staged) => BiosUpdateStates.Staged,
        (BiosUpdateStates.Staging or BiosUpdateStates.Staged or BiosUpdateStates.Failed or BiosUpdateStates.NotApplied or BiosUpdateStates.Unknown,
            BiosUpdateOutcomes.Updated or BiosUpdateOutcomes.Rejected or BiosUpdateOutcomes.NotApplied or BiosUpdateOutcomes.Unknown) => outcome,
        _ => null,
    };

    public static bool Terminal(string state) => !BiosUpdateStates.Active.Contains(state);

    /// <summary>Anvend agentens melding. False = ignoreret (passer ikke til tilstanden).</summary>
    public static bool Apply(BiosUpdateRun run, BiosUpdateReport r, DateTime now)
    {
        if (Next(run.State, r.Outcome) is not { } next) return false;
        // en "venter stadig"-kontrol før genstarten ændrer ikke på forudsætningerne fra klargøringen
        if (r.Checks.Count > 0) run.ChecksJson = Json.Ser(r.Checks);
        run.State = next;
        run.Message = r.Message.Length > 500 ? r.Message[..500] : r.Message;
        run.ReportJson = Json.Ser(r);
        run.UpdatedAt = now;
        if (next == BiosUpdateStates.Staged)
        {
            // kontrollen efter en genstart fandt kapslen stadig ventende: venter nu på en ny genstart
            if (r.StillPending && r.StagedAt is DateTime again && (run.StagedAt is null || again > run.StagedAt)) { run.StagedAt = again; run.RebootRequestedAt = null; }
            else run.StagedAt ??= r.StagedAt ?? now;
        }
        if (r.CurrentVersion is { Length: > 0 } cv && run.FromVersion is null) run.FromVersion = cv;
        run.FinishedAt = Terminal(next) ? now : null;
        return true;
    }

    /// <summary>
    /// Det, brugeren skal se lige nu. Ud over tilstanden: rebooting (genstart bestilt), awaiting_report (genstartet, venter på agenten),
    /// no_report (genstartet eller bestilt for længe siden uden melding – kan hænge i BIOS'en) og stale (tjek/klargøring uden svar).
    /// </summary>
    /// <param name="rebootAborted">Genstartsjobbet, der blev bestilt, fejlede eller blev annulleret: så er der ikke genstartet.</param>
    public static string Effective(BiosUpdateRun run, DateTime? bootAt, DateTime now, bool rebootAborted = false)
    {
        if (run.State is BiosUpdateStates.Checking or BiosUpdateStates.Staging) return now - run.UpdatedAt > Stale ? "stale" : run.State;
        if (run.State != BiosUpdateStates.Staged || run.StagedAt is not DateTime staged) return run.State;
        if (bootAt is DateTime b && b > staged) return now - b <= ReportGrace ? "awaiting_report" : "no_report";
        if (run.RebootRequestedAt is DateTime t && !rebootAborted) return now - t <= ReportGrace ? "rebooting" : "no_report";
        return run.State;
    }
}

/// <summary>BIOS-opdatering inde fra styresystemet: kun på en administrators tryk, aldrig i "Opdatér alt", altid med kopi først.</summary>
public static class BiosUpdateService
{
    public const string Source = "bios";
    public sealed record StartRequest(bool Confirm, string? Target);

    public static Task<BiosUpdateRun?> Latest(AppDb db, int machineId) =>
        db.BiosUpdateRuns.Where(r => r.MachineId == machineId).OrderByDescending(r => r.Id).FirstOrDefaultAsync();

    /// <summary>
    /// Et tjek eller en klargøring, hvis job er slut uden melding fra agenten (annulleret, udløbet, lukket af serveren): kørslen lukkes som
    /// mislykket, så den ikke spærrer for altid og ikke kan genoptages af et kopieret job. Har agenten nået at lægge noget klar, melder den det
    /// selv bagefter (failed → staged er tilladt).
    /// </summary>
    public static async Task CloseOrphans(AppDb db, int machineId, DateTime now)
    {
        var runs = await db.BiosUpdateRuns.Where(r => r.MachineId == machineId && (r.State == BiosUpdateStates.Checking || r.State == BiosUpdateStates.Staging)).ToListAsync();
        var changed = false;
        foreach (var run in runs)
        {
            var status = run.JobId is int jid ? await db.Jobs.AsNoTracking().Where(j => j.Id == jid).Select(j => j.Status).FirstOrDefaultAsync() : null;
            if (Alive(status, run.CreatedAt, now)) continue;
            changed |= BiosUpdateFlow.Apply(run, new BiosUpdateReport
            {
                Outcome = BiosUpdateOutcomes.Failed,
                Message = run.State == BiosUpdateStates.Checking
                    ? "Tjekket sluttede uden svar fra agenten (annulleret, udløbet eller afbrudt). Intet er ændret."
                    : "Klargøringen sluttede uden svar fra agenten (annulleret, udløbet eller afbrudt). Har agenten nået at lægge opdateringen klar, melder den det selv.",
            }, now);
        }
        if (changed) await db.SaveChangesAsync();
    }

    /// <summary>En BIOS-opdatering er i gang eller ligger klar (job eller kørsel): så må BIOS-indstillinger ikke gendannes.</summary>
    public static async Task<bool> UpdateBusy(AppDb db, int machineId) =>
        await db.Jobs.AnyAsync(j => j.MachineId == machineId && j.Type == JobTypes.BiosUpdate && JobStatus.Active.Contains(j.Status))
        || await db.BiosUpdateRuns.AnyAsync(r => r.MachineId == machineId && BiosUpdateStates.Active.Contains(r.State));

    public const string UpdateBusyText = "En BIOS-opdatering er i gang eller ligger klar på maskinen. Gendan indstillingerne, når den er installeret.";

    /// <summary>
    /// Lever et tjek eller en klargøring endnu? Ja, mens dens job er aktivt, eller kort efter oprettelsen (jobbet er ved at blive oprettet).
    /// Et job, der er udløbet, annulleret eller slut uden melding, betyder en forældreløs kørsel. Samme regel i CloseOrphans og Waiting.
    /// </summary>
    public static bool Alive(string? jobStatus, DateTime createdAt, DateTime now) =>
        jobStatus is not null ? JobStatus.Active.Contains(jobStatus) : now - createdAt < TimeSpan.FromMinutes(5);

    /// <summary>
    /// Ligger der en BIOS-opdatering klar (eller er den ved at blive lagt klar)? Så installeres den ved næste genstart, og alle automatiske
    /// genstarter, slukninger og vækninger – opdateringsmotoren (vedligehold, planer, "Opdatér alt"), Home Assistant og baggrundens auto-væk –
    /// springer maskinen over. Den ene regel for dem alle. En klargøring tæller kun, mens den lever (<see cref="Alive"/>): en forældreløs
    /// klargøring (jobbet udløb eller blev annulleret) spærrer ikke for altid, selv om ingen åbner BIOS-siden.
    /// </summary>
    public static async Task<bool> Waiting(AppDb db, int machineId, DateTime? at = null)
    {
        if (await db.BiosUpdateRuns.AnyAsync(r => r.MachineId == machineId && r.State == BiosUpdateStates.Staged)) return true;
        var now = at ?? DateTime.UtcNow;
        var staging = await db.BiosUpdateRuns.AsNoTracking().Where(r => r.MachineId == machineId && r.State == BiosUpdateStates.Staging)
            .Select(r => new { r.JobId, r.CreatedAt }).ToListAsync();
        foreach (var s in staging)
        {
            var status = s.JobId is int jid ? await db.Jobs.AsNoTracking().Where(j => j.Id == jid).Select(j => j.Status).FirstOrDefaultAsync() : null;
            if (Alive(status, s.CreatedAt, now)) return true;
        }
        return false;
    }

    /// <summary>Grunden, når en genstart, slukning eller vækning springes over, fordi en BIOS-opdatering ligger klar.</summary>
    public const string WaitingText = "En BIOS-opdatering ligger klar (eller gøres klar) og installeres ved næste opstart. Brug \"Genstart for at installere BIOS\" på BIOS-siden, når nogen kan holde øje.";

    /// <summary>Så længe efter en genstart med en klar BIOS regnes maskinen for at kunne være midt i flashen.</summary>
    public static readonly TimeSpan FlashGuard = TimeSpan.FromMinutes(60);
    /// <summary>Hængefristen for en genstart, der installerer en BIOS (en Lenovo-flash kan holde maskinen væk i flere minutter).</summary>
    public const int FlashHangMinutes = 30;

    public const string FlashingText = "En BIOS-opdatering kan være ved at blive installeret. Afbryd ikke strømmen – en afbrudt flash kan ødelægge maskinen. Vent, eller se til maskinen på stedet.";

    /// <summary>
    /// Kan maskinen være midt i en BIOS-flash? En BIOS ligger klar, og der er bestilt en genstart til den, eller en genstart/slukning er
    /// i gang eller hænger – inden for <see cref="FlashGuard"/>. Så må strømmen ikke slås fra og til (Home Assistant eller Intel AMT).
    /// </summary>
    public static async Task<bool> MayBeFlashing(AppDb db, int machineId, DateTime now)
    {
        var requested = await db.BiosUpdateRuns.AsNoTracking().Where(r => r.MachineId == machineId && r.State == BiosUpdateStates.Staged)
            .Select(r => r.RebootRequestedAt).ToListAsync();
        if (requested.Count == 0) return false;
        var since = now - FlashGuard;
        if (requested.Any(t => t > since)) return true;
        return await db.Jobs.AnyAsync(j => j.MachineId == machineId && (j.Type == JobTypes.Reboot || j.Type == JobTypes.Shutdown)
            && (j.Status == JobStatus.Restarting || j.Status == JobStatus.Hung) && j.PowerAt > since);
    }

    /// <summary>
    /// Er der allerede bestilt en genstart eller slukning (vedligehold, Home Assistant, manuelt)? Den kører, uanset hvad der sker bagefter,
    /// og ville installere en BIOS, der lægges klar imens – uden opsyn. Så startes klargøringen ikke.
    /// </summary>
    public static Task<bool> PowerPending(AppDb db, int machineId) =>
        db.Jobs.AnyAsync(j => j.MachineId == machineId && (j.Type == JobTypes.Reboot || j.Type == JobTypes.Shutdown) && JobStatus.Active.Contains(j.Status));

    public const string PowerPendingText = "Der er allerede bestilt en genstart eller slukning af maskinen (fx fra vedligehold eller Home Assistant). Den ville installere BIOS'en uden opsyn – annullér den først, eller vent til den er overstået.";

    /// <summary>Det job, der lige nu må hente pakken og sende kopien: kørslens eget job, og kun mens det kører.</summary>
    public static Task<bool> OwnJobRunning(AppDb db, BiosUpdateRun run, int machineId, int? jobId) =>
        run.JobId is int jid && (jobId is null || jobId == jid)
            ? db.Jobs.AnyAsync(j => j.Id == jid && j.MachineId == machineId && j.Status == JobStatus.Running)
            : Task.FromResult(false);

    /// <summary>Genstartsjobbet bestilt til kørslen fejlede, blev annulleret eller udløb (så er maskinen ikke genstartet af det).</summary>
    public static async Task<bool> RebootAborted(AppDb db, BiosUpdateRun run)
    {
        if (run.RebootRequestedAt is not DateTime t) return false;
        var status = await db.Jobs.AsNoTracking()
            .Where(j => j.MachineId == run.MachineId && j.Type == JobTypes.Reboot && j.Source == Source && j.Created >= t.AddMinutes(-1))
            .OrderByDescending(j => j.Id).Select(j => j.Status).FirstOrDefaultAsync();
        return status is JobStatus.Failed or JobStatus.Cancelled or JobStatus.Expired;
    }

    private static Task<BiosUpdateRun?> ActiveRun(AppDb db, int machineId) =>
        db.BiosUpdateRuns.Where(r => r.MachineId == machineId && BiosUpdateStates.Active.Contains(r.State)).OrderByDescending(r => r.Id).FirstOrDefaultAsync();

    private static Task<bool> BiosJobBusy(AppDb db, int machineId) =>
        db.Jobs.AnyAsync(j => j.MachineId == machineId && (j.Type == JobTypes.BiosUpdate || j.Type == JobTypes.BiosSettingsRead || j.Type == JobTypes.BiosSettingsApply)
            && JobStatus.Active.Contains(j.Status));

    public const string RawRomText = "Lenovos pakke til denne model er en rå ROM til USB-værktøjet, ikke en UEFI-kapsel, så den kan ikke lægges klar inde fra Linux. Brug USB-stikket (Secure Boot slås fra, mens der flashes, og til igen bagefter).";
    public const string NoCapsuleSeenText = "Opdatering inde fra Linux kræver en UEFI-kapsel, og Fleetdeck har ikke set en i Lenovos pakke. Brug USB-stikket (Secure Boot slås fra, mens der flashes, og til igen bagefter).";

    /// <summary>
    /// Vejen for maskinen: capsule (Linux, kun når den hentede pakke indeholder en rigtig UEFI-kapsel) eller lenovo-exe (Windows); ellers null
    /// og en forklaring. Lenovos USB-pakker til ThinkCentre er en rå ROM – så er USB-stikket den eneste vej på Linux.
    /// </summary>
    public static (string? Route, string? Error) RouteFor(Machine m, SystemFacts f, MachineBiosStatus? s, bool? capsule = null)
    {
        if (f.IsVirtual == true) return (null, "Virtuel maskine. BIOS'en styres af værten.");
        if (s is null || s.Vendor != BiosMatcher.Lenovo) return (null, "Opdatering inde fra styresystemet findes indtil videre kun til Lenovo.");
        return m.Platform switch
        {
            Platforms.Linux when s.State != BiosStates.Pi => capsule switch
            {
                true => (BiosUpdateRoutes.Capsule, null),
                false => (null, RawRomText),
                null => (null, NoCapsuleSeenText),
            },
            Platforms.Windows => (BiosUpdateRoutes.LenovoExe, null),
            _ => (null, BiosUpdateText.NotSupported),
        };
    }

    /// <summary>Den hentede USB-pakke til maskinens nyeste BIOS: indeholder den en rigtig UEFI-kapsel? Null = ikke hentet (eller ingen ROM).</summary>
    public static async Task<bool?> CapsuleKnown(AppDb db, MachineBiosStatus? s)
    {
        if (s?.Latest is not { } latest || s.CatalogKey is not { } key) return null;
        return await db.BiosPackages.AsNoTracking()
            .Where(p => p.Vendor == s.Vendor && p.CatalogKey == key && p.Version == latest && p.Kind == BiosPackages.UsbZip && p.State == BiosPackages.Ready)
            .OrderByDescending(p => p.Id).Select(p => p.Capsule).FirstOrDefaultAsync();
    }

    /// <summary>RouteFor med det, Fleetdeck ved om pakken (kapsel eller rå ROM).</summary>
    public static async Task<(string? Route, string? Error)> Route(AppDb db, Machine m, SystemFacts f, MachineBiosStatus? s) =>
        RouteFor(m, f, s, m.Platform == Platforms.Linux ? await CapsuleKnown(db, s) : null);

    public static string PackageKind(string route) => route == BiosUpdateRoutes.LenovoExe ? BiosPackages.WinExe : BiosPackages.UsbZip;

    /// <summary>Det, serveren selv kan se, før agenten spørges. Én "fail" = knappen er slået fra.</summary>
    public static List<BiosCheck> ServerChecks(Machine m, MachineBiosStatus? s, BiosPackage? pkg, BiosUpdateRun? active, bool busy, DateTime now, bool? capsule = null,
        bool powerPending = false)
    {
        var f = Json.Des<SystemFacts>(m.FactsJson);
        var l = new List<BiosCheck>();
        var (route, why) = RouteFor(m, f, s, capsule);
        l.Add(route is null ? BiosCheck.Fail("route", why!) : BiosCheck.Ok("route", route == BiosUpdateRoutes.Capsule
            ? "Opdateres inde fra Linux med en UEFI-kapsel (fwupd). Secure Boot kan blive slået til."
            : "Opdateres inde fra Windows med Lenovos eget program."));
        if (s is null || s.State != BiosStates.Newer || s.MatchQuality == "uncertain" || s.Latest is null)
            l.Add(BiosCheck.Fail("newer", s?.State == BiosStates.Newest ? "Maskinen har allerede den nyeste BIOS." : "Fleetdeck kender ingen sikker, nyere BIOS til maskinen."));
        else l.Add(BiosCheck.Ok("newer", $"{s.Latest} er nyere end {s.Current}."));
        l.Add(f.BiosSettings is null ? BiosCheck.Fail("snapshot", BiosUpdateText.NoSettings)
            : BiosCheck.Ok("snapshot", "Fleetdeck tager automatisk en kopi af BIOS-indstillingerne som første trin."));
        l.Add(pkg is { State: BiosPackages.Ready, Sha256.Length: 64 }
            ? BiosCheck.Ok("package", $"Pakken {pkg.FileName} er hentet og har en kendt kontrolsum ({(pkg.ShaSource == "catalog" ? "fra leverandøren" : "låst ved første hentning")}).")
            : BiosCheck.Warn("package", "Pakken er ikke hentet endnu. Den hentes fra leverandøren, når du trykker på knappen."));
        if (active is not null) l.Add(BiosCheck.Fail("active", "Der er allerede en BIOS-opdatering i gang på maskinen."));
        else if (busy) l.Add(BiosCheck.Fail("busy", "Der kører allerede et BIOS-job på maskinen."));
        if (active is null && powerPending) l.Add(BiosCheck.Fail("power", PowerPendingText));
        if (m.LastSeen is not DateTime seen || now - seen > TimeSpan.FromMinutes(5))
            l.Add(BiosCheck.Warn("online", "Maskinen er ikke online lige nu. Jobbet venter, til den melder sig."));
        return l;
    }

    private static string Family(MachineBiosStatus s) =>
        s.CatalogKey?.Split('/')[0] is { Length: > 0 } k ? k.ToLowerInvariant() : LenovoBiosCode.Family(s.Current) ?? "";

    private static BiosUpdateOrder Order(BiosUpdateRun run, string phase, BiosPackage? p) => new()
    {
        RunId = run.Id, Phase = phase, Route = run.Route, Vendor = run.Vendor, Family = run.Family, FromVersion = run.FromVersion,
        TargetVersion = run.TargetVersion, TargetNumeric = run.TargetNumeric,
        PackageUrl = p is null ? null : $"/api/agent/bios/update/{run.Id}/package", FileName = p?.FileName, Size = p?.Size, Sha256 = p?.Sha256, PackageKind = p?.Kind,
    };

    private static async Task<(BiosUpdateRun? Run, string? Error)> NewRun(AppDb db, Machine m, string state, string by, DateTime now)
    {
        var s = await db.BiosStatus.AsNoTracking().FirstOrDefaultAsync(x => x.MachineId == m.Id);
        var f = Json.Des<SystemFacts>(m.FactsJson);
        var (route, why) = await Route(db, m, f, s);
        if (route is null) return (null, why);
        if (s is not { State: BiosStates.Newer, Latest: { Length: > 0 } latest } || s.MatchQuality == "uncertain")
            return (null, "Fleetdeck kender ingen sikker, nyere BIOS til maskinen.");
        if (f.BiosSettings is null) return (null, BiosUpdateText.NoSettings);
        await CloseOrphans(db, m.Id, now);
        if (await ActiveRun(db, m.Id) is not null) return (null, "Der er allerede en BIOS-opdatering i gang på maskinen.");
        if (await BiosJobBusy(db, m.Id)) return (null, "Der kører allerede et BIOS-job på maskinen.");
        if (state == BiosUpdateStates.Staging && await PowerPending(db, m.Id)) return (null, PowerPendingText);
        var run = new BiosUpdateRun
        {
            MachineId = m.Id, CreatedBy = by.Length > 100 ? by[..100] : by, CreatedAt = now, UpdatedAt = now, Route = route, Vendor = s.Vendor,
            Family = Family(s), FromVersion = s.Current ?? f.BiosVersion, TargetVersion = latest, TargetNumeric = s.LatestNumeric, State = state,
        };
        db.BiosUpdateRuns.Add(run);
        await db.SaveChangesAsync();
        return (run, null);
    }

    /// <summary>Tjek forudsætningerne på maskinen (ændrer intet).</summary>
    public static async Task<(BiosUpdateRun? Run, Job? Job, string? Error)> Check(AppDb db, JobService jobs, Machine m, string by, DateTime now)
    {
        var (run, err) = await NewRun(db, m, BiosUpdateStates.Checking, by, now);
        if (run is null) return (null, null, err);
        var job = await jobs.Create(m.Id, JobTypes.BiosUpdate, $"BIOS-opdatering til {run.TargetVersion}: tjek forudsætninger",
            new JobParams { BiosUpdate = Order(run, BiosUpdatePhases.Check, null) }, RunAs.System, null, Source, by, save: false);
        await db.SaveChangesAsync();
        run.JobId = job.Id;
        await jobs.Commit(m.Id);
        return (run, job, null);
    }

    /// <summary>
    /// Start opdateringen: kopi af indstillingerne → forudsætninger → pakke → læg klar. Kræver bekræftelse af netop den version, siden
    /// viste, og en hentet pakke med kendt kontrolsum (kontrolleret lige før af kalderen). Genstarten er et separat valg.
    /// </summary>
    public static async Task<(BiosUpdateRun? Run, Job? Job, string? Error)> Start(AppDb db, JobService jobs, Machine m, BiosPackage pkg, StartRequest r, string by, DateTime now)
    {
        var s = await db.BiosStatus.AsNoTracking().FirstOrDefaultAsync(x => x.MachineId == m.Id);
        if (!r.Confirm || s?.Latest is not { } latest || !string.Equals(r.Target?.Trim(), latest, StringComparison.OrdinalIgnoreCase))
            return (null, null, $"Bekræft opdateringen til {s?.Latest ?? "den nye version"}.");
        if (pkg.State != BiosPackages.Ready || pkg.Sha256 is not { Length: 64 }) return (null, null, "Pakken er ikke hentet og kontrolleret endnu.");
        var (run, err) = await NewRun(db, m, BiosUpdateStates.Staging, by, now);
        if (run is null) return (null, null, err);
        if (pkg.Kind != PackageKind(run.Route) || !string.Equals(pkg.Version, run.TargetVersion, StringComparison.OrdinalIgnoreCase))
        {
            db.BiosUpdateRuns.Remove(run);
            await db.SaveChangesAsync();
            return (null, null, "Pakken passer ikke til maskinen.");
        }
        run.PackageId = pkg.Id;
        var job = await jobs.Create(m.Id, JobTypes.BiosUpdate, $"BIOS-opdatering til {run.TargetVersion}: kopi af indstillinger og klargøring",
            new JobParams { BiosUpdate = Order(run, BiosUpdatePhases.Stage, pkg) }, RunAs.System, null, Source, by, save: false);
        db.Audit.Add(AuditService.Entry(by, "bios.update.start",
            $"BIOS-opdatering startet: {run.FromVersion} → {run.TargetVersion} ({run.Route}, {pkg.FileName}, SHA-256 {pkg.Sha256})", m.Id));
        await db.SaveChangesAsync();
        run.JobId = job.Id;
        await jobs.Commit(m.Id);
        return (run, job, null);
    }

    /// <summary>Genstart nu (brugerens valg), når opdateringen ligger klar.</summary>
    public static async Task<(Job? Job, string? Error)> Reboot(AppDb db, JobService jobs, Machine m, string by, DateTime now)
    {
        if (await ActiveRun(db, m.Id) is not { State: BiosUpdateStates.Staged } run) return (null, "Der ligger ingen BIOS-opdatering klar på maskinen.");
        // højst én genstart ad gangen: en ekstra genstart lige efter flashen ville afbryde kontrollen (og på Windows bruge en BitLocker-pause)
        if (await db.Jobs.AnyAsync(j => j.MachineId == m.Id && (j.Type == JobTypes.Reboot || j.Type == JobTypes.Shutdown) && JobStatus.Active.Contains(j.Status)))
            return (null, "Der er allerede bestilt en genstart eller slukning af maskinen.");
        var job = await jobs.Create(m.Id, JobTypes.Reboot, "Genstart for at installere BIOS-opdateringen",
            new JobParams { Delay = 60, OnlyIfPending = false, Message = "Maskinen genstarter om et minut for at installere en BIOS-opdatering. Sluk ikke for strømmen; den kan være væk i flere minutter." },
            RunAs.System, null, Source, by, save: false);
        run.RebootRequestedAt = now;
        run.UpdatedAt = now;
        await jobs.Commit(m.Id);
        return (job, null);
    }

    /// <summary>Kontrollér resultatet nu (fx hvis agentens automatiske melding efter genstarten udebliver).</summary>
    public static async Task<(Job? Job, string? Error)> Verify(AppDb db, JobService jobs, Machine m, string by)
    {
        var run = await Latest(db, m.Id);
        if (run is null || run.State is not (BiosUpdateStates.Staged or BiosUpdateStates.NotApplied or BiosUpdateStates.Unknown or BiosUpdateStates.Failed)
            || run.State == BiosUpdateStates.Failed && run.StagedAt is null)
            return (null, "Der er ingen BIOS-opdatering at kontrollere.");
        if (await BiosJobBusy(db, m.Id)) return (null, "Der kører allerede et BIOS-job på maskinen.");
        var job = await jobs.Create(m.Id, JobTypes.BiosUpdate, $"BIOS-opdatering til {run.TargetVersion}: kontrollér resultatet",
            new JobParams { BiosUpdate = Order(run, BiosUpdatePhases.Verify, null) }, RunAs.System, null, Source, by, save: false);
        await db.SaveChangesAsync();
        run.JobId = job.Id;
        await jobs.Commit(m.Id);
        return (job, null);
    }

    /// <summary>Agentens melding: kopien før opdateringen (kun mens klargøringen kører) eller et resultat (også efter genstart).</summary>
    public static async Task<BiosUpdatePostResponse> OnAgentPost(AppDb db, Machine m, BiosUpdatePost p, DateTime now)
    {
        var run = await db.BiosUpdateRuns.FirstOrDefaultAsync(x => x.Id == p.RunId && x.MachineId == m.Id);
        if (run is null) return new() { Ok = false, Message = "Fleetdeck kender ikke denne BIOS-opdatering." };
        if (p.Kind == BiosUpdatePostKinds.Snapshot)
        {
            if (run.State != BiosUpdateStates.Staging || p.Settings is not { Items.Count: > 0 } rep || !await OwnJobRunning(db, run, m.Id, p.JobId ?? -1))
                return new() { Ok = false, Message = "Kopien kan kun gemmes, mens opdateringen gøres klar." };
            var snap = await BiosSettingsService.Store(db, m, rep, BiosSnapshotReasons.PreUpdate, run.CreatedBy, p.JobId, now);
            run.PreSnapshotId = snap.Id;
            run.UpdatedAt = now;
            await db.SaveChangesAsync();
            return new() { Ok = true, SnapshotId = snap.Id };
        }
        if (p.Report is { } r && Apply(db, m, run, r, now)) await db.SaveChangesAsync();
        return new() { Ok = true };   // også når meldingen ignoreres: agenten skal ikke blive ved med at sende den
    }

    private static bool Apply(AppDb db, Machine m, BiosUpdateRun run, BiosUpdateReport r, DateTime now)
    {
        var before = run.State;
        if (!BiosUpdateFlow.Apply(run, r, now)) return false;
        if (run.State != before && run.State is not (BiosUpdateStates.Checked or BiosUpdateStates.Blocked))
            db.Audit.Add(AuditService.Entry($"agent:{m.Label}", "bios.update." + run.State, $"BIOS-opdatering til {run.TargetVersion}: {run.Message}", m.Id));
        return true;
    }

    /// <summary>Jobbets resultat. Uden melding fra agenten (fx en undtagelse uden for jobbet) går tjek og klargøring til "mislykkedes".</summary>
    public static async Task OnResult(AppDb db, Machine m, Job j, JobResult r, DateTime now)
    {
        if (j.Type != JobTypes.BiosUpdate || JobStatus.Active.Contains(j.Status)) return;
        var runId = (r.BiosUpdate?.RunId is int id && id > 0) ? id : Json.Des<JobParams>(j.ParamsJson).BiosUpdate?.RunId;
        var run = runId is int rid ? await db.BiosUpdateRuns.FirstOrDefaultAsync(x => x.Id == rid && x.MachineId == m.Id) : null;
        if (run is null) return;
        if (r.BiosUpdate is { } rep) Apply(db, m, run, rep, now);
        else if (run.JobId == j.Id && run.State is BiosUpdateStates.Checking or BiosUpdateStates.Staging)
        {
            var text = r.Output.Trim();
            BiosUpdateFlow.Apply(run, new BiosUpdateReport
            {
                Outcome = BiosUpdateOutcomes.Failed,
                Message = text.Length == 0 ? "Jobbet mislykkedes uden en forklaring fra agenten." : text.Length > 300 ? text[..300] : text,
            }, now);
        }
        await db.SaveChangesAsync();
    }

    public sealed record RunView(int Id, string State, string Effective, string Route, string? FromVersion, string TargetVersion, string? Message,
        List<BiosCheck> Checks, int? PreSnapshotId, int? JobId, string? JobStatus, DateTime CreatedAt, DateTime UpdatedAt, DateTime? StagedAt,
        DateTime? RebootRequestedAt, DateTime? FinishedAt, string CreatedBy, BiosUpdateReport? Report, bool CanReboot, bool CanVerify, bool CanCompare);

    public static RunView View(BiosUpdateRun run, string? jobStatus, DateTime? bootAt, DateTime now, bool rebootAborted = false) => new(
        run.Id, run.State, BiosUpdateFlow.Effective(run, bootAt, now, rebootAborted), run.Route, run.FromVersion, run.TargetVersion, run.Message,
        Json.Des<List<BiosCheck>>(run.ChecksJson), run.PreSnapshotId, run.JobId, jobStatus, run.CreatedAt, run.UpdatedAt, run.StagedAt,
        run.RebootRequestedAt, run.FinishedAt, run.CreatedBy, run.ReportJson is { } rj ? Json.Des<BiosUpdateReport>(rj) : null,
        CanReboot: run.State == BiosUpdateStates.Staged,
        CanVerify: run.State is BiosUpdateStates.Staged or BiosUpdateStates.NotApplied or BiosUpdateStates.Unknown,
        CanCompare: run.PreSnapshotId is not null && run.State is BiosUpdateStates.Updated or BiosUpdateStates.Unknown or BiosUpdateStates.Rejected or BiosUpdateStates.NotApplied);
}
