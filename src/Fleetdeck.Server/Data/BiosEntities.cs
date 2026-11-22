using System.ComponentModel.DataAnnotations;

namespace Fleetdeck.Server.Data;

/// <summary>
/// BIOS-status for én maskine, beregnet af serveren (Services/Bios/BiosMatcher) ud fra agentens fakta og leverandørens katalog.
/// Ligger i sin egen tabel, fordi agentens inventar erstatter maskinens UpgradeItem-rækker hver gang.
/// </summary>
public class MachineBiosStatus
{
    [Key] public int MachineId { get; set; }
    /// <summary>lenovo, dell, hp eller none.</summary>
    [MaxLength(16)] public string Vendor { get; set; } = "none";
    /// <summary>Lenovo: billedpræfiks + maskintype ("m1uk/10T7"); Dell: SKU; HP: board-id.</summary>
    [MaxLength(64)] public string? CatalogKey { get; set; }
    /// <summary>exact, fallback eller uncertain (null, når der ikke er matchet mod et katalog).</summary>
    [MaxLength(16)] public string? MatchQuality { get; set; }
    [MaxLength(64)] public string? Current { get; set; }
    [MaxLength(32)] public string? CurrentNumeric { get; set; }
    [MaxLength(16)] public string? CurrentDate { get; set; }
    [MaxLength(64)] public string? Latest { get; set; }
    [MaxLength(32)] public string? LatestNumeric { get; set; }
    [MaxLength(16)] public string? LatestDate { get; set; }
    /// <summary>Se <see cref="BiosStates"/>.</summary>
    [MaxLength(16)] public string State { get; set; } = BiosStates.Unchecked;
    /// <summary>Leverandøren markerer opdateringen som kritisk.</summary>
    public bool Critical { get; set; }
    /// <summary>Korte ændringslinjer fra leverandørens readme for versionerne efter den nuværende (JSON: [{version, wu, lines:[{text, important}]}]).</summary>
    public string ChangeSummaryJson { get; set; } = "[]";
    /// <summary>Forklaring til brugeren (fx hvorfor matchet er usikkert).</summary>
    [MaxLength(300)] public string? Detail { get; set; }
    /// <summary>fwupd/LVFS tilbyder en opdatering af "System Firmware" (versionen), ellers null.</summary>
    [MaxLength(64)] public string? FwupdVersion { get; set; }
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Kilden, fx "lenovo-catalog", "fwupd" eller "facts".</summary>
    [MaxLength(32)] public string Source { get; set; } = "facts";
}

public static class BiosStates
{
    public const string Newest = "newest", Newer = "newer", Uncertain = "uncertain", Unsupported = "unsupported", Unchecked = "unchecked",
        CatalogError = "catalog_error", Virtual = "virtual", Mac = "mac", Pi = "pi", Fwupd = "fwupd";
}

/// <summary>En BIOS-pakke i serverens cache: data/bios/&lt;vendor&gt;/&lt;key&gt;/&lt;version&gt;/&lt;fil&gt;. Hentes kun, når en administrator beder om det.</summary>
public class BiosPackage
{
    public int Id { get; set; }
    [MaxLength(16)] public string Vendor { get; set; } = "";
    [MaxLength(64)] public string CatalogKey { get; set; } = "";
    [MaxLength(64)] public string Version { get; set; } = "";
    /// <summary>usb-zip, win-exe, bin eller readme.</summary>
    [MaxLength(16)] public string Kind { get; set; } = "";
    [MaxLength(400)] public string SourceUrl { get; set; } = "";
    [MaxLength(200)] public string FileName { get; set; } = "";
    public long Size { get; set; }
    [MaxLength(64)] public string? Sha256 { get; set; }
    /// <summary>catalog (fra leverandøren) eller tofu (låst ved første hentning, fordi kataloget ingen kontrolsum har).</summary>
    [MaxLength(16)] public string ShaSource { get; set; } = "catalog";
    /// <summary>Stien under data-mappen.</summary>
    [MaxLength(400)] public string Path { get; set; } = "";
    /// <summary>downloading, ready eller failed.</summary>
    [MaxLength(16)] public string State { get; set; } = "downloading";
    [MaxLength(300)] public string? Error { get; set; }
    [MaxLength(100)] public string RequestedBy { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public DateTime? DownloadedAt { get; set; }
    public DateTime? LastVerifiedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    /// <summary>Kun usb-zip: indeholder pakken en rigtig UEFI-kapsel (true) eller kun en rå ROM til USB-værktøjet (false)? Null = ikke set endnu.</summary>
    public bool? Capsule { get; set; }
}

/// <summary>Et snapshot af BIOS-indstillingerne (fase 3). Passwords gemmes aldrig.</summary>
public class BiosSettingsSnapshot
{
    public int Id { get; set; }
    public int MachineId { get; set; }
    public DateTime TakenAt { get; set; } = DateTime.UtcNow;
    /// <summary>manual, pre-update, post-boot, pre-restore, post-restore.</summary>
    [MaxLength(16)] public string Reason { get; set; } = "manual";
    /// <summary>thinklmi, lenovo-wmi, dell-wmi-sysman, dell-wmi, hp-bioscfg, hp-wmi.</summary>
    [MaxLength(24)] public string Driver { get; set; } = "";
    [MaxLength(64)] public string? BiosVersion { get; set; }
    [MaxLength(32)] public string? EsrtVersion { get; set; }
    public bool? SecureBoot { get; set; }
    [MaxLength(32)] public string? TpmState { get; set; }
    [MaxLength(32)] public string? DiskEncryption { get; set; }
    /// <summary>[{name, display, type, value, possible[], min, max, rule}]</summary>
    public string ItemsJson { get; set; } = "[]";
    /// <summary>SHA-256 over ItemsJson, så ændringer kan opdages.</summary>
    [MaxLength(64)] public string Sha256 { get; set; } = "";
    [MaxLength(100)] public string By { get; set; } = "";
    /// <summary>Modellen (Lenovo: modelnavnet), så snapshots kan sammenlignes på model og BIOS-familie.</summary>
    [MaxLength(64)] public string? Model { get; set; }
    public int Count { get; set; }
    public bool AdminPassword { get; set; }
    /// <summary>Der ventede BIOS-ændringer, da snapshottet blev taget (null = driveren kan ikke sige det).</summary>
    public bool? PendingReboot { get; set; }
    /// <summary>Jobbet, der tog snapshottet.</summary>
    public int? JobId { get; set; }
}

/// <summary>
/// En BIOS-opdatering inde fra styresystemet (bios_update), fra tjek til resultatet efter genstarten. Tilstanden ligger her (og på agenten),
/// så den overlever, at agenten genstartes, og at maskinen genstarter midt i forløbet. Se Services/Bios/BiosUpdateFlow.
/// </summary>
public class BiosUpdateRun
{
    public int Id { get; set; }
    public int MachineId { get; set; }
    [MaxLength(100)] public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>capsule (Linux, fwupd) eller lenovo-exe (Windows).</summary>
    [MaxLength(16)] public string Route { get; set; } = "";
    [MaxLength(16)] public string Vendor { get; set; } = "";
    [MaxLength(16)] public string Family { get; set; } = "";
    [MaxLength(64)] public string? FromVersion { get; set; }
    [MaxLength(64)] public string TargetVersion { get; set; } = "";
    [MaxLength(32)] public string? TargetNumeric { get; set; }
    public int? PackageId { get; set; }
    /// <summary>Se <see cref="BiosUpdateStates"/>.</summary>
    [MaxLength(16)] public string State { get; set; } = BiosUpdateStates.Checking;
    /// <summary>Det seneste bios_update-job (tjek, klargøring eller kontrol).</summary>
    public int? JobId { get; set; }
    /// <summary>Kopien af indstillingerne, agenten sendte som første trin.</summary>
    public int? PreSnapshotId { get; set; }
    /// <summary>Forudsætningerne fra agenten: [{id, state, text}].</summary>
    public string ChecksJson { get; set; } = "[]";
    /// <summary>Agentens seneste melding (BiosUpdateReport).</summary>
    public string? ReportJson { get; set; }
    [MaxLength(500)] public string? Message { get; set; }
    public DateTime? StagedAt { get; set; }
    public DateTime? RebootRequestedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}

public static class BiosUpdateStates
{
    public const string Checking = "checking", Checked = "checked", Blocked = "blocked", Staging = "staging", Staged = "staged",
        Updated = "updated", Rejected = "rejected", NotApplied = "not_applied", Unknown = "unknown", Failed = "failed";
    /// <summary>En kørsel i gang: der må kun være én pr. maskine.</summary>
    public static readonly string[] Active = [Checking, Staging, Staged];
}
