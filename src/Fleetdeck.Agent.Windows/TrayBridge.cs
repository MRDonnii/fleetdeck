using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Windows;

/// <summary>
/// Tjenesten (SYSTEM) ↔ ikonet i proceslinjen (brugeren). Status skrives til userwork\status.json, som alle kan læse.
/// Ordrer kommer som filer: cmd\ (kun administratorer og SYSTEM – pause, udvidelser) og userwork\cmd\ (alle – kun
/// "opdatér nu" og "synkronisér"). Ikonet starter en administrator-ordre med UAC, så børn ikke kan sætte styringen på pause.
/// </summary>
public sealed class TrayBridge(AgentOptions opt, ILogger<TrayBridge> log)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    public string StatusFile => Path.Combine(opt.UserWork, "status.json");
    public string AdminCmdDir => Path.Combine(opt.Dir, "cmd");
    public string UserCmdDir => Path.Combine(opt.UserWork, "cmd");
    public static string ExePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Fleetdeck", "Fleetdeck.exe");

    public TrayStatus Status { get; } = new();
    private readonly Dictionary<uint, DateTime> _tried = [];
    private bool _shortcut;

    /// <summary>Genvej i Start-menuen, der åbner statusvinduet (og dermed ikonet igen, hvis det er lukket).</summary>
    public static string ShortcutPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Fleetdeck.lnk");

    public void Write()
    {
        try
        {
            Status.Written = DateTime.UtcNow;
            Directory.CreateDirectory(opt.UserWork);
            var tmp = StatusFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Status, Json));
            File.Move(tmp, StatusFile, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.LogDebug("status.json: {Msg}", e.Message); }
    }

    /// <summary>Henter ordrer fra begge mapper (og sletter dem). Brugermappen må kun bede om "opdatér nu" og "synkronisér".</summary>
    public List<TrayCommand> TakeCommands()
    {
        var list = new List<TrayCommand>();
        foreach (var (dir, admin) in new[] { (AdminCmdDir, true), (UserCmdDir, false) })
        {
            try
            {
                Directory.CreateDirectory(dir);
                // ikonet skriver ordren som .tmp og omdøber den til .json, når den er færdig (en halvt skrevet ordre gik før tabt)
                foreach (var f in Directory.GetFiles(dir, "*.json"))
                {
                    try
                    {
                        if (File.GetAttributes(f).HasFlag(FileAttributes.ReparsePoint)) continue;   // aldrig gennem en genvej (slettes i finally)
                        var c = JsonSerializer.Deserialize<TrayCommand>(File.ReadAllText(f), Json);
                        if (c is not null && (admin || c.Action is "upgrade_all" or "sync" or "power_postpone" or "power_now" or "helpdesk_reply" or "helpdesk_read" or "helpdesk_logs")) list.Add(c);
                        else if (c is not null) log.LogWarning("Ordre '{Action}' fra brugermappen afvist (kræver administrator)", c.Action);
                    }
                    catch (JsonException) { }
                    finally { try { File.Delete(f); } catch (IOException) { } }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.LogDebug("cmd: {Msg}", e.Message); }
        }
        return list;
    }

    private void EnsureShortcut()
    {
        if (_shortcut) return;
        _shortcut = true;
        if (File.Exists(ShortcutPath)) return;
        var ps = $"$s=(New-Object -ComObject WScript.Shell).CreateShortcut('{ShortcutPath}'); $s.TargetPath='{ExePath}'; $s.Arguments='status'; " +
                 $"$s.IconLocation='{ExePath},0'; $s.Description='Fleetdeck – status for denne pc'; $s.Save()";
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powershell.exe") { ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", ps }, CreateNoWindow = true, UseShellExecute = false });
            p?.WaitForExit(30000);
            log.LogInformation("Genvej i Start-menuen oprettet");
        }
        catch (System.ComponentModel.Win32Exception e) { log.LogDebug("Genvej: {Msg}", e.Message); }
    }

    /// <summary>Ikonet starter ved login for alle brugere (kan slås fra pr. bruger), og startes nu for den, der er logget ind.</summary>
    public void EnsureTray()
    {
        if (!AgentOptions.IsSystem || !File.Exists(ExePath)) return;
        try
        {
            using var run = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true);
            var want = $"\"{ExePath}\" tray";
            if (run is not null && run.GetValue("Fleetdeck") as string != want) run.SetValue("Fleetdeck", want);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) { log.LogDebug("Run-nøgle: {Msg}", e.Message); }
        // brugermapperne til ordrer og output: brugere må kun oprette filer dér (ingen mapper, ingen genveje)
        Hardening.EnsureDropboxes(opt, m => log.LogDebug("{Msg}", m));
        EnsureShortcut();
        // ikonet startes igen, hvis det er lukket (højst hvert 2. minut); har brugeren slået det fra, lukker det straks selv
        var session = Native.WTSGetActiveConsoleSessionId();
        if (session == 0xFFFFFFFF || Native.ConsoleUser() is null) return;
        if (_tried.TryGetValue(session, out var last) && DateTime.UtcNow - last < TimeSpan.FromMinutes(2)) return;
        var running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExePath)).Any(p => { try { return p.SessionId == session; } catch { return false; } finally { p.Dispose(); } });
        if (running) return;
        _tried[session] = DateTime.UtcNow;
        try { Native.RunAsConsoleUser($"\"{ExePath}\" tray", false, TimeSpan.Zero, Path.GetDirectoryName(ExePath), wait: false); log.LogInformation("Ikonet i proceslinjen er startet for brugeren"); }
        catch (System.ComponentModel.Win32Exception e) { log.LogDebug("Ikon: {Msg}", e.Message); }
    }
}
