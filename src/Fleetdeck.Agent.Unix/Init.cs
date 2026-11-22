namespace Fleetdeck.Agent.Unix;

/// <summary>
/// Opstartssystemet på Linux: systemd (de fleste), OpenRC (Alpine, Gentoo, Artix), runit (Void, Artix) eller SysV (Devuan, MX, antiX).
/// Bestemmer, hvordan agenten installeres som tjeneste, startes igen efter selvopdatering, fjernes og læser fejlede tjenester.
/// </summary>
public static class Init
{
    public enum Kind { Systemd, OpenRC, Runit, SysV }

    public const string Dir = "/opt/fleetdeck", Exe = Dir + "/fleetdeck-agent", Name = "fleetdeck-agent";
    private const string Unit = "/etc/systemd/system/fleetdeck-agent.service", InitD = "/etc/init.d/fleetdeck-agent",
        SvDir = "/etc/sv/fleetdeck-agent", Loop = Dir + "/loop.sh", LoopPid = "/run/fleetdeck-loop.pid";

    private static Kind? _kind;
    public static Kind Current => _kind ??= Detect();
    public static bool IsSystemd => Current == Kind.Systemd;

    private static Kind Detect()
    {
        if (Directory.Exists("/run/systemd/system")) return Kind.Systemd;
        // installerens chroot: systemd kører ikke endnu, men er systemets init
        if (Shell.Has("systemctl") && Shell.Out("readlink -f /sbin/init").Contains("systemd")) return Kind.Systemd;
        if (File.Exists("/sbin/openrc-run") || File.Exists("/usr/sbin/openrc-run") || Directory.Exists("/run/openrc")) return Kind.OpenRC;
        if (Shell.Has("sv") && (Directory.Exists("/run/runit") || Directory.Exists("/etc/runit"))) return Kind.Runit;
        return Kind.SysV;
    }

    public static string Label => Current switch { Kind.Systemd => "systemd", Kind.OpenRC => "OpenRC", Kind.Runit => "runit", _ => "SysV-init" };

    /// <summary>runit: mappen med aktive tjenester (Void: /var/service, Artix: /run/runit/service).</summary>
    private static string RunitServiceDir =>
        new[] { "/var/service", "/run/runit/service", "/etc/runit/runsvdir/default", "/service" }.FirstOrDefault(Directory.Exists) ?? "/var/service";

    /// <summary>Stop en kørende tjeneste (før agenten skiftes ud).</summary>
    public static void Stop() => Shell.Run(Current switch
    {
        Kind.Systemd => "systemctl stop fleetdeck-agent 2>/dev/null || true",
        Kind.OpenRC => "rc-service fleetdeck-agent stop 2>/dev/null || true",
        Kind.Runit => $"sv down {RunitServiceDir}/fleetdeck-agent 2>/dev/null || true",
        _ => $"[ -f {LoopPid} ] && kill $(cat {LoopPid}) 2>/dev/null; pkill -x fleetdeck-agent 2>/dev/null; true",
    }, TimeSpan.FromSeconds(30));

    /// <summary>Skriv tjenesten for opstartssystemet og start den. Alle varianter starter agenten igen, når den afslutter (selvopdatering).</summary>
    public static ShellResult Install()
    {
        switch (Current)
        {
            case Kind.Systemd:
                File.WriteAllText(Unit, """
                    [Unit]
                    Description=Fleetdeck Agent (styring af programmer, opdateringer og helbred)
                    After=network-online.target
                    Wants=network-online.target

                    [Service]
                    Type=simple
                    ExecStart=/opt/fleetdeck/fleetdeck-agent
                    Restart=always
                    RestartSec=5
                    Nice=5
                    CPUWeight=20
                    IOWeight=20

                    [Install]
                    WantedBy=multi-user.target

                    """);
                return Directory.Exists("/run/systemd/system")
                    ? Shell.Run("systemctl daemon-reload && systemctl enable --now fleetdeck-agent", TimeSpan.FromMinutes(1))
                    : Shell.Run("systemctl enable fleetdeck-agent", TimeSpan.FromMinutes(1));   // chroot: starter ved næste opstart

            case Kind.OpenRC:
                // supervise-daemon starter agenten igen, når den afslutter
                WriteExec(InitD, """
                    #!/sbin/openrc-run
                    name="Fleetdeck-agent"
                    description="Fleetdeck Agent (styring af programmer, opdateringer og helbred)"
                    command="/opt/fleetdeck/fleetdeck-agent"
                    supervisor=supervise-daemon
                    respawn_delay=5
                    respawn_max=0
                    nicelevel=5
                    output_log="/var/log/fleetdeck-agent.log"
                    error_log="/var/log/fleetdeck-agent.log"

                    depend() {
                        need net
                        after firewall
                    }

                    """);
                return Shell.Run("rc-update add fleetdeck-agent default >/dev/null && rc-service fleetdeck-agent restart", TimeSpan.FromMinutes(1));

            case Kind.Runit:
                Directory.CreateDirectory(SvDir);
                WriteExec(SvDir + "/run", "#!/bin/sh\nexec nice -n 5 /opt/fleetdeck/fleetdeck-agent 2>&1\n");
                return Shell.Run($"ln -sfn {SvDir} {RunitServiceDir}/fleetdeck-agent && sleep 6 && sv up {RunitServiceDir}/fleetdeck-agent", TimeSpan.FromMinutes(1));

            default:
                // SysV: en lille løkke starter agenten igen (som Restart=always), init-scriptet starter løkken ved opstart
                WriteExec(Loop, $$"""
                    #!/bin/sh
                    echo $$ > {{LoopPid}}
                    while :; do
                      nice -n 5 /opt/fleetdeck/fleetdeck-agent >> /var/log/fleetdeck-agent.log 2>&1
                      sleep 5
                    done

                    """);
                WriteExec(InitD, $$"""
                    #!/bin/sh
                    ### BEGIN INIT INFO
                    # Provides:          fleetdeck-agent
                    # Required-Start:    $network $remote_fs
                    # Required-Stop:     $network $remote_fs
                    # Default-Start:     2 3 4 5
                    # Default-Stop:      0 1 6
                    # Short-Description: Fleetdeck Agent
                    ### END INIT INFO
                    case "$1" in
                      start) [ -f {{LoopPid}} ] && kill -0 $(cat {{LoopPid}}) 2>/dev/null || nohup {{Loop}} >/dev/null 2>&1 & ;;
                      stop) [ -f {{LoopPid}} ] && kill $(cat {{LoopPid}}) 2>/dev/null; pkill -x fleetdeck-agent; rm -f {{LoopPid}} ;;
                      restart) "$0" stop; sleep 1; "$0" start ;;
                      status) [ -f {{LoopPid}} ] && kill -0 $(cat {{LoopPid}}) 2>/dev/null && echo kører || { echo stoppet; exit 3; } ;;
                      *) echo "Brug: $0 {start|stop|restart|status}"; exit 1 ;;
                    esac

                    """);
                return Shell.Run("(command -v update-rc.d >/dev/null && update-rc.d fleetdeck-agent defaults) || (command -v chkconfig >/dev/null && chkconfig --add fleetdeck-agent) || true; " +
                                 "/etc/init.d/fleetdeck-agent restart", TimeSpan.FromMinutes(1));
        }
    }

    /// <summary>Script, der fjerner tjenesten og agenten. Køres frakoblet, så det ikke dræbes sammen med agenten.</summary>
    public static string RemovalScript => Current switch
    {
        Kind.Systemd => $"systemctl disable --now fleetdeck-agent; rm -f {Unit}; systemctl daemon-reload",
        Kind.OpenRC => $"rc-service fleetdeck-agent stop; rc-update del fleetdeck-agent default; rm -f {InitD} /var/log/fleetdeck-agent.log",
        Kind.Runit => $"rm -f {RunitServiceDir}/fleetdeck-agent; sleep 6; rm -rf {SvDir}",
        _ => $"{InitD} stop; (command -v update-rc.d >/dev/null && update-rc.d -f fleetdeck-agent remove) || (command -v chkconfig >/dev/null && chkconfig --del fleetdeck-agent) || true; rm -f {InitD}",
    } + $"; rm -rf {Dir} /etc/fleetdeck";

    /// <summary>Start et script frakoblet agenten (om <paramref name="delay"/> sek.).</summary>
    public static void RunDetached(string script, int delay = 5)
    {
        if (IsSystemd) Shell.Run($"systemd-run --collect --on-active={delay} /bin/sh -c {Pkg.Q(script)}", TimeSpan.FromSeconds(20));
        else Shell.Run($"nohup setsid /bin/sh -c {Pkg.Q($"sleep {delay}; {script}")} >/dev/null 2>&1 &", TimeSpan.FromSeconds(20));
    }

    /// <summary>Tjenester, der er gået ned.</summary>
    public static List<string> FailedServices() => Current switch
    {
        Kind.OpenRC => Shell.Out("rc-status --crashed 2>/dev/null").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.Split(' ')[0] + " (OpenRC)").ToList(),
        Kind.Runit => Shell.Out($"for s in {RunitServiceDir}/*; do sv status \"$s\" 2>/dev/null; done | grep '^fail\\|^down' | grep -v 'normally down'")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split(':').ElementAtOrDefault(1)?.Trim().Split('/').Last() ?? l)
            .Where(n => n.Length > 0).Select(n => n + " (runit)").ToList(),
        _ => [],
    };

    private static void WriteExec(string path, string content)
    {
        File.WriteAllText(path, content);
        File.SetUnixFileMode(path, (UnixFileMode)0b111_101_101);
    }
}
