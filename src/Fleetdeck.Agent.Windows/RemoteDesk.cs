using System.Diagnostics;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Windows;

/// <summary>
/// RustDesk til ubemandet adgang: fast adgangskode (laves af agenten og huskes), kun adgangskode som godkendelse
/// og direkte forbindelse på det lokale net. Kører som SYSTEM, så det rammer RustDesk-tjenestens egen opsætning.
/// </summary>
public static class RemoteDesk
{
    private static bool _appliedThisRun;

    private static string? Exe =>
        new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }
            .Select(d => Path.Combine(d, "RustDesk", "rustdesk.exe")).FirstOrDefault(File.Exists);

    private static (int Code, string Output) Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(30000)) { try { p.Kill(true); } catch (InvalidOperationException) { } return (-1, "tog for lang tid"); }
        return (p.ExitCode, (outTask.Wait(5000) ? outTask.Result : "") + (errTask.Wait(1000) ? errTask.Result : ""));
    }

    public static RemoteDesktopInfo? Ensure(AgentConfig cfg, string configFile)
    {
        if (Exe is not { } exe) return null;
        var id = RemoteDesktop.ParseId(Run(exe, "--get-id").Output);
        if (id is null) return new RemoteDesktopInfo { Error = "RustDesk svarer ikke (kører tjenesten?)" };
        if (cfg.RemotePassword is not { Length: > 0 } || !_appliedThisRun)
        {
            var pw = cfg.RemotePassword is { Length: > 0 } p ? p : RemoteDesktop.NewPassword();
            var r = Run(exe, "--password", pw);
            if (r.Code != 0) return new RemoteDesktopInfo { Id = id, Error = $"Kunne ikke sætte adgangskoden: {r.Output.Trim()}" };
            Run(exe, "--option", "verification-method", "use-permanent-password");
            Run(exe, "--option", "approve-mode", "password");
            Run(exe, "--option", "direct-server", "Y");
            if (cfg.RemotePassword != pw) { cfg.RemotePassword = pw; cfg.Save(configFile); }
            _appliedThisRun = true;
        }
        return new RemoteDesktopInfo { Id = id, Password = cfg.RemotePassword };
    }
}
