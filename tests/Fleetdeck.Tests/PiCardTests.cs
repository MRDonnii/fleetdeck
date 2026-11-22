using System.Security.Cryptography;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Tests;

/// <summary>"Lav SD-kort": valg af basisbillede, fletning af boot-opsætningen, settings.env og selve bygningen med mtools (uden root).
/// Bygningstestene kører kun, hvor sfdisk, mtools og xz findes (FLEETDECK_TOOLS_DIR kan pege på en mappe med mtools).</summary>
public class PiCardTests
{
    private const string Bookworm32 = "Raspbian GNU/Linux 12 (bookworm) armv7l minpi 2026-11-19T01:30:00+01:00";

    /// <summary>3.31.0 gav eksisterende profiler PiExclude = "" (ikke en JSON-liste), og så kunne ingen profil – og dermed ingen maskine – læses.</summary>
    [Fact]
    public void A_profile_with_an_empty_text_list_can_still_be_read()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using var db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        db.Profiles.Add(new Profile { Name = "Linux" });
        db.SaveChanges();
        db.Database.ExecuteSqlRaw("UPDATE Profiles SET PiExclude = '', UpgradeExclude = ''");
        db.ChangeTracker.Clear();
        var p = db.Profiles.AsNoTracking().Single();
        Assert.Empty(p.PiExclude);
        Assert.Empty(p.UpgradeExclude);
    }

    [Fact]
    public void The_migration_gives_existing_profiles_an_empty_list()
    {
        var src = File.ReadAllText(Directory.GetFiles(Path.Combine(Root(), "src", "Fleetdeck.Server", "Data", "Migrations"), "*_PiCards.cs").Single(f => !f.EndsWith(".Designer.cs")));
        Assert.Contains("defaultValue: \"[]\"", src);
        Assert.Contains("SET PiExclude = '[]'", src);
    }

    private static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Fleetdeck.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("Fleetdeck.slnx");
    }

    [Fact]
    public void Catalog_has_bookworm_32_bit_with_a_checksum_and_exact_urls()
    {
        var b = PiCardLogic.Image("bookworm-armhf-lite");
        Assert.NotNull(b);
        Assert.True(b.Usable);
        Assert.Matches("^[0-9a-f]{64}$", b.Sha256);
        Assert.All(PiCardLogic.Catalog, i =>
        {
            Assert.StartsWith("https://downloads.raspberrypi.com/", i.Url);
            Assert.DoesNotContain("latest", i.Url);
            Assert.EndsWith(".img.xz", i.Url);
            Assert.Matches("^[0-9a-f]{64}$", i.Sha256);
        });
        Assert.Contains(PiCardLogic.Catalog, i => i.Debian == "trixie" && i.Arch == "arm64");
    }

    [Fact]
    public void A_bookworm_32_bit_restore_point_gets_the_bookworm_32_bit_image()
    {
        var sys = PiCardLogic.ParseSystem(Bookworm32);
        Assert.Equal("bookworm", sys.Debian);
        Assert.Equal("armhf", sys.Arch);
        Assert.True(sys.ArchCertain);
        var (img, err) = PiCardLogic.MatchBase(sys, null);
        Assert.Null(err);
        Assert.Equal("bookworm-armhf-lite", img!.Id);
    }

    [Fact]
    public void The_dpkg_architecture_wins_over_a_64_bit_kernel()
    {
        var sys = PiCardLogic.ParseSystem("Raspbian GNU/Linux 12 (bookworm) aarch64 minpi 2026-11-19T01:30:00+01:00 dpkg=armhf");
        Assert.Equal("armhf", sys.Arch);
        Assert.True(sys.ArchCertain);
        Assert.Equal("arm64", PiCardLogic.ParseSystem("Debian GNU/Linux 13 (trixie) aarch64 minpi 2026").Arch);
        Assert.False(PiCardLogic.ParseSystem("Debian GNU/Linux 13 (trixie) aarch64 minpi 2026").ArchCertain);
    }

    [Fact]
    public void A_wrong_combination_is_refused_in_danish()
    {
        var sys = PiCardLogic.ParseSystem(Bookworm32);
        var (img, err) = PiCardLogic.MatchBase(sys, "trixie-arm64-lite");
        Assert.Null(img);
        Assert.Contains("passer ikke", err);
        Assert.Contains("32-bit", err);
        var (none, why) = PiCardLogic.MatchBase(PiCardLogic.ParseSystem("noget helt andet"), null);
        Assert.Null(none);
        Assert.Contains("Tag et nyt punkt", why);
    }

    [Fact]
    public void An_image_whose_first_start_is_not_verified_cannot_be_used()
    {
        var (img, err) = PiCardLogic.MatchBase(PiCardLogic.ParseSystem("Debian GNU/Linux 13 (trixie) aarch64 minpi 2026 dpkg=arm64"), null);
        Assert.Null(img);
        Assert.Contains("ikke afprøvet", err);
    }

    [Fact]
    public void Cmdline_keeps_the_new_cards_partuuid_and_adds_only_allowed_flags()
    {
        const string fresh = "console=serial0,115200 console=tty1 root=PARTUUID=8c9453e7-02 rootfstype=ext4 fsck.repair=yes rootwait quiet init=/usr/lib/raspberrypi-sys-mods/firstboot";
        var (ok, bad) = PiCardLogic.SplitFlags("cfg80211.ieee80211_regdom=DK root=PARTUUID=deadbeef-02 ip=192.168.1.50::192.168.1.1:255.255.255.0:minpi:eth0:off init=/bin/sh");
        Assert.Equal(["cfg80211.ieee80211_regdom=DK"], ok);
        Assert.Equal(3, bad.Count);
        var merged = PiCardLogic.MergeCmdline(fresh, ok.Concat(["root=PARTUUID=deadbeef-02"]));
        Assert.Contains("root=PARTUUID=8c9453e7-02", merged);
        Assert.DoesNotContain("deadbeef", merged);
        Assert.Contains("init=/usr/lib/raspberrypi-sys-mods/firstboot", merged);   // billedets egen førstestart (gør plads på kortet) skal blive
        Assert.Contains("cfg80211.ieee80211_regdom=DK", merged);
        Assert.EndsWith(PiCardLogic.FirstrunFlags + "\n", merged);
        Assert.Single(merged.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        // en gang til giver det samme (ingen dobbelte flag)
        Assert.Equal(merged, PiCardLogic.MergeCmdline(merged, ok));
    }

    [Fact]
    public void Config_keeps_the_new_file_and_adds_profile_lines_under_all()
    {
        var (ok, bad) = PiCardLogic.SplitConfigLines("dtoverlay=w1-gpio,gpiopin=4\ndtoverlay=disable-wifi · dtoverlay=disable-bt\nauto_initramfs=0\nkernel=kernel7.img\n# kommentar");
        Assert.Equal(["dtoverlay=w1-gpio,gpiopin=4", "dtoverlay=disable-wifi", "dtoverlay=disable-bt"], ok);
        Assert.Equal(["auto_initramfs=0", "kernel=kernel7.img"], bad);
        var merged = PiCardLogic.MergeConfig("dtparam=audio=on\nauto_initramfs=1\n[cm5]\ndtoverlay=dwc2,dr_mode=host\n[all]\n", ok);
        Assert.Contains("auto_initramfs=1", merged);
        Assert.DoesNotContain("auto_initramfs=0", merged);
        var tail = merged[merged.LastIndexOf("[all]", StringComparison.Ordinal)..];
        Assert.Contains("dtoverlay=w1-gpio,gpiopin=4", tail);
        Assert.Equal("dtparam=audio=on\n", PiCardLogic.MergeConfig("dtparam=audio=on\n", []));
    }

    [Fact]
    public void Exclude_patterns_are_plain_relative_paths()
    {
        Assert.Equal(["etc/systemd/system/NetworkManager.service.requires/*", "*.bak*", "home/bruger/test"],
            PiCardLogic.CleanExcludes(["etc/systemd/system/NetworkManager.service.requires/*", "*.bak*", "/home/bruger/test", "../etc/shadow", "a;rm -rf /", ""]));
    }

    [Fact]
    public void Settings_env_is_safe_for_bash_and_holds_no_secret()
    {
        var env = PiCardLogic.SettingsEnv(new("https://fleet.example:8586", "abc=", "minpi", "bruger", "erstat", 3, "static", "eth0",
            "192.168.1.50/24", "192.168.1.1", "192.168.1.1", "Europe/Copenhagen", "dk", "DK"));
        Assert.Contains("FD_NAME='minpi'", env);
        Assert.Contains("FD_ADDRESS='192.168.1.50/24'", env);
        Assert.DoesNotMatch(PiCardLogic.SecretLike(), env);
        var evil = PiCardLogic.SettingsEnv(new("http://x", "", "a'; rm -rf /; '", "b", "ny", 0, "dhcp", "eth0", "", "", "", "", "", ""));
        Assert.Contains("FD_NAME='a'\\''; rm -rf /; '\\'''", evil);
    }

    [Fact]
    public void Secret_scan_catches_private_keys_and_tokens()
    {
        Assert.Matches(PiCardLogic.SecretLike(), "-----BEGIN OPENSSH PRIVATE KEY-----");
        Assert.Matches(PiCardLogic.SecretLike(), "token=ghp_abcdefghijklmnopqrstuvwxyz123456");
        Assert.DoesNotMatch(PiCardLogic.SecretLike(), "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIExampleKeyForTestsOnly bruger@pc");
        Assert.DoesNotMatch(PiCardLogic.SecretLike(), File.ReadAllText(Template("pi-firstrun.sh")));
        Assert.DoesNotMatch(PiCardLogic.SecretLike(), File.ReadAllText(Template("pi-firstboot.sh")));
    }

    [Theory]
    [InlineData("192.168.1.50/24", true)]
    [InlineData("192.168.1.50", false)]
    [InlineData("192.168.1.500/24", false)]
    [InlineData("192.168.1.50/33", false)]
    public void Address_needs_a_netmask(string a, bool ok) => Assert.Equal(ok, PiCardLogic.CheckAddress(a) is null);

    [Fact]
    public void The_first_start_scripts_remove_the_code_and_the_first_start_flags()
    {
        var run = File.ReadAllText(Template("pi-firstrun.sh"));
        Assert.Contains("rm -rf \"$SRC\"", run);
        Assert.Contains("rm -f \"$FW/firstrun.sh\"", run);
        Assert.Contains("systemd\\.run=", run);
        Assert.Contains("userconf", run);
        Assert.DoesNotContain("chpasswd", run);   // ingen adgangskode på kortet
        var boot = File.ReadAllText(Template("pi-firstboot.sh"));
        Assert.Contains("rm -rf \"$D\"", boot);
        Assert.Contains("X-Fleetdeck-Code", boot);   // koden i en header, ikke i adressen
        Assert.Contains("WS_PROVISION=1", boot);
    }

    private static string Template(string name) => Path.Combine(AppContext.BaseDirectory, "Templates", name);

    // ---------------------------------------------------------------- selve bygningen

    private static bool Tools() => PiImageBuilder.ToolsPresent();

    /// <summary>Et lille syntetisk billede (16 MB FAT + 8 MB "rodfilsystem") med en cmdline.txt og config.txt som Pi OS'.</summary>
    private static async Task<string> SyntheticImage(string dir)
    {
        var img = Path.Combine(dir, "base.img");
        await using (var f = File.Create(img)) f.SetLength(32L * 1024 * 1024);
        await using (var table = new MemoryStream("label: dos\nstart=2048, size=32768, type=c\nstart=34816, size=16384, type=83\n"u8.ToArray()))
            Assert.Equal(0, (await PiImageBuilder.Run("sfdisk", ["-q", img], CancellationToken.None, stdin: table)).Code);
        const long off = 2048L * 512;
        Assert.Equal(0, (await PiImageBuilder.Run("mformat", ["-i", PiImageBuilder.Fat(img, off), "-F", "-v", "bootfs", "-T", "32768", "-h", "64", "-s", "32", "::"], CancellationToken.None)).Code);
        await PiImageBuilder.WriteFile(img, off, "cmdline.txt", "console=tty1 root=PARTUUID=12345678-02 rootfstype=ext4 rootwait quiet init=/usr/lib/raspberrypi-sys-mods/firstboot\n", CancellationToken.None);
        await PiImageBuilder.WriteFile(img, off, "config.txt", "dtparam=audio=on\nauto_initramfs=1\n[all]\n", CancellationToken.None);
        // rodfilsystemet: tilfældige byte, der ikke må ændres
        var root = RandomNumberGenerator.GetBytes(8 * 1024 * 1024);
        await using (var f = new FileStream(img, FileMode.Open)) { f.Position = 34816L * 512; await f.WriteAsync(root); }
        var xz = img + ".xz";
        await using (var o = File.Create(xz))
        await using (var i = File.OpenRead(img)) Assert.Equal(0, (await PiImageBuilder.Run("xz", ["-T0", "-1", "-c"], CancellationToken.None, o, i)).Code);
        return xz;
    }

    private static PiImageBuilder.Files Files(string cmdline, string config) => new(
        PiCardLogic.MergeCmdline(cmdline, ["cfg80211.ieee80211_regdom=DK"]), PiCardLogic.MergeConfig(config, ["dtoverlay=disable-wifi"]),
        PiCardLogic.SettingsEnv(new("http://fleet.example:8585", "", "minpi", "bruger", "erstat", 1, "static", "eth0", "192.168.1.50/24", "192.168.1.1", "", "Europe/Copenhagen", "dk", "DK")),
        "fd_engangskode_eksempel", "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIExampleKeyForTestsOnlyExampleKey bruger@pc\n",
        File.ReadAllText(Template("pi-firstrun.sh")), File.ReadAllText(Template("pi-firstboot.sh")));

    [Fact]
    public async Task Building_a_card_changes_only_the_boot_partition()
    {
        if (!Tools() || !File.Exists(PiToolPath("mformat"))) return;   // værktøjerne findes ikke her
        var dir = Directory.CreateTempSubdirectory("fd-picard-").FullName;
        try
        {
            var baseXz = await SyntheticImage(dir);
            var steps = new List<string>();
            var r = await PiImageBuilder.Build(baseXz, Path.Combine(dir, "out"), "minpi.img.xz", Files, (_, t) => steps.Add(t), CancellationToken.None);
            Assert.True(File.Exists(r.File));
            Assert.Matches("^[0-9a-f]{64}$", r.Sha256);
            Assert.Contains("Komprimerer til .img.xz", steps);
            Assert.False(File.Exists(Path.Combine(dir, "out", "arbejde.img")));   // arbejdsfilen er væk

            // pak begge ud og sammenlign: rodfilsystemet er byte for byte det samme
            var before = Path.Combine(dir, "before.img"); var after = Path.Combine(dir, "after.img");
            await using (var o = File.Create(before)) await PiImageBuilder.Run("xz", ["-dc", baseXz], CancellationToken.None, o);
            await using (var o = File.Create(after)) await PiImageBuilder.Run("xz", ["-dc", r.File], CancellationToken.None, o);
            byte[] Root(string f) { var b = File.ReadAllBytes(f); return b[(34816 * 512)..]; }
            Assert.Equal(SHA256.HashData(Root(before)), SHA256.HashData(Root(after)));

            const long off = 2048L * 512;
            var cmd = await PiImageBuilder.ReadFile(after, off, "cmdline.txt", CancellationToken.None);
            Assert.Contains("root=PARTUUID=12345678-02", cmd);
            Assert.Contains("systemd.run=/boot/firmware/firstrun.sh", cmd);
            Assert.Contains("cfg80211.ieee80211_regdom=DK", cmd);
            Assert.Contains("dtoverlay=disable-wifi", await PiImageBuilder.ReadFile(after, off, "config.txt", CancellationToken.None));
            Assert.Equal("fd_engangskode_eksempel\n", await PiImageBuilder.ReadFile(after, off, "fleetdeck/code", CancellationToken.None));
            Assert.Contains("FD_MODE='erstat'", await PiImageBuilder.ReadFile(after, off, "fleetdeck/settings.env", CancellationToken.None));
            Assert.Contains("ExecStart=/usr/local/sbin/fleetdeck-firstboot", await PiImageBuilder.ReadFile(after, off, "fleetdeck/fleetdeck-firstboot.service", CancellationToken.None));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task A_card_with_something_secret_is_not_built()
    {
        if (!Tools() || !File.Exists(PiToolPath("mformat"))) return;
        var dir = Directory.CreateTempSubdirectory("fd-picard-").FullName;
        try
        {
            var baseXz = await SyntheticImage(dir);
            PiImageBuilder.Files Leaky(string c, string g) => Files(c, g) with { AuthorizedKeys = "-----BEGIN OPENSSH PRIVATE KEY-----\nxx\n" };
            var e = await Assert.ThrowsAsync<InvalidOperationException>(() => PiImageBuilder.Build(baseXz, Path.Combine(dir, "out"), "x.img.xz", Leaky, (_, _) => { }, CancellationToken.None));
            Assert.Contains("hemmelighed", e.Message);
            Assert.False(File.Exists(Path.Combine(dir, "out", "x.img.xz")));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>Med FD_PI_REAL_IMAGE=&lt;sti til et ægte Pi OS-billede (.img.xz)&gt;: byg et kort af det rigtige billede og læs det tilbage.</summary>
    [Fact]
    public async Task A_real_raspberry_pi_os_image_can_be_built()
    {
        var real = Environment.GetEnvironmentVariable("FD_PI_REAL_IMAGE");
        if (real is not { Length: > 0 } || !File.Exists(real) || !Tools()) return;
        var dir = Directory.CreateTempSubdirectory("fd-picard-real-").FullName;
        try
        {
            var r = await PiImageBuilder.Build(real, dir, "rigtig.img.xz", Files, (_, _) => { }, CancellationToken.None);
            var img = Path.Combine(dir, "rigtig.img");
            await using (var o = File.Create(img)) await PiImageBuilder.Run("xz", ["-dc", r.File], CancellationToken.None, o);
            var off = await PiImageBuilder.BootOffset(img, CancellationToken.None);
            var cmd = await PiImageBuilder.ReadFile(img, off, "cmdline.txt", CancellationToken.None);
            Assert.Matches(@"root=PARTUUID=[0-9a-f]{8}-02", cmd);
            Assert.Contains("init=/usr/lib/raspberrypi-sys-mods/firstboot", cmd);
            Assert.EndsWith(PiCardLogic.FirstrunFlags + "\n", cmd);
            Assert.Contains("#!/bin/bash", await PiImageBuilder.ReadFile(img, off, "firstrun.sh", CancellationToken.None));
            Assert.Contains("auto_initramfs=1", await PiImageBuilder.ReadFile(img, off, "config.txt", CancellationToken.None));
        }
        finally { Directory.Delete(dir, true); }
    }

    private static string PiToolPath(string t) => PiImageBuilder.ToolDir.Length > 0 ? Path.Combine(PiImageBuilder.ToolDir, t) : Path.Combine("/usr/bin", t);
}
