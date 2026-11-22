using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Shared;

namespace Fleetdeck.Server.Services;

/// <summary>Hvem en besked skal til: bestemte maskiner, alle med et mærke eller alle på en profil.</summary>
public sealed class HelpdeskTarget
{
    public List<int>? MachineIds { get; set; }
    public string? Tag { get; set; }
    public int? ProfileId { get; set; }
}

/// <summary>En fast besked, man kan vælge i stedet for at skrive den hver gang.</summary>
public sealed class HelpdeskTemplate
{
    public string Name { get; set; } = "";
    public string Text { get; set; } = "";
    public List<HelpdeskButton> Buttons { get; set; } = [];
}

public sealed class HelpdeskSettings
{
    /// <summary>Navnet, brugeren ser som afsender (fx "IT-support").</summary>
    public string Sender { get; set; } = HelpdeskRules.DefaultSender;
    /// <summary>Et nyt svar giver en notifikation som andre alarmer (Home Assistant/webhook, hvis det er sat op).</summary>
    public bool Notify { get; set; } = true;
    public List<HelpdeskTemplate> Templates { get; set; } = [];
}

/// <summary>Ren logik til Beskeder (testes uden database).</summary>
public static class HelpdeskRules
{
    public const string DefaultSender = "IT-support";

    /// <summary>Skabelonerne, en ny installation starter med (kan rettes og slettes).</summary>
    public static List<HelpdeskTemplate> DefaultTemplates() =>
    [
        new() { Name = "Genstart venligst", Text = "Pc'en skal genstartes for at blive færdig med en opdatering. Gem dit arbejde. Må den genstarte nu?",
            Buttons = [new() { Label = "Ja, genstart nu", Action = HelpdeskActions.Reboot }, new() { Label = "Senere" }] },
        new() { Name = "Jeg kigger på det nu", Text = "Jeg kigger på det nu.", Buttons = [new() { Label = "OK" }] },
        new() { Name = "Er det løst?", Text = "Virker det nu?", Buttons = [new() { Label = "Ja" }, new() { Label = "Nej" }] },
    ];

    /// <summary>Maskinerne, beskeden skal til (uden dubletter, i id-orden).</summary>
    public static List<int> Resolve(IEnumerable<(int Id, IReadOnlyCollection<string> Tags, int? ProfileId)> machines, HelpdeskTarget t)
    {
        var list = machines.ToList();
        var ids = new HashSet<int>();
        if (t.MachineIds is { Count: > 0 } want) foreach (var m in list.Where(m => want.Contains(m.Id))) ids.Add(m.Id);
        if (t.Tag is { Length: > 0 } tag) foreach (var m in list.Where(m => m.Tags.Contains(tag.Trim(), StringComparer.OrdinalIgnoreCase))) ids.Add(m.Id);
        if (t.ProfileId is int pid) foreach (var m in list.Where(m => m.ProfileId == pid)) ids.Add(m.Id);
        return ids.Order().ToList();
    }

    /// <summary>Beskeder, der venter på at blive leveret til maskinen (ældste først, højst 20 ad gangen).</summary>
    public static List<HelpdeskMessage> Pending(IEnumerable<HelpdeskMessage> msgs) =>
        msgs.Where(m => !m.FromMachine && m.DeliveredAt is null).OrderBy(m => m.Created).ThenBy(m => m.Id).Take(20).ToList();

    /// <summary>Svar fra maskinen, ingen administrator har læst endnu.</summary>
    public static int Unread(IEnumerable<HelpdeskMessage> msgs) => msgs.Count(m => m.FromMachine && m.ReadAt is null);

    public static HelpdeskNote ToNote(HelpdeskMessage m, string sender) => new()
    {
        Id = m.Id, From = sender, Text = m.Text, At = m.Created, Buttons = Json.Des<List<HelpdeskButton>>(m.ButtonsJson),
    };

    /// <summary>
    /// Brugeren trykkede på en svarknap: gælder kun beskeder til netop denne maskine, kun én gang og kun beskedens egne knapper.
    /// Giver knappen (og dermed handlingen), eller årsagen til, at valget afvises.
    /// </summary>
    public static (HelpdeskButton? Button, string? Why) Answer(HelpdeskMessage? target, int machineId, int? button)
    {
        if (button is not int b) return (null, null);
        if (target is null || target.MachineId != machineId || target.FromMachine) return (null, "beskeden findes ikke på denne maskine");
        if (target.Answer is not null) return (null, "beskeden er allerede besvaret");
        var buttons = Json.Des<List<HelpdeskButton>>(target.ButtonsJson);
        if (b < 0 || b >= buttons.Count) return (null, "ukendt svarknap");
        return (buttons[b], null);
    }

    /// <summary>Grundoplysningerne, der sendes med en ny sag: navn, OS, oppetid, bruger og seneste fejl.</summary>
    public static string Info(string name, SystemFacts f, LiveFacts live, string? user, IEnumerable<string> alerts)
    {
        var parts = new List<string> { name };
        var os = string.Join(' ', new[] { f.Os, f.OsVersion }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (os.Length > 0) parts.Add(os);
        if (live.UptimeH is double up && up >= 0) parts.Add($"oppe i {Uptime(up)}");
        if (!string.IsNullOrWhiteSpace(user)) parts.Add($"bruger {user}");
        var line = string.Join(" · ", parts);
        var errs = alerts.Where(a => a.Length > 0).Take(3).ToList();
        var res = errs.Count > 0 ? $"{line}\nSeneste fejl: {string.Join("; ", errs)}" : $"{line}\nIngen åbne fejl.";
        return res.Length > 1000 ? res[..1000] : res;
    }

    public static string Uptime(double hours) => hours switch
    {
        < 1 => $"{Math.Max(1, (int)Math.Round(hours * 60))} min.",
        < 48 => $"{(int)Math.Round(hours)} timer",
        _ => $"{(int)Math.Round(hours / 24)} dage",
    };

    public static HelpdeskSettings Settings(string sender, string notify, string templatesJson)
    {
        var t = string.IsNullOrWhiteSpace(templatesJson) ? DefaultTemplates() : Json.Des<List<HelpdeskTemplate>>(templatesJson);
        return new() { Sender = Sender(sender), Notify = notify != "0", Templates = Templates(t) };
    }

    /// <summary>Så mange logpakker gemmes pr. maskine (de ældste slettes).</summary>
    public const int KeepLogs = 10;

    public static bool CanCollectLogs(string? platform) => platform is Platforms.Windows or Platforms.Mac or Platforms.Linux;

    /// <summary>Filnavnet, admin får ved download: logfiler-ARBEJDS-PC-20261119-1402.zip (kun sikre tegn).</summary>
    public static string LogsName(string machine, DateTime atUtc)
    {
        var safe = new string((machine ?? "").Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray()).Trim('-');
        if (safe.Length == 0) safe = "maskine";
        if (safe.Length > 40) safe = safe[..40];
        return $"logfiler-{safe}-{atUtc:yyyyMMdd-HHmm}.zip";
    }

    /// <summary>Logpakken fra agenten: base64, en zip under 5 MB med kun tekstfiler i roden. Ellers en fejltekst.</summary>
    public static (byte[]? Zip, List<LogEntry>? Entries, string? Error) ReadLogs(string? b64)
    {
        if (string.IsNullOrEmpty(b64)) return (null, null, "Logfilerne mangler.");
        if (b64.Length > (LogBundle.MaxBytes / 3 + 2) * 4) return (null, null, "Logfilerne er for store (højst 5 MB).");
        byte[] zip;
        try { zip = Convert.FromBase64String(b64); } catch (FormatException) { return (null, null, "Logfilerne kunne ikke læses."); }
        if (!LogBundle.IsZip(zip)) return (null, null, "Logfilerne er ikke en zip eller er for store (højst 5 MB).");
        var entries = LogBundle.Entries(zip);
        return entries is null ? (null, null, "Logfilerne må kun indeholde tekstfiler.") : (zip, entries, null);
    }

    public static string Sender(string? s) => HelpdeskText.Preview(s, 40) is { Length: > 0 } v ? v : DefaultSender;

    public static List<HelpdeskTemplate> Templates(IEnumerable<HelpdeskTemplate>? list) =>
        (list ?? []).Select(t => new HelpdeskTemplate { Name = HelpdeskText.Preview(t.Name, 60), Text = HelpdeskText.Clean(t.Text), Buttons = HelpdeskText.Buttons(t.Buttons) })
            .Where(t => t.Name.Length > 0 && t.Text.Length > 0).Take(30).ToList();
}

/// <summary>Beskeder til maskinerne og svar tilbage (helpdesk). Kun administratorer skriver; brugeren svarer via agenten.</summary>
public sealed class HelpdeskService(AppDb db, SettingsService s, Live live, Notifier notifier, AuditService audit, JobService jobs)
{
    public async Task<HelpdeskSettings> GetSettings() =>
        HelpdeskRules.Settings(await s.Get("helpdesk_sender"), await s.Get("helpdesk_notify"), await s.Get("helpdesk_templates"));

    public async Task SaveSettings(HelpdeskSettings v)
    {
        await s.Set("helpdesk_sender", HelpdeskRules.Sender(v.Sender));
        await s.Set("helpdesk_notify", v.Notify ? "1" : "0");
        await s.Set("helpdesk_templates", Json.Ser(HelpdeskRules.Templates(v.Templates)));
    }

    /// <summary>Beskeder, agenten skal have i check-in-svaret.</summary>
    public static async Task<List<HelpdeskNote>> PendingFor(AppDb db, SettingsService s, int machineId)
    {
        var msgs = await db.HelpdeskMessages.AsNoTracking().Where(m => m.MachineId == machineId && !m.FromMachine && m.DeliveredAt == null).ToListAsync();
        if (msgs.Count == 0) return [];
        var sender = HelpdeskRules.Sender(await s.Get("helpdesk_sender"));
        return HelpdeskRules.Pending(msgs).Select(m => HelpdeskRules.ToNote(m, sender)).ToList();
    }

    private async Task<HelpdeskConversation> Conversation(int machineId)
    {
        var c = await db.HelpdeskConversations.FirstOrDefaultAsync(x => x.MachineId == machineId);
        if (c is null) { c = new HelpdeskConversation { MachineId = machineId }; db.HelpdeskConversations.Add(c); await db.SaveChangesAsync(); }
        return c;
    }

    private static void Touch(HelpdeskConversation c, string text, bool fromMachine)
    {
        c.LastActivity = DateTime.UtcNow; c.Preview = HelpdeskText.Preview(text, 200); c.LastFromMachine = fromMachine;
        if (c.Status != HelpdeskStatus.Open) { c.Status = HelpdeskStatus.Open; c.ClosedAt = null; c.ClosedBy = null; }
    }

    /// <summary>Send en besked til maskiner (pr. id, mærke eller profil). Giver de maskiner, den blev lagt i kø til.</summary>
    public async Task<List<int>> Send(HelpdeskTarget target, string text, IEnumerable<HelpdeskButton>? buttons, string by)
    {
        var clean = HelpdeskText.Clean(text);
        var btns = HelpdeskText.Buttons(buttons);
        if (clean.Length == 0) throw new ArgumentException("Skriv en besked.");
        var all = (await db.Machines.AsNoTracking().Where(m => m.ReplacesId == null).Select(m => new { m.Id, m.Tags, m.ProfileId }).ToListAsync())
            .Select(m => (m.Id, (IReadOnlyCollection<string>)m.Tags, m.ProfileId));
        var ids = HelpdeskRules.Resolve(all, target);
        if (ids.Count == 0) throw new ArgumentException("Ingen maskiner passer.");
        foreach (var id in ids)
        {
            var c = await Conversation(id);
            Touch(c, clean, false);
            db.HelpdeskMessages.Add(new HelpdeskMessage { ConversationId = c.Id, MachineId = id, Author = by, Text = clean, ButtonsJson = Json.Ser(btns) });
        }
        await db.SaveChangesAsync();
        var names = await db.Machines.AsNoTracking().Where(m => ids.Contains(m.Id)).Select(m => m.DisplayName ?? m.Name).ToListAsync();
        await audit.Log(by, "helpdesk.send", $"{(names.Count > 3 ? $"{names.Count} maskiner" : string.Join(", ", names))}: {HelpdeskText.Preview(clean, 200)}");
        foreach (var id in ids) await live.Wake(id);   // online maskiner får beskeden med det samme; de andre, når de kommer online
        await live.Changed("helpdesk");
        return ids;
    }

    /// <summary>Agenten har modtaget beskeder / brugeren har set dem. Kun beskeder til netop denne maskine.</summary>
    public async Task Ack(Machine m, HelpdeskAck a)
    {
        var now = DateTime.UtcNow;
        var ids = a.Delivered.Concat(a.Read).Distinct().Take(200).ToList();
        if (ids.Count == 0) return;
        var rows = await db.HelpdeskMessages.Where(x => x.MachineId == m.Id && !x.FromMachine && ids.Contains(x.Id)).ToListAsync();
        foreach (var r in rows)
        {
            r.DeliveredAt ??= now;
            if (a.Read.Contains(r.Id)) r.ReadAt ??= now;
        }
        await db.SaveChangesAsync();
        if (rows.Count > 0) await live.Changed("helpdesk", m.Id);
    }

    /// <summary>Et svar eller en ny sag fra brugeren ved maskinen.</summary>
    public async Task<(HelpdeskMessage? Msg, string? Error)> FromMachine(Machine m, HelpdeskReply r)
    {
        var text = HelpdeskText.Clean(r.Text);
        byte[]? shot = null; string? type = null;
        if (r.Screenshot is { Length: > 0 } b64)
        {
            try { shot = Convert.FromBase64String(b64); } catch (FormatException) { return (null, "Skærmbilledet kunne ikke læses."); }
            type = HelpdeskText.ImageType(shot);
            if (type is null) return (null, "Skærmbilledet er ikke et billede eller er for stort.");
        }
        byte[]? logs = null; List<LogEntry>? entries = null;
        if (r.Logs is { Length: > 0 } lb64)
        {
            (logs, entries, var bad) = HelpdeskRules.ReadLogs(lb64);
            if (bad is not null) return (null, bad);
        }
        var target = r.ReplyTo is int rid ? await db.HelpdeskMessages.FirstOrDefaultAsync(x => x.Id == rid) : null;
        if (r.ReplyTo is not null && (target is null || target.MachineId != m.Id)) target = null;   // svar på en ukendt/fremmed besked bliver en ny sag
        var (button, why) = HelpdeskRules.Answer(target, m.Id, r.Button);
        if (why is not null)
        {
            // en svarknap, der ikke kan bruges (fx allerede besvaret), må ikke tage brugerens tekst med i faldet: knappen droppes
            if (text.Length == 0 && shot is null && logs is null) return (null, $"Svaret kan ikke bruges: {why}.");
            button = null;
        }
        if (button is not null && text.Length == 0) text = button.Label;
        if (text.Length == 0 && shot is null && logs is null) return (null, "Tomt svar.");
        var user = HelpdeskText.Preview(r.User, 64);
        var c = await Conversation(m.Id);
        var isNew = c.Status == HelpdeskStatus.Closed || !await db.HelpdeskMessages.AnyAsync(x => x.ConversationId == c.Id);
        var info = "";
        // en ny sag fra brugeren (ikke et svar, og ikke en fortsættelse af det, brugeren lige har skrevet)
        var recent = DateTime.UtcNow.AddHours(-1);
        if (target is null && (isNew || !await db.HelpdeskMessages.AnyAsync(x => x.ConversationId == c.Id && x.FromMachine && x.Created > recent)))
        {
            // en ny sag fra brugeren: maskinens grundoplysninger med, så der ikke skal spørges om dem
            var alerts = await db.Alerts.AsNoTracking().Where(a => a.MachineId == m.Id && a.Resolved == null && a.Severity != "info")
                .OrderByDescending(a => a.LastSeen).Select(a => a.Title).Take(3).ToListAsync();
            info = HelpdeskRules.Info(m.Label, Json.Des<SystemFacts>(m.FactsJson), Json.Des<HealthReport>(m.HealthJson).Live, user.Length > 0 ? user : m.User, alerts);
        }
        Touch(c, text.Length > 0 ? text : shot is not null ? "Skærmbillede" : "Logfiler", true);
        var msg = new HelpdeskMessage
        {
            ConversationId = c.Id, MachineId = m.Id, FromMachine = true, Author = user.Length > 0 ? user : m.User ?? "bruger", Text = text,
            ReplyToId = target?.Id, Info = info, HasScreenshot = shot is not null, DeliveredAt = DateTime.UtcNow,
        };
        db.HelpdeskMessages.Add(msg);
        if (target is not null && button is not null) { target.Answer = r.Button; target.AnswerAt = DateTime.UtcNow; target.DeliveredAt ??= DateTime.UtcNow; target.ReadAt ??= DateTime.UtcNow; }
        await db.SaveChangesAsync();
        if (shot is not null) { db.HelpdeskScreenshots.Add(new HelpdeskScreenshot { MessageId = msg.Id, ContentType = type!, Data = shot }); await db.SaveChangesAsync(); }
        if (logs is not null) await SaveLogs(m, logs, entries!, msg.Id, null, msg.Author);
        var who = $"agent:{m.Label}";
        if (button is not null)
        {
            await audit.Log(who, "helpdesk.answer", $"{msg.Author} valgte \"{button.Label}\" kl. {DateTime.Now:HH:mm}", m.Id);
            await RunAction(m, button, msg.Author);
        }
        else await audit.Log(who, isNew ? "helpdesk.new" : "helpdesk.reply", HelpdeskText.Preview(text, 200), m.Id);
        if ((await GetSettings()).Notify)
            notifier.Send($"💬 {m.Label}: {(isNew ? "ny sag" : "nyt svar")} fra {msg.Author}\n{(text.Length > 0 ? HelpdeskText.Preview(text, 300) : shot is not null ? "Skærmbillede" : "Logfiler")}", "helpdesk");
        await live.Changed("helpdesk", m.Id);
        return (msg, null);
    }

    /// <summary>"Ja, genstart nu" / "Ja, sluk nu": samme vej som en genstart fra Fleetdeck, bare med det samme.</summary>
    private async Task RunAction(Machine m, HelpdeskButton b, string user)
    {
        var type = b.Action switch { HelpdeskActions.Reboot => JobTypes.Reboot, HelpdeskActions.Shutdown => JobTypes.Shutdown, _ => null };
        if (type is null) return;
        var verb = type == JobTypes.Reboot ? "genstarter" : "slukker";
        await jobs.Create(m.Id, type, $"{(type == JobTypes.Reboot ? "Genstart" : "Sluk")} (brugeren sagde ja i Beskeder)",
            new JobParams { Delay = 30, Message = $"Fleetdeck: pc'en {verb} nu – du sagde ja." }, RunAs.System, null, "helpdesk", $"{user} (på maskinen)");
        await audit.Log($"agent:{m.Label}", $"helpdesk.{b.Action}", $"brugeren valgte \"{b.Label}\" kl. {DateTime.Now:HH:mm} – pc'en {verb} om 30 sek.", m.Id);
    }

    /// <summary>Gem en logpakke (fra brugeren eller "Hent logfiler"). Kun de seneste <see cref="HelpdeskRules.KeepLogs"/> pr. maskine bevares.</summary>
    private async Task<HelpdeskAttachment> SaveLogs(Machine m, byte[] zip, List<LogEntry> entries, int? messageId, int? jobId, string by)
    {
        var a = new HelpdeskAttachment
        {
            MachineId = m.Id, MessageId = messageId, JobId = jobId, Kind = HelpdeskAttachmentKinds.Logs, ContentType = LogBundle.ContentType,
            Name = HelpdeskRules.LogsName(m.Label, DateTime.UtcNow), Size = zip.Length, EntriesJson = Json.Ser(entries), By = HelpdeskText.Preview(by, 64), Data = zip,
        };
        db.HelpdeskAttachments.Add(a);
        await db.SaveChangesAsync();
        var old = await db.HelpdeskAttachments.Where(x => x.MachineId == m.Id).OrderByDescending(x => x.Id).Skip(HelpdeskRules.KeepLogs).Select(x => x.Id).ToListAsync();
        if (old.Count > 0) await db.HelpdeskAttachments.Where(x => old.Contains(x.Id)).ExecuteDeleteAsync();
        return a;
    }

    /// <summary>"Hent logfiler": et job til maskinen (kun ét ad gangen). Giver jobbet, eller hvorfor det ikke kan lade sig gøre.</summary>
    public async Task<(Job? Job, string? Error)> RequestLogs(int machineId, string by)
    {
        var m = await db.Machines.AsNoTracking().FirstOrDefaultAsync(x => x.Id == machineId);
        if (m is null) return (null, "Maskinen findes ikke.");
        if (m.ReplacesId is not null) return (null, "Maskinen venter på godkendelse.");
        if (!HelpdeskRules.CanCollectLogs(m.Platform)) return (null, "Logfiler kan kun hentes fra Windows, Mac og Linux.");
        var running = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.MachineId == machineId && j.Type == JobTypes.CollectLogs && JobStatus.Active.Contains(j.Status));
        if (running is not null) return (running, null);
        var job = await jobs.Create(machineId, JobTypes.CollectLogs, "Hent logfiler", new JobParams { TimeoutMin = 10 }, RunAs.System, null, "helpdesk", by);
        await live.Changed("helpdesk", machineId);
        return (job, null);
    }

    /// <summary>Agenten sender logfilerne fra "Hent logfiler" – kun til et aktivt collect_logs-job på netop denne maskine.</summary>
    public async Task<(HelpdeskAttachment? File, string? Error)> FromJob(Machine m, LogUpload u)
    {
        var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == u.JobId);
        if (job is null || job.MachineId != m.Id || job.Type != JobTypes.CollectLogs) return (null, "Jobbet findes ikke på denne maskine.");
        if (!JobStatus.Active.Contains(job.Status)) return (null, "Jobbet er ikke i gang.");
        if (await db.HelpdeskAttachments.AnyAsync(a => a.JobId == job.Id)) return (null, "Logfilerne er allerede modtaget.");
        var (zip, entries, bad) = HelpdeskRules.ReadLogs(u.Data);
        if (bad is not null) return (null, bad);
        var a = await SaveLogs(m, zip!, entries!, null, job.Id, job.CreatedBy);
        await audit.Log($"agent:{m.Label}", "helpdesk.logs", $"logfiler hentet ({LogBundle.Size(a.Size)}) til {job.CreatedBy}", m.Id);
        await live.Changed("helpdesk", m.Id);
        return (a, null);
    }

    public async Task MarkRead(int machineId)
    {
        var now = DateTime.UtcNow;
        var n = await db.HelpdeskMessages.Where(x => x.MachineId == machineId && x.FromMachine && x.ReadAt == null)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.ReadAt, now));
        if (n > 0) await live.Changed("helpdesk", machineId);
    }

    public async Task<bool> SetStatus(int machineId, bool close, string by)
    {
        var c = await db.HelpdeskConversations.FirstOrDefaultAsync(x => x.MachineId == machineId);
        if (c is null) return false;
        c.Status = close ? HelpdeskStatus.Closed : HelpdeskStatus.Open;
        c.ClosedAt = close ? DateTime.UtcNow : null; c.ClosedBy = close ? by : null;
        await db.SaveChangesAsync();
        if (close) await MarkRead(machineId);
        await audit.Log(by, close ? "helpdesk.close" : "helpdesk.reopen", "", machineId);
        await live.Changed("helpdesk", machineId);
        return true;
    }
}
