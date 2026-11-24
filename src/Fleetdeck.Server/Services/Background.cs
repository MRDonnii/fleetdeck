using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Shared;

namespace Fleetdeck.Server.Services;

public static class Schedule
{
    /// <summary>Er det tid nu? Kører kun inden for en time efter det planlagte tidspunkt.</summary>
    public static bool Due(string mode, string hhmm, int day, DateTime now)
    {
        if (mode == "off" || !TimeOnly.TryParse(hhmm, out var t)) return false;
        var passed = (now.TimeOfDay - t.ToTimeSpan()).TotalMinutes;
        if (passed is < 0 or > 60) return false;
        return mode == "daily" || (mode == "weekly" && ((int)now.DayOfWeek + 6) % 7 == day);
    }

    /// <summary>Det seneste planlagte tidspunkt, der er gået (lokal tid) – eller null.</summary>
    public static DateTime? LastOccurrence(string mode, string hhmm, int day, DateTime now)
    {
        if (mode == "off" || !TimeOnly.TryParse(hhmm, out var t)) return null;
        for (var back = 0; back < 8; back++)
        {
            var d = now.Date.AddDays(-back).Add(t.ToTimeSpan());
            if (d <= now && (mode == "daily" || (mode == "weekly" && ((int)d.DayOfWeek + 6) % 7 == day))) return d;
        }
        return null;
    }

    public const string StampFormat = "yyyy-MM-dd'T'HH:mm";
    public const string Off = "off";

    /// <summary>
    /// Opdateringer og vedligehold: "seneste planlagte tidspunkt er nyere end seneste kørsel" – så et vindue, der blev misset
    /// (serveren var nede), stadig køres, så længe det er inden for indhentningsvinduet. <paramref name="handled"/> er det gemte
    /// stempel (ny form: tidspunktet; gammel form: datoen). Returnerer tidspunktet, der skal køres, eller null.
    /// </summary>
    public static DateTime? DueOccurrence(string mode, string hhmm, int day, DateTime now, string? handled, TimeSpan catchUp)
    {
        if (LastOccurrence(mode, hhmm, day, now) is not { } occ || now - occ > catchUp) return null;
        // aldrig kørt, eller planen var slået fra: kun inden for en time efter tidspunktet (ellers kører den lige når den slås til)
        // (SettingsService.Get giver "" for en nøgle, der aldrig er gemt – det er også "aldrig kørt")
        if (string.IsNullOrEmpty(handled) || handled == Off) return now - occ <= TimeSpan.FromHours(1) ? occ : null;
        if (DateTime.TryParseExact(handled, StampFormat, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var h))
            return h >= occ ? null : occ;
        if (DateOnly.TryParse(handled, System.Globalization.CultureInfo.InvariantCulture, out var d) && d >= DateOnly.FromDateTime(occ)) return null;
        return occ;
    }

    public static string? Next(string mode, string hhmm, int day)
    {
        if (mode == "off" || !TimeOnly.TryParse(hhmm, out var t)) return null;
        var now = DateTime.Now;
        for (var add = 0; add < 8; add++)
        {
            var d = now.Date.AddDays(add).Add(t.ToTimeSpan());
            if (d > now && (mode == "daily" || ((int)d.DayOfWeek + 6) % 7 == day)) return d.ToString("yyyy-MM-dd HH:mm");
        }
        return null;
    }
}

/// <summary>Agenten sender et resultat igen, til serveren har kvitteret (netværksfejl): samme resultat to gange må kun tælle én gang.</summary>
public static class JobResults
{
    public const string NoAnswer = "\n[Server] Intet svar fra agenten i 4 timer.";
    /// <summary>Jobbet har allerede fået sit resultat. Er det kun opgivet af serveren (intet svar), tages et sent resultat imod.</summary>
    public static bool AlreadyFinal(Job j) => j.Status is JobStatus.Success or JobStatus.Partial
        || (j.Status == JobStatus.Failed && !j.Output.EndsWith(NoAnswer, StringComparison.Ordinal));
}

/// <summary>Planlagte opdateringer og backup, oprydning, alarmer for offline-maskiner og winget-indeks.</summary>
public sealed class BackgroundWorker(IServiceScopeFactory scopes, ILogger<BackgroundWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var tick = 0;
        var lastIndex = DateTime.MinValue;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var sp = scope.ServiceProvider;
                // Hvert trin for sig: én fejl (fx en dårlig profil) må ikke stoppe oprydning, vedligehold og helbred
                await Step("planer", () => RunSchedules(sp));
                await Step("reservekort", () => sp.GetRequiredService<SpareCardService>().Tick(DateTime.Now));
                if (tick % 2 == 0) await Step("auto-wake", () => AutoWake(sp));
                await Step("vedligehold", () => sp.GetRequiredService<MaintenanceService>().Tick());
                await Step("genstart-vagt", () => sp.GetRequiredService<PowerWatchService>().Tick(stop));
                await Step("oprydning", () => Cleanup(sp.GetRequiredService<AppDb>()));
                await Step("anmodninger", async () => { if (await sp.GetRequiredService<AccessRequestService>().Tick() > 0) await sp.GetRequiredService<Live>().Changed("access-requests"); });
                await Step("udrulning", () => sp.GetRequiredService<DeployService>().Sync());
                if (tick % 2 == 1) await Step("rapporter", () => sp.GetRequiredService<Reports.ReportScheduler>().Tick());
                if (tick % 2 == 0)
                    try { await sp.GetRequiredService<UpdateService>().Tick(); }
                    catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "Søgning efter Fleetdeck-opdateringer fejlede"); }
                if (tick % 2 == 1) await Step("apps", () => sp.GetRequiredService<AppWatchService>().Tick(stop));
                if (tick % 10 == 0)
                    try { await sp.GetRequiredService<DebloatService>().Tick(DateTime.UtcNow); }
                    catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "Debloat-håndhævelse fejlede"); }
                if (tick % 10 == 0)
                {
                    var health = sp.GetRequiredService<HealthService>();
                    foreach (var id in await sp.GetRequiredService<AppDb>().Machines.Select(m => m.Id).ToListAsync(stop))
                        await Step($"helbred {id}", () => health.Evaluate(id));
                }
                if (DateTime.UtcNow - lastIndex > TimeSpan.FromDays(1))
                {
                    lastIndex = DateTime.UtcNow;
                    var idx = sp.GetRequiredService<WingetIndex>();
                    _ = Task.Run(() => idx.Refresh(), stop);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogError(e, "Fejl i baggrundstråd"); }
            tick++;
            async Task Step(string name, Func<Task> f)
            {
                try { await f(); }
                catch (Exception e) when (!stop.IsCancellationRequested) { log.LogError(e, "Fejl i baggrundstråd ({Step})", name); }
            }
        } while (await timer.WaitForNextTickAsync(stop));
    }

    private readonly Dictionary<int, (DateTime Last, int Tries)> _wakes = [];
    private readonly HashSet<int> _biosNoWake = [];

    /// <summary>
    /// Vækker pc'er, der er slukket eller sover, mens der står jobs og venter på dem (fx "Opdatér alt" eller vedligehold):
    /// Wake-on-LAN (og Intel AMT, hvis den er sat op) hvert 10. minut, højst 8 forsøg i træk.
    /// </summary>
    private async Task AutoWake(IServiceProvider sp)
    {
        var db = sp.GetRequiredService<AppDb>();
        var poll = await sp.GetRequiredService<SettingsService>().GetNum("poll_seconds", 30);
        var since = DateTime.UtcNow.AddHours(-24);
        var waiting = await db.Jobs.AsNoTracking().Where(j => j.Status == "pending" && j.Created > since).Select(j => j.MachineId).Distinct().ToListAsync();
        foreach (var id in _wakes.Keys.Where(k => !waiting.Contains(k)).ToList()) _wakes.Remove(id);
        _biosNoWake.RemoveWhere(k => !waiting.Contains(k));
        if (waiting.Count == 0) return;
        var amt = sp.GetRequiredService<AmtService>();
        var audit = sp.GetRequiredService<AuditService>();
        foreach (var m in await db.Machines.Where(m => waiting.Contains(m.Id)).ToListAsync())
        {
            if (Endpoints.MachineViews.Online(m, poll)) { _wakes.Remove(m.Id); continue; }
            if (string.IsNullOrEmpty(m.Mac) && string.IsNullOrEmpty(m.AmtPassword)) continue;
            var w = _wakes.GetValueOrDefault(m.Id);
            if (w.Tries >= 8 || DateTime.UtcNow - w.Last < TimeSpan.FromMinutes(10)) continue;
            // en klar BIOS installeres ved næste opstart – også en kold: tænd den ikke uden opsyn (hverken Wake-on-LAN eller Intel AMT)
            if (await Bios.BiosUpdateService.Waiting(db, m.Id))
            {
                if (_biosNoWake.Add(m.Id)) await audit.Log("system", "machine.autowake.skipped", $"{m.Label}: jobs venter, men en BIOS-opdatering ligger klar – tændes ikke automatisk", m.Id);
                continue;
            }
            _biosNoWake.Remove(m.Id);
            var sent = await WakeOnLan.Send(m.Mac, m.Ip);
            var viaAmt = false;
            if (!string.IsNullOrEmpty(m.AmtPassword)) { try { viaAmt = (await amt.Power(m, AmtClient.On)).Ok; } catch (Exception e) when (e is HttpRequestException or TaskCanceledException or UnauthorizedAccessException) { } }
            _wakes[m.Id] = (DateTime.UtcNow, w.Tries + 1);
            if (w.Tries == 0) await audit.Log("system", "machine.autowake", $"{m.Label}: jobs venter – vækker den ({(viaAmt ? "Intel AMT" : sent ? "Wake-on-LAN" : "intet sendt")})");
            log.LogInformation("Auto-væk {Name} (forsøg {N}, WoL {Sent}, AMT {Amt})", m.Label, w.Tries + 1, sent, viaAmt);
        }
    }

    private static async Task RunSchedules(IServiceProvider sp)
    {
        var db = sp.GetRequiredService<AppDb>();
        var settings = sp.GetRequiredService<SettingsService>();
        var jobs = sp.GetRequiredService<JobService>();
        var audit = sp.GetRequiredService<AuditService>();
        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now).ToString("O");
        foreach (var p in await db.Profiles.AsNoTracking().ToListAsync())
        {
            (string Kind, string Mode, string Time, int Day)[] plans =
            [
                ("upgrade", p.UpgradeMode, p.UpgradeTime, p.UpgradeDay),
                ("windows_update", p.WindowsUpdateMode, p.WindowsUpdateTime, p.WindowsUpdateDay),
                ("backup", p.BackupMode, p.BackupTime, p.BackupDay),
                ("maintenance", p.MaintMode, p.MaintTime, p.MaintDay),
                ("kit", p.KitPaths.Count > 0 ? p.KitMode : "off", p.KitTime, p.KitDay),
            ];
            foreach (var (kind, mode, time, day) in plans)
            {
                var key = $"sched:{p.Id}:{kind}";
                var updates = kind is "maintenance" or "upgrade" or "windows_update";
                DateTime? occurrence = null;
                if (updates && mode == "off")
                {
                    if (await settings.Get(key) != Schedule.Off) await settings.Set(key, Schedule.Off);
                    continue;
                }
                if (updates)
                {
                    // opdateringer og vedligehold indhentes: kører, så længe det seneste vindue ikke er kørt og er inden for indhentningen
                    occurrence = Schedule.DueOccurrence(mode, time, day, now, await settings.Get(key), TimeSpan.FromDays(Math.Clamp(p.UpdCatchUpDays, 0, 14)));
                    if (occurrence is null) continue;
                    await settings.Set(key, occurrence.Value.ToString(Schedule.StampFormat, System.Globalization.CultureInfo.InvariantCulture));
                }
                else
                {
                    if (!Schedule.Due(mode, time, day, now)) continue;
                    if (await settings.Get(key) == today) continue;
                    await settings.Set(key, today);
                }
                var machines = await db.Machines.Where(m => m.ProfileId == p.Id).Select(m => m.Id).ToListAsync();
                if (updates)
                {
                    // én motor for alle tre: vedligehold, planlagte programopdateringer og planlagt Windows Update (Services/Maintenance.cs)
                    var maint = sp.GetRequiredService<MaintenanceService>();
                    var plan = kind == "maintenance" ? MaintPlan.From(p)
                        : new MaintPlan(false, [kind], p.RebootPolicy == "after_upgrade" ? "reboot" : "none", false);   // planlagte opdateringer vækker ikke maskiner (det gør vedligeholdelsen)
                    var stamp = DateOnly.FromDateTime(occurrence!.Value).ToString("O");
                    var batch = (kind == "maintenance" ? $"p{p.Id}:{stamp}" : $"s{p.Id}:{kind}:{stamp}") + (plan.Report ? "" : ":silent");
                    foreach (var mid in machines) await maint.Start(mid, plan, batch, "planlægger", kind == "maintenance" ? "maintenance" : "schedule", occurrence.Value.ToUniversalTime());
                    await audit.Log("planlægger", $"schedule.{kind}", $"{p.Name}: {machines.Count} maskine(r){(now - occurrence.Value > TimeSpan.FromHours(1) ? $" (indhentet fra {occurrence.Value:yyyy-MM-dd HH:mm})" : "")}");
                    continue;
                }
                foreach (var mid in machines)
                {
                    if (kind == "kit")
                        await jobs.Create(mid, JobTypes.KitBackup, "Planlagt gendannelsespunkt", new JobParams { Paths = p.KitPaths }, RunAs.System, null, "schedule", "planlægger", false);
                    else
                        await jobs.WinSetup(mid, "Backup", "schedule", "planlægger", false);
                    await jobs.Commit(mid);
                }
                await audit.Log("planlægger", $"schedule.{kind}", $"{p.Name}: {machines.Count} maskine(r)");
            }
        }
    }

    private static async Task Cleanup(AppDb db)
    {
        var now = DateTime.UtcNow;
        await db.Jobs.Where(j => j.Status == JobStatus.Running && (j.ProgressAt ?? j.Started) < now.AddHours(-4))
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Failed).SetProperty(j => j.Finished, now)
                .SetProperty(j => j.Output, j => j.Output + JobResults.NoAnswer));
        await db.Jobs.Where(j => (j.Status == "pending" || j.Status == JobStatus.WaitingUser) && j.Created < now.AddDays(-14))
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Expired).SetProperty(j => j.Finished, now));
        await db.Metrics.Where(m => m.Ts < now.AddDays(-8)).ExecuteDeleteAsync();
        await AmtService.ScrubFinishedJobs(db);   // AMT-adgangskoden i annullerede/udløbne jobs
        await SysLogStore.Prune(db, now);      // fejlloggen: 30 dage
        await ChangeLogStore.Prune(db, now);   // ændringsloggen: 180 dage, højst 3000 pr. maskine
        await Timeline.Prune(db, now);         // tidslinjen: 35 dage
        var healthCut = DateOnly.FromDateTime(now.AddDays(-Reports.ReportTracking.HealthDays));
        await db.HealthDaily.Where(h => h.Day < healthCut).ExecuteDeleteAsync();
        // revisionslog: én gang i døgnet, efter indstillingen (standard 365 dage)
        if (now - lastAuditPrune > TimeSpan.FromHours(24)) { lastAuditPrune = now; await AuditLog.Prune(db, now); }
    }

    private static DateTime lastAuditPrune = DateTime.MinValue;
}
