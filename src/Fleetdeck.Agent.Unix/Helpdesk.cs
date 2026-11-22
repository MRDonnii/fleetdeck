using System.Text.Json;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Unix;

/// <summary>
/// Beskeder (helpdesk) på Mac og Linux – samme server-API som Windows (ack, reply med tekst, svarknap og logfiler).
/// Mac: samtalen skrives til /Library/Application Support/Fleetdeck/helpdesk.json, og Fleetdeck.app åbnes i brugerens
/// session med et svarvindue; svarene kommer som ordrer i appens ordremappe (helpdesk_reply/helpdesk_read/helpdesk_logs).
/// Linux: et svarvindue i brugerens grafiske session med yad, zenity eller kdialog. Findes ingen af dem, vises beskeden
/// som notifikation som før. Svar, der ikke kan sendes (ingen forbindelse), gemmes og sendes ved næste check-in.
/// </summary>
public static class HelpdeskUnix
{
    private static readonly Lock L = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string InboxFile => Mac.Is ? MacPaths.HelpdeskFile : Path.Combine(Kit.Work, "helpdesk.json");
    private static string OutboxFile => Path.Combine(Mac.Is ? MacPaths.AgentDir : Kit.Work, "helpdesk-out.json");
    private static string AppExe => Path.Combine(MacPaths.InstalledApp, MacPaths.AppExecutable);
    private static DateTime _lastLogs = DateTime.MinValue;
    /// <summary>1, mens et svarvindue er åbent på Linux (kun ét ad gangen; nye beskeder vises, når det lukkes).</summary>
    private static int _dialog;

    /// <summary>Nye beskeder fra check-in-svaret: læg dem i samtalen, vis dem og kvittér. Derefter sendes ventende svar.</summary>
    public static async Task Deliver(Api api, List<HelpdeskNote> notes, CancellationToken ct)
    {
        if (notes.Count > 0)
        {
            List<int> added;
            lock (L) { var inbox = Load(); added = inbox.Merge(notes); Save(inbox); }
            if (added.Count > 0) Log.Info($"{added.Count} ny(e) besked(er) fra {notes[0].From}");
            // ingen bruger på skærmen: ingen kvittering – serveren sender beskeden igen, og den vises, når nogen er logget ind
            if (Show(api, notes, added.Count > 0, ct)) await Ack(api, new HelpdeskAck { Delivered = notes.Select(n => n.Id).ToList() }, ct);
        }
        await Flush(api, ct);
    }

    /// <summary>Vis beskederne for den, der er logget ind. Falsk, når ingen er på skærmen (eller intet kunne vises).</summary>
    private static bool Show(Api api, List<HelpdeskNote> notes, bool fresh, CancellationToken ct)
    {
        if (Mac.Is)
        {
            if (Mac.Session() is not { } ms) return false;
            if (!fresh) return true;   // allerede vist (kvitteringen nåede ikke frem)
            if (File.Exists(AppExe))
                return Shell.Run($"launchctl asuser {ms.Uid} sudo -u {Pkg.Q(ms.User)} {Pkg.Q(AppExe)} --messages >/dev/null 2>&1 &", TimeSpan.FromSeconds(15)).ExitCode == 0;
            return Notify(notes);   // appen er ikke installeret: som før
        }
        if (UserSession.Find() is not { } s) return false;
        var tool = LinuxReplyDialog.Pick(Shell.Has);
        if (tool == LinuxReplyDialog.Tool.None) return !fresh || Notify(notes);
        if (fresh || Load().Unread.Any()) StartDialog(api, s, tool, ct);
        return true;
    }

    /// <summary>Uden svarvindue: beskeden som notifikation (Mac: dialog, Linux: notify-send og wall).</summary>
    private static bool Notify(List<HelpdeskNote> notes)
    {
        var shown = false;
        foreach (var n in notes.OrderBy(n => n.At))
        {
            var text = $"{n.From}: {HelpdeskText.Clean(n.Text)}";
            var buttons = HelpdeskText.Buttons(n.Buttons);
            if (buttons.Count > 0) text += $"\n(Svar: {string.Join(" / ", buttons.Select(b => b.Label))} – " +
                (Mac.Is ? "svar fra Fleetdeck i menulinjen.)" : "for at svare herfra skal yad, zenity eller kdialog være installeret.)");
            // Linux-notifikationer forstår lidt HTML (fx <a>): vis tegnene som tekst
            if (!Mac.Is) text = LinuxReplyDialog.EscapeMarkup(text);
            shown |= JobRunner.Notify(text);
        }
        return shown;
    }

    private static void StartDialog(Api api, UserSession s, LinuxReplyDialog.Tool tool, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _dialog, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try { await DialogLoop(api, s, tool, ct); }
            catch (Exception e) when (e is IOException or HttpRequestException or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
            { Log.Warn($"Svarvinduet: {e.Message}"); }
            finally { Volatile.Write(ref _dialog, 0); }
        }, CancellationToken.None);
    }

    /// <summary>Linux: vis samtalen, til brugeren svarer eller lukker. Kom der nye beskeder imens, vises de bagefter.</summary>
    private static async Task DialogLoop(Api api, UserSession s, LinuxReplyDialog.Tool tool, CancellationToken ct)
    {
        for (var round = 0; round < 10 && !ct.IsCancellationRequested; round++)
        {
            HelpdeskInbox inbox; List<int> read;
            lock (L) { inbox = Load(); if (!inbox.Unread.Any()) return; read = inbox.MarkRead(); Save(inbox); }
            await Ack(api, new HelpdeskAck { Read = read }, ct);
            var answer = await Ask(s, tool, inbox);
            if (answer.Kind == HelpdeskAnswer.Failed)
            {
                // vinduet kunne ikke åbnes (fx ingen adgang til skærmen): vis i det mindste beskederne
                Log.Warn("Svarvinduet kunne ikke vises – beskeden vises som notifikation");
                Notify(inbox.Lines.Where(l => !l.Mine && read.Contains(l.Id)).Select(l => new HelpdeskNote { Id = l.Id, From = l.Who, Text = l.Text, At = l.At, Buttons = l.Buttons }).ToList());
                return;
            }
            if (answer.Kind == HelpdeskAnswer.Closed) continue;
            var replyTo = answer.Button is not null ? HelpdeskView.Ask(inbox)?.Id : HelpdeskView.LastFromSupport(inbox);
            var logs = answer.Kind == HelpdeskAnswer.Logs;
            await Handle(api, new TrayCommand { Action = logs ? "helpdesk_logs" : "helpdesk_reply", Text = answer.Text, ReplyTo = replyTo, Button = answer.Button }, s.User, ct);
            Shell.Run($"notify-send -a Fleetdeck Fleetdeck {Pkg.Q(logs ? "Logfilerne sendes til support." : "Svaret er sendt til support.")}", TimeSpan.FromSeconds(10), s.User);
        }
    }

    /// <summary>Kør dialogen i brugerens session (Wayland eller X11) og fortolk svaret.</summary>
    private static async Task<HelpdeskAnswer> Ask(UserSession s, LinuxReplyDialog.Tool tool, HelpdeskInbox inbox)
    {
        var multiline = tool != LinuxReplyDialog.Tool.Zenity || LinuxReplyDialog.ZenityMultiline(Shell.Out("zenity --version 2>/dev/null"));
        var args = LinuxReplyDialog.Args(tool, inbox, multiline);
        var menu = tool == LinuxReplyDialog.Tool.Kdialog && args.Contains("--menu");
        var wait = TimeSpan.FromSeconds(LinuxReplyDialog.TimeoutSeconds + 60);
        var started = DateTime.UtcNow;
        var r = await Task.Run(() => Shell.Run(Script(s, args), wait, s.User));
        var answer = LinuxReplyDialog.Parse(tool, r.ExitCode, r.Output, inbox, menu);
        // lukket på under et sekund: vinduet kom aldrig frem (fx ingen adgang til skærmen) – så en notifikation i stedet
        if (answer.Kind == HelpdeskAnswer.Closed && LinuxReplyDialog.NeverShown(DateTime.UtcNow - started)) return new HelpdeskAnswer(HelpdeskAnswer.Failed);
        if (answer.Kind != "text") return answer;
        r = await Task.Run(() => Shell.Run(Script(s, LinuxReplyDialog.TextArgs(inbox)), wait, s.User));
        return LinuxReplyDialog.Parse(tool, r.ExitCode, r.Output, inbox);
    }

    /// <summary>Kommandolinjen (alt citeret). Wayland bruges, når brugerens session har en Wayland-sokkel; fejltekster fra GTK/Qt kommer ikke med i svaret.</summary>
    private static string Script(UserSession s, List<string> args)
    {
        var wayland = File.Exists($"/run/user/{s.Uid}/wayland-0") ? "WAYLAND_DISPLAY=wayland-0 " : "";
        return $"{wayland}{string.Join(' ', args.Select(Pkg.Q))} 2>/dev/null";
    }

    /// <summary>Mac: en ordre fra appens ordremappe (svar, set, send logfiler).</summary>
    public static Task Handle(Api api, TrayCommand c, CancellationToken ct) => Handle(api, c, Mac.Is ? Mac.Session()?.User : UserSession.Find()?.User, ct);

    /// <summary>Brugeren har set beskeder eller svaret: skriv det i samtalen og send det (som tjenesten i Windows).</summary>
    public static async Task Handle(Api api, TrayCommand c, string? user, CancellationToken ct)
    {
        if (c.Action == "helpdesk_read")
        {
            List<int> read;
            lock (L) { var inbox = Load(); read = inbox.MarkRead(c.Ids); Save(inbox); }
            await Ack(api, new HelpdeskAck { Read = read }, ct);
            return;
        }
        if (c.Action is not ("helpdesk_reply" or "helpdesk_logs")) return;
        var text = HelpdeskText.Clean(c.Text);
        byte[]? logs = null;
        if (c.Action == "helpdesk_logs")
        {
            // højst én logpakke pr. minut
            if (DateTime.UtcNow - _lastLogs < TimeSpan.FromMinutes(1)) { Log.Info("Logfiler sprunget over (sendt for nylig)"); if (text.Length == 0) return; }
            else
            {
                _lastLogs = DateTime.UtcNow;
                try { logs = await Task.Run(() => UnixLogs.Build("sendt af brugeren"), ct); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { Log.Warn($"Logfiler kunne ikke samles: {e.Message}"); }
            }
        }
        HelpdeskReply reply;
        lock (L)
        {
            var inbox = Load();
            var button = c.ReplyTo is int rid && c.Button is int b && inbox.CanAnswer(rid, b) ? b : (int?)null;
            if (text.Length == 0 && button is null && logs is null) return;
            inbox.AddMine(user ?? "", text, c.ReplyTo, button, false, DateTime.UtcNow, logs is not null);
            if (c.ReplyTo is int r2) inbox.MarkRead([r2]);
            Save(inbox);
            reply = new HelpdeskReply { ReplyTo = c.ReplyTo, Text = text, Button = button, User = user, Logs = logs is null ? null : Convert.ToBase64String(logs) };
        }
        lock (L)
        {
            var list = LoadOutbox();
            if (reply.Logs is not null) foreach (var o in list) o.Logs = null;   // kun den nyeste logpakke venter
            list.Add(reply);
            if (list.Count > 20) list.RemoveRange(0, list.Count - 20);
            SaveOutbox(list);
        }
        await Flush(api, ct);
    }

    /// <summary>Send ventende svar (efter et svar og ved hvert check-in).</summary>
    public static async Task Flush(Api api, CancellationToken ct)
    {
        List<HelpdeskReply> list;
        lock (L) list = LoadOutbox();
        if (list.Count == 0) return;
        var sent = 0;
        foreach (var r in list)
        {
            try { await api.Post("/api/agent/helpdesk/reply", r, ct); sent++; }
            catch (HttpRequestException e) when (e.StatusCode is System.Net.HttpStatusCode.BadRequest) { sent++; Log.Warn($"Serveren afviste et svar: {e.Message}"); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { Log.Info($"Svaret sendes senere: {e.Message}"); break; }
        }
        lock (L)
        {
            var now = LoadOutbox();
            now.RemoveRange(0, Math.Min(sent, now.Count));
            SaveOutbox(now);
            var inbox = Load();
            if (now.Count == 0 && inbox.Lines.Any(l => l.Pending)) { foreach (var l in inbox.Lines) l.Pending = false; Save(inbox); }
        }
        if (sent > 0) Log.Info($"{sent} svar sendt til support");
    }

    private static async Task Ack(Api api, HelpdeskAck ack, CancellationToken ct)
    {
        if (ack.Delivered.Count == 0 && ack.Read.Count == 0) return;
        try { await api.Post("/api/agent/helpdesk/ack", ack, ct); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { Log.Warn($"Kvittering for beskeder: {e.Message}"); }
    }

    private static HelpdeskInbox Load()
    {
        try { if (File.Exists(InboxFile)) return JsonSerializer.Deserialize<HelpdeskInbox>(File.ReadAllText(InboxFile), Json) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return new();
    }

    /// <summary>Samtalen skrives helt eller slet ikke (.tmp og omdøb). På Mac kan alle læse den (appen kører som brugeren).</summary>
    private static void Save(HelpdeskInbox inbox)
    {
        try
        {
            inbox.Written = DateTime.UtcNow;
            Directory.CreateDirectory(Path.GetDirectoryName(InboxFile)!);
            var tmp = InboxFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(inbox, Json));
            File.SetUnixFileMode(tmp, Mac.Is ? (UnixFileMode)0b110_100_100 : UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(tmp, InboxFile, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warn($"Beskeder kunne ikke gemmes: {e.Message}"); }
    }

    private static List<HelpdeskReply> LoadOutbox()
    {
        try { if (File.Exists(OutboxFile)) return JsonSerializer.Deserialize<List<HelpdeskReply>>(File.ReadAllText(OutboxFile), Json) ?? []; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return [];
    }

    private static void SaveOutbox(List<HelpdeskReply> list)
    {
        try
        {
            if (list.Count == 0) { File.Delete(OutboxFile); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(OutboxFile)!);
            File.WriteAllText(OutboxFile, JsonSerializer.Serialize(list, Json));
            File.SetUnixFileMode(OutboxFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warn($"Svar kunne ikke gemmes: {e.Message}"); }
    }
}
