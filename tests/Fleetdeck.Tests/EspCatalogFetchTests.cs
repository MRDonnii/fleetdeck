using System.Net;
using System.Text;
using System.Text.Json;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Tests;

/// <summary>Hentning af et privat katalog med nøgle: GitHub gennem git/trees + git/blobs, og nøglen følger aldrig med til en anden vært.</summary>
public sealed class EspFetchTests
{
    private const string Token = "github_pat_TESTtoken_0123456789abcdef";

    private sealed record Seen(string Url, string? Auth, string? Accept);

    /// <summary>Svarer ud fra adressen; husker hver forespørgsel med dens Authorization- og Accept-header.</summary>
    private sealed class FakeNet(Func<Uri, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public readonly List<Seen> Seen = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Seen.Add(new(r.RequestUri!.ToString(), r.Headers.Authorization?.ToString(), r.Headers.Accept.ToString()));
            return Task.FromResult(answer(r.RequestUri!));
        }
    }

    private static HttpResponseMessage Ok(byte[] b) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(b) };
    private static HttpResponseMessage Ok(string s) => Ok(Encoding.UTF8.GetBytes(s));
    private static HttpResponseMessage Redirect(string to) => new(HttpStatusCode.Found) { Headers = { Location = new Uri(to) } };

    private static EspFetch Fetch(FakeNet net) => new(() => new HttpClient(net, disposeHandler: false));

    private static readonly byte[] AllFF = Enumerable.Repeat((byte)0xFF, 8192).ToArray();

    private static FakeNet GitHub() => new(u => u.AbsoluteUri switch
    {
        "https://api.github.com/repos/owner/private-fw/git/trees/main?recursive=1" => Ok(JsonSerializer.Serialize(new
        {
            tree = new object[]
            {
                new { path = "profiles", type = "tree", sha = "t1" },
                new { path = "profiles/catalog.json", type = "blob", sha = "c1" },
                new { path = "profiles/dev/ota_data_initial.bin", type = "blob", sha = "b1" },
            },
        })),
        "https://api.github.com/repos/owner/private-fw/git/blobs/c1" => Ok("{\"firmwares\":[]}"),
        "https://api.github.com/repos/owner/private-fw/git/blobs/b1" => Ok(AllFF),
        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
    });

    [Fact]
    public async Task Private_GitHub_repo_is_read_through_trees_and_raw_blobs_so_binaries_stay_intact()
    {
        var net = GitHub();
        var f = Fetch(net);
        const string Base = "https://raw.githubusercontent.com/owner/private-fw/main/profiles/";
        Assert.Equal("{\"firmwares\":[]}", Encoding.UTF8.GetString(await f.Get(Base + "catalog.json", 1 << 20, Token, default)));
        Assert.Equal(AllFF, await f.Get(Base + "dev/ota_data_initial.bin", 1 << 20, Token, default));   // byte for byte, ikke omkodet
        Assert.All(net.Seen, s => Assert.StartsWith("https://api.github.com/repos/owner/private-fw/git/", s.Url));
        Assert.All(net.Seen, s => Assert.Equal("Bearer " + Token, s.Auth));
        Assert.Single(net.Seen, s => s.Url.Contains("/git/trees/"));          // træet hentes én gang pr. tjek
        Assert.Contains(net.Seen, s => s.Url.EndsWith("/git/blobs/b1") && s.Accept == "application/vnd.github.raw");
        Assert.DoesNotContain(net.Seen, s => s.Url.Contains("/contents/"));   // aldrig contents-API'et
    }

    [Fact]
    public async Task Missing_file_in_the_tree_is_a_404_without_the_token()
    {
        var f = Fetch(GitHub());
        var e = await Assert.ThrowsAsync<InvalidDataException>(() =>
            f.Get("https://raw.githubusercontent.com/owner/private-fw/main/profiles/x/manifest.json", 1 << 20, Token, default));
        Assert.Contains("404", e.Message);
        Assert.DoesNotContain(Token, e.Message);
    }

    [Fact]
    public async Task Without_a_token_the_address_is_fetched_as_it_is()
    {
        var net = new FakeNet(_ => Ok("{}"));
        await Fetch(net).Get("https://raw.githubusercontent.com/owner/public-fw/main/fw/catalog.json", 1 << 20, "", default);
        var s = Assert.Single(net.Seen);
        Assert.Equal("https://raw.githubusercontent.com/owner/public-fw/main/fw/catalog.json", s.Url);
        Assert.Null(s.Auth);
    }

    [Fact]
    public async Task Token_is_never_forwarded_on_a_redirect_to_another_host()
    {
        var net = new FakeNet(u => u.Host switch
        {
            "fw.example" when u.AbsolutePath == "/a/catalog.json" => Redirect("https://fw.example/b/catalog.json"),
            "fw.example" => Redirect("https://cdn.example/b/catalog.json"),
            _ => Ok("{}"),
        });
        await Fetch(net).Get("https://fw.example/a/catalog.json", 1 << 20, Token, default);
        Assert.Equal(3, net.Seen.Count);
        Assert.Equal("Bearer " + Token, net.Seen[0].Auth);    // samme vært: med
        Assert.Equal("Bearer " + Token, net.Seen[1].Auth);
        Assert.Equal("https://cdn.example/b/catalog.json", net.Seen[2].Url);
        Assert.Null(net.Seen[2].Auth);                         // anden vært: aldrig
    }

    [Fact]
    public async Task Token_is_dropped_on_a_downgrade_to_http_and_redirect_loops_stop()
    {
        var net = new FakeNet(u => u.Scheme == "https" ? Redirect("http://fw.example/x") : Ok("{}"));
        await Fetch(net).Get("https://fw.example/catalog.json", 1 << 20, Token, default);
        Assert.Null(net.Seen[1].Auth);

        var loop = new FakeNet(_ => Redirect("https://fw.example/again"));
        var e = await Assert.ThrowsAsync<InvalidDataException>(() => Fetch(loop).Get("https://fw.example/catalog.json", 1 << 20, Token, default));
        Assert.Contains("omdirigeringer", e.Message);
        Assert.Equal(EspFetch.MaxRedirects + 1, loop.Seen.Count);
    }

    [Fact]
    public async Task Errors_explain_the_status_but_never_contain_the_token()
    {
        foreach (var code in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound, HttpStatusCode.InternalServerError })
        {
            var e = await Assert.ThrowsAsync<InvalidDataException>(() =>
                Fetch(new FakeNet(_ => new HttpResponseMessage(code))).Get("https://fw.example/catalog.json", 1 << 20, Token, default));
            Assert.Contains(((int)code).ToString(), e.Message);
            Assert.DoesNotContain(Token, e.Message);
        }
    }

    [Theory]
    [InlineData("github_pat_11AAAA_bbbbCCCC", true)]
    [InlineData("ghp_0123456789abcdefABCDEF", true)]
    [InlineData("kort", false)]
    [InlineData("med mellemrum 0123456789", false)]
    [InlineData("linje\r\nX-Header: 1", false)]
    public void Only_token_like_values_are_sent(string t, bool ok) => Assert.Equal(ok, EspFetch.ValidToken(t));

    /// <summary>Mirror + EspFetch: en fejl i tjekket (fx afvist nøgle) står på siden – uden nøglen.</summary>
    [Fact]
    public async Task Mirror_error_shown_on_the_page_does_not_contain_the_token()
    {
        var root = Path.Combine(Path.GetTempPath(), "fd-espfetch-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "bundled"));
            File.WriteAllText(Path.Combine(root, "bundled", "catalog.json"), "{\"generated\":\"\",\"firmwares\":[]}");
            var f = Fetch(new FakeNet(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));
            var m = new EspFirmwareMirror(Path.Combine(root, "bundled"), Path.Combine(root, "live"), (u, l, ct) => f.Get(u, l, Token, ct));
            m.Start("https://raw.githubusercontent.com/owner/private-fw/main/profiles/");
            Assert.False(await m.Check(default));
            Assert.Contains("401", m.Status.Error);
            Assert.DoesNotContain(Token, m.Status.Error);
            Assert.Equal(0, m.Status.Count);
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    /// <summary>Uden medfølgende firmware: et tomt katalog som start, og det første katalog fra nettet tages i brug.</summary>
    [Fact]
    public async Task Empty_bundle_takes_the_first_catalog_from_the_net()
    {
        var root = Path.Combine(Path.GetTempPath(), "fd-espfetch-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "bundled"));
            File.WriteAllText(Path.Combine(root, "bundled", "catalog.json"), "{\"generated\":\"\",\"firmwares\":[]}");
            var bin = new byte[] { 1, 2, 3 };
            var sha = EspCatalog.PartsHash([bin]);
            var files = new Dictionary<string, byte[]>
            {
                ["https://fw.example/catalog.json"] = Encoding.UTF8.GetBytes($$"""{"generated":"2026-11-22T12:00:00","firmwares":[{"id":"dev","manifest":"dev/manifest.json","sha256":"{{sha}}"}]}"""),
                ["https://fw.example/dev/manifest.json"] = Encoding.UTF8.GetBytes("""{"builds":[{"parts":[{"path":"fw.bin","offset":0}]}]}"""),
                ["https://fw.example/dev/fw.bin"] = bin,
            };
            var m = new EspFirmwareMirror(Path.Combine(root, "bundled"), Path.Combine(root, "live"),
                (u, _, _) => files.TryGetValue(u, out var b) ? Task.FromResult(b) : throw new InvalidDataException("404"));
            m.Start("https://fw.example/");
            Assert.Equal(0, m.Status.Count);
            Assert.True(await m.Check(default));
            Assert.Equal(1, m.Status.Count);
            Assert.NotNull(m.Resolve("dev/fw.bin"));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}

/// <summary>Nøglen gemmes krypteret og kommer aldrig ud igen – siden får kun "der er en nøgle".</summary>
public sealed class EspCatalogSecretTests : IDisposable
{
    private const string Token = "github_pat_TESTtoken_0123456789abcdef";
    private readonly SqliteConnection conn = new("Data Source=:memory:");
    private readonly AppDb db;
    private readonly SettingsService settings;
    private readonly EspCatalogSecret secret;

    public EspCatalogSecretTests()
    {
        conn.Open();
        db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        settings = new SettingsService(db);
        secret = new EspCatalogSecret(settings, new EphemeralDataProtectionProvider());
    }

    public void Dispose() { db.Dispose(); conn.Dispose(); }

    [Fact]
    public async Task Token_is_stored_encrypted_and_only_has_token_is_returned()
    {
        await settings.Set(EspCatalog.UrlSetting, "https://raw.githubusercontent.com/owner/private-fw/main/profiles/");
        Assert.Null(await secret.Save(Token));
        Assert.True(await secret.HasToken());
        var stored = await settings.Get(EspCatalog.TokenSetting);
        Assert.NotEqual(Token, stored);
        Assert.DoesNotContain(Token, stored);
        Assert.Equal(Token, await secret.Token());

        var (url, has) = await secret.Public(admin: true);
        var json = JsonSerializer.Serialize(new { catalogUrl = url, hasToken = has });
        Assert.DoesNotContain(Token, json);
        Assert.DoesNotContain(stored, json);
        Assert.True(has);
        Assert.Null((await secret.Public(admin: false)).Url);   // adressen kun til administratorer
        Assert.DoesNotContain(EspCatalog.TokenSetting, SettingsService.Public);   // og aldrig i de almindelige indstillinger
    }

    [Fact]
    public async Task Bad_token_is_refused_and_clear_removes_it()
    {
        Assert.NotNull(await secret.Save("ikke et token"));
        Assert.False(await secret.HasToken());
        Assert.Null(await secret.Save(Token));
        await secret.Clear();
        Assert.False(await secret.HasToken());
        Assert.Equal("", await secret.Token());
    }

    [Fact]
    public async Task Retired_default_stored_on_an_old_install_shows_as_no_catalog_but_is_not_rewritten()
    {
        await settings.Set(EspCatalog.UrlSetting, EspCatalog.RetiredDefaultUrl);
        Assert.Equal("", (await secret.Public(admin: true)).Url);
        Assert.Equal(EspCatalog.RetiredDefaultUrl, await settings.Get(EspCatalog.UrlSetting));
    }
}
