using System.Text.RegularExpressions;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Unix;

/// <summary>
/// Opdateringsmotoren på Linux og Mac: én runde i fast rækkefølge – reparation/lås → systemets pakker (én gang) →
/// snap/flatpak (Mac: App Store og Homebrew) → firmware sidst (kun Linux, kun på netstrøm, aldrig BIOS/UEFI).
/// Kræver systemet en genstart, melder runden det, og serveren genstarter og beder om næste runde.
/// </summary>
public static partial class UnixUpdateRound
{
    public static JobResult Run(AgentJob j)
    {
        var p = j.Params;
        var round = p.UpdRound ?? 1;
        var max = UpdateRounds.Clamp(p.UpdMaxRounds ?? UpdateRounds.DefaultMax);
        var rep = new UpdateReport { Round = round, Phase = UpdatePhases.Prepare };
        var lines = new List<string>();
        var platform = Mac.Is ? Platforms.Mac : Platforms.Linux;
        void Phase(int a, int b, string phase, string text = "") =>
            LiveJob.Line(LiveJob.Current, $"::ws {a}-{b} Runde {round}/{max} · {UpdatePhases.Label(phase, platform)}{(text.Length > 0 ? ": " + text : "")}");
        var minutes = TimeSpan.FromMinutes(p.TimeoutMin ?? 180);
        var exclude = p.Exclude ?? [];

        // kontrolsøgningen efter den sidste genstart findes kun på Windows
        if (p.UpdVerify == true) { rep.RebootPending = Mac.Is ? Mac.RebootNeeded() : Pkg.RebootNeeded(fresh: true); return Finish(rep, lines, round, max); }
        if (Unraid.Is)
        {
            // Unraid: kun plugins/containere – genstartes aldrig af Fleetdeck
            if (p.UpdApps != false)
            {
                Phase(0, 100, UpdatePhases.Apps);
                Record(rep, UpdatePhases.Apps, "Unraid-plugins", Unraid.UpgradeAll(exclude, TimeSpan.FromMinutes(30)), lines);
            }
            return Finish(rep, lines, round, max);
        }
        if (Mac.Is) return MacRound(p, rep, lines, round, max, minutes, Phase);

        // 0) forudsætninger: vent på en anden pakkeopdatering og reparér en afbrudt (dpkg --configure -a)
        var pendingAtStart = Pkg.RebootNeeded(fresh: true);
        if (p.UpdSystem != false)
        {
            Phase(0, 5, UpdatePhases.Prepare, "venter på pakkelås og reparerer");
            var fix = Shell.Run(Pkg.RepairScript, TimeSpan.FromMinutes(20));
            if (fix.ExitCode != 0) lines.Add($"--  Reparation af pakkesystemet gav fejl ({fix.ExitCode}) – prøver alligevel:\n{Tail(fix.Output)}");
        }
        var reboot = pendingAtStart;

        // 1) systemets pakker – én gang (ikke både "systemopdatering" og "opdatér alt")
        if (p.UpdSystem != false)
        {
            if (pendingAtStart)
            {
                rep.Later(null, "Systempakker");
                lines.Add("--  Systemet venter allerede på en genstart – systempakkerne kommer efter genstarten.");
            }
            else
            {
                Phase(5, 70, UpdatePhases.Software);
                Pkg.Refresh(true);
                var r = Shell.Run(Pkg.SystemScript(exclude) + "exit $rc", minutes);
                Record(rep, UpdatePhases.Software, SystemTitle(r.Output), r, lines);
                reboot = r.Output.Contains(Pkg.RebootMarker, StringComparison.Ordinal) || Pkg.RebootNeeded(fresh: true);
            }
        }

        // 2) snap og flatpak (uafhængige af genstart)
        if (p.UpdApps != false && (Shell.Has("snap") || Shell.Has("flatpak")))
        {
            Phase(70, 88, UpdatePhases.Apps, "snap og flatpak");
            Record(rep, UpdatePhases.Apps, "Snap- og flatpak-programmer", Shell.Run(Pkg.ExtrasScript + "exit 0", minutes), lines);
        }

        // 3) firmware sidst: kun med profilens "firmware", på netstrøm og når intet venter på genstart – aldrig BIOS/UEFI
        if (p.UpdSystem != false && p.IncludeFirmware == true && Shell.Has("fwupdmgr"))
        {
            var fw = Pkg.Firmware();
            var (onAc, pct) = UpdatePlanner.Power(BatteryCollect.Status());
            var plan = UpdatePlanner.Plan(fw.Select(f => new UpdateCandidate
            {
                Id = f.Id, Title = f.Name, Kind = UpdateKinds.Firmware, Bios = f.Bios || UpdatePlanner.LooksLikeBios(f.Name), Locked = exclude.Contains(f.Id),
            }), new UpdateOptions { Apps = false, Firmware = p.IncludeFirmware == true, OnAc = onAc, BatteryPct = pct, RebootPending = reboot });
            rep.Deferred.AddRange(plan.Deferred);
            rep.Remaining.AddRange(plan.Later);
            foreach (var d in plan.Deferred) lines.Add($"--  {d.Title}: {d.Reason}");
            foreach (var c in plan.Phases.Where(x => x.Phase == UpdatePhases.Firmware).SelectMany(x => x.Items))
            {
                Phase(88, 100, UpdatePhases.Firmware, c.Title);
                var r = Shell.Run($"fwupdmgr update {Q(c.Id[Pkg.FirmwarePrefix.Length..])} -y --no-reboot-check 2>&1", minutes);
                if (r.ExitCode == 0) { rep.Add(UpdatePhases.Firmware, c.Id, c.Title); lines.Add($"OK  {c.Title}"); reboot |= Pkg.RebootNeeded(fresh: true); }
                else
                {
                    var why = FwupdErrors.Classify(r.Output) is { } k ? FwupdErrors.Text(k) : $"fejlkode {r.ExitCode}";
                    rep.Fail(UpdatePhases.Firmware, c.Id, c.Title, why);
                    lines.Add($"!!  {c.Title}: {why}");
                }
            }
        }
        rep.RebootPending = reboot;
        return Finish(rep, lines, round, max);
    }

    /// <summary>Mac: macOS først (softwareupdate), derefter App Store og Homebrew – én gang hver.</summary>
    private static JobResult MacRound(JobParams p, UpdateReport rep, List<string> lines, int round, int max, TimeSpan minutes, Action<int, int, string, string> phase)
    {
        var reboot = Mac.RebootNeeded();
        if (p.UpdSystem != false)
        {
            phase(0, 50, UpdatePhases.Software, "");
            var pending = Mac.PendingUpdates(force: true);
            if (reboot && pending.Count > 0)
                foreach (var u in pending) rep.Later(null, u);
            else if (pending.Count > 0)
            {
                var r = Mac.InstallMacOs("-a", minutes);
                var title = pending.Count == 1 ? pending[0] : $"{pending.Count} macOS-opdateringer";
                if (r.Output.Contains("skal ejeren selv godkende", StringComparison.Ordinal))
                {
                    // Apple-chip: hentet, men installationen kræver ejerens adgangskode – ærligt "venter", ikke "lykkedes"
                    rep.Defer(null, title, DeferReasons.Owner);
                    lines.Add($"--  {title}: hentet – {DeferReasons.Owner} (Mac med Apple-chip)");
                }
                else if (r.ExitCode == 0)
                {
                    rep.Add(UpdatePhases.Software, null, title);
                    lines.Add($"OK  {title}");
                    if (RestartRx().IsMatch(r.Output) || Mac.PendingUpdates(force: true).Count > 0 && RestartRx().IsMatch(Shell.Out("softwareupdate -l 2>&1", 180)))
                    {
                        Mac.MarkRebootNeeded();
                        reboot = true;
                    }
                }
                else { rep.Fail(UpdatePhases.Software, null, title, $"fejlkode {r.ExitCode}"); lines.Add($"!!  {title}:\n{Tail(r.Output)}"); }
            }
        }
        if (p.UpdApps != false)
        {
            phase(50, 100, UpdatePhases.Apps, "App Store og Homebrew");
            Record(rep, UpdatePhases.Apps, "App Store- og Homebrew-programmer", Mac.UpgradeAll(p.Exclude ?? [], false, minutes), lines);
        }
        rep.RebootPending = reboot;
        return Finish(rep, lines, round, max);
    }

    [GeneratedRegex(@"restart|genstart|\[restart\]|Action: restart", RegexOptions.IgnoreCase)] private static partial Regex RestartRx();
    [GeneratedRegex(@"(\d+) (?:upgraded|opgraderet)", RegexOptions.IgnoreCase)] private static partial Regex AptCount();

    private static string SystemTitle(string output) =>
        AptCount().Match(output) is { Success: true } m ? (m.Groups[1].Value == "0" ? "Systempakker (ingen nye)" : $"{m.Groups[1].Value} systempakke(r)") : "Systempakker";

    private static void Record(UpdateReport rep, string phase, string title, ShellResult r, List<string> lines)
    {
        if (r.ExitCode == 0) { rep.Add(phase, null, title); lines.Add($"OK  {title}"); }
        else { rep.Fail(phase, null, title, r.TimedOut ? "tiden løb ud" : $"fejlkode {r.ExitCode}"); lines.Add($"!!  {title} ({(r.TimedOut ? "tiden løb ud" : $"fejlkode {r.ExitCode}")})\n{Tail(r.Output)}"); }
    }

    private static JobResult Finish(UpdateReport rep, List<string> lines, int round, int max)
    {
        // "Systempakker (ingen nye)" er ikke noget installeret – men heller ikke en fejl
        rep.Installed.RemoveAll(i => i.Title.EndsWith("(ingen nye)", StringComparison.Ordinal));
        lines.Add($"\nRunde {round}/{max}: {UpdateStatus.Text(rep)}{(rep.RebootPending ? " – genstart kræves" : "")}");
        return new JobResult { Status = UpdateStatus.Of(rep), ExitCode = rep.RebootPending ? 3010 : 0, Output = string.Join('\n', lines).Trim(), Update = rep };
    }

    private static string Tail(string s) => string.Join('\n', s.Split('\n').Where(l => !l.StartsWith("::", StringComparison.Ordinal)).TakeLast(15));
    private static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";
}
