using System.Text.RegularExpressions;

namespace Fleetdeck.Server.Services;

/// <summary>Hvordan et styresystem installeres af sig selv (hvilken svarfil Fleetdeck laver).</summary>
public static class AnswerMethod
{
    public const string Autounattend = "autounattend", Autoinstall = "autoinstall", Preseed = "preseed", Kickstart = "kickstart", Proxmox = "proxmox";
}

/// <summary>Hvor et styresystem er i Fleetdeck: kan vælges, kommer senere (ikke afprøvet i en VM endnu) eller er udeladt.</summary>
public static class OsAvailability { public const string Ready = "ready", Later = "later", Omitted = "omitted"; }

/// <summary>Felterne i guiden, som kan stå tomme. Hvad der sker, når et felt er tomt, afhænger af styresystemet (<see cref="BlankRule"/>).</summary>
public static class DeployField
{
    public const string Name = "name", User = "user", Password = "password", Language = "language", Keyboard = "keyboard", TimeZone = "timezone", Disk = "disk";
    public static readonly string[] All = [Name, User, Password, Language, Keyboard, TimeZone, Disk];
}

/// <summary>Et tomt felt: "ask" = spørges på maskinen, "auto" = vælges af installationen selv, "required" = skal udfyldes, "na" = bruges ikke.</summary>
public sealed record BlankRule(string Kind, string Text)
{
    public const string Ask = "ask", Auto = "auto", Required = "required", Na = "na";
    public bool MayBeBlank => Kind is Ask or Auto or Na;
}

/// <summary>Kilden til et Linux-netboot: kerne og initrd hentes og kontrolleres mod SHA-256.</summary>
/// <param name="Checksums">"treeinfo" (.treeinfo i installationstræet), "sha256sums" (SHA256SUMS i <paramref name="SumsPath"/>) eller "" (ingen netboot).</param>
public sealed record NetbootSource(string TreeUrl, string Kernel, string Initrd, string Checksums, string SumsPath = "", string? AppStream = null);

/// <summary>
/// Et styresystem i Udrulning. Familie → version → variant (skrivebord/server).
/// <see cref="Availability"/> er kun <see cref="OsAvailability.Ready"/>, når den automatiske installation er afprøvet til ende i en VM.
/// </summary>
public sealed record OsEntry(
    string Id, string Family, string FamilyTitle, string Version, string Title, string[] Variants, string Method, string Availability,
    string? Note = null, string? Warning = null, string? Series = null, string? WinDir = null, string? WinKey = null,
    NetbootSource? Netboot = null, bool CanEncrypt = true)
{
    public bool IsWindows => Method == AnswerMethod.Autounattend;
    public bool IsWindowsServer => IsWindows && Family == "windows-server";

    /// <summary>Hvad der sker, når et felt står tomt i guiden.</summary>
    public BlankRule Blank(string field) => (Method, field) switch
    {
        (AnswerMethod.Proxmox, DeployField.User) => new(BlankRule.Na, "Proxmox har kun root-brugeren"),
        (AnswerMethod.Proxmox, DeployField.Disk) => new(BlankRule.Required, "Proxmox bruger altid den første disk"),
        (AnswerMethod.Proxmox, _) => new(BlankRule.Required, "Proxmox kan ikke spørge under installationen – feltet skal udfyldes"),
        (AnswerMethod.Autounattend, DeployField.Name) => new(BlankRule.Auto, "Windows giver pc'en et tilfældigt navn (kan omdøbes i Fleetdeck bagefter)"),
        (AnswerMethod.Autounattend, DeployField.User) when IsWindowsServer => new(BlankRule.Ask, "Kun Administrator – adgangskoden vælges på maskinen"),
        (AnswerMethod.Autounattend, DeployField.User) => new(BlankRule.Ask, "Windows spørger efter en bruger på maskinen"),
        (AnswerMethod.Autounattend, DeployField.Password) when IsWindowsServer => new(BlankRule.Na, "Administrator-adgangskoden vælges på maskinen"),
        (AnswerMethod.Autounattend, DeployField.Password) => new(BlankRule.Na, "Adgangskoden vælges ved første login"),
        (AnswerMethod.Autounattend, DeployField.TimeZone) => new(BlankRule.Auto, "Windows sætter selv tidszonen"),
        (AnswerMethod.Autoinstall, DeployField.TimeZone) => new(BlankRule.Auto, "Ubuntu finder tidszonen ud fra netværket"),
        _ => new(BlankRule.Ask, "Spørges på maskinen under installationen"),
    };
}

/// <summary>Alle styresystemer i Udrulning. Kun dem med <see cref="OsAvailability.Ready"/> kan vælges.</summary>
public static partial class OsCatalog
{
    private const string Untested = "Kommer senere: den automatiske installation er bygget, men ikke afprøvet til ende i en VM endnu.";
    private const string NoSecureBoot = "Secure Boot skal være slået fra (Fleetdecks netværksboot bruger Ubuntus GRUB).";
    private static readonly string[] DesktopServer = ["desktop", "server"];
    private static readonly string[] ServerOnly = ["server"];

    private static NetbootSource Debian(string codename) => new(
        $"https://deb.debian.org/debian/dists/{codename}/main/installer-amd64/current/images/",
        "netboot/debian-installer/amd64/linux", "netboot/debian-installer/amd64/initrd.gz", "sha256sums", "SHA256SUMS");
    private static NetbootSource Rhel(string tree, string? appStream) =>
        new(tree, "images/pxeboot/vmlinuz", "images/pxeboot/initrd.img", "treeinfo", AppStream: appStream);

    public static readonly OsEntry[] All =
    [
        // Windows: WinPE-menuen + autounattend.xml. Microsoft har intet stabilt direkte ISO-link: indholdet af ens egen ISO lægges på sharet.
        new("windows-11", "windows", "Windows", "11", "Windows 11", ["desktop"], AnswerMethod.Autounattend, OsAvailability.Ready,
            WinDir: "win11", WinKey: "VK7JG-NPHTM-C97JM-9MPGT-3V66T"),
        new("windows-10", "windows", "Windows", "10 (22H2)", "Windows 10 (22H2)", ["desktop"], AnswerMethod.Autounattend, OsAvailability.Later,
            Note: Untested, Warning: "Windows 10 får ingen sikkerhedsopdateringer efter oktober 2025 uden ESU (udvidet support).",
            WinDir: "win10", WinKey: "VK7JG-NPHTM-C97JM-9MPGT-3V66T"),
        new("windows-server-2025", "windows-server", "Windows Server", "2025", "Windows Server 2025", DesktopServer, AnswerMethod.Autounattend, OsAvailability.Later,
            Note: Untested + " Skrivebord = Desktop Experience, server = Server Core (Standard-udgaven).", WinDir: "ws2025", CanEncrypt: false),

        // Ubuntu: autoinstall (cloud-init NoCloud) – afprøvet
        new("ubuntu-26.04", "ubuntu", "Ubuntu", "26.04 LTS", "Ubuntu 26.04 LTS", DesktopServer, AnswerMethod.Autoinstall, OsAvailability.Ready, Series: "26.04"),
        new("ubuntu-24.04", "ubuntu", "Ubuntu", "24.04 LTS", "Ubuntu 24.04 LTS", DesktopServer, AnswerMethod.Autoinstall, OsAvailability.Ready, Series: "24.04"),

        // Debian: netboot + preseed
        new("debian-13", "debian", "Debian", "13 Trixie", "Debian 13 Trixie", DesktopServer, AnswerMethod.Preseed, OsAvailability.Later,
            Note: Untested + " " + NoSecureBoot, Series: "trixie", Netboot: Debian("trixie")),
        new("debian-12", "debian", "Debian", "12 Bookworm", "Debian 12 Bookworm", DesktopServer, AnswerMethod.Preseed, OsAvailability.Later,
            Note: Untested + " " + NoSecureBoot, Series: "bookworm", Netboot: Debian("bookworm")),

        // Fedora (seneste udgave findes, når den hentes) – kickstart
        new("fedora", "fedora", "Fedora", "seneste", "Fedora", ["desktop", "server"], AnswerMethod.Kickstart, OsAvailability.Later,
            Note: Untested + " Skrivebord = Workstation, server = Fedora Server. " + NoSecureBoot,
            Netboot: Rhel("https://dl.fedoraproject.org/pub/fedora/linux/releases/{version}/Everything/x86_64/os/", null)),

        // Rocky Linux og AlmaLinux (servere) – kickstart
        new("rocky-10", "rocky", "Rocky Linux", "10", "Rocky Linux 10", ServerOnly, AnswerMethod.Kickstart, OsAvailability.Later, Note: Untested + " " + NoSecureBoot,
            Series: "10", Netboot: Rhel("https://download.rockylinux.org/pub/rocky/10/BaseOS/x86_64/os/", "https://download.rockylinux.org/pub/rocky/10/AppStream/x86_64/os/")),
        new("rocky-9", "rocky", "Rocky Linux", "9", "Rocky Linux 9", ServerOnly, AnswerMethod.Kickstart, OsAvailability.Later, Note: Untested + " " + NoSecureBoot,
            Series: "9", Netboot: Rhel("https://download.rockylinux.org/pub/rocky/9/BaseOS/x86_64/os/", "https://download.rockylinux.org/pub/rocky/9/AppStream/x86_64/os/")),
        new("alma-10", "alma", "AlmaLinux", "10", "AlmaLinux 10", ServerOnly, AnswerMethod.Kickstart, OsAvailability.Later, Note: Untested + " " + NoSecureBoot,
            Series: "10", Netboot: Rhel("https://repo.almalinux.org/almalinux/10/BaseOS/x86_64/os/", "https://repo.almalinux.org/almalinux/10/AppStream/x86_64/os/")),
        new("alma-9", "alma", "AlmaLinux", "9", "AlmaLinux 9", ServerOnly, AnswerMethod.Kickstart, OsAvailability.Later, Note: Untested + " " + NoSecureBoot,
            Series: "9", Netboot: Rhel("https://repo.almalinux.org/almalinux/9/BaseOS/x86_64/os/", "https://repo.almalinux.org/almalinux/9/AppStream/x86_64/os/")),

        // Proxmox VE: answer-fil (proxmox-auto-install-assistant). Proxmox kan ikke netværksboote officielt.
        new("proxmox-ve", "proxmox", "Proxmox VE", "seneste", "Proxmox VE", ServerOnly, AnswerMethod.Proxmox, OsAvailability.Later,
            Note: "Kommer senere: svarfilen (answer.toml) laves, men Proxmox kan ikke installeres over netværket officielt. Den skal bruge en ISO, der er gjort klar med proxmox-auto-install-assistant og startes fra USB eller et virtuelt cd-drev.",
            CanEncrypt: false),

        // Linux Mint: udeladt
        new("linux-mint", "mint", "Linux Mint", "seneste", "Linux Mint", ["desktop"], AnswerMethod.Preseed, OsAvailability.Omitted,
            Note: "Udeladt: Mints installationsprogram (Ubiquity) kan ikke køre helt af sig selv på en stabil måde, og Mint har intet netboot. Brug Ubuntu Desktop i stedet."),
    ];

    public static OsEntry? Get(string? id) => All.FirstOrDefault(e => e.Id == id);

    /// <summary>Den nyeste Fedora-udgave i en mappeoversigt fra dl.fedoraproject.org/pub/fedora/linux/releases/ (fx "43/").</summary>
    public static int? LatestFedora(string listing)
    {
        var nums = FedoraDir().Matches(listing).Select(m => int.Parse(m.Groups[1].Value)).Where(n => n is >= 30 and < 1000).ToList();
        return nums.Count > 0 ? nums.Max() : null;
    }
    [GeneratedRegex("href=\"([0-9]{2,3})/\"")] private static partial Regex FedoraDir();

    /// <summary>SHA-256 for en fil i .treeinfo ([checksums]: "images/pxeboot/vmlinuz = sha256:abc…").</summary>
    public static string? TreeinfoSha256(string treeinfo, string file)
    {
        var inSection = false;
        foreach (var raw in treeinfo.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { inSection = line == "[checksums]"; continue; }
            if (!inSection) continue;
            var eq = line.IndexOf('=');
            if (eq < 0 || line[..eq].Trim() != file) continue;
            var v = line[(eq + 1)..].Trim();
            return v.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && Sha256Hex().IsMatch(v[7..]) ? v[7..].ToLowerInvariant() : null;
        }
        return null;
    }

    /// <summary>SHA-256 for en fil i en SHA256SUMS-fil ("abc…  ./netboot/debian-installer/amd64/linux").</summary>
    public static string? SumsSha256(string sums, string file)
    {
        foreach (var raw in sums.Split('\n'))
        {
            var parts = raw.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !Sha256Hex().IsMatch(parts[0])) continue;
            var name = parts[1].TrimStart('*');
            if (name.StartsWith("./")) name = name[2..];
            if (name == file) return parts[0].ToLowerInvariant();
        }
        return null;
    }
    [GeneratedRegex("^[0-9a-fA-F]{64}$")] private static partial Regex Sha256Hex();
}
