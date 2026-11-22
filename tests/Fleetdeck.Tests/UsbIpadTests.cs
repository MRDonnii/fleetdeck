using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Fleetdeck.Server.Endpoints;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;

namespace Fleetdeck.Tests;

public class UsbIpadCommandTests
{
    [Fact]
    public void Argv_runs_wrapper_isolated_without_color()
    {
        var a = Pmd3.Argv(Pmd3.UsbList());
        Assert.Equal(["-I", "-c", Pmd3.Wrapper, "--no-color", "usbmux", "list", "--usb"], a);
    }

    [Fact]
    public void Env_selects_device_and_disables_pretty_tracebacks()
    {
        var e = Pmd3.Env("00008103-001A2B3C4D5E6F70");
        Assert.Equal("00008103-001A2B3C4D5E6F70", e[Pmd3.UdidEnv]);
        Assert.Equal("1", e["NO_COLOR"]);
        Assert.Equal("1", e["_TYPER_STANDARD_TRACEBACK"]);   // ellers kan typer vise lokale variabler (fx en kode)
        Assert.False(Pmd3.Env(null).ContainsKey(Pmd3.UdidEnv));
    }

    [Fact]
    public void Commands_match_pymobiledevice3_cli()
    {
        Assert.Equal(["profile", "install", "/t/p.mobileconfig"], Pmd3.InstallProfile("/t/p.mobileconfig", null));
        Assert.Equal(["profile", "install", "--keybag", "/t/kb.pem", "/t/p.mobileconfig"], Pmd3.InstallProfile("/t/p.mobileconfig", "/t/kb.pem"));
        Assert.Equal(["profile", "remove", "dk.fleetdeck.dns"], Pmd3.RemoveProfile("dk.fleetdeck.dns"));
        Assert.Equal(["backup2", "backup", "--full", "/w/b"], Pmd3.Backup("/w/b"));
        Assert.Equal(["backup2", "restore", "--password", Pmd3.Secret1, "--source", "SRC", "/w/r"], Pmd3.Restore("/w/r", "SRC"));
        Assert.Equal(["backup2", "encryption", "on", Pmd3.Secret1], Pmd3.EncryptionOn());
        Assert.Equal(["profile", "supervise", "Familien", "--keybag", "/t/kb.pem"], Pmd3.Supervise("Familien", "/t/kb.pem"));
        Assert.Equal(["lockdown", "pair-supervised", "/t/kb.pem"], Pmd3.PairSupervised("/t/kb.pem"));
        Assert.Equal(["profile", "erase-device"], Pmd3.Erase());
        Assert.Equal(["diagnostics", "battery", "single"], Pmd3.Battery());
    }

    [Fact]
    public void Commands_with_secrets_only_carry_placeholders()
    {
        // ingen kommando må kunne bygges med selve koden – den sendes altid på stdin
        foreach (var cmd in new[] { Pmd3.EncryptionOn(), Pmd3.Restore("/w", "SRC") })
        {
            Assert.Contains(Pmd3.Secret1, cmd);
            Assert.All(Pmd3.Argv(cmd), a => Assert.DoesNotContain("hemmelig", a));
        }
    }

    [Fact]
    public void Mask_hides_secrets_in_output()
    {
        Assert.Equal("fejl: *** passer ikke (***)", Pmd3.Mask("fejl: Sup3rKode passer ikke (Sup3rKode)", ["Sup3rKode"]));
        Assert.Equal("abc", Pmd3.Mask("abc", null));
    }

    [Fact]
    public void Job_params_have_no_place_for_secrets()
    {
        var names = typeof(JobParams).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain(names, n => n.Contains("Password", StringComparison.OrdinalIgnoreCase) || n.Contains("Keybag", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(UsbIpadSecretKinds.Keybag, UsbIpadEndpoints.SecretFor(UsbIpadActions.InstallProfile));
        Assert.Equal(UsbIpadSecretKinds.Keybag, UsbIpadEndpoints.SecretFor(UsbIpadActions.Supervise));
        Assert.Equal(UsbIpadSecretKinds.BackupPassword, UsbIpadEndpoints.SecretFor(UsbIpadActions.Backup));
        Assert.Equal(UsbIpadSecretKinds.BackupPassword, UsbIpadEndpoints.SecretFor(UsbIpadActions.Restore));
        Assert.Null(UsbIpadEndpoints.SecretFor(UsbIpadActions.Info));
        Assert.Null(UsbIpadEndpoints.SecretFor(UsbIpadActions.Erase));
        Assert.Equal([UsbIpadSecretKinds.Keybag], UsbIpadEndpoints.SecretKinds(UsbIpadActions.RemoveProfile));
        Assert.Equal([UsbIpadSecretKinds.Keybag, UsbIpadSecretKinds.BackupPassword], UsbIpadEndpoints.SecretKinds(UsbIpadActions.Release));
    }

    [Fact]
    public void A_ticket_gives_each_secret_once()
    {
        var prm = new JobParams { Action = UsbIpadActions.Release };
        Assert.True(UsbIpadEndpoints.TicketAllows(prm, UsbIpadSecretKinds.Keybag));
        prm.TicketUsed = [UsbIpadSecretKinds.Keybag];
        Assert.False(UsbIpadEndpoints.TicketAllows(prm, UsbIpadSecretKinds.Keybag));
        Assert.True(UsbIpadEndpoints.TicketAllows(prm, UsbIpadSecretKinds.BackupPassword));
        Assert.False(UsbIpadEndpoints.TicketAllows(new JobParams { Action = UsbIpadActions.Backup }, UsbIpadSecretKinds.Keybag));
        Assert.False(UsbIpadEndpoints.TicketAllows(new JobParams { Action = UsbIpadActions.Info }, UsbIpadSecretKinds.BackupPassword));
    }

    [Fact]
    public void Experimental_actions_need_the_ipad_name()
    {
        Assert.True(UsbIpadEndpoints.Confirmed("Annas iPad", "  annas ipad "));
        Assert.False(UsbIpadEndpoints.Confirmed("Annas iPad", "Anna"));
        Assert.False(UsbIpadEndpoints.Confirmed("Annas iPad", null));
        Assert.False(UsbIpadEndpoints.Confirmed("Annas iPad", "  "));
        Assert.Equal([UsbIpadActions.Supervise, UsbIpadActions.Erase], UsbIpadActions.Experimental);
    }

    /// <summary>Indpakningen køres med en falsk pymobiledevice3: koden skal komme fra stdin og aldrig stå i processens argumenter.</summary>
    [Fact]
    public void Wrapper_reads_secret_from_stdin()
    {
        var py = new[] { "/usr/bin/python3", "/usr/local/bin/python3" }.FirstOrDefault(File.Exists);
        if (py is null || OperatingSystem.IsWindows()) return;   // ingen Python på denne maskine
        var dir = Directory.CreateTempSubdirectory("fd-pmd3-");
        try
        {
            var pkg = Directory.CreateDirectory(Path.Combine(dir.FullName, "pymobiledevice3"));
            File.WriteAllText(Path.Combine(pkg.FullName, "__init__.py"), "");
            File.WriteAllText(Path.Combine(pkg.FullName, "__main__.py"),
                "import sys, json\ndef main():\n    print(json.dumps({'argv': sys.argv, 'cmdline': open('/proc/self/cmdline','rb').read().decode().split('\\0')}))\n    return 0\n");
            var psi = new ProcessStartInfo(py) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, WorkingDirectory = dir.FullName };
            // uden -I, så den falske pakke i arbejdsmappen findes
            foreach (var a in Pmd3.Argv(Pmd3.Restore("/w", "SRC")).Skip(1)) psi.ArgumentList.Add(a);
            psi.Environment["PYTHONPATH"] = dir.FullName;
            using var p = Process.Start(psi)!;
            p.StandardInput.WriteLine("hemmelig-kode-123");
            p.StandardInput.Close();
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10_000);
            Assert.Equal(0, p.ExitCode);
            var root = JsonDocument.Parse(output).RootElement;
            var argv = root.GetProperty("argv").EnumerateArray().Select(x => x.GetString()).ToList();
            Assert.Equal(["pymobiledevice3", "--no-color", "backup2", "restore", "--password", "hemmelig-kode-123", "--source", "SRC", "/w"], argv);
            if (File.Exists("/proc/self/cmdline"))
                Assert.DoesNotContain(root.GetProperty("cmdline").EnumerateArray(), x => x.GetString()!.Contains("hemmelig-kode-123"));
        }
        finally { dir.Delete(true); }
    }
}

public class UsbIpadParseTests
{
    private static JsonElement J(string s) => Pmd3Parse.Json(s)!.Value;

    [Fact]
    public void Ecid_from_new_style_udid()
    {
        Assert.Equal("0x1A2B3C4D5E6F70", Pmd3Parse.EcidFromUdid("00008103-001A2B3C4D5E6F70"));
        Assert.Equal("", Pmd3Parse.EcidFromUdid("a1b2c3d4e5f60718293a4b5c6d7e8f9012345678"));   // gammel UDID (40 hex)
        Assert.Equal("", Pmd3Parse.EcidFromUdid(null));
    }

    [Fact]
    public void Usbmux_list_with_logging_around()
    {
        var text = """
            2026-11-17 20:00:01 INFO something
            [
              {"BuildVersion": "22B83", "ConnectionType": "USB", "DeviceClass": "iPad", "DeviceName": "Annas iPad", "Identifier": "00008103-001A2B3C4D5E6F70",
               "ProductType": "iPad13,18", "ProductVersion": "18.1", "UniqueDeviceID": "00008103-001A2B3C4D5E6F70"},
              {"ConnectionType": "Network", "DeviceName": "Wifi", "UniqueDeviceID": "00008103-0000000000000001"},
              {"ConnectionType": "USB", "DeviceName": "Gammel", "UniqueDeviceID": "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678"}
            ]
            """;
        var list = Pmd3Parse.UsbList(J(text));
        Assert.Equal(2, list.Count);
        var d = list[0];
        Assert.Equal("00008103-001A2B3C4D5E6F70", d.Udid);
        Assert.Equal("0x1A2B3C4D5E6F70", d.Ecid);
        Assert.Equal("Annas iPad", d.Name);
        Assert.Equal("iPad13,18", d.DeviceType);
        Assert.Equal("18.1", d.Firmware);
        Assert.Equal("", list[1].Ecid);
        Assert.Equal(2, Pmd3Parse.UsbList(J("""["00008103-001A2B3C4D5E6F70", "x"]""")).Count);   // --simple
    }

    [Fact]
    public void Lockdown_info_reads_ecid_number_and_serial()
    {
        var d = Pmd3Parse.Info(J("""
            {"UniqueDeviceID": "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678", "UniqueChipID": 7367947383189308, "SerialNumber": "DMPXK0AAAA00",
             "DeviceName": "Stue", "ProductType": "iPad7,11", "ProductVersion": "17.7", "BuildVersion": "21H16", "DeviceClass": "iPad",
             "ActivationState": "Activated", "PasswordProtected": true}
            """));
        Assert.Equal("0x1A2D1BEE7FAF3C", d.Ecid);
        Assert.Equal("DMPXK0AAAA00", d.Serial);
        Assert.Equal("17.7", d.Firmware);
        Assert.Equal("Activated", d.ActivationState);
        Assert.True(d.Passcode);
        Assert.True(d.Paired);
    }

    [Fact]
    public void Battery_cloud_profiles_and_encryption()
    {
        Assert.Equal((83, true), Pmd3Parse.Battery(J("""{"CurrentCapacity": 83, "MaxCapacity": 100, "IsCharging": true}""")));
        Assert.Equal((50, false), Pmd3Parse.Battery(J("""{"CurrentCapacity": 4000, "MaxCapacity": 8000, "IsCharging": false}""")));
        Assert.Equal((61, null), Pmd3Parse.Battery(J("""{"BatteryCurrentCapacity": 61}""")));
        Assert.Equal((true, "Familien"), Pmd3Parse.Cloud(J("""{"IsSupervised": true, "OrganizationName": "Familien", "CloudConfigurationUIComplete": true}""")));
        Assert.Equal((false, null), Pmd3Parse.Cloud(J("""{"IsSupervised": false}""")));

        var profiles = Pmd3Parse.Profiles(J("""
            {"OrderedIdentifiers": ["dk.fleetdeck.locked.3", "com.other"],
             "ProfileMetadata": {"dk.fleetdeck.locked.3": {"PayloadDisplayName": "Fleetdeck (låst)", "PayloadVersion": 4, "PayloadRemovalDisallowed": true}},
             "Status": "Acknowledged"}
            """));
        Assert.Equal(2, profiles.Count);
        Assert.Equal("Fleetdeck (låst)", profiles[0].Name);
        Assert.Equal(4, profiles[0].Version);
        Assert.Equal("com.other", profiles[1].Identifier);

        Assert.True(Pmd3Parse.EncryptionOn("INFO bla\non\n"));
        Assert.False(Pmd3Parse.EncryptionOn("off"));
        Assert.Null(Pmd3Parse.EncryptionOn("ERROR noget"));
        Assert.Equal(42, Pmd3Parse.Percent("backup:  42%|████      | 42/100"));
        Assert.Null(Pmd3Parse.Percent("INFO done"));
    }

    [Fact]
    public void Watcher_key_ignores_small_battery_changes()
    {
        UsbIpadReport R(int bat) => new() { Ready = true, Devices = [new IosDeviceInfo { Udid = "U", Ecid = "0x1", Battery = bat, Paired = true }] };
        Assert.Equal(UsbIpadWatcher.Key(R(81)), UsbIpadWatcher.Key(R(84)));
        Assert.NotEqual(UsbIpadWatcher.Key(R(81)), UsbIpadWatcher.Key(R(91)));
    }
}

public class UsbIpadServerTests
{
    [Fact]
    public void Store_tracks_fresh_reports_and_changes()
    {
        var s = new UsbIpadStore();
        var t = new DateTime(2026, 11, 17, 20, 0, 0, DateTimeKind.Utc);
        var r = new UsbIpadReport { Ready = true, Devices = [new IosDeviceInfo { Udid = "00008103-001A2B3C4D5E6F70", Name = "Annas iPad" }, new IosDeviceInfo { Name = "uden udid" }] };
        Assert.True(s.Put(7, r, t));
        Assert.Single(s.Get(7, t)!.Value.Report.Devices);
        Assert.Equal("0x1A2B3C4D5E6F70", s.Get(7, t)!.Value.Report.Devices[0].Ecid);   // fra UDID'en
        Assert.False(s.Put(7, new UsbIpadReport { Ready = true, Devices = [new IosDeviceInfo { Udid = "00008103-001A2B3C4D5E6F70", Name = "Annas iPad" }] }, t.AddSeconds(60)));
        Assert.Null(s.Get(7, t.AddMinutes(10)));   // forældet
        Assert.True(s.LinkDue(7, false, t));
        Assert.False(s.LinkDue(7, false, t.AddMinutes(1)));
        Assert.True(s.LinkDue(7, true, t.AddMinutes(1)));
    }

    [Fact]
    public void Keybag_is_pem_with_key_and_certificate()
    {
        var (c, k) = SupervisionIdentity.Create("Familien", DateTimeOffset.UtcNow);
        var pem = SupervisionIdentity.ToPem(c, k);
        Assert.Contains("-----BEGIN RSA PRIVATE KEY-----", pem);   // samme format som pymobiledevice3 (TraditionalOpenSSL)
        Assert.Contains("-----BEGIN CERTIFICATE-----", pem);
        Assert.Null(UsbIpadKeybag.Validate(pem));
        using var cert = X509Certificate2.CreateFromPem(pem);
        Assert.Contains("O=Familien", cert.Subject);
        Assert.NotNull(UsbIpadKeybag.Validate(pem[..pem.IndexOf("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal)]));
        Assert.NotNull(UsbIpadKeybag.Validate(""));
    }

    [Fact]
    public void Backup_is_split_uploaded_and_restored()
    {
        var root = Directory.CreateTempSubdirectory("fd-ipadbak-");
        try
        {
            var src = Directory.CreateDirectory(Path.Combine(root.FullName, "src", "00008103-001A2B3C4D5E6F70"));
            var rnd = new Random(1);
            for (var i = 0; i < 5; i++)
            {
                var data = new byte[40_000]; rnd.NextBytes(data);   // tilfældige data pakkes ikke
                File.WriteAllBytes(Path.Combine(src.FullName, $"f{i}"), data);
            }
            File.WriteAllText(Path.Combine(src.FullName, "Manifest.plist"), "<plist/>");
            var parts = UsbIpadJobs.Pack(src.FullName, Path.Combine(root.FullName, "up"), "ipad-20261117-200000", 64 * 1024);
            Assert.True(parts.Count >= 3);
            Assert.All(parts, p => Assert.True(new FileInfo(p).Length <= 64 * 1024));

            // serveren: delene lægges i iPad'ens mappe, og beskrivelsen skrives, når alle er der
            var dir = Path.Combine(root.FullName, "srv");
            Directory.CreateDirectory(dir);
            for (var i = 0; i < parts.Count; i++) File.Copy(parts[i], UsbIpadBackups.Part(dir, "ipad-20261117-200000", i + 1));
            Assert.Null(UsbIpadBackups.Finish(dir, "ipad-20261117-200000", parts.Count + 1, "U", "ARBEJDS-PC", "Annas iPad"));
            var m = UsbIpadBackups.Finish(dir, "ipad-20261117-200000", parts.Count, "00008103-001A2B3C4D5E6F70", "ARBEJDS-PC", "Annas iPad");
            Assert.NotNull(m);
            Assert.Equal(parts.Sum(p => new FileInfo(p).Length), m.Bytes);

            var dest = Path.Combine(root.FullName, "restore");
            UsbIpadJobs.Unpack(Enumerable.Range(1, parts.Count).Select(i => UsbIpadBackups.Part(dir, "ipad-20261117-200000", i)).ToList(), dest);
            var back = Path.Combine(dest, "00008103-001A2B3C4D5E6F70");
            Assert.Equal(File.ReadAllBytes(Path.Combine(src.FullName, "f3")), File.ReadAllBytes(Path.Combine(back, "f3")));
            Assert.Equal("<plist/>", File.ReadAllText(Path.Combine(back, "Manifest.plist")));
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void Backup_names_and_pruning()
    {
        Assert.Equal("ipad-20261117-200000", UsbIpadBackups.SafeSet("ipad-20261117-200000"));
        Assert.Null(UsbIpadBackups.SafeSet("../ipad-20261117-200000"));
        Assert.Null(UsbIpadBackups.SafeSet("ipad-2026"));
        var dir = Directory.CreateTempSubdirectory("fd-ipadprune-").FullName;
        try
        {
            foreach (var n in new[] { "ipad-20261101-000000", "ipad-20261102-000000", "ipad-20261103-000000", "ipad-20261104-000000" })
            {
                File.WriteAllText(UsbIpadBackups.Part(dir, n, 1), "x");
                Assert.NotNull(UsbIpadBackups.Finish(dir, n, 1, "U", null, null));
            }
            UsbIpadBackups.Prune(dir, 3);
            Assert.False(File.Exists(Path.Combine(dir, "ipad-20261101-000000.json")));
            Assert.False(File.Exists(UsbIpadBackups.Part(dir, "ipad-20261101-000000", 1)));
            Assert.True(File.Exists(UsbIpadBackups.Part(dir, "ipad-20261104-000000", 1)));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Extension_is_offered_on_all_platforms()
    {
        var d = Extensions.BuiltIn.Single(x => x.Id == Extensions.UsbIpadId);
        foreach (var plat in new[] { Platforms.Windows, Platforms.Linux, Platforms.Mac })
        {
            var how = d.For(plat)!;
            Assert.Equal("script", how.Type);
            Assert.Equal(Extensions.UsbIpadDetect, how.Detect);
            Assert.Contains($"pymobiledevice3=={UsbIpadPaths.Pmd3Version}", how.Script!.Replace("$V", UsbIpadPaths.Pmd3Version));
            Assert.DoesNotContain("{{", how.Script);
        }
        Assert.Contains("Apple.AppleMobileDeviceSupport", d.Windows!.Script);
        Assert.Contains("9NP83LWLPZ9K", d.Windows.Script);
        Assert.Contains("usbmuxd", d.Linux!.Script);
        Assert.Equal("/opt/fleetdeck/ipad-usb/tools/pymobiledevice3/bin/python", UsbIpadPaths.Python("/opt/fleetdeck/ipad-usb", false));
        Assert.Equal(@"C:\ProgramData\Fleetdeck\ipad-usb\tools\pymobiledevice3\Scripts\python.exe", UsbIpadPaths.Python(@"C:\ProgramData\Fleetdeck\ipad-usb", true));
    }
}
