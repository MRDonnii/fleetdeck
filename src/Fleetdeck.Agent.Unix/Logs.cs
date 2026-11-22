using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Unix;

/// <summary>
/// "Hent logfiler" på Mac og Linux: agentens log, de seneste fejl og advarsler fra systemloggen og systeminfo, pakket med
/// <see cref="LogBundle"/> (hemmeligheder skjult, højst 5 MB) og sendt til Fleetdeck. Agentens opsætning (token) kommer aldrig med.
/// </summary>
public static class UnixLogs
{
    public const string AgentLog = "/var/log/fleetdeck-agent.log";

    /// <summary>Agentens log: logfilen (Mac, OpenRC, Unraid) eller journalen for tjenesten (systemd).</summary>
    public static string AgentScript(bool mac) => mac
        ? ""
        : "command -v journalctl >/dev/null 2>&1 && journalctl -u fleetdeck-agent -n 3000 --no-pager -o short-iso 2>/dev/null";

    /// <summary>Systemloggen: fejl og advarsler de seneste 7 dage (Mac: fejl den seneste time, ellers tager det for lang tid).</summary>
    public static string EventsScript(bool mac) => mac
        ? "log show --last 1h --style compact --predicate 'messageType == error or messageType == fault' 2>/dev/null | tail -n 600"
        : "if command -v journalctl >/dev/null 2>&1; then journalctl -p warning --since '-7 days' -n 600 --no-pager -o short-iso 2>/dev/null; " +
          "else for f in /var/log/syslog /var/log/messages; do [ -r \"$f\" ] && grep -Ei 'error|fail|warn|crit' \"$f\" | tail -n 600; done; fi";

    public static string InfoScript(bool mac) => mac
        ? "echo \"== macOS ==\"; sw_vers; echo; echo \"== Maskine ==\"; sysctl -n hw.model; sysctl -n machdep.cpu.brand_string; echo; uptime; echo; df -h /; echo; vm_stat | head -n 10"
        : "echo \"== System ==\"; uname -a; [ -r /etc/os-release ] && cat /etc/os-release; echo; uptime; echo; df -h -x tmpfs -x devtmpfs 2>/dev/null || df -h; echo; free -m 2>/dev/null; echo; " +
          "command -v systemctl >/dev/null 2>&1 && systemctl --failed --no-pager 2>/dev/null";

    public static byte[] Build(string why)
    {
        var mac = Mac.Is;
        var agent = LogBundle.ReadTail(AgentLog, 2 * 1024 * 1024, AgentLog + ".1");
        if (agent.Length == 0 && AgentScript(mac) is { Length: > 0 } a) agent = Shell.Run(a, TimeSpan.FromSeconds(60), null, LogBundle.MaxPartChars).Output;
        var parts = new List<LogPart>
        {
            new("agent-log", agent.Length > 0 ? agent : "Agentens log blev ikke fundet."),
            new(mac ? "systemlog" : "journal", Shell.Run(EventsScript(mac), TimeSpan.FromSeconds(90), null, LogBundle.MaxPartChars).Output),
            new("systeminfo", $"Maskine: {Environment.MachineName}\nAgent: {typeof(UnixLogs).Assembly.GetName().Version}\nSamlet: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n\n" +
                Shell.Run(InfoScript(mac), TimeSpan.FromSeconds(30), null, 200_000).Output),
        };
        return LogBundle.Build(parts, $"Logfiler fra {Environment.MachineName} ({why})", DateTime.UtcNow);
    }

    public static async Task<JobResult> Run(Api api, AgentJob j, CancellationToken ct)
    {
        var zip = await Task.Run(() => Build("hentet fra Fleetdeck"), ct);
        try { await api.Post("/api/agent/logs", new LogUpload { JobId = j.Id, Data = Convert.ToBase64String(zip) }, ct); }
        catch (HttpRequestException e) { return new JobResult { Status = JobStatus.Failed, ExitCode = 1, Output = $"Logfilerne kunne ikke sendes: {e.Message}" }; }
        return new JobResult
        {
            Status = JobStatus.Success, ExitCode = 0,
            Output = $"Logfilerne er sendt ({LogBundle.Size(zip.Length)}): agentens log, seneste fejl og advarsler fra systemloggen og systeminfo. Hent dem under Beskeder.",
        };
    }
}
