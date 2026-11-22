using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Fleetdeck.Shared;

// USB-stik med BIOS-opdateringen fra en Mac (samme guide og /_fd/-protokol som Linux, se BiosUsb.cs). Diskene læses med diskutil
// (-plist), serienummeret med ioreg. Kun eksterne, fysiske USB-diske, der kan skubbes ud, vises; den interne disk, startdisken,
// diske med noget monteret uden for /Volumes og APFS-containere med systemet vises slet ikke. Andre APFS-diske og Time Machine-diske
// vises, men kan ikke vælges. Disknumre (disk4) kan skifte på en Mac, så lige før sletningen skal nummer, størrelse, medienavn og
// serienummer passe med det, brugeren bekræftede – to gange: før og efter afmonteringen.

/// <summary>En partition fra diskutil list -plist.</summary>
public sealed record MacPart(string Id, string Content, string Volume, string MountPoint);

/// <summary>En hel disk fra diskutil list/info -plist (og serienummeret fra ioreg).</summary>
public sealed record MacDisk(string Id, long Size, string Media, string Vendor, string Serial, string Bus, bool Internal, bool OsInternal,
    bool Virtual, bool Removable, bool Ejectable, bool WholeDisk, string Content, string MountPoint, List<MacPart> Parts)
{
    public string Path => "/dev/" + Id;
}

/// <summary>Hvad en APFS-container på disken bruges til (diskutil apfs list -plist).</summary>
public enum MacApfsUse { Data, Backup, System }

/// <summary>Alt, der skal til for at bedømme diskene: eksterne fysiske diske, startdiskens container, APFS-brugen pr. hel disk, Time Machine-diske
/// og diske, hvor Time Machine-tjekket ikke kunne gennemføres (kan ikke vælges).</summary>
public sealed record MacScan(List<MacDisk> Disks, string BootWhole, Dictionary<string, MacApfsUse> Apfs, HashSet<string> TimeMachine, HashSet<string>? TmUnknown = null);

/// <summary>En lille, sikker læser af XML-plister (dict, array, string, integer, real, true/false, data, date). Ingen DTD hentes.</summary>
public static class MacPlist
{
    public static object? Parse(string xml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = 20_000_000 };
        using var r = XmlReader.Create(new StringReader(xml), settings);
        var doc = XDocument.Load(r);
        var root = doc.Root;
        if (root is null || root.Name.LocalName != "plist") return null;
        return root.Elements().FirstOrDefault() is { } first ? Value(first) : null;
    }

    private static object? Value(XElement e) => e.Name.LocalName switch
    {
        "dict" => Dict(e),
        "array" => e.Elements().Select(Value).ToList(),
        "string" or "data" or "date" => e.Value,
        "integer" => long.TryParse(e.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l
            : ulong.TryParse(e.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var u) ? (long)Math.Min(u, long.MaxValue) : 0L,
        "real" => double.TryParse(e.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0d,
        "true" => true,
        "false" => false,
        _ => null,
    };

    private static Dictionary<string, object?> Dict(XElement e)
    {
        var o = new Dictionary<string, object?>(StringComparer.Ordinal);
        string? key = null;
        foreach (var x in e.Elements())
        {
            if (x.Name.LocalName == "key") { key = x.Value; continue; }
            if (key is not null) o[key] = Value(x);
            key = null;
        }
        return o;
    }

    public static Dictionary<string, object?>? AsDict(object? o) => o as Dictionary<string, object?>;
    public static List<object?> List(Dictionary<string, object?>? d, string key) => d is not null && d.TryGetValue(key, out var v) && v is List<object?> l ? l : [];
    public static string Str(Dictionary<string, object?>? d, string key) => d is not null && d.TryGetValue(key, out var v) && v is string s ? s : "";
    public static long Long(Dictionary<string, object?>? d, string key) => d is not null && d.TryGetValue(key, out var v) && v is long l ? l : 0;
    public static bool Bool(Dictionary<string, object?>? d, string key) => d is not null && d.TryGetValue(key, out var v) && v is true;
    public static bool Has(Dictionary<string, object?>? d, string key) => d is not null && d.ContainsKey(key);
}

public static partial class BiosUsbMac
{
    public const string Diskutil = "/usr/sbin/diskutil";
    public const string NotRemovable = "Disken er ikke et flytbart USB-stik.";
    public const string ApfsDisk = "Disken indeholder APFS (fx en Mac-disk). Den kan ikke bruges her. Brug et andet stik.";
    public const string TimeMachineDisk = "Disken bruges til Time Machine og kan ikke bruges her.";
    public const string MacAccess = "macOS gav ikke Fleetdeck-agenten adgang til stikket. Giv fleetdeck-agent Fuld diskadgang i Systemindstillinger → "
        + "Anonymitet og sikkerhed → Fuld diskadgang, og prøv igen. Intet andet er ændret.";
    public const string TimeMachineUnknown = "Fleetdeck kunne ikke tjekke, om disken bruges til Time Machine, så den kan ikke vælges. Prøv igen.";
    public const string TooBigForMbr = "Disken er over 2 TB og kan ikke formateres som FAT32 med MBR. Brug et mindre stik.";
    public const string ListFailed = "Diskene kunne ikke læses (diskutil/ioreg). Prøv igen.";
    public const string Tmutil = "/usr/bin/tmutil";

    /// <summary>MBR kan højst adressere 2 TiB (2^32 sektorer á 512 byte).</summary>
    public const long MbrMaxBytes = 2L * 1024 * 1024 * 1024 * 1024;

    /// <summary>Kun hele diske: disk4 – aldrig disk4s1 eller noget med stier og mellemrum.</summary>
    [GeneratedRegex("^disk[0-9]{1,3}$")]
    private static partial Regex WholeId();
    public static bool SafeWhole(string? id) => id is not null && WholeId().IsMatch(id);

    [GeneratedRegex("^disk[0-9]{1,3}(s[0-9]{1,3})?$")]
    private static partial Regex AnyId();

    /// <summary>disk4s2 → disk4 (null, hvis navnet ikke ligner en disk).</summary>
    public static string? WholeOf(string id)
    {
        if (!AnyId().IsMatch(id)) return null;
        var s = id.IndexOf('s', 4);
        return s < 0 ? id : id[..s];
    }

    // ------------------------------------------------------------------ læsning

    /// <summary>WholeDisks og partitionerne fra diskutil list -plist external physical.</summary>
    public static List<(string Id, string Content, string MountPoint, List<MacPart> Parts)> ParseList(string xml)
    {
        var root = MacPlist.AsDict(MacPlist.Parse(xml));
        var whole = MacPlist.List(root, "WholeDisks").OfType<string>().ToHashSet(StringComparer.Ordinal);
        var o = new List<(string, string, string, List<MacPart>)>();
        foreach (var d in MacPlist.List(root, "AllDisksAndPartitions").Select(MacPlist.AsDict))
        {
            var id = MacPlist.Str(d, "DeviceIdentifier");
            if (!whole.Contains(id)) continue;
            var parts = MacPlist.List(d, "Partitions").Select(MacPlist.AsDict).Select(p => new MacPart(MacPlist.Str(p, "DeviceIdentifier"), MacPlist.Str(p, "Content"),
                MacPlist.Str(p, "VolumeName"), MacPlist.Str(p, "MountPoint"))).ToList();
            // APFS-volumer direkte på disken (sjældent) tæller som partitioner med APFS
            parts.AddRange(MacPlist.List(d, "APFSVolumes").Select(MacPlist.AsDict).Select(p => new MacPart(MacPlist.Str(p, "DeviceIdentifier"), "Apple_APFS",
                MacPlist.Str(p, "VolumeName"), MacPlist.Str(p, "MountPoint"))));
            o.Add((id, MacPlist.Str(d, "Content"), MacPlist.Str(d, "MountPoint"), parts));
        }
        return o;
    }

    /// <summary>En hel disk ud fra diskutil info -plist diskN og listen.</summary>
    public static MacDisk ParseInfo(string xml, string content, string mountPoint, List<MacPart> parts, string serial, string vendor)
    {
        var d = MacPlist.AsDict(MacPlist.Parse(xml));
        var size = MacPlist.Long(d, "Size") is > 0 and var s ? s : MacPlist.Long(d, "TotalSize");
        var media = MacPlist.Str(d, "MediaName").Trim();
        if (media.Length == 0) media = MacPlist.Str(d, "IORegistryEntryName").Replace(" Media", "", StringComparison.Ordinal).Trim();
        return new MacDisk(MacPlist.Str(d, "DeviceIdentifier"), size, media, vendor, serial, MacPlist.Str(d, "BusProtocol"),
            Internal: !MacPlist.Has(d, "Internal") || MacPlist.Bool(d, "Internal"),   // ukendt = intern
            OsInternal: MacPlist.Bool(d, "OSInternalMedia"),
            Virtual: MacPlist.Str(d, "VirtualOrPhysical") is "Virtual" || MacPlist.Str(d, "BusProtocol") is "Disk Image",
            Removable: MacPlist.Bool(d, "Removable") || MacPlist.Bool(d, "RemovableMedia"),
            Ejectable: MacPlist.Bool(d, "Ejectable"),
            WholeDisk: MacPlist.Bool(d, "WholeDisk"),
            content.Length > 0 ? content : MacPlist.Str(d, "Content"), mountPoint.Length > 0 ? mountPoint : MacPlist.Str(d, "MountPoint"), parts);
    }

    /// <summary>Startdiskens hele disk fra diskutil info -plist / (ved APFS: den syntetiske container, fx disk3).</summary>
    public static string ParseBoot(string xml) => MacPlist.Str(MacPlist.AsDict(MacPlist.Parse(xml)), "ParentWholeDisk");

    /// <summary>
    /// APFS-brugen pr. fysisk disk (diskutil apfs list -plist): System, hvis containeren rummer macOS (roller System, Data, Preboot,
    /// Recovery, VM) eller er startdiskens; Backup ved Time Machine; ellers Data.
    /// </summary>
    public static Dictionary<string, MacApfsUse> ParseApfs(string xml, string bootWhole)
    {
        var o = new Dictionary<string, MacApfsUse>(StringComparer.Ordinal);
        string[] system = ["System", "Data", "Preboot", "Recovery", "VM", "Update", "xART", "Hardware"];
        foreach (var c in MacPlist.List(MacPlist.AsDict(MacPlist.Parse(xml)), "Containers").Select(MacPlist.AsDict))
        {
            var roles = MacPlist.List(c, "Volumes").Select(MacPlist.AsDict).SelectMany(v => MacPlist.List(v, "Roles").OfType<string>()).ToList();
            var use = MacPlist.Str(c, "ContainerReference") == bootWhole || roles.Any(system.Contains) ? MacApfsUse.System
                : roles.Contains("Backup") ? MacApfsUse.Backup : MacApfsUse.Data;
            foreach (var ps in MacPlist.List(c, "PhysicalStores").Select(MacPlist.AsDict))
                if (WholeOf(MacPlist.Str(ps, "DeviceIdentifier")) is { } w)
                    o[w] = o.TryGetValue(w, out var cur) && cur > use ? cur : use;
        }
        return o;
    }

    /// <summary>
    /// Serienummer og leverandør pr. hel disk fra ioreg -a -r -c IOUSBHostDevice -l: den nærmeste USB-enhed over IOMedia'en med
    /// "BSD Name" = diskN.
    /// </summary>
    public static Dictionary<string, (string Serial, string Vendor)> ParseIoreg(string xml)
    {
        var o = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        void Walk(Dictionary<string, object?>? n, string serial, string vendor)
        {
            if (n is null) return;
            var s = MacPlist.Str(n, "USB Serial Number") is { Length: > 0 } a ? a : MacPlist.Str(n, "kUSBSerialNumberString");
            var v = MacPlist.Str(n, "USB Vendor Name") is { Length: > 0 } b ? b : MacPlist.Str(n, "kUSBVendorString");
            var device = MacPlist.Str(n, "IOObjectClass") is "IOUSBHostDevice" or "IOUSBDevice"
                || !MacPlist.Has(n, "bInterfaceNumber") && (MacPlist.Has(n, "idVendor") || MacPlist.Has(n, "USB Product Name") || MacPlist.Has(n, "kUSBProductString"));
            if (device) { serial = s.Trim(); vendor = v.Trim(); }   // den nærmeste USB-enhed (ikke et interface) over disken
            if (MacPlist.Str(n, "BSD Name") is { Length: > 0 } bsd && SafeWhole(bsd) && !o.ContainsKey(bsd)) o[bsd] = (serial, vendor);
            foreach (var c in MacPlist.List(n, "IORegistryEntryChildren")) Walk(MacPlist.AsDict(c), serial, vendor);
        }
        var root = MacPlist.Parse(xml);
        foreach (var n in root is List<object?> l ? l : [root]) Walk(MacPlist.AsDict(n), "", "");
        return o;
    }

    private static readonly string[] SystemContent = ["Apple_CoreStorage", "Apple_RAID", "Apple_Boot", "Apple_RAID_Offline"];

    /// <summary>
    /// Diskene på denne Mac (kun læsning). null, hvis diskutil eller ioreg fejler, eller hvis en af de listede diske ikke kan læses helt –
    /// serienummeret indgår i øjebliksbilledets nøgle (<see cref="Key"/>), så en halv læsning ville få diske, der sad i før guiden, til at
    /// se nye ud. Kun APFS-oplysningerne er "bedst muligt": fejler apfs list, afvises alle diske med APFS alligevel ud fra partitionernes indhold.
    /// </summary>
    public static MacScan? Scan(ISpareHost h)
    {
        var ls = h.Run(Diskutil, ["list", "-plist", "external", "physical"], timeoutMin: 1);
        if (ls.Exit != 0) return null;
        List<(string Id, string Content, string MountPoint, List<MacPart> Parts)> list;
        try { list = ParseList(ls.Output); } catch (XmlException) { return null; }
        var boot = "";
        var bi = h.Run(Diskutil, ["info", "-plist", "/"], timeoutMin: 1);
        try { if (bi.Exit == 0) boot = ParseBoot(bi.Output); } catch (XmlException) { }
        var apfs = new Dictionary<string, MacApfsUse>(StringComparer.Ordinal);
        var ap = h.Run(Diskutil, ["apfs", "list", "-plist"], timeoutMin: 1);
        try { if (ap.Exit == 0) apfs = ParseApfs(ap.Output, boot); } catch (XmlException) { }
        var usb = new Dictionary<string, (string Serial, string Vendor)>(StringComparer.Ordinal);
        if (list.Count > 0)
        {
            // ioreg skal lykkes: uden serienumre ændrer nøglerne sig, og øjebliksbilledet ville glemme alle diske
            var io = h.Run("/usr/sbin/ioreg", ["-a", "-r", "-c", "IOUSBHostDevice", "-l"], timeoutMin: 1);
            if (io.Exit != 0) return null;
            try { usb = ParseIoreg(io.Output); } catch (XmlException) { return null; }
        }
        var tmDest = list.Count > 0 ? TimeMachineMounts(h) : [];
        var disks = new List<MacDisk>();
        var tm = new HashSet<string>(StringComparer.Ordinal);
        var tmUnknown = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, content, mp, parts) in list)
        {
            if (!SafeWhole(id)) continue;
            var info = h.Run(Diskutil, ["info", "-plist", id], timeoutMin: 1);
            if (info.Exit != 0) return null;
            MacDisk d;
            var (serial, vendor) = usb.TryGetValue(id, out var u) ? u : ("", "");
            try { d = ParseInfo(info.Output, content, mp, parts, serial, vendor); }
            catch (XmlException) { return null; }
            if (d.Id != id) return null;
            if (d.Bus == "USB" && !usb.ContainsKey(id)) return null;   // en USB-disk, ioreg ikke kender: serienummeret er ukendt
            disks.Add(d);
            foreach (var m in Mounts(d).Where(m => m.StartsWith("/Volumes/", StringComparison.Ordinal)))
            {
                if (tmDest.Contains(m)) { tm.Add(id); continue; }
                // HFS+-Time Machine: Backups.backupdb i roden – tjekket med timeout (en hængende volumen må ikke låse guiden)
                var t = h.Run("/bin/test", ["-e", m + "/Backups.backupdb"], timeoutMin: 1);
                if (t.Exit == 0) tm.Add(id);
                else if (t.Exit != 1) tmUnknown.Add(id);
            }
        }
        return new MacScan(disks, boot, apfs, tm, tmUnknown);
    }

    /// <summary>Monteringspunkterne for Time Machine-destinationerne fra tmutil destinationinfo -X ("bedst muligt": tom ved fejl).</summary>
    public static HashSet<string> TimeMachineMounts(ISpareHost h)
    {
        var o = new HashSet<string>(StringComparer.Ordinal);
        var r = h.Run(Tmutil, ["destinationinfo", "-X"], timeoutMin: 1);
        if (r.Exit != 0) return o;
        try { o.UnionWith(ParseTmutil(r.Output)); } catch (XmlException) { }
        return o;
    }

    /// <summary>MountPoint for hver destination i tmutil destinationinfo -X.</summary>
    public static IEnumerable<string> ParseTmutil(string xml) =>
        MacPlist.List(MacPlist.AsDict(MacPlist.Parse(xml)), "Destinations").Select(MacPlist.AsDict).Select(d => MacPlist.Str(d, "MountPoint").TrimEnd('/'))
            .Where(m => m.Length > 0).ToList();

    private static IEnumerable<string> Mounts(MacDisk d) => new[] { d.MountPoint }.Concat(d.Parts.Select(p => p.MountPoint)).Where(m => m.Length > 0);

    // ------------------------------------------------------------------ bedømmelse

    /// <summary>En disk, der slet ikke vises: ikke en ekstern, fysisk USB-disk, startdisken, macOS' egen APFS eller noget monteret uden for /Volumes.</summary>
    public static bool Hidden(MacDisk d, MacScan s)
    {
        if (!SafeWhole(d.Id) || !d.WholeDisk || d.Internal || d.OsInternal || d.Virtual || d.Bus != "USB") return true;
        if (s.BootWhole.Length > 0 && WholeOf(s.BootWhole) == d.Id) return true;
        if (s.Apfs.TryGetValue(d.Id, out var use) && use == MacApfsUse.System) return true;
        if (SystemContent.Contains(d.Content) || d.Parts.Any(p => SystemContent.Contains(p.Content))) return true;
        return Mounts(d).Any(m => m == "/" || !m.StartsWith("/Volumes/", StringComparison.Ordinal));
    }

    /// <summary>Nøglen til øjebliksbilledet: stien og en "|" (så BiosUsbLocal.Forget glemmer et stik, der tages ud), derefter serie, medie og størrelse.</summary>
    public static string Key(MacDisk d) => $"{d.Path}|{Clean(d.Serial)}~{Clean(d.Media)}~{d.Size}";
    private static string Clean(string s) => s.Replace('|', '/').Replace('~', '-');

    /// <summary>Hvorfor disken ikke må skrives (null = den må). Samme udelukkelser som på Linux, plus APFS og Time Machine.</summary>
    public static string? Refusal(MacDisk d, MacScan s, ISet<string>? baseline, long maxBytes, bool allowLarge)
    {
        if (!SafeWhole(d.Id) || !d.WholeDisk || d.Internal || d.Virtual || d.Bus != "USB") return BiosUsb.NotUsb;
        if (!d.Removable && !d.Ejectable) return NotRemovable;
        if (Hidden(d, s)) return BiosUsb.SystemDisk;
        if (d.Size <= 0) return BiosUsb.NoMedia;
        if (s.TimeMachine.Contains(d.Id) || s.Apfs.TryGetValue(d.Id, out var u) && u == MacApfsUse.Backup) return TimeMachineDisk;
        if (s.TmUnknown?.Contains(d.Id) == true) return TimeMachineUnknown;
        if (s.Apfs.ContainsKey(d.Id) || d.Content == "Apple_APFS" || d.Parts.Any(p => p.Content.StartsWith("Apple_APFS", StringComparison.Ordinal))) return ApfsDisk;
        if (d.Size > MbrMaxBytes) return TooBigForMbr;
        if (baseline is null || baseline.Contains(Key(d))) return BiosUsb.NotNew;
        // macOS melder alle eksterne USB-diske som Ejectable; uden flytbart medie er det typisk en ekstern harddisk/SSD → ekstra bekræftelse
        if (!d.Removable && !allowLarge) return BiosUsb.ExternalDrive;
        if (d.Size > maxBytes && !allowLarge) return BiosUsb.TooLarge(maxBytes);
        return null;
    }

    /// <summary>USB-diskene til guiden. Skjulte diske (se <see cref="Hidden"/>) meldes ikke.</summary>
    public static List<BiosUsbDisk> Evaluate(MacScan s, ISet<string>? baseline, long maxBytes) =>
        s.Disks.Where(d => !Hidden(d, s)).Select(d =>
        {
            var why = Refusal(d, s, baseline, maxBytes, allowLarge: false);
            return new BiosUsbDisk
            {
                Id = d.Id, Path = d.Path, Size = d.Size, Model = d.Media, Vendor = d.Vendor, Serial = d.Serial, Partitions = d.Parts.Count,
                Labels = d.Parts.Select(p => string.Join(' ', new[] { p.Volume, FsName(p.Content) }.Where(x => x.Length > 0))).Where(x => x.Length > 0).ToList(),
                Selectable = why is null, Why = why, Large = d.Size > maxBytes || !d.Removable,   // Large = kan vælges med den ekstra bekræftelse
            };
        }).ToList();

    private static string FsName(string content) => content switch
    {
        "DOS_FAT_32" or "DOS_FAT_16" or "DOS_FAT_12" => "FAT",
        "Microsoft Basic Data" or "Windows_NTFS" => "exFAT/NTFS",
        "Apple_HFS" => "Mac OS Extended",
        "Apple_APFS" => "APFS",
        "EFI" => "EFI",
        _ => content,
    };

    // ------------------------------------------------------------------ bekræftelsens medienavn

    private static readonly ConcurrentDictionary<string, (string Media, DateTime At)> Confirmed = new(StringComparer.Ordinal);

    /// <summary>Medienavnet, agenten selv læste ved den lokale bekræftelse – bundet til kvitteringen.</summary>
    public static void RememberMedia(string hash, string media, DateTime? now = null)
    {
        var t = now ?? DateTime.UtcNow;
        foreach (var k in Confirmed.Where(kv => t - kv.Value.At > BiosUsbLocal.ConfirmFor).Select(kv => kv.Key).ToList()) Confirmed.TryRemove(k, out _);
        Confirmed[hash] = (media, t);
    }

    /// <summary>Medienavnet fra bekræftelsen (bruges én gang), eller null.</summary>
    public static string? TakeMedia(string? hash, DateTime? now = null) =>
        hash is { Length: > 0 } && Confirmed.TryRemove(hash, out var c) && (now ?? DateTime.UtcNow) - c.At <= BiosUsbLocal.ConfirmFor ? c.Media : null;

    // ------------------------------------------------------------------ skrivning

    private static bool Denied(string output) =>
        output.Contains("not permitted", StringComparison.OrdinalIgnoreCase) || output.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
        || output.Contains("-69877", StringComparison.Ordinal);   // diskutil: "Couldn't open device" uden adgang

    /// <summary>
    /// Skriv stikket (macOS). Rækkefølge: pakken passer → disken har stadig samme nummer, størrelse, medienavn og serie, og alle udelukkelser
    /// holder → lokal bekræftelse under 5 minutter → afmontér → læs alle diske igen og tjek identitet (også serienummeret) og udelukkelser →
    /// FAT32 med MBR (diskutil eraseDisk; over 32 GiB diskutil partitionDisk med én FAT32-partition på 32 GiB og resten fri, som på Linux) →
    /// montér i en privat mappe → kopiér → afmontér og montér igen → læs alt tilbage → afmontér → skub ud. Afmonterer altid, også ved fejl.
    /// </summary>
    public static BiosUsbReport Write(ISpareHost h, BiosUsbOrder o, string package, BiosUsbLocal local, string? confirmedMedia, Action<string> log,
        Action<int, int, string>? phase = null, string mountRoot = MacPaths.AgentDir + "/bios-usb")
    {
        var rep = new BiosUsbReport { SessionId = o.SessionId, Phase = BiosUsbPhases.Write };
        BiosUsbReport Stop(string outcome, string text) { rep.Outcome = outcome; rep.Message = text; log(text); return rep; }
        BiosUsbReport Failed(string what, SpareCmd r) =>
            Stop("failed", Denied(r.Output) ? MacAccess : r.Exit == 124 ? $"{what} tog for lang tid." : $"{what} fejlede: {r.Output.Trim()}");
        phase ??= (_, _, _) => { };

        // 1. pakken
        phase(0, 5, "Kontrollerer pakken …");
        if (o.Layout is not { } layout || !BiosUsbLayouts.All.Contains(layout) || o.FileName is null) return Stop("refused", "Jobbet mangler pakkens layout.");
        if (!File.Exists(package) || new FileInfo(package).Length != o.PackageSize)
            return Stop("failed", "Pakken har ikke den forventede størrelse. Intet er skrevet.");
        if (o.Sha256 is not { Length: 64 } want || !string.Equals(BiosUsb.Sha256File(package), want, StringComparison.OrdinalIgnoreCase))
            return Stop("failed", "Pakkens SHA-256 passer ikke. Intet er skrevet.");

        // 2. disken: samme stik som bekræftet, og alle udelukkelser holder stadig
        if (!SafeWhole(o.DiskId) || o.Serial is null) return Stop("refused", "Ugyldigt disknavn.");
        var id = o.DiskId!;
        if (!h.Exists(Diskutil)) return Stop("failed", "diskutil findes ikke på denne Mac.");
        if (Scan(h) is not { } s) return Stop("failed", ListFailed);
        var d = s.Disks.FirstOrDefault(x => x.Id == id);
        if (d is null) return Stop("refused", "Stikket er ikke sat i længere. Intet er skrevet.");
        if (d.Serial != o.Serial || d.Size != o.Size) return Stop("refused", "Stikket er ikke det samme som det, du bekræftede. Intet er skrevet.");
        if (Refusal(d, s, local.Baseline(o.SessionId), o.MaxBytes, o.AllowLarge) is { } why) return Stop("refused", why + " Intet er skrevet.");
        if (!local.Take(o.SessionId, id, o.Serial, o.Size, o.ConfirmHash))
            return Stop("refused", "Stikket er ikke bekræftet på denne pc inden for de sidste 5 minutter. Intet er skrevet.");
        if (confirmedMedia is null || confirmedMedia != d.Media)
            return Stop("refused", "Stikket er ikke det samme som det, du bekræftede. Intet er skrevet.");
        rep.DiskLabel = $"{string.Join(' ', new[] { d.Vendor, d.Media }.Where(x => x.Length > 0))} · {SpareCard.Size(d.Size)} · {d.Serial}".Trim();
        log($"Stik: {rep.DiskLabel} ({d.Path})");

        // 3. afmontér, tjek identiteten igen (disknumre kan skifte) og formatér
        phase(5, 25, "Formaterer …");
        var um = h.Run(Diskutil, ["unmountDisk", d.Path], timeoutMin: 2);
        if (um.Exit != 0) return Denied(um.Output) ? Stop("failed", MacAccess)
            : Stop("failed", "Stikket kunne ikke afmonteres. Luk programmer, der bruger det (fx Finder-vinduer), og prøv igen. Intet er skrevet.");
        // hele læsningen igen (diskutil + ioreg): samme nummer, størrelse, medie og serienummer, og alle udelukkelser holder stadig
        var s2 = Scan(h);
        var now = s2?.Disks.FirstOrDefault(x => x.Id == id);
        if (s2 is null || now is null || now.Serial != o.Serial || now.Serial != d.Serial || now.Size != d.Size || now.Media != d.Media
            || now.Vendor != d.Vendor || Refusal(now, s2, local.Baseline(o.SessionId), o.MaxBytes, o.AllowLarge) is not null)
            return Stop("refused", "Stikket ændrede sig under afmonteringen. Intet er skrevet.");
        // FAT32 med MBR. Over 32 GiB: én partition på 32 GiB og resten fri (som Linux), så clusterstørrelsen bliver som på andre stik.
        // diskutil sætter typen DOS_FAT_32 (0x0B) og intet aktivt flag – UEFI starter fra det; se bios-usb-mac.md.
        string[] fmt = d.Size > BiosUsb.FatMaxBytes
            ? ["partitionDisk", d.Path, "2", "MBR", "FAT32", BiosUsb.VolumeLabel, $"{BiosUsb.FatMaxBytes}B", "Free Space", "free", "R"]
            : ["eraseDisk", "FAT32", BiosUsb.VolumeLabel, "MBRFormat", d.Path];
        var er = h.Run(Diskutil, fmt, timeoutMin: 10);
        if (er.Exit != 0) return Failed("diskutil " + fmt[0], er);

        // den nye partition: diskNs1 på samme disk med FAT32
        var part = id + "s1";
        var pi = h.Run(Diskutil, ["info", "-plist", part], timeoutMin: 1);
        Dictionary<string, object?>? pd = null;
        try { if (pi.Exit == 0) pd = MacPlist.AsDict(MacPlist.Parse(pi.Output)); } catch (XmlException) { }
        if (pd is null || MacPlist.Str(pd, "ParentWholeDisk") != id
            || !(MacPlist.Str(pd, "FilesystemType") == "msdos" || MacPlist.Str(pd, "Content").StartsWith("DOS_FAT", StringComparison.Ordinal)))
            return Stop("failed", "Stikket blev formateret, men den nye FAT32-partition blev ikke fundet.");
        var partPath = "/dev/" + part;
        h.Run(Diskutil, ["unmount", partPath], timeoutMin: 2);   // diskutil monterer den selv under /Volumes

        // 4. kopiér og kontrollér i en mappe, kun root kan læse
        var dir = System.IO.Path.Combine(mountRoot, "mnt-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)));
        var mounted = false;
        try
        {
            h.MakeDir(dir);
            bool Mount()
            {
                var m = h.Run(Diskutil, ["mount", "-mountPoint", dir, partPath], timeoutMin: 2);
                mounted = m.Exit == 0;
                if (!mounted) log("diskutil mount fejlede: " + m.Output.Trim());
                return mounted;
            }
            bool Unmount()
            {
                h.Run("/bin/sync", [], timeoutMin: 2);
                var u = h.Run(Diskutil, ["unmount", partPath], timeoutMin: 2);
                if (u.Exit == 0) mounted = false;
                return !mounted;
            }
            if (!Mount()) return Stop("failed", "Stikket kunne ikke monteres efter formateringen.");
            phase(25, 75, "Kopierer …");
            List<(string Path, string Sha, long Size)> manifest;
            try { manifest = BiosUsb.Extract(package, layout, o.FileName, dir, o.Readme); }
            catch (UnauthorizedAccessException) { return Stop("failed", MacAccess); }
            catch (IOException e) when (Denied(e.Message)) { return Stop("failed", MacAccess); }
            catch (Exception e) when (e is IOException or InvalidDataException) { return Stop("failed", "Kopieringen fejlede: " + e.Message); }
            rep.Files = manifest.Count;
            rep.Bytes = manifest.Sum(m => m.Size);
            log($"Kopieret {rep.Files} filer ({SpareCard.Size(rep.Bytes)}).");
            phase(75, 92, "Kontrollerer …");
            if (!Unmount()) return Stop("failed", "Stikket kunne ikke afmonteres efter kopieringen.");
            if (!Mount()) return Stop("failed", "Stikket kunne ikke monteres igen til kontrollen.");
            List<string> bad;
            try { bad = BiosUsb.Verify(dir, manifest); }
            catch (UnauthorizedAccessException) { return Stop("failed", MacAccess); }
            catch (IOException e) { return Stop("failed", "Kontrollen fejlede: " + e.Message); }
            if (bad.Count > 0) return Stop("failed", $"{bad.Count} fil(er) på stikket passer ikke med pakken (fx {bad[0]}). Brug et andet stik.");
            rep.Verified = true;
            if (!Unmount()) return Stop("failed", "Stikket kunne ikke afmonteres efter kontrollen.");
        }
        finally
        {
            if (mounted) { h.Run("/bin/sync", [], timeoutMin: 2); h.Run(Diskutil, ["unmount", "force", partPath], timeoutMin: 2); }
            h.Run("/bin/rmdir", [dir], timeoutMin: 1);
        }

        // 5. skub ud
        phase(92, 100, "Skubber ud …");
        rep.Ejected = h.Run(Diskutil, ["eject", d.Path], timeoutMin: 2).Exit == 0;
        rep.Outcome = "ok";
        rep.Message = $"Stikket er klar: {rep.Files} filer kopieret og kontrolleret." + (rep.Ejected ? " Det er skubbet ud og kan tages ud." : " Skub det ud i Finder, og tag det ud.");
        log(rep.Message);
        return rep;
    }
}
