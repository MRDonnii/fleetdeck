using System.Security.Claims;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Endpoints;
using Fleetdeck.Server.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fleetdeck.Tests;

/// <summary>Status for styringen: kun opslag med klient-id viser, at en iPad bruger DNS-profilen.</summary>
public class IpadControlStatusTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
    private static Ipad Pad(double? hoursAgo = null, string type = DeviceTypes.Ipad) => new()
    {
        Id = 1, Name = "Testenhed", ClientId = "fdtest", DeviceType = type, ProfileSeen = hoursAgo is { } h ? Now.AddHours(-h) : null,
    };

    [Theory]
    [InlineData(0.1, IpadWatch.Active), InlineData(5.9, IpadWatch.Active), InlineData(6d, IpadWatch.Stale), InlineData(30d, IpadWatch.Stale), InlineData(null, IpadWatch.Missing)]
    public void States_follow_the_last_query_via_the_profile(double? hoursAgo, string state) => Assert.Equal(state, IpadWatch.Status(Pad(hoursAgo), Now, 24).State);

    [Fact]
    public void A_short_warning_period_also_shortens_active() => Assert.Equal(IpadWatch.Stale, IpadWatch.Status(Pad(3), Now, 2).State);

    [Fact]
    public void Queries_from_the_home_ip_do_not_count_as_using_the_profile()
    {
        var p = Pad(); p.LanIp = "192.168.1.50";
        IpadWatch.Observe(p, viaClient: null, viaAny: Now);
        Assert.Null(p.ProfileSeen);
        Assert.Equal(Now, p.LastSeen);
        var c = IpadWatch.Status(p, Now, 24);
        Assert.Equal(IpadWatch.Missing, c.State);
        Assert.Equal(Now, c.LanSeen);
    }

    [Fact]
    public void Queries_with_the_client_id_count_and_time_never_goes_backwards()
    {
        var p = Pad();
        IpadWatch.Observe(p, Now, Now);
        IpadWatch.Observe(p, Now.AddHours(-3), Now.AddHours(-3));
        Assert.Equal(Now, p.ProfileSeen);
        Assert.Equal(Now, p.LastSeen);
        Assert.Null(IpadWatch.Status(p, Now, 24).LanSeen);
    }

    [Fact]
    public void Log_rows_are_matched_on_the_exact_client_id_or_ip()
    {
        var log = JsonNode.Parse("""
            {"data":[
              {"client_id":"fdtest","client":"172.16.0.1","time":"2026-11-18T11:59:00Z","question":{"name":"a.dk"},"reason":"NotFilteredNotFound"},
              {"client_id":"fdother","client":"172.16.0.1","time":"2026-11-18T11:58:00Z","question":{"name":"b.dk"},"reason":"NotFilteredNotFound"},
              {"client":"192.168.1.50","time":"2026-11-18T11:57:00Z","question":{"name":"c.dk"},"reason":"FilteredBlackList"},
              {"client":"192.168.1.500","time":"2026-11-18T11:56:00Z","question":{"name":"d.dk"},"reason":"NotFilteredNotFound"}
            ]}
            """);
        Assert.Equal(["a.dk"], AdGuardClient.ParseLog(log, "fdtest", null).Select(r => r.Host));
        var lan = Assert.Single(AdGuardClient.ParseLog(log, "192.168.1.50", "192.168.1.50"));
        Assert.Equal("c.dk", lan.Host);
        Assert.True(lan.Blocked);
    }

    [Fact]
    public void Apple_tv_uses_its_ip_address_as_control()
    {
        var p = Pad(type: DeviceTypes.AppleTv); p.LastSeen = Now.AddMinutes(-5);
        Assert.Equal(IpadWatch.Active, IpadWatch.Status(p, Now, 24).State);
        Assert.Null(IpadWatch.Status(p, Now, 24).LanSeen);
    }

    [Theory]
    [InlineData(0, 24), InlineData(-5, 24), InlineData(double.NaN, 24), InlineData(0.5, 1), InlineData(48, 48), InlineData(99999, 720)]
    public void Warning_period_is_kept_within_bounds(double value, double expected) => Assert.Equal(expected, IpadWatch.Hours(value));
}

/// <summary>Advarslen: én pr. episode, løses af sig selv, kan sættes på pause og siger kun det, Fleetdeck ved.</summary>
public sealed class IpadWatchAlertTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
    private readonly SqliteConnection conn = new("Data Source=:memory:");
    private readonly AppDb db;
    private readonly List<(string Text, string Kind)> sent = [];

    public IpadWatchAlertTests()
    {
        conn.Open();
        db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
    }

    public void Dispose() { db.Dispose(); conn.Dispose(); }

    private Ipad Add(string name, double? hoursAgo, string type = DeviceTypes.Ipad)
    {
        var p = new Ipad { Name = name, ClientId = "fd" + name, DeviceType = type, ProfileSeen = hoursAgo is { } h ? Now.AddHours(-h) : null };
        if (type != DeviceTypes.Ipad && hoursAgo is { } t) p.LastSeen = Now.AddHours(-t);
        db.Ipads.Add(p); db.SaveChanges();
        return p;
    }

    private Task<(bool Changed, bool Held)> Run(DateTime? at = null, double hours = 24) => IpadWatch.Run(db, (t, k) => sent.Add((t, k)), hours, at ?? Now);

    [Fact]
    public async Task Raises_one_alert_per_episode_with_honest_wording()
    {
        var p = Add("Testenhed", 26);
        await Run(); await Run(Now.AddMinutes(1)); await Run(Now.AddHours(5));
        var m = Assert.Single(sent);
        Assert.Equal("warning", m.Kind);
        Assert.Contains("Testenhed har ikke brugt styringen i 26 timer. Den kan være slukket, eller profilen kan være fjernet.", m.Text);
        Assert.Equal(Now, p.WatchAlertSince);
    }

    [Fact]
    public async Task Does_not_alert_before_the_period_has_passed()
    {
        var p = Add("Testenhed", 23.9);
        await Run();
        Assert.Empty(sent);
        Assert.Null(p.WatchAlertSince);
    }

    [Fact]
    public async Task The_period_is_configurable()
    {
        Add("Testenhed", 5);
        await Run(hours: 4);
        Assert.Single(sent);
    }

    [Fact]
    public async Task Resolves_by_itself_when_seen_again_and_a_new_episode_alerts_again()
    {
        var p = Add("Testenhed", 26);
        await Run();
        IpadWatch.Observe(p, Now.AddHours(1), Now.AddHours(1));
        await Run(Now.AddHours(1));
        Assert.Null(p.WatchAlertSince);
        Assert.Equal("resolved", sent[^1].Kind);
        await Run(Now.AddHours(2));
        Assert.Equal(2, sent.Count);
        await Run(Now.AddHours(26));
        Assert.Equal(3, sent.Count);
        Assert.Equal("warning", sent[^1].Kind);
    }

    [Fact]
    public async Task No_alert_for_a_device_that_never_installed_the_profile()
    {
        var p = Add("Testenhed", null);
        p.LanIp = "192.168.1.50"; p.LastSeen = Now.AddHours(-48);
        await db.SaveChangesAsync();
        await Run();
        Assert.Empty(sent);
        Assert.Null(p.WatchAlertSince);
    }

    [Fact]
    public async Task Snooze_silences_the_device_and_closes_the_open_alert_without_a_push()
    {
        var p = Add("Testenhed", 26);
        await Run();
        IpadWatch.Configure(p, null, 7, Now);
        await db.SaveChangesAsync();
        Assert.Null(p.WatchAlertSince);
        await Run(Now.AddDays(3));
        Assert.Single(sent);
    }

    [Fact]
    public async Task After_a_snooze_the_device_gets_the_whole_period_to_return()
    {
        var p = Add("Testenhed", 26);
        IpadWatch.Configure(p, null, 7, Now);
        await db.SaveChangesAsync();
        await Run(Now.AddDays(7).AddHours(1));
        Assert.Empty(sent);
        await Run(Now.AddDays(8).AddHours(1));
        Assert.Single(sent);
    }

    [Fact]
    public async Task Apple_tv_is_off_by_default_but_can_be_turned_on()
    {
        var p = Add("Stue", 48, DeviceTypes.AppleTv);
        await Run();
        Assert.Empty(sent);
        IpadWatch.Configure(p, true, null, Now);
        await db.SaveChangesAsync();
        await Run();
        Assert.DoesNotContain("profilen", Assert.Single(sent).Text);
    }

    [Fact]
    public async Task Turning_the_warning_off_closes_the_alert()
    {
        var p = Add("Testenhed", 26);
        await Run();
        IpadWatch.Configure(p, false, null, Now);
        await db.SaveChangesAsync();
        await Run(Now.AddHours(1));
        Assert.Null(p.WatchAlertSince);
        Assert.Single(sent);
    }

    [Fact]
    public async Task All_devices_quiet_at_once_looks_like_a_dns_fault_and_sends_nothing()
    {
        Add("En", 30); Add("To", 40);
        var (_, held) = await Run();
        Assert.True(held);
        Assert.Empty(sent);
    }

    [Fact]
    public async Task One_quiet_device_among_active_ones_alerts()
    {
        Add("En", 30); Add("To", 1);
        var (_, held) = await Run();
        Assert.False(held);
        Assert.Single(sent);
    }

    [Fact]
    public void Monitor_needs_an_unbroken_half_hour_of_log_reads()
    {
        var m = new WatchMonitor();
        Assert.False(m.Ready(Now));
        m.Note(true, Now);
        Assert.False(m.Ready(Now.AddMinutes(29)));
        Assert.True(m.Ready(Now.AddMinutes(30)));
        m.Note(false, Now.AddMinutes(31));
        m.Note(true, Now.AddMinutes(32));
        Assert.False(m.Ready(Now.AddMinutes(40)));
    }

    // ---- forældre ser kun deres egne enheder

    private async Task<FamilyScope> Parent(params int[] ipads)
    {
        var u = new User { Username = "forælder", PasswordHash = "x", Role = Roles.Parent, Ipads = [.. ipads], Perms = [FamilyPerms.Rules] };
        db.Users.Add(u); await db.SaveChangesAsync();
        return await FamilyScope.Of(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, u.Id.ToString()), new Claim(ClaimTypes.Role, Roles.Parent)], "test")), db);
    }

    [Fact]
    public async Task A_parent_only_reaches_their_own_devices()
    {
        var mine = Add("Min", 1); var other = Add("Anden", 1);
        var sc = await Parent(mine.Id);
        Assert.True(sc.Sees(mine.Id));
        Assert.False(sc.Sees(other.Id));
        Assert.Null(IpadEndpoints.WatchDenied(sc, mine.Id));
        Assert.Equal(404, IpadEndpoints.WatchDenied(sc, other.Id));
    }

    [Fact]
    public void A_parent_without_the_rules_permission_cannot_change_anything() =>
        Assert.Equal(403, IpadEndpoints.WatchDenied(new FamilyScope(false, false, [3], [FamilyPerms.Pause]), 3));

    // ---- migreringen må kun overføre det, der med sikkerhed kom fra klient-id'et

    [Fact]
    public void Migration_only_backfills_devices_that_cannot_have_been_seen_by_ip()
    {
        using var c = new SqliteConnection("Data Source=:memory:"); c.Open();
        using var m = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite(c).Options);
        var migrator = m.GetInfrastructure().GetRequiredService<IMigrator>();
        migrator.Migrate("20261118103100_Reports");
        Insert(c, 1, "ipad", null); Insert(c, 2, "ipad", "192.168.1.50"); Insert(c, 3, "appletv", "192.168.1.51");
        migrator.Migrate();
        var seen = m.Ipads.AsNoTracking().OrderBy(i => i.Id).Select(i => i.ProfileSeen).ToList();
        Assert.NotNull(seen[0]);
        Assert.Null(seen[1]);
        Assert.Null(seen[2]);
        Assert.Empty(m.Ipads.AsNoTracking().First().SetupDone);
    }

    private static void Insert(SqliteConnection c, int id, string type, string? lan)
    {
        var cols = new List<(string Name, string Type)>();
        using (var info = c.CreateCommand())
        {
            info.CommandText = "SELECT name, type FROM pragma_table_info('Ipads') WHERE \"notnull\" = 1 AND dflt_value IS NULL";
            using var r = info.ExecuteReader();
            while (r.Read()) cols.Add((r.GetString(0), r.GetString(1)));
        }
        var values = new Dictionary<string, object?> { ["Id"] = id, ["ClientId"] = $"fd{id}", ["DeviceType"] = type, ["LanIp"] = lan, ["LastSeen"] = "2026-11-18 10:00:00", ["Created"] = "2026-11-01 10:00:00" };
        foreach (var (name, t) in cols) values.TryAdd(name, t == "INTEGER" ? 0 : t == "TEXT" && name.EndsWith('s') ? "[]" : "");
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"INSERT INTO Ipads ({string.Join(',', values.Keys)}) VALUES ({string.Join(',', values.Keys.Select(k => "@" + k))})";
        foreach (var (k, v) in values) cmd.Parameters.AddWithValue("@" + k, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}

/// <summary>Tjeklisten: kun profilen er registreret af Fleetdeck, resten er aldrig kontrolleret.</summary>
public class IpadSetupGuideTests
{
    private static Ipad Child() => new() { Name = "Testenhed", ClientId = "fdtest", Kind = IpadKinds.Child };

    [Fact]
    public void Only_the_profile_step_is_detected_automatically()
    {
        Assert.Equal([IpadSetupGuide.Profile], IpadSetupGuide.Steps.Where(s => s.Auto).Select(s => s.Key));
        var p = Child();
        Assert.All(IpadSetupGuide.For(p), s => Assert.False(s.Done));
        p.ProfileSeen = DateTime.UtcNow;
        Assert.Equal("auto", IpadSetupGuide.For(p).Single(s => s.Key == IpadSetupGuide.Profile).Source);
    }

    [Fact]
    public void Ticked_steps_are_stored_per_device_and_shown_as_marked_by_the_user()
    {
        var p = Child(); var other = Child();
        Assert.True(IpadSetupGuide.Mark(p, IpadSetupGuide.Family, true));
        Assert.True(IpadSetupGuide.Mark(p, IpadSetupGuide.Family, true));
        Assert.Equal([IpadSetupGuide.Family], p.SetupDone);
        Assert.Equal("manual", IpadSetupGuide.For(p).Single(s => s.Key == IpadSetupGuide.Family).Source);
        Assert.Empty(other.SetupDone);
        Assert.True(IpadSetupGuide.Mark(p, IpadSetupGuide.Family, false));
        Assert.Empty(p.SetupDone);
    }

    [Theory]
    [InlineData(IpadSetupGuide.Profile), InlineData("ukendt")]
    public void The_profile_step_and_unknown_steps_cannot_be_ticked(string key)
    {
        var p = Child();
        Assert.False(IpadSetupGuide.Mark(p, key, true));
        Assert.Empty(p.SetupDone);
    }

    [Theory]
    [InlineData(DeviceTypes.AppleTv, IpadKinds.Child), InlineData(DeviceTypes.Android, IpadKinds.Child), InlineData(DeviceTypes.Ipad, IpadKinds.Family)]
    public void The_checklist_is_only_for_childrens_ios_devices(string type, string kind) =>
        Assert.Empty(IpadSetupGuide.For(new Ipad { DeviceType = type, Kind = kind }));

    private static IEnumerable<string> Texts() => IpadSetupGuide.Steps.SelectMany(s => s.Lines.Append(s.Title).Append(s.Note ?? "").Concat(s.Links.Select(l => l.Label)));

    [Fact]
    public void Shipped_texts_contain_no_personal_device_names()
    {
        var named = new Regex(@"\b\p{Lu}\p{Ll}+s (iPad|iPhone|Apple TV)\b");
        Assert.DoesNotContain(Texts(), t => named.IsMatch(t));
        Assert.Equal("24", IpadWatch.DefaultHours.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Every_link_goes_to_apples_support_site() =>
        Assert.All(IpadSetupGuide.Steps.SelectMany(s => s.Links), l => Assert.StartsWith("https://support.apple.com/da-dk/", l.Url));

    [Fact]
    public void The_guide_never_promises_that_screen_time_locks_the_profile()
    {
        var accounts = IpadSetupGuide.Steps.Single(s => s.Key == IpadSetupGuide.Accounts);
        Assert.Contains("ingen indstilling i Skærmtid", accounts.Note);
        Assert.Contains("Apples vejledning nævner ikke VPN og DNS", IpadSetupGuide.Steps.Single(s => s.Key == IpadSetupGuide.ScreenTime).Note);
    }
}
