using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;

namespace Fleetdeck.Tests;

public class EspCatalogTests
{
    private static JsonNode Fw(string id, string sha = "0123456789ab", string? manifest = null) =>
        new JsonObject { ["id"] = id, ["manifest"] = manifest ?? $"{id}/manifest.json", ["sha256"] = sha };

    [Fact]
    public void ValidatesEntriesLikeFwsync()
    {
        Assert.Null(EspCatalog.Validate(Fw("demo-sensor-c3")));
        Assert.NotNull(EspCatalog.Validate(Fw("../etc")));
        Assert.NotNull(EspCatalog.Validate(Fw("a", manifest: "b/manifest.json")));
        Assert.NotNull(EspCatalog.Validate(Fw("a", sha: "0123456789AB")));
        Assert.NotNull(EspCatalog.Validate(Fw("a", sha: "0123")));
        Assert.NotNull(EspCatalog.Validate(new JsonObject { ["id"] = 5 }));
    }

    [Fact]
    public void PartsRejectUnsafeNames()
    {
        Assert.Equal(["boot.bin", "app.bin"], EspCatalog.Parts("""{"builds":[{"parts":[{"path":"boot.bin","offset":0},{"path":"app.bin","offset":65536}]}]}""", "x"));
        Assert.Throws<InvalidDataException>(() => EspCatalog.Parts("""{"builds":[{"parts":[{"path":"../app.bin"}]}]}""", "x"));
        Assert.Throws<InvalidDataException>(() => EspCatalog.Parts("""{"builds":[{"parts":[{"path":"a/b.bin"}]}]}""", "x"));
        Assert.Throws<InvalidDataException>(() => EspCatalog.Parts("""{"builds":[]}""", "x"));
    }

    [Fact]
    public void PartsHashIsSha256OfAllPartsInOrder()
    {
        // sha256("abc") = ba7816bf8f01cfea…
        Assert.Equal("ba7816bf8f01", EspCatalog.PartsHash([Encoding.ASCII.GetBytes("a"), Encoding.ASCII.GetBytes("bc")]));
        Assert.NotEqual("ba7816bf8f01", EspCatalog.PartsHash([Encoding.ASCII.GetBytes("bc"), Encoding.ASCII.GetBytes("a")]));
    }

    [Fact]
    public void NewCatalogOnlyWhenContentChangedAndNotOlder()
    {
        var cur = EspCatalog.Parse("""{"generated":"2026-10-30T16:54:42","firmwares":[{"id":"a","name":"A"}]}""")!;
        var sameReordered = EspCatalog.Parse("""{"firmwares":[{"name":"A","id":"a"}],"generated":"2026-11-01T00:00:00"}""")!;
        var changed = EspCatalog.Parse("""{"generated":"2026-11-01T00:00:00","firmwares":[{"id":"a","name":"A2"}]}""")!;
        var older = EspCatalog.Parse("""{"generated":"2026-10-01T00:00:00","firmwares":[{"id":"a","name":"A2"}]}""")!;
        Assert.False(EspCatalog.ShouldTake(sameReordered, cur));
        Assert.True(EspCatalog.ShouldTake(changed, cur));
        Assert.False(EspCatalog.ShouldTake(older, cur));
        Assert.True(EspCatalog.ShouldTake(changed, null));
        Assert.Null(EspCatalog.Parse("""{"firmwares":{}}"""));
        Assert.Null(EspCatalog.Parse("ikke json"));
    }

    [Theory]
    [InlineData("https://example.com/fw", "https://example.com/fw/")]
    [InlineData(" http://192.0.2.10:8080/firmware/ ", "http://192.0.2.10:8080/firmware/")]
    [InlineData("", "")]
    [InlineData("ftp://example.com/", null)]
    [InlineData("example.com/fw", null)]
    [InlineData("https://example.com/fw?x=1", null)]
    [InlineData("https://example.com/fw/catalog.json", "https://example.com/fw/")]
    [InlineData("https://github.com/owner/repo/tree/main/profiles", "https://raw.githubusercontent.com/owner/repo/main/profiles/")]
    [InlineData("https://github.com/owner/repo/tree/main/profiles/", "https://raw.githubusercontent.com/owner/repo/main/profiles/")]
    [InlineData("https://github.com/owner/repo/blob/v2/fw/catalog.json", "https://raw.githubusercontent.com/owner/repo/v2/fw/")]
    [InlineData("https://github.com/owner/repo.git", "https://raw.githubusercontent.com/owner/repo/main/")]
    [InlineData("https://github.com/owner/repo", "https://raw.githubusercontent.com/owner/repo/main/")]
    [InlineData("https://raw.githubusercontent.com/owner/repo/main/profiles", "https://raw.githubusercontent.com/owner/repo/main/profiles/")]
    [InlineData("https://github.com/owner", null)]
    [InlineData("https://github.com/owner/repo/issues", null)]
    [InlineData("https://user:pw@example.com/fw", null)]
    public void NormalizesCatalogUrl(string input, string? expected) => Assert.Equal(expected, EspCatalog.NormalizeUrl(input));

    /// <summary>Den medfølgende kopi (reserve uden net) skal passe med sit eget katalog – ellers er den ikke opdateret helt.</summary>
    [Fact]
    public void NoDefaultCatalogAndTheRetiredDefaultCountsAsNone()
    {
        Assert.Equal("", EspCatalog.DefaultUrl);
        Assert.Equal("", EspCatalog.Effective(EspCatalog.RetiredDefaultUrl));          // gemt på ældre installationer: intet katalog
        Assert.Equal("", EspCatalog.Effective(EspCatalog.RetiredDefaultUrl.TrimEnd('/')));
        Assert.Equal("", EspCatalog.Effective(null));
        Assert.Equal("", EspCatalog.Effective("ikke en adresse"));
        Assert.Equal("https://fw.example/x/", EspCatalog.Effective("https://fw.example/x"));   // en egen adresse beholdes
    }

    /// <summary>Fleetdeck har ingen firmware med (ejerens profiler er private): kataloget er tomt, men gyldigt.</summary>
    [Fact]
    public void BundledCatalogIsEmpty()
    {
        var root = BundledRoot();
        var cat = EspCatalog.Parse(File.ReadAllText(Path.Combine(root, "catalog.json")));
        Assert.NotNull(cat);
        Assert.Empty((JsonArray)cat!["firmwares"]!);
        Assert.Empty(Directory.GetDirectories(root));
    }

    private static string BundledRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Fleetdeck.Server", "EspFirmware"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "Fleetdeck.Server", "EspFirmware");
    }

    [Fact]
    public void BundledCatalogMatchesFiles()
    {
        var root = BundledRoot();
        var cat = EspCatalog.Parse(File.ReadAllText(Path.Combine(root, "catalog.json")))!;
        var list = (JsonArray)cat["firmwares"]!;   // tomt er i orden
        foreach (var fw in list)
        {
            Assert.Null(EspCatalog.Validate(fw));
            var id = (string)fw!["id"]!;
            var parts = EspCatalog.Parts(File.ReadAllText(Path.Combine(root, id, "manifest.json")), id);
            Assert.Equal((string)fw["sha256"]!, EspCatalog.PartsHash(parts.Select(p => File.ReadAllBytes(Path.Combine(root, id, p)))));
        }
    }
}

public sealed class EspMirrorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "fd-esp-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, byte[]> net = [];
    private readonly List<string> fetched = [];
    private const string Url = "https://fw.example/firmware/";
    private string Bundled => Path.Combine(root, "bundled");
    private string Live => Path.Combine(root, "live");

    public EspMirrorTests()
    {
        Directory.CreateDirectory(Bundled);
        WriteFw(Bundled, "a", "A-data", "2026-10-30T10:00:00", out _);
    }

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    private EspFirmwareMirror Mirror() => new(Bundled, Live, (url, limit, _) =>
    {
        fetched.Add(url[Url.Length..]);
        return net.TryGetValue(url, out var b) ? Task.FromResult(b) : throw new HttpRequestException("404 " + url);
    });

    private static string Manifest(string id) => $$"""{"name":"{{id}}","builds":[{"chipFamily":"ESP32-C3","parts":[{"path":"{{id}}.bin","offset":0}]}]}""";
    private static string Sha(string data) => EspCatalog.PartsHash([Encoding.UTF8.GetBytes(data)]);

    private static void WriteFw(string dir, string id, string data, string generated, out string catalog)
    {
        Directory.CreateDirectory(Path.Combine(dir, id));
        File.WriteAllText(Path.Combine(dir, id, "manifest.json"), Manifest(id));
        File.WriteAllText(Path.Combine(dir, id, id + ".bin"), data);
        catalog = $$"""{"generated":"{{generated}}","firmwares":[{"id":"{{id}}","manifest":"{{id}}/manifest.json","sha256":"{{Sha(data)}}"}]}""";
        File.WriteAllText(Path.Combine(dir, "catalog.json"), catalog);
    }

    private void Publish(string generated, params (string Id, string Data, string? Sha)[] fws)
    {
        var list = new JsonArray();
        foreach (var (id, data, sha) in fws)
        {
            list.Add(new JsonObject { ["id"] = id, ["manifest"] = $"{id}/manifest.json", ["sha256"] = sha ?? Sha(data) });
            net[$"{Url}{id}/manifest.json"] = Encoding.UTF8.GetBytes(Manifest(id));
            net[$"{Url}{id}/{id}.bin"] = Encoding.UTF8.GetBytes(data);
        }
        net[Url + "catalog.json"] = Encoding.UTF8.GetBytes(new JsonObject { ["generated"] = generated, ["firmwares"] = list }.ToJsonString());
    }

    [Fact]
    public async Task MirrorsNewFirmwareAndReusesUnchanged()
    {
        var m = Mirror();
        m.Start(Url);
        Assert.Equal("app", m.Status.Source);
        Publish("2026-11-01T00:00:00", ("a", "A-data", null), ("b", "B-data", null));
        Assert.True(await m.Check(default));
        Assert.Equal("github", m.Status.Source);
        Assert.Equal(2, m.Status.Count);
        Assert.DoesNotContain("a/a.bin", fetched);   // uændret: kopieret fra den medfølgende
        Assert.Contains("b/b.bin", fetched);
        Assert.Equal("B-data", File.ReadAllText(m.Resolve("b/b.bin")!));
        Assert.Equal("A-data", File.ReadAllText(m.Resolve("a/a.bin")!));
        fetched.Clear();
        Assert.False(await m.Check(default));        // intet nyt
        Assert.Equal(["catalog.json"], fetched);

        // efter en genstart bruges det nyeste spejl, ikke den medfølgende kopi
        var again = Mirror();
        again.Start(Url);
        Assert.Equal("github", again.Status.Source);
        Assert.Equal("2026-11-01T00:00:00", again.Status.Generated);
    }

    [Fact]
    public async Task WrongChecksumKeepsWhatWeHave()
    {
        var m = Mirror();
        m.Start(Url);
        Publish("2026-11-01T00:00:00", ("b", "B-data", "000000000000"));
        Assert.False(await m.Check(default));
        Assert.Contains("kontrolsummen", m.Status.Error);
        Assert.Equal("app", m.Status.Source);
        Assert.Equal(Bundled, m.Current);
        Assert.Empty(Directory.GetDirectories(Live));   // det halve spejl er ryddet op
    }

    [Fact]
    public async Task NoNetworkOrNoUrlServesBundledCopy()
    {
        var m = Mirror();
        m.Start(Url);
        Assert.False(await m.Check(default));     // kataloget findes ikke på "nettet"
        Assert.NotEmpty(m.Status.Error);
        Assert.NotNull(m.Resolve("catalog.json"));
        var off = Mirror();
        off.Start("");
        Assert.False(off.Status.Live);
        Assert.False(await off.Check(default));
        Assert.Empty(fetched.Skip(1));
    }

    [Theory]
    [InlineData("../catalog.json")]
    [InlineData("a/../../x")]
    [InlineData("a/b/c")]
    [InlineData("a/.hidden")]
    [InlineData("")]
    [InlineData("a/missing.bin")]
    public void ResolveRejectsUnsafeOrMissing(string path)
    {
        var m = Mirror();
        m.Start("");
        Assert.Null(m.Resolve(path));
    }

    [Fact]
    public void ResolveFindsFiles()
    {
        var m = Mirror();
        m.Start("");
        Assert.Equal(Path.Combine(Bundled, "a", "manifest.json"), m.Resolve("a/manifest.json"));
    }
}

public class EspWifiTests
{
    [Theory]
    [InlineData("Hjemmenet", "hemmelig-kode", true)]
    [InlineData("Åbent net", "", true)]
    [InlineData("Åbent net", null, true)]
    [InlineData("", "hemmelig-kode", false)]
    [InlineData("  ", "hemmelig-kode", false)]
    [InlineData("Hjemmenet", "kort", false)]
    [InlineData("ÆØÅæøåÆØÅæøåÆØÅæø", "hemmelig-kode", false)]   // 36 byte i UTF-8
    [InlineData("Net\nlinje", "hemmelig-kode", false)]
    public void ValidatesLikeImprov(string ssid, string? pass, bool ok) => Assert.Equal(ok, EspWifi.Validate(ssid, pass) is null);

    [Fact]
    public void RejectsTooLongPassword() => Assert.NotNull(EspWifi.Validate("Net", new string('x', 64)));
}

public sealed class EspTlsTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "fd-esptls-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    private TlsInfo Own()
    {
        TlsInfo.CreateSelfSigned(dir, ["localhost", "192.0.2.10"]);
        using var pem = X509Certificate2.CreateFromPemFile(Path.Combine(dir, TlsInfo.CertFile), Path.Combine(dir, TlsInfo.KeyFile));
        var cert = X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
        return new TlsInfo { HttpsPort = 8586, Certificate = cert, Pin = TlsPinning.Of(cert), SelfSigned = true, OwnDir = dir };
    }

    [Fact]
    public void CoversHostsLikeBrowsers()
    {
        string[] names = ["localhost", "192.0.2.10", "Fleetdeck.example", "*.lan.example", "2001:db8::1"];
        Assert.True(TlsInfo.Covers(names, "192.0.2.10"));
        Assert.False(TlsInfo.Covers(names, "192.0.2.11"));
        Assert.True(TlsInfo.Covers(names, "fleetdeck.EXAMPLE"));
        Assert.True(TlsInfo.Covers(names, "minpi.lan.example"));
        Assert.False(TlsInfo.Covers(names, "a.b.lan.example"));
        Assert.False(TlsInfo.Covers(names, "lan.example"));
        Assert.True(TlsInfo.Covers(names, "[2001:db8::1]"));
        Assert.False(TlsInfo.Covers(names, "fleetdeck"));
    }

    [Theory]
    [InlineData("192.0.2.10", true)]
    [InlineData("minpi.example", true)]
    [InlineData("ARBEJDS-PC", true)]
    [InlineData("2001:db8::1", true)]
    [InlineData("*.example", false)]
    [InlineData("a b", false)]
    [InlineData("host:8586", false)]
    [InlineData("-bad.example", false)]
    [InlineData("", false)]
    public void ValidatesHostNames(string host, bool ok) => Assert.Equal(ok, TlsInfo.ValidHost(host));

    [Fact]
    public void AddNameKeepsKeySoAgentPinsStillMatch()
    {
        var tls = Own();
        var pin = tls.Pin;
        var keyBefore = File.ReadAllText(Path.Combine(dir, TlsInfo.KeyFile));
        Assert.False(TlsInfo.Covers(TlsInfo.Names(tls.Certificate!), "fleetdeck.example"));
        Assert.Null(tls.AddName("fleetdeck.example"));
        Assert.True(TlsInfo.Covers(TlsInfo.Names(tls.Certificate!), "fleetdeck.example"));
        Assert.True(TlsInfo.Covers(TlsInfo.Names(tls.Certificate!), "192.0.2.10"));   // de gamle navne er der stadig
        Assert.Equal(pin, TlsPinning.Of(tls.Certificate!));
        Assert.True(tls.Certificate!.HasPrivateKey);
        Assert.Equal(keyBefore, File.ReadAllText(Path.Combine(dir, TlsInfo.KeyFile)));
        // filen på disken er den nye, så navnet også er der efter en genstart
        using var disk = X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(dir, TlsInfo.CertFile)));
        Assert.Contains("fleetdeck.example", TlsInfo.Names(disk));
        Assert.Equal(pin, TlsPinning.Of(disk));
    }

    [Fact]
    public void AddNameRefusesForeignCertificateAndBadNames()
    {
        var own = Own();
        Assert.NotNull(own.AddName("a b"));
        var foreign = new TlsInfo { HttpsPort = 8586, Certificate = own.Certificate, Pin = own.Pin };   // HTTPS_CERT: ikke vores
        Assert.NotNull(foreign.AddName("fleetdeck.example"));
        Assert.Null(own.AddName("192.0.2.10"));   // dækket i forvejen: intet at gøre
    }
}
