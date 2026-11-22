using System.Text;
using System.Text.RegularExpressions;
using Fleetdeck.Server.Data;
using Fleetdeck.Shared;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Server.Services;

/// <summary>
/// Netboot som nødstart for en Raspberry Pi, kørt af Fleetdecks egen dnsmasq (proxy-DHCP og TFTP, deploy/docker/dnsmasq.conf):
/// kun Pi'er med en fil i PXE/pi/hosts får svar, så en opstartsserver aldrig rammer andre maskiner. Pi'ens opstartsfiler ligger i
/// PXE/tftp/&lt;serienummer&gt;/ (Pi'ens bootloader leder selv efter den mappe), og bootcode.bin ligger i roden, hvor en Pi 3 henter den.
/// Rodfilsystemet over NFS deles af værten (en container kan ikke dele NFS uden ekstra rettigheder) – Fleetdeck viser kun, hvor det er.
/// </summary>
public sealed partial class PiNetboot(Paths paths, AppDb db, AuditService audit)
{
    public string Tftp => Path.Combine(paths.Pxe, "tftp");
    public string HostsDir => Path.Combine(paths.Pxe, "pi", "hosts");
    public string SerialDir(string serial) => Path.Combine(Tftp, serial);
    private string HostsFile(string mac) => Path.Combine(HostsDir, MacAddr.Dash(MacAddr.Normalize(mac) ?? mac));

    [GeneratedRegex("^[0-9a-f]{8}$")] public static partial Regex Serial();
    [GeneratedRegex(@"\bnfsroot=([^ ,]+)")] private static partial Regex NfsRoot();
    [GeneratedRegex(@"\broot=(\S+)")] private static partial Regex Root();

    public async Task<object?> Status(int machineId)
    {
        var m = await db.Machines.AsNoTracking().FirstOrDefaultAsync(x => x.Id == machineId);
        if (m is null) return null;
        var facts = Json.Des<SystemFacts>(m.FactsJson);
        var serial = facts.PiSerial is { } s && Serial().IsMatch(s) ? s : null;
        var known = Directory.Exists(Tftp) ? Directory.GetDirectories(Tftp).Select(Path.GetFileName).Where(n => n is not null && Serial().IsMatch(n)).Cast<string>().ToList() : [];
        serial ??= known.Count == 1 ? known[0] : null;
        var dir = serial is null ? null : SerialDir(serial);
        string? cmdline = dir is not null && File.Exists(Path.Combine(dir, "cmdline.txt")) ? (await File.ReadAllTextAsync(Path.Combine(dir, "cmdline.txt"))).Trim() : null;
        var files = dir is not null && Directory.Exists(dir) ? Directory.GetFiles(dir).Length : 0;
        return new
        {
            machine = new { m.Id, name = m.Label, mac = m.Mac, model = facts.Model },
            enabled = MacAddr.Normalize(m.Mac) is { } mac && File.Exists(HostsFile(mac)),
            serial, serialFromAgent = facts.PiSerial is not null, knownSerials = known,
            files, kernel = dir is not null && Directory.GetFiles(dir, "kernel*.img").Length > 0, startElf = dir is not null && File.Exists(Path.Combine(dir, "start.elf")),
            bootcode = File.Exists(Path.Combine(Tftp, "bootcode.bin")) || (dir is not null && File.Exists(Path.Combine(dir, "bootcode.bin"))),
            bootcodeAtRoot = File.Exists(Path.Combine(Tftp, "bootcode.bin")),
            cmdline, root = cmdline is null ? null : Root().Match(cmdline).Groups[1].Value, nfsRoot = cmdline is null ? null : NfsRoot().Match(cmdline) is { Success: true } n ? n.Groups[1].Value : null,
            folder = serial is null ? null : $"PXE/tftp/{serial}",
            pxe = Environment.GetEnvironmentVariable("PXE") != "0",
        };
    }

    /// <summary>Slå netboot til/fra for Pi'en. Til: dnsmasq svarer netop dens MAC-adresse; bootcode.bin lægges i roden, hvis den mangler.</summary>
    public async Task<string?> Set(int machineId, bool on, string? serial, string by)
    {
        var m = await db.Machines.FirstOrDefaultAsync(x => x.Id == machineId);
        if (m is null) return "Maskinen findes ikke";
        if (MacAddr.Normalize(m.Mac) is not { } mac) return "Fleetdeck kender ikke Pi'ens MAC-adresse endnu";
        if (!on)
        {
            try { File.Delete(HostsFile(mac)); } catch (IOException) { }
            await audit.Log(by, "netboot.off", m.Label, m.Id);
            return null;
        }
        var facts = Json.Des<SystemFacts>(m.FactsJson);
        serial = (serial ?? facts.PiSerial ?? "").Trim().ToLowerInvariant();
        if (!Serial().IsMatch(serial)) return "Skriv Pi'ens serienummer (8 tegn, 0-9 og a-f)";
        var dir = SerialDir(serial);
        if (!File.Exists(Path.Combine(dir, "start.elf")) || Directory.GetFiles(dir, "kernel*.img").Length == 0)
            return $"Opstartsfilerne mangler i PXE/tftp/{serial}/ (start.elf, kernel*.img, *.dtb, config.txt og cmdline.txt). Læg dem der først.";
        if (!File.Exists(Path.Combine(dir, "cmdline.txt"))) return $"cmdline.txt mangler i PXE/tftp/{serial}/";
        var root = Path.Combine(Tftp, "bootcode.bin");
        if (!File.Exists(root) && File.Exists(Path.Combine(dir, "bootcode.bin"))) File.Copy(Path.Combine(dir, "bootcode.bin"), root);
        Directory.CreateDirectory(HostsDir);
        await File.WriteAllTextAsync(HostsFile(mac), $"{MacAddr.Colon(mac)},set:rpi\n", new UTF8Encoding(false));
        await audit.Log(by, "netboot.on", $"{m.Label} (serienummer {serial})", m.Id);
        return null;
    }
}
