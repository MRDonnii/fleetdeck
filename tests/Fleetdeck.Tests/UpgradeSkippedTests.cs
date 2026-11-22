using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Fleetdeck.Agent.Windows;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;
using static Fleetdeck.Server.Services.UpgradeAllReport;

namespace Fleetdeck.Tests;

/// <summary>"Opdatér alt" bagefter: listen over det, der blev sprunget over, og skjul/vis igen af gamle drivere.</summary>
public class UpgradeSkippedTests
{
    private static readonly Dictionary<string, string> NoLocks = new();

    // Sådan skriver Windows-agenten resultatet af "Opdatér alt: alle programmer" (Jobs.cs)
    private const string Output = """
        OK  Mozilla Firefox 131.0 → 132.0
        --  Musikprogram: venter (installeres pr. bruger – ingen er logget ind)
        !!  Eksempel Editor: fejlkode 0x8A150011 (installationen fejlede) – sprunget over
        --  Selvopdaterer: kan ikke opdateres via winget (0x8A15002B) – opdaterer sig selv; springes over
        --  Teksteditor: udeladt (undtagelse i profilen)

        1 opdateret, 1 fejlede.
        """;

    private static Skipped One(Item i, string? output = Output, bool withSystem = false, bool systemOk = false, IReadOnlyDictionary<string, string>? locks = null) =>
        Assert.Single(Build([i], locks ?? NoLocks, output, null, withSystem, systemOk));

    [Fact]
    public void Bruger_session_fejl_og_selvopdaterende_faar_hver_sin_grund()
    {
        var user = One(new Item("Musikprogram", "Musik.App"));
        Assert.Equal(UpgradeAllReport.User, user.Group);
        Assert.Contains("ingen var logget ind", user.Reason);
        Assert.Equal("upgrade", user.Action);

        var fail = One(new Item("Eksempel Editor", "Eksempel.Editor"));
        Assert.Equal(Fail, fail.Group);
        Assert.Equal("Fejlkode 0x8A150011 (installationen fejlede).", fail.Reason);

        var self = One(new Item("Selvopdaterer", "Self.Updater"));
        Assert.Equal(Silent, self.Group);
        Assert.Null(self.Action);

        Assert.Equal(Ignored, One(new Item("Teksteditor", "Tekst.Editor")).Group);
    }

    [Fact]
    public void Opdaterede_er_ikke_sprunget_over()
    {
        Assert.Empty(Build([new Item("Mozilla Firefox", "Mozilla.Firefox")], NoLocks, Output, null, false, false));
    }

    [Fact]
    public void Drivere_og_dybe_kilder_forklares_ud_fra_id()
    {
        var wuDriver = One(new Item("Skærmproducent – Monitor – 1.0.0.0", "wu:0b3a1c7e-1111-2222-3333-444455556666", Driver: true, Advice: "Driveren er fra 2015 – over 3 år gammel."));
        Assert.Equal(Silent, wuDriver.Group);
        Assert.Equal("Valgfri driver fra Windows Update – installér den enkeltvis.", wuDriver.Reason);
        Assert.Equal("Driveren er fra 2015 – over 3 år gammel.", wuDriver.Advice);

        var gpu = One(new Item("AMD Software: Adrenalin Edition (grafikdriver)", "gpu:amd"));
        Assert.Equal(Silent, gpu.Group);
        Assert.Equal("amd", gpu.Action);
        Assert.Contains("Installér driver", gpu.Reason);
        Assert.Equal("amd", One(new Item("AMD Chipset Software", "chipset:amd")).Action);
        Assert.Contains("Opdatér firmware", One(new Item("BIOS", "fwupd:abc")).Reason);
    }

    [Fact]
    public void Windows_opdatering_afhaenger_af_om_systemdelen_var_med()
    {
        var wu = new Item("2026-11 Kumulativ opdatering", "wu:0b3a1c7e-1111-2222-3333-444455557777");
        Assert.Equal(Separate, One(wu).Group);                                  // manuel: kun programmer
        Assert.Equal(Fail, One(wu, withSystem: true, systemOk: true).Group);    // automatisk, men står der stadig
        Assert.Contains("fejlede", One(wu, withSystem: true, systemOk: false).Reason);
    }

    [Fact]
    public void Laaste_er_ignoreret_med_hvor_laasen_kommer_fra()
    {
        var x = One(new Item("Mozilla Firefox", "Mozilla.Firefox"), locks: new Dictionary<string, string> { ["mozilla.firefox"] = "alle" }.ToDictionary(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(Ignored, x.Group);
        Assert.Equal("Opdateres aldrig – låst på alle maskiner.", x.Reason);
        Assert.Null(x.Action);
    }

    [Fact]
    public void Nye_efter_start_tages_ikke_med_og_resten_sorteres_efter_gruppe()
    {
        var start = new DateTime(2026, 11, 19, 10, 0, 0, DateTimeKind.Utc);
        var list = Build([
            new Item("Teksteditor", "Tekst.Editor"),
            new Item("Musikprogram", "Musik.App"),
            new Item("Ny app", "Ny.App", FirstSeen: start.AddMinutes(5)),
            new Item("AMD Software", "gpu:amd", FirstSeen: start.AddDays(-2)),
        ], NoLocks, Output, start, false, false);
        Assert.Equal(["AMD Software", "Musikprogram", "Teksteditor"], list.Select(x => x.Name));
    }

    [Fact]
    public void Ukendt_program_uden_linje_er_andet()
    {
        var x = One(new Item("Noget andet", "Andet.Program"));
        Assert.Equal(Other, x.Group);
        Assert.Equal("upgrade", x.Action);
    }

    [Fact]
    public void LineFor_kraever_hele_navnet()
    {
        string[] lines = ["--  Musikprogram Pro: venter", "OK  Musikprogram 1 → 2"];
        Assert.StartsWith("OK", LineFor(lines, "Musikprogram"));
        Assert.StartsWith("--", LineFor(lines, "Musikprogram Pro"));
        Assert.Null(LineFor(lines, "Musik"));
    }

    [Fact]
    public void Skjul_tager_kun_windows_update_id()
    {
        var ok = "wu:0b3a1c7e-1111-2222-3333-444455556666";
        var ids = UpdateHide.Valid([ok, ok.ToUpperInvariant(), "gpu:amd", "wu:ikke-en-guid", " ", "Mozilla.Firefox"]);
        Assert.Equal([ok], ids);
        Assert.Equal("Skjul i Windows Update: Printerdriver", UpdateHide.Title(true, ["Printerdriver"], 1));
        Assert.Equal("Vis igen i Windows Update: 3 opdateringer", UpdateHide.Title(false, ["a", "b", "c"], 3));
    }

    // ------------------------------------------------------------ agentens vurdering af valgfrie drivere

    private static readonly DateTime Now = new(2026, 11, 19);

    [Fact]
    public void Gammel_driver_anbefales_ikke()
    {
        Assert.Equal("Driveren er fra 2015 – over 3 år gammel.", DriverAdvice.For(new DateTime(2015, 3, 10), "Skærmproducent", null, Now));
        Assert.Null(DriverAdvice.For(new DateTime(2025, 1, 1), "Skærmproducent", null, Now));
        Assert.Null(DriverAdvice.For(null, null, null, Now));
    }

    [Fact]
    public void Driver_der_erstatter_microsofts_egen_eller_er_aeldre_anbefales_ikke()
    {
        var ms = new DriverAdvice.Installed(@"USB\VID_1234&PID_5678", "Microsoft", new DateTime(2006, 6, 21));
        Assert.Equal("Erstatter Windows' egen driver.", DriverAdvice.For(new DateTime(2024, 5, 1), "USB-producent", ms, Now));
        Assert.Null(DriverAdvice.For(new DateTime(2024, 5, 1), "Microsoft", ms, Now));    // Microsoft afløser sig selv
        var vendor = new DriverAdvice.Installed(@"PCI\VEN_1022&DEV_1483", "Producent", new DateTime(2025, 6, 1));
        Assert.Equal("Den installerede driver er nyere (2025-06-01).", DriverAdvice.For(new DateTime(2024, 1, 1), "Producent", vendor, Now));
        Assert.Equal("Driveren er fra 2016 – over 3 år gammel. Den installerede driver er nyere (2025-06-01).",
            DriverAdvice.For(new DateTime(2016, 1, 1), "Producent", vendor, Now));
    }

    [Fact]
    public void Driver_findes_paa_hardware_id()
    {
        List<DriverAdvice.Installed> list = [new(@"PCI\VEN_1022&DEV_1483&SUBSYS_00000000&REV_00", "Producent", null), new(@"USB\VID_1234&PID_5678", "Microsoft", null)];
        Assert.Equal("Microsoft", DriverAdvice.Match(list, @"usb\vid_1234&pid_5678")?.Provider);
        Assert.Equal("Producent", DriverAdvice.Match(list, @"PCI\VEN_1022&DEV_1483")?.Provider);   // WU's id er mindre specifikt
        Assert.Null(DriverAdvice.Match(list, @"PCI\VEN_8086&DEV_0001"));
        Assert.Null(DriverAdvice.Match(list, null));
    }
}

/// <summary>Skjulte opdateringer i databasen: tæller ikke med, men kan vises igen – og resultatet af "Opdatér alt" for en maskine.</summary>
public class UpgradeHiddenDbTests : IDisposable
{
    private readonly SqliteConnection conn = new("Data Source=:memory:");
    private readonly AppDb db;
    private readonly Machine m;

    public UpgradeHiddenDbTests()
    {
        conn.Open();
        db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        m = new Machine { Name = "ARBEJDS-PC", Platform = Platforms.Windows };
        db.Machines.Add(m); db.SaveChanges();
    }

    public void Dispose() { db.Dispose(); conn.Dispose(); }

    private const string Old = "wu:0b3a1c7e-1111-2222-3333-444455556666";

    private async Task Seed()
    {
        db.Upgrades.AddRange(
            new UpgradeItem { MachineId = m.Id, Name = "Skærmproducent – Monitor", WingetId = Old, Driver = true, Advice = "Driveren er fra 2015 – over 3 år gammel." },
            new UpgradeItem { MachineId = m.Id, Name = "Printerdriver", WingetId = "wu:0b3a1c7e-1111-2222-3333-444455558888", Driver = true, Hidden = true },
            new UpgradeItem { MachineId = m.Id, Name = "Musikprogram", WingetId = "Musik.App" });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Skjulte_taeller_ikke_med_men_kan_findes()
    {
        await Seed();
        Assert.Equal(2, await UpgradeLocks.OpenCount(db, m.Id));
        Assert.Equal(2, await db.Upgrades.CountAsync());
        Assert.Equal(["Printerdriver"], await db.Upgrades.IgnoreQueryFilters().Where(u => u.Hidden).Select(u => u.Name).ToListAsync());
    }

    [Fact]
    public async Task Skjul_og_vis_igen_flytter_linjen()
    {
        await Seed();
        // det samme som når agenten melder wu_hide lykkedes (AgentEndpoints)
        await db.Upgrades.IgnoreQueryFilters().Where(u => u.MachineId == m.Id && new[] { Old }.Contains(u.WingetId)).ExecuteUpdateAsync(x => x.SetProperty(u => u.Hidden, true));
        Assert.Equal(1, await UpgradeLocks.OpenCount(db, m.Id));
        await db.Upgrades.IgnoreQueryFilters().Where(u => u.MachineId == m.Id && new[] { Old }.Contains(u.WingetId)).ExecuteUpdateAsync(x => x.SetProperty(u => u.Hidden, false));
        Assert.Equal(2, await UpgradeLocks.OpenCount(db, m.Id));
    }

    [Fact]
    public async Task Inventar_sletter_ogsaa_de_skjulte()
    {
        await Seed();
        await db.Upgrades.IgnoreQueryFilters().Where(x => x.MachineId == m.Id).ExecuteDeleteAsync();
        Assert.Equal(0, await db.Upgrades.IgnoreQueryFilters().CountAsync());
    }

    private async Task<Job> AllJob(string status, string output)
    {
        var j = new Job { MachineId = m.Id, Type = JobTypes.Upgrade, Title = "Opdatér alt: alle programmer", Status = status, Output = output,
            ParamsJson = Json.Ser(new JobParams { Exclude = [] }), Created = DateTime.UtcNow.AddMinutes(-10), Started = DateTime.UtcNow.AddMinutes(-9), Finished = DateTime.UtcNow };
        db.Jobs.Add(j); await db.SaveChangesAsync();
        return j;
    }

    [Fact]
    public async Task Resultatet_for_maskinen_viser_det_sprungne_og_hvad_der_kan_skjules()
    {
        await Seed();
        var j = await AllJob(JobStatus.Success, "--  Musikprogram: venter (installeres pr. bruger – ingen er logget ind)\n\n3 opdateret, 0 fejlede.");
        var r = await UpgradeAllReport.ForMachine(db, m.Id, new Dictionary<string, string>());
        Assert.NotNull(r);
        var json = System.Text.Json.Nodes.JsonNode.Parse(Json.Ser(r))!;
        Assert.Equal(j.Id, (int)json["jobId"]!);
        Assert.Equal(3, (int)json["updated"]!);
        var skipped = json["skipped"]!.AsArray();
        Assert.Equal(2, skipped.Count);                                         // den skjulte er ikke med
        Assert.Equal("silent", (string)skipped[0]!["group"]!);
        Assert.Equal("user", (string)skipped[1]!["group"]!);
        Assert.Equal([Old], json["hideable"]!.AsArray().Select(x => (string)x!));
    }

    [Fact]
    public async Task Intet_resultat_mens_opdatér_alt_koerer()
    {
        await Seed();
        await AllJob(JobStatus.Running, "");
        Assert.Null(await UpgradeAllReport.ForMachine(db, m.Id, new Dictionary<string, string>()));
    }
}
