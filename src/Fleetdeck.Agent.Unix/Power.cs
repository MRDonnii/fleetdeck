using System.Diagnostics;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Unix;

/// <summary>
/// Planlagt genstart/slukning: planlægning, annullering og nedtælling på skærmen.
/// De sidste 5 minutter vises et vindue med nedtælling og "Udsæt 1 time" (Linux med skrivebord og Mac).
/// </summary>
public static class Power
{
    private const string PidFile = "/var/run/fleetdeck-power.pid";

    /// <summary>systemd's shutdown kan planlægge og annullere selv; ellers (Unraid, OpenRC, runit, SysV, busybox) venter en lille proces med pidfil.</summary>
    private static bool OwnTimer => Unraid.Is || (!Mac.Is && !Init.IsSystemd);
    private static string PowerCmd(bool reboot) => Unraid.Is ? Unraid.PowerCmd(reboot) : reboot ? "reboot" : "poweroff";
    private static readonly TimeSpan WindowBefore = TimeSpan.FromMinutes(5);
    private static CancellationTokenSource? _countdown;

    private static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";
    private static string Verb(string mode) => mode == JobTypes.Reboot ? "genstarter" : "slukker";

    /// <summary>Planlæg om <paramref name="minutes"/> minutter. Afløser en tidligere plan.</summary>
    public static ShellResult Schedule(string mode, int minutes, string msg)
    {
        Stop();
        ShellResult r;
        if (OwnTimer)
        {
            // Unraid: powerdown stopper arrayet pænt. pid'en gemmes, så planen kan annulleres
            Shell.Run($"wall {Q("Fleetdeck: " + msg)} 2>/dev/null; true", TimeSpan.FromSeconds(10));
            r = Shell.Run($"nohup /bin/sh -c 'echo $$ > {PidFile}; sleep {minutes * 60}; rm -f {PidFile}; {PowerCmd(mode == JobTypes.Reboot)}' >/dev/null 2>&1 &");
        }
        else
            r = Shell.Run($"shutdown {(mode == JobTypes.Reboot ? "-r" : "-h")} +{minutes} {Q(msg)}");
        if (r.ExitCode == 0)
        {
            var at = DateTime.UtcNow.AddMinutes(minutes);
            PlannedPower.Set(mode, at);
            StartCountdown(mode, at, msg);
        }
        return r;
    }

    /// <summary>Annullér planen; med <paramref name="postponeMinutes"/> planlægges den igen så meget senere.</summary>
    public static ShellResult Cancel(int? postponeMinutes = null)
    {
        var mode = PlannedPower.Current?.Mode ?? Scheduled()?.Mode;
        Stop();
        var r = OwnTimer
            ? Shell.Run($"[ -f {PidFile} ] && {{ p=$(cat {PidFile}); pkill -P \"$p\"; kill \"$p\"; rm -f {PidFile}; }}; true")
            : Mac.Is ? Shell.Run("killall shutdown 2>/dev/null; true") : Shell.Run("shutdown -c");
        PlannedPower.Clear();
        if (postponeMinutes is int m && m > 0 && mode is not null)
            return Schedule(mode, m, $"Fleetdeck {Verb(mode)} maskinen (udsat {m} min.)");
        return r;
    }

    /// <summary>Kaldes før "Genstart nu"/"Sluk nu" udføres, så Fleetdeck kan skrive det i jobbet (sættes af agenten).</summary>
    public static Func<string, Task>? Report { get; set; }

    /// <summary>
    /// "Genstart nu"/"Sluk nu" fra skærmen eller menulinjen: samme handling som planen, bare nu. Kun den handling,
    /// Fleetdeck har planlagt på denne maskine – ellers sker der intet (null).
    /// </summary>
    public static async Task<string?> Now(string? asked = null)
    {
        if (PlannedPower.NowAllowed(Current(), asked) is not { } mode) return null;
        Console.WriteLine($"Brugeren valgte {PlannedPower.NowLabel(mode)}.");
        if (Report is { } rep) await rep(mode);
        Cancel();
        var reboot = mode == JobTypes.Reboot;
        Shell.Run(OwnTimer ? $"nohup /bin/sh -c 'sleep 2; {PowerCmd(reboot)}' >/dev/null 2>&1 &" : $"shutdown {(reboot ? "-r" : "-h")} now");
        return mode;
    }

    /// <summary>Gældende plan til check-in: på Linux læses systemd's egen plan, så også "shutdown -c" i en terminal ses.</summary>
    public static (string Mode, DateTime At)? Current()
    {
        if (!Mac.Is && !OwnTimer)
        {
            var s = Scheduled();
            if (s is null) PlannedPower.Clear();
            return s;
        }
        if (OwnTimer && !File.Exists(PidFile)) PlannedPower.Clear();
        return PlannedPower.Current;
    }

    private static (string Mode, DateTime At)? Scheduled()
    {
        try
        {
            const string f = "/run/systemd/shutdown/scheduled";
            if (!File.Exists(f)) return null;
            var kv = File.ReadAllLines(f).Select(l => l.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
            if (!kv.TryGetValue("USEC", out var u) || !long.TryParse(u, out var usec)) return null;
            var mode = kv.GetValueOrDefault("MODE") is "reboot" or "kexec" ? JobTypes.Reboot : JobTypes.Shutdown;
            return (mode, DateTime.UnixEpoch.AddTicks(usec * 10));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static void Stop()
    {
        _countdown?.Cancel();
        _countdown = null;
    }

    /// <summary>Venter til 5 minutter før, og viser så nedtællingen for den, der sidder ved skærmen.</summary>
    private static void StartCountdown(string mode, DateTime at, string msg)
    {
        if (Unraid.Is) return;
        var cts = new CancellationTokenSource();
        _countdown = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                var wait = at - WindowBefore - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, cts.Token);
                var choice = Mac.Is ? MacDialog(mode, at, msg, cts.Token) : LinuxWindow(mode, at, msg, cts.Token);
                if (choice == Choice.Now && !cts.IsCancellationRequested && DateTime.UtcNow < at.AddMinutes(1))
                    await Now(mode);
                else if (choice == Choice.Postpone && !cts.IsCancellationRequested && DateTime.UtcNow < at)
                {
                    Cancel(PlannedPower.PostponeMinutes);
                    Console.WriteLine($"Brugeren udsatte {(mode == JobTypes.Reboot ? "genstarten" : "slukningen")} {PlannedPower.PostponeMinutes} min.");
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Console.WriteLine($"Nedtælling: {e.Message}"); }
        });
    }

    private enum Choice { None, Postpone, Now }

    /// <summary>Kører et skript som brugeren på skrivebordet (zenity, notify-send).</summary>
    private static Process StartAsUser(UserSession s, string script)
    {
        var psi = new ProcessStartInfo("/usr/sbin/runuser") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "-u", s.User, "--", "env", $"HOME={s.Home}", $"XDG_RUNTIME_DIR=/run/user/{s.Uid}",
                     $"DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/{s.Uid}/bus", $"DISPLAY={s.Display}", $"XAUTHORITY={s.XAuthority}",
                     "/bin/bash", "-c", script })
            psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        p.StandardInput.Close();
        return p;
    }

    /// <summary>
    /// zenity-vindue med nedtælling (Udsæt), og – hvor skrivebordet kan vise handlinger i notifikationer – en notifikation
    /// med "Genstart nu"/"Sluk nu" og "Udsæt".
    /// </summary>
    private static Choice LinuxWindow(string mode, DateTime at, string msg, CancellationToken ct)
    {
        var s = UserSession.Find();
        if (s is null) return Choice.None;
        var head = mode == JobTypes.Reboot ? "Computeren genstarter" : "Computeren slukker";
        var now = PlannedPower.NowLabel(mode);
        var later = $"Udsæt {PlannedPower.PostponeMinutes} min.";
        using var done = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<Choice>? notify = null;
        if (Shell.Has("notify-send"))
            notify = Task.Run(() =>
            {
                // libnotify 0.7.10+: --action og --wait; ældre skriveborde får en almindelig besked uden knapper
                var script = $$"""
                    if notify-send --help 2>&1 | grep -q -- '--action'; then
                      notify-send --app-name=Fleetdeck --urgency=critical --wait --action=now={{Q(now)}} --action=later={{Q(later)}} {{Q(head + $" kl. {at.ToLocalTime():HH:mm}")}} {{Q(msg)}}
                    else
                      notify-send --app-name=Fleetdeck --urgency=critical {{Q(head + $" kl. {at.ToLocalTime():HH:mm}")}} {{Q(msg)}}
                    fi
                    """;
                using var p = StartAsUser(s, script);
                using var reg = done.Token.Register(() => { try { p.Kill(true); } catch (InvalidOperationException) { } });
                var o = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit();
                return o == "now" ? Choice.Now : o == "later" ? Choice.Postpone : Choice.None;
            });
        if (!Shell.Has("zenity"))
        {
            var c = notify?.GetAwaiter().GetResult() ?? Choice.None;
            return ct.IsCancellationRequested ? Choice.None : c;
        }
        var end = new DateTimeOffset(at).ToUnixTimeSeconds();
        var total = Math.Max(1, (long)(at - DateTime.UtcNow).TotalSeconds);
        var script = $$"""
            end={{end}}; total={{total}}
            ( while :; do left=$((end-$(date +%s))); [ $left -le 0 ] && { echo 100; break; }
                echo $(( (total-left)*100/total )); printf '# %s om %d:%02d. Gem dit arbejde.\n' {{Q(head)}} $((left/60)) $((left%60)); sleep 1; done ) |
            zenity --progress --title=Fleetdeck --width=460 --percentage=0 --auto-close --cancel-label={{Q($"Udsæt {PlannedPower.PostponeMinutes} min.")}} --text={{Q(msg)}} 2>/dev/null
            exit ${PIPESTATUS[1]}
            """;
        using var p = StartAsUser(s, script);
        using var reg = done.Token.Register(() => { try { p.Kill(true); } catch (InvalidOperationException) { } });
        // valgt i notifikationen: luk nedtællingsvinduet
        notify?.ContinueWith(t => { if (t.Result != Choice.None) try { p.Kill(true); } catch (InvalidOperationException) { } }, TaskScheduler.Default);
        p.WaitForExit();
        var fromNotify = notify is { IsCompletedSuccessfully: true } ? notify.Result : Choice.None;
        var zenity = p.ExitCode == 1 ? Choice.Postpone : Choice.None;
        done.Cancel();   // luk notifikationen, hvis den stadig står der
        if (ct.IsCancellationRequested) return Choice.None;
        return fromNotify != Choice.None ? fromNotify : zenity;
    }

    /// <summary>macOS: dialog med Udsæt, Genstart nu/Sluk nu og OK, der lukker sig selv, når tiden er gået.</summary>
    private static Choice MacDialog(string mode, DateTime at, string msg, CancellationToken ct)
    {
        var s = Mac.Session();
        if (s is null) return Choice.None;
        var now = PlannedPower.NowLabel(mode);
        var secs = Math.Max(5, (int)(at - DateTime.UtcNow).TotalSeconds);
        var text = $"{(mode == JobTypes.Reboot ? "Mac'en genstarter" : "Mac'en slukker")} kl. {at.ToLocalTime():HH:mm}. Gem dit arbejde. {msg}".Replace("\"", "'");
        var later = $"Udsæt {PlannedPower.PostponeMinutes} min.";
        var r = Shell.Run($"launchctl asuser {s.Uid} sudo -u {Q(s.User)} osascript -e {Q($"button returned of (display dialog \"{text}\" with title \"Fleetdeck\" buttons {{\"{later}\", \"{now}\", \"OK\"}} default button 3 giving up after {secs})")}",
            TimeSpan.FromSeconds(secs + 30));
        if (ct.IsCancellationRequested) return Choice.None;
        var b = r.Output.Trim();
        return b == later ? Choice.Postpone : b == now ? Choice.Now : Choice.None;
    }
}
