using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fleetdeck.Shared;

/// <summary>
/// Mac-appen (Fleetdeck.app i menulinjen) ↔ agenten (launchd-tjenesten som root). Samme kontrakt som ikonet i Windows'
/// proceslinje: tjenesten skriver <see cref="TrayStatus"/> til en fil, alle må læse; ordrer kommer som små JSON-filer.
/// Brugerens mappe (alle må lægge filer, ingen kan se andres) tager kun "opdatér nu", "synkronisér", "udsæt" og "tjek adgang";
/// administrator-ordrer (pause, udvidelser) lægges i roots egen mappe af agenten selv, startet med administratoradgangskoden.
/// </summary>
public static class MacPaths
{
    public const string AppName = "Fleetdeck";
    public const string BundleId = "com.fleetdeck.app";
    public const string AgentLabel = "com.fleetdeck.agent";
    public const string MenuBarLabel = "com.fleetdeck.menubar";
    public const string AgentDir = "/usr/local/fleetdeck";
    public const string AgentExe = AgentDir + "/fleetdeck-agent";
    public const string AdminCmdDir = AgentDir + "/cmd";
    public const string DaemonPlist = "/Library/LaunchDaemons/" + AgentLabel + ".plist";
    /// <summary>
    /// Den native menulinje-hjælper (Swift, NSStatusItem) som en rigtig app-pakke med eget bundle-id (<see cref="MenuBundleId"/>):
    /// macOS husker så ikonets plads og viser "Fleetdeck" under Systemindstillinger → Menulinje / Loginemner. Agenten bygger pakken
    /// af binæren fra Fleetdeck.app (<see cref="BundledMenuDir"/>) og signerer den lokalt.
    /// </summary>
    public const string NativeMenuApp = "/usr/local/fleetdeck/menu/FleetdeckMenu.app";
    /// <summary>Programmet i <see cref="NativeMenuApp"/> – ejer ikonet, når det er installeret med det rigtige stempel.</summary>
    public const string NativeMenu = NativeMenuApp + "/Contents/MacOS/FleetdeckMenu";
    /// <summary>Før 3.42: menuen som en løs binær uden pakke (ingen bundle-id, så macOS kunne gemme ikonet uden at kunne slå det til igen).</summary>
    public const string LegacyNativeMenu = "/usr/local/fleetdeck/menu/FleetdeckMenu";
    public const string MenuBundleId = "com.fleetdeck.menu";
    /// <summary>Den native menu, som den følger med Fleetdeck.app (bygget af CI på macOS); agenten lægger den i <see cref="NativeMenu"/>.</summary>
    public const string BundledMenuDir = "/Applications/Fleetdeck.app/Contents/Resources/menu";
    public const string MenuBarPlist = "/Library/LaunchAgents/" + MenuBarLabel + ".plist";
    public const string SupportDir = "/Library/Application Support/Fleetdeck";
    public const string StatusFile = SupportDir + "/status.json";
    public const string UserCmdDir = SupportDir + "/cmd";
    /// <summary>Beskeder (helpdesk): samtalen, som tjenesten skriver og appen viser (som userwork\helpdesk.json i Windows).</summary>
    public const string HelpdeskFile = SupportDir + "/helpdesk.json";
    public const string ConfigDir = "/etc/fleetdeck";
    public const string Log = "/var/log/fleetdeck-agent.log";
    public const string InstalledApp = "/Applications/Fleetdeck.app";
    public const string AppExecutable = "Contents/MacOS/Fleetdeck";
    /// <summary>Systemindstillinger → Anonymitet og sikkerhed → Fuld diskadgang.</summary>
    public const string FullDiskAccessPane = "x-apple.systempreferences:com.apple.preference.security?Privacy_AllFiles";
    public const string SoftwareUpdatePane = "x-apple.systempreferences:com.apple.Software-Update-Settings.extension";
}

/// <summary>
/// Versionsstemplet i den native menu (src/Fleetdeck.MacMenu/main.swift): binæren indeholder <see cref="Marker"/> som tekst.
/// Hæv tallet i <see cref="Marker"/> (begge steder), når menuen får nye punkter, appen eller tjenesten skal kunne regne med.
/// En menu uden det rigtige stempel er forældet: appen viser så sin egen menu, og tjenesten fjerner den gamle.
/// </summary>
public static class MacMenuStamp
{
    public const string Marker = "fleetdeck-menu-api=3";

    public static bool IsCurrent(byte[] binary) => binary.AsSpan().IndexOf(Encoding.ASCII.GetBytes(Marker)) >= 0;

    public static bool IsCurrent(string path)
    {
        try { return File.Exists(path) && IsCurrent(File.ReadAllBytes(path)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}

/// <summary>
/// Den native menu som app-pakke (<see cref="MacPaths.NativeMenuApp"/>). Fleetdeck.app har binæren og ikonet i Resources/menu;
/// agenten (root) bygger pakken med Info.plist, signerer den ad hoc på Mac'en og lægger den i /usr/local/fleetdeck/menu
/// (kun root kan skrive dér). Kildens SHA-256 gemmes i pakken, så den kun bygges om, når appen har en anden menu.
/// </summary>
public static class MacMenuBundle
{
    public const string Executable = "FleetdeckMenu";
    public const string Icon = "menu-icon@2x.png";
    /// <summary>Kildebinærens SHA-256 (Contents/Resources/…) – codesign ændrer selve programmet, så det kan ikke sammenlignes direkte.</summary>
    public const string SourceHashFile = "fleetdeck-source.sha256";

    /// <summary>Info.plist: bundle-id, LSUIElement (intet Dock-ikon) og navnet "Fleetdeck" i Systemindstillinger.</summary>
    public static string InfoPlist() => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0"><dict>
          <key>CFBundleIdentifier</key><string>{MacPaths.MenuBundleId}</string>
          <key>CFBundleName</key><string>Fleetdeck</string>
          <key>CFBundleDisplayName</key><string>Fleetdeck</string>
          <key>CFBundleExecutable</key><string>{Executable}</string>
          <key>CFBundlePackageType</key><string>APPL</string>
          <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
          <key>CFBundleShortVersionString</key><string>{StampVersion}</string>
          <key>CFBundleVersion</key><string>{StampVersion}</string>
          <key>LSUIElement</key><true/>
          <key>LSMinimumSystemVersion</key><string>12.0</string>
          <key>NSHighResolutionCapable</key><true/>
        </dict></plist>
        """;

    /// <summary>Tallet i <see cref="MacMenuStamp.Marker"/> (fx "2") – ændrer sig kun, når menuen gør.</summary>
    public static string StampVersion => MacMenuStamp.Marker[(MacMenuStamp.Marker.LastIndexOf('=') + 1)..];

    /// <summary>Skal pakken bygges (om)? Ja, når den mangler, kilden er en anden, eller Info.plist er ændret.</summary>
    public static bool NeedsBuild(string? recordedSourceHash, string sourceHash, string? installedInfoPlist) =>
        recordedSourceHash?.Trim() != sourceHash || installedInfoPlist != InfoPlist();

    /// <summary>
    /// Som root: signér den nye pakke (ad hoc, med bundle-id'et – så er Info.plist bundet ind), gør den root-ejet og skrivebeskyttet
    /// for andre, og byt den ind. En løs FleetdeckMenu fra før 3.42 og dens ikon fjernes. Fejler signeringen, bruges CI's signatur.
    /// </summary>
    public static string InstallScript(string staged) =>
        "set -e; " +
        // udvidede attributter (fx Finder-info, karantæne) kopieret med fra appen får codesign til at afvise pakken
        $"xattr -cr {MacInstall.Sh(staged)} 2>/dev/null || true; " +
        $"codesign --force --sign - --identifier {MacPaths.MenuBundleId} {MacInstall.Sh(staged)} >/dev/null 2>&1 || true; " +
        $"chown -R root:wheel {MacInstall.Sh(staged)}; chmod -R go-w {MacInstall.Sh(staged)}; chmod 755 {MacInstall.Sh(staged + "/Contents/MacOS/" + Executable)}; " +
        $"rm -rf {MacInstall.Sh(MacPaths.NativeMenuApp)}; mv {MacInstall.Sh(staged)} {MacInstall.Sh(MacPaths.NativeMenuApp)}; " +
        $"rm -f {MacInstall.Sh(MacPaths.LegacyNativeMenu)} {MacInstall.Sh(Path.GetDirectoryName(MacPaths.LegacyNativeMenu) + "/" + Icon)}; echo ok";
}

/// <summary>
/// Kører menulinjen i brugerens session? Svaret fra "launchctl print gui/&lt;uid&gt;/com.fleetdeck.menubar" afgør, hvad agenten gør:
/// ikke indlæst (fx installeret, mens brugeren var logget ind) → bootstrap; gået ned → kickstart; afsluttet af brugeren
/// ("Skjul ikonet", exit 0) → intet, til næste login.
/// </summary>
/// <summary>
/// Alle, der er logget ind på Mac'en – også brugere i baggrunden ved hurtigt brugerskift, ikke kun den ved skærmen. Hver
/// loginsession har sin egen loginwindow-proces, der kører som brugeren (uid 0 er login-skærmen selv).
/// </summary>
public static class MacLoginSessions
{
    /// <summary>Kommandoen, hvis output <see cref="Uids"/> læser.</summary>
    public const string PsCommand = "ps -axo uid=,comm=";

    /// <summary>Brugerne (uid) med en loginwindow-proces, den ved skærmen først. Systemkonti (under 500, fx _mbsetupuser) springes over.</summary>
    public static List<int> Uids(string psOutput, int? consoleUid)
    {
        var list = new List<int>();
        if (consoleUid is int c && c >= 500) list.Add(c);
        foreach (var line in psOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !int.TryParse(parts[0], out var uid) || uid < 500) continue;
            if (Path.GetFileName(parts[1]) != "loginwindow") continue;
            if (!list.Contains(uid)) list.Add(uid);
        }
        return list;
    }
}

public static partial class MacMenuLaunch
{
    public enum Action { None, Bootstrap, Kickstart }

    public static Action Decide(int printExit, string printOutput)
    {
        if (printExit != 0) return Action.Bootstrap;
        if (Running(printOutput)) return Action.None;
        var exit = LastExitRx().Match(printOutput);
        if (exit.Success && exit.Groups[1].Value.Trim() == "0") return Action.None;   // brugeren valgte "Skjul ikonet"
        return Action.Kickstart;
    }

    public static bool Running(string printOutput) => StateRx().Match(printOutput) is { Success: true } m && m.Groups[1].Value.Trim() == "running";

    [GeneratedRegex(@"^\s*state\s*=\s*(.+)$", RegexOptions.Multiline)] private static partial Regex StateRx();
    [GeneratedRegex(@"^\s*last exit code\s*=\s*(.+)$", RegexOptions.Multiline)] private static partial Regex LastExitRx();

    /// <summary>
    /// Opsætningsvinduet åbnes af tjenesten (for brugeren på skærmen), når Mac'en ikke kan blive tilmeldt af sig selv:
    /// intet token, ingen nøgle at prøve igen med (eller en afvist nøgle) og ingen åben "Anmod om adgang" (afvist, udløbet eller aldrig sendt).
    /// </summary>
    public static bool NeedsSetup(bool hasToken, bool hasPendingKey, bool hasAccessRequest, string? accessState, bool keyRejected = false) =>
        !hasToken && (!hasPendingKey || keyRejected) && !(hasAccessRequest && (accessState is null || AccessStates.Open(accessState)));
}

/// <summary>Fleetdeck.app følger agentens version: tjenesten henter disk-imaget fra serveren, når appen er ældre.</summary>
public static partial class MacAppUpdate
{
    /// <summary>CFBundleShortVersionString fra Info.plist (XML).</summary>
    public static string? BundleVersion(string infoPlist) =>
        BundleVersionRx().Match(infoPlist) is { Success: true } m ? m.Groups[1].Value.Trim() : null;

    /// <summary>"3.36.0-beta.1+abc" → 3.36.0 (forudgivelse og byggedata tæller ikke med).</summary>
    public static Version? Parse(string? v) =>
        v is null ? null : Version.TryParse(v.Split('+')[0].Split('-')[0].Trim(), out var r) ? r : null;

    /// <summary>Appen skal opdateres, når den er installeret og ældre end agenten (aldrig ned i version, aldrig ved ukendte versioner).</summary>
    public static bool Needed(string? installedApp, string? agent) =>
        Parse(installedApp) is { } a && Parse(agent) is { } g && a < g;

    public enum Plan { None, Install, Update, WaitForUser }

    /// <summary>
    /// Hvad tjenesten gør med Fleetdeck.app: mangler den (Mac'en er sat op med Terminal-kommandoen eller "kun agenten"), lægges
    /// den på, så snart nogen er logget ind – ellers venter den (en Mac uden skærm og bruger får ingen app). En ældre app opdateres.
    /// </summary>
    /// <param name="noApp">Installeret med WS_NO_APP=1 ("kun agenten"): en manglende app lægges ikke på (en app, nogen selv har lagt på, opdateres stadig).</param>
    public static Plan Decide(bool appExists, string? installedApp, string? agent, bool someoneLoggedIn, bool noApp = false) =>
        !appExists ? (noApp ? Plan.None : someoneLoggedIn ? Plan.Install : Plan.WaitForUser)
        : Needed(installedApp, agent) ? Plan.Update : Plan.None;

    /// <summary>
    /// Huskes "kun agenten" (WS_NO_APP=1 → install --no-app)? --no-app slår det til; en installation med appen (--menubar, fra
    /// opsætningsvinduet eller install.sh med appen) slår det fra; ellers gælder det, der var valgt før (fx en geninstallation, hvor
    /// appen ikke kunne hentes).
    /// </summary>
    public static bool NoApp(bool noAppFlag, bool withApp, bool before) => noAppFlag || (!withApp && before);

    /// <summary>
    /// Læg Fleetdeck.app fra disk-imaget i /Programmer: montér skrivebeskyttet, kopiér ved siden af, byt om, afmontér.
    /// Den gamle app ryddes først væk, når den nye ligger klar (en fejl undervejs efterlader den gamle).
    /// </summary>
    public static string InstallScript(string dmg)
    {
        // skjulte mellemmapper (punktum foran), så Finder ikke viser to Fleetdeck-apps imens
        string app = MacPaths.InstalledApp, fresh = "/Applications/.Fleetdeck.new.app", old = "/Applications/.Fleetdeck.old.app";
        return "set -e; M=$(mktemp -d /tmp/fleetdeck-dmg.XXXXXX); " +
               "trap 'hdiutil detach \"$M\" -force >/dev/null 2>&1 || true; rmdir \"$M\" 2>/dev/null || true' EXIT; " +
               $"hdiutil attach {Sh(dmg)} -nobrowse -readonly -noautoopen -mountpoint \"$M\" >/dev/null; " +
               "test -x \"$M/Fleetdeck.app/Contents/MacOS/Fleetdeck\"; " +
               $"rm -rf {Sh(fresh)}; ditto \"$M/Fleetdeck.app\" {Sh(fresh)}; " +
               $"xattr -dr com.apple.quarantine {Sh(fresh)} 2>/dev/null || true; " +
               $"rm -rf {Sh(old)}; if [ -d {Sh(app)} ]; then mv {Sh(app)} {Sh(old)}; fi; " +
               $"mv {Sh(fresh)} {Sh(app)}; rm -rf {Sh(old)}; echo ok";
    }

    private static string Sh(string s) => MacInstall.Sh(s);

    [GeneratedRegex(@"<key>CFBundleShortVersionString</key>\s*<string>([^<]+)</string>")] private static partial Regex BundleVersionRx();
}

/// <summary>Ordrer fra ikonet: hvilke må alle brugere give, og hvordan skrives/læses de.</summary>
public static class TrayCommands
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Må komme fra brugermappen (uden administrator). Pause og udvidelser kræver administrator. Beskeder: svar, "set" og "send logfiler".
    /// "Hold vågen på strøm" må alle slå til og fra (den virker kun på strøm; på batteri sover Mac'en altid normalt).</summary>
    public static bool UserAllowed(string? action) => action is "upgrade_all" or "sync" or "power_postpone" or "power_now" or "recheck_access" or "keepawake"
        or "helpdesk_reply" or "helpdesk_read" or "helpdesk_logs";

    public static string Serialize(TrayCommand c) => JsonSerializer.Serialize(c, Json);

    public static TrayCommand? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<TrayCommand>(json, Json) is { Action.Length: > 0 } c ? c : null; }
        catch (JsonException) { return null; }
    }

    /// <summary>"tray-cmd &lt;base64&gt;" – base64 kan stå i en kommandolinje uden citering.</summary>
    public static string Encode(TrayCommand c) => Convert.ToBase64String(Encoding.UTF8.GetBytes(Serialize(c)));
    public static TrayCommand? Decode(string b64)
    {
        try { return Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64))); }
        catch (FormatException) { return null; }
    }
}

/// <summary>Det, ikonet og statusvinduet viser – ud fra tjenestens status (testbart uden en Mac).</summary>
public static class TrayView
{
    public enum State { NoService, Offline, Power, Job, Attention, Ok }

    public static readonly TimeSpan Stale = TimeSpan.FromMinutes(5);

    public static State Of(TrayStatus? s, DateTime nowUtc)
    {
        if (s is null || nowUtc - s.Written > Stale) return State.NoService;
        if (!s.Connected) return State.Offline;
        if (s.PowerAt is { } p && p > nowUtc) return State.Power;
        if (s.Job is not null) return State.Job;
        if (s.Alerts > 0 || s.PausedUntil > nowUtc || NeedsFullDiskAccess(s) || s.Security is { Length: > 0 }) return State.Attention;
        return State.Ok;
    }

    /// <summary>Farven på prikken (rød, blå, orange, grøn) som i Windows.</summary>
    public static string Color(State st) => st switch
    {
        State.NoService or State.Offline => "#dc2626",
        State.Job => "#2563eb",
        State.Attention or State.Power => "#d97706",
        _ => "#16a34a",
    };

    /// <summary>Fuld diskadgang mangler (null = kunne ikke afgøres: ingen advarsel).</summary>
    public static bool NeedsFullDiskAccess(TrayStatus? s) => s?.MacAccess?.FullDiskAccess == false;

    public static string Headline(TrayStatus? s, DateTime nowUtc) => s?.AccessRequest is { } ar && Of(s, nowUtc) != State.NoService ? ar.Text : Of(s, nowUtc) switch
    {
        State.NoService => s is null ? "Fleetdeck-tjenesten kører ikke" : "Fleetdeck-tjenesten svarer ikke",
        State.Offline => "Ingen forbindelse til serveren",
        State.Power => $"Mac'en {(s!.PowerMode == JobTypes.Reboot ? "genstarter" : "slukker")} kl. {s.PowerAt!.Value.ToLocalTime():HH:mm}",
        State.Job => s!.Job!.Title,
        State.Attention when s!.Security is { Length: > 0 } sec => $"Sikkerhed: {sec}",
        _ => $"Forbundet{(s!.Profile is { Length: > 0 } p ? $" · profil {p}" : "")}",
    };

    public static string Health(string? h) => h switch { "ok" => "Sund", "warning" => "Advarsel", "critical" => "Kritisk", _ => "–" };

    public static string Ago(DateTime utc, DateTime nowUtc)
    {
        var d = nowUtc - utc;
        return d.TotalSeconds < 60 ? "lige nu" : d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes} min. siden"
            : d.TotalHours < 24 ? $"{(int)d.TotalHours} t siden" : utc.ToLocalTime().ToString("dd-MM HH:mm");
    }

    /// <summary>Besked, når et job starter eller slutter (null = ingen besked).</summary>
    public static string? JobNotice(string? before, string? now) =>
        before == now ? null : now is not null ? now : before is not null ? $"Færdig: {before}" : null;
}

/// <summary>
/// Opsætningsvinduet på en Mac, hvor agenten allerede er installeret (som Windows): hvad den er forbundet til, hvilke udvidelser
/// den har, og hvornår en ændring kræver tilmeldingsnøglen (ny server eller profil = agenten tilmeldes igen).
/// </summary>
public static class MacSetup
{
    /// <summary>
    /// Ses ikonet ikke? På en MacBook med notch skjuler macOS ikoner, der ikke er plads til, og fra macOS 26 kan de slås fra under
    /// Systemindstillinger → Menulinje. Fleetdeck kan altid åbnes fra Programmer eller Spotlight.
    /// </summary>
    public const string MenuBarHint = "Ses ikonet ikke i menulinjen? På en MacBook med notch skjuler macOS ikoner, der ikke er plads til – luk et par andre " +
        "ikoner, eller tjek Systemindstillinger → Menulinje (Tillad i menulinjen: Fleetdeck). Fleetdeck kan altid åbnes fra Programmer eller Spotlight (⌘ + mellemrum).";

    /// <summary>Linjerne i "Forbundet"-boksen øverst.</summary>
    public static List<(string Label, string Value)> Summary(TrayStatus? s, DateTime nowUtc)
    {
        if (s is null) return [("Tjeneste", "Kører ikke – Fleetdeck-tjenesten har ikke skrevet sin status")];
        var st = TrayView.Of(s, nowUtc);
        var conn = st == TrayView.State.NoService ? $"Tjenesten svarer ikke (sidst {TrayView.Ago(s.Written, nowUtc)})"
            : s.Connected ? $"Forbundet{(s.LastContact is { } lc ? $" · kontakt {TrayView.Ago(lc, nowUtc)}" : "")}"
            : $"Ingen forbindelse{(s.Error is { Length: > 0 } e ? $": {e}" : "")}";
        return
        [
            ("Server", s.Server is { Length: > 0 } sv ? sv : "–"),
            ("Maskine", s.Machine is { Length: > 0 } m ? m : "–"),
            ("Profil", ProfileLabel(s.Profile)),
            ("Forbindelse", conn),
            ("Fuld diskadgang", s.MacAccess?.FullDiskAccess switch { true => "Givet ✓", false => "Mangler", _ => "Kunne ikke afgøres" }),
            ("Tilmelding", IsEnrolled(s) ? "Tilmeldt ✓ – nøglen er gemt som agentens eget login" : "Ikke tilmeldt endnu"),
        ];
    }

    /// <summary>Overskriften og farven på boksen (o = grøn, w = orange, b = rød).</summary>
    public static (string Title, char Kind) Headline(TrayStatus? s, DateTime nowUtc) => TrayView.Of(s, nowUtc) switch
    {
        TrayView.State.NoService => (s is null ? "Tjenesten kører ikke" : "Tjenesten svarer ikke", 'b'),
        TrayView.State.Offline => ("Ikke forbundet", 'b'),
        _ when TrayView.NeedsFullDiskAccess(s) => ("Forbundet – Fuld diskadgang mangler", 'w'),
        _ => ("Forbundet", 'o'),
    };

    public static string ProfileLabel(string? profile) => profile is { Length: > 0 } p ? p : "Ingen profil";

    /// <summary>Er serveradressen ændret i forhold til den, agenten er forbundet til?</summary>
    public static bool ServerChanged(string? installed, string? typed)
    {
        var t = Discovery.Normalize(typed ?? "");
        return t.Length > 0 && !string.Equals(Discovery.Normalize(installed ?? ""), t, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Er en anden profil valgt end den nuværende? (null/tom = ingen profil)</summary>
    public static bool ProfileChanged(string? current, string? selected) =>
        !string.Equals(current is { Length: > 0 } ? current : null, selected is { Length: > 0 } ? selected : null, StringComparison.Ordinal);

    /// <summary>Nøglen skal kun bruges, hvis agenten skal tilmeldes igen: ny server eller ny profil.</summary>
    public static bool NeedsKey(TrayStatus? s, string? typedServer, bool profileChanged) =>
        !IsEnrolled(s) || ServerChanged(s!.Server, typedServer) || profileChanged;

    /// <summary>Tilmeldt: tjenesten siger det – eller har haft kontakt med serveren (ældre agenter skriver ikke <see cref="TrayStatus.Enrolled"/>).</summary>
    public static bool IsEnrolled(TrayStatus? s) => s is not null && (s.Enrolled || s.Connected || s.LastContact is not null);

    /// <summary>
    /// Appen åbnes (dobbeltklik, Spotlight, menulinjen eller tjenesten): opsætningsvinduet i stedet for status, når agenten mangler –
    /// eller kører uden at være tilmeldt og ikke venter på en åben "Anmod om adgang" (så kan brugeren kun komme videre herfra).
    /// </summary>
    public static bool ShowSetupOnOpen(bool installed, TrayStatus? s, DateTime nowUtc) =>
        !installed || (s is not null && TrayView.Of(s, nowUtc) != TrayView.State.NoService && !IsEnrolled(s)
                       && (s.AccessRequest is not { } ar || !AccessStates.Open(ar.State)));

    public sealed record ExtensionRow(string Id, string Name, string Gives, bool Installed, bool Pending)
    {
        /// <summary>Kan slås til herfra (fjernelse af en udvidelse findes ikke – den bliver stående).</summary>
        public bool CanInstall => !Installed && !Pending;
        public string Note => Installed ? "Installeret" : Pending ? "Installeres…" : Gives;
    }

    /// <summary>
    /// Udvidelserne til Mac'en med deres rigtige tilstand: tjenestens liste fra serveren, hvis den har den, ellers de indbyggede
    /// (alle slået fra). Rækkefølgen følger de indbyggede, så vinduet ikke hopper.
    /// </summary>
    public static List<ExtensionRow> ExtensionRows(TrayStatus? s, IEnumerable<ExtensionDef> builtIn)
    {
        var mac = builtIn.Where(e => e.Mac is not null).ToList();
        var known = s?.Extensions ?? [];
        var rows = mac.Select(d => known.FirstOrDefault(k => k.Id == d.Id) is { } k
            ? new ExtensionRow(d.Id, d.Name, d.Gives, k.Installed, k.Pending)
            : new ExtensionRow(d.Id, d.Name, d.Gives, false, false)).ToList();
        // udvidelser, serveren kender, men denne app-version ikke har indbygget
        rows.AddRange(known.Where(k => mac.All(d => d.Id != k.Id)).Select(k => new ExtensionRow(k.Id, k.Name, k.Gives, k.Installed, k.Pending)));
        return rows;
    }

    /// <summary>Dem, der skal installeres: slået til i vinduet, men hverken installeret eller på vej.</summary>
    public static List<string> ToInstall(IEnumerable<ExtensionRow> rows, IEnumerable<string> checkedIds)
    {
        var on = checkedIds.ToHashSet();
        return rows.Where(r => r.CanInstall && on.Contains(r.Id)).Select(r => r.Id).ToList();
    }

    /// <summary>Tjenesten henter udvidelsernes tilstand hvert 10. minut – hvert check-in (højst hvert 20. sek.), mens en er på vej.</summary>
    public static bool RefreshExtensions(TrayStatus s, DateTime lastUtc, DateTime nowUtc) =>
        nowUtc - lastUtc > (s.Extensions.Any(e => e.Pending) ? TimeSpan.FromSeconds(20) : TimeSpan.FromMinutes(10));
}

/// <summary>
/// Kommandoerne, Mac-appen kører med administratoradgangskoden (osascript "with administrator privileges"):
/// installér/fjern agenten, administrator-ordrer og genstart af tjenesten. Al citering samlet her, så den kan testes.
/// </summary>
public static partial class MacInstall
{
    /// <summary>Citér til /bin/sh (enkelte anførselstegn).</summary>
    public static string Sh(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>Citér som AppleScript-streng.</summary>
    public static string AppleScript(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>osascript-argumenterne, der kører et shell-script som root (macOS spørger efter en administrators adgangskode).</summary>
    public static string[] Elevated(string script, string? prompt = null) =>
        ["-e", $"do shell script {AppleScript(script)}{(prompt is { Length: > 0 } ? $" with prompt {AppleScript(prompt)}" : "")} with administrator privileges"];

    [GeneratedRegex("^[0-9a-f]{64}$")] private static partial Regex Sha256Hex();
    [GeneratedRegex("^[a-z0-9_-]+$")] private static partial Regex ExtId();

    /// <summary>
    /// Installér: læg appen i /Programmer (hvis den kører et andet sted fra, fx disk-imaget), kopiér den hentede agent til en mappe,
    /// kun root kan skrive i, tjek kontrolsummen igen dér (så ingen kan bytte filen ud imellem), og kør agentens egen "install".
    /// <paramref name="exactExtensions"/>: send listen, også når den er tom (en allerede installeret Mac: serverens standardudvidelser
    /// skal ikke installeres igen – kun dem, brugeren netop har slået til).
    /// </summary>
    public static string InstallScript(string agentFile, string sha256, string server, string key, int profile, IEnumerable<string> extensions, string? appBundle,
        bool exactExtensions = false) =>
        Script(agentFile, sha256, server, $"--key {Sh(key)} --profile {profile}", extensions, appBundle, exactExtensions);

    /// <summary>"Anmod om adgang" (ingen nøgle): som <see cref="InstallScript"/>, men agenten beder administratoren om lov og viser en kode.</summary>
    public static string RequestScript(string agentFile, string sha256, string server, string? message, IEnumerable<string> extensions, string? appBundle) =>
        Script(agentFile, sha256, server, "--request" + (message?.Trim() is { Length: > 0 } m ? $" --message {Sh(m.Length > 200 ? m[..200] : m)}" : ""), extensions, appBundle, false);

    private static string Script(string agentFile, string sha256, string server, string auth, IEnumerable<string> extensions, string? appBundle, bool exactExtensions)
    {
        if (!Sha256Hex().IsMatch(sha256)) throw new ArgumentException("Ugyldig kontrolsum", nameof(sha256));
        var ext = extensions.Where(e => ExtId().IsMatch(e)).ToList();
        var sb = new StringBuilder();
        sb.Append("set -e; ");
        if (appBundle is { Length: > 0 } && appBundle.TrimEnd('/') != MacPaths.InstalledApp)
            sb.Append($"rm -rf {Sh(MacPaths.InstalledApp)}; ditto {Sh(appBundle)} {Sh(MacPaths.InstalledApp)}; ");
        sb.Append($"xattr -dr com.apple.quarantine {Sh(MacPaths.InstalledApp)} 2>/dev/null || true; ");
        sb.Append("T=$(mktemp -d /tmp/fleetdeck.XXXXXX); trap 'rm -rf \"$T\"' EXIT; ");
        sb.Append($"cp {Sh(agentFile)} \"$T/fleetdeck-agent\"; ");
        sb.Append($"echo {Sh(sha256 + "  ")}\"$T/fleetdeck-agent\" | shasum -a 256 -c -s; ");
        sb.Append("chmod 755 \"$T/fleetdeck-agent\"; xattr -c \"$T/fleetdeck-agent\" 2>/dev/null || true; ");
        sb.Append($"\"$T/fleetdeck-agent\" install --server {Sh(server)} {auth}");
        if (ext.Count > 0 || exactExtensions) sb.Append($" --extensions {Sh(string.Join(',', ext))}");
        sb.Append($" --menubar {Sh(MacPaths.InstalledApp)} --service 2>&1");
        return sb.ToString();
    }

    /// <summary>Fjern agenten, tjenesten, ikonet ved login og al konfiguration (appen selv lægger brugeren i papirkurven).</summary>
    public static string UninstallScript() =>
        $"launchctl bootout system/{MacPaths.AgentLabel} 2>/dev/null || true; " +
        // ikonet stoppes hos alle, der er logget ind (også brugere i baggrunden ved hurtigt brugerskift), ikke kun den ved skærmen
        $"for U in $(stat -f %u /dev/console 2>/dev/null) $(ps -axo uid=,comm= 2>/dev/null | awk '$2 ~ /loginwindow$/ {{print $1}}'); do if [ \"$U\" != 0 ]; then launchctl bootout gui/$U/{MacPaths.MenuBarLabel} 2>/dev/null || true; fi; done; " +
        MacKeepAwake.RemovalScript() +
        $"rm -f {Sh(MacPaths.DaemonPlist)} {Sh(MacPaths.MenuBarPlist)}; " +
        $"rm -rf {Sh(MacPaths.AgentDir)} {Sh(MacPaths.ConfigDir)} {Sh(MacPaths.SupportDir)}; echo 'Fleetdeck er fjernet.'";

    /// <summary>Administrator-ordre (pause, udvidelse) – agenten lægger den selv i roots mappe.</summary>
    public static string AdminCommandScript(TrayCommand c) => $"{Sh(MacPaths.AgentExe)} tray-cmd {TrayCommands.Encode(c)}";

    /// <summary>Genstart tjenesten (så den får en netop givet Fuld diskadgang).</summary>
    public static string RestartScript() => $"launchctl kickstart -k system/{MacPaths.AgentLabel}";

    [GeneratedRegex(@"\((-?\d+)\)\s*$")] private static partial Regex OsaError();

    /// <summary>
    /// osascript afslutter altid med 1, når scriptet fejler; scriptets egen exit-kode står til sidst i fejlen: "… (3)".
    /// -128 = brugeren trykkede Annullér.
    /// </summary>
    public static int ScriptExitCode(int osascriptExit, string stderr) =>
        osascriptExit == 0 ? 0 : OsaError().Match(stderr.Trim()) is { Success: true } m ? int.Parse(m.Groups[1].Value) : osascriptExit;

    /// <summary>Exit-koder fra "install" (og osascript) som tekst til brugeren.</summary>
    public static string Explain(int code, string output)
    {
        var last = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
        if (code == -128 || output.Contains("(-128)") || output.Contains("User canceled", StringComparison.OrdinalIgnoreCase)) return "Installationen blev afbrudt (adgangskoden blev ikke givet).";
        return code switch
        {
            0 => last,
            3 => "Tilmeldingsnøglen blev afvist. Hent den igen i Fleetdeck (den kan være skiftet).",
            4 => $"Tjenesten kunne ikke startes. {last}",
            _ when output.Contains("shasum") || output.Contains("FAILED") => "Agentfilen blev ændret undervejs – installationen er stoppet. Prøv igen.",
            _ => $"Installationen fejlede (kode {code}). {last}",
        };
    }

    /// <summary>LaunchAgent for alle brugere: Fleetdeck i menulinjen ved login (genstartes kun, hvis den går ned – ikke når brugeren lukker den).</summary>
    public static string MenuBarAgentPlist(string appBundle) => MenuBarAgentPlist(appBundle, MacMenuStamp.IsCurrent(MacPaths.NativeMenu));

    /// <summary>
    /// <paramref name="nativeMenu"/>: den native menu-app (med det rigtige stempel) er lagt på – ellers viser Fleetdeck.app selv ikonet (--tray).
    /// AssociatedBundleIdentifiers: macOS 13+ viser "Fleetdeck" (ikke en ukendt udvikler) under Systemindstillinger → Loginemner.
    /// </summary>
    public static string MenuBarAgentPlist(string appBundle, bool nativeMenu) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0"><dict>
          <key>Label</key><string>{MacPaths.MenuBarLabel}</string>
          <key>ProgramArguments</key><array>{(nativeMenu ? $"<string>{MacPaths.NativeMenu}</string>" : $"<string>{System.Security.SecurityElement.Escape(appBundle.TrimEnd('/') + "/" + MacPaths.AppExecutable)}</string><string>--tray</string>")}</array>
          <key>AssociatedBundleIdentifiers</key><array><string>{MacPaths.BundleId}</string><string>{MacPaths.MenuBundleId}</string></array>
          <key>RunAtLoad</key><true/>
          <key>KeepAlive</key><dict><key>SuccessfulExit</key><false/></dict>
          <key>LimitLoadToSessionType</key><string>Aqua</string>
          <key>ProcessType</key><string>Interactive</string>
        </dict></plist>
        """;

    /// <summary>"arm64" (Apple Silicon) eller "x64" (Intel) – som serverens /api/agent/download?arch=.</summary>
    public static string Arch(System.Runtime.InteropServices.Architecture a) => a == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
}
