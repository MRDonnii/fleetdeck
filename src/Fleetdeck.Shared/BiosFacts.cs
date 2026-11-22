using System.Globalization;
using System.Text.RegularExpressions;

namespace Fleetdeck.Shared;

/// <summary>
/// BIOS-fakta fra SMBIOS/DMI, fælles for agenterne, så de kan testes uden hardware. Linux læser /sys/class/dmi/id/* gennem en
/// indskydelig læsefunktion (en falsk rod i testene); Windows giver WMI-værdierne direkte.
/// </summary>
public static partial class BiosFacts
{
    public const string DmiRoot = "/sys/class/dmi/id";

    /// <summary>Udfyld BIOS-felterne fra DMI. <paramref name="read"/> får filnavnet (fx "bios_vendor") og giver "" hvis den mangler.</summary>
    public static void FromDmi(SystemFacts f, Func<string, string> read)
    {
        string? R(string name) => Clean(read(name));
        f.BiosVendor = R("bios_vendor");
        f.BiosVersion = R("bios_version");
        f.BiosDate = DmiDate(R("bios_date"));
        f.BiosRelease = R("bios_release");
        f.ProductName = R("product_name");
        f.ProductVersion = R("product_version");
        f.Sku = R("product_sku");
        f.BoardVendor = R("board_vendor");
        f.BoardName = R("board_name");
        f.IsVirtual = Virtual(R("sys_vendor"), f.ProductName, f.BiosVendor);
    }

    /// <summary>Læs DMI fra en mappe (standard /sys/class/dmi/id). Filer, der ikke kan læses (fx kun root), giver "".</summary>
    public static Func<string, string> DirReader(string root = DmiRoot) => name =>
    {
        try { return File.ReadAllText(Path.Combine(root, name)).Trim(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }
    };

    /// <summary>Windows: udfyld fra WMI-værdierne (Win32_BIOS, Win32_ComputerSystem, Win32_ComputerSystemProduct, Win32_BaseBoard).</summary>
    public static void FromWmi(SystemFacts f, string? biosVendor, string? smbiosVersion, DateTime? releaseDate, int? major, int? minor,
        string? model, string? productVersion, string? sku, string? boardVendor, string? boardName, string? sysVendor)
    {
        f.BiosVendor = Clean(biosVendor);
        f.BiosVersion = Clean(smbiosVersion);
        f.BiosDate = releaseDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        // 255.255 betyder "ikke angivet" i SMBIOS
        f.BiosRelease = major is >= 0 and < 255 && minor is >= 0 and < 255 ? $"{major}.{minor}" : null;
        f.ProductName = Clean(model);
        f.ProductVersion = Clean(productVersion);
        f.Sku = Clean(sku);
        f.BoardVendor = Clean(boardVendor);
        f.BoardName = Clean(boardName);
        f.IsVirtual = Virtual(sysVendor, f.ProductName, f.BiosVendor);
    }

    /// <summary>Tomme felter og leverandørernes pladsholdere ("To be filled by O.E.M.", "Default string", "None") bliver null.</summary>
    public static string? Clean(string? s)
    {
        s = s?.Trim().TrimEnd('\0').Trim();
        if (string.IsNullOrEmpty(s)) return null;
        return Placeholder().IsMatch(s) ? null : s;
    }

    /// <summary>DMI-datoen er MM/dd/yyyy; giv yyyy-MM-dd (eller null).</summary>
    public static string? DmiDate(string? s) =>
        DateTime.TryParseExact(s?.Trim(), ["MM/dd/yyyy", "M/d/yyyy", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;

    /// <summary>Kendte hypervisorer i sys_vendor/product_name/bios_vendor.</summary>
    public static bool Virtual(string? sysVendor, string? productName, string? biosVendor)
    {
        var all = $"{sysVendor}|{productName}|{biosVendor}";
        return Hypervisor().IsMatch(all);
    }

    [GeneratedRegex(@"^(to be filled by o\.?e\.?m\.?|default string|none|not applicable|system (product name|version|sku(number)?)|n/?a|0+|x+|\.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"QEMU|KVM|VMware|VirtualBox|innotek|Xen|Bochs|SeaBIOS|Parallels|BHYVE|Virtual Machine|OpenStack|Amazon EC2|Google Compute|EFI Development Kit II / OVMF", RegexOptions.IgnoreCase)]
    private static partial Regex Hypervisor();
}
