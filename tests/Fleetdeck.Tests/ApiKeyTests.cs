using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Endpoints;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;

namespace Fleetdeck.Tests;

internal sealed class ApiClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Nøgleformat, hash, rettigheder og grænser uden HTTP.</summary>
public class ApiKeyFormatTests
{
    [Fact]
    public void Ny_nøgle_er_fd_plus_43_tegn_og_gemmes_kun_som_sha256_og_hint()
    {
        var k = ApiKeyFormat.NewKey();
        Assert.Matches("^fd_[A-Za-z0-9_-]{43}$", k);
        Assert.NotEqual(k, ApiKeyFormat.NewKey());
        Assert.Equal(Crypto.Sha256(k), ApiKeyFormat.Hash(k));
        Assert.Matches("^[0-9a-f]{64}$", ApiKeyFormat.Hash(k));
        Assert.Equal(k[^4..], ApiKeyFormat.Hint(k));
        Assert.Equal("fd_…" + k[^4..], ApiKeyFormat.Display(new ApiKey { Hint = k[^4..] }));
        Assert.Equal("…abcd", ApiKeyFormat.Display(new ApiKey { Hint = "abcd", Migrated = true }));
    }

    private static HttpRequest Req(string? bearer = null, string? xApiKey = null, string? query = null)
    {
        var c = new DefaultHttpContext();
        if (bearer is not null) c.Request.Headers.Authorization = bearer;
        if (xApiKey is not null) c.Request.Headers["X-Api-Key"] = xApiKey;
        if (query is not null) c.Request.QueryString = new QueryString(query);
        return c.Request;
    }

    [Fact]
    public void Nøglen_læses_fra_bearer_eller_x_api_key_men_aldrig_fra_adressen()
    {
        Assert.Equal("fd_abc", ApiKeyFormat.FromRequest(Req(bearer: "Bearer fd_abc")));
        Assert.Equal("fd_abc", ApiKeyFormat.FromRequest(Req(bearer: "bearer  fd_abc ")));
        Assert.Equal("gammel", ApiKeyFormat.FromRequest(Req(xApiKey: "gammel")));
        Assert.Equal("fd_ny", ApiKeyFormat.FromRequest(Req(bearer: "Bearer fd_ny", xApiKey: "gammel")));
        Assert.Equal("gammel", ApiKeyFormat.FromRequest(Req(bearer: "Basic eDp5", xApiKey: "gammel")));
        Assert.Null(ApiKeyFormat.FromRequest(Req(query: "?api_key=fd_abc&key=fd_abc&token=fd_abc")));
        Assert.Null(ApiKeyFormat.FromRequest(Req(bearer: "Bearer ")));
    }

    [Theory]
    [InlineData("read", "read", true)] [InlineData("control", "read", true)] [InlineData("admin", "read", true)]
    [InlineData("read", "control", false)] [InlineData("control", "control", true)] [InlineData("admin", "control", true)]
    [InlineData("read", "admin", false)] [InlineData("control", "admin", false)] [InlineData("admin", "admin", true)]
    [InlineData("", "read", false)] [InlineData("root", "read", false)] [InlineData("admin", "", false)] [InlineData("admin", "alt", false)]
    public void Rettigheder_omfatter_de_lavere_og_ukendte_giver_intet(string have, string need, bool ok) => Assert.Equal(ok, ApiScopes.Allows(have, need));

    [Theory]
    [InlineData("Home Assistant", "read", null, null)]
    [InlineData("Kontrolrum", "admin", 365, null)]
    [InlineData("", "read", null, "navn")]
    [InlineData("   ", "read", null, "navn")]
    [InlineData("x", "root", null, "rettighed")]
    [InlineData("x", null, null, "rettighed")]
    [InlineData("x", "read", 7, "Udløb")]
    [InlineData("a\nb", "read", null, "styretegn")]
    public void Ny_nøgle_valideres(string? name, string? scope, int? days, string? error)
    {
        var e = ApiKeyEndpoints.Validate(new ApiKeyCreate(name, scope, true, days));
        if (error is null) Assert.Null(e); else Assert.Contains(error, e);
        Assert.NotNull(ApiKeyEndpoints.Validate(new ApiKeyCreate(new string('x', 65), "read", true, null)));
    }

    private static (ApiKeyGuard g, ApiClock c) Guard()
    {
        var c = new ApiClock(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
        return (new ApiKeyGuard(c, NullLogger<ApiKeyGuard>.Instance), c);
    }

    [Fact]
    public void Pr_nøgle_60_læse_og_10_styre_kald_i_minuttet()
    {
        var (g, c) = Guard();
        for (var i = 0; i < 60; i++) Assert.True(g.TakeCall(1, ApiScopes.Read, out _));
        Assert.False(g.TakeCall(1, ApiScopes.Read, out var wait));
        Assert.InRange(wait, 1, 60);
        Assert.True(g.TakeCall(2, ApiScopes.Read, out _));      // en anden nøgle har sin egen grænse
        for (var i = 0; i < 10; i++) Assert.True(g.TakeCall(1, ApiScopes.Control, out _));
        Assert.False(g.TakeCall(1, ApiScopes.Admin, out _));    // admin-kald tæller som styre-kald
        c.Now = c.Now.AddSeconds(61);
        Assert.True(g.TakeCall(1, ApiScopes.Read, out _));
        Assert.True(g.TakeCall(1, ApiScopes.Control, out _));
    }

    [Fact]
    public void Forkerte_nøgler_bremses_pr_ip_efter_10_forsøg_i_5_minutter()
    {
        var (g, c) = Guard();
        for (var i = 0; i < 9; i++) g.Failed("192.168.1.77");
        Assert.False(g.Blocked("192.168.1.77", out _));
        g.Failed("192.168.1.77");
        Assert.True(g.Blocked("192.168.1.77", out var wait));
        Assert.InRange(wait, 1, 300);
        Assert.False(g.Blocked("192.168.1.78", out _));
        c.Now = c.Now.AddMinutes(5);
        Assert.False(g.Blocked("192.168.1.77", out _));
    }

    [Fact]
    public void Sidst_brugt_skrives_højst_hvert_60_sekund_eller_når_ip_skifter()
    {
        var (g, c) = Guard();
        Assert.Equal(1, g.TakeUse(1, "192.168.1.29"));
        Assert.Equal(0, g.TakeUse(1, "192.168.1.29"));
        Assert.Equal(0, g.TakeUse(1, "192.168.1.29"));
        Assert.Equal(3, g.TakeUse(1, "192.168.1.30"));            // ny IP: skriv med det samme (alle tre kald)
        c.Now = c.Now.AddSeconds(30);
        Assert.Equal(0, g.TakeUse(1, "192.168.1.30"));
        c.Now = c.Now.AddSeconds(31);
        Assert.Equal(2, g.TakeUse(1, "192.168.1.30"));
        g.Forget(1);
        Assert.Equal(1, g.TakeUse(1, "192.168.1.30"));
    }

    [Theory]
    [InlineData("192.168.1.20", null, true)] [InlineData("192.168.1.5", null, true)] [InlineData("100.100.1.1", null, true)]
    [InlineData("127.0.0.1", null, true)] [InlineData("203.0.113.9", null, false)] [InlineData("8.8.8.8", null, false)]
    [InlineData("127.0.0.1", "CF-Connecting-IP", false)] [InlineData("192.168.1.20", "CF-Ray", false)]
    public async Task Kun_hjemmenettet_afviser_offentlige_adresser_og_cloudflare_tunnelen(string ip, string? header, bool home)
    {
        var c = new DefaultHttpContext();
        c.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        if (header is not null) c.Request.Headers[header] = "x";
        Assert.Equal(home, await ApiKeyGuard.FromHomeNetwork(c));
    }
}

/// <summary>Flytningen af den gamle Kontrolrum-nøgle (kontrolrum_key i klartekst) til tabellen.</summary>
public sealed class ApiKeyMigrationTests : IDisposable
{
    private readonly SqliteConnection conn = new("Data Source=:memory:");
    private readonly AppDb db;
    private readonly SettingsService settings;
    private static readonly DateTime Now = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
    private const string Old = "gammelKontrolrumNoegle0123456789abcdWxYz";

    public ApiKeyMigrationTests()
    {
        conn.Open();
        db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        settings = new SettingsService(db);
    }

    public void Dispose() { db.Dispose(); conn.Dispose(); }

    [Fact]
    public async Task Gammel_nøgle_flyttes_som_Kontrolrum_med_kun_læse_og_klarteksten_slettes()
    {
        await settings.Set(ApiKeyMigration.OldSetting, Old);
        Assert.True(await ApiKeyMigration.FromKontrolrumKey(db, settings, Now));
        var k = Assert.Single(await db.ApiKeys.ToListAsync());
        Assert.Equal("Kontrolrum", k.Name);
        Assert.Equal(ApiScopes.Read, k.Scope);
        Assert.Equal(Crypto.Sha256(Old), k.Sha256);
        Assert.Equal("WxYz", k.Hint);
        Assert.True(k.Migrated);
        Assert.False(k.LanOnly);                                // virkede udefra før, så det gør den stadig
        Assert.Equal("system", k.CreatedBy);
        Assert.Equal("", await settings.Get(ApiKeyMigration.OldSetting));
        // klarteksten findes ingen steder i databasen bagefter
        Assert.DoesNotContain(await db.Settings.Select(s => s.Value).ToListAsync(), v => v != null && v.Contains(Old));
        Assert.DoesNotContain(await db.Audit.Select(a => a.Detail).ToListAsync(), d => d.Contains(Old));
        Assert.Contains(await db.Audit.ToListAsync(), a => a.Action == "api_key.migrated" && a.Detail.Contains("…WxYz"));
    }

    [Fact]
    public async Task Flytningen_kan_køre_ved_hver_opstart_uden_dubletter()
    {
        Assert.False(await ApiKeyMigration.FromKontrolrumKey(db, settings, Now));   // ingen gammel nøgle
        await settings.Set(ApiKeyMigration.OldSetting, Old);
        Assert.True(await ApiKeyMigration.FromKontrolrumKey(db, settings, Now));
        Assert.False(await ApiKeyMigration.FromKontrolrumKey(db, settings, Now));
        // samme gamle nøgle skrevet igen (fx en gammel sikkerhedskopi): ingen dublet, men klarteksten ryddes
        await settings.Set(ApiKeyMigration.OldSetting, Old);
        Assert.False(await ApiKeyMigration.FromKontrolrumKey(db, settings, Now));
        Assert.Equal(1, await db.ApiKeys.CountAsync());
        Assert.Equal("", await settings.Get(ApiKeyMigration.OldSetting));
    }

    [Fact]
    public async Task Gammel_nøgle_flyttes_også_når_der_allerede_er_andre_nøgler()
    {
        db.ApiKeys.Add(new ApiKey { Name = "Home Assistant", Sha256 = Crypto.Sha256("fd_x"), Hint = "x" });
        await db.SaveChangesAsync();
        await settings.Set(ApiKeyMigration.OldSetting, Old);
        Assert.True(await ApiKeyMigration.FromKontrolrumKey(db, settings, Now));
        Assert.Equal(2, await db.ApiKeys.CountAsync());
    }
}

/// <summary>Over rigtig HTTP (Kestrel på en tilfældig port, SQLite i hukommelsen).</summary>
public sealed class ApiKeyEndpointTests : IAsyncLifetime
{
    private const string Old = "gammelKontrolrumNoegle0123456789abcdWxYz";
    private readonly ApiClock clock = new(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
    private readonly SqliteConnection conn = new("Data Source=:memory:");
    private WebApplication app = null!;
    private HttpClient http = null!;

    public async Task InitializeAsync()
    {
        conn.Open();
        var b = WebApplication.CreateBuilder();
        b.Logging.ClearProviders();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        b.Services.AddDbContext<AppDb>(o => o.UseSqlite(conn));
        b.Services.AddSingleton<TimeProvider>(clock);
        b.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        b.Services.AddScoped<SettingsService>();
        b.Services.AddScoped<AuditService>();
        b.Services.AddSingleton<KontrolrumSso>();
        b.Services.AddSingleton<ApiKeyGuard>();
        b.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
        {
            o.Cookie.Name = Csrf.SessionCookie;
            o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
            o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
        });
        b.Services.AddAuthorizationBuilder()
            .AddPolicy("admin", p => p.RequireRole(Roles.Admin).AddRequirements(new TwoFactorRequirement()));
        app = b.Build();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            db.Database.EnsureCreated();
            // som ved opstart efter opdateringen: den gamle nøgle flyttes
            var s = scope.ServiceProvider.GetRequiredService<SettingsService>();
            await s.Set(ApiKeyMigration.OldSetting, Old);
            await ApiKeyMigration.FromKontrolrumKey(db, s, clock.Now.UtcDateTime);
        }
        app.Use((ctx, next) => Csrf.Middleware(ctx, next));
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapKontrolrumEndpoints();
        app.MapApiKeyEndpoints();
        // Kun i testen: et styre-endpoint (der findes ingen endnu) og et login uden adgangskode
        app.MapPost("/api/test/styr", () => Results.Ok(new { ok = true })).RequireApiKey(ApiScopes.Control, "test");
        app.MapGet("/test/login/{role}", async (string role, HttpContext ctx) =>
        {
            await ctx.SignInAsync(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, role + "-bruger"), new Claim(ClaimTypes.Role, role), new Claim(TwoFactorPolicy.Claim, "1")], "test")));
            return Results.Ok();
        });
        await app.StartAsync();
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        http = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(url) };
    }

    public async Task DisposeAsync()
    {
        http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        conn.Dispose();
    }

    private T Db<T>(Func<AppDb, T> f)
    {
        using var scope = app.Services.CreateScope();
        return f(scope.ServiceProvider.GetRequiredService<AppDb>());
    }

    private async Task<string> Login(string role = Roles.Admin)
    {
        var r = await http.GetAsync($"test/login/{role}");
        return r.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]).First(c => c.StartsWith(Csrf.SessionCookie + "="));
    }

    private async Task<HttpResponseMessage> Send(HttpMethod m, string path, object? body = null, string? cookie = null,
        string? bearer = null, string? xApiKey = null, Dictionary<string, string>? headers = null)
    {
        var req = new HttpRequestMessage(m, path);
        req.Headers.Add("X-Fleetdeck", "1");
        if (cookie is not null) req.Headers.Add("Cookie", cookie);
        if (bearer is not null) req.Headers.Add("Authorization", "Bearer " + bearer);
        if (xApiKey is not null) req.Headers.Add("X-Api-Key", xApiKey);
        foreach (var (k, v) in headers ?? []) req.Headers.Add(k, v);
        if (body is not null) req.Content = JsonContent.Create(body, options: JsonSerializerOptions.Web);
        return await http.SendAsync(req);
    }

    private Task<HttpResponseMessage> Status(string? bearer = null, string? xApiKey = null, Dictionary<string, string>? headers = null) =>
        Send(HttpMethod.Get, "api/kontrolrum/status", bearer: bearer, xApiKey: xApiKey, headers: headers);

    private async Task<(string key, int id)> Create(string admin, string name, string scope = ApiScopes.Read, bool lanOnly = true, int? days = null)
    {
        var r = await Send(HttpMethod.Post, "api/settings/api-keys", new { name, scope, lanOnly, expiresDays = days }, admin);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await r.Content.ReadFromJsonAsync<JsonElement>();
        return (j.GetProperty("key").GetString()!, j.GetProperty("item").GetProperty("id").GetInt32());
    }

    private static async Task<string?> Error(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("error", out var e) ? e.GetString() : null;

    [Fact]
    public async Task Kontrolrums_gamle_nøgle_virker_videre_i_x_api_key_og_som_bearer()
    {
        Assert.Equal(HttpStatusCode.OK, (await Status(xApiKey: Old)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Status(bearer: Old)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Status()).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Status(xApiKey: Old + "x")).StatusCode);
        // aldrig i adressen
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, $"api/kontrolrum/status?api_key={Old}&key={Old}")).StatusCode);
        // den gamle nøgle virker udefra som før (kun hjemmenettet er slået fra på den flyttede nøgle)
        Assert.Equal(HttpStatusCode.OK, (await Status(xApiKey: Old, headers: new() { ["CF-Connecting-IP"] = "203.0.113.9" })).StatusCode);
        Assert.Equal(1, Db(db => db.ApiKeys.Single().Uses));
    }

    [Fact]
    public async Task Ny_nøgle_virker_vises_én_gang_og_lækker_ingen_steder()
    {
        var admin = await Login();
        var (key, id) = await Create(admin, "Home Assistant");
        Assert.Matches("^fd_[A-Za-z0-9_-]{43}$", key);
        Assert.Equal(HttpStatusCode.OK, (await Status(bearer: key)).StatusCode);

        var list = await (await Send(HttpMethod.Get, "api/settings/api-keys", cookie: admin)).Content.ReadAsStringAsync();
        Assert.DoesNotContain(key, list);
        Assert.DoesNotContain(Old, list);
        Assert.Contains("fd_…" + key[^4..], list);
        var settings = await (await Send(HttpMethod.Get, "api/settings/kontrolrum", cookie: admin)).Content.ReadAsStringAsync();
        Assert.DoesNotContain(key, settings);
        Assert.DoesNotContain(Old, settings);
        Assert.DoesNotContain("\"key\"", settings);

        Db(db =>
        {
            var k = db.ApiKeys.Single(x => x.Id == id);
            Assert.Equal(Crypto.Sha256(key), k.Sha256);
            Assert.Equal("admin-bruger", k.CreatedBy);
            Assert.True(k.LanOnly);                               // standard: kun hjemmenettet
            foreach (var row in db.ApiKeys.AsNoTracking().ToList()) Assert.DoesNotContain(key, JsonSerializer.Serialize(row));
            Assert.DoesNotContain(db.Settings.Select(s => s.Value).ToList(), v => v != null && (v.Contains(key) || v.Contains(Old)));
            Assert.DoesNotContain(db.Audit.Select(a => a.Detail).ToList(), d => d.Contains(key) || d.Contains(Old));
            Assert.Contains(db.Audit.ToList(), a => a.Action == "api_key.created" && a.User == "admin-bruger" && a.Detail.Contains("Home Assistant"));
            return 0;
        });
        // den gamle "vis/lav ny Kontrolrum-nøgle" findes ikke længere
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Post, "api/settings/kontrolrum-key", new { }, admin)).StatusCode);
    }

    [Fact]
    public async Task Kun_administratorer_kan_se_og_lave_nøgler()
    {
        var viewer = await Login(Roles.Viewer);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, "api/settings/api-keys")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, "api/settings/api-keys", cookie: viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, "api/settings/api-keys", new { name = "x", scope = "admin" }, viewer)).StatusCode);
        // en API-nøgle kan ikke bruges til at administrere nøglerne (heller ikke en administrator-nøgle)
        var (adminKey, _) = await Create(await Login(), "Script", ApiScopes.Admin);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, "api/settings/api-keys", bearer: adminKey)).StatusCode);
    }

    [Fact]
    public async Task Slettet_eller_udløbet_nøgle_giver_401_og_de_andre_virker_videre()
    {
        var admin = await Login();
        var (a, aId) = await Create(admin, "A");
        var (b2, _) = await Create(admin, "B", days: 30);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Delete, $"api/settings/api-keys/{aId}", cookie: admin)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Status(bearer: a)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Status(bearer: b2)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Status(xApiKey: Old)).StatusCode);
        clock.Now = clock.Now.AddDays(31);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Status(bearer: b2)).StatusCode);
        var list = await (await Send(HttpMethod.Get, "api/settings/api-keys", cookie: await Login())).Content.ReadFromJsonAsync<JsonElement>();
        Assert.DoesNotContain(list.GetProperty("keys").EnumerateArray(), k => k.GetProperty("name").GetString() == "A");
        var rej = list.GetProperty("lastRejected");
        Assert.Equal("udløbet nøgle", rej.GetProperty("reason").GetString());
        Assert.Equal("B", rej.GetProperty("name").GetString());
        Assert.Contains(Db(db => db.Audit.ToList()), x => x.Action == "api_key.deleted" && x.Detail.StartsWith("A (#"));
    }

    [Fact]
    public async Task Kun_hjemmenettet_afviser_kald_gennem_tunnelen_med_403()
    {
        var admin = await Login();
        var (key, id) = await Create(admin, "HA");
        var tunnel = new Dictionary<string, string> { ["CF-Connecting-IP"] = "203.0.113.9" };
        var r = await Status(bearer: key, headers: tunnel);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("api_key_home_network_only", await Error(r));
        Assert.Equal(HttpStatusCode.OK, (await Status(bearer: key)).StatusCode);
        // ejeren kan slå det fra på nøglen
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, $"api/settings/api-keys/{id}", new { lanOnly = false }, admin)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Status(bearer: key, headers: tunnel)).StatusCode);
    }

    [Fact]
    public async Task Forkert_rettighed_giver_403_og_styre_kald_har_egen_grænse()
    {
        var admin = await Login();
        var (read, _) = await Create(admin, "Læser");
        var (control, _) = await Create(admin, "Styrer", ApiScopes.Control);
        var r = await Send(HttpMethod.Post, "api/test/styr", new { }, bearer: read);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("api_key_scope", await Error(r));
        for (var i = 0; i < ApiKeyGuard.ControlPerMinute; i++)
            Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, "api/test/styr", new { }, bearer: control)).StatusCode);
        var limited = await Send(HttpMethod.Post, "api/test/styr", new { }, bearer: control);
        Assert.Equal((HttpStatusCode)429, limited.StatusCode);
        Assert.True(limited.Headers.RetryAfter is not null);
        Assert.Equal(HttpStatusCode.OK, (await Status(bearer: control)).StatusCode);   // læse-kald har sin egen grænse
    }

    [Fact]
    public async Task For_mange_forkerte_nøgler_bremser_ip_en_med_retry_after()
    {
        for (var i = 0; i < ApiKeyGuard.FailuresPerIp; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await Status(bearer: "fd_forkert" + i)).StatusCode);
        var r = await Status(xApiKey: Old);
        Assert.Equal((HttpStatusCode)429, r.StatusCode);
        Assert.True(r.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        // afvisningen står i revisionsloggen med hint – aldrig hele nøglen – og højst én gang i minuttet pr. IP
        var audit = Db(db => db.Audit.Where(a => a.Action == "api_key.rejected").ToList());
        Assert.Single(audit);
        Assert.DoesNotContain("fd_forkert", audit[0].Detail);
        clock.Now = clock.Now.AddMinutes(5);
        Assert.Equal(HttpStatusCode.OK, (await Status(xApiKey: Old)).StatusCode);
    }

    [Fact]
    public async Task Ny_nøgle_valideres_og_navne_er_unikke()
    {
        var admin = await Login();
        await Create(admin, "HA");
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, "api/settings/api-keys", new { name = "ha", scope = "read" }, admin)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, "api/settings/api-keys", new { name = "x", scope = "root" }, admin)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, "api/settings/api-keys", new { name = "x", scope = "read", expiresDays = 7 }, admin)).StatusCode);
    }

    [Fact]
    public async Task Siden_viser_hvilke_endpoints_hver_rettighed_giver()
    {
        var admin = await Login();
        var j = await (await Send(HttpMethod.Get, "api/settings/api-keys", cookie: admin)).Content.ReadFromJsonAsync<JsonElement>();
        var eps = j.GetProperty("endpoints").EnumerateArray().Select(e => (e.GetProperty("method").GetString(), e.GetProperty("path").GetString(), e.GetProperty("scope").GetString())).ToList();
        Assert.Contains(("GET", "/api/kontrolrum/status", "read"), eps);
        Assert.Contains(("POST", "/api/test/styr", "control"), eps);
        Assert.Equal("fd_", j.GetProperty("prefix").GetString());
    }
}
