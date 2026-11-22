using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Hubs;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;

namespace Fleetdeck.Tests;

/// <summary>
/// Reservekort på Raspberry Pi. Der findes ingen rigtig kortlæser endnu, så alt køres mod en falsk maskine (FakePi): lsblk-svar,
/// sfdisk, mount og rsync er spillet af testen, og filerne ligger i en midlertidig mappe. Intet her rører en rigtig disk.
/// </summary>
public class SpareCardTests : IDisposable
{
    private readonly string _tmp = Directory.CreateTempSubdirectory("fd-spare-").FullName;
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch (IOException) { } }

    // ------------------------------------------------------------------ valg af kort

    private static BlockDev Disk(string path, string tran, long size, string serial = "", List<string>? mounts = null, params BlockDev[] kids) =>
        new(Path.GetFileName(path), path, "disk", size, tran, serial, "Card Reader", "Generic", "", "dos", "", "", "", mounts ?? [], [.. kids]);
    private static BlockDev Part(string path, string fs, params string[] mounts) =>
        new(Path.GetFileName(path), path, "part", 1_000_000, "", "", "", "", "", "", "", fs, "", [.. mounts], []);

    [Fact]
    public void Only_usb_disks_that_are_not_the_system_card_can_be_chosen()
    {
        var devs = new List<BlockDev>
        {
            Disk("/dev/mmcblk0", "", 32_000_000_000, kids: [Part("/dev/mmcblk0p1", "vfat", "/boot/firmware"), Part("/dev/mmcblk0p2", "ext4", "/")]),
            Disk("/dev/sda", "usb", 32_000_000_000, "000000000819"),
            Disk("/dev/sdb", "sata", 500_000_000_000),
            Disk("/dev/mmcblk1", "usb", 16_000_000_000),   // et kort, der hedder mmcblk*, bruges aldrig – heller ikke på USB
        };
        var byId = new Dictionary<string, string>
        {
            ["usb-Generic_STORAGE_DEVICE_000000000819-0:0"] = "/dev/sda",
            ["usb-Generic_STORAGE_DEVICE_000000000819-0:0-part1"] = "/dev/sda1",
            ["wwn-0x5000"] = "/dev/sda",
            ["mmc-SD32G_0x1234"] = "/dev/mmcblk0",
            ["ata-Disk"] = "/dev/sdb",
            ["usb-Odd"] = "/dev/mmcblk1",
        };
        var c = SpareCard.Candidates(devs, byId);
        var d = Assert.Single(c);
        Assert.Equal("usb-Generic_STORAGE_DEVICE_000000000819-0:0", d.Id);   // usb- frem for wwn-, aldrig -partN
        Assert.Equal("000000000819", d.Serial);
        Assert.Equal("Generic Card Reader · 32 GB", SpareCard.Label(d));
    }

    [Fact]
    public void A_pi_that_boots_from_a_usb_disk_never_offers_that_disk()
    {
        var devs = new List<BlockDev>
        {
            Disk("/dev/sda", "usb", 256_000_000_000, kids: [Part("/dev/sda1", "vfat", "/boot/firmware"), Part("/dev/sda2", "ext4", "/")]),
            Disk("/dev/sdb", "usb", 0),
        };
        var c = SpareCard.Candidates(devs, new Dictionary<string, string> { ["usb-ssd"] = "/dev/sda", ["usb-reader"] = "/dev/sdb" });
        var d = Assert.Single(c);
        Assert.Equal("usb-reader", d.Id);
        Assert.True(d.Empty);
        Assert.Equal("Generic Card Reader · intet kort", SpareCard.Label(d));
    }

    [Theory]
    [InlineData("usb-Generic_STORAGE_DEVICE_000000000819-0:0", true)]
    [InlineData("../sda", false)]
    [InlineData("usb-a/b", false)]
    [InlineData("usb a", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Disk_ids_are_plain_names(string? id, bool ok) => Assert.Equal(ok, SpareCard.SafeId(id));

    // ------------------------------------------------------------------ partitionstabel, cmdline.txt og fstab

    private const string MbrDump = """
        label: dos
        label-id: 0xabcd1234
        device: /dev/mmcblk0
        unit: sectors
        sector-size: 512

        /dev/mmcblk0p1 : start=        8192, size=     1048576, type=c
        /dev/mmcblk0p2 : start=     1056768, size=    61276160, type=83
        """;

    [Fact]
    public void Partition_table_gets_a_new_disk_id_and_the_last_partition_fills_the_card()
    {
        var t = SpareCard.PartitionTable(MbrDump, "5eed0001");
        Assert.Contains("label-id: 0x5eed0001", t);
        Assert.DoesNotContain("abcd1234", t);
        Assert.DoesNotContain("mmcblk0", t);
        Assert.Contains("start=8192, size=1048576, type=c", t);
        Assert.Contains("start=1056768, type=83", t);
        Assert.DoesNotContain("size=61276160", t);
    }

    [Fact]
    public void Gpt_partition_ids_are_left_to_sfdisk()
    {
        var dump = "label: gpt\nlabel-id: 11111111-2222-3333-4444-555555555555\ndevice: /dev/sda\nlast-lba: 999\n\n/dev/sda1 : start=2048, size=1000, type=C12A7328-F81F-11D2-BA4B-00A0C93EC93B, uuid=AAAA\n/dev/sda2 : start=3048, size=5000, type=0FC63DAF-8483-4772-8E79-3D69D8477DE4, uuid=BBBB\n";
        var t = SpareCard.PartitionTable(dump, "5eed0001");
        Assert.DoesNotContain("uuid=", t);
        Assert.DoesNotContain("last-lba", t);
        Assert.DoesNotContain("11111111", t);   // GPT: systemkortets disk-id tages ikke med; sfdisk laver et nyt
    }

    [Fact]
    public void Empty_dump_is_refused() => Assert.Throws<InvalidOperationException>(() => SpareCard.PartitionTable("label: dos\n", "5eed0001"));

    [Fact]
    public void Cmdline_points_root_at_the_spare_card()
    {
        Assert.Equal("console=serial0,115200 console=tty1 root=PARTUUID=5eed0001-02 rootfstype=ext4 fsck.repair=yes rootwait quiet\n",
            SpareCard.FixCmdline("console=serial0,115200 console=tty1 root=PARTUUID=abcd1234-02 rootfstype=ext4 fsck.repair=yes rootwait quiet\n", "5eed0001-02"));
    }

    [Fact]
    public void Cmdline_from_a_netbooted_pi_drops_the_nfs_settings()
    {
        var c = SpareCard.FixCmdline("console=tty1 root=/dev/nfs nfsroot=192.0.2.10:/pi/minpi,vers=4 rw ip=dhcp rootwait", "5eed0001-02");
        Assert.Equal("console=tty1 root=PARTUUID=5eed0001-02 rw rootfstype=ext4 rootwait\n", c);
    }

    [Fact]
    public void Fstab_uses_the_cards_own_partitions_and_keeps_network_lines()
    {
        var fstab = "proc  /proc  proc  defaults  0  0\nPARTUUID=abcd1234-01  /boot/firmware  vfat  defaults  0  2\nPARTUUID=abcd1234-02  /  ext4  defaults,noatime  0  1\n# kommentar\n//192.0.2.10/share  /mnt/share  cifs  noauto  0  0\n";
        var map = new Dictionary<string, string> { ["PARTUUID=abcd1234-01"] = "PARTUUID=5eed0001-01", ["PARTUUID=abcd1234-02"] = "PARTUUID=5eed0001-02" };
        var o = SpareCard.FixFstab(fstab, map, "/boot/firmware", "5eed0001-01", "5eed0001-02");
        Assert.DoesNotContain("abcd1234", o);
        Assert.Contains("PARTUUID=5eed0001-01  /boot/firmware  vfat", o);
        Assert.Contains("PARTUUID=5eed0001-02  /  ext4", o);
        Assert.Contains("//192.0.2.10/share", o);
        Assert.Contains("# kommentar", o);
    }

    [Fact]
    public void Fstab_from_an_nfs_root_gets_a_real_root_and_boot()
    {
        var o = SpareCard.FixFstab("192.0.2.10:/pi/minpi  /  nfs  defaults  0  0\n", new Dictionary<string, string>(), "/boot/firmware", "5eed0001-01", "5eed0001-02");
        Assert.Contains("PARTUUID=5eed0001-02  /  ext4  defaults,noatime  0  1", o);
        Assert.Contains("PARTUUID=5eed0001-01  /boot/firmware  vfat", o);
        Assert.DoesNotContain("nfs", o);
    }

    [Fact]
    public void File_lists_are_compared_without_the_skipped_folders()
    {
        var ex = SpareCard.TreeExclude("/boot/firmware");
        Assert.Contains("proc", ex);
        Assert.Contains("boot/firmware", ex);
        var src = SpareCard.FilterTree(["etc", "etc/hosts", "proc/1/stat", "boot/firmware/kernel8.img", "var/tmp/fleetdeck-spare/x"], ex);
        Assert.Equal(["etc", "etc/hosts"], src);
        var d = SpareCard.CompareLists(src, ["etc"]);
        Assert.Equal(1, d.Missing);
        Assert.True(d.Ok);   // en lille rest er i orden
        var many = Enumerable.Range(0, 100).Select(i => $"f{i}").ToList();
        Assert.False(SpareCard.CompareLists(many, []).Ok);
    }

    [Fact]
    public void Copy_is_stale_after_48_hours()
    {
        var now = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(SpareCard.Stale(now.AddHours(-47), null, now));
        Assert.True(SpareCard.Stale(now.AddHours(-49), null, now));
        Assert.False(SpareCard.Stale(null, now.AddHours(-2), now));   // kortet er lige valgt
        Assert.True(SpareCard.Stale(null, now.AddHours(-50), now));   // valgt for længe siden, ingen kopi er lykkedes
        Assert.False(SpareCard.Stale(null, null, now));
    }

    [Fact]
    public void Netboot_package_never_includes_config_or_cmdline()
    {
        var p = SpareCard.NetbootPaths("/boot/firmware", ["kernel8.img", "kernel_2712.img", "bcm2712-rpi-5-b.dtb", "start4.elf", "fixup4.dat", "config.txt", "cmdline.txt", "issue.txt"], "6.6.51+rpt-rpi-v8");
        Assert.Contains("boot/firmware/kernel8.img", p);
        Assert.Contains("boot/firmware/bcm2712-rpi-5-b.dtb", p);
        Assert.Contains("boot/firmware/overlays", p);
        Assert.Contains("lib/modules/6.6.51+rpt-rpi-v8", p);
        Assert.DoesNotContain(p, x => x.EndsWith("config.txt") || x.EndsWith("cmdline.txt") || x.EndsWith("issue.txt"));
        var a = SpareCard.NetbootSignature("6.6", [("kernel8.img", 10), ("config.txt", 1)]);
        Assert.Equal(a, SpareCard.NetbootSignature("6.6", [("kernel8.img", 10), ("config.txt", 999)]));   // config.txt tæller ikke
        Assert.NotEqual(a, SpareCard.NetbootSignature("6.6", [("kernel8.img", 11)]));
    }

    // ------------------------------------------------------------------ selve kopien på en falsk Pi

    private const string ReaderId = "usb-Generic_STORAGE_DEVICE_000000000819-0:0";
    private static SpareCardJob Job(string? known = null) => new() { DiskId = ReaderId, Serial = "000000000819", KnownDiskId = known };

    [Fact]
    public void First_copy_prepares_the_card_and_fixes_boot_files()
    {
        var pi = new FakePi(_tmp);
        var log = new List<string>();
        var r = SpareCard.Run(pi, Job(), log.Add);

        Assert.Equal("ok", r.Status);
        Assert.True(r.Initialized);
        Assert.True(r.FsckOk);
        Assert.Equal("5eed0001", r.DiskId);
        Assert.Equal("6.6.51+rpt-rpi-v8", r.Kernel);
        // partitionstabellen gik til kortlæseren – med nyt disk-id og uden systemkortets navn
        var sf = Assert.Single(pi.Calls, c => c.File == "sfdisk" && !c.Args.Contains("--dump"));
        Assert.Equal("/dev/sda", sf.Args[^1]);
        Assert.DoesNotContain("abcd1234", sf.Stdin);
        Assert.Contains(pi.Calls, c => c.File == "mkfs.vfat" && c.Args[^1] == "/dev/sda1");
        Assert.Contains(pi.Calls, c => c.File == "mkfs.ext4" && c.Args[^1] == "/dev/sda2");
        // rsync med laveste prioritet
        Assert.Contains(pi.Calls, c => c.File == "nice" && c.Args.Take(4).SequenceEqual(["-n", "19", "ionice", "-c3"]) && c.Args.Contains("rsync") && c.Args.Contains("-aHAXx"));
        // kortets egne PARTUUID'er
        Assert.Contains("root=PARTUUID=5eed0001-02", pi.Read($"{SpareCard.Work}/root/boot/firmware/cmdline.txt"));
        var fstab = pi.Read($"{SpareCard.Work}/root/etc/fstab");
        Assert.Contains("PARTUUID=5eed0001-01", fstab);
        Assert.DoesNotContain("abcd1234", fstab);
        Assert.True(File.Exists(pi.Map($"{SpareCard.Work}/root/boot/firmware/{SpareCard.Marker}")));
        // afmonteret igen, og fsck -n kørt bagefter
        Assert.Equal(2, pi.Calls.Count(c => c.File == "mount"));
        Assert.Equal(2, pi.Calls.Count(c => c.File == "umount"));
        var lastUmount = pi.Calls.FindLastIndex(c => c.File == "umount");
        Assert.True(pi.Calls.FindIndex(c => c.File == "e2fsck" && c.Args.Contains("-n")) > lastUmount);
        AssertSystemCardOnlyRead(pi);
    }

    [Fact]
    public void Next_night_skips_the_preparation()
    {
        var pi = new FakePi(_tmp) { Prepared = true };
        var r = SpareCard.Run(pi, Job(known: "5eed0001"), _ => { });
        Assert.Equal("ok", r.Status);
        Assert.False(r.Initialized);
        Assert.DoesNotContain(pi.Calls, c => c.File is "wipefs" or "mkfs.vfat" or "mkfs.ext4" || (c.File == "sfdisk"));
        AssertSystemCardOnlyRead(pi);
    }

    [Fact]
    public void A_swapped_card_is_prepared_again()
    {
        var pi = new FakePi(_tmp) { Prepared = true };
        var r = SpareCard.Run(pi, Job(known: "0badcafe"), _ => { });
        Assert.Equal("ok", r.Status);
        Assert.True(r.Initialized);
    }

    [Fact]
    public void Never_writes_when_the_chosen_link_points_at_the_system_card()
    {
        var pi = new FakePi(_tmp) { ReaderTarget = "/dev/mmcblk0" };
        var r = SpareCard.Run(pi, Job(), _ => { });
        Assert.Equal("fejl", r.Status);
        Assert.Contains("eget kortstik", r.Message);
        Assert.Empty(pi.Calls);
    }

    [Fact]
    public void Never_writes_to_the_disk_the_pi_runs_from()
    {
        var pi = new FakePi(_tmp) { RootOnReader = true };
        var r = SpareCard.Run(pi, Job(), _ => { });
        Assert.Equal("fejl", r.Status);
        Assert.Contains("Pi'en kører fra", r.Message);
        Assert.All(pi.Calls, c => Assert.Equal("lsblk", c.File));
    }

    [Fact]
    public void A_different_card_serial_stops_the_copy()
    {
        var pi = new FakePi(_tmp) { Serial = "999" };
        var r = SpareCard.Run(pi, Job(), _ => { });
        Assert.Equal("fejl", r.Status);
        Assert.Contains("Serienummeret passer ikke", r.Message);
        Assert.All(pi.Calls, c => Assert.Equal("lsblk", c.File));
    }

    [Fact]
    public void Missing_reader_empty_reader_and_netbooted_pi_give_clear_messages()
    {
        Assert.Contains("Sidder den i Pi'en", SpareCard.Run(new FakePi(_tmp) { ReaderTarget = null }, Job(), _ => { }).Message);
        Assert.Contains("intet kort", SpareCard.Run(new FakePi(_tmp) { CardSize = 0 }, Job(), _ => { }).Message);
        Assert.Contains("over netværket", SpareCard.Run(new FakePi(_tmp) { NfsRoot = true }, Job(), _ => { }).Message);
        Assert.Contains("Intet reservekort", SpareCard.Run(new FakePi(_tmp), new SpareCardJob { DiskId = "../mmcblk0" }, _ => { }).Message);
    }

    [Fact]
    public void A_card_mounted_by_the_desktop_is_left_alone()
    {
        var pi = new FakePi(_tmp) { Prepared = true, ForeignMount = "/media/pi/rootfs" };
        var r = SpareCard.Run(pi, Job(known: "5eed0001"), _ => { });
        Assert.Equal("fejl", r.Status);
        Assert.Contains("/media/pi/rootfs", r.Message);
        Assert.DoesNotContain(pi.Calls, c => c.File is "umount" or "mount");
    }

    [Fact]
    public void Too_small_card_is_refused_before_anything_is_written()
    {
        var pi = new FakePi(_tmp) { CardSize = 2_000_000_000 };
        var r = SpareCard.Run(pi, Job(), _ => { });
        Assert.Contains("for lille", r.Message);
        Assert.DoesNotContain(pi.Calls, c => c.File is "wipefs" or "sfdisk" or "mount");
    }

    [Fact]
    public void Failed_rsync_still_unmounts_and_reports_an_error()
    {
        var pi = new FakePi(_tmp) { Prepared = true, RsyncExit = 23 };
        var r = SpareCard.Run(pi, Job(known: "5eed0001"), _ => { });
        Assert.Equal("fejl", r.Status);
        Assert.Contains("rsync", r.Message);
        Assert.Equal(2, pi.Calls.Count(c => c.File == "umount"));
    }

    [Fact]
    public void Many_missing_files_or_a_bad_fsck_is_not_a_good_copy()
    {
        var pi = new FakePi(_tmp) { Prepared = true, MissingOnCard = 200 };
        Assert.Contains("filer mangler", SpareCard.Run(pi, Job(known: "5eed0001"), _ => { }).Message);
        var pi2 = new FakePi(_tmp) { Prepared = true, FsckExit = 4 };
        var r = SpareCard.Run(pi2, Job(known: "5eed0001"), _ => { });
        Assert.Equal("fejl", r.Status);
        Assert.False(r.FsckOk);
        Assert.Contains("fsck", r.Message);
    }

    /// <summary>Sikkerheden: systemkortet (mmcblk0) må kun læses – sfdisk --dump, aldrig wipefs, sfdisk, mkfs, mount eller fsck.</summary>
    private static void AssertSystemCardOnlyRead(FakePi pi)
    {
        foreach (var c in pi.Calls.Where(c => c.Args.Any(a => a.Contains("mmcblk"))))
            Assert.True(c.File == "sfdisk" && c.Args[0] == "--dump", $"{c.File} {string.Join(' ', c.Args)} rører systemkortet");
        foreach (var c in pi.Calls.Where(c => c.File is "wipefs" or "mkfs.vfat" or "mkfs.ext4" or "mount" or "fsck.vfat" or "e2fsck"))
            Assert.DoesNotContain(c.Args, a => a == "/" || a.StartsWith("/dev/mmcblk"));
    }

    /// <summary>En Raspberry Pi med systemkortet i mmcblk0 og en kortlæser på USB (sda). Kommandoerne spilles; filerne ligger i en temp-mappe.</summary>
    private sealed class FakePi(string root) : ISpareHost
    {
        public sealed record Call(string File, List<string> Args, string Stdin);
        public List<Call> Calls { get; } = [];
        public bool Prepared { get; set; }
        public string? ReaderTarget { get; set; } = "/dev/sda";
        public bool RootOnReader { get; set; }
        public bool NfsRoot { get; set; }
        public string Serial { get; set; } = "000000000819";
        public long CardSize { get; set; } = 32_000_000_000;
        public string? ForeignMount { get; set; }
        public int RsyncExit { get; set; }
        public int MissingOnCard { get; set; }
        public int FsckExit { get; set; }

        public string Map(string p) => Path.Combine(root, p.TrimStart('/'));
        public string Read(string p) => File.ReadAllText(Map(p));

        private string Lsblk()
        {
            object P(string path, string fs, string partuuid, string label, params string[] mounts) =>
                new { name = Path.GetFileName(path), path, type = "part", size = 1_000_000_000L, fstype = fs, partuuid, label, mountpoints = mounts.Length == 0 ? new string?[] { null } : mounts };
            var sys = new List<object>
            {
                P("/dev/mmcblk0p1", "vfat", "abcd1234-01", "bootfs", NfsRoot ? [] : ["/boot/firmware"]),
                P("/dev/mmcblk0p2", "ext4", "abcd1234-02", "rootfs", NfsRoot ? [] : ["/"]),
            };
            var card = Prepared
                ? new List<object> { P("/dev/sda1", "vfat", "5eed0001-01", "bootfs", ForeignMount is null ? [] : []), P("/dev/sda2", "ext4", "5eed0001-02", "rootfs", ForeignMount is null ? [] : [ForeignMount]) }
                : [];
            if (RootOnReader) card = [P("/dev/sda1", "vfat", "x-01", "", "/boot/firmware"), P("/dev/sda2", "ext4", "x-02", "", "/")];
            var devs = new object[]
            {
                new { name = "mmcblk0", path = "/dev/mmcblk0", type = "disk", size = 32_000_000_000L, tran = (string?)null, serial = "0x1234", ptuuid = "abcd1234", pttype = "dos", mountpoints = new string?[] { null }, children = RootOnReader ? [] : sys },
                new { name = "sda", path = "/dev/sda", type = "disk", size = CardSize, tran = "usb", serial = Serial, model = "STORAGE DEVICE", vendor = "Generic", ptuuid = Prepared ? "5eed0001" : "", pttype = "dos", mountpoints = new string?[] { null }, children = card },
            };
            return JsonSerializer.Serialize(new { blockdevices = devs });
        }

        public SpareCmd Run(string file, IReadOnlyList<string> args, string? stdin = null, int timeoutMin = 30)
        {
            Calls.Add(new Call(file, [.. args], stdin ?? ""));
            switch (file)
            {
                case "lsblk": return new(0, Lsblk());
                case "df": return new(0, "     Used\n4000000000\n");
                case "uname": return new(0, "6.6.51+rpt-rpi-v8\n");
                case "sfdisk" when args.Contains("--dump"): return new(0, MbrDump);
                case "sfdisk": Prepared = true; return new(0, "");
                case "e2fsck" or "fsck.vfat": return new(FsckExit, "");
                case "nice" when args.Contains("rsync"):
                    var dest = Map(args[^1]);
                    Directory.CreateDirectory(dest);
                    if (args.Contains("-aHAXx"))
                    {
                        Directory.CreateDirectory(Path.Combine(dest, "etc"));
                        File.WriteAllText(Path.Combine(dest, "etc", "fstab"), "proc  /proc  proc  defaults  0  0\nPARTUUID=abcd1234-01  /boot/firmware  vfat  defaults  0  2\nPARTUUID=abcd1234-02  /  ext4  defaults,noatime  0  1\n");
                    }
                    else File.WriteAllText(Path.Combine(dest, "cmdline.txt"), "console=tty1 root=PARTUUID=abcd1234-02 rootfstype=ext4 rootwait\n");
                    return new(RsyncExit, RsyncExit == 0 ? "" : "rsync error: some files could not be transferred");
                default: return new(0, "");
            }
        }

        public string? ReadText(string path) => File.Exists(Map(path)) ? File.ReadAllText(Map(path)) : null;
        public void WriteText(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(Map(path))!); File.WriteAllText(Map(path), text); }
        public bool Exists(string path) => path == "/boot/firmware/config.txt" || Path.Exists(Map(path));
        public void MakeDir(string path) => Directory.CreateDirectory(Map(path));
        public string? RealPath(string path) => path == $"/dev/disk/by-id/{ReaderId}" ? ReaderTarget : null;
        public string[] ListDir(string path) => [];
        public List<string> ListTree(string r)
        {
            var files = Enumerable.Range(0, 300).Select(i => $"usr/lib/f{i}").Prepend("usr/lib").Prepend("usr").Prepend("etc/fstab").Prepend("etc").ToList();
            return r == "/" ? files : files.Take(files.Count - MissingOnCard).ToList();
        }
    }

    // ------------------------------------------------------------------ server: valg, plan og status

    private sealed class Server : IDisposable
    {
        private readonly SqliteConnection _conn = new("Data Source=:memory:");
        public AppDb Db { get; }
        public SpareCardService Svc { get; }

        public Server(string tmp)
        {
            _conn.Open();
            Db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite(_conn).Options);
            Db.Database.EnsureCreated();
            var live = new Live(new NoHub<UiHub>(), new NoHub<AgentHub>());
            var settings = new SettingsService(Db);
            Svc = new SpareCardService(Db, new JobService(Db, live), new AuditService(Db), settings, live, new Paths(tmp, tmp));
            var health = new HealthReport
            {
                SpareDisks =
                [
                    new SpareDisk { Id = ReaderId, Path = "/dev/sda", Size = 32_000_000_000, Vendor = "Generic", Model = "STORAGE DEVICE", Serial = "000000000819" },
                    new SpareDisk { Id = "usb-empty", Path = "/dev/sdb", Size = 0 },
                    new SpareDisk { Id = "usb-mounted", Path = "/dev/sdc", Size = 16_000_000_000, Mounted = true },
                ],
            };
            Db.Machines.Add(new Machine { Id = 1, Name = "minpi", Platform = Platforms.Linux, FactsJson = Json.Ser(new SystemFacts { PiSerial = "1234abcd", Model = "Raspberry Pi 5 Model B" }), HealthJson = Json.Ser(health) });
            Db.Machines.Add(new Machine { Id = 2, Name = "ARBEJDS-PC", Platform = Platforms.Windows, HealthJson = Json.Ser(health) });
            Db.SaveChanges();
        }

        public void Dispose() { Db.Dispose(); _conn.Dispose(); }
        public int Jobs => Db.Jobs.Count(j => j.Type == JobTypes.SpareCard);
    }

    private static readonly DateTime Night = new(2026, 11, 20, 2, 20, 0);

    [Fact]
    public async Task Nothing_is_copied_by_default()
    {
        using var s = new Server(_tmp);
        await s.Svc.Tick(Night);
        Assert.Equal(0, s.Jobs);
        Assert.Equal("Vælg og bekræft et reservekort først", await s.Svc.Update(1, new(true, null, null), "admin"));
        Assert.Equal("Vælg og bekræft et reservekort først", (await s.Svc.Start(1, "manual", "admin")).Error);
        Assert.Equal(0, s.Jobs);
        Assert.False(new SpareCardState().Enabled);
    }

    [Fact]
    public async Task Choosing_a_card_needs_confirmation_and_a_real_usb_disk()
    {
        using var s = new Server(_tmp);
        Assert.Contains("Bekræft", await s.Svc.Pick(1, new(ReaderId, false), "admin"));
        Assert.Contains("kun på Raspberry Pi", await s.Svc.Pick(2, new(ReaderId, true), "admin"));
        Assert.Contains("ikke på Pi'ens liste", await s.Svc.Pick(1, new("usb-other", true), "admin"));
        Assert.Contains("intet kort", await s.Svc.Pick(1, new("usb-empty", true), "admin"));
        Assert.Contains("monteret", await s.Svc.Pick(1, new("usb-mounted", true), "admin"));
        Assert.Null(await s.Svc.Pick(1, new(ReaderId, true), "admin"));
        var st = await s.Db.SpareCards.AsNoTracking().SingleAsync();
        Assert.Equal("000000000819", st.Serial);
        Assert.False(st.Enabled);   // valgt er ikke det samme som slået til
        Assert.Contains(s.Db.Audit, a => a.Action == "sparecard.choose");
    }

    [Fact]
    public async Task Once_enabled_it_copies_once_a_night()
    {
        using var s = new Server(_tmp);
        await s.Svc.Pick(1, new(ReaderId, true), "admin");
        Assert.Null(await s.Svc.Update(1, new(true, "02:15", null), "admin"));
        await s.Svc.Tick(Night.AddHours(-2));
        Assert.Equal(0, s.Jobs);
        await s.Svc.Tick(Night);
        await s.Svc.Tick(Night.AddMinutes(1));
        Assert.Equal(1, s.Jobs);
        var job = await s.Db.Jobs.SingleAsync();
        var p = Json.Des<JobParams>(job.ParamsJson);
        Assert.Equal(ReaderId, p.Spare!.DiskId);
        Assert.Equal("000000000819", p.Spare.Serial);
        Assert.Equal("schedule", job.Source);
        Assert.Equal("En kopi er allerede i gang", (await s.Svc.Start(1, "manual", "admin")).Error);
    }

    [Fact]
    public async Task Agent_report_is_saved_only_for_its_own_job()
    {
        using var s = new Server(_tmp);
        await s.Svc.Pick(1, new(ReaderId, true), "admin");
        var (job, _) = await s.Svc.Start(1, "manual", "admin");
        var m = await s.Db.Machines.FindAsync(1);
        var other = await s.Db.Machines.FindAsync(2);
        Assert.False(await s.Svc.FromAgent(other!, new SpareCardReport { JobId = job!.Id, Status = "ok" }));
        Assert.False(await s.Svc.FromAgent(m!, new SpareCardReport { JobId = job.Id + 99, Status = "ok" }));
        Assert.True(await s.Svc.FromAgent(m!, new SpareCardReport { JobId = job.Id, Status = "ok", Message = "Kopien er lavet.", DiskId = "5eed0001", Bytes = 4_000_000_000, Kernel = "6.6.51" }));
        var st = await s.Db.SpareCards.AsNoTracking().SingleAsync();
        Assert.Equal("ok", st.Status);
        Assert.Equal("5eed0001", st.KnownDiskId);
        Assert.NotNull(st.LastOk);
    }

    [Fact]
    public async Task Machine_page_view_serializes_for_the_web()
    {
        using var s = new Server(_tmp);
        await s.Svc.Pick(1, new(ReaderId, true), "admin");
        var json = JsonSerializer.Serialize(await s.Svc.View(1), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var j = JsonDocument.Parse(json);
        Assert.True(j.RootElement.GetProperty("isPi").GetBoolean());
        Assert.True(j.RootElement.GetProperty("chosen").GetBoolean());
        Assert.True(j.RootElement.GetProperty("netbootOn").GetBoolean());
        Assert.Equal(JsonValueKind.Object, j.RootElement.GetProperty("netbootSync").ValueKind);
        Assert.Equal(3, j.RootElement.GetProperty("candidates").GetArrayLength());
        Assert.Null(await s.Svc.View(99));
    }

    [Fact]
    public async Task A_job_that_ends_without_a_report_is_shown_as_an_error()
    {
        using var s = new Server(_tmp);
        await s.Svc.Pick(1, new(ReaderId, true), "admin");
        var (job, _) = await s.Svc.Start(1, "manual", "admin");
        await s.Svc.Reconcile();
        Assert.Equal("kører", (await s.Db.SpareCards.AsNoTracking().SingleAsync()).Status);   // stadig i gang
        var j = await s.Db.Jobs.FindAsync(job!.Id);
        j!.Status = JobStatus.Failed; j.Output = "Ukendt jobtype";
        await s.Db.SaveChangesAsync();
        await s.Svc.Reconcile();
        var st = await s.Db.SpareCards.AsNoTracking().SingleAsync();
        Assert.Equal("fejl", st.Status);
        Assert.Contains("Ukendt jobtype", st.Message);
    }

    [Fact]
    public void A_failed_copy_keeps_the_last_good_one()
    {
        var ok = new DateTime(2026, 11, 19, 2, 30, 0, DateTimeKind.Utc);
        var st = new SpareCardState { LastOk = ok, KnownDiskId = "5eed0001", Status = "ok" };
        SpareCardService.Apply(st, new SpareCardReport { Status = "fejl", Message = "rsync fejlede", DiskId = "0badcafe" }, ok.AddDays(1));
        Assert.Equal("fejl", st.Status);
        Assert.Equal(ok, st.LastOk);
        Assert.Equal("5eed0001", st.KnownDiskId);
        SpareCardService.Apply(st, new SpareCardReport { Status = "noget andet" }, ok.AddDays(1));
        Assert.Equal("fejl", st.Status);
    }

    [Fact]
    public void Alarm_only_when_enabled_and_older_than_48_hours()
    {
        var now = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
        var st = new SpareCardState { ConfirmedAt = now.AddDays(-10), LastOk = now.AddHours(-50), Status = "fejl", Message = "Kortlæseren findes ikke" };
        Assert.Null(SpareCardService.Finding(st, now));   // slået fra
        st.Enabled = true;
        var f = SpareCardService.Finding(st, now);
        Assert.NotNull(f);
        Assert.Equal("warning", f.Severity);
        Assert.Contains("50 timer", f.Detail);
        Assert.Contains("Kortlæseren findes ikke", f.Detail);
        st.LastOk = now.AddHours(-3);
        Assert.Null(SpareCardService.Finding(st, now));
        Assert.Null(SpareCardService.Finding(null, now));
    }

    [Fact]
    public void Home_assistant_gets_status_age_and_a_problem_sensor()
    {
        var now = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
        var st = new SpareCardState { Enabled = true, ConfirmedAt = now.AddDays(-5), LastOk = now.AddHours(-49.5), Status = "ok" };
        using var j = JsonDocument.Parse(SpareCardHa.State(st, now));
        Assert.Equal("ok", j.RootElement.GetProperty("status").GetString());
        Assert.Equal(49.5, j.RootElement.GetProperty("age_h").GetDouble());
        Assert.Equal("ON", j.RootElement.GetProperty("stale").GetString());
        var conf = SpareCardHa.Configs("homeassistant", "fleetdeck", 1, "minpi");
        Assert.Equal(4, conf.Count);
        Assert.Contains(conf, c => c.Topic == "homeassistant/binary_sensor/fleetdeck_1/sparecard_stale/config" && c.Payload.Contains("\"problem\""));
        Assert.Equal(SpareCardHa.ConfigTopics("homeassistant", "fleetdeck", 1), conf.Select(c => c.Topic));
        Assert.Equal("slået fra", JsonDocument.Parse(SpareCardHa.State(new SpareCardState(), now)).RootElement.GetProperty("status").GetString());
    }

    // ------------------------------------------------------------------ netboot-nødsystemet

    private string Tar(params (string Name, string Text)[] files)
    {
        var p = Path.Combine(_tmp, $"nb-{Guid.NewGuid():N}.tar.gz");
        using var fs = File.Create(p);
        using var gz = new GZipStream(fs, CompressionLevel.Fastest);
        using var tw = new TarWriter(gz, TarEntryFormat.Pax);
        foreach (var (n, t) in files)
        {
            if (t.StartsWith("->"))
            {
                tw.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, n) { LinkName = t[2..] });
                continue;
            }
            var e = new PaxTarEntry(TarEntryType.RegularFile, n) { DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(t)) };
            tw.WriteEntry(e);
        }
        return p;
    }

    private const string K = "6.6.51+rpt-rpi-v8";

    [Fact]
    public void Netboot_package_only_unpacks_boot_files_overlays_and_this_kernels_modules()
    {
        var tar = Tar(("boot/firmware/kernel8.img", "k"), ("boot/firmware/config.txt", "x"), ("boot/firmware/cmdline.txt", "x"),
            ("boot/firmware/overlays/vc4.dtbo", "o"), ($"lib/modules/{K}/kernel/a.ko", "m"), ("lib/modules/5.0/old.ko", "x"),
            ("boot/firmware/../../etc/passwd", "x"), ("etc/shadow", "x"), ("boot/firmware/evil.dtb", "->/etc/shadow"));
        var stage = Path.Combine(_tmp, "stage");
        var (boot, mods) = NetbootSync.Extract(tar, stage, K);
        Assert.Equal(2, boot);
        Assert.Equal(1, mods);
        Assert.True(File.Exists(Path.Combine(stage, "boot", "kernel8.img")));
        Assert.True(File.Exists(Path.Combine(stage, "boot", "overlays", "vc4.dtbo")));
        Assert.False(File.Exists(Path.Combine(stage, "boot", "config.txt")));
        Assert.False(File.Exists(Path.Combine(stage, "boot", "cmdline.txt")));
        Assert.False(File.Exists(Path.Combine(stage, "boot", "evil.dtb")));
        Assert.Equal(["a.ko"], Directory.GetFiles(Path.Combine(stage, "modules"), "*", SearchOption.AllDirectories).Select(Path.GetFileName));
    }

    [Fact]
    public void Netboot_kernel_is_only_swapped_when_the_modules_can_go_with_it()
    {
        var tftp = Path.Combine(_tmp, "tftp", "1234abcd");
        var nfs = Path.Combine(_tmp, "nfsroot", "1234abcd");
        var tar = Tar(("boot/firmware/kernel8.img", "ny"), ($"lib/modules/{K}/modules.dep", "m"));

        Assert.Equal("sprunget", NetbootSync.Apply(tftp, nfs, tar, K).Status);   // netboot er ikke sat op
        Directory.CreateDirectory(tftp);
        File.WriteAllText(Path.Combine(tftp, "kernel8.img"), "gammel");
        File.WriteAllText(Path.Combine(tftp, "config.txt"), "netboot-opsætning");
        var w = NetbootSync.Apply(tftp, nfs, tar, K);
        Assert.Equal("venter", w.Status);   // ingen NFS-rod: kernen skiftes ikke
        Assert.Equal("gammel", File.ReadAllText(Path.Combine(tftp, "kernel8.img")));

        Directory.CreateDirectory(Path.Combine(nfs, "usr", "lib", "modules", "6.1.0-old"));
        var ok = NetbootSync.Apply(tftp, nfs, tar, K);
        Assert.Equal("ok", ok.Status);
        Assert.Equal("ny", File.ReadAllText(Path.Combine(tftp, "kernel8.img")));
        Assert.Equal("netboot-opsætning", File.ReadAllText(Path.Combine(tftp, "config.txt")));
        Assert.True(File.Exists(Path.Combine(nfs, "usr", "lib", "modules", K, "modules.dep")));
        Assert.True(Directory.Exists(Path.Combine(nfs, "usr", "lib", "modules", "6.1.0-old")));   // andre kerners moduler bliver
        Assert.Equal("fejl", NetbootSync.Apply(tftp, nfs, tar, "../../etc").Status);
        Assert.Equal("fejl", NetbootSync.Apply(tftp, nfs, Tar(("boot/firmware/kernel8.img", "k")), K).Status);   // ingen moduler
    }

    private sealed class NoHub<T> : IHubContext<T> where T : Hub
    {
        public IHubClients Clients { get; } = new NoClients();
        public IGroupManager Groups { get; } = new NoGroups();
    }
    private sealed class NoClients : IHubClients
    {
        private static readonly IClientProxy P = new NoProxy();
        public IClientProxy All => P;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => P;
        public IClientProxy Client(string connectionId) => P;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => P;
        public IClientProxy Group(string groupName) => P;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => P;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => P;
        public IClientProxy User(string userId) => P;
        public IClientProxy Users(IReadOnlyList<string> userIds) => P;
    }
    private sealed class NoProxy : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class NoGroups : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
