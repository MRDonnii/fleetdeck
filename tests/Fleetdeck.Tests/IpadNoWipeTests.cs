using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fleetdeck.Server.Endpoints;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;
using Xunit;

namespace Fleetdeck.Tests;

/// <summary>"Overvåg uden datatab": kommandoer (pymobiledevice3 11.58), sikkerhedsregler, backup-tjek og selve forløbet med en falsk iPad.</summary>
public class NoWipeCommandTests
{
    [Fact] public void RestoreWithoutSystemFilesUsesTheRealFlagNames()
    {
        Assert.Equal(["backup2", "restore", "--no-system", "--settings", "--no-remove", "--reboot", "--password", Pmd3.Secret1, "--source", "UDID-1", "/w/backup"],
            NoWipeCommands.Restore("/w/backup", "UDID-1", system: false));
        Assert.Equal("--system", NoWipeCommands.Restore("/w", "U", system: true)[2]);
        // koden står aldrig på kommandolinjen – kun pladsholderen, som indpakningen erstatter med stdin
        Assert.DoesNotContain(NoWipeCommands.Restore("/w", "U", false), a => a.Contains("kode"));
    }

    [Theory]
    [InlineData("16.7.16", false), InlineData("26.3", false), InlineData("27.0", true), InlineData("28.1.2", true), InlineData(null, false), InlineData("x", false)]
    public void FullRestoreOnlyFromIpadOs27(string? fw, bool full) => Assert.Equal(full, NoWipeCommands.FullRestore(fw));

    [Fact] public void ProbeCommands()
    {
        Assert.Equal(["lockdown", "get", "com.apple.purplebuddy"], NoWipeCommands.SetupState());
        Assert.Equal(["lockdown", "get", "com.apple.disk_usage"], NoWipeCommands.DiskUsage());
        Assert.Equal(["apps", "list", "--type", "User", "--calculate-sizes"], NoWipeCommands.UserApps());
        Assert.Equal(["profile", "supervise", "Familien", "--keybag", "/k.pem"], Pmd3.Supervise("Familien", "/k.pem"));
    }

    [Fact] public void ProbeOutputIsParsed()
    {
        using var s = JsonDocument.Parse("""{"SetupDone": false, "ForceNoBuddy": false}""");
        Assert.False(NoWipeCommands.SetupDone(s.RootElement));
        using var d = JsonDocument.Parse("""{"TotalDataCapacity": 30000000000, "TotalDataAvailable": 10000000000, "TotalDiskCapacity": 32000000000}""");
        Assert.Equal((30_000_000_000L, 10_000_000_000L), NoWipeCommands.DiskUsage(d.RootElement));
        using var a = JsonDocument.Parse("""{"com.a": {"StaticDiskUsage": 100, "DynamicDiskUsage": 5}, "com.b": {"StaticDiskUsage": 50}, "com.c": {}}""");
        Assert.Equal((3, 150L), NoWipeCommands.UserApps(a.RootElement));
    }

    [Fact] public void SnapshotNamesKeepTheUdid()
    {
        var n = MobileSyncSnapshot.Name("00008020-001A", new DateTime(2026, 11, 17, 22, 5, 9));
        Assert.Equal("00008020-001A.fleetdeck-20261117-220509", n);
        Assert.True(MobileSyncSnapshot.IsSnapshot(n));
        Assert.False(MobileSyncSnapshot.IsSnapshot("00008020-001A"));
    }

    [Fact] public void KeybagPemIsTheSameAsTheServers()
    {
        var (cert, key) = SupervisionIdentity.Create("Familien", DateTimeOffset.UtcNow);
        Assert.Equal(SupervisionIdentity.ToPem(cert, key), IosKeybagPem.FromDer(cert, key));
    }

    [Fact] public void PlanStepsAreNumberedAndEndAt100()
    {
        var a = NoWipePlan.Steps(NoWipeMethods.RestoreNoSystem, freshBackup: true);
        Assert.Equal([NoWipePlan.Check, NoWipePlan.Backup, NoWipePlan.Fetch, NoWipePlan.Erase, NoWipePlan.Wait, NoWipePlan.Supervise, NoWipePlan.Restore,
            NoWipePlan.Profile, NoWipePlan.Verify], a.Select(s => s.Id));
        Assert.StartsWith("Trin 1/9: ", a[0].Title);
        Assert.Equal(100, a[^1].To);
        Assert.True(a.Zip(a.Skip(1)).All(x => x.First.To == x.Second.From));
        var b = NoWipePlan.Steps(NoWipeMethods.SuperviseOnly, freshBackup: false);
        Assert.DoesNotContain(NoWipePlan.Restore, b.Select(s => s.Id));
        Assert.DoesNotContain(NoWipePlan.Fetch, b.Select(s => s.Id));
    }

    [Fact] public void NoWipeHasItsOwnSecretsAndCannotBeStartedAsAnOrdinaryAction()
    {
        Assert.Contains(UsbIpadSecretKinds.Keybag, UsbIpadEndpoints.SecretKinds(UsbIpadActions.SuperviseNoWipe));
        Assert.Contains(UsbIpadSecretKinds.BackupPassword, UsbIpadEndpoints.SecretKinds(UsbIpadActions.SuperviseNoWipe));
        Assert.True(IosJobs.NeedsIdentity(IosActions.SuperviseNoWipe) && IosJobs.NeedsBackupPassword(IosActions.SuperviseNoWipe));
        Assert.Contains(IosActions.SuperviseNoWipe, IosActions.Erasing);
        Assert.Contains(UsbIpadActions.SuperviseNoWipe, UsbIpadActions.NoWipeOnly);
    }
}

public class NoWipeSafetyTests
{
    private const long G = 1L << 30;
    private static readonly NoWipeDeviceState Full = new(true, 64 * G, 30 * G, 25, 10 * G, "Activated");

    [Fact] public void UsedSpaceLeavesOutTheApps()
    {
        Assert.Equal(34 * G, Full.UsedBytes);
        Assert.Equal(24 * G, Full.ComparableBytes);
        Assert.Null(new NoWipeDeviceState(null, null, 5, null, null).UsedBytes);
    }

    [Fact] public void AnEmptyOrUnsetIpadIsNeverBackedUp()
    {
        Assert.Null(NoWipeSafety.RefuseBackup(Full));
        Assert.Contains("opsætningsassistenten", NoWipeSafety.RefuseBackup(Full with { SetupDone = false }));
        Assert.Contains("ikke aktiveret", NoWipeSafety.RefuseBackup(Full with { ActivationState = "Unactivated" }));
        Assert.Contains("ingen apps", NoWipeSafety.RefuseBackup(Full with { UserApps = 0 }));
        Assert.Contains("næsten tom", NoWipeSafety.RefuseBackup(Full with { AvailableBytes = 63 * G, AppStaticBytes = 0 }));
        // ukendte værdier stopper ikke en backup af en iPad i brug
        Assert.Null(NoWipeSafety.RefuseBackup(new NoWipeDeviceState(null, null, null, null, null)));
    }

    private static NoWipeCandidate C(string set, int day, long bytes, bool? enc = true, bool? ok = true, bool? empty = false, string store = NoWipeCandidate.Mac) => new()
    {
        Store = store, Set = set, Created = new DateTime(2026, 11, day, 12, 0, 0, DateTimeKind.Utc), Bytes = bytes, Parts = 1, Encrypted = enc, Verified = ok, SourceEmpty = empty,
    };

    [Fact] public void TheNewestGoodBackupWinsAndBadOnesAreSkippedWithAReason()
    {
        var list = new[]
        {
            C("ipad-1-20261117-000000", 17, 80L << 20, empty: true),   // tonight's empty backup
            C("ipad-1-20261116-000000", 16, 20 * G, ok: null),         // not checked
            C("ipad-20261115-000000", 15, 3 * G, store: NoWipeCandidate.Usb),   // too small compared to the iPad
            C("ipad-1-20261114-000000", 14, 15 * G),
            C("ipad-1-20261101-000000", 1, 16 * G),
        };
        var c = NoWipeSafety.Select(list, 24 * G, null, null);
        Assert.Null(c.Error);
        Assert.Equal("ipad-1-20261114-000000", c.Chosen!.Set);
        Assert.Equal(3, c.Notes.Count);
        Assert.Contains(c.Notes, n => n.Contains("tom iPad"));
        Assert.Contains(c.Notes, n => n.Contains("ikke tjekket"));
        Assert.Contains(c.Notes, n => n.Contains("i forhold til"));
    }

    [Fact] public void AnExplicitChoiceIsUsedEvenIfTheRulesWouldSkipIt()
    {
        var list = new[] { C("a", 17, 1 * G, ok: null), C("b", 16, 20 * G) };
        var c = NoWipeSafety.Select(list, 24 * G, NoWipeCandidate.Mac, "a");
        Assert.Equal("a", c.Chosen!.Set);
        Assert.Contains(c.Notes, n => n.Contains("valgt udtrykkeligt"));
        Assert.NotNull(NoWipeSafety.Select(list, 24 * G, NoWipeCandidate.Usb, "a").Error);   // wrong store
        Assert.NotNull(NoWipeSafety.Select(list, 24 * G, null, "missing").Error);
    }

    [Fact] public void UnknownUsageRequiresAnExplicitChoice()
    {
        var list = new[] { C("b", 16, 20 * G) };
        Assert.Contains("udtrykkeligt", NoWipeSafety.Select(list, null, null, null).Error);
        Assert.Equal("b", NoWipeSafety.Select(list, null, null, "b").Chosen!.Set);
    }

    [Fact] public void NothingGoodMeansNoChoice()
    {
        Assert.Contains("ingen backup", NoWipeSafety.Select([], 5 * G, null, null).Error);
        Assert.Contains("opfylder", NoWipeSafety.Select([C("x", 1, 100L << 20), C("y", 2, 10 * G, enc: false)], 1 * G, null, null).Error);
    }
}

public class NoWipeBackupFileTests
{
    private static void Tlv(MemoryStream ms, string tag, byte[] v)
    {
        ms.Write(Encoding.ASCII.GetBytes(tag));
        var len = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(len, (uint)v.Length); ms.Write(len);
        ms.Write(v);
    }

    private static byte[] U32(int v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, (uint)v); return b; }

    internal static byte[] Keybag(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(20); var dpsl = RandomNumberGenerator.GetBytes(20);
        var pk = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), dpsl, 50, HashAlgorithmName.SHA256, 32);
        pk = Rfc2898DeriveBytes.Pbkdf2(pk, salt, 10, HashAlgorithmName.SHA1, 32);
        using var ms = new MemoryStream();
        Tlv(ms, "VERS", U32(4)); Tlv(ms, "TYPE", U32(1)); Tlv(ms, "UUID", RandomNumberGenerator.GetBytes(16));
        Tlv(ms, "WRAP", U32(0)); Tlv(ms, "SALT", salt); Tlv(ms, "ITER", U32(10)); Tlv(ms, "DPWT", U32(1)); Tlv(ms, "DPIC", U32(50)); Tlv(ms, "DPSL", dpsl);
        Tlv(ms, "UUID", RandomNumberGenerator.GetBytes(16)); Tlv(ms, "CLAS", U32(1)); Tlv(ms, "WRAP", U32(2)); Tlv(ms, "KTYP", U32(0));
        Tlv(ms, "WPKY", IosBackupKeybag.Wrap(pk, RandomNumberGenerator.GetBytes(32)));
        return ms.ToArray();
    }

    internal static string ManifestXml(bool encrypted, byte[]? keybag, string udid) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0"><dict>
          <key>IsEncrypted</key><{(encrypted ? "true" : "false")}/>
          {(keybag is null ? "" : $"<key>BackupKeyBag</key><data>{Convert.ToBase64String(keybag)}</data>")}
          <key>Lockdown</key><dict><key>UniqueDeviceID</key><string>{udid}</string><key>ProductVersion</key><string>16.7.16</string></dict>
          <key>Version</key><string>10.0</string><key>Date</key><date>2026-11-17T20:00:00Z</date><key>Size</key><integer>-1</integer>
        </dict></plist>
        """;

    /// <summary>En lille binær plist: { IsEncrypted: true, Data: 01 02, N: 5, S: "hej", A: [ -2 ] }.</summary>
    private static byte[] BinaryPlist()
    {
        byte[][] objs =
        [
            [0xD5, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10],
            [0x5B, .. Encoding.ASCII.GetBytes("IsEncrypted")], [0x54, .. Encoding.ASCII.GetBytes("Data")], [0x51, (byte)'N'], [0x51, (byte)'S'], [0x51, (byte)'A'],
            [0x09], [0x42, 1, 2], [0x10, 5], [0x53, .. Encoding.ASCII.GetBytes("hej")], [0xA1, 11], [0x13, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFE],
        ];
        var ms = new MemoryStream();
        ms.Write("bplist00"u8);
        var offs = new List<long>();
        foreach (var o in objs) { offs.Add(ms.Position); ms.Write(o); }
        var table = ms.Position;
        foreach (var o in offs) ms.WriteByte((byte)o);
        var trailer = new byte[32];
        trailer[6] = 1; trailer[7] = 1;
        BinaryPrimitives.WriteUInt64BigEndian(trailer.AsSpan(8), (ulong)objs.Length);
        BinaryPrimitives.WriteUInt64BigEndian(trailer.AsSpan(16), 0);
        BinaryPrimitives.WriteUInt64BigEndian(trailer.AsSpan(24), (ulong)table);
        ms.Write(trailer);
        return ms.ToArray();
    }

    [Fact] public void BinaryPlistsAreRead()
    {
        var d = ApplePlist.ReadDict(BinaryPlist())!;
        Assert.Equal(true, d["IsEncrypted"]);
        Assert.Equal(new byte[] { 1, 2 }, d["Data"]);
        Assert.Equal(5L, d["N"]);
        Assert.Equal("hej", d["S"]);
        Assert.Equal(-2L, Assert.Single((List<object?>)d["A"]!));
        Assert.Throws<InvalidDataException>(() => ApplePlist.Read([.. "bplist00"u8, 1, 2, 3]));
    }

    [Fact] public void XmlPlistsAreRead()
    {
        var d = ApplePlist.ReadDict(Encoding.UTF8.GetBytes(ManifestXml(true, [9, 8], "U1")))!;
        Assert.Equal(true, d["IsEncrypted"]);
        Assert.Equal(new byte[] { 9, 8 }, d["BackupKeyBag"]);
        Assert.Equal(-1L, d["Size"]);
        Assert.Equal(new DateTime(2026, 11, 17, 20, 0, 0, DateTimeKind.Utc), d["Date"]);
        Assert.Equal("U1", ((Dictionary<string, object?>)d["Lockdown"]!)["UniqueDeviceID"]);
    }

    [Fact] public void TheBackupIsCheckedAgainstTheHouseholdPassword()
    {
        var root = Directory.CreateTempSubdirectory("fd-nowipe-").FullName;
        try
        {
            var dir = Path.Combine(root, "UDID-9");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "Manifest.plist"), ManifestXml(true, Keybag("hus-kode-1234"), "UDID-9"));
            Assert.Equal(dir, IosBackupManifest.FindBackupDir(root));
            Assert.Equal(new IosBackupManifest.Info(true, true, "UDID-9"), IosBackupManifest.Inspect(dir, "hus-kode-1234"));
            Assert.False(IosBackupManifest.Inspect(dir, "forkert").Verified);
            File.WriteAllText(Path.Combine(dir, "Manifest.plist"), ManifestXml(false, null, "UDID-9"));
            Assert.Equal(new IosBackupManifest.Info(false, null, "UDID-9"), IosBackupManifest.Inspect(dir, "hus-kode-1234"));
            Assert.Equal(new IosBackupManifest.Info(null, null, null), IosBackupManifest.Inspect(root, "x"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact] public void UnpackingSkipsMacMetadataFilesAndJoinsTheParts()
    {
        var root = Directory.CreateTempSubdirectory("fd-unpack-").FullName;
        try
        {
            var tgz = Path.Combine(root, "all.tgz");
            using (var fs = File.Create(tgz))
            using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
            using (var w = new TarWriter(gz, TarEntryFormat.Pax))
            {
                w.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "UDID-1/"));
                w.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "UDID-1/Manifest.plist") { DataStream = new MemoryStream(RandomNumberGenerator.GetBytes(70_000)) });
                w.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "UDID-1/._Manifest.plist") { DataStream = new MemoryStream([1, 2, 3]) });
                w.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "UDID-1/ab/abcdef") { DataStream = new MemoryStream([4, 5]) });
            }
            var bytes = File.ReadAllBytes(tgz);
            var p1 = Path.Combine(root, "p1"); var p2 = Path.Combine(root, "p2");
            File.WriteAllBytes(p1, bytes[..(bytes.Length / 2)]); File.WriteAllBytes(p2, bytes[(bytes.Length / 2)..]);
            var dest = Path.Combine(root, "out");
            UsbIpadJobs.Unpack([p1, p2], dest);
            Assert.Equal(70_000, new FileInfo(Path.Combine(dest, "UDID-1", "Manifest.plist")).Length);
            Assert.True(File.Exists(Path.Combine(dest, "UDID-1", "ab", "abcdef")));
            Assert.False(File.Exists(Path.Combine(dest, "UDID-1", "._Manifest.plist")));
        }
        finally { Directory.Delete(root, true); }
    }
}

/// <summary>Opbevaringen: det valgte sæt fastholdes, og sæt, et kørende job kan bruge, ryddes ikke op.</summary>
public class NoWipeStorageTests
{
    private static void MacSet(string dir, string set, DateTime created, bool empty = false)
    {
        Directory.CreateDirectory(Path.Combine(dir, set));
        File.WriteAllText(Path.Combine(dir, set, "p001.tar.gz"), "x");
        File.WriteAllText(Path.Combine(dir, set, "meta.json"), JsonSerializer.Serialize(new IpadBackups.Meta
        {
            Set = set, Created = created, Parts = 1, Bytes = 1, Encrypted = true, PasswordVerified = true, DeviceEmpty = empty,
        }));
    }

    private static void UsbSet(string dir, string set)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(UsbIpadBackups.Part(dir, set, 1), "x");
        Assert.NotNull(UsbIpadBackups.Finish(dir, set, 1, "U", "pc", "iPad", true, true, false, 123));
    }

    [Fact] public void MacPruningKeepsThePinnedAndProtectedSets()
    {
        var dir = Directory.CreateTempSubdirectory("fd-mac-").FullName;
        try
        {
            for (var i = 1; i <= 6; i++) MacSet(dir, $"ipad-1-202611{i:00}-000000", new DateTime(2026, 11, i));
            IpadBackups.PinDir(dir, "ipad-1-20261101-000000");
            IpadBackups.PruneDir(dir, 3, new HashSet<string> { "ipad-1-20261102-000000" });
            var left = IpadBackups.ListDir(dir).Select(x => x.Set).Order().ToList();
            Assert.Equal(["ipad-1-20261101-000000", "ipad-1-20261102-000000", "ipad-1-20261104-000000", "ipad-1-20261105-000000", "ipad-1-20261106-000000"], left);
            Assert.True(IpadBackups.ListDir(dir).Single(x => x.Set == "ipad-1-20261101-000000").Pinned);
            // en ny fastholdelse flytter mærket
            IpadBackups.PinDir(dir, "ipad-1-20261105-000000");
            Assert.Equal(["ipad-1-20261105-000000"], IpadBackups.ListDir(dir).Where(x => x.Pinned).Select(x => x.Set));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact] public void UsbPruningKeepsThePinnedAndProtectedSets()
    {
        var dir = Directory.CreateTempSubdirectory("fd-usb-").FullName;
        try
        {
            for (var i = 1; i <= 6; i++) UsbSet(dir, $"ipad-202611{i:00}-000000");
            UsbIpadBackups.Pin(dir, "ipad-20261101-000000");
            UsbIpadBackups.Prune(dir, 3, new HashSet<string> { "ipad-20261102-000000" });
            var left = UsbIpadBackups.ListDir(dir);
            Assert.Equal(["ipad-20261106-000000", "ipad-20261105-000000", "ipad-20261104-000000", "ipad-20261102-000000", "ipad-20261101-000000"], left.Select(x => x.Set));
            var pinned = Assert.Single(left, x => x.Pinned);
            Assert.Equal("ipad-20261101-000000", pinned.Set);
            Assert.True(pinned.Encrypted == true && pinned.Verified == true && pinned.DeviceEmpty == false && pinned.DeviceBytes == 123);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact] public void CandidatesComeFromBothStores()
    {
        var root = Directory.CreateTempSubdirectory("fd-cand-").FullName;
        try
        {
            var mac = Path.Combine(root, "1"); var usb = Path.Combine(root, "ipad-1");
            MacSet(mac, "ipad-1-20261110-000000", new DateTime(2026, 11, 10), empty: true);
            UsbSet(usb, "ipad-20261111-000000");
            var c = NoWipeBackups.Candidates(mac, usb);
            Assert.Equal([NoWipeCandidate.Usb, NoWipeCandidate.Mac], c.Select(x => x.Store));
            Assert.True(c[1].SourceEmpty);
            Assert.Equal("taget af en tom iPad", NoWipeSafety.Ineligible(c[1], null));
        }
        finally { Directory.Delete(root, true); }
    }
}

/// <summary>Forløbet med en falsk iPad: intet slettes, før backuppen er valgt, hentet og tjekket.</summary>
public class NoWipeFlowTests
{
    private const long G = 1L << 30;
    private const string Pw = "hus-kode-1234";

    private sealed class Fake
    {
        public NoWipeDeviceState State = new(true, 64 * G, 40 * G, 20, 5 * G, "Activated");
        public bool SupervisedAfter = true, SuperviseOk = true, RestoreOk = true, FetchOk = true, FetchWrongPassword;
        public string Firmware = "16.7.16";
        public int Erases, Restores, Fresh, Supervises;
        public bool? RestoredWithSystem, SkipSetup;
        public NoWipeCandidate? Chosen;
        public readonly List<string> Log = [];
        private bool _erased;

        public NoWipeOps Ops() => new()
        {
            Line = Log.Add,
            Phase = (_, _, _) => { },
            Read = _ => Task.FromResult<IosDeviceInfo?>(new IosDeviceInfo
            {
                Ecid = "0x1", Udid = "UDID-1", Firmware = Firmware, Paired = true, Supervised = _erased ? (Supervises > 0 && SuperviseOk && (Restores == 0 || SupervisedAfter)) : false,
            }),
            Probe = (_, _) => Task.FromResult(State),
            FreshBackup = (_, _, _, _) =>
            {
                Fresh++;
                return Task.FromResult<NoWipeCandidate?>(new NoWipeCandidate { Store = "usb", Set = "ipad-20261117-230000", Created = DateTime.UtcNow, Bytes = 20 * G, Parts = 2, Encrypted = true, Verified = true });
            },
            Chosen = (c, _) => { Chosen = c; return Task.CompletedTask; },
            Fetch = (c, dest, _, _, _) =>
            {
                if (!FetchOk) return Task.FromResult(false);
                var dir = Path.Combine(dest, "UDID-1");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "Manifest.plist"), NoWipeBackupFileTests.ManifestXml(true, NoWipeBackupFileTests.Keybag(FetchWrongPassword ? "anden" : Pw), "UDID-1"));
                return Task.FromResult(true);
            },
            Erase = (_, _, _) => { Erases++; _erased = true; return Task.FromResult(true); },
            WaitBack = (_, _, _, _) => Task.FromResult<IosDeviceInfo?>(new IosDeviceInfo { Ecid = "0x1", Udid = "UDID-1" }),
            Supervise = (skip, _, _, _) => { Supervises++; SkipSetup = skip; return Task.FromResult(SuperviseOk); },
            EnsurePaired = _ => Task.FromResult(true),
            Restore = (dir, source, system, _, _, _) =>
            {
                Restores++; RestoredWithSystem = system;
                Assert.Equal("UDID-1", source);
                Assert.True(File.Exists(Path.Combine(dir, source, "Manifest.plist")));
                return Task.FromResult(RestoreOk);
            },
            PairSupervised = _ => Task.FromResult<bool?>(true),
            InstallProfile = (_, _, _, _) => Task.FromResult(true),
        };
    }

    private static readonly NoWipeCandidate Good = new()
    {
        Store = "mac", Set = "ipad-1-20261110-000000", Created = new DateTime(2026, 11, 10), Bytes = 18 * G, Parts = 3, Encrypted = true, Verified = true, SourceEmpty = false,
    };
    private static readonly NoWipeCandidate EmptyOne = new()
    {
        Store = "mac", Set = "ipad-1-20261117-000000", Created = new DateTime(2026, 11, 17), Bytes = 60L << 20, Parts = 1, Encrypted = true, Verified = true, SourceEmpty = true,
    };

    private static async Task<(NoWipeResult, Fake)> Run(Action<Fake>? setup = null, string method = NoWipeMethods.RestoreNoSystem, bool fresh = false,
        NoWipeCandidate[]? list = null, string? set = null)
    {
        var f = new Fake();
        setup?.Invoke(f);
        var work = Directory.CreateTempSubdirectory("fd-flow-").FullName;
        try
        {
            var req = new NoWipeRequest(method, fresh, list ?? [EmptyOne, Good], set is null ? null : "mac", set, work, Pw, "Familien");
            return (await NoWipeFlow.Run(req, f.Ops(), CancellationToken.None), f);
        }
        finally { Directory.Delete(work, true); }
    }

    [Fact] public async Task MethodAErasesSupervisesAndRestoresWithoutSystemFiles()
    {
        var (r, f) = await Run();
        Assert.True(r.Ok);
        Assert.Equal(Good.Set, f.Chosen!.Set);   // tonight's empty backup is skipped
        Assert.Equal((1, 1, 1), (f.Erases, f.Supervises, f.Restores));
        Assert.False(f.RestoredWithSystem);
        Assert.False(f.SkipSetup);
        Assert.Contains(f.Log, l => l.Contains("Overvåget: ja"));
        Assert.Contains(f.Log, l => l.Contains("uden systemfiler"));
    }

    [Fact] public async Task IpadOs27UsesTheNormalFullRestore()
    {
        var (r, f) = await Run(x => x.Firmware = "27.0");
        Assert.True(r.Ok);
        Assert.True(f.RestoredWithSystem);
    }

    [Fact] public async Task LostSupervisionIsReportedWithTheFallbackOffer()
    {
        var (r, f) = await Run(x => x.SupervisedAfter = false);
        Assert.False(r.Ok);
        Assert.True(r.Restored);
        Assert.True(r.OfferSuperviseOnly);
        Assert.Equal(1, f.Erases);   // never erased a second time by itself
        Assert.Contains(f.Log, l => l.Contains("Overvåget uden gendannelse"));
        Assert.Contains(f.Log, l => l.Contains("Overvåget: NEJ"));
    }

    [Fact] public async Task AFreshBackupIsNeverTakenOfAnIpadInSetup()
    {
        var (r, f) = await Run(x => x.State = x.State with { SetupDone = false, AvailableBytes = 62 * G }, fresh: true);
        Assert.True(r.Ok);
        Assert.Equal(0, f.Fresh);
        Assert.Equal(Good.Set, f.Chosen!.Set);
        Assert.Contains(f.Log, l => l.StartsWith("Ny backup springes over"));
    }

    [Fact] public async Task AFreshBackupOfAnIpadInUseIsPreferred()
    {
        var (r, f) = await Run(fresh: true);
        Assert.True(r.Ok);
        Assert.Equal(1, f.Fresh);
        Assert.Equal("ipad-20261117-230000", f.Chosen!.Set);
    }

    [Fact] public async Task NothingIsErasedWithoutAGoodBackup()
    {
        var (r, f) = await Run(list: [EmptyOne]);
        Assert.False(r.Ok);
        Assert.False(r.Erased);
        Assert.Equal(0, f.Erases);
        Assert.Contains(f.Log, l => l.Contains("Intet er slettet"));
    }

    [Fact] public async Task NothingIsErasedIfTheBackupCannotBeFetchedOrUnlocked()
    {
        var (r1, f1) = await Run(x => x.FetchOk = false);
        Assert.Equal(0, f1.Erases);
        Assert.False(r1.Ok);
        var (r2, f2) = await Run(x => x.FetchWrongPassword = true);
        Assert.Equal(0, f2.Erases);
        Assert.Contains(f2.Log, l => l.Contains("låser ikke backuppen op"));
    }

    [Fact] public async Task AFailedSupervisionStillRestoresTheData()
    {
        var (r, f) = await Run(x => x.SuperviseOk = false);
        Assert.False(r.Ok);
        Assert.Equal(1, f.Restores);
        Assert.True(r.Restored);
    }

    [Fact] public async Task AFailedRestoreKeepsTheBackupAndSaysSo()
    {
        var (r, f) = await Run(x => x.RestoreOk = false);
        Assert.False(r.Ok);
        Assert.True(r.Erased);
        Assert.Contains(f.Log, l => l.Contains("ligger urørt") && l.Contains(Good.Set));
    }

    [Fact] public async Task SuperviseOnlyNeverRestoresAndSkipsSetup()
    {
        var (r, f) = await Run(method: NoWipeMethods.SuperviseOnly);
        Assert.True(r.Ok);
        Assert.Equal(0, f.Restores);
        Assert.True(f.SkipSetup);
        Assert.Equal(Good.Set, f.Chosen!.Set);   // the backup is still chosen and pinned
    }

    [Fact] public async Task AnExplicitlyChosenBackupIsUsed()
    {
        var (r, f) = await Run(set: EmptyOne.Set);
        Assert.True(r.Ok);
        Assert.Equal(EmptyOne.Set, f.Chosen!.Set);
    }
}
