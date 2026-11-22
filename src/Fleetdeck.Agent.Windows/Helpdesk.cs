using System.Text.Json;
using System.Text.RegularExpressions;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Windows;

/// <summary>
/// Beskeder (helpdesk), tjenestens side: nye beskeder fra serveren skrives til userwork\helpdesk.json, som ikonet i
/// proceslinjen viser for brugeren. Brugerens svar kommer som ordrer fra ikonet (helpdesk_reply/helpdesk_read) og
/// sendes til serveren. Kan de ikke sendes (ingen forbindelse), gemmes de i work\ og sendes ved næste check-in.
/// Serveren afgør selv, om en svarknap må bruges – agenten melder kun brugerens valg.
/// </summary>
public sealed partial class HelpdeskBridge(AgentOptions opt, ILogger<HelpdeskBridge> log)
{
    public string InboxFile => Path.Combine(opt.UserWork, "helpdesk.json");
    private string OutboxFile => Path.Combine(opt.Work, "helpdesk-out.json");
    private string UserCmdDir => Path.Combine(opt.UserWork, "cmd");
    private readonly Lock _lock = new();
    /// <summary>Den indloggede bruger (kan skiftes i test).</summary>
    public Func<string?> CurrentUser { get; set; } = Native.ConsoleUser;
    /// <summary>Samler logpakken ("Send logfiler"); sættes af tjenesten (og i test).</summary>
    public Func<CancellationToken, Task<byte[]>>? CollectLogs { get; set; }
    private DateTime _lastLogs = DateTime.MinValue;
    /// <summary>Højst én logpakke fra brugeren pr. minut (et barn, der trykker løs, fylder ikke databasen).</summary>
    public static readonly TimeSpan LogsEvery = TimeSpan.FromMinutes(1);

    [GeneratedRegex("^[a-f0-9]{32}\\.(jpg|png)$")] private static partial Regex ShotName();

    public HelpdeskInbox Load()
    {
        try { if (File.Exists(InboxFile)) return JsonSerializer.Deserialize<HelpdeskInbox>(File.ReadAllText(InboxFile), TrayBridge.Json) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { log.LogDebug("helpdesk.json: {Msg}", e.Message); }
        return new();
    }

    private void Save(HelpdeskInbox inbox)
    {
        try
        {
            inbox.Written = DateTime.UtcNow;
            Directory.CreateDirectory(opt.UserWork);
            var tmp = InboxFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(inbox, TrayBridge.Json));
            File.Move(tmp, InboxFile, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.LogWarning("Beskeder kunne ikke gemmes: {Msg}", e.Message); }
    }

    /// <summary>Nye beskeder fra check-in-svaret: læg dem i samtalen og kvittér (serveren sender dem igen, til vi har kvitteret).</summary>
    public async Task Deliver(ServerClient api, List<HelpdeskNote> notes, CancellationToken ct)
    {
        if (notes.Count == 0) return;
        List<int> added;
        lock (_lock) { var inbox = Load(); added = inbox.Merge(notes); Save(inbox); }
        if (added.Count > 0) log.LogInformation("{N} ny(e) besked(er) fra {From}", added.Count, notes[0].From);
        try { await api.Post("/api/agent/helpdesk/ack", new HelpdeskAck { Delivered = notes.Select(n => n.Id).ToList() }, ct); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { log.LogDebug("Kvittering for beskeder: {Msg}", e.Message); }
    }

    /// <summary>En ordre fra ikonet: brugeren har set beskeder eller svaret.</summary>
    public async Task Handle(ServerClient api, TrayCommand c, CancellationToken ct)
    {
        if (c.Action == "helpdesk_read")
        {
            List<int> read;
            lock (_lock) { var inbox = Load(); read = inbox.MarkRead(c.Ids); Save(inbox); }
            try { if (read.Count > 0) await api.Post("/api/agent/helpdesk/ack", new HelpdeskAck { Read = read }, ct); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { log.LogDebug("Kvittering (læst): {Msg}", e.Message); }
            return;
        }
        if (c.Action is not ("helpdesk_reply" or "helpdesk_logs")) return;
        var shot = TakeScreenshot(c.File);
        var text = HelpdeskText.Clean(c.Text);
        var user = CurrentUser();
        byte[]? logs = null;
        if (c.Action == "helpdesk_logs")
        {
            if (CollectLogs is null || DateTime.UtcNow - _lastLogs < LogsEvery) { log.LogInformation("Logfiler sprunget over (sendt for nylig)"); if (text.Length == 0 && shot is null) return; }
            else
            {
                _lastLogs = DateTime.UtcNow;
                try { logs = await CollectLogs(ct); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { log.LogWarning("Logfiler kunne ikke samles: {Msg}", e.Message); }
            }
        }
        HelpdeskReply reply;
        lock (_lock)
        {
            var inbox = Load();
            var button = c.ReplyTo is int rid && c.Button is int b && inbox.CanAnswer(rid, b) ? b : (int?)null;
            if (text.Length == 0 && button is null && shot is null && logs is null) return;
            inbox.AddMine(user ?? "", text, c.ReplyTo, button, shot is not null, DateTime.UtcNow, logs is not null);
            if (c.ReplyTo is int r2) inbox.MarkRead([r2]);
            Save(inbox);
            reply = new HelpdeskReply
            {
                ReplyTo = c.ReplyTo, Text = text, Button = button, User = user, Screenshot = shot is null ? null : Convert.ToBase64String(shot),
                Logs = logs is null ? null : Convert.ToBase64String(logs),
            };
        }
        Queue(reply);
        await Flush(api, ct);
    }

    /// <summary>Skærmbilledet, ikonet har lagt i brugermappen: kun vores egne filnavne, ingen genveje, og det skal være et billede.</summary>
    private byte[]? TakeScreenshot(string? name)
    {
        if (name is null) return null;
        var path = Path.Combine(UserCmdDir, Path.GetFileName(name));
        try
        {
            if (!ShotName().IsMatch(Path.GetFileName(name)) || !File.Exists(path)) return null;
            var fi = new FileInfo(path);
            if (fi.Attributes.HasFlag(FileAttributes.ReparsePoint) || fi.Length > HelpdeskText.MaxScreenshotBytes) return null;
            var data = File.ReadAllBytes(path);
            return HelpdeskText.ImageType(data) is null ? null : data;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.LogDebug("Skærmbillede: {Msg}", e.Message); return null; }
        finally { try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    }

    private void Queue(HelpdeskReply r)
    {
        lock (_lock)
        {
            var list = LoadOutbox();
            // kun den nyeste logpakke venter (de fylder op til 5 MB); teksten i de ældre svar sendes stadig
            if (r.Logs is not null) foreach (var o in list) o.Logs = null;
            list.Add(r);
            if (list.Count > 20) list.RemoveRange(0, list.Count - 20);
            SaveOutbox(list);
        }
    }

    /// <summary>Send ventende svar (efter et svar fra brugeren og ved hvert check-in).</summary>
    public async Task Flush(ServerClient api, CancellationToken ct)
    {
        List<HelpdeskReply> list;
        lock (_lock) list = LoadOutbox();
        if (list.Count == 0) return;
        var sent = 0;
        foreach (var r in list)
        {
            try { await api.Post("/api/agent/helpdesk/reply", r, ct); sent++; }
            catch (HttpRequestException e) when (e.StatusCode is System.Net.HttpStatusCode.BadRequest) { sent++; log.LogWarning("Serveren afviste et svar: {Msg}", e.Message); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { log.LogInformation("Svaret sendes senere: {Msg}", e.Message); break; }
        }
        lock (_lock)
        {
            var now = LoadOutbox();
            now.RemoveRange(0, Math.Min(sent, now.Count));
            SaveOutbox(now);
            var inbox = Load();
            if (now.Count == 0 && inbox.Lines.Any(l => l.Pending)) { foreach (var l in inbox.Lines) l.Pending = false; Save(inbox); }
        }
        if (sent > 0) log.LogInformation("{N} svar sendt til support", sent);
    }

    private List<HelpdeskReply> LoadOutbox()
    {
        try { if (File.Exists(OutboxFile)) return JsonSerializer.Deserialize<List<HelpdeskReply>>(File.ReadAllText(OutboxFile), TrayBridge.Json) ?? []; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return [];
    }

    private void SaveOutbox(List<HelpdeskReply> list)
    {
        try
        {
            Directory.CreateDirectory(opt.Work);
            if (list.Count == 0) { File.Delete(OutboxFile); return; }
            File.WriteAllText(OutboxFile, JsonSerializer.Serialize(list, TrayBridge.Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.LogWarning("Svar kunne ikke gemmes: {Msg}", e.Message); }
    }
}
