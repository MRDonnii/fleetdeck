using System.Security;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Windows;

/// <summary>
/// Job "remove_app": afinstallerer et program fra programlisten – altid helt lydløst og med vagten, der svarer på dialogbokse.
/// UninstallId fra inventaret siger hvordan: "HKLM|64|nøgle", "HKLM|32|nøgle", "HKU|sid|nøgle", "store|PackageFullName" eller "steam|appid".
/// </summary>
public sealed partial class JobRunner
{
    private const string UninstallRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private static readonly TimeSpan RemoveTimeout = TimeSpan.FromMinutes(60);
    /// <summary>0 = ok, 3010 = genstart kræves, 1605 = ikke installeret (MSI)</summary>
    private static readonly int[] RemoveOk = [0, 3010, 1605];
    private static readonly int[] WingetRemoveOk = [.. Winget.Ok, 3010, 1605];
    private const int WingetNoPackage = unchecked((int)0x8A150014);

    private static readonly string[] ProtectedNames = ["Microsoft Visual C++", ".NET", "Windows Software Development Kit", "Microsoft Edge WebView2", "Fleetdeck"];
    private static readonly Regex DriverVendor = new(@"\b(Intel|NVIDIA|AMD|Advanced Micro Devices|Realtek)\b", RegexOptions.IgnoreCase);

    private async Task<JobOutcome> RemoveApp(JobParams p, CancellationToken ct)
    {
        var id = p.Identifier?.Trim() ?? "";
        var name = p.Name?.Trim() ?? "";
        if (IsProtected(name, null, DirOf(p.Path))) return Refuse();
        var parts = id.Split('|', 3);
        switch (parts[0].ToUpperInvariant())
        {
            case "HKLM" or "HKU" when parts.Length == 3:
                return await RemoveRegistered(parts, p, ct);
            case "STORE" when id.Length > 6:
                return await RemoveStore(id[6..], ct);
            case "STEAM" when parts.Length >= 2:
                return await RemoveSteam(parts[1], name, ct);
            case "":
                return await RemoveByName(name, p.Path, ct);
            default:
                return new(JobStatus.Failed, -1, $"Ved ikke hvordan programmet afinstalleres (ukendt id: {id}).");
        }
    }

    private static JobOutcome Refuse() => new(JobStatus.Failed, -1, "Fleetdeck afinstallerer ikke systemkomponenter og drivere herfra.");

    /// <summary>Runtimes, SDK'er, drivere og agenten selv afinstalleres ikke fra programlisten.</summary>
    private static bool IsProtected(string? name, string? publisher, string? dir)
    {
        name ??= "";
        if (ProtectedNames.Any(x => name.Contains(x, StringComparison.OrdinalIgnoreCase))) return true;
        if (name.Contains("Driver", StringComparison.OrdinalIgnoreCase) && DriverVendor.IsMatch($"{publisher} {name}")) return true;
        var self = Path.GetDirectoryName(Environment.ProcessPath);
        if (self is null || string.IsNullOrWhiteSpace(dir)) return false;
        try
        {
            var d = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
            var s = Path.GetFullPath(self).TrimEnd('\\') + "\\";
            return d.StartsWith(s, StringComparison.OrdinalIgnoreCase) || s.StartsWith(d, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static string? DirOf(string? exe)
    {
        if (string.IsNullOrWhiteSpace(exe)) return null;
        try { return Path.GetDirectoryName(exe.Trim('"')); } catch (ArgumentException) { return null; }
    }

    // ------------------------------------------------------------------ Uninstall-registreringen

    private async Task<JobOutcome> RemoveRegistered(string[] id, JobParams p, CancellationToken ct)
    {
        var perUser = id[0].Equals("HKU", StringComparison.OrdinalIgnoreCase);
        if (id[2].Length == 0 || id[2].Contains('\\') || perUser && !Regex.IsMatch(id[1], @"^S-1-5-21-[\d-]+$")
            || !perUser && id[1] is not ("64" or "32"))
            return new(JobStatus.Failed, -1, $"Ugyldigt afinstallations-id: {string.Join('|', id)}");
        var runAs = RunAs.System;
        if (perUser)
        {
            // installeret pr. bruger: afinstalleres i brugerens session, så det skal være den bruger, der er logget ind
            if (!string.Equals(ConsoleSid(), id[1], StringComparison.OrdinalIgnoreCase))
                return new(JobStatus.WaitingUser, null, "Brugeren skal være logget ind.");
            runAs = RunAs.User;
        }

        string dn, kind; string? pub, quiet, uninst, dir; bool msi;
        using (var k = OpenEntry(id))
        {
            if (k is null) return new(JobStatus.Success, 0, $"{(p.Name is { Length: > 0 } n ? n : "Programmet")} er allerede afinstalleret.");
            dn = k.GetValue("DisplayName") as string is { Length: > 0 } d ? d : p.Name ?? id[2];
            pub = k.GetValue("Publisher") as string;
            quiet = k.GetValue("QuietUninstallString") as string;
            uninst = k.GetValue("UninstallString") as string;
            msi = k.GetValue("WindowsInstaller") is int wi && wi == 1;
            kind = Headless.Kind(id[2], k);
            dir = Headless.InstallDir(k) ?? DirOf(p.Path);
        }
        if (IsProtected(dn, pub, dir)) return Refuse();

        LiveJob.Phase(0, 100, $"Afinstallerer {dn}");
        var lines = new List<string>();
        void Note(string m) { lines.Add($"[Agent] {m}"); log.LogInformation("{Msg}", m); }
        Headless.CloseRunning(dir, Note, "før afinstallationen");

        RunResult r;
        var guid = MsiGuid(id[2], uninst);
        if (!string.IsNullOrWhiteSpace(quiet))
        {
            Note($"Kører den lydløse afinstallation: {quiet}");
            r = await RunCommand(quiet, runAs, ct);
        }
        else if ((msi || IsGuid(id[2])) && guid is not null)
        {
            Note($"Windows Installer: msiexec /x {guid} /qn /norestart");
            r = await runner.Run("msiexec.exe", $"/x {guid} /qn /norestart", runAs, RemoveTimeout, null, ct, watch: true);
        }
        else
        {
            Note($"Prøver winget uninstall --name \"{dn}\"");
            r = await winget.Run($"uninstall --name \"{dn.Replace("\"", "")}\" --exact --silent {Winget.Common}", runAs, RemoveTimeout, ct);
            if (!r.Cancelled && !r.NoUser && EntryExists(id))
            {
                lines.Add(r.Output);
                if (string.IsNullOrWhiteSpace(uninst))
                    return new(JobStatus.Failed, r.ExitCode, Join(lines, "Ved ikke hvordan programmet afinstalleres (winget kender det ikke, og der er ingen afinstallationskommando)."));
                var cmd = SilentCommand(uninst, kind);
                Note(r.ExitCode == WingetNoPackage || r.ExitCode == -2
                    ? $"winget kender ikke programmet – bruger programmets eget afinstallationsprogram ({kind}): {cmd}"
                    : $"winget fik det ikke afinstalleret ({Code(r.ExitCode)}) – bruger programmets eget afinstallationsprogram ({kind}): {cmd}");
                r = await RunCommand(cmd, runAs, ct);
            }
        }
        return await Finish(id, dn, r, lines, ct);
    }

    private async Task<JobOutcome> Finish(string[] id, string dn, RunResult r, List<string> lines, CancellationToken ct)
    {
        lines.Add(r.Output);
        if (r.NoUser) return new(JobStatus.WaitingUser, null, "Brugeren skal være logget ind.");
        if (r.Cancelled) return new(JobStatus.Cancelled, r.ExitCode, Join(lines, "Annulleret fra Fleetdeck."));
        if (r.Stopped is not null) lines.Add($"[Agent] {r.Stopped}");
        var ok = r.ExitCode is int c && (RemoveOk.Contains(c) || WingetRemoveOk.Contains(c));
        // Nogle afinstallationsprogrammer (NSIS, Inno) kopierer sig selv til TEMP og afslutter straks: vent lidt på, at posten forsvinder
        var gone = await WaitGone(id, ok ? TimeSpan.FromSeconds(90) : TimeSpan.FromSeconds(4), ct);
        if (gone)
            return new(JobStatus.Success, r.ExitCode, Join(lines, r.ExitCode == 3010
                ? $"{dn} er afinstalleret. Pc'en skal genstartes for at gøre afinstallationen færdig."
                : $"{dn} er afinstalleret."));
        var why = r.Stopped is not null ? "Afinstallationen blev stoppet af vagten"
            : r.TimedOut ? $"Tidsgrænsen på {RemoveTimeout.TotalMinutes:0} min blev nået"
            : ok ? $"Afinstallationen sluttede uden fejl ({Code(r.ExitCode)}), men programmet er stadig installeret"
            : $"Afinstallationen fejlede ({Code(r.ExitCode)})";
        var last = LastLines(r.Output, 5);
        return new(JobStatus.Failed, r.ExitCode, Join(lines, last.Length > 0 ? $"{why}:\n{last}" : $"{why}."));
    }

    private static string Join(List<string> lines, string final) =>
        string.Join('\n', lines.Where(l => !string.IsNullOrWhiteSpace(l)).Append(final));

    private static string Code(int? c) => c is null ? "ingen afslutningskode" : c < 0 || c > 0xFFFF ? $"kode 0x{c:X8}" : $"kode {c}";

    private static string LastLines(string output, int n) => string.Join('\n', output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(l => l.Length > 1 && !l.All(ch => ch is '-' or '\\' or '|' or '/' or ' ' or '█' or '▒')).TakeLast(n));

    private static RegistryKey? OpenEntry(string[] id)
    {
        try
        {
            if (id[0].Equals("HKU", StringComparison.OrdinalIgnoreCase))
                return Registry.Users.OpenSubKey($@"{id[1]}\{UninstallRoot}\{id[2]}");
            using var hk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, id[1] == "32" ? RegistryView.Registry32 : RegistryView.Registry64);
            return hk.OpenSubKey($@"{UninstallRoot}\{id[2]}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException or ArgumentException) { return null; }
    }

    private static bool EntryExists(string[] id) { using var k = OpenEntry(id); return k is not null; }

    private static async Task<bool> WaitGone(string[] id, TimeSpan max, CancellationToken ct)
    {
        var until = DateTime.UtcNow + max;
        while (true)
        {
            if (!EntryExists(id)) return true;
            if (DateTime.UtcNow >= until || ct.IsCancellationRequested) return false;
            try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { return !EntryExists(id); }
        }
    }

    /// <summary>SID for brugeren, der er logget ind på konsollen, eller null.</summary>
    private static string? ConsoleSid()
    {
        if (Native.ConsoleUser() is not { } u) return null;
        try { return ((SecurityIdentifier)new NTAccount(u).Translate(typeof(SecurityIdentifier))).Value; }
        catch (SystemException) { return null; }
    }

    private static readonly Regex GuidRx = new(@"\{[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}\}", RegexOptions.IgnoreCase);
    private static bool IsGuid(string keyName) => GuidRx.IsMatch(keyName) && GuidRx.Match(keyName).Length == keyName.Length;
    private static string? MsiGuid(string keyName, string? uninstall) =>
        IsGuid(keyName) ? keyName : uninstall is not null && Regex.IsMatch(uninstall, "msiexec", RegexOptions.IgnoreCase) && GuidRx.Match(uninstall) is { Success: true } m ? m.Value : null;

    /// <summary>Afinstallationskommandoen med lydløs-flag efter installationstype (Inno, NSIS, MSI); andre køres som de er.</summary>
    public static string SilentCommand(string cmd, string kind)
    {
        cmd = cmd.Trim();
        if (Regex.IsMatch(cmd, @"msiexec", RegexOptions.IgnoreCase))
        {
            cmd = Regex.Replace(cmd, @"(^|\s)/I(?=\s*\{)", "$1/X", RegexOptions.IgnoreCase);
            return Regex.IsMatch(cmd, @"(^|\s)/q", RegexOptions.IgnoreCase) ? cmd : cmd + " /qn /norestart";
        }
        string Add(string c, string flag) => Regex.IsMatch(c, $@"(^|\s){Regex.Escape(flag)}(\s|$)", RegexOptions.IgnoreCase) ? c : c + " " + flag;
        return kind switch
        {
            "inno" => Add(Add(Add(cmd, "/VERYSILENT"), "/SUPPRESSMSGBOXES"), "/NORESTART"),
            "nullsoft" => Regex.IsMatch(cmd, @"(^|\s)/S(\s|$)") ? cmd : cmd + " /S",
            _ => cmd,
        };
    }

    /// <summary>Deler en kommandolinje i programfil og argumenter – også en sti med mellemrum uden anførselstegn.</summary>
    public static (string Exe, string Args) SplitCommand(string cmd)
    {
        cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
        if (cmd.StartsWith('"'))
        {
            var end = cmd.IndexOf('"', 1);
            if (end > 0) return (cmd[1..end], cmd[(end + 1)..].Trim());
        }
        Match? first = null;
        foreach (Match m in Regex.Matches(cmd, @"\.exe(?=\s|$)", RegexOptions.IgnoreCase))
        {
            first ??= m;
            var exe = cmd[..(m.Index + 4)];
            if (File.Exists(exe)) return (exe, cmd[(m.Index + 4)..].Trim());
        }
        if (first is not null) return (cmd[..(first.Index + 4)], cmd[(first.Index + 4)..].Trim());
        var sp = cmd.IndexOf(' ');
        return sp < 0 ? (cmd, "") : (cmd[..sp], cmd[(sp + 1)..].Trim());
    }

    private Task<RunResult> RunCommand(string cmd, string runAs, CancellationToken ct)
    {
        var (exe, args) = SplitCommand(cmd);
        var dir = Path.IsPathRooted(exe) && DirOf(exe) is { } d && Directory.Exists(d) ? d : null;
        return runner.Run(exe, args, runAs, RemoveTimeout, dir, ct, watch: true);
    }

    // ------------------------------------------------------------------ Store, Steam og uden id

    private async Task<JobOutcome> RemoveStore(string package, CancellationToken ct)
    {
        var q = Runner.Quote(package);
        var ps = $$"""
            $p = @(Get-AppxPackage -AllUsers | Where-Object { $_.PackageFullName -eq {{q}} -or $_.Name -eq {{q}} })
            if ($p.Count -eq 0) { 'Er allerede afinstalleret.'; exit 0 }
            try { $p | Remove-AppxPackage -AllUsers -ErrorAction Stop } catch { $_.Exception.Message; exit 1 }
            # ikke installér den igen for nye brugere
            Get-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -eq $p[0].Name } |
                Remove-AppxProvisionedPackage -Online -AllUsers -ErrorAction SilentlyContinue | Out-Null
            "$($p[0].Name) er afinstalleret."
            """;
        LiveJob.Phase(0, 100, "Afinstallerer Store-appen");
        return JobOutcome.From(await runner.PowerShell(ps, RunAs.System, TimeSpan.FromMinutes(15), ct), [0]);
    }

    private async Task<JobOutcome> RemoveSteam(string appId, string name, CancellationToken ct)
    {
        if (!Regex.IsMatch(appId, @"^\d+$")) return new(JobStatus.Failed, -1, $"Ugyldigt Steam-id: {appId}");
        var r = await runner.Run("cmd.exe", $"/c start \"\" \"steam://uninstall/{appId}\"", RunAs.User, TimeSpan.FromMinutes(1), null, ct);
        if (r.NoUser) return new(JobStatus.WaitingUser, null, "Brugeren skal være logget ind.");
        var o = JobOutcome.From(r, [0]);
        return o.Status == JobStatus.Success ? o with { Output = $"Steam afinstallerer spillet{(name.Length > 0 ? $" ({name})" : "")} – Steam beder brugeren bekræfte." } : o;
    }

    /// <summary>Intet afinstallations-id: prøv winget ud fra navnet.</summary>
    private async Task<JobOutcome> RemoveByName(string name, string? exe, CancellationToken ct)
    {
        if (name.Length == 0) return new(JobStatus.Failed, -1, "Ved ikke hvordan programmet afinstalleres.");
        var lines = new List<string>();
        Headless.CloseRunning(DirOf(exe), m => lines.Add($"[Agent] {m}"), "før afinstallationen");
        LiveJob.Phase(0, 100, $"Afinstallerer {name}");
        var r = await winget.Run($"uninstall --name \"{name.Replace("\"", "")}\" --exact --silent {Winget.Common}", RunAs.System, RemoveTimeout, ct);
        lines.Add(r.Output);
        if ((r.ExitCode == WingetNoPackage || r.ExitCode == -2) && !r.Cancelled)
            return new(JobStatus.Failed, r.ExitCode, Join(lines, "Ved ikke hvordan programmet afinstalleres."));
        var o = JobOutcome.From(r, WingetRemoveOk);
        return o with { Output = Join(lines, o.Status == JobStatus.Success
            ? r.ExitCode == 3010 ? $"{name} er afinstalleret. Pc'en skal genstartes for at gøre afinstallationen færdig." : $"{name} er afinstalleret."
            : r.Stopped ?? $"Afinstallationen fejlede ({Code(r.ExitCode)}){(Winget.LastLine(r.Output) is { Length: > 0 } ll ? $": {ll}" : ".")}") };
    }
}
