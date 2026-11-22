using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.RegularExpressions;

namespace Fleetdeck.Shared;

/// <summary>
/// Besked til agenten om at åbne en terminal. Findes kun i det signerede check-in-svar (<see cref="SignedCommands.Terminals"/>),
/// så en falsk server eller en mand-i-midten ikke kan starte en skal. Ældre agenter kender ikke feltet og ignorerer det.
/// </summary>
public sealed class TerminalTicket
{
    public string SessionId { get; set; } = "";
    /// <summary>Engangshemmelighed, som agenten viser serveren, når den forbinder til sessionen.</summary>
    public string Secret { get; set; } = "";
    public int Cols { get; set; } = 120;
    public int Rows { get; set; } = 32;
}

public static partial class TerminalPolicy
{
    /// <summary>Agentens melding ved check-in om, at den kan åbne en terminal.</summary>
    public const string Capability = "pty1";
    public const string SecretHeader = "X-Terminal-Secret";
    public const int MaxConcurrent = 8, MaxFrame = 64 * 1024;

    [GeneratedRegex("^[a-f0-9]{32}$")] private static partial Regex IdRx();
    [GeneratedRegex("^[a-f0-9]{64}$")] private static partial Regex SecretRx();

    public static bool TicketValid(TerminalTicket? t) =>
        t is not null && IdRx().IsMatch(t.SessionId) && SecretRx().IsMatch(t.Secret) && t.Cols is >= 10 and <= 500 && t.Rows is >= 4 and <= 200;

    /// <summary>En skal må kun gå over en krypteret forbindelse eller den lokale socket-fil – aldrig over almindelig http.</summary>
    public static bool TransportOk(string? server) =>
        server is { } s && (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("unix:", StringComparison.Ordinal));

    public static Uri AgentUri(string server, string sessionId)
    {
        var b = server.StartsWith("unix:", StringComparison.Ordinal) ? "ws://localhost" : "wss://" + server.TrimEnd('/')["https://".Length..];
        return new Uri($"{b}/api/agent/terminal/{sessionId}");
    }
}

/// <summary>Rammerne på terminal-forbindelsen. Første byte er typen; resten afhænger af typen.</summary>
public static class TerminalFrame
{
    public const byte Data = 0, Resize = 1, Exit = 2, Status = 3;

    public static byte[] MakeData(ReadOnlySpan<byte> d) { var b = new byte[d.Length + 1]; b[0] = Data; d.CopyTo(b.AsSpan(1)); return b; }
    public static byte[] MakeResize(int cols, int rows)
    {
        var b = new byte[5]; b[0] = Resize;
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(1), (ushort)cols); BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(3), (ushort)rows);
        return b;
    }
    public static byte[] MakeExit(int code) { var b = new byte[5]; b[0] = Exit; BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(1), code); return b; }
    public static byte[] MakeStatus(string text) { var t = System.Text.Encoding.UTF8.GetBytes(text); var b = new byte[t.Length + 1]; b[0] = Status; t.CopyTo(b, 1); return b; }

    /// <summary>Gyldig størrelse? Ugyldige rammer ignoreres, så en fejl i modparten ikke kan vælte sessionen.</summary>
    public static bool TryResize(ReadOnlySpan<byte> f, out int cols, out int rows)
    {
        cols = rows = 0;
        if (f.Length != 5 || f[0] != Resize) return false;
        cols = BinaryPrimitives.ReadUInt16BigEndian(f[1..]); rows = BinaryPrimitives.ReadUInt16BigEndian(f[3..]);
        return cols is >= 10 and <= 500 && rows is >= 4 and <= 200;
    }
}

/// <summary>En kørende skal på maskinen (ConPTY på Windows, pseudoterminal på Linux og Mac).</summary>
public interface IPtySession : IDisposable
{
    /// <summary>Blokerer, til der er uddata. 0 = skallen er lukket.</summary>
    int Read(byte[] buffer);
    void Write(ReadOnlySpan<byte> data);
    void Resize(int cols, int rows);
    /// <summary>Venter på, at skallen stopper, og giver dens slutkode.</summary>
    int WaitForExit();
}

/// <summary>Agentens side af en terminal: forbinder ud til serveren og kobler forbindelsen sammen med skallen.</summary>
public static class TerminalAgent
{
    private static readonly ConcurrentDictionary<string, DateTime> Seen = new();
    private static int _running;

    /// <summary>Starter de sessioner, som det signerede svar beder om. Afviser alt over en ukrypteret forbindelse.</summary>
    public static void Start(IEnumerable<TerminalTicket> tickets, string server, string token, Func<HttpMessageHandler> handler,
        Func<int, int, IPtySession> startPty, Action<string> log, CancellationToken stop)
    {
        foreach (var t in tickets)
        {
            if (!TerminalPolicy.TicketValid(t)) { log("Terminal afvist: ugyldig billet"); continue; }
            if (!TerminalPolicy.TransportOk(server)) { log("Terminal afvist: forbindelsen til serveren er ikke krypteret"); continue; }
            foreach (var old in Seen.Where(kv => kv.Value < DateTime.UtcNow.AddHours(-1)).Select(kv => kv.Key).ToList()) Seen.TryRemove(old, out _);
            if (!Seen.TryAdd(t.SessionId, DateTime.UtcNow)) continue;     // samme billet to gange: kun én skal
            if (Interlocked.Increment(ref _running) > TerminalPolicy.MaxConcurrent)
            {
                Interlocked.Decrement(ref _running); log("Terminal afvist: for mange åbne sessioner"); continue;
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    log($"Terminal åbnet (session {t.SessionId[..8]})");
                    using var h = handler();
                    using var invoker = new HttpMessageInvoker(h, disposeHandler: false);
                    using var ws = new ClientWebSocket();
                    ws.Options.SetRequestHeader("Authorization", "Bearer " + token);
                    ws.Options.SetRequestHeader(TerminalPolicy.SecretHeader, t.Secret);
                    ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                    await ws.ConnectAsync(TerminalPolicy.AgentUri(server, t.SessionId), invoker, stop);
                    using var pty = startPty(t.Cols, t.Rows);
                    await Pump(ws, pty, stop);
                }
                catch (Exception e) when (e is not OperationCanceledException) { log($"Terminal fejlede: {e.Message}"); }
                catch (OperationCanceledException) { }
                finally { Interlocked.Decrement(ref _running); log($"Terminal lukket (session {t.SessionId[..8]})"); }
            }, CancellationToken.None);
        }
    }

    /// <summary>Flytter data begge veje, til skallen stopper, eller forbindelsen lukkes.</summary>
    public static async Task Pump(WebSocket ws, IPtySession pty, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var output = Task.Factory.StartNew(async () =>
        {
            var buf = new byte[16 * 1024];
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var n = pty.Read(buf);
                    if (n <= 0) break;
                    await ws.SendAsync(TerminalFrame.MakeData(buf.AsSpan(0, n)), WebSocketMessageType.Binary, true, cts.Token);
                }
                var code = pty.WaitForExit();
                if (ws.State == WebSocketState.Open) await ws.SendAsync(TerminalFrame.MakeExit(code), WebSocketMessageType.Binary, true, cts.Token);
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException) { }
            finally { await cts.CancelAsync(); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        var frame = new byte[TerminalPolicy.MaxFrame + 16];
        try
        {
            while (ws.State == WebSocketState.Open && !cts.IsCancellationRequested)
            {
                var got = 0; ValueWebSocketReceiveResult r;
                do
                {
                    if (got >= frame.Length) return;     // for stor ramme: luk i stedet for at gætte
                    r = await ws.ReceiveAsync(frame.AsMemory(got), cts.Token);
                    got += r.Count;
                } while (!r.EndOfMessage && r.MessageType != WebSocketMessageType.Close);
                if (r.MessageType == WebSocketMessageType.Close || got == 0) break;
                if (frame[0] == TerminalFrame.Data) pty.Write(frame.AsSpan(1, got - 1));
                else if (TerminalFrame.TryResize(frame.AsSpan(0, got), out var c, out var rows)) pty.Resize(c, rows);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException) { }
        finally
        {
            await cts.CancelAsync();
            pty.Dispose();      // lukker skallen, så læsningen slutter
            try { await output; } catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException) { }
        }
    }
}
