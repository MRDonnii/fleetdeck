using Fleetdeck.Agent.Windows;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;

namespace Fleetdeck.Tests;

public class WingetParsingTests
{
    private const string Danish = """
        Navn                         Id                         Version     Tilgængelig  Kilde
        ---------------------------------------------------------------------------------------
        7-Zip 26.05 (x64)            7zip.7zip                  26.05       26.06        winget
        CPUID CPU-Z 3.01             CPUID.CPU-Z                3.01        3.02         winget
        Battle.net                   Blizzard.BattleNet         Unknown     2.0.0.4232   winget
        3 opgraderinger er tilgængelige.
        """;

    [Fact]
    public void ParsesUpgradeTableIndependentOfLanguage()
    {
        var rows = Winget.ParseTable(Danish);
        Assert.Equal(3, rows.Count);
        Assert.Equal("7zip.7zip", rows[0].Id);
        Assert.Equal("26.06", rows[0].Available);
        Assert.Equal("Unknown", rows[2].Current);
    }

    [Fact]
    public void EmptyOutputGivesNoRows() => Assert.Empty(Winget.ParseTable("Der blev ikke fundet nogen opdateringer."));

    [Fact]
    public void CleanRemovesProgressBarsAndSpinners()
    {
        var raw = "  -\r  \\\r  |\r██████████▒▒▒▒  2 MB / 5 MB\r\nSuccessfully installed\n";
        Assert.Equal("Successfully installed", Runner.Clean(raw));
    }

    [Fact]
    public void WingetSuccessCodesIncludeAlreadyInstalled()
    {
        Assert.Contains(unchecked((int)0x8A150061), Winget.Ok);
        Assert.Contains(0, Winget.Ok);
        Assert.DoesNotContain(1, Winget.Ok);
    }
}

public class ScheduleTests
{
    [Theory]
    [InlineData("daily", "03:00", 6, "2026-11-11 03:00", true)]
    [InlineData("daily", "03:00", 6, "2026-11-11 03:59", true)]
    [InlineData("daily", "03:00", 6, "2026-11-11 04:01", false)]   // kun inden for en time
    [InlineData("daily", "03:00", 6, "2026-11-11 02:59", false)]
    [InlineData("weekly", "03:00", 2, "2026-11-11 03:10", true)]   // onsdag = 2
    [InlineData("weekly", "03:00", 6, "2026-11-11 03:10", false)]
    [InlineData("off", "03:00", 6, "2026-11-11 03:10", false)]
    public void DueOnlyInsideWindow(string mode, string time, int day, string now, bool expected) =>
        Assert.Equal(expected, Schedule.Due(mode, time, day, DateTime.Parse(now, System.Globalization.CultureInfo.InvariantCulture)));
}

public class ComplianceTests
{
    private static readonly string[] Names = ["Microsoft Visual Studio Code (User)", "7-Zip 26.05 (x64)", "Counter-Strike 2 (Steam 730)", "FileZilla 3.71.1"];
    private static readonly HashSet<string> Ids = ["7zip.7zip"];

    [Fact] public void MatchesWingetId() => Assert.True(ComplianceService.IsInstalled(new Package { Name = "7-Zip", Type = "winget", Identifier = "7zip.7zip" }, Names, Ids));
    [Fact] public void MatchesNormalisedName() => Assert.True(ComplianceService.IsInstalled(new Package { Name = "Visual Studio Code", Type = "winget", Identifier = "x" }, Names, Ids));
    [Fact] public void MatchesSteamAppId() => Assert.True(ComplianceService.IsInstalled(new Package { Name = "CS2", Type = PackageTypes.Steam, Identifier = "730" }, Names, Ids));
    [Fact] public void MissingSteamGame() => Assert.False(ComplianceService.IsInstalled(new Package { Name = "Dota 2", Type = PackageTypes.Steam, Identifier = "570" }, Names, Ids));
    [Fact] public void UsesDetectRegex() => Assert.True(ComplianceService.IsInstalled(new Package { Name = "x", Type = "file", Detect = "^FileZilla" }, Names, Ids));
    private static readonly string[] Real = ["REW 5.40 beta 135", "RealVNC Connect Viewer 8.5.3", "OpenAI.ChatGPT-Desktop", "Microsoft Visual C++ v14 Redistributable (x64) - 14.51.36247"];
    [Fact] public void MatchesBetaVersionWithParenthesisInName() => Assert.True(ComplianceService.IsInstalled(new Package { Name = "REW (Room EQ Wizard)", Type = "winget", Identifier = "JohnMulcahy.REW" }, Real, Ids));
    [Fact] public void MatchesWordsInDifferentOrder() => Assert.True(ComplianceService.IsInstalled(new Package { Name = "RealVNC Viewer", Type = "winget", Identifier = "x" }, Real, Ids));
    [Fact] public void MatchesStoreAppName() => Assert.True(ComplianceService.IsInstalled(new Package { Name = "ChatGPT", Type = "winget", Identifier = "OpenAI.ChatGPT" }, Real, Ids));
    [Fact] public void DoesNotMatchUnrelated() => Assert.False(ComplianceService.IsInstalled(new Package { Name = "DirectX (ældre spil)", Type = "winget", Identifier = "Microsoft.DirectX" }, Real, Ids));
    [Fact] public void NotInstalled() => Assert.False(ComplianceService.IsInstalled(new Package { Name = "Spotify", Type = "winget", Identifier = "Spotify.Spotify" }, Names, Ids));
}

public class ScriptPackageTests
{
    [Fact]
    public void ScriptPackageIsFoundByItsDetectPattern()
    {
        var p = new Fleetdeck.Server.Data.Package { Name = "Kiosk Warden", Type = Fleetdeck.Shared.PackageTypes.Script, Identifier = "kiosk-warden", Detect = "^Kiosk Warden$" };
        Assert.True(Fleetdeck.Server.Services.ComplianceService.IsInstalled(p, ["Kiosk Warden", "curl"], new HashSet<string> { "curl" }));
        Assert.False(Fleetdeck.Server.Services.ComplianceService.IsInstalled(p, ["curl"], new HashSet<string> { "curl", "kiosk" }));
    }
}

public class HelperTests
{
    /// <summary>Alle nøgler, reglerne kan give (prefix:x for nøgler med et navn efter kolon).</summary>
    public static readonly string[] AllFindingKeys =
    [
        "offline", "reservedns", "sparecard", "debloat_drift", "disk:C:", "smart:x", "sectors:x", "wear:x", "dtemp:x", "battery:0", "diskdata", "cputemp", "notemp",
        "defender", "defsig", "threats", "firewall", "services", "crash", "winupdate", "drive:x", "agentupdate", "autostart", "fulldisk", "macsign",
        "encryption", "escrow", "tpm", "net:speed", "net:wifi", "net:errors:eth0", "net:flaps:eth0",
        "syslog:crash", "syslog:whea", "syslog:whea-corrected", "syslog:disk", "syslog:disk-errors", "syslog:power", "syslog:oom",
        "kept:x", "reboot", "compliance", "backup", "important:0123abcd",
    ];

    [Fact]
    public void EveryAdviceFixExistsInCatalog()
    {
        foreach (var p in new string?[] { null, Platforms.Windows, Platforms.Linux, Platforms.Mac })
        foreach (var sev in new[] { "critical", "warning", "info" })
        foreach (var k in AllFindingKeys)
        {
            var a = HelperCatalog.AdviceFor(k, sev, p);
            Assert.True(a is not null, $"{k} ({p}) mangler en forklaring");
            Assert.False(string.IsNullOrWhiteSpace(a!.Why), $"{k} ({p}) mangler Why");
            Assert.InRange(a.Steps.Count, 1, 4);
            Assert.All(a.Steps, s => Assert.False(string.IsNullOrWhiteSpace(s)));
            if (a.FixId is { } id)
            {
                var fix = HelperCatalog.Get(id);
                Assert.NotNull(fix);
                // rettelsen skal kunne køre på platformen, og et af trinene skal pege på knappen med dens egen tekst
                if (p is not null) Assert.True(HelperService.PlatformBlocker(fix!, p) is null, $"{k} ({p}): {id} kan ikke køre på platformen");
                var button = a.Button ?? fix!.Button;
                Assert.Contains(a.Steps, s => s.Contains($"\"{button}\"", StringComparison.Ordinal));
            }
        }
    }

    /// <summary>Vagt: en ny alarmnøgle i reglerne uden forklaring får testen til at fejle (nøglerne læses fra kildekoden).</summary>
    [Fact]
    public void EveryRuleKeyInTheSourceIsCovered()
    {
        var rx = new System.Text.RegularExpressions.Regex(@"new(?: Finding)?\(\$?""([a-z_]+)[:""][^;]*?""(?:critical|warning|info)""", System.Text.RegularExpressions.RegexOptions.Singleline);
        var dir = Path.Combine(MacPackTests.RepoRoot, "src", "Fleetdeck.Server", "Services");
        var found = Directory.GetFiles(dir, "*.cs").SelectMany(f => rx.Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value)).ToHashSet();
        var net = System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(Path.Combine(MacPackTests.RepoRoot, "src", "Fleetdeck.Shared", "Network.cs")), "new\\(\\$?\"(net:[a-z]+)")
            .Select(m => m.Groups[1].Value);
        Assert.Contains("debloat_drift", found);
        Assert.Contains("services", found);
        var known = AllFindingKeys.Select(k => k.StartsWith("net:") ? string.Join(':', k.Split(':').Take(2)) : k.Split(':')[0]).ToHashSet();
        foreach (var k in found.Concat(net)) Assert.True(known.Contains(k), $"Alarmnøglen {k} mangler i AllFindingKeys/HelperCatalog.AdviceFor");
    }

    [Theory]
    [InlineData("windows", "Cumulative Update for Windows 11 (KB5050000), Google Chrome 131", "windows_update")]
    [InlineData("windows", "Google Chrome 131, 7-Zip 24.09", "upgrade_all")]
    [InlineData("linux", "openssl 3.0.15", "system_update")]
    [InlineData("mac", "Safari 18.2", "system_update")]
    public void ImportantUpdatesPickTheRightFix(string platform, string detail, string fix) =>
        Assert.Equal(fix, HelperCatalog.AdviceFor("important:0123abcd", "warning", platform, detail)!.FixId);

    [Fact]
    public void NewFixesAreNeverAutomatic()
    {
        foreach (var id in new[] { "debloat_reapply", "service_start" })
        {
            var f = HelperCatalog.Get(id)!;
            Assert.False(f.AutoAllowed);
            Assert.False(f.Tool);
        }
        Assert.Equal(Risk.Safe, HelperCatalog.Get("debloat_reapply")!.Risk);
        Assert.Equal(Risk.Confirm, HelperCatalog.Get("service_start")!.Risk);
        Assert.NotNull(HelperService.PlatformBlocker(HelperCatalog.Get("debloat_reapply")!, Platforms.Linux));
        Assert.NotNull(HelperService.PlatformBlocker(HelperCatalog.Get("service_start")!, Platforms.Mac));
        Assert.Null(HelperCatalog.AdviceFor("services", "warning", Platforms.Mac)!.FixId);
        Assert.Null(HelperCatalog.AdviceFor("services", "warning", Platforms.Linux, "Unraid-array: STOPPED.")!.FixId);
    }

    [Theory]
    [InlineData("windows", "windows_update")]
    [InlineData("linux", "system_update")]
    [InlineData("mac", "system_update")]
    public void UpdateAdviceFitsThePlatform(string platform, string fix)
    {
        var a = HelperCatalog.AdviceFor("winupdate", "info", platform)!;
        Assert.Equal(fix, a.FixId);
        Assert.NotNull(HelperCatalog.Get(fix));
        if (platform != "windows") Assert.DoesNotContain("Microsoft", a.Why);
    }

    [Fact]
    public void OnlySafeFixesMayRunAutomatically() =>
        Assert.All(HelperCatalog.All.Where(f => f.AutoAllowed), f => Assert.Equal(Risk.Safe, f.Risk));

    [Fact]
    public void ReinstallRequiresTypedConfirmation() => Assert.Equal(Risk.Danger, HelperCatalog.Get("pxe_reinstall")!.Risk);

    [Fact]
    public void ScriptFixesHaveScripts() =>
        Assert.All(HelperCatalog.All.Where(f => f.Kind == "script"), f => Assert.False(string.IsNullOrWhiteSpace(f.Script)));

    [Fact]
    public void ExplainsWingetAndMsiCodes()
    {
        var notFound = JobInsights.Explain(new Job { Status = "failed", Type = "install", ExitCode = unchecked((int)0x8A150014), RunAs = "system" });
        Assert.Equal("Pakken blev ikke fundet", notFound!.Title);
        var msi = JobInsights.Explain(new Job { Status = "failed", Type = "install", ExitCode = 1603, RunAs = "system" });
        Assert.Contains("retry_user", msi!.Actions);
        Assert.Null(JobInsights.Explain(new Job { Status = "success" }));
    }

    [Theory]
    [InlineData("windows_update_reset")]
    [InlineData("winget_reinstall")]
    [InlineData("disk_cleanup_full")]
    [InlineData("repair_windows")]
    [InlineData("network_reset")]
    [InlineData("disk_cleanup")]
    public void WindowsRepairsOnlyRunOnWindows(string id)
    {
        var fix = HelperCatalog.Get(id)!;
        Assert.Null(HelperService.PlatformBlocker(fix, Platforms.Windows));
        Assert.Equal("Denne rettelse findes kun til Windows", HelperService.PlatformBlocker(fix, Platforms.Linux));
        Assert.Equal("Denne rettelse findes kun til Windows", HelperService.PlatformBlocker(fix, Platforms.Mac));
    }

    [Fact]
    public void PlatformFixesAndGeneralFixesKeepTheirGating()
    {
        Assert.Null(HelperService.PlatformBlocker(HelperCatalog.Get("firewall_enable_mac")!, Platforms.Mac));
        Assert.NotNull(HelperService.PlatformBlocker(HelperCatalog.Get("firewall_enable_mac")!, Platforms.Windows));
        Assert.Null(HelperService.PlatformBlocker(HelperCatalog.Get("disk_tools_linux")!, Platforms.Linux));
        Assert.NotNull(HelperService.PlatformBlocker(HelperCatalog.Get("disk_tools_linux")!, Platforms.Mac));
        foreach (var id in new[] { "reboot_pending", "upgrade_all", "wake", "backup_now" })
            Assert.Null(HelperService.PlatformBlocker(HelperCatalog.Get(id)!, Platforms.Linux));
    }

    [Fact]
    public void NewRepairsAskFirstAndStayAwayFromUserFilesAndPolicies()
    {
        foreach (var id in new[] { "windows_update_reset", "winget_reinstall", "disk_cleanup_full" })
        {
            var f = HelperCatalog.Get(id)!;
            Assert.Equal(Risk.Confirm, f.Risk);
            Assert.False(f.AutoAllowed);
        }
        var reset = HelperCatalog.WindowsUpdateReset;
        Assert.Contains("} finally {", reset);   // tjenesterne startes igen, også ved fejl
        Assert.DoesNotContain("Policies", reset);
        Assert.DoesNotContain("sdset", reset);
        Assert.DoesNotContain("Set-Service", reset);
        foreach (var kept in new[] { "DownloadsFolder", "Recycle Bin", "Previous Installations", "Device Driver Packages", "Windows Defender" })
            Assert.DoesNotContain(kept, HelperCatalog.DiskCleanupFull);
        Assert.Contains("Repair-WinGetPackageManager -AllUsers", HelperCatalog.WingetReinstall);
    }

    /// <summary>Gemmer alle scripts som .ps1, så de kan syntakstjekkes med Windows PowerShell 5.1 (sæt WS_EXPORT_SCRIPTS).</summary>
    [Fact]
    public void ExportScriptsForSyntaxCheck()
    {
        var dir = Environment.GetEnvironmentVariable("WS_EXPORT_SCRIPTS");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        foreach (var f in HelperCatalog.All.Where(f => f.Script is not null))
            File.WriteAllText(Path.Combine(dir, f.Id + ".ps1"), f.Script, new System.Text.UTF8Encoding(true));
    }
}

public class CryptoTests
{
    [Fact]
    public void PasswordHashRoundTrip()
    {
        var h = Crypto.HashPassword("hemmelig-æøå");
        Assert.StartsWith("pbkdf2-sha256$", h);
        Assert.True(Crypto.VerifyPassword("hemmelig-æøå", h));
        Assert.False(Crypto.VerifyPassword("forkert", h));
    }

    [Fact] public void HashesAreSalted() => Assert.NotEqual(Crypto.HashPassword("a"), Crypto.HashPassword("a"));
}

public class DeployTests
{
    [Theory]
    [InlineData("Hello world!", "saltstring", "$6$saltstring$svn8UoSVapNtMuq1ukKS4tPQd8iKwSMHWjl/O817G3uBnIFNjnQJuesI68u4OTLiBFdcbYEdFCoEOfaS35inz1")]
    [InlineData("a", "xy", "$6$xy$5250cwD6G0HaskWEVps7HLt3iuI6RaW0CtpUDWt4NW09jSLinak2eYcP4KwS5.Qo0wBM5cNC2oN8ZPa0MGMfh1")]
    [InlineData("Dette er en meget lang adgangskode med mere end fireogtres tegn i alt, ja!!", "abcdefghijklmnop",
        "$6$abcdefghijklmnop$XBBqvYwzQ9wr8YhJPguTCfdO6Bq0lPPPlKkWO.fFsnlcznorFYXKtCvZkmuS1rJ8x58GOdQjF9613/uT8G/9l0")]
    public void Sha512CryptMatchesOpenSsl(string pw, string salt, string expected) => Assert.Equal(expected, ShaCrypt.Sha512(pw, salt));

    [Fact] public void RandomSaltIs16Chars() => Assert.Matches(@"^\$6\$[./0-9A-Za-z]{16}\$[./0-9A-Za-z]{86}$", ShaCrypt.Sha512("x"));

    [Theory]
    [InlineData("52:54:00:ab:cd:ef", "52-54-00-AB-CD-EF")]
    [InlineData("525400ABCDEF", "52-54-00-AB-CD-EF")]
    [InlineData(" 52-54-00-ab-cd-ef ", "52-54-00-AB-CD-EF")]
    [InlineData("52:54:00:ab:cd", null)]
    [InlineData("zz:54:00:ab:cd:ef", null)]
    public void NormalizesMac(string input, string? expected) => Assert.Equal(expected, MacAddr.Normalize(input));

    [Fact] public void GrubAndDnsmasqFormats() { Assert.Equal("52:54:00:ab:cd:ef", MacAddr.Colon("52-54-00-AB-CD-EF")); Assert.Equal("52-54-00-ab-cd-ef", MacAddr.Dash("52-54-00-AB-CD-EF")); }

    [Fact] public void AsciiForWinPe() => Assert.Equal("Boernenes pc  Soeren", DeployService.Ascii("Børnenes pc & Søren!"));
}

public class SetupCompleteTests
{
    [Fact]
    public void SetupCompleteRespectsDeployMode()
    {
        var c = WinSetupStore.SetupCompleteCmd("http://server:8585", "KEY");
        Assert.Contains("call \"%~dp0deploy.cmd\"", c);
        Assert.Contains("if /i \"%WS_MODE%\"==\"clean\" (", c);
        Assert.Contains(@"set ""LOG=%WINDIR%\Setup\Scripts\Fleetdeck-install.log""", c);
        Assert.Contains("install --server \"http://server:8585\" --key \"KEY\" %WS_ARGS%", c);
        Assert.Contains("\r\n", c);
        var dir = Environment.GetEnvironmentVariable("WS_EXPORT_SCRIPTS");
        if (!string.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, "SetupComplete.cmd"), c); }
    }
}

public class CatalogTests
{
    [Fact]
    public void CatalogHasDescriptionsAndNoDuplicates()
    {
        Assert.True(AppCatalog.All.Length > 120);
        Assert.All(AppCatalog.All, i => Assert.False(string.IsNullOrWhiteSpace(i.Description)));
        var keys = AppCatalog.All.Select(i => (i.Type, i.Id.Length > 0 ? i.Id.ToLowerInvariant() : i.Name, i.Args)).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.All(AppCatalog.All.Where(i => i.Type == PackageTypes.Winget), i => Assert.Contains('.', i.Id));
    }
}

public class PlatformTests
{
    [Theory]
    [InlineData("winget", "windows", true)]
    [InlineData("winget", "linux", false)]
    [InlineData("apt", "linux", true)]
    [InlineData("snap", "linux", true)]
    [InlineData("apt", "windows", false)]
    [InlineData("steam", null, true)]
    public void PackageFitsPlatform(string type, string? platform, bool fits) => Assert.Equal(fits, PackageTypes.Fits(type, platform));

    [Fact]
    public void AptPackageMatchesById()
    {
        var ids = new HashSet<string> { "vlc", "htop" };
        Assert.True(ComplianceService.IsInstalled(new Package { Name = "VLC", Type = PackageTypes.Apt, Identifier = "vlc" }, [], ids));
        Assert.False(ComplianceService.IsInstalled(new Package { Name = "GIMP", Type = PackageTypes.Apt, Identifier = "gimp" }, ["gimp-data"], ids));
    }
}

public class SharingAndPlatformTests
{
    [Theory]
    [InlineData("192.168.1.10", "http://192.168.1.10:8585")]
    [InlineData(" 192.168.1.10/ ", "http://192.168.1.10:8585")]
    [InlineData("http://server:9000", "http://server:9000")]
    [InlineData("https://ws.example.dk", "https://ws.example.dk")]
    [InlineData("fleetdeck.local", "http://fleetdeck.local:8585")]
    public void Serveradresse_normaliseres(string input, string expected) => Assert.Equal(expected, Discovery.Normalize(input));

    [Theory]
    [InlineData("apt", "linux", true)]
    [InlineData("flatpak", "linux", true)]
    [InlineData("brew", "mac", true)]
    [InlineData("mas", "mac", true)]
    [InlineData("winget", "windows", true)]
    [InlineData("winget", "mac", false)]
    [InlineData("brew", "linux", false)]
    [InlineData("apt", "windows", false)]
    public void Pakketyper_passer_til_platform(string type, string platform, bool fits) => Assert.Equal(fits, PackageTypes.Fits(type, platform));

    [Theory]
    [InlineData("aarch64", "arm64")]
    [InlineData("arm64", "arm64")]
    [InlineData("armv7l", "arm")]
    [InlineData("x86_64", "x64")]
    [InlineData(null, "x64")]
    public void Processortyper_normaliseres(string? arch, string expected) => Assert.Equal(expected, AgentBinary.NormalizeArch(arch));

    [Fact]
    public void Udvidelser_har_unikke_id_og_mindst_en_platform()
    {
        Assert.Equal(Extensions.BuiltIn.Length, Extensions.BuiltIn.Select(e => e.Id).Distinct().Count());
        Assert.All(Extensions.BuiltIn, e => Assert.True(e.Windows is not null || e.Linux is not null || e.Mac is not null));
        Assert.All(Extensions.BuiltIn.SelectMany(e => new[] { e.Windows, e.Linux, e.Mac }).Where(i => i is not null),
            i => Assert.Contains(i!.Type, new[] { "winget", "pkg", "flatpak", "brew", "script", "helper" }));
    }

    [Fact]
    public void Temperatur_udvidelsen_bruger_hjaelperens_script() =>
        Assert.NotNull(HelperCatalog.Get(Extensions.BuiltIn.Single(e => e.Id == "temperatur").Windows!.Identifier!)?.Script);
}

public class HiddenDialogTests
{
    private static int Pick(string title, string text, params (string, bool)[] b) => Headless.Choose(title, text, b);

    [Fact] // Inno Setup i Ajax PRO Desktop: Ja/Nej, "Ja" er standard
    public void Ajax_erstat_svares_ja() =>
        Assert.Equal(0, Pick("Setup", "Ajax PRO Desktop is already installed on your computer. Would you like to replace it?", ("&Yes", true), ("&No", false)));

    [Fact] // dansk Windows: standardknappen er Nej -> vælg alligevel ikke en afvisning
    public void Standardknap_der_er_afvisning_springes_over() =>
        Assert.Equal(0, Pick("Testboks", "Test fra Fleetdeck", ("&Ja", false), ("&Nej", true)));

    [Fact]
    public void Genstart_svares_senere() =>
        Assert.Equal(1, Pick("Setup", "You must restart your computer to complete the installation. Restart now?", ("&Yes", true), ("&No", false)));

    [Fact]
    public void Ok_boks_trykkes() => Assert.Equal(0, Pick("Fejl", "Kunne ikke finde filen.", ("OK", true)));

    [Fact]
    public void Naeste_foran_annuller() => Assert.Equal(1, Pick("Installation", "Velkommen", ("Annullér", false), ("&Næste >", false)));

    [Fact]
    public void Kun_annuller_bruges_som_sidste_udvej() => Assert.Equal(0, Pick("Installation", "Venter", ("Cancel", false)));
}

public class EnrollKeyTests
{
    [Theory]
    [InlineData("AbC-12_xyzAbC-12_xyz1234", null, "AbC-12_xyzAbC-12_xyz1234")]
    [InlineData("  \"AbC-12_xyzAbC-12_xyz1234\"​ ", null, "AbC-12_xyzAbC-12_xyz1234")]
    [InlineData("irm \"http://192.168.1.5:8585/install.ps1?key=AbC-12_xyzAbC-12_xyz1234&profile=3\" | iex", "http://192.168.1.5:8585", "AbC-12_xyzAbC-12_xyz1234")]
    [InlineData("curl -fsSL \"http://srv:8585/install.sh?key=AbC-12_xyz\" | sudo bash", "http://srv:8585", "AbC-12_xyz")]
    [InlineData("http://192.168.1.5:8585/hent#k=AbC-12_xyzAbC-12_xyz1234", "http://192.168.1.5:8585", "AbC-12_xyzAbC-12_xyz1234")]
    public void FindsServerAndKey(string input, string? server, string key)
    {
        var (s, k) = EnrollKey.Parse(input);
        Assert.Equal(server, s);
        Assert.Equal(key, k);
    }
}

public class CheckProbeTests
{
    [Theory]
    [InlineData(200, null, true)]
    [InlineData(301, null, true)]
    [InlineData(404, null, false)]
    [InlineData(204, "200,204", true)]
    [InlineData(500, "2xx", false)]
    [InlineData(401, "200-299,401", true)]
    public void StatusRules(int code, string? expect, bool ok) => Assert.Equal(ok, CheckProbe.StatusOk(code, expect));
}

public class IpadPolicyTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static Ipad Child() => new() { Id = 3, Name = "Test", ClientId = "fdabc", Schedule = IpadPolicy.DefaultBedtime(), YtMode = YtModes.Moderate };

    [Fact]
    public void Slot_index_starts_monday_midnight()
    {
        Assert.Equal(0, IpadPolicy.SlotOf(new DateTime(2026, 11, 16, 0, 10, 0)));          // mandag
        Assert.Equal(48 * 6 + 47, IpadPolicy.SlotOf(new DateTime(2026, 11, 22, 23, 45, 0))); // søndag 23:45
    }

    [Fact]
    public void Bedtime_blocks_everything_but_essentials()
    {
        var p = Child();
        var st = IpadPolicy.State(p, new DateTime(2026, 11, 16, 21, 0, 0, DateTimeKind.Utc), Utc, 0);   // mandag 21:00
        Assert.Equal("bed", st.Mode);
        Assert.Equal("blocked", st.Yt);
        var d = IpadPolicy.Build(p, st, new Dictionary<string, string>(), IpadPolicy.DefaultSchoolGroups);
        Assert.Contains("/.*/$client=fd-ipad-3", d.Rules);
        Assert.Contains("@@||apple.com^$client=fd-ipad-3", d.Rules);
        Assert.Contains("youtube", d.Services);
        Assert.Equal(new DateTime(2026, 11, 17, 7, 0, 0, DateTimeKind.Utc), st.NextChange);
    }

    [Fact]
    public void Daytime_gives_restricted_youtube_until_quota_is_used()
    {
        var p = Child(); p.YtDailyMin = 60;
        var noon = new DateTime(2026, 11, 16, 15, 0, 0, DateTimeKind.Utc);
        var st = IpadPolicy.State(p, noon, Utc, 30);
        Assert.Equal("free", st.Mode);
        Assert.Equal(YtModes.Moderate, st.Yt);
        var d = IpadPolicy.Build(p, st, new Dictionary<string, string>(), []);
        Assert.Contains("|www.youtube.com^$dnsrewrite=NOERROR;CNAME;restrictmoderate.youtube.com,client=fd-ipad-3", d.Rules);
        Assert.DoesNotContain("youtube", d.Services);
        Assert.Equal("quota", IpadPolicy.State(p, noon, Utc, 60).Yt);
        p.YtBonusDay = DateOnly.FromDateTime(noon); p.YtBonusMin = 15;
        Assert.Equal(YtModes.Moderate, IpadPolicy.State(p, noon, Utc, 60).Yt);
    }

    [Fact]
    public void Pause_and_extra_time_override_schedule()
    {
        var p = Child();
        var now = new DateTime(2026, 11, 16, 21, 0, 0, DateTimeKind.Utc);
        p.FreeUntil = now.AddMinutes(30);
        Assert.Equal("free", IpadPolicy.State(p, now, Utc, 0).Mode);
        p.PauseUntil = now.AddMinutes(10);
        Assert.Equal("pause", IpadPolicy.State(p, now, Utc, 0).Mode);
    }

    [Theory]
    [InlineData(YtModes.Moderate)]
    [InlineData(YtModes.Strict)]
    [InlineData(YtModes.KidsOnly)]
    public void Blocked_youtube_service_is_not_undone_by_restricted_mode(string mode)
    {
        var p = new Ipad { Id = 5, Name = "Barn", ClientId = "fdg23456789abcde", YtMode = mode, BlockedServices = ["youtube"] };
        var st = IpadPolicy.State(p, new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc), TimeZoneInfo.Utc, 0);
        var d = IpadPolicy.Build(p, st, new Dictionary<string, string>(), []);
        Assert.Contains("youtube", d.Services);
        Assert.DoesNotContain(d.Rules, r => r.Contains("dnsrewrite"));
    }

    [Fact]
    public void School_time_blocks_service_groups()
    {
        var p = Child(); var s = new char[336]; Array.Fill(s, '0'); s[20] = 's'; p.Schedule = new string(s);
        var st = IpadPolicy.State(p, new DateTime(2026, 11, 16, 10, 0, 0, DateTimeKind.Utc), Utc, 0);
        Assert.Equal("school", st.Mode);
        var d = IpadPolicy.Build(p, st, new Dictionary<string, string> { ["roblox"] = "gaming", ["whatsapp"] = "messenger" }, IpadPolicy.DefaultSchoolGroups);
        Assert.Contains("roblox", d.Services);
        Assert.DoesNotContain("whatsapp", d.Services);
    }

    [Fact]
    public void User_rules_section_is_replaced_not_duplicated()
    {
        var cur = new List<string> { "||ads.example^", AdGuardClient.Begin, "old", AdGuardClient.End, "" };
        var next = AdGuardClient.ReplaceSection(cur, ["new"]);
        Assert.Equal(["||ads.example^", AdGuardClient.Begin, "new", AdGuardClient.End], next);
        Assert.Equal(["||ads.example^"], AdGuardClient.ReplaceSection(next, []));
    }

    private static Ipad KidsOnly() { var p = Child(); p.YtMode = YtModes.KidsOnly; return p; }
    private static readonly DateTime Noon = new(2026, 11, 16, 15, 0, 0, DateTimeKind.Utc);   // mandag, fri tid

    /// <summary>Spærres værten af en af reglerne "||domæne^$client=…"?</summary>
    private static bool BlockedBy(IEnumerable<string> rules, string host) => rules.Where(r => r.StartsWith("||", StringComparison.Ordinal))
        .Select(r => r[2..r.IndexOf('^')]).Any(d => host == d || host.EndsWith("." + d, StringComparison.Ordinal));

    [Fact]
    public void Kids_only_generates_exact_rules_without_youtube_service()
    {
        var p = KidsOnly();
        var st = IpadPolicy.State(p, Noon, Utc, 0);
        Assert.Equal("free", st.Mode);
        Assert.Equal(YtModes.KidsOnly, st.Yt);
        var d = IpadPolicy.Build(p, st, new Dictionary<string, string>(), []);
        Assert.DoesNotContain("youtube", d.Services);   // AdGuards "youtube" spærrer også youtubekids.com
        Assert.Equal([
            "||youtube.com^$client=fd-ipad-3",
            "||youtu.be^$client=fd-ipad-3",
            "||yt.be^$client=fd-ipad-3",
            "||youtube-nocookie.com^$client=fd-ipad-3",
            "||youtubeembeddedplayer.googleapis.com^$client=fd-ipad-3",
            "||youtube.dk^$client=fd-ipad-3",
            "||youtubegaming.com^$client=fd-ipad-3",
            "||youtubego.com^$client=fd-ipad-3",
            "||withyoutube.com^$client=fd-ipad-3",
            "|youtubei.googleapis.com^$dnsrewrite=NOERROR;CNAME;restrict.youtube.com,client=fd-ipad-3",
            "|youtube.googleapis.com^$dnsrewrite=NOERROR;CNAME;restrict.youtube.com,client=fd-ipad-3",
            "@@||restrict.youtube.com^$client=fd-ipad-3",
            "@@||restrictmoderate.youtube.com^$client=fd-ipad-3",
        ], d.Rules);
    }

    [Theory]
    [InlineData("www.youtube.com", true)]
    [InlineData("m.youtube.com", true)]
    [InlineData("youtube.com", true)]
    [InlineData("music.youtube.com", true)]
    [InlineData("youtu.be", true)]
    [InlineData("www.youtube-nocookie.com", true)]
    [InlineData("youtubeembeddedplayer.googleapis.com", true)]
    [InlineData("www.youtubekids.com", false)]
    [InlineData("youtubekids.com", false)]
    [InlineData("youtubei.googleapis.com", false)]
    [InlineData("rr3---sn-uxax4vopj5qx-q0nl.googlevideo.com", false)]
    [InlineData("i.ytimg.com", false)]
    [InlineData("yt3.ggpht.com", false)]
    [InlineData("youtube-ui.l.google.com", false)]   // CNAME-mål for YouTubes adresser – må ikke spærres, ellers rammes Kids også
    public void Kids_only_blocks_regular_youtube_but_not_kids_hosts(string host, bool blocked)
    {
        var p = KidsOnly();
        var d = IpadPolicy.Build(p, IpadPolicy.State(p, Noon, Utc, 0), new Dictionary<string, string>(), []);
        Assert.Equal(blocked, BlockedBy(d.Rules, host));
    }

    [Fact]
    public void Kids_only_keeps_kids_domains_and_restrict_targets_open()
    {
        var p = KidsOnly();
        var d = IpadPolicy.Build(p, IpadPolicy.State(p, Noon, Utc, 0), new Dictionary<string, string>(), []);
        foreach (var t in IpadPolicy.YtRestrictTargets) Assert.Contains($"@@||{t}^$client=fd-ipad-3", d.Rules);
        Assert.All(IpadPolicy.YtKidsDomains, k => Assert.False(BlockedBy(d.Rules, k)));
    }

    [Fact]
    public void Kids_only_quota_and_windows_block_youtube_kids_too()
    {
        var p = KidsOnly(); p.YtDailyMin = 45;
        Assert.Equal(YtModes.KidsOnly, IpadPolicy.State(p, Noon, Utc, 44).Yt);
        var st = IpadPolicy.State(p, Noon, Utc, 45);
        Assert.Equal("quota", st.Yt);
        var d = IpadPolicy.Build(p, st, new Dictionary<string, string>(), []);
        Assert.Contains("youtube", d.Services);   // hele tjenesten inkl. youtubekids.com
        Assert.DoesNotContain(d.Rules, r => r.Contains("restrict.youtube.com"));

        var w = new char[336]; Array.Fill(w, '0'); p.YtDailyMin = 0; p.YtWindows = new string(w);
        Assert.Equal("window", IpadPolicy.State(p, Noon, Utc, 0).Yt);
        w[IpadPolicy.SlotOf(Noon)] = '1'; p.YtWindows = new string(w);
        Assert.Equal(YtModes.KidsOnly, IpadPolicy.State(p, Noon, Utc, 0).Yt);
    }

    [Fact]
    public void Youtube_kids_traffic_counts_as_youtube_and_blocked_lookups_do_not()
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse("""
            {"blocked_services":[{"id":"youtube","name":"YouTube","group_id":"streaming","rules":["||youtube.com^","||googlevideo.com^","||ytimg.com^"]}]}
            """);
        var cat = ServiceCatalog.Parse(root);
        Assert.Equal("youtube", cat.Classify("www.youtubekids.com"));   // tilføjes, selv om AdGuards liste skulle mangle den
        Assert.Equal("youtube", cat.Classify("rr1---sn-abc.googlevideo.com"));
        Assert.True(AdGuardClient.IsBlocked("FilteredBlackList"));
        Assert.True(AdGuardClient.IsBlocked("FilteredBlockedService"));
        Assert.False(AdGuardClient.IsBlocked("RewriteRule"));
        Assert.False(AdGuardClient.IsBlocked("NotFilteredNotFound"));
        Assert.False(AdGuardClient.IsBlocked(null));
    }

    [Theory]
    [InlineData("kids", true)]
    [InlineData("strict", true)]
    [InlineData("nope", false)]
    [InlineData(null, false)]
    public void Yt_modes_are_validated(string? m, bool ok) => Assert.Equal(ok, YtModes.Valid(m));

    [Fact]
    public void Domains_are_cleaned()
    {
        Assert.Equal(["www.skole.dk", "aula.dk"], IpadPolicy.Domains(["https://www.Skole.dk/login?x=1", "*.aula.dk", "nope", "bad domain.dk"]).ToList());
    }
}

public class KitVerifyTests
{
    [Fact]
    public void Good_kit_passes_and_broken_kit_fails()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var good = Path.Combine(dir, "good.tar.gz");
        using (var fs = File.Create(good))
        using (var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionLevel.Fastest))
        using (var tw = new System.Formats.Tar.TarWriter(gz))
        {
            var e = new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, "work/manifest/units.txt")
            { DataStream = new MemoryStream("a.service enabled active\nb.service enabled inactive\n"u8.ToArray()) };
            tw.WriteEntry(e);
            tw.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, "opt/app/x.py") { DataStream = new MemoryStream([1, 2, 3]) });
        }
        var r = Kits.Verify(good);
        Assert.True(r.Ok); Assert.Equal(2, r.Files); Assert.Equal(2, r.Units);
        var bad = Path.Combine(dir, "bad.tar.gz");
        File.WriteAllBytes(bad, File.ReadAllBytes(good)[..40]);
        Assert.False(Kits.Verify(bad).Ok);
        Directory.Delete(dir, true);
    }
}
