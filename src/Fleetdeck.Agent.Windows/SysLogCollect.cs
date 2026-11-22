using System.Diagnostics.Eventing.Reader;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Windows;

/// <summary>
/// Fejlloggen fra Windows' hændelseslog (System, Application og Windows Update): kritiske hændelser, fejl og advarsler.
/// Kører som SYSTEM, højst hvert 10. minut. En fejl i én log springer kun den log over.
/// </summary>
public static class SysLogCollect
{
    public static readonly SysLogSchedule Schedule = new(SysLogPolicy.Every);
    private const int PerChannel = 400;

    public static List<SysLogEvent> Collect(DateTime sinceUtc)
    {
        var o = new List<SysLogEvent>();
        foreach (var ch in WinEventLog.Channels)
            o.AddRange(Read(ch, WinEventLog.Query(sinceUtc), PerChannel, newestFirst: true));
        return SysLogPolicy.Prioritize(o.Select(SysLogPolicy.Clean));
    }

    /// <summary>Læser højst <paramref name="max"/> poster. Logge, der ikke findes, eller som ikke må læses, giver en tom liste.</summary>
    internal static List<SysLogEvent> Read(string channel, string xpath, int max, bool newestFirst)
    {
        var o = new List<SysLogEvent>();
        try
        {
            using var r = new EventLogReader(new EventLogQuery(channel, PathType.LogName, xpath) { ReverseDirection = newestFirst });
            for (var ev = r.ReadEvent(); ev is not null && o.Count < max; ev = r.ReadEvent())
                using (ev) o.Add(WinEventLog.Map(ev.ProviderName, ev.Id, ev.Level, ev.TimeCreated, channel, Describe(ev), ev.RecordId));
        }
        catch (Exception e) when (e is EventLogException or UnauthorizedAccessException or InvalidOperationException) { }
        return o;
    }

    /// <summary>Den læsbare besked. Mangler udbyderens tekster (afinstalleret program), bruges hændelsens data i stedet.</summary>
    private static string? Describe(EventRecord ev)
    {
        try { if (ev.FormatDescription() is { Length: > 0 } d) return d; }
        catch (Exception e) when (e is EventLogException or InvalidOperationException) { }
        try { return string.Join(" · ", ev.Properties.Select(p => p.Value?.ToString()).Where(v => !string.IsNullOrWhiteSpace(v)).Take(8)); }
        catch (Exception e) when (e is EventLogException or InvalidOperationException) { return null; }
    }

    /// <summary>Det seneste postnummer i loggen (null = tom eller kan ikke læses).</summary>
    internal static long? LatestRecord(string channel)
    {
        try
        {
            using var r = new EventLogReader(new EventLogQuery(channel, PathType.LogName, "*") { ReverseDirection = true });
            using var ev = r.ReadEvent();
            return ev?.RecordId;
        }
        catch (Exception e) when (e is EventLogException or UnauthorizedAccessException or InvalidOperationException) { return null; }
    }
}

/// <summary>
/// Live-log på Windows: nye poster i System og Application hvert andet sekund (efter postnummer, så intet læses to gange).
/// Starter med de seneste ti minutters hændelser. Ingen abonnement efterlades: alt stopper, når live-loggen lukkes.
/// </summary>
public sealed class WinLiveSource(bool all) : ILiveLogSource
{
    private static readonly string[] Channels = ["System", "Application"];
    private readonly Queue<SysLogEvent> _queue = new();
    private readonly Dictionary<string, long> _last = [];
    private bool _primed;

    public async Task<SysLogEvent?> Next(CancellationToken ct)
    {
        while (true)
        {
            if (_queue.TryDequeue(out var e)) return e;
            if (_primed) await Task.Delay(TimeSpan.FromSeconds(2), ct);
            await Task.Run(Poll, ct);
            _primed = true;
        }
    }

    private void Poll()
    {
        var batch = new List<SysLogEvent>();
        foreach (var ch in Channels)
        {
            if (!_last.TryGetValue(ch, out var after))
            {
                _last[ch] = SysLogCollect.LatestRecord(ch) ?? 0;
                batch.AddRange(SysLogCollect.Read(ch, WinEventLog.Query(DateTime.UtcNow.AddMinutes(-10), null, all), 30, newestFirst: true));
                continue;
            }
            // fra sidste kendte til den nyeste lige nu: poster, der kommer under læsningen, tages næste gang
            if (SysLogCollect.LatestRecord(ch) is not { } latest || latest <= after) continue;
            batch.AddRange(SysLogCollect.Read(ch, WinEventLog.Query(null, after, all, latest), 200, newestFirst: false));
            _last[ch] = latest;
        }
        foreach (var e in batch.OrderBy(x => x.Time)) _queue.Enqueue(e);
    }

    public void Dispose() { }
}
