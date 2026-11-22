using System.Diagnostics;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Unix;

/// <summary>
/// Fejlloggen på Linux (journald, eller /var/log/syslog på fx Unraid) og Mac (unified log + panikrapporter).
/// Kun fejl og advarsler, kun så ofte som <see cref="SysLogPolicy"/> tillader, og en fejl her stopper aldrig agenten.
/// </summary>
public static class SysLogUnix
{
    public static readonly SysLogSchedule Schedule = new(Mac.Is ? SysLogPolicy.EveryMac : SysLogPolicy.Every);
    private const string SyslogFile = "/var/log/syslog";
    private static bool? _journal;
    private static bool Journal => _journal ??= !Mac.Is && Shell.Has("journalctl");

    public static List<SysLogEvent> Collect(DateTime sinceUtc, DateTime nowUtc)
    {
        var o = new List<SysLogEvent>();
        if (Mac.Is)
        {
            // en mappe, der ikke kan læses, må ikke tage unified log med sig
            try { o.AddRange(MacPanics(sinceUtc)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { Log.Warn($"Panikrapporter: {e.Message}"); }
            var r = Shell.Run(MacLog.ShowCommand(nowUtc - sinceUtc), TimeSpan.FromSeconds(25), maxChars: 1_000_000);
            o.AddRange(MacLog.Parse(r.Output).Where(e => e.Time >= sinceUtc));
        }
        else if (Journal)
        {
            var r = Shell.Run(JournalLog.Command(sinceUtc), TimeSpan.FromSeconds(30), maxChars: 3_000_000);
            o.AddRange(JournalLog.Parse(r.Output).Where(e => e.Time >= sinceUtc));
        }
        else if (File.Exists(SyslogFile))
            o.AddRange(SyslogText.Parse(LogBundle.ReadTail(SyslogFile, 512 * 1024), sinceUtc, nowUtc, TimeZoneInfo.Local));
        return SysLogPolicy.Prioritize(o.Select(SysLogPolicy.Clean));
    }

    /// <summary>Kernepanik-rapporter (.panic og .ips med bug_type 210), højst ti, kun de første 64 kB af hver.</summary>
    private static IEnumerable<SysLogEvent> MacPanics(DateTime sinceUtc)
    {
        const string dir = "/Library/Logs/DiagnosticReports";
        if (!Directory.Exists(dir)) yield break;
        var files = new DirectoryInfo(dir).EnumerateFiles().Where(f => (f.Extension is ".panic" or ".ips") && f.LastWriteTimeUtc >= sinceUtc)
            .OrderByDescending(f => f.LastWriteTimeUtc).Take(10).ToList();
        foreach (var f in files)
        {
            string text;
            try
            {
                using var s = f.OpenRead();
                var buf = new byte[Math.Min(64 * 1024, (int)Math.Min(f.Length, int.MaxValue))];
                var n = s.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false);
                text = System.Text.Encoding.UTF8.GetString(buf, 0, n);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            if (MacLog.Panic(f.Name, text, f.LastWriteTimeUtc) is { } p) yield return p;
        }
    }

    /// <summary>Live: journalctl -f, tail -F på syslog, eller log stream på Mac.</summary>
    public static ILiveLogSource Live(LiveLogTicket t)
    {
        var all = t.Level == "all";
        if (Mac.Is)
            return new ProcessLiveSource("log", ["stream", "--style", "ndjson", "--predicate", all ? MacLog.LivePredicateAll : MacLog.LivePredicate], MacLog.Line);
        if (Journal)
            return new ProcessLiveSource("journalctl", ["-f", "-o", "json", "-n", "20", "-q", "--no-pager", "-p", all ? "info" : "warning"], JournalLog.Line);
        return new ProcessLiveSource("tail", ["-F", "-n", "20", SyslogFile], l => SyslogText.Line(l, DateTime.UtcNow, TimeZoneInfo.Local, all));
    }
}

/// <summary>Et program, der skriver loggen linje for linje (journalctl -f, log stream, tail -F). Stoppes, når live-loggen lukkes.</summary>
public sealed class ProcessLiveSource : ILiveLogSource
{
    private readonly Process _p;
    private readonly Func<string, SysLogEvent?> _parse;

    public ProcessLiveSource(string file, IEnumerable<string> args, Func<string, SysLogEvent?> parse)
    {
        _parse = parse;
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        _p = Process.Start(psi) ?? throw new InvalidOperationException($"{file} kunne ikke startes");
        _p.ErrorDataReceived += (_, _) => { };
        _p.BeginErrorReadLine();
    }

    public async Task<SysLogEvent?> Next(CancellationToken ct)
    {
        while (true)
        {
            var line = await _p.StandardOutput.ReadLineAsync(ct);
            if (line is null) return null;
            if (_parse(line) is { } e) return e;
        }
    }

    public void Dispose()
    {
        try { if (!_p.HasExited) _p.Kill(entireProcessTree: true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        _p.Dispose();
    }
}
