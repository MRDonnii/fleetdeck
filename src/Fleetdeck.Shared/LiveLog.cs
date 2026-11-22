using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fleetdeck.Shared;

/// <summary>
/// Besked til agenten om at sende systemloggen live. Findes kun i det signerede check-in-svar (<see cref="SignedCommands.LiveLogs"/>),
/// ligesom terminalen. Ældre agenter kender ikke feltet og ignorerer det (de får aldrig en skal i stedet).
/// </summary>
public sealed class LiveLogTicket
{
    public string SessionId { get; set; } = "";
    public string Secret { get; set; } = "";
    /// <summary>warning = fejl og advarsler, all = også information.</summary>
    public string Level { get; set; } = "warning";
}

public static partial class LiveLogPolicy
{
    /// <summary>Agentens melding ved check-in om, at den kan sende loggen live.</summary>
    public const string Capability = "livelog1";
    /// <summary>Højst så mange linjer i sekundet (med en buffer på <see cref="Burst"/>); resten tælles og meldes som sprunget over.</summary>
    public const int PerSecond = 20, Burst = 100;
    /// <summary>Højst så mange live-logge på én maskine ad gangen.</summary>
    public const int MaxConcurrent = 4;
    /// <summary>Stopper altid efter en halv time; brugeren kan starte igen.</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(30);
    /// <summary>Siden sender et livstegn hvert halve minut, mens den er synlig. Uden livstegn i to minutter stopper serveren.</summary>
    public static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(30), HeartbeatTimeout = TimeSpan.FromMinutes(2);

    [GeneratedRegex("^[a-f0-9]{32}$")] private static partial Regex IdRx();
    [GeneratedRegex("^[a-f0-9]{64}$")] private static partial Regex SecretRx();

    public static bool TicketValid(LiveLogTicket? t) =>
        t is not null && IdRx().IsMatch(t.SessionId) && SecretRx().IsMatch(t.Secret) && t.Level is "warning" or "all";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    public static byte[] Frame(SysLogEvent e) => TerminalFrame.MakeData(JsonSerializer.SerializeToUtf8Bytes(e, Web));
}

/// <summary>Spand med tokens: højst <c>perSecond</c> linjer i sekundet over tid, med plads til et kort udbrud.</summary>
public sealed class LiveLogRate(int perSecond = LiveLogPolicy.PerSecond, int burst = LiveLogPolicy.Burst)
{
    private double _tokens = burst;
    private DateTime _at = DateTime.MinValue;
    public int Dropped { get; private set; }

    public bool Take(DateTime now)
    {
        if (_at != DateTime.MinValue) _tokens = Math.Min(burst, _tokens + (now - _at).TotalSeconds * perSecond);
        _at = now;
        if (_tokens >= 1) { _tokens -= 1; return true; }
        Dropped++;
        return false;
    }

    /// <summary>Antallet, der er sprunget over siden sidst (og nulstil).</summary>
    public int TakeDropped() { var d = Dropped; Dropped = 0; return d; }
}

/// <summary>Kilden til live-loggen på maskinen (journalctl -f, log stream, eller hændelseslogens nye poster).</summary>
public interface ILiveLogSource : IDisposable
{
    /// <summary>Næste hændelse. Null = kilden er stoppet.</summary>
    Task<SysLogEvent?> Next(CancellationToken ct);
}

/// <summary>Agentens side af live-loggen: forbinder ud til serveren (samme vej som terminalen) og sender hændelser, til siden lukkes.</summary>
public static class LiveLogAgent
{
    private static readonly ConcurrentDictionary<string, DateTime> Seen = new();
    private static int _running;

    public static void Start(IEnumerable<LiveLogTicket> tickets, string server, string token, Func<HttpMessageHandler> handler,
        Func<LiveLogTicket, ILiveLogSource> open, Action<string> log, CancellationToken stop)
    {
        foreach (var t in tickets)
        {
            if (!LiveLogPolicy.TicketValid(t)) { log("Live-log afvist: ugyldig billet"); continue; }
            if (!TerminalPolicy.TransportOk(server)) { log("Live-log afvist: forbindelsen til serveren er ikke krypteret"); continue; }
            foreach (var old in Seen.Where(kv => kv.Value < DateTime.UtcNow.AddHours(-1)).Select(kv => kv.Key).ToList()) Seen.TryRemove(old, out _);
            if (!Seen.TryAdd(t.SessionId, DateTime.UtcNow)) continue;
            if (Interlocked.Increment(ref _running) > LiveLogPolicy.MaxConcurrent)
            {
                Interlocked.Decrement(ref _running); log("Live-log afvist: for mange på én gang"); continue;
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    log($"Live-log startet (session {t.SessionId[..8]})");
                    using var h = handler();
                    using var invoker = new HttpMessageInvoker(h, disposeHandler: false);
                    using var ws = new ClientWebSocket();
                    ws.Options.SetRequestHeader("Authorization", "Bearer " + token);
                    ws.Options.SetRequestHeader(TerminalPolicy.SecretHeader, t.Secret);
                    ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                    await ws.ConnectAsync(TerminalPolicy.AgentUri(server, t.SessionId), invoker, stop);
                    using var src = open(t);
                    await Pump(ws, src, () => DateTime.UtcNow, stop);
                }
                catch (Exception e) when (e is not OperationCanceledException) { log($"Live-log fejlede: {e.Message}"); }
                catch (OperationCanceledException) { }
                finally { Interlocked.Decrement(ref _running); log($"Live-log stoppet (session {t.SessionId[..8]})"); }
            }, CancellationToken.None);
        }
    }

    /// <summary>
    /// Sender hændelser fra kilden, renset og begrænset, til forbindelsen lukkes, kilden stopper eller tiden er gået.
    /// Alt fra serveren ignoreres (live-loggen tager ikke imod input).
    /// </summary>
    public static async Task Pump(WebSocket ws, ILiveLogSource src, Func<DateTime> now, CancellationToken ct, TimeSpan? lifetime = null)
    {
        // Annullering af en igangværende læsning eller afsendelse lukker en WebSocket brat. Derfor stopper "work" kun kilden,
        // og læsningen stoppes først, når slutbeskeden er sendt.
        using var work = CancellationTokenSource.CreateLinkedTokenSource(ct);
        work.CancelAfter(lifetime ?? LiveLogPolicy.MaxLifetime);
        using var stopRead = new CancellationTokenSource();
        var reader = Task.Run(async () =>
        {
            var buf = new byte[4096];
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    var r = await ws.ReceiveAsync(buf, stopRead.Token);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                }
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException) { }
            finally { try { await work.CancelAsync(); } catch (ObjectDisposedException) { } }
        }, CancellationToken.None);
        var rate = new LiveLogRate();
        var code = 0;
        try
        {
            while (!work.IsCancellationRequested)
            {
                var e = await src.Next(work.Token);
                if (e is null) { code = 1; break; }
                SysLogPolicy.Clean(e);
                e.Kind = SysLogKinds.Of(e);
                if (!rate.Take(now())) continue;
                if (rate.TakeDropped() is > 0 and var n)
                    await ws.SendAsync(LiveLogPolicy.Frame(Note(now(), $"{n} linjer sprunget over – loggen skriver hurtigere, end live-visningen viser (højst {LiveLogPolicy.PerSecond} i sekundet).")),
                        WebSocketMessageType.Binary, true, ct);
                await ws.SendAsync(LiveLogPolicy.Frame(e), WebSocketMessageType.Binary, true, ct);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException) { }
        finally
        {
            try
            {
                if (ws.State == WebSocketState.Open)
                {
                    using var bye = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await ws.SendAsync(TerminalFrame.MakeExit(code), WebSocketMessageType.Binary, true, bye.Token);
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", bye.Token);
                }
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException) { }
            if (await Task.WhenAny(reader, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None)) != reader) await stopRead.CancelAsync();
            try { await reader; } catch (Exception e) when (e is WebSocketException or OperationCanceledException) { }
        }
    }

    public static SysLogEvent Note(DateTime at, string text) => new() { Time = at, Level = SysLogLevels.Info, Log = "fleetdeck", Source = "Fleetdeck", Message = text };
}
