using System.Text.Json.Nodes;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Tests;

/// <summary>Opsætningsfilen (uden hemmeligheder), Pi-netboot i Fleetdecks egen dnsmasq og tunnelen i selve containeren.</summary>
public class SiteConfigTests : IDisposable
{
    private readonly SqliteConnection conn = new("Data Source=:memory:");
    private readonly AppDb db;
    private readonly string dir = Directory.CreateTempSubdirectory("fd-site-").FullName;

    public SiteConfigTests()
    {
        conn.Open();
        db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
    }

    public void Dispose() { db.Dispose(); conn.Dispose(); Directory.Delete(dir, true); }

    private static readonly Lock EnvGate = new();
    private Paths TempPaths()
    {
        // Paths læser miljøet, når den laves: sæt det kort og læg det straks tilbage, så andre test ikke mærker det
        lock (EnvGate)
        {
            var (w, d) = (Environment.GetEnvironmentVariable("WINSETUP_DIR"), Environment.GetEnvironmentVariable("DATA_DIR"));
            Environment.SetEnvironmentVariable("WINSETUP_DIR", dir);
            Environment.SetEnvironmentVariable("DATA_DIR", Path.Combine(dir, "data"));
            try { return new Paths(); }
            finally { Environment.SetEnvironmentVariable("WINSETUP_DIR", w); Environment.SetEnvironmentVariable("DATA_DIR", d); }
        }
    }

    [Theory]
    [InlineData("adguard_pass")] [InlineData("enroll_key")] [InlineData("tunnel_token")] [InlineData("tunnel_nonce")] [InlineData("krsso:0f18")]
    [InlineData("terminal_pin_1")] [InlineData("webhook_url")] [InlineData("ipad_supervision_key")] [InlineData("ipad_supervision_cert")] [InlineData("kontrolrum_sso_key")]
    [InlineData("update_token")] [InlineData("mqtt_pass")] [InlineData("share_pass")] [InlineData("ipad_backup_password")] [InlineData("kontrolrum_key")]
    [InlineData("backup_last_ok")] [InlineData("sched:4:kit")] [InlineData("update_state")] [InlineData("api_key_rejected")]
    public void Secrets_and_state_never_go_in_the_file(string key) => Assert.False(SiteConfig.Exportable(key));

    [Theory]
    [InlineData("server_url")] [InlineData("mqtt_host")] [InlineData("doh_host")] [InlineData("tunnel_inside")] [InlineData("install_timezone")] [InlineData("alert_disk_pct")]
    public void Ordinary_settings_go_in_the_file(string key) => Assert.True(SiteConfig.Exportable(key));

    [Fact]
    public async Task Export_and_import_bring_back_the_setup_without_secrets()
    {
        db.Settings.AddRange(new Setting { Key = "server_url", Value = "http://fleet.example:8585" }, new Setting { Key = "tunnel_token", Value = "hemmelig" },
            new Setting { Key = "mqtt_host", Value = "192.0.2.5" });
        var prof = new Profile { Name = "Pi-servere", PiExclude = ["*.bak*"], PiConfigLines = "dtoverlay=disable-wifi", PiCmdlineFlags = "cfg80211.ieee80211_regdom=DK", KitPaths = ["/opt/app"] };
        db.Profiles.Add(prof);
        await db.SaveChangesAsync();
        db.Machines.Add(new Machine { Name = "MINPI", Platform = Platforms.Linux, ProfileId = prof.Id, Protected = true, Tags = ["kiosk"] });
        await db.SaveChangesAsync();

        var doc = await SiteConfig.Export(db, TempPaths());
        var text = doc.ToJsonString();
        Assert.DoesNotContain("hemmelig", text);
        Assert.DoesNotContain("tunnel_token", text);
        Assert.Contains("fleet.example", text);
        Assert.Contains("dtoverlay=disable-wifi", text);

        // en ny installation: samme maskine tilmeldt, ingen profil og ingen indstillinger
        db.Profiles.RemoveRange(db.Profiles); db.Settings.RemoveRange(db.Settings.Where(s => s.Key != "tunnel_token"));
        var m = db.Machines.Single(); m.ProfileId = null; m.Protected = false; m.Tags = [];
        await db.SaveChangesAsync();
        doc["settings"]!.AsObject()["tunnel_token"] = "fra-filen";   // en hemmelighed i filen må ikke sætte noget
        var r = await SiteConfig.Import(db, JsonNode.Parse(doc.ToJsonString())!.AsObject());
        Assert.Equal(1, r.ProfilesCreated);
        Assert.Contains("indstilling tunnel_token", r.Skipped);
        db.ChangeTracker.Clear();
        var p = db.Profiles.Single();
        Assert.Equal(["*.bak*"], p.PiExclude);
        Assert.Equal("dtoverlay=disable-wifi", p.PiConfigLines);
        Assert.Equal(["/opt/app"], p.KitPaths);
        var back = db.Machines.Single();
        Assert.Equal(p.Id, back.ProfileId);
        Assert.True(back.Protected);
        Assert.Equal(["kiosk"], back.Tags);
        Assert.Equal("hemmelig", db.Settings.Find("tunnel_token")!.Value);
        Assert.Equal("http://fleet.example:8585", db.Settings.Find("server_url")!.Value);
    }

    [Fact]
    public async Task Printers_and_where_they_go_come_back_from_the_file()
    {
        var prof = new Profile { Name = "Hjemmekontor" };
        db.Profiles.Add(prof);
        await db.SaveChangesAsync();
        var m = new Machine { Name = "ARBEJDS-PC", Platform = Platforms.Windows };
        db.Machines.Add(m);
        var pr = new Printer { Name = "Kontorprinter", Uri = "ipp://192.0.2.40/ipp/print", Location = "Kontoret", Duplex = "none", Color = false, StatusJson = "{\"state\":\"idle\"}" };
        db.Printers.Add(pr);
        await db.SaveChangesAsync();
        db.PrinterAssignments.AddRange(new PrinterAssignment { PrinterId = pr.Id, ProfileId = prof.Id, MakeDefault = true },
            new PrinterAssignment { PrinterId = pr.Id, MachineId = m.Id, Keep = false });
        await db.SaveChangesAsync();

        var doc = await SiteConfig.Export(db, TempPaths());
        Assert.DoesNotContain("StatusJson", doc.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        db.Printers.RemoveRange(db.Printers);
        await db.SaveChangesAsync();

        var r = await SiteConfig.Import(db, JsonNode.Parse(doc.ToJsonString())!.AsObject());
        Assert.Equal(1, r.Printers);
        db.ChangeTracker.Clear();
        var back = db.Printers.Include(x => x.Assignments).Single();
        Assert.Equal(("Kontorprinter", "Kontoret", "none", false), (back.Name, back.Location, back.Duplex, back.Color));
        Assert.Equal(2, back.Assignments.Count);
        Assert.True(back.Assignments.Single(a => a.ProfileId == prof.Id).MakeDefault);
        Assert.False(back.Assignments.Single(a => a.MachineId == m.Id).Keep);
        // igen: ingen dubletter
        await SiteConfig.Import(db, JsonNode.Parse(doc.ToJsonString())!.AsObject());
        db.ChangeTracker.Clear();
        Assert.Equal(2, db.PrinterAssignments.Count());
    }

    [Fact]
    public async Task A_file_that_is_not_fleetdecks_is_refused()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => SiteConfig.Import(db, new JsonObject { ["noget"] = 1 }));
    }

    [Fact]
    public void Dnsmasq_answers_only_uefi_pcs_and_chosen_raspberry_pis()
    {
        var conf = File.ReadAllText(Path.Combine(Root(), "deploy", "docker", "dnsmasq.conf"));
        Assert.Contains("dhcp-ignore=tag:!efi64,tag:!rpi", conf);
        Assert.Contains("dhcp-hostsdir=/winsetup/PXE/pi/hosts", conf);
        Assert.Contains("pxe-service=tag:rpi,0,\"Raspberry Pi Boot\"", conf);
        Assert.Contains("mkdir -p /winsetup/PXE/deploy/hosts /winsetup/PXE/pi/hosts", File.ReadAllText(Path.Combine(Root(), "deploy", "docker", "start.sh")));
    }

    [Fact]
    public async Task Netboot_needs_the_boot_files_and_then_answers_only_that_pi()
    {
        var paths = TempPaths();
        db.Machines.Add(new Machine { Name = "MINPI", Platform = Platforms.Linux, Mac = "B8-27-EB-00-11-22", FactsJson = """{"model":"Raspberry Pi 3 Model B Rev 1.2","piSerial":"1a2b3c4d"}""" });
        await db.SaveChangesAsync();
        var id = db.Machines.Single().Id;
        var nb = new PiNetboot(paths, db, new AuditService(db));
        Assert.Contains("mangler", await nb.Set(id, true, null, "test"));
        var sd = Path.Combine(paths.Pxe, "tftp", "1a2b3c4d");
        Directory.CreateDirectory(sd);
        foreach (var f in new[] { "start.elf", "kernel7.img", "bootcode.bin" }) File.WriteAllText(Path.Combine(sd, f), "x");
        File.WriteAllText(Path.Combine(sd, "cmdline.txt"), "console=tty1 root=/dev/nfs nfsroot=192.0.2.10:/srv/rootfs,vers=3 rw ip=dhcp rootwait");
        Assert.Null(await nb.Set(id, true, null, "test"));
        Assert.Equal("b8:27:eb:00:11:22,set:rpi\n", File.ReadAllText(Path.Combine(paths.Pxe, "pi", "hosts", "B8-27-EB-00-11-22".ToLowerInvariant())));
        Assert.True(File.Exists(Path.Combine(paths.Pxe, "tftp", "bootcode.bin")));   // en Pi 3 henter den i roden
        var st = System.Text.Json.JsonSerializer.SerializeToElement(await nb.Status(id));
        Assert.True(st.GetProperty("enabled").GetBoolean());
        Assert.Equal("192.0.2.10:/srv/rootfs", st.GetProperty("nfsRoot").GetString());
        Assert.Null(await nb.Set(id, false, null, "test"));
        Assert.False(File.Exists(Path.Combine(paths.Pxe, "pi", "hosts", "b8-27-eb-00-11-22")));
    }

    [Fact]
    public void The_tunnel_reports_when_cloudflared_is_missing()
    {
        Environment.SetEnvironmentVariable("CLOUDFLARED_PATH", Path.Combine(dir, "findes-ikke"));
        try { Assert.False(TunnelInside.Available); }
        finally { Environment.SetEnvironmentVariable("CLOUDFLARED_PATH", null); }
    }

    private static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Fleetdeck.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("Fleetdeck.slnx");
    }
}
