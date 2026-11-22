using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;

namespace Fleetdeck.Shared;

// ------------------------------------------------------------------ opsætningsvinduet (forslag B), fælles for Mac, Windows og Linux
//
// Ejeren valgte forslag B ("macOS-indbygget"): en sidebjælke med trinene Velkommen → Server → Tilmelding → Profil og tilvalg →
// Installation → Tilladelser (Mac) / Færdig (Windows og Linux), én skærm ad gangen og fejl ved det felt, de handler om.
// Trin, skærme, overgange og tekster står her, så de tre platforme siger og gør det samme – kun tegningen er platformens egen.

public enum SetupOs { Mac, Windows, Linux }

/// <summary>Trinene i sidebjælken (Linux: "Trin n af 6" i dialogernes titel).</summary>
public enum SetupStep { Welcome, Server, Enroll, Profile, Install, Finish }

/// <summary>Alle skærme (tilstandene fra kortlægningen af det gamle vindue + forslag B).</summary>
public enum SetupScreen
{
    Welcome,        // velkomst – med "Indsæt link/kommando"
    Searching,      // søger efter Fleetdeck på netværket
    Found,          // fandt én eller flere servere
    NoServer,       // fejl: ingen server fundet
    ServerError,    // fejl: certifikat, kan ikke nå, ikke Fleetdeck, gammel WinStyring
    Key,            // tilmeldingsnøgle (tom eller "Nøglen passer")
    KeyError,       // fejl: forkert nøgle
    Request,        // anmod om adgang (besked til administratoren)
    Waiting,        // venter på godkendelse – koden
    Rejected,       // afvist eller udløbet
    Profile,        // profil og tilvalg ("Klar" når en profil er valgt)
    Installing,     // henter, tjekker og installerer
    NoConnection,   // installeret, men ingen forbindelse efter 2 minutter
    Enrolled,       // tilmeldt → Fuld diskadgang (kun Mac)
    Done,           // alt er klar (Windows: evt. genstart)
    Installed,      // allerede installeret: oversigt, skift server/profil, tilvalg, afinstallér
}

public enum SetupField { None, Server, Key, Profile, Message }

public enum SetupTone { Info, Ok, Warn, Bad }

public enum SetupStepState { Todo, Current, Done }

/// <summary>Et trin i sidebjælken: titel, ikon (navn fra <see cref="SetupIcons"/>) og flisens farve.</summary>
public sealed record SetupStepInfo(SetupStep Step, string Title, string Icon, string Color);

/// <summary>En side i "Installeret"-oversigten.</summary>
public sealed record SetupSection(string Id, string Title, string Icon, string Color);

/// <summary>Resultatet af et tjek: ok, eller en fejl knyttet til et felt (server/nøgle) med en kort titel, en forklaring og tekniske detaljer.</summary>
public sealed record SetupCheck(bool Ok, SetupField Field, string Title, string Text, string? Detail = null, string? Version = null)
{
    public static SetupCheck Good(string title, string text, string? version = null) => new(true, SetupField.None, title, text, null, version);
    public static SetupCheck Bad(SetupField field, string title, string text, string? detail = null) => new(false, field, title, text, detail);
    /// <summary>Én linje (til statuslinjer, notify-send og terminalen).</summary>
    public string Line => Text.Length == 0 ? Title : $"{Title} {Text}";
}

/// <summary>Overskrift, tekst, ikon og knapper til en skærm.</summary>
public sealed record SetupPage(SetupScreen Screen, SetupStep Step, string Icon, string Color, string Title, string Text, string? Primary, string? Secondary);

/// <summary>Det, teksterne skal bruge: platform, server, fundne servere, kode, navn og profil.</summary>
public sealed record SetupContext(SetupOs Os, string? Server = null, int Found = 0, string? Code = null, string? Machine = null, string? Profile = null,
    bool Reboot = false, bool Expired = false, bool Installed = false);

/// <summary>Ord, der skifter med platformen.</summary>
public sealed record SetupWords(string Device, string DeviceLower, string This, string Tray, string Script, string Password, string Example)
{
    public static SetupWords For(SetupOs os) => os switch
    {
        SetupOs.Mac => new("Mac'en", "Mac'en", "denne Mac", "i menulinjen øverst til højre", "install.sh",
            "macOS beder om din adgangskode i et separat vindue.", "fx Den nye MacBook i stuen"),
        SetupOs.Windows => new("Pc'en", "pc'en", "denne pc", "ved uret nederst til højre", "install.ps1",
            "Windows spørger om lov til at installere (administrator).", "fx Den nye bærbare i stuen"),
        _ => new("Maskinen", "maskinen", "denne maskine", "som tjenesten fleetdeck-agent", "install.sh",
            "Du bliver bedt om din adgangskode (pkexec eller sudo).", "fx Serveren i kælderen"),
    };
}

/// <summary>Farverne fra forslag B (macOS-systemfarver + Fleetdecks egen blå).</summary>
public static class SetupColors
{
    public const string Fleet = "#2563eb", Blue = "#0a84ff", Gray = "#8e8e93", Purple = "#a55ee6", Green = "#30c048", Indigo = "#5e5ce6",
        Orange = "#ff9500", Red = "#ff3b30", Teal = "#30a8d8";
    public static string Of(SetupTone t) => t switch { SetupTone.Ok => Green, SetupTone.Warn => Orange, SetupTone.Bad => Red, _ => Blue };
}

/// <summary>
/// Ikonerne fra forslag B som SVG-stier (24×24, streg 1,7, runde ender). Mac tegner dem som Avalonia-geometri, Windows med GDI+ og
/// HTML-gengivelserne direkte. Fyldte figurer er markeret med "F:" foran.
/// </summary>
public static class SetupIcons
{
    public static readonly IReadOnlyDictionary<string, string[]> Paths = new Dictionary<string, string[]>
    {
        ["server"] = ["M5.5 4h13a2 2 0 0 1 2 2v3a2 2 0 0 1-2 2h-13a2 2 0 0 1-2-2v-3a2 2 0 0 1 2-2Z", "M5.5 13h13a2 2 0 0 1 2 2v3a2 2 0 0 1-2 2h-13a2 2 0 0 1-2-2v-3a2 2 0 0 1 2-2Z", "M12 7.5h5M12 16.5h5", "M7.4 7.5h.2M7.4 16.5h.2"],
        ["key"] = ["M12.2 15.5a4.2 4.2 0 1 1-8.4 0a4.2 4.2 0 1 1 8.4 0Z", "M11 12.5 20 3.5M16.5 7l2.6 2.6M18.6 5l2 2"],
        ["stack"] = ["M12 3.5 20.5 8 12 12.5 3.5 8Z", "M3.5 12 12 16.5 20.5 12", "M3.5 16 12 20.5 20.5 16"],
        ["down"] = ["M20.5 12a8.5 8.5 0 1 1-17 0a8.5 8.5 0 1 1 17 0Z", "M12 7.5v8.5M8.3 12.5 12 16.2l3.7-3.7"],
        ["hand"] = ["M8 13V6.2a1.4 1.4 0 0 1 2.8 0V12M10.8 11V4.8a1.4 1.4 0 0 1 2.8 0V11M13.6 11V5.8a1.4 1.4 0 0 1 2.8 0v6.5M16.4 12V8.6a1.4 1.4 0 0 1 2.8 0v5.6c0 4-2.6 6.6-6.4 6.6-2.6 0-4.1-1.1-5.5-3.1L4.6 14a1.45 1.45 0 0 1 2.3-1.7L8 13.6"],
        ["xmark"] = ["M7.5 7.5l9 9M16.5 7.5l-9 9"],
        ["check"] = ["M5 12.5 9.5 17 19 7.2"],
        ["info"] = ["M20.5 12a8.5 8.5 0 1 1-17 0a8.5 8.5 0 1 1 17 0Z", "M12 11v5.5", "M12 7.7v.2"],
        ["plane"] = ["M20.5 3.5 10.2 13.8", "M20.5 3.5 14 20.5l-3.8-6.7L3.5 10Z"],
        ["hour"] = ["M7 3.5h10M7 20.5h10", "M8 3.5c0 4.5 8 4.8 8 8.5s-8 4-8 8.5M16 3.5c0 4.5-8 4.8-8 8.5s8 4 8 8.5"],
        ["wifix"] = ["M3 9.2a13.5 13.5 0 0 1 13.5-3.1M6 12.4a9 9 0 0 1 7.5-2.2M9 15.6a4.5 4.5 0 0 1 3.6-1.1", "M12 18.7v.2", "M17 10.5l4.5 4.5M21.5 10.5 17 15"],
        ["refresh"] = ["M19.5 11.5a7.5 7.5 0 0 1-13.4 4.6M4.5 12.5a7.5 7.5 0 0 1 13.4-4.6", "M18.3 3.8v4.4h-4.4M5.7 20.2v-4.4h4.4"],
        ["copy"] = ["M10.7 8.5h7.1a2.2 2.2 0 0 1 2.2 2.2v7.6a2.2 2.2 0 0 1-2.2 2.2h-7.1a2.2 2.2 0 0 1-2.2-2.2v-7.6a2.2 2.2 0 0 1 2.2-2.2Z", "M15.5 8.5V6a2 2 0 0 0-2-2H6a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h2.5"],
        ["clip"] = ["M7.7 4.5h8.6a2.2 2.2 0 0 1 2.2 2.2v12.1a2.2 2.2 0 0 1-2.2 2.2h-8.6a2.2 2.2 0 0 1-2.2-2.2v-12.1a2.2 2.2 0 0 1 2.2-2.2Z", "M10.2 2.8h3.6a1.2 1.2 0 0 1 1.2 1.2v1a1.2 1.2 0 0 1-1.2 1.2h-3.6a1.2 1.2 0 0 1-1.2-1.2v-1a1.2 1.2 0 0 1 1.2-1.2Z", "M9 11h6M9 14.5h6"],
        ["lock"] = ["M7.2 10.5h9.6a2.2 2.2 0 0 1 2.2 2.2v5.6a2.2 2.2 0 0 1-2.2 2.2h-9.6a2.2 2.2 0 0 1-2.2-2.2v-5.6a2.2 2.2 0 0 1 2.2-2.2Z", "M8.2 10.5V7.8a3.8 3.8 0 0 1 7.6 0v2.7"],
        ["pulse"] = ["M3 12.5h4l2.2-5.5 4 11 2.4-5.5H21"],
        ["laptop"] = ["M6.1 5h11.8a1.6 1.6 0 0 1 1.6 1.6v7.3a1.6 1.6 0 0 1-1.6 1.6h-11.8a1.6 1.6 0 0 1-1.6-1.6v-7.3a1.6 1.6 0 0 1 1.6-1.6Z", "M2.5 18.8h19"],
        ["remote"] = ["M5 4h14a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2h-14a2 2 0 0 1-2-2v-8a2 2 0 0 1 2-2Z", "M8.5 20h7M12 16v4M10 8.5 13.5 10 10 11.5"],
        ["mug"] = ["M5 8h11v6.5a4.5 4.5 0 0 1-4.5 4.5h-2A4.5 4.5 0 0 1 5 14.5Z", "M16 10h1.5a2.5 2.5 0 0 1 0 5H16"],
        ["appstore"] = ["M20.5 12a8.5 8.5 0 1 1-17 0a8.5 8.5 0 1 1 17 0Z", "M9.6 7.5 14.8 16.5M14.4 7.5 9.2 16.5M7.2 13.6h9.6"],
        ["ipad"] = ["M7.7 3h8.6a2.2 2.2 0 0 1 2.2 2.2v13.6a2.2 2.2 0 0 1-2.2 2.2h-8.6a2.2 2.2 0 0 1-2.2-2.2v-13.6a2.2 2.2 0 0 1 2.2-2.2Z", "M11 18.2h2"],
        ["thermo"] = ["M14 4.5v9.8a4 4 0 1 1-4 0V4.5a2 2 0 0 1 4 0Z", "M12 11v5.5"],
        ["disk"] = ["M5.5 5h13l2.5 7v5a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-5Z", "M3 12h18M7 15.5h.01M10.5 15.5h.01"],
        ["pkg"] = ["M12 3 20 7.5v9L12 21l-8-4.5v-9Z", "M4 7.5 12 12l8-4.5M12 12v9"],
        ["sparkle"] = ["M12 3.5 13.8 10.2 20.5 12l-6.7 1.8L12 20.5l-1.8-6.7L3.5 12l6.7-1.8Z"],
        ["chev"] = ["M9.5 6 15.5 12l-6 6"],
        ["chevd"] = ["M6 9.5 12 15.5l6-6"],
        ["finder"] = ["M7 3.5h10a3.5 3.5 0 0 1 3.5 3.5v10a3.5 3.5 0 0 1-3.5 3.5h-10a3.5 3.5 0 0 1-3.5-3.5v-10a3.5 3.5 0 0 1 3.5-3.5Z", "M12 3.5c-1.4 3-1.6 6.5-1 9.5h2.2M8 9v1.5M16 9v1.5M7.5 15.5c2.6 1.8 6.4 1.8 9 0"],
        ["menubar"] = ["M5 7h12M5 12h9M5 17h5", "F:M19.8 17a1.8 1.8 0 1 1-3.6 0a1.8 1.8 0 1 1 3.6 0Z"],
        ["terminal"] = ["M5 4h14a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2h-14a2 2 0 0 1-2-2v-12a2 2 0 0 1 2-2Z", "M7 9l3 3-3 3M12.5 15h4.5"],
        ["checkc"] = ["F:M21 12a9 9 0 1 1-18 0a9 9 0 1 1 18 0Z", "W:M7.8 12.3 10.7 15.2 16.4 9.3"],
        ["xc"] = ["F:M21 12a9 9 0 1 1-18 0a9 9 0 1 1 18 0Z", "W:M9 9l6 6M15 9l-6 6"],
        ["warn"] = ["F:M10.3 4.2a2 2 0 0 1 3.4 0l7.4 12.9a2 2 0 0 1-1.7 3H4.6a2 2 0 0 1-1.7-3Z", "W:M12 9v4.5", "W:M12 16.7v.2"],
    };

    /// <summary>Ikonet som SVG (til HTML-gengivelser og test).</summary>
    public static string Svg(string name, string color = "currentColor", double stroke = 1.7)
    {
        var parts = Paths[name].Select(p =>
            p.StartsWith("F:") ? $"<path d=\"{p[2..]}\" fill=\"{color}\" stroke=\"none\"/>"
            : p.StartsWith("W:") ? $"<path d=\"{p[2..]}\" fill=\"none\" stroke=\"#fff\" stroke-width=\"2\"/>"
            : $"<path d=\"{p}\"/>");
        return $"<svg viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"{color}\" stroke-width=\"{stroke.ToString(System.Globalization.CultureInfo.InvariantCulture)}\" stroke-linecap=\"round\" stroke-linejoin=\"round\">{string.Concat(parts)}</svg>";
    }
}

/// <summary>Teksterne – ens på alle platforme (kun ordene for Mac'en/pc'en/maskinen skifter).</summary>
public static class SetupTexts
{
    public const string ServerPlaceholder = "fx 192.168.1.10 eller fleetdeck.local";
    public const string KeyPlaceholder = "Indsæt nøglen";
    public const string WhereIsAddress = "Adressen står i Fleetdeck under Tilføj maskine.";
    /// <summary>"Find automatisk" fandt intet: typisk Fleetdeck i Docker med bridge-net (broadcasts når ikke containeren) eller et andet net.</summary>
    public const string NoServerTitle = "Ingen server fundet automatisk",
        NoServerText = "Det sker, hvis Fleetdeck kører i Docker uden sit eget netværk, eller hvis serveren er på et andet net. " +
                       "Skriv serverens adresse (fx http://192.168.1.10:8585).";
    public const string OnNetwork = "På netværket", OrTypeAddress = "Eller skriv adressen", SearchingRow = "Søger på netværket…", SearchAgain = "Søg igen";
    public const string KeySection = "Tilmeldingsnøgle", MessageSection = "Besked til administratoren", Optional = "(valgfri)";
    public const string HaveKey = "Jeg har en nøgle", HaveKeySub = "Står under Tilføj maskine";
    public const string AskAccess = "Anmod om adgang", AskAccessSub = "Administratoren godkender";
    public const string PasteTitle = "Har du fået et link eller en kommando?", PasteText = "Indsæt det, så udfyldes server, nøgle og profil af sig selv.", Paste = "Indsæt";
    public const string ExtensionsSection = "Tilvalg", ProfileSection = "Profil";
    public const string NoProfile = "Ingen profil", NoProfileSub = "Kun overvågning og opdateringer";
    public const string CopyCode = "Kopiér kode", Details = "Tekniske detaljer", Help = "Hjælp";
    public const string Back = "Tilbage", Continue = "Fortsæt", Close = "Luk", TryAgain = "Prøv igen", AskAgain = "Anmod igen", CheckAgain = "Tjek igen";
    public const string ChangeServer = "Skift server eller profil…";
    public const string Uninstall = "Afinstallér Fleetdeck…", TestConnection = "Test forbindelse", SaveAndReconnect = "Gem og forbind igen";
    /// <summary>Installeret → Oversigt (Mac): diagnosefilen, ejeren kan sende (se MacDiagnose).</summary>
    public const string DiagnoseSection = "Fejlsøgning", SaveDiagnose = "Gem diagnose…",
        DiagnoseNote = "Samler agentens log, status for ikonet og tjenesten og appens version i én fil på skrivebordet – uden nøgler og adgangskoder. " +
                       "Send filen til den, der hjælper dig.";

    /// <summary>Trinene i sidebjælken. Sidste trin er "Tilladelser" på Mac (Fuld diskadgang) og "Færdig" ellers.</summary>
    public static IReadOnlyList<SetupStepInfo> Steps(SetupOs os) =>
    [
        new(SetupStep.Welcome, "Velkommen", "sparkle", SetupColors.Fleet),
        new(SetupStep.Server, "Server", "server", SetupColors.Blue),
        new(SetupStep.Enroll, "Tilmelding", "key", SetupColors.Gray),
        new(SetupStep.Profile, "Profil og tilvalg", "stack", SetupColors.Purple),
        new(SetupStep.Install, "Installation", "down", SetupColors.Green),
        os == SetupOs.Mac ? new(SetupStep.Finish, "Tilladelser", "hand", SetupColors.Indigo) : new(SetupStep.Finish, "Færdig", "check", SetupColors.Indigo),
    ];

    /// <summary>Siderne i "Installeret"-oversigten (Skift server/Opsætning).</summary>
    public static IReadOnlyList<SetupSection> Sections(SetupOs os)
    {
        var l = new List<SetupSection>
        {
            new("overview", "Oversigt", "laptop", SetupColors.Gray),
            new("server", "Server og tilmelding", "server", SetupColors.Blue),
            new("profile", "Profil", "stack", SetupColors.Purple),
            new("extensions", "Tilvalg", "pkg", SetupColors.Orange),
        };
        if (os == SetupOs.Mac) l.Add(new("access", "Tilladelser", "hand", SetupColors.Indigo));
        else l.RemoveAll(x => x.Id == "profile");   // Windows: profilen skiftes med "Skift server eller profil…" (guiden)
        return l;
    }

    /// <summary>"Trin 2 af 6 · Server" (Linux-dialogernes titel og terminalens overskrifter).</summary>
    public static string StepLabel(SetupOs os, SetupStep step)
    {
        var steps = Steps(os);
        var i = steps.ToList().FindIndex(s => s.Step == step);
        return $"Trin {i + 1} af {steps.Count} · {steps[i].Title}";
    }

    public static string Subtitle(SetupOs os) => os switch { SetupOs.Mac => "Opsætning af denne Mac", SetupOs.Windows => "Opsætning af denne pc", _ => "Opsætning af denne maskine" };

    /// <summary>De tre punkter på velkomstskærmen.</summary>
    public static IReadOnlyList<(string Icon, string Title, string Text)> Features(SetupOs os)
    {
        var w = SetupWords.For(os);
        return
        [
            ("down", "Opdateringer klares for dig", os == SetupOs.Mac ? "Programmer og macOS holdes opdateret i baggrunden." : os == SetupOs.Windows ? "Programmer, drivere og Windows holdes opdateret i baggrunden." : "Pakker og programmer holdes opdateret i baggrunden."),
            ("pulse", "Overblik og hjælp på afstand", $"Administratoren kan se, hvordan {w.DeviceLower} har det, og hjælpe, når noget driller."),
            ("lock", "Du bestemmer", "Intet installeres, før du har sagt god for det med din adgangskode."),
        ];
    }

    /// <summary>De tre trin i "Anmod om adgang".</summary>
    public static readonly string[] RequestFlow = ["Du sender en anmodning", "Du får en kode, som du giver administratoren", "Administratoren godkender og vælger profil"];

    /// <summary>Linjerne på "Installerer"-skærmen (den sidste afhænger af platformen).</summary>
    /// <remarks>Mac henter agenten fra serveren og tjekker signaturen; Windows og Linux installerer det program, der allerede kører.</remarks>
    public static IReadOnlyList<(string Title, string? Sub)> InstallSteps(SetupOs os) => os == SetupOs.Mac
        ?
        [
            ("Henter agenten fra serveren", null),
            ("Tjekker signaturen", null),
            ("Installerer", SetupWords.For(os).Password),
            ("Venter på, at serveren bekræfter forbindelsen", "Op til 2 minutter."),
        ]
        :
        [
            (os == SetupOs.Windows ? "Kopierer Fleetdeck til Programmer" : "Kopierer agenten til /opt/fleetdeck", null),
            ("Installerer tjenesten og tilmelder " + SetupWords.For(os).DeviceLower, SetupWords.For(os).Password),
            ("Venter på, at serveren bekræfter forbindelsen", "Op til 2 minutter."),
        ];

    /// <summary>"Kan du ikke se ikonet?" på Færdig-skærmen.</summary>
    public static (string Title, string Text) TrayTip(SetupOs os) => os switch
    {
        SetupOs.Mac => ("Kan du ikke se ikonet?", "Det kan gemme sig bag kameraet (notch) eller være slået fra under Systemindstillinger → Menulinje. Fleetdeck kan altid åbnes fra Programmer."),
        SetupOs.Windows => ("Kan du ikke se ikonet?", "Det kan gemme sig under pilen ^ ved uret. Træk det ud på proceslinjen, så det altid ses."),
        _ => ("Status og log", "Se status med \"fleetdeck-agent status\" og loggen med \"journalctl -u fleetdeck-agent\"."),
    };

    /// <summary>"?"-hjælpen nederst til venstre: en kort forklaring til trinet.</summary>
    public static (string Title, string Text) HelpFor(SetupOs os, SetupStep step)
    {
        var w = SetupWords.For(os);
        return step switch
        {
            SetupStep.Welcome => ("Om opsætningen", $"Fleetdeck installerer en lille tjeneste, der holder {w.This} opdateret og lader administratoren hjælpe på afstand. " +
                "Har du fået et link eller en kommando fra Fleetdeck, så indsæt det – så udfyldes resten af sig selv."),
            SetupStep.Server => ("Hvor finder jeg adressen?", $"Adressen står i Fleetdeck under Maskiner → Tilføj maskine. Kun IP-adressen er nok, fx 192.168.1.10. " +
                $"{w.Device} skal være på samme netværk som serveren for at finde den automatisk."),
            SetupStep.Enroll => ("Nøgle eller anmodning?", "Tilmeldingsnøglen står i Fleetdeck under Tilføj maskine. Har du ingen nøgle, så vælg \"Anmod om adgang\": " +
                "administratoren får besked, sammenligner koden og vælger profilen."),
            SetupStep.Profile => ("Hvad er en profil?", "En profil er en liste over programmer og indstillinger, som Fleetdeck installerer og holder opdateret. " +
                "Vælg \"Ingen profil\", hvis maskinen kun skal overvåges og opdateres. Tilvalg kan også slås til senere."),
            SetupStep.Install => ("Hvad sker der nu?", $"Agenten hentes fra serveren, og dens signatur tjekkes, før den installeres. {w.Password} " +
                "Bagefter venter vinduet op til 2 minutter på, at serveren bekræfter forbindelsen."),
            _ => os == SetupOs.Mac ? ("Fuld diskadgang og menulinjen", MacSetup.MenuBarHint)
                : os == SetupOs.Windows ? ("Ikonet ved uret", "Fleetdeck ligger ved uret nederst til højre. Kan du ikke se det, så tryk på pilen ^ og træk ikonet ud på proceslinjen.")
                : ("Status og log", "fleetdeck-agent status viser forbindelsen. Loggen: journalctl -u fleetdeck-agent."),
        };
    }

    public static string Pick(string? server) => server is { Length: > 0 } s ? s : "serveren";

    /// <summary>Overskrift, tekst, ikon og knapper til en skærm.</summary>
    public static SetupPage Page(SetupScreen screen, SetupContext c)
    {
        var w = SetupWords.For(c.Os);
        var srv = Pick(c.Server);
        const string findTitle = "Find din Fleetdeck-server";
        var findText = $"{w.Device} leder efter Fleetdeck på netværket. Vælg serveren, eller skriv adressen selv.";
        var enrollTitle = $"Tilmeld {w.DeviceLower}";
        var enrollText = $"{Cap(srv)} skal vide, at {w.DeviceLower} må komme på.";
        var step = SetupFlow.StepOf(screen);
        SetupPage P(string icon, string color, string title, string text, string? primary, string? secondary) => new(screen, step, icon, color, title, text, primary, secondary);
        return screen switch
        {
            SetupScreen.Welcome => P("logo", SetupColors.Fleet, "Velkommen til Fleetdeck",
                $"Fleetdeck holder {w.This} opdateret og sund – helt af sig selv. Opsætningen tager et par minutter.", Continue, null),
            SetupScreen.Searching => P("server", SetupColors.Blue, findTitle, findText, Continue, Back),
            SetupScreen.Found => P("server", SetupColors.Blue, findTitle,
                c.Found == 1 ? $"{w.Device} fandt Fleetdeck på netværket. Tryk Fortsæt, eller skriv en anden adresse."
                             : $"{w.Device} fandt {Count(c.Found)} servere. Vælg den, du har fået besked om.", Continue, Back),
            SetupScreen.NoServer => P("server", SetupColors.Blue, findTitle, findText, Continue, Back),
            SetupScreen.ServerError => P("server", SetupColors.Blue, findTitle, "Skriv adressen på serveren, eller vælg den på netværket.", TryAgain, Back),
            SetupScreen.Key or SetupScreen.KeyError => P("key", SetupColors.Gray, enrollTitle, enrollText, Continue, Back),
            SetupScreen.Request => P("key", SetupColors.Gray, enrollTitle, enrollText, "Send anmodning", Back),
            SetupScreen.Waiting => P("hour", SetupColors.Orange, "Venter på godkendelse",
                $"Giv denne kode til administratoren af {srv}. Anmodningen står allerede i Fleetdeck.", Close, null),
            SetupScreen.Rejected => c.Expired
                ? P("hour", SetupColors.Gray, "Anmodningen er udløbet", $"Administratoren af {srv} nåede ikke at svare på anmodningen med kode {c.Code ?? "–"}.", AskAgain, Close)
                : P("xmark", SetupColors.Red, "Anmodningen blev ikke godkendt", $"Administratoren af {srv} afviste anmodningen med kode {c.Code ?? "–"}.", AskAgain, Close),
            SetupScreen.Profile => P("stack", SetupColors.Purple, "Vælg profil og tilvalg", "Profilen bestemmer, hvilke programmer Fleetdeck holder styr på.",
                c.Installed ? SaveAndReconnect : "Installér", Back),
            SetupScreen.Installing => P("down", SetupColors.Green, "Installerer Fleetdeck", $"Det tager normalt under et minut. Hold {w.DeviceLower} vågen imens.", "Installerer…", null),
            SetupScreen.NoConnection => P("down", SetupColors.Green, "Næsten færdig", "Agenten er installeret, men har ikke fået forbindelse til serveren endnu.", CheckAgain, Close),
            SetupScreen.Enrolled => P("hand", SetupColors.Indigo, "Giv Fleetdeck Fuld diskadgang",
                "Så kan Fleetdeck se og opdatere alle programmer. macOS kræver, at du selv slår det til.", Continue, "Gør det senere"),
            SetupScreen.Done => c.Reboot
                ? P("warn", SetupColors.Orange, "Næsten klar", $"Fleetdeck er installeret og forbundet. En genstart af {w.DeviceLower} gør installationen færdig.", "Genstart nu", "Senere")
                : P("checkc", SetupColors.Green, "Alt er klar", $"Fleetdeck passer nu på {w.This}. Du finder den {w.Tray}.", "Færdig", null),
            SetupScreen.Installed => c.Os == SetupOs.Mac
                ? P("laptop", SetupColors.Gray, "Oversigt", $"Fleetdeck er installeret på {w.This}.", SaveAndReconnect, TestConnection)
                : P("laptop", SetupColors.Gray, "Oversigt", $"Fleetdeck er installeret på {w.This}.", ChangeServer, null),
            _ => throw new ArgumentOutOfRangeException(nameof(screen)),
        };
    }

    private static string Cap(string s) => s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..] : s;

    private static string Count(int n) => n switch { 2 => "to", 3 => "tre", 4 => "fire", 5 => "fem", _ => n.ToString() };

    /// <summary>Teksten ved en installeret server i "fandt"-listen: "192.168.1.10:8585 · version 3.41.3".</summary>
    public static string FoundLine(Discovery.Found f) => $"{HostPort(f.Url)} · version {f.Version}";

    public static string HostPort(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? $"{u.Host}:{u.Port}" : url;

    public static string ProfileLine(ProfileOption p) => $"{p.Packages} programmer · {p.Machines} maskiner";

    /// <summary>"Tilmeldt som X · profil Y".</summary>
    public static string EnrolledAs(string? machine, string? profile) =>
        $"Tilmeldt som {(machine is { Length: > 0 } m ? m : "–")}" + (profile is { Length: > 0 } p ? $" · profil {p}" : " · ingen profil");

    /// <summary>Linjen på "Venter"-skærmen ud fra anmodningens tilstand (det, tjenesten skriver i status.json).</summary>
    public static (string Title, string Text, SetupTone Tone) WaitingRow(SetupOs os, TrayAccessRequest? r) => r switch
    {
        { Problem.Length: > 0 } when AccessStates.Open(r.State) => ("Kan ikke nå serveren", $"Prøver igen af sig selv: {r.Problem}", SetupTone.Warn),
        { State: AccessStates.Approved or AccessStates.Issued } => ("Godkendt – tilmelder", "Administratoren har godkendt. Et øjeblik…", SetupTone.Ok),
        _ => ("Venter på svar", $"{SetupWords.For(os).Device} spørger serveren hvert andet sekund.", SetupTone.Info),
    };
}

/// <summary>
/// Trin-/tilstandsmaskinen. Platformene laver selve arbejdet (søg, test, installér, anmod) og melder resultatet herind; modellen siger,
/// hvilken skærm der skal vises, hvilket trin sidebjælken står på, hvilke knapper der virker, og hvilken fejl der hører til hvilket felt.
/// </summary>
public sealed class SetupFlow
{
    public SetupOs Os { get; }
    public SetupScreen Screen { get; private set; }
    /// <summary>Agenten var installeret, da vinduet åbnede (oversigten; "Gem og forbind igen").</summary>
    public bool Installed { get; private set; }
    /// <summary>"Anmod om adgang" er valgt på Tilmelding-trinet.</summary>
    public bool Ask { get; private set; }
    public IReadOnlyList<Discovery.Found> Found { get; private set; } = [];
    public string Server { get; set; } = "";
    public SetupCheck? ServerCheck { get; private set; }
    public SetupCheck? KeyCheck { get; private set; }
    /// <summary>Fejl fra installationen/anmodningen (vises ved knapperne på den skærm, den kom fra).</summary>
    public SetupCheck? ActionError { get; private set; }
    public string? Code { get; private set; }
    public string? Reason { get; private set; }
    public bool Expired { get; private set; }
    public bool Reboot { get; private set; }
    public bool Granted { get; private set; }
    private readonly Stack<SetupScreen> _history = new();

    public SetupFlow(SetupOs os, bool installed = false)
    {
        Os = os; Installed = installed;
        Screen = installed ? SetupScreen.Installed : SetupScreen.Welcome;
    }

    public SetupContext Context(string? machine = null, string? profile = null) =>
        new(Os, ServerName(), Found.Count, Code, machine, profile, Reboot, Expired, Installed);

    public SetupPage Page(string? machine = null, string? profile = null) => SetupTexts.Page(Screen, Context(machine, profile));

    /// <summary>Navnet på serveren, som det står i teksterne: det fundne navn, ellers værtsnavnet.</summary>
    public string? ServerName()
    {
        if (Server.Length == 0) return null;
        var url = Discovery.Normalize(Server);
        if (Found.FirstOrDefault(f => string.Equals(Discovery.Normalize(f.Url), url, StringComparison.OrdinalIgnoreCase)) is { Name.Length: > 0 } f) return f.Name;
        return SetupTexts.HostPort(url);
    }

    /// <summary>Trinet, en skærm hører til i sidebjælken.</summary>
    public static SetupStep StepOf(SetupScreen s) => s switch
    {
        SetupScreen.Welcome => SetupStep.Welcome,
        SetupScreen.Searching or SetupScreen.Found or SetupScreen.NoServer or SetupScreen.ServerError => SetupStep.Server,
        SetupScreen.Key or SetupScreen.KeyError or SetupScreen.Request or SetupScreen.Waiting or SetupScreen.Rejected => SetupStep.Enroll,
        SetupScreen.Profile => SetupStep.Profile,
        SetupScreen.Installing or SetupScreen.NoConnection => SetupStep.Install,
        SetupScreen.Enrolled or SetupScreen.Done or SetupScreen.Installed => SetupStep.Finish,
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };

    public SetupStep Step => StepOf(Screen);

    /// <summary>Flueben, aktuelt eller endnu ikke.</summary>
    public SetupStepState StateOf(SetupStep step)
    {
        if (Screen == SetupScreen.Done && !Reboot) return SetupStepState.Done;
        // anmodning: profilen vælges af administratoren og installationen sker sammen med anmodningen
        if (Ask && Screen is SetupScreen.Waiting && step is SetupStep.Install) return SetupStepState.Done;
        return step < Step ? SetupStepState.Done : step == Step ? SetupStepState.Current : SetupStepState.Todo;
    }

    /// <summary>Lille undertekst under et trin i sidebjælken.</summary>
    public string? StepNote(SetupStep step) => step switch
    {
        SetupStep.Enroll when Screen == SetupScreen.Waiting => "Venter på godkendelse",
        SetupStep.Enroll when Screen == SetupScreen.Rejected => Expired ? "Udløbet" : "Afvist",
        SetupStep.Profile when Ask && Screen is SetupScreen.Request or SetupScreen.Waiting => "Vælges af administratoren",
        _ => null,
    };

    /// <summary>Fejlen, der hører til et felt (vises lige under feltet med rød ring).</summary>
    public SetupCheck? ErrorFor(SetupField field) =>
        ServerCheck is { Ok: false } s && s.Field == field ? s
        : KeyCheck is { Ok: false } k && k.Field == field ? k
        : ActionError is { Ok: false } a && a.Field == field ? a : null;

    /// <summary>Må hovedknappen bruges? (adresse/nøgle udfyldt, profil valgt, adgang givet …)</summary>
    public bool CanContinue(bool hasAddress = false, bool hasKey = false, bool profileChosen = false) => Screen switch
    {
        SetupScreen.Searching or SetupScreen.Found or SetupScreen.NoServer or SetupScreen.ServerError => hasAddress,
        SetupScreen.Key => hasKey || KeyCheck is { Ok: true },
        SetupScreen.KeyError => hasKey,
        SetupScreen.Profile => profileChosen,
        SetupScreen.Installing => false,
        SetupScreen.Enrolled => Granted,
        _ => true,
    };

    private void Go(SetupScreen next, bool remember = true)
    {
        if (remember && next != Screen && Screen is not (SetupScreen.Installing or SetupScreen.Searching)) _history.Push(Screen);
        Screen = next;
    }

    // ---------------------------------------------------------------- hændelser

    /// <summary>Velkommen → søg på netværket. Er serveren allerede udfyldt (indsat link), springes søgningen ikke over – den viser bare adressen.</summary>
    public void Start() { ActionError = null; Go(SetupScreen.Searching); }

    public void SearchStarted() { ServerCheck = null; Screen = SetupScreen.Searching; }

    /// <summary>Søgningen er færdig: én server udfylder adressen, flere vises som liste, ingen giver "Fandt ingen server".</summary>
    public void SearchDone(IReadOnlyList<Discovery.Found> found)
    {
        Found = found;
        if (found.Count == 1 && Server.Length == 0) Server = found[0].Url;
        Screen = found.Count > 0 ? SetupScreen.Found : SetupScreen.NoServer;
    }

    /// <summary>Den valgte server er tjekket (uden nøgle): videre til Tilmelding – eller tilbage med fejlen ved adressefeltet.</summary>
    public void ServerTested(SetupCheck c)
    {
        ServerCheck = c;
        if (Screen == SetupScreen.Installed) return; // oversigten: resultatet vises på stedet
        if (c.Ok) { KeyCheck = null; Go(Ask ? SetupScreen.Request : SetupScreen.Key); }
        else Go(SetupScreen.ServerError, remember: Screen != SetupScreen.ServerError);
    }

    /// <summary>Nøglen er testet: "Nøglen passer" (bliv på skærmen, Fortsæt henter profilerne) eller fejl ved nøglefeltet (eller adressen).</summary>
    public void KeyTested(SetupCheck c)
    {
        if (Screen == SetupScreen.Installed)
        {
            // Oversigten ("Test forbindelse"): bliv på oversigten, resultatet vises ved feltet, det handler om.
            if (c.Field == SetupField.Server) { ServerCheck = c; KeyCheck = null; } else KeyCheck = c;
            return;
        }
        KeyCheck = c;
        if (c.Ok) { Screen = SetupScreen.Key; return; }
        if (c.Field == SetupField.Server) { ServerCheck = c; KeyCheck = null; Screen = SetupScreen.ServerError; return; }
        Screen = SetupScreen.KeyError;
    }

    /// <summary>Nøglen er ændret efter testen: test igen.</summary>
    public void KeyChanged()
    {
        if (KeyCheck is null) return;
        KeyCheck = null;
        if (Screen == SetupScreen.KeyError) Screen = SetupScreen.Key;
    }

    /// <summary>Server er ændret efter testen.</summary>
    public void ServerChanged() { ServerCheck = null; KeyCheck = null; }

    /// <summary>"Jeg har en nøgle" / "Anmod om adgang".</summary>
    public void ChooseAsk(bool ask)
    {
        Ask = ask; ActionError = null;
        if (Screen is SetupScreen.Key or SetupScreen.KeyError or SetupScreen.Request)
            Screen = ask ? SetupScreen.Request : KeyCheck is { Ok: false } ? SetupScreen.KeyError : SetupScreen.Key;
    }

    /// <summary>Profilerne er hentet: Profil og tilvalg.</summary>
    public void ProfilesLoaded() { ActionError = null; Go(SetupScreen.Profile); }

    /// <summary>Profilerne kunne ikke hentes, selv om nøglen passer.</summary>
    public void ProfilesFailed() => KeyCheck = SetupCheck.Bad(SetupField.Key, "Profilerne kunne ikke hentes.", "Forbindelsen virker, men serveren sendte ingen profiler. Prøv igen.");

    public void InstallStarted() { ActionError = null; Go(SetupScreen.Installing); }

    /// <summary>
    /// Installationen er slut. connected: tjenesten meldte sig forbundet. reboot: forbundet, men en genstart gør det færdigt (Windows, Smart
    /// App Control). error: installationen fejlede – tilbage til skærmen før, med fejlen ved knapperne (eller ved nøglen, hvis den blev afvist).
    /// </summary>
    public void InstallFinished(bool connected, bool reboot = false, SetupCheck? error = null)
    {
        if (error is not null)
        {
            ActionError = error;
            Screen = _history.Count > 0 ? _history.Pop() : SetupScreen.Profile;
            if (error.Field == SetupField.Key) { KeyCheck = error; ActionError = null; Screen = SetupScreen.KeyError; }
            return;
        }
        Reboot = reboot;
        Screen = connected ? AfterConnected() : SetupScreen.NoConnection;
    }

    /// <summary>En handling på skærmen fejlede (fx anmodningen kunne ikke sendes): fejlen ved knapperne – eller ved nøglen, hvis den blev afvist.</summary>
    public void ActionFailed(SetupCheck error)
    {
        if (error.Field == SetupField.Key) { KeyCheck = error; Screen = SetupScreen.KeyError; return; }
        ActionError = error;
    }

    /// <summary>Tilmeldt: Fuld diskadgang på Mac, ellers færdig.</summary>
    public SetupScreen AfterConnected() => Os == SetupOs.Mac && !Granted ? SetupScreen.Enrolled : SetupScreen.Done;

    /// <summary>Hvornår anmodningen blev sendt (før installationen startede): ældre status ignoreres, mens der ventes.</summary>
    public DateTime? RequestedAt { get; private set; }

    /// <summary>
    /// Anmodningen er sendt (agenten installeret uden nøgle): vis koden. sentAt: tidspunktet lige før installationen/anmodningen startede –
    /// en status.json skrevet før det (fx "tilmeldt" fra før en "Anmod igen" med --force) springes over.
    /// </summary>
    public void RequestSent(string code, DateTime? sentAt = null)
    {
        Code = code; ActionError = null; Expired = false; Reason = null; RequestedAt = sentAt; Screen = SetupScreen.Waiting;
    }

    /// <summary>Tjenestens status mens der ventes: godkendt og tilmeldt, afvist/udløbet – eller stadig åben (ny kode?).</summary>
    public void RequestUpdate(TrayStatus s)
    {
        if (Screen != SetupScreen.Waiting) return;
        if (RequestedAt is { } at && s.Written < at) return;   // gammel status (fra før anmodningen): ikke "Alt er klar" før godkendelsen
        if (s.Enrolled || s.Connected) { Screen = AfterConnected(); return; }
        if (s.AccessRequest is not { } r) return;
        if (r.Code.Length > 0) Code = r.Code;
        if (AccessStates.Open(r.State)) return;
        Expired = r.State == AccessStates.Expired;
        Reason = r.Reason;
        Screen = SetupScreen.Rejected;
    }

    /// <summary>"Anmod igen" efter afvisning.</summary>
    public void AskAgain() { Ask = true; Screen = SetupScreen.Request; }

    /// <summary>Fuld diskadgang er givet (Mac).</summary>
    public void AccessGranted() => Granted = true;

    /// <summary>"Fortsæt" / "Gør det senere" på Tilladelser → Færdig.</summary>
    public void Finish() { if (Screen is SetupScreen.Enrolled or SetupScreen.NoConnection) Screen = SetupScreen.Done; }

    /// <summary>Afinstalleret fra oversigten: forfra som en ny installation.</summary>
    public void Uninstalled()
    {
        Installed = false; Ask = false; ServerCheck = KeyCheck = ActionError = null; Code = null; _history.Clear();
        Screen = SetupScreen.Welcome;
    }

    /// <summary>Tilbage-knappen.</summary>
    public void Back()
    {
        ActionError = null;
        Screen = Screen switch
        {
            SetupScreen.Searching or SetupScreen.Found or SetupScreen.NoServer or SetupScreen.ServerError => Installed ? SetupScreen.Installed : SetupScreen.Welcome,
            SetupScreen.Key or SetupScreen.KeyError or SetupScreen.Request => Found.Count > 0 ? SetupScreen.Found : SetupScreen.NoServer,
            SetupScreen.Profile => KeyCheck is { Ok: false } ? SetupScreen.KeyError : SetupScreen.Key,
            _ => Screen,
        };
        _history.Clear();
    }

    /// <summary>Alle skærme (til test og til skærmbillederne).</summary>
    public static IReadOnlyList<SetupScreen> AllScreens => Enum.GetValues<SetupScreen>();

    /// <summary>Skærmene, en platform skal kunne vise (Enrolled = Fuld diskadgang findes kun på Mac).</summary>
    public static IReadOnlyList<SetupScreen> ScreensFor(SetupOs os) =>
        AllScreens.Where(s => os == SetupOs.Mac || s != SetupScreen.Enrolled).ToList();
}

/// <summary>Tjek af server og nøgle – fælles for de tre opsætningsvinduer (før havde hver sin kopi).</summary>
public static class SetupProbe
{
    /// <summary>
    /// Kan serveren nås, er det Fleetdeck (3.x), og passer nøglen? Uden nøgle tjekkes kun serveren. Fejl knyttes til feltet, de handler om,
    /// og certifikatfejl får en dansk forklaring med .NET's tekst under "Tekniske detaljer".
    /// </summary>
    public static async Task<SetupCheck> Test(HttpClient http, string url, string? key, SetupOs os, CancellationToken ct = default)
    {
        var w = SetupWords.For(os);
        if (url.Length == 0) return SetupCheck.Bad(SetupField.Server, "Skriv serverens adresse.", SetupTexts.WhereIsAddress);
        try
        {
            using var r0 = await http.GetAsync(url + "/api/health", ct);
            string? version = null;
            if (r0.IsSuccessStatusCode)
                try
                {
                    using var doc = JsonDocument.Parse(await r0.Content.ReadAsStringAsync(ct));
                    if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("ok", out _)
                        && doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String) version = v.GetString();
                }
                catch (JsonException) { }
            if (version is null)
                return SetupCheck.Bad(SetupField.Server, "Det er ikke en Fleetdeck-server.", $"{url} svarer, men det er ikke Fleetdeck. Tjek adressen i Fleetdeck under Tilføj maskine.");
            // en WinStyring 2.x-server ville erstatte agenten med sin gamle (og maskinen melder sig så aldrig): stop her
            if (Version.TryParse(version.Split('-', '+')[0], out var sv) && sv.Major < 3)
                return SetupCheck.Bad(SetupField.Server, "Serveren skal opgraderes først.",
                    $"{url} kører WinStyring {version}. Opgradér serveren til Fleetdeck først – ellers erstatter den denne agent med sin gamle.");
            if (string.IsNullOrWhiteSpace(key))
                return SetupCheck.Good("Forbindelse OK.", $"Fleetdeck {version} på {url}.", version);
            using var r = await http.GetAsync($"{url}/{w.Script}?key={Uri.EscapeDataString(key.Trim())}", ct);
            return r.IsSuccessStatusCode
                ? SetupCheck.Good("Nøglen passer.", $"Forbundet til {url}, version {version}.", version)
                : SetupCheck.Bad(SetupField.Key, "Nøglen passer ikke.", "Hent den igen i Fleetdeck under Tilføj maskine – den kan være skiftet.");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException)
        {
            return Unreachable(url, e, os);
        }
    }

    /// <summary>Forklaringen på en netværksfejl – ved adressefeltet.</summary>
    public static SetupCheck Unreachable(string url, Exception e, SetupOs os)
    {
        var w = SetupWords.For(os);
        if (IsCertificate(e))
            return SetupCheck.Bad(SetupField.Server, CertificateTitle,
                $"{w.Device} stoler ikke på serverens HTTPS-certifikat. Tjek adressen i Fleetdeck under Tilføj maskine, eller bed administratoren om et gyldigt certifikat.",
                e.InnerException?.Message is { Length: > 0 } inner ? $"{e.Message} {inner}" : e.Message);
        if (e is UriFormatException || e is InvalidOperationException)
            return SetupCheck.Bad(SetupField.Server, "Adressen er ikke gyldig.", "Skriv kun IP-adressen eller navnet, fx 192.168.1.10 eller fleetdeck.local.", e.Message);
        if (e is TaskCanceledException)
            return SetupCheck.Bad(SetupField.Server, "Serveren svarer ikke.", $"Der kom intet svar fra {url}. Er adressen rigtig, og er {w.DeviceLower} på samme netværk?", e.Message);
        if (Find<SocketException>(e) is { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain })
            return SetupCheck.Bad(SetupField.Server, "Navnet kan ikke findes.", $"{w.Device} kan ikke slå {SetupTexts.HostPort(url)} op. Prøv med IP-adressen i stedet.", e.Message);
        return SetupCheck.Bad(SetupField.Server, $"Kan ikke nå {url}.", $"Er adressen rigtig, og er {w.DeviceLower} på samme netværk?", e.Message);
    }

    public const string CertificateTitle = "Serverens certifikat kan ikke bekræftes";

    /// <summary>Er fejlen et certifikatproblem? (vises som advarsel med "Tekniske detaljer")</summary>
    public static bool IsCertificateText(SetupCheck c) => c.Title == CertificateTitle;

    /// <summary>TLS-/certifikatfejl (selvsigneret, udløbet, forkert navn).</summary>
    public static bool IsCertificate(Exception e) =>
        e is HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } || Find<AuthenticationException>(e) is not null;

    private static T? Find<T>(Exception? e) where T : Exception
    {
        for (; e is not null; e = e.InnerException) if (e is T t) return t;
        return null;
    }

    /// <summary>Exit-koden fra "install --request" uden for 0/9: forklaringen (den sidste linje i agentens output).</summary>
    public static string LastLine(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
}

/// <summary>
/// SVG-stier (fra <see cref="SetupIcons"/>) som kubiske Bézier-figurer – så Windows kan tegne de samme ikoner med GDI+ (GraphicsPath.AddBeziers)
/// uden en SVG-motor. Kan M, L, H, V, C, S, Q, A og Z (store og små bogstaver); buer omregnes til Bézier-kurver.
/// </summary>
public static class SvgPath
{
    /// <summary>En figur: startpunkt + punkter i tripler (kontrol 1, kontrol 2, slut); Closed = Z.</summary>
    public sealed record Figure(List<(double X, double Y)> Points, bool Closed);

    public static List<Figure> Parse(string d)
    {
        var figs = new List<Figure>();
        List<(double X, double Y)>? cur = null;
        double x = 0, y = 0, sx = 0, sy = 0, lcx = 0, lcy = 0;
        char cmd = ' ', prev = ' ';
        var i = 0;
        void Line(double nx, double ny) { cur!.Add((x + (nx - x) / 3, y + (ny - y) / 3)); cur.Add((x + 2 * (nx - x) / 3, y + 2 * (ny - y) / 3)); cur.Add((nx, ny)); x = nx; y = ny; }
        void Cubic(double c1x, double c1y, double c2x, double c2y, double nx, double ny) { cur!.Add((c1x, c1y)); cur.Add((c2x, c2y)); cur.Add((nx, ny)); lcx = c2x; lcy = c2y; x = nx; y = ny; }
        while (true)
        {
            while (i < d.Length && (char.IsWhiteSpace(d[i]) || d[i] == ',')) i++;
            if (i >= d.Length) break;
            if (char.IsLetter(d[i])) { cmd = d[i++]; }
            else if (cmd == ' ') throw new FormatException($"SVG-sti uden kommando: {d}");
            var rel = char.IsLower(cmd);
            double ox = rel ? x : 0, oy = rel ? y : 0;
            switch (char.ToUpperInvariant(cmd))
            {
                case 'M':
                {
                    var (nx, ny) = (Num() + ox, Num() + oy);
                    cur = [(nx, ny)]; figs.Add(new Figure(cur, false));
                    x = sx = nx; y = sy = ny;
                    cmd = rel ? 'l' : 'L';   // flere tal efter M er linjer
                    break;
                }
                case 'L': Line(Num() + ox, Num() + oy); break;
                case 'H': Line(Num() + ox, y); break;
                case 'V': Line(x, Num() + oy); break;
                case 'C': { var a = Num() + ox; var b = Num() + oy; var c = Num() + ox; var e = Num() + oy; var f = Num() + ox; var g = Num() + oy; Cubic(a, b, c, e, f, g); prev = 'C'; continue; }
                case 'S':
                {
                    var (r1x, r1y) = prev is 'C' or 'S' ? (2 * x - lcx, 2 * y - lcy) : (x, y);
                    var c = Num() + ox; var e = Num() + oy; var f = Num() + ox; var g = Num() + oy;
                    Cubic(r1x, r1y, c, e, f, g); prev = 'S'; continue;
                }
                case 'Q':
                {
                    var qx = Num() + ox; var qy = Num() + oy; var nx = Num() + ox; var ny = Num() + oy;
                    Cubic(x + 2.0 / 3 * (qx - x), y + 2.0 / 3 * (qy - y), nx + 2.0 / 3 * (qx - nx), ny + 2.0 / 3 * (qy - ny), nx, ny);
                    break;
                }
                case 'A':
                {
                    var rx = Num(); var ry = Num(); var rot = Num(); var large = Flag(); var sweep = Flag(); var nx = Num() + ox; var ny = Num() + oy;
                    Arc(cur!, ref x, ref y, rx, ry, rot, large, sweep, nx, ny);
                    break;
                }
                case 'Z':
                    if (cur is not null && (x != sx || y != sy)) Line(sx, sy);
                    if (figs.Count > 0) figs[^1] = figs[^1] with { Closed = true };
                    x = sx; y = sy; cmd = ' ';
                    break;
                default: throw new FormatException($"SVG-kommandoen {cmd} kendes ikke: {d}");
            }
            prev = char.ToUpperInvariant(cmd);
        }
        return figs;

        double Num()
        {
            while (i < d.Length && (char.IsWhiteSpace(d[i]) || d[i] == ',')) i++;
            var start = i;
            if (i < d.Length && (d[i] == '-' || d[i] == '+')) i++;
            var dot = false;
            while (i < d.Length && (char.IsDigit(d[i]) || (d[i] == '.' && !dot)))
            {
                if (d[i] == '.') dot = true;
                i++;
            }
            if (i < d.Length && (d[i] == 'e' || d[i] == 'E')) { i++; if (d[i] is '-' or '+') i++; while (i < d.Length && char.IsDigit(d[i])) i++; }
            return double.Parse(d[start..i], System.Globalization.CultureInfo.InvariantCulture);
        }
        bool Flag()
        {
            while (i < d.Length && (char.IsWhiteSpace(d[i]) || d[i] == ',')) i++;
            return d[i++] == '1';
        }
    }

    /// <summary>Elliptisk bue → Bézier (SVG-specifikationens bilag F.6).</summary>
    private static void Arc(List<(double X, double Y)> pts, ref double x, ref double y, double rx, double ry, double rotDeg, bool large, bool sweep, double nx, double ny)
    {
        if (rx == 0 || ry == 0 || (x == nx && y == ny)) { pts.Add((x, y)); pts.Add((nx, ny)); pts.Add((nx, ny)); x = nx; y = ny; return; }
        rx = Math.Abs(rx); ry = Math.Abs(ry);
        var phi = rotDeg * Math.PI / 180; var cos = Math.Cos(phi); var sin = Math.Sin(phi);
        var dx = (x - nx) / 2; var dy = (y - ny) / 2;
        var x1 = cos * dx + sin * dy; var y1 = -sin * dx + cos * dy;
        var lam = x1 * x1 / (rx * rx) + y1 * y1 / (ry * ry);
        if (lam > 1) { rx *= Math.Sqrt(lam); ry *= Math.Sqrt(lam); }
        var num = rx * rx * ry * ry - rx * rx * y1 * y1 - ry * ry * x1 * x1;
        var den = rx * rx * y1 * y1 + ry * ry * x1 * x1;
        var co = Math.Sqrt(Math.Max(0, num / den)) * (large == sweep ? -1 : 1);
        var cxp = co * rx * y1 / ry; var cyp = -co * ry * x1 / rx;
        var cx = cos * cxp - sin * cyp + (x + nx) / 2; var cy = sin * cxp + cos * cyp + (y + ny) / 2;
        double Ang(double ux, double uy, double vx, double vy)
        {
            var a = Math.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);
            return a;
        }
        var t1 = Ang(1, 0, (x1 - cxp) / rx, (y1 - cyp) / ry);
        var dt = Ang((x1 - cxp) / rx, (y1 - cyp) / ry, (-x1 - cxp) / rx, (-y1 - cyp) / ry);
        if (!sweep && dt > 0) dt -= 2 * Math.PI; else if (sweep && dt < 0) dt += 2 * Math.PI;
        var segs = (int)Math.Ceiling(Math.Abs(dt) / (Math.PI / 2));
        var step = dt / segs;
        var k = 4.0 / 3 * Math.Tan(step / 4);
        for (var s = 0; s < segs; s++)
        {
            var a1 = t1 + s * step; var a2 = a1 + step;
            (double, double) P(double ex, double ey) => (cos * rx * ex - sin * ry * ey + cx, sin * rx * ex + cos * ry * ey + cy);
            var (c1, s1, c2, s2) = (Math.Cos(a1), Math.Sin(a1), Math.Cos(a2), Math.Sin(a2));
            pts.Add(P(c1 - k * s1, s1 + k * c1));
            pts.Add(P(c2 + k * s2, s2 - k * c2));
            pts.Add(s == segs - 1 ? (nx, ny) : P(c2, s2));
        }
        x = nx; y = ny;
    }
}
