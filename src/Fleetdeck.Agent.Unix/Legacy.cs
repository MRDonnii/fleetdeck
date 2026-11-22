namespace Fleetdeck.Agent.Unix;

/// <summary>
/// Overgangen fra WinStyring: når serveren skifter til Fleetdeck, henter den gamle agent Fleetdeck som "opdatering" og
/// genstarter fra den gamle sti. Så flytter agenten sig selv: konfigurationen kopieres, Fleetdeck-tjenesten installeres og
/// startes, og den gamle installation fjernes. Ingen maskine skal geninstalleres i hånden.
/// </summary>
public static class Legacy
{
    private const string OldEtc = "/etc/winstyring";
    private static string Self => Path.GetFullPath(Environment.ProcessPath!);

    /// <summary>Kører vi fra en gammel WinStyring-installation?</summary>
    public static bool Running => Self.Contains("/winstyring/", StringComparison.Ordinal);

    /// <summary>Kopiér den gamle konfiguration, hvis Fleetdeck ikke har en endnu.</summary>
    public static void CopyConfig()
    {
        if (System.IO.File.Exists(AgentConfig.File) || !System.IO.File.Exists(OldEtc + "/agent.json")) return;
        Directory.CreateDirectory(AgentConfig.Dir);
        System.IO.File.Copy(OldEtc + "/agent.json", AgentConfig.File);
        System.IO.File.SetUnixFileMode(AgentConfig.File, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>Flyt: installér som Fleetdeck (kopierer denne fil), og fjern derefter den gamle WinStyring-installation.</summary>
    public static int Migrate()
    {
        Log.Info("Gammel WinStyring-installation fundet – flytter til Fleetdeck");
        CopyConfig();
        var rc = Setup.InstallService();
        if (rc != 0) { Log.Warn($"Flytningen til Fleetdeck fejlede (kode {rc}) – fortsætter som før"); return rc; }
        RemoveOld();
        return 0;
    }

    /// <summary>Fjern den gamle installation (efter et øjeblik, så denne proces kan nå at afslutte).</summary>
    public static void RemoveOld()
    {
        string script;
        if (Mac.Is)
            script = "sleep 5; launchctl bootout system/dk.winstyring.agent 2>/dev/null; rm -f /Library/LaunchDaemons/dk.winstyring.agent.plist; rm -rf /usr/local/winstyring /etc/winstyring";
        else if (Unraid.Is)
            script = "sleep 5; sed -i '/plugins\\/winstyring\\/start.sh/d' /boot/config/go; " +
                     "for f in /var/run/winstyring-loop.pid /var/run/winstyring-agent.pid; do [ -f $f ] && kill $(cat $f) 2>/dev/null; rm -f $f; done; " +
                     "pkill -f '/usr/local/winstyring/loo[p].sh'; pkill -f '^/usr/local/winstyring/winstyring-agen[t]$'; " +
                     "rm -rf /boot/config/plugins/winstyring /usr/local/winstyring /etc/winstyring";
        else
            script = "sleep 5; systemctl disable --now winstyring-agent 2>/dev/null; rm -f /etc/systemd/system/winstyring-agent.service; " +
                     "systemctl daemon-reload; rm -rf /opt/winstyring /etc/winstyring";
        // Linux: som sin egen systemd-opgave – ellers dræbes oprydningen sammen med den gamle tjeneste, den slår fra
        if (!Mac.Is && !Unraid.Is && Shell.Has("systemd-run"))
            Shell.Run($"systemd-run --collect --on-active=3 /bin/sh -c {Pkg.Q(script)}", TimeSpan.FromSeconds(10));
        else
            Shell.Run($"nohup /bin/sh -c {Pkg.Q(script)} >/dev/null 2>&1 &", TimeSpan.FromSeconds(10));
    }
}
