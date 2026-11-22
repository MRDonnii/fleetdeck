namespace Fleetdeck.Shared;

/// <summary>Unraid-serveren: array og diske, Docker-containere og VM'er (sendes med helbred fra Unraid-agenten).</summary>
public sealed class UnraidInfo
{
    public string Version { get; set; } = "";
    public string ArrayState { get; set; } = "";
    public int? SyncErrors { get; set; }
    public DateTime? ParityLast { get; set; }
    public int? ParityProgress { get; set; }          // 0-100, når et paritetstjek kører
    public List<UnraidDisk> Disks { get; set; } = [];
    public List<UnraidContainer> Containers { get; set; } = [];
    public List<UnraidVm> Vms { get; set; } = [];
    /// <summary>Delte mapper (Shares) – til valg af netværksdrev.</summary>
    public List<UnraidShare> Shares { get; set; } = [];
    public DateTime Collected { get; set; }
}

public sealed class UnraidDisk
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";             // Parity | Data | Cache | Flash
    public string? Device { get; set; }
    public string Status { get; set; } = "";           // DISK_OK, DISK_NP (ingen disk) …
    public int? TempC { get; set; }
    public double? SizeGb { get; set; }
    public double? UsedGb { get; set; }
    public string? FsType { get; set; }
    public bool? Ssd { get; set; }
}

public sealed class UnraidContainer
{
    public string Name { get; set; } = "";
    /// <summary>Containerens fulde id – så Fleetdeck kan finde sin egen container (værtsnavnet i en container er id'ets start).</summary>
    public string? Id { get; set; }
    public string Image { get; set; } = "";
    public string State { get; set; } = "";            // running | exited | paused | restarting | created
    public DateTime? Started { get; set; }
    public string? Icon { get; set; }
    public string? WebUi { get; set; }
    public bool UpdateAvailable { get; set; }
    public bool Autostart { get; set; }
    public double? Cpu { get; set; }
    public double? MemMb { get; set; }
    public string? Network { get; set; }
    public string? Ip { get; set; }
}

public sealed class UnraidVm
{
    public string Name { get; set; } = "";
    public string State { get; set; } = "";            // running | shut off | paused
    public int? Vcpus { get; set; }
    public int? MemoryMb { get; set; }
    public bool Autostart { get; set; }
}

public sealed class UnraidShare
{
    public string Name { get; set; } = "";
    public string? Comment { get; set; }
    /// <summary>Delt via SMB (shareExport ≠ "-").</summary>
    public bool Smb { get; set; }
}
