using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fleetdeck.Tests;

/// <summary>YouTube og YouTube Kids hver for sig: alle kombinationer, tider, minutter pr. app og migreringen.</summary>
public class IpadYoutubeAppsTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateTime Noon = new(2026, 11, 16, 15, 0, 0, DateTimeKind.Utc);   // mandag, fri tid
    private static readonly Dictionary<string, string> NoGroups = [];

    private static Ipad Pad(string yt = YtModes.Moderate, string kids = KidsModes.Allowed, bool linked = false) =>
        new() { Id = 3, Name = "Test", ClientId = "fdabc", YtMode = yt, KidsMode = kids, KidsLinked = linked };

    private static IpadDesired Build(Ipad p, YtUsage use = default) => IpadPolicy.Build(p, IpadPolicy.State(p, Noon, Utc, use), NoGroups, []);

    private static IEnumerable<string> Blocks(IpadDesired d) => d.Rules.Where(r => r.StartsWith("||", StringComparison.Ordinal)).Select(r => r[2..r.IndexOf('^')]);
    private static IEnumerable<string> Rewrites(IpadDesired d) => d.Rules.Where(r => r.Contains("$dnsrewrite", StringComparison.Ordinal)).Select(r => r[1..r.IndexOf('^')]);
    private static bool Hits(string host, string domain) => host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);

    // ---- den rene politik

    [Theory]
    [InlineData(YtModes.Open, "allowed", false, false, false, null)]
    [InlineData(YtModes.Moderate, "allowed", false, false, false, "restrictmoderate.youtube.com")]
    [InlineData(YtModes.Strict, "allowed", false, false, false, "restrict.youtube.com")]
    [InlineData(YtModes.Blocked, "allowed", false, true, false, null)]
    [InlineData("quota", "allowed", false, true, false, null)]
    [InlineData("window", "allowed", false, true, false, null)]
    [InlineData(YtModes.Open, "blocked", false, false, true, null)]
    [InlineData(YtModes.Moderate, "quota", false, false, true, "restrictmoderate.youtube.com")]
    [InlineData(YtModes.Strict, "window", false, false, true, "restrict.youtube.com")]
    [InlineData(YtModes.Blocked, "blocked", true, false, true, null)]
    [InlineData("quota", "window", true, false, true, null)]
    [InlineData("school", "school", true, false, true, null)]
    public void Every_combination_has_the_right_plan(string yt, string kids, bool service, bool kidsOnly, bool blockKids, string? target) =>
        Assert.Equal(new YtPlan(service, kidsOnly, blockKids, target), IpadPolicy.PlanYoutube(yt, kids));

    [Theory]
    [InlineData(YtModes.Moderate, "allowed")]
    [InlineData(YtModes.Blocked, "allowed")]
    [InlineData(YtModes.Open, "blocked")]
    public void A_blocked_service_always_stops_both_apps(string yt, string kids) =>
        Assert.Equal(new YtPlan(true, false, true, null), IpadPolicy.PlanYoutube(yt, kids, serviceBlocked: true));

    // ---- reglerne, AdGuard får

    [Fact]
    public void Both_allowed_blocks_nothing()
    {
        var d = Build(Pad(YtModes.Open));
        Assert.DoesNotContain("youtube", d.Services);
        Assert.Empty(d.Rules);
    }

    [Fact]
    public void Youtube_blocked_with_kids_allowed_never_uses_the_service_that_kills_kids()
    {
        var d = Build(Pad(YtModes.Blocked));
        Assert.DoesNotContain("youtube", d.Services);
        Assert.Contains("youtube.com", Blocks(d));
        Assert.DoesNotContain(IpadPolicy.KidsDomain, Blocks(d));
        // den almindelige app kan stadig nå den fælles API, men kun i streng Begrænset tilstand
        Assert.Contains("|youtubei.googleapis.com^$dnsrewrite=NOERROR;CNAME;restrict.youtube.com,client=fd-ipad-3", d.Rules);
        Assert.Contains("@@||restrict.youtube.com^$client=fd-ipad-3", d.Rules);
    }

    [Fact]
    public void Kids_blocked_with_youtube_allowed_only_blocks_the_kids_domain()
    {
        var d = Build(Pad(YtModes.Moderate, KidsModes.Blocked));
        Assert.DoesNotContain("youtube", d.Services);
        Assert.Equal([IpadPolicy.KidsDomain], Blocks(d));
        Assert.Contains(d.Rules, r => r.StartsWith("|www.youtube.com^$dnsrewrite", StringComparison.Ordinal));
    }

    [Fact]
    public void Both_blocked_uses_the_service_and_the_kids_domain()
    {
        var d = Build(Pad(YtModes.Blocked, KidsModes.Blocked));
        Assert.Contains("youtube", d.Services);
        Assert.Equal([IpadPolicy.KidsDomain], Blocks(d));
        Assert.Empty(Rewrites(d));
    }

    public static TheoryData<string, string> AllModes()
    {
        var t = new TheoryData<string, string>();
        foreach (var y in new[] { YtModes.Open, YtModes.Moderate, YtModes.Strict, YtModes.Blocked, YtModes.KidsOnly })
            foreach (var k in new[] { KidsModes.Allowed, KidsModes.Blocked }) t.Add(y, k);
        return t;
    }

    [Theory, MemberData(nameof(AllModes))]
    public void A_rewrite_never_undoes_a_block(string yt, string kids)
    {
        var d = Build(Pad(yt, kids));
        Assert.DoesNotContain(Rewrites(d), h => Blocks(d).Any(b => Hits(h, b)));
        if (d.Services.Contains("youtube")) Assert.Empty(Rewrites(d));
    }

    [Theory]
    [InlineData(YtModes.Moderate), InlineData(YtModes.Strict), InlineData(YtModes.Open)]
    public void A_service_blocked_under_rules_is_never_rewritten(string yt)
    {
        var p = Pad(yt); p.BlockedServices = ["youtube"];
        var d = Build(p);
        Assert.Contains("youtube", d.Services);
        Assert.Empty(Rewrites(d));
    }

    [Theory, MemberData(nameof(AllModes))]
    public void Bedtime_stops_both_apps_without_rewrites(string yt, string kids)
    {
        var p = Pad(yt, kids); p.PauseUntil = Noon.AddHours(1);
        var st = IpadPolicy.State(p, Noon, Utc, default(YtUsage));
        Assert.Equal(KidsModes.Blocked, st.Kids);
        var d = IpadPolicy.Build(p, st, NoGroups, []);
        Assert.Contains("youtube", d.Services);
        Assert.Empty(Rewrites(d));
    }

    // ---- tid og minutter rammer kun den ene app

    private static string Closed() => new('0', IpadPolicy.Slots);

    [Fact]
    public void A_used_up_youtube_quota_leaves_kids_open()
    {
        var p = Pad(); p.YtDailyMin = 30; p.KidsDailyMin = 30;
        var st = IpadPolicy.State(p, Noon, Utc, new YtUsage(500, 30, 10));
        Assert.Equal(("quota", KidsModes.Allowed), (st.Yt, st.Kids));
        Assert.DoesNotContain("youtube", IpadPolicy.Build(p, st, NoGroups, []).Services);
    }

    [Fact]
    public void A_used_up_kids_quota_leaves_youtube_open()
    {
        var p = Pad(); p.YtDailyMin = 30; p.KidsDailyMin = 30;
        var st = IpadPolicy.State(p, Noon, Utc, new YtUsage(500, 10, 30));
        Assert.Equal((YtModes.Moderate, "quota"), (st.Yt, st.Kids));
        Assert.Equal([IpadPolicy.KidsDomain], Blocks(IpadPolicy.Build(p, st, NoGroups, [])));
    }

    [Fact]
    public void Closed_windows_only_move_that_app_to_blocked()
    {
        var p = Pad(); p.YtWindows = Closed();
        var st = IpadPolicy.State(p, Noon, Utc, default(YtUsage));
        Assert.Equal(("window", KidsModes.Allowed), (st.Yt, st.Kids));

        p = Pad(); p.KidsWindows = Closed();
        st = IpadPolicy.State(p, Noon, Utc, default(YtUsage));
        Assert.Equal((YtModes.Moderate, "window"), (st.Yt, st.Kids));
    }

    [Fact]
    public void Linked_apps_share_todays_pool_and_windows()
    {
        var p = Pad(linked: true); p.YtDailyMin = 30;
        var st = IpadPolicy.State(p, Noon, Utc, new YtUsage(30, 0, 0));
        Assert.Equal(("quota", "quota"), (st.Yt, st.Kids));
        Assert.Contains("youtube", IpadPolicy.Build(p, st, NoGroups, []).Services);

        p = Pad(linked: true); p.YtWindows = Closed();
        st = IpadPolicy.State(p, Noon, Utc, default(YtUsage));
        Assert.Equal(("window", "window"), (st.Yt, st.Kids));
    }

    [Theory]
    [InlineData("www.youtubekids.com", IpadPolicy.AppKids)]
    [InlineData("youtubekids.com.", IpadPolicy.AppKids)]
    [InlineData("www.youtube.com", IpadPolicy.AppYoutube)]
    [InlineData("m.youtube.com", IpadPolicy.AppYoutube)]
    [InlineData("youtu.be", IpadPolicy.AppYoutube)]
    [InlineData("youtubei.googleapis.com", null)]
    [InlineData("youtube.googleapis.com", null)]
    [InlineData("rr3---sn-abc.googlevideo.com", null)]
    [InlineData("i.ytimg.com", null)]
    [InlineData("yt3.ggpht.com", null)]
    [InlineData("restrict.youtube.com", null)]
    [InlineData("notyoutube.com", null)]
    public void Minutes_are_only_counted_for_hosts_unique_to_one_app(string host, string? app) => Assert.Equal(app, IpadPolicy.YtAppOf(host));

    // ---- ét klik

    [Fact]
    public void Toggling_one_app_does_not_touch_the_other()
    {
        var p = Pad(YtModes.Strict);
        IpadPolicy.SetApp(p, IpadPolicy.AppKids, false);
        Assert.Equal((YtModes.Strict, KidsModes.Blocked), (p.YtMode, p.KidsMode));
        IpadPolicy.SetApp(p, IpadPolicy.AppKids, true);
        IpadPolicy.SetApp(p, IpadPolicy.AppYoutube, false);
        Assert.Equal((YtModes.Blocked, KidsModes.Allowed), (p.YtMode, p.KidsMode));
    }

    [Theory]
    [InlineData(IpadKinds.Child, YtModes.Moderate), InlineData(IpadKinds.Family, YtModes.Open)]
    public void Allowing_youtube_again_uses_a_safe_mode(string kind, string mode)
    {
        var p = Pad(YtModes.Blocked); p.Kind = kind;
        IpadPolicy.SetApp(p, IpadPolicy.AppYoutube, true);
        Assert.Equal(mode, p.YtMode);
    }

    [Fact]
    public void Allowing_one_app_while_the_service_is_blocked_keeps_the_other_blocked()
    {
        var p = Pad(YtModes.Moderate); p.BlockedServices = ["youtube", "tiktok"];
        IpadPolicy.SetApp(p, IpadPolicy.AppKids, true);
        Assert.Equal(["tiktok"], p.BlockedServices);
        Assert.Equal((YtModes.Blocked, KidsModes.Allowed), (p.YtMode, p.KidsMode));

        p = Pad(YtModes.Moderate); p.BlockedServices = ["youtube"];
        IpadPolicy.SetApp(p, IpadPolicy.AppYoutube, true);
        Assert.Equal((YtModes.Moderate, KidsModes.Blocked), (p.YtMode, p.KidsMode));
    }

    // ---- eksisterende enheder opfører sig som før

    [Theory]
    [InlineData("open", "open", "allowed")]
    [InlineData("moderate", "moderate", "allowed")]
    [InlineData("strict", "strict", "allowed")]
    [InlineData("blocked", "blocked", "blocked")]
    [InlineData("kids", "blocked", "allowed")]
    public void Migration_keeps_todays_behaviour(string before, string yt, string kids)
    {
        using var c = new SqliteConnection("Data Source=:memory:"); c.Open();
        using var m = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite(c).Options);
        var migrator = m.GetInfrastructure().GetRequiredService<IMigrator>();
        migrator.Migrate("20261118120714_IpadControlWatch");
        Insert(c, before);
        migrator.Migrate();
        var p = m.Ipads.AsNoTracking().Single();
        Assert.Equal((yt, kids, true), (p.YtMode, p.KidsMode, p.KidsLinked));
    }

    private static void Insert(SqliteConnection c, string ytMode)
    {
        var cols = new List<(string Name, string Type)>();
        using (var info = c.CreateCommand())
        {
            info.CommandText = "SELECT name, type FROM pragma_table_info('Ipads') WHERE \"notnull\" = 1 AND dflt_value IS NULL";
            using var r = info.ExecuteReader();
            while (r.Read()) cols.Add((r.GetString(0), r.GetString(1)));
        }
        var values = new Dictionary<string, object> { ["Id"] = 1, ["ClientId"] = "fd1", ["YtMode"] = ytMode, ["Created"] = "2026-11-01 10:00:00" };
        foreach (var (name, t) in cols) values.TryAdd(name, t == "INTEGER" ? 0 : name.EndsWith('s') ? "[]" : "");
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"INSERT INTO Ipads ({string.Join(',', values.Keys)}) VALUES ({string.Join(',', values.Keys.Select(k => "@" + k))})";
        foreach (var (k, v) in values) cmd.Parameters.AddWithValue("@" + k, v);
        cmd.ExecuteNonQuery();
    }
}
