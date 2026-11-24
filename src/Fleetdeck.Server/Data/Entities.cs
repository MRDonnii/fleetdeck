using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Fleetdeck.Server.Data;

public static class Roles
{
    public const string Admin = "admin", Viewer = "viewer", Parent = "parent";
}

/// <summary>Hvad en forælder må på de iPads, brugeren har adgang til (administratorer må alt).</summary>
public static class FamilyPerms
{
    public const string Pause = "pause", Rules = "rules", Update = "update", Lock = "lock", Erase = "erase";
    public static readonly string[] All = [Pause, Rules, Update, Lock, Erase];
    public static readonly string[] Default = [Pause, Rules];
    /// <summary>Det, en forælder kan få: kun styring af barnet. Resten (enheden) er administratorens.</summary>
    public static readonly string[] Parent = [Pause, Rules];
}

public class User
{
    public int Id { get; set; }
    [MaxLength(64)] public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    [MaxLength(16)] public string Role { get; set; } = Roles.Admin;
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Created { get; set; } = DateTime.UtcNow;
    /// <summary>Forælder: adgang til alle iPads (også nye) – ellers kun dem i <see cref="Ipads"/>.</summary>
    public bool IpadAll { get; set; }
    public List<int> Ipads { get; set; } = [];
    public List<string> Perms { get; set; } = [];
    public DateTime? LastLogin { get; set; }

    // Totrinsbekræftelse (TOTP). Hemmeligheden er krypteret med serverens DataProtection-nøgle; null = slået fra.
    public string? TotpSecret { get; set; }
    public DateTime? TotpEnabledAt { get; set; }
    /// <summary>Seneste brugte 30-sekunders-trin: samme kode kan ikke bruges to gange.</summary>
    public long TotpLastStep { get; set; }
    /// <summary>Hashede engangs-gendannelseskoder (en brugt kode fjernes).</summary>
    public List<string> RecoveryCodes { get; set; } = [];
    /// <summary>Mislykkede forsøg i træk (adgangskode eller kode) og spærring med stigende ventetid.</summary>
    public int FailedLogins { get; set; }
    public DateTime? LockoutUntil { get; set; }
    /// <summary>Må logges ind fra Kontrolrum (signeret SSO, uden adgangskode og kode). Standard: fra. Kun administratorer kan ændre det.</summary>
    public bool KontrolrumSso { get; set; }
    [NotMapped] public bool TotpEnabled => TotpSecret is not null;
}

public static class IpadKinds { public const string Child = "child", Family = "family"; }
/// <summary>
/// Enhedstyper i iPad-modulet. iPad og iPhone styres med en DNS-profil (og evt. låst profil/MDM); Apple TV og Android styres
/// indtil videre via deres faste IP-adresse på hjemmets netværk (AdGuard-klient pr. IP).
/// </summary>
public static class DeviceTypes
{
    public const string Ipad = "ipad", Iphone = "iphone", AppleTv = "appletv", Android = "android";
    public static readonly string[] All = [Ipad, Iphone, AppleTv, Android];
    public static bool Valid(string? t) => t is Ipad or Iphone or AppleTv or Android;
    /// <summary>Normaliserer en type fra en forespørgsel (null/tom = ingen; ukendt = null).</summary>
    public static string? Parse(string? t) => t?.Trim().ToLowerInvariant() is { Length: > 0 } x && Valid(x) ? x : null;
    /// <summary>iOS-enheder (DNS-profil via QR, låst profil, USB, MDM).</summary>
    public static bool IsIos(string t) => t is Ipad or Iphone;
    /// <summary>Typer, der kun kan styres via en fast IP-adresse derhjemme (indtil profiler til dem er klar).</summary>
    public static bool NeedsLanIp(string t) => t is AppleTv or Android;
    /// <summary>Gæt typen ud fra modelnavnet (bruges til eksisterende rækker): "Apple TV HD", "AppleTV5,3", "iPhone 15" …</summary>
    public static string FromModel(string? model)
    {
        var m = (model ?? "").Replace(" ", "").ToLowerInvariant();
        if (m.Contains("appletv")) return AppleTv;
        if (m.Contains("iphone")) return Iphone;
        if (m.Contains("android") || m.Contains("pixel") || m.Contains("galaxy")) return Android;
        return Ipad;
    }
    /// <summary>Tjekker typen og IP-adressen sammen. Returnerer en fejltekst eller null.</summary>
    public static string? Validate(string type, string? lanIp)
    {
        if (!Valid(type)) return "Ukendt enhedstype";
        var ip = lanIp?.Trim() ?? "";
        if (ip.Length > 0 && !System.Net.IPAddress.TryParse(ip, out _)) return "Skriv en IP-adresse, fx 192.168.1.87";
        if (ip.Length == 0 && NeedsLanIp(type))
            return type == AppleTv ? "Apple TV styres via sin faste IP-adresse derhjemme – skriv IP-adressen (lås den fast i routeren)"
                : "Enheden styres via sin faste IP-adresse derhjemme – skriv IP-adressen (lås den fast i routeren)";
        return null;
    }
}

/// <summary>YouTube-tilstande. <see cref="KidsOnly"/> = kun YouTube Kids (almindelig YouTube spærret så godt det kan lade sig gøre med DNS).</summary>
public static class YtModes
{
    public const string Open = "open", Moderate = "moderate", Strict = "strict", Blocked = "blocked", KidsOnly = "kids";
    public static bool Valid(string? m) => m is Open or Moderate or Strict or Blocked or KidsOnly;
}

/// <summary>YouTube Kids styres for sig: tilladt eller spærret.</summary>
public static class KidsModes
{
    public const string Allowed = "allowed", Blocked = "blocked";
    public static bool Valid(string? m) => m is Allowed or Blocked;
}

/// <summary>
/// En iPad. Internettet styres gennem AdGuard Home (krypteret DNS med iPad'ens eget klient-id), så regler, sengetid og YouTube
/// følger iPad'en – også på 4G og andres wifi. Ugeskemaer er 336 tegn: 7 dage × 48 halve timer, mandag 00:00 først.
/// </summary>
public class Ipad
{
    public int Id { get; set; }
    [MaxLength(80)] public string Name { get; set; } = "";
    [MaxLength(16)] public string Kind { get; set; } = IpadKinds.Child;
    /// <summary>Enhedstype: ipad, iphone, appletv eller android (<see cref="DeviceTypes"/>).</summary>
    [MaxLength(16)] public string DeviceType { get; set; } = DeviceTypes.Ipad;
    [MaxLength(80)] public string? Owner { get; set; }
    [MaxLength(80)] public string? Model { get; set; }
    /// <summary>Klient-id i AdGuard (indgår i DNS-adressen …/dns-query/&lt;id&gt;).</summary>
    [MaxLength(40)] public string ClientId { get; set; } = "";
    /// <summary>Fast IP-adresse på hjemmets wifi (valgfri): så virker reglerne derhjemme uden profil (iPad'en skal bruge AdGuard som DNS).</summary>
    [MaxLength(64)] public string? LanIp { get; set; }
    /// <summary>Ugeskema: '0' = fri, 's' = skoletid (spil, sociale medier og video spærret), 'b' = sengetid (alt spærret undtagen det tilladte).</summary>
    [MaxLength(336)] public string Schedule { get; set; } = new('0', 336);
    public List<string> BlockedServices { get; set; } = [];
    public List<string> AllowedDomains { get; set; } = [];
    public List<string> BlockedDomains { get; set; } = [];
    public bool FilterAdult { get; set; } = true;
    public bool SafeSearch { get; set; } = true;
    [MaxLength(16)] public string YtMode { get; set; } = YtModes.Moderate;
    /// <summary>Hvornår YouTube må bruges: '1' = tilladt (336 tegn). Tom = altid (bortset fra sengetid).</summary>
    [MaxLength(336)] public string? YtWindows { get; set; }
    /// <summary>YouTube-minutter pr. dag (0 = ingen grænse).</summary>
    public int YtDailyMin { get; set; }
    public int YtBonusMin { get; set; }
    public DateOnly? YtBonusDay { get; set; }
    /// <summary>YouTube Kids: tilladt eller spærret, uafhængigt af <see cref="YtMode"/>.</summary>
    [MaxLength(16)] public string KidsMode { get; set; } = KidsModes.Allowed;
    /// <summary>Sand: YouTube Kids deler YouTubes tider og minutter (som før appsene blev adskilt). Falsk: felterne herunder gælder.</summary>
    public bool KidsLinked { get; set; } = true;
    /// <summary>Hvornår YouTube Kids må bruges: '1' = tilladt (336 tegn). Tom = altid.</summary>
    [MaxLength(336)] public string? KidsWindows { get; set; }
    /// <summary>YouTube Kids-minutter pr. dag (0 = ingen grænse).</summary>
    public int KidsDailyMin { get; set; }
    public DateTime? PauseUntil { get; set; }
    /// <summary>Ekstra tid: sengetid/skoletid er løftet indtil.</summary>
    public DateTime? FreeUntil { get; set; }
    /// <summary>Seneste DNS-opslag fra enheden – med klient-id eller fra dens faste IP-adresse derhjemme.</summary>
    public DateTime? LastSeen { get; set; }
    /// <summary>Seneste DNS-opslag med enhedens klient-id, altså via DNS-profilen. Opslag fra den faste IP-adresse tæller ikke.</summary>
    public DateTime? ProfileSeen { get; set; }
    /// <summary>Advarsel om, at styringen ikke bruges: null = ingen åben advarsel. null på <see cref="WatchEnabled"/> = standard for enhedstypen.</summary>
    public DateTime? WatchAlertSince { get; set; }
    public bool? WatchEnabled { get; set; }
    /// <summary>Ingen advarsel før dette tidspunkt ("Tager på ferie").</summary>
    public DateTime? WatchSnoozeUntil { get; set; }
    /// <summary>Trin i opsætningsguiden, som en voksen selv har markeret som gjort (kan ikke kontrolleres af Fleetdeck).</summary>
    public List<string> SetupDone { get; set; } = [];
    [MaxLength(64)] public string? Applied { get; set; }
    public string? ApplyError { get; set; }
    [MaxLength(40)] public string? ProfileToken { get; set; }
    public DateTime? ProfileTokenExpires { get; set; }
    public DateTime Created { get; set; } = DateTime.UtcNow;

    // ---- Statisk tilstand: låst profil (installeres med Apple Configurator/cfgutil på en overvåget iPad)
    /// <summary>Apps (bundle-id'er), der skjules og ikke kan åbnes (blockedAppBundleIDs – kræver overvågning).</summary>
    public List<string> HiddenApps { get; set; } = [];
    /// <summary>App Store er synlig (falsk: App Store skjules – apps kan stadig installeres og opdateres via Apple Configurator).</summary>
    public bool AllowAppInstall { get; set; }
    public bool AllowInAppPurchases { get; set; }
    /// <summary>Apple-konto og andre konti må ændres.</summary>
    public bool AllowAccountChanges { get; set; }
    /// <summary>Aldersgrænse for apps (4, 9, 13, 16 eller 18 år). null = ingen grænse.</summary>
    public int? AgeRatingApps { get; set; }
    /// <summary>Overvåget (supervised) – meldt af Mac'en, når iPad'en er tilsluttet.</summary>
    public bool Supervised { get; set; }
    /// <summary>Den låste profils version: tælles op, hver gang noget i profilen ændres.</summary>
    public int LockedProfileVersion { get; set; } = 1;
    /// <summary>Den version, Mac'en sidst så installeret på iPad'en (null = ingen/ukendt).</summary>
    public int? LockedProfileApplied { get; set; }
    /// <summary>Fingeraftryk af profilens indhold (uden version), så versionen kun tælles op ved rigtige ændringer.</summary>
    [MaxLength(64)] public string? LockedProfileHash { get; set; }
    /// <summary>Koden til at fjerne den låste profil på iPad'en (krypteret med DataProtection).</summary>
    public string? RemovalPassword { get; set; }
    /// <summary>iPad'ens ECID og serienummer, når den er linket til en enhed, Mac'en har set.</summary>
    [MaxLength(40)] public string? Ecid { get; set; }
    [MaxLength(40)] public string? Serial { get; set; }
    [MaxLength(40)] public string? OsVersion { get; set; }
    /// <summary>Sidst set tilsluttet en Mac.</summary>
    public DateTime? DeviceSeen { get; set; }
}

/// <summary>Aktivitet: én række pr. iPad, tjeneste og 5-minutters-periode med trafik (minutter = antal × 5).</summary>
public class IpadSlot
{
    public long Id { get; set; }
    public int IpadId { get; set; }
    [MaxLength(48)] public string Service { get; set; } = "";
    public DateTime Slot { get; set; }
}

public class Setting
{
    [Key, MaxLength(64)] public string Key { get; set; } = "";
    public string? Value { get; set; }
}

public class Profile
{
    public int Id { get; set; }
    [MaxLength(100)] public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Enforce { get; set; } = true;
    public string UpgradeMode { get; set; } = "off";        // off | daily | weekly
    public string UpgradeTime { get; set; } = "03:00";
    public int UpgradeDay { get; set; } = 6;                  // 0 = mandag
    public List<string> UpgradeExclude { get; set; } = [];
    public string WindowsUpdateMode { get; set; } = "off";
    public string WindowsUpdateTime { get; set; } = "03:30";
    public int WindowsUpdateDay { get; set; } = 6;
    public string RebootPolicy { get; set; } = "never";      // never | after_upgrade
    public string BackupMode { get; set; } = "off";
    public string BackupTime { get; set; } = "21:00";
    public int BackupDay { get; set; } = 6;
    public bool ProvDrivers { get; set; } = true;
    public bool ProvRestore { get; set; } = true;
    public bool ProvAppearance { get; set; } = true;
    public bool ProvWindowsUpdate { get; set; } = true;
    // Natlig vedligeholdelse: tænd (Wake-on-LAN) → opgaver → genstart/sluk → morgenrapport
    // Hold kørende: tjenester, agenten genstarter med det samme, hvis de stopper (Linux: systemd-enheder)
    public List<string> KeepRunning { get; set; } = [];
    // Gendannelsespunkt (Linux): stier/mønstre, der pakkes og gemmes på serveren, så maskinen kan sættes helt op igen
    public List<string> KitPaths { get; set; } = [];
    // SD-kort til Raspberry Pi ("Lav SD-kort", Services/PiCards.cs): det, der kun hørte til den gamle opsætning, og boot-linjer, der flettes ind
    public List<string> PiExclude { get; set; } = [];
    public string PiConfigLines { get; set; } = "";
    public string PiCmdlineFlags { get; set; } = "";
    public string KitMode { get; set; } = "off";              // off | daily | weekly
    public string KitTime { get; set; } = "01:30";
    public int KitDay { get; set; } = 6;
    public string MaintMode { get; set; } = "off";            // off | daily | weekly
    public string MaintTime { get; set; } = "02:30";
    public int MaintDay { get; set; } = 6;
    public bool MaintWake { get; set; } = true;
    public List<string> MaintTasks { get; set; } = ["windows_update", "upgrade"];   // windows_update | upgrade | disk_cleanup | defender_signatures
    public string MaintAfter { get; set; } = "shutdown_if_woken";   // none | reboot | shutdown_if_woken | shutdown
    public bool MaintReport { get; set; } = true;
    // Opdateringsmotoren (Services/Maintenance.cs): runder i fast rækkefølge, fortsættelse efter genstart, indhentning
    /// <summary>Drivere fra Windows Update med (dem, Fleetdeck fraråder – gamle, nedgraderinger – springes stadig over).</summary>
    public bool UpdDrivers { get; set; } = true;
    /// <summary>Firmware med: altid sidst, kun på netstrøm og aldrig selve BIOS'en. Fra som standard (BIOS-delen er sin egen vej).</summary>
    public bool UpdFirmware { get; set; }
    /// <summary>Windows-funktionsopdateringer (ny Windows-version).</summary>
    public bool UpdFeatureUpdates { get; set; }
    /// <summary>Højst så mange runder (og genstarter) pr. kørsel.</summary>
    public int UpdMaxRounds { get; set; } = 4;
    /// <summary>Et vindue, der blev misset (slukket, offline, serveren nede), indhentes inden for så mange døgn.</summary>
    public int UpdCatchUpDays { get; set; } = 3;
    /// <summary>Aldrig automatisk opdatering eller genstart (fx en styring, der skal køre uafbrudt) – kun med et tryk ud for hver linje.</summary>
    public bool UpdNeverAuto { get; set; }
    /// <summary>Genstart uden varsel (ingen nedtælling, intet krav om at pc'en er ledig) – kun til ejerens egne pc'er.</summary>
    public bool UpdRebootNoWarning { get; set; }
    /// <summary>Stilletid ("ikke i arbejdstiden"): ingen genstart eller slukning mellem disse klokkeslæt (tom = ingen).</summary>
    [MaxLength(5)] public string UpdQuietFrom { get; set; } = "";
    [MaxLength(5)] public string UpdQuietTo { get; set; } = "";
    /// <summary>Hjælperens rettelser der må køre automatisk (id'er fra RemediationCatalog).</summary>
    public List<string> AutoFix { get; set; } = ["defender_signatures", "crash_analysis", "retry_failed"];
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public List<ProfilePackage> Packages { get; set; } = [];
}

/// <summary>Én maskines natlige vedligeholdelse: hvilke jobs der hørte til, og hvordan det gik.</summary>
public class MaintenanceRun
{
    public int Id { get; set; }
    public int MachineId { get; set; }
    public Machine? Machine { get; set; }
    [MaxLength(64)] public string Batch { get; set; } = "";    // samme kørsel på tværs af maskiner – én morgenrapport
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public bool WasOffline { get; set; }
    public bool WakeSent { get; set; }
    public List<int> JobIds { get; set; } = [];
    public DateTime? Finished { get; set; }
    /// <summary>waiting | running | rebooting | reboot_wait | hung | done | partial | failed | offline | skipped | blocked | superseded</summary>
    [MaxLength(16)] public string State { get; set; } = "running";
    public string Summary { get; set; } = "";
    public bool Reported { get; set; }
    // ---- opdateringsmotoren (Engine = true): serveren ejer tilstanden og laver næste skridt, når det forrige er færdigt
    public bool Engine { get; set; }
    /// <summary>maintenance | schedule | manual</summary>
    [MaxLength(16)] public string Kind { get; set; } = "maintenance";
    /// <summary>Opgaverne og "bagefter" (MaintPlan) som JSON – gemt, så kørslen kan fortsætte efter genstart af serveren.</summary>
    public string PlanJson { get; set; } = "{}";
    /// <summary>"" (venter) | tasks | round | round_wait | reboot | reboot_wait | final | final_wait | final_wait_nr | verify | legacy | done</summary>
    [MaxLength(16)] public string Step { get; set; } = "";
    public int Round { get; set; }
    public int MaxRounds { get; set; } = 4;
    public int Reboots { get; set; }
    public int? CurrentJobId { get; set; }
    /// <summary>Vinduets start (UTC) og hvor længe et misset vindue indhentes.</summary>
    public DateTime NotBefore { get; set; } = DateTime.UtcNow;
    public DateTime CatchUpUntil { get; set; } = DateTime.UtcNow.AddDays(3);
    /// <summary>Indhentning: maskinen kom først online længe efter vinduet – installér stille, genstart kun når den er ledig.</summary>
    public bool CatchUp { get; set; }
    /// <summary>Næste skridt tidligst (nyt forsøg på en genstart, der blev sprunget over).</summary>
    public DateTime? NextAt { get; set; }
    public int RebootTries { get; set; }
    /// <summary>Hele kørslens samlede resultat (UpdateReport): installeret, fejlet, venter, mangler.</summary>
    public string ReportJson { get; set; } = "";
    /// <summary>Hvorfor runderne stoppede (fx "ingen fremgang", "højst 4 runder").</summary>
    [MaxLength(200)] public string StopReason { get; set; } = "";
    /// <summary>Det, der manglede efter forrige runde – til "ingen fremgang".</summary>
    public List<string> LastLeft { get; set; } = [];
    /// <summary>Nye forsøg brugt (afbrudt runde, fejlet søgning) – højst UpdateRounds.MaxRetries.</summary>
    public int Retries { get; set; }
    /// <summary>Det, der blev installeret lige før en genstart (UpdateItem-liste som JSON) – kommer det igen bagefter, blev det rullet tilbage.</summary>
    public string LastInstalledJson { get; set; } = "";
}

public class Package
{
    public int Id { get; set; }
    [MaxLength(200)] public string Name { get; set; } = "";
    [MaxLength(16)] public string Type { get; set; } = "winget";
    public string Identifier { get; set; } = "";
    public string Args { get; set; } = "";
    [MaxLength(16)] public string RunAs { get; set; } = "system";
    public string Detect { get; set; } = "";
    public string Category { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.UtcNow;
}

public static class Intents
{
    public const string Required = "required", Available = "available", Uninstall = "uninstall";
    public static readonly string[] All = [Required, Available, Uninstall];
}

public class ProfilePackage
{
    public int ProfileId { get; set; }
    public Profile? Profile { get; set; }
    public int PackageId { get; set; }
    public Package? Package { get; set; }
    [MaxLength(16)] public string Intent { get; set; } = Intents.Required;
}

public class Machine
{
    public int Id { get; set; }
    /// <summary>Værtsnavnet, som agenten tilmelder sig med (maskinens identitet). Ændres aldrig fra brugerfladen.</summary>
    [MaxLength(64)] public string Name { get; set; } = "";
    /// <summary>Navnet, som admin har givet maskinen. Agenten rører det aldrig; null = vis værtsnavnet.</summary>
    [MaxLength(64)] public string? DisplayName { get; set; }
    /// <summary>Det navn, der vises overalt (brugerflade, Home Assistant, beskeder). I EF-forespørgsler: <c>m.DisplayName ?? m.Name</c>.</summary>
    [NotMapped] public string Label => DisplayName ?? Name;
    [MaxLength(16)] public string Platform { get; set; } = "windows";   // windows | linux
    public string? TokenHash { get; set; }
    public int? ProfileId { get; set; }
    public Profile? Profile { get; set; }
    public List<string> Tags { get; set; } = [];
    public string Notes { get; set; } = "";
    public DateTime? Enrolled { get; set; }
    public DateTime? LastSeen { get; set; }
    /// <summary>Siden hvornår maskinen har været online uden afbrydelse (bærbare: siden den sidst vågnede eller kom på nettet).</summary>
    public DateTime? OnlineSince { get; set; }
    /// <summary>Hvornår serveren begyndte at føre tidslinje (offline-perioder og opstarter) for maskinen. Før det er tidslinjen "ukendt".</summary>
    public DateTime? TimelineSince { get; set; }
    /// <summary>Intel AMT-adgangskoden, som Fleetdeck har sat (krypteret med DataProtection).</summary>
    public string? AmtPassword { get; set; }
    /// <summary>Programmer (winget-id/pakkenavn), der aldrig må opdateres på netop denne maskine.</summary>
    public List<string> UpgradeExclude { get; set; } = [];
    /// <summary>Maskinens webflade (fx Unraid eller en styring) – "Forbind" åbner den. Unraid får sin automatisk.</summary>
    [MaxLength(300)] public string? WebUrl { get; set; }
    public DateTime? LastInventory { get; set; }
    public DateTime? LastHealth { get; set; }
    public string? AgentVersion { get; set; }
    public string? Ip { get; set; }
    public string? Mac { get; set; }
    public string? User { get; set; }
    public string FactsJson { get; set; } = "{}";
    public string HealthJson { get; set; } = "{}";
    public string IgnoredAlertsJson { get; set; } = "[]";   // alarmer brugeren har valgt at ignorere på denne maskine
    public bool InventoryRequested { get; set; } = true;
    public int? HealthScore { get; set; }
    [MaxLength(16)] public string HealthState { get; set; } = "unknown";
    /// <summary>Mac: iPads, der sidst var tilsluttet med kabel (liste af IosDeviceInfo som JSON).</summary>
    public string IosDevicesJson { get; set; } = "[]";
    public DateTime? IosDevicesAt { get; set; }

    // ---- tillid mellem agent og server (se docs/sikkerhed-agent.md)
    /// <summary>Agentens token blev udstedt til et andet id (en godkendt erstatning, hvis ventende post er slettet). Null = tokenets id er <see cref="Id"/>.</summary>
    public int? TokenAlias { get; set; }
    /// <summary>Venter på godkendelse som erstatning for denne maskine (samme navn, tilmeldt uden bevis for at være den samme maskine).</summary>
    public int? ReplacesId { get; set; }
    /// <summary>Den ventende tilmelding bad om genskabelse fra gendannelsessæt: køres først efter godkendelse.</summary>
    public bool PendingProvision { get; set; }
    /// <summary>Profilen, den ventende tilmelding blev godkendt eller installeret med (0 = bevidst ingen): bruges, hvis den beholdes som ny.</summary>
    public int? PendingProfileId { get; set; }
    /// <summary>Hvordan maskinen blev tilmeldt: key (global nøgle), code (engangskode), token (geninstallation med gammelt token), deploy.</summary>
    [MaxLength(16)] public string? EnrollMethod { get; set; }
    /// <summary>Agentens melding: null = ældre agent uden signerede jobs, "unpinned" = understøtter, men har ikke pinnet nøglen, "pinned".</summary>
    [MaxLength(16)] public string? JobTrust { get; set; }
    /// <summary>Seneste afvisning, agenten har meldt (fx usigneret svar).</summary>
    [MaxLength(300)] public string? TrustError { get; set; }
    public DateTime? TrustErrorAt { get; set; }
    /// <summary>http eller https ved seneste check-in, og om agenten har fastlåst serverens TLS-nøgle.</summary>
    [MaxLength(8)] public string? Transport { get; set; }
    /// <summary>Agenten har meldt, at den kan åbne en terminal.</summary>
    public bool TerminalCapable { get; set; }
    /// <summary>Agenten har meldt, at den kan sende systemloggen live (og indsamler fejlloggen).</summary>
    public bool LiveLogCapable { get; set; }
    public bool TerminalFavorite { get; set; }
    /// <summary>Beskyttet maskine: risikable kommandoer kræver, at maskinens navn skrives.</summary>
    public bool Protected { get; set; }
    public bool TlsPinned { get; set; }
    /// <summary>Siden hvornår der har ventet system-opdateringer (Windows Update/apt/softwareupdate). Null = ingen venter.</summary>
    public DateTime? OsUpdatesSince { get; set; }
    /// <summary>Mac: "Hold vågen på strøm (også med lukket låg)". Kan ændres i Fleetdeck (administrator) og på Mac'en; sidste ændring vinder.</summary>
    public bool KeepAwakeOnAc { get; set; }
    /// <summary>Hvornår den sidst blev ændret (serverens ur), hvorfra (web | mac) og af hvem (brugernavn i Fleetdeck).</summary>
    public DateTime? KeepAwakeAt { get; set; }
    [MaxLength(8)] public string? KeepAwakeSource { get; set; }
    [MaxLength(64)] public string? KeepAwakeBy { get; set; }
    // ---- strøm som sidste udvej, når en genstart hænger: en kontakt med strømmåler i Home Assistant (valgt af brugeren)
    [MaxLength(120)] public string? PowerSwitch { get; set; }
    [MaxLength(120)] public string? PowerSensor { get; set; }
    /// <summary>Over så mange watt trækker maskinen strøm (er tændt eller hænger).</summary>
    public int PowerMinWatts { get; set; } = 15;
}

/// <summary>Et SD-kort-billede til en Raspberry Pi: til en ny Pi ("ny") eller til at erstatte kortet i en eksisterende ("erstat").
/// Selve billedet ligger i data/pi-cards/&lt;id&gt;/ og slettes efter 24 timer. Ingen hemmeligheder: kun id'et på engangskoden.</summary>
public class PiCard
{
    public int Id { get; set; }
    public int? MachineId { get; set; }
    [MaxLength(63)] public string Name { get; set; } = "";
    [MaxLength(10)] public string Mode { get; set; } = "ny";            // ny | erstat
    [MaxLength(80)] public string? KitName { get; set; }
    [MaxLength(40)] public string ImageId { get; set; } = "";
    public int? EnrollCodeId { get; set; }
    public int? ProfileId { get; set; }
    [MaxLength(16)] public string Status { get; set; } = "bygger";      // bygger | klar | fejl | slettet
    public int Progress { get; set; }
    [MaxLength(400)] public string Message { get; set; } = "";
    [MaxLength(120)] public string? File { get; set; }
    [MaxLength(64)] public string? Sha256 { get; set; }
    public long Size { get; set; }
    /// <summary>Det, kortet er bygget med (IP, bruger, udeladte stier, ekstra boot-linjer) – til visning og selve gendannelsen.</summary>
    public string SettingsJson { get; set; } = "{}";
    /// <summary>Tidslinjen fra kortet selv: startet, tilmeldt, fejl (JSON-liste).</summary>
    public string EventsJson { get; set; } = "[]";
    public DateTime Created { get; set; } = DateTime.UtcNow;
    [MaxLength(64)] public string CreatedBy { get; set; } = "";
    public DateTime Expires { get; set; }
    public DateTime? Downloaded { get; set; }
}

/// <summary>
/// API-nøgle til et program (fx Kontrolrums status-kort eller Home Assistant), Indstillinger → Adgang for programmer.
/// Kun SHA-256 af nøglen og de sidste 4 tegn gemmes; selve nøglen vises én gang, når den laves. Agenternes maskine-tokens er noget andet.
/// </summary>
public class ApiKey
{
    public int Id { get; set; }
    /// <summary>Programmets navn. Står i listen og i loggen i stedet for nøglen.</summary>
    [MaxLength(64)] public string Name { get; set; } = "";
    /// <summary>read | control | admin (ApiScopes).</summary>
    [MaxLength(16)] public string Scope { get; set; } = Fleetdeck.Shared.ApiScopes.Read;
    /// <summary>hex(SHA-256(nøgle)) – opslag sker på den.</summary>
    [MaxLength(64)] public string Sha256 { get; set; } = "";
    /// <summary>Nøglens sidste 4 tegn, så den kan genkendes.</summary>
    [MaxLength(8)] public string Hint { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.UtcNow;
    [MaxLength(64)] public string CreatedBy { get; set; } = "";
    public DateTime? LastUsed { get; set; }
    [MaxLength(64)] public string? LastIp { get; set; }
    public long Uses { get; set; }
    /// <summary>Kun fra hjemmenettet/VPN – aldrig gennem Cloudflare-tunnelen eller fra en offentlig adresse.</summary>
    public bool LanOnly { get; set; } = true;
    public DateTime? Expires { get; set; }
    /// <summary>Slettet (virker ikke længere og vises ikke i listen).</summary>
    public DateTime? Revoked { get; set; }
    /// <summary>Flyttet fra den gamle enkelt-nøgle (kontrolrum_key) ved opdateringen.</summary>
    public bool Migrated { get; set; }
}

/// <summary>Engangskode til tilmelding af én maskine (i stedet for den globale nøgle). Kun SHA-256 af koden gemmes.</summary>
public class EnrollCode
{
    public int Id { get; set; }
    [MaxLength(64)] public string CodeHash { get; set; } = "";
    /// <summary>Første tegn af koden, så den kan genkendes i listen.</summary>
    [MaxLength(8)] public string Hint { get; set; } = "";
    [MaxLength(100)] public string Label { get; set; } = "";
    /// <summary>Koden må bruges til at geninstallere netop denne maskine (overtager dens identitet uden ekstra godkendelse).</summary>
    public int? ReplaceMachineId { get; set; }
    public int? ProfileId { get; set; }
    public DateTime Created { get; set; } = DateTime.UtcNow;
    [MaxLength(64)] public string CreatedBy { get; set; } = "";
    public DateTime Expires { get; set; }
    public DateTime? UsedAt { get; set; }
    public int? UsedByMachineId { get; set; }
    /// <summary>Udstedt til en "Anmod om adgang": koden virker kun sammen med anmodningens hemmelighed.</summary>
    public int? AccessRequestId { get; set; }
    /// <summary>Navnet, administratoren godkendte maskinen under (bruges i stedet for agentens eget).</summary>
    [MaxLength(31)] public string? Name { get; set; }
}

/// <summary>
/// "Anmod om adgang": en maskine uden tilmeldingsnøgle beder om lov. Kun SHA-256 af hemmeligheden gemmes; koden er den, brugeren
/// og administratoren sammenligner. Godkendelsen giver en engangskode, der kun virker sammen med hemmeligheden.
/// </summary>
public class AccessRequest
{
    public int Id { get; set; }
    [MaxLength(64)] public string SecretHash { get; set; } = "";
    [MaxLength(7)] public string Code { get; set; } = "";
    [MaxLength(31)] public string Name { get; set; } = "";
    [MaxLength(16)] public string Platform { get; set; } = "";
    public string? FactsJson { get; set; }
    [MaxLength(120)] public string? Os { get; set; }
    [MaxLength(120)] public string? Model { get; set; }
    [MaxLength(64)] public string? RequestedBy { get; set; }
    [MaxLength(200)] public string? Message { get; set; }
    [MaxLength(45)] public string Ip { get; set; } = "";
    /// <summary>Fingeraftrykket, klienten sagde, den så. Bruges kun, når serveren ikke kender sit eget.</summary>
    [MaxLength(64)] public string? ObservedPin { get; set; }
    /// <summary>Koden er regnet med serverens eget fingeraftryk (en mellemmand ville give en anden kode hos brugeren).</summary>
    public bool PinVerified { get; set; }
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public DateTime Expires { get; set; }
    public DateTime? LastPoll { get; set; }
    [MaxLength(16)] public string State { get; set; } = "pending";
    public int? ProfileId { get; set; }
    [MaxLength(31)] public string? ApprovedName { get; set; }
    [MaxLength(64)] public string? DecidedBy { get; set; }
    public DateTime? DecidedAt { get; set; }
    [MaxLength(200)] public string? RejectReason { get; set; }
    public int? EnrollCodeId { get; set; }
    public int? MachineId { get; set; }
    /// <summary>Agentens offentlige nøgle (P-256, base64): engangskoden krypteres til den.</summary>
    [MaxLength(200)] public string? PublicKey { get; set; }
    /// <summary>Den udstedte engangskode, krypteret til <see cref="PublicKey"/> (kan kun åbnes af agenten). Gives igen, til den er brugt.</summary>
    [MaxLength(400)] public string? SealedKey { get; set; }
}

/// <summary>
/// "Anmod om adgang": en blokering fra administratoren (IP-adresse, MAC-adresse eller maskinnavn) med udløb. Så kan en enhed på
/// netværket ikke blive ved med at optage de åbne pladser. IPv6 blokeres pr. /64 (som hastighedsgrænsen).
/// </summary>
public class AccessBlock
{
    public int Id { get; set; }
    /// <summary>ip | mac | name (se AccessBlockKinds).</summary>
    [MaxLength(8)] public string Kind { get; set; } = "";
    /// <summary>Normaliseret: IP som AccessRequestService.RateKey, MAC som 12 hex-tegn med store bogstaver, navn som CleanName.</summary>
    [MaxLength(64)] public string Value { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public DateTime Until { get; set; }
    [MaxLength(64)] public string CreatedBy { get; set; } = "";
    [MaxLength(200)] public string? Reason { get; set; }
    /// <summary>Anmodningen, blokeringen blev lavet fra (kun til visning).</summary>
    public int? AccessRequestId { get; set; }
    /// <summary>Antal afviste forsøg, siden blokeringen blev lavet, og det seneste.</summary>
    public int Hits { get; set; }
    public DateTime? LastHit { get; set; }
}

/// <summary>Netværksdrev for en profil (alle dens maskiner) eller én maskine. Maskinens eget drev vinder over profilens for samme mappe.</summary>
/// <summary>Debloat og privatliv på én Windows-maskine: valg, håndhævelse, seneste tilstand og før-værdier til fortryd.</summary>
public class MachineDebloat
{
    [Key] public int MachineId { get; set; }
    public Machine? Machine { get; set; }
    /// <summary>"" (intet valgt) | let | anbefalet | maks – se DebloatProfiles.</summary>
    [MaxLength(16)] public string Profile { get; set; } = "";
    /// <summary>Den version af profilen, maskinen har sagt ja til. Nye punkter i profilen kommer først med, når nogen vælger det.</summary>
    public int ProfileVersion { get; set; } = Services.DebloatProfiles.Current;
    /// <summary>Punkter valgt til ud over profilen.</summary>
    public List<string> Include { get; set; } = [];
    /// <summary>Punkter fra profilen, der er valgt fra på netop denne maskine.</summary>
    public List<string> Exclude { get; set; } = [];
    /// <summary>Hold håndhævet: tjek dagligt og efter nye Windows-builds.</summary>
    public bool Enforce { get; set; }
    /// <summary>alert (giv besked) | reapply (anvend igen, højst én gang i døgnet).</summary>
    [MaxLength(16)] public string EnforceMode { get; set; } = "alert";
    /// <summary>Punkter, Fleetdeck har anvendt (og som kan fortrydes/holdes øje med).</summary>
    public List<string> Applied { get; set; } = [];
    /// <summary>Anvendte punkter, der er slået til igen siden.</summary>
    public List<string> Drift { get; set; } = [];
    /// <summary>Punkt-id → DebloatItemState.</summary>
    public string StateJson { get; set; } = "{}";
    /// <summary>Punkt-id → DebloatSnapshot (værdierne, før Fleetdeck ændrede dem).</summary>
    public string BeforeJson { get; set; } = "{}";
    /// <summary>DebloatPowerInfo: den aktive strømplan og planerne på pc'en.</summary>
    public string PowerJson { get; set; } = "{}";
    public int? Build { get; set; }
    [MaxLength(64)] public string? Edition { get; set; }
    public DateTime? LastScan { get; set; }
    public DateTime? LastApplied { get; set; }
    public DateTime? LastReapply { get; set; }
    public int? LastJobId { get; set; }
    [MaxLength(300)] public string? RestorePoint { get; set; }
}

public class NetworkDrive
{
    public int Id { get; set; }
    public int? ProfileId { get; set; }
    public int? MachineId { get; set; }
    public string Host { get; set; } = "";
    public string Share { get; set; } = "";
    public string? Letter { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Fundet på maskinen og gemt automatisk (brugeren koblede det selv på) – bruger maskinens eget login.</summary>
    public bool Adopted { get; set; }
    /// <summary>Skrevet ind manuelt fra en anden server: brug maskinens eget gemte login, ikke Unraid-brugeren.</summary>
    public bool OwnLogin { get; set; }
    /// <summary>Fundet drev, som man har valgt ikke at holde øje med: samles ikke op igen.</summary>
    public bool Ignored { get; set; }
    public DateTime Created { get; set; }
}

/// <summary>En printer i Fleetdeck (siden Printere). Lægges på profiler eller maskiner med PrinterAssignment.</summary>
public class Printer
{
    public int Id { get; set; }
    [MaxLength(100)] public string Name { get; set; } = "";
    /// <summary>ipp://…/ipp/print (uden driver) eller socket://, lpd:// (med driver).</summary>
    [MaxLength(400)] public string Uri { get; set; } = "";
    [MaxLength(100)] public string Location { get; set; } = "";
    [MaxLength(200)] public string MakeModel { get; set; } = "";
    [MaxLength(16)] public string Paper { get; set; } = "A4";
    [MaxLength(8)] public string Duplex { get; set; } = "long";     // none | long | short
    public bool Color { get; set; } = true;
    /// <summary>Kræver driver: virker ikke uden (ikke IPP Everywhere).</summary>
    public bool NeedsDriver { get; set; }
    /// <summary>Driverens navn (Windows) eller CUPS-model/PPD-sti.</summary>
    [MaxLength(200)] public string Driver { get; set; } = "";
    /// <summary>Pakken med driveren (lægges på de maskiner, der har printeren).</summary>
    public int? DriverPackageId { get; set; }
    [MaxLength(16)] public string Source { get; set; } = "manual";   // manual | bonjour | scan | agent
    /// <summary>Seneste svar fra printeren (IPP): blæk/toner, tilstand og fejl.</summary>
    public string? StatusJson { get; set; }
    public DateTime? LastPolled { get; set; }
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public List<PrinterAssignment> Assignments { get; set; } = [];
}

/// <summary>Printer på en profil (alle dens maskiner) eller én maskine.</summary>
public class PrinterAssignment
{
    public int Id { get; set; }
    public int PrinterId { get; set; }
    public Printer? Printer { get; set; }
    public int? ProfileId { get; set; }
    public int? MachineId { get; set; }
    /// <summary>Gør til standardprinter, hvis maskinen ikke har en.</summary>
    public bool MakeDefault { get; set; }
    /// <summary>Hold på: læg den på igen, hvis nogen fjerner den.</summary>
    public bool Keep { get; set; } = true;
    public DateTime Created { get; set; } = DateTime.UtcNow;
}

public class SoftwareItem
{
    public long Id { get; set; }
    public int MachineId { get; set; }
    public string Name { get; set; } = "";
    public string? Version { get; set; }
    public string? Publisher { get; set; }
    public string? Scope { get; set; }
    public string? Description { get; set; }
    public string? InstallPath { get; set; }
    public string? Exe { get; set; }
    public string? IconHash { get; set; }
    public string? UninstallId { get; set; }
}

/// <summary>Programikon (PNG), delt mellem maskiner: samme ikon gemmes kun én gang.</summary>
public class SoftwareIcon
{
    public string Hash { get; set; } = "";
    public byte[] Png { get; set; } = [];
    public DateTime Created { get; set; }
}

/// <summary>
/// Seneste lykkede installation/afinstallation af en pakke på en maskine. Slettes ikke af "Ryd op" (jobs gør), så
/// overholdelsen stadig ved, at programmet er installeret/afinstalleret af Fleetdeck, og ikke selv sætter et job i gang igen.
/// </summary>
public class PackageOutcome
{
    public long Id { get; set; }
    public int MachineId { get; set; }
    public int PackageId { get; set; }
    [MaxLength(32)] public string Type { get; set; } = "";   // install | uninstall
    public int JobId { get; set; }
    public DateTime Finished { get; set; }
}

public class MachineWingetId
{
    public long Id { get; set; }
    public int MachineId { get; set; }
    public string Value { get; set; } = "";
}

public class UpgradeItem
{
    public long Id { get; set; }
    public int MachineId { get; set; }
    public string Name { get; set; } = "";
    public string WingetId { get; set; } = "";
    public string? Current { get; set; }
    public string? Available { get; set; }
    /// <summary>Første gang denne opdatering (samme version) blev set – til "Venter i"-kolonnen i rapporten Opdateringer.</summary>
    public DateTime? FirstSeen { get; set; }
    /// <summary>En driver fra Windows Update ("Opdatér alt" installerer dem ikke).</summary>
    public bool Driver { get; set; }
    /// <summary>Hvorfor den ikke anbefales (fra agenten), ellers null.</summary>
    [MaxLength(200)] public string? Advice { get; set; }
    /// <summary>Skjult – i Windows Update (fra agenten) eller i Fleetdeck (UpgradeMark). Et globalt filter i AppDb holder skjulte og
    /// dem, der ikke kan installeres, ude af alle lister og tal – brug IgnoreQueryFilters() for at se dem.</summary>
    public bool Hidden { get; set; }
    /// <summary>Kan ikke installeres på maskinen (fx firmware, som maskinens firmware afviser) – forklaringen, ellers null. Fra UpgradeMark.</summary>
    [MaxLength(300)] public string? Blocked { get; set; }
}

/// <summary>
/// Serverens egen hukommelse om en opdatering på én maskine, så den overlever et nyt inventar: skjult i Fleetdeck (alt andet end
/// Windows Update, som skjules på pc'en) og/eller "kan ikke installeres" (agenten meldte en fejl, der ikke hjælper at prøve igen).
/// Gælder den version, der var tilgængelig (Available); kommer der en ny version, slettes mærket.
/// </summary>
public class UpgradeMark
{
    public long Id { get; set; }
    public int MachineId { get; set; }
    [MaxLength(400)] public string WingetId { get; set; } = "";
    [MaxLength(200)] public string Name { get; set; } = "";
    /// <summary>Versionen, mærket gælder (null = alle).</summary>
    [MaxLength(100)] public string? Available { get; set; }
    public bool Hidden { get; set; }
    /// <summary>Hvorfor den ikke kan installeres (vises til brugeren), ellers null.</summary>
    [MaxLength(300)] public string? Blocked { get; set; }
    public DateTime Created { get; set; } = DateTime.UtcNow;
    [MaxLength(100)] public string By { get; set; } = "";
}

public class Job
{
    public int Id { get; set; }
    public int MachineId { get; set; }
    public Machine? Machine { get; set; }
    [MaxLength(32)] public string Type { get; set; } = "";
    public string Title { get; set; } = "";
    public string ParamsJson { get; set; } = "{}";
    [MaxLength(16)] public string RunAs { get; set; } = "system";
    public int? PackageId { get; set; }
    [MaxLength(16)] public string Status { get; set; } = "pending";
    [MaxLength(16)] public string Source { get; set; } = "manual";   // manual | compliance | schedule | provision
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = "";
    public DateTime? Started { get; set; }
    public DateTime? Finished { get; set; }
    public int? ExitCode { get; set; }
    public string Output { get; set; } = "";
    public int? Progress { get; set; }                    // 0-100 mens jobbet kører (null = ukendt)
    [MaxLength(300)] public string Step { get; set; } = "";   // hvad agenten laver lige nu
    public DateTime? ProgressAt { get; set; }
    public DateTime? StepSince { get; set; }   // hvornår trinnet sidst ændrede sig (agentens ur, UTC)
    // ---- genstart/sluk: vagten (Services/PowerWatch.cs) – jobbet lykkes først, når maskinen er tilbage (eller slukket)
    /// <summary>Det planlagte tidspunkt for genstarten/slukningen (UTC), som agenten meldte.</summary>
    public DateTime? PowerAt { get; set; }
    /// <summary>Maskinens opstartstid, før genstarten blev sendt (UTC).</summary>
    public DateTime? BootBefore { get; set; }
    public bool BootBeforePrecise { get; set; }
    /// <summary>Wake-on-LAN-test: hvornår magic packet blev sendt.</summary>
    public DateTime? WakeSentAt { get; set; }
    /// <summary>Hvornår jobbet blev markeret som "hænger" (beskeden sendes én gang).</summary>
    public DateTime? HungAt { get; set; }
    /// <summary>update_round: rundens strukturerede resultat (UpdateReport) som JSON.</summary>
    public string? ResultJson { get; set; }
}

public class Script
{
    public int Id { get; set; }
    [MaxLength(200)] public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    [MaxLength(16)] public string RunAs { get; set; } = "system";
    public string Body { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.UtcNow;
}

public class Alert
{
    public int Id { get; set; }
    public int MachineId { get; set; }
    public Machine? Machine { get; set; }
    [MaxLength(64)] public string Key { get; set; } = "";
    [MaxLength(16)] public string Severity { get; set; } = "warning";   // critical | warning | info
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public DateTime FirstSeen { get; set; } = DateTime.UtcNow;
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    public DateTime? Resolved { get; set; }
    public DateTime? Acknowledged { get; set; }
    public string? AckBy { get; set; }
    // Hjælperen: seneste rettelse for denne alarm (bruges til pause og loft over forsøg)
    public int? FixJobId { get; set; }
    public DateTime? LastFixAt { get; set; }
    public int FixAttempts { get; set; }
}

/// <summary>
/// En fejl eller advarsel fra maskinens systemlog (Windows' hændelseslog, journald, macOS). Gemmes i 30 dage og højst
/// <see cref="Fleetdeck.Shared.SysLogPolicy.MaxPerMachine"/> pr. maskine. Kind er den kendte type (<see cref="Fleetdeck.Shared.SysLogKinds"/>).
/// </summary>
public class SysLogRow
{
    public long Id { get; set; }
    public int MachineId { get; set; }
    public DateTime Time { get; set; }
    [MaxLength(10)] public string Level { get; set; } = "";
    [MaxLength(60)] public string Log { get; set; } = "";
    [MaxLength(100)] public string Source { get; set; } = "";
    public int? EventId { get; set; }
    [MaxLength(700)] public string Message { get; set; } = "";
    [MaxLength(32)] public string? Kind { get; set; }
    /// <summary>Ens hændelser samles under denne nøgle, og den kan skjules (<see cref="Fleetdeck.Shared.SysLogPolicy.GroupKey"/>).</summary>
    [MaxLength(200)] public string GroupKey { get; set; } = "";
    [MaxLength(32)] public string Hash { get; set; } = "";
    public DateTime Received { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Ændringsloggen: et program eller en driver, der er installeret, fjernet eller opdateret mellem to inventarer
/// (<see cref="Fleetdeck.Server.Services.ChangeLogStore"/>). Gemmes i 180 dage og højst 3000 pr. maskine.
/// </summary>
public class ChangeLogRow
{
    public long Id { get; set; }
    public int MachineId { get; set; }
    public DateTime Time { get; set; }
    /// <summary>program eller driver.</summary>
    [MaxLength(10)] public string Kind { get; set; } = "";
    /// <summary>installed, removed eller updated.</summary>
    [MaxLength(10)] public string Action { get; set; } = "";
    [MaxLength(200)] public string Name { get; set; } = "";
    [MaxLength(100)] public string? From { get; set; }
    [MaxLength(100)] public string? To { get; set; }
    [MaxLength(100)] public string? Publisher { get; set; }
}

/// <summary>En installeret driver fra seneste inventar (kun Windows) – grundlaget for ændringsloggen for drivere.</summary>
public class DriverItem
{
    public long Id { get; set; }
    public int MachineId { get; set; }
    [MaxLength(200)] public string Name { get; set; } = "";
    [MaxLength(60)] public string? Version { get; set; }
    [MaxLength(100)] public string? Provider { get; set; }
    [MaxLength(40)] public string? Class { get; set; }
    [MaxLength(10)] public string? Date { get; set; }
}

/// <summary>
/// Tidslinjen pr. maskine: perioder uden kontakt (offline, Start–End) og opstarter (boot, kun Start). Gemmes i 35 dage.
/// Opdateringer, alvorlige hændelser og ændringer hentes fra deres egne tabeller.
/// </summary>
public class TimelineMark
{
    public long Id { get; set; }
    public int MachineId { get; set; }
    /// <summary>offline eller boot (<see cref="Fleetdeck.Server.Services.TimelineKinds"/>).</summary>
    [MaxLength(10)] public string Kind { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime? End { get; set; }
}

public class Metric
{
    public long Id { get; set; }
    public int MachineId { get; set; }
    public DateTime Ts { get; set; } = DateTime.UtcNow;
    public double? Cpu { get; set; }
    public double? Ram { get; set; }
    public double? Disk { get; set; }
    public double? CpuTemp { get; set; }
    public double? GpuTemp { get; set; }
}

/// <summary>Familien i <see cref="Deployment.Os"/>. Alt andet end Windows installeres fra GRUB (Linux).</summary>
public static class DeployOs
{
    public const string Windows = "windows", Ubuntu = "ubuntu", Debian = "debian", Fedora = "fedora", Rocky = "rocky", Alma = "alma", Proxmox = "proxmox";
}
public static class DeployMode
{
    public const string Clean = "clean", Managed = "managed", Profile = "profile";
    public static readonly string[] All = [Clean, Managed, Profile];
}
public static class DeployStatus
{
    public const string Ready = "ready", Installing = "installing", Done = "done", Cancelled = "cancelled", Expired = "expired";
    public static readonly string[] Active = [Ready, Installing];
}

/// <summary>En forberedt udrulning: næste gang pc'en med denne MAC booter fra netværket, installeres dette.</summary>
public class Deployment
{
    public int Id { get; set; }
    [MaxLength(17)] public string Mac { get; set; } = "";          // AA-BB-CC-DD-EE-FF
    [MaxLength(16)] public string Os { get; set; } = DeployOs.Windows;
    [MaxLength(16)] public string Series { get; set; } = "";       // Ubuntu: 24.04 / 26.04
    [MaxLength(16)] public string Variant { get; set; } = "";      // Ubuntu: desktop / server
    [MaxLength(16)] public string Mode { get; set; } = DeployMode.Profile;
    public int? ProfileId { get; set; }
    [MaxLength(63)] public string PcName { get; set; } = "";
    [MaxLength(64)] public string UserName { get; set; } = "";
    [MaxLength(100)] public string FullName { get; set; } = "";
    public string PasswordHash { get; set; } = "";                  // Ubuntu: sha512-crypt
    public string Packages { get; set; } = "";                      // Ubuntu: ekstra apt-pakker
    public bool AutoDisk { get; set; }                               // hele den første disk bruges uden at spørge
    public int? MachineId { get; set; }                              // eksisterende maskine (geninstallation)
    /// <summary>Id fra OsCatalog (fx "debian-13"). Tom på ældre udrulninger: så gælder Os + Series.</summary>
    [MaxLength(32)] public string OsId { get; set; } = "";
    public string SshKey { get; set; } = "";                         // Linux: offentlig SSH-nøgle til brugeren
    public bool NoAdmin { get; set; }                                // Linux: brugeren får ikke sudo
    [MaxLength(16)] public string Language { get; set; } = "";       // tom = Indstillingernes sprog
    [MaxLength(16)] public string Keyboard { get; set; } = "";       // tom = Indstillingernes tastatur
    [MaxLength(64)] public string TimeZone { get; set; } = "";       // tom = Indstillingernes tidszone (IANA)
    public bool Encrypt { get; set; }                                // BitLocker/LUKS – nøglen skrives på maskinen
    /// <summary>Felter, der stod tomme i guiden og derfor spørges om på maskinen (DeployField: name, user, password, language …).</summary>
    public List<string> AskFields { get; set; } = [];
    [MaxLength(16)] public string Status { get; set; } = DeployStatus.Ready;
    public DateTime? Started { get; set; }
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = "";
    public DateTime? Finished { get; set; }
    public string Notes { get; set; } = "";
}

public class AuditEntry
{
    public long Id { get; set; }
    public DateTime Ts { get; set; } = DateTime.UtcNow;
    public string User { get; set; } = "";
    public string Action { get; set; } = "";
    public string Detail { get; set; } = "";
    /// <summary>Maskinen, handlingen gjaldt (til filteret i Revisionslog). Ældre rækker har kun navnet i Detail.</summary>
    public int? MachineId { get; set; }
}

/// <summary>
/// Gendannelsesnøgle til diskkryptering (BitLocker-adgangskode eller FileVault-nøgle), krypteret med data protection.
/// Gamle nøgler bevares (Superseded), så en disk, der ligger i en skuffe, stadig kan låses op.
/// </summary>
public class RecoveryKey
{
    public int Id { get; set; }
    public int MachineId { get; set; }
    public Machine? Machine { get; set; }
    /// <summary>bitlocker | filevault</summary>
    [MaxLength(16)] public string Kind { get; set; } = "";
    /// <summary>Drevet, fx "C:" eller "/".</summary>
    [MaxLength(64)] public string Volume { get; set; } = "";
    /// <summary>BitLocker: nøglebeskytterens ID. FileVault: "personal".</summary>
    [MaxLength(128)] public string ProtectorId { get; set; } = "";
    /// <summary>Selve nøglen, beskyttet med data protection ("Fleetdeck.RecoveryKey"). Må aldrig sendes ud uden "Vis nøgle".</summary>
    public string Protected { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.UtcNow;
    /// <summary>Senest agenten meldte, at nøglen stadig findes på maskinen.</summary>
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    /// <summary>Udskiftet af en nyere nøgle (eller fjernet fra maskinen). Null = gældende.</summary>
    public DateTime? Superseded { get; set; }
    /// <summary>agent (læst af agenten) | rotate (lavet af jobbet "Ny gendannelsesnøgle").</summary>
    [MaxLength(16)] public string Source { get; set; } = "agent";
    public int? JobId { get; set; }
}

/// <summary>Et overvågningstjek (à la Uptime Kuma): en hjemmeside, port, adresse, container eller et push-signal.</summary>
public class ServiceCheck
{
    public int Id { get; set; }
    [MaxLength(120)] public string Name { get; set; } = "";
    [MaxLength(16)] public string Type { get; set; } = CheckTypes.Http;
    [MaxLength(500)] public string Target { get; set; } = "";      // url | vært:port | vært | dns-navn | containernavn
    public int? MachineId { get; set; }                               // container: Unraid-maskinen
    public int? RunOnMachineId { get; set; }                          // køres af denne maskines agent i stedet for serveren
    [MaxLength(60)] public string? Group { get; set; }
    // HTTP
    [MaxLength(200)] public string? Keyword { get; set; }             // skal stå på siden (eller ikke, hvis KeywordInvert)
    public bool KeywordInvert { get; set; }
    [MaxLength(40)] public string? ExpectStatus { get; set; }         // fx "200-399" (standard) eller "200,204"
    public bool IgnoreTls { get; set; }
    public int CertWarnDays { get; set; } = 14;
    // DNS
    [MaxLength(200)] public string? Expect { get; set; }              // DNS: svaret skal indeholde dette
    // push
    [MaxLength(64)] public string? PushToken { get; set; }
    // tidsplan
    public int IntervalSec { get; set; } = 60;
    public int TimeoutSec { get; set; } = 10;
    public int FailThreshold { get; set; } = 2;                       // fejl i træk, før den regnes som nede
    public bool Enabled { get; set; } = true;
    public bool Notify { get; set; } = true;
    public DateTime Created { get; set; } = DateTime.UtcNow;
    // tilstand
    [MaxLength(8)] public string State { get; set; } = CheckStates.Pending;
    public int FailCount { get; set; }
    public DateTime? LastCheck { get; set; }
    public DateTime? LastChange { get; set; }
    public int? LastMs { get; set; }
    [MaxLength(300)] public string? LastMessage { get; set; }
    public DateTime? CertExpires { get; set; }
    public bool CertWarned { get; set; }
    public DateTime? LastPush { get; set; }
}

/// <summary>Ét tjek-resultat (pulsslag). Gemmes i 30 dage.</summary>
public class CheckBeat
{
    public long Id { get; set; }
    public int CheckId { get; set; }
    public DateTime Ts { get; set; } = DateTime.UtcNow;
    public bool Up { get; set; }
    public int? Ms { get; set; }
    [MaxLength(300)] public string? Message { get; set; }
}

public static class CheckTypes
{
    public const string Http = "http", Tcp = "tcp", Ping = "ping", Dns = "dns", Container = "container", Push = "push";
    public static readonly string[] All = [Http, Tcp, Ping, Dns, Container, Push];
}

public static class CheckStates
{
    public const string Up = "up", Down = "down", Pending = "pending", Paused = "paused";
}

/// <summary>Apple-styring (MDM): hvor langt en iPad er i tilmeldingen.</summary>
public static class MdmStates
{
    /// <summary>Tilmeldingslink/profil er lavet, men iPad'en har ikke meldt sig endnu.</summary>
    public const string Pending = "pending";
    /// <summary>iPad'en har installeret profilen (Authenticate), men har ikke sendt push-token endnu.</summary>
    public const string Authenticated = "authenticated";
    public const string Enrolled = "enrolled";
    /// <summary>MDM-profilen er fjernet på iPad'en.</summary>
    public const string CheckedOut = "checked_out";
}

/// <summary>
/// En iPad under Apple-styring (MDM via NanoHUB). Valgfri pr. iPad: uden en række her styres iPad'en kun via AdGuard (og evt. statisk tilstand).
/// Fleetdeck er kilden til sandheden; NanoHUB er kun protokol, kø og push.
/// </summary>
public class MdmDevice
{
    [Key] public int IpadId { get; set; }
    /// <summary>Tilfældig nøgle i profilens ServerURL (?fd=…), så NanoHUBs webhooks kan kobles til den rigtige iPad.</summary>
    [MaxLength(64)] public string EnrollKey { get; set; } = "";
    [MaxLength(16)] public string State { get; set; } = MdmStates.Pending;
    [MaxLength(64)] public string? Udid { get; set; }
    [MaxLength(64)] public string? EnrollmentId { get; set; }
    /// <summary>Forventet serienummer (valgfrit): melder en anden enhed sig med profilen, skal en administrator godkende den.</summary>
    [MaxLength(40)] public string? ExpectedSerial { get; set; }
    public bool Approved { get; set; } = true;
    [MaxLength(200)] public string? ApprovalNote { get; set; }
    [MaxLength(40)] public string? Serial { get; set; }
    [MaxLength(80)] public string? DeviceName { get; set; }
    [MaxLength(80)] public string? ProductName { get; set; }
    [MaxLength(80)] public string? ModelName { get; set; }
    [MaxLength(40)] public string? OsVersion { get; set; }
    [MaxLength(40)] public string? BuildVersion { get; set; }
    public bool? Supervised { get; set; }
    public bool? ActivationLock { get; set; }
    /// <summary>Batteri 0–1.</summary>
    public double? BatteryLevel { get; set; }
    public double? StorageTotalGb { get; set; }
    public double? StorageFreeGb { get; set; }
    public DateTime? LastCheckin { get; set; }
    public DateTime? LastInventory { get; set; }
    public DateTime? EnrolledAt { get; set; }
    public DateTime? CheckedOutAt { get; set; }
    public DateTime Created { get; set; } = DateTime.UtcNow;
    /// <summary>Serienummer og udløb på det seneste identitetscertifikat, Fleetdeck har udstedt til iPad'en.</summary>
    [MaxLength(64)] public string? IdentitySerial { get; set; }
    public DateTime? IdentityExpires { get; set; }
    /// <summary>Den låste profil (samme indhold som i statisk tilstand) leveres via MDM.</summary>
    public bool LockedViaMdm { get; set; }
    /// <summary>Fingeraftryk af den låste profil, iPad'en sidst bekræftede via MDM (null = ikke installeret via MDM).</summary>
    [MaxLength(64)] public string? LockedInstalledHash { get; set; }
    public int? LockedInstalledVersion { get; set; }
    /// <summary>OS-opdatering via DDM (softwareupdate.enforcement.specific): målversion og frist i iPad'ens lokale tid (yyyy-MM-ddTHH:mm:ss).</summary>
    [MaxLength(40)] public string? OsTargetVersion { get; set; }
    [MaxLength(40)] public string? OsTargetBuild { get; set; }
    [MaxLength(20)] public string? OsTargetLocal { get; set; }
    public string? PushError { get; set; }
    public bool NotSeenWarned { get; set; }
}

/// <summary>Engangslink til tilmeldingsprofilen. Kun tokenets SHA-256 gemmes; profilen (med iPad'ens identitet) ligger krypteret.</summary>
public class MdmEnrollToken
{
    public int Id { get; set; }
    [MaxLength(64)] public string TokenHash { get; set; } = "";
    public int IpadId { get; set; }
    public DateTime Expires { get; set; }
    /// <summary>Brugt: iPad'en har meldt sig (Authenticate) – linket virker ikke længere.</summary>
    public DateTime? UsedAt { get; set; }
    /// <summary>Antal hentninger af selve profilen (forhåndsvisninger af linket tæller ikke).</summary>
    public int Fetches { get; set; }
    public string ProfileEnc { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.UtcNow;
}

public static class MdmCommandStates
{
    public const string Queued = "queued", Sent = "sent", Acknowledged = "acknowledged", Error = "error", NotNow = "notnow", Unknown = "unknown", Declared = "declared", Failed = "failed";
    public static readonly string[] Open = [Queued, Sent, NotNow];
}

/// <summary>En MDM-kommando (eller DDM-ændring) sendt fra Fleetdeck – historikken pr. iPad.</summary>
public class MdmCommand
{
    [Key, MaxLength(40)] public string CommandUuid { get; set; } = "";
    public int IpadId { get; set; }
    [MaxLength(48)] public string RequestType { get; set; } = "";
    [MaxLength(16)] public string Status { get; set; } = MdmCommandStates.Queued;
    [MaxLength(80)] public string RequestedBy { get; set; } = "";
    /// <summary>Kort beskrivelse til historikken (fx beskeden på låseskærmen).</summary>
    [MaxLength(300)] public string? Detail { get; set; }
    [MaxLength(64)] public string? PayloadHash { get; set; }
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public DateTime? Updated { get; set; }
    public DateTime? Completed { get; set; }
    public string? Error { get; set; }
    public string? ResultJson { get; set; }
}

/// <summary>En app, der sidst blev set installeret på en iPad (via Mac'en med cfgutil eller via Apple-styring).</summary>
public class IpadApp
{
    public int Id { get; set; }
    public int IpadId { get; set; }
    [MaxLength(200)] public string BundleId { get; set; } = "";
    [MaxLength(120)] public string? Name { get; set; }
    [MaxLength(64)] public string? Version { get; set; }
    [MaxLength(64)] public string? Build { get; set; }
    /// <summary>App Store har en nyere version (regnes ud, når appen scannes eller App Store-oplysningerne hentes).</summary>
    public bool Outdated { get; set; }
    public DateTime Seen { get; set; } = DateTime.UtcNow;
}

/// <summary>Seneste app-scanning af en iPad.</summary>
public class IpadAppScan
{
    [Key] public int IpadId { get; set; }
    public DateTime? ScannedAt { get; set; }
    /// <summary>"mac" (cfgutil via kabel) eller "mdm" (InstalledApplicationList).</summary>
    [MaxLength(8)] public string? Source { get; set; }
    /// <summary>Seneste anmodning om en scanning (job eller MDM-kommando).</summary>
    public DateTime? Requested { get; set; }
    [MaxLength(8)] public string? RequestedVia { get; set; }
    [MaxLength(400)] public string? Error { get; set; }
}

/// <summary>Cache af App Stores oplysninger om en app (iTunes Lookup, dansk butik). Hentes højst én gang i døgnet pr. app.</summary>
public class AppStoreApp
{
    [Key, MaxLength(200)] public string BundleId { get; set; } = "";
    /// <summary>Appen findes i den danske App Store.</summary>
    public bool Found { get; set; }
    public long? TrackId { get; set; }
    [MaxLength(200)] public string? Name { get; set; }
    [MaxLength(64)] public string? Version { get; set; }
    [MaxLength(500)] public string? IconUrl { get; set; }
    [MaxLength(500)] public string? StoreUrl { get; set; }
    public DateTime? Released { get; set; }
    public DateTime Checked { get; set; }
    [MaxLength(300)] public string? Error { get; set; }
}

/// <summary>Helbredsscore pr. maskine og dag (lokal dato): dagens laveste score og værste tilstand. Til rapporten Sundhed.</summary>
public class HealthDaily
{
    public long Id { get; set; }
    public int MachineId { get; set; }
    public DateOnly Day { get; set; }
    public int Score { get; set; }
    [MaxLength(16)] public string State { get; set; } = "unknown";
    public int Samples { get; set; }
    public int ScoreSum { get; set; }
}

/// <summary>Gemt vært uden agent (router, NAS, switch …), som terminalen forbinder til med SSH.</summary>
public class TerminalHost
{
    public int Id { get; set; }
    [MaxLength(64)] public string Name { get; set; } = "";
    [MaxLength(253)] public string Address { get; set; } = "";
    public int Port { get; set; } = 22;
    [MaxLength(64)] public string Username { get; set; } = "";
    [MaxLength(60)] public string? Group { get; set; }
    public bool Favorite { get; set; }
    public bool Protected { get; set; }
    public int? CredentialId { get; set; }
    /// <summary>Den fastlåste værtsnøgle (algoritme og SHA-256). Null, til der er forbundet første gang.</summary>
    [MaxLength(40)] public string? HostKeyType { get; set; }
    [MaxLength(64)] public string? HostKeyFingerprint { get; set; }
    /// <summary>En ændret værtsnøgle, som venter på en administrators godkendelse. Forbindelsen er stoppet imens.</summary>
    [MaxLength(40)] public string? PendingKeyType { get; set; }
    [MaxLength(64)] public string? PendingKeyFingerprint { get; set; }
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public DateTime? LastConnected { get; set; }
}

/// <summary>Login i nøgleboksen. <see cref="Cipher"/> er krypteret og forlader aldrig serveren.</summary>
public class TerminalCredential
{
    public int Id { get; set; }
    [MaxLength(64)] public string Name { get; set; } = "";
    [MaxLength(16)] public string Kind { get; set; } = "key";     // key | password
    public string Cipher { get; set; } = "";
    /// <summary>Offentlig nøgle i authorized_keys-format (kun nøgler).</summary>
    [MaxLength(2000)] public string? PublicKey { get; set; }
    [MaxLength(64)] public string? Fingerprint { get; set; }
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsed { get; set; }
}

/// <summary>Gemt kommando til terminalen. <c>{{navn}}</c> i kommandoen er en variabel, der udfyldes ved kørsel.</summary>
public class TerminalSnippet
{
    public int Id { get; set; }
    [MaxLength(80)] public string Name { get; set; } = "";
    [MaxLength(4000)] public string Command { get; set; } = "";
    [MaxLength(300)] public string Description { get; set; } = "";
    /// <summary>Tom = alle. Ellers windows, linux eller mac.</summary>
    [MaxLength(16)] public string Platform { get; set; } = "";
    /// <summary>Markeret som risikabel af brugeren. Kendte farlige kommandoer opdages også automatisk.</summary>
    public bool Risky { get; set; }
    public DateTime Created { get; set; } = DateTime.UtcNow;
}
