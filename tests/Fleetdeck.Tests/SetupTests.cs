using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using System.Xml.Linq;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Endpoints;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;
using Xunit;

namespace Fleetdeck.Tests;

/// <summary>Opsætningsguiden ved første start: kun åben uden brugere, alt-eller-intet, og kun én administrator ved samtidige forsøg.</summary>
public sealed class SetupWizardTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("fd-setup-test-").FullName;
    private string DbPath => Path.Combine(_dir, "fleetdeck.db");
    private AppDb Db() => new(new DbContextOptionsBuilder<AppDb>().UseSqlite($"Data Source={DbPath}").Options);

    public SetupWizardTests() { using var db = Db(); db.Database.Migrate(); }
    public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static SetupInput Input(string user = "anna", string? notify = null) => new(
        user, "korrekt-hest", "Familien Berg", "192.168.1.10:8585/", "sv-SE", "se", "Europe/Stockholm",
        notify, notify == "mqtt" ? "192.168.1.20" : null, notify == "mqtt" ? 1884 : null, "ha", "hemmelig");

    private static async Task<string?> Setting(AppDb db, string key) => (await db.Settings.FindAsync(key))?.Value;

    [Fact]
    public async Task CreatesAdminAndSettings_WhenThereAreNoUsers()
    {
        using var db = Db();
        var setup = new SetupService(db);
        Assert.True(await setup.IsOpen());
        var r = await setup.Complete(Input(notify: "mqtt"));
        Assert.Equal(SetupStatus.Done, r.Status);
        var u = await db.Users.SingleAsync();
        Assert.Equal(("anna", Roles.Admin), (u.Username, u.Role));
        Assert.True(Crypto.VerifyPassword("korrekt-hest", u.PasswordHash));
        Assert.Equal("http://192.168.1.10:8585", await Setting(db, "server_url"));
        Assert.Equal("sv-SE", await Setting(db, InstallLocale.KLanguage));
        Assert.Equal("se", await Setting(db, InstallLocale.KKeyboard));
        Assert.Equal("Europe/Stockholm", await Setting(db, InstallLocale.KTimeZone));
        Assert.Equal("Familien Berg", await Setting(db, IpadSupervision.KOrg));
        Assert.Equal(("192.168.1.20", "1884", "ha", "hemmelig"),
            (await Setting(db, "mqtt_host"), await Setting(db, "mqtt_port"), await Setting(db, "mqtt_user"), await Setting(db, "mqtt_pass")));
        Assert.Contains(db.Audit, a => a.Action == "setup" && a.User == "anna");
        Assert.False(await setup.IsOpen());
    }

    [Fact]
    public async Task IsClosedAndChangesNothing_OnceAUserExists()
    {
        using (var db = Db()) Assert.Equal(SetupStatus.Done, (await new SetupService(db).Complete(Input())).Status);
        using (var db = Db())
        {
            var again = await new SetupService(db).Complete(Input("mallory") with { ServerUrl = "http://evil:1", Language = "en-US" });
            Assert.Equal(SetupStatus.Closed, again.Status);
            Assert.Null(again.User);
        }
        using (var db = Db())
        {
            Assert.Equal("anna", (await db.Users.SingleAsync()).Username);
            Assert.Equal("http://192.168.1.10:8585", await Setting(db, "server_url"));
            Assert.Equal("sv-SE", await Setting(db, InstallLocale.KLanguage));
            Assert.Null(await Setting(db, "mqtt_host"));
        }
    }

    [Fact]
    public async Task ClosedEvenForAnInvalidRequest_WhenAUserExists()
    {
        using var db = Db();
        db.Users.Add(new User { Username = "gammel", PasswordHash = "x", Role = Roles.Admin });
        await db.SaveChangesAsync();
        Assert.Equal(SetupStatus.Closed, (await new SetupService(db).Complete(new SetupInput(null, null, null, null, null, null, null))).Status);
    }

    public static TheoryData<SetupInput> Invalid => new()
    {
        Input() with { Username = "a" },
        Input() with { Password = "kort" },
        Input() with { ServerUrl = "" },
        Input() with { ServerUrl = "ftp://server" },
        Input() with { Language = "xx-XX" },
        Input() with { Keyboard = "klingon" },
        Input() with { TimeZone = "Mars/Olympus" },
        Input() with { Notify = "mqtt", MqttHost = " " },
        Input() with { Notify = "sms" },
    };

    [Theory, MemberData(nameof(Invalid))]
    public async Task InvalidInput_WritesNothing(SetupInput i)
    {
        using var db = Db();
        var r = await new SetupService(db).Complete(i);
        Assert.Equal(SetupStatus.Invalid, r.Status);
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
        Assert.Empty(db.Users);
        Assert.Empty(db.Settings);
        Assert.True(await new SetupService(db).IsOpen());
    }

    [Fact]
    public async Task ConcurrentRequests_CreateExactlyOneAdmin()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(n => Task.Run(async () =>
        {
            using var db = Db();
            return await new SetupService(db).Complete(Input($"bruger{n}"));
        })));
        Assert.Single(results, r => r.Status == SetupStatus.Done);
        Assert.Equal(7, results.Count(r => r.Status == SetupStatus.Closed));
        using var check = Db();
        Assert.Equal(1, await check.Users.CountAsync());
    }

    [Theory]
    [InlineData("192.168.1.10:8585", "http://192.168.1.10:8585")]
    [InlineData("https://fleet.example.com/", "https://fleet.example.com")]
    [InlineData("http://[fd00::1]:8585", "http://[fd00::1]:8585")]
    [InlineData("ftp://x", null)]
    [InlineData("http://x/?a=1", null)]
    [InlineData("", null)]
    public void ServerUrlIsNormalized(string url, string? expected) => Assert.Equal(expected, SetupService.NormalizeUrl(url));

    /// <summary>Over HTTP: guiden svarer, mens der ingen brugere er – derefter 404 på både GET og POST.</summary>
    [Fact]
    public async Task Endpoints_Return404_AfterSetup()
    {
        var b = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        b.Logging.ClearProviders();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        b.Services.AddDbContext<AppDb>(o => o.UseSqlite($"Data Source={DbPath}"));
        b.Services.AddScoped<SettingsService>().AddScoped<SetupService>().AddScoped<AuditService>();
        b.Services.AddSingleton<Paths>().AddSingleton<WinSetupStore>().AddSingleton<AgentBinary>();
        b.Services.AddHttpClient().AddSingleton<OsLibrary>().AddScoped<DeployService>();   // Udrulning → sprog m.m. (samme fil)
        b.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
        b.Services.AddAuthorization(o => { o.AddPolicy("user", p => p.RequireAuthenticatedUser()); o.AddPolicy("admin", p => p.RequireRole(Roles.Admin)); });
        b.Services.AddRateLimiter(o => o.AddPolicy("login", _ => RateLimitPartition.GetNoLimiter("x")));
        await using var app = b.Build();
        app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter();
        app.MapSetupEndpoints();
        await app.StartAsync();
        var addr = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        using var http = new HttpClient { BaseAddress = new Uri(addr) };

        var info = await http.GetAsync("/api/setup?lang=sv-SE");
        Assert.Equal(HttpStatusCode.OK, info.StatusCode);
        var body = await info.Content.ReadAsStringAsync();
        Assert.Contains("\"language\":\"sv-SE\"", body);
        Assert.Contains("\"keyboard\":\"se\"", body);
        Assert.Contains(addr.TrimEnd('/'), body);   // adressen foreslås ud fra requesten

        var bad = await http.PostAsJsonAsync("/api/setup", Input() with { Password = "kort" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var ok = await http.PostAsJsonAsync("/api/setup", Input());
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Contains(ok.Headers.GetValues("Set-Cookie"), c => c.StartsWith(".AspNetCore.Cookies", StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/setup")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsJsonAsync("/api/setup", Input("mallory"))).StatusCode);
        await app.StopAsync();
        using var db = Db();
        Assert.Equal("anna", (await db.Users.SingleAsync()).Username);
    }
}

/// <summary>Sprog, tastatur og tidszone til installationer: eksisterende installationer beholder de danske værdier, andre kan vælges.</summary>
public sealed class InstallLocaleTests : IDisposable
{
    private const string Before = "20261118015948_RecoveryKeyEscrowAndAuditMachine";
    private readonly string _dir = Directory.CreateTempSubdirectory("fd-locale-test-").FullName;
    private AppDb Db() => new(new DbContextOptionsBuilder<AppDb>().UseSqlite($"Data Source={Path.Combine(_dir, "fleetdeck.db")}").Options);
    public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static string Repo(string rel) => File.ReadAllText(Path.Combine(MacPackTests.RepoRoot, rel));

    // ---------------------------------------------------------------- migrering
    /// <summary>En bruger i tabellen, som den så ud før migreringen: alle NOT NULL-kolonner uden standard udfyldes neutralt.</summary>
    private static void InsertOldUser(AppDb db)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        var cols = new List<(string Name, string Type)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name, type, \"notnull\", dflt_value, pk FROM pragma_table_info('Users')";
            using var r = cmd.ExecuteReader();
            while (r.Read()) if (r.GetInt32(2) == 1 && r.IsDBNull(3) && r.GetInt32(4) == 0) cols.Add((r.GetString(0), r.GetString(1)));
        }
        string Val(string name, string type) => name switch
        {
            "Username" => "'anna'", "PasswordHash" => "'x'", "Role" => "'admin'",
            _ => type.Contains("INT", StringComparison.OrdinalIgnoreCase) ? "0" : "''",
        };
        using var ins = conn.CreateCommand();
        ins.CommandText = $"INSERT INTO Users ({string.Join(",", cols.Select(c => c.Name))}) VALUES ({string.Join(",", cols.Select(c => Val(c.Name, c.Type)))})";
        ins.ExecuteNonQuery();
    }

    [Fact]
    public async Task Migration_WritesTheOldDanishValues_ForAnExistingInstall()
    {
        using (var db = Db())
        {
            db.GetService<IMigrator>().Migrate(Before);
            // rå SQL: modellen har kolonner fra senere migreringer, som tabellen ikke har endnu
            InsertOldUser(db);
            db.Database.Migrate();
        }
        using (var db = Db())
        {
            var l = await InstallLocale.Load(new SettingsService(db));
            Assert.Equal(("da-DK", "dk", "Europe/Copenhagen"), (l.Language.Id, l.Keyboard.Id, l.TimeZone));
            // præcis det, der før stod fast i koden
            Assert.Equal("  locale: da_DK.UTF-8\n  keyboard:\n    layout: dk\n  timezone: Europe/Copenhagen\n", l.UbuntuYaml());
            Assert.Equal(("da-DK", "0406:00000406", "Romance Standard Time"), (l.WindowsLanguage, l.WindowsInputLocale, l.WindowsTimeZone));
            Assert.Equal(("da", "DK"), l.Apple);
        }
    }

    [Fact]
    public void Migration_LeavesAFreshInstallToTheWizard()
    {
        using var db = Db();
        db.Database.Migrate();
        Assert.Empty(db.Settings.Where(s => s.Key.StartsWith("install_")));
    }

    [Fact]
    public void Migration_HasNoPendingModelChanges()
    {
        using var db = Db();
        Assert.False(db.Database.HasPendingModelChanges());
    }

    // ---------------------------------------------------------------- Windows: PXE (menu.cmd) og USB (Lib.ps1)
    /// <summary>Gør det samme som provision/pxe/menu.cmd: defaults.cmd, standardværdier og LINE-erstatningerne – læst fra selve filen.</summary>
    private static string RenderLikeMenuCmd(string? defaultsCmd)
    {
        var menu = Repo("provision/pxe/menu.cmd");
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PCNAME"] = "STUE-PC", ["WUSER"] = "Anna", ["D_MODE"] = "profile", ["D_PROFILE"] = "3", ["D_DEPLOY"] = "7",
        };
        foreach (Match m in Regex.Matches(defaultsCmd ?? "", "set \"([A-Z_]+)=([^\"]*)\"")) vars[m.Groups[1].Value] = m.Groups[2].Value;
        foreach (Match m in Regex.Matches(menu, "if not defined ([A-Z_]+) set \"([A-Z_]+)=([^\"]*)\""))
            vars.TryAdd(m.Groups[2].Value, m.Groups[3].Value);
        var xml = Repo("provision/usb/autounattend-pxe.xml");
        var subs = Regex.Matches(menu, @"set ""LINE=!LINE:(__[A-Z]+__)=(?:%([A-Z_]+)%)?!""");
        Assert.NotEmpty(subs);
        foreach (Match m in subs)
            xml = xml.Replace(m.Groups[1].Value, m.Groups[2].Success ? vars[m.Groups[2].Value] : "");
        Assert.DoesNotMatch("__[A-Z]+__", xml);   // menu.cmd stopper, hvis en pladsholder er tilbage
        return xml;
    }

    private static XNamespace U => "urn:schemas-microsoft-com:unattend";
    private static string[] Values(XDocument d, string element) => d.Descendants(U + element).Select(e => e.Value).ToArray();

    private static void AssertUnattend(string xml, string lang, string input, string tz)
    {
        var d = XDocument.Parse(xml);
        Assert.All(Values(d, "UILanguage"), v => Assert.Equal(lang, v));
        Assert.All(Values(d, "SystemLocale").Concat(Values(d, "UserLocale")), v => Assert.Equal(lang, v));
        Assert.All(Values(d, "InputLocale"), v => Assert.Equal(input, v));
        Assert.Equal([tz], Values(d, "TimeZone"));
        Assert.Equal(3, Values(d, "UILanguage").Length);
    }

    [Theory]
    [InlineData("en-US", "us", "America/New_York", "0409:00000409", "Eastern Standard Time")]
    [InlineData("sv-SE", "se", "Europe/Stockholm", "041d:0000041d", "W. Europe Standard Time")]
    [InlineData("da-DK", "dk", "Europe/Copenhagen", "0406:00000406", "Romance Standard Time")]
    public void PxeUnattend_RendersTheChosenLocale(string lang, string kb, string tz, string input, string winTz)
    {
        var l = InstallLocale.From(lang, kb, tz);
        AssertUnattend(RenderLikeMenuCmd(l.WinPeCmd()), lang, input, winTz);
    }

    [Fact]
    public void PxeUnattend_WithoutDefaultsFromFleetdeck_IsDanishAsBefore() =>
        AssertUnattend(RenderLikeMenuCmd(null), "da-DK", "0406:00000406", "Romance Standard Time");

    [Theory]
    [InlineData("en-GB", "gb", "Europe/London", "0809:00000809", "GMT Standard Time")]
    [InlineData("sv-SE", "se", "Europe/Stockholm", "041d:0000041d", "W. Europe Standard Time")]
    public void UsbUnattend_RendersTheChosenLocale(string lang, string kb, string tz, string input, string winTz)
    {
        var l = InstallLocale.From(lang, kb, tz);
        var xml = l.RenderUnattend(Repo("provision/usb/autounattend.example.xml")).Replace("__PCNAME__", "STUE-PC").Replace("__USER__", "Anna");
        Assert.DoesNotMatch("__[A-Z]+__", xml);
        AssertUnattend(xml, lang, input, winTz);
        // Lib.ps1 læser locale.json (skrevet af serveren) og bruger de samme navne og pladsholdere
        var json = System.Text.Json.JsonDocument.Parse(l.ShareJson()).RootElement;
        Assert.Equal((lang, input, winTz), (json.GetProperty("language").GetString(), json.GetProperty("inputLocale").GetString(), json.GetProperty("timeZone").GetString()));
        var lib = Repo("provision/windows/Lib.ps1");
        foreach (var s in new[] { "Replace('__LANG__', $loc.language)", "Replace('__INPUTLOCALE__', $loc.inputLocale)", "Replace('__TIMEZONE__', $loc.timeZone)" })
            Assert.Contains(s, lib);
    }

    // ---------------------------------------------------------------- Ubuntu og iPad
    [Fact]
    public async Task UbuntuAutoinstall_UsesTheSettings()
    {
        using var db = Db();
        db.Database.Migrate();
        var s = new SettingsService(db);
        await s.Set(InstallLocale.KLanguage, "en-US"); await s.Set(InstallLocale.KKeyboard, "us-intl"); await s.Set(InstallLocale.KTimeZone, "America/Chicago");
        await s.Set("server_url", "http://192.168.1.5:8585");
        var deploy = new DeployService(db, new Paths(), s, new OsLibrary(new Paths(), new NoHttp(), NullLogger<OsLibrary>.Instance), NullLogger<DeployService>.Instance);
        var y = await deploy.UserData(new Deployment { Mac = "AA-BB-CC-DD-EE-FF", Os = DeployOs.Ubuntu, Series = "24.04", Variant = "server", PcName = "nas", UserName = "anna", FullName = "Anna" });
        Assert.Contains("  locale: en_US.UTF-8\n  keyboard:\n    layout: us\n    variant: intl\n  timezone: America/Chicago\n", y);
        Assert.DoesNotContain("da_DK", y);
    }

    [Fact]
    public void IpadSupervision_UsesTheLanguageAndRegion()
    {
        var pad = new Ipad { Id = 4, Name = "Stue" };
        var p = IosJobs.Params(IosActions.Supervise, "0x1", pad, null, null, "n", InstallLocale.From("sv-SE", null, "Europe/Stockholm"));
        Assert.Equal(("sv", "SE"), (p.Language, p.Region));
    }

    [Fact]
    public void UnknownOrEmptyValues_FallBackSensibly()
    {
        var l = InstallLocale.From("", null, "Ikke/En/Zone");
        Assert.Equal("en-US", l.Language.Id);
        Assert.Equal("us", l.Keyboard.Id);                     // sprogets eget tastatur
        Assert.True(InstallLocale.ValidTimeZone(l.TimeZone));  // serverens zone eller UTC
        Assert.Equal("sv-SE", InstallLocale.GuessLanguage("sv").Id);
        Assert.Equal("nb-NO", InstallLocale.GuessLanguage("nb-NO").Id);
        Assert.Equal("en-US", InstallLocale.GuessLanguage("ja-JP").Id);
    }

    [Fact]
    public void EveryLanguageHasAKnownKeyboard_AndEveryKeyboardAWindowsLayout()
    {
        Assert.All(InstallLocale.Languages, l => Assert.NotNull(InstallLocale.FindKeyboard(l.Keyboard)));
        Assert.All(InstallLocale.Keyboards, k => Assert.Matches("^[0-9a-f]{4}:[0-9a-f]{8}$", k.Windows));
        Assert.All(InstallLocale.Languages, l => Assert.Matches("^[a-z]{2}_[A-Z]{2}\\.UTF-8$", l.Linux));
    }

    private sealed class NoHttp : IHttpClientFactory { public HttpClient CreateClient(string name) => throw new InvalidOperationException(); }
}

/// <summary>deploy/compose/docker-compose.yml: gyldig compose (docker compose config, ellers YAML-parsing) med de forventede standarder.</summary>
public sealed class ComposeFileTests
{
    private static string File_ => Path.Combine(MacPackTests.RepoRoot, "deploy", "compose", "docker-compose.yml");

    private static (int Code, string Out) Run(string exe, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(60_000)) { p.Kill(true); return (-1, "timeout"); }
            return (p.ExitCode, o.Result + e.Result);
        }
        catch (System.ComponentModel.Win32Exception) { return (-1, "findes ikke"); }
    }

    [Fact]
    public void ComposeFile_Validates()
    {
        var text = File.ReadAllText(File_);
        Assert.DoesNotContain("\t", text);
        if (Run("docker", "compose", "version").Code == 0)
        {
            var r = Run("docker", "compose", "-f", File_, "config", "--quiet");
            Assert.True(r.Code == 0, r.Out);
        }
        else if (Run("python3", "-c", "import yaml").Code == 0)
        {
            // Samme struktur som compose kræver: services.fleetdeck med image, restart, porte, volumener og healthcheck
            const string check = """
                import sys, yaml
                d = yaml.safe_load(open(sys.argv[1], encoding='utf-8'))
                s = d['services']['fleetdeck']
                assert s['restart'] == 'unless-stopped'
                assert s['image'].startswith('ghcr.io/')
                assert any(str(p).endswith(':8585') for p in s['ports'])
                assert '/data' in [v.split(':')[1] for v in s['volumes']]
                assert s['healthcheck']['test'][0] == 'CMD' and '--healthcheck' in s['healthcheck']['test']
                assert 'TZ' in s['environment']
                print('ok')
                """;
            var r = Run("python3", "-I", "-c", check, File_);
            Assert.True(r.Code == 0, r.Out);
        }
        // Uden værktøjer: i det mindste de vigtigste linjer
        foreach (var s in new[] { "restart: unless-stopped", "healthcheck:", "- ./data:/data", "TZ:", ":8585\"" })
            Assert.Contains(s, text);
    }
}
