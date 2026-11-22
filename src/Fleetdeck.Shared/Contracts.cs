namespace Fleetdeck.Shared;

/// <summary>Kørselskontekst for et job på agenten.</summary>
public static class RunAs
{
    public const string System = "system";        // LocalSystem (maskinniveau)
    public const string User = "user";            // den indloggede bruger, uden administrator
    public const string UserAdmin = "user_admin"; // den indloggede bruger, elevated (kræver at brugeren er admin)
    public static readonly string[] All = [System, User, UserAdmin];
}

public static class JobTypes
{
    public const string Install = "install", Uninstall = "uninstall", Upgrade = "upgrade", Script = "script",
        Reboot = "reboot", Shutdown = "shutdown", Message = "message", Inventory = "inventory", Health = "health",
        WinSetup = "winsetup", RemoveAgent = "remove_agent", Lock = "lock", Logoff = "logoff",
        WindowsUpdate = "windows_update", CancelPower = "cancel_power", WakeScreen = "wake_screen", RemoveApp = "remove_app", KillHeadless = "kill_headless",
        AmtActivate = "amt_activate", AmtDeactivate = "amt_deactivate", KitBackup = "kit_backup", KitRestore = "kit_restore",
        Ios = "ios",   // kun Mac: iPads tilsluttet med kabel (Apple Configurators cfgutil) – se IosActions
        IosApps = "ios_apps",   // kun Mac: læs de installerede apps på én tilsluttet iPad (Ecid)
        UsbIpad = "usb_ipad",   // alle platforme med udvidelsen "iPad via USB" (pymobiledevice3) – se UsbIpadActions
        FileVaultKey = "filevault_key",   // kun Mac: ny personlig FileVault-gendannelsesnøgle (brugeren skriver sin adgangskode på Mac'en)
        CollectLogs = "collect_logs",   // Beskeder → "Hent logfiler": agentens log, seneste fejl og systeminfo som zip (LogBundle)
        WuHide = "wu_hide",   // kun Windows: skjul (eller vis igen) Windows Update-opdateringer (IUpdate.IsHidden) – UpdateIds og Hide
        UpdateRound = "update_round",   // opdateringsmotoren: én runde i fast rækkefølge (UpdatePhases) med struktureret resultat (UpdateReport)
        BiosSettingsRead = "bios_settings_read",   // læs alle BIOS-indstillinger → snapshot (BiosSettingsJob)
        BiosSettingsApply = "bios_settings_apply",   // skriv valgte BIOS-indstillinger i én gemning (BiosSettings + evt. Ticket til adgangskoden)
        BiosUpdate = "bios_update",   // BIOS-opdatering inde fra styresystemet, kun på et udtrykkeligt tryk og aldrig i "Opdatér alt" (BiosUpdateJob)
        UsbDisks = "usb_disks",   // USB-stik med BIOS-opdatering: øjebliksbillede af diskene og vent på et nyt stik (BiosUsbOrder, kun læsning)
        BiosUsbWrite = "bios_usb_write",   // formatér det bekræftede stik og læg BIOS-pakken på (BiosUsb.Write på Linux, BiosUsbWin.Write på Windows, BiosUsbMac.Write på Mac)
        SpareCard = "spare_card";   // kun Raspberry Pi: natlig kopi af hele systemet til reservekortet i en USB-kortlæser (SpareCard.Run)

    /// <summary>Job, der ikke må kopieres med "Prøv igen": de hører til en kørsel/session og startes forfra fra BIOS-siden.</summary>
    public static readonly string[] NoRetry = [BiosUpdate, BiosSettingsApply, BiosUsbWrite, UsbDisks];
}

/// <summary>Opstartstidspunktet til check-in: nu minus oppetiden, rundet til hele sekunder (UTC).</summary>
public static class BootTime
{
    public static DateTime? FromUptime(DateTime nowUtc, double? uptimeSec)
    {
        if (uptimeSec is not double up || up < 0 || double.IsNaN(up)) return null;
        var t = nowUtc.AddSeconds(-up).Ticks;
        return new DateTime(t - t % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    }
}

/// <summary>Planlagt genstart/slukning, som agenten melder, så den kan vises med nedtælling og annulleres.</summary>
public static class PlannedPower
{
    /// <summary>Hvor længe man kan udsætte fra skærmen.</summary>
    public const int PostponeMinutes = 60;
    public static DateTime? At { get; private set; }
    public static string? Mode { get; private set; }
    public static void Set(string mode, DateTime atUtc) { Mode = mode; At = atUtc; }
    public static void Clear() { Mode = null; At = null; }
    /// <summary>Gældende plan, eller null når tidspunktet er passeret for mere end 10 minutter siden.</summary>
    public static (string Mode, DateTime At)? Current => At is { } a && Mode is { } m && a > DateTime.UtcNow.AddMinutes(-10) ? (m, a) : null;

    /// <summary>
    /// "Genstart nu"/"Sluk nu" fra skærmen: kun den handling, Fleetdeck selv har planlagt på netop denne maskine.
    /// Giver planens handling, eller null (ingen plan, en anden handling end den planlagte, eller planen er for gammel).
    /// </summary>
    public static string? NowAllowed((string Mode, DateTime At)? plan, string? asked = null) =>
        plan is { } p && p.Mode is JobTypes.Reboot or JobTypes.Shutdown && (asked is null || asked == p.Mode) ? p.Mode : null;

    /// <summary>Knappens tekst: "Genstart nu" eller "Sluk nu".</summary>
    public static string NowLabel(string mode) => mode == JobTypes.Reboot ? "Genstart nu" : "Sluk nu";

    /// <summary>Linjen i jobbets output og revisionsloggen, når brugeren selv sagde ja.</summary>
    public static string NowNote(string mode, DateTime localTime) =>
        $"Brugeren valgte {NowLabel(mode)} kl. {localTime:HH:mm}.";

    /// <summary>
    /// Argumenter til Windows' shutdown.exe. <paramref name="force"/> giver /f (luk hængende programmer). Bemærk: Windows
    /// tvinger i forvejen, når /t er større end 0 – /f gør det bare udtrykkeligt, også ved /t 0 ("Genstart nu").
    /// </summary>
    public static string WindowsArgs(string mode, int delaySec, string msg, bool force)
    {
        var t = Math.Clamp(delaySec, 0, 315360000);
        var c = msg.Replace("\"", "'").Replace("\r", " ").Replace("\n", " ");
        if (c.Length > 500) c = c[..500];
        return $"{(mode == JobTypes.Reboot ? "/r" : "/s")}{(force ? " /f" : "")} /t {t} /d p:4:1 /c \"{c}\"";
    }
}

public static class JobStatus
{
    public const string Pending = "pending", Running = "running", Success = "success", Failed = "failed",
        Cancelled = "cancelled", WaitingUser = "waiting_user", Expired = "expired",
        /// <summary>Genstart/sluk er sendt, men maskinen er endnu ikke tilbage med en ny opstartstid (eller slukket). Se PowerWatch på serveren.</summary>
        Restarting = "restarting",
        /// <summary>Genstarten/slukningen kom ikke igennem inden for fristen: maskinen hænger (eller vågnede ikke).</summary>
        Hung = "hung",
        /// <summary>Delvist gennemført: noget lykkedes, men noget fejlede eller venter (se jobbets UpdateReport).</summary>
        Partial = "partial";
    public static readonly string[] Active = [Pending, Running, WaitingUser, Restarting];
}

/// <summary>
/// Rettighed for en API-nøgle (Indstillinger → Adgang for programmer). Hver rettighed omfatter de lavere:
/// <c>read</c> = kun læse status, <c>control</c> = læse og styre den daglige drift, <c>admin</c> = alt.
/// Hvilke endpoints hver rettighed giver adgang til, står ved selve endpointet (<c>RequireApiKey(scope)</c> på serveren).
/// </summary>
public static class ApiScopes
{
    public const string Read = "read", Control = "control", Admin = "admin";
    public static readonly string[] All = [Read, Control, Admin];

    /// <summary>0 = ukendt rettighed (giver ingen adgang).</summary>
    public static int Level(string? scope) => scope switch { Read => 1, Control => 2, Admin => 3, _ => 0 };

    /// <summary>Må en nøgle med rettigheden <paramref name="have"/> kalde et endpoint, der kræver <paramref name="need"/>?</summary>
    public static bool Allows(string? have, string? need) => Level(need) > 0 && Level(have) >= Level(need);
}

public static class PackageTypes
{
    public const string Winget = "winget", MsStore = "msstore", File = "file",
        Steam = "steam",   // Identifier = Steam app-id; installeres via steam://install/<id> i brugerens session
        Uri = "uri",       // Identifier = launcher-link (Epic, Battle.net ...), åbnes i brugerens session
        Apt = "apt",       // Linux: distroens pakke (apt/dnf/zypper/pacman; Identifier = pakkenavn eller "apt:x dnf:y …")
        Pkg = "pkg",       // Linux: samme som apt, men navngivet efter distroens egen pakkehåndtering
        Flatpak = "flatpak", // Linux: Flathub-app (Identifier = fx org.videolan.VLC)
        Snap = "snap",     // Linux: snap-pakke (Args fx --classic)
        Brew = "brew",     // Mac: Homebrew (Identifier = navn, cask:navn eller formula:navn)
        Mas = "mas",       // Mac: App Store (Identifier = app-id fra mas search)
        Script = "script"; // Linux/Mac: installeres med et script (Args = scriptet, køres som root); Detect = navnet i inventaret
    public static readonly string[] All = [Winget, MsStore, File, Steam, Uri, Apt, Snap];
    public static bool IsUserOnly(string type) => type is Steam or Uri;
    public static bool IsLinux(string type) => type is Apt or Pkg or Snap or Flatpak or Script;
    public static bool IsMac(string type) => type is Brew or Mas;
    /// <summary>Kan pakketypen bruges på platformen?</summary>
    public static bool Fits(string type, string? platform) => platform switch
    {
        Platforms.Linux => IsLinux(type), Platforms.Mac => IsMac(type) || type == Script, _ => !IsLinux(type) && !IsMac(type),
    };
}

public static class Platforms
{
    public const string Windows = "windows", Linux = "linux", Mac = "mac";
    /// <summary>Denne agents platform (Linux-agenten kører også på macOS).</summary>
    public static string Current => OperatingSystem.IsMacOS() ? Mac : OperatingSystem.IsLinux() ? Linux : Windows;
}

// ------------------------------------------------------------------ tilmelding
/// <param name="Key">Den globale tilmeldingsnøgle eller en engangskode.</param>
/// <param name="PreviousToken">Agentens hidtidige token (geninstallation på samme maskine): beviser, at det er den samme maskine.</param>
/// <param name="AccessSecret">Kun ved "Anmod om adgang": anmodningens hemmelighed. En engangskode udstedt til en anmodning virker kun sammen med den.</param>
public sealed record EnrollRequest(string Key, string Name, bool Provision, SystemFacts? Facts, int? ProfileId = null, int? DeployId = null,
    string? Platform = null, List<string>? Extensions = null, string? PreviousToken = null, string? AccessSecret = null);
/// <param name="JobKey">Serverens offentlige jobnøgle (PEM): agenten pinner den og kræver signerede jobs fra start. Null = serveren signerer ikke.</param>
/// <param name="TlsPin">Fingeraftryk af serverens TLS-nøgle (til selvsignerede certifikater).</param>
/// <param name="Pending">Navnet findes allerede: maskinen venter på, at en administrator godkender den.</param>
public sealed record EnrollResponse(int MachineId, string Token, int PollSeconds, string? JobKey = null, string? TlsPin = null, bool Pending = false);
/// <summary>En profil, man kan vælge ved installationen. ProfileId 0 i tilmeldingen = bevidst ingen profil.</summary>
public sealed record ProfileOption(int Id, string Name, int Machines, int Packages);

// ------------------------------------------------------------------ check-in
public sealed class CheckinRequest
{
    /// <summary>Linux: processoren (x64, arm64, arm), så serveren sender den rigtige agent ved opdatering. Mangler = x64.</summary>
    public string? Arch { get; set; }
    public string AgentVersion { get; set; } = "";
    public LiveFacts Facts { get; set; } = new();
    /// <summary>Engangs-nonce: serveren lægger den i det signerede svar (ældre agenter sender ingen og får intet signeret svar).</summary>
    public string? Nonce { get; set; }
    public AgentTrustReport? Trust { get; set; }
    /// <summary>Mac: "Hold vågen på strøm" er ændret lokalt (menulinjen/appen) og endnu ikke bekræftet af serveren.</summary>
    public KeepAwakeChange? KeepAwake { get; set; }
    /// <summary>Hvad agenten kan ud over det grundlæggende (fx terminal). Ældre agenter sender intet.</summary>
    public List<string>? Caps { get; set; }
}

public sealed class CheckinResponse
{
    /// <summary>Overvågningstjek, som agenten skal køre fra denne maskine nu (resultater til /api/agent/checks).</summary>
    public List<AgentCheck> Checks { get; set; } = [];
    /// <summary>Tjenester, der altid skal køre (profilens "Hold kørende"): agenten starter dem igen med det samme, hvis de stopper.</summary>
    public List<string> KeepRunning { get; set; } = [];
    // til ikonet i proceslinjen
    public string? Profile { get; set; }
    public int Upgrades { get; set; }
    public int Alerts { get; set; }
    public string? Health { get; set; }
    public string? WebUrl { get; set; }
    public int PollSeconds { get; set; } = 30;
    public bool InventoryDue { get; set; }
    public bool HealthDue { get; set; }
    public string AgentVersion { get; set; } = "";
    /// <summary>Serverens officielle adresse. Er den ændret, skifter agenten selv over (efter et sundhedstjek).</summary>
    public string ServerUrl { get; set; } = "";
    public List<AgentJob> Jobs { get; set; } = [];
    public ShareInfo? Share { get; set; }
    /// <summary>Netværksdrev, maskinen skal have (fra profil og maskine). Login: Share.User/Share.Password.</summary>
    public List<DriveSpec> Drives { get; set; } = [];
    /// <summary>Mac: "Hold vågen på strøm (også med lukket låg)". Null = ikke en Mac (eller ældre server).</summary>
    public KeepAwakeSetting? KeepAwake { get; set; }
    /// <summary>Nye beskeder til brugeren på maskinen (Beskeder/helpdesk). Ældre servere sender intet.</summary>
    public List<HelpdeskNote> Helpdesk { get; set; } = [];
    /// <summary>Printere, maskinen skal have (profil + maskine). Null = ældre server eller afvist svar: rør ingenting.</summary>
    public List<PrinterSpec>? Printers { get; set; }
    /// <summary>Find printere på netværket (Bonjour/WSD) og meld dem med helbredet, når værdien er ny. Null = ældre server eller ingen søgning.</summary>
    public DateTime? PrinterSearchAt { get; set; }
    /// <summary>Signeret udgave af jobs, opdatering, adresse, drev og tjenester (kun når agenten sendte en nonce).</summary>
    public CommandEnvelope? Signed { get; set; }
}

/// <summary>Et netværksdrev: \\Host\Share som bogstav (Windows) eller /mnt/Name (Linux) / ~/Name (Mac).</summary>
public sealed class DriveSpec
{
    /// <summary>Stabilt id (fx "d12"), så agenten kan fjerne drev, der ikke længere ønskes.</summary>
    public string Key { get; set; } = "";
    public string Host { get; set; } = "";
    public string Share { get; set; } = "";
    /// <summary>Windows: drevbogstav uden kolon (fx "M"); tom = næste ledige.</summary>
    public string? Letter { get; set; }
    /// <summary>Linux/Mac: mappenavn (fx "Media" → /mnt/Media); også navnet i Stifinder.</summary>
    public string Name { get; set; } = "";
    /// <summary>Fundet på maskinen (brugeren koblede det selv på): brug det login, der allerede er gemt, ikke Unraid-brugeren.</summary>
    public bool OwnLogin { get; set; }
}

/// <summary>Netværksdrev, brugeren selv har koblet på (ikke lavet af Fleetdeck) – gemmes på serveren og holdes forbundet.</summary>
public sealed class FoundDrive
{
    public string Host { get; set; } = "";
    public string Share { get; set; } = "";
    /// <summary>Windows: bogstav uden kolon.</summary>
    public string? Letter { get; set; }
    /// <summary>Hvor det er: "M:" eller "/mnt/data".</summary>
    public string Target { get; set; } = "";
    public string Name { get; set; } = "";
}
public sealed class FoundDrivesRequest { public List<FoundDrive> Drives { get; set; } = []; }

/// <summary>Status for et netværksdrev på maskinen (sendes med helbredet).</summary>
public sealed class DriveState
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Hvor drevet er: "M:" eller "/mnt/Media".</summary>
    public string Target { get; set; } = "";
    public bool Ok { get; set; }
    public string? Error { get; set; }
    /// <summary>Udfyldes af serveren: sidst set forbundet, og siden hvornår den nuværende tilstand har varet (UTC).</summary>
    public DateTime? LastOk { get; set; }
    public DateTime? Since { get; set; }
}

public sealed class ShareInfo
{
    public string Path { get; set; } = "";
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class AgentJob
{
    public int Id { get; set; }
    public string Type { get; set; } = "";
    public string Title { get; set; } = "";
    public string RunAs { get; set; } = Fleetdeck.Shared.RunAs.System;
    public JobParams Params { get; set; } = new();
}

/// <summary>Alle parametre et job kan have. Ubrugte felter er null.</summary>
public sealed class JobParams
{
    /// <summary>remove_app: programfilen/app-stien (Mac: .app), til at finde programmet igen.</summary>
    public string? Path { get; set; }
    // pakker
    public string? Name { get; set; }
    public string? PackageType { get; set; }
    public string? Identifier { get; set; }
    public string? Args { get; set; }
    public string? Detect { get; set; }
    public string? FileUrl { get; set; }
    // opdatering
    public string? WingetId { get; set; }
    public List<string>? Exclude { get; set; }
    /// <summary>Gendannelsespunkt: stier/mønstre, der skal med (kit_backup).</summary>
    public List<string>? Paths { get; set; }
    /// <summary>Opdatering: pakker med et andet "Kør som" end SYSTEM (winget-id → user/user_admin), sat af serveren.</summary>
    public Dictionary<string, string>? RunAsById { get; set; }
    // script
    public string? Body { get; set; }
    public int? TimeoutMin { get; set; }
    // genstart/besked
    public int? Delay { get; set; }
    public bool? OnlyIfPending { get; set; }
    public int? OnlyIfIdleMin { get; set; }   // genstart/sluk kun hvis ingen har brugt pc'en i så mange minutter
    /// <summary>Genstart/sluk: luk hængende programmer (Windows: shutdown /f). Mangler = ja.</summary>
    public bool? Force { get; set; }
    /// <summary>Sluk: test af Wake-on-LAN – Fleetdeck sender et magic packet, når maskinen er slukket, og måler om den kommer op.</summary>
    public bool? WolTest { get; set; }
    public string? Message { get; set; }
    public string? Text { get; set; }
    /// <summary>Besked, der bare er til orientering: ingen bruger på skærmen er ikke en fejl, og jobbet venter ikke.</summary>
    public bool? Notice { get; set; }
    // WinSetup
    public string? Step { get; set; }
    // Windows Update
    public bool? IncludeDrivers { get; set; }
    // opdateringsmotoren (update_round)
    /// <summary>update_round: rundens nummer og det højeste antal runder (til visningen "Runde 2/4").</summary>
    public int? UpdRound { get; set; }
    public int? UpdMaxRounds { get; set; }
    /// <summary>update_round: systemdelen (Windows Update, apt/dnf …, macOS) og programdelen (winget, Office, snap, flatpak, brew, mas).</summary>
    public bool? UpdSystem { get; set; }
    public bool? UpdApps { get; set; }
    /// <summary>update_round: firmware med (sidst, kun på netstrøm, aldrig BIOS).</summary>
    public bool? IncludeFirmware { get; set; }
    /// <summary>update_round: Windows-funktionsopdateringer (ny Windows-version) med.</summary>
    public bool? FeatureUpdates { get; set; }
    /// <summary>update_round: kun en kontrolsøgning efter den sidste genstart – intet installeres; alt fundet meldes som "mangler".</summary>
    public bool? UpdVerify { get; set; }
    /// <summary>update_round (indhentning): bruges pc'en (aktivitet inden for så mange minutter), venter drivere og firmware.</summary>
    public int? UpdIdleMin { get; set; }
    /// <summary>Genstart: udløbet efter dette tidspunkt (UTC) – fx en pc, der sov, da genstarten blev bestilt om natten. Agenten genstarter så ikke.</summary>
    public DateTime? NotAfter { get; set; }
    // iPads via Mac'en (ios)
    /// <summary>ios: hvad der skal gøres (<see cref="IosActions"/>).</summary>
    public string? Action { get; set; }
    /// <summary>ios: iPad'ens ECID (fx 0x1A2B3C4D5E6F) – alle handlinger undtagen list.</summary>
    public string? Ecid { get; set; }
    /// <summary>ios: iPad'en i Fleetdeck (den låste profil hentes til den).</summary>
    public int? IpadId { get; set; }
    /// <summary>ios install_profile: den låste profils PayloadIdentifier.</summary>
    public string? ProfileIdentifier { get; set; }
    /// <summary>ios install_profile: ældre profiler (PayloadIdentifier), der fjernes først, hvis de er installeret – fx den gamle DNS-profil fra QR-koden.</summary>
    public List<string>? ReplaceIds { get; set; }
    /// <summary>ios: engangskode til /api/agent/ios-secrets/{jobId} (selve hemmelighederne står aldrig i jobbet).</summary>
    public string? SecretNonce { get; set; }
    /// <summary>ios restore_backup: backup-sættet i Fleetdeck (ipad-&lt;id&gt;-&lt;tid&gt;), hvis Mac'en ikke selv har backuppen.</summary>
    public string? BackupSet { get; set; }
    /// <summary>ios supervise: rækkefølgen (<see cref="SupervisePlan"/>): restore_first (standard) eller prepare_first.</summary>
    public string? Order { get; set; }
    /// <summary>ios supervise: sprog og område til opsætningen (fx da / DK).</summary>
    public string? Language { get; set; }
    public string? Region { get; set; }
    // iPad via USB på en pc (usb_ipad) – Action, IpadId, ProfileIdentifier, ReplaceIds og TimeoutMin bruges også
    /// <summary>usb_ipad: iPad'ens UDID (pymobiledevice3 vælger iPad'en med den).</summary>
    public string? Udid { get; set; }
    /// <summary>usb_ipad: engangsbillet, som agenten bytter til én hemmelighed (backup-kode eller overvågningsnøgle) mens jobbet kører.
    /// Selve hemmeligheden står aldrig her. Fjernes, når den er brugt, og når jobbet er slut.</summary>
    public string? Ticket { get; set; }
    /// <summary>spare_card: det valgte reservekort og hvad der skal med (se SpareCard).</summary>
    public SpareCardJob? Spare { get; set; }
    /// <summary>usb_ipad: de slags hemmeligheder, billetten allerede er byttet til (release skal bruge både nøglen og backup-koden – hver én gang).</summary>
    public List<string>? TicketUsed { get; set; }
    /// <summary>wu_hide: Windows Update-opdateringerne (id'er med "wu:" foran), der skal skjules eller vises igen.</summary>
    public List<string>? UpdateIds { get; set; }
    /// <summary>wu_hide: true = skjul, false = vis igen.</summary>
    public bool? Hide { get; set; }
    /// <summary>usb_ipad restore: backuppen i Fleetdeck (fx ipad-20261117-203000), antal dele og den iPad, den blev taget af.</summary>
    public string? Backup { get; set; }
    public int? Parts { get; set; }
    public string? SourceUdid { get; set; }
    /// <summary>usb_ipad supervise: organisationsnavnet, iPad'en viser som tilsynsførende.</summary>
    public string? Org { get; set; }
    // "Overvåg uden datatab" (ios og usb_ipad: supervise_nowipe) – BackupSet er det udtrykkeligt valgte sæt (ellers vælger agenten efter reglerne)
    /// <summary>supervise_nowipe: <see cref="NoWipeMethods"/>.</summary>
    public string? Method { get; set; }
    /// <summary>supervise_nowipe: hvor det valgte sæt ligger ("mac" eller "usb").</summary>
    public string? BackupStore { get; set; }
    /// <summary>supervise_nowipe: tag en ny backup først (springes over, hvis iPad'en er tom eller står i opsætningen).</summary>
    public bool? FreshBackup { get; set; }
    /// <summary>supervise_nowipe: backupperne i Fleetdeck, agenten må vælge imellem (uden hemmeligheder).</summary>
    public List<NoWipeCandidate>? Candidates { get; set; }
    // BIOS-indstillinger (bios_settings_read/apply). Adgangskoden står aldrig her: Ticket byttes til den mens jobbet kører.
    /// <summary>bios_settings_apply: værdierne, der skal skrives (kun hvis den nuværende værdi stadig er ExpectOld).</summary>
    public List<BiosSettingChange>? BiosSettings { get; set; }
    /// <summary>bios_settings_read: hvorfor snapshottet tages (manual, pre-update, post-boot, post-restore).</summary>
    public string? BiosReason { get; set; }
    /// <summary>bios_update: hvad agenten skal gøre (tjek, læg klar eller kontrollér) og hvilken pakke.</summary>
    public BiosUpdateOrder? BiosUpdate { get; set; }
    /// <summary>usb_disks og bios_usb_write: guidens session, det bekræftede stik og pakken.</summary>
    public BiosUsbOrder? BiosUsb { get; set; }
}

public sealed class JobStartResponse
{
    public bool Ok { get; set; }
}

public sealed class JobResult
{
    public string Status { get; set; } = JobStatus.Success;
    public int? ExitCode { get; set; }
    public string Output { get; set; } = "";
    /// <summary>Genstart/slukning planlagt af jobbet (UTC), så websiden straks kan vise nedtællingen.</summary>
    public DateTime? PowerAt { get; set; }
    public string? PowerMode { get; set; }
    /// <summary>ios-job: de iPads, Mac'en så (til serveren – vises ikke som output).</summary>
    public IosReport? Ios { get; set; }
    /// <summary>ios_apps-job: de apps, Mac'en så på iPad'en.</summary>
    public IosAppsReport? IosApps { get; set; }
    /// <summary>update_round: hvad runden installerede, hvad der fejlede, og hvad der venter.</summary>
    public UpdateReport? Update { get; set; }
    /// <summary>Særlig grund (JobReasons), fx at firmwaren afviser opdateringen – serveren foreslår den så ikke igen.</summary>
    public string? Reason { get; set; }
    /// <summary>bios_settings_read/apply: indstillingerne som de står nu (efter apply: som BIOS'en melder dem før genstart).</summary>
    public BiosSettingsReport? BiosSettings { get; set; }
    /// <summary>bios_settings_apply: kopien taget lige før skrivningen.</summary>
    public BiosSettingsReport? BiosSettingsBefore { get; set; }
    public BiosApplyResult? BiosApply { get; set; }
    /// <summary>bios_update: forudsætninger og resultat.</summary>
    public BiosUpdateReport? BiosUpdate { get; set; }
    /// <summary>usb_disks og bios_usb_write: diskene eller resultatet.</summary>
    public BiosUsbReport? BiosUsb { get; set; }
}

// ------------------------------------------------------------------ fakta og helbred
public sealed class LiveFacts
{
    public string? Hostname { get; set; }
    public string? User { get; set; }
    public string? Ip { get; set; }
    public string? Mac { get; set; }
    public double? Cpu { get; set; }
    public double? Ram { get; set; }
    public double? RamUsedGb { get; set; }
    public double? RamTotalGb { get; set; }
    public double? UptimeH { get; set; }
    /// <summary>Opstartstidspunktet (UTC, på sekundet). Ældre agenter sender kun <see cref="UptimeH"/> (afrundet til 0,1 time).</summary>
    public DateTime? BootAt { get; set; }
    public bool PendingReboot { get; set; }
    /// <summary>Planlagt genstart ("reboot") eller slukning ("shutdown") og hvornår (UTC).</summary>
    public string? PowerMode { get; set; }
    public DateTime? PowerAt { get; set; }
    /// <summary>Hovedløse browsere (startet med --headless, fx af automatisering), der kører lige nu.</summary>
    public int? HeadlessBrowsers { get; set; }
    /// <summary>Nogen er forbundet med fjernskrivebord (RustDesk) lige nu.</summary>
    public bool RemoteActive { get; set; }
    /// <summary>Agenten kan ikke opdatere sig selv (seneste grund) – vises som alarm.</summary>
    public string? UpdateError { get; set; }
    /// <summary>Mac: "Hold vågen på strøm" – indstilling, strømkilde og hvad agenten gør lige nu.</summary>
    public KeepAwakeState? KeepAwake { get; set; }
    public Temperatures Temps { get; set; } = new();
    public List<VolumeInfo> Volumes { get; set; } = [];
}

public sealed class Temperatures
{
    public double? Cpu { get; set; }
    public double? Gpu { get; set; }
    public string? Source { get; set; }
}

public sealed class VolumeInfo
{
    public string Drive { get; set; } = "";
    public double SizeGb { get; set; }
    public double FreeGb { get; set; }
    public double PctUsed { get; set; }
}

public sealed class SystemFacts
{
    public string? Hostname { get; set; }
    public string? Os { get; set; }
    /// <summary>Linux: distroens ID og familie fra os-release (fx "linuxmint" / "ubuntu debian") – til logo og hjælp.</summary>
    public string? Distro { get; set; }
    public string? DistroLike { get; set; }
    /// <summary>Linux: pakkehåndtering og opstartssystem (fx "apt", "systemd").</summary>
    public string? PackageManager { get; set; }
    public string? InitSystem { get; set; }
    public string? OsVersion { get; set; }
    public string? Build { get; set; }
    public string? Installed { get; set; }
    public string? LastBoot { get; set; }
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    /// <summary>Raspberry Pi: serienummeret (8 hex-tegn), som Pi'en bruger som mappe ved netboot.</summary>
    public string? PiSerial { get; set; }
    public string? Board { get; set; }
    public string? Bios { get; set; }
    public string? CpuName { get; set; }
    public int? Cores { get; set; }
    public int? Threads { get; set; }
    public double? RamGb { get; set; }
    public List<string> Gpus { get; set; } = [];
    public List<NicInfo> Nics { get; set; } = [];
    public string? Domain { get; set; }
    public string? TimeZone { get; set; }
    public string? Firmware { get; set; }
    public bool? SecureBoot { get; set; }
    public string? Edition { get; set; }
    public string? AgentBuild { get; set; }

    // BIOS/UEFI (til BIOS-tjekket, se BiosFacts). Ældre agenter sender dem ikke; så bruges kun Bios-feltet ovenfor.
    // Serienumre og service tags hentes bevidst ikke: ingen af leverandørernes kataloger skal bruge dem.
    /// <summary>BIOS'ens leverandør (Linux bios_vendor, Windows Win32_BIOS.Manufacturer), fx "LENOVO".</summary>
    public string? BiosVendor { get; set; }
    /// <summary>BIOS-versionen som den står i SMBIOS (fx "M1UKT75A" eller "N2HET77W (1.60 )").</summary>
    public string? BiosVersion { get; set; }
    /// <summary>BIOS'ens dato som yyyy-MM-dd.</summary>
    public string? BiosDate { get; set; }
    /// <summary>SMBIOS "BIOS release" som major.minor (Linux bios_release, Windows SystemBiosMajor/MinorVersion), fx "1.117".</summary>
    public string? BiosRelease { get; set; }
    /// <summary>Linux product_name / Windows Win32_ComputerSystem.Model (Lenovo: maskintypen, fx "10T7…").</summary>
    public string? ProductName { get; set; }
    /// <summary>Linux product_version / Windows Win32_ComputerSystemProduct.Version (Lenovo: modelnavnet, fx "ThinkCentre M720q").</summary>
    public string? ProductVersion { get; set; }
    /// <summary>Linux product_sku / Windows Win32_ComputerSystem.SystemSKUNumber.</summary>
    public string? Sku { get; set; }
    public string? BoardVendor { get; set; }
    public string? BoardName { get; set; }
    /// <summary>En virtuel maskine (QEMU, VMware, Hyper-V …): BIOS'en styres af værten.</summary>
    public bool? IsVirtual { get; set; }
    /// <summary>BIOS-indstillinger, agenten kan læse (driver, antal, adgangskode, ventende ændringer). Null = ingen.</summary>
    public BiosSettingsInfo? BiosSettings { get; set; }
}

public sealed class NicInfo
{
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public string? Speed { get; set; }
    public string? Mac { get; set; }
}

/// <summary>Fjernskrivebord (RustDesk) sat op af agenten til ubemandet adgang: ID og den faste adgangskode.</summary>
public sealed class RemoteDesktopInfo
{
    public string Tool { get; set; } = "rustdesk";
    public string Id { get; set; } = "";
    /// <summary>Kun til serveren – sendes aldrig videre til websiden (kun via administrator-knappen "Forbind").</summary>
    public string? Password { get; set; }
    public string? Error { get; set; }
}

public static class RemoteDesktop
{
    /// <summary>Stærk adgangskode til ubemandet adgang (kun bogstaver og tal, så den kan stå i en URL og på en kommandolinje).</summary>
    public static string NewPassword()
    {
        const string chars = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return new string(System.Security.Cryptography.RandomNumberGenerator.GetItems<char>(chars, 20));
    }
    /// <summary>RustDesk-ID'et i et svar (sidste linje med kun cifre).</summary>
    public static string? ParseId(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault(l => l.Length is >= 6 and <= 12 && l.All(char.IsAsciiDigit));
}

public sealed class HealthReport
{
    public LiveFacts Live { get; set; } = new();
    public RemoteDesktopInfo? Remote { get; set; }
    /// <summary>Intel AMT (vPro): fjernstyring af strøm m.m. direkte i hardwaren. Null = ikke tjekket / ikke Windows.</summary>
    public AmtInfo? Amt { get; set; }
    /// <summary>Starter agenten selv ved opstart (uden login)? Windows: tjeneste + vagthund.</summary>
    public AutostartInfo? Autostart { get; set; }
    /// <summary>Tjenesterne fra "Hold kørende" og hvad agenten har gjort ved dem.</summary>
    public List<KeptService>? Kept { get; set; }
    /// <summary>Raspberry Pi: USB-diske, der kan bruges som reservekort (aldrig mmcblk* eller den disk, "/" ligger på). Null = ikke en Pi.</summary>
    public List<SpareDisk>? SpareDisks { get; set; }
    public List<DriveState> Drives { get; set; } = [];
    /// <summary>Printerne fra Fleetdeck og deres status på maskinen. Null = ikke tjekket (ældre agent).</summary>
    public List<PrinterState>? Printers { get; set; }
    /// <summary>Alle printere, maskinen har (også dem, Fleetdeck ikke har lagt på). Null = ikke tjekket.</summary>
    public List<LocalPrinter>? LocalPrinters { get; set; }
    /// <summary>Netværksprintere, maskinen selv har fundet (Bonjour/WSD), også dem den ikke har lagt på. Null = ikke søgt.</summary>
    public List<SeenPrinter>? NetPrinters { get; set; }
    /// <summary>Hvornår agenten sidst søgte efter printere (UTC).</summary>
    public DateTime? NetPrintersAt { get; set; }
    public List<DiskHealth> Disks { get; set; } = [];
    public DefenderStatus? Defender { get; set; }
    public List<AntivirusProduct> Antivirus { get; set; } = [];
    public Dictionary<string, bool> Firewall { get; set; } = [];
    public string? BitLocker { get; set; }
    /// <summary>Diskkryptering pr. drev (BitLocker, FileVault, LUKS) – aldrig selve nøglerne. Null = ikke tjekket.</summary>
    public EncryptionInfo? Encryption { get; set; }
    /// <summary>TPM-chippen (Windows: Win32_Tpm, Linux: sysfs/tpm2-tools, Mac: Secure Enclave). Null = ikke tjekket (ældre agent).</summary>
    public TpmInfo? Tpm { get; set; }
    /// <summary>Netværksadaptere: status, hastighed, adresser, Wi-Fi, trafik og fejl. Null = ikke tjekket (ældre agent).</summary>
    public NetworkInfo? Network { get; set; }
    /// <summary>Batterier på bærbare (Windows: WMI root\wmi, Linux: /sys/class/power_supply, Mac: ioreg). Tom = intet batteri; null = ikke tjekket.</summary>
    public List<BatteryInfo>? Batteries { get; set; }
    public EventSummary Events { get; set; } = new();
    /// <summary>
    /// Nye fejl og advarsler fra systemloggen siden sidste gang (Windows' hændelseslog, journald, macOS' unified log).
    /// Null = ikke indsamlet i denne runde (ældre agent, eller for kort tid siden). Serveren gemmer dem i sin egen tabel og
    /// fjerner dem fra helbredet. Begrænset af <see cref="SysLogPolicy"/>; hemmeligheder er fjernet på maskinen.
    /// </summary>
    public List<SysLogEvent>? SysLog { get; set; }
    public UpdateSummary? Updates { get; set; }
    public string? LastUpdate { get; set; }
    /// <summary>Linux: systemd-tjenester der er fejlet (system og den indloggede brugers, fx kiosk-*).</summary>
    public List<string> FailedServices { get; set; } = [];
    /// <summary>Kun Unraid-serveren: array, diske, containere og VM'er.</summary>
    public UnraidInfo? Unraid { get; set; }
    /// <summary>Kun macOS: Fuld diskadgang (TCC) og agentens signatur. Null = ikke Mac.</summary>
    public MacAccessInfo? MacAccess { get; set; }
    /// <summary>macOS: Fleetdeck.app og menulinjen hos brugeren på skærmen (null = ikke en Mac / ældre agent).</summary>
    public MacMenuInfo? MacMenu { get; set; }
}

/// <summary>macOS: er Fleetdeck.app og menulinjen lagt på, og kører ikonet hos brugeren på skærmen?</summary>
public sealed class MacMenuInfo
{
    public string? AppVersion { get; set; }
    /// <summary>Den native menu-app (FleetdeckMenu.app med det rigtige stempel) er lagt på.</summary>
    public bool NativeMenu { get; set; }
    /// <summary>LaunchAgent'en (/Library/LaunchAgents/com.fleetdeck.menubar.plist) findes – ikonet starter ved login for alle brugere.</summary>
    public bool LoginItem { get; set; }
    /// <summary>Brugeren på skærmen (null = ingen logget ind).</summary>
    public string? User { get; set; }
    /// <summary>Kører ikonet hos <see cref="User"/>? Null = ikke tjekket (ingen logget ind).</summary>
    public bool? Running { get; set; }
    /// <summary>Hvad agenten sidst gjorde (startet, genstartet, skjult af brugeren …).</summary>
    public string? Detail { get; set; }
}

/// <summary>macOS: har agenten Fuld diskadgang, og overlever den opdateringer (fast lokal signatur frem for ad hoc)?</summary>
public sealed class MacAccessInfo
{
    /// <summary>Null = kunne ikke afgøres.</summary>
    public bool? FullDiskAccess { get; set; }
    /// <summary>Mac'en tager iPad-backup (cfgutil eller en MobileSync-backupmappe findes) – så er adgangen nødvendig.</summary>
    public bool NeededForIpadBackup { get; set; }
    public bool StableSignature { get; set; }
    /// <summary>Fx "fast lokal signatur (Fleetdeck Agent (local))" eller "ad hoc".</summary>
    public string? Signature { get; set; }
    public string Path { get; set; } = "";
}

public sealed class DiskHealth
{
    public string Name { get; set; } = "";
    public string? Media { get; set; }
    public string? Bus { get; set; }
    public double? SizeGb { get; set; }
    public string? Health { get; set; }
    public string? Status { get; set; }
    public double? Wear { get; set; }
    public double? Temp { get; set; }
    public long? ReadErrors { get; set; }
    public long? PowerOnHours { get; set; }
    // ---- diskhelbred (3.40.0): se DiskSmart. Null = ukendt (ældre agent, eller værktøjet giver det ikke)
    /// <summary>Kort id for disken (de første 12 hex-tegn af SHA-256 af serienummeret) – selve serienummeret sendes aldrig.</summary>
    public string? Id { get; set; }
    /// <summary>NVMe, SSD eller HDD (<see cref="DiskKinds"/>).</summary>
    public string? Kind { get; set; }
    /// <summary>Samlet vurdering: ok, warning eller failing (<see cref="DiskStates"/>). Sættes af serveren ud fra tallene.</summary>
    public string? State { get; set; }
    /// <summary>Resterende levetid i procent (SSD: 100 − slid, eller producentens egen værdi).</summary>
    public double? LifeLeft { get; set; }
    /// <summary>ATA: reallokerede sektorer (SMART 5). NVMe/Windows: ukendt.</summary>
    public long? Reallocated { get; set; }
    /// <summary>ATA: sektorer, der venter på at blive flyttet (SMART 197), plus ikke-rettelige (198).</summary>
    public long? Pending { get; set; }
    /// <summary>NVMe: mediefejl. Windows: ikke-rettede læse- og skrivefejl.</summary>
    public long? MediaErrors { get; set; }
    /// <summary>NVMe: "critical warning"-bits (0 = alt i orden).</summary>
    public int? CriticalWarning { get; set; }
    /// <summary>NVMe: reserveblokke tilbage i procent.</summary>
    public double? Spare { get; set; }
    public double? TempMax { get; set; }
    /// <summary>Hvor tallene kommer fra: smartctl, wmi, diskutil eller sysfs.</summary>
    public string? Source { get; set; }
    /// <summary>Disken sov (standby) og blev ikke vækket – serveren beholder de sidst kendte SMART-tal.</summary>
    public bool Standby { get; set; }
    /// <summary>Serveren: det første antal reallokerede sektorer, Fleetdeck så, og hvornår tallet sidst steg.</summary>
    public long? ReallocatedFirst { get; set; }
    public DateTime? ReallocatedRoseAt { get; set; }
    public DateTime? MediaErrorsRoseAt { get; set; }
}

public sealed class DefenderStatus
{
    public bool? Enabled { get; set; }
    public bool? Realtime { get; set; }
    public string? Mode { get; set; }
    public int? SigAgeDays { get; set; }
    public int? QuickScanAgeDays { get; set; }
    public bool? Tamper { get; set; }
    public int ThreatsLast30Days { get; set; }
    public string? Version { get; set; }
}

public sealed class AntivirusProduct
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    public bool UpToDate { get; set; }
}

public sealed class EventSummary
{
    public int? Crashes7d { get; set; }
    public int? Errors24h { get; set; }
    public string? LastCrash { get; set; }
}

public sealed class UpdateSummary
{
    public int? Pending { get; set; }
    public List<string> Titles { get; set; } = [];
    public string? Error { get; set; }
}

public sealed class HealthPost
{
    public HealthReport Health { get; set; } = new();
}

// ------------------------------------------------------------------ inventar
public sealed class InventoryReport
{
    public SystemFacts Facts { get; set; } = new();
    public HealthReport Health { get; set; } = new();
    public List<SoftwareEntry> Software { get; set; } = [];
    public List<string> WingetIds { get; set; } = [];
    public List<UpgradeEntry> Upgrades { get; set; } = [];
    /// <summary>Installerede drivere (kun Windows, uden Microsofts egne). Null = ikke indsamlet (ældre agent eller andet system).</summary>
    public List<DriverEntry>? Drivers { get; set; }
}

public sealed class SoftwareEntry
{
    public string Name { get; set; } = "";
    public string? Version { get; set; }
    public string? Publisher { get; set; }
    public string? Scope { get; set; }
    /// <summary>Kort beskrivelse (højst 200 tegn), fx fra pakkens resumé eller programmets .desktop-fil.</summary>
    public string? Description { get; set; }
    /// <summary>Mappen, programmet er installeret i.</summary>
    public string? InstallPath { get; set; }
    /// <summary>Selve programfilen (fx C:\…\firefox.exe, /usr/bin/firefox, /Applications/Firefox.app).</summary>
    public string? Exe { get; set; }
    /// <summary>
    /// Hvordan programmet afinstalleres fra Fleetdeck (job "remove_app"): Windows "HKLM|64|&lt;nøgle&gt;" / "HKLM|32|&lt;nøgle&gt;" /
    /// "HKU|&lt;sid&gt;|&lt;nøgle&gt;" i Uninstall-registreringen, "store|&lt;PackageFullName&gt;" for Store-apps, "steam|&lt;appid&gt;";
    /// Linux/Mac: tom (pakkenavnet + Scope bruges), Mac-apps: stien til .app.
    /// </summary>
    public string? UninstallId { get; set; }
    /// <summary>Ikonets id: de første 32 hex-tegn af SHA-256 af PNG-filen. Selve billedet sendes kun, hvis serveren mangler det.</summary>
    public string? IconHash { get; set; }
}

/// <summary>Programikoner: agenten spørger, hvilke serveren mangler, og sender kun dem (PNG, højst 64×64).</summary>
public sealed class IconHashesRequest { public List<string> Hashes { get; set; } = []; }
public sealed class IconHashesResponse { public List<string> Missing { get; set; } = []; }
public sealed class IconUpload { public string Hash { get; set; } = ""; public string Png { get; set; } = ""; }   // Png: base64
public sealed class IconUploadRequest { public List<IconUpload> Icons { get; set; } = []; }

public static class SoftwareIcons
{
    public const int MaxBytes = 24 * 1024, PerRequest = 50, MaxPerRun = 400;
    public static string HashOf(byte[] png) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(png))[..32];
    public static string? Clip(string? s, int max = 200) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length <= max ? s.Trim() : s.Trim()[..(max - 1)] + "…";
}

public sealed class UpgradeEntry
{
    public string Name { get; set; } = "";
    public string Id { get; set; } = "";
    public string? Current { get; set; }
    public string? Available { get; set; }
    /// <summary>En driver (Windows Update: Type = Driver). "Opdatér alt" installerer ikke drivere fra Windows Update.</summary>
    public bool Driver { get; set; }
    /// <summary>Hvorfor den ikke anbefales (fx "Driveren er fra 2015 – over 3 år gammel"), ellers null.</summary>
    public string? Advice { get; set; }
    /// <summary>Skjult i Windows Update (IsHidden): tilbydes ikke og tæller ikke med, men kan vises igen.</summary>
    public bool Hidden { get; set; }
    /// <summary>Firmware, der er selve systemets BIOS/UEFI (fwupd: UEFI-kapsel, flashrom, Intel ME …) – flashes aldrig automatisk.</summary>
    public bool Bios { get; set; }
}

public sealed class InventoryResponse
{
    public int JobsCreated { get; set; }
}

/// <summary>Hub-metoder (SignalR) som serveren kalder på agenten.</summary>
public static class AgentHubMethods
{
    public const string Wake = "Wake";
}

/// <summary>Hub-metoder som serveren kalder på browseren.</summary>
public static class UiHubMethods
{
    public const string Changed = "Changed";
    public const string Progress = "Progress";   // { jobId, machineId, progress, step } – kun fremdrift, intet skal hentes igen
}


/// <summary>Intel AMT på maskinen (fra Intels rpc-værktøj: rpc amtinfo).</summary>
public sealed class AmtInfo
{
    public bool Present { get; set; }
    public string? Version { get; set; }
    public string? Mode { get; set; }            // "pre-provisioning state" | "activated in client control mode" | "activated in admin control mode"
    public string? Uuid { get; set; }
    public string? AmtIp { get; set; }
    public bool LinkUp { get; set; }
    public string? Error { get; set; }
    public DateTime Checked { get; set; } = DateTime.UtcNow;
    public bool Activated => Mode is { } m && m.Contains("activated", StringComparison.OrdinalIgnoreCase);
}

public sealed class AutostartInfo
{
    public bool Ok { get; set; }
    public string Detail { get; set; } = "";
    public string? Fixed { get; set; }      // hvad agenten selv rettede denne gang
}

public sealed class KeptService
{
    public string Name { get; set; } = "";
    public bool Active { get; set; }
    public string State { get; set; } = "";          // active | failed | inactive | ukendt
    public int Restarts24h { get; set; }
    public DateTime? LastRestart { get; set; }
    public string? LastError { get; set; }
    /// <summary>Bevidst sprunget over: enheden har en betingelse (Condition…/ExecCondition), der ikke er opfyldt på maskinen
    /// – fx en NFS-watchdog på en maskine, der starter fra SD-kort. Ingen fejl; agenten prøver kun igen hver halve time.</summary>
    public bool Skipped { get; set; }

    /// <summary>systemd's svar betyder "sprunget over pga. betingelse": inaktiv (ikke fejlet) og ConditionResult=no.</summary>
    public static bool SkippedByCondition(string isActive, string conditionResult) =>
        isActive.Trim() == "inactive" && conditionResult.Trim() == "no";

    /// <summary>Skal der slås alarm? Kun når tjenesten hverken kører eller bevidst er sprunget over.</summary>
    public static bool NeedsAlert(KeptService k) => !k.Active && !k.Skipped;
}
