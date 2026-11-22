using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Fleetdeck.Shared;

namespace Fleetdeck.Tests;

/// <summary>
/// USB-stik med BIOS-opdateringen fra en Windows-pc (BiosUsbWin). Der køres aldrig PowerShell og skrives aldrig til en rigtig disk:
/// alt køres mod en falsk Windows-maskine (FakeWinHost) med Get-Disk-uddata og en midlertidig mappe som "drev E:".
/// </summary>
public class BiosUsbWinTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("fd-bios-usb-win-").FullName;
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    // ------------------------------------------------------------------ falsk Windows-maskine

    private static object Part(string letter, string fs, string label, bool system = false, bool boot = false, bool windows = false,
        bool page = false, bool sysDrive = false, int blProt = -1, int blConv = -1, long size = 1_000_000_000) =>
        new { Letter = letter, Size = size, IsSystem = system, IsBoot = boot, FileSystem = fs, Label = label, Windows = windows, PageFile = page,
            SystemDrive = sysDrive, BlProt = blProt, BlConv = blConv };

    private static object Disk(int n, int bus, long size, string serial, string uid, object[] parts, bool system = false, bool boot = false,
        bool ro = false, bool offline = false, string model = "SanDisk Cruzer Blade USB Device") =>
        new { Number = n, Bus = bus == 7 ? "USB" : "NVMe", BusId = bus, Size = size, Model = model, Vendor = "", Serial = serial, UniqueId = uid,
            DevicePath = $@"\\?\usbstor#disk&ven_sandisk&prod_cruzer_blade&rev_1.00#{serial}&0#{{53f56307-b6bf-11d0-94f2-00a0c91efb8b}}",
            IsSystem = system, IsBoot = boot, IsOffline = offline, IsReadOnly = ro, Parts = parts };

    private static object SystemNvme() => Disk(0, 17, 512_000_000_000, "SYS1", "eui.0025385b71b0a1b2",
        [Part("", "FAT32", "", system: true), Part("C", "NTFS", "Windows", boot: true, windows: true, page: true, sysDrive: true)], system: true, boot: true,
        model: "Samsung SSD 980");

    private const string StickUid = @"USBSTOR\DISK&VEN_SANDISK&PROD_CRUZER_BLADE&REV_1.00\4C530001000000000001&0:PC1";

    private static object Stick(string serial = "4C530001000000000001", long size = 16_000_000_000, int n = 1, string uid = StickUid, params object[] parts) =>
        Disk(n, 7, size, serial, uid, parts.Length > 0 ? parts : [Part("E", "FAT32", "OLDSTICK")]);

    private static WinDisk W(object disk) => BiosUsbWin.ParseList(JsonSerializer.Serialize(new[] { disk }))![0];

    private sealed class FakeWinHost(string drive) : IWinUsbHost
    {
        public List<object> Disks { get; set; } = [SystemNvme(), Stick()];
        public List<string> Prepares { get; } = [];
        public string? PrepareOutput { get; set; }
        public bool PrepareTimesOut { get; set; }
        public int Dismounts { get; private set; }
        public Action<int>? OnDismount { get; set; }
        public List<string> Ejected { get; } = [];
        public bool ListFails { get; set; }

        public Task<WinPs> PowerShell(string script, TimeSpan timeout, CancellationToken ct)
        {
            if (script == BiosUsbWin.ListScript)
                return Task.FromResult(ListFails ? new WinPs(1, "Get-Disk : Access denied") : new WinPs(0, "\uFEFF" + JsonSerializer.Serialize(Disks) + "\r\n"));
            Prepares.Add(script);
            if (PrepareTimesOut) return Task.FromResult(new WinPs(null, "", TimedOut: true));
            var stick = BiosUsbWin.ParseList(JsonSerializer.Serialize(Disks))!.First(d => d.BusId == 7 && !BiosUsbWin.InUse(d));
            return Task.FromResult(new WinPs(0, PrepareOutput ?? $"FDOK|E|FAT32|{BiosUsb.VolumeLabel}|{Math.Min(stick.Size, BiosUsbWin.FatMaxBytes)}|{BiosUsbWin.Fingerprint(stick)}\r\n"));
        }
        public string Root(string letter) { Assert.Equal("E", letter); Directory.CreateDirectory(drive); return drive; }
        public string? Dismount(string letter) { Dismounts++; OnDismount?.Invoke(Dismounts); return null; }
        public bool Eject(string devicePath) { Ejected.Add(devicePath); return true; }
        public bool Destructive => Prepares.Count > 0;
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

    private string Drive => Path.Combine(_root, "E");

    /// <summary>Øjebliksbillede uden stikket, derefter er stikket sat i og bekræftet lokalt.</summary>
    private (BiosUsbOrder Order, string Package, BiosUsbLocal Local) Ready(FakeWinHost h)
    {
        var pkg = Zip(LenovoFiles);
        var local = new BiosUsbLocal();
        local.SetBaseline("s1", [BiosUsbWin.Key(W(SystemNvme()))]);
        var stick = W(Stick());
        var id = BiosUsbWin.IdOf(stick);
        var o = new BiosUsbOrder
        {
            SessionId = "s1", Phase = BiosUsbPhases.Write, DiskId = id, Serial = stick.Serial, Size = stick.Size,
            ConfirmHash = local.Confirm("s1", id, stick.Serial, stick.Size),
            Layout = BiosUsbLayouts.LenovoZip, FileName = "m1ujt79usa.zip", Sha256 = BiosUsb.Sha256File(pkg), PackageSize = new FileInfo(pkg).Length,
            Readme = "Trin 1\nTrin 2",
        };
        return (o, pkg, local);
    }

    // ------------------------------------------------------------------ disklisten

    [Fact]
    public void List_output_from_windows_powershell_is_parsed()
    {
        // som Windows PowerShell 5.1 kan skrive det: BOM, en advarsel foran, en enkelt partition som objekt og en liste som {value, Count}
        const string ps = "WARNING: noget\r\n[{\"Number\":0,\"Bus\":\"NVMe\",\"BusId\":17,\"Size\":512110190592,\"Model\":\"Samsung SSD 980\",\"Vendor\":null,"
            + "\"Serial\":\"0025_385B\",\"UniqueId\":\"eui.0025\",\"DevicePath\":\"\\\\\\\\?\\\\scsi#disk\",\"IsSystem\":true,\"IsBoot\":true,\"IsOffline\":false,\"IsReadOnly\":false,"
            + "\"Parts\":{\"value\":[{\"Letter\":\"\",\"Size\":104857600,\"IsSystem\":true,\"IsBoot\":false,\"FileSystem\":\"FAT32\",\"Label\":\"\",\"Windows\":false,\"PageFile\":false,\"SystemDrive\":false,\"BlProt\":-1,\"BlConv\":-1},"
            + "{\"Letter\":\"C\",\"Size\":511000000000,\"IsSystem\":false,\"IsBoot\":true,\"FileSystem\":\"NTFS\",\"Label\":\"Windows\",\"Windows\":true,\"PageFile\":true,\"SystemDrive\":true,\"BlProt\":1,\"BlConv\":1}],\"Count\":2}},"
            + "{\"Number\":2,\"Bus\":\"USB\",\"BusId\":\"7\",\"Size\":\"15376000000\",\"Model\":\"Kingston DataTraveler 3.0 USB Device\",\"Vendor\":\"Kingston\","
            + "\"Serial\":\"  E0D55EA5  \",\"UniqueId\":\"USBSTOR\\\\DISK&VEN_KINGSTON\\\\E0D55EA5&0\",\"DevicePath\":\"\\\\\\\\?\\\\usbstor#disk&ven_kingston#e0d55ea5&0#{53f56307-b6bf-11d0-94f2-00a0c91efb8b}\","
            + "\"IsSystem\":false,\"IsBoot\":false,\"IsOffline\":false,\"IsReadOnly\":false,"
            + "\"Parts\":{\"Letter\":\"f\",\"Size\":15375000000,\"IsSystem\":false,\"IsBoot\":false,\"FileSystem\":\"exFAT\",\"Label\":\"KINGSTON\",\"Windows\":false,\"PageFile\":false,\"SystemDrive\":false,\"BlProt\":0,\"BlConv\":0}}]\r\n";
        var disks = BiosUsbWin.ParseList("\uFEFF" + ps)!;
        Assert.Equal(2, disks.Count);
        Assert.Equal(2, disks[0].Parts.Count);
        Assert.True(disks[0].Parts[1].BitLocker);
        Assert.True(BiosUsbWin.InUse(disks[0]));
        var k = disks[1];
        Assert.Equal((2, 7, 15_376_000_000L, "E0D55EA5"), (k.Number, k.BusId, k.Size, k.Serial));
        Assert.Equal("F", Assert.Single(k.Parts).Letter);
        Assert.False(k.Parts[0].BitLocker);
        Assert.StartsWith(@"\\?\usbstor#", k.DevicePath);
        Assert.Null(BiosUsbWin.ParseList("Get-Disk : Adgang nægtet"));
        Assert.Empty(BiosUsbWin.ParseList("[]")!);
    }

    [Fact]
    public void Evaluate_hides_system_disks_and_marks_sticks_present_before_baseline()
    {
        var all = BiosUsbWin.ParseList(JsonSerializer.Serialize(new[]
        {
            SystemNvme(),
            Stick(),
            // Windows To Go / en USB-disk med Windows eller sidefilen må aldrig vises
            Disk(3, 7, 64_000_000_000, "WTG", "USBSTOR\\WTG", [Part("G", "NTFS", "", windows: true)]),
            Disk(4, 7, 64_000_000_000, "PF", "USBSTOR\\PF", [Part("H", "NTFS", "", page: true)]),
            Disk(5, 7, 64_000_000_000, "BOOT", "USBSTOR\\BOOT", [], boot: true),
            Disk(6, 11, 2_000_000_000_000, "SATA", "SATA\\X", []),   // ikke USB
        }))!;
        var before = BiosUsbWin.Evaluate(all, null, BiosUsb.DefaultMaxBytes);
        var only = Assert.Single(before);
        Assert.Equal(BiosUsb.NotNew, only.Why);   // intet øjebliksbillede = intet kan vælges
        Assert.Equal(@"\\.\PhysicalDrive1", only.Path);
        Assert.Equal("SanDisk Cruzer Blade", only.Model);
        Assert.Equal(["E: OLDSTICK FAT32"], only.Labels);
        Assert.Matches("^win-1-[0-9a-f]{12}$", only.Id);
        Assert.True(SpareCard.SafeId(only.Id));

        var after = BiosUsbWin.Evaluate(all, new HashSet<string> { BiosUsbWin.Key(all[0]) }, BiosUsb.DefaultMaxBytes);
        Assert.True(Assert.Single(after).Selectable);
    }

    [Fact]
    public void Bitlocker_read_only_offline_and_large_sticks_cannot_be_chosen()
    {
        var baseline = new HashSet<string>();
        string? Why(object d) => BiosUsbWin.Evaluate([W(d)], baseline, BiosUsb.DefaultMaxBytes).Single().Why;
        Assert.Equal(BiosUsbWin.BitLocker, Why(Stick(parts: Part("E", "", "", blProt: 2))));   // låst BitLocker To Go
        Assert.Equal(BiosUsbWin.BitLocker, Why(Stick(parts: Part("E", "FAT32", "", blProt: 0, blConv: 1))));   // ved at blive krypteret
        Assert.Null(Why(Stick(parts: Part("E", "FAT32", "", blProt: 0, blConv: 0))));
        Assert.Equal(BiosUsbWin.ReadOnly, Why(Disk(1, 7, 16_000_000_000, "S", "U", [], ro: true)));
        Assert.Equal(BiosUsbWin.Offline, Why(Disk(1, 7, 16_000_000_000, "S", "U", [], offline: true)));
        Assert.Equal(BiosUsb.NoMedia, Why(Disk(1, 7, 0, "", "U", [])));
        var big = BiosUsbWin.Evaluate([W(Stick(size: 1_000_000_000_000))], baseline, BiosUsb.DefaultMaxBytes).Single();
        Assert.Equal(BiosUsb.TooLarge(BiosUsb.DefaultMaxBytes), big.Why);
        Assert.True(big.Large && big.Usable(true, BiosUsb.DefaultMaxBytes) && !big.Usable(false, BiosUsb.DefaultMaxBytes));
    }

    [Fact]
    public void Stick_taken_out_and_put_back_in_counts_as_new()
    {
        var now = DateTime.UtcNow;
        var local = new BiosUsbLocal(() => now);
        var stick = W(Stick());
        local.SetBaseline("s", [BiosUsbWin.Key(W(SystemNvme())), BiosUsbWin.Key(stick)]);
        Assert.Equal(BiosUsb.NotNew, BiosUsbWin.Refusal(stick, local.Baseline("s"), BiosUsb.DefaultMaxBytes, false));
        local.Forget("s", [BiosUsbWin.Key(W(SystemNvme()))]);   // taget ud
        now += BiosUsbLocal.ForgetAfter;
        local.Forget("s", [BiosUsbWin.Key(W(SystemNvme()))]);   // stadig ude efter 5 s
        Assert.Null(BiosUsbWin.Refusal(stick, local.Baseline("s"), BiosUsb.DefaultMaxBytes, false));   // sat i igen
        Assert.NotNull(local.Baseline("s")!.FirstOrDefault(k => k.StartsWith("win0|", StringComparison.Ordinal)));   // systemdisken huskes
    }

    [Fact]
    public void Disk_missing_from_a_single_scan_is_not_forgotten()
    {
        // fx en USB-backupdisk, der kortvarigt genopremses (selective suspend, ustabil hub) eller mangler i Storage-cachen
        var now = DateTime.UtcNow;
        var local = new BiosUsbLocal(() => now);
        var sys = BiosUsbWin.Key(W(SystemNvme()));
        var backup = W(Stick());
        local.SetBaseline("s", [sys, BiosUsbWin.Key(backup)]);
        var before = local.Baseline("s")!;

        local.Forget("s", [sys]);                                   // mangler i én scanning
        Assert.Equal(BiosUsb.NotNew, BiosUsbWin.Refusal(backup, local.Baseline("s"), BiosUsb.DefaultMaxBytes, false));
        now += TimeSpan.FromSeconds(10);
        local.Forget("s", [sys, BiosUsbWin.Key(backup)]);           // er tilbage: tællingen nulstilles
        local.Forget("s", [sys]);                                   // mangler igen, men kun én gang i træk
        Assert.Equal(BiosUsb.NotNew, BiosUsbWin.Refusal(backup, local.Baseline("s"), BiosUsb.DefaultMaxBytes, false));

        // to scanninger i træk, men inden for 5 s: huskes stadig
        now += TimeSpan.FromSeconds(2);
        local.Forget("s", [sys]);
        Assert.Equal(BiosUsb.NotNew, BiosUsbWin.Refusal(backup, local.Baseline("s"), BiosUsb.DefaultMaxBytes, false));
        // ude i mindst 5 s og flere scanninger: glemt
        now += TimeSpan.FromSeconds(3);
        local.Forget("s", [sys]);
        Assert.Null(BiosUsbWin.Refusal(backup, local.Baseline("s"), BiosUsb.DefaultMaxBytes, false));
        Assert.Contains(sys, local.Baseline("s")!);
        // det gamle øjebliksbillede, en læser holdt, ændres aldrig (Forget erstatter det)
        Assert.Contains(BiosUsbWin.Key(backup), before);
    }

    [Theory]
    [InlineData(16_000_000_000L, 0L)]
    [InlineData(BiosUsbWin.FatMaxBytes, 0L)]
    [InlineData(BiosUsbWin.FatMaxBytes + 1024 * 1024, 0L)]
    [InlineData(BiosUsbWin.FatMaxBytes + BiosUsbWin.PartitionSlack - 1, 0L)]
    [InlineData(BiosUsbWin.FatMaxBytes + BiosUsbWin.PartitionSlack, BiosUsbWin.FatMaxBytes)]
    [InlineData(64_000_000_000L, BiosUsbWin.FatMaxBytes)]
    public void Partition_size_leaves_room_for_the_1_MiB_offset(long disk, long expected) =>
        Assert.Equal(expected, BiosUsbWin.PartitionSize(disk));

    [Fact]
    public async Task List_takes_baseline_first_and_then_waits_for_a_new_stick()
    {
        var h = new FakeWinHost(Drive);
        var local = new BiosUsbLocal();
        var o = new BiosUsbOrder { SessionId = "s1" };
        var first = (await BiosUsbWin.List(h, o, local, CancellationToken.None))!;
        Assert.True(first.Baseline);
        Assert.Equal(BiosUsb.NotNew, Assert.Single(first.Disks!).Why);
        // stikket sidder stadig i: venter, men finder intet nyt
        var none = (await BiosUsbWin.List(h, o, local, CancellationToken.None, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(10)))!;
        Assert.Equal("Intet nyt stik fundet.", none.Message);
        // et andet stik sættes i
        h.Disks = [SystemNvme(), Stick(), Stick(serial: "NEW2", n: 2, uid: "USBSTOR\\NEW2")];
        var found = (await BiosUsbWin.List(h, o, local, CancellationToken.None, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(10)))!;
        Assert.Equal("Stikket er fundet.", found.Message);
        Assert.True(found.Disks!.Single(d => d.Serial == "NEW2").Selectable);
        h.ListFails = true;
        Assert.Null(await BiosUsbWin.List(h, o, local, CancellationToken.None));
    }

    // ------------------------------------------------------------------ klargøringsscriptet

    [Fact]
    public void Prepare_script_checks_the_disk_again_before_it_erases_anything()
    {
        var stick = W(Stick(size: 64_000_000_000));
        var fp = BiosUsbWin.Fingerprint(stick);
        var s = BiosUsbWin.PrepareScript(stick.Number, fp, BiosUsbWin.PartitionSize(stick.Size));
        Assert.Contains("$n = 1\n", s.ReplaceLineEndings("\n"));
        Assert.Contains($"-ne '{fp}'", s);
        Assert.Contains("-Size 33554432000 -IsActive -MbrType FAT32", s);   // højst 32000 MiB på et 64 GB-stik
        Assert.Contains($"-FileSystem FAT32 -NewFileSystemLabel '{BiosUsb.VolumeLabel}'", s);
        Assert.Contains("-PartitionStyle MBR", s);
        Assert.Contains("'BusType') -ne 7", s);
        var erase = s.IndexOf("Clear-Disk", StringComparison.Ordinal);
        foreach (var check in new[] { "Fail 'changed'", "Fail 'unsafe'", "$p.IsSystem -or $p.IsBoot" })
            Assert.True(s.IndexOf(check, StringComparison.Ordinal) is > 0 and var i && i < erase, check);
        Assert.True(s.IndexOf("Format-Volume", StringComparison.Ordinal) < s.IndexOf("AssignDriveLetter", StringComparison.Ordinal));   // bogstav først efter formatering
        // kun tal og hex fra jobbet – aldrig stikkets serie eller navn
        Assert.DoesNotContain(stick.Serial, s);
        Assert.DoesNotContain("SanDisk", s, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-UseMaximumSize", BiosUsbWin.PrepareScript(1, fp, BiosUsbWin.PartitionSize(16_000_000_000)));
        Assert.Throws<ArgumentException>(() => BiosUsbWin.PrepareScript(1, "abc'; Clear-Disk 0 '", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => BiosUsbWin.PrepareScript(-1, fp, 0));
    }

    [Fact]
    public void Prepare_output_and_errors_are_read()
    {
        var ok = BiosUsbWin.ParsePrepare("noget\r\nFDOK|e|FAT32|FLEETBIOS|16000000000|" + new string('a', 64) + "\r\n");
        Assert.True(ok.Ok);
        Assert.Equal(("E", "FAT32", 16_000_000_000L), (ok.Letter, ok.FileSystem, ok.Size));
        var fail = BiosUsbWin.ParsePrepare("FDFAIL|format|Størrelsen er for stor til FAT32.\r\n");
        Assert.False(fail.Ok);
        Assert.Equal("Windows kunne ikke formatere stikket som FAT32: Størrelsen er for stor til FAT32.", BiosUsbWin.PrepareError(fail));
        Assert.Contains("Intet er skrevet", BiosUsbWin.PrepareError(BiosUsbWin.ParsePrepare("FDFAIL|changed|")));
        Assert.Contains("drevbogstav", BiosUsbWin.PrepareError(BiosUsbWin.ParsePrepare("FDFAIL|letter|")));
        Assert.Equal("unknown", BiosUsbWin.ParsePrepare("powershell.exe: kunne ikke starte").Step);
    }

    [Fact]
    public void Device_path_becomes_an_instance_id_for_eject()
    {
        Assert.Equal(@"usbstor\disk&ven_sandisk&prod_cruzer_blade&rev_1.00\4C53&0",
            BiosUsbWin.InstanceId(@"\\?\usbstor#disk&ven_sandisk&prod_cruzer_blade&rev_1.00#4C53&0#{53f56307-b6bf-11d0-94f2-00a0c91efb8b}"));
        Assert.Null(BiosUsbWin.InstanceId(""));
        Assert.Null(BiosUsbWin.InstanceId(@"\\?\a\b#{x}"));
    }

    // ------------------------------------------------------------------ skrivning

    [Fact]
    public async Task Write_formats_copies_verifies_and_ejects()
    {
        var h = new FakeWinHost(Drive);
        var (o, pkg, local) = Ready(h);
        var phases = new List<string>();
        var r = await BiosUsbWin.Write(h, o, pkg, local, _ => { }, (_, _, t) => phases.Add(t), CancellationToken.None);
        Assert.Equal("ok", r.Outcome);
        Assert.True(r.Verified);
        Assert.True(r.Ejected);
        Assert.Equal(5, r.Files);   // 4 fra pakken + LÆS-MIG.txt
        var script = Assert.Single(h.Prepares);
        Assert.Equal(BiosUsbWin.PrepareScript(1, BiosUsbWin.Fingerprint(W(Stick())), 0), script);   // disk 1, aldrig systemdisken (0)
        Assert.Equal(2, h.Dismounts);   // før kontrollen og før udskubningen
        Assert.StartsWith(@"\\?\usbstor#", Assert.Single(h.Ejected));
        Assert.Equal(["Kontrollerer pakken …", "Formaterer …", "Kopierer …", "Kontrollerer …", "Skubber ud …"], phases);
        Assert.Equal("shell", File.ReadAllText(Path.Combine(Drive, "EFI", "BOOT", "BOOTX64.efi")));
        Assert.True(File.Exists(Path.Combine(Drive, BiosUsb.ReadmeName)));
        Assert.Contains("SanDisk Cruzer Blade", r.DiskLabel);
    }

    [Fact]
    public async Task Write_refuses_without_local_confirmation_or_used_twice()
    {
        var h = new FakeWinHost(Drive);
        var (o, pkg, local) = Ready(h);
        var good = o.ConfirmHash;
        o.ConfirmHash = new string('a', 64);
        var r = await BiosUsbWin.Write(h, o, pkg, local, _ => { }, null, CancellationToken.None);
        Assert.Equal("refused", r.Outcome);
        Assert.Contains("bekræftet", r.Message);
        o.ConfirmHash = good;   // kvitteringen er brugt (forsøget ovenfor fjernede den)
        Assert.Equal("refused", (await BiosUsbWin.Write(h, o, pkg, local, _ => { }, null, CancellationToken.None)).Outcome);
        Assert.False(h.Destructive);
    }

    [Fact]
    public async Task Write_refuses_when_another_stick_has_taken_its_place_or_is_no_longer_safe()
    {
        foreach (var (disks, text) in new (List<object>, string)[]
        {
            ([SystemNvme(), Stick(serial: "OTHER")], "ikke det samme"),
            ([SystemNvme()], "ikke sat i længere"),
            ([SystemNvme(), Stick(n: 2)], "ikke sat i længere"),   // andet disknummer = andet navn
            ([SystemNvme(), Stick(parts: Part("E", "", "", blProt: 2))], "BitLocker"),
        })
        {
            var h = new FakeWinHost(Drive);
            var (o, pkg, local) = Ready(h);
            h.Disks = disks;
            var r = await BiosUsbWin.Write(h, o, pkg, local, _ => { }, null, CancellationToken.None);
            Assert.Equal("refused", r.Outcome);
            Assert.Contains(text, r.Message);
            Assert.False(h.Destructive);
        }
    }

    [Fact]
    public async Task Write_refuses_a_stick_that_was_in_before_the_baseline_and_a_bad_package()
    {
        var h = new FakeWinHost(Drive);
        var (o, pkg, local) = Ready(h);
        local.SetBaseline("s1", [BiosUsbWin.Key(W(SystemNvme())), BiosUsbWin.Key(W(Stick()))]);
        var r = await BiosUsbWin.Write(h, o, pkg, local, _ => { }, null, CancellationToken.None);
        Assert.Equal("refused", r.Outcome);
        Assert.Contains(BiosUsb.NotNew, r.Message);

        var (o2, pkg2, local2) = Ready(h);
        o2.Sha256 = new string('0', 64);
        Assert.Contains("SHA-256", (await BiosUsbWin.Write(h, o2, pkg2, local2, _ => { }, null, CancellationToken.None)).Message);
        Assert.False(h.Destructive);
    }

    [Fact]
    public async Task Write_stops_when_windows_reports_a_problem_while_preparing()
    {
        var h = new FakeWinHost(Drive) { PrepareOutput = "FDFAIL|changed|" };
        var (o, pkg, local) = Ready(h);
        var r = await BiosUsbWin.Write(h, o, pkg, local, _ => { }, null, CancellationToken.None);
        Assert.Equal("refused", r.Outcome);
        Assert.Contains("Intet er skrevet", r.Message);

        h = new FakeWinHost(Drive) { PrepareOutput = "FDOK|E|NTFS|FLEETBIOS|16000000000|" + BiosUsbWin.Fingerprint(W(Stick())) };
        (o, pkg, local) = Ready(h);
        r = await BiosUsbWin.Write(h, o, pkg, local, _ => { }, null, CancellationToken.None);
        Assert.Equal("failed", r.Outcome);
        Assert.Contains("FAT32", r.Message);
        Assert.False(Directory.Exists(Drive));   // intet kopieret

        h = new FakeWinHost(Drive) { PrepareTimesOut = true };
        (o, pkg, local) = Ready(h);
        r = await BiosUsbWin.Write(h, o, pkg, local, _ => { }, null, CancellationToken.None);
        Assert.Equal("failed", r.Outcome);
        Assert.Contains("5 minutter", r.Message);
    }

    [Fact]
    public async Task Write_fails_when_readback_differs()
    {
        var h = new FakeWinHost(Drive);
        h.OnDismount = n => { if (n == 1) File.WriteAllText(Path.Combine(Drive, "flash2.efi"), "flashes"); };
        var (o, pkg, local) = Ready(h);
        var r = await BiosUsbWin.Write(h, o, pkg, local, _ => { }, null, CancellationToken.None);
        Assert.Equal("failed", r.Outcome);
        Assert.False(r.Verified);
        Assert.Contains("flash2.efi", r.Message);
        Assert.Empty(h.Ejected);
    }

    // ------------------------------------------------------------------ /_fd/ (samme protokol som Linux)

    private static Dictionary<string, string> Q(params (string K, string V)[] kv) => kv.ToDictionary(x => x.K, x => x.V);

    [Fact]
    public async Task Claim_sends_the_nonce_over_the_agents_own_channel()
    {
        var h = new FakeWinHost(Drive);
        var local = new BiosUsbLocal();
        var nonce = new string('n', 32);
        string? sent = null;
        var r = await BiosUsbWin.Local("/_fd/claim", Q(("nonce", nonce)), h, local,
            (n, _) => { sent = n; return Task.FromResult(new BiosUsbClaimResponse { SessionId = "s1", Machine = "arbejds-pc" }); }, CancellationToken.None);
        Assert.Equal(200, r.Status);
        Assert.Equal(nonce, sent);
        Assert.Equal("arbejds-pc", JsonDocument.Parse(r.Body).RootElement.GetProperty("machine").GetString());
        Assert.Equal(400, (await BiosUsbWin.Local("/_fd/claim", Q(("nonce", "kort")), h, local, null, CancellationToken.None)).Status);
        Assert.Equal(503, (await BiosUsbWin.Local("/_fd/claim", Q(("nonce", nonce)), h, local, null, CancellationToken.None)).Status);   // ikke tilmeldt endnu
        var gone = await BiosUsbWin.Local("/_fd/claim", Q(("nonce", nonce)), h, local,
            (_, _) => throw new HttpRequestException("nej", null, HttpStatusCode.NotFound), CancellationToken.None);
        Assert.Equal(409, gone.Status);
        Assert.Contains("ikke gyldig", gone.Body);
        Assert.Equal(404, (await BiosUsbWin.Local("/_fd/andet", Q(), h, local, null, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Confirm_gives_a_receipt_that_the_write_job_accepts()
    {
        var h = new FakeWinHost(Drive);
        var local = new BiosUsbLocal();
        var o0 = new BiosUsbOrder { SessionId = "s1" };
        h.Disks = [SystemNvme()];
        await BiosUsbWin.List(h, o0, local, CancellationToken.None);   // øjebliksbillede uden stikket
        h.Disks = [SystemNvme(), Stick()];
        var stick = W(Stick());
        var id = BiosUsbWin.IdOf(stick);
        Dictionary<string, string> Ask(string serial, long size, string disk) =>
            Q(("session", "s1"), ("disk", disk), ("serial", serial), ("size", size.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("max", "0"), ("large", "0"));

        Assert.Equal(409, (await BiosUsbWin.Local("/_fd/confirm", Ask("OTHER", stick.Size, id), h, local, null, CancellationToken.None)).Status);
        Assert.Equal(400, (await BiosUsbWin.Local("/_fd/confirm", Ask(stick.Serial, stick.Size, @"..\x"), h, local, null, CancellationToken.None)).Status);
        var sys = BiosUsbWin.IdOf(W(SystemNvme()));
        Assert.Equal(409, (await BiosUsbWin.Local("/_fd/confirm", Ask("SYS1", 512_000_000_000, sys), h, local, null, CancellationToken.None)).Status);   // systemdisken findes ikke
        var r = await BiosUsbWin.Local("/_fd/confirm", Ask(stick.Serial, stick.Size, id), h, local, null, CancellationToken.None);
        Assert.Equal(200, r.Status);
        var hash = JsonDocument.Parse(r.Body).RootElement.GetProperty("hash").GetString()!;

        var pkg = Zip(LenovoFiles);
        var o = new BiosUsbOrder
        {
            SessionId = "s1", Phase = BiosUsbPhases.Write, DiskId = id, Serial = stick.Serial, Size = stick.Size, ConfirmHash = hash,
            Layout = BiosUsbLayouts.LenovoZip, FileName = "m1ujt79usa.zip", Sha256 = BiosUsb.Sha256File(pkg), PackageSize = new FileInfo(pkg).Length,
        };
        Assert.Equal("ok", (await BiosUsbWin.Write(h, o, pkg, local, _ => { }, null, CancellationToken.None)).Outcome);
    }

    [Fact]
    public void Report_round_trips_in_job_result()
    {
        var r = new JobResult { BiosUsb = new BiosUsbReport { SessionId = "s1", Disks = [new BiosUsbDisk { Id = "win-1-abcdefabcdef", Path = @"\\.\PhysicalDrive1" }] } };
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var back = JsonSerializer.Deserialize<JobResult>(JsonSerializer.Serialize(r, o), o)!;
        Assert.Equal("win-1-abcdefabcdef", back.BiosUsb!.Disks![0].Id);
    }
}
