using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Endpoints;
using Fleetdeck.Server.Hubs;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;

namespace Fleetdeck.Tests;

public class HelpdeskTextTests
{
    [Fact]
    public void Html_stays_as_plain_text_and_control_characters_go()
    {
        var t = HelpdeskText.Clean("<b>Hej</b> <script>alert(1)</script>\r\nlinje 2\u0007‮");
        Assert.Equal("<b>Hej</b> <script>alert(1)</script>\nlinje 2", t);   // HTML røres ikke – den vises som tekst
        Assert.Equal("", HelpdeskText.Clean("  \r\n \t "));
        Assert.Equal(HelpdeskText.MaxText, HelpdeskText.Clean(new string('x', 9000)).Length);
        Assert.Equal("a\n\nb", HelpdeskText.Clean("a\n\n\n\n\nb"));
    }

    [Fact]
    public void Preview_is_one_line_and_short()
    {
        Assert.Equal("a b c", HelpdeskText.Preview("a\nb\tc"));
        var p = HelpdeskText.Preview(new string('y', 300), 50);
        Assert.Equal(50, p.Length);
        Assert.EndsWith("…", p);
    }

    [Fact]
    public void Buttons_are_short_unique_at_most_four_and_only_known_actions()
    {
        var b = HelpdeskText.Buttons([
            new() { Label = "OK" }, new() { Label = "ok" }, new() { Label = "  " }, new() { Label = "Ja, genstart nu", Action = HelpdeskActions.Reboot },
            new() { Label = "Slet alt", Action = "format_c" }, new() { Label = "Senere" }, new() { Label = "Femte" },
        ]);
        Assert.Equal(["OK", "Ja, genstart nu", "Slet alt", "Senere"], b.Select(x => x.Label));
        Assert.Equal(HelpdeskActions.Reboot, b[1].Action);
        Assert.Equal(HelpdeskActions.None, b[2].Action);   // ukendt handling bliver til et almindeligt svar
        Assert.Empty(HelpdeskText.Buttons(null));
    }

    [Fact]
    public void Only_jpeg_and_png_count_as_screenshots()
    {
        Assert.Equal("image/jpeg", HelpdeskText.ImageType([0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0]));
        Assert.Equal("image/png", HelpdeskText.ImageType([0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10]));
        Assert.Null(HelpdeskText.ImageType("<html><body>"u8));
        Assert.Null(HelpdeskText.ImageType(new byte[HelpdeskText.MaxScreenshotBytes + 1]));
    }
}

public class HelpdeskInboxTests
{
    private static HelpdeskNote Note(int id, string text = "Hej", params HelpdeskButton[] b) =>
        new() { Id = id, From = "IT-support", Text = text, At = DateTime.UtcNow.AddMinutes(id), Buttons = b.ToList() };

    [Fact]
    public void The_same_message_is_only_added_once()
    {
        var inbox = new HelpdeskInbox();
        Assert.Equal([1, 2], inbox.Merge([Note(1), Note(2)]));
        Assert.Equal([3], inbox.Merge([Note(2), Note(3)]));   // serveren sender igen, til agenten har kvitteret
        Assert.Equal(3, inbox.Lines.Count);
        Assert.Equal(3, inbox.Unread.Count());
    }

    [Fact]
    public void Mark_read_returns_what_to_acknowledge()
    {
        var inbox = new HelpdeskInbox();
        inbox.Merge([Note(1), Note(2)]);
        Assert.Equal([2], inbox.MarkRead([2]));
        Assert.Equal([1], inbox.MarkRead());
        Assert.Empty(inbox.MarkRead());
        Assert.Empty(inbox.Unread);
    }

    [Fact]
    public void A_button_can_be_used_once_and_only_on_its_own_message()
    {
        var inbox = new HelpdeskInbox();
        inbox.Merge([Note(5, "Må pc'en genstarte?", new HelpdeskButton { Label = "Ja, genstart nu", Action = HelpdeskActions.Reboot }, new HelpdeskButton { Label = "Senere" })]);
        Assert.False(inbox.CanAnswer(5, 2));
        Assert.False(inbox.CanAnswer(99, 0));
        Assert.True(inbox.CanAnswer(5, 1));
        var mine = inbox.AddMine("anna", "", 5, 1, false, DateTime.UtcNow);
        Assert.Equal("Senere", mine.Text);   // knappens tekst er svaret
        Assert.True(mine.Pending);
        Assert.False(inbox.CanAnswer(5, 0));
    }

    [Fact]
    public void Only_the_latest_lines_are_kept()
    {
        var inbox = new HelpdeskInbox();
        inbox.Merge(Enumerable.Range(1, 60).Select(i => Note(i)));
        Assert.Equal(HelpdeskInbox.Keep, inbox.Lines.Count);
        Assert.Equal(21, inbox.Lines[0].Id);
    }
}

public class HelpdeskRulesTests
{
    [Fact]
    public void Targets_by_machine_tag_and_profile_without_duplicates()
    {
        var ms = new (int, IReadOnlyCollection<string>, int?)[] { (1, ["kontor"], 10), (2, ["Kontor", "bærbar"], null), (3, [], 10), (4, ["stue"], 11) };
        Assert.Equal([1, 2], HelpdeskRules.Resolve(ms, new() { Tag = "KONTOR" }));
        Assert.Equal([1, 3], HelpdeskRules.Resolve(ms, new() { ProfileId = 10 }));
        Assert.Equal([1, 2, 3, 4], HelpdeskRules.Resolve(ms, new() { MachineIds = [4, 1], Tag = "kontor", ProfileId = 10 }));
        Assert.Empty(HelpdeskRules.Resolve(ms, new() { MachineIds = [99] }));
        Assert.Empty(HelpdeskRules.Resolve(ms, new()));
    }

    [Fact]
    public void Pending_and_unread()
    {
        var now = DateTime.UtcNow;
        var msgs = new List<HelpdeskMessage>
        {
            new() { Id = 3, Created = now.AddMinutes(2) }, new() { Id = 1, Created = now }, new() { Id = 2, Created = now.AddMinutes(1), DeliveredAt = now },
            new() { Id = 4, FromMachine = true }, new() { Id = 5, FromMachine = true, ReadAt = now },
        };
        Assert.Equal([1, 3], HelpdeskRules.Pending(msgs).Select(m => m.Id));
        Assert.Equal(1, HelpdeskRules.Unread(msgs));
    }

    [Fact]
    public void Answers_are_checked_against_the_machine_and_the_message()
    {
        var m = new HelpdeskMessage { Id = 1, MachineId = 7, ButtonsJson = Json.Ser(new List<HelpdeskButton> { new() { Label = "Ja, genstart nu", Action = HelpdeskActions.Reboot } }) };
        Assert.Equal(HelpdeskActions.Reboot, HelpdeskRules.Answer(m, 7, 0).Button!.Action);
        Assert.Equal((null, null), HelpdeskRules.Answer(m, 7, null));
        Assert.NotNull(HelpdeskRules.Answer(m, 8, 0).Why);   // en anden maskine kan ikke svare
        Assert.NotNull(HelpdeskRules.Answer(m, 7, 1).Why);
        Assert.NotNull(HelpdeskRules.Answer(null, 7, 0).Why);
        m.Answer = 0;
        Assert.NotNull(HelpdeskRules.Answer(m, 7, 0).Why);   // kun én gang
    }

    [Fact]
    public void Info_has_name_os_uptime_user_and_latest_errors()
    {
        var i = HelpdeskRules.Info("ARBEJDS-PC", new SystemFacts { Os = "Windows 11 Pro", OsVersion = "24H2" }, new LiveFacts { UptimeH = 75 }, "anna", ["Disk C: 92 % fuld"]);
        Assert.Equal("ARBEJDS-PC · Windows 11 Pro 24H2 · oppe i 3 dage · bruger anna\nSeneste fejl: Disk C: 92 % fuld", i);
        Assert.EndsWith("Ingen åbne fejl.", HelpdeskRules.Info("minpi", new(), new(), null, []));
        Assert.Equal("20 min.", HelpdeskRules.Uptime(0.33));
        Assert.Equal("5 timer", HelpdeskRules.Uptime(5.2));
    }

    [Fact]
    public void Settings_default_to_neutral_sender_and_templates()
    {
        var s = HelpdeskRules.Settings("", "", "");
        Assert.Equal("IT-support", s.Sender);
        Assert.True(s.Notify);
        Assert.Contains(s.Templates, t => t.Name == "Genstart venligst" && t.Buttons.Any(b => b.Action == HelpdeskActions.Reboot));
        Assert.Empty(HelpdeskRules.Settings("Support", "0", "[]").Templates);
        Assert.False(HelpdeskRules.Settings("Support", "0", "[]").Notify);
    }
}

public sealed class HelpdeskServiceTests : IDisposable
{
    private readonly SqliteConnection conn = new("Data Source=:memory:");
    private readonly AppDb db;
    private readonly HelpdeskService svc;

    public HelpdeskServiceTests()
    {
        conn.Open();
        db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        var live = new Live(new NoHub<UiHub>(), new NoHub<AgentHub>());
        var sp = new ServiceCollection().BuildServiceProvider();
        var notifier = new Notifier(new NoHttp(), sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<Notifier>.Instance);
        svc = new HelpdeskService(db, new SettingsService(db), live, notifier, new AuditService(db), new JobService(db, live));
        db.Settings.Add(new Setting { Key = "helpdesk_notify", Value = "0" });
        db.Machines.Add(new Machine { Id = 1, Name = "ARBEJDS-PC", Tags = ["kontor"], FactsJson = Json.Ser(new SystemFacts { Os = "Windows 11" }) });
        db.Machines.Add(new Machine { Id = 2, Name = "STUE-PC", Tags = ["kontor"] });
        db.Machines.Add(new Machine { Id = 3, Name = "minpi" });
        db.SaveChanges();
    }

    public void Dispose() { db.Dispose(); conn.Dispose(); }
    private Machine M(int id) => db.Machines.Find(id)!;

    [Fact]
    public async Task Offline_machine_gets_the_message_when_it_checks_in_until_it_acknowledges()
    {
        var ids = await svc.Send(new() { Tag = "kontor" }, "Gem dit arbejde, tak.", null, "admin");
        Assert.Equal([1, 2], ids);
        var s = new SettingsService(db);
        var p = await HelpdeskService.PendingFor(db, s, 1);
        Assert.Single(p);
        Assert.Equal("IT-support", p[0].From);
        Assert.Single(await HelpdeskService.PendingFor(db, s, 1));   // sendes igen ved næste check-in
        await svc.Ack(M(2), new HelpdeskAck { Delivered = [p[0].Id] });   // en anden maskine kan ikke kvittere for den
        Assert.Single(await HelpdeskService.PendingFor(db, s, 1));
        await svc.Ack(M(1), new HelpdeskAck { Delivered = [p[0].Id] });
        Assert.Empty(await HelpdeskService.PendingFor(db, s, 1));
        Assert.Single(await HelpdeskService.PendingFor(db, s, 2));
        await svc.Ack(M(1), new HelpdeskAck { Read = [p[0].Id] });
        Assert.NotNull((await db.HelpdeskMessages.AsNoTracking().FirstAsync(x => x.Id == p[0].Id)).ReadAt);
    }

    [Fact]
    public async Task Reply_counts_as_unread_until_an_admin_reads_it()
    {
        await svc.Send(new() { MachineIds = [1] }, "Hej", null, "admin");
        var note = (await HelpdeskService.PendingFor(db, new SettingsService(db), 1))[0];
        var (msg, err) = await svc.FromMachine(M(1), new HelpdeskReply { ReplyTo = note.Id, Text = "<i>Tak</i>", User = "anna" });
        Assert.Null(err);
        Assert.Equal("<i>Tak</i>", msg!.Text);   // gemt som tekst
        Assert.Equal(1, await db.HelpdeskMessages.CountAsync(x => x.FromMachine && x.ReadAt == null));
        await svc.MarkRead(1);
        Assert.Equal(0, await db.HelpdeskMessages.CountAsync(x => x.FromMachine && x.ReadAt == null));
        var c = await db.HelpdeskConversations.AsNoTracking().SingleAsync();
        Assert.True(c.LastFromMachine);
        Assert.Equal("", msg.Info);   // et svar får ikke grundoplysningerne med
    }

    [Fact]
    public async Task User_started_case_carries_basic_info_and_reopens_a_closed_conversation()
    {
        db.Alerts.Add(new Alert { MachineId = 1, Key = "disk", Title = "Disk C: næsten fuld" });
        await db.SaveChangesAsync();
        var (first, _) = await svc.FromMachine(M(1), new HelpdeskReply { Text = "Printeren virker ikke", User = "anna" });
        Assert.Contains("ARBEJDS-PC · Windows 11", first!.Info);
        Assert.Contains("Disk C: næsten fuld", first.Info);
        var (more, _) = await svc.FromMachine(M(1), new HelpdeskReply { Text = "Og skærmen blinker", User = "anna" });
        Assert.Equal("", more!.Info);   // en fortsættelse lige efter får ikke oplysningerne igen
        Assert.True(await svc.SetStatus(1, close: true, "admin"));
        var (again, _) = await svc.FromMachine(M(1), new HelpdeskReply { Text = "Igen", User = "anna" });
        Assert.NotEqual("", again!.Info);   // en lukket sag, der åbnes igen, er en ny sag
        Assert.Equal(HelpdeskStatus.Open, (await db.HelpdeskConversations.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task Yes_restart_now_creates_a_reboot_job_once()
    {
        await svc.Send(new() { MachineIds = [1] }, "Må pc'en genstarte nu?", [new() { Label = "Ja, genstart nu", Action = HelpdeskActions.Reboot }, new() { Label = "Senere" }], "admin");
        var id = (await db.HelpdeskMessages.SingleAsync()).Id;
        var (ok, err) = await svc.FromMachine(M(1), new HelpdeskReply { ReplyTo = id, Button = 0, User = "anna" });
        Assert.Null(err);
        Assert.Equal("Ja, genstart nu", ok!.Text);
        var job = await db.Jobs.SingleAsync();
        Assert.Equal(JobTypes.Reboot, job.Type);
        Assert.Equal(1, job.MachineId);
        Assert.Contains(await db.Audit.Select(a => a.Detail).ToListAsync(), d => d.Contains("brugeren valgte \"Ja, genstart nu\""));
        var (again, why) = await svc.FromMachine(M(1), new HelpdeskReply { ReplyTo = id, Button = 0 });
        Assert.Null(again);
        Assert.Contains("allerede", why);
        Assert.Equal(1, await db.Jobs.CountAsync());
    }

    [Fact]
    public async Task Another_machine_cannot_answer_and_never_restarts_anything()
    {
        await svc.Send(new() { MachineIds = [1] }, "Må pc'en genstarte nu?", [new() { Label = "Ja, genstart nu", Action = HelpdeskActions.Reboot }], "admin");
        var id = (await db.HelpdeskMessages.SingleAsync()).Id;
        var (msg, why) = await svc.FromMachine(M(3), new HelpdeskReply { ReplyTo = id, Button = 0 });
        Assert.Null(msg);
        Assert.NotNull(why);
        Assert.Equal(0, await db.Jobs.CountAsync());
        // samme svar uden knap bliver en ny sag på maskine 3 – ikke et svar på maskine 1's besked
        var (other, _) = await svc.FromMachine(M(3), new HelpdeskReply { ReplyTo = id, Text = "hej" });
        Assert.Null(other!.ReplyToId);
        Assert.Equal(3, other.MachineId);
    }

    [Fact]
    public async Task Screenshot_must_be_an_image_and_is_stored_separately()
    {
        var (bad, why) = await svc.FromMachine(M(1), new HelpdeskReply { Text = "se her", Screenshot = Convert.ToBase64String("<svg onload=x>"u8.ToArray()) });
        Assert.Null(bad); Assert.NotNull(why);
        byte[] jpg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5];
        var (ok, _) = await svc.FromMachine(M(1), new HelpdeskReply { Screenshot = Convert.ToBase64String(jpg) });
        Assert.True(ok!.HasScreenshot);
        Assert.Equal(jpg, (await db.HelpdeskScreenshots.SingleAsync(s => s.MessageId == ok.Id)).Data);
    }

    // Punkt 14 (ejerens test: "man kunne kun sende et skærmbillede tilbage"): et tekstsvar på en besked med svarknapper
    // må aldrig falde bort, fordi knappen ikke kan bruges længere (fx allerede besvaret, eller samtalen er skrevet om).
    [Fact]
    public async Task A_text_reply_is_kept_even_when_its_button_can_no_longer_be_used()
    {
        await svc.Send(new() { MachineIds = [1] }, "Jeg kigger på det nu.", [new() { Label = "OK" }], "admin");
        var id = (await db.HelpdeskMessages.SingleAsync()).Id;
        Assert.Null((await svc.FromMachine(M(1), new HelpdeskReply { ReplyTo = id, Button = 0, User = "anna" })).Error);
        var (msg, err) = await svc.FromMachine(M(1), new HelpdeskReply { ReplyTo = id, Button = 0, Text = "Tak, den virker nu", User = "anna" });
        Assert.Null(err);
        Assert.Equal("Tak, den virker nu", msg!.Text);
        Assert.Equal(0, (await db.HelpdeskMessages.AsNoTracking().SingleAsync(x => x.Id == id)).Answer);   // det første valg står
        // et rent tekstsvar (ingen knap, intet skærmbillede) går også igennem
        var (plain, err2) = await svc.FromMachine(M(1), new HelpdeskReply { ReplyTo = id, Text = "Og printeren også", User = "anna" });
        Assert.Null(err2);
        Assert.Equal("Og printeren også", plain!.Text);
        Assert.False(plain.HasScreenshot);
    }

    [Fact]
    public async Task Logs_from_the_user_are_stored_with_the_message_and_only_the_latest_are_kept()
    {
        var zip = LogBundle.Build([new LogPart("agent-log", "linje 1\nlinje 2")], "test", DateTime.UtcNow);
        var (msg, err) = await svc.FromMachine(M(1), new HelpdeskReply { Logs = Convert.ToBase64String(zip), User = "anna" });
        Assert.Null(err);
        Assert.Equal("", msg!.Text);
        var a = await db.HelpdeskAttachments.AsNoTracking().SingleAsync();
        Assert.Equal(msg.Id, a.MessageId);
        Assert.Equal(zip, a.Data);
        Assert.StartsWith("logfiler-ARBEJDS-PC-", a.Name);
        Assert.Contains("agent-log.txt", a.EntriesJson);
        Assert.Equal("Logfiler", (await db.HelpdeskConversations.AsNoTracking().SingleAsync()).Preview);
        for (var i = 0; i < HelpdeskRules.KeepLogs + 2; i++) await svc.FromMachine(M(1), new HelpdeskReply { Text = $"nr. {i}", Logs = Convert.ToBase64String(zip) });
        Assert.Equal(HelpdeskRules.KeepLogs, await db.HelpdeskAttachments.CountAsync());
    }

    [Fact]
    public async Task Logs_must_be_a_flat_zip_of_text_files()
    {
        Assert.NotNull((await svc.FromMachine(M(1), new HelpdeskReply { Logs = Convert.ToBase64String("MZ ikke en zip"u8.ToArray()) })).Error);
        Assert.NotNull((await svc.FromMachine(M(1), new HelpdeskReply { Logs = "ikke base64!" })).Error);
        Assert.Equal(0, await db.HelpdeskAttachments.CountAsync());
    }

    [Fact]
    public async Task Fetch_logs_creates_one_job_and_only_that_machine_can_answer_it_once()
    {
        db.Machines.Find(1)!.Platform = Platforms.Windows; db.Machines.Find(3)!.Platform = Platforms.Linux; await db.SaveChangesAsync();
        var (job, err) = await svc.RequestLogs(1, "admin");
        Assert.Null(err);
        Assert.Equal(JobTypes.CollectLogs, job!.Type);
        var (again, _) = await svc.RequestLogs(1, "admin");
        Assert.Equal(job.Id, again!.Id);   // kun ét ad gangen
        Assert.Equal(1, await db.Jobs.CountAsync(j => j.Type == JobTypes.CollectLogs));
        var zip = Convert.ToBase64String(LogBundle.Build([new LogPart("journal", "ok")], "test", DateTime.UtcNow));
        Assert.NotNull((await svc.FromJob(M(3), new LogUpload { JobId = job.Id, Data = zip })).Error);   // en anden maskine
        var (file, ferr) = await svc.FromJob(M(1), new LogUpload { JobId = job.Id, Data = zip });
        Assert.Null(ferr);
        Assert.Null(file!.MessageId);
        Assert.Equal(job.Id, file.JobId);
        Assert.Equal("admin", file.By);
        Assert.NotNull((await svc.FromJob(M(1), new LogUpload { JobId = job.Id, Data = zip })).Error);   // kun én gang
        Assert.Equal(0, await db.HelpdeskMessages.CountAsync());   // ingen besked til brugeren, intet ulæst
    }

    [Fact]
    public async Task Empty_messages_and_no_targets_are_refused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => svc.Send(new() { MachineIds = [1] }, "  ", null, "admin"));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.Send(new() { Tag = "findes-ikke" }, "Hej", null, "admin"));
        Assert.NotNull((await svc.FromMachine(M(1), new HelpdeskReply { Text = " " })).Error);
    }

    [Fact]
    public async Task Settings_round_trip_cleaned()
    {
        await svc.SaveSettings(new HelpdeskSettings { Sender = "  Support\n", Notify = false, Templates = [new() { Name = "Hej", Text = "Hej <b>du</b>" }, new() { Name = "", Text = "x" }] });
        var s = await svc.GetSettings();
        Assert.Equal("Support", s.Sender);
        Assert.False(s.Notify);
        Assert.Equal("Hej <b>du</b>", Assert.Single(s.Templates).Text);
    }

    private sealed class NoHttp : IHttpClientFactory { public HttpClient CreateClient(string name) => throw new InvalidOperationException(); }
    private sealed class NoHub<T> : IHubContext<T> where T : Hub
    {
        public IHubClients Clients { get; } = new NoClients();
        public IGroupManager Groups { get; } = new NoGroups();
    }
    private sealed class NoClients : IHubClients
    {
        private static readonly IClientProxy P = new NoProxy();
        public IClientProxy All => P;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => P;
        public IClientProxy Client(string connectionId) => P;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => P;
        public IClientProxy Group(string groupName) => P;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => P;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => P;
        public IClientProxy User(string userId) => P;
        public IClientProxy Users(IReadOnlyList<string> userIds) => P;
    }
    private sealed class NoProxy : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class NoGroups : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

public class HelpdeskChannelTests
{
    private sealed class Store : ITrustStore { public string? JobKey { get; set; } public string? TlsPin { get; set; } }

    private static CommandEnvelope Signed(ECDsa k, string pem, string nonce) => CommandSigning.Sign(k, pem, new SignedCommands
    {
        MachineId = 5, Nonce = nonce, Issued = DateTime.UtcNow, Expires = DateTime.UtcNow.AddMinutes(5),
        Helpdesk = [new HelpdeskNote { Id = 1, From = "IT-support", Text = "Hej" }],
    });

    [Fact]
    public void Messages_only_come_from_a_response_signed_with_the_pinned_key()
    {
        using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256); var pem = k.ExportSubjectPublicKeyInfoPem();
        var nonce = CommandSigning.NewNonce();
        // en falsk besked i den usignerede del erstattes af den signerede
        var r = new CheckinResponse { Signed = Signed(k, pem, nonce), Helpdesk = [new HelpdeskNote { Id = 9, Text = "Klik her" }] };
        CommandGate.Apply(r, new Store { JobKey = pem }, 5, nonce, DateTime.UtcNow);
        Assert.Equal("Hej", Assert.Single(r.Helpdesk).Text);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var forged = new CheckinResponse { Signed = Signed(other, other.ExportSubjectPublicKeyInfoPem(), nonce), Helpdesk = [new HelpdeskNote { Id = 9, Text = "Klik her" }] };
        var o = CommandGate.Apply(forged, new Store { JobKey = pem }, 5, nonce, DateTime.UtcNow);
        Assert.False(o.Trusted);
        Assert.Empty(forged.Helpdesk);
    }

    [Fact]
    public void Admin_endpoints_require_admin_and_agent_endpoints_do_not_use_cookies()
    {
        var b = WebApplication.CreateBuilder();
        b.Services.AddDbContext<AppDb>(o => o.UseSqlite("Data Source=:memory:"));
        b.Services.AddSignalR();
        b.Services.AddHttpClient();
        b.Services.AddSingleton<Live>();
        b.Services.AddSingleton<Notifier>();
        b.Services.AddScoped<SettingsService>();
        b.Services.AddScoped<AuditService>();
        b.Services.AddScoped<JobService>();
        b.Services.AddScoped<HelpdeskService>();
        b.Services.AddAuthorization();
        var app = b.Build();
        app.MapHelpdeskEndpoints();
        var eps = ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().ToList();
        Assert.True(eps.Count >= 10);
        foreach (var e in eps)
        {
            var raw = e.RoutePattern.RawText!;
            var auth = e.Metadata.GetOrderedMetadata<IAuthorizeData>();
            if (raw.StartsWith("/api/agent/")) Assert.Empty(auth);   // agenten bruger sit eget token
            else Assert.Contains(auth, a => a.Policy == "admin");
        }
    }
}

/// <summary>Windows-agentens side: samtalen i helpdesk.json, ordrer fra ikonet og svar, der venter på forbindelsen.</summary>
public sealed class HelpdeskAgentTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("fd-hd-").FullName;
    private readonly Fleetdeck.Agent.Windows.AgentOptions opt;
    private readonly Fleetdeck.Agent.Windows.HelpdeskBridge bridge;
    private readonly Recorder rec = new();
    private readonly Fleetdeck.Agent.Windows.ServerClient api;

    public HelpdeskAgentTests()
    {
        opt = new Fleetdeck.Agent.Windows.AgentOptions { Dir = dir };
        bridge = new Fleetdeck.Agent.Windows.HelpdeskBridge(opt, NullLogger<Fleetdeck.Agent.Windows.HelpdeskBridge>.Instance) { CurrentUser = () => "anna" };
        api = new Fleetdeck.Agent.Windows.ServerClient(new HttpClient(rec), new Fleetdeck.Agent.Windows.AgentConfig { Server = "http://192.0.2.10:8080" });
    }
    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    private sealed class Recorder : HttpMessageHandler
    {
        public bool Offline;
        public List<(string Path, string Body)> Calls = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            if (Offline) throw new HttpRequestException("ingen forbindelse");
            Calls.Add((r.RequestUri!.AbsolutePath, r.Content is null ? "" : await r.Content.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }

    private static HelpdeskNote Note(int id, params HelpdeskButton[] b) => new() { Id = id, From = "IT-support", Text = "Må pc'en genstarte?", At = DateTime.UtcNow, Buttons = b.ToList() };

    [Fact]
    public async Task Delivered_messages_land_in_the_inbox_once_and_are_acknowledged()
    {
        await bridge.Deliver(api, [Note(1)], CancellationToken.None);
        await bridge.Deliver(api, [Note(1)], CancellationToken.None);   // igen: samme besked, ny kvittering
        Assert.Single(bridge.Load().Lines);
        Assert.Equal(2, rec.Calls.Count(c => c.Path == "/api/agent/helpdesk/ack"));
        await bridge.Handle(api, new TrayCommand { Action = "helpdesk_read", Ids = [1] }, CancellationToken.None);
        Assert.Contains(rec.Calls, c => c.Path == "/api/agent/helpdesk/ack" && c.Body.Contains("\"read\":[1]"));
        Assert.Empty(bridge.Load().Unread);
    }

    [Fact]
    public async Task A_forged_button_is_not_sent_and_a_real_one_is_sent_once()
    {
        await bridge.Deliver(api, [Note(1, new HelpdeskButton { Label = "Ja, genstart nu", Action = HelpdeskActions.Reboot })], CancellationToken.None);
        await bridge.Handle(api, new TrayCommand { Action = "helpdesk_reply", ReplyTo = 1, Button = 5, Text = "" }, CancellationToken.None);
        Assert.DoesNotContain(rec.Calls, c => c.Path == "/api/agent/helpdesk/reply");   // tomt svar med ukendt knap sendes ikke
        await bridge.Handle(api, new TrayCommand { Action = "helpdesk_reply", ReplyTo = 1, Button = 0 }, CancellationToken.None);
        var sent = Assert.Single(rec.Calls, c => c.Path == "/api/agent/helpdesk/reply");
        Assert.Contains("\"button\":0", sent.Body);
        Assert.Contains("\"user\":\"anna\"", sent.Body);
        await bridge.Handle(api, new TrayCommand { Action = "helpdesk_reply", ReplyTo = 1, Button = 0, Text = "igen" }, CancellationToken.None);
        Assert.DoesNotContain("\"button\"", rec.Calls.Last().Body);   // knappen kan kun bruges én gang
    }

    [Fact]
    public async Task Screenshot_is_only_taken_from_our_own_image_files()
    {
        var cmd = Path.Combine(opt.UserWork, "cmd"); Directory.CreateDirectory(cmd);
        var secret = Path.Combine(cmd, "agent.json"); File.WriteAllText(secret, "{\"token\":\"hemmelig\"}");
        await bridge.Handle(api, new TrayCommand { Action = "helpdesk_reply", Text = "se", File = "agent.json" }, CancellationToken.None);
        Assert.DoesNotContain("\"screenshot\":\"", rec.Calls.Last().Body);
        var fake = $"{Guid.NewGuid():N}.jpg"; File.WriteAllText(Path.Combine(cmd, fake), "ikke et billede");
        await bridge.Handle(api, new TrayCommand { Action = "helpdesk_reply", Text = "se", File = fake }, CancellationToken.None);
        Assert.DoesNotContain("\"screenshot\":\"", rec.Calls.Last().Body);
        Assert.False(File.Exists(Path.Combine(cmd, fake)));
        var real = $"{Guid.NewGuid():N}.jpg"; File.WriteAllBytes(Path.Combine(cmd, real), [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4]);
        await bridge.Handle(api, new TrayCommand { Action = "helpdesk_reply", File = real }, CancellationToken.None);
        Assert.Contains("\"screenshot\":\"/9j/4", rec.Calls.Last().Body);
        Assert.False(File.Exists(Path.Combine(cmd, real)));
    }

    // Punkt 14: hele vejen for et tekstsvar – ordren, som ikonet skriver den, læses af tjenesten og sendes til serveren med teksten.
    [Fact]
    public async Task A_text_reply_from_the_tray_reaches_the_server_with_its_text()
    {
        await bridge.Deliver(api, [Note(7, new HelpdeskButton { Label = "OK" })], CancellationToken.None);
        var tray = new Fleetdeck.Agent.Windows.TrayBridge(opt, NullLogger<Fleetdeck.Agent.Windows.TrayBridge>.Instance);
        Directory.CreateDirectory(tray.UserCmdDir);
        File.WriteAllText(Path.Combine(tray.UserCmdDir, $"{Guid.NewGuid():N}.json"),
            System.Text.Json.JsonSerializer.Serialize(new TrayCommand { Action = "helpdesk_reply", Text = "Den virker nu, tak", ReplyTo = 7 }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        foreach (var c in tray.TakeCommands()) await bridge.Handle(api, c, CancellationToken.None);
        var sent = Assert.Single(rec.Calls, c => c.Path == "/api/agent/helpdesk/reply");
        var r = System.Text.Json.JsonSerializer.Deserialize<HelpdeskReply>(sent.Body, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.Equal("Den virker nu, tak", r.Text);
        Assert.Equal(7, r.ReplyTo);
        Assert.Null(r.Screenshot);
        Assert.False(bridge.Load().Lines.Last().Pending);
    }

    [Fact]
    public async Task Send_logs_collects_once_a_minute_and_sends_the_text_with_them()
    {
        var collected = 0;
        bridge.CollectLogs = _ => { collected++; return Task.FromResult(LogBundle.Build([new LogPart("agent-log", "ok")], "test", DateTime.UtcNow)); };
        await bridge.Handle(api, new TrayCommand { Action = "helpdesk_logs", Text = "Den fryser" }, CancellationToken.None);
        var sent = Assert.Single(rec.Calls, c => c.Path == "/api/agent/helpdesk/reply");
        Assert.Contains("\"logs\":\"UEsDB", sent.Body);   // en zip (PK..)
        Assert.Contains("Den fryser", sent.Body);
        Assert.True(bridge.Load().Lines.Single().Logs);
        await bridge.Handle(api, new TrayCommand { Action = "helpdesk_logs" }, CancellationToken.None);
        Assert.Equal(1, collected);   // lige efter: ingen ny pakke, og intet tomt svar
        Assert.Single(rec.Calls, c => c.Path == "/api/agent/helpdesk/reply");
    }

    [Fact]
    public async Task Only_the_newest_log_bundle_waits_for_the_connection()
    {
        rec.Offline = true;
        bridge.CollectLogs = _ => Task.FromResult(LogBundle.Build([new LogPart("agent-log", "ok")], "test", DateTime.UtcNow));
        await bridge.Handle(api, new TrayCommand { Action = "helpdesk_logs", Text = "svar 1" }, CancellationToken.None);
        typeof(Fleetdeck.Agent.Windows.HelpdeskBridge).GetField("_lastLogs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(bridge, DateTime.MinValue);
        await bridge.Handle(api, new TrayCommand { Action = "helpdesk_logs", Text = "svar 2" }, CancellationToken.None);
        rec.Offline = false;
        await bridge.Flush(api, CancellationToken.None);
        var replies = rec.Calls.Where(c => c.Path == "/api/agent/helpdesk/reply").ToList();
        Assert.Equal(2, replies.Count);
        Assert.DoesNotContain("\"logs\":\"UEsDB", replies[0].Body);   // teksten kom med, den gamle pakke ikke
        Assert.Contains("svar 1", replies[0].Body);
        Assert.Contains("\"logs\":\"UEsDB", replies[1].Body);
    }

    [Fact]
    public async Task Replies_wait_for_the_connection_and_are_sent_later()
    {
        rec.Offline = true;
        await bridge.Handle(api, new TrayCommand { Action = "helpdesk_reply", Text = "Printeren virker ikke" }, CancellationToken.None);
        Assert.True(bridge.Load().Lines.Single().Pending);
        rec.Offline = false;
        await bridge.Flush(api, CancellationToken.None);
        Assert.Single(rec.Calls, c => c.Path == "/api/agent/helpdesk/reply");
        Assert.False(bridge.Load().Lines.Single().Pending);
        await bridge.Flush(api, CancellationToken.None);
        Assert.Single(rec.Calls, c => c.Path == "/api/agent/helpdesk/reply");   // sendes ikke to gange
    }
}
