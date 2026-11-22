using System.Diagnostics;
using System.Xml.Linq;
using Fleetdeck.Shared;
using Xunit;

namespace Fleetdeck.Tests;

/// <summary>
/// Mac uden GUI: Terminal-kommandoen og "kun agenten" gav hverken Fleetdeck.app, opsætningsvindue eller ikon, LaunchAgent'en blev kun
/// skrevet (ikonet kom først ved næste login), og ingen tjekkede, at ikonet kørte. Menuen var en løs binær uden bundle-id, så macOS
/// kunne gemme ikonet (notch / Systemindstillinger → Menulinje). Det, der kan testes uden en Mac, testes her.
/// </summary>
public class MacGuiTests
{
    private static readonly DateTime Now = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
    private static string Repo(string rel) => File.ReadAllText(Path.Combine(MacPackTests.RepoRoot, rel));
    private static string Norm(string s) => s.Replace("\r\n", "\n").Trim();

    // ---------------------------------------------------------------- menu-appen (FleetdeckMenu.app)

    [Fact]
    public void MenuAppHasABundleIdAndNoDockIcon()
    {
        var x = XDocument.Parse(MacMenuBundle.InfoPlist());
        var d = x.Root!.Element("dict")!.Elements().ToList();
        string? Val(string key) => d.SkipWhile(e => e.Value != key).Skip(1).FirstOrDefault() is { } v ? (v.Name == "true" ? "true" : v.Value) : null;
        Assert.Equal("com.fleetdeck.menu", Val("CFBundleIdentifier"));
        Assert.Equal(MacPaths.MenuBundleId, Val("CFBundleIdentifier"));
        Assert.Equal("FleetdeckMenu", Val("CFBundleExecutable"));
        Assert.Equal("APPL", Val("CFBundlePackageType"));
        Assert.Equal("Fleetdeck", Val("CFBundleName"));
        Assert.Equal("true", Val("LSUIElement"));
        Assert.Equal("12.0", Val("LSMinimumSystemVersion"));
        Assert.Equal(MacMenuBundle.StampVersion, Val("CFBundleVersion"));
        Assert.Matches("^[0-9]+$", MacMenuBundle.StampVersion);
        // samme fil, som CI tjekker med Apples plutil/codesign
        Assert.Equal(Norm(MacMenuBundle.InfoPlist()), Norm(Repo("src/Fleetdeck.MacMenu/Info.plist")));
    }

    [Fact]
    public void MenuAppIsOnlyRebuiltWhenTheMenuChanges()
    {
        var info = MacMenuBundle.InfoPlist();
        Assert.True(MacMenuBundle.NeedsBuild(null, "abc", null));          // ikke bygget endnu
        Assert.False(MacMenuBundle.NeedsBuild("abc\n", "abc", info));      // uændret: intet sker (kører hvert minut)
        Assert.True(MacMenuBundle.NeedsBuild("abc", "def", info));         // ny menu i appen
        Assert.True(MacMenuBundle.NeedsBuild("abc", "abc", info.Replace("12.0", "11.0")));
    }

    [Fact]
    public void MenuAppIsSignedOwnedByRootAndReplacesTheLooseBinary()
    {
        var s = MacMenuBundle.InstallScript("/usr/local/fleetdeck/.FleetdeckMenu.new.app");
        Assert.Contains("codesign --force --sign - --identifier com.fleetdeck.menu '/usr/local/fleetdeck/.FleetdeckMenu.new.app'", s);
        Assert.Contains("chown -R root:wheel", s);
        Assert.Contains("chmod -R go-w", s);   // brugeren kører menuen, men kan ikke ændre den
        Assert.Contains($"mv '/usr/local/fleetdeck/.FleetdeckMenu.new.app' '{MacPaths.NativeMenuApp}'", s);
        Assert.Contains($"rm -f '{MacPaths.LegacyNativeMenu}'", s);
        Assert.True(s.IndexOf("codesign", StringComparison.Ordinal) < s.IndexOf("mv ", StringComparison.Ordinal), "signeres, før den byttes ind");
        Assert.True(s.IndexOf("xattr -cr", StringComparison.Ordinal) < s.IndexOf("codesign", StringComparison.Ordinal), "udvidede attributter væk før codesign");
        AssertShellSyntax(s);
    }

    [Fact]
    public void PathsKeepTheMenuRootOwnedAndTheStatusReadable()
    {
        Assert.StartsWith(MacPaths.AgentDir + "/", MacPaths.NativeMenuApp);          // kun root kan skrive i /usr/local/fleetdeck
        Assert.Equal(MacPaths.NativeMenuApp + "/Contents/MacOS/FleetdeckMenu", MacPaths.NativeMenu);
        Assert.Equal(Path.GetDirectoryName(MacPaths.NativeMenuApp), Path.GetDirectoryName(MacPaths.LegacyNativeMenu));
        Assert.StartsWith("/Library/LaunchAgents/", MacPaths.MenuBarPlist);         // alle brugere, ved hvert login
        Assert.DoesNotContain(MacPaths.SupportDir, MacPaths.NativeMenuApp);          // ikke i mappen, alle kan skrive ordrer i
        Assert.Equal(MacPaths.InstalledApp + "/Contents/Resources/menu", MacPaths.BundledMenuDir);
    }

    // ---------------------------------------------------------------- LaunchAgent

    [Fact]
    public void LaunchAgentStartsTheMenuAppForEveryUserAtLogin()
    {
        var plist = MacInstall.MenuBarAgentPlist(MacPaths.InstalledApp, nativeMenu: true);
        var x = XDocument.Parse(plist);
        var d = x.Root!.Element("dict")!.Elements().ToList();
        XElement Val(string key) => d.SkipWhile(e => e.Value != key).Skip(1).First();
        Assert.Equal(MacPaths.MenuBarLabel, Val("Label").Value);
        Assert.Equal([MacPaths.NativeMenu], Val("ProgramArguments").Elements("string").Select(e => e.Value));
        Assert.Equal("true", Val("RunAtLoad").Name.LocalName);
        Assert.Equal("false", Val("KeepAlive").Elements().Skip(1).First().Name.LocalName);   // SuccessfulExit=false: "Skjul ikonet" respekteres
        Assert.Equal("Aqua", Val("LimitLoadToSessionType").Value);                           // kun i en rigtig skærmsession
        Assert.Equal("Interactive", Val("ProcessType").Value);
        Assert.Contains(MacPaths.BundleId, Val("AssociatedBundleIdentifiers").Elements("string").Select(e => e.Value));
        Assert.Contains(MacPaths.MenuBundleId, Val("AssociatedBundleIdentifiers").Elements("string").Select(e => e.Value));
        Assert.DoesNotContain("UserName", plist);   // aldrig som root – LaunchAgent'en kører som den bruger, der logger ind

        var app = XDocument.Parse(MacInstall.MenuBarAgentPlist("/Applications/Fleetdeck.app/", nativeMenu: false));
        Assert.Equal(["/Applications/Fleetdeck.app/Contents/MacOS/Fleetdeck", "--tray"],
            app.Descendants("array").First().Elements("string").Select(e => e.Value));
    }

    // ---------------------------------------------------------------- ikonet kører hos brugeren

    private const string Running = """
        gui/501/com.fleetdeck.menubar = {
        	active count = 1
        	path = /Library/LaunchAgents/com.fleetdeck.menubar.plist
        	type = LaunchAgent
        	state = running
        	program = /usr/local/fleetdeck/menu/FleetdeckMenu.app/Contents/MacOS/FleetdeckMenu
        	runs = 1
        	pid = 812
        	last exit code = (never exited)
        }
        """;

    [Fact]
    public void AgentStartsTheMenuWhenItIsNotLoadedOrCrashedButNotWhenTheUserHidIt()
    {
        Assert.Equal(MacMenuLaunch.Action.Bootstrap, MacMenuLaunch.Decide(113, "Could not find service \"com.fleetdeck.menubar\" in domain for user gui: 501"));
        Assert.Equal(MacMenuLaunch.Action.None, MacMenuLaunch.Decide(0, Running));
        Assert.True(MacMenuLaunch.Running(Running));
        var hidden = Running.Replace("state = running", "state = not running").Replace("(never exited)", "0");
        Assert.Equal(MacMenuLaunch.Action.None, MacMenuLaunch.Decide(0, hidden));        // "Skjul ikonet (til næste login)"
        Assert.False(MacMenuLaunch.Running(hidden));
        Assert.Equal(MacMenuLaunch.Action.Kickstart, MacMenuLaunch.Decide(0, Running.Replace("state = running", "state = not running").Replace("(never exited)", "1")));
        Assert.Equal(MacMenuLaunch.Action.Kickstart, MacMenuLaunch.Decide(0, Running.Replace("state = running", "state = not running")));
        Assert.Equal(MacMenuLaunch.Action.Kickstart, MacMenuLaunch.Decide(0, Running.Replace("state = running", "state = spawn scheduled").Replace("(never exited)", "-9")));
    }

    [Theory]
    [InlineData(false, false, false, null, true)]                         // agenten uden konfiguration til en server
    [InlineData(true, false, false, null, false)]                         // tilmeldt
    [InlineData(false, true, false, null, false)]                         // nøglen prøves igen, når serveren kan nås
    [InlineData(false, false, true, null, false)]                         // anmodning sendt, venter
    [InlineData(false, false, true, AccessStates.Pending, false)]
    [InlineData(false, false, true, AccessStates.Approved, false)]
    [InlineData(false, false, true, AccessStates.Rejected, true)]         // afvist: kun opsætningen kommer videre
    [InlineData(false, false, false, AccessStates.Expired, true)]
    public void SetupWindowOpensOnlyWhenTheMacCannotEnrollByItself(bool token, bool pendingKey, bool access, string? state, bool open) =>
        Assert.Equal(open, MacMenuLaunch.NeedsSetup(token, pendingKey, access, state));

    [Fact]
    public void ARejectedKeyOpensTheSetupWindowInsteadOfACrashLoop()
    {
        Assert.True(MacMenuLaunch.NeedsSetup(false, true, false, null, keyRejected: true));
        Assert.False(MacMenuLaunch.NeedsSetup(true, true, false, null, keyRejected: true));   // tilmeldt alligevel
        // tjenesten må ikke gå ned i ring på Mac (så nåede menulinjens løkke aldrig at åbne opsætningen): den venter
        var program = Repo("src/Fleetdeck.Agent.Unix/Program.cs");
        Assert.Contains("MacMenuBar.KeyRejected = true", program);
        Assert.Contains("cfg.AccessState, KeyRejected)", Repo("src/Fleetdeck.Agent.Unix/MacMenuBar.cs"));
    }

    [Fact]
    public void OpeningTheAppShowsSetupWhenTheAgentIsNotEnrolled()
    {
        var notEnrolled = new TrayStatus { Written = Now, Connected = false, Error = "Venter på at kunne tilmelde sig serveren" };
        Assert.True(MacSetup.ShowSetupOnOpen(false, null, Now));                    // ingen agent: installationen
        Assert.True(MacSetup.ShowSetupOnOpen(true, notEnrolled, Now));              // "kun agenten" uden tilmelding
        Assert.False(MacSetup.ShowSetupOnOpen(true, new TrayStatus { Written = Now, Connected = true, Enrolled = true }, Now));
        Assert.False(MacSetup.ShowSetupOnOpen(true, null, Now));                    // tjenesten svarer ikke: status siger det
        var waiting = new TrayStatus { Written = Now, AccessRequest = new TrayAccessRequest { Code = "123-456", State = AccessStates.Pending } };
        Assert.False(MacSetup.ShowSetupOnOpen(true, waiting, Now));                 // venter på administratoren: status med koden
        waiting.AccessRequest.State = AccessStates.Rejected;
        Assert.True(MacSetup.ShowSetupOnOpen(true, waiting, Now));
        Assert.Contains("Systemindstillinger → Menulinje", MacSetup.MenuBarHint);
        Assert.Contains("notch", MacSetup.MenuBarHint);
        Assert.Contains("MacSetup.MenuBarHint", Repo("src/Fleetdeck.MacApp/SetupWindow.cs"));
        Assert.Contains("MacSetup.MenuBarHint", Repo("src/Fleetdeck.MacApp/StatusWindow.cs"));
        Assert.Contains("MacSetup.ShowSetupOnOpen", Repo("src/Fleetdeck.MacApp/Tray.cs"));
    }

    // ---------------------------------------------------------------- appen lægges på af tjenesten

    [Theory]
    [InlineData(false, null, "3.42.0", true, MacAppUpdate.Plan.Install)]        // Terminal-kommandoen / "kun agenten"
    [InlineData(false, null, "3.42.0", false, MacAppUpdate.Plan.WaitForUser)]   // ingen logget ind (Mac uden skærm)
    [InlineData(true, "3.24.1", "3.42.0", true, MacAppUpdate.Plan.Update)]
    [InlineData(true, "3.24.1", "3.42.0", false, MacAppUpdate.Plan.Update)]
    [InlineData(true, "3.42.0", "3.42.0", true, MacAppUpdate.Plan.None)]
    [InlineData(true, null, "3.42.0", true, MacAppUpdate.Plan.None)]           // ukendt version: rør den ikke
    public void ServiceInstallsAMissingAppAndUpdatesAnOldOne(bool exists, string? app, string agent, bool user, MacAppUpdate.Plan plan) =>
        Assert.Equal(plan, MacAppUpdate.Decide(exists, app, agent, user));

    [Fact]
    public void AgentKeepsTheMenuRunningAndOpensSetupFromTheService()
    {
        var sync = Repo("src/Fleetdeck.Agent.Unix/MacAppSync.cs");
        Assert.DoesNotContain("if (!Directory.Exists(MacPaths.InstalledApp)) return;", sync);   // stoppede før, når appen manglede
        Assert.Contains("MacAppUpdate.Decide(", sync);
        Assert.Contains("AgentSigning.Check(Tmp, key, hash, sig)", sync);                         // disk-imaget skal være serverens
        Assert.Contains("FileShare.None", sync);                                                   // "install" og tjenesten på én gang

        var program = Repo("src/Fleetdeck.Agent.Unix/Program.cs");
        var loop = program.IndexOf("MacMenuBar.Loop(cfg, stop.Token)", StringComparison.Ordinal);
        Assert.True(loop > 0 && loop < program.IndexOf("await EnsureEnrolled(cfg, stop.Token)", StringComparison.Ordinal),
            "ikonet og opsætningen skal virke, mens agenten venter på tilmelding");
        // appen lægges på uanset om serverens svar er godkendt (disk-imaget tjekkes selv mod den pinnede nøgle)
        var trusted = program.Split('\n').First(l => l.Contains("if (gate.Trusted)"));
        var tick = program.Split('\n').Single(l => l.Contains("await MacAppSync.Tick("));
        Assert.Equal(Indent(trusted), Indent(tick));   // på niveau med if (gate.Trusted) – ikke inde i blokken
        // install giver altid menulinjen – også uden --menubar (install.sh, "setup")
        Assert.Contains("MacTray.InstallMenuBar(Arg(\"--menubar\") is { Length: > 0 } app ? app : MacPaths.InstalledApp)", program);

        var bar = Repo("src/Fleetdeck.Agent.Unix/MacMenuBar.cs");
        Assert.Contains("launchctl print {target}", bar);
        Assert.Contains("launchctl bootstrap gui/{s.Uid}", bar);
        Assert.Contains("launchctl kickstart {target}", bar);
        Assert.Contains("BootFailed", bar);   // et afvist bootstrap (slået fra under Loginemner) prøves ikke hvert minut
        Assert.Contains("launchctl asuser {s.Uid} sudo -u {Pkg.Q(s.User)} open -n -a {Pkg.Q(MacPaths.InstalledApp)} --args --setup", bar);
        Assert.DoesNotContain("pkill", bar);
        Assert.DoesNotContain("pgrep", bar);
        Assert.Contains("MacMenu = MacMenuBar.Report()", Repo("src/Fleetdeck.Agent.Unix/Mac.cs"));   // meldes i HealthJson
        Assert.Contains("MacTray.InstallMenuBar", program);
        Assert.Contains("MacMenuBar.EnsureRunning(u, report: u.Uid == console?.Uid)", Repo("src/Fleetdeck.Agent.Unix/MacTray.cs"));
    }

    [Fact]
    public void UninstallAlsoStopsTheIconForTheUserOnScreen()
    {
        var s = MacInstall.UninstallScript();
        Assert.Contains("launchctl bootout gui/$U/com.fleetdeck.menubar", s);
        Assert.Contains("loginwindow", s);   // også brugere i baggrunden (hurtigt brugerskift)
        Assert.Contains($"'{MacPaths.AgentDir}'", s);   // menu-appen ligger i agentens mappe og forsvinder med den
        AssertShellSyntax(s);
    }

    // ---------------------------------------------------------------- Swift-menuen

    [Fact]
    public void SwiftMenuStaysVisibleAndOpensTheAppThroughLaunchServices()
    {
        var swift = Repo("src/Fleetdeck.MacMenu/main.swift");
        Assert.Contains("item.autosaveName = ", swift);
        Assert.Contains("item.isVisible = true", swift);
        Assert.Contains("Bundle.main.path(forResource: \"menu-icon@2x\", ofType: \"png\")", swift);
        Assert.Contains("NSWorkspace.shared.openApplication(at:", swift);
        Assert.Contains("cfg.activates = true", swift);
        Assert.Contains("createsNewApplicationInstance = true", swift);   // en kørende app får ordren via sin socket
        Assert.Contains("Fleetdeck.app mangler", swift);
        Assert.DoesNotContain("try? p.run()", swift);                      // fejl blev skjult, og vinduet kunne åbne bagved
        Assert.DoesNotContain("Process()", swift);
    }

    // ---------------------------------------------------------------- install.sh (Terminal-kommandoen)

    [Fact]
    public void InstallScriptIsValidBashAndGivesTheMacTheAppAndTheMenu()
    {
        var sh = Repo("src/Fleetdeck.Server/Templates/install.sh");
        var mac = sh[sh.IndexOf("if [ \"$(uname -s)\" = \"Darwin\" ]; then", StringComparison.Ordinal)..];
        mac = mac[..(mac.IndexOf("\nfi\n", StringComparison.Ordinal) + 4)];
        Assert.Contains("case \"$(uname -m)\" in arm64) ARCH=arm64 ;; *) ARCH=x64 ;; esac", mac);   // Apple Silicon / Intel
        Assert.Contains("sysctl -in hw.optional.arm64", mac);   // Terminal under Rosetta: stadig arm64 på Apple Silicon
        Assert.Contains("mac_app \"$ARCH\"", mac);
        Assert.Contains("APPARG=(--menubar /Applications/Fleetdeck.app)", mac);
        Assert.Contains("${APPARG[@]+\"${APPARG[@]}\"} --service", mac);
        Assert.Contains("WS_NO_APP", mac);
        Assert.Contains("kind=dmg&arch=$1", sh);
        Assert.Contains("xattr -dr com.apple.quarantine", sh);
        if (!File.Exists("/bin/bash")) return;
        var (code, output) = Bash(sh, "-n");
        Assert.True(code == 0, output);
    }

    [Fact]
    public void InstallScriptInstallsTheAppFromTheDiskImageAndFallsBackToAgentOnly()
    {
        if (!File.Exists("/bin/bash") || OperatingSystem.IsWindows()) return;
        var sh = Repo("src/Fleetdeck.Server/Templates/install.sh");
        var start = sh.IndexOf("mac_app() {", StringComparison.Ordinal);
        var fn = sh[start..(sh.IndexOf("\n}\n", start, StringComparison.Ordinal) + 3)];
        var dir = Directory.CreateTempSubdirectory("fd-macapp-").FullName;
        try
        {
            var bin = Directory.CreateDirectory(Path.Combine(dir, "bin")).FullName;
            var apps = Directory.CreateDirectory(Path.Combine(dir, "Applications")).FullName;
            void Stub(string name, string body)
            {
                File.WriteAllText(Path.Combine(bin, name), "#!/bin/bash\n" + body + "\n");
                File.SetUnixFileMode(Path.Combine(bin, name), (UnixFileMode)0b111_101_101);
            }
            // curl: skriver "disk-imaget" (eller fejler som en server uden Mac-appen); hdiutil: "monterer" en mappe med appen
            Stub("curl", $"echo \"$@\" >> '{dir}/curl.log'; [ -n \"${{FAIL_CURL:-}}\" ] && exit 22; while [ $# -gt 0 ]; do [ \"$1\" = -o ] && echo dmg > \"$2\"; shift; done; exit 0");
            Stub("hdiutil", "if [ \"$1\" = attach ]; then while [ $# -gt 0 ]; do [ \"$1\" = -mountpoint ] && mp=$2; shift; done; " +
                            "mkdir -p \"$mp/Fleetdeck.app/Contents/MacOS\"; printf '#!/bin/sh\\n' > \"$mp/Fleetdeck.app/Contents/MacOS/Fleetdeck\"; chmod 755 \"$mp/Fleetdeck.app/Contents/MacOS/Fleetdeck\"; fi; " +
                            "if [ \"$1\" = detach ]; then rm -rf \"$2\"/Fleetdeck.app; fi; exit 0");
            Stub("ditto", "cp -R \"$1\" \"$2\"");
            Stub("xattr", "exit 0");
            string Run(string env) =>
                Bash($"set -euo pipefail\nexport PATH='{bin}':$PATH {env}\nTLS=()\nSERVER=http://srv:8585\n{fn.Replace("/Applications", apps)}\nmac_app arm64 && echo RC=0 || echo RC=$?\n").Output;

            var ok = Run("");
            Assert.Contains("RC=0", ok);
            Assert.True(File.Exists(Path.Combine(apps, "Fleetdeck.app", "Contents", "MacOS", "Fleetdeck")), ok);
            Assert.False(Directory.Exists(Path.Combine(apps, ".Fleetdeck.new.app")));
            Assert.Contains("http://srv:8585/api/agent/download?platform=mac&kind=dmg&arch=arm64", File.ReadAllText(Path.Combine(dir, "curl.log")));

            // serveren har ingen app: agenten installeres alligevel – med den app, der allerede ligger der …
            var keep = Run("FAIL_CURL=1");
            Assert.Contains("RC=0", keep);
            Assert.Contains("kun agenten installeres nu", keep);
            Assert.True(Directory.Exists(Path.Combine(apps, "Fleetdeck.app")));
            // … eller uden (så sendes --menubar ikke, og tjenesten lægger appen på senere)
            Directory.Delete(Path.Combine(apps, "Fleetdeck.app"), true);
            Assert.Contains("RC=1", Run("FAIL_CURL=1"));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------------------------------------------------------------- CI: menuen kommer med i pakken

    [Fact]
    public void ReleaseChecksTheMenuAppWithApplesToolsAndPacksTheMenu()
    {
        var wf = Repo(".github/workflows/release-image.yml");
        Assert.Contains("cp src/Fleetdeck.MacMenu/Info.plist \"$T/Contents/Info.plist\"", wf);
        Assert.Contains("codesign --verify --deep --strict", wf);
        Assert.Contains("plutil -lint src/Fleetdeck.MacMenu/Info.plist", wf);
        Assert.Contains("name: fleetdeckmenu", wf);
        Assert.Contains("path: src/Fleetdeck.MacMenu/build/", wf);
        var df = Repo("Dockerfile");
        Assert.Contains("--menu $M/arm64", df);
        Assert.Contains("--menu $M/x64", df);
        Assert.Contains("REQUIRE_MAC_MENU", df);
        // MacPack lægger menuen dér, hvor agenten henter den
        Assert.Contains("Path.Combine(resourcesDir, \"menu\")", Repo("src/Fleetdeck.MacPack/AppBundle.cs"));
        Assert.EndsWith("/Contents/Resources/menu", MacPaths.BundledMenuDir);
    }

    // ---------------------------------------------------------------- hjælpere

    private static int Indent(string line) => line.Length - line.TrimStart().Length;

    private static (int Code, string Output) Bash(string script, string? flag = null)
    {
        var psi = new ProcessStartInfo("/bin/bash") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        if (flag is not null) psi.ArgumentList.Add(flag);
        psi.ArgumentList.Add("-s");
        using var p = Process.Start(psi)!;
        p.StandardInput.Write(script);
        p.StandardInput.Close();
        var outTask = p.StandardOutput.ReadToEndAsync();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, outTask.Result + err);
    }

    private static void AssertShellSyntax(string script)
    {
        if (!File.Exists("/bin/sh")) return;
        var p = Process.Start(new ProcessStartInfo("/bin/sh", ["-n", "-c", script]) { RedirectStandardError = true })!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"sh -n: {err}\n{script}");
    }
}
