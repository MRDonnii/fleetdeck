using System.Text.Json;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Windows;

/// <summary>
/// Logfiler til Beskeder ("Send logfiler" fra brugeren og "Hent logfiler" fra admin): agentens log, de seneste fejl og
/// advarsler fra Windows' hændelseslog og systeminfo, pakket med <see cref="LogBundle"/> (hemmeligheder skjult, højst 5 MB).
/// config.json (agentens token) og andre filer i Fleetdecks mappe kommer aldrig med.
/// </summary>
public sealed class LogCollector(AgentOptions opt, Runner runner, Collectors collectors, ILogger<LogCollector> log)
{
    /// <summary>Hændelsesloggen: fejl og advarsler (niveau 1–3) fra System og Programmer de seneste 7 dage, højst 150 af hver.</summary>
    public const string EventsScript =
        "$since = (Get-Date).AddDays(-7)\r\n" +
        "foreach ($name in 'System', 'Application') {\r\n" +
        "  \"== $name (fejl og advarsler, seneste 7 dage) ==\"\r\n" +
        "  Get-WinEvent -FilterHashtable @{ LogName = $name; Level = 1, 2, 3; StartTime = $since } -MaxEvents 150 -ErrorAction SilentlyContinue |\r\n" +
        "    ForEach-Object { '{0:yyyy-MM-dd HH:mm:ss}  {1,-9} {2} ({3}): {4}' -f $_.TimeCreated, $_.LevelDisplayName, $_.ProviderName, $_.Id, (($_.Message -replace '\\s+', ' ').Trim()) }\r\n" +
        "  ''\r\n" +
        "}\r\n";

    public async Task<byte[]> Build(string why, CancellationToken ct)
    {
        var parts = new List<LogPart>
        {
            new("agent-log", LogBundle.ReadTail(opt.LogFile, 2 * 1024 * 1024, opt.LogFile + ".1")),
            new("haendelseslog", await Events(ct)),
            new("systeminfo", SystemInfo()),
        };
        var zip = LogBundle.Build(parts, $"Logfiler fra {Environment.MachineName} ({why})", DateTime.UtcNow);
        log.LogInformation("Logfiler samlet ({Size}, {Why})", LogBundle.Size(zip.Length), why);
        return zip;
    }

    private async Task<string> Events(CancellationToken ct)
    {
        try
        {
            var r = await runner.PowerShell(EventsScript, RunAs.System, TimeSpan.FromSeconds(90), ct);
            return r.TimedOut ? r.Output + "\n[stoppet efter 90 sek.]" : r.Output;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return $"Hændelsesloggen kunne ikke læses: {e.Message}";
        }
    }

    private string SystemInfo()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        var lines = new List<string>
        {
            $"Maskine: {Environment.MachineName}",
            $"Agent: {typeof(LogCollector).Assembly.GetName().Version}",
            $"Windows: {Environment.OSVersion.VersionString}",
            $"Oppetid: {TimeSpan.FromMilliseconds(Environment.TickCount64):d\\.hh\\:mm} (d.tt:mm)",
            $"Bruger på skærmen: {Native.ConsoleUser() ?? "ingen"}",
            $"Samlet: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            "",
        };
        try { lines.Add("== Fakta ==\n" + JsonSerializer.Serialize(collectors.System(), o)); }
        catch (Exception e) when (e is not OutOfMemoryException) { lines.Add($"Fakta kunne ikke læses: {e.Message}"); }
        try { lines.Add("== Helbred ==\n" + JsonSerializer.Serialize(collectors.Health(false), o)); }
        catch (Exception e) when (e is not OutOfMemoryException) { lines.Add($"Helbred kunne ikke læses: {e.Message}"); }
        return string.Join('\n', lines);
    }
}
