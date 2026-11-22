using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Hubs;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;

namespace Fleetdeck.Tests;

/// <summary>Siden Apps: versioner, kanaler, læsning af den kørende version, besked én gang, GitHub-grænser og ETag.</summary>
public class AppWatchTests
{
    private static ReleaseInfo R(string v, bool pre = false, string notes = "") => new(v, "v" + v, "v" + v, notes, pre || v.Contains('-'), DateTime.UtcNow.AddDays(-1), null);
    private static readonly List<ReleaseInfo> Rels = [R("2.4.0-beta.3"), R("2.3.1"), R("2.3.0"), R("2.2.0")];

    [Theory]
    [InlineData("2.4.0-beta.3", "2.4.0-beta.10")]
    [InlineData("2.4.0-beta.10", "2.4.0")]
    [InlineData("1.9.9", "1.10.0")]
    [InlineData("v1.2", "1.2.1")]
    public void Versions_compare_as_semver(string older, string newer) => Assert.True(SemVer.Parse(older) < SemVer.Parse(newer));

    [Fact]
    public void Stable_channel_skips_prereleases_beta_takes_them()
    {
        Assert.Equal("2.3.1", AppWatch.Compare("2.3.0", Rels, ReleaseFeed.Stable).Latest!.Version);
        var beta = AppWatch.Compare("2.3.0", Rels, ReleaseFeed.Beta);
        Assert.Equal(AppStatus.New, beta.Status);
        Assert.Equal("2.4.0-beta.3", beta.Latest!.Version);
        Assert.Equal(["2.4.0-beta.3", "2.3.1"], beta.Newer.Select(r => r.Version));
    }

    [Fact]
    public void Running_newer_than_latest_release_is_ahead_not_an_update()
    {
        Assert.Equal(AppStatus.Ahead, AppWatch.Compare("3.3.0", Rels, ReleaseFeed.Stable).Status);
        // en beta, mens kanalen er stabil: også nyere end seneste stabile
        Assert.Equal(AppStatus.Ahead, AppWatch.Compare("2.4.0-beta.3", Rels, ReleaseFeed.Stable).Status);
        Assert.Equal(AppStatus.Ok, AppWatch.Compare("v2.3.1", Rels, ReleaseFeed.Stable).Status);
        Assert.Equal(AppStatus.Unknown, AppWatch.Compare(null, Rels, ReleaseFeed.Stable).Status);
        Assert.Equal(AppStatus.NoRelease, AppWatch.Compare("1.0.0", [R("1.1.0-beta.1")], ReleaseFeed.Stable).Status);
    }

    [Theory]
    [InlineData("{\"version\":\"1.7.2\"}", "", "1.7.2")]
    [InlineData("{\"data\":{\"app\":{\"version\":\"v2.0.0-beta.4\"}}}", "data.app.version", "2.0.0-beta.4")]
    [InlineData("{\"items\":[{\"v\":\"3.1\"}]}", "items.0.v", "3.1.0")]
    [InlineData("{\"Version\":\"1.2.3\"}", "version", "1.2.3")]
    [InlineData("1.24.0\n", "", "1.24.0")]
    [InlineData("Lytter på 192.0.2.10 – version 1.8.0 (bygget 2026.11.17)", "", "1.8.0")]
    [InlineData("minapp v4.5.6+abc123", "", "4.5.6")]
    public void Version_is_read_from_json_or_text(string text, string field, string expected) => Assert.Equal(expected, AppWatch.Extract(text, field).Version);

    [Theory]
    [InlineData("ikke json", "version", "ikke JSON")]
    [InlineData("{\"a\":1}", "b.c", "findes ikke")]
    [InlineData("{\"version\":\"latest\"}", "version", "ikke et versionsnummer")]
    [InlineData("hej", "", "intet versionsnummer")]
    [InlineData("", "", "tomt")]
    public void Unreadable_versions_explain_why(string text, string field, string part) => Assert.Contains(part, AppWatch.Extract(text, field).Error);

    [Theory]
    [InlineData("ghcr.io/ejer/minapp:1.2.3", null, "1.2.3")]
    [InlineData("ghcr.io/ejer/minapp:v2.0.0-beta.1@sha256:abc", null, "2.0.0-beta.1")]
    [InlineData("192.0.2.5:5000/minapp:latest", "3.4.5", "3.4.5")]
    [InlineData("ejer/minapp:latest", "", null)]
    [InlineData("192.0.2.5:5000/minapp", null, null)]
    public void Docker_version_comes_from_label_or_tag(string image, string? label, string? expected) => Assert.Equal(expected, AppWatch.FromImage(image, label));

    [Fact]
    public void Job_output_is_read_between_the_markers()
    {
        var file = new WatchedApp { ReadKind = "file", ReadField = "" };
        Assert.Equal("1.24.0", AppWatch.FromJob(file, $"agent 9.9.9 starter\n{AppWatch.Begin}\n1.24.0\n{AppWatch.End}\n").Version);
        var docker = new WatchedApp { ReadKind = "docker" };
        Assert.Equal("2.3.1", AppWatch.FromJob(docker, $"{AppWatch.Begin}\n|ghcr.io/ejer/mailfilter:2.3.1\n{AppWatch.End}").Version);
        Assert.Equal("2.3.2", AppWatch.FromJob(docker, $"{AppWatch.Begin}\n2.3.2|ghcr.io/ejer/mailfilter:latest\n{AppWatch.End}").Version);
    }

    [Fact]
    public void Read_scripts_fit_the_platform()
    {
        var f = new WatchedApp { ReadKind = "file", ReadTarget = "/opt/minapp/VERSION" };
        Assert.Contains("cat -- '/opt/minapp/VERSION'", AppWatch.ReadScript(f, "linux"));
        Assert.Contains("Get-Content -Raw -LiteralPath 'C:\\minapp\\version.txt'", AppWatch.ReadScript(new WatchedApp { ReadKind = "file", ReadTarget = "C:\\minapp\\version.txt" }, "windows"));
        Assert.Contains("docker inspect --format '{{index .Config.Labels \"org.opencontainers.image.version\"}}|{{.Config.Image}}' minapp",
            AppWatch.ReadScript(new WatchedApp { ReadKind = "docker", PlaceName = "minapp" }, "linux"));
        Assert.Null(AppWatch.ReadScript(new WatchedApp { ReadKind = "http" }, "linux"));
    }

    [Fact]
    public void Notification_is_sent_once_per_version()
    {
        var a = new WatchedApp { Name = "Minapp", Notify = true, ReadKind = "http", Running = "2.3.0" };
        var cmp = AppWatch.Compare(a.Running, Rels, ReleaseFeed.Stable);
        var (send, remember) = AppWatch.ShouldNotify(a, cmp);
        Assert.True(send); Assert.Equal("2.3.1", remember);
        a.NotifiedVersion = remember;
        Assert.False(AppWatch.ShouldNotify(a, cmp).Send);
        // en endnu nyere version giver en ny besked
        var cmp2 = AppWatch.Compare(a.Running, [R("2.3.2"), .. Rels], ReleaseFeed.Stable);
        Assert.Equal((true, "2.3.2"), AppWatch.ShouldNotify(a, cmp2));
        // opdateret eller nyere end udgivelsen: ingen besked
        Assert.False(AppWatch.ShouldNotify(new WatchedApp { Notify = true, Running = "2.3.1" }, AppWatch.Compare("2.3.1", Rels, ReleaseFeed.Stable)).Send);
        Assert.False(AppWatch.ShouldNotify(new WatchedApp { Notify = true, Running = "9.0.0" }, AppWatch.Compare("9.0.0", Rels, ReleaseFeed.Stable)).Send);
        // beskeder slået fra: huskes, men sendes ikke
        Assert.Equal((false, "2.3.1"), AppWatch.ShouldNotify(new WatchedApp { Notify = false, Running = "2.3.0" }, cmp));
    }

    [Fact]
    public void Without_a_running_version_the_first_release_is_the_baseline()
    {
        var a = new WatchedApp { Notify = true, ReadKind = "none" };
        Assert.Equal((false, "2.3.1"), AppWatch.ShouldNotify(a, AppWatch.Compare(null, Rels, ReleaseFeed.Stable)));
        a.NotifiedVersion = "2.3.1";
        Assert.Equal((true, "2.3.2"), AppWatch.ShouldNotify(a, AppWatch.Compare(null, [R("2.3.2"), .. Rels], ReleaseFeed.Stable)));
    }

    [Fact]
    public void Rate_limits_are_respected()
    {
        var now = new DateTime(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);
        var reset = new DateTimeOffset(now.AddMinutes(40)).ToUnixTimeSeconds().ToString();
        var gone = AppWatch.Rate(HttpStatusCode.Forbidden, new Dictionary<string, string> { ["x-ratelimit-remaining"] = "0", ["x-ratelimit-reset"] = reset, ["x-ratelimit-limit"] = "60" }, now);
        Assert.Equal(now.AddMinutes(40), gone.BlockedUntil);
        Assert.Equal(60, gone.Limit);
        Assert.Equal(now.AddSeconds(90), AppWatch.Rate(HttpStatusCode.TooManyRequests, new Dictionary<string, string> { ["retry-after"] = "90" }, now).BlockedUntil);
        Assert.Null(AppWatch.Rate(HttpStatusCode.OK, new Dictionary<string, string> { ["x-ratelimit-remaining"] = "4999" }, now).BlockedUntil);
        Assert.NotNull(AppWatch.Rate(HttpStatusCode.OK, new Dictionary<string, string> { ["x-ratelimit-remaining"] = "3", ["x-ratelimit-reset"] = reset }, now).BlockedUntil);
        Assert.True(AppWatch.Due(null, 6, now));
        Assert.False(AppWatch.Due(now.AddHours(-5), 6, now));
        Assert.True(AppWatch.Due(now.AddHours(-6), 6, now));
    }

    [Fact]
    public void Validation_and_never_automatic()
    {
        WatchedApp Ok() => new() { Name = "Minapp", Repo = "ejer/minapp", Channel = "stable", PlaceKind = "container", ReadKind = "none" };
        Assert.Null(AppWatch.Validate(Ok()));
        Assert.NotNull(AppWatch.Validate(WithRepo("minapp")));
        var h = Ok(); h.ReadKind = "http"; h.ReadTarget = "ftp://192.0.2.10/version";
        Assert.Contains("http://", AppWatch.Validate(h));
        h.ReadTarget = "http://192.0.2.10:8080/api/version"; Assert.Null(AppWatch.Validate(h));
        var d = Ok(); d.ReadKind = "docker"; d.PlaceName = "minapp"; Assert.Contains("maskinen", AppWatch.Validate(d));
        d.MachineId = 1; Assert.Null(AppWatch.Validate(d));
        var f = Ok(); f.ReadKind = "file"; f.MachineId = 1; f.ReadTarget = "/opt/x'; rm -rf /"; Assert.NotNull(AppWatch.Validate(f));
        var auto = Ok(); auto.AutoUpdate = true; Assert.Contains("Opdatér-kommando", AppWatch.Validate(auto));
        // "aldrig automatisk" slår automatisk opdatering fra
        var never = Ok(); never.AutoUpdate = true; never.NeverAuto = true; never.UpdateCommand = "x"; never.MachineId = 1;
        AppWatch.Clean(never);
        Assert.False(never.AutoUpdate);
        Assert.Null(AppWatch.Validate(never));
    }

    private static WatchedApp WithRepo(string repo) => new() { Name = "Minapp", Repo = repo, Channel = "stable", PlaceKind = "container", ReadKind = "none" };

    [Fact]
    public void Update_command_gets_version_and_tag()
    {
        Assert.Equal("cd /opt/minapp && ./opdater.sh 1.8.0 v1.8.0", AppWatch.ExpandCommand("cd /opt/minapp && ./opdater.sh {version} {tag}", R("1.8.0")));
        Assert.Null(AppWatch.ExpandCommand("x {tag}", new ReleaseInfo("1.8.0", "v1.8.0;rm", "", "", false, null, null)));
    }

    [Fact]
    public void Notify_text_has_name_versions_and_notes()
    {
        var t = AppWatch.NotifyText(new WatchedApp { Name = "Ventilation", NeverAuto = true }, R("1.8.0", notes: "## Nyt\n\n- Ugeplan"), "1.7.2", "minpi");
        Assert.StartsWith("📦 Ventilation: ny version 1.8.0 (kører 1.7.2 på minpi)\n", t);
        Assert.Contains("- Ugeplan", t);
        Assert.Contains("Aldrig automatisk", t);
    }
}

/// <summary>AppWatchService mod en database i hukommelsen og et falsk GitHub (ingen netværk).</summary>
public class AppWatchServiceTests : IDisposable
{
    private readonly SqliteConnection conn = new("Data Source=:memory:");
    private readonly AppDb db;
    private readonly FakeHttp http = new();
    private readonly AppWatchService svc;
    private readonly EphemeralDataProtectionProvider dp = new();

    private const string Json1 = """
        [{"tag_name":"v1.1.0-beta.1","name":"1.1.0-beta.1","body":"Beta","prerelease":true,"draft":false,"published_at":"2026-11-18T10:00:00Z","html_url":"https://github.com/ejer/minapp/releases/tag/v1.1.0-beta.1"},
         {"tag_name":"v1.0.1","name":"1.0.1","body":"- Rettet fejl","prerelease":false,"draft":false,"published_at":"2026-11-17T10:00:00Z","html_url":"https://github.com/ejer/minapp/releases/tag/v1.0.1"},
         {"tag_name":"v1.0.0","name":"1.0.0","body":"Første","prerelease":false,"draft":false,"published_at":"2026-11-01T10:00:00Z"},
         {"tag_name":"v2.0.0","draft":true}]
        """;

    public AppWatchServiceTests()
    {
        conn.Open();
        db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var s = new SettingsService(db);
        svc = new AppWatchService(db, s, new AuditService(db), new JobService(db, new Live(new NoHub<UiHub>(), new NoHub<AgentHub>())), http,
            new Notifier(http, scopes, NullLogger<Notifier>.Instance), dp, NullLogger<AppWatchService>.Instance);
    }

    public void Dispose() { db.Dispose(); conn.Dispose(); }

    private async Task<WatchedApp> Add(string? running = "1.0.0")
    {
        var a = new WatchedApp { Name = "Minapp", Repo = "ejer/minapp", Channel = "stable", ReadKind = "http", ReadTarget = "http://192.0.2.10/version", Running = running };
        db.WatchedApps.Add(a); await db.SaveChangesAsync();
        return a;
    }

    [Fact]
    public async Task Check_uses_etag_and_notifies_once()
    {
        var a = await Add();
        http.Next = _ => Ok(Json1, "\"abc\"");
        var r1 = await svc.Check(a);
        Assert.Null(r1.Error);
        Assert.True(r1.Notified);
        Assert.Equal("1.0.1", a.NotifiedVersion);
        Assert.Equal("\"abc\"", a.ETag);
        Assert.Equal(["1.1.0-beta.1", "1.0.1", "1.0.0"], AppWatchService.Releases(a).Select(r => r.Version));   // kladden er sprunget over
        Assert.Equal("https://api.github.com/repos/ejer/minapp/releases?per_page=30", http.Requests[0].RequestUri!.ToString());

        // uændret: GitHub svarer 304, listen bliver, og der kommer ingen ny besked
        http.Next = req => { Assert.Equal("\"abc\"", string.Join("", req.Headers.GetValues("If-None-Match"))); return new HttpResponseMessage(HttpStatusCode.NotModified); };
        var r2 = await svc.Check(a);
        Assert.True(r2.NotModified);
        Assert.False(r2.Notified);
        Assert.Equal(3, AppWatchService.Releases(a).Count);
    }

    [Fact]
    public async Task Two_apps_from_the_same_project_share_one_request()
    {
        var a = await Add();
        var b = new WatchedApp { Name = "Minapp (pc)", Repo = "ejer/minapp", Channel = "stable", ReadKind = "none" };
        db.WatchedApps.Add(b); await db.SaveChangesAsync();
        http.Next = _ => Ok(Json1, "\"abc\"");
        await svc.Check(a);
        var r = await svc.Check(b);
        Assert.False(r.Asked);
        Assert.Single(http.Requests);
        Assert.Equal(3, AppWatchService.Releases(b).Count);
    }

    [Fact]
    public async Task Rate_limit_stops_further_requests_until_reset()
    {
        var a = await Add();
        http.Next = _ =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{}") };
            res.Headers.Add("X-RateLimit-Remaining", "0");
            res.Headers.Add("X-RateLimit-Reset", DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds().ToString());
            return res;
        };
        Assert.Contains("afviser", (await svc.Check(a)).Error);
        var r = await svc.Check(a);
        Assert.False(r.Asked);
        Assert.Single(http.Requests);
        Assert.Contains("grænsen", a.CheckError);
    }

    [Fact]
    public async Task Token_is_stored_encrypted_and_sent_as_bearer()
    {
        var a = await Add();
        await svc.SetToken("ghp_eksempelnoegle", "admin");
        var stored = (await db.Settings.FindAsync(AppWatch.TokenKey))!.Value;
        Assert.DoesNotContain("ghp_", stored);
        http.Next = req => { Assert.Equal("Bearer ghp_eksempelnoegle", req.Headers.Authorization!.ToString()); return Ok("[]"); };
        Assert.Null((await svc.Check(a)).Error);
    }

    [Fact]
    public async Task Http_version_is_read_from_the_json_field()
    {
        var a = await Add(running: null);
        a.ReadField = "data.version";
        http.Next = _ => Ok("{\"data\":{\"version\":\"1.0.1\"}}");
        await svc.Read(a);
        Assert.Equal("1.0.1", a.Running);
        Assert.Null(a.ReadError);
        http.Next = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        await svc.Read(a);
        Assert.Equal("1.0.1", a.Running);   // den sidst kendte version bliver stående
        Assert.Contains("503", a.ReadError);
    }

    [Fact]
    public async Task Update_requires_confirmation_and_never_runs_automatically_when_marked()
    {
        var m = new Machine { Name = "minpi", Platform = "linux", LastSeen = DateTime.UtcNow };
        db.Machines.Add(m); await db.SaveChangesAsync();
        var a = await Add();
        a.MachineId = m.Id; a.UpdateCommand = "cd /opt/minapp && ./opdater.sh {version}"; a.NeverAuto = true;
        http.Next = _ => Ok(Json1);
        await svc.Check(a);
        Assert.Contains("Bekræft", (await svc.Update(a, null, false, "admin")).Error);
        Assert.Contains("automatisk", (await svc.Update(a, null, false, "automatisk", auto: true)).Error);
        var (id, err) = await svc.Update(a, null, true, "admin");
        Assert.Null(err);
        var job = await db.Jobs.SingleAsync(j => j.Id == id);
        Assert.Equal("Opdatér Minapp til 1.0.1", job.Title);
        Assert.Contains("./opdater.sh 1.0.1", job.ParamsJson);
        Assert.Equal(AppWatch.Source, job.Source);
        Assert.Contains("allerede", (await svc.Update(a, null, true, "admin")).Error);
    }

    [Fact]
    public async Task Site_config_has_apps_without_token_or_state()
    {
        var m = new Machine { Name = "minpi", Platform = "linux" };
        db.Machines.Add(m); await db.SaveChangesAsync();
        var a = await Add();
        a.MachineId = m.Id; a.NeverAuto = true; a.UpdateCommand = "./opdater.sh {version}"; a.NotifiedVersion = "1.0.1"; a.ETag = "\"abc\"";
        await db.SaveChangesAsync();
        await svc.SetToken("ghp_eksempelnoegle", "admin");
        db.Settings.Add(new Setting { Key = AppWatch.RateKey, Value = "{\"remaining\":10}" });
        db.Settings.Add(new Setting { Key = AppWatch.HoursKey, Value = "12" });
        await db.SaveChangesAsync();

        var doc = await SiteConfig.Export(db, new Paths(Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "fd-appwatch-ingen")));
        var text = doc.ToJsonString();
        Assert.DoesNotContain("ghp_", text);
        Assert.DoesNotContain(AppWatch.TokenKey, text);
        Assert.DoesNotContain(AppWatch.RateKey, text);
        Assert.DoesNotContain("abc", text);
        Assert.DoesNotContain("notifiedVersion", text);
        var app = Assert.Single((JsonArray)doc["apps"]!)!;
        Assert.Equal("minpi", app["machine"]!.ToString());
        Assert.True(app["neverAuto"]!.GetValue<bool>());
        Assert.Equal("12", doc["settings"]![AppWatch.HoursKey]!.ToString());

        // læses ind i en tom installation: appen oprettes med maskinen fundet ud fra navnet
        db.WatchedApps.RemoveRange(db.WatchedApps); await db.SaveChangesAsync();
        var r = await SiteConfig.Import(db, doc);
        Assert.Equal(1, r.Apps);
        var back = await db.WatchedApps.SingleAsync();
        Assert.Equal((m.Id, true, "./opdater.sh {version}", (string?)null), (back.MachineId!.Value, back.NeverAuto, back.UpdateCommand, back.NotifiedVersion));
        // igen: intet ændret
        Assert.Equal(0, (await SiteConfig.Import(db, doc)).Apps);
    }

    private static HttpResponseMessage Ok(string json, string? etag = null)
    {
        var res = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (etag is not null) res.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
        res.Headers.Add("X-RateLimit-Remaining", "4999");
        return res;
    }

    /// <summary>Svarer i stedet for nettet; hver forespørgsel gemmes.</summary>
    private sealed class FakeHttp : HttpMessageHandler, IHttpClientFactory
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Next { get; set; } = _ => throw new InvalidOperationException("Testen må ikke kalde nettet");
        public List<HttpRequestMessage> Requests { get; } = [];
        public HttpClient CreateClient(string name) => new(this, false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Next(request));
        }
    }

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
