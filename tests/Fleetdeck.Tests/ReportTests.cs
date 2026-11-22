using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Endpoints;
using Fleetdeck.Server.Services;
using Fleetdeck.Server.Services.Reports;
using Fleetdeck.Shared;

namespace Fleetdeck.Tests;

/// <summary>Rapporter: hver rapports forespørgsel på en seedet database, CSV til dansk Excel, PDF, kun administratorer, planlagte rapporter – og ingen hemmeligheder.</summary>
public sealed class ReportTests : IDisposable
{
    private readonly SqliteConnection conn = new("Data Source=:memory:");
    private readonly AppDb db;
    private static readonly DateTime Now = DateTime.UtcNow;

    private static readonly string[] Secrets =
        ["RD-SECRET-PW", "AMT-SECRET", "TOKENHASH-SECRET", "RECOVERY-SECRET", "TOTP-SECRET", "PWHASH-SECRET", "CODE-SECRET", "REMOVAL-SECRET",
         "MQTT-SECRET", "PARAM-SECRET", "OUTPUT-SECRET", "ALERT-DETAIL-SECRET"];

    public ReportTests()
    {
        conn.Open();
        db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        db.Profiles.Add(new Profile { Id = 1, Name = "Børn" });
        db.Machines.Add(new Machine
        {
            Id = 1, Name = "STUE-PC", DisplayName = "Stue-pc", Platform = Platforms.Windows, ProfileId = 1, User = "anna", AgentVersion = "3.26.0", Ip = "192.168.1.21",
            LastSeen = Now, HealthScore = 70, HealthState = "critical", JobTrust = AgentTrust.Pinned, TlsPinned = true, OsUpdatesSince = Now.AddDays(-9),
            AmtPassword = "AMT-SECRET", TokenHash = "TOKENHASH-SECRET",
            FactsJson = Json.Ser(new SystemFacts { Os = "Windows 11 Pro", OsVersion = "24H2", Build = "26100", CpuName = "Intel i5-12400", RamGb = 15.8, Manufacturer = "Lenovo", Model = "M70q" }),
            HealthJson = Json.Ser(new HealthReport
            {
                Live = new LiveFacts { Volumes = [new VolumeInfo { Drive = "C:", SizeGb = 500, FreeGb = 200 }], Temps = new Temperatures { Cpu = 55, Gpu = 48 } },
                Remote = new RemoteDesktopInfo { Id = "123456789", Password = "RD-SECRET-PW" },
                Firewall = new() { ["Domain"] = true, ["Public"] = false },
                Defender = new DefenderStatus { Enabled = true, Realtime = true, SigAgeDays = 1 },
                Encryption = new EncryptionInfo { Kind = EncryptionKinds.BitLocker, Volumes = [new EncryptedVolume { Mount = "C:", System = true, Encrypted = true, Protected = true, RecoveryKeys = 1 }] },
                Updates = new UpdateSummary { Pending = 1, Titles = ["2026-11 Kumulativ opdatering"] },
                Disks = [new DiskHealth { Name = "Samsung 980", Media = "SSD", SizeGb = 500, Health = "Healthy", Wear = 85, Temp = 41, PowerOnHours = 9000 }],
            }),
        });
        db.Machines.Add(new Machine
        {
            Id = 2, Name = "KONTOR", DisplayName = "=cmd|' /C calc'!A0", Platform = Platforms.Linux, AgentVersion = "3.25.0", LastSeen = Now.AddDays(-3), HealthScore = 98, HealthState = "ok",
            FactsJson = Json.Ser(new SystemFacts { Os = "Ubuntu 24.04", RamGb = 8 }),
            HealthJson = Json.Ser(new HealthReport { Encryption = new EncryptionInfo { Kind = EncryptionKinds.Luks, Volumes = [new EncryptedVolume { Mount = "/", System = true, Encrypted = false }] } }),
        });
        db.Software.AddRange(
            new SoftwareItem { MachineId = 1, Name = "7-Zip", Version = "24.07", Publisher = "Igor Pavlov" },
            new SoftwareItem { MachineId = 2, Name = "7-Zip", Version = "24.07", Publisher = "Igor Pavlov" },
            new SoftwareItem { MachineId = 1, Name = "Google Chrome", Version = "130.0" },
            new SoftwareItem { MachineId = 2, Name = "Google Chrome", Version = "131.0" },
            new SoftwareItem { MachineId = 1, Name = "@Discord; \"beta\"", Version = "-1" });
        db.Upgrades.Add(new UpgradeItem { MachineId = 1, Name = "7-Zip", WingetId = "7zip.7zip", Current = "24.07", Available = "24.08", FirstSeen = Now.AddDays(-41) });
        db.Jobs.AddRange(
            new Job { MachineId = 1, Type = JobTypes.Upgrade, Title = "Opdatér 7-Zip", Status = JobStatus.Failed, Created = Now.AddDays(-2), Finished = Now.AddDays(-2), ExitCode = 1 },
            new Job { MachineId = 1, Type = JobTypes.Install, Title = "Installér Chrome", Status = JobStatus.Success, Created = Now.AddDays(-3), Started = Now.AddDays(-3), Finished = Now.AddDays(-3).AddMinutes(2) },
            new Job { MachineId = 1, Type = JobTypes.Script, Title = "Script: ryd op", Status = JobStatus.Success, Created = Now.AddDays(-1), ParamsJson = "{\"script\":\"PARAM-SECRET\"}", Output = "OUTPUT-SECRET" },
            new Job { MachineId = 2, Type = JobTypes.Script, Title = "Gammel", Status = JobStatus.Failed, Created = Now.AddDays(-40), Finished = Now.AddDays(-40) },
            new Job { MachineId = 2, Type = JobTypes.WindowsUpdate, Title = "Systemopdatering", Status = JobStatus.Failed, Created = Now.AddDays(-40), Finished = Now.AddDays(-40) });
        db.Alerts.AddRange(
            new Alert { MachineId = 1, Key = "disk:C:", Severity = "critical", Title = "Næsten fuld disk C:", Detail = "ALERT-DETAIL-SECRET", FirstSeen = Now.AddDays(-1) },
            new Alert { MachineId = 1, Key = "disk:D:", Severity = "warning", Title = "Lidt plads på D:", FirstSeen = Now.AddDays(-8), Resolved = Now.AddDays(-5) },
            new Alert { MachineId = 2, Key = "winupdate", Severity = "info", Title = "Systemopdateringer venter", FirstSeen = Now.AddDays(-2) });
        db.RecoveryKeys.Add(new RecoveryKey { MachineId = 1, Kind = EncryptionKinds.BitLocker, Volume = "C:", ProtectorId = "{abc}", Protected = "RECOVERY-SECRET" });
        db.Users.AddRange(
            new User { Username = "anna", Role = Roles.Admin, PasswordHash = "PWHASH-SECRET", TotpSecret = "TOTP-SECRET", TotpEnabledAt = Now.AddDays(-30), RecoveryCodes = ["CODE-SECRET", "x"] },
            new User { Username = "bent", Role = Roles.Viewer, PasswordHash = "PWHASH-SECRET" });
        var today = DateOnly.FromDateTime(ReportFormat.Local(Now));
        db.HealthDaily.AddRange(
            new HealthDaily { MachineId = 1, Day = today.AddDays(-1), Score = 60, State = "critical", Samples = 2, ScoreSum = 140 },
            new HealthDaily { MachineId = 1, Day = today.AddDays(-2), Score = 90, State = "ok", Samples = 1, ScoreSum = 90 },
            new HealthDaily { MachineId = 2, Day = today.AddDays(-2), Score = 100, State = "ok", Samples = 1, ScoreSum = 100 });
        db.Metrics.Add(new Metric { MachineId = 1, Ts = Now.AddDays(-1), CpuTemp = 82, GpuTemp = 60 });
        db.Ipads.Add(new Ipad { Id = 1, Name = "Emils iPad", Owner = "Emil", Kind = IpadKinds.Child, ClientId = "c1", RemovalPassword = "REMOVAL-SECRET" });
        db.Ipads.Add(new Ipad { Id = 2, Name = "Stue-iPad", Kind = IpadKinds.Family, ClientId = "c2" });
        var slot0 = new DateTime(Now.Year, Now.Month, Now.Day, Now.Hour, Now.Minute / 5 * 5, 0, DateTimeKind.Utc).AddHours(-1);
        for (var i = 0; i < 12; i++) db.IpadSlots.Add(new IpadSlot { IpadId = 1, Service = "*", Slot = slot0.AddMinutes(-5 * i) });
        for (var i = 0; i < 6; i++) db.IpadSlots.Add(new IpadSlot { IpadId = 1, Service = "youtube", Slot = slot0.AddMinutes(-5 * i) });
        for (var i = 0; i < 4; i++) db.IpadSlots.Add(new IpadSlot { IpadId = 2, Service = "*", Slot = slot0.AddMinutes(-5 * i) });   // familie-iPad: ikke med
        db.Settings.Add(new Setting { Key = "mqtt_pass", Value = "MQTT-SECRET" });
        db.SaveChanges();
    }

    public void Dispose() { db.Dispose(); conn.Dispose(); }

    private Task<ReportResult> Run(string name, ReportFilter? f = null) => ReportCatalog.Run(db, ReportCatalog.Find(name)!, f ?? new(), Now);

    private static ReportSection Sec(ReportResult r, string key) => r.Sections.Single(s => s.Key == key);
    private static object? Cell(ReportSection s, object?[] row, string col) => row[s.Columns.FindIndex(c => c.Key == col)];

    [Fact]
    public async Task Inventory_lists_machines_with_hardware_and_filters_by_profile_and_ids()
    {
        var r = await Run("inventory");
        var s = Sec(r, "machines");
        Assert.Equal(2, s.Rows.Count);
        var stue = s.Rows.Single(x => (string)x[0]! == "Stue-pc");
        Assert.Equal("Windows 11 Pro", Cell(s, stue, "os"));
        Assert.Equal("24H2 (26100)", Cell(s, stue, "version"));
        Assert.Equal(15.8, Cell(s, stue, "ram"));
        Assert.Equal(500.0, Cell(s, stue, "disk"));
        Assert.Equal(60.0, Cell(s, stue, "diskUsed"));
        Assert.Equal("Børn", Cell(s, stue, "profile"));
        Assert.Equal("3.26.0", Cell(s, stue, "agent"));
        Assert.Contains(r.Stats, x => x.Label == "Ældre agent" && x.Value == "1");
        Assert.Contains(r.Stats, x => x.Label == "Ikke set i 24 t" && x.Value == "1");
        Assert.Single(Sec(await Run("inventory", new(Profile: 1)), "machines").Rows);
        Assert.Single(Sec(await Run("inventory", new(Machines: [2])), "machines").Rows);
    }

    [Fact]
    public async Task Software_counts_per_program_and_version_and_filters_by_name()
    {
        var r = await Run("software");
        var lic = Sec(r, "licenses");
        var zip = lic.Rows.Single(x => (string)x[0]! == "7-Zip");
        Assert.Equal(2, Cell(lic, zip, "count"));
        Assert.Equal(2, lic.Rows.Count(x => (string)x[0]! == "Google Chrome"));   // to versioner, hver på én maskine
        Assert.Equal(5, Sec(r, "installed").Rows.Count);
        Assert.Contains(r.Stats, x => x.Label == "Flere versioner" && x.Value == "1");
        var f = await Run("software", new(Q: "chrome"));
        Assert.All(Sec(f, "installed").Rows, x => Assert.Equal("Google Chrome", x[1]));
        Assert.Equal(2, Sec(f, "installed").Rows.Count);
    }

    [Fact]
    public async Task Updates_show_waiting_time_and_failed_in_period()
    {
        var r = await Run("updates");
        var p = Sec(r, "pending");
        var zip = p.Rows.Single(x => (string)x[2]! == "7-Zip");
        Assert.Equal(41.0, Cell(p, zip, "waited"));
        var os = p.Rows.Single(x => (string)x[1]! == "System");
        Assert.Equal(9.0, Cell(p, os, "waited"));
        var failed = Sec(r, "failed");
        Assert.Single(failed.Rows);                                          // windows_update for 40 dage siden er uden for 30 dage
        Assert.Equal("Opdatér 7-Zip", Cell(failed, failed.Rows[0], "title"));
        Assert.Equal(2, Sec(await Run("updates", new(Days: 90)), "failed").Rows.Count);
        Assert.Contains(r.Stats, x => x.Label == "Ældste (dage)" && x.Value == "41");
    }

    [Fact]
    public async Task Security_reports_status_counts_users_and_open_alerts()
    {
        var r = await Run("security");
        var m = Sec(r, "machines");
        var stue = m.Rows.Single(x => (string)x[0]! == "Stue-pc");
        Assert.Equal("Fra: Public", Cell(m, stue, "firewall"));
        Assert.Equal("Defender", Cell(m, stue, "av"));
        Assert.Equal("BitLocker: krypteret", Cell(m, stue, "encryption"));
        Assert.Equal(1, Cell(m, stue, "keys"));
        Assert.Equal("Nøgle pinnet", Cell(m, stue, "trust"));
        Assert.Equal(1, Cell(m, stue, "critical"));
        var kontor = m.Rows.Single(x => (string)x[0]! != "Stue-pc");
        Assert.Equal("LUKS: ikke krypteret", Cell(m, kontor, "encryption"));
        Assert.Equal("Ældre agent", Cell(m, kontor, "trust"));
        var u = Sec(r, "users");
        Assert.Equal(true, Cell(u, u.Rows.Single(x => (string)x[0]! == "anna"), "totp"));
        Assert.Equal(2, Cell(u, u.Rows.Single(x => (string)x[0]! == "anna"), "codes"));
        Assert.Equal(false, Cell(u, u.Rows.Single(x => (string)x[0]! == "bent"), "totp"));
        var a = Sec(r, "alerts");
        Assert.Equal(["critical", "info"], a.Rows.Select(x => (string)x[0]!).ToArray());   // løste alarmer er ikke med; sorteret efter alvor
        Assert.Contains(r.Stats, x => x.Label == "Uden 2FA" && x.Value == "1 af 2");
    }

    [Fact]
    public async Task Health_has_history_top_alerts_disks_and_temperatures()
    {
        var r = await Run("health");
        var m = Sec(r, "machines");
        var stue = m.Rows.Single(x => (string)x[0]! == "Stue-pc");
        Assert.Equal(60, Cell(m, stue, "min"));
        Assert.Equal(77.0, Cell(m, stue, "avg"));      // (140 + 90) / 3
        Assert.Equal(1, Cell(m, stue, "critDays"));
        Assert.Equal(2, Sec(r, "history").Rows.Count);
        var top = Sec(r, "topAlerts");
        var disk = top.Rows.Single(x => (int)x[2]! == 2);    // disk:C: og disk:D: samles
        Assert.Equal("critical", Cell(top, disk, "severity"));
        Assert.Equal(1, Cell(top, disk, "open"));
        Assert.Equal(85.0, Cell(Sec(r, "disks"), Sec(r, "disks").Rows[0], "wear"));
        var t = Sec(r, "temps");
        Assert.Equal(82.0, Cell(t, t.Rows.Single(), "cpuMax"));
        Assert.Contains(r.Stats, x => x.Label == "Slidte diske" && x.Value == "1");
    }

    [Fact]
    public async Task Jobs_give_success_rates_per_type_and_machine()
    {
        var r = await Run("jobs");
        Assert.Equal(3, Sec(r, "jobs").Rows.Count);           // jobs fra for 40 dage siden er uden for perioden
        var types = Sec(r, "types");
        var script = types.Rows.Single(x => (string)x[0]! == JobTypes.Script);
        Assert.Equal(100.0, Cell(types, script, "rate"));
        var machines = Sec(r, "machines");
        var stue = machines.Rows.Single();
        Assert.Equal(3, Cell(machines, stue, "total"));
        Assert.Equal(66.7, Cell(machines, stue, "rate"));
        Assert.Equal(2.0, Cell(Sec(r, "jobs"), Sec(r, "jobs").Rows.Single(x => (string)x[2]! == JobTypes.Install), "minutes"));
        var all = await Run("jobs", new(Days: 90));
        Assert.Equal(5, Sec(all, "jobs").Rows.Count);
        Assert.Equal(50.0, Cell(Sec(all, "types"), Sec(all, "types").Rows.Single(x => (string)x[0]! == JobTypes.Script), "rate"));
    }

    [Fact]
    public async Task Ipads_sum_minutes_per_child_week_and_service()
    {
        var r = await Run("ipads");
        var w = Sec(r, "weeks");
        Assert.All(w.Rows, x => Assert.Equal("Emil (Emils iPad)", x[0]));   // kun børne-iPads
        Assert.Equal(60, w.Rows.Sum(x => (int)Cell(w, x, "minutes")!));
        var s = Sec(r, "services");
        Assert.Equal(30, s.Rows.Where(x => (string)x[2]! == "youtube").Sum(x => (int)x[3]!));
        Assert.Contains(r.Stats, x => x.Label == "Mest brugt" && x.Value == "youtube");
    }

    [Fact]
    public async Task Csv_is_excel_safe_bom_semicolons_decimal_comma_and_formula_protection()
    {
        var csv = await ReportExport.Csv(await Run("inventory"), "machines");
        Assert.StartsWith("﻿", csv);
        var lines = csv.TrimStart('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("Maskine;Platform;OS;Version;", lines[0]);
        Assert.Equal(3, lines.Length);
        // formel i maskinens navn neutraliseres (og citeres, fordi den indeholder ')
        Assert.Contains(lines, l => l.StartsWith("'=cmd|' /C calc'!A0;", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.StartsWith("=", StringComparison.Ordinal));
        var stue = lines.Single(l => l.StartsWith("Stue-pc;", StringComparison.Ordinal));
        Assert.Contains(";15,8;", stue);    // decimalkomma, ikke citeret
        Assert.Contains(";60;", stue);

        var sw = await ReportExport.Csv(await Run("software"), "installed");
        Assert.Contains("\"'@Discord; \"\"beta\"\"\"", sw);   // @ beskyttes; semikolon og citationstegn citeres
        Assert.Contains(";'-1;", sw);                            // tekst der starter med - (versionen) beskyttes
        foreach (var c in new[] { "=", "+", "-", "@" }) Assert.Equal("'" + c + "x", ExcelCsv.Cell(c + "x"));

        // flere tabeller: titel og en tom linje imellem
        var all = await ReportExport.Csv(await Run("updates"));
        Assert.Contains("Ventende opdateringer\r\nMaskine;Type;", all);
        Assert.Contains("\r\n\r\nPr. maskine\r\n", all);
    }

    [Fact]
    public async Task Pdf_is_a_valid_document_with_danish_letters()
    {
        var pdf = ReportExport.Pdf(await Run("security"));
        var text = Encoding.Latin1.GetString(pdf);
        Assert.StartsWith("%PDF-1.4", text);
        Assert.EndsWith("%%EOF\n", text);
        Assert.Contains("/BaseFont /Helvetica", text);
        Assert.Contains("Fleetdeck-brugere", text);
        Assert.Contains("N\u00f8gle pinnet", text);   // ø som WinAnsi-byte
        // xref peger på objekterne
        var start = int.Parse(text[(text.LastIndexOf("startxref\n", StringComparison.Ordinal) + 10)..].Split('\n')[0]);
        Assert.StartsWith("xref", text[start..]);
        // mange rækker giver flere sider
        var big = new ReportResult { Name = "x", Title = "Stor", Generated = Now, Sections = [new ReportSection { Key = "a", Title = "A", Columns = [new("a", "A")], Rows = Enumerable.Range(0, 300).Select(i => new object?[] { $"række {i}" }).ToList() }] };
        Assert.Contains("Side 3 af", Encoding.Latin1.GetString(ReportExport.Pdf(big)));
    }

    [Fact]
    public async Task No_secrets_or_keys_in_any_export()
    {
        foreach (var def in ReportCatalog.All)
        {
            var r = await ReportCatalog.Run(db, def, new(Days: 365), Now);
            var outputs = new[]
            {
                await ReportExport.Csv(r), Encoding.Latin1.GetString(ReportExport.Pdf(r)),
                JsonSerializer.Serialize(ReportExport.Page(r, null, 1, 5000), Json.Web), r.Summary,
            };
            foreach (var o in outputs)
                foreach (var s in Secrets) Assert.False(o.Contains(s, StringComparison.Ordinal), $"{def.Name} indeholder {s}");
        }
        var sched = new ReportSchedule { Name = "Alt", Reports = ReportCatalog.All.Select(d => d.Name).ToList() };
        var msg = await ReportScheduler.Compose(db, sched, "https://fd.example/rapporter", Now) + await ReportScheduler.ComposeSummary(db, new WeeklySummary { Enabled = true }, "", Now);
        foreach (var s in Secrets) Assert.DoesNotContain(s, msg);
    }

    [Fact]
    public void Json_pages_rows()
    {
        var r = new ReportResult { Name = "x", Title = "X", Generated = Now, Sections = [new ReportSection { Key = "a", Title = "A", Columns = [new("n", "N", "num")], Rows = Enumerable.Range(1, 25).Select(i => new object?[] { i }).ToList() }] };
        var json = JsonSerializer.SerializeToElement(ReportExport.Page(r, "a", 3, 10), Json.Web);
        var sec = json.GetProperty("sections")[0];
        Assert.Equal(25, sec.GetProperty("total").GetInt32());
        Assert.Equal([21, 22, 23, 24, 25], sec.GetProperty("rows").EnumerateArray().Select(x => x[0].GetInt32()).ToArray());
        Assert.Empty(JsonSerializer.SerializeToElement(ReportExport.Page(r, "findes-ikke", 1, 10), Json.Web).GetProperty("sections").EnumerateArray());
    }

    [Fact]
    public void Report_endpoints_are_admin_only()
    {
        var b = WebApplication.CreateBuilder();
        b.Services.AddDbContext<AppDb>(o => o.UseSqlite("Data Source=:memory:"));
        b.Services.AddScoped<AuditService>();
        b.Services.AddScoped<SettingsService>();
        b.Services.AddAuthorization();
        var app = b.Build();
        app.MapReportEndpoints();
        var eps = ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().ToList();
        Assert.Equal(5, eps.Count);
        foreach (var e in eps)
        {
            Assert.StartsWith("/api/reports", e.RoutePattern.RawText);
            Assert.Contains(e.Metadata.GetOrderedMetadata<IAuthorizeData>(), a => a.Policy == "admin");
            Assert.Null(e.Metadata.GetMetadata<IAllowAnonymous>());
        }
    }

    [Fact]
    public void Json_preview_is_audited_at_most_once_per_window()
    {
        var t = new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc);
        var user = "test-" + Guid.NewGuid().ToString("N");
        Assert.True(ReportEndpoints.ShouldAuditJson(user, "inventory", t));
        Assert.False(ReportEndpoints.ShouldAuditJson(user, "inventory", t.AddMinutes(5)));
        Assert.True(ReportEndpoints.ShouldAuditJson(user, "software", t.AddMinutes(5)));
        Assert.True(ReportEndpoints.ShouldAuditJson(user, "inventory", t.AddMinutes(16)));
    }

    [Fact]
    public void Schedule_due_times_weekly_and_monthly()
    {
        var tz = IpadEngine.Tz;
        DateTime Utc(int y, int mo, int d, int h, int mi) => TimeZoneInfo.ConvertTimeToUtc(new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Unspecified), tz);
        // onsdag 18.11.2026 kl. 10 lokal tid
        var now = Utc(2026, 11, 18, 10, 0);
        Assert.Equal(Utc(2026, 11, 18, 7, 0), ReportScheduler.Previous("weekly", 2, "07:00", now));     // onsdag i dag
        Assert.Equal(Utc(2026, 11, 11, 12, 0), ReportScheduler.Previous("weekly", 2, "12:00", now));    // senere i dag → sidste uge
        Assert.Equal(Utc(2026, 11, 15, 18, 0), ReportScheduler.Previous("weekly", 6, "18:00", now));    // søndag
        Assert.Equal(Utc(2026, 11, 1, 7, 0), ReportScheduler.Previous("monthly", 1, "07:00", now));
        Assert.Equal(Utc(2026, 10, 20, 7, 0), ReportScheduler.Previous("monthly", 20, "07:00", now));
        Assert.True(ReportScheduler.IsDue("weekly", 2, "07:00", null, now));
        Assert.True(ReportScheduler.IsDue("weekly", 2, "07:00", Utc(2026, 11, 11, 7, 0), now));
        Assert.False(ReportScheduler.IsDue("weekly", 2, "07:00", Utc(2026, 11, 18, 7, 1), now));       // allerede sendt
        Assert.False(ReportScheduler.IsDue("weekly", 6, "18:00", null, now));                          // for længe siden: indhentes ikke
    }

    [Fact]
    public async Task Scheduled_job_sends_due_reports_and_weekly_summary_once()
    {
        var s = new SettingsService(db);
        await s.Set("server_url", "https://fd.example/");
        var now = DateTime.UtcNow;
        var local = ReportFormat.Local(now).AddMinutes(-30);
        var day = ((int)local.DayOfWeek + 6) % 7;
        var time = local.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);   // dansk kultur skriver ellers 12.35
        await ReportScheduler.Save(s, new ReportScheduleConfig
        {
            Schedules =
            [
                new ReportSchedule { Id = "a", Name = "Ugentlig drift", Reports = ["updates", "security"], Frequency = "weekly", Day = day, Time = time, LastRun = now.AddDays(-7) },
                new ReportSchedule { Id = "b", Name = "Slået fra", Reports = ["inventory"], Frequency = "weekly", Day = day, Time = time, Enabled = false },
            ],
            Summary = new WeeklySummary { Enabled = false, Day = day, Time = time },
        });
        var sent = new List<(string Text, string Kind)>();
        var first = await ReportScheduler.RunDue(db, s, (t, k) => sent.Add((t, k)), now);
        Assert.Single(first);
        var msg = sent.Single().Text.Split('\n');
        Assert.Equal("📊 Ugentlig drift", msg[0]);
        Assert.StartsWith("Opdateringer: Ventende 2", msg[1]);
        Assert.StartsWith("Sikkerhed: Firewall fra 1", msg[2]);
        Assert.Equal("https://fd.example/rapporter?r=updates", msg[^1]);
        Assert.Equal("report", sent[0].Kind);
        Assert.Empty(await ReportScheduler.RunDue(db, s, (t, k) => sent.Add((t, k)), now.AddMinutes(1)));   // ikke to gange
        Assert.NotNull((await ReportScheduler.Load(s)).Schedules[0].LastRun);
        Assert.Contains(await db.Audit.Select(a => a.Action).ToListAsync(), a => a == "report.scheduled");

        // ugentlig opsummering: fra som standard, sender én gang når den slås til
        Assert.False(new ReportScheduleConfig().Summary.Enabled);
        var cfg = await ReportScheduler.Load(s);
        cfg.Summary.Enabled = true;
        await ReportScheduler.Save(s, cfg);
        sent.Clear();
        await ReportScheduler.RunDue(db, s, (t, k) => sent.Add((t, k)), now.AddMinutes(2));
        var sum = Assert.Single(sent).Text.Split('\n');
        Assert.Equal("📅 Ugentlig opsummering", sum[0]);
        Assert.Equal("iPads (7 dage): Emil 1 t 0 m (mest youtube 30 m)", sum[1]);
        Assert.StartsWith("Flåden: 2 maskiner · Ø 84/100 · 1 kritiske og 0 advarsler", sum[2]);
        Assert.Equal("https://fd.example/rapporter", sum[3]);
    }

    [Fact]
    public void Schedule_validation()
    {
        var ok = new ReportScheduleRequest([new ReportSchedule { Name = "x", Reports = ["inventory"], Frequency = "monthly", Day = 1, Time = "07:00" }], new WeeklySummary());
        Assert.Null(ReportEndpoints.Validate(ok));
        Assert.NotNull(ReportEndpoints.Validate(ok with { Schedules = [new ReportSchedule { Frequency = "monthly", Day = 31, Time = "07:00" }] }));
        Assert.NotNull(ReportEndpoints.Validate(ok with { Schedules = [new ReportSchedule { Frequency = "daily", Time = "07:00" }] }));
        Assert.NotNull(ReportEndpoints.Validate(ok with { Summary = new WeeklySummary { Time = "25:00" } }));
    }

    [Fact]
    public async Task Tracking_keeps_first_seen_and_os_update_age_and_daily_health()
    {
        var old = new[] { new UpgradeItem { WingetId = "a", Available = "2", FirstSeen = Now.AddDays(-5) }, new UpgradeItem { WingetId = "b", Available = "1", FirstSeen = Now.AddDays(-9) } };
        var fresh = new[] { new UpgradeItem { WingetId = "a", Available = "2" }, new UpgradeItem { WingetId = "b", Available = "2" }, new UpgradeItem { WingetId = "c", Available = "1" } };
        ReportTracking.CarryFirstSeen(fresh, old, Now);
        Assert.Equal(Now.AddDays(-5), fresh[0].FirstSeen);
        Assert.Equal(Now, fresh[1].FirstSeen);    // ny version = ny ventetid
        Assert.Equal(Now, fresh[2].FirstSeen);

        var m = new Machine();
        ReportTracking.TrackOsUpdates(m, new HealthReport { Updates = new UpdateSummary { Pending = 2 } }, Now.AddDays(-3));
        ReportTracking.TrackOsUpdates(m, new HealthReport { Updates = new UpdateSummary { Pending = 3 } }, Now);
        Assert.Equal(Now.AddDays(-3), m.OsUpdatesSince);
        ReportTracking.TrackOsUpdates(m, new HealthReport(), Now);            // ukendt: uændret
        Assert.NotNull(m.OsUpdatesSince);
        ReportTracking.TrackOsUpdates(m, new HealthReport { Updates = new UpdateSummary { Pending = 0 } }, Now);
        Assert.Null(m.OsUpdatesSince);

        await ReportTracking.RecordHealth(db, 2, 80, "warning", Now);
        await ReportTracking.RecordHealth(db, 2, 95, "ok", Now);
        await db.SaveChangesAsync();
        await ReportTracking.RecordHealth(db, 2, 70, "ok", Now);
        await db.SaveChangesAsync();
        var today = DateOnly.FromDateTime(ReportFormat.Local(Now));
        var row = await db.HealthDaily.SingleAsync(h => h.MachineId == 2 && h.Day == today);
        Assert.Equal((70, "warning", 3, 245), (row.Score, row.State, row.Samples, row.ScoreSum));
    }
}
