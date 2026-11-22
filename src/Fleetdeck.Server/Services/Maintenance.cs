using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Endpoints;
using Fleetdeck.Shared;

namespace Fleetdeck.Server.Services;

/// <summary>Indstillinger for én vedligeholdelse (fra profilen, eller valgt ved "Vedligehold nu").</summary>
public sealed record MaintPlan(bool Wake, List<string> Tasks, string After, bool Report)
{
    public static MaintPlan From(Profile? p) => p is null
        ? new(true, ["windows_update", "upgrade"], "reboot", true)
        : new(p.MaintWake, p.MaintTasks, p.MaintAfter, p.MaintReport);

    public static readonly string[] AllTasks = ["windows_update", "upgrade", "disk_cleanup", "defender_signatures"];
    public static readonly string[] AllAfter = ["none", "reboot", "shutdown_if_woken", "shutdown"];

    public bool HasUpdates => Tasks.Contains("windows_update") || Tasks.Contains("upgrade");
}

/// <summary>
/// Hård spærre: maskiner, der aldrig må opdateres eller genstartes automatisk (beskyttede maskiner, profiler med
/// "aldrig automatisk" og profiler med et af de beskyttede navne i <see cref="LegacyNeverAutoNames"/>). Gælder alle automatiske
/// veje – planlægning, vedligehold og "Opdatér alt (automatisk)". Et tryk på Opdatér ud for én linje er stadig muligt.
/// </summary>
public static class AutoUpdateGuard
{
    /// <summary>
    /// Profilnavne, der altid spærres, også uden "aldrig automatisk" (bagudkompatibelt: ældre installationer kendte kun denne
    /// regel for en ventilationsstyring). Navnet sammenlignes med data i eksisterende databaser og må derfor ikke ændres.
    /// </summary>
    public static readonly string[] LegacyNeverAutoNames = ["dantherm"];

    public static string? Blocked(Machine m, Profile? p) =>
        m.Protected ? "maskinen er beskyttet – den opdateres og genstartes aldrig automatisk"
        : p?.UpdNeverAuto == true ? $"profilen \"{p.Name}\" tillader aldrig automatisk opdatering eller genstart"
        : p is not null && LegacyNeverAutoNames.Any(n => p.Name.Contains(n, StringComparison.OrdinalIgnoreCase))
            ? $"profilen \"{p.Name}\" styrer udstyr, der kun opdateres med ejerens udtrykkelige OK"
        : null;
}

/// <summary>Stilletid ("ikke i arbejdstiden"): ingen genstart eller slukning mellem to klokkeslæt (lokal tid).</summary>
public static class QuietHours
{
    public static bool Inside(string? from, string? to, DateTime local)
    {
        if (!TimeOnly.TryParse(from, out var f) || !TimeOnly.TryParse(to, out var t) || f == t) return false;
        var now = TimeOnly.FromDateTime(local);
        return f < t ? now >= f && now < t : now >= f || now < t;
    }

    /// <summary>Næste gang stilletiden slutter (lokal tid).</summary>
    public static DateTime End(string to, DateTime local)
    {
        var t = TimeOnly.Parse(to);
        var d = local.Date.Add(t.ToTimeSpan());
        return d > local ? d : d.AddDays(1);
    }
}

/// <summary>Skal en slukket maskine tændes? Ikke ud fra forældede data: er seneste søgning gammel, tændes den altid.</summary>
public static class UpdateWake
{
    /// <summary>Seneste "Patch Tuesday" (2. tirsdag i måneden, ca. kl. 18 dansk tid = 17 UTC) før eller lig med nu.</summary>
    public static DateTime PatchTuesday(DateTime nowUtc)
    {
        static DateTime For(int y, int m)
        {
            var first = new DateTime(y, m, 1, 17, 0, 0, DateTimeKind.Utc);
            var tue = first.AddDays(((int)DayOfWeek.Tuesday - (int)first.DayOfWeek + 7) % 7);
            return tue.AddDays(7);
        }
        var pt = For(nowUtc.Year, nowUtc.Month);
        return pt <= nowUtc ? pt : For(nowUtc.AddMonths(-1).Year, nowUtc.AddMonths(-1).Month);
    }

    public static bool Stale(DateTime? lastHealthUtc, DateTime nowUtc) =>
        lastHealthUtc is not { } h || nowUtc - h > TimeSpan.FromDays(7) || h < PatchTuesday(nowUtc);
}

/// <summary>
/// Opdateringsmotoren og den natlige vedligeholdelse. Én kørsel (MaintenanceRun) går igennem faste trin:
/// vent på maskinen (indhentning) → hjælpeopgaver → runder (UpdateRound-job, se UpdatePhases) → genstart → ny runde … →
/// "bagefter" (genstart/sluk) → morgenrapport. Serveren laver først næste trin, når det forrige er færdigt, og ejer tilstanden,
/// så en kørsel fortsætter efter genstart – også efter en genstart af serveren.
/// </summary>
public sealed partial class MaintenanceService(AppDb db, JobService jobs, SettingsService settings, Notifier notifier, Live live, ILogger<MaintenanceService> log)
{
    public const string Source = "maintenance";
    private static readonly TimeSpan LegacyDeadline = TimeSpan.FromHours(5);   // kun kørsler fra før motoren
    /// <summary>Kom maskinen først online så længe efter vinduet, er det en indhentning: installér stille, genstart kun når den er ledig.</summary>
    public static readonly TimeSpan CatchUpAfter = TimeSpan.FromHours(3);
    public static readonly TimeSpan RebootRetry = TimeSpan.FromMinutes(30);
    public const int MaxRebootTries = 48;
    public static readonly TimeSpan HungGiveUp = TimeSpan.FromHours(24);

    /// <summary>Kørslerne ændres ét sted ad gangen: en ny kørsel (fra en anmodning) og baggrundens Tick må ikke starte samme runde to gange.</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>En genstart, der ikke er hentet af agenten inden da, er udløbet og bestilles igen med de regler, der gælder til den tid.</summary>
    public static readonly TimeSpan RebootValid = TimeSpan.FromMinutes(20);
    /// <summary>En runde, der stadig kører efter så lang tid (selv med livstegn), opgives – så kørslen ikke spærrer maskinen for altid.</summary>
    public static readonly TimeSpan RoundLimit = TimeSpan.FromHours(6);
    /// <summary>Nyt forsøg efter en fejlet søgning.</summary>
    public static readonly TimeSpan SearchRetry = TimeSpan.FromMinutes(30);
    /// <summary>Genstart uden varsel kun så længe efter vinduets start (bagefter kan nogen sidde ved pc'en).</summary>
    public static readonly TimeSpan NoWarningWindow = TimeSpan.FromHours(2);

    public async Task<MaintenanceRun?> Start(int machineId, MaintPlan plan, string batch, string by, string kind = "maintenance", DateTime? notBefore = null)
    {
        await Gate.WaitAsync();
        try { return await StartLocked(machineId, plan, batch, by, kind, notBefore); }
        finally { Gate.Release(); }
    }

    private async Task<MaintenanceRun?> StartLocked(int machineId, MaintPlan plan, string batch, string by, string kind, DateTime? notBefore)
    {
        var m = await db.Machines.Include(x => x.Profile).FirstOrDefaultAsync(x => x.Id == machineId);
        if (m is null) return null;
        var prof = m.Profile;
        var now = DateTime.UtcNow;
        var from = notBefore ?? now;
        var run = new MaintenanceRun
        {
            MachineId = m.Id, Batch = batch, Engine = true, Kind = kind, PlanJson = JsonSerializer.Serialize(plan, Json.Web),
            MaxRounds = UpdateRounds.Clamp(prof?.UpdMaxRounds ?? UpdateRounds.DefaultMax), NotBefore = from,
            CatchUpUntil = from.AddDays(Math.Clamp(prof?.UpdCatchUpDays ?? 3, 0, 14)).AddHours(kind == "manual" ? 12 : 0),
            State = "waiting", Created = now,
        };
        if (AutoUpdateGuard.Blocked(m, prof) is { } why && (plan.HasUpdates || plan.After != "none"))
        {
            run.State = "blocked"; run.Step = "done"; run.Finished = now; run.Summary = why; run.Reported = true;
            db.MaintenanceRuns.Add(run);
            await db.SaveChangesAsync();
            log.LogInformation("Vedligeholdelse {Batch} på {Machine} spærret: {Why}", batch, m.Label, why);
            return run;
        }
        // venter der allerede en kørsel på maskinen, lægges den nye ind i den (opgaverne samlet) – så intet tabes, og en slukket pc
        // ikke får tre nætter i træk på én gang
        var waiting = await db.MaintenanceRuns.Where(r => r.MachineId == m.Id && r.Finished == null && r.Engine && r.State == "waiting" && r.Step == "")
            .OrderBy(r => r.Id).ToListAsync();
        if (waiting.Count > 0)
        {
            var keep = waiting[0];
            var merged = waiting.Skip(1).Aggregate(PlanOf(keep), (acc, o) => MergePlans(acc, PlanOf(o)));
            foreach (var old in waiting.Skip(1))
            {
                old.State = "superseded"; old.Step = "done"; old.Finished = now; old.Summary = "lagt sammen med en anden kørsel"; old.Reported = true;
                keep.Kind = StrongerKind(keep.Kind, old.Kind);
            }
            keep.PlanJson = JsonSerializer.Serialize(MergePlans(merged, plan), Json.Web);
            keep.Kind = StrongerKind(keep.Kind, kind);
            if (run.NotBefore > keep.NotBefore) keep.NotBefore = run.NotBefore;
            if (run.CatchUpUntil > keep.CatchUpUntil) keep.CatchUpUntil = run.CatchUpUntil;
            keep.MaxRounds = run.MaxRounds;
            // morgenrapporten: kommer den nye kørsel med rapport, og den gamle uden, følger kørslen den nye batch
            if (keep.Batch.EndsWith(":silent", StringComparison.Ordinal) && !batch.EndsWith(":silent", StringComparison.Ordinal)) keep.Batch = batch;
            await db.SaveChangesAsync();
            log.LogInformation("Vedligeholdelse {Batch} på {Machine}: lagt sammen med ventende kørsel {Run}", batch, m.Label, keep.Id);
            return keep;
        }
        var poll = await settings.GetNum("poll_seconds", 30);
        var offline = !MachineViews.Online(m, poll);
        run.WasOffline = offline;
        if (offline)
        {
            // en slukket pc tændes, når der er noget at gøre – eller når det, Fleetdeck ved om den, er for gammelt til at vide det
            var reasons = await WakeReasons(m, plan);
            if (plan.HasUpdates && reasons.Count == 0 && UpdateWake.Stale(LastKnown(m), now)) reasons.Add("seneste opdateringssøgning er gammel");
            // en klar BIOS installeres ved næste opstart – også en kold: en slukket pc med klar BIOS tændes aldrig automatisk
            if (plan.Wake && reasons.Count > 0 && await Bios.BiosUpdateService.Waiting(db, m.Id, now))
            {
                reasons.Clear();
                reasons.Add(BiosNoWakeWhy);
            }
            else if (plan.Wake && reasons.Count > 0) run.WakeSent = await WakeOnLan.Send(m.Mac, m.Ip);
            run.Summary = reasons.Count > 0 ? string.Join(", ", reasons) : "";
        }
        // kørslen og dens første skridt gemmes samlet (ingen mellemtilstand, som baggrunden kan nå at føre videre)
        db.MaintenanceRuns.Add(run);
        run.Machine = m;
        await Advance(run, plan, now);
        await db.SaveChangesAsync();
        log.LogInformation("Vedligeholdelse {Batch} på {Machine}: {Kind}, offline={Offline}, wake={Wake}", batch, m.Label, kind, offline, run.WakeSent);
        return run;
    }

    /// <summary>Hvornår Fleetdeck sidst hørte nyt om maskinens opdateringer (helbred eller inventar – LastHealth nulstilles efter jobs).</summary>
    public static DateTime? LastKnown(Machine m) =>
        m.LastHealth is { } h && m.LastInventory is { } i ? (h > i ? h : i) : m.LastHealth ?? m.LastInventory;

    private static readonly string[] AfterOrder = ["none", "reboot", "shutdown_if_woken", "shutdown"];
    private static readonly string[] KindOrder = ["schedule", "maintenance", "manual"];

    private static string StrongerKind(string a, string b) => Array.IndexOf(KindOrder, b) > Array.IndexOf(KindOrder, a) ? b : a;

    /// <summary>To planer for samme maskine lagt sammen: alle opgaver, det stærkeste "bagefter", vækning og rapport, hvis en af dem vil.</summary>
    public static MaintPlan MergePlans(MaintPlan a, MaintPlan b) => new(
        a.Wake || b.Wake,
        a.Tasks.Concat(b.Tasks).Distinct().ToList(),
        Array.IndexOf(AfterOrder, b.After) > Array.IndexOf(AfterOrder, a.After) ? b.After : a.After,
        a.Report || b.Report);

    /// <summary>Grunde til at tænde en slukket maskine – ud fra seneste inventar og helbredstjek. Tom liste = lad den sove.</summary>
    private async Task<List<string>> WakeReasons(Machine m, MaintPlan plan)
    {
        var h = Json.Des<HealthReport>(m.HealthJson);
        var r = new List<string>();
        if (plan.Tasks.Contains("windows_update") && h.Updates?.Pending is > 0) r.Add($"{h.Updates.Pending} Windows-opdatering(er) venter");
        if (plan.HasUpdates)
        {
            // det, motoren faktisk vil installere (låste, frarådede drivere og BIOS tæller ikke)
            var n = await UpgradeLocks.EngineCount(db, m.Id, m.Profile?.UpdDrivers ?? true, m.Profile?.UpdFirmware ?? false);
            if (n > 0) r.Add($"{n} opdatering(er) venter");
        }
        if (plan.Tasks.Contains("disk_cleanup") && h.Live.Volumes.FirstOrDefault(v => v.Drive.StartsWith("C", StringComparison.OrdinalIgnoreCase)) is { PctUsed: >= 90 } c)
            r.Add($"disken er {c.PctUsed:0} % fuld");
        if (plan.Tasks.Contains("defender_signatures") && h.Defender is { Enabled: true, SigAgeDays: > 3 } d) r.Add($"virusdefinitioner er {d.SigAgeDays} dage gamle");
        return r;
    }

    /// <summary>Kaldes af baggrundstråden: fører kørslerne videre og sender morgenrapporten, når hele batchen er færdig.</summary>
    public async Task Tick(DateTime? at = null)
    {
        await Gate.WaitAsync();
        try { await TickLocked(at); }
        finally { Gate.Release(); }
    }

    private async Task TickLocked(DateTime? at)
    {
        var now = at ?? DateTime.UtcNow;
        var open = await db.MaintenanceRuns.Include(r => r.Machine).ThenInclude(m => m!.Profile).Where(r => r.Finished == null).ToListAsync();
        foreach (var r in open)
        {
            if (!r.Engine) { await LegacyTick(r, now); continue; }
            var plan = PlanOf(r);
            await Advance(r, plan, now);
        }
        await db.SaveChangesAsync();
        if (open.Any(r => r.Finished is not null)) await live.Changed("machines");

        // Én besked pr. kørsel, når alle maskinerne i den er færdige
        var batches = await db.MaintenanceRuns.Where(r => !r.Reported).GroupBy(r => r.Batch).Select(g => g.Key).ToListAsync();
        foreach (var b in batches)
        {
            var runs = await db.MaintenanceRuns.Include(r => r.Machine).Where(r => r.Batch == b).ToListAsync();
            if (runs.Any(r => r.Finished is null)) continue;
            foreach (var r in runs) r.Reported = true;
            await db.SaveChangesAsync();
            if (!b.EndsWith(":silent", StringComparison.Ordinal)) notifier.Send(MorningReport(runs));
        }
    }

    /// <summary>Morgenrapporten: én linje pr. maskine og, hvor noget mangler, hvad der mangler.</summary>
    public static string MorningReport(IReadOnlyList<MaintenanceRun> runs)
    {
        var icon = runs.Any(r => r.State is "failed" or "offline" or "partial" or "hung") ? "🟠" : "🌙";
        var lines = new List<string> { $"{icon} Vedligeholdelse færdig" };
        foreach (var r in runs.Where(r => r.State is not ("superseded" or "blocked")).OrderBy(r => r.Machine?.Label))
        {
            lines.Add($"• {r.Machine?.Label}: {r.Summary}");
            if (r.Engine && r.ReportJson.Length > 0)
                foreach (var miss in UpdateStatus.Missing(Json.Des<UpdateReport>(r.ReportJson)).Take(8)) lines.Add($"   – {miss}");
        }
        return string.Join('\n', lines);
    }

    private static MaintPlan PlanOf(MaintenanceRun r)
    {
        try { return JsonSerializer.Deserialize<MaintPlan>(r.PlanJson, Json.Web) ?? MaintPlan.From(null); }
        catch (JsonException) { return MaintPlan.From(null); }
    }

    // ------------------------------------------------------------------ tilstandsmaskinen

    /// <summary>Ét skridt for én kørsel: venter den på et job, ses der på resultatet; ellers startes næste trin, når tiden er inde.</summary>
    public async Task Advance(MaintenanceRun run, MaintPlan plan, DateTime now)
    {
        var m = run.Machine ?? await db.Machines.Include(x => x.Profile).FirstAsync(x => x.Id == run.MachineId);
        run.Machine = m;
        // spærren gælder også en kørsel, der allerede er i gang (profilen kan være ændret, eller maskinen beskyttet undervejs):
        // ingen nye skridt, og det, der står i kø til kørslen, annulleres
        if (AutoUpdateGuard.Blocked(m, m.Profile) is { } why)
        {
            await CancelPending(run, now, "[Server] Annulleret: maskinen må ikke længere opdateres eller genstartes automatisk.");
            run.State = "blocked"; run.Summary = why; Close(run, now);
            return;
        }
        if (run.CurrentJobId is int jid)
        {
            var j = await db.Jobs.FirstOrDefaultAsync(x => x.Id == jid);
            if (j is not null && JobStatus.Active.Contains(j.Status))
            {
                // kom maskinen aldrig online inden for indhentningsvinduet: opgiv – så genstart/sluk ikke pludselig sker midt på dagen
                if (j.Started is null && j.Status == JobStatus.Pending && now > run.CatchUpUntil)
                {
                    await CancelPending(run, now, null);
                    await Finish(run, plan, now, "kom ikke online inden for indhentningsvinduet", offline: true);
                    return;
                }
                // en genstart, som agenten ikke hentede i tide (pc'en sov): annullér den – den bestilles igen, når pc'en er tilbage
                if (j.Type == JobTypes.Reboot && j.Status == JobStatus.Pending && j.Started is null && now > j.Created + RebootValid)
                {
                    j.Status = JobStatus.Cancelled; j.Finished = now; j.Output = RebootExpired;
                    await Completed(run, plan, j, now);
                    return;
                }
                // en runde, der hænger (fx et Windows Update-kald, der aldrig svarer), må ikke spærre maskinen for altid
                if (j.Type == JobTypes.UpdateRound && j.Started is { } st && now - st > RoundLimit)
                {
                    j.Status = JobStatus.Failed; j.Finished = now; j.Output += $"\n[Server] Opgivet: runden kørte i over {RoundLimit.TotalHours:0} timer.";
                    run.CurrentJobId = null;
                    run.ReportJson = Json.Ser(UpdateRounds.Merge(run.ReportJson.Length > 0 ? Json.Des<UpdateReport>(run.ReportJson) : null,
                        new UpdateReport { Round = run.Round, Error = $"runden svarede ikke i {RoundLimit.TotalHours:0} timer" }));
                    await Finish(run, plan, now, $"runden kørte i over {RoundLimit.TotalHours:0} timer");
                    return;
                }
                return;
            }
            await Completed(run, plan, j, now);
            return;
        }
        var poll = await settings.GetNum("poll_seconds", 30);
        switch (run.Step)
        {
            case "":
                if (now > run.CatchUpUntil) { await Finish(run, plan, now, "kom ikke online inden for indhentningsvinduet", offline: true); return; }
                if (now < run.NotBefore) return;
                // én kørsel ad gangen pr. maskine: denne venter, til den forrige er færdig
                if (await db.MaintenanceRuns.AnyAsync(o => o.MachineId == run.MachineId && o.Id != run.Id && o.Engine && o.Finished == null && o.Step != "" && o.Step != "done")) return;
                if (!MachineViews.Online(m, poll)) return;   // venter på maskinen (Wake-on-LAN er sendt én gang, hvis der var grund)
                run.CatchUp = now - run.NotBefore > CatchUpAfter;
                run.State = "running";
                if (!await StartTasks(run, plan, m)) await StartRound(run, plan, m, 1, now);
                return;
            case "round_wait":
                // ny runde efter en afbrudt runde eller en fejlet søgning – når tiden er inde, og maskinen er tilbage
                if (run.NextAt is { } rn && now < rn) return;
                if (now > run.CatchUpUntil) { await Final(run, plan, m, now, "indhentningsvinduet lukkede, før runden kunne gentages", allowReboot: false); return; }
                if (!MachineViews.Online(m, poll)) return;
                await StartRound(run, plan, m, Math.Max(1, run.Round), now);
                return;
            case "reboot_wait":
                if (run.NextAt is { } next && now < next) return;
                if (now > run.CatchUpUntil) { await Final(run, plan, m, now, "indhentningsvinduet lukkede, før genstarten kunne ske", allowReboot: false); return; }
                if (!MachineViews.Online(m, poll)) return;   // en genstart bestilles først, når maskinen kan tage imod den
                await RequestReboot(run, plan, m, now);
                return;
            case "final_wait" or "final_wait_nr":
                if (run.NextAt is { } fn && now < fn) return;
                if (now > run.CatchUpUntil) { await Finish(run, plan, now, null); return; }
                if (!MachineViews.Online(m, poll)) return;
                await Final(run, plan, m, now, null, allowReboot: run.Step == "final_wait");
                return;
        }
    }

    public const string RebootExpired = "[Server] Udløbet: genstarten blev ikke hentet i tide (maskinen sov) – bestilles igen.";

    /// <summary>Annullér kørslens jobs, der stadig står i kø (ikke dem, agenten er i gang med).</summary>
    private async Task CancelPending(MaintenanceRun run, DateTime now, string? note)
    {
        if (run.JobIds.Count == 0) return;
        var pending = await db.Jobs.Where(x => run.JobIds.Contains(x.Id) && x.Status == JobStatus.Pending).ToListAsync();
        foreach (var x in pending)
        {
            x.Status = JobStatus.Cancelled; x.Finished = now;
            if (note is not null) x.Output = (x.Output.Length > 0 ? x.Output + "\n" : "") + note;
        }
    }

    /// <summary>Hjælpeopgaverne (kun Windows): virusdefinitioner før opdateringerne, oprydning bagefter i samme kø.</summary>
    private async Task<bool> StartTasks(MaintenanceRun run, MaintPlan plan, Machine m)
    {
        if (m.Platform != Platforms.Windows) return false;
        var created = new List<Job>();
        foreach (var t in new[] { "defender_signatures", "disk_cleanup" }.Where(plan.Tasks.Contains))
        {
            var fix = HelperCatalog.Get(t)!;
            created.Add(await jobs.Create(m.Id, JobTypes.Script, $"Vedligehold: {fix.Title}",
                new JobParams { Body = fix.Script, TimeoutMin = fix.TimeoutMin, Name = fix.Id }, fix.RunAs, null, Source, "planlægger", false));
        }
        if (created.Count == 0) return false;
        await jobs.Commit(m.Id);
        run.JobIds = [.. run.JobIds, .. created.Select(j => j.Id)];
        run.CurrentJobId = created[^1].Id;
        run.Step = "tasks";
        return true;
    }

    /// <summary>Er kørslen (nu) en indhentning? Sat, når den begyndte – eller fordi den er trukket ind i dagtimerne.</summary>
    public static bool IsCatchUp(MaintenanceRun run, DateTime now) => run.CatchUp || now - run.NotBefore > CatchUpAfter;

    /// <summary>Rundens parametre (ren funktion, så den kan testes).</summary>
    public static JobParams RoundParams(MaintenanceRun run, MaintPlan plan, Profile? p, string platform, int round, DateTime now, bool verify = false)
    {
        var linux = platform == Platforms.Linux;
        // planlagte opdateringer er det, de altid har været – aldrig firmware (på Linux: systempakker og snap/flatpak)
        return new JobParams
        {
            UpdRound = round, UpdMaxRounds = run.MaxRounds, UpdVerify = verify ? true : null,
            UpdSystem = plan.Tasks.Contains("windows_update") || linux && plan.Tasks.Contains("upgrade"),
            UpdApps = plan.Tasks.Contains("upgrade") || linux && plan.Tasks.Contains("windows_update"),
            IncludeDrivers = p?.UpdDrivers ?? true,
            IncludeFirmware = run.Kind != "schedule" && (p?.UpdFirmware ?? false),
            FeatureUpdates = p?.UpdFeatureUpdates ?? false,
            // indhentning midt på dagen: drivere og firmware venter, mens nogen bruger pc'en
            UpdIdleMin = IsCatchUp(run, now) && run.Kind != "manual" ? 15 : null,
            Exclude = p?.UpgradeExclude ?? [],
        };
    }

    private async Task StartRound(MaintenanceRun run, MaintPlan plan, Machine m, int round, DateTime now, bool verify = false)
    {
        if (!plan.HasUpdates) { await Final(run, plan, m, now, "ingen opdateringsopgaver"); return; }
        var title = verify ? "Vedligehold: kontrol efter genstart" : $"Vedligehold: runde {round}/{run.MaxRounds} – opdateringer";
        var j = await jobs.Create(m.Id, JobTypes.UpdateRound, title, RoundParams(run, plan, m.Profile, m.Platform, round, now, verify), RunAs.System, null, Source, "planlægger");
        run.JobIds = [.. run.JobIds, j.Id];
        run.CurrentJobId = j.Id;
        run.Round = round;
        run.Step = verify ? "verify" : "round";
        run.State = "running";
        run.NextAt = null;
    }

    /// <summary>Svarede agenten, at den ikke kender jobtypen (for gammel til motoren)?</summary>
    public static bool UnknownJobType(Job j) =>
        j.Output.StartsWith("Ukendt jobtype", StringComparison.Ordinal) || j.Output.Contains("findes kun til Windows", StringComparison.Ordinal);

    private async Task Completed(MaintenanceRun run, MaintPlan plan, Job? j, DateTime now)
    {
        var m = run.Machine!;
        switch (run.Step)
        {
            case "tasks":
                run.CurrentJobId = null;
                await StartRound(run, plan, m, 1, now);
                return;
            case "round":
            {
                run.CurrentJobId = null;
                if (j?.Status == JobStatus.Cancelled) { await Finish(run, plan, now, "stoppet fra Fleetdeck"); return; }
                var rep = j?.ResultJson is { Length: > 0 } rj ? Json.Des<UpdateReport>(rj) : null;
                if (rep is null && j is not null && UnknownJobType(j))
                {
                    // agenten er for gammel til motoren: kør det på den gamle måde, så knappen og planerne stadig opdaterer
                    await Legacy(run, plan, m, now);
                    return;
                }
                if (rep is null)
                {
                    // afbrudt (genstart, slukket, låg lukket) eller intet svar: gentag runden, når maskinen er tilbage – kun en
                    // fejl, agenten selv melder, afslutter kørslen
                    if (j is not null && run.Retries < UpdateRounds.MaxRetries && now < run.CatchUpUntil)
                    {
                        run.Retries++;
                        run.Step = "round_wait"; run.State = "waiting"; run.NextAt = now.AddMinutes(2);
                        return;
                    }
                    rep = new UpdateReport { Round = run.Round, Error = j is null ? "jobbet forsvandt" : $"runden gav intet resultat ({j.Status})" };
                }
                var before = Json.Des<List<UpdateItem>>(run.LastInstalledJson.Length > 0 ? run.LastInstalledJson : "[]");
                var rolledBack = UpdateRounds.RolledBack(before, rep);
                UpdateRounds.MarkRolledBack(rep, rolledBack, before.Where(b => b.Id is not null).GroupBy(b => b.Id!).ToDictionary(g => g.Key, g => g.First().Title));
                var total = UpdateRounds.Merge(run.ReportJson.Length > 0 ? Json.Des<UpdateReport>(run.ReportJson) : null, rep);
                UpdateRounds.MarkRolledBack(total, rolledBack);
                var decision = UpdateRounds.Decide(run.Round, run.MaxRounds, run.Reboots, run.LastLeft, rep, mayReboot: plan.After != "none", run.Retries, rolledBack);
                // genstarter vi nu, huskes det installerede – kommer det igen efter genstarten, blev det rullet tilbage
                run.LastInstalledJson = decision.Next == UpdateNext.Reboot ? Json.Ser(UpdateRounds.RebootSensitive(rep)) : "";
                if (!decision.FinalReboot) total.RebootPending = false;   // en genstart hjælper ikke – lov den ikke som "mangler"
                run.ReportJson = Json.Ser(total);
                run.LastLeft = [.. UpdateRounds.Left(rep)];
                switch (decision.Next)
                {
                    case UpdateNext.Reboot: await RequestReboot(run, plan, m, now); break;
                    case UpdateNext.Round: await StartRound(run, plan, m, run.Round + 1, now); break;
                    case UpdateNext.Retry:
                        run.Retries++;
                        run.Round++;   // tæller med i grænsen for runder
                        run.Step = "round_wait"; run.State = "waiting"; run.NextAt = now + SearchRetry;
                        break;
                    default: await Final(run, plan, m, now, decision.Why, allowReboot: decision.FinalReboot); break;
                }
                return;
            }
            case "reboot":
            {
                var verdict = RebootOutcome(j, m, now);
                if (verdict == "wait") { run.State = "hung"; return; }   // vagten følger maskinen videre (Services/PowerWatch.cs)
                run.CurrentJobId = null;
                switch (verdict)
                {
                    case "back": run.Reboots++; await StartRound(run, plan, m, run.Round + 1, now); break;
                    case "not_needed": run.LastInstalledJson = ""; await StartRound(run, plan, m, run.Round + 1, now); break;
                    case "lost": await Finish(run, plan, now, "maskinen kom ikke tilbage efter genstarten"); break;
                    case "cancelled": await Final(run, plan, m, now, "genstarten blev annulleret", allowReboot: false); break;
                    case "expired": await RetryReboot(run, plan, m, now, TimeSpan.Zero); break;   // bestilles igen, når maskinen er tilbage
                    default: await RetryReboot(run, plan, m, now, RebootRetry); break;   // i brug eller fejlet
                }
                return;
            }
            case "final":
            {
                // var der en afsluttende genstart (efter sidste runde), er den ventende genstart klaret
                var lastRound = await db.Jobs.AsNoTracking().Where(x => run.JobIds.Contains(x.Id) && x.Type == JobTypes.UpdateRound).MaxAsync(x => (int?)x.Id) ?? 0;
                var finalReboot = await db.Jobs.Where(x => run.JobIds.Contains(x.Id) && x.Type == JobTypes.Reboot && x.Id > lastRound).OrderByDescending(x => x.Id).FirstOrDefaultAsync();
                if (finalReboot is not null && finalReboot.Id == j?.Id) finalReboot = j;   // samme (evt. netop ændrede) objekt
                var outcome = finalReboot is null ? null : RebootOutcome(finalReboot, m, now);
                if (outcome == "wait") { run.State = "hung"; return; }
                run.CurrentJobId = null;
                if (outcome == "expired" && j?.Id == finalReboot!.Id && run.RebootTries < MaxRebootTries && now < run.CatchUpUntil)
                {
                    // pc'en sov, da den sidste genstart blev bestilt: bestil den igen, når pc'en er tilbage
                    run.RebootTries++;
                    run.Step = "final_wait"; run.State = "reboot_wait"; run.NextAt = now;
                    return;
                }
                if (outcome == "back") run.Reboots++;
                if (outcome == "back" && run.ReportJson.Length > 0)
                {
                    var t = Json.Des<UpdateReport>(run.ReportJson);
                    var wasPending = t.RebootPending;
                    t.RebootPending = false; t.RebootSuggested = false; run.ReportJson = Json.Ser(t);
                    // Windows: en kort kontrolsøgning efter den sidste genstart – kom det installerede ikke ind (rullet tilbage), ses det her
                    if (wasPending && j?.Id == finalReboot!.Id && m.Platform == Platforms.Windows && plan.Tasks.Contains("windows_update") && UpdateRounds.SystemProgress(t))
                    {
                        run.LastInstalledJson = Json.Ser(UpdateRounds.RebootSensitive(t));
                        await StartRound(run, plan, m, run.Round, now, verify: true);
                        return;
                    }
                }
                await Finish(run, plan, now, null);
                return;
            }
            case "verify":
            {
                run.CurrentJobId = null;
                var rep = j?.ResultJson is { Length: > 0 } rj ? Json.Des<UpdateReport>(rj) : null;
                if (rep is not null && run.ReportJson.Length > 0)
                {
                    var total = Json.Des<UpdateReport>(run.ReportJson);
                    var before = Json.Des<List<UpdateItem>>(run.LastInstalledJson.Length > 0 ? run.LastInstalledJson : "[]");
                    var rolledBack = UpdateRounds.RolledBack(before, rep);
                    UpdateRounds.MarkRolledBack(total, rolledBack);
                    // det, der stadig tilbydes efter den sidste genstart, mangler stadig
                    var known = total.Failed.Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var x in rep.Remaining.Where(x => !known.Contains(x.Key) && !rolledBack.Contains(x.Id ?? "", StringComparer.OrdinalIgnoreCase)))
                        if (!total.Remaining.Any(r => r.Key.Equals(x.Key, StringComparison.OrdinalIgnoreCase))) total.Remaining.Add(x);
                    total.RebootPending = rep.RebootPending;
                    if (rolledBack.Count > 0) run.StopReason = "opdatering rullet tilbage efter genstart";
                    run.ReportJson = Json.Ser(total);
                }
                run.LastInstalledJson = "";
                await Finish(run, plan, now, null);
                return;
            }
            case "legacy":
            {
                run.CurrentJobId = null;
                var js = await db.Jobs.AsNoTracking().Where(x => run.JobIds.Contains(x.Id) && x.Id != (j == null ? 0 : j.Id)).ToListAsync();
                if (j is not null) js.Add(j);
                js = js.Where(x => x.Type != JobTypes.UpdateRound).OrderBy(x => x.Id).ToList();
                var started = js.Any(x => x.Started is not null);
                run.State = !started ? "offline" : js.Any(x => x.Status == JobStatus.Failed) ? "failed" : js.Any(x => x.Status == JobStatus.Partial) ? "partial" : "done";
                run.Summary = LegacySummary(run, js) + " (agenten var for gammel til opdateringsmotoren – kørt på den gamle måde)";
                Close(run, now);
                return;
            }
            default:
                run.CurrentJobId = null;
                return;
        }
    }

    /// <summary>For gamle agenter: de jobs, "Opdatér alt" og planerne brugte før motoren (Windows Update, programmer, genstart hvis nødvendigt).</summary>
    private async Task Legacy(MaintenanceRun run, MaintPlan plan, Machine m, DateTime now)
    {
        var created = new List<Job>();
        var p = m.Profile;
        if (plan.Tasks.Contains("windows_update"))
            created.Add(await jobs.Create(m.Id, JobTypes.WindowsUpdate, "Vedligehold: Windows Update", new JobParams { IncludeDrivers = p?.UpdDrivers ?? false }, RunAs.System, null, Source, "planlægger", false));
        if (plan.Tasks.Contains("upgrade"))
            created.Add(await jobs.Create(m.Id, JobTypes.Upgrade, "Vedligehold: alle programmer", new JobParams { Exclude = p?.UpgradeExclude ?? [] }, RunAs.System, null, Source, "planlægger", false));
        // en BIOS-opdatering, der ligger klar, installeres ved næste genstart: den genstart vælger ejeren selv
        if (plan.After != "none" && created.Count > 0 && !await Bios.BiosUpdateService.Waiting(db, m.Id, now))
        {
            var prm = RebootParams(run, p, m.Platform, now);
            prm.NotAfter = null;   // gamle agenter kender ikke udløbet; genstarten kommer lige efter opdateringerne
            created.Add(await jobs.Create(m.Id, JobTypes.Reboot, "Vedligehold: genstart hvis nødvendigt", prm, RunAs.System, null, Source, "planlægger", false));
        }
        run.ReportJson = ""; run.StopReason = "agenten er for gammel til opdateringsmotoren";
        if (created.Count == 0) { await Finish(run, plan, now, run.StopReason); return; }
        await jobs.Commit(m.Id);
        run.JobIds = [.. run.JobIds, .. created.Select(x => x.Id)];
        run.CurrentJobId = created[^1].Id;
        run.Step = "legacy"; run.State = "running";
    }

    /// <summary>
    /// Hvordan gik genstart-jobbet: back (tilbage med ny opstartstid), not_needed, skipped (i brug), cancelled (annulleret fra Fleetdeck),
    /// expired (ikke hentet i tide), wait (hænger stadig – vagten følger den), lost (hang i over et døgn) eller failed.
    /// </summary>
    public static string RebootOutcome(Job? j, Machine m, DateTime now)
    {
        if (j is null) return "failed";
        if (j.Output.Contains("Udløbet", StringComparison.Ordinal) && j.Status is JobStatus.Success or JobStatus.Cancelled) return "expired";
        if (j.Status == JobStatus.Success)
            return j.Output.Contains("Ingen genstart nødvendig", StringComparison.Ordinal) ? "not_needed"
                : j.Output.Contains("i brug", StringComparison.OrdinalIgnoreCase) ? "skipped" : "back";
        if (j.Status == JobStatus.Hung)
        {
            // vagten mistede sporet, men maskinen er oppe med en ny opstartstid: fortsæt alligevel
            var (boot, _) = PowerWatch.Boot(Json.Des<HealthReport>(m.HealthJson).Live, m.LastSeen);
            if (boot is { } b && j.Started is { } st && b > st && m.LastSeen > b) return "back";
            return now - (j.HungAt ?? j.Finished ?? now) > HungGiveUp ? "lost" : "wait";
        }
        return j.Status == JobStatus.Cancelled ? "cancelled" : "failed";
    }

    /// <summary>Grunden, når motoren ikke genstarter, fordi en BIOS-opdatering ligger klar til næste genstart.</summary>
    public const string BiosWaitingWhy = "en BIOS-opdatering ligger klar – genstarten vælger ejeren selv";
    /// <summary>Grunden, når motoren ikke tænder en slukket pc, fordi en BIOS-opdatering ligger klar (den installeres ved opstarten).</summary>
    public const string BiosNoWakeWhy = "BIOS ligger klar – tændes ikke automatisk";
    /// <summary>Grunden, når motoren ikke slukker, fordi en BIOS-opdatering ligger klar (næste opstart installerer den – også en automatisk).</summary>
    public const string BiosNoShutdownWhy = "en BIOS-opdatering ligger klar – slukkes ikke automatisk";

    /// <summary>Genstart mellem runderne: ejerens egne pc'er uden varsel (kun om natten); ellers med nedtælling og kun når pc'en er ledig.</summary>
    private async Task RequestReboot(MaintenanceRun run, MaintPlan plan, Machine m, DateTime now)
    {
        // en BIOS-opdatering, der ligger klar, installeres ved næste genstart: den genstart vælger ejeren selv – motoren stopper her
        if (await Bios.BiosUpdateService.Waiting(db, m.Id, now))
        {
            await Final(run, plan, m, now, BiosWaitingWhy, allowReboot: false);
            return;
        }
        var p = m.Profile;
        var local = now.ToLocalTime();
        if (p is not null && QuietHours.Inside(p.UpdQuietFrom, p.UpdQuietTo, local))
        {
            run.Step = "reboot_wait"; run.State = "reboot_wait"; run.CurrentJobId = null;
            run.NextAt = QuietHours.End(p.UpdQuietTo, local).ToUniversalTime();
            return;
        }
        var prm = RebootParams(run, p, m.Platform, now);
        var j = await jobs.Create(m.Id, JobTypes.Reboot, $"Vedligehold: genstart (runde {run.Round}/{run.MaxRounds})", prm, RunAs.System, null, Source, "planlægger");
        run.JobIds = [.. run.JobIds, j.Id];
        run.CurrentJobId = j.Id;
        run.Step = "reboot"; run.State = "rebooting"; run.NextAt = null;
    }

    /// <summary>
    /// Genstartens regler (ren funktion, så den kan testes) – afgjort ved hver genstart, ikke kun da kørslen begyndte:
    /// uden varsel kun på ejerens egne pc'er, kun lige efter vinduet (om natten) og ikke efter et nyt forsøg.
    /// "Opdatér alt" giver altid en nedtælling, som beskeden på skærmen lover.
    /// </summary>
    public static JobParams RebootParams(MaintenanceRun run, Profile? p, string platform, DateTime now)
    {
        var dev = platform == Platforms.Windows ? "Pc'en" : platform == Platforms.Mac ? "Mac'en" : "Maskinen";
        var catchUp = IsCatchUp(run, now);
        var notAfter = now + RebootValid;
        if (p?.UpdRebootNoWarning == true && run.Kind != "manual" && !catchUp && now - run.NotBefore <= NoWarningWindow && run.RebootTries == 0)
            return new JobParams { Delay = 0, OnlyIfPending = true, NotAfter = notAfter, Message = "Fleetdeck: genstarter for at fortsætte opdateringerne." };
        if (run.Kind == "manual" && !catchUp)
            return new JobParams { Delay = 300, OnlyIfPending = true, NotAfter = notAfter, Message = $"Fleetdeck: opdateringerne kræver en genstart. {dev} genstarter om 5 minutter – gem dit arbejde." };
        return new JobParams
        {
            Delay = catchUp ? 300 : 60, OnlyIfPending = true, OnlyIfIdleMin = 15, NotAfter = notAfter,
            Message = $"Fleetdeck: opdateringerne kræver en genstart. {dev} genstarter om {(catchUp ? "5 minutter" : "1 minut")}.",
        };
    }

    /// <summary>Genstarten blev sprunget over (i brug) eller udløb: nyt forsøg, så længe vinduet er åbent.</summary>
    private async Task RetryReboot(MaintenanceRun run, MaintPlan plan, Machine m, DateTime now, TimeSpan wait)
    {
        run.RebootTries++;
        if (run.RebootTries >= MaxRebootTries || now + wait > run.CatchUpUntil)
        {
            await Final(run, plan, m, now, "genstarten blev ikke til noget (pc'en var i brug)", allowReboot: false);
            return;
        }
        run.Step = "reboot_wait"; run.State = "reboot_wait"; run.NextAt = now + wait;
    }

    /// <summary>
    /// "Bagefter": en sidste genstart, hvis noget stadig venter på den, og/eller sluk. Er det stilletid, venter kørslen, til stilletiden
    /// slutter (så længe indhentningsvinduet er åbent), i stedet for at droppe genstarten.
    /// </summary>
    private async Task Final(MaintenanceRun run, MaintPlan plan, Machine m, DateTime now, string? why, bool allowReboot = true)
    {
        var p = m.Profile;
        if (why is not null) run.StopReason = why.Length > 200 ? why[..200] : why;   // gemmes til opsummeringen
        var total = run.ReportJson.Length > 0 ? Json.Des<UpdateReport>(run.ReportJson) : null;
        var local = now.ToLocalTime();
        var quiet = p is not null && QuietHours.Inside(p.UpdQuietFrom, p.UpdQuietTo, local);
        var wantReboot = allowReboot && plan.After != "none" && (total is null ? !plan.HasUpdates : total.RebootPending || total.RebootSuggested) && run.Reboots < run.MaxRounds;
        if (wantReboot && await Bios.BiosUpdateService.Waiting(db, m.Id, now))
        {
            wantReboot = false;   // en klar BIOS-opdatering må aldrig installeres af en automatisk genstart
            var stop = string.IsNullOrEmpty(run.StopReason) ? BiosWaitingWhy : $"{run.StopReason}; {BiosWaitingWhy}";
            run.StopReason = stop.Length > 200 ? stop[..200] : stop;
        }
        var shutdown = plan.After == "shutdown" || (plan.After == "shutdown_if_woken" && run.WasOffline && !IsCatchUp(run, now));
        if (shutdown && await Bios.BiosUpdateService.Waiting(db, m.Id, now))
        {
            // en slukket pc med klar BIOS flasher ved næste opstart, uanset hvem der tænder den: lad den være tændt
            shutdown = false;
            var stop = string.IsNullOrEmpty(run.StopReason) ? BiosNoShutdownWhy
                : run.StopReason.Contains(BiosWaitingWhy, StringComparison.Ordinal) ? run.StopReason : $"{run.StopReason}; {BiosNoShutdownWhy}";
            run.StopReason = stop.Length > 200 ? stop[..200] : stop;
        }
        if (quiet && (wantReboot || shutdown))
        {
            var end = QuietHours.End(p!.UpdQuietTo, local).ToUniversalTime();
            if (end < run.CatchUpUntil)
            {
                run.CurrentJobId = null;
                run.Step = allowReboot ? "final_wait" : "final_wait_nr"; run.State = "reboot_wait"; run.NextAt = end;
                return;
            }
        }
        var created = new List<Job>();
        if (!quiet && wantReboot)
            created.Add(await jobs.Create(m.Id, JobTypes.Reboot, "Vedligehold: genstart hvis nødvendigt", RebootParams(run, p, m.Platform, now), RunAs.System, null, Source, "planlægger", false));
        if (!quiet && shutdown)
            created.Add(await jobs.Create(m.Id, JobTypes.Shutdown, "Vedligehold: sluk igen",
                new JobParams { Delay = 120, OnlyIfIdleMin = 15, Message = "Fleetdeck: vedligeholdelsen er færdig. Pc'en slukker om 2 minutter." },
                RunAs.System, null, Source, "planlægger", false));
        if (created.Count == 0) { await Finish(run, plan, now, null); return; }
        await jobs.Commit(m.Id);
        run.JobIds = [.. run.JobIds, .. created.Select(j => j.Id)];
        run.CurrentJobId = created[^1].Id;
        run.Step = "final"; run.State = "running"; run.NextAt = null;
    }

    private async Task Finish(MaintenanceRun run, MaintPlan plan, DateTime now, string? why, bool offline = false)
    {
        var js = await db.Jobs.AsNoTracking().Where(j => run.JobIds.Contains(j.Id)).ToListAsync();
        var total = run.ReportJson.Length > 0 ? Json.Des<UpdateReport>(run.ReportJson) : null;
        var started = js.Any(j => j.Started is not null);
        if (offline && !started) run.State = "offline";
        else if (total is not null)
            run.State = UpdateStatus.Final(total) switch { JobStatus.Success => "done", JobStatus.Partial => "partial", _ => "failed" };
        else run.State = js.Any(j => j.Status == JobStatus.Failed) ? "failed" : "done";
        if (run.State == "done" && js.Any(j => j.Type == JobTypes.Script && j.Status == JobStatus.Failed)) run.State = "partial";
        if (why is not null) run.StopReason = why.Length > 200 ? why[..200] : why;
        run.Summary = Summarize(run, js, total, run.StopReason);
        Close(run, now);
    }

    private static void Close(MaintenanceRun run, DateTime now)
    {
        run.Finished = now; run.Step = "done"; run.CurrentJobId = null; run.NextAt = null;
    }

    [GeneratedRegex(@"(\d+) opdateret, (\d+) fejlede")] private static partial Regex UpgradeCount();
    [GeneratedRegex(@"Frigjort: ([\d.,-]+) GB")] private static partial Regex Freed();

    /// <summary>Kort og ærlig opsummering af en motor-kørsel: hvad der skete, og – hvis det ikke er alt – hvad der mangler.</summary>
    public static string Summarize(MaintenanceRun r, IReadOnlyList<Job> js, UpdateReport? total, string? why)
    {
        if (r.State == "offline")
            return r.WasOffline ? (r.WakeSent ? "kom ikke online (Wake-on-LAN sendt) – intet gjort" : "slukket og kom ikke online – intet gjort") : "agenten svarede ikke – intet gjort";
        var parts = new List<string>();
        if (r.WasOffline && r.WakeSent) parts.Add("tændt med Wake-on-LAN");
        if (r.CatchUp) parts.Add("indhentet");
        foreach (var j in js.Where(j => j.Type == JobTypes.Script).OrderBy(j => j.Id))
        {
            var f = Freed().Match(j.Output);
            var name = j.Title.Replace("Vedligehold: ", "");
            parts.Add(f.Success ? $"{f.Groups[1].Value} GB ryddet" : j.Status == JobStatus.Success ? name.ToLowerInvariant() : $"{name} fejlede");
        }
        if (total is not null)
        {
            parts.Add(UpdateStatus.Text(total));
            if (r.Round > 1 || r.Reboots > 0) parts.Add($"{r.Round} runde(r), {r.Reboots} genstart(er)");
        }
        if (js.LastOrDefault(j => j.Type == JobTypes.Shutdown) is { } sd)
            parts.Add(sd.Status == JobStatus.Cancelled ? "ikke slukket" : sd.Output.Contains("i brug", StringComparison.OrdinalIgnoreCase) ? "ikke slukket (i brug)" : sd.Status == JobStatus.Success ? "slukket igen" : "kunne ikke slukke");
        if (why is { Length: > 0 } w && w != "intet tilbage" && r.State != "done") parts.Add($"stoppede: {w}");
        return parts.Count > 0 ? string.Join(", ", parts) : "intet at gøre";
    }

    /// <summary>Hvad kørslen er i gang med lige nu – "Runde 2/4 · Drivere" – til visningen.</summary>
    public static string PhaseText(MaintenanceRun r, Job? current, DateTime nowUtc)
    {
        var round = $"Runde {Math.Max(1, r.Round)}/{r.MaxRounds}";
        return r.Step switch
        {
            "" => nowUtc < r.NotBefore ? $"Venter til {r.NotBefore.ToLocalTime():HH:mm}" : "Venter på maskinen",
            "tasks" => "Hjælpeopgaver",
            "round" when current?.Step is { Length: > 0 } s && s.StartsWith("Runde ", StringComparison.Ordinal) => s.Split(':')[0],
            "round" => current?.Status == JobStatus.Pending ? $"{round} · i kø" : round,
            "reboot" => r.State == "hung" ? $"{round} · Genstart hænger" : $"{round} · Genstarter",
            "reboot_wait" => $"{round} · Venter på at kunne genstarte{(r.NextAt is { } n && n > nowUtc ? $" (nyt forsøg {n.ToLocalTime():HH:mm})" : "")}",
            "round_wait" => $"{round} · Venter på at kunne prøve igen{(r.NextAt is { } rn && rn > nowUtc ? $" (kl. {rn.ToLocalTime():HH:mm})" : "")}",
            "final" => "Afslutter",
            "final_wait" or "final_wait_nr" => r.NextAt is { } fn && fn > nowUtc ? $"Afslutter efter stilletiden (kl. {fn.ToLocalTime():HH:mm})" : "Afslutter, når maskinen er tilbage",
            "verify" => "Kontrol efter den sidste genstart",
            "legacy" => "Opdaterer på den gamle måde (agenten er for gammel)",
            _ => "",
        };
    }

    // ------------------------------------------------------------------ kørsler fra før motoren
    private async Task LegacyTick(MaintenanceRun r, DateTime now)
    {
        var js = await db.Jobs.AsNoTracking().Where(j => r.JobIds.Contains(j.Id)).ToListAsync();
        var active = js.Where(j => JobStatus.Active.Contains(j.Status)).ToList();
        var late = now - r.Created > LegacyDeadline;
        if (active.Count > 0 && !late) return;
        if (late && active.Count > 0)
        {
            var ids = active.Select(j => j.Id).ToList();
            await db.Jobs.Where(j => ids.Contains(j.Id) && j.Status != JobStatus.Running)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Cancelled).SetProperty(j => j.Finished, now));
        }
        var started = js.Any(j => j.Started is not null);
        r.State = !started ? "offline" : js.Any(j => j.Status == JobStatus.Failed) ? "failed" : js.Any(j => j.Status == JobStatus.Partial) ? "partial" : "done";
        r.Summary = LegacySummary(r, js);
        r.Finished = now;
    }

    private static string LegacySummary(MaintenanceRun r, List<Job> js)
    {
        if (r.State == "offline") return r.WasOffline ? "kom ikke online – intet gjort" : "agenten svarede ikke – intet gjort";
        var parts = new List<string>();
        foreach (var j in js.OrderBy(j => j.Id))
        {
            var ok = j.Status == JobStatus.Success;
            switch (j.Type)
            {
                case JobTypes.WindowsUpdate:
                    var n = j.Output.Split('\n').Count(l => l.StartsWith("OK ", StringComparison.Ordinal));
                    parts.Add(j.Status == JobStatus.Partial ? $"{n} Windows-opdatering(er), nogle fejlede" : !ok ? "Windows Update fejlede" : j.Output.Contains("Ingen Windows-opdateringer venter") ? "Windows var opdateret" : $"{n} Windows-opdatering(er)");
                    break;
                case JobTypes.Upgrade:
                    var u = UpgradeCount().Match(j.Output);
                    parts.Add(u.Success ? $"{u.Groups[1].Value} program(mer) opdateret{(u.Groups[2].Value != "0" ? $", {u.Groups[2].Value} fejlede" : "")}" : ok ? "programmer opdateret" : "programopdatering fejlede");
                    break;
            }
            if (j.Status == JobStatus.Cancelled) parts.Add($"{j.Title.Replace("Vedligehold: ", "")} nåede ikke at køre");
        }
        return parts.Count > 0 ? string.Join(", ", parts) : "intet at gøre";
    }
}
