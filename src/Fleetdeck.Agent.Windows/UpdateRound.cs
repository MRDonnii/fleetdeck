using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Windows;

/// <summary>
/// Opdateringsmotoren på Windows: én runde i fast rækkefølge (UpdatePhases) –
/// eksklusive opdateringer hver for sig → Windows-opdateringer i én batch → drivere → programmer → firmware sidst.
/// Det, der fejler i en batch, prøves én gang mere for sig selv. Kræver en fase genstart, stopper Windows Update-delen,
/// og serveren genstarter og beder om næste runde.
/// </summary>
public sealed partial class JobRunner
{
    /// <summary>Højst så længe for én Windows Update-søgning og én installations-batch – et COM-kald, der hænger, må ikke låse agenten.</summary>
    public static readonly TimeSpan SearchLimit = TimeSpan.FromMinutes(20), InstallLimit = TimeSpan.FromMinutes(90);

    private async Task<JobOutcome> UpdateRound(AgentJob j, CancellationToken ct)
    {
        var p = j.Params;
        var round = p.UpdRound ?? 1;
        var max = UpdateRounds.Clamp(p.UpdMaxRounds ?? UpdateRounds.DefaultMax);
        var verify = p.UpdVerify == true;
        var rep = new UpdateReport { Round = round, Phase = UpdatePhases.Prepare };
        var lines = new List<string>();
        string Head(string phase) => verify ? $"Kontrol efter genstart · {UpdatePhases.Label(phase)}" : $"Runde {round}/{max} · {UpdatePhases.Label(phase)}";

        // 0) forudsætninger: venter Windows Update (CBS/WU) allerede på en genstart, installeres der ikke mere fra Windows Update før den.
        //    PendingFileRenameOperations tæller ikke med: den står ofte der hele tiden (antivirus, drivere, programmer)
        var pendingAtStart = Collectors.WuRebootPending();
        var renamesAtStart = Collectors.FileRenamesPending();
        var (onAc, pct) = UpdatePlanner.Power(BatteryCollect.Collect());
        // indhentning midt på dagen: bruges pc'en, venter drivere og firmware (skærm og netværk må ikke blinke midt i et møde)
        var userActive = p.UpdIdleMin is int idleMin && await IdleMinutes(ct) is int idle && idle < idleMin;
        var opts = new UpdateOptions
        {
            System = p.UpdSystem != false, Apps = p.UpdApps != false && !verify,
            Drivers = p.IncludeDrivers != false, Firmware = p.IncludeFirmware == true, FeatureUpdates = p.FeatureUpdates == true,
            OnAc = onAc, BatteryPct = pct, RebootPending = pendingAtStart, UserActive = userActive,
        };
        var candidates = new List<UpdateCandidate>();
        var wuDead = false;
        if (opts.System)
        {
            LiveJob.Phase(0, 8, $"{Head(UpdatePhases.Prepare)}: søger efter opdateringer");
            try
            {
                candidates = await Task.Run(WindowsUpdate.Candidates, ct).WaitAsync(SearchLimit, ct);
                rep.Offered = candidates.Select(c => WuId(c.Id)).ToList();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                wuDead = e is TimeoutException;
                rep.Error = e is TimeoutException ? $"Windows Update svarede ikke på søgningen inden for {SearchLimit.TotalMinutes:0} min."
                    : $"Windows Update-søgningen fejlede: {e.Message}";
                lines.Add($"!!  {rep.Error}");
            }
        }
        // låst ("Opdatér aldrig"), skjult i Fleetdeck eller undtaget i profilen
        var exclude = new HashSet<string>(p.Exclude ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var c in candidates) c.Locked = exclude.Contains(DeepUpdates.WuPrefix + c.Id);
        if (pendingAtStart) lines.Add("--  Windows venter allerede på en genstart – Windows Update fortsætter efter genstarten.");
        if (userActive) lines.Add("--  Pc'en er i brug – drivere og firmware venter, til den er ledig.");
        var plan = UpdatePlanner.Plan(candidates, opts);
        foreach (var d in plan.Deferred) { d.Id = WuIdOrNull(d.Id); rep.Deferred.Add(d); }
        foreach (var x in plan.Later) rep.Later(WuIdOrNull(x.Id), x.Title);
        foreach (var d in plan.Deferred) lines.Add($"--  {d.Title}: {d.Reason}");

        if (verify)
        {
            // kontrol efter den sidste genstart: det, der stadig tilbydes, er ikke kommet ind (fx rullet tilbage)
            foreach (var c in plan.Phases.Where(x => x.Phase != UpdatePhases.Apps).SelectMany(x => x.Items)) rep.Later(WuId(c.Id), c.Title);
            rep.RebootPending = Collectors.WuRebootPending();
            lines.Add($"\nKontrol efter genstart: {(rep.Remaining.Count == 0 ? "intet mangler" : $"{rep.Remaining.Count} tilbydes stadig")}");
            return new(UpdateStatus.Of(rep), 0, string.Join('\n', lines).Trim(), Update: rep);
        }

        var reboot = pendingAtStart;
        var n = 0;
        foreach (var ph in plan.Phases)
        {
            n++;
            var from = 8 + 92 * (n - 1) / plan.Phases.Count; var to = 8 + 92 * n / plan.Phases.Count;
            if (ct.IsCancellationRequested) { foreach (var c in ph.Items) rep.Later(WuId(c.Id), c.Title); continue; }
            // efter en genstartskrævende fase venter resten af Windows Update (og firmware altid) til næste runde
            if (reboot && UpdatePhases.NeedsCleanState(ph.Phase))
            {
                foreach (var c in ph.Items) rep.Later(WuId(c.Id), c.Title);
                if (ph.Items.Count > 0) lines.Add($"--  {UpdatePhases.Label(ph.Phase)}: {ph.Items.Count} venter til efter genstarten");
                continue;
            }
            // Windows Update hang: resten af Windows Update-faserne venter til næste runde (programmerne kører stadig)
            if (wuDead && ph.Phase != UpdatePhases.Apps)
            {
                foreach (var c in ph.Items) rep.Later(WuId(c.Id), c.Title);
                continue;
            }
            LiveJob.Phase(from, to, $"{Head(ph.Phase)}{(ph.Items.Count > 0 ? $": {ph.Items.Count} opdatering(er)" : "")}");
            lines.Add($"\n== {UpdatePhases.Label(ph.Phase)} ==");
            rep.Phase = ph.Phase;
            if (ph.Phase == UpdatePhases.Apps)
            {
                var apps = await Upgrade(new JobParams { Exclude = p.Exclude, RunAsById = p.RunAsById }, ct, rep);
                lines.Add(apps.Output.Trim());
                continue;
            }
            if (ph.Phase == UpdatePhases.Firmware)
            {
                // strømmen kan være ændret siden runden begyndte (bærbar taget ud af stikket): mål igen lige før firmwaren
                var (ac2, pct2) = UpdatePlanner.Power(BatteryCollect.Collect());
                if (!UpdatePlanner.PowerOk(ac2, pct2))
                {
                    foreach (var c in ph.Items) { rep.Defer(WuId(c.Id), c.Title, DeferReasons.Power); lines.Add($"--  {c.Title}: {DeferReasons.Power}"); }
                    continue;
                }
                var (bitlockerOk, bl) = await SuspendBitLocker(ct);
                if (bl.Length > 0) lines.Add(bl);
                if (!bitlockerOk)
                {
                    // ellers beder pc'en om gendannelsesnøglen ved næste opstart
                    foreach (var c in ph.Items) { rep.Defer(WuId(c.Id), c.Title, DeferReasons.BitLocker); lines.Add($"--  {c.Title}: {DeferReasons.BitLocker}"); }
                    continue;
                }
            }
            var batches = ph.OneByOne ? ph.Items.Select(c => new List<UpdateCandidate> { c }).ToList() : [ph.Items];
            foreach (var batch in batches)
            {
                if (ct.IsCancellationRequested || wuDead) { foreach (var c in batch) rep.Later(WuId(c.Id), c.Title); continue; }
                var (rb, dead) = await InstallBatch(batch, ph.Phase, Head(ph.Phase), rep, lines, ct);
                reboot |= rb; wuDead |= dead;
                // eksklusive opdateringer: kræver én af dem genstart, venter resten
                if (reboot && ph.Phase == UpdatePhases.Exclusive) { foreach (var c in batches.SkipWhile(b => b != batch).Skip(1).SelectMany(b => b)) rep.Later(WuId(c.Id), c.Title); break; }
            }
        }
        DeepUpdates.Forget();
        rep.RebootPending = reboot || Collectors.WuRebootPending();
        // programmerne efterlod filer til næste opstart: ikke grund til en ny runde – kun til én afsluttende genstart
        rep.RebootSuggested = !rep.RebootPending && !renamesAtStart && rep.Installed.Any(i => i.Phase == UpdatePhases.Apps) && Collectors.FileRenamesPending();
        if (wuDead && rep.Error is null) rep.Error = $"Windows Update svarede ikke inden for {InstallLimit.TotalMinutes:0} min. – resten venter til næste runde";
        var status = UpdateStatus.Of(rep);
        lines.Add($"\nRunde {round}/{max}: {UpdateStatus.Text(rep)}{(rep.RebootPending ? " – genstart kræves" : "")}");
        return new(status, rep.RebootPending ? 3010 : 0, string.Join('\n', lines).Trim(), Update: rep);
    }

    /// <summary>
    /// Installér en batch; det, der fejler, prøves én gang mere for sig selv. Returnerer, om der kræves genstart, og om Windows Update
    /// hang (så resten venter til næste runde).
    /// </summary>
    private static async Task<(bool Reboot, bool Dead)> InstallBatch(List<UpdateCandidate> batch, string phase, string head, UpdateReport rep, List<string> lines, CancellationToken ct)
    {
        if (batch.Count == 0) return (false, false);
        var titles = batch.ToDictionary(c => c.Id, c => c.Title, StringComparer.OrdinalIgnoreCase);
        List<WindowsUpdate.WuResult> results; bool reboot;
        try { (results, reboot) = await Task.Run(() => WindowsUpdate.InstallSet(titles.Keys, head), ct).WaitAsync(InstallLimit, ct); }
        catch (TimeoutException)
        {
            foreach (var c in batch) rep.Later(WuId(c.Id), c.Title);
            lines.Add($"!!  Windows Update svarede ikke inden for {InstallLimit.TotalMinutes:0} min. – {batch.Count} opdatering(er) venter til næste runde");
            return (false, true);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            results = batch.Select(c => new WindowsUpdate.WuResult(c.Id, c.Title, 4, e.HResult)).ToList(); reboot = false;
            lines.Add($"!!  Installationen fejlede: {e.Message}");
        }
        var retry = new List<WindowsUpdate.WuResult>();
        foreach (var r in results)
            if (r.Ok) { rep.Add(phase, WuId(r.Id), r.Title); lines.Add(r.Warn ? $"OK  {r.Title} (installeret med en advarsel, {r.Hex})" : $"OK  {r.Title}"); }
            else retry.Add(r);
        // ikke fundet længere (installeret i mellemtiden eller erstattet) er ikke en fejl
        foreach (var gone in titles.Keys.Where(id => results.All(r => !r.Id.Equals(id, StringComparison.OrdinalIgnoreCase))))
            lines.Add($"--  {titles[gone]}: venter ikke længere");
        foreach (var r in retry)
        {
            if (batch.Count == 1) { rep.Fail(phase, WuId(r.Id), r.Title, r.Hex); lines.Add($"!!  {r.Title} ({r.Hex})"); continue; }
            // fejlede i batchen: én gang mere alene
            lines.Add($"..  {r.Title} fejlede i batchen ({r.Hex}) – prøver den alene");
            try
            {
                var (again, rb) = await Task.Run(() => WindowsUpdate.InstallSet([r.Id], $"{head}: nyt forsøg"), ct).WaitAsync(InstallLimit, ct);
                reboot |= rb;
                if (again.Count == 0) lines.Add($"--  {r.Title}: venter ikke længere (installeret eller erstattet)");
                else if (again[0].Ok) { rep.Add(phase, WuId(r.Id), r.Title); lines.Add($"OK  {r.Title} (andet forsøg)"); }
                else { rep.Fail(phase, WuId(r.Id), r.Title, again[0].Hex); lines.Add($"!!  {r.Title} ({again[0].Hex})"); }
            }
            catch (TimeoutException)
            {
                rep.Later(WuId(r.Id), r.Title);
                lines.Add($"!!  {r.Title}: Windows Update svarede ikke – venter til næste runde");
                return (reboot, true);
            }
            catch (Exception e) when (e is not OperationCanceledException) { rep.Fail(phase, WuId(r.Id), r.Title, $"0x{e.HResult:X8}"); lines.Add($"!!  {r.Title}: {e.Message}"); }
        }
        return (reboot, false);
    }

    private static string? WuIdOrNull(string? id) => string.IsNullOrEmpty(id) ? id : id.StartsWith(DeepUpdates.WuPrefix, StringComparison.Ordinal) ? id : WuId(id);

    private static string WuId(string id) => DeepUpdates.WuPrefix + id;

    /// <summary>
    /// Før firmware: sæt BitLocker på pause for én genstart, så pc'en ikke beder om gendannelsesnøglen bagefter.
    /// Returnerer false, hvis BitLocker er slået til, og pausen ikke lykkedes – så springes firmwaren over.
    /// </summary>
    private async Task<(bool Ok, string Line)> SuspendBitLocker(CancellationToken ct)
    {
        var r = await runner.PowerShell(
            "$v = Get-BitLockerVolume -MountPoint $env:SystemDrive -ErrorAction SilentlyContinue; " +
            "if ($v -and $v.ProtectionStatus -eq 'On') { try { Suspend-BitLocker -MountPoint $env:SystemDrive -RebootCount 1 -ErrorAction Stop | Out-Null; 'SUSPENDED' } catch { 'FAILED ' + $_.Exception.Message } } else { 'OFF' }",
            RunAs.System, TimeSpan.FromMinutes(2), ct);
        var o = r.Output.Trim();
        if (r.ExitCode == 0 && o.EndsWith("OFF", StringComparison.Ordinal)) return (true, "");
        if (r.ExitCode == 0 && o.EndsWith("SUSPENDED", StringComparison.Ordinal)) return (true, "--  BitLocker er sat på pause til næste genstart (firmware).");
        return (false, $"!!  BitLocker kunne ikke sættes på pause ({(o.Length > 0 ? o : $"fejlkode {r.ExitCode}")}) – firmware springes over.");
    }
}
