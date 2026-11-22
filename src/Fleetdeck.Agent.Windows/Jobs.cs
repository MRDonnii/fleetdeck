using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Windows;

/// <summary>winget: fundet direkte i WindowsApps, så det virker som SYSTEM.</summary>
public sealed class Winget(Runner runner, AgentOptions opt)
{
    // 0 = ok, 0x8A15002B = ingen nyere version, 0x8A150061 = allerede installeret, 0x8A150109 = genstart kræves
    public static readonly int[] Ok = [0, unchecked((int)0x8A15002B), unchecked((int)0x8A150061), unchecked((int)0x8A150109)];
    public static readonly int[] UninstallOk = [.. Ok, unchecked((int)0x8A150014) /*ikke installeret*/, 3010, 1605];

    public static string? SystemPath()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        try
        {
            return Directory.EnumerateDirectories(root, "Microsoft.DesktopAppInstaller_*_x64__8wekyb3d8bbwe")
                .Select(d => (d, v: Version.TryParse(Path.GetFileName(d).Split('_')[1], out var v) ? v : new Version()))
                .OrderByDescending(x => x.v).Select(x => Path.Combine(x.d, "winget.exe")).FirstOrDefault(File.Exists);
        }
        catch (Exception) { return null; }
    }

    public async Task<RunResult> Run(string args, string runAs, TimeSpan timeout, CancellationToken ct)
    {
        // install/upgrade/uninstall starter et installationsprogram: vagten holder øje med det
        var watch = Regex.IsMatch(args, @"^(install|upgrade --id|uninstall)\b");
        if (runAs != RunAs.System || !AgentOptions.IsSystem)
            return await runner.Run("winget.exe", args, runAs, timeout, null, ct, watch);     // brugerens WindowsApps-alias
        var exe = SystemPath();
        if (exe is null) return new RunResult(-2, "winget blev ikke fundet (App Installer mangler eller er ikke registreret endnu).");
        return await runner.Run(exe, args, RunAs.System, timeout, Path.GetDirectoryName(exe), ct, watch);
    }

    public const string Common = "--accept-source-agreements --disable-interactivity";
    /// <summary>Altid helt lydløst: ingen spørgsmål, ingen vinduer, ingen aftaler at klikke på.</summary>
    public const string Headless = "--silent --accept-package-agreements " + Common;

    public async Task<List<string>> ExportIds(CancellationToken ct)
    {
        Directory.CreateDirectory(opt.Work);
        var file = Path.Combine(opt.Work, "winget-export.json");
        File.Delete(file);
        await Run($"export -o \"{file}\" {Common}", RunAs.System, TimeSpan.FromMinutes(10), ct);
        if (!File.Exists(file)) return [];
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(file, ct));
        File.Delete(file);
        return doc.RootElement.GetProperty("Sources").EnumerateArray()
            .SelectMany(s => s.GetProperty("Packages").EnumerateArray()).Select(p => p.GetProperty("PackageIdentifier").GetString() ?? "").ToList();
    }

    public async Task<List<UpgradeEntry>> Upgrades(CancellationToken ct)
    {
        var r = await Run($"upgrade --include-unknown {Common}", RunAs.System, TimeSpan.FromMinutes(10), ct);
        var list = ParseTable(r.Output);
        // "Spøgelser": opdateret med succes, men winget viser stadig samme version (fx Windows App Runtime, hvor den nye
        // version installeres ved siden af den gamle). Skjules i 7 dage, så de ikke står på listen og fejler for evigt.
        var done = Done();
        return list.Where(u => !(done.TryGetValue(u.Id, out var d) && d.Current == u.Current && d.At > DateTime.UtcNow.AddDays(-7))).ToList();
    }

    private string DoneFile => Path.Combine(opt.Dir, "upgraded.json");
    private sealed record DoneEntry(string Current, DateTime At);
    private Dictionary<string, DoneEntry> Done()
    {
        try { return File.Exists(DoneFile) ? JsonSerializer.Deserialize<Dictionary<string, DoneEntry>>(File.ReadAllText(DoneFile)) ?? [] : []; }
        catch (Exception e) when (e is JsonException or IOException) { return []; }
    }
    /// <summary>Husk en opdatering, der lykkedes (id og den version, der var før).</summary>
    public void MarkUpgraded(string id, string? current)
    {
        try
        {
            var d = Done(); d[id] = new DoneEntry(current ?? "", DateTime.UtcNow);
            foreach (var old in d.Where(kv => kv.Value.At < DateTime.UtcNow.AddDays(-7)).Select(kv => kv.Key).ToList()) d.Remove(old);
            File.WriteAllText(DoneFile, JsonSerializer.Serialize(d));
        }
        catch (IOException) { }
    }

    /// <summary>Den installerede version er ukendt for winget (fx Battle.net): så kan den ikke opdateres herfra.</summary>
    public static bool UnknownVersion(string? current) => string.IsNullOrWhiteSpace(current) || current.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
        || current.Equals("Ukendt", StringComparison.OrdinalIgnoreCase) || current.StartsWith('<');

    /// <summary>Sidste meningsfulde linje fra winget (fejlbeskeden), til jobbets output.</summary>
    public static string LastLine(string output) => output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .LastOrDefault(l => l.Length > 3 && !l.StartsWith('[') && !l.All(c => c is '-' or '\\' or '|' or '/' or ' ')) ?? "";

    /// <summary>Læser winget's tabel ud fra kolonnepositionerne (uafhængigt af sprog).</summary>
    public static List<UpgradeEntry> ParseTable(string text)
    {
        var lines = text.Split('\n');
        var sep = Array.FindIndex(lines, l => Regex.IsMatch(l, "^-{10,}"));
        if (sep < 1) return [];
        var starts = Regex.Matches(lines[sep - 1], @"\S+").Select(m => m.Index).ToArray();
        if (starts.Length < 4) return [];
        string Cell(string l, int i)
        {
            if (starts[i] >= l.Length) return "";
            var end = i + 1 < starts.Length ? Math.Min(starts[i + 1], l.Length) : l.Length;
            return l[starts[i]..end].Trim();
        }
        var res = new List<UpgradeEntry>();
        foreach (var l in lines.Skip(sep + 1))
        {
            if (l.Length <= starts[3]) break;
            var id = Cell(l, 1);
            if (id.Length == 0 || id.Contains(' ')) continue;
            res.Add(new UpgradeEntry { Name = Cell(l, 0), Id = id, Current = Cell(l, 2), Available = Cell(l, 3) });
        }
        return res;
    }
}

public sealed record JobOutcome(string Status, int? ExitCode, string Output, Func<Task>? After = null, IosReport? Ios = null, UpdateReport? Update = null, JobResult? Bios = null)
{
    public static JobOutcome From(RunResult r, int[] okCodes) => r.NoUser
        ? new(JobStatus.WaitingUser, null, "Venter på at en bruger logger ind.")
        : r.Cancelled ? new(JobStatus.Cancelled, r.ExitCode, r.Output)
        : new(r.ExitCode is int c && okCodes.Contains(c) ? JobStatus.Success : JobStatus.Failed, r.ExitCode, r.Output);
}

/// <summary>Udfører de jobs serveren sender.</summary>
public sealed partial class JobRunner(Runner runner, Winget winget, AgentOptions opt, ILogger<JobRunner> log)
{
    public Func<CancellationToken, Task>? SendInventory { get; set; }
    public Func<CancellationToken, Task>? SendHealth { get; set; }
    public Func<string, string, CancellationToken, Task>? DownloadFile { get; set; }
    /// <summary>iPad via USB: serverforbindelsen til hemmeligheder, backup-dele og den låste profil.</summary>
    public UsbIpadIo? UsbIo { get; set; }
    /// <summary>bios_settings_apply: byt engangsbilletten til BIOS-adgangskoden.</summary>
    public Func<BiosSecretRequest, CancellationToken, Task<BiosSecretResponse?>>? BiosSecret { get; set; }
    /// <summary>bios_update: kopien før opdateringen, meldinger og pakken.</summary>
    public BiosUpdateIo? BiosUpdateIo { get; set; }
    public ShareInfo? Share { get; set; }
    /// <summary>"Hent logfiler": saml pakken og send den til jobbet (giver teksten til jobbets output).</summary>
    public Func<int, CancellationToken, Task<string>>? CollectLogs { get; set; }

    public const string PowerExpired = "Udløbet – bestilt for længe siden; genstarter ikke.";

    private static readonly string[] Simulated = [JobTypes.Install, JobTypes.Uninstall, JobTypes.Upgrade, JobTypes.Reboot, JobTypes.Shutdown,
        JobTypes.WinSetup, JobTypes.RemoveAgent, JobTypes.WindowsUpdate, JobTypes.Logoff, JobTypes.Lock, JobTypes.RemoveApp, JobTypes.UpdateRound];

    public async Task<JobOutcome> Execute(AgentJob j, CancellationToken ct)
    {
        if (opt.DryRun && Simulated.Contains(j.Type))
            return new(JobStatus.Success, 0, $"[Testtilstand] '{j.Title}' blev ikke udført.");
        var p = j.Params;
        switch (j.Type)
        {
            case JobTypes.Install: return await Install(j, ct);
            case JobTypes.Uninstall: return await Uninstall(j, ct);
            case JobTypes.Upgrade: return await Upgrade(p, ct);
            case JobTypes.RemoveApp: return await RemoveApp(p, ct);
            case JobTypes.WindowsUpdate:
                var (wuStatus, output, reboot) = await Task.Run(() => WindowsUpdate.InstallAll(p.IncludeDrivers ?? false), ct);
                return new(wuStatus, reboot ? 3010 : 0, output);
            case JobTypes.UpdateRound: return await UpdateRound(j, ct);
            case JobTypes.WuHide:
            {
                var ids = (p.UpdateIds ?? []).Where(x => x.StartsWith(DeepUpdates.WuPrefix, StringComparison.Ordinal)).Select(x => x[DeepUpdates.WuPrefix.Length..]).ToList();
                if (ids.Count == 0) return new(JobStatus.Failed, 1, "Ingen Windows Update-opdateringer at skjule.");
                var (hidOk, hidOut) = await Task.Run(() => WindowsUpdate.SetHidden(ids, p.Hide ?? true), ct);
                DeepUpdates.Forget();
                return new(hidOk ? JobStatus.Success : JobStatus.Failed, hidOk ? 0 : 1, hidOut);
            }
            case JobTypes.Script:
                return JobOutcome.From(await runner.PowerShell(p.Body ?? "", j.RunAs, TimeSpan.FromMinutes(p.TimeoutMin ?? 30), ct), [0]);
            case JobTypes.Reboot or JobTypes.Shutdown:
                // bestilt mens pc'en sov (fx om natten) og først hentet nu: genstart ikke midt i arbejdet – serveren bestiller en ny
                if (p.NotAfter is { } notAfter && DateTime.UtcNow > notAfter) return new(JobStatus.Success, 0, PowerExpired);
                if (p.OnlyIfPending == true && !Collectors.PendingReboot()) return new(JobStatus.Success, 0, "Ingen genstart nødvendig.");
                if (p.OnlyIfIdleMin is int idleMin && await IdleMinutes(ct) is int idle && idle < idleMin)
                    return new(JobStatus.Success, 0, $"Pc'en er i brug (sidste aktivitet for {idle} min. siden) – {(j.Type == JobTypes.Reboot ? "genstarter" : "slukker")} ikke.");
                var delay = Math.Clamp(p.Delay ?? 60, 0, 86400);
                var msg = string.IsNullOrWhiteSpace(p.Message) ? "Fleetdeck: pc'en genstarter om lidt." : p.Message!;
                WinPower.Schedule(j.Type, delay, msg, force: p.Force != false);
                // ellers kan pc'en nå at falde i søvn, før genstarten sker (så sker den først, når nogen vækker den)
                KeepAwake.HoldFor(TimeSpan.FromSeconds(delay + 180), $"Fleetdeck: {(j.Type == JobTypes.Reboot ? "genstart" : "sluk")} om {delay} sek.");
                return new(JobStatus.Success, 0, $"{(j.Type == JobTypes.Reboot ? "Genstarter" : "Slukker")} kl. {PlannedPower.At?.ToLocalTime():HH:mm} (om {delay} sek.).");
            case JobTypes.CancelPower:
                return WinPower.Cancel(p.Delay is > 0 ? p.Delay : null)
                    ? new(JobStatus.Success, 0, PlannedPower.At is { } at ? $"Udsat til kl. {at.ToLocalTime():HH:mm}." : "Annulleret.")
                    : new(JobStatus.Success, 0, "Der var ingen planlagt genstart eller slukning.");
            case JobTypes.Message:
                return Native.SendMessage("Besked fra Fleetdeck", p.Text ?? "")
                    ? new(JobStatus.Success, 0, "Beskeden er vist.")
                    : p.Notice == true ? new(JobStatus.Success, 0, "Ingen er logget ind – beskeden er sprunget over.") : new(JobStatus.WaitingUser, null, "Ingen er logget ind.");
            case JobTypes.AmtActivate or JobTypes.AmtDeactivate:
            {
                if (p.Text is not { Length: >= 8 } pw) return new(JobStatus.Failed, 1, "Mangler AMT-adgangskoden fra Fleetdeck.");
                if (!Amt.Present()) return new(JobStatus.Failed, 1, "Denne pc har ikke Intel AMT.");
                var (amtOk, amtOut) = j.Type == JobTypes.AmtActivate ? await Amt.Activate(pw, ct) : await Amt.Deactivate(pw, ct);
                var info = await Amt.Info(forceRefresh: true, ct: ct);
                var tail = info is null ? "" : $"\nStatus nu: {info.Mode ?? info.Error ?? "ukendt"}{(info.AmtIp is { Length: > 0 } ip ? $" · AMT-adresse {ip}" : "")}";
                return new(amtOk ? JobStatus.Success : JobStatus.Failed, amtOk ? 0 : 1, amtOut + tail);
            }
            case JobTypes.CollectLogs:
                if (CollectLogs is null) return new(JobStatus.Failed, 1, "Logfiler kan ikke hentes her.");
                try { return new(JobStatus.Success, 0, await CollectLogs(j.Id, ct)); }
                catch (HttpRequestException e) { return new(JobStatus.Failed, 1, $"Logfilerne kunne ikke sendes: {e.Message}"); }
            case JobTypes.KillHeadless:
                // hovedløse browsere (fx efterladt af automatisering); browsere med vindue røres ikke
                return JobOutcome.From(await runner.PowerShell(
                    "$p = Get-CimInstance Win32_Process | Where-Object { $_.Name -match '^(chrome|msedge|chromium|firefox|headless_shell)\\.exe$' -and $_.CommandLine -match '--headless' -and $_.CommandLine -notmatch '--type=' }; " +
                    "$p | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }; \"Lukkede $(@($p).Count) hovedløse browsere.\"",
                    RunAs.System, TimeSpan.FromMinutes(2), ct), [0]);
            case JobTypes.WakeScreen:
                // før fjernskrivebord: bevæg musen en smule i brugerens session, så skærmen tænder
                return JobOutcome.From(await runner.PowerShell(
                    "Add-Type -Namespace W -Name U -MemberDefinition '[DllImport(\"user32.dll\")] public static extern void mouse_event(int f,int x,int y,int d,int e);'; " +
                    "[W.U]::mouse_event(1,1,0,0,0); Start-Sleep -Milliseconds 100; [W.U]::mouse_event(1,-1,0,0,0); 'Skærmen er tændt.'",
                    RunAs.User, TimeSpan.FromSeconds(30), ct), [0]);
            case JobTypes.Lock:
                return JobOutcome.From(await runner.Run("rundll32.exe", "user32.dll,LockWorkStation", RunAs.User, TimeSpan.FromSeconds(30), null, ct), [0]);
            case JobTypes.Logoff:
                return Native.Logoff() ? new(JobStatus.Success, 0, "Brugeren er logget af.") : new(JobStatus.Success, 0, "Ingen var logget ind.");
            case JobTypes.Inventory:
                if (SendInventory is not null) await SendInventory(ct);
                return new(JobStatus.Success, 0, "Inventar og helbred sendt.");
            case JobTypes.Health:
                if (SendHealth is not null) await SendHealth(ct);
                return new(JobStatus.Success, 0, "Helbred sendt.");
            case JobTypes.WinSetup: return await WinSetupStep(j, ct);
            case JobTypes.UsbIpad:
            {
                if (UsbIo is null) return new(JobStatus.Failed, 1, "Agenten er ikke klar til iPad-jobs endnu.");
                var r = await UsbIpadJobs.Run(j, UsbIo, ct);
                return new(r.Status, r.ExitCode, r.Output, Ios: r.Ios);
            }
            case JobTypes.BiosSettingsRead or JobTypes.BiosSettingsApply:
            {
                if (BiosSecret is null) return new(JobStatus.Failed, 1, "Agenten er ikke klar til BIOS-jobs endnu.");
                var backend = await Task.Run(() => LenovoBiosWmi.TryOpen() is { } w ? new LenovoWmiSettings(w) : null, ct);
                var r = await BiosSettingsJob.Run(j, backend, BiosSecret, ct);
                return new(r.Status, r.ExitCode, r.Output, Bios: r);
            }
            case JobTypes.BiosUpdate:   // kun på et tryk; kopi af indstillingerne først; aldrig i "Opdatér alt"
            {
                if (BiosUpdateIo is null) return new(JobStatus.Failed, 1, "Agenten er ikke klar til BIOS-jobs endnu.");
                var backend = await Task.Run(() => LenovoBiosWmi.TryOpen() is { } w ? new LenovoWmiSettings(w) : null, ct);
                var r = await BiosUpdateJob.Run(j, BiosUpdates.Updater(opt), backend, BiosUpdateIo, BiosUpdates.Store(opt), ct);
                return new(r.Status, r.ExitCode, r.Output, Bios: r);
            }
            case JobTypes.UsbDisks or JobTypes.BiosUsbWrite:   // USB-stik med BIOS-opdateringen: kun det stik, der er bekræftet her
                return await BiosUsbJob(j, ct);
            case JobTypes.RemoveAgent:
                return new(JobStatus.Success, 0, "Agenten afinstalleres.", () =>
                {
                    var exe = Environment.ProcessPath!;
                    Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 3 >nul & \"{exe}\" uninstall") { CreateNoWindow = true, UseShellExecute = false });
                    return Task.CompletedTask;
                });
            default: return new(JobStatus.Failed, -1, $"Ukendt jobtype: {j.Type}");
        }
    }

    private async Task<JobOutcome> Install(AgentJob j, CancellationToken ct)
    {
        var p = j.Params;
        switch (p.PackageType)
        {
            case PackageTypes.Steam:
            {
                if (!PackageIds.SteamId(p.Identifier)) return new(JobStatus.Failed, 1, $"Ugyldigt Steam-id: {p.Identifier}");
                var o = JobOutcome.From(await runner.Run("cmd.exe", $"/c start \"\" \"steam://install/{p.Identifier}\"", RunAs.User, TimeSpan.FromMinutes(1), null, ct), [0]);
                return o.Status == JobStatus.Success
                    ? o with { Output = $"Installationen af {p.Name} er startet i Steam. Steam skal være logget ind; spillet hentes i baggrunden." } : o;
            }
            case PackageTypes.Uri:
                if (!PackageIds.SafeUri(p.Identifier)) return new(JobStatus.Failed, 1, "Adressen må ikke indeholde anførselstegn, mellemrum eller kontroltegn.");
                return JobOutcome.From(await runner.Run("cmd.exe", $"/c start \"\" \"{p.Identifier}\"", RunAs.User, TimeSpan.FromMinutes(1), null, ct), [0]);
            case PackageTypes.File:
                // ny mappe i den beskyttede work-mappe (i brugerens session: kun den bruger må læse) – aldrig en mappe, brugere kan skrive i
                var dir = Hardening.NewRunDir(opt, forUser: j.RunAs != RunAs.System && AgentOptions.IsSystem);
                var file = Path.Combine(dir, PackageIds.FileName(p.Identifier));
                try
                {
                    await DownloadFile!(p.FileUrl!, file, ct);
                    if (j.RunAs != RunAs.User && runner.Refused(file) is { } refused) return JobOutcome.From(refused, [0]);
                    var msiArgs = Regex.IsMatch(p.Args ?? "", @"(^|\s)/q", RegexOptions.IgnoreCase) ? p.Args : $"/qn /norestart {p.Args}";   // MSI altid lydløst
                    var r = file.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
                        ? await runner.Run("msiexec.exe", $"/i \"{file}\" {msiArgs}", j.RunAs, TimeSpan.FromMinutes(90), dir, ct, watch: true)
                        : await runner.Run(file, p.Args ?? "", j.RunAs, TimeSpan.FromMinutes(90), dir, ct, watch: true);
                    return JobOutcome.From(r, [0, 3010, 1641]);
                }
                finally { try { WindowsAcl.RemoveNoFollow(dir); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
            default:
                var args = $"install --id \"{p.Identifier}\" --exact --source {p.PackageType} {Winget.Headless} {p.Args}";
                var res = await winget.Run(args, j.RunAs, TimeSpan.FromMinutes(90), ct);
                // pakken angiver ikke installationsomfang: "--scope machine" udelukker den – prøv igen uden
                if (res.ExitCode != 0 && p.Args?.Contains("--scope", StringComparison.OrdinalIgnoreCase) == true &&
                    res.Output.Contains("No package found matching input criteria", StringComparison.OrdinalIgnoreCase))
                {
                    var plain = System.Text.RegularExpressions.Regex.Replace(p.Args, @"--scope\s+\S+", "").Trim();
                    var again = await winget.Run($"install --id \"{p.Identifier}\" --exact --source {p.PackageType} {Winget.Headless} {plain}", j.RunAs, TimeSpan.FromMinutes(90), ct);
                    res = again with { Output = res.Output + "\n[Agent] Pakken angiver ikke omfang – prøver igen uden --scope\n" + again.Output };
                }
                return JobOutcome.From(res, Winget.Ok);
        }
    }

    private async Task<JobOutcome> Uninstall(AgentJob j, CancellationToken ct)
    {
        var p = j.Params;
        switch (p.PackageType)
        {
            case PackageTypes.Steam:
                if (!PackageIds.SteamId(p.Identifier)) return new(JobStatus.Failed, 1, $"Ugyldigt Steam-id: {p.Identifier}");
                return JobOutcome.From(await runner.Run("cmd.exe", $"/c start \"\" \"steam://uninstall/{p.Identifier}\"", RunAs.User, TimeSpan.FromMinutes(1), null, ct), [0]);
            case PackageTypes.File:
                var entry = FindUninstall(p.Detect, p.Name);
                if (entry is null) return new(JobStatus.Success, 0, "Programmet er ikke installeret.");
                var cmd = entry.Value.Quiet ?? entry.Value.Normal;
                if (Regex.IsMatch(cmd, "msiexec", RegexOptions.IgnoreCase)) cmd = Regex.Replace(cmd, "/I", "/X", RegexOptions.IgnoreCase) + " /qn /norestart";
                if (j.RunAs != RunAs.User && ExecGuard.ExeOfCommandLine(cmd) is { } unExe && runner.Refused(unExe) is { } unRefused)
                    return JobOutcome.From(unRefused, Winget.UninstallOk);
                return JobOutcome.From(await runner.Run("cmd.exe", $"/c \"{cmd}\"", j.RunAs, TimeSpan.FromMinutes(60), null, ct, watch: true), Winget.UninstallOk);
            default:
                return JobOutcome.From(await winget.Run($"uninstall --id \"{p.Identifier}\" --exact --silent {Winget.Common}", j.RunAs, TimeSpan.FromMinutes(60), ct), Winget.UninstallOk);
        }
    }

    private static (string Normal, string? Quiet)? FindUninstall(string? detect, string? name)
    {
        var rx = new Regex(string.IsNullOrWhiteSpace(detect) ? Regex.Escape(name ?? "?") : detect, RegexOptions.IgnoreCase);
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var root = hk.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (root is null) continue;
            foreach (var sub in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(sub);
                if (k?.GetValue("DisplayName") is string dn && rx.IsMatch(dn) && k.GetValue("UninstallString") is string us)
                    return (us, k.GetValue("QuietUninstallString") as string);
            }
        }
        return null;
    }

    /// <summary>Minutter siden brugeren sidst rørte mus eller tastatur. null = ingen er logget ind (eller det kunne ikke måles).</summary>
    private async Task<int?> IdleMinutes(CancellationToken ct)
    {
        const string ps = """
            Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class WsIdle { [StructLayout(LayoutKind.Sequential)] struct L { public uint s; public uint t; } [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref L l); public static uint Ms() { var l = new L(); l.s = 8; GetLastInputInfo(ref l); return (uint)Environment.TickCount - l.t; } }'
            [math]::Floor([WsIdle]::Ms() / 60000)
            """;
        var r = await runner.PowerShell(ps, RunAs.User, TimeSpan.FromMinutes(1), ct);
        if (r.NoUser) return null;
        return int.TryParse(r.Output.Trim().Split('\n').LastOrDefault()?.Trim(), out var m) ? m : 0;   // ukendt = regn som i brug
    }

    /// <summary>
    /// Opdaterer ét program helt lydløst: finder installationstypen (Inno/MSI/NSIS) for ekstra lydløs-flag,
    /// lukker programmet, hvis det kører (ellers venter installationen usynligt på, at det bliver lukket),
    /// og starter det igen for brugeren bagefter.
    /// </summary>
    /// <param name="runAs">Pakkens "Kør som" (fx bruger for Spotify/Discord, der installeres pr. bruger)</param>
    /// <param name="timeout">Højst så længe pr. program (vagten stopper normalt et hængende program længe før)</param>
    private async Task<RunResult> UpgradeOne(string id, string? name, List<string> notes, CancellationToken ct, string runAs = RunAs.System, TimeSpan? timeout = null)
    {
        if (EdgeGuids.TryGetValue(id, out var guid)) return await EdgeSelfUpdate(guid, notes, ct);
        var system = runAs == RunAs.System;
        if (!system && Native.ConsoleUser() is null)
            return new RunResult(null, "", NoUser: true);
        var app = name is null ? null : Headless.Find(name);
        var custom = Headless.CustomArgs(app?.Kind);
        var restart = Headless.CloseRunning(app?.InstallDir, m => { notes.Add($"    {m}"); log.LogInformation("{Msg}", m); });
        var args = $"upgrade --id \"{id}\" --exact --include-unknown {Winget.Headless}" + (custom.Length > 0 ? $" --custom \"{custom}\"" : "");
        var r = await winget.Run(args, runAs, timeout ?? TimeSpan.FromMinutes(60), ct);
        // 0x8A15005F = "Install location must be provided" (fx Battle.net): prøv igen med programmets egen mappe
        if (r.ExitCode == unchecked((int)0x8A15005F) && app?.InstallDir is { Length: > 0 } loc)
        {
            notes.Add($"    winget skal have installationsmappen – prøver igen med {loc}");
            r = await winget.Run($"{args} --location \"{loc}\"", runAs, timeout ?? TimeSpan.FromMinutes(60), ct);
        }
        // Fejler det som SYSTEM, er det ofte et program installeret pr. bruger (fx Teams, Battle.net): prøv én gang i brugerens session
        if (system && !(r.ExitCode is int ok0 && Winget.Ok.Contains(ok0)) && !r.Cancelled && r.Stopped is null && !r.TimedOut && Native.ConsoleUser() is not null)
        {
            notes.Add($"    Fejlede som SYSTEM (0x{r.ExitCode ?? 0:X8}) – prøver igen som brugeren");
            var userArgs = $"upgrade --id \"{id}\" --exact --include-unknown {Winget.Headless}";
            var ru = await winget.Run(userArgs, RunAs.User, timeout ?? TimeSpan.FromMinutes(60), ct);
            if (!ru.NoUser) r = ru with { Output = r.Output + "\n--- som brugeren ---\n" + ru.Output };
        }
        if (r.Stopped is not null) notes.Add($"    {r.Stopped}");
        foreach (var exe in restart)
        {
            if (runner.Launch(exe)) notes.Add($"    Startede {Path.GetFileName(exe)} igen");
        }
        return r;
    }

    private async Task<JobOutcome> Upgrade(JobParams p, CancellationToken ct, UpdateReport? rep = null)
    {
        if (p.WingetId is { } deep && DeepUpdates.IsDeep(deep)) return await DeepUpgrade(deep, p.Name, ct);
        if (!string.IsNullOrEmpty(p.WingetId))
        {
            var notes = new List<string>();
            var one = await UpgradeOne(p.WingetId, p.Name, notes, ct, RunAsFor(p, p.WingetId), TimeSpan.FromMinutes(90));
            var o = JobOutcome.From(one, Winget.Ok);
            return notes.Count > 0 ? o with { Output = o.Output + "\n" + string.Join('\n', notes) } : o;
        }
        LiveJob.Phase(0, 5, "Finder programmer med opdateringer");
        var ups = await winget.Upgrades(ct);
        var exclude = new HashSet<string>(p.Exclude ?? [], StringComparer.OrdinalIgnoreCase);
        var lines = new List<string>(); int done = 0, fail = 0, n = 0;
        var todo = Math.Max(1, ups.Count(u => !exclude.Contains(u.Id)));
        foreach (var u in ups)
        {
            if (exclude.Contains(u.Id)) { lines.Add($"--  {u.Name}: udeladt (undtagelse i profilen)"); rep?.Defer(u.Id, u.Name, DeferReasons.Locked); continue; }
            if (ct.IsCancellationRequested) { lines.Add($"--  {u.Name}: ikke opdateret (jobbet blev annulleret)"); rep?.Later(u.Id, u.Name); continue; }
            LiveJob.Phase(5 + 95 * n / todo, 5 + 95 * (n + 1) / todo, $"Opdaterer {u.Name} ({++n}/{todo})");
            var notes = new List<string>();
            RunResult r;
            // Ét program, der fejler eller hænger, må aldrig stoppe resten: højst 20 min pr. program, og fejl springes over
            try { r = await UpgradeOne(u.Id, u.Name, notes, ct, RunAsFor(p, u.Id), TimeSpan.FromMinutes(20)); }
            catch (Exception e) when (e is not OperationCanceledException) { r = new RunResult(-5, $"Agent-fejl: {e.Message}"); }
            if (r.ExitCode is int c && Winget.Ok.Contains(c)) { done++; lines.Add($"OK  {u.Name} {u.Current} → {u.Available}"); LiveJob.Upgraded(u.Id); winget.MarkUpgraded(u.Id, u.Current); rep?.Add(UpdatePhases.Apps, u.Id, u.Name); }
            else if (r.Cancelled) { lines.Add($"--  {u.Name}: stoppet (jobbet blev annulleret)"); rep?.Later(u.Id, u.Name); }
            else if (r.NoUser) { lines.Add($"--  {u.Name}: venter (installeres pr. bruger – ingen er logget ind)"); rep?.Defer(u.Id, u.Name, DeferReasons.User); }
            else if (r.ExitCode is int nc && NotApplicable.Contains(nc))
            {
                // fx Edge 0x8A15008E: ikke en fejl – den opdaterer sig selv
                lines.Add($"--  {u.Name}: kan ikke opdateres via winget (0x{nc:X8}) – opdaterer sig selv; springes over");
                rep?.Defer(u.Id, u.Name, DeferReasons.SelfUpdating);
            }
            else
            {
                fail++;
                var why = r.Stopped ?? (r.TimedOut ? "tidsgrænsen på 20 min blev nået" : r.ExitCode == -5 ? r.Output
                    : $"fejlkode 0x{r.ExitCode ?? 0:X8}{(Winget.LastLine(r.Output) is { Length: > 0 } ll ? $" ({ll})" : "")}");
                lines.Add($"!!  {u.Name}: {why} – sprunget over");
                rep?.Fail(UpdatePhases.Apps, u.Id, u.Name, r.ExitCode is int ec and not -5 ? $"0x{ec:X8}" : why);
            }
            lines.AddRange(notes);
        }
        if (ct.IsCancellationRequested)
        {
            lines.Add($"\nAnnulleret fra Fleetdeck. {done} opdateret, {fail} fejlede.");
            return new(JobStatus.Cancelled, null, string.Join('\n', lines));
        }
        // Office (Klik og kør) hører med til "Opdatér alt"; Windows Update har sit eget job, og grafikdriveren installeres med AMD's program
        if (!exclude.Contains(DeepUpdates.OfficeId) && !ct.IsCancellationRequested)
            try
            {
                if (await DeepUpdates.Office(ct) is { } office)
                {
                    var (ok, text) = DeepUpdates.UpdateOffice();
                    if (ok) { done++; rep?.Add(UpdatePhases.Apps, DeepUpdates.OfficeId, office.Name); } else { fail++; rep?.Fail(UpdatePhases.Apps, DeepUpdates.OfficeId, office.Name, text); }
                    lines.Add($"{(ok ? "OK " : "!! ")} {office.Name} {office.Current} → {office.Available}: {text}");
                    DeepUpdates.Forget();
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { lines.Add($"--  Office: kunne ikke tjekkes ({e.Message})"); }
        if (ups.Count == 0 && done == 0 && fail == 0) lines.Add("Alle programmer er opdaterede.");
        lines.Add($"\n{done} opdateret, {fail} fejlede.");
        return new(fail > 0 ? JobStatus.Failed : JobStatus.Success, fail, string.Join('\n', lines));
    }

    /// <summary>Én linje fra den dybe scanning (DeepUpdates): en Windows-opdatering, Office eller grafikdriveren.</summary>
    private async Task<JobOutcome> DeepUpgrade(string id, string? name, CancellationToken ct)
    {
        if (id == DeepUpdates.AmdId)
        {
            // kun når nogen trykker Opdatér ud for driveren – "Opdatér alt" springer den over, fordi skærmen blinker
            var (amdOk, amdText, _) = await DeepUpdates.InstallAmd(runner, winget, opt.Work, null, ct);
            return new(amdOk ? JobStatus.Success : JobStatus.Failed, amdOk ? 0 : 1, amdText);
        }
        if (id == AmdChipset.Id)
        {
            // kun når nogen trykker Installér ud for driveren: den kræver en indlogget administrator og en genstart bagefter
            var (chipOk, chipText, _) = await AmdChipset.Install(runner, DeepUpdates.Http, DeepUpdates.Big, opt.Work, ct);
            return new(chipOk ? JobStatus.Success : JobStatus.Failed, chipOk ? 0 : 1, chipText);
        }
        if (id == DeepUpdates.OfficeId)
        {
            var (ok, text) = DeepUpdates.UpdateOffice();
            DeepUpdates.Forget();
            return new(ok ? JobStatus.Success : JobStatus.Failed, ok ? 0 : 1, text);
        }
        var (wuOk, output, reboot) = WindowsUpdate.InstallIds([id[DeepUpdates.WuPrefix.Length..]]);
        DeepUpdates.Forget();
        if (output.StartsWith("Ingen", StringComparison.Ordinal)) output = $"{name ?? "Opdateringen"} venter ikke længere – den er nok installeret i mellemtiden.";
        return new(wuOk ? JobStatus.Success : JobStatus.Failed, wuOk ? 0 : 1, output + (reboot ? "\nGenstart kræves." : ""));
    }

    /// <summary>
    /// Edge og WebView2 kan winget ikke opdatere ("installationstypen er anderledes"), og Edges egen opdatering når ikke altid at køre
    /// (pc'en sover). Derfor bedes Edges egen opdatering (MicrosoftEdgeUpdate.exe) om at hente den nyeste version med det samme.
    /// </summary>
    private static readonly Dictionary<string, string> EdgeGuids = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.Edge"] = "{56EB18F8-B008-4CBD-B6D2-8C97FE7E9062}",
        ["Microsoft.Edge.Beta"] = "{2CD8A007-E189-409D-A2C8-9AF4EF3C72AA}",
        ["Microsoft.Edge.Dev"] = "{0D50BFEC-CD6A-4F9A-964C-C7416E3ACB10}",
        ["Microsoft.EdgeWebView2Runtime"] = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
    };

    private async Task<RunResult> EdgeSelfUpdate(string guid, List<string> notes, CancellationToken ct)
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\EdgeUpdate\MicrosoftEdgeUpdate.exe");
        if (!File.Exists(exe)) return new RunResult(unchecked((int)0x8A15008E), "Edges opdateringsprogram mangler");
        notes.Add("    opdateres med Edges egen opdatering (MicrosoftEdgeUpdate)");
        return await runner.Run(exe, $"/silent /install \"appguid={guid}&needsadmin=True\"", RunAs.System, TimeSpan.FromMinutes(20), ct: ct);
    }
    /// <summary>winget: "en nyere version findes, men installationstypen er anderledes" og "ingen opdatering kan anvendes" – ikke en fejl.</summary>
    private static readonly HashSet<int> NotApplicable = [unchecked((int)0x8A15008E), unchecked((int)0x8A15002B)];

    /// <summary>Pakkens "Kør som" fra serveren (RunAsById), ellers SYSTEM.</summary>
    private static string RunAsFor(JobParams p, string id) =>
        p.RunAsById?.FirstOrDefault(kv => string.Equals(kv.Key, id, StringComparison.OrdinalIgnoreCase)).Value switch
        {
            RunAs.User => RunAs.User, RunAs.UserAdmin => RunAs.UserAdmin, _ => RunAs.System,
        };

    private async Task<JobOutcome> WinSetupStep(AgentJob j, CancellationToken ct)
    {
        if (Share is null || string.IsNullOrEmpty(Share.Path)) return new(JobStatus.Failed, -3, "Serveren har ingen WinSetup-share sat op.");
        if (j.RunAs == RunAs.System && !await ConnectShare(ct))
            return new(JobStatus.Failed, -3, $"Kan ikke nå {Share.Path}. Sæt Unraid-bruger og adgangskode under Indstillinger.");
        var run = Path.Combine(Share.Path, "Run.ps1");
        // exit 75: brugeren har (endnu) ikke adgang til sharen – fx lige efter første login, før Bootstrap har gemt Unraid-login
        var r = await runner.PowerShell($"if (-not (Test-Path {Runner.Quote(run)})) {{ exit 75 }}\r\n& {Runner.Quote(run)} -Do {j.Params.Step} -Unattended\r\nexit $LASTEXITCODE",
            j.RunAs, TimeSpan.FromHours(4), ct);
        if (r.ExitCode == 75) return new(JobStatus.WaitingUser, null, $"Venter på at brugeren får adgang til {Share.Path} (Unraid-login gemmes ved første login).");
        return JobOutcome.From(r, [0]);
    }

    private async Task<bool> ConnectShare(CancellationToken ct)
    {
        var run = Path.Combine(Share!.Path, "Run.ps1");
        if (File.Exists(run)) return true;
        if (!string.IsNullOrEmpty(Share.User))
        {
            var r = await runner.Run("net.exe", $"use \"{Share.Path}\" /user:\"{Share.User}\" \"{Share.Password}\" /persistent:no", RunAs.System, TimeSpan.FromSeconds(30), null, ct);
            if (r.ExitCode != 0) log.LogWarning("net use fejlede: {Output}", r.Output);
        }
        return File.Exists(run);
    }
}

/// <summary>BIOS-opdatering på Windows (Lenovo). Arbejdsmappen ligger i agentens datamappe (kun SYSTEM og administratorer).</summary>
public static class BiosUpdates
{
    public static string Dir(AgentOptions opt) => Path.Combine(opt.Dir, "bios");
    public static BiosUpdateStore Store(AgentOptions opt) => new(Dir(opt));
    public static IBiosUpdater Updater(AgentOptions opt) => new LenovoWindowsUpdater(BiosProcess.Run, Dir(opt));
}
