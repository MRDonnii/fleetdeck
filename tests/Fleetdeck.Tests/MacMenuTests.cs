using System.Text;
using System.Xml.Linq;
using Fleetdeck.MacPack;
using Fleetdeck.Shared;
using Xunit;

namespace Fleetdeck.Tests;

/// <summary>
/// Mac-menulinjen: den native menu (Swift) skal have versionsstemplet for at blive brugt og komme med i pakken; ellers viser appen
/// selv menuen. Fleetdeck.app følger agentens version (før blev en Mac hængende på den app, den blev installeret med).
/// </summary>
public class MacMenuTests
{
    private static string Repo(string rel) => File.ReadAllText(Path.Combine(MacPackTests.RepoRoot, rel));

    [Fact]
    public void SwiftMenuCarriesTheSameStampAsTheApp()
    {
        var swift = Repo("src/Fleetdeck.MacMenu/main.swift");
        Assert.Contains($"let menuStamp = \"{MacMenuStamp.Marker}\"", swift);
        Assert.Contains("print(menuStamp)", swift);   // bruges, så swiftc -O ikke smider teksten væk
        // det, stemplet lover: beskeder og "Skriv til support…" i menuen
        Assert.Contains("\"Beskeder…\"", swift);
        Assert.Contains("\"Skriv til support…\"", swift);
    }

    [Fact]
    public void StaleMenuIsNotCurrent()
    {
        Assert.True(MacMenuStamp.IsCurrent(Encoding.ASCII.GetBytes("xx" + MacMenuStamp.Marker + "yy")));
        Assert.False(MacMenuStamp.IsCurrent(Encoding.ASCII.GetBytes("Hold vågen --setup")));          // som den håndbyggede fra 18/11
        Assert.False(MacMenuStamp.IsCurrent(Encoding.ASCII.GetBytes("fleetdeck-menu-api=1")));
        Assert.False(MacMenuStamp.IsCurrent("/findes/ikke/FleetdeckMenu"));
    }

    [Fact]
    public void AppAndMenuAlwaysOfferContactingSupport()
    {
        Assert.Contains("Item(\"Skriv til support…\", ShowCompose)", Repo("src/Fleetdeck.MacApp/Tray.cs"));
        var status = Repo("src/Fleetdeck.MacApp/StatusWindow.cs");
        Assert.Contains("Kontakt admin…", status);
        Assert.Contains("_tray.ShowCompose()", status);
        // appen stoler kun på en native menu med stempel – ellers viser den selv ikonet
        Assert.Contains("MacMenuStamp.IsCurrent(MacPaths.NativeMenu)", Repo("src/Fleetdeck.MacApp/Tray.cs"));
    }

    [Fact]
    public void LaunchAgentUsesTheAppWhenNoCurrentNativeMenu()
    {
        var plist = MacInstall.MenuBarAgentPlist(MacPaths.InstalledApp);   // ingen /usr/local/fleetdeck/menu her
        Assert.Contains("<string>/Applications/Fleetdeck.app/Contents/MacOS/Fleetdeck</string><string>--tray</string>", plist);
        Assert.DoesNotContain(MacPaths.NativeMenu, plist);
    }

    [Fact]
    public void PackageTakesOnlyACurrentMenu()
    {
        var dir = Directory.CreateTempSubdirectory("fd-macmenu-").FullName;
        try
        {
            var pub = Directory.CreateDirectory(Path.Combine(dir, "pub")).FullName;
            File.WriteAllBytes(Path.Combine(pub, "Fleetdeck"), MacPackTests.FakeMachO());
            var menu = Directory.CreateDirectory(Path.Combine(dir, "menu")).FullName;
            var app = Path.Combine(dir, "Fleetdeck.app");

            // forældet: Mach-O uden stempel
            File.WriteAllBytes(Path.Combine(menu, "FleetdeckMenu"), MacPackTests.FakeMachO(3000, 500));
            var e = Assert.Throws<InvalidOperationException>(() => AppBundle.Build(new AppBundle.Options(pub, app, "3.37.0", MenuDir: menu)));
            Assert.Contains("forældet", e.Message);
            // ikke et macOS-program
            File.WriteAllBytes(Path.Combine(menu, "FleetdeckMenu"), Encoding.ASCII.GetBytes("#!/bin/sh " + MacMenuStamp.Marker));
            Assert.Throws<InvalidOperationException>(() => AppBundle.Build(new AppBundle.Options(pub, app, "3.37.0", MenuDir: menu)));

            // aktuel: lægges i Resources/menu (med ikonet) og forsegles i CodeResources; appens signatur holder stadig
            var bin = MacPackTests.FakeMachO(3000, 500).Concat(Encoding.ASCII.GetBytes(MacMenuStamp.Marker)).ToArray();
            File.WriteAllBytes(Path.Combine(menu, "FleetdeckMenu"), bin);
            File.WriteAllBytes(Path.Combine(menu, "menu-icon@2x.png"), [1, 2, 3]);
            AppBundle.Build(new AppBundle.Options(pub, app, "3.37.0", MenuDir: menu));
            var c = Path.Combine(app, "Contents");
            var packed = Path.Combine(c, "Resources", "menu", "FleetdeckMenu");
            Assert.Equal(bin, File.ReadAllBytes(packed));
            Assert.True(File.Exists(Path.Combine(c, "Resources", "menu", "menu-icon@2x.png")));
            if (!OperatingSystem.IsWindows()) Assert.True(File.GetUnixFileMode(packed).HasFlag(UnixFileMode.UserExecute));
            var cr = File.ReadAllBytes(Path.Combine(c, "_CodeSignature", "CodeResources"));
            var keys = XDocument.Parse(Encoding.UTF8.GetString(cr)).Descendants("key").Select(k => k.Value).ToList();
            Assert.Contains("Resources/menu/FleetdeckMenu", keys);
            var v = MachOSigner.Verify(File.ReadAllBytes(Path.Combine(c, "MacOS", "Fleetdeck")), File.ReadAllBytes(Path.Combine(c, "Info.plist")), cr);
            Assert.True(v.Ok, v.Why);
            // og appens version kan læses af agenten (MacAppSync)
            Assert.Equal("3.37.0", MacAppUpdate.BundleVersion(File.ReadAllText(Path.Combine(c, "Info.plist"))));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("3.24.1", "3.36.0", true)]       // Mac'en, der stod på appen fra installationen
    [InlineData("3.36.0", "3.36.0", false)]
    [InlineData("3.37.0", "3.36.0", false)]      // aldrig ned i version
    [InlineData("3.36.0", "3.37.0-beta.1", true)]
    [InlineData(null, "3.36.0", false)]          // ingen app / ukendt version: rør den ikke
    [InlineData("3.24.1", null, false)]
    [InlineData("noget", "3.36.0", false)]
    public void AppFollowsTheAgentVersion(string? app, string? agent, bool update) => Assert.Equal(update, MacAppUpdate.Needed(app, agent));

    [Fact]
    public void AppInstallKeepsTheOldAppUntilTheNewOneIsReady()
    {
        var s = MacAppUpdate.InstallScript("/usr/local/fleetdeck/Fleetdeck.dmg.download");
        Assert.StartsWith("set -e;", s);
        Assert.Contains("-nobrowse -readonly", s);
        Assert.Contains("hdiutil detach", s);   // trap: afmonteres også ved fejl
        var copy = s.IndexOf("ditto", StringComparison.Ordinal);
        var swap = s.IndexOf("mv '/Applications/Fleetdeck.app' '/Applications/.Fleetdeck.old.app'", StringComparison.Ordinal);
        Assert.True(copy > 0 && swap > copy, "den nye app skal ligge klar, før den gamle flyttes");
        Assert.DoesNotContain("rm -rf '/Applications/Fleetdeck.app'", s);
    }

    [Fact]
    public void ReleaseBuildsTheSwiftMenuOnMacOsAndRequiresIt()
    {
        var wf = Repo(".github/workflows/release-image.yml");
        Assert.Contains("runs-on: macos-latest", wf);
        Assert.Contains("swiftc -O", wf);
        Assert.Contains("codesign --force -s -", wf);
        Assert.Contains("needs: [macmenu, test]", wf);   // billedet bygges først, når menuen og testene (tests.yml) er grønne
        Assert.Contains("uses: ./.github/workflows/tests.yml", wf);
        Assert.Contains("REQUIRE_MAC_MENU=1", wf);
        var df = Repo("Dockerfile");
        Assert.Contains("ARG REQUIRE_MAC_MENU=0", df);
        Assert.Contains("$MA --out /out/dmg/Fleetdeck-mac-arm64.dmg", df);
        Assert.Contains("$MX --out /out/dmg/Fleetdeck-mac-x64.dmg", df);
        // serveren signerer disk-imaget, så agenten kan opdatere appen
        Assert.Contains("keys.For(dmg)", Repo("src/Fleetdeck.Server/Endpoints/AgentEndpoints.cs"));
    }
}
