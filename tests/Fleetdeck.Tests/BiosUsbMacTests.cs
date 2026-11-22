using System.IO.Compression;
using Fleetdeck.Shared;

namespace Fleetdeck.Tests;

/// <summary>
/// USB-stik med BIOS-opdateringen fra en Mac (BiosUsbMac). Der skrives aldrig til en rigtig disk: diskutil og ioreg er falske svar
/// (eksempel-plister i samme form som macOS'), og "stikket" er en midlertidig mappe.
/// </summary>
public class BiosUsbMacTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("fd-bios-usb-mac-").FullName;
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    // ------------------------------------------------------------------ eksempel-plister

    private static string P(string body) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n"
        + "<plist version=\"1.0\">\n" + body + "\n</plist>";

    private static string Part(string id, string content, string vol = "", string mp = "") =>
        $"<dict><key>Content</key><string>{content}</string><key>DeviceIdentifier</key><string>{id}</string>"
        + (mp.Length > 0 ? $"<key>MountPoint</key><string>{mp}</string>" : "") + $"<key>Size</key><integer>1000000000</integer>"
        + (vol.Length > 0 ? $"<key>VolumeName</key><string>{vol}</string>" : "") + "</dict>";

    private static string ListDisk(string id, string content, params string[] parts) =>
        $"<dict><key>Content</key><string>{content}</string><key>DeviceIdentifier</key><string>{id}</string><key>OSInternal</key><false/>"
        + $"<key>Partitions</key><array>{string.Join("", parts)}</array><key>Size</key><integer>16008609792</integer></dict>";

    private sealed record Info(string Id, long Size = 16_008_609_792, string Media = "SanDisk Ultra", string Bus = "USB", bool Internal = false,
        bool Removable = true, bool Ejectable = true, string Kind = "Physical", bool OsInternal = false);

    private static string InfoXml(Info i) => P(
        $"<dict><key>BusProtocol</key><string>{i.Bus}</string><key>DeviceIdentifier</key><string>{i.Id}</string><key>DeviceNode</key><string>/dev/{i.Id}</string>"
        + $"<key>Ejectable</key><{(i.Ejectable ? "true" : "false")}/><key>IORegistryEntryName</key><string>{i.Media} Media</string>"
        + $"<key>Internal</key><{(i.Internal ? "true" : "false")}/><key>MediaName</key><string>{i.Media}</string>"
        + $"<key>OSInternalMedia</key><{(i.OsInternal ? "true" : "false")}/><key>Removable</key><{(i.Removable ? "true" : "false")}/>"
        + $"<key>RemovableMedia</key><{(i.Removable ? "true" : "false")}/><key>Size</key><integer>{i.Size}</integer>"
        + $"<key>VirtualOrPhysical</key><string>{i.Kind}</string><key>WholeDisk</key><true/></dict>");

    private const string Boot = "<dict><key>DeviceIdentifier</key><string>disk3s1s1</string><key>MountPoint</key><string>/</string><key>ParentWholeDisk</key><string>disk3</string></dict>";

    // disk3 = startdiskens container på den interne disk0; disk8 = en ekstern APFS-disk (disk5) med data; disk9 = Time Machine på disk6
    private const string Apfs = """
        <dict><key>Containers</key><array>
          <dict><key>ContainerReference</key><string>disk3</string><key>PhysicalStores</key><array><dict><key>DeviceIdentifier</key><string>disk0s2</string></dict></array>
            <key>Volumes</key><array><dict><key>Roles</key><array><string>System</string></array></dict><dict><key>Roles</key><array><string>Data</string></array></dict></array></dict>
          <dict><key>ContainerReference</key><string>disk8</string><key>PhysicalStores</key><array><dict><key>DeviceIdentifier</key><string>disk5s2</string></dict></array>
            <key>Volumes</key><array><dict><key>Roles</key><array/></dict></array></dict>
          <dict><key>ContainerReference</key><string>disk9</string><key>PhysicalStores</key><array><dict><key>DeviceIdentifier</key><string>disk6s2</string></dict></array>
            <key>Volumes</key><array><dict><key>Roles</key><array><string>Backup</string></array></dict></array></dict>
          <dict><key>ContainerReference</key><string>disk11</string><key>PhysicalStores</key><array><dict><key>DeviceIdentifier</key><string>disk12s2</string></dict></array>
            <key>Volumes</key><array><dict><key>Roles</key><array><string>System</string></array></dict></array></dict>
        </array></dict>
        """;

    /// <summary>USB-hub uden serie → stikket (IOUSBHostDevice med serie) → interface (har idVendor, men er ikke enheden) → … → IOMedia disk4.</summary>
    private static string Ioreg(string serial = "4C530001", params string[] others) => P($"""
        <array>{string.Join("", others.Select(OtherUsb))}<dict><key>IOObjectClass</key><string>IOUSBHostDevice</string><key>USB Product Name</key><string>USB2.0 Hub</string><key>idVendor</key><integer>1507</integer>
          <key>IORegistryEntryChildren</key><array>
            <dict><key>IOObjectClass</key><string>IOUSBHostDevice</string><key>USB Product Name</key><string>Ultra</string><key>USB Vendor Name</key><string>SanDisk</string>
              <key>USB Serial Number</key><string>{serial}</string><key>idVendor</key><integer>1921</integer>
              <key>IORegistryEntryChildren</key><array>
                <dict><key>IOObjectClass</key><string>IOUSBHostInterface</string><key>idVendor</key><integer>1921</integer><key>bInterfaceNumber</key><integer>0</integer>
                  <key>IORegistryEntryChildren</key><array>
                    <dict><key>IOObjectClass</key><string>IOMedia</string><key>BSD Name</key><string>disk4</string><key>Whole</key><true/>
                      <key>IORegistryEntryChildren</key><array><dict><key>IOObjectClass</key><string>IOMedia</string><key>BSD Name</key><string>disk4s1</string></dict></array></dict>
                  </array></dict>
              </array></dict>
          </array></dict></array>
        """);

    /// <summary>En anden USB-disk i ioreg (serie SER-diskN).</summary>
    private static string OtherUsb(string id) =>
        $"<dict><key>IOObjectClass</key><string>IOUSBHostDevice</string><key>USB Serial Number</key><string>SER-{id}</string><key>idVendor</key><integer>1</integer>"
        + $"<key>IORegistryEntryChildren</key><array><dict><key>IOObjectClass</key><string>IOMedia</string><key>BSD Name</key><string>{id}</string></dict></array></dict>";

    // ------------------------------------------------------------------ falsk Mac

    private sealed class FakeMac : ISpareHost
    {
        public List<string> Whole { get; set; } = ["disk4", "disk5", "disk6", "disk7", "disk12"];
        public Dictionary<string, Info> Infos { get; } = new()
        {
            ["disk4"] = new("disk4"),
            ["disk5"] = new("disk5", 500_000_000_000, "Samsung T7", Removable: false),
            ["disk6"] = new("disk6", 2_000_000_000_000, "My Passport", Removable: false),
            ["disk7"] = new("disk7", 8_000_000_000, "Flash"),
            ["disk12"] = new("disk12", 1_000_000_000_000, "External boot", Removable: false),
            ["disk0"] = new("disk0", 512_000_000_000, "APPLE SSD", Bus: "Apple Fabric", Internal: true, Removable: false, Ejectable: false, OsInternal: true),
        };
        public string Disk4Content { get; set; } = ListDisk("disk4", "FDisk_partition_scheme", Part("disk4s1", "DOS_FAT_32", "OLDSTICK", "/Volumes/OLDSTICK"));
        public string Serial { get; set; } = "4C530001";
        public List<(string File, List<string> Args)> Calls { get; } = [];
        public Func<string, SpareCmd?>? Override { get; set; }
        /// <summary>Kaldes efter "diskutil unmountDisk" (fx et andet stik, der overtager disknummeret).</summary>
        public Action? AfterUnmount { get; set; }
        public bool Erased { get; private set; }
        /// <summary>USB-diske, ioreg ikke kender.</summary>
        public HashSet<string> IoregMissing { get; } = [];

        public SpareCmd Run(string file, IReadOnlyList<string> args, string? stdin = null, int timeoutMin = 30)
        {
            Calls.Add((file, args.ToList()));
            var line = string.Join(' ', args);
            if (Override?.Invoke(line) is { } o) return o;
            if (file == "/usr/sbin/ioreg") return new(0, Ioreg(Serial, Whole.Where(w => w != "disk4" && !IoregMissing.Contains(w)).ToArray()));
            if (file == BiosUsbMac.Tmutil) return new(0, P("<dict><key>Destinations</key><array/></dict>"));
            if (file == "/bin/test") return new(args is ["-e", "/Volumes/TM/Backups.backupdb"] ? 0 : 1, "");
            if (file != BiosUsbMac.Diskutil) return new(0, "");
            switch (line)
            {
                case "list -plist external physical":
                    var all = new Dictionary<string, string>
                    {
                        ["disk4"] = Disk4Content,
                        ["disk5"] = ListDisk("disk5", "GUID_partition_scheme", Part("disk5s1", "EFI", "EFI"), Part("disk5s2", "Apple_APFS")),
                        ["disk6"] = ListDisk("disk6", "GUID_partition_scheme", Part("disk6s2", "Apple_HFS", "TM", "/Volumes/TM")),
                        ["disk7"] = ListDisk("disk7", "FDisk_partition_scheme", Part("disk7s1", "DOS_FAT_32", "X", "/private/var/odd")),
                        ["disk12"] = ListDisk("disk12", "GUID_partition_scheme", Part("disk12s2", "Apple_APFS")),
                    };
                    return new(0, P($"<dict><key>AllDisksAndPartitions</key><array>{string.Join("", Whole.Select(w => all[w]))}</array>"
                        + $"<key>WholeDisks</key><array>{string.Join("", Whole.Select(w => $"<string>{w}</string>"))}</array></dict>"));
                case "info -plist /": return new(0, P(Boot));
                case "apfs list -plist": return new(0, P(Apfs));
                case "info -plist disk4s1":
                    return Erased ? new(0, P("<dict><key>Content</key><string>DOS_FAT_32</string><key>FilesystemType</key><string>msdos</string>"
                        + "<key>ParentWholeDisk</key><string>disk4</string></dict>")) : new(1, "");
                case "unmountDisk /dev/disk4": AfterUnmount?.Invoke(); return new(0, "");
            }
            if (args is ["info", "-plist", var id] && Infos.TryGetValue(id, out var i)) return new(0, InfoXml(i));
            if (args is ["eraseDisk", ..] or ["partitionDisk", ..]) { Erased = true; return new(0, "Finished partitioning on disk4"); }
            return new(0, "");
        }
        public string? ReadText(string path) => null;
        public void WriteText(string path, string text) { }
        public bool Exists(string path) => path == BiosUsbMac.Diskutil || path == "/Volumes/TM/Backups.backupdb";
        public void MakeDir(string path) => Directory.CreateDirectory(path);
        public string? RealPath(string path) => null;
        public string[] ListDir(string path) => [];
        public List<string> ListTree(string root) => [];
        public bool Destructive => Calls.Any(c => c.Args.Count > 0 && c.Args[0] is "eraseDisk" or "partitionDisk" or "zeroDisk" or "randomDisk");
        public IEnumerable<string> DiskArgs => Calls.Where(c => c.File == BiosUsbMac.Diskutil).SelectMany(c => c.Args).Where(a => a.Contains("disk", StringComparison.Ordinal));
    }

    private string Zip(params (string Name, string Text)[] files)
    {
        var p = Path.Combine(_root, $"pkg-{Guid.NewGuid():N}.zip");
        using (var z = ZipFile.Open(p, ZipArchiveMode.Create))
            foreach (var (n, t) in files)
                using (var w = new StreamWriter(z.CreateEntry(n).Open())) w.Write(t);
        return p;
    }

    private static readonly (string, string)[] LenovoFiles =
        [("EFI/BOOT/BOOTX64.efi", "shell"), ("startup.nsh", "flash2.efi /rfg"), ("flash2.efi", "flasher"), ("IMAGEM1.ROM", new string('x', 5000))];

    /// <summary>Øjebliksbillede uden stikket (disk4 er ny), lokal bekræftelse af disk4 og en ordre, der passer.</summary>
    private (BiosUsbOrder Order, string Package, BiosUsbLocal Local) Ready(FakeMac h, long size = 16_008_609_792)
    {
        var pkg = Zip(LenovoFiles);
        var local = new BiosUsbLocal();
        var s = BiosUsbMac.Scan(h)!;
        local.SetBaseline("s1", s.Disks.Where(d => d.Id != "disk4").Select(BiosUsbMac.Key));
        var hash = local.Confirm("s1", "disk4", h.Serial, size);
        var o = new BiosUsbOrder
        {
            SessionId = "s1", Phase = BiosUsbPhases.Write, DiskId = "disk4", Serial = h.Serial, Size = size, ConfirmHash = hash,
            Layout = BiosUsbLayouts.LenovoZip, FileName = "q9zjt79usa.zip", Sha256 = BiosUsb.Sha256File(pkg), PackageSize = new FileInfo(pkg).Length,
            Readme = "Trin 1\nTrin 2",
        };
        h.Calls.Clear();
        return (o, pkg, local);
    }

    private BiosUsbReport Write(FakeMac h, BiosUsbOrder o, string pkg, BiosUsbLocal local, string? media = "SanDisk Ultra") =>
        BiosUsbMac.Write(h, o, pkg, local, media, _ => { }, mountRoot: Path.Combine(_root, "mnt"));

    // ------------------------------------------------------------------ plister

    [Fact]
    public void Plist_reader_handles_apple_doctype_and_types_without_fetching_the_dtd()
    {
        var d = MacPlist.AsDict(MacPlist.Parse(P("<dict><key>a</key><integer>18446744073709551615</integer><key>b</key><true/><key>c</key><array><string>x</string><real>1.5</real></array>"
            + "<key>d</key><dict><key>e</key><string>æøå</string></dict></dict>")))!;
        Assert.Equal(long.MaxValue, MacPlist.Long(d, "a"));
        Assert.True(MacPlist.Bool(d, "b"));
        Assert.Equal(new List<object?> { "x", 1.5 }, MacPlist.List(d, "c"));
        Assert.Equal("æøå", MacPlist.Str(MacPlist.AsDict(d["d"]), "e"));
        Assert.Null(MacPlist.Parse("<notplist/>"));
    }

    [Fact]
    public void Ioreg_serial_comes_from_the_nearest_usb_device_not_the_interface_or_hub()
    {
        var m = BiosUsbMac.ParseIoreg(Ioreg());
        Assert.Equal(("4C530001", "SanDisk"), m["disk4"]);
        Assert.False(m.ContainsKey("disk4s1"));   // kun hele diske
    }

    [Fact]
    public void Apfs_list_marks_system_backup_and_data_by_physical_disk()
    {
        var a = BiosUsbMac.ParseApfs(P(Apfs), "disk3");
        Assert.Equal(MacApfsUse.System, a["disk0"]);
        Assert.Equal(MacApfsUse.Data, a["disk5"]);
        Assert.Equal(MacApfsUse.Backup, a["disk6"]);
        Assert.Equal(MacApfsUse.System, a["disk12"]);
        Assert.Equal("disk3", BiosUsbMac.ParseBoot(P(Boot)));
        Assert.Equal("disk4", BiosUsbMac.WholeOf("disk4s12"));
        Assert.Null(BiosUsbMac.WholeOf("../disk4"));
    }

    // ------------------------------------------------------------------ valg og afvisning

    [Fact]
    public void Evaluate_shows_only_external_usb_disks_and_refuses_apfs_and_time_machine()
    {
        var h = new FakeMac();
        var s = BiosUsbMac.Scan(h)!;
        var list = BiosUsbMac.Evaluate(s, new HashSet<string>(), BiosUsb.DefaultMaxBytes);
        // disk7 (monteret uden for /Volumes) og disk12 (APFS med macOS) vises slet ikke
        Assert.Equal(new[] { "disk4", "disk5", "disk6" }, list.Select(d => d.Id));
        var stick = list[0];
        Assert.True(stick.Selectable);
        Assert.Equal(("/dev/disk4", "SanDisk Ultra", "SanDisk", "4C530001"), (stick.Path, stick.Model, stick.Vendor, stick.Serial));
        Assert.Equal(["OLDSTICK FAT"], stick.Labels);
        Assert.Equal(BiosUsbMac.ApfsDisk, list[1].Why);
        Assert.False(list[1].Usable(true, BiosUsb.DefaultMaxBytes));   // stor og APFS: heller ikke med den ekstra bekræftelse
        Assert.Equal(BiosUsbMac.TimeMachineDisk, list[2].Why);
        Assert.True(list[2].Large);   // stor, men afvist af en anden grund – kan ikke vælges med den ekstra bekræftelse
        Assert.False(list[2].Usable(true, BiosUsb.DefaultMaxBytes));
        // ingen kommando må ændre noget, mens diskene læses
        Assert.False(h.Destructive);
        Assert.All(h.Calls.Where(c => c.File == BiosUsbMac.Diskutil), c => Assert.Contains(c.Args[0], new[] { "list", "info", "apfs" }));
    }

    [Fact]
    public void Internal_virtual_and_non_usb_disks_are_hidden_even_if_diskutil_lists_them()
    {
        var h = new FakeMac { Whole = ["disk4"] };
        h.Infos["disk4"] = new("disk4", Internal: true);
        Assert.Empty(BiosUsbMac.Evaluate(BiosUsbMac.Scan(h)!, new HashSet<string>(), BiosUsb.DefaultMaxBytes));
        h.Infos["disk4"] = new("disk4", Kind: "Virtual");
        Assert.Empty(BiosUsbMac.Evaluate(BiosUsbMac.Scan(h)!, new HashSet<string>(), BiosUsb.DefaultMaxBytes));
        h.Infos["disk4"] = new("disk4", Bus: "SATA");
        Assert.Empty(BiosUsbMac.Evaluate(BiosUsbMac.Scan(h)!, new HashSet<string>(), BiosUsb.DefaultMaxBytes));
        h.Infos["disk4"] = new("disk4", OsInternal: true);
        Assert.Empty(BiosUsbMac.Evaluate(BiosUsbMac.Scan(h)!, new HashSet<string>(), BiosUsb.DefaultMaxBytes));
        h.Infos["disk4"] = new("disk4", Removable: false, Ejectable: false);
        Assert.Equal(BiosUsbMac.NotRemovable, Assert.Single(BiosUsbMac.Evaluate(BiosUsbMac.Scan(h)!, new HashSet<string>(), BiosUsb.DefaultMaxBytes)).Why);
    }

    [Fact]
    public void Stick_present_before_the_snapshot_is_not_new_until_it_is_taken_out()
    {
        var h = new FakeMac();
        var now = DateTime.UtcNow;
        var local = new BiosUsbLocal(() => now);
        local.SetBaseline("s", BiosUsbMac.Scan(h)!.Disks.Select(BiosUsbMac.Key));
        Assert.Equal(BiosUsb.NotNew, BiosUsbMac.Evaluate(BiosUsbMac.Scan(h)!, local.Baseline("s"), BiosUsb.DefaultMaxBytes)[0].Why);
        h.Whole = ["disk5", "disk6", "disk7", "disk12"];   // taget ud
        local.Forget("s", BiosUsbMac.Scan(h)!.Disks.Select(BiosUsbMac.Key));
        now += BiosUsbLocal.ForgetAfter;                     // stadig ude efter 5 s (to scanninger)
        local.Forget("s", BiosUsbMac.Scan(h)!.Disks.Select(BiosUsbMac.Key));
        h.Whole = ["disk4", "disk5", "disk6", "disk7", "disk12"];   // sat i igen med samme nummer
        Assert.True(BiosUsbMac.Evaluate(BiosUsbMac.Scan(h)!, local.Baseline("s"), BiosUsb.DefaultMaxBytes)[0].Selectable);
    }

    [Fact]
    public void Large_stick_needs_the_extra_confirmation()
    {
        var h = new FakeMac();
        h.Infos["disk4"] = new("disk4", 512_000_000_000);
        var d = BiosUsbMac.Evaluate(BiosUsbMac.Scan(h)!, new HashSet<string>(), BiosUsb.DefaultMaxBytes)[0];
        Assert.True(d.Large);
        Assert.False(d.Selectable);
        Assert.True(d.Usable(true, BiosUsb.DefaultMaxBytes));
    }

    // ------------------------------------------------------------------ skrivning

    [Fact]
    public void Write_erases_only_the_confirmed_stick_as_fat32_copies_verifies_and_ejects()
    {
        var h = new FakeMac();
        var (o, pkg, local) = Ready(h);
        var rep = Write(h, o, pkg, local);
        Assert.Equal("ok", rep.Outcome);
        Assert.True(rep.Verified);
        Assert.True(rep.Ejected);
        Assert.Equal(LenovoFiles.Length + 1, rep.Files);   // + LÆS-MIG.txt
        var cmds = h.Calls.Where(c => c.File == BiosUsbMac.Diskutil).Select(c => string.Join(' ', c.Args)).ToList();
        Assert.Contains("eraseDisk FAT32 FLEETBIOS MBRFormat /dev/disk4", cmds);
        Assert.Single(cmds, c => c.StartsWith("eraseDisk", StringComparison.Ordinal));
        Assert.Contains("eject /dev/disk4", cmds);
        Assert.Equal(2, cmds.Count(c => c.StartsWith("mount -mountPoint ", StringComparison.Ordinal) && c.EndsWith(" /dev/disk4s1", StringComparison.Ordinal)));
        // efter afmonteringen nævner alle kommandoer, der ikke kun læser (list/info/apfs), kun disk4 og dens partition
        var after = h.Calls.SkipWhile(c => c.Args is not ["unmountDisk", ..]).Where(c => c.File == BiosUsbMac.Diskutil && c.Args[0] is not ("list" or "info" or "apfs"))
            .SelectMany(c => c.Args).Where(a => a.Contains("disk", StringComparison.Ordinal) && !a.StartsWith(_root, StringComparison.Ordinal));
        Assert.All(after, a => Assert.Matches("^(/dev/)?disk4(s1)?$", a));
    }

    [Fact]
    public void Write_formats_large_sticks_as_fat32_too()
    {
        var h = new FakeMac();
        h.Infos["disk4"] = new("disk4", 64_000_000_000);
        var (o, pkg, local) = Ready(h, 64_000_000_000);
        Assert.Equal("ok", Write(h, o, pkg, local).Outcome);
        // som på Linux: én FAT32-partition på 32 GiB, resten fri
        Assert.Contains(h.Calls, c => string.Join(' ', c.Args) == "partitionDisk /dev/disk4 2 MBR FAT32 FLEETBIOS 34359738368B Free Space free R");
        Assert.DoesNotContain(h.Calls, c => c.Args is ["eraseDisk", ..]);
    }

    [Fact]
    public void Disks_over_the_mbr_limit_are_refused_with_a_clear_reason()
    {
        var h = new FakeMac();
        h.Infos["disk4"] = new("disk4", 3_000_000_000_000);
        var d = BiosUsbMac.Evaluate(BiosUsbMac.Scan(h)!, new HashSet<string>(), BiosUsb.DefaultMaxBytes)[0];
        Assert.Equal(BiosUsbMac.TooBigForMbr, d.Why);
        Assert.False(d.Usable(true, BiosUsb.DefaultMaxBytes));
    }

    // ------------------------------------------------------------------ fejl i læsningen må ikke ødelægge øjebliksbilledet

    [Fact]
    public void Scan_fails_instead_of_returning_disks_without_serial_numbers()
    {
        var h = new FakeMac { Override = l => l == "-a -r -c IOUSBHostDevice -l" ? new SpareCmd(1, "") : null };
        Assert.Null(BiosUsbMac.Scan(h));
        h.Override = l => l == "-a -r -c IOUSBHostDevice -l" ? new SpareCmd(124, "") : null;   // timeout
        Assert.Null(BiosUsbMac.Scan(h));
        h.Override = l => l == "-a -r -c IOUSBHostDevice -l" ? new SpareCmd(0, Ioreg()[..400]) : null;   // klippet output
        Assert.Null(BiosUsbMac.Scan(h));
        h.Override = null;
        h.IoregMissing.Add("disk5");   // en USB-disk, ioreg ikke kender
        Assert.Null(BiosUsbMac.Scan(h));
        h.IoregMissing.Clear();
        h.Override = l => l == "info -plist disk5" ? new SpareCmd(1, "") : null;   // en disk, der ikke kan læses
        Assert.Null(BiosUsbMac.Scan(h));
        h.Override = null;
        Assert.NotNull(BiosUsbMac.Scan(h));
    }

    [Fact]
    public void Ioreg_failing_while_polling_keeps_disks_from_before_the_snapshot_old()
    {
        var h = new FakeMac();
        var local = new BiosUsbLocal();
        // ioreg fejler ved øjebliksbilledet: intet øjebliksbillede (agenten melder fejl)
        h.Override = l => l == "-a -r -c IOUSBHostDevice -l" ? new SpareCmd(1, "") : null;
        Assert.Null(BiosUsbMac.Scan(h));
        h.Override = null;
        local.SetBaseline("s", BiosUsbMac.Scan(h)!.Disks.Select(BiosUsbMac.Key));
        // ioreg fejler midt i pollingen: læsningen giver null, så agenten glemmer intet
        h.Override = l => l == "-a -r -c IOUSBHostDevice -l" ? new SpareCmd(0, "ioreg: warning\n" + Ioreg()) : null;
        Assert.Null(BiosUsbMac.Scan(h));
        h.Override = null;
        var s = BiosUsbMac.Scan(h)!;
        local.Forget("s", s.Disks.Select(BiosUsbMac.Key));
        Assert.All(BiosUsbMac.Evaluate(s, local.Baseline("s"), BiosUsb.DefaultMaxBytes), d => Assert.False(d.Usable(true, BiosUsb.DefaultMaxBytes)));
        Assert.Equal(BiosUsb.NotNew, BiosUsbMac.Evaluate(s, local.Baseline("s"), BiosUsb.DefaultMaxBytes)[0].Why);
    }

    // ------------------------------------------------------------------ eksterne harddiske og Time Machine

    [Fact]
    public void External_drive_without_removable_media_needs_the_extra_confirmation()
    {
        var h = new FakeMac();
        h.Infos["disk4"] = new("disk4", 120_000_000_000, "Portable SSD", Removable: false);
        var d = BiosUsbMac.Evaluate(BiosUsbMac.Scan(h)!, new HashSet<string>(), BiosUsb.DefaultMaxBytes)[0];
        Assert.Equal(BiosUsb.ExternalDrive, d.Why);
        Assert.True(d.Large);
        Assert.False(d.Usable(false, BiosUsb.DefaultMaxBytes));
        Assert.True(d.Usable(true, BiosUsb.DefaultMaxBytes));

        var (o, pkg, local) = Ready(h, 120_000_000_000);
        Assert.Equal("refused", Write(h, o, pkg, local, media: "Portable SSD").Outcome);
        Assert.False(h.Destructive);
        o.AllowLarge = true;
        o.ConfirmHash = local.Confirm("s1", "disk4", h.Serial, 120_000_000_000);
        Assert.Equal("ok", Write(h, o, pkg, local, media: "Portable SSD").Outcome);
    }

    [Fact]
    public void Time_machine_check_that_hangs_or_fails_makes_the_disk_unselectable()
    {
        var h = new FakeMac { Override = l => l == "-e /Volumes/OLDSTICK/Backups.backupdb" ? new SpareCmd(124, "") : null };
        var d = BiosUsbMac.Evaluate(BiosUsbMac.Scan(h)!, new HashSet<string>(), BiosUsb.DefaultMaxBytes)[0];
        Assert.Equal(BiosUsbMac.TimeMachineUnknown, d.Why);
        Assert.False(d.Usable(true, BiosUsb.DefaultMaxBytes));
        Assert.DoesNotContain(h.Calls, c => c.Args is ["-e", var p] && !p.StartsWith("/Volumes/", StringComparison.Ordinal));

        // en Time Machine-destination fra tmutil (også uden Backups.backupdb, fx uden Fuld diskadgang)
        h = new FakeMac { Override = l => l == "destinationinfo -X"
            ? new SpareCmd(0, P("<dict><key>Destinations</key><array><dict><key>Kind</key><string>Local</string><key>MountPoint</key><string>/Volumes/OLDSTICK</string></dict></array></dict>")) : null };
        Assert.Equal(BiosUsbMac.TimeMachineDisk, BiosUsbMac.Evaluate(BiosUsbMac.Scan(h)!, new HashSet<string>(), BiosUsb.DefaultMaxBytes)[0].Why);
    }

    [Fact]
    public void Write_refuses_a_swapped_or_unconfirmed_stick_without_touching_it()
    {
        // andet serienummer (et andet stik har fået disk4)
        var h = new FakeMac();
        var (o, pkg, local) = Ready(h);
        h.Serial = "OTHER";
        Assert.Equal("refused", Write(h, o, pkg, local).Outcome);
        Assert.False(h.Destructive);

        // andet medienavn end ved bekræftelsen
        h = new FakeMac();
        (o, pkg, local) = Ready(h);
        Assert.Equal("refused", Write(h, o, pkg, local, media: "Kingston").Outcome);
        Assert.False(h.Destructive);

        // ingen lokal bekræftelse (medienavnet mangler)
        h = new FakeMac();
        (o, pkg, local) = Ready(h);
        Assert.Equal("refused", Write(h, o, pkg, local, media: null).Outcome);
        Assert.False(h.Destructive);

        // forkert kvittering
        h = new FakeMac();
        (o, pkg, local) = Ready(h);
        o.ConfirmHash = new string('0', 64);
        Assert.Equal("refused", Write(h, o, pkg, local).Outcome);
        Assert.False(h.Destructive);

        // stikket var der, før guiden begyndte
        h = new FakeMac();
        (o, pkg, local) = Ready(h);
        local.SetBaseline("s1", BiosUsbMac.Scan(h)!.Disks.Select(BiosUsbMac.Key));
        Assert.Contains(BiosUsb.NotNew, Write(h, o, pkg, local).Message);
        Assert.False(h.Destructive);

        // disknummeret skifter til et andet medie under afmonteringen
        h = new FakeMac();
        (o, pkg, local) = Ready(h);
        h.AfterUnmount = () => h.Infos["disk4"] = new("disk4", Media: "Other stick");
        Assert.Equal("refused", Write(h, o, pkg, local).Outcome);
        Assert.False(h.Destructive);

        // kun serienummeret skifter under afmonteringen (fx to ens stik)
        h = new FakeMac();
        (o, pkg, local) = Ready(h);
        h.AfterUnmount = () => h.Serial = "OTHER";
        Assert.Equal("refused", Write(h, o, pkg, local).Outcome);
        Assert.False(h.Destructive);

        // ioreg fejler efter afmonteringen
        h = new FakeMac();
        (o, pkg, local) = Ready(h);
        h.AfterUnmount = () => h.Override = l => l == "-a -r -c IOUSBHostDevice -l" ? new SpareCmd(1, "") : null;
        Assert.Equal("refused", Write(h, o, pkg, local).Outcome);
        Assert.False(h.Destructive);
    }

    [Theory]
    [InlineData("disk4s1")]
    [InlineData("disk0")]
    [InlineData("/dev/disk4")]
    [InlineData("disk4 disk0")]
    [InlineData("")]
    public void Write_refuses_partitions_paths_and_the_internal_disk(string id)
    {
        var h = new FakeMac();
        var (o, pkg, local) = Ready(h);
        o.DiskId = id;
        Assert.Equal("refused", Write(h, o, pkg, local).Outcome);
        Assert.False(h.Destructive);
    }

    [Fact]
    public void Write_refuses_apfs_and_time_machine_even_with_a_confirmation()
    {
        foreach (var (id, size, why) in new[] { ("disk5", 500_000_000_000L, BiosUsbMac.ApfsDisk), ("disk6", 2_000_000_000_000L, BiosUsbMac.TimeMachineDisk) })
        {
            var h = new FakeMac();
            var (o, pkg, local) = Ready(h);
            o.DiskId = id; o.Size = size; o.Serial = "SER-" + id; o.AllowLarge = true;
            o.ConfirmHash = local.Confirm("s1", id, o.Serial, size);
            local.SetBaseline("s1", []);
            var rep = Write(h, o, pkg, local, media: h.Infos[id].Media);
            Assert.Equal("refused", rep.Outcome);
            Assert.StartsWith(why, rep.Message);
            Assert.False(h.Destructive);
        }
    }

    [Fact]
    public void Write_checks_the_package_before_anything_else()
    {
        var h = new FakeMac();
        var (o, pkg, local) = Ready(h);
        o.Sha256 = new string('a', 64);
        Assert.Equal("failed", Write(h, o, pkg, local).Outcome);
        Assert.Empty(h.Calls);
    }

    [Fact]
    public void Missing_disk_access_gives_a_danish_explanation()
    {
        var h = new FakeMac { Override = l => l.StartsWith("eraseDisk", StringComparison.Ordinal) ? new SpareCmd(1, "Error: -69877: Couldn't open device (Operation not permitted)") : null };
        var (o, pkg, local) = Ready(h);
        var rep = Write(h, o, pkg, local);
        Assert.Equal("failed", rep.Outcome);
        Assert.Equal(BiosUsbMac.MacAccess, rep.Message);
        Assert.Contains("Fuld diskadgang", rep.Message);

        h = new FakeMac { Override = l => l == "unmountDisk /dev/disk4" ? new SpareCmd(1, "Unmount of disk4 failed: at least one volume could not be unmounted") : null };
        (o, pkg, local) = Ready(h);
        rep = Write(h, o, pkg, local);
        Assert.Contains("kunne ikke afmonteres", rep.Message);
        Assert.False(h.Destructive);
    }

    [Fact]
    public void Write_fails_cleanly_when_the_new_partition_is_not_fat32()
    {
        var h = new FakeMac { Override = l => l == "info -plist disk4s1" ? new SpareCmd(0, P("<dict><key>ParentWholeDisk</key><string>disk9</string></dict>")) : null };
        var (o, pkg, local) = Ready(h);
        var rep = Write(h, o, pkg, local);
        Assert.Equal("failed", rep.Outcome);
        Assert.DoesNotContain(h.Calls, c => c.Args is ["mount", ..]);
    }

    [Fact]
    public void Confirmed_media_name_is_single_use_and_expires()
    {
        var t = new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);
        BiosUsbMac.RememberMedia("h1", "SanDisk Ultra", t);
        Assert.Equal("SanDisk Ultra", BiosUsbMac.TakeMedia("h1", t.AddMinutes(1)));
        Assert.Null(BiosUsbMac.TakeMedia("h1", t.AddMinutes(1)));
        BiosUsbMac.RememberMedia("h2", "SanDisk Ultra", t);
        Assert.Null(BiosUsbMac.TakeMedia("h2", t.AddMinutes(6)));
        Assert.Null(BiosUsbMac.TakeMedia(null));
    }
}
