using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Fleetdeck.MacPack;
using Fleetdeck.Shared;
using Xunit;

namespace Fleetdeck.Tests;

/// <summary>Mac-appen: kontrakten med tjenesten, administrator-kommandoerne og visningen af status.</summary>
public class MacAppTests
{
    private static readonly DateTime Now = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
    private static TrayStatus Fresh(Action<TrayStatus>? f = null) { var s = new TrayStatus { Machine = "MAC1", Connected = true, Written = Now }; f?.Invoke(s); return s; }

    [Fact]
    public void OnlySafeCommandsAreAllowedFromTheUserFolder()
    {
        foreach (var a in new[] { "upgrade_all", "sync", "power_postpone", "recheck_access" }) Assert.True(TrayCommands.UserAllowed(a), a);
        foreach (var a in new[] { "pause", "resume", "extension", "", null }) Assert.False(TrayCommands.UserAllowed(a), a ?? "null");
    }

    [Fact]
    public void CommandsRoundTripThroughBase64ForTheCommandLine()
    {
        var c = new TrayCommand { Action = "extension", Id = "homebrew", Minutes = 5 };
        var b64 = TrayCommands.Encode(c);
        Assert.Matches("^[A-Za-z0-9+/=]+$", b64);   // kan stå i en shell-kommando uden citering
        var back = TrayCommands.Decode(b64)!;
        Assert.Equal(("extension", "homebrew", 5), (back.Action, back.Id, back.Minutes));
        Assert.Null(TrayCommands.Decode("ikke base64!"));
        Assert.Null(TrayCommands.Parse("{\"action\":\"\"}"));
        Assert.Null(TrayCommands.Parse("{nonsens"));
    }

    [Fact]
    public void TrayStateFollowsTheWindowsTrayColours()
    {
        Assert.Equal(TrayView.State.NoService, TrayView.Of(null, Now));
        Assert.Equal(TrayView.State.NoService, TrayView.Of(Fresh(s => s.Written = Now.AddMinutes(-6)), Now));
        Assert.Equal(TrayView.State.Offline, TrayView.Of(Fresh(s => s.Connected = false), Now));
        Assert.Equal(TrayView.State.Power, TrayView.Of(Fresh(s => { s.PowerAt = Now.AddMinutes(30); s.PowerMode = JobTypes.Reboot; }), Now));
        Assert.Equal(TrayView.State.Ok, TrayView.Of(Fresh(s => s.PowerAt = Now.AddMinutes(-1)), Now));
        Assert.Equal(TrayView.State.Job, TrayView.Of(Fresh(s => s.Job = new TrayJob { Title = "Installerer Firefox" }), Now));
        Assert.Equal(TrayView.State.Attention, TrayView.Of(Fresh(s => s.Alerts = 2), Now));
        Assert.Equal(TrayView.State.Attention, TrayView.Of(Fresh(s => s.PausedUntil = Now.AddMinutes(10)), Now));
        Assert.Equal(TrayView.State.Ok, TrayView.Of(Fresh(s => s.PausedUntil = Now.AddMinutes(-10)), Now));
        Assert.Equal(TrayView.State.Attention, TrayView.Of(Fresh(s => s.MacAccess = new MacAccessInfo { FullDiskAccess = false }), Now));
        Assert.Equal(TrayView.State.Ok, TrayView.Of(Fresh(s => s.MacAccess = new MacAccessInfo { FullDiskAccess = null }), Now));
        Assert.Equal("#dc2626", TrayView.Color(TrayView.State.Offline));
        Assert.Equal("#2563eb", TrayView.Color(TrayView.State.Job));
        Assert.Equal("#d97706", TrayView.Color(TrayView.State.Attention));
        Assert.Equal("#16a34a", TrayView.Color(TrayView.State.Ok));
    }

    [Fact]
    public void HeadlineAndNotices()
    {
        Assert.Equal("Fleetdeck-tjenesten kører ikke", TrayView.Headline(null, Now));
        Assert.Equal("Forbundet · profil Skole", TrayView.Headline(Fresh(s => s.Profile = "Skole"), Now));
        Assert.Equal("Installerer Firefox", TrayView.Headline(Fresh(s => s.Job = new TrayJob { Title = "Installerer Firefox" }), Now));
        Assert.StartsWith("Mac'en genstarter kl. ", TrayView.Headline(Fresh(s => { s.PowerAt = Now.AddHours(1); s.PowerMode = JobTypes.Reboot; }), Now));
        Assert.Equal("Installerer X", TrayView.JobNotice(null, "Installerer X"));
        Assert.Equal("Færdig: Installerer X", TrayView.JobNotice("Installerer X", null));
        Assert.Null(TrayView.JobNotice("A", "A"));
        Assert.Null(TrayView.JobNotice(null, null));
        Assert.Equal("lige nu", TrayView.Ago(Now.AddSeconds(-5), Now));
        Assert.Equal("3 min. siden", TrayView.Ago(Now.AddMinutes(-3), Now));
        Assert.Equal("Kritisk", TrayView.Health("critical"));
    }

    [Fact]
    public void QuotingSurvivesNastyInput()
    {
        Assert.Equal("'a'\\''b'", MacInstall.Sh("a'b"));
        Assert.Equal("\"say \\\"hi\\\" \\\\o/\"", MacInstall.AppleScript("say \"hi\" \\o/"));
        var args = MacInstall.Elevated("echo \"x\"", "Hvorfor");
        Assert.Equal("-e", args[0]);
        Assert.Equal("do shell script \"echo \\\"x\\\"\" with prompt \"Hvorfor\" with administrator privileges", args[1]);
    }

    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void InstallScriptChecksTheAgentAgainAsRootAndInstallsTheMenuBar()
    {
        var s = MacInstall.InstallScript("/var/folders/x/fleetdeck-1/fleetdeck-agent", Sha, "http://192.168.1.5:8585", "k'ey;rm -rf /", 3, ["homebrew", "bad id;x", "mas"], "/Volumes/Fleetdeck/Fleetdeck.app");
        Assert.StartsWith("set -e; ", s);
        Assert.Contains("ditto '/Volumes/Fleetdeck/Fleetdeck.app' '/Applications/Fleetdeck.app'", s);
        Assert.Contains("mktemp -d /tmp/fleetdeck.XXXXXX", s);
        Assert.Contains($"echo '{Sha}  '\"$T/fleetdeck-agent\" | shasum -a 256 -c -s", s);
        Assert.Contains("--key 'k'\\''ey;rm -rf /'", s);    // nøglen kan ikke bryde ud af citeringen
        Assert.Contains("--profile 3", s);
        Assert.Contains("--extensions 'homebrew,mas'", s);  // ugyldige udvidelses-id'er ryger ud
        Assert.Contains("--menubar '/Applications/Fleetdeck.app' --service", s);
        Assert.DoesNotContain("ditto", MacInstall.InstallScript("/tmp/a", Sha, "http://s", "k", 0, [], "/Applications/Fleetdeck.app"));
        Assert.Throws<ArgumentException>(() => MacInstall.InstallScript("/tmp/a", "nope", "http://s", "k", 0, [], null));
        AssertShellSyntax(s);
        AssertShellSyntax(MacInstall.UninstallScript());
        AssertShellSyntax(MacInstall.AdminCommandScript(new TrayCommand { Action = "pause", Minutes = 60 }));
        AssertShellSyntax(MacInstall.RestartScript());
    }

    private static void AssertShellSyntax(string script)
    {
        if (!File.Exists("/bin/sh")) return;
        var p = Process.Start(new ProcessStartInfo("/bin/sh", ["-n", "-c", script]) { RedirectStandardError = true })!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"sh -n: {err}\n{script}");
    }

    [Fact]
    public void UninstallRemovesDaemonMenuBarAndConfig()
    {
        var s = MacInstall.UninstallScript();
        Assert.Contains("launchctl bootout system/com.fleetdeck.agent", s);
        foreach (var p in new[] { MacPaths.DaemonPlist, MacPaths.MenuBarPlist, MacPaths.AgentDir, MacPaths.ConfigDir, MacPaths.SupportDir }) Assert.Contains($"'{p}'", s);
    }

    [Fact]
    public void OsascriptExitCodesAreUnwrapped()
    {
        Assert.Equal(0, MacInstall.ScriptExitCode(0, ""));
        Assert.Equal(3, MacInstall.ScriptExitCode(1, "0:123: execution error: Forkert tilmeldingsnøgle (3)\n"));
        Assert.Equal(-128, MacInstall.ScriptExitCode(1, "execution error: User canceled. (-128)"));
        Assert.Equal(1, MacInstall.ScriptExitCode(1, "noget andet"));
        Assert.Contains("afbrudt", MacInstall.Explain(-128, ""));
        Assert.Contains("afvist", MacInstall.Explain(3, ""));
        Assert.Contains("kode 9", MacInstall.Explain(9, "linje\nsidste linje"));
    }

    [Fact]
    public void MenuBarLaunchAgentIsAValidPlist()
    {
        var x = XDocument.Parse(MacInstall.MenuBarAgentPlist("/Applications/Fleetdeck.app"));
        var strings = x.Descendants("string").Select(e => e.Value).ToList();
        Assert.Contains(MacPaths.MenuBarLabel, strings);
        Assert.Contains("/Applications/Fleetdeck.app/Contents/MacOS/Fleetdeck", strings);
        Assert.Contains("--tray", strings);
        Assert.Contains("Aqua", strings);
        Assert.Equal("x64", MacInstall.Arch(System.Runtime.InteropServices.Architecture.X64));
        Assert.Equal("arm64", MacInstall.Arch(System.Runtime.InteropServices.Architecture.Arm64));
    }
}

/// <summary>Mac-pakken bygget på Linux: ad hoc-signatur, .app, HFS+ og DMG.</summary>
public class MacPackTests
{
    /// <summary>Et lille, gyldigt 64-bit Mach-O-program: header, __PAGEZERO, __TEXT (med én sektion), __LINKEDIT – uden signatur.</summary>
    internal static byte[] FakeMachO(int textBytes = 20000, int linkEditBytes = 3000)
    {
        const int headerSpace = 4096;
        var textSize = headerSpace + textBytes;
        var d = new byte[textSize + linkEditBytes];
        new Random(7).NextBytes(d.AsSpan(headerSpace));
        BinaryPrimitives.WriteUInt32LittleEndian(d, MachOSigner.MhMagic64);
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(4), 0x0100000C);   // arm64
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(12), 2);           // MH_EXECUTE
        var o = 32; var n = 0;
        void Seg(string name, ulong fileOff, ulong fileSize, int nsects)
        {
            var size = 72 + nsects * 80;
            BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(o), 0x19);
            BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan((o + 4)), (uint)size);
            Encoding.ASCII.GetBytes(name).CopyTo(d, o + 8);
            BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan((o + 32)), fileSize);   // vmsize
            BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan((o + 40)), fileOff);
            BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan((o + 48)), fileSize);
            BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan((o + 64)), (uint)nsects);
            if (nsects > 0) BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan((o + 72 + 48)), headerSpace);   // sektionens filoffset
            o += size; n++;
        }
        Seg("__PAGEZERO", 0, 0, 0);
        Seg("__TEXT", 0, (ulong)textSize, 1);
        Seg("__LINKEDIT", (ulong)textSize, (ulong)linkEditBytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(16), (uint)n);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(20), (uint)(o - 32));
        return d;
    }

    [Fact]
    public void AdHocSignatureCoversEveryPageAndCanBeReplaced()
    {
        var bin = FakeMachO();
        Assert.Equal(-1, MachOSigner.Parse(bin).SigCmdOffset);
        var signed = MachOSigner.Sign(bin, "com.fleetdeck.test", null, out var cdhash);
        Assert.Equal(20, cdhash.Length);
        var lay = MachOSigner.Parse(signed);
        Assert.True(lay.SigCmdOffset > 0);
        Assert.Equal(0u, lay.DataOff % 16);
        Assert.Equal(signed.Length, (int)(lay.DataOff + lay.DataSize));
        var v = MachOSigner.Verify(signed);
        Assert.True(v.Ok, v.Why);
        Assert.Equal(("com.fleetdeck.test", 2), (v.Identifier, v.SpecialSlots));
        // __LINKEDIT skal række til og med signaturen
        var leSize = BinaryPrimitives.ReadUInt64LittleEndian(signed.AsSpan(lay.LinkEditCmdOffset + 48));
        Assert.Equal((ulong)signed.Length - lay.LinkEditFileOff, leSize);

        // signér igen (som når SDK'ens ad hoc-signatur erstattes) med Info.plist og CodeResources bundet ind
        var info = Encoding.UTF8.GetBytes("<plist/>"); var res = Encoding.UTF8.GetBytes("<plist>r</plist>");
        var again = MachOSigner.Sign(signed, "com.fleetdeck.app", new MachOSigner.Special(SHA256.HashData(info), SHA256.HashData(res)), out _);
        Assert.Equal(MachOSigner.Parse(signed).DataOff, MachOSigner.Parse(again).DataOff);   // samme kode, samme grænse
        var v2 = MachOSigner.Verify(again, info, res);
        Assert.True(v2.Ok, v2.Why);
        Assert.Equal(3, v2.SpecialSlots);
        Assert.False(MachOSigner.Verify(again, Encoding.UTF8.GetBytes("<plist>ændret</plist>"), res).Ok);
        // en ændret byte i koden opdages
        again[5000] ^= 0xFF;
        Assert.False(MachOSigner.Verify(again).Ok);
        Assert.Throws<InvalidDataException>(() => MachOSigner.Parse(new byte[64]));
    }

    [Fact]
    public void AppBundleIsSealed()
    {
        var dir = Directory.CreateTempSubdirectory("macpack-").FullName;
        try
        {
            var pub = Directory.CreateDirectory(Path.Combine(dir, "pub")).FullName;
            File.WriteAllBytes(Path.Combine(pub, "Fleetdeck"), FakeMachO());
            File.WriteAllText(Path.Combine(pub, "Fleetdeck.Shared.pdb"), "x");   // .pdb springes over
            var ico = File.ReadAllBytes(Path.Combine(RepoRoot, "src/Fleetdeck.Agent.Windows/fleetdeck.ico"));
            var icns = Icns.FromPngs(Icns.PngsFromIco(ico));
            Assert.Equal("icns", Encoding.ASCII.GetString(icns, 0, 4));
            Assert.Equal(icns.Length, (int)BinaryPrimitives.ReadUInt32BigEndian(icns.AsSpan(4)));
            Assert.Contains("ic08", Encoding.ASCII.GetString(icns));
            var app = Path.Combine(dir, "Fleetdeck.app");
            AppBundle.Build(new AppBundle.Options(pub, app, "3.16.0", Icns: icns));
            var c = Path.Combine(app, "Contents");
            var info = File.ReadAllBytes(Path.Combine(c, "Info.plist"));
            var plist = XDocument.Parse(Encoding.UTF8.GetString(info));
            var keys = plist.Descendants("key").Select(k => k.Value).ToList();
            Assert.DoesNotContain("LSUIElement", keys);   // i Dock og Cmd-Tab, så længe appen kører
            Assert.Contains("com.fleetdeck.app", plist.Descendants("string").Select(e => e.Value));
            Assert.Equal("APPL????", File.ReadAllText(Path.Combine(c, "PkgInfo")));
            var cr = File.ReadAllBytes(Path.Combine(c, "_CodeSignature", "CodeResources"));
            var crx = XDocument.Parse(Encoding.UTF8.GetString(cr));
            var files2 = crx.Root!.Element("dict")!.Elements().SkipWhile(e => e.Value != "files2").Skip(1).First();
            var hash2 = files2.Elements("dict").Single().Elements("data").Last().Value.Trim();
            Assert.Equal(Convert.ToBase64String(SHA256.HashData(icns)), hash2);
            Assert.Equal("Resources/Fleetdeck.icns", files2.Element("key")!.Value);
            var v = MachOSigner.Verify(File.ReadAllBytes(Path.Combine(c, "MacOS", "Fleetdeck")), info, cr);
            Assert.True(v.Ok, v.Why);
            Assert.Equal("com.fleetdeck.app", v.Identifier);

            // ekstra filer ved siden af programmet ville være usigneret kode i MacOS/
            File.WriteAllText(Path.Combine(pub, "libnoget.dylib"), "x");
            Assert.Throws<InvalidOperationException>(() => AppBundle.Build(new AppBundle.Options(pub, app, "3.16.0")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void UdzoRoundTripsWithChecksums()
    {
        var raw = new byte[3 * Udif.ChunkSectors * Udif.SectorSize + 7 * Udif.SectorSize];
        new Random(1).NextBytes(raw.AsSpan(0, 100_000));                       // tilfældigt (komprimeres ikke: rå bid)
        Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("Fleetdeck ", 50_000))).CopyTo(raw, Udif.ChunkSectors * Udif.SectorSize);   // komprimerbart
        // tredje bid er nuller
        using var ms = new MemoryStream();
        Udif.Write(new MemoryStream(raw), ms);
        var dmg = ms.ToArray();
        Assert.True(dmg.Length < raw.Length);
        Assert.Equal("koly", Encoding.ASCII.GetString(dmg, dmg.Length - 512, 4));
        Assert.Equal(raw, Udif.Read(dmg));
        dmg[10] ^= 1;   // data ændret → data-kontrolsummen passer ikke
        Assert.Throws<InvalidDataException>(() => Udif.Read(dmg));
    }

    [Fact]
    public void HfsVolumeHasCatalogInOrderAndFileData()
    {
        var hfs = new HfsPlus("Fleetdeck");
        var app = hfs.AddFolder(hfs.Root, "Fleetdeck.app");
        var contents = hfs.AddFolder(app, "Contents");
        var exe = Enumerable.Range(0, 10_000).Select(i => (byte)i).ToArray();
        hfs.AddFile(hfs.AddFolder(contents, "MacOS"), "Fleetdeck", exe, 0x1ED);
        hfs.AddSymlink(hfs.Root, "Programmer", "/Applications");
        hfs.AddFile(hfs.Root, "Læs mig først.txt", Encoding.UTF8.GetBytes("hej"));
        // mange filer, så kataloget fylder flere blade og får et indeks-niveau
        var many = hfs.AddFolder(hfs.Root, "mange");
        for (var i = 0; i < 150; i++) hfs.AddFile(many, $"fil-{i:000}.txt", Encoding.UTF8.GetBytes($"indhold {i}"));
        Assert.Throws<ArgumentException>(() => hfs.AddFile(hfs.Root, "a:b", []));
        using var ms = new MemoryStream();
        hfs.Write(ms);
        var img = ms.ToArray();
        var vh = img.AsSpan(1024);
        Assert.Equal(0x4858, BinaryPrimitives.ReadUInt16BigEndian(vh));             // 'HX'
        Assert.Equal(HfsPlus.BlockSize, (int)BinaryPrimitives.ReadUInt32BigEndian(vh[40..]));
        var total = BinaryPrimitives.ReadUInt32BigEndian(vh[44..]);
        Assert.Equal(img.Length, (int)total * HfsPlus.BlockSize);
        Assert.Equal(153u, BinaryPrimitives.ReadUInt32BigEndian(vh[32..]));            // filer (inkl. symlink)
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32BigEndian(vh[36..]));              // mapper (uden roden)
        Assert.Equal(img.AsSpan(1024, 512).ToArray(), img.AsSpan(img.Length - 1024, 512).ToArray());   // kopien af headeren

        // katalog-B-træet: alle blade i rækkefølge, nøglerne sorteret
        var catStart = (int)BinaryPrimitives.ReadUInt32BigEndian(vh[(272 + 16)..]) * HfsPlus.BlockSize;
        var cat = img.AsSpan(catStart);
        var nodeSize = BinaryPrimitives.ReadUInt16BigEndian(cat[(14 + 18)..]);
        var depth = BinaryPrimitives.ReadUInt16BigEndian(cat[14..]);
        Assert.True(depth >= 2, "forventede indeks-niveau");
        var leafRecords = BinaryPrimitives.ReadUInt32BigEndian(cat[(14 + 6)..]);
        var node = (int)BinaryPrimitives.ReadUInt32BigEndian(cat[(14 + 10)..]);
        var keys = new List<byte[]>(); var names = new List<string>();
        byte[]? exeRecord = null;
        while (node != 0)
        {
            var nd = cat.Slice(node * nodeSize, nodeSize);
            Assert.Equal(0xFF, nd[8]);   // blad
            var count = BinaryPrimitives.ReadUInt16BigEndian(nd[10..]);
            for (var r = 0; r < count; r++)
            {
                var off = BinaryPrimitives.ReadUInt16BigEndian(nd[(nodeSize - 2 * (r + 1))..]);
                var klen = BinaryPrimitives.ReadUInt16BigEndian(nd[off..]);
                var key = nd.Slice(off, klen + 2).ToArray();
                keys.Add(key);
                var nlen = BinaryPrimitives.ReadUInt16BigEndian(key.AsSpan(6));
                var name = new string(Enumerable.Range(0, nlen).Select(i => (char)BinaryPrimitives.ReadUInt16BigEndian(key.AsSpan(8 + 2 * i))).ToArray());
                names.Add(name);
                if (name == "Fleetdeck" && BinaryPrimitives.ReadInt16BigEndian(nd[(off + klen + 2)..]) == 2) exeRecord = nd.Slice(off + klen + 2, 248).ToArray();
            }
            node = (int)BinaryPrimitives.ReadUInt32BigEndian(nd);
        }
        Assert.Equal((int)leafRecords, keys.Count);
        Assert.Equal(2 * (153 + 4 + 1), keys.Count);   // post + thread for hver fil og mappe, inkl. roden
        for (var i = 1; i < keys.Count; i++) Assert.True(HfsPlus_Compare(keys[i - 1], keys[i]) < 0, $"rækkefølge ved {names[i]}");
        Assert.Contains("Læs mig først.txt", names);
        // programmets data ligger i den extent, posten peger på, med rettigheder 755
        Assert.NotNull(exeRecord);
        Assert.Equal(0x81ED, BinaryPrimitives.ReadUInt16BigEndian(exeRecord.AsSpan(32 + 10)));
        var len = (int)BinaryPrimitives.ReadUInt64BigEndian(exeRecord.AsSpan(88));
        var start = (int)BinaryPrimitives.ReadUInt32BigEndian(exeRecord.AsSpan(88 + 16));
        Assert.Equal(exe, img.AsSpan(start * HfsPlus.BlockSize, len).ToArray());

        FsckIfAvailable(img);
    }

    private static int HfsPlus_Compare(byte[] a, byte[] b)
    {
        var pa = BinaryPrimitives.ReadUInt32BigEndian(a.AsSpan(2)); var pb = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(2));
        if (pa != pb) return pa.CompareTo(pb);
        return a.AsSpan(8).SequenceCompareTo(b.AsSpan(8)) is var c && c != 0 ? c : a.Length.CompareTo(b.Length);
    }

    /// <summary>Er fsck.hfsplus (hfsprogs) installeret, tjekkes billedet også med den.</summary>
    private static void FsckIfAvailable(byte[] img)
    {
        var fsck = new[] { "/usr/sbin/fsck.hfsplus", "/sbin/fsck.hfsplus", Environment.GetEnvironmentVariable("FSCK_HFSPLUS") ?? "" }.FirstOrDefault(File.Exists);
        if (fsck is null) return;
        var f = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(f, img);
            var p = Process.Start(new ProcessStartInfo(fsck, ["-n", "-f", f]) { RedirectStandardOutput = true, RedirectStandardError = true })!;
            var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.True(p.ExitCode == 0 && o.Contains("appears to be OK"), o);
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void DmgContainsTheAppTheApplicationsLinkAndTheReadme()
    {
        var dir = Directory.CreateTempSubdirectory("macpack-").FullName;
        try
        {
            var pub = Directory.CreateDirectory(Path.Combine(dir, "pub")).FullName;
            File.WriteAllBytes(Path.Combine(pub, "Fleetdeck"), FakeMachO());
            var app = Path.Combine(dir, "Fleetdeck.app");
            AppBundle.Build(new AppBundle.Options(pub, app, "3.16.0"));
            var dmg = Path.Combine(dir, "Fleetdeck.dmg");
            Dmg.Build(app, dmg, Icns.FromPngs(Icns.PngsFromIco(File.ReadAllBytes(Path.Combine(RepoRoot, "src/Fleetdeck.Agent.Windows/fleetdeck.ico")))));
            var raw = Udif.Read(File.ReadAllBytes(dmg));
            Assert.Equal(0x4858, BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(1024)));
            var text = Encoding.BigEndianUnicode.GetString(raw);
            foreach (var n in new[] { "Fleetdeck.app", "Programmer", "Info.plist", "CodeResources", ".VolumeIcon.icns" }) Assert.Contains(n, text);
            Assert.Contains("/Applications", Encoding.UTF8.GetString(raw));
            Assert.Contains("Åbn alligevel", Encoding.UTF8.GetString(raw));   // Gatekeeper-vejledningen
            Assert.False(File.Exists(dmg + ".hfs.tmp"));
            FsckIfAvailable(raw);
        }
        finally { Directory.Delete(dir, true); }
    }

    internal static string RepoRoot
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "Fleetdeck.slnx"))) d = d.Parent;
            return d?.FullName ?? throw new DirectoryNotFoundException("Fleetdeck.slnx");
        }
    }
}
