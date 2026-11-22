using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using Fleetdeck.MacPack;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;
using Xunit;

namespace Fleetdeck.Tests;

/// <summary>
/// Mac: appen i Dock, ældre Intel-Mac'er (legacy-bygget), startvagten, osascripts fejltekst og "Find automatisk" uden broadcast.
/// </summary>
public class MacDockIntelTests
{
    // ---------------------------------------------------------------- Dock

    [Theory]
    [InlineData("show")] [InlineData("setup")] [InlineData("tray")] [InlineData("messages")] [InlineData("compose")]
    public void App_is_in_the_Dock_whenever_it_runs(string mode)
    {
        Assert.True(MacDock.ShowInDock(mode));
        Assert.Equal(MacDock.Regular, MacDock.Policy(mode));
    }

    [Fact]
    public void Dock_click_brings_an_open_window_forward_or_opens_the_main_window()
    {
        Assert.Equal(MacDock.DockClick.OpenMain, MacDock.OnDockClick(0));
        Assert.Equal(MacDock.DockClick.BringToFront, MacDock.OnDockClick(1));
        Assert.Equal(MacDock.DockClick.BringToFront, MacDock.OnDockClick(3));
        Assert.Equal((0L, 1L), (MacDock.Regular, MacDock.Accessory));   // NSApplicationActivationPolicyRegular / Accessory
    }

    [Fact]
    public void Info_plist_has_no_LSUIElement_and_declares_the_programs_own_minimum_macOS()
    {
        var dir = Directory.CreateTempSubdirectory("macdock-").FullName;
        try
        {
            var pub = Directory.CreateDirectory(Path.Combine(dir, "pub")).FullName;
            File.WriteAllBytes(Path.Combine(pub, "Fleetdeck"), WithMinOs(MacPackTests.FakeMachO(), 12, 0));
            var app = Path.Combine(dir, "Fleetdeck.app");
            AppBundle.Build(new AppBundle.Options(pub, app, "3.45.0"));
            var c = Path.Combine(app, "Contents");
            var d = Plist(File.ReadAllText(Path.Combine(c, "Info.plist")));
            Assert.False(d.ContainsKey("LSUIElement"));                 // i Dock og Cmd-Tab
            Assert.False(d.ContainsKey("LSBackgroundOnly"));
            Assert.Equal("Fleetdeck", d["CFBundleName"]);
            Assert.Equal("Fleetdeck", d["CFBundleDisplayName"]);
            Assert.Equal("Fleetdeck", d["CFBundleIconFile"]);
            Assert.Equal("APPL", d["CFBundlePackageType"]);
            Assert.Equal("12.0", d["LSMinimumSystemVersion"]);
            var v = MachOSigner.Verify(File.ReadAllBytes(Path.Combine(c, "MacOS", "Fleetdeck")), File.ReadAllBytes(Path.Combine(c, "Info.plist")),
                File.ReadAllBytes(Path.Combine(c, "_CodeSignature", "CodeResources")));
            Assert.True(v.Ok, v.Why);

            // legacy-bygget (.NET 8) er bygget til 10.15 → Finder kan åbne det på Catalina og Big Sur
            File.WriteAllBytes(Path.Combine(pub, "Fleetdeck"), WithMinOs(MacPackTests.FakeMachO(), 10, 15));
            AppBundle.Build(new AppBundle.Options(pub, app, "3.45.0"));
            Assert.Equal("10.15", Plist(File.ReadAllText(Path.Combine(c, "Info.plist")))["LSMinimumSystemVersion"]);
            // et ønske højere end programmets eget minimum vinder; et lavere gør ikke
            AppBundle.Build(new AppBundle.Options(pub, app, "3.45.0", MinimumSystem: "13.0"));
            Assert.Equal("13.0", Plist(File.ReadAllText(Path.Combine(c, "Info.plist")))["LSMinimumSystemVersion"]);
            AppBundle.Build(new AppBundle.Options(pub, app, "3.45.0", MinimumSystem: "10.13"));
            Assert.Equal("10.15", Plist(File.ReadAllText(Path.Combine(c, "Info.plist")))["LSMinimumSystemVersion"]);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Minimum_macOS_is_read_from_the_Mach_O()
    {
        Assert.Equal(new Version(12, 0, 0), AppBundle.MinimumOs(WithMinOs(MacPackTests.FakeMachO(), 12, 0)));
        Assert.Equal(new Version(10, 15, 0), AppBundle.MinimumOs(WithMinOs(MacPackTests.FakeMachO(), 10, 15)));
        Assert.Null(AppBundle.MinimumOs(MacPackTests.FakeMachO()));          // ingen LC_BUILD_VERSION
        Assert.Null(AppBundle.MinimumOs(new byte[16]));
        Assert.Equal("12.0", AppBundle.MinimumSystemFor(MacPackTests.FakeMachO(), null));
        // universel binær: den højeste af skivernes
        var fat = Fat(WithMinOs(MacPackTests.FakeMachO(), 10, 13), WithMinOs(MacPackTests.FakeMachO(), 11, 0));
        Assert.Equal(new Version(11, 0, 0), AppBundle.MinimumOs(fat));
        Assert.True(AppBundle.IsMachO(fat));
    }

    // ---------------------------------------------------------------- ældre Mac'er

    [Theory]
    [InlineData(Architecture.Arm64, "15.1", "arm64")] [InlineData(Architecture.Arm64, "11.7", "arm64")]
    [InlineData(Architecture.X64, "12.0", "x64")] [InlineData(Architecture.X64, "26.1", "x64")]
    [InlineData(Architecture.X64, "11.7.10", "x64-legacy")] [InlineData(Architecture.X64, "10.16", "x64-legacy")] [InlineData(Architecture.X64, "10.15.7", "x64-legacy")]
    [InlineData(Architecture.X64, null, "x64")] [InlineData(Architecture.X64, "0.0", "x64")]
    public void Intel_Macs_older_than_macOS_12_get_the_legacy_build(Architecture a, string? os, string arch)
    {
        var v = os is null ? null : Version.Parse(os);
        Assert.Equal(arch, MacCompat.ArchFor(a, v));
        Assert.Equal(arch, MacInstall.Arch(a, v));
    }

    [Fact]
    public void Too_old_macOS_gets_a_clear_Danish_message()
    {
        Assert.Contains("10.15 (Catalina)", MacCompat.Unsupported(Architecture.X64, new Version(10, 14, 6)));
        Assert.Contains("10.14.6", MacCompat.Unsupported(Architecture.X64, new Version(10, 14, 6)));
        Assert.Contains("macOS 12 (Monterey)", MacCompat.Unsupported(Architecture.Arm64, new Version(11, 6)));
        Assert.Null(MacCompat.Unsupported(Architecture.X64, new Version(10, 15)));
        Assert.Null(MacCompat.Unsupported(Architecture.X64, new Version(11, 7)));
        Assert.Null(MacCompat.Unsupported(Architecture.Arm64, new Version(12, 0)));
        Assert.Null(MacCompat.Unsupported(Architecture.X64, null));
        Assert.Equal(new Version(12, 0), MacCompat.Minimum);
        Assert.Equal(new Version(10, 15), MacCompat.LegacyMinimum);
        Assert.False(MacCompat.IsLegacyBuild);   // testene kører på det almindelige byg
    }

    [Fact]
    public void Server_serves_the_legacy_agent_and_dmg_by_name()
    {
        Assert.Equal("x64-legacy", AgentBinary.NormalizeArch("x64-legacy"));
        Assert.Equal("x64-legacy", AgentBinary.NormalizeArch("LEGACY-X64"));
        Assert.Equal("fleetdeck-agent-mac-x64-legacy", AgentBinary.MacAgentFile("x64-legacy"));
        Assert.Equal("fleetdeck-agent-mac-x64", AgentBinary.MacAgentFile("x64"));
        Assert.Equal("fleetdeck-agent-mac-x64", AgentBinary.MacAgentFile(null));
        Assert.Equal("fleetdeck-agent-mac-arm64", AgentBinary.MacAgentFile("aarch64"));
        Assert.Equal(("Fleetdeck-mac-x64-legacy.dmg", "Fleetdeck-Intel-macOS10.15-11.dmg"), AgentBinary.MacDmgFile("x64-legacy"));
        Assert.Equal(("Fleetdeck-mac-x64.dmg", "Fleetdeck-Intel.dmg"), AgentBinary.MacDmgFile("x64"));
        Assert.Equal(("Fleetdeck-mac-arm64.dmg", "Fleetdeck-AppleSilicon.dmg"), AgentBinary.MacDmgFile("arm64"));
        // Linux påvirkes ikke
        Assert.Equal("x64", AgentBinary.NormalizeArch("x86_64"));
    }

    [Fact]
    public void Build_files_ship_the_legacy_packages()
    {
        var docker = File.ReadAllText(Path.Combine(MacPackTests.RepoRoot, "Dockerfile"));
        Assert.Contains("-r osx-x64 -p:FleetdeckLegacy=true -o /out/macagent-x64-legacy", docker);
        Assert.Contains("-r osx-x64 -p:FleetdeckLegacy=true -o /out/macapp-x64-legacy", docker);
        Assert.Contains("--out /out/dmg/Fleetdeck-mac-x64-legacy.dmg", docker);
        Assert.Contains("./agent/fleetdeck-agent-mac-x64-legacy", docker);
        var install = File.ReadAllText(Path.Combine(MacPackTests.RepoRoot, "src/Fleetdeck.Server/Templates/install.sh"));
        Assert.Contains("sw_vers -productVersion", install);
        Assert.Contains("ARCH=x64-legacy", install);
        Assert.Contains("Fleetdeck kræver macOS 10.15 (Catalina) eller nyere", install);
        Assert.Contains("Fleetdeck kræver macOS 12 (Monterey) eller nyere", install);
        foreach (var proj in new[] { "Shared/Fleetdeck.Shared.csproj", "Agent.Unix/Fleetdeck.Agent.Unix.csproj", "MacApp/Fleetdeck.MacApp.csproj" })
            Assert.Contains("<TargetFramework Condition=\"'$(FleetdeckLegacy)' == 'true'\">net8.0</TargetFramework>",
                File.ReadAllText(Path.Combine(MacPackTests.RepoRoot, "src/Fleetdeck." + proj)));
    }

    // ---------------------------------------------------------------- startvagten

    [Fact]
    public void Startup_log_and_alert_text()
    {
        var at = new DateTime(2026, 11, 22, 12, 0, 0);
        Assert.Equal("2026-11-22 12:00:00 start Fleetdeck 3.45.0 (legacy) · macOS 11.7.10 · X64 · .NET 8.0.34 · show",
            MacStartupLog.StartLine(at, "3.45.0", "11.7.10", "X64", "8.0.34", true, "show"));
        Assert.Equal("2026-11-22 12:00:00 klar efter 4.2 s", MacStartupLog.ReadyLine(at, TimeSpan.FromSeconds(4.21)));
        var e = new System.Reflection.TargetInvocationException(new DllNotFoundException("libAvaloniaNative.dylib kunne ikke indlæses\nmere tekst"));
        Assert.Equal("Fleetdeck kunne ikke starte: libAvaloniaNative.dylib kunne ikke indlæses", MacStartupLog.AlertText(e));
        Assert.StartsWith("Fleetdeck kunne ikke starte: ", MacStartupLog.AlertText(new Exception(new string('x', 1000))));
        Assert.True(MacStartupLog.AlertText(new Exception(new string('x', 1000))).Length < 340);
        var block = MacStartupLog.CrashBlock(at, "opstart", e);
        Assert.Contains("FEJL (opstart): System.Reflection.TargetInvocationException", block);
        Assert.Contains("DllNotFoundException", block);
        Assert.Equal(Path.Combine("/Users/bror", "Library", "Logs", "Fleetdeck"), MacStartupLog.Dir("/Users/bror"));
        // loggen holdes lille og starter på en hel linje
        var big = string.Concat(Enumerable.Range(0, 20000).Select(i => $"linje {i}\n"));
        var t = MacStartupLog.Trim(big, 10_000);
        Assert.True(Encoding.UTF8.GetByteCount(t) <= 10_000);
        Assert.StartsWith("linje ", t);
        Assert.EndsWith("linje 19999\n", t);
        Assert.Equal("kort\n", MacStartupLog.Trim("kort\n"));
    }

    // ---------------------------------------------------------------- osascripts fejltekst

    [Fact]
    public void Osascript_wrapping_is_removed_from_the_message()
    {
        const string raw = "0:767: execution error: Serveren tager ikke imod anmodninger om adgang lige nu. Bed administratoren om en engangskode eller en tilmeldingsnøgle. (3)\n";
        Assert.Equal("Serveren tager ikke imod anmodninger om adgang lige nu. Bed administratoren om en engangskode eller en tilmeldingsnøgle.", MacInstall.CleanOsaOutput(raw));
        Assert.Equal(3, MacInstall.ScriptExitCode(1, raw));   // exit-koden læses stadig af den rå tekst
        // "do shell script" skiller scriptets linjer med \r: den sidste linje er beskeden
        var multi = "0:1220: execution error: Henter agenten…\rForbinder til http://192.168.1.10:8585\rTilmeldingsnøglen blev afvist. (3)";
        Assert.Equal("Henter agenten…\nForbinder til http://192.168.1.10:8585\nTilmeldingsnøglen blev afvist.", MacInstall.CleanOsaOutput(multi));
        Assert.Equal("Tilmeldingsnøglen blev afvist.", SetupProbe.LastLine(MacInstall.CleanOsaOutput(multi)));
        // stdout før fejlen bevares; annulleret adgangskode
        Assert.Equal("ok\nUser canceled.", MacInstall.CleanOsaOutput("ok\n0:42: execution error: User canceled. (-128)"));
        // ikke en osascript-fejl: uændret (bortset fra mellemrum i enderne)
        Assert.Equal("Fleetdeck er installeret. Kode: AB12-CD34", MacInstall.CleanOsaOutput("Fleetdeck er installeret. Kode: AB12-CD34\n"));
        Assert.Equal("Fejl (kode 2)", MacInstall.CleanOsaOutput("Fejl (kode 2)"));
        Assert.Equal("", MacInstall.CleanOsaOutput(""));
    }

    // ---------------------------------------------------------------- "Find automatisk" uden broadcast

    private static Discovery.LocalNet Net(string ip, int prefix, string name = "en0", NetworkInterfaceType type = NetworkInterfaceType.Ethernet,
        bool up = true, bool gw = true, string desc = "") => new(name, desc, type, up, IPAddress.Parse(ip), prefix, gw);

    [Fact]
    public void Scan_targets_are_the_24_around_the_own_address()
    {
        var t = Discovery.ScanTargets([Net("192.168.1.37", 24)]);
        Assert.Equal(253, t.Count);
        Assert.DoesNotContain(IPAddress.Parse("192.168.1.37"), t);
        Assert.DoesNotContain(IPAddress.Parse("192.168.1.0"), t);
        Assert.DoesNotContain(IPAddress.Parse("192.168.1.255"), t);
        Assert.Equal(IPAddress.Parse("192.168.1.1"), t[0]);
        // /22: kun /24'en omkring egen adresse
        var t22 = Discovery.ScanTargets([Net("10.0.5.20", 22)]);
        Assert.Equal(253, t22.Count);
        Assert.All(t22, a => Assert.StartsWith("10.0.5.", a.ToString()));
        // /16 eller større, link-local, nede, virtuelle kort og IPv6 springes over
        Assert.Empty(Discovery.ScanTargets([Net("10.1.2.3", 16), Net("10.1.2.3", 8), Net("169.254.10.2", 24), Net("192.168.2.5", 24, up: false)]));
        Assert.Empty(Discovery.ScanTargets([Net("172.17.0.1", 24, "docker0"), Net("192.168.122.1", 24, "virbr0"), Net("10.8.0.2", 24, "utun3"),
            Net("172.20.0.1", 24, "vEthernet (WSL)", desc: "Hyper-V Virtual Ethernet Adapter"), Net("10.9.0.2", 24, "wg0", NetworkInterfaceType.Tunnel),
            Net("127.0.0.1", 24, "lo0", NetworkInterfaceType.Loopback), Net("fe80::1", 64)]));
        // et lille net: kun dets egne adresser
        Assert.Equal([IPAddress.Parse("192.168.9.10")], Discovery.ScanTargets([Net("192.168.9.9", 30)]));
    }

    [Fact]
    public void Scan_takes_at_most_two_interfaces_gateway_first_and_no_duplicates()
    {
        var t = Discovery.ScanTargets([
            Net("192.168.50.2", 24, "en1", NetworkInterfaceType.Wireless80211, gw: false),
            Net("192.168.1.10", 24, "en0"),
            Net("192.168.1.11", 24, "en5"),            // samme /24 som en0: én gang
            Net("10.20.30.7", 24, "en7"),
        ]);
        Assert.Equal(2 * 253, t.Count);                  // en0's /24 (uden .10, men med .11) + en7's /24
        Assert.Contains(IPAddress.Parse("192.168.1.11"), t);
        Assert.Contains(IPAddress.Parse("10.20.30.1"), t);
        Assert.DoesNotContain(IPAddress.Parse("192.168.50.1"), t);
        Assert.Equal(t.Count, t.Distinct().Count());
    }

    [Theory]
    [InlineData("""{"ok":true,"version":"3.45.0","release":"3.45.0-beta.1","app":"fleetdeck"}""", "3.45.0")]
    [InlineData("""{"app":"fleetdeck","version":"3.50.0","ok":true,"release":"3.50.0","extra":1}""", "3.50.0")]
    [InlineData("""{"ok":true,"version":"3.44.1","release":"3.44.1"}""", "3.44.1")]     // ældre server uden markør
    [InlineData("""{"ok":true,"version":"3.44.1","release":"3.44.1","name":"router"}""", null)]
    [InlineData("""{"ok":true,"version":"1.2"}""", null)]
    [InlineData("""{"status":"ok","version":"2.0"}""", null)]
    [InlineData("""{"app":"andet","version":"1"}""", null)]
    [InlineData("""{"ok":false,"version":"3.44.1","release":"3.44.1"}""", null)]
    [InlineData("<html>router</html>", null)] [InlineData("[]", null)] [InlineData("", null)] [InlineData(null, null)]
    public void Only_Fleetdeck_health_answers_are_accepted(string? body, string? version) => Assert.Equal(version, Discovery.FleetdeckHealthVersion(body));

    [Fact]
    public async Task Scan_finds_the_server_and_stops()
    {
        var targets = Enumerable.Range(1, 254).Select(i => IPAddress.Parse($"192.168.1.{i}")).ToList();
        var asked = 0;
        var found = await Discovery.Scan(targets, TimeSpan.FromSeconds(3), probe: async (ip, ct) =>
        {
            Interlocked.Increment(ref asked);
            await Task.Delay(5, ct);
            if (ip.ToString() == "192.168.1.200") return """{"ok":true,"version":"3.45.0","release":"3.45.0","app":"fleetdeck"}""";
            if (ip.ToString() == "192.168.1.1") return "<html>router</html>";
            throw new HttpRequestException("forbindelsen blev afvist");
        });
        var f = Assert.Single(found);
        Assert.Equal("http://192.168.1.200:8585", f.Url);
        Assert.Equal("3.45.0", f.Version);
        Assert.Null(f.AccessRequests);
        Assert.True(asked < 254 + 1);

        // ingen svarer: tom liste inden for tiden
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Empty(await Discovery.Scan(targets, TimeSpan.FromMilliseconds(300), probe: async (_, ct) => { await Task.Delay(10_000, ct); return null; }));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3));
        Assert.Empty(await Discovery.Scan([], TimeSpan.FromSeconds(1)));
    }

    // ---------------------------------------------------------------- hjælpere

    private static Dictionary<string, string> Plist(string xml)
    {
        var els = XDocument.Parse(xml).Root!.Element("dict")!.Elements().ToList();
        var d = new Dictionary<string, string>();
        for (var i = 0; i + 1 < els.Count; i += 2) d[els[i].Value] = els[i + 1].Name == "true" ? "true" : els[i + 1].Value;
        return d;
    }

    /// <summary>Tilføj LC_BUILD_VERSION (platform macOS, minos) til en falsk Mach-O (der er plads i headeren).</summary>
    private static byte[] WithMinOs(byte[] b, int major, int minor)
    {
        var ncmds = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(16));
        var size = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(20));
        var at = 32 + (int)size;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), 0x32);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at + 4), 24);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at + 8), 1);                                  // PLATFORM_MACOS
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at + 12), (uint)(major << 16 | minor << 8));  // minos
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at + 16), 15u << 16);                         // sdk
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at + 20), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), ncmds + 1);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(20), size + 24);
        return b;
    }

    private static byte[] Fat(params byte[][] slices)
    {
        var header = 8 + 20 * slices.Length;
        var offs = new List<int>(); var pos = (header + 4095) / 4096 * 4096;
        foreach (var s in slices) { offs.Add(pos); pos = (pos + s.Length + 4095) / 4096 * 4096; }
        var b = new byte[pos];
        BinaryPrimitives.WriteUInt32BigEndian(b, 0xCAFEBABE);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), (uint)slices.Length);
        for (var i = 0; i < slices.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8 + 20 * i + 8), (uint)offs[i]);
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8 + 20 * i + 12), (uint)slices[i].Length);
            slices[i].CopyTo(b, offs[i]);
        }
        return b;
    }
}
