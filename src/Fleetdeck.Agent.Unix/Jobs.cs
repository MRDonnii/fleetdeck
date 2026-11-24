using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Unix;

/// <summary>Udfører jobs fra serveren på Linux.</summary>
public sealed class JobRunner(Api api, AgentConfig cfg)
{
    private const string AptOpts = "-y -o Dpkg::Options::=--force-confdef -o Dpkg::Options::=--force-confold -o DPkg::Lock::Timeout=600";

    public async Task SendInventory(CancellationToken ct)
    {
        var inv = await Task.Run(Collect.Inventory, ct);
        await api.Post<InventoryResponse>("/api/agent/inventory", inv, ct);
        Log.Info($"Inventar sendt: {inv.Software.Count} pakker, {inv.Upgrades.Count} opdateringer");
        // programikoner, serveren mangler – en fejl her må ikke vælte inventaret
        try { await AppIcons.Upload(api, inv.Software, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) { Log.Warn($"Programikoner blev ikke sendt: {e.Message}"); }
    }

    public async Task SendHealth(CancellationToken ct)
    {
        var h = await Task.Run(Collect.Health, ct);
        try { h.Remote = await Task.Run(() => RemoteDesk.Ensure(cfg), ct); }
        catch (Exception e) when (e is not OperationCanceledException) { Log.Warn($"Fjernskrivebord: {e.Message}"); }
        h.Kept = Keeper.Report();
        try { h.Encryption = await Task.Run(DiskCrypto.Status, ct); }   // FileVault/LUKS: kun status
        catch (Exception e) when (e is not OperationCanceledException) { Log.Warn($"Kryptering: {e.Message}"); }
        try { h.Tpm = await Task.Run(TpmCollect.Status, ct); }   // TPM (Linux) / Secure Enclave (Mac)
        catch (Exception e) when (e is not OperationCanceledException) { Log.Warn($"TPM: {e.Message}"); }
        try { h.Network = await Task.Run(NetCollect.Status, ct); }   // netværksadaptere: status, hastighed, Wi-Fi
        catch (Exception e) when (e is not OperationCanceledException) { Log.Warn($"Netværk: {e.Message}"); }
        try { h.Batteries = await Task.Run(BatteryCollect.Status, ct); }   // bærbare: kapacitet, opladninger
        catch (Exception e) when (e is not OperationCanceledException) { Log.Warn($"Batteri: {e.Message}"); }
        try { h.Drives = await Task.Run(Drives.Status, ct); }
        catch (Exception e) when (e is not OperationCanceledException) { Log.Warn($"Netværksdrev-status: {e.Message}"); }
        h.Printers = Printers.States;
        h.LocalPrinters = Printers.Local;
        h.NetPrinters = Printers.Net;
        h.NetPrintersAt = Printers.NetAt;
        try { h.SpareDisks = await Task.Run(SpareCardAgent.Candidates, ct); }   // Raspberry Pi: USB-diske til reservekort
        catch (Exception e) when (e is not OperationCanceledException) { Log.Warn($"Reservekort: {e.Message}"); }
        // fejlloggen: højst hvert 10. minut (Mac: 30.), fra hvor serveren sidst fik den
        var logAt = DateTime.UtcNow;
        if (SysLogUnix.Schedule.Due(logAt))
        {
            SysLogUnix.Schedule.Tried(logAt);
            try { h.SysLog = await Task.Run(() => SysLogUnix.Collect(SysLogUnix.Schedule.Since(logAt), logAt), ct); }
            catch (Exception e) when (e is not OperationCanceledException) { Log.Warn($"Fejllog: {e.Message}"); }
        }
        await api.Post("/api/agent/health", new HealthPost { Health = h }, ct);
        if (h.SysLog is not null) SysLogUnix.Schedule.Sent(logAt);
    }

    /// <summary>Jobresultater, der venter på at nå frem (kun root kan læse dem).</summary>
    public readonly PendingResults Results = new(AgentConfig.Dir + "/results");
    private Task SendResult(int id, JobResult r, CancellationToken ct) => api.Post($"/api/agent/jobs/{id}/result", r, ct);
    /// <summary>Send resultater, der ikke nåede frem sidst, igen – til serveren har kvitteret.</summary>
    public Task FlushResults(CancellationToken ct) => Results.Flush(SendResult, Log.Info, ct);

    /// <summary>Kør ét job. Returnerer false hvis agenten skal stoppe (fjernet).</summary>
    public async Task<bool> Run(AgentJob j, CancellationToken ct)
    {
        var start = await api.Post<JobStartResponse>($"/api/agent/jobs/{j.Id}/start", new { }, ct);
        if (!start.Ok) return true;
        Log.Info($"Job {j.Id}: {j.Title}");
        JobResult res;
        var stopAfter = false;
        LiveJob.Begin(j.Id);
        MacTray.JobStarted(j.Title);
        using var live = new CancellationTokenSource();
        using var job = CancellationTokenSource.CreateLinkedTokenSource(ct);   // annulleres, hvis jobbet stoppes i Fleetdeck
        Shell.JobCancel = job.Token;
        var reporter = ReportProgress(j.Id, job, live.Token);
        try
        {
            if (j.Type == JobTypes.WinSetup && (Mac.Is || Unraid.Is))
                res = Fail(Mac.Is ? "Genskab-kit findes endnu ikke til Mac (brug Time Machine)." : "Genskab-kit bruges ikke på Unraid (tag backup af USB-stikket under Main → Flash).");
            else if (j.Type == JobTypes.WinSetup && j.Params.Step is "Backup" or "Restore")
            {
                // Linux: genskab-kit i stedet for WinSetup's Windows-trin
                var kr = j.Params.Step == "Backup" ? await Kit.Backup(api, ct) : await Kit.Restore(api, ct);
                res = Done(kr);
            }
            else if (j.Type is JobTypes.KitBackup or JobTypes.KitRestore) res = await Recovery(j, ct);
            else if (j.Type == JobTypes.Ios) res = await Ios.Run(api, j, ct);   // iPads via kabel (kun Mac)
            else if (j.Type == JobTypes.IosApps) res = await IosApps.Run(j, ct);   // apps på en iPad via kabel (kun Mac)
            else if (j.Type == JobTypes.UsbIpad) res = await UsbIpadJobs.Run(j, api.UsbIo, job.Token);   // iPad via USB (pymobiledevice3)
            else if (j.Type == JobTypes.FileVaultKey) res = await DiskCrypto.RotateFileVault(api, j, job.Token);   // ny FileVault-nøgle (kun Mac)
            else if (j.Type == JobTypes.CollectLogs) res = await UnixLogs.Run(api, j, job.Token);   // Beskeder → "Hent logfiler"
            else if (j.Type is JobTypes.BiosSettingsRead or JobTypes.BiosSettingsApply)   // BIOS-indstillinger via firmware-attributes
                res = await BiosSettingsJob.Run(j, FirmwareAttributes.Detect(), api.BiosSecret, job.Token);
            else if (j.Type == JobTypes.BiosUpdate)   // BIOS-opdatering (UEFI-kapsel via fwupd): kun på et tryk, kopi af indstillingerne først
                res = await BiosUpdateJob.Run(j, BiosUpdates.Updater(), FirmwareAttributes.Detect(), api.BiosUpdateIo, BiosUpdates.Store, job.Token);
            else if (j.Type is JobTypes.UsbDisks or JobTypes.BiosUsbWrite)   // USB-stik med BIOS-opdateringen: kun det stik, der er bekræftet her
                res = await BiosUsbAgent.Run(api, j, job.Token);   // Linux (lsblk) og Mac (diskutil)
            else if (j.Type == JobTypes.SpareCard) res = await SpareCardAgent.Run(api, j, job.Token);   // Raspberry Pi: natlig kopi til reservekortet
            else (res, stopAfter) = await Task.Run(() => Execute(j, ct), ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { res = new JobResult { Status = JobStatus.Cancelled, Output = "Annulleret fra Fleetdeck." }; }
        catch (Exception e) { res = new JobResult { Status = JobStatus.Failed, Output = e.ToString() }; }
        finally { LiveJob.End(j.Id); live.Cancel(); await reporter; Shell.JobCancel = default; MacTray.JobEnded(); }
        if (job.IsCancellationRequested && !ct.IsCancellationRequested) res.Status = JobStatus.Cancelled;
        // når resultatet ikke frem, gemmes det og sendes igen ved næste check-in (ellers står jobbet som "kører" og ender som fejlet)
        await Results.Send(j.Id, res, SendResult, Log.Warn, ct);
        // en udvidelse (også som script, fx diskhelbred på Mac): programlisten med det samme, så Fleetdeck ser den som installeret
        var extension = j.Title.StartsWith("Udvidelse: ", StringComparison.Ordinal);
        if ((j.Type is JobTypes.Install or JobTypes.Uninstall or JobTypes.RemoveApp or JobTypes.Upgrade or JobTypes.WindowsUpdate or JobTypes.UpdateRound or JobTypes.Inventory
             || extension && j.Type == JobTypes.Script) && res.Status is JobStatus.Success or JobStatus.Partial)
            await SendInventory(ct);
        if (extension) MacTray.ExtensionsChanged();
        else if (j.Type == JobTypes.Health) await SendHealth(ct);
        // nyinstalleret program (fx fjernskrivebord): send helbred med det samme, så det bliver sat op og vist
        if (j.Type is JobTypes.Install or JobTypes.Script && res.Status == JobStatus.Success && j.Type != JobTypes.Health)
            try { await SendHealth(ct); } catch (HttpRequestException e) { Log.Warn($"Helbred: {e.Message}"); }
        if (stopAfter) StartRemoval();
        return !stopAfter;
    }

    /// <summary>
    /// Sender jobbets fremdrift hvert 4. sekund, når der er noget nyt, og mindst hvert 10. sekund.
    /// Svaret fortæller, om jobbet er stoppet i Fleetdeck – så stoppes det her.
    /// </summary>
    private async Task ReportProgress(int jobId, CancellationTokenSource job, CancellationToken ct)
    {
        var last = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(4000, ct); } catch (OperationCanceledException) { return; }
            if (LiveJob.Take(DateTime.UtcNow - last > TimeSpan.FromSeconds(10)) is not { } t) continue;
            last = DateTime.UtcNow;
            if (t.JobId == jobId) MacTray.JobProgress(t.Post.Percent, t.Post.Step);
            try
            {
                var r = await api.Post<JobProgressResponse>($"/api/agent/jobs/{t.JobId}/progress", t.Post, ct);
                if (r?.Cancel == true && t.JobId == jobId && !job.IsCancellationRequested) { Log.Info($"Job {jobId} stoppet fra Fleetdeck"); job.Cancel(); }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { }
        }
    }

    private static JobResult Done(ShellResult r, string? okText = null) => new()
    {
        Status = r.ExitCode == 0 ? JobStatus.Success : JobStatus.Failed, ExitCode = r.ExitCode,
        Output = (r.TimedOut ? "[Agent] Tiden løb ud.\n" : "") + (r.Output.Length > 0 ? r.Output : okText ?? ""),
    };
    /// <summary>fwupdmgr update: fejl, der ikke kan løses ved at prøve igen (firmwaren afviser en Secure Boot-nøgle …), får en dansk
    /// forklaring øverst og en særlig grund, så serveren ikke foreslår opdateringen igen.</summary>
    internal static JobResult Firmware(ShellResult r)
    {
        var res = Done(r);
        if (r.ExitCode != 0 && !r.TimedOut && FwupdErrors.Classify(r.Output) is { } why)
        {
            res.Reason = why;
            res.Output = FwupdErrors.Text(why) + "\n\n" + res.Output;
        }
        return res;
    }
    private static JobResult Fail(string text) => new() { Status = JobStatus.Failed, ExitCode = 1, Output = text };
    private static JobResult Ok(string text) => new() { Status = JobStatus.Success, ExitCode = 0, Output = text };
    private static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";   // sikker bash-citering

    /// <summary>Gendannelsespunkt (profilens stier → Fleetdeck) og sikker gendannelse (overskriver intet, starter kun stoppede tjenester).</summary>
    private async Task<JobResult> Recovery(AgentJob j, CancellationToken ct)
    {
        var p = j.Params;
        if (j.Type == JobTypes.KitBackup)
        {
            var paths = (p.Paths ?? []).Where(RecoveryKit.SafePath).ToList();
            var file = $"{UnixRoot.EnsureRootOnly(UnixRoot.KitWork)}/{Environment.MachineName.ToLowerInvariant()}-{DateTime.Now:yyyyMMdd-HHmmss}.tar.gz";
            var r = Shell.Run(RecoveryKit.BuildScript(paths, file), TimeSpan.FromMinutes(p.TimeoutMin ?? 45));
            if (r.ExitCode != 0 || !File.Exists(file)) return Done(r);
            try { await api.PutFile($"/api/agent/kit?name={Uri.EscapeDataString(Path.GetFileName(file))}", file, ct); }
            catch (HttpRequestException e) { return Fail($"Pakken er lavet, men kunne ikke sendes: {e.Message}\n{r.Output}"); }
            finally { try { File.Delete(file); } catch (IOException) { } }
            return Ok($"Gendannelsespunkt gemt i Fleetdeck: {Path.GetFileName(file)}\n{r.Output}");
        }
        if (j.Type == JobTypes.KitRestore)
        {
            if (p.Name is not { Length: > 0 } name || name.Contains('/')) return Fail("Vælg et gendannelsespunkt");
            var file = Path.Combine(UnixRoot.EnsureRootOnly(UnixRoot.KitWork), name);
            try { await api.Download($"/api/agent/kit/{Uri.EscapeDataString(name)}", file, ct); }
            catch (HttpRequestException e) { return Fail($"Kunne ikke hente {name}: {e.Message}"); }
            // "nytkort": et nyt SD-kort fra "Lav SD-kort" – det udeladte springes over, og boot-opsætningen røres ikke
            var r = Shell.Run(RecoveryKit.RestoreScript(file, p.Step == "nytkort", p.Exclude), TimeSpan.FromMinutes(p.TimeoutMin ?? 60));
            try { File.Delete(file); } catch (IOException) { }
            return Done(r);
        }
        return Fail("Ukendt gendannelsesjob");
    }

    private (JobResult, bool) Execute(AgentJob j, CancellationToken ct)
    {
        var p = j.Params;
        var minutes = TimeSpan.FromMinutes(p.TimeoutMin ?? 60);
        switch (j.Type)
        {
            case JobTypes.Install:
            case JobTypes.Uninstall:
            {
                if (p.PackageType == PackageTypes.Script)
                {
                    // projekt med eget installationsscript (fx Kiosk Warden): køres som root med jobbets tidsgrænse
                    if (j.Type != JobTypes.Install) return (Fail("Script-pakker afinstalleres med deres egen afinstallation (fx fra programlisten)."), false);
                    if (string.IsNullOrWhiteSpace(p.Args)) return (Fail("Script-pakken har intet script."), false);
                    if (Unraid.Is) return (Fail("Script-pakker installeres ikke på Unraid."), false);
                    return (Done(Shell.Run(p.Args, minutes)), false);
                }
                var id = (p.Identifier ?? "").Trim();
                if (id.Length == 0 || id.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '.' or '+' or '_' or ':'))) return (Fail($"Ugyldigt pakkenavn: {id}"), false);
                var install = j.Type == JobTypes.Install;
                var extra = string.Join(' ', (p.Args ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(a => a.StartsWith("--")).Select(Q));
                if (Unraid.Is)
                    return (Fail("På Unraid installeres programmer som plugins eller containere i Unraids egen webside (Apps-fanen)."), false);
                if (Mac.Is)
                {
                    // macOS: Homebrew (navn, cask:navn, formula:navn) eller App Store (mas:id)
                    var macId = p.PackageType == PackageTypes.Mas && !id.StartsWith("mas:") ? "mas:" + id : id;
                    if (p.PackageType is not (PackageTypes.Brew or PackageTypes.Mas or PackageTypes.Pkg or PackageTypes.Apt))
                        return (Fail($"Pakketypen \"{p.PackageType}\" findes ikke til Mac."), false);
                    return (Done(Mac.InstallOrRemove(macId, install, minutes)), false);
                }
                switch (p.PackageType)
                {
                    case PackageTypes.Apt or PackageTypes.Pkg:
                    {
                        // distroens egen pakkehåndtering (apt, dnf, zypper eller pacman)
                        var name = Pkg.NameFor(id);
                        if (name.Length == 0) return (Ok($"Pakken findes ikke til {Pkg.NativeName} – springes over."), false);
                        if (install) Pkg.Refresh();
                        return (Done(Shell.Run(install ? Pkg.InstallCmd(name, extra) : Pkg.RemoveCmd(name), minutes)), false);
                    }
                    case PackageTypes.Snap:
                        if (install && !Shell.Has("snap")) return (Fail("snap er ikke installeret på denne maskine (installér udvidelsen eller brug en anden pakketype)."), false);
                        return (Done(Shell.Run(install ? $"snap install {Q(id)} {extra}" : $"snap remove {Q(id)}", minutes)), false);
                    case PackageTypes.Flatpak:
                        if (!Shell.Has("flatpak")) return (Fail("flatpak er ikke installeret – installér udvidelsen \"Flatpak-programmer\" først."), false);
                        return (Done(Shell.Run(install ? $"flatpak install -y --noninteractive flathub {Q(id)}" : $"flatpak uninstall -y --noninteractive {Q(id)}", minutes)), false);
                    default:
                        return (Fail($"Pakketypen \"{p.PackageType}\" findes kun til Windows."), false);
                }
            }
            case JobTypes.RemoveApp:
                // afinstallér et program fra programlisten (systempakker prøvekøres først)
                return (RemoveApp.Run(p, minutes), false);
            case JobTypes.UpdateRound:
                return (UnixUpdateRound.Run(j), false);
            case JobTypes.Upgrade:
            case JobTypes.WindowsUpdate:
            {
                if (Unraid.Is)
                {
                    if (p.WingetId is { Length: > 0 } uOne) return (Done(Unraid.UpgradeOne(uOne, minutes)), false);
                    return (Done(Unraid.UpgradeAll(p.Exclude ?? [], TimeSpan.FromMinutes(30)), "Alt er opdateret."), false);
                }
                if (Mac.Is)
                {
                    if (p.WingetId is { Length: > 0 } macOne) return (Done(Mac.UpgradeOne(macOne, minutes)), false);
                    // "Opdatér alt" tager programmerne; Windows Update-jobbet svarer til macOS-opdateringer
                    return (Done(Mac.UpgradeAll(p.Exclude ?? [], j.Type == JobTypes.WindowsUpdate, TimeSpan.FromMinutes(p.TimeoutMin ?? 180)), "Alt er opdateret."), false);
                }
                if (p.WingetId is { Length: > 0 } fw && fw.StartsWith(Pkg.FirmwarePrefix, StringComparison.Ordinal))
                    return (Firmware(Shell.Run($"fwupdmgr update {Q(fw[Pkg.FirmwarePrefix.Length..])} -y --no-reboot-check 2>&1", minutes)), false);
                if (p.WingetId is { Length: > 0 } one)
                {
                    Pkg.Refresh(true);
                    var cmd = one.Contains('.') && Shell.Has("flatpak") && Shell.Out($"flatpak info {Q(one)} >/dev/null 2>&1 && echo ja") == "ja"
                        ? $"flatpak update -y --noninteractive {Q(one)}"
                        : $"{Pkg.UpgradeOneCmd(one)} || {{ command -v snap >/dev/null && snap refresh {Q(one)}; }}";
                    return (Done(Shell.Run(cmd, minutes)), false);
                }
                var script = Pkg.UpgradeAllScript(p.Exclude ?? []);
                return (Done(Shell.Run(script, TimeSpan.FromMinutes(p.TimeoutMin ?? 180)), "Alt er opdateret."), false);
            }
            case JobTypes.Script:
            {
                var body = p.Body ?? "";
                if (body.Length == 0) return (Fail("Tomt script"), false);
                string? user = j.RunAs == RunAs.System ? null : UserSession.Find()?.User;
                if (j.RunAs != RunAs.System && user is null) return (Fail("Ingen bruger er logget ind på skærmen."), false);
                return (Done(Shell.Run(body, minutes, user)), false);
            }
            case JobTypes.Reboot:
            case JobTypes.Shutdown:
            {
                // bestilt mens maskinen sov og først hentet nu: genstart ikke – serveren bestiller en ny med de rigtige regler
                if (p.NotAfter is { } notAfter && DateTime.UtcNow > notAfter) return (Ok("Udløbet – bestilt for længe siden; genstarter ikke."), false);
                if (j.Type == JobTypes.Reboot && p.OnlyIfPending == true && !(Mac.Is ? Mac.RebootNeeded() : Pkg.RebootNeeded(fresh: true))) return (Ok("Ingen genstart nødvendig."), false);
                // "kun hvis ingen bruger den": en skærmsession, der ikke er gået i dvale (IdleHint), regnes som i brug
                if (p.OnlyIfIdleMin is int idleMin && Shell.Out("loginctl show-seat seat0 -p ActiveSession --value 2>/dev/null") is { Length: > 0 } sid)
                {
                    var hint = Shell.Out($"loginctl show-session {Q(sid)} -p IdleHint --value");
                    var since = long.TryParse(Shell.Out($"loginctl show-session {Q(sid)} -p IdleSinceHint --value"), out var us) && us > 0
                        ? (DateTime.UtcNow - DateTime.UnixEpoch.AddTicks(us * 10)).TotalMinutes : 0;
                    if (hint != "yes" || since < idleMin)
                        return (Ok($"Maskinen er i brug (skærmen er aktiv) – {(j.Type == JobTypes.Reboot ? "genstarter" : "slukker")} ikke."), false);
                }
                var mins = Math.Max(1, (int)Math.Ceiling((p.Delay ?? 60) / 60.0));
                var msg = p.Message is { Length: > 0 } mm ? mm : j.Type == JobTypes.Reboot ? "Fleetdeck genstarter maskinen" : "Fleetdeck slukker maskinen";
                Notify(msg);
                var r = Power.Schedule(j.Type, mins, msg);
                var res = Done(r, $"{(j.Type == JobTypes.Reboot ? "Genstarter" : "Slukker")} om {mins} minut(ter).");
                if (r.ExitCode == 0) { res.PowerAt = PlannedPower.At; res.PowerMode = j.Type; }
                return (res, false);
            }
            case JobTypes.CancelPower:
            {
                var had = Power.Current() is not null;
                var r = Power.Cancel(p.Delay is int d && d > 0 ? (int)Math.Ceiling(d / 60.0) : null);
                if (!had) return (Ok("Der var ingen planlagt genstart eller slukning."), false);
                if (p.Delay is > 0) { var res = Done(r, $"Udsat til kl. {PlannedPower.At?.ToLocalTime():HH:mm}."); res.PowerAt = PlannedPower.At; res.PowerMode = PlannedPower.Mode; return (res, false); }
                Notify("Den planlagte genstart/slukning er annulleret.");
                return (Done(r, "Annulleret."), false);
            }
            case JobTypes.Message:
                return (Notify(p.Text ?? p.Message ?? "") ? Ok("Besked vist.") : p.Notice == true ? Ok("Ingen bruger på skærmen – beskeden er sprunget over.") : Fail("Kunne ikke vise beskeden (ingen bruger på skærmen?)"), false);
            case JobTypes.KillHeadless:
                return (Done(HeadlessBrowsers.Close()), false);
            case JobTypes.WakeScreen:
            {
                // før fjernskrivebord: tænd skærmen, så der er noget at se (en kiosk med skærmvagt vækkes via den)
                if (Mac.Is) return (Done(Shell.Run("caffeinate -u -t 3"), "Skærmen er tændt."), false);
                if (UserSession.Find() is not { } us) return (Ok("Ingen bruger på skærmen – intet at vække."), false);
                var warden = $"{us.Home}/kiosk/warden-state.sh";
                var r = File.Exists(warden)
                    ? Shell.Run($"{Q(warden)} on", TimeSpan.FromSeconds(60), us.User)
                    : Shell.Run("xset dpms force on 2>/dev/null; xset s reset 2>/dev/null; " +
                                "gdbus call --session --dest org.gnome.ScreenSaver --object-path /org/gnome/ScreenSaver --method org.gnome.ScreenSaver.SetActive false >/dev/null 2>&1; " +
                                "loginctl unlock-sessions 2>/dev/null; true", TimeSpan.FromSeconds(20), us.User);
                return (Done(r, File.Exists(warden) ? "Kioskens skærm er vækket." : "Skærmen er tændt."), false);
            }
            case JobTypes.Lock:
                return (Done(Mac.Is ? Mac.Lock() : Shell.Run("loginctl lock-sessions"), "Skærmen er låst."), false);
            case JobTypes.Logoff:
                if (Mac.Is) return (Done(Mac.Logoff(), "Logget af."), false);
                return (UserSession.Find() is { } s ? Done(Shell.Run($"loginctl terminate-user {Q(s.User)}"), "Logget af.") : Ok("Ingen er logget ind."), false);
            case JobTypes.Inventory:
                return (Ok("Inventar sendes."), false);
            case JobTypes.Health:
                return (Ok("Helbred sendes."), false);
            case JobTypes.RemoveAgent:
                return (Ok("Agenten fjerner sig selv om få sekunder."), true);
            default:
                return (Fail($"\"{j.Type}\" findes kun til Windows."), false);
        }
    }

    /// <summary>Besked på skærmen (GNOME-notifikation) og i terminaler.</summary>
    internal static bool Notify(string text)
    {
        if (Mac.Is) return Mac.Notify(text);
        if (text.Length == 0) return false;
        Shell.Run($"wall {Q("Fleetdeck: " + text)}", TimeSpan.FromSeconds(10));
        var s = UserSession.Find();
        return s is not null && Shell.Run($"notify-send -u critical -a Fleetdeck Fleetdeck {Q(text)}", TimeSpan.FromSeconds(10), s.User).ExitCode == 0;
    }

    private static void StartRemoval()
    {
        if (Mac.Is) { Mac.StartRemoval(); return; }
        if (Unraid.Is) { Unraid.StartRemoval(); return; }
        Init.RunDetached(Init.RemovalScript);
    }
}

/// <summary>BIOS-opdatering på Linux: UEFI-kapsel via fwupd. Tilstanden ligger i /var/lib/fleetdeck/bios og overlever genstarter.</summary>
public static class BiosUpdates
{
    public static readonly BiosUpdateStore Store = new(LinuxCapsuleUpdater.DefaultWork);
    /// <summary>Kun Linux (Mac'ens firmware følger macOS).</summary>
    public static IBiosUpdater? Updater() => Mac.Is ? null : new LinuxCapsuleUpdater(new FirmwareFs(), BiosProcess.Run);
}
