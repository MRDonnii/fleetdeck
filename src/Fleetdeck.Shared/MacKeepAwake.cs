using System.Text.RegularExpressions;

namespace Fleetdeck.Shared;

/// <summary>"Hold vågen på strøm" som serveren sender den (i det signerede check-in-svar).</summary>
public sealed class KeepAwakeSetting
{
    public bool On { get; set; }
    /// <summary>Hvornår den sidst blev ændret (serverens ur). Null = aldrig ændret.</summary>
    public DateTime? At { get; set; }
    /// <summary>web (Fleetdeck) | mac (menulinjen/appen på maskinen).</summary>
    public string? Source { get; set; }
}

/// <summary>En ændring, brugeren har lavet lokalt på Mac'en (menulinjen eller appen), meldt ved check-in.</summary>
public sealed class KeepAwakeChange
{
    public bool On { get; set; }
    /// <summary>Hvornår ændringen skete (Mac'ens ur).</summary>
    public DateTime At { get; set; }
    /// <summary>Mac'ens ur, da check-in blev sendt – så serveren kan rette for et ur, der går forkert.</summary>
    public DateTime Now { get; set; }
}

/// <summary>Tilstanden lige nu på Mac'en (i LiveFacts ved hvert check-in og i status.json til menulinjen).</summary>
public sealed class KeepAwakeState
{
    /// <summary>Indstillingen, agenten følger (den sidst kendte, hvis serveren ikke kan nås).</summary>
    public bool Setting { get; set; }
    /// <summary>Strøm fra lysnettet (null = ukendt, regnes som batteri).</summary>
    public bool? OnAc { get; set; }
    /// <summary>held | battery | off | user | error – se <see cref="MacKeepAwake.States"/>.</summary>
    public string State { get; set; } = MacKeepAwake.States.Off;
    public string? Error { get; set; }
    /// <summary>Hvornår indstillingen sidst blev ændret, og hvorfra (web | mac).</summary>
    public DateTime? ChangedAt { get; set; }
    public string? ChangedFrom { get; set; }
}

/// <summary>
/// "Hold vågen på strøm (også med lukket låg)" på Mac: på strøm sætter agenten <c>pmset -a disablesleep 1</c> (det er den, der holder
/// en MacBook vågen med lukket låg); på batteri, eller når indstillingen er fra, straks <c>disablesleep 0</c> igen.
/// Agenten ændrer kun det, den selv har sat (markørfilen), og en lille uafhængig vagt (LaunchDaemon hvert 30. sekund) slår dvale
/// til igen på batteri, selv hvis agenten er gået ned eller hængt. Alt her er ren logik, så det kan testes uden en Mac.
/// </summary>
public static partial class MacKeepAwake
{
    public const string WatchdogLabel = "com.fleetdeck.keepawake";
    public const string WatchdogPlist = "/Library/LaunchDaemons/" + WatchdogLabel + ".plist";
    public const string WatchdogScript = MacPaths.AgentDir + "/keepawake-watchdog.sh";
    /// <summary>Findes, når (og kun når) Fleetdeck har sat disablesleep 1. Kun root kan skrive i mappen.</summary>
    public const string Marker = MacPaths.ConfigDir + "/keepawake.marker";
    /// <summary>Den sidst kendte indstilling (bruges, når serveren ikke kan nås, og ved opstart).</summary>
    public const string StateFile = MacPaths.ConfigDir + "/keepawake.json";
    public const string AcPower = "AC Power";
    public static readonly TimeSpan Poll = TimeSpan.FromSeconds(5);
    /// <summary>Hvor tit pmset spørges om disablesleep, når intet har ændret sig (ellers kun ved skift).</summary>
    public static readonly TimeSpan Recheck = TimeSpan.FromMinutes(5);

    public static class States
    {
        public const string Held = "held", Battery = "battery", Off = "off", User = "user", Error = "error";
    }

    public enum Act { None, Enable, Disable }

    public sealed record Decision(Act Act, string State);

    /// <summary>IOPSGetProvidingPowerSourceType: "AC Power" | "Battery Power" | "UPS Power". Alt andet end "AC Power" (også ukendt) er batteri.</summary>
    public static bool IsAc(string? providing) => providing == AcPower;

    /// <summary>
    /// Hvad agenten skal gøre. <paramref name="sleepDisabled"/> er pmsets nuværende disablesleep (null = ukendt / ikke læst).
    /// Batteri (eller indstilling fra) med markør: slå altid dvale til igen – uden at vente på at læse pmset.
    /// Uden markør røres et disablesleep 1 aldrig: det har brugeren selv sat.
    /// </summary>
    public static Decision Decide(bool setting, bool onAc, bool marker, bool? sleepDisabled)
    {
        var want = setting && onAc;
        var idle = setting ? States.Battery : States.Off;
        if (!want)
        {
            if (marker) return new(Act.Disable, idle);
            return new(Act.None, sleepDisabled == true ? States.User : idle);
        }
        if (marker) return new(sleepDisabled == true ? Act.None : Act.Enable, States.Held);   // vores egen: sæt den igen, hvis den er væk
        return sleepDisabled switch
        {
            true => new(Act.None, States.User),       // sat af brugeren før Fleetdeck: lad den være
            false => new(Act.Enable, States.Held),
            null => new(Act.None, States.Error),      // kan ikke se, om brugeren har sat den: gør intet
        };
    }

    /// <summary>Skal disablesleep læses for at kunne beslutte? (Batteri med markør handler med det samme.)</summary>
    public static bool NeedsSleepDisabled(bool setting, bool onAc, bool marker) => !(marker && !(setting && onAc));

    [GeneratedRegex(@"^\s*SleepDisabled\s+(\d+)", RegexOptions.Multiline)] private static partial Regex SleepDisabledLine();

    /// <summary>"pmset -g": linjen " SleepDisabled  1". Mangler linjen i et ellers gyldigt svar, er den 0. Tomt svar = ukendt.</summary>
    public static bool? ParseSleepDisabled(string? pmsetG)
    {
        if (string.IsNullOrWhiteSpace(pmsetG)) return null;
        if (SleepDisabledLine().Match(pmsetG) is { Success: true } m) return m.Groups[1].Value != "0";
        return pmsetG.Contains("Currently in use", StringComparison.OrdinalIgnoreCase) ? false : null;
    }

    /// <summary>"pmset -g ps": første linje "Now drawing from 'AC Power'". Reserve, hvis IOKit ikke kan bruges.</summary>
    public static bool ParsePsIsAc(string? pmsetPs) => pmsetPs?.Split('\n').FirstOrDefault()?.Contains("'" + AcPower + "'") == true;

    /// <summary>Teksten i Fleetdeck og menulinjen.</summary>
    public static string Text(KeepAwakeState? s) => s?.State switch
    {
        States.Held => "Strøm · holdes vågen",
        States.Battery => "Batteri · sover normalt",
        States.User => "Dvale er slået fra af brugeren (pmset)",
        States.Error => s.Error is { Length: > 0 } e ? $"Fejl: {e}" : "Fejl",
        States.Off => "Fra",
        _ => "–",
    };

    // ------------------------------------------------------------------ vagten (uafhængig af agenten)

    /// <summary>
    /// Vagten: slår kun dvale til igen, når markøren findes (Fleetdeck har sat den) OG Mac'en ikke er på lysnettet.
    /// Kan "pmset -g ps" ikke læses, regnes det som batteri. Markøren fjernes kun, når pmset lykkedes (ellers prøves igen om 30 s).
    /// </summary>
    public static string WatchdogScriptText() => $$"""
        #!/bin/sh
        # Fleetdeck: vagt for "Hold vågen på strøm". Kører hvert 30. sekund (launchd), uafhængigt af agenten.
        # Slår kun dvale til igen, hvis Fleetdeck selv har slået den fra (markøren findes) og Mac'en kører på batteri.
        MARKER='{{Marker}}'
        [ -f "$MARKER" ] || exit 0
        SRC=$(/usr/bin/pmset -g ps 2>/dev/null | /usr/bin/head -n 1)
        case "$SRC" in
          *"'{{AcPower}}'"*) exit 0 ;;
        esac
        if /usr/bin/pmset -a disablesleep 0; then
          /bin/rm -f "$MARKER"
          /usr/bin/logger -t fleetdeck-keepawake "Batteri: dvale slået til igen (disablesleep 0)"
        fi
        exit 0

        """;

    public static string WatchdogPlistText() => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0"><dict>
          <key>Label</key><string>{WatchdogLabel}</string>
          <key>ProgramArguments</key><array><string>/bin/sh</string><string>{WatchdogScript}</string></array>
          <key>StartInterval</key><integer>30</integer>
          <key>RunAtLoad</key><true/>
          <key>ProcessType</key><string>Background</string>
        </dict></plist>
        """;

    /// <summary>Til afinstallation (efter at agenten er stoppet): slå dvale til igen, hvis Fleetdeck satte den, og fjern vagten.</summary>
    public static string RemovalScript() =>
        $"if [ -f {MacInstall.Sh(Marker)} ]; then pmset -a disablesleep 0; rm -f {MacInstall.Sh(Marker)}; fi; " +
        $"launchctl bootout system/{WatchdogLabel} 2>/dev/null || true; rm -f {MacInstall.Sh(WatchdogPlist)} {MacInstall.Sh(WatchdogScript)}; ";

    // ------------------------------------------------------------------ to-vejs synkronisering (sidste ændring vinder)

    /// <summary>Agentens gemte indstilling. <see cref="Pending"/>: en lokal ændring, serveren ikke har bekræftet endnu.</summary>
    public sealed class Local
    {
        public bool On { get; set; }
        public DateTime? At { get; set; }
        public string? Source { get; set; }
        public bool Pending { get; set; }
    }

    /// <summary>Brugeren slår den til/fra på Mac'en: gælder med det samme og meldes til serveren.</summary>
    public static Local Toggle(bool on, DateTime nowUtc) => new() { On = on, At = nowUtc, Source = "mac", Pending = true };

    /// <summary>Det, der sendes med check-in (kun en ubekræftet lokal ændring).</summary>
    public static KeepAwakeChange? Outgoing(Local l, DateTime nowUtc) =>
        l.Pending && l.At is { } at ? new KeepAwakeChange { On = l.On, At = at, Now = nowUtc } : null;

    /// <summary>
    /// Svaret fra serveren. Null (ældre server, afvist svar, ikke en Mac): behold den sidst kendte.
    /// En ubekræftet lokal ændring overskrives kun af et svar på netop det check-in, der bar den (<paramref name="sentAt"/>);
    /// serveren har da allerede afgjort, hvilken ændring der er nyest. Er der ændret lokalt igen siden, venter den til næste check-in.
    /// </summary>
    public static Local Merge(Local l, KeepAwakeSetting? server, DateTime? sentAt)
    {
        if (server is null) return l;
        if (l.Pending && (sentAt is null || l.At != sentAt)) return l;
        return new Local { On = server.On, At = server.At, Source = server.Source, Pending = false };
    }

    /// <summary>
    /// Serveren: skal en ændring fra Mac'en gælde? Mac'ens tidspunkt rettes for urforskel (serverens ur minus Mac'ens ur ved afsendelse)
    /// og kan aldrig ligge efter serverens nu. Returnerer tidspunktet, den gemmes med, eller null, hvis en nyere ændring (fx fra web) vinder.
    /// </summary>
    public static DateTime? Accept(DateTime? currentAt, KeepAwakeChange c, DateTime serverNowUtc)
    {
        var skew = serverNowUtc - c.Now;
        var at = c.At + skew;
        if (at > serverNowUtc) at = serverNowUtc;
        if (currentAt is { } cur && at <= cur) return null;
        return at;
    }
}
