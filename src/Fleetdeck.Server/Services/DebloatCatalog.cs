namespace Fleetdeck.Server.Services;

// Debloat og privatliv til Windows 10/11.
//
// Registreringsnøgler og app-lister er sammenholdt med to open source-projekter (begge MIT-licens):
//   Raphire/Win11Debloat   https://github.com/Raphire/Win11Debloat   (Regfiles/*.reg, Appslist.txt)
//   ChrisTitusTech/winutil https://github.com/ChrisTitusTech/winutil (config/tweaks.json, config/appx.json, config/feature.json,
//                          functions/public/Invoke-WPFUpdatessecurity.ps1, Invoke-WPFUltimatePerformance.ps1, Invoke-WPFFixes*.ps1)
// Fleetdeck kopierer ikke deres kode; listerne nedenfor er skrevet her og holdt til dokumenterede
// gruppepolitik-nøgler (HKLM\SOFTWARE\Policies …), hvor sådan en findes. Se docs/debloat.md.
//
// Grundregler (testes i DebloatTests):
//  * Intet her må ødelægge Windows Update, Defender, aktivering, Microsoft Store eller netværket.
//    Ingen hosts-fil, ingen firewall-blokering af Microsofts adresser, ingen fjernelse af Store/winget/rammeværker.
//  * Alt kan fortrydes: før-værdier gemmes på serveren. Apps, der ikke kan installeres igen, er markeret tydeligt.
//  * Beskyttede pakker (ProtectedApps) må aldrig matche en fjernelse – heller ikke via jokertegn.

public static class DebloatRisk
{
    public const string Safe = "sikker", Moderate = "moderat", Advanced = "avanceret";
    public static readonly string[] All = [Safe, Moderate, Advanced];
}

public static class DebloatProfiles
{
    public const string None = "", Let = "let", Anbefalet = "anbefalet", Maks = "maks";
    /// <summary>
    /// Profilernes nuværende version. Et punkt, der kommer med i en profil senere, får <see cref="DebloatItem.ProfileSince"/> sat, og maskiner, der
    /// valgte profilen før, beholder deres gamle indhold, indtil nogen vælger at opdatere dem. 1 = de første 62 punkter, 2 = punkterne fra winutil.
    /// </summary>
    public const int Current = 2;
    public static readonly string[] All = [Let, Anbefalet, Maks];
    public static readonly Dictionary<string, (string Title, string Description)> Info = new()
    {
        [Let] = ("Let", "Slår reklamer, forslag, reklame-id og 'foreslåede' apps fra – også i søgefeltet og Edge. Ingen apps fjernes, og intet kan mærkes i hverdagen."),
        [Anbefalet] = ("Anbefalet", "Alt fra Let, plus: telemetri på laveste niveau, Copilot og Recall fra, ingen websøgning i Start eller widgets, og de typiske medfølgende apps fjernes (fx Clipchamp, Solitaire, Nyheder). Desuden vises filtypenavne og 'Afslut opgave', hurtig start slås fra, og Wake-on-LAN slås til."),
        [Maks] = ("Maks", "Alt fra Anbefalet, plus punkter, der kan mærkes: Teams (ny), nye Outlook, Xbox-apps, Kort og streaming-apps fjernes, og placering, telemetriopgaver og AI i Paint og Notesblok slås fra. Se listen igennem, før du anvender."),
    };
}

/// <summary>Hvor en registreringsværdi hører hjemme.</summary>
public static class RegHive
{
    /// <summary>HKEY_LOCAL_MACHINE (hele maskinen).</summary>
    public const string Machine = "HKLM";
    /// <summary>Hver brugers HKCU: alle eksisterende profiler (også dem, der ikke er logget ind) plus standardprofilen til nye brugere.</summary>
    public const string Users = "HKCU";
    /// <summary>Kun standardprofilen (C:\Users\Default) – det nye brugere får.</summary>
    public const string DefaultUser = "DEFAULT";
    /// <summary>Loginskærmen (HKEY_USERS\.DEFAULT).</summary>
    public const string Logon = "LOGON";
    /// <summary>Hver brugers klasser (UsrClass.dat, det HKCU\Software\Classes peger på). Stien er relativ til klassernes rod. Standardprofilen har ingen.</summary>
    public const string Classes = "CLASSES";
}

public static class RegKind { public const string Dword = "dword", String = "string"; }

/// <summary>
/// Én registreringsværdi. <paramref name="Value"/> null = værdien slettes. <paramref name="WindowsDefault"/> bruges kun ved fortryd
/// for en profil, der ikke fandtes, da punktet blev anvendt (null = slet værdien, så Windows bruger sin standard).
/// <paramref name="Bits"/>: værdien er en bitmaske, der lægges oven i det, der står i forvejen, så andre bit i værdien ikke røres.
/// </summary>
public sealed record RegTweak(string Hive, string Path, string Name, string Kind, object? Value, object? WindowsDefault = null, int MinBuild = 0, int MaxBuild = 0, bool Bits = false)
{
    public string Key => $"{Hive}|{Path}|{Name}".ToLowerInvariant();
}

/// <summary>En tjeneste sættes til <paramref name="Start"/> (3 = efter behov, 4 = deaktiveret). Fortryd: den tidligere starttype.</summary>
public sealed record ServiceTweak(string Name, int Start, int WindowsDefault);

/// <summary>En planlagt opgave slås fra. Fortryd: slås til igen, hvis den var slået til før.</summary>
public sealed record TaskTweak(string Path, string Name);

/// <summary>
/// En medfølgende app (AppX), der fjernes for alle brugere og fra standardbilledet (deprovision).
/// <paramref name="Name"/> er pakkenavnet (må indeholde *). <paramref name="StoreId"/> = Microsoft Store-id til geninstallation
/// (null = kan ikke installeres igen herfra, fx fordi appen er udgået).
/// </summary>
public sealed record AppxRemoval(string Name, string Title, string? StoreId = null);

/// <summary>Et program, der afinstalleres via winget for den bruger, der er logget ind (fortryd = installér igen).</summary>
public sealed record WingetRemoval(string Id, string Title);

/// <summary>Windows' strømplaner. Kopierne har faste GUID'er, så Fleetdeck kan kende (og fjerne) sine egne.</summary>
public static class DebloatPower
{
    public const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    public const string High = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    /// <summary>Den skjulte skabelon til Ultimativ ydelse (powercfg -duplicatescheme).</summary>
    public const string Ultimate = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    public const string HighCopy = "f1ee7dec-0002-4f00-8a00-8c5e7fda635c";
    public const string UltimateCopy = "f1ee7dec-0003-4f00-8a00-e9a42b02eb61";
    /// <summary>Planer, Fleetdeck selv opretter og derfor må slette igen.</summary>
    public static readonly string[] FleetdeckCopies = [HighCopy, UltimateCopy];
    /// <summary>Vælgerens id → katalogpunktet.</summary>
    public static readonly Dictionary<string, string> Choices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["balanced"] = "power.plan_balanced", ["high"] = "power.plan_high", ["ultimate"] = "power.plan_ultimate",
    };
    public const string Group = "powerplan";
}

/// <summary>Skift til en strømplan. <paramref name="Base"/> bruges, hvis den findes; ellers kopieres den til <paramref name="Copy"/>. Fortryd: den tidligere plan.</summary>
public sealed record PowerPlanTweak(string Base, string? Copy = null);

/// <summary>En indstilling i den aktive strømplan (null = den side røres ikke). Fortryd: de tidligere værdier.</summary>
public sealed record PowerSettingTweak(string Subgroup, string Setting, int? Ac, int? Dc)
{
    public string Key => $"pw|{Subgroup}|{Setting}";
}

/// <summary>En valgfri Windows-funktion, der slås til. Fortryd: slås fra igen, hvis den var fra før.</summary>
public sealed record FeatureTweak(string Name)
{
    public string Key => $"feat|{Name}";
}

public sealed class DebloatItem
{
    public required string Id { get; init; }
    public required string Category { get; init; }
    /// <summary>Kontaktens tekst: hvad der sker på pc'en, når punktet anvendes (fx "Slå reklame-id fra", "Fjern Clipchamp", "Vis filtypenavne").</summary>
    public required string Title { get; init; }
    /// <summary>Hvad tilstanden handler om, i status og jobbets output: "Reklame-id: fra".</summary>
    public required string Subject { get; init; }
    /// <summary>Tilstanden med punktets egne ord, når det er anvendt / ikke er anvendt (null = kategoriens ord).</summary>
    public string? On { get; init; }
    public string? Off { get; init; }
    /// <summary>Hvad der sker, når man trykker Anvend.</summary>
    public required string Description { get; init; }
    /// <summary>Hvad Fortryd gør (null = udledes af det, punktet ændrer; se DebloatCatalog.UndoText).</summary>
    public string? Undo { get; init; }
    /// <summary>Ændringen ses først, når brugeren har logget af og på igen.</summary>
    public bool NeedsLogoff { get; init; }
    public required string Risk { get; init; }
    /// <summary>Profiler, punktet er med i som standard (tom = kun ved eget valg).</summary>
    public string[] Profiles { get; init; } = [];
    /// <summary>Laveste/højeste Windows-build (0 = ingen grænse). Windows 11 = 22000+.</summary>
    public int MinBuild { get; init; }
    public int MaxBuild { get; init; }
    /// <summary>Udgaver, punktet virker på (home | pro | enterprise). Tom = alle.</summary>
    public string[] Editions { get; init; } = [];
    /// <summary>Kort bemærkning til brugerfladen (fx hvad man mister, eller at det kræver log af).</summary>
    public string? Note { get; init; }
    public RegTweak[] Registry { get; init; } = [];
    public ServiceTweak[] Services { get; init; } = [];
    public TaskTweak[] Tasks { get; init; } = [];
    public AppxRemoval[] Apps { get; init; } = [];
    public WingetRemoval[] Winget { get; init; } = [];
    public PowerPlanTweak? PowerPlan { get; init; }
    public PowerSettingTweak[] PowerSettings { get; init; } = [];
    /// <summary>Slår dvaletilstand fra (powercfg /hibernate off).</summary>
    public bool HibernateOff { get; init; }
    /// <summary>Slår Wake-on-LAN til på kablede netkort. Fortryd slår det ALDRIG fra igen: Fleetdeck bruger det til at vække pc'en.</summary>
    public bool WakeOnLan { get; init; }
    public FeatureTweak[] Features { get; init; } = [];
    /// <summary>Kun ét punkt i gruppen kan være valgt ad gangen (fx strømplanen).</summary>
    public string? Exclusive { get; init; }
    /// <summary>Ændringen virker først efter en genstart.</summary>
    public bool NeedsReboot { get; init; }
    /// <summary>Profilversionen, punktet kom med i sine profiler (se DebloatProfiles.Current).</summary>
    public int ProfileSince { get; init; } = 1;

    /// <summary>Apps i punktet, der ikke kan installeres igen herfra.</summary>
    public IEnumerable<AppxRemoval> NotReinstallable => Apps.Where(a => a.StoreId is null);
    /// <summary>Kan punktet fortrydes helt? (Registrering, tjenester og opgaver kan altid; apps kun med Store-id.)</summary>
    public bool FullyReversible => !NotReinstallable.Any();
}

/// <summary>En kategori. Ordene bruges for et punkts tilstand, når punktet ikke har sine egne (DebloatItem.On/Off): "fra"/"til", "fjernet"/"installeret" …</summary>
public sealed record DebloatCategory(string Id, string Title, string Description, string AppliedLabel = "fra", string NotAppliedLabel = "til");

public static class DebloatCatalog
{
    public static readonly DebloatCategory[] Categories =
    [
        new("telemetry", "Telemetri og sporing", "Hvad Windows sender til Microsoft, og hvad der huskes om brugeren."),
        new("ads", "Reklamer og forslag", "Tips, 'forslag' og reklamer i Start, på låseskærmen, i Indstillinger og Stifinder slås fra."),
        new("ai", "AI, Copilot og Recall", "Copilot, Recall og andre AI-funktioner slås fra med Microsofts egne politikker. Copilot-appen kan også fjernes."),
        new("search", "Websøgning i Start", "Søgning i Start finder kun ting på pc'en – ingen Bing-resultater og ingen 'højdepunkter'."),
        new("widgets", "Widgets og nyheder", "Nyheds- og vejrpanelet på proceslinjen slås fra."),
        new("consumer", "Foreslåede apps", "Windows og producenten lægger ikke længere apps og spil ind af sig selv. Det, der allerede er installeret, fjernes under Medfølgende apps."),
        new("apps", "Medfølgende apps", "Apps fjernes for alle brugere og fra Windows' standardbillede, så nye brugere heller ikke får dem. Ved Fortryd hentes de fleste igen fra Microsoft Store.", "fjernet", "installeret"),
        new("extra", "Valgfrit", "Edge, Brave, Game DVR og OneDrive. Nogle er med i niveauerne, resten vælges enkeltvis."),
        new("power", "Strøm og ydelse", "Dvale, hurtig start, tidsgrænser og Wake-on-LAN. Strømplanen vælges øverst på siden.", "anvendt", "ikke anvendt"),
        new("ux", "Udseende og brugervenlighed", "Små ting, der gør Windows rarere at bruge. De fleste gælder hver bruger på pc'en.", "anvendt", "ikke anvendt"),
        new("network", "Netværk (avanceret)", "Kun til pc'er med et konkret netværksproblem. Træder i kraft efter en genstart.", "anvendt", "ikke anvendt"),
        new("updates", "Opdateringspolitik", "Hvornår Windows installerer nye versioner. At slå Windows Update fra tilbydes ikke. Fleetdecks egne opdateringsjobs virker i begge tilstande.", "anvendt", "standard"),
        new("features", "Windows-funktioner", "Valgfrie funktioner, der installeres fra Windows selv. De fleste kræver en genstart.", "installeret", "ikke installeret"),
    ];

    /// <summary>
    /// Pakker, der ALDRIG må fjernes (Store, winget, rammeværker, sikkerhed, kerneapps). Motoren tjekker også listen,
    /// og springer desuden alt over, som Windows selv markerer som rammeværk eller ikke-fjernbart.
    /// </summary>
    public static readonly string[] ProtectedApps =
    [
        "Microsoft.WindowsStore", "Microsoft.StorePurchaseApp", "Microsoft.Services.Store.Engagement",
        "Microsoft.DesktopAppInstaller", "Microsoft.Winget.Source",
        "Microsoft.WindowsCalculator", "Microsoft.Windows.Photos", "Microsoft.WindowsNotepad", "Microsoft.WindowsTerminal", "Microsoft.WindowsTerminalPreview",
        "Microsoft.Paint", "Microsoft.MSPaint", "Microsoft.ScreenSketch", "Microsoft.WindowsCamera",
        "Microsoft.VCLibs.*", "Microsoft.UI.Xaml.*", "Microsoft.NET.*", "Microsoft.WindowsAppRuntime.*", "MicrosoftCorporationII.WinAppRuntime.*",
        "Microsoft.SecHealthUI", "Microsoft.Windows.SecHealthUI", "Microsoft.AAD.BrokerPlugin", "Microsoft.AccountsControl",
        "Microsoft.XboxIdentityProvider", "Microsoft.Xbox.TCUI",
        "Microsoft.Windows.ShellExperienceHost", "Microsoft.Windows.StartMenuExperienceHost", "Microsoft.Windows.CloudExperienceHost",
        "Microsoft.Windows.ContentDeliveryManager", "Microsoft.Windows.Search", "MicrosoftWindows.Client.*", "MicrosoftWindows.CrossDevice",
        "Microsoft.LanguageExperiencePack*", "Microsoft.MicrosoftEdge*", "Microsoft.Edge*",
        "Microsoft.HEIFImageExtension", "Microsoft.HEVCVideoExtension", "Microsoft.VP9VideoExtensions", "Microsoft.WebMediaExtensions",
        "Microsoft.WebpImageExtension", "Microsoft.RawImageExtension", "Microsoft.AV1VideoExtension", "Microsoft.MPEG2VideoExtension",
        "MicrosoftCorporationII.QuickAssist", "Microsoft.WindowsSoundRecorder", "Microsoft.ZuneMusic",
    ];

    /// <summary>Konkrete pakkenavne, som testene bruger til at sikre, at ingen fjernelse rammer noget beskyttet.</summary>
    public static readonly string[] ProtectedExamples =
    [
        "Microsoft.WindowsStore", "Microsoft.StorePurchaseApp", "Microsoft.DesktopAppInstaller", "Microsoft.WindowsCalculator", "Microsoft.Windows.Photos",
        "Microsoft.WindowsNotepad", "Microsoft.WindowsTerminal", "Microsoft.Paint", "Microsoft.ScreenSketch", "Microsoft.VCLibs.140.00",
        "Microsoft.VCLibs.140.00.UWPDesktop", "Microsoft.UI.Xaml.2.8", "Microsoft.NET.Native.Framework.2.2", "Microsoft.NET.Native.Runtime.2.2",
        "Microsoft.WindowsAppRuntime.1.5", "Microsoft.SecHealthUI", "Microsoft.XboxIdentityProvider", "Microsoft.Xbox.TCUI", "Microsoft.AAD.BrokerPlugin",
        "Microsoft.Windows.ShellExperienceHost", "Microsoft.Windows.StartMenuExperienceHost", "MicrosoftWindows.Client.WebExperience",
        "Microsoft.MicrosoftEdge.Stable", "Microsoft.HEIFImageExtension", "Microsoft.WebMediaExtensions", "MicrosoftCorporationII.QuickAssist",
    ];

    /// <summary>
    /// Fejl ved fjernelse af en app, der betyder "den er her allerede ikke": fil/sti ikke fundet (0x80070002, 0x80070003) og
    /// pakke ikke fundet (0x80073CF1). Motoren tæller dem som udført. Teksterne dækker engelsk og dansk Windows.
    /// </summary>
    public const string AlreadyGonePattern = @"0x80070002|0x80070003|0x80073CF1|cannot find the (file|path)|angivne (fil|sti) blev ikke fundet";

    // ------------------------------------------------------------------ korte veje til registreringen
    private const string Pol = @"SOFTWARE\Policies\Microsoft\Windows";
    private const string UPol = @"Software\Policies\Microsoft\Windows";
    private const string Cdm = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    private const string Adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string Edge = @"SOFTWARE\Policies\Microsoft\Edge";
    private const string Win11 = "Windows 11";

    private static RegTweak Lm(string path, string name, int v, int min = 0, int max = 0) => new(RegHive.Machine, path, name, RegKind.Dword, v, null, min, max);
    private static RegTweak Cu(string path, string name, int v, int min = 0, int max = 0) => new(RegHive.Users, path, name, RegKind.Dword, v, null, min, max);
    private static string[] P(params string[] p) => p;
    private static readonly string[] LetUp = [DebloatProfiles.Let, DebloatProfiles.Anbefalet, DebloatProfiles.Maks];
    private static readonly string[] RecUp = [DebloatProfiles.Anbefalet, DebloatProfiles.Maks];
    private static readonly string[] MaxOnly = [DebloatProfiles.Maks];
    private static AppxRemoval App(string name, string title, string? store = null) => new(name, title, store);
    private static RegTweak CuS(string path, string name, string v) => new(RegHive.Users, path, name, RegKind.String, v);
    private const string SubSleep = "238c9fa8-0aad-41ed-83f4-97be242c8f20", StandbyIdle = "29f6c1db-86da-48c5-9fdb-f2b67b1f44da";
    private const string SubVideo = "7516b95f-f776-4464-8c53-06167f40cc99", VideoIdle = "3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e";
    private const string SubUsb = "2a737441-1930-4402-8d77-b2bebba308a3", UsbSuspend = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";
    private const string SubPcie = "501a4d13-42af-4429-9fd1-a8218c268e20", PcieAspm = "ee12f906-d277-404b-b6da-e5fa1a576df5";
    private const string WuPol = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
    private const string SleepGroup = "sleep";
    private static readonly string[] ProEnt = ["pro", "enterprise"];

    public static readonly DebloatItem[] Items =
    [
        // ============================================================== Telemetri og sporing
        new()
        {
            Id = "telemetry.diagnostic_data", Category = "telemetry", Risk = DebloatRisk.Safe, Profiles = RecUp,
            Title = "Sæt diagnosedata til laveste niveau", Subject = "Diagnosedata", On = "laveste niveau", Off = "ikke begrænset",
            Description = "Windows' diagnosedata sættes til 'Påkrævet' – det laveste niveau, Home og Pro tillader – og Windows holder op med at bede om feedback. Windows Update påvirkes ikke.",
            Note = "Indstillingen vises bagefter som 'administreret af din organisation'.",
            Registry = [Lm($@"{Pol}\DataCollection", "AllowTelemetry", 1), Lm($@"{Pol}\DataCollection", "DoNotShowFeedbackNotifications", 1)],
        },
        new()
        {
            Id = "telemetry.diagtrack", Category = "telemetry", Risk = DebloatRisk.Moderate, Profiles = RecUp,
            Title = "Slå telemetritjenesten fra (DiagTrack)", Subject = "Telemetritjenesten", On = "deaktiveret", Off = "slået til",
            Description = "Tjenesten 'Forbundne brugeroplevelser og telemetri' stoppes og deaktiveres, så diagnosedata ikke sendes løbende. Windows Update, Defender og Store virker uden den.",
            Services = [new("DiagTrack", 4, 2)],
        },
        new()
        {
            Id = "telemetry.dmwappush", Category = "telemetry", Risk = DebloatRisk.Advanced, Profiles = MaxOnly,
            Title = "Slå WAP-push-tjenesten fra (dmwappushservice)", Subject = "WAP-push-tjenesten", On = "deaktiveret", Off = "slået til",
            Description = "Tjenesten, der videresender telemetri- og enhedsstyringsbeskeder, deaktiveres. Kun relevant på private pc'er.",
            Note = "Pc'er, der er tilmeldt arbejde/skole (Intune/MDM), skal beholde den.",
            Services = [new("dmwappushservice", 4, 3)],
        },
        new()
        {
            Id = "telemetry.advertising_id", Category = "telemetry", Risk = DebloatRisk.Safe, Profiles = LetUp,
            Title = "Slå reklame-id fra", Subject = "Reklame-id",
            Description = "Apps kan ikke længere bruge et fælles reklame-id til at målrette reklamer på tværs af apps.",
            Registry = [Lm($@"{Pol}\AdvertisingInfo", "DisabledByGroupPolicy", 1), Cu(@"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0)],
        },
        new()
        {
            Id = "telemetry.activity_history", Category = "telemetry", Risk = DebloatRisk.Safe, Profiles = RecUp,
            Title = "Slå aktivitetshistorik fra", Subject = "Aktivitetshistorik",
            Description = "Windows gemmer og sender ikke længere, hvilke apps, filer og sider der er brugt.",
            Registry = [Lm($@"{Pol}\System", "EnableActivityFeed", 0), Lm($@"{Pol}\System", "PublishUserActivities", 0), Lm($@"{Pol}\System", "UploadUserActivities", 0)],
        },
        new()
        {
            Id = "telemetry.tailored", Category = "telemetry", Risk = DebloatRisk.Safe, Profiles = LetUp,
            Title = "Slå skræddersyede oplevelser fra", Subject = "Skræddersyede oplevelser",
            Description = "Microsoft bruger ikke diagnosedata til personlige tips, reklamer og anbefalinger.",
            Registry = [Cu($@"{UPol}\CloudContent", "DisableTailoredExperiencesWithDiagnosticData", 1), Cu(@"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0)],
        },
        new()
        {
            Id = "telemetry.feedback", Category = "telemetry", Risk = DebloatRisk.Safe, Profiles = RecUp,
            Title = "Slå spørgsmål om feedback fra", Subject = "Spørgsmål om feedback",
            Description = "Windows spørger ikke længere 'Hvor sandsynligt er det, at du vil anbefale Windows?'.",
            Registry = [Cu(@"Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0)],
        },
        new()
        {
            Id = "telemetry.location", Category = "telemetry", Risk = DebloatRisk.Moderate, Profiles = MaxOnly,
            Title = "Slå placering fra for hele pc'en", Subject = "Placering",
            Description = "Placeringstjenesten slås fra for alle brugere. Den kan slås til igen under Indstillinger › Beskyttelse af personlige oplysninger og sikkerhed › Placering.",
            Note = "Automatisk tidszone, 'Find min enhed' og lokalt vejr holder op med at virke.",
            Registry = [new(RegHive.Machine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location", "Value", RegKind.String, "Deny", "Allow")],
        },
        new()
        {
            Id = "telemetry.app_launch", Category = "telemetry", Risk = DebloatRisk.Safe, Profiles = RecUp,
            Title = "Slå sporing af app-start fra", Subject = "Sporing af app-start",
            Description = "Windows registrerer ikke, hvilke programmer der startes (bruges til 'Mest brugte' i Start).",
            Note = "Listen 'Mest brugte' i Start bliver tom.",
            Registry = [Cu(Adv, "Start_TrackProgs", 0)],
        },
        new()
        {
            Id = "telemetry.inking_typing", Category = "telemetry", Risk = DebloatRisk.Safe, Profiles = RecUp,
            Title = "Slå personlig håndskrift og tastning fra", Subject = "Personlig håndskrift og tastning",
            Description = "Windows indsamler ikke det, der skrives og tegnes, for at lave en personlig ordbog, og sender det ikke til Microsoft.",
            Registry =
            [
                Cu(@"Software\Microsoft\InputPersonalization", "RestrictImplicitInkCollection", 1),
                Cu(@"Software\Microsoft\InputPersonalization", "RestrictImplicitTextCollection", 1),
                Cu(@"Software\Microsoft\InputPersonalization\TrainedDataStore", "HarvestContacts", 0),
                Cu(@"Software\Microsoft\Personalization\Settings", "AcceptedPrivacyPolicy", 0),
                Cu(@"Software\Microsoft\Input\TIPC", "Enabled", 0),
            ],
        },
        new()
        {
            Id = "telemetry.ceip_tasks", Category = "telemetry", Risk = DebloatRisk.Moderate, Profiles = MaxOnly,
            Title = "Slå CEIP og dets planlagte opgaver fra", Subject = "CEIP (kundeoplevelse)",
            Description = "Programmet til forbedring af kundeoplevelsen (CEIP) og dets planlagte opgaver slås fra.",
            Registry = [Lm(@"SOFTWARE\Policies\Microsoft\SQMClient\Windows", "CEIPEnable", 0)],
            Tasks =
            [
                new(@"\Microsoft\Windows\Customer Experience Improvement Program\", "Consolidator"),
                new(@"\Microsoft\Windows\Customer Experience Improvement Program\", "UsbCeip"),
                new(@"\Microsoft\Windows\Autochk\", "Proxy"),
                new(@"\Microsoft\Windows\Feedback\Siuf\", "DmClient"),
                new(@"\Microsoft\Windows\Feedback\Siuf\", "DmClientOnScenarioDownload"),
                new(@"\Microsoft\Windows\DiskDiagnostic\", "Microsoft-Windows-DiskDiagnosticDataCollector"),
            ],
        },
        new()
        {
            Id = "telemetry.compat_appraiser", Category = "telemetry", Risk = DebloatRisk.Advanced, Profiles = MaxOnly,
            Title = "Slå kompatibilitetsvurderingen fra (Appraiser)", Subject = "Kompatibilitetsvurdering",
            Description = "De planlagte opgaver, der jævnligt scanner programmer og sender kompatibilitetsdata, slås fra.",
            Note = "Månedlige opdateringer virker som før, men tilbud om nye Windows-versioner (fx 24H2 → 25H2) kan blive forsinket, fordi Windows Update bruger vurderingen.",
            Tasks =
            [
                new(@"\Microsoft\Windows\Application Experience\", "Microsoft Compatibility Appraiser"),
                new(@"\Microsoft\Windows\Application Experience\", "Microsoft Compatibility Appraiser Exp"),
                new(@"\Microsoft\Windows\Application Experience\", "ProgramDataUpdater"),
            ],
        },

        // ============================================================== Reklamer og forslag
        new()
        {
            Id = "ads.start_suggestions", Category = "ads", Risk = DebloatRisk.Safe, Profiles = LetUp,
            Title = "Slå forslag og anbefalinger i Start fra", Subject = "Forslag i Start",
            Description = "Start-menuen viser ikke længere 'foreslåede' apps, tips og anbefalede genveje.",
            Registry = [Cu(Cdm, "SystemPaneSuggestionsEnabled", 0), Cu(Cdm, "SubscribedContent-338388Enabled", 0), Cu(Adv, "Start_IrisRecommendations", 0, min: 22000)],
        },
        new()
        {
            Id = "ads.lockscreen", Category = "ads", Risk = DebloatRisk.Safe, Profiles = LetUp,
            Title = "Slå tips og reklamer på låseskærmen fra", Subject = "Tips på låseskærmen",
            Description = "Låseskærmen viser ikke længere 'sjove fakta, tips og tricks' oven på billedet. Selve billedet (Windows-fokus) bliver.",
            Registry = [Cu(Cdm, "RotatingLockScreenOverlayEnabled", 0), Cu(Cdm, "SubscribedContent-338387Enabled", 0)],
        },
        new()
        {
            Id = "ads.settings", Category = "ads", Risk = DebloatRisk.Safe, Profiles = LetUp,
            Title = "Slå forslag i Indstillinger fra", Subject = "Forslag i Indstillinger",
            Description = "Appen Indstillinger viser ikke længere foreslået indhold og reklamer for Microsoft-tjenester.",
            Registry = [Cu(Cdm, "SubscribedContent-338393Enabled", 0), Cu(Cdm, "SubscribedContent-353694Enabled", 0), Cu(Cdm, "SubscribedContent-353696Enabled", 0),
                Lm($@"{Pol}\CloudContent", "DisableConsumerAccountStateContent", 1)],
        },
        new()
        {
            Id = "ads.tips", Category = "ads", Risk = DebloatRisk.Safe, Profiles = LetUp,
            Title = "Slå pop op-tips og forslag fra", Subject = "Pop op-tips",
            Description = "Windows viser ikke længere pop op-tips og forslag om at prøve Microsoft-tjenester, mens pc'en bruges.",
            Registry = [Cu(Cdm, "SubscribedContent-338389Enabled", 0), Cu(Cdm, "SoftLandingEnabled", 0)],
        },
        new()
        {
            Id = "ads.welcome", Category = "ads", Risk = DebloatRisk.Safe, Profiles = LetUp,
            Title = "Slå velkomstskærmen efter opdateringer fra", Subject = "Velkomstskærm efter opdateringer",
            Description = "Windows viser ikke længere 'Se, hvad der er nyt' og forslag efter større opdateringer.",
            Registry = [Cu(Cdm, "SubscribedContent-310093Enabled", 0)],
        },
        new()
        {
            Id = "ads.get_more", Category = "ads", Risk = DebloatRisk.Safe, Profiles = LetUp,
            Title = "Slå 'Få endnu mere ud af Windows' fra", Subject = "'Få endnu mere ud af Windows'",
            Description = "Den blå skærm efter opdateringer, der foreslår OneDrive, Microsoft 365 og Microsoft-konto, vises ikke længere.",
            Registry = [Cu(@"Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement", "ScoobeSystemSettingEnabled", 0)],
        },
        new()
        {
            Id = "ads.explorer_sync", Category = "ads", Risk = DebloatRisk.Safe, Profiles = LetUp,
            Title = "Slå reklamer i Stifinder fra", Subject = "Reklamer i Stifinder",
            Description = "Stifinder viser ikke længere reklamer for OneDrive og Microsoft 365 (meddelelser fra synkroniseringsudbydere).",
            Registry = [Cu(Adv, "ShowSyncProviderNotifications", 0)],
        },
        new()
        {
            Id = "ads.start_account", Category = "ads", Risk = DebloatRisk.Safe, Profiles = LetUp, MinBuild = 22000,
            Title = "Slå kontomeddelelser i Start fra", Subject = "Kontomeddelelser i Start",
            Description = "Start-menuen viser ikke længere opfordringer til at logge ind med Microsoft-konto eller købe Microsoft 365.",
            Registry = [Cu(Adv, "Start_AccountNotifications", 0), Cu(@"Software\Microsoft\Windows\CurrentVersion\SystemSettings\AccountNotifications", "EnableAccountNotifications", 0)],
        },
        new()
        {
            Id = "ads.notification_suggestions", Category = "ads", Risk = DebloatRisk.Safe, Profiles = LetUp,
            Title = "Slå forslag i meddelelser fra", Subject = "Forslag i meddelelser",
            Description = "Windows sender ikke længere 'foreslåede' meddelelser (fx om at prøve nye funktioner).",
            Registry = [Cu(@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\Windows.SystemToast.Suggested", "Enabled", 0)],
        },

        // ============================================================== AI, Copilot og Recall
        new()
        {
            Id = "ai.copilot", Category = "ai", Risk = DebloatRisk.Safe, Profiles = RecUp,
            Title = "Slå Copilot i Windows fra", Subject = "Copilot i Windows",
            Description = "Windows Copilot slås fra med Microsofts politik, og Copilot-knappen fjernes fra proceslinjen. På nye Windows-versioner er Copilot en app – den fjernes med punktet 'Fjern Copilot-appen'.",
            Registry = [Lm($@"{Pol}\WindowsCopilot", "TurnOffWindowsCopilot", 1), Cu($@"{UPol}\WindowsCopilot", "TurnOffWindowsCopilot", 1), Cu(Adv, "ShowCopilotButton", 0)],
        },
        new()
        {
            Id = "ai.copilot_app", Category = "ai", Risk = DebloatRisk.Moderate, Profiles = RecUp,
            Title = "Fjern Copilot-appen", Subject = "Copilot-appen", On = "fjernet", Off = "installeret",
            Description = "Copilot-appen fjernes for alle brugere og fra Windows' standardbillede.",
            Apps = [App("Microsoft.Copilot", "Microsoft Copilot", "9NHT9RB2F4HD")],
        },
        new()
        {
            Id = "ai.recall", Category = "ai", Risk = DebloatRisk.Safe, Profiles = RecUp, MinBuild = 26100,
            Title = "Slå Recall fra", Subject = "Recall",
            Description = "Recall må ikke gemme skærmbilleder af det, du laver, til søgning. Gælder kun Copilot+-pc'er med Windows 11 24H2 og nyere.",
            Registry = [Lm($@"{Pol}\WindowsAI", "DisableAIDataAnalysis", 1), Cu($@"{UPol}\WindowsAI", "DisableAIDataAnalysis", 1)],
        },
        new()
        {
            Id = "ai.click_to_do", Category = "ai", Risk = DebloatRisk.Safe, Profiles = RecUp, MinBuild = 26100,
            Title = "Slå Click to Do fra", Subject = "Click to Do",
            Description = "AI-handlingerne på det, der er på skærmen (Click to Do), slås fra.",
            Registry = [Lm($@"{Pol}\WindowsAI", "DisableClickToDo", 1), Cu($@"{UPol}\WindowsAI", "DisableClickToDo", 1)],
        },
        new()
        {
            Id = "ai.app_features", Category = "ai", Risk = DebloatRisk.Moderate, Profiles = MaxOnly, MinBuild = 22000,
            Title = "Slå AI i Paint og Notesblok fra", Subject = "AI i Paint og Notesblok",
            Description = "Cocreator, generativ udfyldning og billedskaber i Paint og AI-funktionerne i Notesblok slås fra.",
            Registry =
            [
                Lm(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint", "DisableCocreator", 1),
                Lm(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint", "DisableGenerativeFill", 1),
                Lm(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint", "DisableImageCreator", 1),
                Lm(@"SOFTWARE\Policies\WindowsNotepad", "DisableAIFeatures", 1),
            ],
        },
        new()
        {
            Id = "ai.cortana", Category = "ai", Risk = DebloatRisk.Safe, Profiles = RecUp,
            Title = "Slå Cortana fra", Subject = "Cortana",
            Description = "Cortana slås fra med Microsofts politik (Cortana er udgået, men kan stadig dukke op på Windows 10).",
            Registry = [Lm($@"{Pol}\Windows Search", "AllowCortana", 0)],
        },

        // ============================================================== Websøgning
        new()
        {
            Id = "search.web", Category = "search", Risk = DebloatRisk.Safe, Profiles = RecUp,
            Title = "Slå Bing- og websøgning i Start fra", Subject = "Websøgning i Start", NeedsLogoff = true,
            Description = "Søgning i Start viser kun apps, filer og indstillinger på pc'en – ingen Bing-resultater, og intet af det skrevne sendes til Bing.",
            Registry =
            [
                Cu($@"{UPol}\Explorer", "DisableSearchBoxSuggestions", 1),
                Cu(@"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 0),
                Lm($@"{Pol}\Windows Search", "DisableWebSearch", 1),
                Lm($@"{Pol}\Windows Search", "ConnectedSearchUseWeb", 0),
            ],
        },
        new()
        {
            Id = "search.highlights", Category = "search", Risk = DebloatRisk.Safe, Profiles = LetUp,
            Title = "Slå søgehøjdepunkter fra", Subject = "Søgehøjdepunkter",
            Description = "Søgefeltet viser ikke længere dagens 'højdepunkter', trends og reklamebilleder.",
            Registry = [Lm($@"{Pol}\Windows Search", "EnableDynamicContentInWSB", 0)],
        },

        // ============================================================== Widgets
        new()
        {
            Id = "widgets.news", Category = "widgets", Risk = DebloatRisk.Safe, Profiles = RecUp,
            Title = "Slå widgets og nyheder fra", Subject = "Widgets / Nyheder og interesser",
            Description = "Nyheds- og vejrpanelet slås fra (Widgets i Windows 11, 'Nyheder og interesser' i Windows 10).",
            Registry = [Lm(@"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0, min: 22000), Lm($@"{Pol}\Windows Feeds", "EnableFeeds", 0, max: 21999)],
        },

        // ============================================================== Foreslåede apps
        new()
        {
            Id = "consumer.suggested_apps", Category = "consumer", Risk = DebloatRisk.Safe, Profiles = LetUp,
            Title = "Stop automatisk installerede 'foreslåede' apps", Subject = "Automatiske 'foreslåede' apps",
            Description = "Windows henter ikke længere spil og apps (fx Candy Crush) af sig selv til brugerne.",
            Note = "Spil og apps, der allerede er installeret, bliver – de fjernes under Medfølgende apps.",
            Registry =
            [
                Cu(Cdm, "SilentInstalledAppsEnabled", 0), Cu(Cdm, "PreInstalledAppsEnabled", 0),
                Cu(Cdm, "PreInstalledAppsEverEnabled", 0), Cu(Cdm, "OemPreInstalledAppsEnabled", 0),
            ],
        },
        new()
        {
            Id = "consumer.policy", Category = "consumer", Risk = DebloatRisk.Safe, Profiles = RecUp,
            Title = "Slå Microsoft-forbrugeroplevelser fra (politik)", Subject = "Forbrugeroplevelser",
            Description = "Politikkerne 'Slå Microsoft-forbrugeroplevelser fra' og 'skyoptimeret indhold' i Start sættes.",
            Note = "Windows overholder kun politikken fuldt på Enterprise og Education; på Home og Pro klarer punktet 'foreslåede apps' det samme.",
            Registry = [Lm($@"{Pol}\CloudContent", "DisableWindowsConsumerFeatures", 1), Lm($@"{Pol}\CloudContent", "DisableCloudOptimizedContent", 1)],
        },

        // ============================================================== Medfølgende apps
        new()
        {
            Id = "apps.promo_games", Category = "apps", Risk = DebloatRisk.Moderate, Profiles = RecUp,
            Title = "Fjern reklamespil og -apps fra tredjepart", Subject = "Reklamespil",
            Description = "Candy Crush, Bubble Witch, March of Empires og lignende, som Windows har lagt ind som reklame, fjernes for alle brugere.",
            Note = "Spillene fjernes også, hvis nogen faktisk spiller dem. De kan hentes igen i Microsoft Store.",
            Apps =
            [
                App("king.com.CandyCrushSaga", "Candy Crush Saga", "9NBLGGH18846"), App("king.com.CandyCrushSodaSaga", "Candy Crush Soda Saga"),
                App("king.com.BubbleWitch3Saga", "Bubble Witch 3 Saga"), App("king.com.CandyCrushFriends", "Candy Crush Friends"),
                App("A278AB0D.MarchofEmpires", "March of Empires"), App("*.HiddenCity", "Hidden City"),
                App("*.AdobePhotoshopExpress", "Photoshop Express (reklame)"), App("*.Duolingo-LearnLanguagesforFree", "Duolingo (reklame)"),
                App("*.EclipseManager", "Eclipse Manager"), App("ActiproSoftwareLLC.*", "Code Writer"), App("*.Flipboard", "Flipboard"),
                App("*.Asphalt8Airborne", "Asphalt 8"), App("*.FarmVille2CountryEscape", "FarmVille 2"), App("*.RoyalRevolt2", "Royal Revolt 2"),
                App("PandoraMediaInc.*", "Pandora"), App("*.Twitter", "Twitter (reklame)"),
            ],
        },
        new()
        {
            Id = "apps.clipchamp", Category = "apps", Risk = DebloatRisk.Safe, Profiles = RecUp,
            Title = "Fjern Clipchamp", Subject = "Clipchamp", Description = "Microsofts videoredigering (kræver konto og har betalingsfunktioner) fjernes for alle brugere.",
            Apps = [App("Clipchamp.Clipchamp", "Clipchamp", "9P1J8S7CCWWT")],
        },
        new()
        {
            Id = "apps.bing_news", Category = "apps", Risk = DebloatRisk.Safe, Profiles = RecUp,
            Title = "Fjern Microsoft Nyheder", Subject = "Microsoft Nyheder", Description = "Nyhedsappen fra MSN fjernes for alle brugere.",
            Apps = [App("Microsoft.BingNews", "Microsoft Nyheder", "9WZDNCRFHVFW")],
        },
        new()
        {
            Id = "apps.bing_weather", Category = "apps", Risk = DebloatRisk.Safe, Profiles = RecUp,
            Title = "Fjern MSN Vejret", Subject = "MSN Vejret", Description = "Vejr-appen fra MSN fjernes for alle brugere. Vejret kan stadig ses på nettet og i widgets.",
            Apps = [App("Microsoft.BingWeather", "MSN Vejret", "9WZDNCRFJ3Q2")],
        },
        new()
        {
            Id = "apps.bing_search", Category = "apps", Risk = DebloatRisk.Safe, Profiles = RecUp,
            Title = "Fjern Bing-appen", Subject = "Bing-appen", Description = "Den separate Bing-søgeapp fjernes. Windows' egen søgning i Start påvirkes ikke.",
            Apps = [App("Microsoft.BingSearch", "Bing")],
        },
        new()
        {
            Id = "apps.solitaire", Category = "apps", Risk = DebloatRisk.Safe, Profiles = RecUp,
            Title = "Fjern Solitaire Collection", Subject = "Solitaire Collection", Description = "Kabalespillet med reklamer og abonnement fjernes for alle brugere.",
            Apps = [App("Microsoft.MicrosoftSolitaireCollection", "Solitaire Collection", "9WZDNCRFHWD2")],
        },
        new()
        {
            Id = "apps.people", Category = "apps", Risk = DebloatRisk.Safe, Profiles = RecUp,
            Title = "Fjern Personer", Subject = "Personer", Description = "Den gamle kontakt-app fjernes (den er udgået; kontakter ligger i Outlook).",
            Apps = [App("Microsoft.People", "Personer")],
        },
        new()
        {
            Id = "apps.tips", Category = "apps", Risk = DebloatRisk.Safe, Profiles = RecUp,
            Title = "Fjern Tips", Subject = "Tips", Description = "Appen med tips til Windows (Kom godt i gang) fjernes.",
            Apps = [App("Microsoft.Getstarted", "Tips")],
        },
        new()
        {
            Id = "apps.feedback_hub", Category = "apps", Risk = DebloatRisk.Safe, Profiles = RecUp,
            Title = "Fjern Feedbackhub", Subject = "Feedbackhub", Description = "Appen til at sende feedback til Microsoft fjernes.",
            Apps = [App("Microsoft.WindowsFeedbackHub", "Feedbackhub", "9NBLGGH4R32N")],
        },
        new()
        {
            Id = "apps.get_help", Category = "apps", Risk = DebloatRisk.Moderate, Profiles = MaxOnly,
            Title = "Fjern Få hjælp", Subject = "Få hjælp", Description = "Microsofts hjælpe-app fjernes.",
            Note = "Windows 11's fejlfindere åbner 'Få hjælp' – uden den virker de ikke.",
            Apps = [App("Microsoft.GetHelp", "Få hjælp", "9PKDZBMV1H3T")],
        },
        new()
        {
            Id = "apps.office_hub", Category = "apps", Risk = DebloatRisk.Safe, Profiles = RecUp,
            Title = "Fjern Microsoft 365-appen", Subject = "Microsoft 365-appen", Description = "Start-appen, der reklamerer for Microsoft 365, fjernes. Installerede Word, Excel osv. røres ikke.",
            Apps = [App("Microsoft.MicrosoftOfficeHub", "Microsoft 365 (Office)", "9WZDNCRD29V9")],
        },
        new()
        {
            Id = "apps.teams_consumer", Category = "apps", Risk = DebloatRisk.Safe, Profiles = RecUp,
            Title = "Fjern den gamle private Teams", Subject = "Teams (gammel)", Description = "Chat-knappens gamle private Teams i Windows 11 fjernes. Den nye Teams røres ikke.",
            Apps = [App("MicrosoftTeams", "Microsoft Teams (gammel)")],
        },
        new()
        {
            Id = "apps.teams_new", Category = "apps", Risk = DebloatRisk.Moderate, Profiles = MaxOnly,
            Title = "Fjern Teams (ny)", Subject = "Teams (ny)", Description = "Den nye Teams-app, som Windows lægger ind, fjernes for alle brugere.",
            Note = "Det er samme app til arbejde og skole – fravælg punktet, hvis nogen bruger Teams.",
            Apps = [App("MSTeams", "Microsoft Teams", "XP8BT8DW290MPQ")],
        },
        new()
        {
            Id = "apps.linkedin", Category = "apps", Risk = DebloatRisk.Safe, Profiles = RecUp,
            Title = "Fjern LinkedIn", Subject = "LinkedIn", Description = "LinkedIn-appen, der ligger i Start som reklame, fjernes.",
            Apps = [App("7EE7776C.LinkedInforWindows", "LinkedIn", "9WZDNCRFJ4Q7")],
        },
        new()
        {
            Id = "apps.devhome", Category = "apps", Risk = DebloatRisk.Safe, Profiles = RecUp,
            Title = "Fjern Dev Home", Subject = "Dev Home", Description = "Udviklerværktøjet, som Microsoft har stoppet, fjernes.",
            Apps = [App("Microsoft.Windows.DevHome", "Dev Home")],
        },
        new()
        {
            Id = "apps.mixed_reality_3d", Category = "apps", Risk = DebloatRisk.Safe, Profiles = RecUp,
            Title = "Fjern 3D- og Mixed Reality-apps", Subject = "3D- og Mixed Reality-apps", Description = "3D Viewer, Print 3D, 3D Builder og Mixed Reality-portalen fjernes.",
            Apps =
            [
                App("Microsoft.Microsoft3DViewer", "3D Viewer", "9NBLGGH42THS"), App("Microsoft.Print3D", "Print 3D"),
                App("Microsoft.3DBuilder", "3D Builder"), App("Microsoft.MixedReality.Portal", "Mixed Reality-portal"),
            ],
        },
        new()
        {
            Id = "apps.legacy_msn", Category = "apps", Risk = DebloatRisk.Safe, Profiles = RecUp,
            Title = "Fjern gamle MSN- og Windows 10-apps", Subject = "Gamle MSN-apps", Description = "Penge, Sport, Rejser, Mad og drikke, Sway, Skype, Mobilabonnementer, Beskeder m.fl. (alle udgået) fjernes.",
            Apps =
            [
                App("Microsoft.BingFinance", "MSN Penge"), App("Microsoft.BingSports", "MSN Sport"), App("Microsoft.BingTravel", "MSN Rejser"),
                App("Microsoft.BingFoodAndDrink", "MSN Mad og drikke"), App("Microsoft.BingHealthAndFitness", "MSN Sundhed"),
                App("Microsoft.Office.Sway", "Sway"), App("Microsoft.SkypeApp", "Skype"), App("Microsoft.Messaging", "Beskeder"),
                App("Microsoft.OneConnect", "Mobilabonnementer"), App("Microsoft.NetworkSpeedTest", "Network Speed Test"), App("Microsoft.News", "Nyheder"),
            ],
        },
        new()
        {
            Id = "apps.cortana", Category = "apps", Risk = DebloatRisk.Safe, Profiles = RecUp,
            Title = "Fjern Cortana-appen", Subject = "Cortana-appen", Description = "Cortana er udgået; appen fjernes.",
            Apps = [App("Microsoft.549981C3F5F10", "Cortana", "9NFFX4SZZ23L")],
        },
        new()
        {
            Id = "apps.outlook_new", Category = "apps", Risk = DebloatRisk.Moderate, Profiles = MaxOnly,
            Title = "Fjern nye Outlook", Subject = "Nye Outlook", Description = "Den nye Outlook-app (erstatter Mail og Kalender) fjernes. Klassisk Outlook fra Office røres ikke.",
            Note = "Lad være, hvis nogen læser mail i den nye Outlook.",
            Apps = [App("Microsoft.OutlookforWindows", "Outlook (ny)", "9NRX63209R7B")],
        },
        new()
        {
            Id = "apps.mail_calendar", Category = "apps", Risk = DebloatRisk.Moderate, Profiles = MaxOnly,
            Title = "Fjern Mail og Kalender", Subject = "Mail og Kalender", Description = "De gamle Mail- og Kalender-apps, som Microsoft har lukket, fjernes.",
            Apps = [App("microsoft.windowscommunicationsapps", "Mail og Kalender")],
        },
        new()
        {
            Id = "apps.streaming_promos", Category = "apps", Risk = DebloatRisk.Moderate, Profiles = MaxOnly,
            Title = "Fjern Spotify, Disney+, Prime Video og Netflix", Subject = "Streaming-apps", Description = "Streaming-apps, som Windows har lagt ind som reklame, fjernes.",
            Note = "Fjerner også appen, hvis den er hentet fra Microsoft Store og bruges. Programmer hentet fra producentens hjemmeside røres ikke.",
            Apps =
            [
                App("SpotifyAB.SpotifyMusic", "Spotify", "9NCBCSZSJRSB"), App("Disney.37853FC22B2CE", "Disney+", "9NXQXXLFST89"),
                App("AmazonVideo.PrimeVideo", "Prime Video", "9P6RC76MSMMJ"), App("4DF9E0F8.Netflix", "Netflix", "9WZDNCRFJ3TJ"),
            ],
        },
        new()
        {
            Id = "apps.maps", Category = "apps", Risk = DebloatRisk.Moderate, Profiles = MaxOnly,
            Title = "Fjern Kort", Subject = "Kort", Description = "Windows Kort fjernes (Microsoft er ved at udfase appen).",
            Apps = [App("Microsoft.WindowsMaps", "Kort")],
        },
        new()
        {
            Id = "apps.movies_tv", Category = "apps", Risk = DebloatRisk.Moderate, Profiles = MaxOnly,
            Title = "Fjern Film og tv", Subject = "Film og tv", Description = "Den gamle film- og tv-app fjernes. Medieafspiller røres ikke.",
            Apps = [App("Microsoft.ZuneVideo", "Film og tv", "9WZDNCRFJ3P2")],
        },
        new()
        {
            Id = "apps.power_automate", Category = "apps", Risk = DebloatRisk.Moderate, Profiles = MaxOnly,
            Title = "Fjern Power Automate", Subject = "Power Automate", Description = "Microsofts automatiseringsværktøj fjernes.",
            Apps = [App("Microsoft.PowerAutomateDesktop", "Power Automate", "9NFTCH6J7FHV")],
        },
        new()
        {
            Id = "apps.xbox", Category = "apps", Risk = DebloatRisk.Advanced, Profiles = MaxOnly,
            Title = "Fjern Xbox-apps og Game Bar", Subject = "Xbox-apps", Description = "Xbox-appen, Xbox Game Bar og tale-til-tekst-overlayet fjernes.",
            Note = "Spil fra Xbox/Game Pass kræver Xbox-appen. Xbox-login til spil (Identity Provider) røres ikke.",
            Apps =
            [
                App("Microsoft.GamingApp", "Xbox", "9MV0B5HZVK9Z"), App("Microsoft.XboxApp", "Xbox Console Companion"),
                App("Microsoft.XboxGamingOverlay", "Xbox Game Bar", "9NZKPSTSNW4P"), App("Microsoft.XboxSpeechToTextOverlay", "Xbox tale-til-tekst"),
            ],
        },

        // ============================================================== Valgfrit
        new()
        {
            Id = "extra.edge_first_run", Category = "extra", Risk = DebloatRisk.Safe, Profiles = RecUp,
            Title = "Spring Edges velkomstguide over", Subject = "Edges velkomstguide", On = "springes over", Off = "vises", Description = "Edge springer velkomstguiden og 'importér data'-skærmen over.",
            Note = "Edge viser 'Din browser administreres af din organisation'.",
            Registry = [Lm(Edge, "HideFirstRunExperience", 1)],
        },
        new()
        {
            Id = "extra.edge_shopping", Category = "extra", Risk = DebloatRisk.Safe, Profiles = LetUp,
            Title = "Slå shopping og reklamer i Edge fra", Subject = "Shopping i Edge", Description = "Shoppingassistent, Microsoft Rewards, anbefalinger og reklamemeddelelser i Edge slås fra.",
            Note = "Edge viser 'Din browser administreres af din organisation'.",
            Registry = [Lm(Edge, "EdgeShoppingAssistantEnabled", 0), Lm(Edge, "ShowRecommendationsEnabled", 0), Lm(Edge, "ShowMicrosoftRewards", 0), Lm(Edge, "PersonalizationReportingEnabled", 0)],
        },
        new()
        {
            Id = "extra.edge_sidebar", Category = "extra", Risk = DebloatRisk.Safe, Profiles = MaxOnly,
            Title = "Slå Edges sidepanel og Copilot fra", Subject = "Edges sidepanel", Description = "Edges sidepanel (med Copilot) slås fra.",
            Registry = [Lm(Edge, "HubsSidebarEnabled", 0)],
        },
        new()
        {
            Id = "extra.game_dvr", Category = "extra", Risk = DebloatRisk.Moderate, Profiles = MaxOnly,
            Title = "Slå Game DVR fra", Subject = "Game DVR", Description = "Baggrundsoptagelse af spil slås fra, hvilket kan give lidt mere ydelse.",
            Note = "Optagelse med Xbox Game Bar virker ikke bagefter.",
            Registry = [Cu(@"System\GameConfigStore", "GameDVR_Enabled", 0), Cu(@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0), Lm($@"{Pol}\GameDVR", "AllowGameDVR", 0)],
        },
        new()
        {
            Id = "extra.onedrive", Category = "extra", Risk = DebloatRisk.Advanced,   // aldrig i en profil: vælges kun med vilje
            Title = "Slå OneDrive fra og afinstallér den", Subject = "OneDrive", On = "fra og afinstalleret", Off = "til", Undo = "Politikken fjernes, så OneDrive må synkronisere igen, og OneDrive installeres igen med winget for den bruger, der er logget ind.",
            Description = "Slår OneDrive fra med politik, så den ikke synkroniserer, afinstallerer den for den bruger, der er logget ind, og stopper installationen for nye brugere.",
            Note = "Filer, der kun ligger i skyen, er stadig på onedrive.com. Mapper, der er flyttet til OneDrive (Skrivebord, Dokumenter), bliver liggende lokalt.",
            Registry =
            [
                Lm($@"{Pol}\OneDrive", "DisableFileSyncNGSC", 1),
                new(RegHive.DefaultUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "OneDriveSetup", RegKind.String, null),
            ],
            Winget = [new("Microsoft.OneDrive", "Microsoft OneDrive")],
        },

        // ============================================================== fra winutil: privatliv, producent-apps og browsere
        new()
        {
            Id = "telemetry.online_speech", Category = "telemetry", Risk = DebloatRisk.Safe, Profiles = RecUp, ProfileSince = 2,
            Title = "Slå onlinetalegenkendelse fra", Subject = "Onlinetalegenkendelse",
            Description = "Det, der siges til Windows, sendes ikke til Microsoft for at blive genkendt.",
            Note = "Diktering med Win+H virker kun med de sprog, der kan køre lokalt på pc'en.",
            Registry = [Cu(@"Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy", "HasAccepted", 0)],
        },
        new()
        {
            Id = "consumer.device_metadata", Category = "consumer", Risk = DebloatRisk.Safe, Profiles = RecUp, ProfileSince = 2,
            Title = "Stop producent-apps til nyt udstyr", Subject = "Producent-apps til nyt udstyr",
            Description = "Windows henter ikke producentens app og ikoner af sig selv, når der sættes nyt udstyr i. Drivere kommer stadig via Windows Update.",
            Registry = [Lm($@"{Pol}\Device Metadata", "PreventDeviceMetadataFromNetwork", 1)],
        },
        new()
        {
            Id = "consumer.wpbt", Category = "consumer", Risk = DebloatRisk.Moderate, Profiles = MaxOnly, ProfileSince = 2, NeedsReboot = true,
            Title = "Stop programmer fra bundkortet (WPBT)", Subject = "Programmer fra bundkortet",
            Description = "Bundkortets firmware kan ikke længere lægge producentens programmer ind i Windows ved opstart (fx Armoury Crate).",
            Note = "Tyverisikring, der ligger i firmwaren (fx Absolute), holder op med at virke.",
            Registry = [Lm(@"SYSTEM\CurrentControlSet\Control\Session Manager", "DisableWpbtExecution", 1)],
        },
        new()
        {
            Id = "extra.edge_privacy", Category = "extra", Risk = DebloatRisk.Safe, Profiles = RecUp, ProfileSince = 2,
            Title = "Begræns Edges diagnosedata og feedback", Subject = "Edges diagnosedata", On = "begrænset", Off = "ikke begrænset",
            Description = "Edge sender færrest mulige diagnosedata, spørger ikke om feedback, og fejlsider hentet fra nettet slås fra.",
            Note = "Edge viser 'Din browser administreres af din organisation'.",
            Registry = [Lm(Edge, "DiagnosticData", 0), Lm(Edge, "UserFeedbackAllowed", 0), Lm(Edge, "AlternateErrorPagesEnabled", 0)],
        },
        new()
        {
            Id = "extra.edge_promos", Category = "extra", Risk = DebloatRisk.Safe, Profiles = RecUp, ProfileSince = 2,
            Title = "Slå Edges kampagner og genveje fra", Subject = "Edges kampagner",
            Description = "Ingen opfordringer til at gøre Edge til standardbrowser, ingen Insider-reklamer og ingen ny genvej på skrivebordet efter opdateringer.",
            Note = "Edge viser 'Din browser administreres af din organisation'.",
            Registry = [Lm(Edge, "DefaultBrowserSettingsCampaignEnabled", 0), Lm(Edge, "MicrosoftEdgeInsiderPromotionEnabled", 0), Lm(Edge, "WalletDonationEnabled", 0),
                Lm(@"SOFTWARE\Policies\Microsoft\EdgeUpdate", "CreateDesktopShortcutDefault", 0)],
        },
        new()
        {
            Id = "extra.brave", Category = "extra", Risk = DebloatRisk.Safe,
            Title = "Slå Braves belønninger, wallet, VPN og AI fra", Subject = "Braves ekstra funktioner",
            Description = "Slår Braves ekstra funktioner og dataindsamling fra med browserens egne politikker. Gør ingenting, hvis Brave ikke er installeret.",
            Registry =
            [
                Lm(@"SOFTWARE\Policies\BraveSoftware\Brave", "BraveRewardsDisabled", 1), Lm(@"SOFTWARE\Policies\BraveSoftware\Brave", "BraveWalletDisabled", 1),
                Lm(@"SOFTWARE\Policies\BraveSoftware\Brave", "BraveVPNDisabled", 1), Lm(@"SOFTWARE\Policies\BraveSoftware\Brave", "BraveAIChatEnabled", 0),
                Lm(@"SOFTWARE\Policies\BraveSoftware\Brave", "BraveStatsPingEnabled", 0), Lm(@"SOFTWARE\Policies\BraveSoftware\Brave", "BraveP3AEnabled", 0),
                Lm(@"SOFTWARE\Policies\BraveSoftware\Brave", "MetricsReportingEnabled", 0),
            ],
        },

        // ============================================================== Strøm og ydelse
        new()
        {
            Id = "power.plan_balanced", Category = "power", Risk = DebloatRisk.Safe, Exclusive = DebloatPower.Group,
            Title = "Strømplan: Balanceret", Subject = "Strømplan", On = "Balanceret", Off = "en anden plan",
            Description = "Windows' standardplan: fuld ydelse, når der er brug for den, og strømbesparelse, når pc'en keder sig.",
            PowerPlan = new(DebloatPower.Balanced),
        },
        new()
        {
            Id = "power.plan_high", Category = "power", Risk = DebloatRisk.Safe, Exclusive = DebloatPower.Group,
            Title = "Strømplan: Høj ydelse", Subject = "Strømplan", On = "Høj ydelse", Off = "en anden plan",
            Description = "Processoren holdes på høj hastighed. Bruger mere strøm og giver mere varme.",
            Note = "Ikke til bærbare på batteri.",
            PowerPlan = new(DebloatPower.High, DebloatPower.HighCopy),
        },
        new()
        {
            Id = "power.plan_ultimate", Category = "power", Risk = DebloatRisk.Moderate, Exclusive = DebloatPower.Group,
            Title = "Strømplan: Ultimativ ydelse", Subject = "Strømplan", On = "Ultimativ ydelse", Off = "en anden plan",
            Description = "Windows' skjulte plan til arbejdsstationer: ingen strømbesparelse overhovedet. Fleetdeck opretter planen, når den vælges.",
            Note = "Kun til stationære pc'er. Bruger mærkbart mere strøm, også når pc'en ikke laver noget.",
            PowerPlan = new(DebloatPower.Ultimate, DebloatPower.UltimateCopy),
        },
        new()
        {
            Id = "power.wol", Category = "power", Risk = DebloatRisk.Safe, Profiles = RecUp, ProfileSince = 2, WakeOnLan = true,
            Title = "Slå Wake-on-LAN til", Subject = "Wake-on-LAN", On = "til", Off = "ikke slået til",
            Description = "De kablede netkort får lov at vække pc'en. Fleetdeck bruger det til at tænde pc'en, når der venter opgaver.",
            Note = "Det skal også være slået til i BIOS/UEFI.",
        },
        new()
        {
            Id = "power.fast_startup_off", Category = "power", Risk = DebloatRisk.Moderate, Profiles = RecUp, ProfileSince = 2, NeedsReboot = true,
            Title = "Slå hurtig start fra", Subject = "Hurtig start", On = "fra", Off = "til",
            Description = "Windows lukker helt ned, når pc'en slukkes. Med hurtig start går Windows i en slags dvale i stedet, og det giver problemer med Wake-on-LAN fra slukket tilstand, dual-boot og opdateringer, der venter på en rigtig genstart.",
            Note = "Pc'en er et par sekunder længere om at starte fra slukket tilstand.",
            Registry = [new(RegHive.Machine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", RegKind.Dword, 0, 1)],
        },
        new()
        {
            Id = "power.hibernate_off", Category = "power", Risk = DebloatRisk.Moderate, HibernateOff = true,
            Title = "Slå dvaletilstand fra", Subject = "Dvaletilstand", On = "fra", Off = "til",
            Description = "Dvale slås fra, og dvalefilen (hiberfil.sys) slettes, så der bliver flere GB fri på disken.",
            Note = "Slår også hurtig start fra. Bærbare kan ikke længere gå i dvale, når batteriet er ved at løbe tør.",
        },
        new()
        {
            Id = "power.usb_suspend_off", Category = "power", Risk = DebloatRisk.Moderate,
            Title = "Slå USB-strømbesparelse fra på lysnet", Subject = "USB-strømbesparelse på lysnet", On = "fra", Off = "til",
            Description = "Windows slukker ikke længere for USB-enheder, der er i ro, når pc'en er på lysnet. Hjælper, hvis mus, lydkort eller diske falder ud.",
            Note = "Gælder den aktive strømplan.",
            PowerSettings = [new(SubUsb, UsbSuspend, 0, null)],
        },
        new()
        {
            Id = "power.pcie_aspm_off", Category = "power", Risk = DebloatRisk.Moderate,
            Title = "Slå PCI Express-strømbesparelse fra på lysnet", Subject = "PCIe-strømbesparelse på lysnet", On = "fra", Off = "til",
            Description = "Strømstyringen af PCIe-forbindelsen slås fra, når pc'en er på lysnet. Hjælper ved hak og ved netkort eller NVMe-diske, der falder ud.",
            Note = "Gælder den aktive strømplan. Pc'en bruger lidt mere strøm i tomgang.",
            PowerSettings = [new(SubPcie, PcieAspm, 0, null)],
        },
        new()
        {
            Id = "power.sleep_never_ac", Category = "power", Risk = DebloatRisk.Moderate, Exclusive = SleepGroup,
            Title = "Aldrig strømsparetilstand på lysnet", Subject = "Strømsparetilstand på lysnet", On = "aldrig", Off = "planens egen",
            Description = "Pc'en går aldrig i strømsparetilstand, når den er tilsluttet strøm. Godt til pc'er, der skal kunne nås hele tiden.",
            Note = "Gælder den aktive strømplan. På batteri følger pc'en planens egen tidsgrænse. Kan ikke vælges sammen med 'Strømsparetilstand efter 30 min' – vælger du den ene, fravælges den anden.",
            PowerSettings = [new(SubSleep, StandbyIdle, 0, null)],
        },
        new()
        {
            Id = "power.sleep_30", Category = "power", Risk = DebloatRisk.Safe, Exclusive = SleepGroup,
            Title = "Strømsparetilstand efter 30 min (15 min på batteri)", Subject = "Strømsparetilstand", On = "efter 30/15 min", Off = "planens egen",
            Description = "Pc'en går i strømsparetilstand efter 30 minutter uden brug på lysnet og 15 minutter på batteri. Fleetdeck kan vække den igen med Wake-on-LAN.",
            Note = "Gælder den aktive strømplan. Kan ikke vælges sammen med 'Aldrig strømsparetilstand på lysnet'.",
            PowerSettings = [new(SubSleep, StandbyIdle, 1800, 900)],
        },
        new()
        {
            Id = "power.monitor_timeouts", Category = "power", Risk = DebloatRisk.Safe,
            Title = "Sluk skærmen efter 15 min (5 min på batteri)", Subject = "Skærmen slukker", On = "efter 15/5 min", Off = "planens egen",
            Description = "Skærmen slukkes efter 15 minutter uden brug på lysnet og 5 minutter på batteri. Sparer strøm og skåner skærmen.",
            Note = "Gælder den aktive strømplan.",
            PowerSettings = [new(SubVideo, VideoIdle, 900, 300)],
        },

        // ============================================================== Udseende og brugervenlighed
        new()
        {
            Id = "ux.classic_context_menu", Category = "ux", Risk = DebloatRisk.Safe, MinBuild = 22000,
            Title = "Brug den klassiske højrekliksmenu", Subject = "Højrekliksmenu", On = "klassisk", Off = "Windows 11's korte", NeedsLogoff = true,
            Description = "Viser hele menuen med det samme i stedet for Windows 11's korte menu med 'Vis flere indstillinger'.",
            Note = "Nye brugere får den, næste gang punktet anvendes.",
            Registry = [new(RegHive.Classes, @"CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32", "", RegKind.String, "")],
        },
        new()
        {
            Id = "ux.file_extensions", Category = "ux", Risk = DebloatRisk.Safe, Profiles = RecUp, ProfileSince = 2,
            Title = "Vis filtypenavne", Subject = "Filtypenavne", On = "vises", Off = "skjult",
            Description = "Stifinder viser hele filnavnet. Så er det til at se, at 'faktura.pdf.exe' er et program og ikke en PDF.",
            Registry = [Cu(Adv, "HideFileExt", 0)],
        },
        new()
        {
            Id = "ux.hidden_files", Category = "ux", Risk = DebloatRisk.Safe,
            Title = "Vis skjulte filer", Subject = "Skjulte filer", On = "vises", Off = "skjult",
            Description = "Stifinder viser skjulte filer og mapper. Windows' beskyttede systemfiler forbliver skjulte.",
            Registry = [Cu(Adv, "Hidden", 1)],
        },
        new()
        {
            Id = "ux.taskbar_left", Category = "ux", Risk = DebloatRisk.Safe, MinBuild = 22000,
            Title = "Flyt proceslinjen til venstre", Subject = "Proceslinjen", On = "til venstre", Off = "midtfor",
            Description = "Start-knappen og ikonerne står i venstre side som i Windows 10.",
            Registry = [Cu(Adv, "TaskbarAl", 0)],
        },
        new()
        {
            Id = "ux.task_view_off", Category = "ux", Risk = DebloatRisk.Safe,
            Title = "Skjul knappen Opgavevisning", Subject = "Knappen Opgavevisning", On = "skjult", Off = "vises",
            Description = "Knappen fjernes fra proceslinjen. Win+Tab virker stadig.",
            Registry = [Cu(Adv, "ShowTaskViewButton", 0)],
        },
        new()
        {
            Id = "ux.end_task", Category = "ux", Risk = DebloatRisk.Safe, Profiles = RecUp, ProfileSince = 2, MinBuild = 22631,
            Title = "Vis 'Afslut opgave' på proceslinjen", Subject = "'Afslut opgave'", On = "vises", Off = "skjult",
            Description = "Højreklik på et program på proceslinjen for at lukke det, når det hænger. Så skal man ikke ind i Jobliste.",
            Registry = [Cu($@"{Adv}\TaskbarDeveloperSettings", "TaskbarEndTask", 1)],
        },
        new()
        {
            Id = "ux.explorer_this_pc", Category = "ux", Risk = DebloatRisk.Safe,
            Title = "Lad Stifinder åbne i 'Denne pc'", Subject = "Stifinder åbner i", On = "Denne pc", Off = "Hjem",
            Description = "Stifinder starter med drev og mapper i stedet for 'Hjem' med seneste filer.",
            Registry = [Cu(Adv, "LaunchTo", 1)],
        },
        new()
        {
            Id = "ux.numlock", Category = "ux", Risk = DebloatRisk.Safe,
            Title = "Tænd NumLock ved opstart", Subject = "NumLock ved opstart", On = "tændt", Off = "slukket",
            Description = "Det numeriske tastatur virker med det samme, også på loginskærmen.",
            Note = "Lad være på små bærbare, hvor bogstavtasterne også er taltaster.",
            Registry = [new(RegHive.Logon, @"Control Panel\Keyboard", "InitialKeyboardIndicators", RegKind.String, "2"), CuS(@"Control Panel\Keyboard", "InitialKeyboardIndicators", "2")],
        },
        new()
        {
            Id = "ux.dark_mode", Category = "ux", Risk = DebloatRisk.Safe,
            Title = "Brug mørkt tema", Subject = "Tema", On = "mørkt", Off = "lyst", NeedsLogoff = true,
            Description = "Windows og apps bruger mørke farver.",
            Registry = [Cu(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0), Cu(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0)],
        },
        new()
        {
            Id = "ux.sticky_keys", Category = "ux", Risk = DebloatRisk.Safe,
            Title = "Slå genvejen til Træge taster fra", Subject = "Genvej til Træge taster", On = "fra", Off = "til",
            Description = "Fem tryk på Skift åbner ikke længere spørgsmålet om Træge taster.",
            Note = "Lad være, hvis nogen på pc'en bruger Træge taster: indstillingen slår dem også fra.",
            Registry = [CuS(@"Control Panel\Accessibility\StickyKeys", "Flags", "506")],
        },
        new()
        {
            Id = "ux.mouse_accel", Category = "ux", Risk = DebloatRisk.Advanced,
            Title = "Slå museacceleration fra", Subject = "Museacceleration", On = "fra", Off = "til", NeedsLogoff = true,
            Description = "Musen flytter sig lige langt, uanset hvor hurtigt den bevæges. Mest for spillere.",
            Note = "Føles anderledes i starten.",
            Registry = [CuS(@"Control Panel\Mouse", "MouseSpeed", "0"), CuS(@"Control Panel\Mouse", "MouseThreshold1", "0"), CuS(@"Control Panel\Mouse", "MouseThreshold2", "0")],
        },
        new()
        {
            Id = "ux.detailed_bsod", Category = "ux", Risk = DebloatRisk.Safe, Profiles = RecUp, ProfileSince = 2, NeedsReboot = true,
            Title = "Vis fejlkoder på blå skærm", Subject = "Fejlkoder på blå skærm", On = "vises", Off = "skjult",
            Description = "En blå skærm viser de tekniske detaljer, så det er nemmere at finde årsagen.",
            Registry = [Lm(@"SYSTEM\CurrentControlSet\Control\CrashControl", "DisplayParameters", 1)],
        },
        new()
        {
            Id = "ux.long_paths", Category = "ux", Risk = DebloatRisk.Safe, NeedsReboot = true,
            Title = "Tillad lange filstier", Subject = "Lange filstier", On = "tilladt", Off = "ikke tilladt",
            Description = "Programmer, der understøtter det, kan bruge stier over 260 tegn.",
            Registry = [Lm(@"SYSTEM\CurrentControlSet\Control\FileSystem", "LongPathsEnabled", 1)],
        },

        // ============================================================== Netværk
        new()
        {
            Id = "network.prefer_ipv4", Category = "network", Risk = DebloatRisk.Advanced, NeedsReboot = true,
            Title = "Foretræk IPv4 frem for IPv6", Subject = "Foretrukken protokol", On = "IPv4", Off = "IPv6 (standard)",
            Description = "Windows prøver IPv4 først, når en adresse har begge dele. IPv6 forbliver slået til. Kan hjælpe ved en dårlig IPv6-forbindelse hos udbyderen.",
            Note = "Kun relevant, hvis forbindelser er langsomme at starte eller fejler over IPv6.",
            Registry = [new(RegHive.Machine, @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters", "DisabledComponents", RegKind.Dword, 0x20, null, 0, 0, Bits: true)],
        },
        new()
        {
            Id = "network.teredo_off", Category = "network", Risk = DebloatRisk.Advanced, NeedsReboot = true,
            Title = "Slå Teredo fra", Subject = "Teredo", On = "fra", Off = "til",
            Description = "Slår Teredo-tunnelen fra med Microsofts politik. Den sender IPv6 gennem IPv4 og bruges sjældent i dag.",
            Note = "Nogle ældre spil og Xbox-gruppechat kan have brug for Teredo.",
            Registry = [new(RegHive.Machine, $@"{Pol}\TCPIP\v6Transition", "Teredo_State", RegKind.String, "Disabled")],
        },

        // ============================================================== Opdateringspolitik
        new()
        {
            Id = "updates.security_only", Category = "updates", Risk = DebloatRisk.Moderate, Editions = ProEnt,
            Title = "Udsæt nye Windows-versioner i et år", Subject = "Opdateringer", On = "nye versioner udsat", Off = "standard",
            Description = "Nye Windows-versioner (fx 24H2 → 25H2) udsættes i 365 dage og de månedlige opdateringer i 4 dage med Microsofts politikker for Windows Update for Business. Månedens sikkerhedsrettelser kommer stadig – bare 4 dage senere.",
            Note = "Virker på Pro og Enterprise. Fleetdecks opdateringsjobs installerer det, Windows tilbyder, så de følger samme udsættelse.",
            Registry = [Lm(WuPol, "DeferFeatureUpdates", 1), Lm(WuPol, "DeferFeatureUpdatesPeriodInDays", 365), Lm(WuPol, "DeferQualityUpdates", 1), Lm(WuPol, "DeferQualityUpdatesPeriodInDays", 4)],
        },

        // ============================================================== Windows-funktioner
        new()
        {
            Id = "features.wsl", Category = "features", Risk = DebloatRisk.Safe,
            Title = "Installér WSL (Linux i Windows)", Subject = "WSL",
            Description = "Windows-undersystem til Linux installeres, så Linux-programmer kan køre direkte i Windows. Selve Linux-distributionen hentes bagefter med 'wsl --install'.",
            Features = [new("VirtualMachinePlatform"), new("Microsoft-Windows-Subsystem-Linux")],
        },
        new()
        {
            Id = "features.hyperv", Category = "features", Risk = DebloatRisk.Moderate, Editions = ProEnt,
            Title = "Installér Hyper-V", Subject = "Hyper-V",
            Description = "Microsofts virtuelle maskiner (Hyper-V) installeres.",
            Note = "Kræver virtualisering slået til i BIOS. VirtualBox og VMware kan blive langsommere.",
            Features = [new("Microsoft-Hyper-V-All")],
        },
        new()
        {
            Id = "features.sandbox", Category = "features", Risk = DebloatRisk.Safe, Editions = ProEnt,
            Title = "Installér Windows Sandbox", Subject = "Windows Sandbox",
            Description = "En engangs-Windows til at prøve programmer sikkert installeres. Alt i den forsvinder, når den lukkes.",
            Note = "Kræver virtualisering slået til i BIOS.",
            Features = [new("Containers-DisposableClientVM")],
        },
        new()
        {
            Id = "features.netfx3", Category = "features", Risk = DebloatRisk.Safe,
            Title = "Installér .NET Framework 3.5", Subject = ".NET Framework 3.5",
            Description = ".NET Framework 3.5 installeres. Mange ældre programmer og spil kræver det.",
            Note = "Hentes fra Windows Update, så pc'en skal være på nettet.",
            Features = [new("NetFx3")],
        },
        new()
        {
            Id = "features.nfs", Category = "features", Risk = DebloatRisk.Moderate, Editions = ProEnt,
            Title = "Installér NFS-klienten", Subject = "NFS-klient",
            Description = "NFS-klienten installeres, så pc'en kan bruge netværksdrev på NAS og Linux-servere, der deler over NFS.",
            Features = [new("ServicesForNFS-ClientOnly"), new("ClientForNFS-Infrastructure"), new("NFS-Administration")],
        },
        new()
        {
            Id = "features.legacy_media", Category = "features", Risk = DebloatRisk.Safe,
            Title = "Installér ældre mediekomponenter", Subject = "Ældre mediekomponenter",
            Description = "Windows Media Player og DirectPlay installeres. Ældre spil og programmer bruger dem.",
            Features = [new("WindowsMediaPlayer"), new("MediaPlayback"), new("DirectPlay"), new("LegacyComponents")],
        },
    ];

    public static readonly Dictionary<string, DebloatItem> ById = Items.ToDictionary(i => i.Id, StringComparer.OrdinalIgnoreCase);

    public static DebloatItem? Find(string id) => ById.GetValueOrDefault(id);

    /// <summary>Punkterne i en profil, sådan som den så ud i <paramref name="version"/> (i katalogets rækkefølge).</summary>
    public static IEnumerable<DebloatItem> ForProfile(string? profile, int version = DebloatProfiles.Current) =>
        string.IsNullOrEmpty(profile) ? [] : Items.Where(i => i.ProfileSince <= version && i.Profiles.Contains(profile, StringComparer.OrdinalIgnoreCase));

    /// <summary>Punkter, profilen har fået, siden maskinen valgte den i <paramref name="version"/>.</summary>
    public static IEnumerable<DebloatItem> NewInProfile(string? profile, int version) =>
        ForProfile(profile).Where(i => i.ProfileSince > version);

    /// <summary>Højst ét punkt pr. gruppe: det sidst nævnte vinder.</summary>
    public static List<string> KeepOnePerGroup(IEnumerable<string> ids)
    {
        var list = ids.ToList();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keep = new List<string>();
        for (var n = list.Count - 1; n >= 0; n--)
            if (Find(list[n])?.Exclusive is not { } g || taken.Add(g)) keep.Insert(0, list[n]);
        return keep;
    }

    /// <summary>
    /// Maskinens valg: profilens punkter + egne tilvalg − egne fravalg. Ukendte id'er ignoreres.
    /// </summary>
    public static List<DebloatItem> Resolve(string? profile, IEnumerable<string>? include, IEnumerable<string>? exclude, int version = DebloatProfiles.Current)
    {
        var inc = new HashSet<string>(KeepOnePerGroup(include ?? []), StringComparer.OrdinalIgnoreCase);
        var exc = new HashSet<string>(exclude ?? [], StringComparer.OrdinalIgnoreCase);
        var prof = ForProfile(profile, version).Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Items.Where(i => (prof.Contains(i.Id) || inc.Contains(i.Id)) && !exc.Contains(i.Id)).ToList();
    }

    /// <summary>Jokertegn som i PowerShell's -like (kun *), uden forskel på store og små bogstaver.</summary>
    public static bool Like(string text, string pattern) =>
        System.Text.RegularExpressions.Regex.IsMatch(text, "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace(@"\*", ".*") + "$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static bool IsProtected(string packageName) => ProtectedApps.Any(p => Like(packageName, p));

    public static DebloatCategory CategoryOf(DebloatItem i) => Categories.First(c => c.Id == i.Category);
    /// <summary>Tilstandens ord, når punktet er anvendt (fx "fra", "vises", "fjernet").</summary>
    public static string StateOn(DebloatItem i) => i.On ?? CategoryOf(i).AppliedLabel;
    /// <summary>Tilstandens ord, når punktet ikke er anvendt (fx "til", "skjult", "installeret").</summary>
    public static string StateOff(DebloatItem i) => i.Off ?? CategoryOf(i).NotAppliedLabel;

    /// <summary>Hvad Fortryd gør med punktet – udledt af, hvad punktet ændrer, så teksten passer til motoren.</summary>
    public static string UndoText(DebloatItem i)
    {
        if (i.Undo is { } u) return u;
        if (i.WakeOnLan) return "Ændrer intet: Wake-on-LAN forbliver slået til, fordi Fleetdeck bruger det til at vække pc'en.";
        if (i.PowerPlan is not null)
            return "Pc'en skifter tilbage til den strømplan, den havde, før Fleetdeck skiftede første gang (findes den ikke mere: Balanceret). Fleetdecks egne kopier af planerne slettes.";
        if (i.Features.Length > 0) return "Funktionen fjernes igen, men kun hvis Fleetdeck installerede den. Var den der i forvejen, bliver den.";
        if (i.HibernateOff) return "Dvale slås til igen, hvis den var slået til før.";
        if (i.Apps.Length > 0)
        {
            var back = i.Apps.Where(a => a.StoreId is not null).Select(a => a.Title).ToList();
            var lost = i.NotReinstallable.Select(a => a.Title).ToList();
            var one = i.Apps.Length == 1;
            if (back.Count == 0) return (one ? "Appen" : "Apps") + " kan ikke hentes igen herfra, fordi de ikke længere findes i Microsoft Store.";
            var t = (one ? "Appen hentes" : "Apps hentes") + " igen fra Microsoft Store for den bruger, der er logget ind (andre brugere kan selv hente dem i Store).";
            return lost.Count > 0 ? t + " Kan ikke hentes igen: " + string.Join(", ", lost) + "." : t;
        }
        if (i.PowerSettings.Length > 0) return "Den aktive strømplans tidligere værdier sættes tilbage.";
        if (i.Services.Length > 0 && i.Registry.Length == 0 && i.Tasks.Length == 0) return "Tjenesten får sin tidligere starttype igen.";
        if (i.Tasks.Length > 0 && i.Registry.Length == 0) return "De planlagte opgaver slås til igen, hvis de var slået til før.";
        return "Det, der stod før, sættes tilbage. Har nogen ændret det siden, får det lov at blive.";
    }
}
