using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;

namespace Fleetdeck.Tests;

/// <summary>fwupd-fejl, der ikke kan løses ved at prøve igen (firmwaren afviser en Secure Boot-nøgle), genkendes på dansk og engelsk.</summary>
public class FwupdErrorTests
{
    // En Linux-pc med Secure Boot slået til: KEK CA 2011 → 2023. Dæmonen skriver på systemets sprog (dansk).
    private const string KioskDa = """
        Downloading…             [***************************************]
        Decompressing…           [***************************************]
        failed to write-firmware: failed to write (null): failed to write data to efivarsfs: Fejl under skrivning til fildeskriptor: Adgang nægtet
        """;

    private const string KioskEn = """
        Downloading…             [***************************************]
        Decompressing…           [***************************************]
        Writing…                 [                                       ]
        failed to write-firmware: failed to write (null): failed to write data to efivarsfs: Error writing to file descriptor: Permission denied
        """;

    [Theory]
    [InlineData(KioskDa)]
    [InlineData(KioskEn)]
    [InlineData("failed to write data to efivarsfs: Error writing to file descriptor: Operation not permitted")]
    [InlineData("failed to write data to efivarsfs: Fejl under skrivning til fildeskriptor: Operationen er ikke tilladt")]
    [InlineData("failed to write data to efivarsfs: Error writing to file descriptor: Read-only file system")]
    [InlineData("failed to write data to efivarsfs: Fejl under skrivning til fildeskriptor: Filsystem med kun læseadgang")]
    [InlineData("failed to write efivarfs variable KEK-8be4df61-93ca-11d2-aa0d-00e098032b8c: EACCES")]
    [InlineData("System firmware cannot accept DBX updates")]
    [InlineData("failed to write KEK: Security Violation")]
    [InlineData("KEK update is not signed by the platform key (PK)")]
    [InlineData("db update: invalid signature")]
    public void Firmwaren_afviser_opdateringen(string output) =>
        Assert.Equal(JobReasons.FirmwareRejected, FwupdErrors.Classify(output));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Successfully installed firmware")]
    [InlineData("failed to download https://fwupd.org/downloads/x.cab: Could not resolve host: fwupd.org")]
    [InlineData("failed to open /var/lib/fwupd/pending.db: Permission denied")]               // ikke efivarsfs: kan skyldes noget andet
    [InlineData("failed to verify metadata signature: expired")]                               // ingen Secure Boot-variabel
    [InlineData("KEK CA: The Key Exchange Key updates the signature database (db)")]           // beskrivelse, ingen fejl
    [InlineData("failed to write data to efivarsfs: No space left on device")]                 // fuld NVRAM – en anden sag
    public void Andre_fejl_kan_proeves_igen(string? output) => Assert.Null(FwupdErrors.Classify(output));

    [Fact]
    public void Forklaringen_er_paa_dansk()
    {
        Assert.Equal("Firmwaren afviser opdateringen (Secure Boot-nøgle). Den kan ikke installeres fra Linux – kræver en BIOS-opdatering fra producenten. Maskinen starter fint uden.",
            FwupdErrors.Text(JobReasons.FirmwareRejected));
        Assert.True(JobReasons.IsCannotInstall(JobReasons.FirmwareRejected));
        Assert.False(JobReasons.IsCannotInstall(null));
        Assert.False(JobReasons.IsCannotInstall("noget_andet"));
    }

    [Fact]
    public void Serveren_tager_agentens_grund_eller_genkender_fwupd_selv()
    {
        // ny agent: den særlige grund
        Assert.Equal(JobReasons.FirmwareRejected, UpgradeMarks.CannotInstallReason("fwupd:abc", JobReasons.FirmwareRejected, "hvad som helst"));
        // ældre agent uden grund: serveren læser fwupd-outputtet selv
        Assert.Equal(JobReasons.FirmwareRejected, UpgradeMarks.CannotInstallReason("fwupd:abc", null, KioskDa));
        // kun fwupd: en Linux-pakke med "Adgang nægtet" er ikke firmware
        Assert.Null(UpgradeMarks.CannotInstallReason("firefox", null, KioskDa));
        Assert.Null(UpgradeMarks.CannotInstallReason("fwupd:abc", null, "Could not resolve host"));
    }
}

/// <summary>Kan ikke installeres og skjul i Fleetdeck (UpgradeMarks): huskes, når agenten sender et nyt inventar.</summary>
public class UpgradeMarksDbTests : IDisposable
{
    private readonly SqliteConnection conn = new("Data Source=:memory:");
    private readonly AppDb db;
    private readonly Machine m;
    private const string Kek = "fwupd:b7a1d3d90faa1f6275d9a98da4fb3be7118e61c7";
    private static readonly DateTime Now = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

    public UpgradeMarksDbTests()
    {
        conn.Open();
        db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        m = new Machine { Name = "kiosk", Platform = Platforms.Linux };
        db.Machines.Add(m); db.SaveChanges();
    }

    public void Dispose() { db.Dispose(); conn.Dispose(); }

    private static List<UpgradeItem> Inventory(int machineId, string kekVersion = "2023") =>
    [
        new() { MachineId = machineId, Name = "KEK CA", WingetId = Kek, Current = "2011", Available = kekVersion },
        new() { MachineId = machineId, Name = "firefox", WingetId = "firefox", Current = "131.0", Available = "132.0" },
    ];

    /// <summary>Som /api/agent/inventory: slet alt, læg de nye linjer ind med mærkerne.</summary>
    private async Task SendInventory(string kekVersion = "2023")
    {
        await db.Upgrades.IgnoreQueryFilters().Where(x => x.MachineId == m.Id).ExecuteDeleteAsync();
        var fresh = Inventory(m.Id, kekVersion);
        await UpgradeMarks.ApplyInventory(db, m.Id, fresh);
        db.Upgrades.AddRange(fresh);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task Firmware_der_afvises_foreslaas_ikke_igen_heller_ikke_efter_nyt_inventar()
    {
        await SendInventory();
        Assert.Equal(2, await UpgradeLocks.OpenCount(db, m.Id));

        Assert.True(await UpgradeMarks.Block(db, m.Id, Kek, FwupdErrors.RejectedText, Now));
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        Assert.Equal(1, await UpgradeLocks.OpenCount(db, m.Id));                  // tæller ikke med
        Assert.Equal(["firefox"], await db.Upgrades.Select(u => u.Name).ToListAsync());

        await SendInventory();                                                    // agenten melder den igen (Blocked = null fra agenten)
        var kek = await db.Upgrades.IgnoreQueryFilters().SingleAsync(u => u.WingetId == Kek);
        Assert.Equal(FwupdErrors.RejectedText, kek.Blocked);
        Assert.Equal(1, await UpgradeLocks.OpenCount(db, m.Id));
        Assert.Contains(Kek, await UpgradeMarks.Exclude(db, m.Id));               // "Opdatér alt" lader den være

        await SendInventory("2024");                                              // ny version: tilbydes igen, mærket er væk
        Assert.Equal(2, await UpgradeLocks.OpenCount(db, m.Id));
        Assert.Empty(await db.UpgradeMarks.ToListAsync());
    }

    [Fact]
    public async Task Lykkes_den_alligevel_glemmes_det()
    {
        await SendInventory();
        await UpgradeMarks.Block(db, m.Id, Kek, FwupdErrors.RejectedText, Now);
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        await UpgradeMarks.Unblock(db, m.Id, Kek);
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        Assert.Empty(await db.UpgradeMarks.ToListAsync());
        Assert.Null((await db.Upgrades.SingleAsync(u => u.WingetId == Kek)).Blocked);
    }

    [Fact]
    public async Task Skjul_i_Fleetdeck_huskes_og_kan_vises_igen()
    {
        await SendInventory();
        var names = await UpgradeMarks.SetHidden(db, m.Id, [Kek, "findes-ikke"], true, "admin", Now);
        db.ChangeTracker.Clear();
        Assert.Equal(["KEK CA"], names);                                          // kun det, maskinen har meldt
        Assert.Equal(["firefox"], await db.Upgrades.Select(u => u.Name).ToListAsync());

        await SendInventory();                                                    // agenten ved intet om det – serveren husker
        Assert.True((await db.Upgrades.IgnoreQueryFilters().SingleAsync(u => u.WingetId == Kek)).Hidden);
        Assert.Contains(Kek, await UpgradeMarks.Exclude(db, m.Id));

        Assert.Equal(["KEK CA"], await UpgradeMarks.SetHidden(db, m.Id, [Kek], false, "admin", Now));
        db.ChangeTracker.Clear();
        Assert.Empty(await db.UpgradeMarks.ToListAsync());
        Assert.Equal(2, await db.Upgrades.CountAsync());
        await SendInventory();
        Assert.Equal(2, await db.Upgrades.CountAsync());
    }

    [Fact]
    public async Task Skjult_og_blokeret_vis_igen_beholder_blokeringen()
    {
        await SendInventory();
        await UpgradeMarks.Block(db, m.Id, Kek, FwupdErrors.RejectedText, Now);
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        await UpgradeMarks.SetHidden(db, m.Id, [Kek], true, "admin", Now);
        await UpgradeMarks.SetHidden(db, m.Id, [Kek], false, "admin", Now);
        db.ChangeTracker.Clear();
        var k = await db.UpgradeMarks.SingleAsync();
        Assert.False(k.Hidden);
        Assert.Equal(FwupdErrors.RejectedText, k.Blocked);
    }

    [Fact]
    public void Kun_andet_end_Windows_Update_skjules_i_Fleetdeck()
    {
        Assert.Equal([Kek, "firefox", "macos:Sonoma 14.7"],
            UpgradeMarks.Valid([Kek, " firefox ", "wu:0b3a1c7e-1111-2222-3333-444455556666", "", "macos:Sonoma 14.7", "firefox", "bad\nid"]));
        Assert.Equal("Skjult: KEK CA", UpdateHide.Title(true, ["KEK CA"], 1, inFleetdeck: true));
        Assert.Equal("Vises igen: 2 opdateringer", UpdateHide.Title(false, ["a", "b"], 2, inFleetdeck: true));
    }

    [Fact]
    public async Task Opdater_alt_viser_den_under_kan_ikke_installeres()
    {
        await SendInventory();
        await UpgradeMarks.Block(db, m.Id, Kek, FwupdErrors.RejectedText, Now);
        db.Jobs.Add(new Job { MachineId = m.Id, Type = JobTypes.Upgrade, Title = "Opdatér alt: alle programmer", ParamsJson = "{}", Status = JobStatus.Success,
            Created = Now.AddHours(1), Started = Now.AddHours(1), Finished = Now.AddHours(1.1), Output = "Alt er opdateret." });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();

        var skipped = UpgradeAllReport.Build(
            [new UpgradeAllReport.Item("KEK CA", Kek, Blocked: FwupdErrors.RejectedText), new UpgradeAllReport.Item("Andet firmware", "fwupd:andet")],
            new Dictionary<string, string>(), "", null, false, false);
        var kek = skipped.Single(x => x.WingetId == Kek);
        Assert.Equal(UpgradeAllReport.Blocked, kek.Group);
        Assert.Equal(FwupdErrors.RejectedText, kek.Reason);
        Assert.Null(kek.Action);                                                  // ingen "Opdatér firmware"-knap
        Assert.Equal(UpgradeAllReport.Silent, skipped.Single(x => x.WingetId == "fwupd:andet").Group);

        // fra databasen: den blokerede er med, selv om det globale filter holder den ude af tallene
        var report = System.Text.Json.JsonSerializer.Serialize(await UpgradeAllReport.ForMachine(db, m.Id, new Dictionary<string, string>()));
        Assert.Contains("\"Group\":\"blocked\"", report);
    }
}
