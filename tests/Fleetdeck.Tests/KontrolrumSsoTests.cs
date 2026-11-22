using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
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
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Endpoints;
using Fleetdeck.Server.Services;

namespace Fleetdeck.Tests;

/// <summary>Formatet og tjekkene uden HTTP – og en testvektor lavet med Kontrolrums egen kode (server/formlogin.js).</summary>
public class KontrolrumSsoFormatTests
{
    // Formatet er det samme som Kontrolrums fleetdeckAssertion(key, "søren", Date.UTC(2025, 10, 18, 12)) giver. Nøglen er et
    // åbenlyst falsk mønster (aldrig en rigtig nøgle), og signaturen er regnet uafhængigt af C#-koden med Pythons hmac-modul:
    //   python3 -c "import hmac,hashlib; print(hmac.new(KEY.encode(), TEXT.encode(), hashlib.sha256).hexdigest())"
    public static readonly string VectorKey = string.Concat(Enumerable.Repeat("0123456789abcdef", 4));
    public const string VectorJson = """{"v":1,"aud":"fleetdeck","sub":"søren","iat":1763467200,"exp":1763467260,"nonce":"1f0edbeb4a2c0a4cdddf9964b25ffbfe","sig":"401dcc5588cb60f37f14f37576c772348f0bcb7d2e720a965756fba950468a0f"}""";

    private static KontrolrumAssertion Parse(string json) => JsonSerializer.Deserialize<KontrolrumAssertion>(json, JsonSerializerOptions.Web)!;

    [Fact]
    public void Kontrolrums_signatur_godkendes_byte_for_byte()
    {
        var a = Parse(VectorJson);
        Assert.Equal("kontrolrum-sso\n1\nfleetdeck\nsøren\n1763467200\n1763467260\n1f0edbeb4a2c0a4cdddf9964b25ffbfe", KontrolrumSso.Text(a));
        Assert.Equal(a.Sig, KontrolrumSso.Sign(VectorKey, a));
        Assert.True(KontrolrumSso.WellFormed(a));
        Assert.True(KontrolrumSso.SignatureValid(VectorKey, a));
        Assert.True(KontrolrumSso.SignatureValid(VectorKey, a with { Sig = a.Sig!.ToUpperInvariant() }));
    }

    [Fact]
    public void Ændret_felt_eller_forkert_nøgle_afvises()
    {
        var a = Parse(VectorJson);
        Assert.False(KontrolrumSso.SignatureValid(VectorKey, a with { Sub = "admin" }));
        Assert.False(KontrolrumSso.SignatureValid(VectorKey, a with { Exp = a.Exp + 1 }));
        Assert.False(KontrolrumSso.SignatureValid(VectorKey[..^1] + "0", a));
        Assert.False(KontrolrumSso.SignatureValid(VectorKey, a with { Sig = "ikke-hex" }));
        Assert.False(KontrolrumSso.SignatureValid(VectorKey, a with { Sig = a.Sig![..62] }));
    }

    [Fact]
    public void Ny_nøgle_er_32_tilfældige_bytes_i_hex()
    {
        var k = KontrolrumSso.NewKey();
        Assert.Matches("^[0-9a-f]{64}$", k);
        Assert.NotEqual(k, KontrolrumSso.NewKey());
    }

    [Fact]
    public void Ufuldstændig_påstand_eller_mærkelig_engangsværdi_afvises()
    {
        var a = Parse(VectorJson);
        Assert.False(KontrolrumSso.WellFormed(null));
        Assert.False(KontrolrumSso.WellFormed(a with { Sub = "" }));
        Assert.False(KontrolrumSso.WellFormed(a with { Iat = null }));
        Assert.False(KontrolrumSso.WellFormed(a with { Nonce = "kort" }));
        Assert.False(KontrolrumSso.WellFormed(a with { Nonce = "abc%def'ghi;jklmnop" }));
    }
}

/// <summary>Endpointet over rigtig HTTP (Kestrel på en tilfældig port, SQLite i hukommelsen).</summary>
public sealed class KontrolrumSsoEndpointTests : IAsyncLifetime
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private const string Key = "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90";
    private readonly Clock clock = new(DateTimeOffset.FromUnixTimeSeconds(1763467210));
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
        b.Services.AddSingleton<TwoFactorService>();
        b.Services.AddSingleton<KontrolrumSso>();
        b.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
        {
            o.Cookie.Name = Csrf.SessionCookie;
            o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
            o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
        });
        b.Services.AddAuthorizationBuilder()
            .AddPolicy("admin", p => p.RequireRole(Roles.Admin).AddRequirements(new TwoFactorRequirement()))
            .AddPolicy("session", p => p.RequireAuthenticatedUser());
        b.Services.AddRateLimiter(o => { o.RejectionStatusCode = 429; KontrolrumSso.AddRateLimit(o); });
        app = b.Build();
        using (var scope = app.Services.CreateScope()) scope.ServiceProvider.GetRequiredService<AppDb>().Database.EnsureCreated();
        app.Use((ctx, next) => Csrf.Middleware(ctx, next));
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.MapAuthEndpoints();
        app.MapKontrolrumEndpoints();
        // Ville sessionen også klare den strengeste politik ("påkrævet for alle")?
        app.MapGet("/test/strict", (ClaimsPrincipal p) => new { ok = TwoFactorPolicy.Satisfied(p, TwoFactorPolicy.All) }).RequireAuthorization("session");
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

    private void EnableSso(string key = Key) => Db(db =>
    {
        db.Settings.Add(new Setting { Key = KontrolrumSso.KeySetting, Value = app.Services.GetRequiredService<KontrolrumSso>().Protect(key) });
        return db.SaveChanges();
    });

    private int AddUser(string name, bool allowed, bool totp = false, string role = Roles.Admin) => Db(db =>
    {
        var u = new User { Username = name, PasswordHash = Crypto.HashPassword("hemmelig-kode"), Role = role, KontrolrumSso = allowed, TotpSecret = totp ? "beskyttet" : null };
        db.Users.Add(u);
        db.SaveChanges();
        return u.Id;
    });

    private KontrolrumAssertion Assertion(string sub, string key = Key, long? iat = null, int life = 60, string aud = "fleetdeck", string? nonce = null)
    {
        var i = iat ?? clock.Now.ToUnixTimeSeconds();
        var a = new KontrolrumAssertion(1, aud, sub, i, i + life, nonce ?? Guid.NewGuid().ToString("N"), null);
        return a with { Sig = KontrolrumSso.Sign(key, a) };
    }

    private async Task<HttpResponseMessage> Post(object body, string? cookie = null) => await Send(HttpMethod.Post, "api/auth/kontrolrum", body, cookie);

    private async Task<HttpResponseMessage> Send(HttpMethod m, string path, object? body = null, string? cookie = null)
    {
        var req = new HttpRequestMessage(m, path);
        req.Headers.Add("X-Fleetdeck", "1");
        if (cookie is not null) req.Headers.Add("Cookie", cookie);
        if (body is string s) req.Content = new StringContent(s, Encoding.UTF8, "application/json");
        else if (body is not null) req.Content = JsonContent.Create(body, options: JsonSerializerOptions.Web);
        return await http.SendAsync(req);
    }

    private static string? Session(HttpResponseMessage r) =>
        r.Headers.TryGetValues("Set-Cookie", out var v) ? v.Select(c => c.Split(';')[0]).FirstOrDefault(c => c.StartsWith(Csrf.SessionCookie + "=")) : null;

    private static async Task<string?> Error(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("error", out var e) ? e.GetString() : null;

    private List<AuditEntry> Audit() => Db(db => db.Audit.AsNoTracking().OrderBy(a => a.Id).ToList());

    [Fact]
    public async Task Gyldig_påstand_giver_en_session_og_skrives_i_revisionsloggen()
    {
        EnableSso();
        AddUser("kontrolrum", allowed: true);
        var r = await Post(Assertion("kontrolrum"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("kontrolrum", j.GetProperty("username").GetString());
        Assert.False(j.GetProperty("mustEnroll").GetBoolean());
        var cookie = Session(r);
        Assert.NotNull(cookie);
        var me = await Send(HttpMethod.Get, "api/auth/me", cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Contains(Audit(), a => a.Action == "login.kontrolrum" && a.User == "kontrolrum" && a.Detail.Contains("Kontrolrum-SSO"));
    }

    [Fact]
    public async Task Testvektoren_fra_Kontrolrum_logger_ind()
    {
        EnableSso(KontrolrumSsoFormatTests.VectorKey);
        AddUser("søren", allowed: true);
        var r = await Post(KontrolrumSsoFormatTests.VectorJson);   // præcis de bytes Kontrolrum sender
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.NotNull(Session(r));
    }

    [Fact]
    public async Task Bruger_med_totrinsbekræftelse_får_en_færdig_session()
    {
        EnableSso();
        AddUser("anna", allowed: true, totp: true);
        var r = await Post(Assertion("anna"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(j.TryGetProperty("twoFactor", out _));   // ingen kode-trin
        Assert.False(j.GetProperty("mustEnroll").GetBoolean());
        var cookie = Session(r)!;
        var me = await (await Send(HttpMethod.Get, "api/auth/me", cookie: cookie)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(me.GetProperty("twoFactor").GetProperty("enabled").GetBoolean());
        Assert.False(me.GetProperty("twoFactor").GetProperty("mustEnroll").GetBoolean());
        var strict = await (await Send(HttpMethod.Get, "test/strict", cookie: cookie)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(strict.GetProperty("ok").GetBoolean());
        // og sessionen kan bruge en admin-side, som kræver totrinsbekræftelse opfyldt
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "api/settings/kontrolrum", cookie: cookie)).StatusCode);
    }

    [Fact]
    public async Task Forkert_signatur_giver_401()
    {
        EnableSso();
        AddUser("kontrolrum", allowed: true);
        var r = await Post(Assertion("kontrolrum", key: "forkert"));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal("bad_signature", await Error(r));
        Assert.Null(Session(r));
        Assert.Contains(Audit(), a => a.Action == "login.kontrolrum_failed" && a.Detail.Contains("bad_signature"));
    }

    [Fact]
    public async Task Forkert_modtager_afvises()
    {
        EnableSso();
        AddUser("kontrolrum", allowed: true);
        var r = await Post(Assertion("kontrolrum", aud: "spamfilter"));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal("bad_audience", await Error(r));
    }

    [Fact]
    public async Task Udløbet_afvises_men_60_sekunders_urforskel_tåles()
    {
        EnableSso();
        AddUser("kontrolrum", allowed: true);
        var now = clock.Now.ToUnixTimeSeconds();
        var old = await Post(Assertion("kontrolrum", iat: now - 200));   // exp = now - 140
        Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        Assert.Equal("expired", await Error(old));
        var future = await Post(Assertion("kontrolrum", iat: now + 90));
        Assert.Equal("expired", await Error(future));
        Assert.Equal(HttpStatusCode.OK, (await Post(Assertion("kontrolrum", iat: now - 110))).StatusCode);   // exp 50 s siden
        Assert.Equal(HttpStatusCode.OK, (await Post(Assertion("kontrolrum", iat: now + 45))).StatusCode);   // uret foran
    }

    [Fact]
    public async Task For_langt_vindue_afvises()
    {
        EnableSso();
        AddUser("kontrolrum", allowed: true);
        var r = await Post(Assertion("kontrolrum", life: 121));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal("expired", await Error(r));
        Assert.Equal(HttpStatusCode.OK, (await Post(Assertion("kontrolrum", life: 120))).StatusCode);
    }

    [Fact]
    public async Task Genafspilning_af_samme_engangsværdi_afvises()
    {
        EnableSso();
        AddUser("kontrolrum", allowed: true);
        var a = Assertion("kontrolrum");
        Assert.Equal(HttpStatusCode.OK, (await Post(a)).StatusCode);
        var again = await Post(a);
        Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
        Assert.Equal("replay", await Error(again));
        // gemt i databasen (overlever en genstart), og ryddes op, når påstanden ikke længere kan godkendes
        Assert.True(Db(db => db.Settings.Any(s => s.Key == KontrolrumSso.NoncePrefix + a.Nonce)));
        clock.Now = clock.Now.AddMinutes(5);
        Assert.Equal(HttpStatusCode.OK, (await Post(Assertion("kontrolrum"))).StatusCode);
        Assert.False(Db(db => db.Settings.Any(s => s.Key == KontrolrumSso.NoncePrefix + a.Nonce)));
    }

    [Fact]
    public async Task Slået_fra_giver_403()
    {
        AddUser("kontrolrum", allowed: true);
        var r = await Post(Assertion("kontrolrum"));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("sso_disabled", await Error(r));
    }

    [Fact]
    public async Task Bruger_uden_flueben_eller_ukendt_bruger_afvises()
    {
        EnableSso();
        AddUser("ejer", allowed: false);
        var r = await Post(Assertion("ejer"));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal("unknown_user", await Error(r));
        Assert.Null(Session(r));
        Assert.Equal("unknown_user", await Error(await Post(Assertion("findes-ikke"))));
    }

    [Fact]
    public async Task Rate_limit_pr_klient_IP_giver_429()
    {
        EnableSso();
        AddUser("kontrolrum", allowed: true);
        for (var i = 0; i < KontrolrumSso.RatePermit; i++)
            Assert.NotEqual(HttpStatusCode.TooManyRequests, (await Post(Assertion("kontrolrum", key: "forkert"))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Post(Assertion("kontrolrum"))).StatusCode);
    }

    [Fact]
    public async Task Nøglen_vises_kun_når_den_laves_og_en_ny_nøgle_gør_den_gamle_ugyldig()
    {
        AddUser("admin", allowed: true);
        AddUser("kontrolrum", allowed: true);
        EnableSso();
        var admin = Session(await Post(Assertion("admin")))!;

        var made = await Send(HttpMethod.Post, "api/settings/kontrolrum-sso", new { }, admin);
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        var key1 = (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString()!;
        Assert.Matches("^[0-9a-f]{64}$", key1);

        // aldrig igen: ikke i indstillingerne, ikke i klartekst i databasen og ikke i revisionsloggen
        var settings = await (await Send(HttpMethod.Get, "api/settings/kontrolrum", cookie: admin)).Content.ReadAsStringAsync();
        Assert.DoesNotContain(key1, settings);
        var info = JsonDocument.Parse(settings).RootElement.GetProperty("sso");
        Assert.True(info.GetProperty("enabled").GetBoolean());
        Assert.Equal(KontrolrumSso.Fingerprint(key1), info.GetProperty("fingerprint").GetString());
        Assert.DoesNotContain(key1, Db(db => db.Settings.Single(s => s.Key == KontrolrumSso.KeySetting).Value));
        Assert.DoesNotContain(Audit(), a => a.Detail.Contains(key1));

        Assert.Equal(HttpStatusCode.OK, (await Post(Assertion("kontrolrum", key: key1))).StatusCode);
        var key2 = (await (await Send(HttpMethod.Post, "api/settings/kontrolrum-sso", new { }, admin)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString()!;
        Assert.NotEqual(key1, key2);
        Assert.Equal("bad_signature", await Error(await Post(Assertion("kontrolrum", key: key1))));
        Assert.Equal(HttpStatusCode.OK, (await Post(Assertion("kontrolrum", key: key2))).StatusCode);

        // Slå fra
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Delete, "api/settings/kontrolrum-sso", cookie: admin)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(Assertion("kontrolrum", key: key2))).StatusCode);
    }

    [Fact]
    public async Task Fluebenet_sættes_kun_af_administratorer()
    {
        EnableSso();
        AddUser("admin", allowed: true);
        AddUser("læser", allowed: true, role: Roles.Viewer);
        var target = AddUser("mor", allowed: false, role: Roles.Parent);
        var viewer = Session(await Post(Assertion("læser")))!;
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Put, $"api/settings/kontrolrum-sso/users/{target}", new { allowed = true }, viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, "api/settings/kontrolrum-sso", new { }, viewer)).StatusCode);

        var admin = Session(await Post(Assertion("admin")))!;
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, $"api/settings/kontrolrum-sso/users/{target}", new { allowed = true }, admin)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post(Assertion("mor"))).StatusCode);
        Assert.Contains(Audit(), a => a.Action == "user.kontrolrum_sso" && a.Detail.StartsWith("mor"));
    }

    [Fact]
    public void Ny_bruger_har_ikke_fluebenet() => Assert.False(new User().KontrolrumSso);
}
