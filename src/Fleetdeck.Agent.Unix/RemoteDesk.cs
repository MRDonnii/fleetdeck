using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Unix;

/// <summary>
/// RustDesk til ubemandet adgang: fast adgangskode (laves af agenten og huskes), kun adgangskode som godkendelse
/// og direkte forbindelse på det lokale net. ID og adgangskode sendes med helbredet, så Fleetdeck kan forbinde med ét klik.
/// </summary>
public static class RemoteDesk
{
    private static bool _appliedThisRun;

    private static string? Exe =>
        Mac.Is ? (File.Exists("/Applications/RustDesk.app/Contents/MacOS/rustdesk") ? "/Applications/RustDesk.app/Contents/MacOS/rustdesk" : null)
        : Shell.Has("rustdesk") ? "rustdesk" : null;

    /// <summary>Er nogen forbundet? RustDesk starter forbindelsesvinduet ("--cm") for hver aktiv forbindelse.</summary>
    public static bool Active() => !Unraid.Is && Shell.Out("pgrep -f '[r]ustdesk --cm' >/dev/null 2>&1 && echo ja", 5) == "ja";

    public static RemoteDesktopInfo? Ensure(AgentConfig cfg)
    {
        if (Unraid.Is || Exe is not { } exe) return null;
        var id = RemoteDesktop.ParseId(Shell.Out($"{exe} --get-id 2>/dev/null", 20));
        if (id is null) return new RemoteDesktopInfo { Error = "RustDesk svarer ikke (kører tjenesten?)" };
        // sæt adgangskode og indstillinger første gang, og én gang pr. agentstart (hvis nogen har ændret dem)
        if (cfg.RemotePassword is not { Length: > 0 } || !_appliedThisRun)
        {
            var pw = cfg.RemotePassword is { Length: > 0 } p ? p : RemoteDesktop.NewPassword();
            var r = Shell.Run($"{exe} --password {Pkg.Q(pw)} && " +
                              $"{exe} --option verification-method use-permanent-password; " +
                              $"{exe} --option approve-mode password; {exe} --option direct-server Y; true", TimeSpan.FromSeconds(60));
            if (r.ExitCode != 0) return new RemoteDesktopInfo { Id = id, Error = $"Kunne ikke sætte adgangskoden: {r.Output.Trim()}" };
            if (cfg.RemotePassword != pw) { cfg.RemotePassword = pw; cfg.Save(); }
            _appliedThisRun = true;
        }
        return new RemoteDesktopInfo { Id = id, Password = cfg.RemotePassword };
    }
}
