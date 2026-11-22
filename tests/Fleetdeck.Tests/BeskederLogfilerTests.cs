using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Fleetdeck.Shared;

namespace Fleetdeck.Tests;

/// <summary>Logpakken fra maskinen: hemmeligheder skjult, højst 5 MB, kun tekstfiler.</summary>
public class LogBundleTests
{
    [Theory]
    [InlineData("Authorization: Bearer abc.def.ghi-123", "abc.def")]
    [InlineData("POST /api/agent/checkin token=s3cr3tT0ken&x=1", "s3cr3tT0ken")]
    [InlineData("{\"plainToken\":\"Zm9vYmFyYmF6\",\"server\":\"http://192.0.2.10\"}", "Zm9vYmFyYmF6")]
    [InlineData("password = Hemmelig123!", "Hemmelig123!")]
    [InlineData("adgangskode: Sommer2026", "Sommer2026")]
    [InlineData("hent https://anna:Kode42@192.0.2.10/fil", "Kode42")]
    [InlineData("jwt eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjMifQ.c2lnbmF0dXJlLXRlc3Q", "eyJhbGciOi")]
    [InlineData("nøgle Q2hhcmxpZU1pa2VGb3h0cm90R29sZlNpZXJyYTEyMzQ1Njc4OTA=", "Q2hhcmxpZU1pa2VGb3h0cm90")]
    [InlineData("tpm 0123456789abcdef0123456789abcdef01234567", "0123456789abcdef0123")]
    [InlineData("produktnøgle ABCDE-12345-FGHIJ-67890-KLMNO", "ABCDE-12345")]
    public void Secrets_are_hidden(string line, string secret)
    {
        var r = LogBundle.Redact(line);
        Assert.DoesNotContain(secret, r);
        Assert.Contains(LogBundle.Hidden, r);
    }

    [Fact]
    public void Private_keys_are_hidden_but_ordinary_log_lines_stay_readable()
    {
        var key = "-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAA\n-----END OPENSSH PRIVATE KEY-----";
        Assert.DoesNotContain("b3BlbnNzaC1rZXktdjEAAAAA", LogBundle.Redact(key));
        const string normal = "14:02:11 info: Fleetdeck.Agent.Windows.AgentWorker Inventar sendt: 112 programmer, C:\\Program Files\\Fleetdeck\\Fleetdeck.exe, fleetdeck-agent-windows-x64-3-34-0";
        Assert.Equal(normal, LogBundle.Redact(normal));
    }

    [Fact]
    public void The_bundle_is_a_zip_of_text_files_with_a_readme_and_no_secrets()
    {
        var zip = LogBundle.Build([new LogPart("Agent log", "ok\nBearer hemmeligt-token-123\n"), new LogPart("../../etc/passwd", "x"), new LogPart("Agent log", "igen")], "Logfiler fra ARBEJDS-PC", new DateTime(2026, 11, 19, 13, 2, 0, DateTimeKind.Utc));
        Assert.True(LogBundle.IsZip(zip));
        var entries = LogBundle.Entries(zip)!;
        Assert.Equal(["LÆS-MIG.txt", "agent-log.txt", "etcpasswd.txt", "agent-log-2.txt"], entries.Select(e => e.Name));
        using var z = new ZipArchive(new MemoryStream(zip));
        var text = new StreamReader(z.GetEntry("agent-log.txt")!.Open()).ReadToEnd();
        Assert.DoesNotContain("hemmeligt-token-123", text);
        Assert.Contains("Bearer [skjult]", text);
    }

    [Fact]
    public void A_bundle_that_is_too_big_keeps_the_newest_end_and_stays_under_the_limit()
    {
        var rnd = new Random(7);
        var sb = new StringBuilder();
        for (var i = 0; i < 40_000; i++) sb.Append("linje ").Append(i).Append(' ').Append(rnd.NextInt64()).Append(' ').Append(Guid.NewGuid()).Append('\n');
        sb.Append("DEN SIDSTE LINJE\n");
        var zip = LogBundle.Build([new LogPart("agent-log", sb.ToString())], "test", DateTime.UtcNow, maxBytes: 200_000);
        Assert.True(zip.Length <= 200_000);
        using var z = new ZipArchive(new MemoryStream(zip));
        var text = new StreamReader(z.GetEntry("agent-log.txt")!.Open()).ReadToEnd();
        Assert.Contains("DEN SIDSTE LINJE", text);
        Assert.StartsWith("[… begyndelsen er skåret væk …]", text);
        Assert.Contains("skåret", new StreamReader(z.GetEntry("LÆS-MIG.txt")!.Open()).ReadToEnd());
    }

    [Fact]
    public void Only_flat_zips_of_text_files_are_accepted()
    {
        Assert.Null(LogBundle.Entries("ikke en zip"u8.ToArray()));
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            using var w = new StreamWriter(z.CreateEntry("mappe/fil.txt").Open()); w.Write("x");
        }
        Assert.Null(LogBundle.Entries(ms.ToArray()));
        using var ms2 = new MemoryStream();
        using (var z = new ZipArchive(ms2, ZipArchiveMode.Create, true))
        {
            using var w = new StreamWriter(z.CreateEntry("program.exe").Open()); w.Write("MZ");
        }
        Assert.Null(LogBundle.Entries(ms2.ToArray()));
    }

    [Fact]
    public void Read_tail_takes_the_end_of_the_log_and_the_rotated_file_before_it()
    {
        var dir = Directory.CreateTempSubdirectory("fd-log-").FullName;
        try
        {
            var f = Path.Combine(dir, "agent.log");
            File.WriteAllText(f + ".1", "gammel 1\ngammel 2\n");
            File.WriteAllText(f, string.Join('\n', Enumerable.Range(1, 200).Select(i => $"ny {i}")) + "\n");
            var all = LogBundle.ReadTail(f, 100_000, f + ".1");
            Assert.StartsWith("gammel 1", all);
            Assert.EndsWith("ny 200\n", all);
            var tail = LogBundle.ReadTail(f, 60);
            Assert.StartsWith("[… begyndelsen", tail);
            Assert.EndsWith("ny 200\n", tail);
            Assert.Equal("", LogBundle.ReadTail(Path.Combine(dir, "findes-ikke.log"), 100));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Names_and_sizes()
    {
        Assert.Equal("logfiler-ARBEJDS-PC-20261119-1402.zip", Fleetdeck.Server.Services.HelpdeskRules.LogsName("ARBEJDS-PC", new DateTime(2026, 11, 19, 14, 2, 0)));
        Assert.Equal("logfiler-B-RBAR-7-20261119-1402.zip", Fleetdeck.Server.Services.HelpdeskRules.LogsName("BÆRBAR 7", new DateTime(2026, 11, 19, 14, 2, 0)));
        Assert.Equal("312 kB", LogBundle.Size(312 * 1024));
        Assert.Equal("1,5 MB", LogBundle.Size(1536 * 1024));
    }
}

/// <summary>Svarfeltet i vinduet på pc'en: tasterne og linjen "Sendes…/Sendt".</summary>
public class HelpdeskWindowLogicTests
{
    [Fact]
    public void Enter_sends_and_shift_enter_makes_a_new_line()
    {
        Assert.Equal(Fleetdeck.Agent.Windows.ReplyKeys.Act.Send, Fleetdeck.Agent.Windows.ReplyKeys.OnKey(enter: true, shift: false, ctrl: false, alt: false));
        Assert.Equal(Fleetdeck.Agent.Windows.ReplyKeys.Act.NewLine, Fleetdeck.Agent.Windows.ReplyKeys.OnKey(enter: true, shift: true, ctrl: false, alt: false));
        Assert.Equal(Fleetdeck.Agent.Windows.ReplyKeys.Act.Send, Fleetdeck.Agent.Windows.ReplyKeys.OnKey(enter: true, shift: false, ctrl: true, alt: false));
        Assert.Equal(Fleetdeck.Agent.Windows.ReplyKeys.Act.None, Fleetdeck.Agent.Windows.ReplyKeys.OnKey(enter: false, shift: false, ctrl: false, alt: false));
        Assert.Equal(Fleetdeck.Agent.Windows.ReplyKeys.Act.None, Fleetdeck.Agent.Windows.ReplyKeys.OnKey(enter: true, shift: false, ctrl: false, alt: true));
    }

    [Fact]
    public void State_line_says_sending_sent_or_waiting_for_the_connection()
    {
        var now = new DateTime(2026, 11, 19, 13, 0, 0, DateTimeKind.Utc);
        var inbox = new HelpdeskInbox();
        Assert.Equal("", HelpdeskText.SendState(inbox.Lines, now));
        var mine = inbox.AddMine("", "hej", null, null, false, now.AddSeconds(-5));
        Assert.Equal("Sendes…", HelpdeskText.SendState(inbox.Lines, now));
        Assert.StartsWith("Ikke sendt endnu", HelpdeskText.SendState(inbox.Lines, now.AddMinutes(2)));
        mine.Pending = false;
        Assert.StartsWith("Sendt kl. ", HelpdeskText.SendState(inbox.Lines, now));
        inbox.AddMine("", "", null, null, false, now, logs: true);
        Assert.Equal("Samler og sender logfiler…", HelpdeskText.SendState(inbox.Lines, now));
    }
}

/// <summary>Ordrer fra ikonet i brugermappen: Beskeder må bruges af alle, halvt skrevne ordrer læses ikke.</summary>
public sealed class TrayCommandTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("fd-tray-").FullName;
    private readonly Fleetdeck.Agent.Windows.TrayBridge tray;
    public TrayCommandTests() => tray = new(new Fleetdeck.Agent.Windows.AgentOptions { Dir = dir }, NullLogger<Fleetdeck.Agent.Windows.TrayBridge>.Instance);
    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    [Fact]
    public void Helpdesk_commands_from_the_user_folder_are_taken_and_temporary_files_are_left()
    {
        Directory.CreateDirectory(tray.UserCmdDir);
        // sådan skriver ikonet en ordre (HelpdeskTray.Write)
        foreach (var c in new[] { new TrayCommand { Action = "helpdesk_reply", Text = "Printeren virker ikke", ReplyTo = 4 }, new TrayCommand { Action = "helpdesk_logs" }, new TrayCommand { Action = "pause" } })
            File.WriteAllText(Path.Combine(tray.UserCmdDir, $"{Guid.NewGuid():N}.json"), JsonSerializer.Serialize(c, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        File.WriteAllText(Path.Combine(tray.UserCmdDir, "halv.tmp"), "{\"action\":\"helpdesk_re");
        var got = tray.TakeCommands();
        Assert.Equal(["helpdesk_logs", "helpdesk_reply"], got.Select(c => c.Action).Order());   // pause kræver administrator
        Assert.Equal("Printeren virker ikke", got.Single(c => c.Action == "helpdesk_reply").Text);
        Assert.True(File.Exists(Path.Combine(tray.UserCmdDir, "halv.tmp")));
    }
}
