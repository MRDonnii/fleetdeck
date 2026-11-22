using System.ComponentModel.DataAnnotations;

namespace Fleetdeck.Server.Data;

/// <summary>
/// Reservekort på én Raspberry Pi (Services/SpareCards.cs): det valgte USB-kort, om det kopieres hver nat, og hvordan sidste kopi gik.
/// Ligger på maskinen og ikke i profilen, fordi kortlæserens by-id-navn kun findes på netop den Pi. Slået fra, til et kort er valgt og bekræftet.
/// </summary>
public class SpareCardState
{
    [Key] public int MachineId { get; set; }
    public Machine? Machine { get; set; }
    /// <summary>Kopiér hver nat (kun muligt, når et kort er bekræftet).</summary>
    public bool Enabled { get; set; }
    [MaxLength(5)] public string Time { get; set; } = "02:15";
    /// <summary>Hold netboot-nødsystemet friskt med samme kerne og moduler som Pi'en.</summary>
    public bool Netboot { get; set; } = true;
    /// <summary>Navnet i /dev/disk/by-id.</summary>
    [MaxLength(200)] public string DiskId { get; set; } = "";
    [MaxLength(120)] public string DiskLabel { get; set; } = "";
    [MaxLength(80)] public string Serial { get; set; } = "";
    public long Size { get; set; }
    public DateTime? ConfirmedAt { get; set; }
    [MaxLength(64)] public string ConfirmedBy { get; set; } = "";
    /// <summary>Kortets disk-id (PTUUID) efter klargøringen – så partitionstabellen kun kopieres én gang.</summary>
    [MaxLength(40)] public string? KnownDiskId { get; set; }
    public DateTime? LastRun { get; set; }
    public DateTime? LastOk { get; set; }
    /// <summary>"" | kører | ok | fejl</summary>
    [MaxLength(16)] public string Status { get; set; } = "";
    [MaxLength(400)] public string Message { get; set; } = "";
    [MaxLength(80)] public string? Kernel { get; set; }
    public long Bytes { get; set; }
    public int? JobId { get; set; }
    /// <summary>Netboot-nødsystemets kerne-signatur og status for sidste opdatering.</summary>
    [MaxLength(120)] public string? NetbootSignature { get; set; }
    [MaxLength(16)] public string NetbootStatus { get; set; } = "";
    [MaxLength(300)] public string NetbootMessage { get; set; } = "";
    public DateTime? NetbootAt { get; set; }
}
