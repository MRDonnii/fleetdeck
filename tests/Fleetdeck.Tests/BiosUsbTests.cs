using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services.Bios;
using Fleetdeck.Shared;

namespace Fleetdeck.Tests;

/// <summary>
/// USB-stik med BIOS-opdateringen (BiosUsb, BiosUsbLocal, BiosUsbSessions og LocalWebs /_fd/). Der skrives aldrig til en rigtig
/// disk: alt køres mod en falsk maskine (FakeUsbHost) med lsblk-svar og en midlertidig mappe som "stikket".
/// </summary>
public class BiosUsbTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("fd-bios-usb-").FullName;
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    // ------------------------------------------------------------------ falsk maskine

    private static object Part(string path, string fs, string label, params string[] mounts) =>
        new { name = Path.GetFileName(path), path, type = "part", size = 1_000_000_000L, fstype = fs, label, mountpoints = mounts.Length == 0 ? new string?[] { null } : mounts };

    private static object Disk(string path, string tran, long size, string serial, object[] children, string model = "Flash Disk", string vendor = "Generic") =>
        new { name = Path.GetFileName(path), path, type = "disk", size, tran, serial, model, vendor, mountpoints = new string?[] { null }, children };

    private static string Lsblk(params object[] devs) => JsonSerializer.Serialize(new { blockdevices = devs });

    /// <summary>Systemdisk (NVMe med /, /boot/efi og swap) + et 16 GB USB-stik med en gammel partition.</summary>
    private static List<object> Standard(string stickSerial = "AA11", long stickSize = 16_000_000_000, params string[] stickMounts) =>
    [
        Disk("/dev/nvme0n1", "nvme", 512_000_000_000, "SYS1", [Part("/dev/nvme0n1p1", "vfat", "", "/boot/efi"), Part("/dev/nvme0n1p2", "ext4", "", "/"), Part("/dev/nvme0n1p3", "swap", "", "[SWAP]")]),
        Disk("/dev/sda", "usb", stickSize, stickSerial, [Part("/dev/sda1", "vfat", "OLDSTICK", stickMounts)]),
    ];

    private static readonly Dictionary<string, string> ById = new()
    {
        ["nvme-SYS1"] = "/dev/nvme0n1", ["nvme-SYS1-part1"] = "/dev/nvme0n1p1",
        ["usb-Generic_Flash_Disk_AA11-0:0"] = "/dev/sda", ["usb-Generic_Flash_Disk_AA11-0:0-part1"] = "/dev/sda1",
    };
    private const string StickId = "usb-Generic_Flash_Disk_AA11-0:0";

    private sealed class FakeUsbHost(string mountRoot) : ISpareHost
    {
        public List<object> Devs { get; set; } = Standard();
        public List<(string File, List<string> Args, string? Stdin)> Calls { get; } = [];
        public HashSet<string> Present { get; } = ["/usr/sbin/mkfs.vfat", "/usr/bin/udisksctl"];
        public Action<string>? AfterMount { get; set; }
        public SpareCmd Run(string file, IReadOnlyList<string> args, string? stdin = null, int timeoutMin = 30)
        {
            Calls.Add((file, args.ToList(), stdin));
            if (file == "lsblk") return new(0, Lsblk([.. Devs]));
            if (file == "mount") AfterMount?.Invoke(args[^1]);
            return new(0, "");
        }
        public string? ReadText(string path) => null;
        public void WriteText(string path, string text) { }
        public bool Exists(string path) => Present.Contains(path);
        public void MakeDir(string path) { Assert.StartsWith(mountRoot, path); Directory.CreateDirectory(path); }
        public string? RealPath(string path) => path.StartsWith("/dev/disk/by-id/", StringComparison.Ordinal) && ById.TryGetValue(path[16..], out var t) ? t : null;
        public string[] ListDir(string path) => [.. ById.Keys];
        public List<string> ListTree(string root) => [];
        public bool Destructive => Calls.Any(c => c.File is "wipefs" or "sfdisk" or "mkfs.vfat");
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

    private (BiosUsbOrder Order, string Package, BiosUsbLocal Local) Ready(FakeUsbHost h, string? zip = null)
    {
        var pkg = zip ?? Zip(LenovoFiles);
        var local = new BiosUsbLocal();
        // øjebliksbillede uden stikket, derefter sættes stikket i
        local.SetBaseline("s1", ["/dev/nvme0n1|SYS1"]);
        var hash = local.Confirm("s1", StickId, "AA11", 16_000_000_000);
        var o = new BiosUsbOrder
        {
            SessionId = "s1", Phase = BiosUsbPhases.Write, DiskId = StickId, Serial = "AA11", Size = 16_000_000_000, ConfirmHash = hash,
            Layout = BiosUsbLayouts.LenovoZip, FileName = "q9zjt79usa.zip", Sha256 = BiosUsb.Sha256File(pkg), PackageSize = new FileInfo(pkg).Length,
            Readme = "Trin 1\nTrin 2",
        };
        return (o, pkg, local);
    }

    // ------------------------------------------------------------------ udelukkelser

    private sealed class SeqHost(Dictionary<string, string> seq) : ISpareHost
    {
        public SpareCmd Run(string file, IReadOnlyList<string> args, string? stdin = null, int timeoutMin = 30) => throw new NotSupportedException();
        public string? ReadText(string path) => seq.TryGetValue(path, out var v) ? v + "\n" : null;
        public void WriteText(string path, string text) => throw new NotSupportedException();
        public bool Exists(string path) => false;
        public void MakeDir(string path) { }
        public string? RealPath(string path) => null;
        public string[] ListDir(string path) => [];
        public List<string> ListTree(string root) => [];
    }

    [Fact]
    public void Stick_taken_out_and_put_back_in_counts_as_new()
    {
        // diskseq: samme /dev/sda og samme serie, men kernen tæller sekvensen op ved hver indsættelse
        var host = new SeqHost(new() { ["/sys/block/sda/diskseq"] = "9", ["/sys/block/nvme0n1/diskseq"] = "1" });
        var first = BiosUsb.WithSeq(SpareCard.ParseLsblk(Lsblk([.. Standard()])), host);
        var now = DateTime.UtcNow;
        var local = new BiosUsbLocal(() => now);
        local.SetBaseline("s", first.Where(d => d.Type == "disk").Select(BiosUsb.Key));
        Assert.Equal(BiosUsb.NotNew, Assert.Single(BiosUsb.Evaluate(first, ById, local.Baseline("s"), BiosUsb.DefaultMaxBytes)).Why);
        host = new SeqHost(new() { ["/sys/block/sda/diskseq"] = "12", ["/sys/block/nvme0n1/diskseq"] = "1" });
        var again = BiosUsb.WithSeq(SpareCard.ParseLsblk(Lsblk([.. Standard()])), host);
        Assert.True(Assert.Single(BiosUsb.Evaluate(again, ById, local.Baseline("s"), BiosUsb.DefaultMaxBytes)).Selectable);

        // uden diskseq (ældre kerne): en disk, der forsvinder, glemmes, så den tæller som ny, når den kommer igen
        var plain = SpareCard.ParseLsblk(Lsblk([.. Standard()]));
        local.SetBaseline("t", plain.Where(d => d.Type == "disk").Select(BiosUsb.Key));
        local.Forget("t", plain.Where(d => d.Type == "disk" && d.Path != "/dev/sda").Select(BiosUsb.Key));   // taget ud
        now += BiosUsbLocal.ForgetAfter;
        local.Forget("t", plain.Where(d => d.Type == "disk" && d.Path != "/dev/sda").Select(BiosUsb.Key));   // stadig ude
        Assert.True(Assert.Single(BiosUsb.Evaluate(plain, ById, local.Baseline("t"), BiosUsb.DefaultMaxBytes)).Selectable);
        Assert.Contains("/dev/nvme0n1|SYS1", local.Baseline("t")!);
    }

    [Fact]
    public void Evaluate_hides_system_disk_and_marks_sticks_present_before_baseline()
    {
        var devs = SpareCard.ParseLsblk(Lsblk([.. Standard()]));
        var before = BiosUsb.Evaluate(devs, ById, new HashSet<string> { "/dev/nvme0n1|SYS1", "/dev/sda|AA11" }, BiosUsb.DefaultMaxBytes);
        var d = Assert.Single(before);   // NVMe-systemdisken vises slet ikke
        Assert.Equal("/dev/sda", d.Path);
        Assert.False(d.Selectable);
        Assert.Equal(BiosUsb.NotNew, d.Why);
        Assert.Equal(["OLDSTICK vfat"], d.Labels);

        var after = Assert.Single(BiosUsb.Evaluate(devs, ById, new HashSet<string> { "/dev/nvme0n1|SYS1" }, BiosUsb.DefaultMaxBytes));
        Assert.True(after.Selectable);
        Assert.Equal(StickId, after.Id);
        Assert.Null(after.Why);
        // uden øjebliksbillede kan intet vælges
        Assert.False(BiosUsb.Evaluate(devs, ById, null, BiosUsb.DefaultMaxBytes)[0].Selectable);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/boot")]
    [InlineData("[SWAP]")]
    [InlineData("/srv/data")]
    public void Usb_disk_used_by_the_system_is_never_listed(string mount)
    {
        var devs = SpareCard.ParseLsblk(Lsblk([.. Standard(stickMounts: mount)]));
        Assert.Empty(BiosUsb.Evaluate(devs, ById, new HashSet<string>(), BiosUsb.DefaultMaxBytes));
        Assert.Equal(BiosUsb.SystemDisk, BiosUsb.Refusal(devs[1], devs, new HashSet<string>(), BiosUsb.DefaultMaxBytes, true));
    }

    [Fact]
    public void Stick_mounted_by_the_desktop_under_media_is_allowed()
    {
        var devs = SpareCard.ParseLsblk(Lsblk([.. Standard(stickMounts: "/media/user/OLDSTICK")]));
        Assert.True(Assert.Single(BiosUsb.Evaluate(devs, ById, new HashSet<string>(), BiosUsb.DefaultMaxBytes)).Selectable);
    }

    [Theory]
    [InlineData("crypto_LUKS")]
    [InlineData("LVM2_member")]
    [InlineData("linux_raid_member")]
    public void Usb_disk_with_luks_lvm_or_raid_is_never_listed(string fs)
    {
        var devs = SpareCard.ParseLsblk(Lsblk(Disk("/dev/sda", "usb", 16_000_000_000, "AA11", [Part("/dev/sda1", fs, "")])));
        Assert.Empty(BiosUsb.Evaluate(devs, ById, new HashSet<string>(), BiosUsb.DefaultMaxBytes));
    }

    [Fact]
    public void Non_usb_and_large_disks()
    {
        var devs = SpareCard.ParseLsblk(Lsblk(Disk("/dev/sdb", "sata", 16_000_000_000, "S1", []), Disk("/dev/sda", "usb", 1_000_000_000_000, "AA11", [])));
        Assert.Equal(BiosUsb.NotUsb, BiosUsb.Refusal(devs[0], devs, new HashSet<string>(), BiosUsb.DefaultMaxBytes, true));
        var big = Assert.Single(BiosUsb.Evaluate(devs, ById, new HashSet<string>(), BiosUsb.DefaultMaxBytes));
        Assert.True(big.Large);
        Assert.Equal("Disken er større end 256 GB.", big.Why);
        Assert.Null(BiosUsb.Refusal(devs[1], devs, new HashSet<string>(), BiosUsb.DefaultMaxBytes, allowLarge: true));
    }

    [Fact]
    public void Card_reader_without_media_and_disk_without_stable_name()
    {
        var devs = SpareCard.ParseLsblk(Lsblk(Disk("/dev/sda", "usb", 0, "AA11", []), Disk("/dev/sdc", "usb", 8_000_000_000, "ZZ", [])));
        var list = BiosUsb.Evaluate(devs, ById, new HashSet<string>(), BiosUsb.DefaultMaxBytes);
        Assert.Equal(BiosUsb.NoMedia, list[0].Why);
        Assert.Equal(BiosUsb.NoStableName, list[1].Why);
    }

    [Fact]
    public void Partition_path_and_sfdisk_script()
    {
        Assert.Equal("/dev/sda1", BiosUsb.PartitionPath("/dev/sda"));
        Assert.Equal("/dev/nvme0n1p1", BiosUsb.PartitionPath("/dev/nvme0n1"));
        Assert.Equal("label: dos\ntype=c, bootable\n", BiosUsb.SfdiskScript(16_000_000_000));
        Assert.Contains("size=32GiB", BiosUsb.SfdiskScript(64_000_000_000));
        Assert.Equal(BiosUsbLayouts.LenovoZip, BiosUsb.LayoutFor("lenovo", "usb-zip"));
        Assert.Null(BiosUsb.LayoutFor("lenovo", "win-exe"));
    }

    [Theory]
    [InlineData("../evil", null)]
    [InlineData("/etc/passwd", null)]
    [InlineData("C:/x", null)]
    [InlineData("a/../../b", null)]
    [InlineData("EFI\\BOOT\\BOOTX64.efi", "EFI/BOOT/BOOTX64.efi")]
    [InlineData("./startup.nsh", null)]
    [InlineData("startup.nsh", "startup.nsh")]
    public void Zip_entry_names_are_checked(string name, string? expected) => Assert.Equal(expected, BiosUsb.SafeEntry(name));

    // ------------------------------------------------------------------ lokal bekræftelse

    [Fact]
    public void Local_confirmation_must_match_and_is_used_once()
    {
        var now = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
        var local = new BiosUsbLocal(() => now);
        var h = local.Confirm("s1", "usb-x", "SER", 100);
        Assert.Equal(64, h.Length);
        Assert.False(local.Take("s1", "usb-x", "SER", 100, (h[0] == '0' ? "1" : "0") + h[1..]));   // forkert kvittering – og den er nu brugt
        Assert.False(local.Take("s1", "usb-x", "SER", 100, h));
        h = local.Confirm("s1", "usb-x", "SER", 100);
        Assert.False(local.Take("s1", "usb-x", "OTHER", 100, h));
        h = local.Confirm("s1", "usb-x", "SER", 100);
        Assert.False(local.Take("s1", "usb-x", "SER", 101, h));
        h = local.Confirm("s1", "usb-x", "SER", 100);
        now = now.AddMinutes(6);
        Assert.False(local.Take("s1", "usb-x", "SER", 100, h));   // for gammel
        h = local.Confirm("s1", "usb-x", "SER", 100);
        Assert.True(local.Take("s1", "usb-x", "SER", 100, h));
        Assert.False(local.Take("s1", "usb-x", "SER", 100, h));   // én gang
    }

    // ------------------------------------------------------------------ skrivning

    [Fact]
    public void Write_formats_copies_verifies_and_ejects()
    {
        var h = new FakeUsbHost(_root);
        var (o, pkg, local) = Ready(h);
        var phases = new List<string>();
        var r = BiosUsb.Write(h, o, pkg, local, _ => { }, (_, _, t) => phases.Add(t), _root);
        Assert.Equal("ok", r.Outcome);
        Assert.True(r.Verified);
        Assert.True(r.Ejected);
        Assert.Equal(5, r.Files);   // 4 fra pakken + LÆS-MIG.txt
        var files = h.Calls.Select(c => c.File).ToList();
        Assert.True(files.IndexOf("wipefs") < files.IndexOf("sfdisk") && files.IndexOf("sfdisk") < files.IndexOf("mkfs.vfat") && files.IndexOf("mkfs.vfat") < files.IndexOf("mount"));
        Assert.Contains(h.Calls, c => c.File == "mkfs.vfat" && c.Args.SequenceEqual(["-F", "32", "-n", BiosUsb.VolumeLabel, "/dev/sda1"]));
        Assert.Contains(h.Calls, c => c.File == "sfdisk" && c.Args[^1] == "/dev/sda" && c.Stdin == BiosUsb.SfdiskScript(16_000_000_000));
        Assert.Equal(2, h.Calls.Count(c => c.File == "mount"));   // monteres igen til kontrollen
        Assert.Contains(h.Calls, c => c.File == "udisksctl" && c.Args.SequenceEqual(["power-off", "-b", "/dev/sda"]));
        Assert.DoesNotContain(h.Calls, c => c.Args.Any(a => a.Contains("nvme", StringComparison.Ordinal)));   // systemdisken røres aldrig
        Assert.Equal(["Kontrollerer pakken …", "Formaterer …", "Kopierer …", "Kontrollerer …", "Skubber ud …"], phases);
        // mappen er ryddet op igen, og LÆS-MIG.txt fik Windows-linjeskift
        var dir = h.Calls.First(c => c.File == "mount").Args[^1];
        Assert.Contains(h.Calls, c => c.File == "rmdir" && c.Args[0] == dir);
        var readme = File.ReadAllBytes(Path.Combine(dir, BiosUsb.ReadmeName));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, readme[..3]);
        Assert.Equal("Trin 1\r\nTrin 2", Encoding.UTF8.GetString(readme[3..]));
        Assert.Equal("shell", File.ReadAllText(Path.Combine(dir, "EFI", "BOOT", "BOOTX64.efi")));
    }

    [Fact]
    public void Write_refuses_without_local_confirmation()
    {
        var h = new FakeUsbHost(_root);
        var (o, pkg, local) = Ready(h);
        o.ConfirmHash = new string('a', 64);
        var r = BiosUsb.Write(h, o, pkg, local, _ => { }, null, _root);
        Assert.Equal("refused", r.Outcome);
        Assert.Contains("bekræftet", r.Message);
        Assert.False(h.Destructive);
    }

    [Fact]
    public void Write_refuses_when_another_stick_has_taken_its_place()
    {
        var h = new FakeUsbHost(_root) { Devs = Standard(stickSerial: "BB22") };
        var (o, pkg, local) = Ready(h);
        var r = BiosUsb.Write(h, o, pkg, local, _ => { }, null, _root);
        Assert.Equal("refused", r.Outcome);
        Assert.Contains("ikke det samme", r.Message);
        Assert.False(h.Destructive);
    }

    [Fact]
    public void Write_refuses_a_stick_that_was_in_before_the_baseline()
    {
        var h = new FakeUsbHost(_root);
        var (o, pkg, local) = Ready(h);
        local.SetBaseline("s1", ["/dev/nvme0n1|SYS1", "/dev/sda|AA11"]);
        var r = BiosUsb.Write(h, o, pkg, local, _ => { }, null, _root);
        Assert.Equal("refused", r.Outcome);
        Assert.StartsWith(BiosUsb.NotNew, r.Message);
        Assert.False(h.Destructive);
    }

    [Fact]
    public void Write_refuses_when_the_stick_is_now_mounted_as_system()
    {
        var h = new FakeUsbHost(_root) { Devs = Standard(stickMounts: "/") };
        var (o, pkg, local) = Ready(h);
        Assert.Equal("refused", BiosUsb.Write(h, o, pkg, local, _ => { }, null, _root).Outcome);
        Assert.False(h.Destructive);
    }

    [Fact]
    public void Write_stops_on_wrong_package_checksum_or_missing_mkfs()
    {
        var h = new FakeUsbHost(_root);
        var (o, pkg, local) = Ready(h);
        o.Sha256 = new string('0', 64);
        Assert.Contains("SHA-256", BiosUsb.Write(h, o, pkg, local, _ => { }, null, _root).Message);
        Assert.Empty(h.Calls);   // pakken tjekkes før noget andet

        var h2 = new FakeUsbHost(_root);
        var (o2, pkg2, local2) = Ready(h2);
        h2.Present.Remove("/usr/sbin/mkfs.vfat");
        var r = BiosUsb.Write(h2, o2, pkg2, local2, _ => { }, null, _root);
        Assert.Equal(BiosUsb.MkfsMissing, r.Message);
        Assert.False(h2.Destructive);
    }

    [Fact]
    public void Write_unmounts_desktop_mount_first_and_fails_when_readback_differs()
    {
        var h = new FakeUsbHost(_root) { Devs = Standard(stickMounts: "/media/user/OLDSTICK") };
        var (o, pkg, local) = Ready(h);
        var n = 0;
        h.AfterMount = dir => { if (++n == 2) File.WriteAllText(Path.Combine(dir, "flash2.efi"), "ødelagt"); };   // stikket gemte noget andet
        var r = BiosUsb.Write(h, o, pkg, local, _ => { }, null, _root);
        Assert.Equal("failed", r.Outcome);
        Assert.Contains("passer ikke", r.Message);
        Assert.Equal(("umount", "/dev/sda1"), (h.Calls.First(c => c.File == "umount").File, h.Calls.First(c => c.File == "umount").Args[0]));
        Assert.Contains(h.Calls, c => c.File == "rmdir");
    }

    [Fact]
    public void Extract_refuses_zip_with_path_traversal()
    {
        var zip = Zip(("../../etc/evil", "x"));
        var dir = Directory.CreateDirectory(Path.Combine(_root, "stick")).FullName;
        Assert.Throws<InvalidDataException>(() => BiosUsb.Extract(zip, BiosUsbLayouts.LenovoZip, "p.zip", dir, null));
        Assert.False(File.Exists(Path.Combine(_root, "..", "etc", "evil")));
    }

    [Fact]
    public void Extract_layouts_for_dell_and_hp()
    {
        var file = Path.Combine(_root, "bios.bin");
        File.WriteAllText(file, "image");
        var dell = Directory.CreateDirectory(Path.Combine(_root, "dell")).FullName;
        var hp = Directory.CreateDirectory(Path.Combine(_root, "hp")).FullName;
        Assert.Equal("OptiPlex_1.36.1.exe", Assert.Single(BiosUsb.Extract(file, BiosUsbLayouts.DellExe, "OptiPlex_1.36.1.exe", dell, null)).Path);
        var m = BiosUsb.Extract(file, BiosUsbLayouts.HpBin, "T37_013600.bin", hp, null);
        Assert.Equal("EFI/HP/BIOS/New/T37_013600.bin", m[0].Path);
        Assert.Empty(BiosUsb.Verify(hp, m));
        Assert.Throws<InvalidDataException>(() => BiosUsb.Extract(file, BiosUsbLayouts.DellExe, "../x.exe", dell, null));
    }

    // ------------------------------------------------------------------ LocalWeb /_fd/

    private static LocalWeb.Request Req(string head) => LocalWeb.Parse(Encoding.Latin1.GetBytes(head))!;

    [Fact]
    public void Local_endpoints_require_post_same_origin_and_header()
    {
        const string ok = "POST /_fd/confirm?session=s HTTP/1.1\r\nHost: localhost:8587\r\nOrigin: http://localhost:8587\r\nX-Fleetdeck: 1\r\nContent-Length: 0";
        Assert.True(LocalWeb.IsLocal("/_fd/claim?nonce=x"));
        Assert.False(LocalWeb.IsLocal("/api/_fd/claim"));
        Assert.Null(LocalWeb.LocalRefuse(Req(ok), 8587));
        Assert.Equal(405, LocalWeb.LocalRefuse(Req(ok.Replace("POST", "GET")), 8587)!.Status);
        Assert.Equal(403, LocalWeb.LocalRefuse(Req(ok.Replace("\r\nOrigin: http://localhost:8587", "")), 8587)!.Status);   // ingen Origin = navigation
        Assert.Equal(403, LocalWeb.LocalRefuse(Req(ok.Replace("http://localhost:8587", "http://localhost:3000")), 8587)!.Status);
        Assert.Equal(403, LocalWeb.LocalRefuse(Req(ok.Replace("\r\nX-Fleetdeck: 1", "")), 8587)!.Status);
        Assert.Equal(400, LocalWeb.LocalRefuse(Req(ok.Replace("Content-Length: 0", "Content-Length: 5")), 8587)!.Status);
    }

    [Fact]
    public void Local_target_is_split_and_decoded()
    {
        var (path, q) = LocalWeb.SplitTarget("/_fd/confirm?session=abc&disk=usb-Generic_Flash%3A0&size=16000000000&disk=other");
        Assert.Equal("/_fd/confirm", path);
        Assert.Equal("usb-Generic_Flash:0", q["disk"]);
        Assert.Equal("16000000000", q["size"]);
        Assert.Equal("/_fd/claim", LocalWeb.SplitTarget("/_fd/claim").Path);
    }

    // ------------------------------------------------------------------ serverens session

    private static Machine M(int id, string name, string platform = "linux") => new() { Id = id, Name = name, Platform = platform };

    [Fact]
    public void Session_nonce_is_short_lived_and_single_use()
    {
        var now = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
        var usb = new BiosUsbSessions(() => now);
        var s = usb.Create(1, "reception-pc", "lenovo", BiosUsbLayouts.LenovoZip, "Q9ZKT79A", 7, "admin");
        Assert.Null(usb.Claim("forkert-kode-1234567890", M(2, "pc-a")));
        now = now.AddMinutes(3);
        Assert.Null(usb.Claim(s.Nonce, M(2, "pc-a")));   // udløbet
        var n = usb.Renew(s.Id)!;
        var c = usb.Claim(n, M(2, "pc-a"));
        Assert.Equal(2, c!.ThisId);
        Assert.Equal("pc-a", c.ThisName);
        Assert.Null(usb.Claim(n, M(3, "pc-b")));   // én gang
        Assert.Null(usb.Renew(s.Id));   // pc'en er fundet
        now = now.AddHours(2);
        Assert.Null(usb.Get(s.Id));   // sessionen udløber
    }

    [Fact]
    public void Write_or_list_job_that_ends_without_a_result_does_not_leave_the_wizard_waiting()
    {
        var usb = new BiosUsbSessions();
        var s = usb.Create(1, "reception-pc", "lenovo", BiosUsbLayouts.LenovoZip, "Q9ZKT79A", 7, "admin");
        usb.Claim(s.Nonce, M(2, "pc-a"));
        s.Disks = [new BiosUsbDisk { Id = StickId, Serial = "AA11", Size = 16_000_000_000, Selectable = true }];
        s.WriteJobId = 10; s.ListJobId = 9;
        var write = new Job { Id = 10, Status = JobStatus.Running };
        usb.Settle(s, null, write);
        Assert.Equal("Stikket er ved at blive skrevet.", BiosUsbSessions.CannotWrite(s, StickId, "AA11", 16_000_000_000, false));
        write.Status = JobStatus.Cancelled;   // annulleret, eller agenten genstartede og jobbet blev lukket
        usb.Settle(s, new Job { Id = 9, Status = JobStatus.Expired, Created = DateTime.UtcNow }, write);
        Assert.Equal("failed", s.Result!.Outcome);
        Assert.Contains("halvt formateret", s.Result.Message);
        Assert.Null(BiosUsbSessions.CannotWrite(s, StickId, "AA11", 16_000_000_000, false));   // kan prøves igen med det samme
        Assert.Contains("udløb", s.ListMessage);
    }

    [Fact]
    public void Server_only_writes_a_disk_from_the_pcs_own_list()
    {
        var usb = new BiosUsbSessions();
        var s = usb.Create(1, "reception-pc", "lenovo", BiosUsbLayouts.LenovoZip, "Q9ZKT79A", 7, "admin");
        Assert.Equal("Denne pc er ikke genkendt endnu.", BiosUsbSessions.CannotWrite(s, StickId, "AA11", 16_000_000_000, false));
        usb.Claim(s.Nonce, M(2, "pc-a"));
        s.Disks =
        [
            new BiosUsbDisk { Id = StickId, Serial = "AA11", Size = 16_000_000_000, Selectable = true },
            new BiosUsbDisk { Id = "usb-old", Serial = "OLD", Size = 8_000_000_000, Why = BiosUsb.NotNew },
            new BiosUsbDisk { Id = "usb-big", Serial = "BIG", Size = 1_000_000_000_000, Large = true, Why = BiosUsb.TooLarge(BiosUsb.DefaultMaxBytes) },
        ];
        Assert.Null(BiosUsbSessions.CannotWrite(s, StickId, "AA11", 16_000_000_000, false));
        Assert.NotNull(BiosUsbSessions.CannotWrite(s, StickId, "AA11", 15_000_000_000, false));
        Assert.Equal(BiosUsb.NotNew, BiosUsbSessions.CannotWrite(s, "usb-old", "OLD", 8_000_000_000, true));
        Assert.NotNull(BiosUsbSessions.CannotWrite(s, "usb-big", "BIG", 1_000_000_000_000, false));
        Assert.Null(BiosUsbSessions.CannotWrite(s, "usb-big", "BIG", 1_000_000_000_000, true));   // kun med ekstra bekræftelse
        Assert.NotNull(BiosUsbSessions.CannotWrite(s, "usb-none", "X", 1, true));
    }

    [Fact]
    public void Target_steps_follow_vendor_and_platform()
    {
        var lin = BiosUsbSessions.TargetSteps("lenovo", "linux");
        Assert.Contains(lin, x => x.Contains("Secure Boot fra"));
        Assert.Contains(lin, x => x.Contains("LUKS"));
        Assert.DoesNotContain(lin, x => x.Contains("BitLocker"));
        Assert.Contains(BiosUsbSessions.TargetSteps("lenovo", "windows"), x => x.Contains("BitLocker"));
        Assert.Contains(BiosUsbSessions.TargetSteps("dell", "windows"), x => x.Contains("BIOS Flash Update"));
    }

    [Fact]
    public void Order_round_trips_in_job_params()
    {
        var p = new JobParams { BiosUsb = new BiosUsbOrder { SessionId = "s1", Phase = BiosUsbPhases.Write, DiskId = StickId, Size = 5, ConfirmHash = Convert.ToHexStringLower(SHA256.HashData([1])) } };
        var back = JsonSerializer.Deserialize<JobParams>(JsonSerializer.Serialize(p, new JsonSerializerOptions(JsonSerializerDefaults.Web)), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(StickId, back.BiosUsb!.DiskId);
        Assert.Equal(BiosUsb.DefaultMaxBytes, back.BiosUsb.MaxBytes);
    }
}
