using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Endpoints;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;
using Xunit;

namespace Fleetdeck.Tests;

/// <summary>Uddata fra de tre platforme (opdigtede eksempler i samme form som de rigtige værktøjer).</summary>
public class SysLogParseTests
{
    [Fact] public void Journald_json_lines_become_events_with_priority_kernel_and_unit()
    {
        var output = string.Join('\n',
            """{"__REALTIME_TIMESTAMP":"1763631302123456","PRIORITY":"3","_TRANSPORT":"kernel","MESSAGE":"blk_update_request: I/O error, dev sda, sector 123456 op 0x0:(READ)"}""",
            """{"__REALTIME_TIMESTAMP":"1763631303000000","PRIORITY":"4","_TRANSPORT":"journal","_SYSTEMD_UNIT":"backup.service","MESSAGE":"retrying upload"}""",
            """{"__REALTIME_TIMESTAMP":"1763631304000000","PRIORITY":"2","SYSLOG_IDENTIFIER":"sshd","MESSAGE":[104,101,106,255]}""",
            "-- No entries --", "{ikke json", "");
        var e = JournalLog.Parse(output);
        Assert.Equal(3, e.Count);
        Assert.Equal(("kernel", "kernel", SysLogLevels.Error), (e[0].Log, e[0].Source, e[0].Level));
        Assert.Equal(new DateTime(2025, 11, 20, 9, 35, 2, DateTimeKind.Utc).AddTicks(1234560), e[0].Time);
        Assert.Equal(("journal", "backup", SysLogLevels.Warning), (e[1].Log, e[1].Source, e[1].Level));
        Assert.Equal(SysLogLevels.Critical, e[2].Level);
        Assert.StartsWith("hej", e[2].Message);          // ugyldig UTF-8 som bytes
        Assert.Equal(SysLogKinds.IoError, SysLogKinds.Of(e[0]));
        Assert.Contains("--since @", JournalLog.Command(DateTime.UtcNow.AddHours(-1)));
    }

    [Fact] public void Classic_syslog_keeps_only_errors_and_warnings_and_handles_new_year()
    {
        var tz = TimeZoneInfo.Utc;
        var now = new DateTime(2026, 1, 2, 12, 0, 0, DateTimeKind.Utc);
        var text = """
            Dec 31 23:59:58 Tower kernel: ata3.00: failed command: READ FPDMA QUEUED
            Jan  2 10:00:01 Tower emhttpd: spinning down /dev/sdb
            Jan  2 10:00:02 Tower kernel: Out of memory: Killed process 4321 (python3) total-vm:123kB
            Jan  2 10:00:03 Tower rc.docker[1234]: warning: container restarted
            noget helt andet
            """;
        var e = SyslogText.Parse(text, now.AddDays(-7), now, tz);
        Assert.Equal(3, e.Count);
        Assert.Equal(2025, e[0].Time.Year);
        Assert.Equal(SysLogKinds.Oom, SysLogKinds.Of(e[1]));
        Assert.Equal(("rc.docker", SysLogLevels.Warning), (e[2].Source, e[2].Level));
        Assert.Null(SyslogText.Line("Jan  2 10:00:01 Tower emhttpd: spinning down /dev/sdb", now, tz));
        Assert.Equal(SysLogLevels.Info, SyslogText.Line("Jan  2 10:00:01 Tower emhttpd: spinning down /dev/sdb", now, tz, all: true)!.Level);
    }

    [Fact] public void Mac_unified_log_ndjson_and_timestamps()
    {
        var line = """{"timestamp":"2026-11-20 10:15:02.123456+0100","messageType":"Fault","eventMessage":"Watchdog timeout","processImagePath":"\/kernel","subsystem":"com.apple.iokit"}""";
        var e = MacLog.Line(line)!;
        Assert.Equal(new DateTime(2026, 11, 20, 9, 15, 2, DateTimeKind.Utc).AddTicks(1234560), e.Time);
        Assert.Equal(("kernel", SysLogLevels.Error, "unified"), (e.Source, e.Level, e.Log));
        Assert.Equal("[com.apple.iokit] Watchdog timeout", e.Message);
        Assert.Equal(SysLogLevels.Warning, MacLog.Line(line.Replace("Fault", "Error"))!.Level);
        Assert.Null(MacLog.Line("Filtering the log data using \"messageType == fault\""));
        Assert.Contains("--last 30m", MacLog.ShowCommand(TimeSpan.FromMinutes(29.5)));
        Assert.Contains("--last 120m", MacLog.ShowCommand(TimeSpan.FromDays(2)));     // aldrig mere end to timer på en Mac
    }

    [Fact] public void Mac_panic_reports_are_found_but_app_crashes_are_not()
    {
        var ips = """{"bug_type":"210","timestamp":"2026-11-19 22:01:13.00 +0100","os_version":"macOS 15.1"}""" + "\n" +
                  """{"panicString":"panic(cpu 4 caller 0xfffffe0012345678): watchdog timeout: no checkins from watchdogd\nDebugger message: panic"}""";
        var p = MacLog.Panic("panic-full-2026-11-19-220113.ips", ips, DateTime.UtcNow)!;
        Assert.Equal(new DateTime(2026, 11, 19, 21, 1, 13, DateTimeKind.Utc), p.Time);
        Assert.Equal(SysLogLevels.Critical, p.Level);
        Assert.StartsWith("Kernepanik: panic(cpu 4", p.Message);
        Assert.Equal(SysLogKinds.MacPanic, SysLogKinds.Of(p));
        Assert.Null(MacLog.Panic("Safari-2026-11-19.ips", """{"bug_type":"309"}""" + "\n{}", DateTime.UtcNow));
        Assert.NotNull(MacLog.Panic("Kernel-2026-11-19.panic", "Thu Nov 19\npanic(cpu 0 caller 0x1): \"zone map exhausted\"\n", DateTime.UtcNow));
        Assert.Null(MacLog.Panic("noget.txt", "", DateTime.UtcNow));
    }

    [Fact] public void Windows_records_map_levels_and_queries_are_built_safely()
    {
        var e = WinEventLog.Map("Microsoft-Windows-Kernel-Power", 41, 1, new DateTime(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc), "System",
            "The system has rebooted without cleanly shutting down first.", 9001);
        Assert.Equal((SysLogLevels.Critical, "System", 41, 9001L), (e.Level, e.Log, e.EventId!.Value, e.RecordId!.Value));
        Assert.Equal(SysLogKinds.UnexpectedShutdown, SysLogKinds.Of(e));
        Assert.Equal(SysLogLevels.Warning, WinEventLog.Map("x", 1, 3, null, "Application", null, null).Level);
        Assert.Contains("ingen beskrivelse", WinEventLog.Map("x", 1, 2, null, "Application", null, null).Message);
        Assert.Equal("WindowsUpdateClient", WinEventLog.ShortLog("Microsoft-Windows-WindowsUpdateClient/Operational"));
        Assert.Equal("*[System[(Level=1 or Level=2 or Level=3) and TimeCreated[@SystemTime>='2026-11-20T08:00:00.000Z']]]",
            WinEventLog.Query(new DateTime(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc)));
        Assert.Equal("*[System[(Level=0 or Level=1 or Level=2 or Level=3 or Level=4) and EventRecordID>10 and EventRecordID<=20]]", WinEventLog.Query(null, 10, true, 20));
    }
}

public class SysLogPolicyTests
{
    [Fact] public void Secrets_are_removed_before_anything_leaves_the_machine()
    {
        var e = SysLogPolicy.Clean(new SysLogEvent
        {
            Time = DateTime.UtcNow, Source = "app", Level = "ERROR",
            Message = "login failed password=Hemmelig123 Authorization: Bearer abcdefghijklmnop https://192.168.1.5/api/x?token=abc&user=y\r\n\r\n\r\nnæste linje",
        });
        Assert.DoesNotContain("Hemmelig123", e.Message);
        Assert.DoesNotContain("abcdefghijklmnop", e.Message);
        Assert.DoesNotContain("token=abc", e.Message);
        Assert.Contains("https://192.168.1.5/api/x?", e.Message);
        Assert.Contains("\nnæste linje", e.Message);
        Assert.Equal(SysLogLevels.Error, e.Level);
        var big = SysLogPolicy.Clean(new SysLogEvent { Time = DateTime.UtcNow, Source = new string('s', 500), Message = new string('x', 5000) });
        Assert.Equal(SysLogPolicy.MaxMessage, big.Message.Length);
        Assert.Equal(SysLogPolicy.MaxSource, big.Source.Length);
    }

    [Theory]
    [InlineData("Microsoft-Windows-WER-SystemErrorReporting", 1001, 2, SysLogKinds.BugCheck)]
    [InlineData("BugCheck", 1001, 2, SysLogKinds.BugCheck)]
    [InlineData("Microsoft-Windows-WHEA-Logger", 18, 2, SysLogKinds.WheaFatal)]
    [InlineData("Microsoft-Windows-WHEA-Logger", 19, 3, SysLogKinds.WheaCorrected)]
    [InlineData("disk", 7, 2, SysLogKinds.DiskBadBlock)]
    [InlineData("disk", 51, 3, SysLogKinds.DiskPaging)]
    [InlineData("disk", 153, 3, SysLogKinds.DiskRetry)]
    [InlineData("Ntfs", 55, 2, SysLogKinds.NtfsCorrupt)]
    [InlineData("Microsoft-Windows-WindowsUpdateClient", 20, 2, SysLogKinds.UpdateFailed)]
    [InlineData("Microsoft-Windows-DistributedCOM", 10016, 2, SysLogKinds.DcomNoise)]
    [InlineData("EventLog", 6008, 2, SysLogKinds.Shutdown6008)]
    [InlineData("Microsoft-Windows-Kernel-Power", 41, 1, SysLogKinds.UnexpectedShutdown)]
    [InlineData("Service Control Manager", 7000, 2, null)]
    public void Known_windows_events_are_recognised(string source, int id, byte level, string? kind) =>
        Assert.Equal(kind, SysLogKinds.Of(WinEventLog.Map(source, id, level, DateTime.UtcNow, "System", "x", 1)));

    [Theory]
    [InlineData("Out of memory: Killed process 812 (java) total-vm:9GB", SysLogKinds.Oom)]
    [InlineData("java invoked oom-killer: gfp_mask=0x140cca", null)]   // samme OOM giver flere linjer; kun "Killed process" tæller
    [InlineData("Kernel panic - not syncing: Fatal exception", SysLogKinds.KernelPanic)]
    [InlineData("EXT4-fs error (device sda1): ext4_find_entry", SysLogKinds.FsError)]
    [InlineData("Buffer I/O error on dev sdb1, logical block 0", SysLogKinds.IoError)]
    [InlineData("python3[812]: segfault at 0 ip 00007f", SysLogKinds.Segfault)]
    [InlineData("Started Daily apt download activities.", null)]
    public void Known_linux_messages_are_recognised(string message, string? kind) =>
        Assert.Equal(kind, SysLogKinds.Of(new SysLogEvent { Source = "kernel", Log = "kernel", Message = message }));

    [Fact] public void Group_key_ignores_numbers_and_uses_the_windows_id()
    {
        var a = new SysLogEvent { Source = "nginx", Message = "upstream 192.168.1.12:8080 timed out after 30s (pid 1234)" };
        var b = new SysLogEvent { Source = "nginx", Message = "upstream 192.168.1.13:8080 timed out after 31s (pid 999)" };
        Assert.Equal(SysLogPolicy.GroupKey(a), SysLogPolicy.GroupKey(b));
        Assert.Equal("disk|7", SysLogPolicy.GroupKey(new SysLogEvent { Source = "Disk", EventId = 7, Message = "x" }));
        Assert.Equal("kernel|oom", SysLogPolicy.GroupKey(new SysLogEvent { Source = "kernel", Message = "Out of memory: Killed process 1 (a)" }));
    }

    [Fact] public void Prioritize_keeps_the_worst_and_newest_and_caps_the_count()
    {
        var now = DateTime.UtcNow;
        var list = Enumerable.Range(0, 500).Select(i => new SysLogEvent { Time = now.AddMinutes(-i), Level = SysLogLevels.Warning, Source = "a", Message = "w" })
            .Append(new SysLogEvent { Time = now.AddHours(-20), Level = SysLogLevels.Critical, Source = "Microsoft-Windows-Kernel-Power", EventId = 41 }).ToList();
        var p = SysLogPolicy.Prioritize(list);
        Assert.Equal(SysLogPolicy.MaxPerReport, p.Count);
        Assert.Contains(p, e => e.Level == SysLogLevels.Critical);
        Assert.True(p.Zip(p.Skip(1)).All(x => x.First.Time <= x.Second.Time));     // i tidsorden
    }

    [Fact] public void Schedule_collects_at_most_every_interval_and_resumes_after_a_failed_send()
    {
        var s = new SysLogSchedule(TimeSpan.FromMinutes(10));
        var t0 = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(s.Due(t0));
        Assert.Equal(t0.AddHours(-24), s.Since(t0));
        s.Tried(t0);
        Assert.False(s.Due(t0.AddMinutes(5)));
        Assert.True(s.Due(t0.AddMinutes(10)));
        Assert.Equal(t0.AddHours(-14), s.Since(t0.AddHours(10)));     // ikke sendt: samme start igen
        s.Sent(t0);
        Assert.Equal(t0, s.Since(t0.AddMinutes(10)));
        Assert.Equal(t0.AddDays(10).AddHours(-48), s.Since(t0.AddDays(10)));   // aldrig mere end to døgn tilbage
    }
}

public class SysLogRuleTests
{
    private static readonly DateTime Now = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
    private static SysLogHit H(string kind, double hoursAgo) => new(Now.AddHours(-hoursAgo), kind, SysLogLevels.Error);
    private static List<Finding> F(params SysLogHit[] hits) => SysLogRules.Findings(hits, Now);

    [Fact] public void Nothing_from_an_empty_or_harmless_log() { Assert.Empty(F()); Assert.Empty(F(H(SysLogKinds.DcomNoise, 1), H(SysLogKinds.Segfault, 1))); }

    [Fact] public void A_blue_screen_warns_and_two_are_critical_but_old_ones_are_forgotten()
    {
        Assert.Equal("warning", Assert.Single(F(H(SysLogKinds.BugCheck, 5))).Severity);
        var two = Assert.Single(F(H(SysLogKinds.BugCheck, 5), H(SysLogKinds.KernelPanic, 30)));
        Assert.Equal(("syslog:crash", "critical"), (two.Key, two.Severity));
        Assert.Contains("senest", two.Detail);
        Assert.Empty(F(H(SysLogKinds.BugCheck, 24 * 4)));
    }

    [Fact] public void One_unexpected_shutdown_is_a_power_cut_two_in_a_week_warn()
    {
        Assert.Empty(F(H(SysLogKinds.UnexpectedShutdown, 2)));
        Assert.Equal("syslog:power", Assert.Single(F(H(SysLogKinds.UnexpectedShutdown, 2), H(SysLogKinds.UnexpectedShutdown, 100))).Key);
        Assert.Empty(F(H(SysLogKinds.UnexpectedShutdown, 2), H(SysLogKinds.UnexpectedShutdown, 24 * 8)));
    }

    [Fact] public void Hardware_errors_fatal_is_critical_corrected_needs_many()
    {
        Assert.Equal("critical", Assert.Single(F(H(SysLogKinds.WheaFatal, 100))).Severity);
        Assert.Empty(F(Enumerable.Range(0, SysLogRules.WheaCorrectedPerDay - 1).Select(i => H(SysLogKinds.WheaCorrected, 1)).ToArray()));
        Assert.Equal("warning", Assert.Single(F(Enumerable.Range(0, SysLogRules.WheaCorrectedPerDay).Select(i => H(SysLogKinds.WheaCorrected, 1)).ToArray())).Severity);
    }

    [Fact] public void Disk_bad_blocks_warn_at_once_retries_only_when_repeated()
    {
        Assert.Equal("syslog:disk", Assert.Single(F(H(SysLogKinds.DiskBadBlock, 100))).Key);
        Assert.Empty(F(H(SysLogKinds.DiskRetry, 1), H(SysLogKinds.IoError, 2), H(SysLogKinds.DiskPaging, 3), H(SysLogKinds.StorportReset, 4)));
        Assert.Single(F(H(SysLogKinds.DiskRetry, 1), H(SysLogKinds.IoError, 2), H(SysLogKinds.DiskPaging, 3), H(SysLogKinds.StorportReset, 4), H(SysLogKinds.IoError, 5)));
    }

    [Fact] public void Out_of_memory_needs_two_kills_in_a_day()
    {
        Assert.Empty(F(H(SysLogKinds.Oom, 1)));
        Assert.Empty(F(H(SysLogKinds.Oom, 1), H(SysLogKinds.Oom, 30)));
        Assert.Equal("syslog:oom", Assert.Single(F(H(SysLogKinds.Oom, 1), H(SysLogKinds.Oom, 20))).Key);
    }

    [Fact] public void Acknowledged_log_alerts_return_on_a_new_event()
    {
        Assert.True(AlertAck.Reopens("syslog:crash", "warning", "warning", "1 gang – senest 19.11. 10:00", "1 gang – senest 20.11. 11:00"));
        Assert.False(AlertAck.Reopens("syslog:disk", "warning", "warning", "x – senest 19.11. 10:00", "y – senest 19.11. 10:00"));
    }

    [Fact] public void Frequent_log_alerts_stay_acknowledged_until_they_get_worse()
    {
        // rettede WHEA-fejl, gentagne diskfejl og OOM: nye hændelser hele tiden – en kvittering skal holde
        var soft = Assert.Single(F(Enumerable.Range(0, SysLogRules.WheaCorrectedPerDay + 5).Select(i => H(SysLogKinds.WheaCorrected, 1)).ToArray()));
        Assert.Equal("syslog:whea-corrected", soft.Key);
        Assert.DoesNotContain("senest", soft.Detail);
        Assert.False(AlertAck.Reopens(soft.Key, "warning", "warning", "55 rettede fejl det sidste døgn", soft.Detail));
        Assert.Equal("syslog:disk-errors", Assert.Single(F(Enumerable.Range(1, SysLogRules.DiskSoftPerDay).Select(i => H(SysLogKinds.DiskRetry, i)).ToArray())).Key);
        Assert.False(AlertAck.Reopens("syslog:oom", "warning", "warning", "Systemet har lukket programmer 2 gange det sidste døgn for at frigøre hukommelse",
            Assert.Single(F(H(SysLogKinds.Oom, 1), H(SysLogKinds.Oom, 2), H(SysLogKinds.Oom, 3))).Detail));
        Assert.True(AlertAck.Reopens("syslog:whea-corrected", "warning", "critical", "x", "y"));
    }

    [Theory]
    [InlineData("traps: chrome[4121] general protection fault ip:7f1c2a sp:7ffd error:0 in libc.so.6[7f1c00+195000]", null)]
    [InlineData("general protection fault, probably for non-canonical address 0xdead000000000100: 0000 [#1] PREEMPT SMP NOPTI", SysLogKinds.KernelPanic)]
    [InlineData("general protection fault: 0000 [#1] SMP PTI", SysLogKinds.KernelPanic)]
    [InlineData("I/O error, dev fd0, sector 0 op 0x0:(READ) flags 0x0 phys_seg 1 prio class 0", null)]
    [InlineData("Buffer I/O error on dev sr0, logical block 0, async page read", null)]
    [InlineData("blk_update_request: I/O error, dev loop3, sector 2048", null)]
    [InlineData("I/O error, dev sdb, sector 123456 op 0x0:(READ) flags 0x80700 phys_seg 1 prio class 0", SysLogKinds.IoError)]
    public void Linux_kinds_ignore_program_crashes_and_floppy_cdrom_noise(string message, string? kind) =>
        Assert.Equal(kind, SysLogKinds.Of(new SysLogEvent { Source = "kernel", Log = "kernel", Message = message }));

    [Fact] public void Dcom_noise_gets_no_priority_over_other_errors()
    {
        var t = Now.AddHours(-1);
        var dcom = Enumerable.Range(0, 5).Select(i => new SysLogEvent { Time = t.AddMinutes(i), Level = "error", Source = "Microsoft-Windows-DistributedCOM", EventId = 10016, Message = "d" });
        var app = new SysLogEvent { Time = t.AddMinutes(-30), Level = "error", Source = "Application Error", EventId = 1000, Message = "a" };
        var kept = SysLogPolicy.Prioritize(dcom.Append(app), 3);
        Assert.DoesNotContain(app, kept);   // ingen fordel til nogen af dem: de nyeste vinder
        Assert.All(kept, e => Assert.Equal(10016, e.EventId));
        var disk = new SysLogEvent { Time = t.AddMinutes(-30), Level = "error", Source = "disk", EventId = 7, Message = "b" };
        Assert.Contains(disk, SysLogPolicy.Prioritize(dcom.Append(disk), 3));   // en kendt, rigtig fejl går foran
    }

    [Fact] public void Odd_journal_lines_are_skipped_not_fatal()
    {
        Assert.Null(JournalLog.Line("{\"__REALTIME_TIMESTAMP\":\"99999999999999999999\",\"MESSAGE\":\"x\"}"));
        Assert.Null(JournalLog.Line("{\"__REALTIME_TIMESTAMP\":\"922337203685477580\",\"MESSAGE\":\"x\"}"));
        Assert.Null(JournalLog.Line("{\"__REALTIME_TIMESTAMP\":\"1700000000000000\",\"MESSAGE\":[1.5,2]}"));
    }

    [Fact] public void Groups_count_and_mark_hidden_types()
    {
        SysLogRow R(string key, string level, double hoursAgo, string msg = "m") => new() { GroupKey = key, Level = level, Source = key.Split('|')[0], Time = Now.AddHours(-hoursAgo), Message = msg, Log = "System" };
        var g = SysLogGroups.Build([R("disk|7", "warning", 5, "gammel"), R("disk|7", "error", 1, "ny"), R("dcom|10016", "error", 2)],
            new HashSet<string> { "dcom|10016" }, new HashSet<string>());
        Assert.Equal(2, g.Count);
        var disk = g[0];
        Assert.Equal((2, "error", "ny", Now.AddHours(-5)), (disk.Count, disk.Level, disk.Message, disk.First));
        Assert.Equal("machine", g[1].Ignored);
        Assert.True(SysLogGroups.LevelMatches("critical", "error"));
        Assert.False(SysLogGroups.LevelMatches("warning", "error"));
        Assert.True(SysLogGroups.Serious(null, "critical"));
        Assert.False(SysLogGroups.Serious(SysLogKinds.DcomNoise, "error"));
    }
}

/// <summary>Serverens lager: dubletter, alder og loft pr. maskine (rigtig SQLite-fil).</summary>
public class SysLogStoreTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"fd-syslog-{Guid.NewGuid():N}.db");
    private AppDb Db() => new(new DbContextOptionsBuilder<AppDb>().UseSqlite($"Data Source={_file}").Options);
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(_file); }

    [Fact] public async Task Ingest_skips_duplicates_old_events_and_secrets_and_caps_per_machine()
    {
        await using (var db = Db()) { await db.Database.EnsureCreatedAsync(); db.Machines.Add(new Machine { Id = 3, Name = "PC-1" }); await db.SaveChangesAsync(); }
        var now = DateTime.UtcNow;
        List<SysLogEvent> Batch() =>
        [
            new() { Time = now.AddMinutes(-5), Level = "error", Log = "System", Source = "disk", EventId = 7, Message = "bad block", RecordId = 1 },
            new() { Time = now.AddMinutes(-4), Level = "warning", Log = "Application", Source = "app", EventId = 1, Message = "password=Hemmelig", RecordId = 2 },
            new() { Time = now.AddDays(-40), Level = "error", Log = "System", Source = "old", EventId = 1, Message = "for gammel", RecordId = 3 },
        ];
        await using (var db = Db())
        {
            Assert.Equal(2, await SysLogStore.Ingest(db, 3, Batch(), now));
            Assert.Equal(0, await SysLogStore.Ingest(db, 3, Batch(), now));      // samme igen (fx efter genstart): ingen dubletter
            var rows = await db.SysLog.OrderBy(x => x.Time).ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Equal((SysLogKinds.DiskBadBlock, "disk|7"), (rows[0].Kind, rows[0].GroupKey));
            Assert.DoesNotContain("Hemmelig", rows[1].Message);
        }
        await using (var db = Db())
        {
            var many = Enumerable.Range(0, SysLogPolicy.MaxPerMachine + 150).Select(i => new SysLogRow
            {
                MachineId = 3, Time = now.AddSeconds(-i - 1000), Level = "warning", Source = "x", GroupKey = "x|1", Hash = $"h{i}", Message = "m",
            });
            db.SysLog.AddRange(many); await db.SaveChangesAsync();
            await SysLogStore.Trim(db, 3);
            Assert.Equal(SysLogPolicy.MaxPerMachine, await db.SysLog.CountAsync());
            Assert.True(await db.SysLog.AnyAsync(x => x.Source == "disk"));        // de nyeste bliver
            db.SysLog.Add(new SysLogRow { MachineId = 3, Time = now.AddDays(-31), Level = "error", Source = "y", GroupKey = "y|1", Hash = "gammel", Message = "m" });
            await db.SaveChangesAsync();
            Assert.Equal(1, await SysLogStore.Prune(db, now));
        }
    }
}

/// <summary>Live-loggen: billetter, rettigheder, hastighed, rensning og oprydning.</summary>
public class LiveLogTests
{
    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ListSource(IEnumerable<SysLogEvent> events, bool hang = false) : ILiveLogSource
    {
        private readonly Queue<SysLogEvent> _q = new(events);
        public bool Disposed;
        public async Task<SysLogEvent?> Next(CancellationToken ct)
        {
            if (_q.TryDequeue(out var e)) return e;
            if (hang) await Task.Delay(Timeout.Infinite, ct);
            return null;
        }
        public void Dispose() => Disposed = true;
    }

    private static async Task<(WebSocket A, WebSocket B)> Pair()
    {
        using var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); l.Start();
        var c = new System.Net.Sockets.TcpClient();
        await c.ConnectAsync(System.Net.IPAddress.Loopback, ((System.Net.IPEndPoint)l.LocalEndpoint).Port);
        var s = await l.AcceptTcpClientAsync();
        return (WebSocket.CreateFromStream(c.GetStream(), false, null, TimeSpan.Zero), WebSocket.CreateFromStream(s.GetStream(), true, null, TimeSpan.Zero));
    }

    private static async Task<List<byte[]>> ReadAll(WebSocket ws)
    {
        var o = new List<byte[]>(); var buf = new byte[64 * 1024];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var r = await ws.ReceiveAsync(buf, cts.Token);
            if (r.MessageType == WebSocketMessageType.Close) return o;
            o.Add(buf[..r.Count]);
            if (buf[0] == TerminalFrame.Exit) { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", cts.Token); return o; }
        }
    }

    [Fact] public void Tickets_must_be_well_formed()
    {
        Assert.True(LiveLogPolicy.TicketValid(new LiveLogTicket { SessionId = new string('a', 32), Secret = new string('b', 64) }));
        Assert.False(LiveLogPolicy.TicketValid(new LiveLogTicket { SessionId = new string('a', 32), Secret = new string('b', 64), Level = "rm -rf" }));
        Assert.False(LiveLogPolicy.TicketValid(new LiveLogTicket { SessionId = "x", Secret = new string('b', 64) }));
        Assert.False(LiveLogPolicy.TicketValid(null));
    }

    [Fact] public void Rate_limit_allows_a_burst_then_the_steady_rate()
    {
        var r = new LiveLogRate(perSecond: 10, burst: 20);
        var t = DateTime.UtcNow;
        Assert.Equal(20, Enumerable.Range(0, 50).Count(_ => r.Take(t)));
        Assert.Equal(30, r.TakeDropped());
        Assert.Equal(0, r.Dropped);
        Assert.Equal(10, Enumerable.Range(0, 50).Count(_ => r.Take(t.AddSeconds(1))));
    }

    [Fact] public async Task Pump_sends_cleaned_events_reports_dropped_lines_and_ends_with_exit()
    {
        var now = DateTime.UtcNow;
        var events = Enumerable.Range(0, LiveLogPolicy.Burst + 30).Select(i => new SysLogEvent { Time = now, Level = "error", Source = "app", Message = $"linje {i} token=hemmelig{i}" })
            .Prepend(new SysLogEvent { Time = now, Level = "critical", Source = "Microsoft-Windows-Kernel-Power", EventId = 41, Message = "rebooted" });
        var (agent, server) = await Pair();
        var src = new ListSource(events);
        var pump = LiveLogAgent.Pump(agent, src, () => now, CancellationToken.None);
        var frames = await ReadAll(server);
        await pump.WaitAsync(TimeSpan.FromSeconds(10));
        var data = frames.Where(f => f[0] == TerminalFrame.Data).Select(f => JsonSerializer.Deserialize<SysLogEvent>(f.AsSpan(1), new JsonSerializerOptions(JsonSerializerDefaults.Web))!).ToList();
        Assert.Equal(LiveLogPolicy.Burst, data.Count);       // resten er sprunget over (samme tidspunkt = ingen nye tokens)
        Assert.Equal(SysLogKinds.UnexpectedShutdown, data[0].Kind);
        Assert.DoesNotContain(data, e => e.Message.Contains("hemmelig"));
        Assert.Equal(TerminalFrame.Exit, frames[^1][0]);
    }

    [Fact] public async Task Pump_stops_when_the_page_closes_and_after_the_lifetime()
    {
        var (agent, server) = await Pair();
        var src = new ListSource([], hang: true);
        var pump = LiveLogAgent.Pump(agent, src, () => DateTime.UtcNow, CancellationToken.None, TimeSpan.FromMilliseconds(300));
        Assert.Equal(TerminalFrame.Exit, (await ReadAll(server))[^1][0]);
        await pump.WaitAsync(TimeSpan.FromSeconds(10));     // levetiden er udløbet, selv om kilden aldrig svarer
        var (agent2, server2) = await Pair();
        var pump2 = LiveLogAgent.Pump(agent2, new ListSource([], hang: true), () => DateTime.UtcNow, CancellationToken.None);
        await server2.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        await pump2.WaitAsync(TimeSpan.FromSeconds(10));     // siden er lukket
        server.Dispose(); server2.Dispose();
    }

    [Fact] public void Live_log_tickets_never_reach_the_terminal_list()
    {
        var broker = new TerminalBroker(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), new TerminalGate(TimeProvider.System),
            TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<TerminalBroker>.Instance);
        var t = new LiveLogTicket { SessionId = new string('c', 32), Secret = new string('d', 64) };
        broker.Add(new TerminalSession { Id = t.SessionId, UserId = 1, UserName = "admin", Stamp = "s", Label = "pc", Kind = TerminalSession.LiveLogKind, MachineId = 7,
            UnlockToken = "", TicketHash = "x", AgentSecretHash = "y", LiveLog = t, Created = broker.Now, LastInput = broker.Now });
        Assert.Empty(broker.TakeTickets(7));
        Assert.Single(broker.TakeLiveLogs(7));
        Assert.Empty(broker.TakeLiveLogs(7));         // kun én gang
    }

    [Fact] public void Eligibility_requires_the_new_agent_and_the_signed_channel()
    {
        var m = new Machine { LiveLogCapable = true, JobTrust = AgentTrust.Pinned, Transport = "https" };
        Assert.Null(LiveLogEligibility.Why(m, SignedJobsMode.Sign));
        Assert.NotNull(LiveLogEligibility.Why(m, SignedJobsMode.Off));
        m.Transport = "http"; Assert.NotNull(LiveLogEligibility.Why(m, SignedJobsMode.Sign));
        m.Transport = "https"; m.LiveLogCapable = false; Assert.NotNull(LiveLogEligibility.Why(m, SignedJobsMode.Sign));
        m.LiveLogCapable = true; m.TerminalCapable = false; Assert.Null(LiveLogEligibility.Why(m, SignedJobsMode.Sign));    // terminal er ikke et krav
    }

    [Fact] public void Live_logs_only_come_from_a_response_signed_with_the_pinned_key()
    {
        using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256); var pem = k.ExportSubjectPublicKeyInfoPem();
        var nonce = CommandSigning.NewNonce();
        CommandEnvelope Env(ECDsa key, string p) => CommandSigning.Sign(key, p, new SignedCommands
        {
            MachineId = 5, Nonce = nonce, Issued = DateTime.UtcNow, Expires = DateTime.UtcNow.AddMinutes(5),
            LiveLogs = [new LiveLogTicket { SessionId = new string('a', 32), Secret = new string('b', 64) }],
        });
        var store = new TrustStore { JobKey = pem };
        Assert.Single(CommandGate.Apply(new CheckinResponse { Signed = Env(k, pem) }, store, 5, nonce, DateTime.UtcNow).LiveLogs);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Empty(CommandGate.Apply(new CheckinResponse { Signed = Env(other, other.ExportSubjectPublicKeyInfoPem()) }, store, 5, nonce, DateTime.UtcNow).LiveLogs);
        Assert.Empty(CommandGate.Apply(new CheckinResponse { Signed = Env(k, pem) }, new TrustStore(), 5, nonce, DateTime.UtcNow).LiveLogs);   // første kontakt
    }

    private sealed class TrustStore : ITrustStore { public string? JobKey { get; set; } public string? TlsPin { get; set; } }

    private static async Task<(ServiceProvider Sp, string File)> Services()
    {
        var file = Path.Combine(Path.GetTempPath(), $"fd-livelog-{Guid.NewGuid():N}.db");
        var sc = new ServiceCollection();
        sc.AddScoped<AuditService>();
        sc.AddDbContext<AppDb>(o => o.UseSqlite($"Data Source={file}"));
        var sp = sc.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        await db.Database.EnsureCreatedAsync();
        db.Users.Add(new User { Id = 1, Username = "admin", Role = Roles.Admin, SecurityStamp = "s" });
        db.Users.Add(new User { Id = 2, Username = "laeser", Role = Roles.Viewer, SecurityStamp = "s" });
        await db.SaveChangesAsync();
        return (sp, file);
    }

    [Fact] public async Task Sweep_stops_hidden_pages_old_sessions_and_users_who_are_not_admin()
    {
        var (sp, file) = await Services();
        var clock = new Clock(DateTimeOffset.UtcNow);
        var broker = new TerminalBroker(sp.GetRequiredService<IServiceScopeFactory>(), new TerminalGate(clock), clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<TerminalBroker>.Instance);
        TerminalSession S(char c, int user) => new() { Id = new string(c, 32), UserId = user, UserName = "u", Stamp = "s", Label = "pc", Kind = TerminalSession.LiveLogKind, MachineId = 1,
            UnlockToken = "", TicketHash = "x", Created = broker.Now, LastInput = broker.Now, TicketUsed = 1 };
        var quiet = S('a', 1); var busy = S('b', 1); var viewer = S('c', 2);
        foreach (var s in new[] { quiet, busy, viewer }) broker.Add(s);
        clock.Now += TimeSpan.FromMinutes(3); busy.LastInput = broker.Now;
        await broker.Sweep(CancellationToken.None);
        Assert.NotNull(quiet.CloseReason);     // intet livstegn i 2 minutter: siden er lukket eller skjult
        Assert.Null(busy.CloseReason);
        Assert.NotNull(viewer.CloseReason);    // ikke administrator
        clock.Now += TimeSpan.FromMinutes(28); busy.LastInput = broker.Now;
        await broker.Sweep(CancellationToken.None);
        Assert.Contains("30 minutter", busy.CloseReason);
        await sp.DisposeAsync(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(file);
    }

    [Fact] public async Task Browser_input_never_reaches_the_machine_and_the_session_is_audited_as_livelog()
    {
        var (sp, file) = await Services();
        var broker = new TerminalBroker(sp.GetRequiredService<IServiceScopeFactory>(), new TerminalGate(TimeProvider.System), TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<TerminalBroker>.Instance);
        var s = new TerminalSession { Id = new string('e', 32), UserId = 1, UserName = "admin", Stamp = "s", Label = "pc", Kind = TerminalSession.LiveLogKind, MachineId = 3,
            UnlockToken = "", TicketHash = "x", Created = broker.Now, LastInput = broker.Now };
        broker.Add(s);
        var (browser, serverSide) = await Pair(); var (agentServer, agentClient) = await Pair();
        s.AgentSocket.TrySetResult(agentServer);
        var run = broker.Run(s, serverSide, async ct => { var b = new AgentTerminalBackend(s); await b.Attach(ct); return b; });
        await browser.SendAsync(TerminalFrame.MakeData("rm -rf /"u8), WebSocketMessageType.Binary, true, default);
        await agentClient.SendAsync(LiveLogPolicy.Frame(new SysLogEvent { Time = DateTime.UtcNow, Level = "error", Source = "kernel", Message = "hej" }), WebSocketMessageType.Binary, true, default);
        var buf = new byte[4096];
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            while (true) { var r = await browser.ReceiveAsync(buf, cts.Token); if (r.Count > 1 && buf[0] == TerminalFrame.Data) { Assert.Contains("hej", Encoding.UTF8.GetString(buf, 1, r.Count - 1)); break; } }
        // agenten har intet fået fra browseren
        using (var quiet = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await agentClient.ReceiveAsync(buf, quiet.Token));
        broker.Close(s, "Stoppet.");
        await run.WaitAsync(TimeSpan.FromSeconds(10)); agentServer.Abort();
        using (var scope = sp.CreateScope())
        {
            var actions = scope.ServiceProvider.GetRequiredService<AppDb>().Audit.Select(a => a.Action).ToList();
            Assert.Contains("livelog.connect", actions); Assert.Contains("livelog.disconnect", actions);
            Assert.DoesNotContain("terminal.connect", actions);
        }
        await sp.DisposeAsync(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(file);
    }

    [Fact] public void Reading_is_for_users_while_hiding_and_live_are_admin_only()
    {
        var b = WebApplication.CreateBuilder();
        b.Services.AddDbContext<AppDb>(o => o.UseSqlite("Data Source=:memory:"));
        foreach (var t in new[] { typeof(SettingsService), typeof(HealthService), typeof(AuditService) }) b.Services.AddScoped(t);
        b.Services.AddSingleton<Live>(); b.Services.AddSingleton<TerminalBroker>();
        b.Services.AddAuthorization(); b.Services.AddLogging();
        var app = b.Build();
        app.MapSysLogEndpoints();
        var eps = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>().ToList();
        string Policy(string pattern, string? method = null) => eps.Where(e => e.RoutePattern.RawText == pattern
                && (method is null || e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods.Contains(method) == true))
            .Select(e => string.Join(",", e.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy))).Single();
        Assert.Equal("admin", Policy("/api/machines/{id:int}/syslog"));
        Assert.Equal("admin", Policy("/api/syslog/events"));
        Assert.Equal("admin", Policy("/api/syslog/ignore"));
        Assert.Equal("admin", Policy("/api/syslog/live", "POST"));
        Assert.Equal("admin", Policy("/api/syslog/live/{id}", "DELETE"));
        Assert.Equal("admin", Policy("/api/syslog/live/ws/{id}"));
        Assert.Contains(eps.Single(e => e.RoutePattern.RawText == "/api/syslog/live" ).Metadata, m => m.GetType().Name.Contains("RateLimit", StringComparison.Ordinal));
    }
}
