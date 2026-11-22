using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Hubs;
using Fleetdeck.Server.Services;
using Fleetdeck.Server.Services.Bios;
using Fleetdeck.Shared;

namespace Fleetdeck.Tests;

// BIOS-opdatering inde fra styresystemet (bios_update). Alle navne, versioner og GUID'er er opdigtede.
internal static class BiosUpdateFixture
{
    public const string Guid = "11111111-2222-4333-8444-555555555555";
    public const string From = "Q9ZKT75A", Target = "Q9ZKT79A";

    public static BiosUpdateOrder Order(string phase = BiosUpdatePhases.Stage, string route = BiosUpdateRoutes.Capsule, string? sha = null, long? size = null) => new()
    {
        RunId = 7, Phase = phase, Route = route, Vendor = "lenovo", Family = "q9zk", FromVersion = From, TargetVersion = Target,
        PackageUrl = "/api/agent/bios/update/7/package", FileName = "q9zkt79usa.zip", Sha256 = sha, Size = size, PackageKind = "usb-zip",
    };

    /// <summary>En stationær Linux-pc, hvor alt er i orden.</summary>
    public static BiosProbe GoodProbe() => new()
    {
        Virtual = false, BiosVersion = From, Model = "ThinkCentre M999q", OnAc = null, Battery = null, SettingsReadable = true, PendingSettings = false,
        Fwupd = true, DeviceId = "abc123", DeviceGuid = Guid, VersionRaw = 0x00010075, VersionLowestRaw = 0x00010075, Updatable = true,
        EspFree = 500L << 20, SecureBoot = true, TpmUnlock = false, BootOrder = "0001,0000",
    };

    /// <summary>En opdigtet ROM: 2 MB nuller med versionskoden og ESRT-GUID'en indlejret.</summary>
    public static byte[] Rom(string code = Target, string? guid = Guid, int size = 2 << 20)
    {
        var b = new byte[size];
        Encoding.ASCII.GetBytes(code + "1.79").CopyTo(b, 4096);
        if (guid is not null) System.Guid.Parse(guid).ToByteArray().CopyTo(b, 8192);
        return b;
    }

    /// <summary>ROM'en med en EFI_CAPSULE_HEADER (ESRT-GUID'en) forrest.</summary>
    public static byte[] Capsule(byte[] rom, string guid = Guid)
    {
        const int h = 32;
        var b = new byte[h + rom.Length];
        System.Guid.Parse(guid).ToByteArray().CopyTo(b, 0);
        BitConverter.GetBytes((uint)h).CopyTo(b, 16);
        BitConverter.GetBytes((uint)b.Length).CopyTo(b, 24);
        rom.CopyTo(b, h);
        return b;
    }

    public static byte[] Zip(byte[] rom, string name = "imageQ9Z.ROM")
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            using (var e = z.CreateEntry("startup.nsh").Open()) e.Write("flash.efi image.rom"u8);
            using (var e = z.CreateEntry(name).Open()) e.Write(rom);
        }
        return ms.ToArray();
    }

    public static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));
}

public sealed class BiosUpdateLogicTests
{
    [Fact]
    public void Lenovo_codes_compare_by_hex_level_within_family()
    {
        Assert.Equal(0x79, LenovoBiosCode.Level("Q9ZKT79A"));
        Assert.Equal(0x2B, LenovoBiosCode.Level("q9zkt2ba"));
        Assert.Null(LenovoBiosCode.Level("1.0.0.121"));
        Assert.Equal(1, LenovoBiosCode.Compare("Q9ZKT75A", "Q9ZKT79A"));
        Assert.Equal(1, LenovoBiosCode.Compare("Q9ZKT79A", "Q9ZKT7AA"));   // 7A (hex) er nyere end 79
        Assert.Equal(0, LenovoBiosCode.Compare("Q9ZKT79A", "q9zkt79a"));
        Assert.Equal(-1, LenovoBiosCode.Compare("Q9ZKT79A", "Q9ZKT75A"));
        Assert.Null(LenovoBiosCode.Compare("Q9ZKT75A", "AB12T79A"));   // anden familie
        Assert.Equal("q9zk", LenovoBiosCode.Family("Q9ZKT75A"));
    }

    [Fact]
    public void Target_raw_version_follows_current_raw_only_when_it_matches()
    {
        Assert.Equal(0x00010079, LenovoBiosCode.TargetRaw("Q9ZKT75A", 0x00010075, "Q9ZKT79A"));
        Assert.Equal(65657, LenovoBiosCode.TargetRaw("Q9ZKT75A", 65653, "Q9ZKT79A"));
        Assert.Null(LenovoBiosCode.TargetRaw("Q9ZKT75A", 0x00010080, "Q9ZKT79A"));   // ender ikke på 75
        Assert.Null(LenovoBiosCode.TargetRaw("Q9ZKT75A", null, "Q9ZKT79A"));
    }

    private static List<BiosCheck> Eval(Action<BiosProbe>? change = null, string route = BiosUpdateRoutes.Capsule, int? snapshot = 81, bool stage = true)
    {
        var p = BiosUpdateFixture.GoodProbe();
        change?.Invoke(p);
        return BiosPreconditions.Evaluate(BiosUpdateFixture.Order(route: route), p, snapshot, stage);
    }

    private static string StateOf(List<BiosCheck> l, string id) => l.Single(c => c.Id == id).State;

    [Fact]
    public void Preconditions_all_ok_on_a_desktop()
    {
        var l = Eval();
        Assert.False(BiosPreconditions.Blocked(l));
        Assert.All(l, c => Assert.Equal(BiosCheckStates.Ok, c.State));
        Assert.Contains("81", l.Single(c => c.Id == "snapshot").Text);
        Assert.Equal("Stationær pc på lysnettet.", l.Single(c => c.Id == "power").Text);
    }

    [Fact]
    public void Preconditions_block_on_missing_backup_power_vm_pending_and_versions()
    {
        Assert.Equal(BiosCheckStates.Fail, StateOf(Eval(snapshot: null), "snapshot"));
        Assert.Equal(BiosCheckStates.Fail, StateOf(Eval(p => p.SettingsReadable = false, stage: false, snapshot: null), "snapshot"));
        Assert.Equal(BiosCheckStates.Ok, StateOf(Eval(stage: false, snapshot: null), "snapshot"));
        Assert.Equal(BiosCheckStates.Fail, StateOf(Eval(p => { p.OnAc = false; p.Battery = 90; }), "power"));
        Assert.Equal(BiosCheckStates.Fail, StateOf(Eval(p => { p.OnAc = true; p.Battery = 20; }), "power"));
        Assert.Equal(BiosCheckStates.Ok, StateOf(Eval(p => { p.OnAc = true; p.Battery = 30; }), "power"));
        Assert.Equal(BiosCheckStates.Warn, StateOf(Eval(p => { p.OnAc = null; p.Battery = 80; }), "power"));
        Assert.Equal(BiosCheckStates.Fail, StateOf(Eval(p => p.Virtual = true), "vm"));
        Assert.Equal(BiosCheckStates.Fail, StateOf(Eval(p => p.PendingSettings = true), "pending"));
        Assert.Equal(BiosCheckStates.Fail, StateOf(Eval(p => p.BiosVersion = "AB12T75A"), "family"));
        Assert.Equal(BiosCheckStates.Fail, StateOf(Eval(p => p.BiosVersion = BiosUpdateFixture.Target), "version"));
        var down = Eval(p => p.BiosVersion = "Q9ZKT7BA");
        Assert.Contains("nedgraderer aldrig", down.Single(c => c.Id == "version").Text);
    }

    [Fact]
    public void Capsule_preconditions_cover_fwupd_esp_lowest_tpm_and_password()
    {
        Assert.Equal(BiosCheckStates.Fail, StateOf(Eval(p => p.Fwupd = false), "fwupd"));
        Assert.Equal(BiosCheckStates.Fail, StateOf(Eval(p => p.DeviceId = null), "fwupd"));
        Assert.Equal(BiosCheckStates.Fail, StateOf(Eval(p => p.Updatable = false), "fwupd"));
        Assert.Equal(BiosCheckStates.Fail, StateOf(Eval(p => p.UpdateState = FwupdJson.Pending), "fwupd"));
        Assert.Equal(BiosCheckStates.Fail, StateOf(Eval(p => p.EspFree = 10L << 20), "esp"));
        Assert.Equal(BiosCheckStates.Warn, StateOf(Eval(p => p.EspFree = null), "esp"));
        Assert.Equal(BiosCheckStates.Fail, StateOf(Eval(p => p.VersionLowestRaw = 0x00010080), "lowest"));
        Assert.Equal(BiosCheckStates.Warn, StateOf(Eval(p => p.VersionRaw = null), "lowest"));
        Assert.Equal(BiosCheckStates.Warn, StateOf(Eval(p => p.TpmUnlock = true), "tpm"));
        var pw = Eval(p => p.AdminPassword = true);
        Assert.Equal(BiosCheckStates.Warn, StateOf(pw, "password"));
        Assert.False(BiosPreconditions.Blocked(pw));
    }

    [Fact]
    public void Windows_preconditions_block_thinkpad_and_password_and_warn_bitlocker()
    {
        var r = BiosUpdateRoutes.LenovoExe;
        Assert.Equal(BiosCheckStates.Fail, StateOf(Eval(p => p.Model = "ThinkPad X999", r), "model"));
        Assert.Equal(BiosCheckStates.Fail, StateOf(Eval(p => p.AdminPassword = true, r), "password"));
        Assert.Equal(BiosCheckStates.Warn, StateOf(Eval(p => p.BitLocker = true, r), "bitlocker"));
        Assert.Equal(BiosCheckStates.Ok, StateOf(Eval(p => p.BitLocker = false, r), "bitlocker"));
        Assert.DoesNotContain(Eval(route: r), c => c.Id is "fwupd" or "esp");
        Assert.True(BiosPreconditions.Blocked(Eval(route: "ukendt")));
    }

    [Fact]
    public void Rom_check_needs_size_version_and_guid()
    {
        Assert.Equal(BiosCheckStates.Ok, BiosRom.Check(BiosUpdateFixture.Rom(), BiosUpdateFixture.Target, BiosUpdateFixture.Guid).State);
        Assert.Equal(BiosCheckStates.Ok, BiosRom.Check(BiosUpdateFixture.Rom(guid: null), BiosUpdateFixture.Target, null).State);
        Assert.Equal(BiosCheckStates.Fail, BiosRom.Check(BiosUpdateFixture.Rom(code: "Q9ZKT78A"), BiosUpdateFixture.Target, BiosUpdateFixture.Guid).State);
        Assert.Equal(BiosCheckStates.Fail, BiosRom.Check(BiosUpdateFixture.Rom(guid: null), BiosUpdateFixture.Target, BiosUpdateFixture.Guid).State);
        Assert.Equal(BiosCheckStates.Fail, BiosRom.Check(BiosUpdateFixture.Rom(size: 4096 * 10), BiosUpdateFixture.Target, null).State);
        Assert.Equal(BiosCheckStates.Fail, BiosRom.Check(BiosUpdateFixture.Rom(size: (2 << 20) + 1), BiosUpdateFixture.Target, null).State);
    }

    [Fact]
    public void Rom_is_picked_only_when_unambiguous()
    {
        Assert.Equal("imageQ9Z.ROM", BiosRom.Pick(["startup.nsh", "efi/boot/BOOTX64.efi", "imageQ9Z.ROM"]));
        Assert.Equal("x/imageQ9Z.rom", BiosRom.Pick(["x/imageQ9Z.rom", "x/backup.rom"]));
        Assert.Null(BiosRom.Pick(["a.rom", "b.rom"]));
        Assert.Null(BiosRom.Pick(["startup.nsh"]));
    }

    [Fact]
    public void Rom_is_extracted_from_zip_under_a_fixed_name()
    {
        var dir = Directory.CreateTempSubdirectory("fd-bios-zip-").FullName;
        try
        {
            var rom = BiosUpdateFixture.Rom();
            var zip = Path.Combine(dir, "p.zip");
            File.WriteAllBytes(zip, BiosUpdateFixture.Zip(rom, "../../evil/imageQ9Z.ROM"));
            var (path, err) = BiosRom.FromZip(zip, dir);
            Assert.Null(err);
            Assert.Equal(Path.Combine(dir, "firmware.rom"), path);
            Assert.Equal(rom, File.ReadAllBytes(path!));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Package_check_verifies_size_and_sha256()
    {
        var f = Path.GetTempFileName();
        try
        {
            var data = new byte[] { 1, 2, 3, 4 };
            File.WriteAllBytes(f, data);
            Assert.Equal(BiosCheckStates.Ok, BiosPackageCheck.Check(f, BiosUpdateFixture.Sha(data), 4).State);
            Assert.Equal(BiosCheckStates.Fail, BiosPackageCheck.Check(f, BiosUpdateFixture.Sha([9]), 4).State);
            Assert.Equal(BiosCheckStates.Fail, BiosPackageCheck.Check(f, BiosUpdateFixture.Sha(data), 5).State);
            Assert.Equal(BiosCheckStates.Fail, BiosPackageCheck.Check(f, null, 4).State);
            Assert.Equal(BiosCheckStates.Fail, BiosPackageCheck.Check(f + ".mangler", BiosUpdateFixture.Sha(data), 4).State);
        }
        finally { File.Delete(f); }
    }

    public const string FwupdSample = """
        {"Devices":[
          {"Name":"UEFI Device Firmware","DeviceId":"zzz","Guid":["99999999-0000-4000-8000-000000000000"],"Plugin":"uefi_capsule","Flags":["internal","updatable"],"VersionRaw":3223520659},
          {"Name":"System Firmware","DeviceId":"abc123","Guid":["11111111-2222-4333-8444-555555555555"],"Plugin":"uefi_capsule",
           "Flags":["internal","updatable","require-ac","needs-reboot","can-verify"],"Version":"65653","VersionRaw":65653,"VersionLowest":"65653","UpdateState":1},
          {"Name":"SSD","DeviceId":"ssd","Plugin":"nvme","Flags":["updatable"]}
        ]}
        """;

    [Fact]
    public void Fwupd_json_finds_system_firmware()
    {
        var d = FwupdJson.SystemFirmware(FwupdSample)!;
        Assert.Equal("abc123", d.DeviceId);
        Assert.Equal(BiosUpdateFixture.Guid, d.Guid);
        Assert.Equal(65653, d.VersionRaw);
        Assert.Equal(65653, d.VersionLowestRaw);
        Assert.True(d.Updatable);
        Assert.Equal(FwupdJson.Pending, d.UpdateState);
        Assert.Null(FwupdJson.SystemFirmware("""{"Devices":[{"Name":"SSD","DeviceId":"x","Plugin":"nvme"}]}"""));
        Assert.Null(FwupdJson.SystemFirmware("ikke json"));
        Assert.Equal(0x10075, FwupdJson.NumText("0x00010075"));
        Assert.Null(FwupdJson.NumText("1.0.0.117"));
    }

    [Fact]
    public void Linux_readers_for_esrt_power_boot_order_secure_boot_and_tpm()
    {
        var root = Directory.CreateTempSubdirectory("fd-bios-fs-").FullName;
        try
        {
            void W(string rel, string v) { var p = Path.Combine(root, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, v + "\n"); }
            W($"{Esrt.Dir}/entry0/fw_class", BiosUpdateFixture.Guid.ToUpperInvariant());
            W($"{Esrt.Dir}/entry0/fw_version", "65657");
            W($"{Esrt.Dir}/entry0/last_attempt_version", "65657");
            W($"{Esrt.Dir}/entry0/last_attempt_status", "0");
            W("sys/class/power_supply/AC/type", "Mains"); W("sys/class/power_supply/AC/online", "1");
            W("sys/class/power_supply/BAT0/type", "Battery"); W("sys/class/power_supply/BAT0/capacity", "55");
            W("sys/class/power_supply/hidpp_battery_0/type", "Battery"); W("sys/class/power_supply/hidpp_battery_0/scope", "Device");
            W("sys/class/power_supply/hidpp_battery_0/capacity", "5");
            var fs = new FirmwareFs(root);
            var e = Esrt.Read(fs).Single();
            Assert.Equal(BiosUpdateFixture.Guid, e.Guid);
            Assert.Equal(65657, e.LastAttemptVersion);
            Assert.Equal(0, e.LastAttemptStatus);
            Assert.Equal((true, 55), BiosLinux.Power(fs));   // musens batteri tæller ikke
            Assert.Equal((null, null), BiosLinux.Power(new FirmwareFs(Path.Combine(root, "mangler"))));
        }
        finally { Directory.Delete(root, true); }
        Assert.Equal("0001,0000,0003", BiosLinux.BootOrder("BootCurrent: 0001\nTimeout: 1 seconds\nBootOrder: 0001,0000,0003\nBoot0000* x"));
        Assert.True(BiosLinux.SecureBoot("SecureBoot enabled\n"));
        Assert.False(BiosLinux.SecureBoot("SecureBoot disabled"));
        Assert.True(BiosLinux.TpmUnlock("root UUID=x none tpm2-device=auto,discard"));
        Assert.False(BiosLinux.TpmUnlock("# tpm2-device=auto\nroot UUID=x none luks"));
        Assert.Equal("signaturen blev afvist", Esrt.StatusText(5));
    }

    private static BiosUpdateState Staged() => new()
    {
        RunId = 7, Phase = BiosUpdateState.Staged, FromVersion = BiosUpdateFixture.From, TargetVersion = BiosUpdateFixture.Target, TargetRaw = 0x00010079,
        DeviceGuid = BiosUpdateFixture.Guid, BootOrder = "0001,0000", StagedAt = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public void Verify_classifies_after_boot()
    {
        var ok = BiosVerify.Classify(Staged(), new BiosAfterBoot { BiosVersion = "Q9ZKT79A", EsrtStatus = 0, EsrtAttemptVersion = 0x00010079, BootOrder = "0001,0000" });
        Assert.Equal(BiosUpdateOutcomes.Updated, ok.Outcome);
        Assert.False(ok.BootOrderChanged);
        Assert.Equal(BiosUpdateOutcomes.Updated, BiosVerify.Classify(Staged(), new BiosAfterBoot { BiosVersion = null, VersionRaw = 0x00010079 }).Outcome);
        var order = BiosVerify.Classify(Staged(), new BiosAfterBoot { BiosVersion = "Q9ZKT79A", BootOrder = "0000,0001" });
        Assert.True(order.BootOrderChanged);
        Assert.Contains("Bootrækkefølgen", order.Message);

        var rej = BiosVerify.Classify(Staged(), new BiosAfterBoot { BiosVersion = "Q9ZKT75A", EsrtStatus = 5, EsrtAttemptVersion = 0x00010079 });
        Assert.Equal(BiosUpdateOutcomes.Rejected, rej.Outcome);
        Assert.Contains("Firmwaren afviste opdateringen (signaturen blev afvist)", rej.Message);
        Assert.Equal(BiosUpdateOutcomes.Rejected,
            BiosVerify.Classify(Staged(), new BiosAfterBoot { BiosVersion = "Q9ZKT75A", FwupdState = FwupdJson.Failed, FwupdError = "x" }).Outcome);
        // en gammel fejl fra et tidligere forsøg med en anden version tæller ikke som afvisning af denne
        Assert.Equal(BiosUpdateOutcomes.NotApplied,
            BiosVerify.Classify(Staged(), new BiosAfterBoot { BiosVersion = "Q9ZKT75A", EsrtStatus = 1, EsrtAttemptVersion = 0x00010070 }).Outcome);
        // kapslen ligger der stadig (fx slukket i stedet for genstartet): kørslen fortsætter som klar
        var still = BiosVerify.Classify(Staged(), new BiosAfterBoot { BiosVersion = "Q9ZKT75A", FwupdState = FwupdJson.Pending });
        Assert.Equal(BiosUpdateOutcomes.Staged, still.Outcome);
        Assert.True(still.StillPending);
        Assert.Equal(BiosUpdateOutcomes.NotApplied, BiosVerify.Classify(Staged(), new BiosAfterBoot { BiosVersion = "Q9ZKT75A" }).Outcome);
        Assert.Equal(BiosUpdateOutcomes.Unknown, BiosVerify.Classify(Staged(), new BiosAfterBoot { BiosVersion = "Q9ZKT77A" }).Outcome);
    }

    [Fact]
    public void Esrt_attempt_is_judged_against_the_values_before_staging()
    {
        // uden målversion: en gammel fejlstatus fra et tidligere forsøg er ikke en afvisning
        var noTarget = Staged(); noTarget.TargetRaw = null;
        noTarget.EsrtBaseline = true; noTarget.EsrtStatusBefore = 1; noTarget.EsrtAttemptBefore = 0x00010070;
        Assert.Equal(BiosUpdateOutcomes.NotApplied,
            BiosVerify.Classify(noTarget, new BiosAfterBoot { BiosVersion = "Q9ZKT75A", EsrtStatus = 1, EsrtAttemptVersion = 0x00010070 }).Outcome);
        // firmwaren afviser en kapsel med ugyldigt format: last_attempt_version bliver 0, men status ændrer sig
        var st = Staged(); st.EsrtBaseline = true; st.EsrtStatusBefore = 0; st.EsrtAttemptBefore = 0x00010075;
        var rej = BiosVerify.Classify(st, new BiosAfterBoot { BiosVersion = "Q9ZKT75A", EsrtStatus = 4, EsrtAttemptVersion = 0 });
        Assert.Equal(BiosUpdateOutcomes.Rejected, rej.Outcome);
        // uden ændring siden klargøringen: ikke forsøgt
        Assert.Equal(BiosUpdateOutcomes.NotApplied,
            BiosVerify.Classify(st, new BiosAfterBoot { BiosVersion = "Q9ZKT75A", EsrtStatus = 0, EsrtAttemptVersion = 0x00010075 }).Outcome);
    }

    [Fact]
    public void Capsule_check_accepts_only_a_real_capsule_header()
    {
        var rom = BiosUpdateFixture.Rom();
        Assert.Equal(BiosCheckStates.Fail, BiosRom.Capsule(rom, BiosUpdateFixture.Guid).State);
        var cap = BiosUpdateFixture.Capsule(rom);
        Assert.Equal(BiosCheckStates.Ok, BiosRom.Capsule(cap, BiosUpdateFixture.Guid).State);
        Assert.Equal(BiosCheckStates.Ok, BiosRom.Check(cap, BiosUpdateFixture.Target, BiosUpdateFixture.Guid).State);
        // en header med en fremmed GUID tæller ikke
        Assert.Equal(BiosCheckStates.Fail, BiosRom.Capsule(cap, "99999999-0000-4000-8000-000000000000").State);
    }

    [Fact]
    public void Lenovo_signature_is_required_for_the_windows_package()
    {
        Assert.True(LenovoWindowsUpdater.SignedByLenovo("Valid|CN=Lenovo, O=Lenovo, L=Morrisville, S=North Carolina, C=US"));
        Assert.True(LenovoWindowsUpdater.SignedByLenovo("Valid|CN=Lenovo, O=\"Lenovo (Beijing) Limited\", C=CN"));
        Assert.False(LenovoWindowsUpdater.SignedByLenovo("NotSigned|"));
        Assert.False(LenovoWindowsUpdater.SignedByLenovo("HashMismatch|CN=Lenovo, O=Lenovo"));
        Assert.False(LenovoWindowsUpdater.SignedByLenovo("Valid|CN=Lenovo, O=Evil Lenovo Fans"));
        Assert.False(LenovoWindowsUpdater.SignedByLenovo("Valid|CN=x, O=American Megatrends International, LLC"));
        Assert.True(LenovoWindowsUpdater.SignedByLenovo("Valid|CN=x, O=American Megatrends International, LLC", flashTool: true));
    }

    [Fact]
    public void Windows_probe_json_is_parsed()
    {
        const string json = """
            {"BiosVersion":"Q9ZKT75A ","BiosVendor":"LENOVO","Manufacturer":"LENOVO","Model":"11AB","ProductVersion":"ThinkCentre M999q",
             "Batteries":[{"Charge":64,"Status":2}],"BitLocker":"On","SecureBoot":true,
             "Esrt":[{"Guid":"{11111111-2222-4333-8444-555555555555}","Type":1,"Version":65653,"LastAttemptVersion":65653,"LastAttemptStatus":0},
                     {"Guid":"{99999999-0000-4000-8000-000000000000}","Type":2,"Version":5}]}
            """;
        var s = LenovoWindowsUpdater.Parse(json);
        Assert.Equal("Q9ZKT75A", s.Probe.BiosVersion);
        Assert.Equal("ThinkCentre M999q", s.Probe.Model);
        Assert.False(s.Probe.Virtual);
        Assert.Equal(64, s.Probe.Battery);
        Assert.True(s.Probe.OnAc);
        Assert.True(s.Probe.BitLocker);
        Assert.True(s.Probe.SecureBoot);
        Assert.Equal(BiosUpdateFixture.Guid, s.Probe.DeviceGuid);
        Assert.Equal(65653, s.Probe.VersionRaw);
        Assert.Equal(2, s.Esrt.Count);
        var desk = LenovoWindowsUpdater.Parse("""{"BiosVersion":"Q9ZKT75A","Batteries":null,"BitLocker":"Off","Esrt":{"Guid":"x","Type":1,"Version":1}}""");
        Assert.Null(desk.Probe.Battery);
        Assert.Null(desk.Probe.OnAc);
        Assert.False(desk.Probe.BitLocker);
        Assert.Single(desk.Esrt);   // ét objekt i stedet for en liste (ConvertTo-Json)
        Assert.Null(LenovoWindowsUpdater.Parse("").Probe.BiosVersion);
        Assert.Equal(BiosCheckStates.Ok, BiosPreconditions.Evaluate(BiosUpdateFixture.Order(route: BiosUpdateRoutes.LenovoExe),
            new BiosProbe { BiosVersion = "Q9ZKT75A", Virtual = false, SettingsReadable = true, BitLocker = false }, 10, true).Single(c => c.Id == "power").State);
    }
}

/// <summary>Jobbet på agenten med falsk platform, falske indstillinger og falsk server.</summary>
public sealed class BiosUpdateJobTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("fd-bios-job-").FullName;
    private readonly BiosUpdateStore store;
    private readonly List<string> calls = [];
    private readonly byte[] package = BiosUpdateFixture.Zip(BiosUpdateFixture.Capsule(BiosUpdateFixture.Rom()));
    private static readonly DateTime T0 = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

    public BiosUpdateJobTests() { store = new BiosUpdateStore(Path.Combine(dir, "state")); }
    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    private sealed class FakeSettings(List<string> calls) : IBiosSettingsBackend
    {
        public string Driver => BiosSettingsDrivers.ThinkLmi;
        public bool CanWrite => true;
        public bool Pending;
        public BiosSettingsInfo Info() => new() { Driver = Driver, Count = 2, PendingReboot = Pending, Writable = true };
        public BiosSettingsReport Read()
        {
            calls.Add("settings.read");
            return new() { Driver = Driver, Items = [new() { Name = "WakeOnLAN", Value = "Automatic" }, new() { Name = "AfterPowerLoss", Value = "Power On" }] };
        }
        public BiosApplyResult Apply(IReadOnlyList<BiosSettingChange> changes, string? password) => throw new InvalidOperationException("må ikke skrive");
    }

    private sealed class FakeUpdater(List<string> calls, string work) : IBiosUpdater
    {
        public BiosProbe Probe_ = BiosUpdateFixture.GoodProbe();
        public bool StageOk = true, Installed = true, Throw, ToolFailed;
        public bool? IsPending = true;
        public DateTime? Boot = T0.AddHours(-1);
        public BiosAfterBoot After = new() { BiosVersion = BiosUpdateFixture.Target, EsrtStatus = 0, EsrtAttemptVersion = 0x00010079, BootOrder = "0001,0000" };
        public string Route => BiosUpdateRoutes.Capsule;
        public string WorkRoot => work;
        public Task<BiosProbe> Probe(BiosUpdateOrder o, CancellationToken ct) { calls.Add("probe"); return Task.FromResult(Probe_); }
        public Task<BiosStageResult> Stage(BiosUpdateOrder o, string pkg, string workDir, BiosProbe p, CancellationToken ct)
        {
            calls.Add("stage");
            Assert.False(ct.CanBeCanceled);   // installationen må aldrig kunne annulleres
            Assert.True(File.Exists(pkg));
            if (Throw) throw new IOException("agenten døde");
            return Task.FromResult(new BiosStageResult { Ok = StageOk, Installed = Installed, ToolFailed = ToolFailed, Message = StageOk ? "klar" : "fejl" });
        }
        public Task<BiosAfterBoot> AfterBoot(BiosUpdateState s, CancellationToken ct) { calls.Add("after"); return Task.FromResult(After); }
        public Task<bool?> Pending(BiosUpdateState s, CancellationToken ct) { calls.Add("pending"); return Task.FromResult(IsPending); }
        public DateTime? BootAt() => Boot;
    }

    private bool snapshotOk = true, postFails;
    private readonly List<BiosUpdatePost> posts = [];

    private BiosUpdateIo Io(byte[]? served = null) => new(
        (p, ct) =>
        {
            calls.Add("post." + p.Kind + (p.Report is { } r ? "." + r.Outcome : ""));
            if (postFails) throw new HttpRequestException("ingen forbindelse");
            posts.Add(p);
            return Task.FromResult<BiosUpdatePostResponse?>(new() { Ok = p.Kind != BiosUpdatePostKinds.Snapshot || snapshotOk, SnapshotId = 1 });
        },
        (url, file, ct) => { calls.Add("download"); File.WriteAllBytes(file, served ?? package); return Task.CompletedTask; });

    private AgentJob Job(string phase = BiosUpdatePhases.Stage) => new()
    {
        Id = 42, Type = JobTypes.BiosUpdate, Params = new JobParams { BiosUpdate = BiosUpdateFixture.Order(phase, sha: BiosUpdateFixture.Sha(package), size: package.Length) },
    };

    private FakeUpdater Up() => new(calls, Path.Combine(dir, "work"));

    [Fact]
    public async Task Stage_takes_the_backup_first_then_checks_downloads_and_stages()
    {
        var up = Up();
        var r = await BiosUpdateJob.Run(Job(), up, new FakeSettings(calls), Io(), store, CancellationToken.None, () => T0);
        Assert.Equal(JobStatus.Success, r.Status);
        Assert.Equal(BiosUpdateOutcomes.Staged, r.BiosUpdate!.Outcome);
        Assert.Equal(["settings.read", "post.snapshot", "probe", "download", "after", "stage"], calls);
        Assert.Contains(r.BiosUpdate.Checks, c => c.Id == "package" && c.State == BiosCheckStates.Ok);
        var st = store.Load()!;
        Assert.Equal(BiosUpdateState.Staged, st.Phase);
        Assert.Equal(T0, st.StagedAt);
        Assert.Equal(0x00010079, st.TargetRaw);
        Assert.Equal(BiosUpdateFixture.From, st.FromVersion);
        Assert.Equal(2, posts.Single(p => p.Kind == BiosUpdatePostKinds.Snapshot).Settings!.Items.Count);
    }

    [Fact]
    public async Task No_backup_in_fleetdeck_means_nothing_is_downloaded_or_staged()
    {
        snapshotOk = false;
        var r = await BiosUpdateJob.Run(Job(), Up(), new FakeSettings(calls), Io(), store, CancellationToken.None);
        Assert.Equal(JobStatus.Failed, r.Status);
        Assert.Equal(BiosUpdateOutcomes.Blocked, r.BiosUpdate!.Outcome);
        Assert.DoesNotContain("download", calls);
        Assert.DoesNotContain("stage", calls);
        Assert.Null(store.Load());
        Assert.Empty(Directory.GetDirectories(Path.Combine(dir, "work")));   // arbejdsmappen er ryddet op

        calls.Clear();
        var none = await BiosUpdateJob.Run(Job(), Up(), null, Io(), store, CancellationToken.None);
        Assert.Equal(BiosUpdateOutcomes.Blocked, none.BiosUpdate!.Outcome);
        Assert.Equal(BiosUpdateText.NoSettings, none.BiosUpdate.Checks.Single().Text);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task Failed_precondition_or_bad_checksum_stops_before_staging()
    {
        var up = Up();
        up.Probe_.OnAc = false; up.Probe_.Battery = 80;
        var r = await BiosUpdateJob.Run(Job(), up, new FakeSettings(calls), Io(), store, CancellationToken.None);
        Assert.Equal(BiosUpdateOutcomes.Blocked, r.BiosUpdate!.Outcome);
        Assert.DoesNotContain("download", calls);

        calls.Clear();
        var bad = await BiosUpdateJob.Run(Job(), Up(), new FakeSettings(calls), Io(served: [1, 2, 3]), store, CancellationToken.None);
        Assert.Equal(BiosUpdateOutcomes.Blocked, bad.BiosUpdate!.Outcome);
        Assert.Contains("download", calls);
        Assert.DoesNotContain("stage", calls);
        Assert.Null(store.Load());

        calls.Clear();
        var pending = new FakeSettings(calls) { Pending = true };
        Assert.Equal(BiosUpdateOutcomes.Blocked, (await BiosUpdateJob.Run(Job(), Up(), pending, Io(), store, CancellationToken.None)).BiosUpdate!.Outcome);
    }

    [Fact]
    public async Task Stage_that_fails_before_install_changes_nothing_and_after_install_is_recovered()
    {
        var up = Up();
        up.StageOk = false; up.Installed = false;
        var r = await BiosUpdateJob.Run(Job(), up, new FakeSettings(calls), Io(), store, CancellationToken.None);
        Assert.Equal(BiosUpdateOutcomes.Blocked, r.BiosUpdate!.Outcome);
        Assert.Null(store.Load());

        // installationen gik i gang, men programmet fejlede: ligger der alligevel noget klar, behandles det som klar
        var up2 = Up();
        up2.StageOk = false; up2.Installed = true; up2.IsPending = true;
        var r2 = await BiosUpdateJob.Run(Job(), up2, new FakeSettings(calls), Io(), store, CancellationToken.None, () => T0);
        Assert.Equal(BiosUpdateOutcomes.Staged, r2.BiosUpdate!.Outcome);
        Assert.Equal(BiosUpdateState.Staged, store.Load()!.Phase);
        store.Clear();

        var up3 = Up();
        up3.StageOk = false; up3.Installed = true; up3.IsPending = false;
        var r3 = await BiosUpdateJob.Run(Job(), up3, new FakeSettings(calls), Io(), store, CancellationToken.None);
        Assert.Equal(BiosUpdateOutcomes.Failed, r3.BiosUpdate!.Outcome);
        Assert.Null(store.Load());
    }

    [Fact]
    public async Task Flash_tool_error_is_a_failure_unless_something_is_provably_pending()
    {
        // Windows: Pending kan ikke aflæses (null). Programmet meldte selv fejl → mislykkedes, ikke "lagt klar"
        var up = Up();
        up.StageOk = false; up.Installed = true; up.ToolFailed = true; up.IsPending = null;
        var r = await BiosUpdateJob.Run(Job(), up, new FakeSettings(calls), Io(), store, CancellationToken.None, () => T0);
        Assert.Equal(BiosUpdateOutcomes.Failed, r.BiosUpdate!.Outcome);
        Assert.Equal(JobStatus.Failed, r.Status);
        Assert.DoesNotContain("alligevel", r.BiosUpdate.Message);
        Assert.Null(store.Load());

        // ligger der beviseligt noget klar, er det klar
        var up2 = Up();
        up2.StageOk = false; up2.Installed = true; up2.ToolFailed = true; up2.IsPending = true;
        var r2 = await BiosUpdateJob.Run(Job(), up2, new FakeSettings(calls), Io(), store, CancellationToken.None, () => T0);
        Assert.Equal(BiosUpdateOutcomes.Staged, r2.BiosUpdate!.Outcome);
        Assert.Contains("alligevel", r2.BiosUpdate.Message);
        store.Clear();

        // timeout (ikke en fejlmelding) og kan ikke ses: "kan ikke se, om den ligger klar" – aldrig "alligevel klar"
        var up3 = Up();
        up3.StageOk = false; up3.Installed = true; up3.IsPending = null;
        var r3 = await BiosUpdateJob.Run(Job(), up3, new FakeSettings(calls), Io(), store, CancellationToken.None, () => T0);
        Assert.Equal(BiosUpdateOutcomes.Staged, r3.BiosUpdate!.Outcome);
        Assert.Contains("kan ikke se", r3.BiosUpdate.Message);
        Assert.DoesNotContain("alligevel", r3.BiosUpdate.Message);
    }

    [Fact]
    public async Task Exception_during_install_is_recovered_from_pending_state()
    {
        var up = Up();
        up.Throw = true; up.IsPending = null;   // kan ikke ses → kontrolleres efter næste genstart
        var r = await BiosUpdateJob.Run(Job(), up, new FakeSettings(calls), Io(), store, CancellationToken.None, () => T0);
        Assert.Equal(BiosUpdateOutcomes.Staged, r.BiosUpdate!.Outcome);
        Assert.Contains("kan ikke se", r.BiosUpdate.Message);
        Assert.Equal(BiosUpdateState.Staged, store.Load()!.Phase);
    }

    [Fact]
    public async Task Another_staged_update_blocks_a_new_one()
    {
        store.Save(new BiosUpdateState { RunId = 3, Phase = BiosUpdateState.Staged, TargetVersion = "X" });
        var r = await BiosUpdateJob.Run(Job(), Up(), new FakeSettings(calls), Io(), store, CancellationToken.None);
        Assert.Equal(BiosUpdateOutcomes.Blocked, r.BiosUpdate!.Outcome);
        Assert.Empty(calls);
        Assert.Equal(3, store.Load()!.RunId);   // den ventende opdatering røres ikke
        var check = await BiosUpdateJob.Run(Job(BiosUpdatePhases.Check), Up(), new FakeSettings(calls), Io(), store, CancellationToken.None);
        Assert.Equal(BiosUpdateOutcomes.Blocked, check.BiosUpdate!.Outcome);
        Assert.Contains(check.BiosUpdate.Checks, c => c.Id == "other");
    }

    [Fact]
    public async Task Check_only_reads()
    {
        var r = await BiosUpdateJob.Run(Job(BiosUpdatePhases.Check), Up(), new FakeSettings(calls), Io(), store, CancellationToken.None);
        Assert.Equal(JobStatus.Success, r.Status);
        Assert.Equal(BiosUpdateOutcomes.Checked, r.BiosUpdate!.Outcome);
        Assert.Equal(["probe"], calls);
        Assert.Null(store.Load());
    }

    [Fact]
    public async Task Route_mismatch_and_missing_order_fail_cleanly()
    {
        var j = Job();
        j.Params.BiosUpdate!.Route = BiosUpdateRoutes.LenovoExe;
        Assert.Equal(BiosUpdateOutcomes.Blocked, (await BiosUpdateJob.Run(j, Up(), new FakeSettings(calls), Io(), store, CancellationToken.None)).BiosUpdate!.Outcome);
        Assert.Equal(JobStatus.Failed, (await BiosUpdateJob.Run(new AgentJob { Type = JobTypes.BiosUpdate }, Up(), null, Io(), store, CancellationToken.None)).Status);
        Assert.Equal(BiosUpdateOutcomes.Blocked, (await BiosUpdateJob.Run(Job(), null, null, Io(), store, CancellationToken.None)).BiosUpdate!.Outcome);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task Resume_after_reboot_reports_once_and_survives_a_failed_send()
    {
        var up = Up();
        await BiosUpdateJob.Run(Job(), up, new FakeSettings(calls), Io(), store, CancellationToken.None, () => T0);
        calls.Clear();
        // ingen genstart endnu: intet sendes
        Assert.False(await BiosUpdateJob.Resume(up, Io(), store, CancellationToken.None));
        Assert.Empty(calls);
        // genstartet; serveren kan ikke nås: meldingen gemmes på disken
        up.Boot = T0.AddMinutes(5);
        postFails = true;
        Assert.False(await BiosUpdateJob.Resume(up, Io(), store, CancellationToken.None));
        Assert.Equal(BiosUpdateOutcomes.Updated, store.Load()!.Pending!.Outcome);
        // næste forsøg: samme melding sendes (uden at læse igen), og tilstanden ryddes
        postFails = false; calls.Clear();
        Assert.True(await BiosUpdateJob.Resume(up, Io(), store, CancellationToken.None));
        Assert.Equal(["post.report.updated"], calls);
        Assert.Null(store.Load());
    }

    [Fact]
    public async Task Resume_keeps_a_capsule_that_is_still_pending_and_checks_again_after_the_next_boot()
    {
        var up = Up();
        await BiosUpdateJob.Run(Job(), up, new FakeSettings(calls), Io(), store, CancellationToken.None, () => T0);
        // maskinen blev slukket i stedet for genstartet: versionen er den gamle, men fwupd har stadig kapslen klar
        up.Boot = T0.AddMinutes(5);
        up.After = new BiosAfterBoot { BiosVersion = "Q9ZKT75A", FwupdState = FwupdJson.Pending };
        Assert.True(await BiosUpdateJob.Resume(up, Io(), store, CancellationToken.None, () => T0.AddMinutes(6)));
        var sent = posts.Last().Report!;
        Assert.Equal(BiosUpdateOutcomes.Staged, sent.Outcome);
        Assert.True(sent.StillPending);
        var st = store.Load()!;                      // tilstanden er bevaret
        Assert.Equal(T0.AddMinutes(6), st.StagedAt);
        Assert.Null(st.Pending);
        calls.Clear();
        Assert.False(await BiosUpdateJob.Resume(up, Io(), store, CancellationToken.None, () => T0.AddMinutes(8)));   // ingen ny genstart endnu
        Assert.Empty(calls);
    }

    [Fact]
    public async Task Resume_reports_an_interrupted_staging()
    {
        var up = Up();
        store.Save(new BiosUpdateState { RunId = 7, Phase = BiosUpdateState.Staging, TargetVersion = BiosUpdateFixture.Target, InstallStarted = false });
        Assert.True(await BiosUpdateJob.Resume(up, Io(), store, CancellationToken.None));
        Assert.Equal(BiosUpdateOutcomes.Failed, posts.Single().Report!.Outcome);
        Assert.Null(store.Load());

        // installationen var gået i gang, og der ligger noget klar: klar, og resultatet kontrolleres efter næste genstart
        posts.Clear();
        store.Save(new BiosUpdateState { RunId = 8, Phase = BiosUpdateState.Staging, TargetVersion = BiosUpdateFixture.Target, InstallStarted = true });
        up.IsPending = true;
        Assert.True(await BiosUpdateJob.Resume(up, Io(), store, CancellationToken.None, () => T0));
        Assert.Equal(BiosUpdateOutcomes.Staged, posts.Single().Report!.Outcome);
        var st = store.Load()!;
        Assert.Equal(BiosUpdateState.Staged, st.Phase);
        Assert.Null(st.Pending);
        Assert.Equal(T0, st.StagedAt);
    }

    [Fact]
    public async Task Verify_before_reboot_says_waiting_and_after_reboot_classifies()
    {
        var up = Up();
        await BiosUpdateJob.Run(Job(), up, new FakeSettings(calls), Io(), store, CancellationToken.None, () => T0);
        var wait = await BiosUpdateJob.Run(Job(BiosUpdatePhases.Verify), up, null, Io(), store, CancellationToken.None);
        Assert.Equal(BiosUpdateOutcomes.Staged, wait.BiosUpdate!.Outcome);
        Assert.NotNull(store.Load());
        up.Boot = T0.AddMinutes(3);
        up.After = new BiosAfterBoot { BiosVersion = BiosUpdateFixture.From, EsrtStatus = 6, EsrtAttemptVersion = 0x00010079 };
        var r = await BiosUpdateJob.Run(Job(BiosUpdatePhases.Verify), up, null, Io(), store, CancellationToken.None);
        Assert.Equal(BiosUpdateOutcomes.Rejected, r.BiosUpdate!.Outcome);
        Assert.Equal(JobStatus.Failed, r.Status);
        Assert.Null(store.Load());
    }
}

/// <summary>Linux-vejen mod et falsk filtræ og falske programmer: præcis hvilke kommandoer der køres.</summary>
public sealed class LinuxCapsuleUpdaterTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("fd-bios-linux-").FullName;
    private readonly List<string> cmds = [];
    private bool capOnEsp = true;

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    private void W(string rel, string v) { var p = Path.Combine(root, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, v + "\n"); }

    private Task<BiosCmdResult> Run(string file, IReadOnlyList<string> args, TimeSpan timeout, string? wd, bool kill, CancellationToken ct)
    {
        cmds.Add(file + " " + string.Join(' ', args.Select(a => a.StartsWith(root) ? Path.GetFileName(a) : a)));
        if (file == "fwupdtool")
        {
            Assert.False(kill);   // flash-trinnet stoppes aldrig ved tidsudløb
            if (capOnEsp) W("boot/efi/EFI/distro/fw/fwupd-11111111.cap", "x");
        }
        return Task.FromResult(file switch
        {
            "fwupdmgr" when args[0] == "get-devices" => new BiosCmdResult(0, BiosUpdateLogicTests.FwupdSample.Replace("\"UpdateState\":1", "\"UpdateState\":0")),
            "mokutil" => new BiosCmdResult(0, "SecureBoot enabled"),
            "efibootmgr" => new BiosCmdResult(0, "BootOrder: 0001,0000"),
            _ => new BiosCmdResult(0, ""),
        });
    }

    private LinuxCapsuleUpdater Updater()
    {
        W("sys/class/dmi/id/bios_version", "Q9ZKT75A");
        W("sys/class/dmi/id/sys_vendor", "LENOVO");
        W("sys/class/dmi/id/product_name", "11AB");
        W("sys/class/dmi/id/product_version", "ThinkCentre M999q");
        W("boot/efi/EFI/distro/shimx64.efi", "x");
        W("proc/uptime", "3600.5 1000.0");
        return new LinuxCapsuleUpdater(new FirmwareFs(root), Run, Path.Combine(root, "work"), _ => 900L << 20);
    }

    [Fact]
    public async Task Probe_reads_dmi_fwupd_esp_and_secure_boot()
    {
        var p = await Updater().Probe(BiosUpdateFixture.Order(), CancellationToken.None);
        Assert.Equal("Q9ZKT75A", p.BiosVersion);
        Assert.False(p.Virtual);
        Assert.True(p.Fwupd);
        Assert.Equal("abc123", p.DeviceId);
        Assert.Equal(BiosUpdateFixture.Guid, p.DeviceGuid);
        Assert.Equal(65653, p.VersionRaw);
        Assert.Equal(900L << 20, p.EspFree);
        Assert.True(p.SecureBoot);
        Assert.Equal("0001,0000", p.BootOrder);
        Assert.Null(p.Battery);
        Assert.DoesNotContain(cmds, c => c.Contains("install") || c.Contains("reboot"));
    }

    [Fact]
    public async Task Stage_installs_the_rom_with_fwupd_and_never_reboots()
    {
        var up = Updater();
        var p = await up.Probe(BiosUpdateFixture.Order(), CancellationToken.None);
        var work = Directory.CreateDirectory(Path.Combine(root, "work", "run-1")).FullName;
        var zip = Path.Combine(work, "p.zip");
        File.WriteAllBytes(zip, BiosUpdateFixture.Zip(BiosUpdateFixture.Capsule(BiosUpdateFixture.Rom())));
        cmds.Clear();
        var r = await up.Stage(BiosUpdateFixture.Order(), zip, work, p, CancellationToken.None);
        Assert.True(r.Ok, r.Message);
        Assert.True(r.Installed);
        Assert.Equal(["systemctl stop fwupd.service", "fwupdtool install-blob firmware.rom abc123 --no-reboot-check", "systemctl start fwupd.service"], cmds);
        Assert.Equal(DateTime.UtcNow.AddSeconds(-3600), up.BootAt()!.Value, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Stage_refuses_a_raw_usb_rom_without_touching_fwupd()
    {
        var up = Updater();
        var p = await up.Probe(BiosUpdateFixture.Order(), CancellationToken.None);
        var work = Directory.CreateDirectory(Path.Combine(root, "work", "run-raw")).FullName;
        var zip = Path.Combine(work, "p.zip");
        File.WriteAllBytes(zip, BiosUpdateFixture.Zip(BiosUpdateFixture.Rom()));
        cmds.Clear();
        var r = await up.Stage(BiosUpdateFixture.Order(), zip, work, p, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.False(r.Installed);
        Assert.Contains("USB-stikket", r.Message);
        Assert.Empty(cmds);
    }

    [Fact]
    public async Task Stage_refuses_a_rom_for_another_version_without_touching_fwupd()
    {
        var up = Updater();
        var p = await up.Probe(BiosUpdateFixture.Order(), CancellationToken.None);
        var work = Directory.CreateDirectory(Path.Combine(root, "work", "run-2")).FullName;
        var zip = Path.Combine(work, "p.zip");
        File.WriteAllBytes(zip, BiosUpdateFixture.Zip(BiosUpdateFixture.Rom(code: "Q9ZKT78A")));
        cmds.Clear();
        var r = await up.Stage(BiosUpdateFixture.Order(), zip, work, p, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.False(r.Installed);
        Assert.Empty(cmds);
    }

    [Fact]
    public async Task Stage_without_a_pending_capsule_is_not_ok()
    {
        capOnEsp = false;
        var up = Updater();
        var p = await up.Probe(BiosUpdateFixture.Order(), CancellationToken.None);
        var work = Directory.CreateDirectory(Path.Combine(root, "work", "run-3")).FullName;
        var zip = Path.Combine(work, "p.zip");
        File.WriteAllBytes(zip, BiosUpdateFixture.Zip(BiosUpdateFixture.Capsule(BiosUpdateFixture.Rom())));
        var r = await up.Stage(BiosUpdateFixture.Order(), zip, work, p, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.True(r.Installed);
        Assert.Contains("ingen opdatering klar", r.Message);
    }

    [Fact]
    public async Task After_boot_reads_version_fwupd_and_esrt()
    {
        var up = Updater();
        W("sys/class/dmi/id/bios_version", "Q9ZKT79A");
        W($"{Esrt.Dir}/entry0/fw_class", BiosUpdateFixture.Guid);
        W($"{Esrt.Dir}/entry0/last_attempt_status", "0");
        W($"{Esrt.Dir}/entry0/last_attempt_version", "65657");
        var a = await up.AfterBoot(new BiosUpdateState { DeviceGuid = BiosUpdateFixture.Guid }, CancellationToken.None);
        Assert.Equal("Q9ZKT79A", a.BiosVersion);
        Assert.Equal(0, a.EsrtStatus);
        Assert.Equal(65657, a.EsrtAttemptVersion);
        Assert.Equal("0001,0000", a.BootOrder);
    }
}

/// <summary>Serverens tilstandsmaskine og kørsler mod en rigtig (SQLite) database med migrationerne.</summary>
public sealed class BiosUpdateServerTests : IDisposable
{
    private readonly SqliteConnection conn = new("Data Source=:memory:");
    private readonly AppDb db;
    private readonly JobService jobs;
    private static readonly DateTime T0 = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

    public BiosUpdateServerTests()
    {
        conn.Open();
        db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite(conn).Options);
        db.Database.Migrate();
        jobs = new JobService(db, new Live(new NoHub<UiHub>(), new NoHub<AgentHub>()));
    }

    public void Dispose() { db.Dispose(); conn.Dispose(); }

    [Fact]
    public void Flow_accepts_only_matching_reports()
    {
        Assert.Equal(BiosUpdateStates.Checked, BiosUpdateFlow.Next(BiosUpdateStates.Checking, BiosUpdateOutcomes.Checked));
        Assert.Null(BiosUpdateFlow.Next(BiosUpdateStates.Staging, BiosUpdateOutcomes.Checked));
        Assert.Equal(BiosUpdateStates.Staged, BiosUpdateFlow.Next(BiosUpdateStates.Staging, BiosUpdateOutcomes.Staged));
        Assert.Equal(BiosUpdateStates.Staged, BiosUpdateFlow.Next(BiosUpdateStates.Failed, BiosUpdateOutcomes.Staged));   // agenten vinder
        Assert.Equal(BiosUpdateStates.Updated, BiosUpdateFlow.Next(BiosUpdateStates.Staged, BiosUpdateOutcomes.Updated));
        Assert.Equal(BiosUpdateStates.Updated, BiosUpdateFlow.Next(BiosUpdateStates.Staging, BiosUpdateOutcomes.Updated));   // klar-meldingen gik tabt
        Assert.Null(BiosUpdateFlow.Next(BiosUpdateStates.Updated, BiosUpdateOutcomes.Rejected));
        Assert.Null(BiosUpdateFlow.Next(BiosUpdateStates.Checked, BiosUpdateOutcomes.Updated));
        Assert.Null(BiosUpdateFlow.Next(BiosUpdateStates.Blocked, BiosUpdateOutcomes.Staged));
    }

    [Fact]
    public void Effective_state_tracks_reboot_and_missing_report()
    {
        var run = new BiosUpdateRun { State = BiosUpdateStates.Staged, StagedAt = T0, UpdatedAt = T0 };
        Assert.Equal(BiosUpdateStates.Staged, BiosUpdateFlow.Effective(run, T0.AddHours(-2), T0.AddHours(5)));
        run.RebootRequestedAt = T0.AddMinutes(10);
        Assert.Equal("rebooting", BiosUpdateFlow.Effective(run, T0.AddHours(-2), T0.AddMinutes(20)));
        Assert.Equal("no_report", BiosUpdateFlow.Effective(run, T0.AddHours(-2), T0.AddMinutes(50)));   // fx hængt ved F1 (0162)
        Assert.Equal("awaiting_report", BiosUpdateFlow.Effective(run, T0.AddMinutes(15), T0.AddMinutes(30)));
        Assert.Equal("no_report", BiosUpdateFlow.Effective(run, T0.AddMinutes(15), T0.AddHours(2)));
        var staging = new BiosUpdateRun { State = BiosUpdateStates.Staging, UpdatedAt = T0 };
        Assert.Equal(BiosUpdateStates.Staging, BiosUpdateFlow.Effective(staging, null, T0.AddHours(1)));
        Assert.Equal("stale", BiosUpdateFlow.Effective(staging, null, T0.AddHours(4)));
    }

    /// <param name="capsule">Linux: den hentede USB-pakke indeholder en rigtig UEFI-kapsel (true), en rå ROM (false) eller er ikke hentet (null).</param>
    private Machine AddMachine(string platform = Platforms.Linux, string state = BiosStates.Newer, bool settings = true, bool vm = false, bool? capsule = true)
    {
        if (platform == Platforms.Linux && capsule is { } c) AddPackage(capsule: c);
        var f = new SystemFacts
        {
            Manufacturer = "LENOVO", BiosVendor = "LENOVO", BiosVersion = BiosUpdateFixture.From, ProductVersion = "ThinkCentre M999q", IsVirtual = vm,
            BiosSettings = settings ? new BiosSettingsInfo { Driver = BiosSettingsDrivers.ThinkLmi, Count = 2, Writable = true } : null,
        };
        var m = new Machine { Name = "pc-" + Guid.NewGuid().ToString("N")[..6], Platform = platform, FactsJson = Json.Ser(f), LastSeen = T0 };
        db.Machines.Add(m);
        db.SaveChanges();
        db.BiosStatus.Add(new MachineBiosStatus
        {
            MachineId = m.Id, Vendor = BiosMatcher.Lenovo, CatalogKey = "q9zk/11AB", MatchQuality = "exact", Current = BiosUpdateFixture.From,
            Latest = BiosUpdateFixture.Target, LatestNumeric = "1.0.0.121", State = state,
        });
        db.SaveChanges();
        return m;
    }

    private BiosPackage AddPackage(string kind = BiosPackages.UsbZip, string version = BiosUpdateFixture.Target, bool capsule = true)
    {
        if (db.BiosPackages.FirstOrDefault(x => x.CatalogKey == "q9zk/11AB" && x.Version == version && x.Kind == kind) is { } had) return had;
        var p = new BiosPackage
        {
            Vendor = BiosMatcher.Lenovo, CatalogKey = "q9zk/11AB", Version = version, Kind = kind, FileName = "q9zkt79usa.zip", Size = 1234,
            Sha256 = new string('a', 64), ShaSource = "tofu", State = BiosPackages.Ready, Path = "bios/x", Capsule = kind == BiosPackages.UsbZip ? capsule : null,
        };
        db.BiosPackages.Add(p);
        db.SaveChanges();
        return p;
    }

    private static BiosUpdateService.StartRequest Yes => new(true, BiosUpdateFixture.Target);

    [Fact]
    public async Task Check_creates_a_run_and_a_bios_update_job_that_only_checks()
    {
        var m = AddMachine();
        var (run, job, err) = await BiosUpdateService.Check(db, jobs, m, "admin", T0);
        Assert.Null(err);
        Assert.Equal(BiosUpdateStates.Checking, run!.State);
        Assert.Equal(JobTypes.BiosUpdate, job!.Type);
        var o = Json.Des<JobParams>(job.ParamsJson).BiosUpdate!;
        Assert.Equal(BiosUpdatePhases.Check, o.Phase);
        Assert.Equal(BiosUpdateRoutes.Capsule, o.Route);
        Assert.Equal("q9zk", o.Family);
        Assert.Null(o.PackageUrl);
        Assert.Equal(job.Id, run.JobId);
        // jobbet slutter med forudsætningerne
        await BiosUpdateService.OnResult(db, m, Done(job), new JobResult { BiosUpdate = new() { RunId = run.Id, Outcome = BiosUpdateOutcomes.Checked, Checks = [BiosCheck.Ok("vm", "ok")] } }, T0);
        Assert.Equal(BiosUpdateStates.Checked, (await db.BiosUpdateRuns.FindAsync(run.Id))!.State);
    }

    private Job Done(Job j, string status = JobStatus.Success) { j.Status = status; db.SaveChanges(); return j; }

    [Fact]
    public async Task Start_needs_confirmation_of_the_exact_version_and_a_matching_package()
    {
        var m = AddMachine();
        Assert.NotNull((await BiosUpdateService.Start(db, jobs, m, AddPackage(), new(false, BiosUpdateFixture.Target), "admin", T0)).Error);
        Assert.NotNull((await BiosUpdateService.Start(db, jobs, m, AddPackage(BiosPackages.WinExe, "Q9ZKT78A"), new(true, "Q9ZKT78A"), "admin", T0)).Error);
        Assert.NotNull((await BiosUpdateService.Start(db, jobs, m, AddPackage(BiosPackages.WinExe), Yes, "admin", T0)).Error);   // forkert pakketype
        Assert.Empty(await db.BiosUpdateRuns.ToListAsync());
        var pkg = await db.BiosPackages.FirstAsync(p => p.Kind == BiosPackages.UsbZip);
        var (run, job, err) = await BiosUpdateService.Start(db, jobs, m, pkg, Yes, "admin", T0);
        Assert.Null(err);
        Assert.Equal(BiosUpdateStates.Staging, run!.State);
        var o = Json.Des<JobParams>(job!.ParamsJson).BiosUpdate!;
        Assert.Equal(BiosUpdatePhases.Stage, o.Phase);
        Assert.Equal($"/api/agent/bios/update/{run.Id}/package", o.PackageUrl);
        Assert.Equal(pkg.Sha256, o.Sha256);
        Assert.Contains(await db.Audit.ToListAsync(), a => a.Action == "bios.update.start");
        // kun én kørsel ad gangen
        Assert.NotNull((await BiosUpdateService.Start(db, jobs, m, pkg, Yes, "admin", T0)).Error);
        Assert.NotNull((await BiosUpdateService.Check(db, jobs, m, "admin", T0)).Error);
    }

    [Fact]
    public async Task No_update_for_vm_unknown_newest_or_without_settings_backup()
    {
        Assert.NotNull((await BiosUpdateService.Check(db, jobs, AddMachine(vm: true), "admin", T0)).Error);
        Assert.NotNull((await BiosUpdateService.Check(db, jobs, AddMachine(state: BiosStates.Newest), "admin", T0)).Error);
        Assert.NotNull((await BiosUpdateService.Check(db, jobs, AddMachine(settings: false), "admin", T0)).Error);
        Assert.NotNull((await BiosUpdateService.Check(db, jobs, AddMachine(platform: Platforms.Mac), "admin", T0)).Error);
        var win = AddMachine(platform: Platforms.Windows);
        var (run, _, _) = await BiosUpdateService.Check(db, jobs, win, "admin", T0);
        Assert.Equal(BiosUpdateRoutes.LenovoExe, run!.Route);
    }

    [Fact]
    public async Task Snapshot_report_reboot_and_verify_through_the_whole_run()
    {
        var m = AddMachine();
        var (run, job, _) = await BiosUpdateService.Start(db, jobs, m, AddPackage(), Yes, "admin", T0);
        // kopien sendes kun af kørslens eget job, mens det kører
        var early = await BiosUpdateService.OnAgentPost(db, m, new BiosUpdatePost
        {
            RunId = run!.Id, JobId = job!.Id, Kind = BiosUpdatePostKinds.Snapshot,
            Settings = new BiosSettingsReport { Driver = BiosSettingsDrivers.ThinkLmi, Items = [new() { Name = "WakeOnLAN", Value = "Automatic" }] },
        }, T0);
        Assert.False(early.Ok);
        job.Status = JobStatus.Running; await db.SaveChangesAsync();
        Assert.False((await BiosUpdateService.OnAgentPost(db, m, new BiosUpdatePost
        {
            RunId = run.Id, JobId = job.Id + 1000, Kind = BiosUpdatePostKinds.Snapshot,
            Settings = new BiosSettingsReport { Driver = BiosSettingsDrivers.ThinkLmi, Items = [new() { Name = "WakeOnLAN", Value = "Automatic" }] },
        }, T0)).Ok);
        // kopien før opdateringen
        var snap = await BiosUpdateService.OnAgentPost(db, m, new BiosUpdatePost
        {
            RunId = run!.Id, JobId = job!.Id, Kind = BiosUpdatePostKinds.Snapshot,
            Settings = new BiosSettingsReport { Driver = BiosSettingsDrivers.ThinkLmi, Items = [new() { Name = "WakeOnLAN", Value = "Automatic" }] },
        }, T0);
        Assert.True(snap.Ok);
        var s = await db.BiosSnapshots.SingleAsync();
        Assert.Equal(BiosSnapshotReasons.PreUpdate, s.Reason);
        Assert.Equal(s.Id, (await db.BiosUpdateRuns.FindAsync(run.Id))!.PreSnapshotId);
        // genstart kun når den ligger klar
        Assert.NotNull((await BiosUpdateService.Reboot(db, jobs, m, "admin", T0)).Error);
        await BiosUpdateService.OnResult(db, m, Done(job), new JobResult { BiosUpdate = new() { RunId = run.Id, Outcome = BiosUpdateOutcomes.Staged, StagedAt = T0, Message = "klar" } }, T0);
        Assert.Equal(BiosUpdateStates.Staged, (await db.BiosUpdateRuns.FindAsync(run.Id))!.State);
        // en kopi kan ikke sendes, når den ikke gøres klar længere
        Assert.False((await BiosUpdateService.OnAgentPost(db, m, new BiosUpdatePost { RunId = run.Id, Kind = BiosUpdatePostKinds.Snapshot,
            Settings = new BiosSettingsReport { Items = [new() { Name = "x", Value = "y" }] } }, T0)).Ok);
        var (reboot, rerr) = await BiosUpdateService.Reboot(db, jobs, m, "admin", T0.AddMinutes(1));
        Assert.Null(rerr);
        Assert.Equal(JobTypes.Reboot, reboot!.Type);
        Assert.Equal(T0.AddMinutes(1), (await db.BiosUpdateRuns.FindAsync(run.Id))!.RebootRequestedAt);
        // efter genstarten
        var res = await BiosUpdateService.OnAgentPost(db, m, new BiosUpdatePost { RunId = run.Id, Report = new()
            { RunId = run.Id, Outcome = BiosUpdateOutcomes.Updated, Message = "BIOS'en er opdateret til Q9ZKT79A.", CurrentVersion = "Q9ZKT79A" } }, T0.AddMinutes(9));
        Assert.True(res.Ok);
        var done = (await db.BiosUpdateRuns.FindAsync(run.Id))!;
        Assert.Equal(BiosUpdateStates.Updated, done.State);
        Assert.Equal(T0.AddMinutes(9), done.FinishedAt);
        var view = BiosUpdateService.View(done, null, T0.AddMinutes(5), T0.AddMinutes(10));
        Assert.True(view.CanCompare);
        Assert.False(view.CanReboot);
        // en sen, gentaget melding ændrer intet, men kvitteres
        Assert.True((await BiosUpdateService.OnAgentPost(db, m, new BiosUpdatePost { RunId = run.Id, Report = new() { Outcome = BiosUpdateOutcomes.Rejected } }, T0.AddMinutes(11))).Ok);
        Assert.Equal(BiosUpdateStates.Updated, (await db.BiosUpdateRuns.FindAsync(run.Id))!.State);
        // en anden maskine kan ikke melde på kørslen
        Assert.False((await BiosUpdateService.OnAgentPost(db, AddMachine(), new BiosUpdatePost { RunId = run.Id, Report = new() { Outcome = BiosUpdateOutcomes.Rejected } }, T0)).Ok);
    }

    [Fact]
    public async Task Cancelled_staging_job_closes_the_run_and_cannot_be_resumed_by_a_copied_job()
    {
        var m = AddMachine();
        var pkg = AddPackage();
        var (run, job, _) = await BiosUpdateService.Start(db, jobs, m, pkg, Yes, "admin", T0);
        Done(job!, JobStatus.Cancelled);   // annulleret, mens maskinen var offline
        // et kopieret job ("Prøv igen") med samme RunId: kørslens eget job kører ikke → hverken pakke eller kopi
        var copy = await jobs.Create(m.Id, JobTypes.BiosUpdate, job!.Title, Json.Des<JobParams>(job.ParamsJson), RunAs.System, null, "manual", "admin");
        copy.Status = JobStatus.Running; await db.SaveChangesAsync();
        var r = (await db.BiosUpdateRuns.FindAsync(run!.Id))!;
        Assert.False(await BiosUpdateService.OwnJobRunning(db, r, m.Id, null));
        Assert.False((await BiosUpdateService.OnAgentPost(db, m, new BiosUpdatePost { RunId = run.Id, JobId = copy.Id, Kind = BiosUpdatePostKinds.Snapshot,
            Settings = new BiosSettingsReport { Items = [new() { Name = "x", Value = "y" }] } }, T0)).Ok);
        // kørslen spærrer ikke for altid: den lukkes, og en ny opdatering kan startes
        await BiosUpdateService.CloseOrphans(db, m.Id, T0.AddMinutes(1));
        Assert.Equal(BiosUpdateStates.Failed, (await db.BiosUpdateRuns.FindAsync(run.Id))!.State);
        copy.Status = JobStatus.Cancelled; await db.SaveChangesAsync();
        Assert.Null((await BiosUpdateService.Start(db, jobs, m, pkg, Yes, "admin", T0.AddMinutes(2))).Error);
        Assert.Contains(JobTypes.BiosUpdate, JobTypes.NoRetry);
        Assert.Contains(JobTypes.BiosSettingsApply, JobTypes.NoRetry);
    }

    [Fact]
    public async Task Reboot_is_ordered_once_and_an_aborted_reboot_does_not_show_no_report()
    {
        var m = AddMachine();
        var (run, job, _) = await BiosUpdateService.Start(db, jobs, m, AddPackage(), Yes, "admin", T0);
        await BiosUpdateService.OnResult(db, m, Done(job!), new JobResult { BiosUpdate = new() { RunId = run!.Id, Outcome = BiosUpdateOutcomes.Staged, StagedAt = T0, Message = "klar" } }, T0);
        Assert.True(await BiosUpdateService.Waiting(db, m.Id));
        Assert.True(await BiosUpdateService.UpdateBusy(db, m.Id));   // gendannelse af indstillinger er spærret
        var (reboot, err) = await BiosUpdateService.Reboot(db, jobs, m, "admin", T0.AddMinutes(1));
        Assert.Null(err);
        Assert.NotNull((await BiosUpdateService.Reboot(db, jobs, m, "admin", T0.AddMinutes(2))).Error);   // ingen ekstra genstart
        var r = (await db.BiosUpdateRuns.FindAsync(run.Id))!;
        Assert.Equal("no_report", BiosUpdateFlow.Effective(r, null, T0.AddHours(2)));
        Done(reboot!, JobStatus.Cancelled);   // brugeren afbrød nedtællingen
        Assert.True(await BiosUpdateService.RebootAborted(db, r));
        Assert.Equal(BiosUpdateStates.Staged, BiosUpdateFlow.Effective(r, null, T0.AddHours(2), rebootAborted: true));
        Assert.Null((await BiosUpdateService.Reboot(db, jobs, m, "admin", T0.AddHours(3))).Error);   // og kan bestilles igen
    }

    [Fact]
    public void Still_pending_after_a_boot_keeps_the_run_staged_for_the_next_boot()
    {
        var run = new BiosUpdateRun { State = BiosUpdateStates.Staged, StagedAt = T0, RebootRequestedAt = T0.AddMinutes(1), UpdatedAt = T0 };
        Assert.True(BiosUpdateFlow.Apply(run, new BiosUpdateReport { Outcome = BiosUpdateOutcomes.Staged, StillPending = true, StagedAt = T0.AddHours(1), Message = "venter" }, T0.AddHours(1)));
        Assert.Equal((BiosUpdateStates.Staged, (DateTime?)T0.AddHours(1), (DateTime?)null), (run.State, run.StagedAt, run.RebootRequestedAt));
        Assert.Equal(BiosUpdateStates.Staged, BiosUpdateFlow.Effective(run, T0.AddMinutes(30), T0.AddHours(2)));
    }

    [Fact]
    public async Task Job_failing_without_a_report_fails_the_run_and_verify_is_offered_after_staging()
    {
        var m = AddMachine();
        var (run, job, _) = await BiosUpdateService.Start(db, jobs, m, AddPackage(), Yes, "admin", T0);
        await BiosUpdateService.OnResult(db, m, Done(job!, JobStatus.Failed), new JobResult { Status = JobStatus.Failed, Output = "Agenten er ikke klar til BIOS-jobs endnu." }, T0);
        var r = (await db.BiosUpdateRuns.FindAsync(run!.Id))!;
        Assert.Equal(BiosUpdateStates.Failed, r.State);
        Assert.Contains("ikke klar", r.Message);
        Assert.NotNull((await BiosUpdateService.Verify(db, jobs, m, "admin")).Error);   // intet lagt klar
        // agenten melder senere, at den alligevel lå klar (fx efter en genstart af agenten): agenten vinder
        await BiosUpdateService.OnAgentPost(db, m, new BiosUpdatePost { RunId = run.Id, Report = new() { Outcome = BiosUpdateOutcomes.Staged, StagedAt = T0 } }, T0);
        Assert.Equal(BiosUpdateStates.Staged, (await db.BiosUpdateRuns.FindAsync(run.Id))!.State);
        var (vjob, verr) = await BiosUpdateService.Verify(db, jobs, m, "admin");
        Assert.Null(verr);
        Assert.Equal(BiosUpdatePhases.Verify, Json.Des<JobParams>(vjob!.ParamsJson).BiosUpdate!.Phase);
    }

    [Fact]
    public async Task Linux_is_only_offered_the_capsule_route_when_the_package_has_a_real_capsule()
    {
        // Lenovos USB-pakke til ThinkCentre er en rå ROM: kun USB-stikket (Linux) og Windows-vejen må tilbydes
        var raw = AddMachine(capsule: false);
        Assert.Equal(BiosUpdateService.RawRomText, (await BiosUpdateService.Check(db, jobs, raw, "admin", T0)).Error);
        var s = db.BiosStatus.Single(x => x.MachineId == raw.Id);
        var route = BiosUpdateService.ServerChecks(raw, s, null, null, false, T0, capsule: false).Single(c => c.Id == "route");
        Assert.Equal(BiosCheckStates.Fail, route.State);
        Assert.Contains("USB-stikket", route.Text);
        Assert.DoesNotContain("Secure Boot skal ikke", route.Text);
        Assert.Equal((null, BiosUpdateService.NoCapsuleSeenText), BiosUpdateService.RouteFor(raw, Json.Des<SystemFacts>(raw.FactsJson), s, null));
        Assert.Equal(BiosUpdateRoutes.LenovoExe, BiosUpdateService.RouteFor(AddMachine(platform: Platforms.Windows), new SystemFacts(), s).Route);
        Assert.Equal(BiosUpdateRoutes.Capsule, BiosUpdateService.RouteFor(raw, new SystemFacts(), s, true).Route);
    }

    [Fact]
    public void A_raw_rom_in_the_zip_is_not_a_capsule_but_a_capsule_header_is()
    {
        var dir = Directory.CreateTempSubdirectory("fd-cap");
        try
        {
            static string Zip(string dir, string name, byte[] rom)
            {
                var path = Path.Combine(dir, name);
                using var z = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
                using (var e = z.CreateEntry("BIOS/imageM1U.ROM").Open()) e.Write(rom);
                using (var t = z.CreateEntry("README.TXT").Open()) t.Write("x"u8);
                return path;
            }
            // rå AMI-ROM: firmware-volume forrest (16 nul-byte og FV-GUID), ingen kapselheader
            var rom = new byte[2 << 20];
            "_FVH"u8.CopyTo(rom.AsSpan(40));
            Assert.False(BiosRom.ZipHasCapsule(Zip(dir.FullName, "raw.zip", rom)));
            // samme ROM med en EFI_CAPSULE_HEADER foran (FMP-GUID, HeaderSize 32, CapsuleImageSize = hele filen)
            var cap = new byte[rom.Length + 32];
            BiosRom.FmpCapsule.ToByteArray().CopyTo(cap, 0);
            BitConverter.GetBytes(32u).CopyTo(cap, 16);
            BitConverter.GetBytes((uint)cap.Length).CopyTo(cap, 24);
            rom.CopyTo(cap, 32);
            Assert.True(BiosRom.ZipHasCapsule(Zip(dir.FullName, "cap.zip", cap)));
            Assert.Null(BiosRom.ZipHasCapsule(Path.Combine(dir.FullName, "findes-ikke.zip")));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Server_checks_explain_why_the_button_is_off()
    {
        var m = AddMachine();
        var s = db.BiosStatus.Single(x => x.MachineId == m.Id);
        var ok = BiosUpdateService.ServerChecks(m, s, null, null, false, T0.AddMinutes(1), capsule: true);
        Assert.False(BiosPreconditions.Blocked(ok));
        Assert.Equal(BiosCheckStates.Warn, ok.Single(c => c.Id == "package").State);   // hentes først på et tryk
        var busy = BiosUpdateService.ServerChecks(m, s, null, null, true, T0.AddHours(1), capsule: true);
        Assert.True(BiosPreconditions.Blocked(busy));
        Assert.Equal(BiosCheckStates.Warn, busy.Single(c => c.Id == "online").State);
        s.State = BiosStates.Newest;
        Assert.Equal(BiosCheckStates.Fail, BiosUpdateService.ServerChecks(m, s, null, null, false, T0).Single(c => c.Id == "newer").State);
    }

    [Fact]
    public async Task An_orphaned_staging_run_does_not_block_automatic_reboots()
    {
        var m = AddMachine();
        var (run, job, _) = await BiosUpdateService.Start(db, jobs, m, AddPackage(), Yes, "admin", T0);
        Assert.True(await BiosUpdateService.Waiting(db, m.Id, T0.AddMinutes(1)));        // klargøringen kører (jobbet venter)
        Done(job!, JobStatus.Expired);                                                     // pc'en var slukket: jobbet udløb
        Assert.False(await BiosUpdateService.Waiting(db, m.Id, T0.AddDays(1)), "en forældreløs klargøring må ikke spærre, til nogen åbner BIOS-siden");
        Assert.Equal(BiosUpdateStates.Staging, (await db.BiosUpdateRuns.FindAsync(run!.Id))!.State);   // ingen har lukket den endnu
        // et nyt job, der lige er ved at blive oprettet, tæller (samme regel som CloseOrphans)
        Assert.True(BiosUpdateService.Alive(null, T0, T0.AddMinutes(2)));
        Assert.False(BiosUpdateService.Alive(null, T0, T0.AddMinutes(10)));
        Assert.True(BiosUpdateService.Alive(JobStatus.Running, T0, T0.AddHours(5)));
        Assert.False(BiosUpdateService.Alive(JobStatus.Cancelled, T0, T0.AddMinutes(1)));
        // agenten melder alligevel "klar" bagefter: så spærrer den igen
        await BiosUpdateService.OnAgentPost(db, m, new BiosUpdatePost { RunId = run.Id, Report = new() { Outcome = BiosUpdateOutcomes.Staged, StagedAt = T0 } }, T0.AddDays(1));
        Assert.True(await BiosUpdateService.Waiting(db, m.Id, T0.AddDays(1)));
    }

    [Fact]
    public async Task A_reboot_ordered_before_the_bios_start_blocks_staging()
    {
        var m = AddMachine();
        // vedligehold eller Home Assistant har bestilt en genstart med nedtælling – den ville installere BIOS'en uden opsyn
        var reboot = await jobs.Create(m.Id, JobTypes.Reboot, "Vedligehold: genstart", new JobParams { Delay = 300 }, RunAs.System, null, "maintenance", "planlægger");
        Assert.True(await BiosUpdateService.PowerPending(db, m.Id));
        var (run, _, err) = await BiosUpdateService.Start(db, jobs, m, AddPackage(), Yes, "admin", T0);
        Assert.Null(run);
        Assert.Equal(BiosUpdateService.PowerPendingText, err);
        var checks = BiosUpdateService.ServerChecks(m, await db.BiosStatus.FirstAsync(x => x.MachineId == m.Id), null, null, false, T0, capsule: true, powerPending: true);
        Assert.Equal(BiosCheckStates.Fail, checks.Single(c => c.Id == "power").State);
        // agenten har meldt den planlagte genstart (status "genstarter"): stadig spærret
        reboot.Status = JobStatus.Restarting; await db.SaveChangesAsync();
        Assert.NotNull((await BiosUpdateService.Start(db, jobs, m, AddPackage(), Yes, "admin", T0)).Error);
        // annulleret: nu kan den startes
        Done(reboot, JobStatus.Cancelled);
        Assert.Null((await BiosUpdateService.Start(db, jobs, m, AddPackage(), Yes, "admin", T0)).Error);
        // et tjek ændrer intet og spærres ikke af en bestilt genstart
        var m2 = AddMachine();
        await jobs.Create(m2.Id, JobTypes.Reboot, "Genstart (Home Assistant)", new JobParams { Delay = 300 }, RunAs.System, null, "homeassistant", "Home Assistant");
        Assert.Null((await BiosUpdateService.Check(db, jobs, m2, "admin", T0)).Error);
    }

    [Fact]
    public async Task Power_must_not_be_cut_while_a_staged_bios_may_be_flashing()
    {
        var m = AddMachine();
        var (run, job, _) = await BiosUpdateService.Start(db, jobs, m, AddPackage(), Yes, "admin", T0);
        Assert.False(await BiosUpdateService.MayBeFlashing(db, m.Id, T0.AddMinutes(1)));   // klargøres stadig: intet flashes endnu
        await BiosUpdateService.OnResult(db, m, Done(job!), new JobResult { BiosUpdate = new() { RunId = run!.Id, Outcome = BiosUpdateOutcomes.Staged, StagedAt = T0, Message = "klar" } }, T0);
        Assert.False(await BiosUpdateService.MayBeFlashing(db, m.Id, T0.AddMinutes(2)));   // klar, men ingen genstart: strømmen kan slås fra uden fare
        var (reboot, _) = await BiosUpdateService.Reboot(db, jobs, m, "admin", T0.AddMinutes(5));
        Assert.True(await BiosUpdateService.MayBeFlashing(db, m.Id, T0.AddMinutes(11)), "6 minutter inde i en Lenovo-flash: ingen sluk og tænd");
        Assert.True(await BiosUpdateService.MayBeFlashing(db, m.Id, T0.AddMinutes(40)));
        // genstarten blev først hentet senere (pc'en var slukket): vagten følger den, og vinduet regnes fra genstarten
        reboot!.Status = JobStatus.Hung; reboot.PowerAt = T0.AddHours(3); await db.SaveChangesAsync();
        Assert.True(await BiosUpdateService.MayBeFlashing(db, m.Id, T0.AddHours(3).AddMinutes(30)));
        Assert.False(await BiosUpdateService.MayBeFlashing(db, m.Id, T0.AddHours(5)));     // efter vinduet: hænger den, må ejeren slukke og tænde
        Assert.True(BiosUpdateService.FlashHangMinutes >= 30);
    }

    [Fact]
    public async Task Update_engine_migration_leaves_profiles_of_protected_machines_alone()
    {
        using var c = new SqliteConnection("Data Source=:memory:");
        c.Open();
        using var mdb = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite(c).Options);
        var migrator = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(mdb);
        migrator.Migrate("20261120131449_HealthHistory");
        Insert(c, "Profiles", new() { ["Id"] = 1, ["Name"] = "Kontor" });
        Insert(c, "Profiles", new() { ["Id"] = 2, ["Name"] = "Dantherm-styring" });
        Insert(c, "Machines", new() { ["Id"] = 1, ["Name"] = "beskyttet", ["ProfileId"] = 1, ["Protected"] = 1 });
        Insert(c, "Machines", new() { ["Id"] = 2, ["Name"] = "almindelig", ["ProfileId"] = 1, ["Protected"] = 0 });
        migrator.Migrate();
        var never = mdb.Profiles.AsNoTracking().OrderBy(p => p.Id).Select(p => p.UpdNeverAuto).ToList();
        Assert.Equal([false, true], never);   // kun ventilationsstyringens profil; den beskyttede maskine er spærret for sig selv
        var office = new Profile { Name = "Kontor", UpdNeverAuto = never[0] };
        Assert.NotNull(AutoUpdateGuard.Blocked(new Machine { Protected = true }, office));
        Assert.Null(AutoUpdateGuard.Blocked(new Machine(), office));   // de andre maskiner på profilen opdateres stadig om natten

        static void Insert(SqliteConnection c, string table, Dictionary<string, object> values)
        {
            // de øvrige påkrævede kolonner fyldes med 0/"" (skemaet på det tidspunkt i historikken)
            using (var info = c.CreateCommand())
            {
                info.CommandText = $"PRAGMA table_info({table})";
                using var r = info.ExecuteReader();
                while (r.Read())
                    if (r.GetInt32(3) == 1 && r.IsDBNull(4) && r.GetInt32(5) == 0 && !values.ContainsKey(r.GetString(1)))
                        values[r.GetString(1)] = r.GetString(2).Contains("TEXT", StringComparison.OrdinalIgnoreCase) ? "" : 0;
            }
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"INSERT INTO {table} ({string.Join(",", values.Keys)}) VALUES ({string.Join(",", values.Keys.Select(k => "$" + k))})";
            foreach (var (k, v) in values) cmd.Parameters.AddWithValue("$" + k, v);
            cmd.ExecuteNonQuery();
        }
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
