using Fleetdeck.Shared;
using System.Text.Json;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace Fleetdeck.Agent.Windows;

/// <summary>Installation på Windows: mapper med rettigheder, programfil, tjeneste – og flytningen fra WinStyring.</summary>
public static class Installer
{
    public const string ServiceName = "Fleetdeck";
    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Fleetdeck");
    public static string Exe => Path.Combine(InstallDir, "Fleetdeck.exe");

    // WinStyring (forgængeren)
    private const string OldService = "WinStyring";
    private static string OldInstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinStyring");
    private static string OldDataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WinStyring");

    public static int Sc(string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo("sc.exe", arguments) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }

    /// <summary>Kopiér denne programfil til Program Files\Fleetdeck (hvis den ikke allerede kører derfra).</summary>
    public static void CopySelf()
    {
        Directory.CreateDirectory(InstallDir);
        if (!string.Equals(Environment.ProcessPath, Exe, StringComparison.OrdinalIgnoreCase)) File.Copy(Environment.ProcessPath!, Exe, true);
    }

    /// <summary>
    /// Rettigheder på programmappen og datamappen: kun SYSTEM og administratorer kan ændre noget, agenten kører.
    /// userwork må brugere kun læse; i userwork\cmd og userwork\out kan de kun oprette filer. Se <see cref="Hardening"/>.
    /// </summary>
    public static void PrepareDirs(AgentOptions opt) => Hardening.Apply(opt, Console.WriteLine, InstallDir);

    /// <summary>Opret (eller genopret) Windows-tjenesten og start den.</summary>
    /// <summary>
    /// Efter installationen: sørg for, at tjenesten kører (start den igen, hvis den stopper), og vent på første rigtige
    /// check-in hos serveren (status.json skrives af tjenesten). 0 = forbundet, 6 = tjenesten vil ikke starte, 7 = ingen forbindelse endnu.
    /// </summary>
    public static async Task<(int Code, string Message)> VerifyFirstContact(AgentOptions opt, DateTime since, TimeSpan timeout)
    {
        var file = Path.Combine(opt.UserWork, "status.json");
        var end = DateTime.UtcNow + timeout;
        var starts = 0; string? last = null;
        while (DateTime.UtcNow < end)
        {
            try
            {
                using var sc = new System.ServiceProcess.ServiceController(ServiceName);
                if (sc.Status == System.ServiceProcess.ServiceControllerStatus.Stopped)
                {
                    if (++starts > 3)
                        return (6, "Tjenesten \"Fleetdeck Agent\" vil ikke starte" + (last is null ? "." : $": {last}") +
                            (SmartAppControl.Active ? " Smart App Control blokerer den sandsynligvis – slå den fra, og prøv igen." : " Tjek antivirus, og prøv igen."));
                    try { sc.Start(); } catch (InvalidOperationException e) { last = e.InnerException?.Message ?? e.Message; }
                }
            }
            catch (InvalidOperationException e) { return (6, $"Tjenesten findes ikke: {e.Message}"); }
            try
            {
                if (File.Exists(file) && JsonSerializer.Deserialize<TrayStatus>(await File.ReadAllTextAsync(file), TrayBridge.Json) is { } s)
                {
                    if (s.Connected && s.LastContact is { } lc && lc.ToUniversalTime() >= since.AddSeconds(-5))
                        return (0, $"Forbindelsen er bekræftet: {(s.Machine.Length > 0 ? s.Machine : Environment.MachineName)} er i Fleetdeck" + (s.Profile is { Length: > 0 } p ? $" med profilen {p}." : " (uden profil)."));
                    if (s.Error is { Length: > 0 } er) last = er;
                }
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
            await Task.Delay(2000);
        }
        return (7, "Fleetdeck er installeret, men har ikke fået forbindelse endnu" + (last is null ? "." : $" ({last}).") + " Tjenesten bliver ved med at prøve af sig selv.");
    }

    public static bool CreateService()
    {
        if (Sc($"query {ServiceName}") == 0) { Sc($"stop {ServiceName}"); Thread.Sleep(1500); Sc($"delete {ServiceName}"); Thread.Sleep(1000); }
        if (Sc($"create {ServiceName} binPath= \"\\\"{Exe}\\\"\" start= delayed-auto DisplayName= \"Fleetdeck Agent\"") != 0) return false;
        Sc($"description {ServiceName} \"Fleetdeck: programmer, opdateringer og helbred på denne pc\"");
        Sc($"failure {ServiceName} reset= 86400 actions= restart/5000/restart/10000/restart/30000");
        Sc($"failureflag {ServiceName} 1");
        Autostart.Ensure();   // vagthund: starter tjenesten ved opstart og hvert 5. minut, hvis den står stille
        Sc($"start {ServiceName}");
        return true;
    }

    // ------------------------------------------------------------------ WinStyring → Fleetdeck

    /// <summary>Kører vi fra en gammel WinStyring-installation (efter at den hentede Fleetdeck som "opdatering")?</summary>
    public static bool RunningAsLegacy => (Environment.ProcessPath ?? "").Contains(@"\WinStyring\", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Flyt til Fleetdeck: programfil, konfiguration (tokenet er krypteret til maskinen og kan genbruges), ny tjeneste – og fjern
    /// derefter den gamle WinStyring-tjeneste og dens mapper. Kaldes af den gamle tjeneste, som bagefter stoppes.
    /// </summary>
    public static bool MigrateFromLegacy(AgentOptions opt, Action<string> log)
    {
        try
        {
            // allerede flyttet (den gamle tjeneste blev genstartet, før den nåede at blive slettet): bare ryd op
            if (Sc($"query {ServiceName}") == 0 && File.Exists(opt.ConfigFile)) { RemoveLegacy(delaySeconds: 1); return true; }
            log("Gammel WinStyring-installation fundet – flytter til Fleetdeck");
            CopySelf();
            PrepareDirs(opt);
            foreach (var f in new[] { "config.json", "upgraded.json" })
            {
                var src = Path.Combine(OldDataDir, f); var dst = Path.Combine(opt.Dir, f);
                if (File.Exists(src) && !File.Exists(dst)) File.Copy(src, dst);
            }
            if (!CreateService()) { log("Kunne ikke oprette Fleetdeck-tjenesten"); return false; }
            RemoveLegacy(delaySeconds: 2);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log($"Flytningen fejlede: {e.Message}"); return false; }
    }

    /// <summary>Fjern WinStyring-tjenesten, dens Run-nøgle og mapper (efter en pause, så en kørende proces kan nå at stoppe).</summary>
    public static void RemoveLegacy(int delaySeconds = 0)
    {
        if (Sc($"query {OldService}") != 0 && !Directory.Exists(OldInstallDir) && !Directory.Exists(OldDataDir)) return;
        try { using var run = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true); run?.DeleteValue(OldService, false); }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException) { }
        var cmd = $"/c timeout /t {delaySeconds} /nobreak >nul & sc stop {OldService} >nul & timeout /t 3 /nobreak >nul & sc delete {OldService} >nul" +
                  $" & taskkill /f /im WinStyring.Agent.exe >nul 2>&1 & rmdir /s /q \"{OldInstallDir}\" & rmdir /s /q \"{OldDataDir}\"";
        Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false });
    }
}
