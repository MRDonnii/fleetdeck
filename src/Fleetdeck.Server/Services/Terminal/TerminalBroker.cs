using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Shared;

namespace Fleetdeck.Server.Services;

/// <summary>Den ende af en session, der taler med maskinen: agentens forbindelse eller en SSH-klient.</summary>
public interface ITerminalBackend : IAsyncDisposable
{
    Task Write(ReadOnlyMemory<byte> data, CancellationToken ct);
    Task Resize(int cols, int rows, CancellationToken ct);
    /// <summary>Sender uddata videre, til forbindelsen lukkes. Giver skallens slutkode, hvis den kendes.</summary>
    Task<int?> Pump(Func<ReadOnlyMemory<byte>, Task> output, CancellationToken ct);
}

public sealed class TerminalSession
{
    public required string Id { get; init; }
    public required int UserId { get; init; }
    public required string UserName { get; init; }
    public required string Stamp { get; init; }
    public required string Label { get; init; }
    public required string Kind { get; init; }     // agent | ssh | livelog
    public bool IsLiveLog => Kind == LiveLogKind;
    public const string LiveLogKind = "livelog";
    public int? MachineId { get; init; }
    public int? HostId { get; init; }
    public bool Protected { get; init; }
    /// <summary>windows, linux eller mac. Null = ukendt (SSH-værter).</summary>
    public string? Platform { get; init; }
    public required string UnlockToken { get; init; }
    public required string TicketHash { get; init; }
    public string? AgentSecretHash { get; init; }
    public TerminalTicket? Ticket { get; set; }
    /// <summary>Live-log (Kind = livelog): billetten til agenten, til den er hentet.</summary>
    public LiveLogTicket? LiveLog { get; set; }
    public int Cols { get; init; } = 120;
    public int Rows { get; init; } = 32;
    public DateTimeOffset Created { get; init; }
    public DateTimeOffset LastInput { get; set; }
    public int TicketUsed;
    public int AgentClaimed;
    public readonly TaskCompletionSource<WebSocket> AgentSocket = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>Holdes åben, til agentens forbindelse er færdig.</summary>
    public readonly TaskCompletionSource AgentDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly CancellationTokenSource Cancel = new();
    public volatile string? CloseReason;
    public ITerminalBackend? Backend { get; set; }
    public readonly SemaphoreSlim InputLock = new(1, 1);

    /// <summary>Skriver til skallen (tastetryk og gemte kommandoer skiftes til, så de ikke blandes).</summary>
    public async Task Input(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (Backend is not { } b) return;
        await InputLock.WaitAsync(ct);
        try { await b.Write(data, ct); } finally { InputLock.Release(); }
    }
}

/// <summary>Holder styr på åbne terminaler: billetter til agenterne, grænser, inaktivitet og afbrydelse, når brugeren mister adgangen.</summary>
public sealed class TerminalBroker(IServiceScopeFactory scopes, TerminalGate gate, TimeProvider clock, ILogger<TerminalBroker> log) : BackgroundService
{
    public const int MaxPerUser = 8, MaxTotal = 32;
    public static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(15), ConnectWindow = TimeSpan.FromSeconds(90);
    private readonly ConcurrentDictionary<string, TerminalSession> _sessions = new();

    public static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
    private static bool Same(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));
    public DateTimeOffset Now => clock.GetUtcNow();

    public int CountFor(int userId) => _sessions.Values.Count(s => s.UserId == userId);
    /// <summary>Højst så mange live-logge pr. bruger og pr. maskine ad gangen.</summary>
    public const int MaxLiveLogsPerUser = 4;
    public bool LiveLogFull(int userId, int machineId) =>
        Full(userId) || _sessions.Values.Count(s => s.IsLiveLog && s.UserId == userId) >= MaxLiveLogsPerUser
        || _sessions.Values.Count(s => s.IsLiveLog && s.MachineId == machineId) >= LiveLogPolicy.MaxConcurrent;
    public bool Full(int userId) => CountFor(userId) >= MaxPerUser || _sessions.Count >= MaxTotal;
    public void Add(TerminalSession s) => _sessions[s.Id] = s;
    public TerminalSession? Find(string id) => _sessions.GetValueOrDefault(id);
    public IEnumerable<TerminalSession> ForUser(int userId) => _sessions.Values.Where(s => s.UserId == userId);

    /// <summary>Billetterne til maskinen. Hver billet udleveres kun én gang.</summary>
    public IReadOnlyList<TerminalTicket> TakeTickets(int machineId)
    {
        var list = new List<TerminalTicket>();
        foreach (var s in _sessions.Values.Where(s => s.MachineId == machineId && !s.IsLiveLog && s.Ticket is not null))
        {
            lock (s) { if (s.Ticket is { } t) { list.Add(t); s.Ticket = null; } }
        }
        return list;
    }

    /// <summary>Live-log-billetterne til maskinen (hver kun én gang). Holdes adskilt fra terminalerne: en live-log åbner aldrig en skal.</summary>
    public IReadOnlyList<LiveLogTicket> TakeLiveLogs(int machineId)
    {
        var list = new List<LiveLogTicket>();
        foreach (var s in _sessions.Values.Where(s => s.MachineId == machineId && s.IsLiveLog && s.LiveLog is not null))
        {
            lock (s) { if (s.LiveLog is { } t) { list.Add(t); s.LiveLog = null; } }
        }
        return list;
    }

    public bool ClaimBrowser(TerminalSession s, string ticket, int userId) =>
        s.UserId == userId && Now - s.Created <= ConnectWindow && Same(s.TicketHash, Hash(ticket)) && Interlocked.Exchange(ref s.TicketUsed, 1) == 0;

    public bool ClaimAgent(TerminalSession s, int machineId, string? secret) =>
        s.MachineId == machineId && s.AgentSecretHash is { } h && !string.IsNullOrEmpty(secret) && Now - s.Created <= ConnectWindow
        && Same(h, Hash(secret)) && Interlocked.Exchange(ref s.AgentClaimed, 1) == 0;

    public void Close(TerminalSession s, string reason)
    {
        s.CloseReason ??= reason;
        try { s.Cancel.Cancel(); } catch (ObjectDisposedException) { }
    }

    public void Remove(TerminalSession s) => _sessions.TryRemove(s.Id, out _);

    /// <summary>Lukker sessioner, der er glemt, eller hvis bruger ikke længere må være her (logget ud, skiftet kode, mistet rollen).</summary>
    public async Task Sweep(CancellationToken ct)
    {
        if (_sessions.IsEmpty) return;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var ids = _sessions.Values.Select(s => s.UserId).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct);
        foreach (var s in _sessions.Values)
        {
            if (!users.TryGetValue(s.UserId, out var u) || u.SecurityStamp != s.Stamp || u.Role != Roles.Admin) Close(s, "Du er ikke længere logget ind.");
            else if (s.IsLiveLog && Now - s.Created > LiveLogPolicy.MaxLifetime) Close(s, "Live-loggen stopper efter 30 minutter. Tryk Live igen for at fortsætte.");
            else if (s.IsLiveLog && s.TicketUsed == 1 && Now - s.LastInput > LiveLogPolicy.HeartbeatTimeout) Close(s, "Stoppet, fordi siden er lukket eller skjult.");
            else if (Now - s.LastInput > IdleLimit) Close(s, "Lukket efter 15 minutter uden brug.");
            else if (s.TicketUsed == 0 && Now - s.Created > ConnectWindow) Close(s, "Forbindelsen blev ikke oprettet i tide.");
        }
        foreach (var s in _sessions.Values.Where(s => s.TicketUsed == 0 && Now - s.Created > ConnectWindow)) { s.AgentDone.TrySetResult(); Remove(s); }
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(15), clock, stop); await Sweep(stop); }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { log.LogWarning(e, "Terminal: oprydning fejlede"); }
        }
    }

    /// <summary>Kobler browseren til maskinen og bliver her, til sessionen slutter.</summary>
    public async Task Run(TerminalSession s, WebSocket browser, Func<CancellationToken, Task<ITerminalBackend>> connect)
    {
        using var scope = scopes.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<AuditService>();
        var started = Now; var ct = s.Cancel.Token;
        using var sendLock = new SemaphoreSlim(1, 1);
        async Task Send(byte[] frame)
        {
            await sendLock.WaitAsync(CancellationToken.None);
            try { if (browser.State == WebSocketState.Open) await browser.SendAsync(frame, WebSocketMessageType.Binary, true, CancellationToken.None); }
            finally { sendLock.Release(); }
        }
        try
        {
            await Send(TerminalFrame.MakeStatus("Forbinder …"));
            await using var backend = await connect(ct);
            s.Backend = backend;
            s.LastInput = Now;
            await Send(TerminalFrame.MakeStatus(""));
            await audit.Log(s.UserName, $"{Area(s)}.connect", $"{s.Label} ({s.Kind})", s.MachineId);
            var output = backend.Pump(d => Send(TerminalFrame.MakeData(d.Span)), ct);
            var input = ReadBrowser(s, browser, backend, ct);
            var first = await Task.WhenAny(output, input);
            if (first == output && output.IsCompletedSuccessfully) await Send(TerminalFrame.MakeExit(output.Result ?? 0));
            await s.Cancel.CancelAsync();
            try { await Task.WhenAll(output, input); } catch (Exception e) when (e is OperationCanceledException or WebSocketException or IOException) { }
        }
        catch (TerminalConnectException e) { s.CloseReason ??= e.Message; await audit.Log(s.UserName, $"{Area(s)}.connect_failed", $"{s.Label}: {e.Audit}", s.MachineId); }
        catch (OperationCanceledException) { s.CloseReason ??= "Forbindelsen blev afbrudt."; }
        catch (Exception e) when (e is WebSocketException or IOException) { s.CloseReason ??= "Forbindelsen blev afbrudt."; }
        finally
        {
            Remove(s);
            s.AgentDone.TrySetResult();
            if (s.CloseReason is { } why) try { await Send(TerminalFrame.MakeStatus(why)); } catch (Exception e) when (e is WebSocketException or ObjectDisposedException) { }
            await audit.Log(s.UserName, $"{Area(s)}.disconnect", $"{s.Label} · {(int)(Now - started).TotalSeconds} s{(s.CloseReason is { } r ? " · " + r : "")}", s.MachineId);
            try { if (browser.State == WebSocketState.Open) await browser.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); }
            catch (Exception e) when (e is WebSocketException or ObjectDisposedException) { }
            s.Cancel.Dispose();
        }
    }

    /// <summary>Første led i revisionslogen: terminal eller livelog.</summary>
    private static string Area(TerminalSession s) => s.IsLiveLog ? "livelog" : "terminal";

    private async Task ReadBrowser(TerminalSession s, WebSocket browser, ITerminalBackend backend, CancellationToken ct)
    {
        var buf = new byte[TerminalPolicy.MaxFrame + 16];
        while (browser.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var got = 0; ValueWebSocketReceiveResult r;
            do
            {
                if (got >= buf.Length) { Close(s, "For stor besked fra browseren."); return; }
                r = await browser.ReceiveAsync(buf.AsMemory(got), ct);
                got += r.Count;
            } while (!r.EndOfMessage && r.MessageType != WebSocketMessageType.Close);
            if (r.MessageType == WebSocketMessageType.Close || got == 0) return;
            if (s.IsLiveLog) { s.LastInput = Now; continue; }      // live-log: siden sender kun livstegn, intet går videre til maskinen
            if (buf[0] == TerminalFrame.Data)
            {
                s.LastInput = Now; gate.Touch(s.UnlockToken);
                await s.Input(buf.AsMemory(1, got - 1), ct);
            }
            else if (TerminalFrame.TryResize(buf.AsSpan(0, got), out var c, out var rows)) await backend.Resize(c, rows, ct);
        }
    }
}

/// <summary>Forbindelsen kunne ikke oprettes. <see cref="Message"/> vises til brugeren; <see cref="Audit"/> skrives i revisionsloggen.</summary>
public sealed class TerminalConnectException(string message, string? audit = null) : Exception(message)
{
    public string Audit { get; } = audit ?? message;
}

/// <summary>Maskinen nås gennem agentens egen forbindelse til serveren.</summary>
public sealed class AgentTerminalBackend(TerminalSession session) : ITerminalBackend
{
    private WebSocket? _ws;
    private readonly SemaphoreSlim _send = new(1, 1);

    public async Task Attach(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TerminalBroker.ConnectWindow);
        try { _ws = await session.AgentSocket.Task.WaitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TerminalConnectException("Agenten svarede ikke. Er maskinen tændt?", "agenten svarede ikke"); }
    }

    private async Task Send(byte[] frame, CancellationToken ct)
    {
        await _send.WaitAsync(ct);
        try { await _ws!.SendAsync(frame, WebSocketMessageType.Binary, true, ct); } finally { _send.Release(); }
    }

    public Task Write(ReadOnlyMemory<byte> data, CancellationToken ct) => Send(TerminalFrame.MakeData(data.Span), ct);
    public Task Resize(int cols, int rows, CancellationToken ct) => Send(TerminalFrame.MakeResize(cols, rows), ct);

    public async Task<int?> Pump(Func<ReadOnlyMemory<byte>, Task> output, CancellationToken ct)
    {
        var buf = new byte[TerminalPolicy.MaxFrame + 16];
        while (_ws!.State == WebSocketState.Open)
        {
            var got = 0; ValueWebSocketReceiveResult r;
            do
            {
                if (got >= buf.Length) return null;
                r = await _ws.ReceiveAsync(buf.AsMemory(got), ct);
                got += r.Count;
            } while (!r.EndOfMessage && r.MessageType != WebSocketMessageType.Close);
            if (r.MessageType == WebSocketMessageType.Close || got == 0) return null;
            if (buf[0] == TerminalFrame.Data) await output(buf.AsMemory(1, got - 1));
            else if (buf[0] == TerminalFrame.Exit && got == 5) return System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(buf.AsSpan(1, 4));
        }
        return null;
    }

    public ValueTask DisposeAsync() { session.AgentDone.TrySetResult(); return ValueTask.CompletedTask; }
}
