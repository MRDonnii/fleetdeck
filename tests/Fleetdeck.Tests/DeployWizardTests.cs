using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services;
using Xunit;

namespace Fleetdeck.Tests;

/// <summary>Udrulning: flere styresystemer, svarfilerne og guiden "Ny installation" (tomme felter spørges på maskinen).</summary>
public class DeployWizardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fd-deploy-" + Guid.NewGuid().ToString("N"));
    private AppDb Db() => new(new DbContextOptionsBuilder<AppDb>().UseSqlite($"Data Source={Path.Combine(_dir, "fleetdeck.db")}").Options);
    public DeployWizardTests() { Directory.CreateDirectory(_dir); using var db = Db(); db.Database.Migrate(); }
    public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private sealed class NoHttp : IHttpClientFactory { public HttpClient CreateClient(string name) => throw new InvalidOperationException(); }

    private Paths TempPaths => new(Path.Combine(_dir, "data"), Path.Combine(_dir, "winsetup"));

    private async Task<DeployService> Service(AppDb db, bool untested = false)
    {
        var s = new SettingsService(db);
        if (untested) await s.Set(DeployService.KUntested, "1");
        await s.Set(InstallLocale.KLanguage, "da-DK"); await s.Set(InstallLocale.KKeyboard, "dk"); await s.Set(InstallLocale.KTimeZone, "Europe/Copenhagen");
        await s.Set("server_url", "http://192.0.2.10:8585");
        await s.Set("enroll_key", "KEY");
        await s.Set(DeployInstallKey.SecretSetting, TestSecret);
        return new DeployService(db, TempPaths, s, new OsLibrary(TempPaths, new NoHttp(), NullLogger<OsLibrary>.Instance), NullLogger<DeployService>.Instance);
    }

    private const string TestSecret = "test-hemmelighed-til-udrulningskoder-0123456789";

    private static Deployment LegacyUbuntu() => new()
    {
        Mac = "AA-BB-CC-DD-EE-FF", Os = DeployOs.Ubuntu, Series = "24.04", Variant = "desktop", PcName = "minpc", UserName = "anna",
        FullName = "Anna Eksempel", PasswordHash = "$6$salt$hash", Packages = "htop git",
    };

    // Den Ubuntu-svarfil, Fleetdeck lavede før guiden (afprøvet i en VM). Fuldt udfyldte felter skal give præcis den samme.
    private const string LegacyUbuntuUserData = """
        #cloud-config
        autoinstall:
          version: 1
          interactive-sections:
            - storage
          locale: da_DK.UTF-8
          keyboard:
            layout: dk
          timezone: Europe/Copenhagen
          identity:
            hostname: minpc
            realname: "Anna Eksempel"
            username: anna
            password: "$6$salt$hash"
          ssh:
            install-server: true
            allow-pw: true
          storage:
            layout:
              name: lvm
          packages:
            - curl
            - ubuntu-desktop-minimal
            - htop
            - git
          late-commands:
            - rm -f /target/etc/netplan/50-cloud-init.yaml
            - "printf 'network:\n  version: 2\n  renderer: NetworkManager\n' > /target/etc/netplan/01-network-manager-all.yaml"
            - chmod 600 /target/etc/netplan/01-network-manager-all.yaml
            - 'curtin in-target -- env WS_NAME=minpc WS_PROVISION=1 sh -c "curl -fsSL ''http://192.0.2.10:8585/install.sh?key=KEY'' | bash" || true'
            - "curl -fsS -m 20 -X POST http://192.0.2.10:8585/pxe/ubuntu/aa-bb-cc-dd-ee-ff/done || true"

        """;

    [Fact]
    public async Task Ubuntu_LegacyDeploymentGivesTheSameAnswerFileAsBefore()
    {
        using var db = Db();
        var deploy = await Service(db);
        var d = LegacyUbuntu();
        // samme svarfil som før – bortset fra tilmeldingen: en engangskode for udrulningen i stedet for den globale nøgle
        Assert.Equal(LegacyUbuntuUserData.Replace("\r\n", "\n").Replace("key=KEY", "key=" + DeployInstallKey.Code(TestSecret, d)), await deploy.UserData(d));
    }

    [Fact]
    public async Task Pxe_answer_file_carries_a_one_time_code_never_the_global_key()
    {
        using var db = Db();
        var deploy = await Service(db);
        db.Machines.Add(new Machine { Id = 7, Name = "MINPC" });
        await db.SaveChangesAsync();
        var d = LegacyUbuntu();
        d.Id = 42; d.MachineId = 7;
        var first = await deploy.UserData(d);
        var again = await deploy.UserData(d);                       // installeren må hente svarfilen igen
        Assert.Equal(first, again);
        Assert.DoesNotContain("key=KEY", first);
        var code = DeployInstallKey.Code(TestSecret, d);
        Assert.Contains("key=" + code, first);
        var row = Assert.Single(db.EnrollCodes.AsNoTracking());      // kun registreret én gang, kun som hash
        Assert.Equal(Crypto.Sha256(code), row.CodeHash);
        Assert.Equal(7, row.ReplaceMachineId);                       // gælder kun netop den maskine
        Assert.Null(row.UsedAt);
        Assert.True(row.Expires <= DateTime.UtcNow + DeployInstallKey.Valid + TimeSpan.FromMinutes(1));
        var check = await new EnrollmentService(db, new SettingsService(db), new AuditService(db)).CheckKey(code);
        Assert.Equal(EnrollmentService.KeyKind.Code, check.Kind);
        // en anden udrulning får en anden kode
        var other = LegacyUbuntu(); other.Id = 43; other.Created = d.Created;
        Assert.NotEqual(code, DeployInstallKey.Code(TestSecret, other));
    }

    // ---------------------------------------------------------------- fælles hjælpere
    private const string Hash = "$6$eksempelsalt$abcdefghijklmnopqrstuvwxyz0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwx";
    private const string Key = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIEksempelNoegleTilTestBrugIkkeRigtig anna@minpc";

    private static InstallSpec Full(string variant = "server") => new()
    {
        Hostname = "minpc", UserName = "anna", FullName = "Anna Eksempel", PasswordHash = Hash,
        Language = InstallLocale.FindLanguage("da-DK"), Keyboard = InstallLocale.FindKeyboard("dk"), TimeZone = "Europe/Copenhagen",
        AutoDisk = true, Variant = variant, Packages = ["htop"],
        InstallUrl = "http://192.0.2.10:8585/install.sh?key=KEY", DoneUrl = "http://192.0.2.10:8585/pxe/linux/aa-bb-cc-dd-ee-ff/done",
    };

    private static InstallSpec Empty(string variant = "server") => new()
    {
        Variant = variant, InstallUrl = "http://192.0.2.10:8585/install.sh?key=KEY", DoneUrl = "http://192.0.2.10:8585/pxe/linux/aa-bb-cc-dd-ee-ff/done",
    };

    private static void NoSecrets(string text, string password = "hemmelig-kode")
    {
        Assert.DoesNotContain(password, text);
        Assert.DoesNotContain("enroll", text);
    }

    // ---------------------------------------------------------------- kataloget
    [Fact]
    public void Catalog_OnlyTestedSystemsCanBeChosen()
    {
        Assert.Equal(OsCatalog.All.Length, OsCatalog.All.Select(e => e.Id).Distinct().Count());
        var ready = OsCatalog.All.Where(e => e.Availability == OsAvailability.Ready).Select(e => e.Id).Order().ToArray();
        Assert.Equal(["ubuntu-24.04", "ubuntu-26.04", "windows-11"], ready);   // afprøvet til ende i en VM
        Assert.All(OsCatalog.All.Where(e => e.Availability != OsAvailability.Ready), e => Assert.False(string.IsNullOrWhiteSpace(e.Note)));
        Assert.Contains("oktober 2025", OsCatalog.Get("windows-10")!.Warning);
        Assert.Equal(OsAvailability.Omitted, OsCatalog.Get("linux-mint")!.Availability);
        Assert.All(OsCatalog.All.Where(e => e.Netboot is not null), e => Assert.StartsWith("https://", e.Netboot!.TreeUrl));
        Assert.All(OsCatalog.All.Where(e => e.IsWindows), e => Assert.False(string.IsNullOrEmpty(e.WinDir)));
        foreach (var id in new[] { "windows-10", "windows-server-2025", "debian-13", "debian-12", "fedora", "rocky-9", "rocky-10", "alma-9", "alma-10", "proxmox-ve" })
            Assert.NotNull(OsCatalog.Get(id));
    }

    [Fact]
    public void Catalog_SaysWhatHappensWithBlankFields()
    {
        var pve = OsCatalog.Get("proxmox-ve")!;
        Assert.Equal(BlankRule.Required, pve.Blank(DeployField.Name).Kind);
        Assert.Equal(BlankRule.Na, pve.Blank(DeployField.User).Kind);
        var win = OsCatalog.Get("windows-11")!;
        Assert.Equal(BlankRule.Auto, win.Blank(DeployField.Name).Kind);
        Assert.Equal(BlankRule.Ask, win.Blank(DeployField.User).Kind);
        Assert.Equal(BlankRule.Na, win.Blank(DeployField.Password).Kind);
        Assert.Equal(BlankRule.Auto, OsCatalog.Get("ubuntu-24.04")!.Blank(DeployField.TimeZone).Kind);
        Assert.All(DeployField.All, f => Assert.Equal(BlankRule.Ask, OsCatalog.Get("debian-13")!.Blank(f).Kind));
        Assert.All(DeployField.All, f => Assert.Equal(BlankRule.Ask, OsCatalog.Get("rocky-10")!.Blank(f).Kind));
    }

    [Fact]
    public void Checksums_AreReadFromTreeinfoAndSha256Sums()
    {
        const string treeinfo = "[general]\nfamily = Rocky Linux\n\n[checksums]\nimages/install.img = sha256:1111111111111111111111111111111111111111111111111111111111111111\n" +
                                "images/pxeboot/vmlinuz = sha256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\n\n[images-x86_64]\nkernel = images/pxeboot/vmlinuz\n";
        Assert.Equal(new string('a', 64), OsCatalog.TreeinfoSha256(treeinfo, "images/pxeboot/vmlinuz"));
        Assert.Null(OsCatalog.TreeinfoSha256(treeinfo, "images/pxeboot/initrd.img"));
        Assert.Null(OsCatalog.TreeinfoSha256("[images-x86_64]\nimages/pxeboot/vmlinuz = sha256:" + new string('b', 64), "images/pxeboot/vmlinuz"));

        var sums = $"{new string('c', 64)}  ./netboot/debian-installer/amd64/linux\n{new string('d', 64)}  ./netboot/debian-installer/amd64/initrd.gz\n";
        Assert.Equal(new string('d', 64), OsCatalog.SumsSha256(sums, "netboot/debian-installer/amd64/initrd.gz"));
        Assert.Equal(new string('e', 64), OsCatalog.SumsSha256($"{new string('e', 64)} *ubuntu-24.04.3-live-server-amd64.iso\n", "ubuntu-24.04.3-live-server-amd64.iso"));
        Assert.Null(OsCatalog.SumsSha256("ikke-en-hash  ./linux", "linux"));

        Assert.Equal(44, OsCatalog.LatestFedora("<a href=\"42/\">42/</a> <a href=\"44/\">44/</a> <a href=\"43/\">43/</a> <a href=\"test/\">test/</a>"));
        Assert.Null(OsCatalog.LatestFedora("<html>tom</html>"));
    }

    // ---------------------------------------------------------------- Ubuntu
    [Fact]
    public void Ubuntu_BlankFieldsAreAskedOnTheMachine()
    {
        var y = AnswerFiles.Autoinstall(Empty("desktop"));
        Assert.Contains("  interactive-sections:\n    - locale\n    - keyboard\n    - identity\n    - storage\n", y);
        Assert.Contains("  timezone: geoip\n", y);
        Assert.DoesNotContain("password:", y);
        Assert.DoesNotContain("  identity:", y);
        Assert.Contains("WS_NAME=\\$(cat /etc/hostname)", y);   // navnet valgt på maskinen læses i den nye installation
        Assert.Contains("ubuntu-desktop-minimal", y);

        var full = AnswerFiles.Autoinstall(Full() with { SshKey = Key, Admin = false, ProfileId = 4 });
        Assert.DoesNotContain("interactive-sections", full);
        Assert.Contains($"    authorized-keys:\n      - \"{Key}\"\n", full);
        Assert.Contains("deluser anna sudo", full);
        Assert.Contains("env WS_NAME=minpc WS_PROFILE=4 WS_PROVISION=1", full);
        Assert.Contains($"password: \"{Hash}\"", full);
    }

    [Fact]
    public void Ubuntu_PartlyFilledIdentityIsShownAsSuggestion()
    {
        var y = AnswerFiles.Autoinstall(Full() with { PasswordHash = "" });
        Assert.Contains("    - identity\n", y);
        Assert.Contains("  identity:\n    hostname: minpc\n    realname: \"Anna Eksempel\"\n    username: anna\n", y);
        Assert.DoesNotContain("password:", y);
        Assert.Contains("    - storage\n", AnswerFiles.Autoinstall(Full() with { Encrypt = true }));   // krypteringsnøglen skrives på maskinen
    }

    // ---------------------------------------------------------------- Debian (preseed)
    [Fact]
    public void Debian_PreseedFullAndBlank()
    {
        var p = AnswerFiles.Preseed(Full("desktop"));
        Assert.Contains("d-i debian-installer/locale string da_DK.UTF-8\n", p);
        Assert.Contains("d-i keyboard-configuration/xkb-keymap select dk\n", p);
        Assert.Contains("d-i netcfg/get_hostname string minpc\n", p);
        Assert.Contains("d-i passwd/username string anna\n", p);
        Assert.Contains($"d-i passwd/user-password-crypted password {Hash}\n", p);
        Assert.Contains("d-i time/zone string Europe/Copenhagen\n", p);
        Assert.Contains("d-i partman-auto/method string lvm\n", p);
        Assert.Contains("tasksel tasksel/first multiselect standard, ssh-server, gnome-desktop\n", p);
        Assert.Contains("d-i pkgsel/include string curl sudo openssh-server htop\n", p);
        Assert.Contains("in-target curl -fsS -m 20 -X POST http://192.0.2.10:8585/pxe/linux/aa-bb-cc-dd-ee-ff/done || true", p);
        Assert.False(AnswerFiles.PreseedAsks(Full()));
        Assert.Single(p.Split('\n'), l => l.StartsWith("d-i preseed/late_command"));

        var e = AnswerFiles.Preseed(Empty());
        foreach (var key in new[] { "debian-installer/locale", "xkb-keymap", "netcfg/get_hostname", "passwd/username", "passwd/user-password", "time/zone", "partman-auto/method" })
            Assert.DoesNotContain(key, e);
        Assert.Contains("export WS_NAME=$(cat /etc/hostname)", e);
        Assert.DoesNotContain("gnome-desktop", e);
        Assert.True(AnswerFiles.PreseedAsks(Empty()));
    }

    [Fact]
    public void Debian_EncryptionAsksForTheKeyAndKeysAreInstalled()
    {
        var p = AnswerFiles.Preseed(Full() with { Encrypt = true, SshKey = Key, Admin = false });
        Assert.Contains("d-i partman-auto/method string crypto\n", p);
        Assert.DoesNotContain("partman-crypto/passphrase", p);
        Assert.Contains($"echo '{Key}' > /target/home/anna/.ssh/authorized_keys", p);
        Assert.Contains("in-target deluser anna sudo", p);
        var us = AnswerFiles.Preseed(Full() with { Keyboard = InstallLocale.FindKeyboard("us-intl") });
        Assert.Contains("d-i keyboard-configuration/variantcode string intl\n", us);
    }

    // ---------------------------------------------------------------- Fedora, Rocky, Alma (kickstart)
    [Fact]
    public void Kickstart_FullIsTextModeAndBlankIsAsked()
    {
        var k = AnswerFiles.Kickstart(Full() with { SshKey = Key }, "rocky", "https://example.org/rocky/10/BaseOS/x86_64/os/", "https://example.org/rocky/10/AppStream/x86_64/os/");
        Assert.StartsWith("# Fleetdeck", k);
        Assert.Contains("\ntext\n", k);
        Assert.Contains("repo --name=AppStream --baseurl=\"https://example.org/rocky/10/AppStream/x86_64/os/\"\n", k);
        Assert.Contains("lang da_DK.UTF-8\n", k);
        Assert.Contains("keyboard --xlayouts='dk'\n", k);
        Assert.Contains("timezone Europe/Copenhagen --utc\n", k);
        Assert.Contains("--hostname=minpc", k);
        Assert.Contains($"user --name=anna --gecos=\"Anna Eksempel\" --password={Hash} --iscrypted --groups=wheel\n", k);
        Assert.Contains("rootpw --lock\n", k);
        Assert.Contains($"sshkey --username=anna \"{Key}\"\n", k);
        Assert.Contains("autopart --type=lvm\n", k);
        Assert.Contains("@^server-product-environment\n", k);
        Assert.Contains("%post\n", k);

        var e = AnswerFiles.Kickstart(Empty(), "alma", "https://example.org/alma/");
        Assert.Contains("\ngraphical\n", e);
        foreach (var key in new[] { "\nlang ", "\nkeyboard ", "\ntimezone ", "--hostname", "\nuser ", "\nrootpw", "autopart", "clearpart" })
            Assert.DoesNotContain(key, e);
        Assert.DoesNotContain("AppStream", e);
    }

    [Fact]
    public void Kickstart_FedoraWorkstationAndEncryption()
    {
        var k = AnswerFiles.Kickstart(Full("desktop") with { Encrypt = true, Admin = false, Keyboard = InstallLocale.FindKeyboard("us-intl") }, "fedora", "https://example.org/fedora/");
        Assert.Contains("@^workstation-product-environment\n", k);
        Assert.Contains("autopart --type=lvm --encrypted\n", k);
        Assert.DoesNotContain("--passphrase", k);
        Assert.Contains("\ngraphical\n", k);   // nøglen skrives på maskinen
        Assert.DoesNotContain("--groups=wheel", k);
        Assert.Contains("keyboard --xlayouts='us (intl)'\n", k);
        Assert.Contains("@^server-product-environment\n", AnswerFiles.Kickstart(Full("server"), "fedora", "https://example.org/fedora/"));
    }

    // ---------------------------------------------------------------- Proxmox (answer.toml)
    [Fact]
    public void Proxmox_AnswerNeedsEverythingAndHashesOnly()
    {
        var a = AnswerFiles.ProxmoxAnswer(Full() with { Hostname = "pve1", SshKey = Key, Keyboard = InstallLocale.FindKeyboard("us") }, "vda", "http://192.0.2.10:8585/pxe/linux/x/first-boot");
        Assert.Contains("[global]\nkeyboard = \"en-us\"\ncountry = \"dk\"\nfqdn = \"pve1.localdomain\"\n", a);
        Assert.Contains($"root-password-hashed = \"{Hash}\"\n", a);
        Assert.Contains($"root-ssh-keys = [\"{Key}\"]\n", a);
        Assert.Contains("disk-list = [\"vda\"]\n", a);
        Assert.Contains("[first-boot]\nsource = \"from-url\"", a);
        Assert.DoesNotContain("root-password =", a);
        Assert.Throws<ArgumentException>(() => AnswerFiles.ProxmoxAnswer(Empty()));
        Assert.Equal("de-ch", AnswerFiles.ProxmoxKeyboard("ch"));
        Assert.Equal("dk", AnswerFiles.ProxmoxKeyboard("dk"));
    }

    // ---------------------------------------------------------------- Windows (autounattend.xml)
    private static InstallSpec Win() => new()
    {
        Hostname = "STUE-PC", UserName = "Anna", Mode = "profile", ProfileId = 3, DeployId = 7,
        Language = InstallLocale.FindLanguage("da-DK"), Keyboard = InstallLocale.FindKeyboard("dk"), TimeZone = "Europe/Copenhagen",
    };

    private static XNamespace U => "urn:schemas-microsoft-com:unattend";

    [Fact]
    public void Windows_FullSpecIsTheSameAsTheTestedTemplate()
    {
        // Det, menu.cmd laver af skabelonen (afprøvet), med de samme værdier
        var template = File.ReadAllText(Path.Combine(MacPackTests.RepoRoot, "provision/usb/autounattend-pxe.xml"))
            .Replace("__PCNAME__", "STUE-PC").Replace("__USER__", "Anna").Replace("__MODE__", "profile").Replace("__PROFILE__", "3").Replace("__DEPLOY__", "7")
            .Replace("__LANG__", "da-DK").Replace("__INPUTLOCALE__", "0406:00000406").Replace("__TIMEZONE__", "Romance Standard Time").Replace("__DISKCONFIG__", "");
        var mine = AnswerFiles.Autounattend(Win(), OsCatalog.Get("windows-11")!);
        Assert.True(XNode.DeepEquals(XDocument.Parse(template).Root, XDocument.Parse(mine).Root), mine);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", mine);
    }

    [Fact]
    public void Windows_BlankFieldsAreLeftOut()
    {
        var x = XDocument.Parse(AnswerFiles.Autounattend(new InstallSpec { Mode = "clean" }, OsCatalog.Get("windows-10")!));
        Assert.Empty(x.Descendants(U + "ComputerName"));
        Assert.Empty(x.Descendants(U + "TimeZone"));
        Assert.Empty(x.Descendants(U + "AutoLogon"));
        Assert.Empty(x.Descendants(U + "SetupUILanguage"));
        Assert.Empty(x.Descendants(U + "InputLocale"));
        Assert.Empty(x.Descendants(U + "component").Where(c => ((string?)c.Attribute("name") ?? "").StartsWith("Microsoft-Windows-International-Core")));
        Assert.Empty(x.Descendants(U + "DiskConfiguration"));
        Assert.Equal("false", x.Descendants(U + "HideLocalAccountScreen").Single().Value);   // Windows spørger efter en bruger
        Assert.DoesNotContain(x.Descendants(U + "Path"), p => p.Value.Contains("New-LocalUser"));
        Assert.Contains(x.Descendants(U + "Path"), p => p.Value.Contains("'set WS_MODE=clean'"));
    }

    [Fact]
    public void Windows_ServerDiskAndBitLocker()
    {
        var srv = XDocument.Parse(AnswerFiles.Autounattend(Win() with { AutoDisk = true }, OsCatalog.Get("windows-server-2025")!, 2));
        Assert.Equal("2", srv.Descendants(U + "MetaData").Single().Element(U + "Value")!.Value);
        Assert.Equal("3", srv.Descendants(U + "InstallTo").Single().Element(U + "PartitionID")!.Value);
        Assert.Equal("true", srv.Descendants(U + "WillWipeDisk").Single().Value);
        Assert.Empty(srv.Descendants(U + "ProductKey"));             // Server: nøglen spørges, hvis mediet kræver den
        Assert.Empty(srv.Descendants(U + "HideOnlineAccountScreens"));
        Assert.DoesNotContain(srv.Descendants(U + "Path"), p => p.Value.Contains("BypassNRO"));
        Assert.Equal("false", srv.Descendants(U + "HideLocalAccountScreen").Single().Value);   // Administrator-adgangskoden vælges på maskinen

        var enc = AnswerFiles.Autounattend(Win() with { Encrypt = true }, OsCatalog.Get("windows-11")!);
        Assert.DoesNotContain("PreventDeviceEncryption", enc);
        Assert.Contains("Enable-BitLocker", enc);
        Assert.Contains("PreventDeviceEncryption", AnswerFiles.Autounattend(Win(), OsCatalog.Get("windows-11")!));
    }

    // ---------------------------------------------------------------- guiden (DeployService.Create)
    private static DeployRequest Req(string os, string? name = "minpc", string? user = "anna", string? pw = "hemmelig-kode") =>
        new("52-54-00-12-34-56", os, null, null, name, user, "Anna Eksempel", pw, "server", "htop", null, null, AutoDisk: true);

    private void FakeDownloaded(string id, string repo = "https://example.org/tree/")
    {
        var pxe = TempPaths.Pxe;
        Directory.CreateDirectory(Path.Combine(pxe, "tftp", "linux", id));
        Directory.CreateDirectory(Path.Combine(pxe, "tftp", "ubuntu"));
        Directory.CreateDirectory(Path.Combine(pxe, "os", id));
        foreach (var f in new[] { "linux", "initrd" }) File.WriteAllText(Path.Combine(pxe, "tftp", "linux", id, f), "x");
        foreach (var f in new[] { "bootx64.efi", "grubx64.efi" }) File.WriteAllText(Path.Combine(pxe, "tftp", "ubuntu", f), "x");
        File.WriteAllText(Path.Combine(pxe, "os", id, "info.json"), Json.Ser(new OsInfo { Version = "13", Repo = repo }));
    }

    [Fact]
    public async Task Create_UntestedSystemsNeedTheTestSwitch()
    {
        using var db = Db();
        var (d, err) = await (await Service(db)).Create(Req("debian-13"), "admin");
        Assert.Null(d);
        Assert.Contains("kommer senere", err);
        (d, err) = await (await Service(db, untested: true)).Create(Req("linux-mint"), "admin");
        Assert.Null(d);
        Assert.Contains("Udeladt", err);
        (d, err) = await (await Service(db, untested: true)).Create(Req("debian-13"), "admin");
        Assert.Null(d);
        Assert.Contains("ikke hentet", err);
    }

    [Fact]
    public async Task Create_DebianWithBlankFieldsWritesGrubAndPreseed()
    {
        using var db = Db();
        FakeDownloaded("debian-13");
        var deploy = await Service(db, untested: true);
        var (d, err) = await deploy.Create(Req("debian-13", name: "", user: "", pw: "") with { Language = "", Keyboard = null, TimeZone = "Europe/Stockholm", AutoDisk = false }, "admin");
        Assert.Null(err);
        Assert.Equal(DeployOs.Debian, d!.Os);
        Assert.Equal("debian-13", d.OsId);
        Assert.Equal([DeployField.Name, DeployField.User, DeployField.Password, DeployField.Disk, DeployField.Language], d.AskFields);
        Assert.Equal("", d.PasswordHash);
        Assert.Equal("Europe/Stockholm", d.TimeZone);
        Assert.Equal("", d.Keyboard);   // = Indstillingernes tastatur

        var grub = File.ReadAllText(Path.Combine(TempPaths.Pxe, "tftp", "grub", "mac", "52:54:00:12:34:56.cfg"));
        Assert.Contains("linux /linux/debian-13/linux auto=true priority=high url=http://192.0.2.10:8585/pxe/linux/52-54-00-12-34-56/preseed.cfg", grub);
        Assert.DoesNotContain("hostname=", grub);
        Assert.Equal("52:54:00:12:34:56,set:ubuntu\n", File.ReadAllText(Path.Combine(TempPaths.Pxe, "deploy", "hosts", "52-54-00-12-34-56")));

        var p = await deploy.LinuxAnswer(d, "preseed.cfg");
        Assert.NotNull(p);
        Assert.DoesNotContain("debian-installer/locale", p);           // spørges
        Assert.Contains("d-i keyboard-configuration/xkb-keymap select dk", p);
        Assert.Contains("d-i time/zone string Europe/Stockholm", p);
        Assert.Null(await deploy.LinuxAnswer(d, "ks.cfg"));
        Assert.Same(d, await deploy.ActiveLinux("52:54:00:12:34:56"));
        Assert.Null(await deploy.ActiveUbuntu("52:54:00:12:34:56"));
    }

    [Fact]
    public async Task Create_PasswordIsOnlyStoredAsHash()
    {
        using var db = Db();
        FakeDownloaded("rocky-10");
        var deploy = await Service(db, untested: true);
        var (d, err) = await deploy.Create(Req("rocky-10") with { SshKey = "  " + Key + "\n", Admin = false }, "admin");
        Assert.Null(err);
        Assert.StartsWith("$6$", d!.PasswordHash);
        Assert.Equal(Key, d.SshKey);
        Assert.True(d.NoAdmin);
        Assert.Empty(d.AskFields);
        var ks = await deploy.LinuxAnswer(d, "ks.cfg");
        NoSecrets(ks!);
        Assert.Contains("url --url=\"https://example.org/tree/\"", ks);
        var grub = File.ReadAllText(Path.Combine(TempPaths.Pxe, "tftp", "grub", "mac", "52:54:00:12:34:56.cfg"));
        Assert.Contains("inst.ks=http://192.0.2.10:8585/pxe/linux/52-54-00-12-34-56/ks.cfg", grub);
        Assert.Contains("inst.repo=https://example.org/tree/", grub);
        foreach (var f in Directory.GetFiles(TempPaths.Pxe, "*", SearchOption.AllDirectories)) NoSecrets(File.ReadAllText(f));
        var row = await db.Deployments.AsNoTracking().SingleAsync();
        Assert.DoesNotContain("hemmelig-kode", Json.Ser(row));
    }

    [Theory]
    [InlineData("kort", null, "mindst 6 tegn")]
    [InlineData("hemmelig-kode", "ikke-en-nøgle", "SSH-nøglen")]
    public async Task Create_RejectsBadInput(string pw, string? key, string expected)
    {
        using var db = Db();
        FakeDownloaded("debian-12");
        var (d, err) = await (await Service(db, untested: true)).Create(Req("debian-12", pw: pw) with { SshKey = key }, "admin");
        Assert.Null(d);
        Assert.Contains(expected, err);
    }

    [Fact]
    public async Task Create_SshKeyNeedsAUserName()
    {
        using var db = Db();
        FakeDownloaded("debian-12");
        var (d, err) = await (await Service(db, untested: true)).Create(Req("debian-12", user: "") with { SshKey = Key }, "admin");
        Assert.Null(d);
        Assert.Contains("brugernavn", err);
    }

    [Fact]
    public async Task Windows_PlainWindows11KeepsTheTemplateAndNewChoicesGetTheirOwnFile()
    {
        using var db = Db();
        Directory.CreateDirectory(Path.Combine(TempPaths.Pxe, "win11"));
        File.WriteAllText(Path.Combine(TempPaths.Pxe, "win11", "setup.exe"), "x");
        var deploy = await Service(db);
        // Sprog m.m. som i Indstillingerne og disken vælges på maskinen = skabelonen, som før
        var (d, err) = await deploy.Create(new DeployRequest("52-54-00-12-34-56", "windows-11", "managed", null, "STUE-PC", "Anna", null, null, null, null, null, null,
            Language: "da-DK", Keyboard: "dk", TimeZone: "Europe/Copenhagen"), "admin");
        Assert.Null(err);
        Assert.False(DeployService.NeedsOwnAnswerFile(d!));
        var dir = Path.Combine(TempPaths.Pxe, "deploy");
        Assert.False(File.Exists(Path.Combine(dir, "52-54-00-12-34-56.xml")));
        Assert.DoesNotContain("D_XML", File.ReadAllText(Path.Combine(dir, "52-54-00-12-34-56.cmd")));

        // Tomt pc-navn og ingen bruger: egen svarfil
        (d, err) = await deploy.Create(new DeployRequest("52-54-00-12-34-56", "windows-11", "clean", null, "", "", null, null, null, null, null, null, AutoDisk: true), "admin");
        Assert.Null(err);
        Assert.True(DeployService.NeedsOwnAnswerFile(d!));
        var cmd = File.ReadAllText(Path.Combine(dir, "52-54-00-12-34-56.cmd"));
        Assert.Contains("set \"D_XML=1\"\r\n", cmd);
        Assert.Contains("set \"D_WINDIR=win11\"\r\n", cmd);
        var x = XDocument.Parse(File.ReadAllText(Path.Combine(dir, "52-54-00-12-34-56.xml")));
        Assert.Empty(x.Descendants(U + "ComputerName"));
        Assert.Single(x.Descendants(U + "DiskConfiguration"));
        Assert.Equal("Romance Standard Time", x.Descendants(U + "TimeZone").Single().Value);
    }

    [Fact]
    public void MenuCmd_UsesTheOwnAnswerFileAndTheChosenWindowsFolder()
    {
        var menu = File.ReadAllText(Path.Combine(MacPackTests.RepoRoot, "provision/pxe/menu.cmd"));
        Assert.Contains("if defined D_XML goto installxml", menu);
        Assert.Contains("copy /y \"%DEPLOYDIR%\\%MAC%.xml\" \"%OUTXML%\"", menu);
        Assert.Contains("\"%W%\\PXE\\%D_WINDIR%\\setup.exe\" /unattend:\"%OUTXML%\"", menu);
        Assert.Contains("set \"D_WINDIR=win11\"", menu);
        // Menuen ("M") bruger aldrig udrulningens svarfil
        Assert.Contains("(set \"D_XML=\" & set \"D_WINDIR=win11\" & goto menu)", menu);
    }

    [Fact]
    public void Describe_NamesTheSystem()
    {
        Assert.Equal("Debian 13 Trixie Desktop", DeployService.Describe(new Deployment { OsId = "debian-13", Os = DeployOs.Debian, Variant = "desktop" }));
        Assert.Equal("Windows 11 (med mine ting)", DeployService.Describe(new Deployment { Os = DeployOs.Windows, Mode = DeployMode.Managed }));
        Assert.Equal("Ubuntu 24.04 LTS Server", DeployService.Describe(new Deployment { Os = DeployOs.Ubuntu, Series = "24.04", Variant = "server" }));
        Assert.Equal("Windows Server 2025 Core", DeployService.Describe(new Deployment { OsId = "windows-server-2025", Os = DeployOs.Windows, Variant = "server" }));
        Assert.Equal("Rocky Linux 10", DeployService.Describe(new Deployment { OsId = "rocky-10", Os = DeployOs.Rocky, Variant = "server" }));
    }
}
