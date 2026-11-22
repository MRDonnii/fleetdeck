using System.Text.Json;
using Fleetdeck.Shared;

namespace Fleetdeck.Tests;

/// <summary>
/// Mac: ikonet hos alle loginsessioner (også hurtigt brugerskift), den native menu respekterer "Vis Fleetdeck ved login",
/// WS_NO_APP=1 huskes, og "Gem diagnose…" samler det, et diagnosejob ellers skulle hente – uden hemmeligheder.
/// </summary>
public sealed class MacSessionsDiagnoseTests
{
    private static string Repo(string rel)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Fleetdeck.slnx"))) d = d.Parent;
        return File.ReadAllText(Path.Combine(d!.FullName, rel));
    }

    private const string Ps = """
          0 /sbin/launchd
          0 /System/Library/CoreServices/loginwindow.app/Contents/MacOS/loginwindow
        248 /System/Library/CoreServices/loginwindow.app/Contents/MacOS/loginwindow
        501 /System/Library/CoreServices/loginwindow.app/Contents/MacOS/loginwindow
        501 /System/Library/CoreServices/Dock.app/Contents/MacOS/Dock
        502 /System/Library/CoreServices/loginwindow.app/Contents/MacOS/loginwindow
        503 /Applications/loginwindow-helper
        123456789 /System/Library/CoreServices/loginwindow.app/Contents/MacOS/loginwindow
        """;

    [Fact]
    public void All_login_sessions_are_found_with_the_console_user_first()
    {
        Assert.Equal([502, 501, 123456789], MacLoginSessions.Uids(Ps, 502));
        Assert.Equal([501, 502, 123456789], MacLoginSessions.Uids(Ps, 501));
        // ingen ved skærmen (login-skærmen vises), men brugere i baggrunden: de får stadig ikonet
        Assert.Equal([501, 502, 123456789], MacLoginSessions.Uids(Ps, null));
        Assert.Empty(MacLoginSessions.Uids("", null));
        Assert.Empty(MacLoginSessions.Uids("  0 /System/Library/CoreServices/loginwindow.app/Contents/MacOS/loginwindow", 0));
        Assert.Contains("loginwindow", Repo("src/Fleetdeck.Shared/MacApp.cs"));
    }

    [Fact]
    public void The_service_starts_the_icon_for_every_session_not_only_the_console()
    {
        var bar = Repo("src/Fleetdeck.Agent.Unix/MacMenuBar.cs");
        Assert.Contains("foreach (var u in Mac.Sessions(s)) EnsureRunning(u, report: u.Uid == s?.Uid);", bar);
        Assert.Contains("foreach (var s in Mac.Sessions(console))", bar);   // Restart: ny app/menu hos alle
        // genstart og afviste forsøg huskes pr. bruger (ellers ville to brugere skiftes til at nulstille hinanden)
        Assert.Contains("Dictionary<int, DateTime> Kicked = [], BootFailed = []", bar);
        Assert.DoesNotContain("_kickedUid", bar);
        Assert.Contains("foreach (var u in Mac.Sessions(console)) MacMenuBar.EnsureRunning(u", Repo("src/Fleetdeck.Agent.Unix/MacTray.cs"));
        var mac = Repo("src/Fleetdeck.Agent.Unix/Mac.cs");
        Assert.Contains("MacLoginSessions.Uids(Shell.Out(MacLoginSessions.PsCommand), console?.Uid)", mac);
        // kun helbredsrapporten følger brugeren ved skærmen
        Assert.Contains("if (report) MacMenuBar.Remember(user, running, detail);", bar);
    }

    [Fact]
    public void Native_menu_respects_show_at_login()
    {
        var swift = Repo("src/Fleetdeck.MacMenu/main.swift");
        var check = swift.IndexOf("if autostartOff() { exit(0) }", StringComparison.Ordinal);
        Assert.True(check > 0);
        Assert.True(check < swift.IndexOf("let app = NSApplication.shared", StringComparison.Ordinal));   // før ikonet vises
        Assert.Contains("/Library/Application Support/Fleetdeck/settings.json", swift);
        Assert.Contains("(obj[\"autostart\"] as? Bool) == false", swift);
        // samme nøgle, som appen skriver (Settings.Autostart med TrayCommands.Json)
        Assert.Contains("public bool Autostart { get; set; } = true;", Repo("src/Fleetdeck.MacApp/MacSys.cs"));
        Assert.Contains("JsonSerializer.Serialize(this, TrayCommands.Json)", Repo("src/Fleetdeck.MacApp/MacSys.cs"));
        Assert.Equal("{\"autostart\":false}", JsonSerializer.Serialize(new { Autostart = false }, TrayCommands.Json));
        // exit 0 = "skjult af brugeren": tjenesten genstarter den ikke
        Assert.Equal(MacMenuLaunch.Action.None, MacMenuLaunch.Decide(0, "state = not running\nlast exit code = 0\n"));
        // ny adfærd: nyt stempel, så en ældre menu (uden tjekket) regnes for forældet og fjernes
        Assert.Equal("fleetdeck-menu-api=3", MacMenuStamp.Marker);
        Assert.False(MacMenuStamp.IsCurrent(System.Text.Encoding.ASCII.GetBytes("fleetdeck-menu-api=2")));
    }

    [Fact]
    public void No_app_is_remembered_so_the_service_does_not_install_the_app_later()
    {
        Assert.True(MacAppUpdate.NoApp(noAppFlag: true, withApp: false, before: false));
        Assert.False(MacAppUpdate.NoApp(false, withApp: true, before: true));      // installeret med appen: glemt igen
        Assert.True(MacAppUpdate.NoApp(false, withApp: false, before: true));      // geninstallation uden app (fx kunne ikke hentes): huskes
        Assert.False(MacAppUpdate.NoApp(false, false, false));
        Assert.Equal(MacAppUpdate.Plan.None, MacAppUpdate.Decide(false, null, "3.42.0", someoneLoggedIn: true, noApp: true));
        Assert.Equal(MacAppUpdate.Plan.None, MacAppUpdate.Decide(false, null, "3.42.0", someoneLoggedIn: false, noApp: true));
        Assert.Equal(MacAppUpdate.Plan.Install, MacAppUpdate.Decide(false, null, "3.42.0", someoneLoggedIn: true));
        // en app, nogen selv har lagt på, holdes stadig opdateret
        Assert.Equal(MacAppUpdate.Plan.Update, MacAppUpdate.Decide(true, "3.41.0", "3.42.0", true, noApp: true));

        var sh = Repo("src/Fleetdeck.Server/Templates/install.sh");
        Assert.Contains("if [ -n \"${WS_NO_APP:-}\" ]; then APPARG=(--no-app); elif mac_app \"$ARCH\"; then APPARG=(--menubar /Applications/Fleetdeck.app); fi", sh);
        var prog = Repo("src/Fleetdeck.Agent.Unix/Program.cs");
        Assert.Contains("cfg.NoApp = MacAppUpdate.NoApp(args.Contains(\"--no-app\"), Arg(\"--menubar\") is { Length: > 0 }, old?.NoApp ?? false);", prog);
        Assert.Contains("public bool NoApp { get; set; }", prog);
        Assert.Contains("Mac.Session() is not null, cfg.NoApp)", Repo("src/Fleetdeck.Agent.Unix/MacAppSync.cs"));
    }

    private static MacDiagnose.Input Sample(string? log = null, (int, string)? menu = null) => new()
    {
        At = new DateTime(2026, 11, 21, 10, 30, 0, DateTimeKind.Utc), AppVersion = "3.42.0", Os = "Unix 15.1.0", Arch = "Arm64", User = "testbruger", Uid = 501,
        AgentInstalled = true, LoginItem = true, NativeMenu = true, AutostartAtLogin = true,
        Status = new TrayStatus { Machine = "TEST-MAC", Server = "https://fleet.example.test", Connected = true, Enrolled = true, Version = "3.42.0", Profile = "Standard" },
        MenuLaunchctl = menu ?? (0, "gui/501/com.fleetdeck.menubar = {\n\tstate = running\n\tpid = 812\n\tpath = /Library/LaunchAgents/com.fleetdeck.menubar.plist\n}"),
        AgentLaunchctl = (0, "system/com.fleetdeck.agent = {\n\tstate = running\n\tprogram = /usr/local/fleetdeck/fleetdeck-agent\n}"),
        AgentLog = log ?? "2026-11-21 10:29:01 INFO Tjekket ind\n2026-11-21 10:29:31 INFO Tjekket ind",
    };

    [Fact]
    public void Diagnose_collects_versions_icon_state_launchctl_and_the_log()
    {
        var text = MacDiagnose.Build(Sample());
        Assert.Contains("Fleetdeck.app             : 3.42.0", text);
        Assert.Contains("Ikonet kører              : ja", text);
        Assert.Contains($"Native menu               : ja ({MacMenuStamp.Marker})", text);
        Assert.Contains("== launchctl print gui/501/com.fleetdeck.menubar (kode 0) ==", text);
        Assert.Contains("== launchctl print system/com.fleetdeck.agent (kode 0) ==", text);
        Assert.Contains("path = /Library/LaunchAgents/com.fleetdeck.menubar.plist", text);   // stier skjules ikke
        Assert.Contains("program = /usr/local/fleetdeck/fleetdeck-agent", text);
        Assert.Contains("10:29:31 INFO Tjekket ind", text);
        Assert.Contains("Server                    : https://fleet.example.test", text);
        Assert.Equal("Fleetdeck-diagnose-2026-11-21-1030.txt", MacDiagnose.FileName(new DateTime(2026, 11, 21, 10, 30, 0)));
        Assert.Equal(["print", "gui/501/com.fleetdeck.menubar"], MacDiagnose.MenuArgs(501));
        Assert.Equal(["print", "system/com.fleetdeck.agent"], MacDiagnose.AgentArgs);
    }

    [Fact]
    public void Diagnose_explains_why_the_icon_is_not_running()
    {
        var off = MacDiagnose.Build(Sample(menu: (113, "Could not find service \"com.fleetdeck.menubar\" in domain for user gui: 501")));
        Assert.Contains("Ikonet kører              : nej", off);
        Assert.Contains("ikke indlæst for brugeren", off);
        var hidden = MacDiagnose.Build(Sample(menu: (0, "state = not running\nlast exit code = 0\n")) with { });
        Assert.Contains("lukket af brugeren eller \"Vis ved login\" er slået fra (kode 0)", hidden);
        Assert.Contains("sidst afsluttet med 78", MacDiagnose.MenuState((0, "state = not running\nlast exit code = 78: EX_CONFIG\n")));
        Assert.True(MacDiagnose.IconRunning(Sample(menu: (113, "")) with { NativeMenuProcess = true }));
        Assert.Contains("Vis ved login (brugeren)  : fra", MacDiagnose.Build(Sample() with { AutostartAtLogin = false }));
        Assert.Contains("kan ikke læses som", MacDiagnose.Build(Sample() with { AgentLog = null, AgentLogError = "kan ikke læses som testbruger" }));
    }

    [Fact]
    public void Redact_hides_unnamed_short_keys_but_keeps_words_with_pin()
    {
        // en tilmeldingsnøgle (24 tegn base64url) uden key= foran, og en tlsPin i JSON
        var text = MacDiagnose.Redact("Kopierede aZ3kP9qL0wX7mN2bV5cR8tY1 fra udklipsholderen · \"tlsPin\": \"sha256//abc\" · tls_pin=xyz");
        Assert.DoesNotContain("aZ3kP9qL0wX7mN2bV5cR8tY1", text);
        Assert.DoesNotContain("sha256//abc", text);
        Assert.DoesNotContain("xyz", text);
        // almindelig diagnose med "pin" inde i ordet og maskinbetegnelser står der stadig
        const string plain = "mapping: 3 · spinning=ja · Spinner: 2 · x86_64-apple-darwin23 · MacBookPro18,3 · version 3.41.3";
        Assert.Equal(plain, MacDiagnose.Redact(plain));
    }

    [Fact]
    public void Diagnose_contains_no_secrets()
    {
        const string secretLog = """
            INFO install --server https://fleet.example.test --key FDK-abc123-SECRET --tls-pin sha256//AAAA
            INFO Hentede https://fleet.example.test/install.sh?key=TOPSECRET123&x=1 og https://fleet.example.test/hent#k=ALSOSECRET
            DEBUG Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.payload.sig
            DEBUG {"token": "tok en with spaces", "signingKey":"MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE", "enroll_key": abcdef}
            DEBUG password=hunter2 jobKey: Zm9vYmFyYmF6cXV4MTIzNDU2Nzg5MDEyMzQ1Ng
            DEBUG tilfældig 3f9a8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1a
            INFO state = running · pid = 1234 · version 3.41.3 · com.fleetdeck.menubar · /usr/local/fleetdeck/menu/FleetdeckMenu.app/Contents/MacOS/FleetdeckMenu
            """;
        var text = MacDiagnose.Build(Sample(secretLog));
        foreach (var s in new[] { "FDK-abc123-SECRET", "sha256//AAAA", "TOPSECRET123", "ALSOSECRET", "eyJhbGciOiJIUzI1NiJ9", "tok en with spaces",
                     "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE", "abcdef}", "hunter2", "Zm9vYmFyYmF6cXV4MTIzNDU2Nzg5MDEyMzQ1Ng", "3f9a8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1a" })
            Assert.DoesNotContain(s, text);
        Assert.Contains("--key [skjult]", text);
        Assert.Contains("?key=[skjult]&x=1", text);
        Assert.Contains("\"token\": \"[skjult]\"", text);
        // det, der skal kunne læses, står der stadig
        Assert.Contains("state = running · pid = 1234 · version 3.41.3 · com.fleetdeck.menubar · /usr/local/fleetdeck/menu/FleetdeckMenu.app/Contents/MacOS/FleetdeckMenu", text);
        Assert.Contains("https://fleet.example.test", text);
        // Redact er stabil (kan køres flere gange)
        Assert.Equal(text, MacDiagnose.Redact(text));
    }

    [Fact]
    public void Diagnose_keeps_only_the_newest_log_lines()
    {
        var log = string.Join('\n', Enumerable.Range(1, MacDiagnose.LogLines + 50).Select(i => $"linje {i}"));
        var t = MacDiagnose.Tail(log, MacDiagnose.LogLines);
        Assert.StartsWith("… (50 ældre linjer udeladt)", t);
        Assert.DoesNotContain("linje 50\n", t);
        Assert.EndsWith($"linje {MacDiagnose.LogLines + 50}", t);
        Assert.Equal("a\nb", MacDiagnose.Tail("a\r\nb\n", 10));
    }

    [Fact]
    public void The_installed_overview_has_the_diagnose_button()
    {
        var w = Repo("src/Fleetdeck.MacApp/SetupWindow.cs");
        var overview = w[w.IndexOf("case \"overview\":", StringComparison.Ordinal)..w.IndexOf("case \"server\":", StringComparison.Ordinal)];
        Assert.Contains("Mac.Btn(SetupTexts.SaveDiagnose", overview);
        Assert.Contains("await SaveDiagnose()", overview);
        var d = Repo("src/Fleetdeck.MacApp/Diagnose.cs");
        Assert.Contains("MacDiagnose.Build(", d);
        Assert.Contains("MacSys.Reveal(path)", d);
        Assert.DoesNotContain("pgrep", d);
        Assert.DoesNotContain("pkill", d);
        Assert.DoesNotContain("Elevated", d);   // ingen adgangskode: alt læses som brugeren
        Assert.DoesNotContain(MacPaths.ConfigDir, d);   // /etc/fleetdeck (nøgler og token) læses aldrig
    }
}
