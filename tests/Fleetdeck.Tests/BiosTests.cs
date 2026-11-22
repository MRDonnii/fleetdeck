using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services;
using Fleetdeck.Server.Services.Bios;
using Fleetdeck.Shared;

namespace Fleetdeck.Tests;

internal static class BiosFixtures
{
    public static string Dir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "bios");
    public static string Read(string name) => File.ReadAllText(Path.Combine(Dir, name));
    public static List<LenovoModel> Models() => LenovoCatalog.ParseCatalog(Read("catalogv2-sample.xml"));

    public static LenovoCatalogView View(bool withDetails = true) => new()
    {
        Models = Models(), FetchedAt = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc),
        Packages = withDetails
            ? new(StringComparer.OrdinalIgnoreCase) { ["q9zjy79usa"] = new(LenovoCatalog.ParsePackage(Read("package-sample.xml")), LenovoCatalog.ParseReadme(Read("readme-sample.txt"))) }
            : new(StringComparer.OrdinalIgnoreCase),
    };

    /// <summary>En opdigtet Lenovo-desktop: product_name uden maskintype (som efter et bundkortskifte), modelnavnet i product_version.</summary>
    public static SystemFacts Desktop(string bios = "Q9ZKT75A", string release = "1.117") => new()
    {
        Manufacturer = "LENOVO", BiosVendor = "LENOVO", BiosVersion = bios, BiosRelease = release, BiosDate = "2024-01-26",
        ProductName = "m999q", ProductVersion = "ThinkCentre M999q", IsVirtual = false, Firmware = "UEFI",
    };
}

/// <summary>BIOS-fakta fra DMI (Linux) og WMI (Windows).</summary>
public class BiosFactsTests
{
    [Fact]
    public void Dmi_laeses_gennem_en_falsk_rod()
    {
        var files = new Dictionary<string, string>
        {
            ["bios_vendor"] = "LENOVO\n", ["bios_version"] = "Q9ZKT75A", ["bios_date"] = "01/26/2024", ["bios_release"] = "1.117",
            ["product_name"] = "19X1S0XX00", ["product_version"] = "ThinkCentre M999q", ["product_sku"] = "LENOVO_MT_19X1_BU_Think_FM_ThinkCentre M999q",
            ["board_vendor"] = "LENOVO", ["board_name"] = "To be filled by O.E.M.", ["sys_vendor"] = "LENOVO",
        };
        var f = new SystemFacts();
        BiosFacts.FromDmi(f, n => files.GetValueOrDefault(n, ""));
        Assert.Equal("LENOVO", f.BiosVendor);
        Assert.Equal("Q9ZKT75A", f.BiosVersion);
        Assert.Equal("2024-01-26", f.BiosDate);
        Assert.Equal("1.117", f.BiosRelease);
        Assert.Equal("19X1S0XX00", f.ProductName);
        Assert.Equal("ThinkCentre M999q", f.ProductVersion);
        Assert.Null(f.BoardName);   // pladsholder
        Assert.False(f.IsVirtual);
        Assert.Equal("19X1", BiosMatcher.Mtm(f));
    }

    [Fact]
    public void Dmi_fra_en_mappe_og_manglende_filer()
    {
        var dir = Directory.CreateTempSubdirectory("fd-dmi-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "bios_version"), "1.2.3\n");
            File.WriteAllText(Path.Combine(dir, "sys_vendor"), "QEMU");
            File.WriteAllText(Path.Combine(dir, "product_name"), "Standard PC (Q35 + ICH9, 2009)");
            var f = new SystemFacts();
            BiosFacts.FromDmi(f, BiosFacts.DirReader(dir));
            Assert.Equal("1.2.3", f.BiosVersion);
            Assert.Null(f.BiosVendor);
            Assert.True(f.IsVirtual);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Wmi_255_betyder_ikke_angivet()
    {
        var f = new SystemFacts();
        BiosFacts.FromWmi(f, "LENOVO", "Q9ZKT75A", new DateTime(2024, 1, 26), 255, 255, "19X1S0XX00", "ThinkCentre M999q", "LENOVO_MT_19X1", "LENOVO", "3136", "LENOVO");
        Assert.Null(f.BiosRelease);
        Assert.Equal("2024-01-26", f.BiosDate);
        BiosFacts.FromWmi(f, "Microsoft Corporation", "Hyper-V UEFI Release v4.1", null, 4, 1, "Virtual Machine", null, null, null, null, "Microsoft Corporation");
        Assert.Equal("4.1", f.BiosRelease);
        Assert.True(f.IsVirtual);
    }

    [Theory]
    [InlineData("01/26/2024", "2024-01-26")]
    [InlineData("1/2/2024", "2024-01-02")]
    [InlineData("", null)]
    [InlineData("garbage", null)]
    public void DmiDato(string input, string? expected) => Assert.Equal(expected, BiosFacts.DmiDate(input));
}

/// <summary>Lenovos versionsformater og sammenligning.</summary>
public class BiosVersionTests
{
    [Fact]
    public void Katalogets_formater()
    {
        var a = LenovoBiosVersion.FromCatalog("Q9ZKT79A_1.0.0.121");
        Assert.Equal(("Q9ZKT79A", "q9zk", 0x79, "1.121"), (a.Code, a.Image, a.Level, a.Numeric));
        var hex = LenovoBiosVersion.FromCatalog("Q8AKT2BA_1.0.0.43");
        Assert.Equal(0x2B, hex.Level);
        Assert.Equal("1.43", hex.Numeric);
        var tp = LenovoBiosVersion.FromCatalog("1.60");
        Assert.Null(tp.Code);
        Assert.Equal("1.60", tp.Numeric);
        var odd = LenovoBiosVersion.FromCatalog("Q7XKT63A_1.0.0.3F");   // et hex-tal i WU-versionen: kun niveauet bruges
        Assert.Equal(0x63, odd.Level);
        Assert.Null(odd.Numeric);
        Assert.Null(LenovoBiosVersion.FromCatalog("Q5QKT31A").Numeric);
    }

    [Fact]
    public void Maskinens_formater()
    {
        var d = LenovoBiosVersion.FromMachine("Q9ZKT75A", "1.117");
        Assert.Equal(("Q9ZKT75A", "q9zk", 0x75, "1.117"), (d.Code, d.Image, d.Level, d.Numeric));
        var tp = LenovoBiosVersion.FromMachine("Z9HET77W (1.58 )", "1.58");
        Assert.Equal(("z9he", "1.58"), (tp.Image, tp.Numeric));
        var old = LenovoBiosVersion.FromMachine("Q9ZKT75A (2024-01-26)", null);   // ældre Windows-agent: "VER (dato)"
        Assert.Equal("Q9ZKT75A", old.Code);
    }

    [Theory]
    [InlineData("Q9ZKT75A", "1.117", "Q9ZKT79A_1.0.0.121", VersionOrder.Newer)]
    [InlineData("Q9ZKT79A", "1.121", "Q9ZKT79A_1.0.0.121", VersionOrder.Same)]
    [InlineData("Q9ZKT7AA", "1.122", "Q9ZKT79A_1.0.0.121", VersionOrder.Older)]   // en nedgradering er aldrig "nyere"
    [InlineData("Q8AKT29A", "1.41", "Q8AKT2BA_1.0.0.43", VersionOrder.Newer)]     // hex-niveau
    [InlineData("Q9ZKT75A", "1.125", "Q9ZKT79A_1.0.0.121", VersionOrder.Conflict)] // niveau og WU-version er uenige
    [InlineData("Q1AKT75A", "1.117", "Q9ZKT79A_1.0.0.121", VersionOrder.OtherFamily)]
    [InlineData("Z9HET77W (1.58 )", "1.58", "1.60", VersionOrder.Newer)]
    [InlineData("Z9HET77W (1.60 )", "1.60", "1.60", VersionOrder.Same)]
    [InlineData("Q7XKT61A", null, "Q7XKT63A_1.0.0.3F", VersionOrder.Newer)]
    [InlineData("weird", null, "Q7XKT63A", VersionOrder.Unknown)]
    public void Sammenligning(string machine, string? release, string catalog, VersionOrder expected) =>
        Assert.Equal(expected, BiosVersions.Compare(LenovoBiosVersion.FromMachine(machine, release), LenovoBiosVersion.FromCatalog(catalog)));

    [Theory]
    [InlineData("Q9ZKT7*", "Q9ZKT75A", true)]
    [InlineData("Q9ZKT75*", "q9zkt75a", true)]
    [InlineData("Q9ZKT78*", "Q9ZKT75A", false)]
    [InlineData("Q9ZKT75A", "Q9ZKT75A", true)]
    [InlineData("", "Q9ZKT75A", false)]
    [InlineData("Q9ZKT7*", null, false)]
    public void Level_regler(string pattern, string? code, bool expected) => Assert.Equal(expected, BiosVersions.LevelMatches(pattern, code));
}

/// <summary>Parsere til Lenovos katalog, modelkatalog, pakke-XML og readme (opdigtede eksempler).</summary>
public class LenovoCatalogTests
{
    [Fact]
    public void Catalogv2()
    {
        var models = BiosFixtures.Models();
        Assert.Equal(6, models.Count);
        var m = models[0];
        Assert.Equal("ThinkCentre M999q", m.Name);
        Assert.Equal(["19X1", "19X2"], m.Types);
        Assert.Equal("q9zk", m.Bios!.Image);
        Assert.Equal(new string('a', 64), m.Bios.Sha256);   // små bogstaver
        Assert.Equal(new DateOnly(2026, 4, 10), m.Bios.Date);
        Assert.Equal("q9zjy79usa", m.Bios.Stem);
        Assert.Null(models.Single(x => x.Name == "ThinkStation Q1").Bios);
    }

    [Fact]
    public void Modelkatalog_finder_pakke_xml_efter_filstamme()
    {
        var xml = BiosFixtures.Read("model-catalog-sample.xml");
        var hit = LenovoCatalog.FindPackage(xml, "q9zjy79usa");
        Assert.Equal("https://download.lenovo.com/pccbbs/thinkcentre_bios/q9zjy79usa_2_.xml", hit?.Url);
        Assert.Equal(new string('2', 64), hit?.Sha256);
        Assert.Null(LenovoCatalog.FindPackage(xml, "q9zjy7"));   // et præfiks er ikke nok
    }

    [Fact]
    public void Pakke_xml()
    {
        var p = LenovoCatalog.ParsePackage(BiosFixtures.Read("package-sample.xml"));
        Assert.Equal("Q9ZKT79A", p.Version);
        Assert.Equal(new DateOnly(2026, 4, 9), p.ReleaseDate);
        Assert.Equal(9, p.DependsOn.Count);
        Assert.Equal(["Q9ZKT79*"], p.DetectInstall);
        Assert.False(p.Critical);
        Assert.Equal(("q9zjy79usa.exe", 1024L), (p.Installer!.Name, p.Installer.Size!.Value));
        Assert.Equal("q9zjy79usa.txt", p.Readme!.Name);
        Assert.Equal(new string('4', 64), p.Readme.Sha256);
    }

    [Fact]
    public void Readme_aendringer_og_vigtige_linjer()
    {
        var c = LenovoCatalog.ParseReadme(BiosFixtures.Read("readme-sample.txt"));
        Assert.Equal(["Q9ZKT79A", "Q9ZKT78A", "Q9ZKT77A", "Q9ZKT75A", "Q9ZKT6EA"], c.Select(x => x.Version));
        Assert.Equal("1.0.0.121", c[0].Wu);
        Assert.Null(c[4].Wu);
        Assert.False(c[0].Lines[0].Important);
        Assert.All(c[1].Lines, l => Assert.True(l.Important));                     // UEFI CA / KEK
        Assert.False(c[2].Lines[0].Important);
        Assert.True(c[3].Lines[0].Important);                                       // sikkerhed
        Assert.Equal("Fix PXE boot failure issue when the cable is replugged.", c[4].Lines.Single().Text);
    }

    [Theory]
    [InlineData("https://download.lenovo.com/pccbbs/thinkcentre_bios/q9zjy79usa.exe", "https://download.lenovo.com/pccbbs/thinkcentre_bios/q9zjt79usa.zip")]
    [InlineData("https://download.lenovo.com/pccbbs/mobiles/z9hur25w.exe", null)]
    [InlineData("not a url", null)]
    public void Usb_pakkens_navn(string exe, string? zip) => Assert.Equal(zip, LenovoCatalog.UsbZipUrl(exe));
}

/// <summary>Match mod kataloget og den samlede status.</summary>
public class BiosMatcherTests
{
    private static readonly DateTime Now = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
    private static MachineBiosStatus Eval(SystemFacts f, LenovoCatalogView? v = null, string platform = Platforms.Linux, string? fwupd = null) =>
        BiosMatcher.Evaluate(1, platform, f, v ?? BiosFixtures.View(), fwupd, Now);

    [Fact]
    public void Lenovo_uden_maskintype_matches_paa_billedpraefiks_og_modelnavn()
    {
        var s = Eval(BiosFixtures.Desktop());
        Assert.Equal(BiosStates.Newer, s.State);
        Assert.Equal(BiosMatcher.Exact, s.MatchQuality);
        Assert.Equal(("Q9ZKT75A", "1.117", "Q9ZKT79A", "1.121", "2026-04-10"), (s.Current, s.CurrentNumeric, s.Latest, s.LatestNumeric, s.LatestDate));
        Assert.Equal("q9zk/19X1", s.CatalogKey);
        Assert.Equal("lenovo-catalog", s.Source);
        Assert.False(s.Critical);
        var changes = System.Text.Json.JsonSerializer.Deserialize<List<BiosChange>>(s.ChangeSummaryJson, Json.Web)!;
        Assert.Equal(["Q9ZKT79A", "Q9ZKT78A", "Q9ZKT77A"], changes.Select(c => c.Version));   // kun det nye, nyeste først
    }

    [Fact]
    public void Maskintype_fra_product_name_og_sku()
    {
        var f = BiosFixtures.Desktop();
        f.ProductName = "19X3S0XX00"; f.ProductVersion = null;
        var s = Eval(f);
        Assert.Equal((BiosMatcher.Exact, "q9zk/19X3"), (s.MatchQuality, s.CatalogKey));
        f.ProductName = "INVALID"; f.Sku = "LENOVO_MT_19X2_BU_Think_FM_ThinkCentre M999q";
        Assert.Equal("q9zk/19X2", Eval(f).CatalogKey);
    }

    [Fact]
    public void Kun_billedpraefikset_giver_fallback()
    {
        var f = BiosFixtures.Desktop();
        f.ProductName = "unknown"; f.ProductVersion = null;
        var s = Eval(f);
        Assert.Equal((BiosStates.Newer, BiosMatcher.Fallback), (s.State, s.MatchQuality));
    }

    [Fact]
    public void Modsigelser_er_usikre_og_viser_ingen_opdatering()
    {
        var f = BiosFixtures.Desktop();
        f.ProductName = "18C1S0XX00"; f.ProductVersion = null;   // maskintypen hører til en anden model end BIOS-familien
        var s = Eval(f);
        Assert.Equal((BiosStates.Uncertain, BiosMatcher.Uncertain), (s.State, s.MatchQuality));
        Assert.Null(s.Latest);

        var unknown = BiosFixtures.Desktop("Q1QKT10A");
        unknown.ProductVersion = "ThinkCentre Q0";
        Assert.Equal(BiosStates.Uncertain, Eval(unknown).State);
        Assert.Contains("findes ikke", Eval(unknown).Detail);
    }

    [Fact]
    public void Uenig_niveau_og_version_er_usikkert()
    {
        var s = Eval(BiosFixtures.Desktop("Q9ZKT75A", "1.125"));
        Assert.Equal(BiosStates.Uncertain, s.State);
        Assert.Equal(BiosMatcher.Uncertain, s.MatchQuality);
    }

    [Fact]
    public void Pakkens_level_regler_skal_daekke_den_nuvaerende_version()
    {
        var s = Eval(BiosFixtures.Desktop("Q9ZKT72A", "1.114"));
        Assert.Equal(BiosStates.Uncertain, s.State);
        Assert.Contains("Q9ZKT72A", s.Detail);
        // uden pakke-XML kender vi ikke reglerne: så vises den nye version
        Assert.Equal(BiosStates.Newer, Eval(BiosFixtures.Desktop("Q9ZKT72A", "1.114"), BiosFixtures.View(withDetails: false)).State);
    }

    [Fact]
    public void Nyeste_og_nedgradering()
    {
        Assert.Equal(BiosStates.Newest, Eval(BiosFixtures.Desktop("Q9ZKT79A", "1.121")).State);
        Assert.Equal(BiosStates.Newest, Eval(BiosFixtures.Desktop("Q9ZKT7AA", "1.122")).State);
    }

    [Fact]
    public void ThinkPad_med_prikket_version()
    {
        var f = new SystemFacts { Manufacturer = "LENOVO", BiosVendor = "LENOVO", BiosVersion = "Z9HET77W (1.58 )", BiosRelease = "1.58", ProductName = "29ZZ0001XX", ProductVersion = "ThinkPad Z99 Gen 1" };
        var s = Eval(f);
        Assert.Equal((BiosStates.Newer, BiosMatcher.Exact, "1.60"), (s.State, s.MatchQuality, s.Latest));
    }

    [Fact]
    public void Aeldre_agent_med_kun_bios_feltet()
    {
        var f = new SystemFacts { Manufacturer = "LENOVO", Model = "19X1S0XX00", Bios = "Q9ZKT75A (2024-01-26)" };
        var s = Eval(f, platform: Platforms.Windows);
        Assert.Equal((BiosStates.Newer, BiosMatcher.Exact), (s.State, s.MatchQuality));
    }

    [Fact]
    public void Maskiner_uden_bios_funktion()
    {
        Assert.Equal(BiosStates.Mac, Eval(new SystemFacts { Manufacturer = "Apple", Model = "Mac mini" }, platform: Platforms.Mac).State);
        Assert.Equal(BiosStates.Pi, Eval(new SystemFacts { Manufacturer = "Raspberry Pi Ltd", PiSerial = "abcd1234" }).State);
        Assert.Equal(BiosStates.Virtual, Eval(new SystemFacts { Manufacturer = "QEMU", Model = "Standard PC (Q35 + ICH9, 2009)", BiosVersion = "1.16.3" }).State);
        Assert.Equal(BiosStates.Unchecked, Eval(new SystemFacts { Manufacturer = "LENOVO" }).State);
    }

    [Fact]
    public void Andre_leverandoerer_er_ikke_daekket_endnu_men_fwupd_vises()
    {
        var dell = new SystemFacts { Manufacturer = "Dell Inc.", BiosVendor = "Dell Inc.", BiosVersion = "1.34.0", Model = "OptiPlex 0000" };
        var s = Eval(dell);
        Assert.Equal((BiosStates.Unsupported, BiosMatcher.Dell), (s.State, s.Vendor));
        Assert.Contains("Dell", s.Detail);
        var withFw = Eval(dell, fwupd: "1.36.1");
        Assert.Equal((BiosStates.Fwupd, "1.36.1", "fwupd"), (withFw.State, withFw.Latest, withFw.Source));
        var other = Eval(new SystemFacts { Manufacturer = "Supermicro", BiosVendor = "American Megatrends Inc.", BiosVersion = "3.4" });
        Assert.Equal((BiosStates.Unsupported, BiosMatcher.None), (other.State, other.Vendor));
    }

    [Fact]
    public void Katalog_ikke_hentet_eller_nede()
    {
        Assert.Equal(BiosStates.Unchecked, Eval(BiosFixtures.Desktop(), new LenovoCatalogView()).State);
        var down = Eval(BiosFixtures.Desktop(), new LenovoCatalogView { Error = "HTTP 503" });
        Assert.Equal(BiosStates.CatalogError, down.State);
        Assert.Contains("HTTP 503", down.Detail);
        // sidste gode svar bruges stadig, selv om den seneste hentning fejlede
        var stale = BiosFixtures.View();
        Assert.Equal(BiosStates.Newer, Eval(BiosFixtures.Desktop(), new LenovoCatalogView { Models = stale.Models, Packages = stale.Packages, FetchedAt = stale.FetchedAt, Error = "HTTP 503" }).State);
    }

    [Fact]
    public void Flere_forskellige_bios_til_samme_familie_er_usikkert()
    {
        var models = BiosFixtures.Models();
        var first = models[0];
        models.Add(first with { Name = "ThinkCentre M999x", Types = ["19X9"], Bios = first.Bios! with { Version = "Q9ZKT80A_1.0.0.128" } });
        var r = BiosMatcher.MatchLenovo(BiosFixtures.Desktop(), LenovoBiosVersion.FromMachine("Q9ZKT75A", "1.117"), models);
        Assert.Equal(BiosMatcher.Uncertain, r.Quality);
    }
}

/// <summary>Skemaet for katalogtjenesten.</summary>
public class BiosScheduleTests
{
    [Fact]
    public void Daglig_med_tilfaeldigt_forskud_og_backoff_ved_fejl()
    {
        var rnd = new Random(1);
        for (var i = 0; i < 50; i++)
        {
            var d = BiosCatalogService.NextDelay(true, 0, rnd);
            Assert.InRange(d, TimeSpan.FromHours(24), TimeSpan.FromHours(25));
        }
        Assert.Equal(TimeSpan.FromHours(1), BiosCatalogService.NextDelay(false, 1, rnd));
        Assert.Equal(TimeSpan.FromHours(6), BiosCatalogService.NextDelay(false, 2, rnd));
        Assert.Equal(TimeSpan.FromHours(6), BiosCatalogService.NextDelay(false, 7, rnd));
    }
}

/// <summary>Falske leverandørværter: svar pr. adresse og en log over, hvad der blev spurgt om.</summary>
internal sealed class FakeVendor : HttpMessageHandler
{
    public readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> Routes = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> Requested = [];

    public void Bytes(string url, byte[] data, string? etag = null) => Routes[url] = req =>
    {
        if (etag is not null && req.Headers.IfNoneMatch.Any(t => t.Tag == etag)) return new HttpResponseMessage(HttpStatusCode.NotModified);
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
        if (etag is not null) r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
        return r;
    };
    public void Text(string url, string text, string? etag = null) => Bytes(url, Encoding.UTF8.GetBytes(text), etag);
    public void Status(string url, HttpStatusCode code) => Routes[url] = _ => new HttpResponseMessage(code);
    public void Redirect(string url, string to) => Routes[url] = _ => { var r = new HttpResponseMessage(HttpStatusCode.Found); r.Headers.Location = new Uri(to); return r; };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.ToString();
        Requested.Add(request.Method + " " + url);
        return Task.FromResult(Routes.TryGetValue(url, out var f) ? f(request) : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

/// <summary>En strøm, der står stille, til <c>release</c> bliver sat (som en forbindelse, der dør uden at blive lukket).</summary>
internal sealed class GatedStream(TaskCompletionSource release, byte[] data) : Stream
{
    private int pos;
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        await release.Task.WaitAsync(ct);
        var n = Math.Min(buffer.Length, data.Length - pos);
        data.AsMemory(pos, n).CopyTo(buffer);
        pos += n;
        return n;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, default).GetAwaiter().GetResult();
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => pos; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal sealed class FakeHttpFactory(HttpMessageHandler h) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(h, disposeHandler: false);
}

/// <summary>HTTP til leverandørerne: kun HTTPS, kun kendte værter, omdirigeringer kun inden for listen.</summary>
public class BiosHttpTests
{
    [Theory]
    [InlineData("https://download.lenovo.com/x.xml", true)]
    [InlineData("http://download.lenovo.com/x.xml", false)]
    [InlineData("https://download.lenovo.com:8443/x.xml", false)]
    [InlineData("https://evil.example/x.xml", false)]
    [InlineData("https://user:pw@download.lenovo.com/x.xml", false)]
    [InlineData("https://ftp.hp.com/pub/pcbios/x.xml", true)]
    public void Tilladte_vaerter(string url, bool ok) => Assert.Equal(ok, BiosHttp.Allowed(new Uri(url)));

    [Fact]
    public async Task Omdirigering_uden_for_listen_afvises()
    {
        var v = new FakeVendor();
        v.Redirect("https://download.lenovo.com/a.xml", "https://download.lenovo.com/b.xml");
        v.Text("https://download.lenovo.com/b.xml", "ok");
        v.Redirect("https://download.lenovo.com/c.xml", "https://mirror.example/c.xml");
        using var c = new HttpClient(v, false);
        Assert.Equal("ok", Encoding.UTF8.GetString(await BiosHttp.Bytes(c, "https://download.lenovo.com/a.xml", 100, default)));
        var e = await Assert.ThrowsAsync<BiosDownloadException>(() => BiosHttp.Bytes(c, "https://download.lenovo.com/c.xml", 100, default));
        Assert.Contains("mirror.example", e.Message);
        Assert.DoesNotContain(v.Requested, r => r.Contains("mirror.example"));
        await Assert.ThrowsAsync<BiosDownloadException>(() => BiosHttp.Bytes(c, "https://download.lenovo.com/b.xml", 1, default));   // for stor
    }
}

/// <summary>Katalogtjenesten og pakkecachen med en database i hukommelsen og falske leverandørværter.</summary>
public sealed class BiosServiceTests : IDisposable
{
    // delt database i hukommelsen: hver DbContext får sin egen forbindelse, ellers deler samtidige anmodninger og baggrundshentningen
    // én SqliteConnection, som ikke tåler samtidig brug (gav en hentning, der aldrig startede); conn holder databasen i live
    private readonly SqliteConnection conn = new($"Data Source=file:fd-bios-{Guid.NewGuid():N}?mode=memory&cache=shared");
    private readonly ServiceProvider sp;
    private readonly string data = Directory.CreateTempSubdirectory("fd-bios-").FullName;
    private readonly FakeVendor vendor = new();
    private readonly BiosCatalog catalog;
    private readonly BiosPackages packages;
    private const string ExeUrl = "https://download.lenovo.com/pccbbs/thinkcentre_bios/q9zjy79usa.exe";
    private const string ZipUrl = "https://download.lenovo.com/pccbbs/thinkcentre_bios/q9zjt79usa.zip";
    private const string PkgUrl = "https://download.lenovo.com/pccbbs/thinkcentre_bios/q9zjy79usa_2_.xml";
    private const string ReadmeUrl = "https://download.lenovo.com/pccbbs/thinkcentre_bios/q9zjy79usa.txt";

    public BiosServiceTests()
    {
        conn.Open();
        var services = new ServiceCollection();
        services.AddDbContext<AppDb>(o => o.UseSqlite(conn.ConnectionString));
        sp = services.BuildServiceProvider();
        using (var scope = sp.CreateScope()) scope.ServiceProvider.GetRequiredService<AppDb>().Database.EnsureCreated();
        var paths = new Paths(data, data);
        var http = new FakeHttpFactory(vendor);
        catalog = new BiosCatalog(paths, http, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<BiosCatalog>.Instance);
        packages = new BiosPackages(paths, http, sp.GetRequiredService<IServiceScopeFactory>(), catalog, NullLogger<BiosPackages>.Instance);
    }

    public void Dispose()
    {
        sp.Dispose(); conn.Dispose();
        try { Directory.Delete(data, true); } catch (IOException) { }
    }

    private AppDb Db() => sp.CreateScope().ServiceProvider.GetRequiredService<AppDb>();

    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    /// <summary>Kataloget med 12 modeller (så det ikke afvises som "for få"), pakke-XML og readme med rigtige kontrolsummer.</summary>
    private (byte[] Exe, string ExeSha) Vendor(byte[]? exe = null)
    {
        exe ??= [(byte)'M', (byte)'Z', 1, 2, 3, 4, 5, 6];
        var exeSha = Sha(exe);
        var cat = BiosFixtures.Read("catalogv2-sample.xml").Replace(new string('A', 64), exeSha.ToUpperInvariant());
        var extra = string.Concat(Enumerable.Range(0, 8).Select(i =>
            $"<Model name=\"Filler {i}\"><Types><Type>9F{i:D2}</Type></Types><BIOS version=\"F{i}ZKT10A\" image=\"f{i}zk\" date=\"2020-01-01\">https://download.lenovo.com/pccbbs/thinkcentre_bios/f{i}zjy10usa.exe</BIOS></Model>"));
        cat = cat.Replace("</ModelList>", extra + "</ModelList>");
        vendor.Text(LenovoCatalog.CatalogUrl, cat, etag: "\"v1\"");
        var readme = Encoding.UTF8.GetBytes(BiosFixtures.Read("readme-sample.txt"));
        var pkg = BiosFixtures.Read("package-sample.xml").Replace(new string('4', 64), Sha(readme).ToUpperInvariant())
            .Replace(new string('A', 64), exeSha.ToUpperInvariant()).Replace("<Size>1024</Size>", $"<Size>{exe.Length}</Size>");
        var pkgBytes = Encoding.UTF8.GetBytes(pkg);
        vendor.Text(LenovoCatalog.ModelCatalogUrl("19X1"), BiosFixtures.Read("model-catalog-sample.xml").Replace(new string('2', 64), Sha(pkgBytes)));
        vendor.Bytes(PkgUrl, pkgBytes);
        vendor.Bytes(ReadmeUrl, readme);
        vendor.Bytes(ExeUrl, exe);
        return (exe, exeSha);
    }

    private async Task<int> AddMachine(SystemFacts f, string platform = Platforms.Linux)
    {
        using var db = Db();
        var m = new Machine { Name = "pc-" + Guid.NewGuid().ToString("N")[..6], Platform = platform, FactsJson = Json.Ser(f) };
        db.Machines.Add(m);
        await db.SaveChangesAsync();
        return m.Id;
    }

    [Fact]
    public async Task Katalog_hentes_matches_og_gemmes_paa_disken()
    {
        Vendor();
        var id = await AddMachine(BiosFixtures.Desktop());
        var dell = await AddMachine(new SystemFacts { Manufacturer = "Dell Inc.", BiosVendor = "Dell Inc.", BiosVersion = "1.0.0" });
        Assert.True(await catalog.Refresh(default));
        using (var db = Db())
        {
            var s = await db.BiosStatus.FindAsync(id);
            Assert.Equal(BiosStates.Newer, s!.State);
            Assert.Contains("Q9ZKT78A", s.ChangeSummaryJson);   // readme hentet og verificeret
            Assert.Equal(BiosStates.Unsupported, (await db.BiosStatus.FindAsync(dell))!.State);
        }
        Assert.True(File.Exists(Path.Combine(catalog.Dir, "catalogv2.xml")));
        Assert.True(File.Exists(Path.Combine(catalog.Dir, "pkg", "q9zjy79usa.xml")));

        // anden gang: ETag giver 304, og pakke-XML/readme hentes ikke igen
        vendor.Requested.Clear();
        Assert.True(await catalog.Refresh(default));
        Assert.Single(vendor.Requested);

        // en ny serverstart læser det gemte katalog
        var again = new BiosCatalog(new Paths(data, data), new FakeHttpFactory(vendor), sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<BiosCatalog>.Instance);
        Assert.True(again.Lenovo.Loaded);
        Assert.True(again.Lenovo.Packages.ContainsKey("q9zjy79usa"));
    }

    [Fact]
    public async Task Katalog_nede_beholder_sidste_gode_svar()
    {
        Vendor();
        var id = await AddMachine(BiosFixtures.Desktop());
        Assert.True(await catalog.Refresh(default));
        vendor.Status(LenovoCatalog.CatalogUrl, HttpStatusCode.ServiceUnavailable);
        Assert.False(await catalog.Refresh(default));
        Assert.True(catalog.Lenovo.Loaded);
        Assert.Contains("503", catalog.Lenovo.Error);
        using var db = Db();
        Assert.Equal(BiosStates.Newer, (await db.BiosStatus.FindAsync(id))!.State);
        var v = (await catalog.Vendors(db)).Single(x => x.Vendor == "lenovo");
        Assert.NotNull(v.ErrorSince);
        Assert.Contains("Sidste kendte svar", v.Text);
    }

    [Fact]
    public async Task Ingen_lenovo_i_floden_henter_intet()
    {
        Vendor();
        await AddMachine(new SystemFacts { Manufacturer = "HP", BiosVendor = "HP", BiosVersion = "01.02.03" });
        Assert.True(await catalog.Refresh(default));
        Assert.Empty(vendor.Requested);
    }

    [Fact]
    public async Task Fwupd_fra_den_eksisterende_firmware_pipeline()
    {
        var id = await AddMachine(new SystemFacts { Manufacturer = "Dell Inc.", BiosVendor = "Dell Inc.", BiosVersion = "1.34.0" });
        using var db = Db();
        db.Upgrades.Add(new UpgradeItem { MachineId = id, Name = "System Firmware (firmware)", WingetId = "fwupd:abc123", Current = "1.34.0", Available = "1.36.1" });
        await db.SaveChangesAsync();
        await catalog.Rematch(db, [id]);
        var s = await db.BiosStatus.AsNoTracking().FirstAsync(x => x.MachineId == id);
        Assert.Equal((BiosStates.Fwupd, "1.36.1"), (s.State, s.FwupdVersion));
    }

    [Fact]
    public async Task Pakke_hentes_kun_paa_tryk_og_tjekkes_mod_katalogets_sha()
    {
        var (exe, sha) = Vendor();
        var id = await AddMachine(BiosFixtures.Desktop(), Platforms.Windows);
        await catalog.Refresh(default);
        Assert.DoesNotContain(vendor.Requested, r => r.EndsWith(".exe"));   // kataloget henter aldrig selve pakken

        using var db = Db();
        var (p, err) = await packages.Request(db, id, BiosPackages.WinExe, "admin", wait: true);
        Assert.Null(err);
        await db.Entry(p!).ReloadAsync();
        Assert.Equal((BiosPackages.Ready, sha, "catalog", (long)exe.Length), (p!.State, p.Sha256, p.ShaSource, p.Size));
        Assert.Equal(exe, await File.ReadAllBytesAsync(packages.FullPath(p)));
        Assert.Equal("lenovo/q9zk-19X1/Q9ZKT79A/q9zjy79usa.exe".Replace('/', Path.DirectorySeparatorChar), Path.GetRelativePath(Path.Combine(data, "bios"), packages.FullPath(p)));
        Assert.Contains(await db.Audit.Select(a => a.Action).ToListAsync(), a => a == "bios.package.download");
        Assert.Null(await packages.Verify(db, p));

        // filen ændres på disken: den slettes før brug
        await File.WriteAllBytesAsync(packages.FullPath(p), [(byte)'M', (byte)'Z', 9]);
        Assert.Equal(BiosPackages.ChecksumError, await packages.Verify(db, p));
        Assert.False(File.Exists(packages.FullPath(p)));
        Assert.Equal(BiosPackages.Failed, p.State);
    }

    [Fact]
    public async Task Forkert_sha_sletter_filen()
    {
        Vendor();
        var id = await AddMachine(BiosFixtures.Desktop(), Platforms.Windows);
        await catalog.Refresh(default);
        vendor.Bytes(ExeUrl, [(byte)'M', (byte)'Z', 6, 6, 6, 6, 6, 6]);   // samme størrelse, andet indhold
        using var db = Db();
        var (p, _) = await packages.Request(db, id, BiosPackages.WinExe, "admin", wait: true);
        await db.Entry(p!).ReloadAsync();
        Assert.Equal(BiosPackages.Failed, p!.State);
        Assert.Equal(BiosPackages.ChecksumError, p.Error);
        Assert.False(File.Exists(packages.FullPath(p)));
        Assert.False(File.Exists(packages.FullPath(p) + ".partial"));
    }

    [Fact]
    public async Task Usb_pakken_laases_ved_foerste_hentning_og_faar_laes_mig()
    {
        Vendor();
        var id = await AddMachine(BiosFixtures.Desktop());
        await catalog.Refresh(default);
        byte[] zip;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                using (var w = new StreamWriter(z.CreateEntry("efi/boot/BOOTX64.efi").Open())) w.Write("shell");
                using (var w = new StreamWriter(z.CreateEntry("startup.nsh").Open())) w.Write("flash");
            }
            zip = ms.ToArray();
        }
        vendor.Bytes(ZipUrl, zip);
        using var db = Db();
        var (p, err) = await packages.Request(db, id, BiosPackages.UsbZip, "admin", wait: true);
        Assert.Null(err);
        await db.Entry(p!).ReloadAsync();
        Assert.Equal((BiosPackages.Ready, "tofu", Sha(zip)), (p!.State, p.ShaSource, p.Sha256));

        // senere hentninger skal give samme fil
        vendor.Bytes(ZipUrl, [.. zip, 0]);
        p.State = BiosPackages.Failed; await db.SaveChangesAsync();
        await packages.Request(db, id, BiosPackages.UsbZip, "admin", wait: true);
        await db.Entry(p).ReloadAsync();
        Assert.Equal(BiosPackages.ChecksumError, p.Error);

        var readme = BiosPackages.UsbReadme("ThinkCentre M999q", "Q9ZKT79A", "q9zjt79usa.zip", Sha(zip), "tofu");
        Assert.Contains("Secure Boot", readme);
        Assert.Contains(Sha(zip), readme);
        var src = Path.Combine(data, "src.zip");
        await File.WriteAllBytesAsync(src, zip);
        // som Kestrels svar: synkron skrivning er forbudt (før gav det "Synchronous operations are disallowed")
        using var outMs = new AsyncOnlyStream();
        await BiosPackages.WriteUsbZip(src, outMs, readme, default);
        outMs.Position = 0;
        using var result = new ZipArchive(outMs, ZipArchiveMode.Read);
        Assert.Equal(["efi/boot/BOOTX64.efi", "startup.nsh", "LÆS-MIG.txt"], result.Entries.Select(e => e.FullName));
    }

    [Fact]
    public async Task Hentning_der_gaar_i_staa_ender_som_fejl_og_kan_proeves_igen()
    {
        var (exe, _) = Vendor();
        var id = await AddMachine(BiosFixtures.Desktop(), Platforms.Windows);
        await catalog.Refresh(default);
        vendor.Routes[ExeUrl] = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new GatedStream(new TaskCompletionSource(), exe)) };
        var old = BiosHttp.IdleTimeout;
        BiosHttp.IdleTimeout = TimeSpan.FromMilliseconds(300);
        try
        {
            using var db = Db();
            var (p, _) = await packages.Request(db, id, BiosPackages.WinExe, "admin", wait: true).WaitAsync(TimeSpan.FromSeconds(20));
            await db.Entry(p!).ReloadAsync();
            Assert.Equal((BiosPackages.Failed, BiosHttp.Stalled), (p!.State, p.Error));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(packages.FullPath(p))!, "*.partial"));
        }
        finally { BiosHttp.IdleTimeout = old; }

        // næste forsøg starter en ny hentning (rækken hænger ikke i "henter")
        vendor.Bytes(ExeUrl, exe);
        using var db2 = Db();
        var (p2, _) = await packages.Request(db2, id, BiosPackages.WinExe, "admin", wait: true);
        await db2.Entry(p2!).ReloadAsync();
        Assert.Equal(BiosPackages.Ready, p2!.State);
    }

    [Fact]
    public async Task To_samtidige_anmodninger_starter_kun_een_hentning()
    {
        var (exe, _) = Vendor();
        var id = await AddMachine(BiosFixtures.Desktop(), Platforms.Windows);
        await catalog.Refresh(default);
        var release = new TaskCompletionSource();
        vendor.Routes[ExeUrl] = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new GatedStream(release, exe)) };
        using var db1 = Db();
        using var db2 = Db();
        var r = await Task.WhenAll(packages.Request(db1, id, BiosPackages.WinExe, "admin"), packages.Request(db2, id, BiosPackages.WinExe, "admin"));
        Assert.All(r, x => Assert.Null(x.Error));
        // hentningen kører i baggrunden: vent, til den har spurgt leverandøren, og lidt til, så en eventuel anden også ville være nået
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!vendor.Requested.ToList().Any(q => q.EndsWith(".exe")) && DateTime.UtcNow < until) await Task.Delay(20);
        await Task.Delay(300);
        Assert.Single(vendor.Requested.ToList(), q => q.EndsWith(".exe"));
        Assert.True(packages.InProgress(r[0].Package!.Id));   // Indstillinger → BIOS må ikke slette en pakke, der hentes
        release.SetResult();
        using var db = Db();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        BiosPackage? p;
        while ((p = await db.BiosPackages.AsNoTracking().SingleAsync()).State == BiosPackages.Downloading && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.Equal(BiosPackages.Ready, p.State);
        Assert.Equal(exe, await File.ReadAllBytesAsync(packages.FullPath(p)));
        // rækken bliver "ready" lidt før hentningen melder sig færdig
        while (packages.InProgress(p.Id) && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.False(packages.InProgress(p.Id));
    }

    [Fact]
    public async Task Detaljer_der_ikke_kan_hentes_proeves_ikke_igen_med_det_samme()
    {
        Vendor();
        vendor.Status(ReadmeUrl, HttpStatusCode.NotFound);
        var id = await AddMachine(BiosFixtures.Desktop());
        await catalog.Refresh(default);
        // readme'en mangler: pakke-XML'en gemmes alligevel, så detaljerne findes (uden ændringslinjer)
        using (var db = Db())
        {
            var facts = Json.Des<SystemFacts>((await db.Machines.FindAsync(id))!.FactsJson);
            Assert.NotNull(catalog.LenovoFor(facts)!.Value.Details);
        }

        // pakke-XML'en findes slet ikke: stammen holder pause, og et nyt katalogtjek henter ikke modelkatalogerne igen
        var id2 = await AddMachine(BiosFixtures.Desktop());
        vendor.Status(PkgUrl, HttpStatusCode.NotFound);
        var fresh = new BiosCatalog(new Paths(Path.Combine(data, "b"), Path.Combine(data, "b")), new FakeHttpFactory(vendor), sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<BiosCatalog>.Instance);
        await fresh.Refresh(default);
        using var db3 = Db();
        var stem = fresh.LenovoFor(Json.Des<SystemFacts>((await db3.Machines.FindAsync(id2))!.FactsJson))!.Value.Bios.Stem;
        Assert.False(fresh.DetailsDue(stem, DateTime.UtcNow));
        Assert.True(fresh.DetailsDue(stem, DateTime.UtcNow.AddHours(7)));
        var before = vendor.Requested.Count(q => q.Contains(LenovoCatalog.ModelCatalogUrl("19X1")));
        await fresh.Refresh(default);
        Assert.Equal(before, vendor.Requested.Count(q => q.Contains(LenovoCatalog.ModelCatalogUrl("19X1"))));
    }

    [Fact]
    public async Task Samtidige_rematch_indsaetter_ikke_samme_maskine_to_gange()
    {
        Vendor();
        var id = await AddMachine(BiosFixtures.Desktop());
        await catalog.Refresh(default);
        using (var db = Db()) { db.BiosStatus.RemoveRange(db.BiosStatus); await db.SaveChangesAsync(); }
        using var a = Db();
        using var b = Db();
        await Task.WhenAll(catalog.Rematch(a, null), catalog.Rematch(b, [id]));
        using var c = Db();
        Assert.Equal(1, await c.BiosStatus.CountAsync(x => x.MachineId == id));
    }

    [Fact]
    public async Task Usikkert_match_giver_ingen_pakke()
    {
        Vendor();
        var f = BiosFixtures.Desktop();
        f.ProductName = "18C1S0XX00"; f.ProductVersion = null;
        var id = await AddMachine(f);
        await catalog.Refresh(default);
        using var db = Db();
        var (p, err) = await packages.Request(db, id, BiosPackages.UsbZip, "admin");
        Assert.Null(p);
        Assert.Contains("sikker", err);
    }

    [Fact]
    public async Task Oprydning_beholder_de_to_nyeste_og_sletter_ubrugte()
    {
        using var db = Db();
        var now = new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);
        foreach (var (v, at) in new[] { ("Q9ZKT75A", now.AddDays(-30)), ("Q9ZKT77A", now.AddDays(-20)), ("Q9ZKT79A", now.AddDays(-10)) })
            db.BiosPackages.Add(new BiosPackage { Vendor = "lenovo", CatalogKey = "q9zk/19X1", Version = v, Kind = BiosPackages.UsbZip, State = BiosPackages.Ready, DownloadedAt = at, Path = "bios/x/" + v });
        db.BiosPackages.Add(new BiosPackage { Vendor = "lenovo", CatalogKey = "q8ak/18C1", Version = "Q8AKT2BA", Kind = BiosPackages.WinExe, State = BiosPackages.Ready, DownloadedAt = now.AddDays(-100), Path = "bios/y/z" });
        await db.SaveChangesAsync();
        Assert.Equal(2, await packages.Prune(db, now));
        Assert.Equal(["Q9ZKT77A", "Q9ZKT79A"], await db.BiosPackages.OrderBy(p => p.Version).Select(p => p.Version).ToListAsync());
    }

    [Theory]
    [InlineData("../../etc/passwd", "etc-passwd")]
    [InlineData("q9zk/19X1", "q9zk-19X1")]
    [InlineData("LÆS MIG.zip", "L_S_MIG.zip")]
    public void Sikre_stier(string input, string expected) => Assert.Equal(expected, BiosPackages.Safe(input));
}

/// <summary>En stream, der kun tillader asynkron skrivning – som HTTP-svaret i Kestrel.</summary>
internal sealed class AsyncOnlyStream : MemoryStream
{
    public override void Write(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous operations are disallowed.");
    public override void Write(ReadOnlySpan<byte> buffer) => throw new InvalidOperationException("Synchronous operations are disallowed.");
    public override void WriteByte(byte value) => throw new InvalidOperationException("Synchronous operations are disallowed.");
    public override void Flush() => throw new InvalidOperationException("Synchronous operations are disallowed.");
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) { var a = buffer.ToArray(); base.Write(a, 0, a.Length); return ValueTask.CompletedTask; }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) { base.Write(buffer, offset, count); return Task.CompletedTask; }
    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
}
