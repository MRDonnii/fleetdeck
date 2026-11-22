using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;

namespace Fleetdeck.Tests;

/// <summary>Opdateringsmotoren efter review: genstart-løkker, tilbagerulning, BIOS-genkendelse, planer der lægges sammen.</summary>
public class UpdateEngineReviewTests
{
    private static UpdateItem I(string id, string title, string phase = UpdatePhases.Software) => new() { Id = id, Title = title, Phase = phase };

    [Fact]
    public void PendingRenamesFromAppsCannotLoopRebootsWithoutWindowsUpdateProgress()
    {
        // runde 2 efter en genstart: kun programmer installeret, men der "kræves" stadig genstart → stop, og ingen afsluttende genstart
        var r = new UpdateReport { Round = 2, Installed = [I("7zip", "7-Zip", UpdatePhases.Apps)], RebootPending = true };
        var d = UpdateRounds.Decide(2, 4, 1, [], r);
        Assert.Equal(UpdateNext.Finish, d.Next);
        Assert.StartsWith("genstart løser ikke", d.Why);
        Assert.False(d.FinalReboot);
        // runde 1 må stadig genstarte (Windows ventede allerede på en genstart, da runden begyndte)
        Assert.Equal(UpdateNext.Reboot, UpdateRounds.Decide(1, 4, 0, [], new UpdateReport { Round = 1, RebootPending = true }).Next);
        // fremgang fra Windows Update efter genstarten: en genstart mere er i orden
        var progress = new UpdateReport { Round = 2, Installed = [I("wu:cu", "Kumulativ")], RebootPending = true };
        Assert.Equal(UpdateNext.Reboot, UpdateRounds.Decide(2, 4, 1, [], progress).Next);
    }

    [Fact]
    public void AnUpdateOfferedAgainAfterTheRebootWasRolledBack()
    {
        var before = new List<UpdateItem> { I("wu:cu", "2026-11 Kumulativ (KB1)"), I("wu:def", "Defender (Version 1.1)") };
        var after = new UpdateReport
        {
            Round = 2,
            Installed = [I("wu:cu", "2026-11 Kumulativ (KB1)"), I("wu:def", "Defender (Version 1.2)")],   // Defender: samme id, ny version → ikke tilbagerullet
            RebootPending = true,
        };
        var rolled = UpdateRounds.RolledBack(before, after);
        Assert.Equal(["wu:cu"], rolled);
        var d = UpdateRounds.Decide(2, 4, 1, [], after, rolledBack: rolled);
        Assert.Equal(UpdateNext.Finish, d.Next);
        Assert.False(d.FinalReboot);

        var total = UpdateRounds.Merge(new UpdateReport { Installed = [.. before] }, after);
        UpdateRounds.MarkRolledBack(total, rolled);
        Assert.DoesNotContain(total.Installed, i => i.Id == "wu:cu");
        var f = Assert.Single(total.Failed);
        Assert.Equal(UpdateRounds.RolledBackCode, f.Code);
        Assert.Equal("2026-11 Kumulativ (KB1)", f.Title);
        Assert.Equal(JobStatus.Partial, UpdateStatus.Final(total));
    }

    [Fact]
    public void FailedSearchIsRetriedBeforeTheRunGivesUp()
    {
        var r = new UpdateReport { Round = 1, Installed = [I("7zip", "7-Zip", UpdatePhases.Apps)], Error = "Windows Update-søgningen fejlede" };
        Assert.Equal(UpdateNext.Retry, UpdateRounds.Decide(1, 4, 0, [], r).Next);
        Assert.Equal(UpdateNext.Finish, UpdateRounds.Decide(1, 4, 0, [], r, retries: UpdateRounds.MaxRetries).Next);
        Assert.Equal(UpdateNext.Finish, UpdateRounds.Decide(4, 4, 0, [], r).Next);
    }

    [Fact]
    public void FailuresThatAreNoLongerOfferedAreForgotten()
    {
        var total = new UpdateReport { Failed = [new() { Id = "wu:x", Title = "KB-x", Code = "0x80070643" }, new() { Id = "app", Title = "App", Code = "1" }] };
        var round = new UpdateReport { Round = 2, Offered = ["wu:y"] };
        var merged = UpdateRounds.Merge(total, round);
        Assert.DoesNotContain(merged.Failed, f => f.Id == "wu:x");   // installeret eller erstattet – ikke en fejl
        Assert.Contains(merged.Failed, f => f.Id == "app");          // programmer følges ikke af Windows Update-søgningen
        // søgningen fejlede (Offered = null): intet glemmes
        Assert.Contains(UpdateRounds.Merge(total, new UpdateReport { Round = 2 }).Failed, f => f.Id == "wu:x");
    }

    [Theory]
    [InlineData("Dell Inc. - Firmware - 1.21.0", null, null, true)]                                                  // intet hardware-id → BIOS
    [InlineData("LENOVO - Firmware - 1.46.0", @"UEFI\RES_{11111111-2222-3333-4444-555555555555}", 1, true)]         // ESRT: systemfirmware
    [InlineData("HP Inc. - Firmware - 01.15.00", @"UEFI\RES_{11111111-2222-3333-4444-555555555555}", null, true)]    // ukendt i ESRT
    [InlineData("Lenovo - Firmware - 2.3", @"UEFI\RES_{11111111-2222-3333-4444-555555555555}", 2, false)]            // enhedsfirmware (dock, touchpad)
    [InlineData("HP System Firmware 01.23", @"UEFI\RES_{11111111-2222-3333-4444-555555555555}", 2, true)]            // titlen siger BIOS
    public void WindowsUpdateFirmwareIsBiosUnlessEsrtSaysDevice(string title, string? hwid, int? esrt, bool bios) =>
        Assert.Equal(bios, UpdatePlanner.IsBiosFirmware(title, hwid, _ => esrt));

    [Fact]
    public void FwupdSystemFirmwareIsBiosWhateverItIsCalled()
    {
        Assert.True(UpdatePlanner.FwupdIsBios("uefi_capsule", ["internal", "updatable"], "System Firmware (firmware)"));
        Assert.True(UpdatePlanner.FwupdIsBios("uefi_capsule", [], "ThinkPad X1"));
        Assert.True(UpdatePlanner.FwupdIsBios("flashrom", [], "Coreboot"));
        Assert.False(UpdatePlanner.FwupdIsBios("nvme", ["internal"], "Samsung SSD 980"));
        Assert.False(UpdatePlanner.FwupdIsBios("synaptics_mst", [], "Dock"));
    }

    [Fact]
    public void CatchUpWhileTheUserWorksDefersDriversAndFirmware()
    {
        var items = new[]
        {
            new UpdateCandidate { Id = "drv", Title = "Intel Display", Kind = UpdateKinds.Driver },
            new UpdateCandidate { Id = "fw", Title = "Dock Firmware", Kind = UpdateKinds.Firmware },
            new UpdateCandidate { Id = "cu", Title = "Kumulativ" },
        };
        var plan = UpdatePlanner.Plan(items, new UpdateOptions { Apps = false, UserActive = true });
        Assert.Equal([UpdatePhases.Software], plan.Phases.Select(p => p.Phase));
        Assert.All(plan.Deferred, d => { Assert.Equal(DeferReasons.Idle, d.Reason); Assert.True(d.Blocking); });
        Assert.Equal(2, plan.Deferred.Count);
    }

    [Fact]
    public void PlansForTheSameMachineAreMergedNotLost()
    {
        var upgrade = new MaintPlan(false, ["upgrade"], "none", false);
        var wu = new MaintPlan(false, ["windows_update"], "reboot", false);
        var maint = new MaintPlan(true, ["windows_update", "upgrade", "disk_cleanup"], "shutdown_if_woken", true);
        var m = MaintenanceService.MergePlans(MaintenanceService.MergePlans(upgrade, wu), maint);
        Assert.Equal(["upgrade", "windows_update", "disk_cleanup"], m.Tasks);
        Assert.Equal("shutdown_if_woken", m.After);
        Assert.True(m.Wake);
        Assert.True(m.Report);
    }

    [Fact]
    public void RoundParamsKeepSchedulesAwayFromFirmwareAndWaitForIdleOnCatchUp()
    {
        var now = DateTime.UtcNow;
        var profile = new Profile { UpdFirmware = true };
        var both = new MaintPlan(false, ["windows_update", "upgrade"], "reboot", false);
        // planlagt programopdatering på Linux: systempakker og snap/flatpak som før – aldrig firmware
        var lin = MaintenanceService.RoundParams(new MaintenanceRun { Kind = "schedule", NotBefore = now }, new MaintPlan(false, ["upgrade"], "none", false), profile, Platforms.Linux, 1, now);
        Assert.True(lin.UpdSystem);
        Assert.False(lin.IncludeFirmware);
        // vedligeholdelse med firmware slået til i profilen
        Assert.True(MaintenanceService.RoundParams(new MaintenanceRun { Kind = "maintenance", NotBefore = now }, both, profile, Platforms.Windows, 1, now).IncludeFirmware);
        // firmware er fra som standard (og uden profil)
        Assert.False(MaintenanceService.RoundParams(new MaintenanceRun { Kind = "maintenance", NotBefore = now }, both, new Profile(), Platforms.Windows, 1, now).IncludeFirmware);
        Assert.False(MaintenanceService.RoundParams(new MaintenanceRun { Kind = "manual", NotBefore = now }, both, null, Platforms.Windows, 1, now).IncludeFirmware);
        // indhentning: drivere og firmware venter, mens pc'en bruges; om natten ikke
        Assert.Equal(15, MaintenanceService.RoundParams(new MaintenanceRun { Kind = "maintenance", NotBefore = now.AddHours(-5) }, both, profile, Platforms.Windows, 1, now).UpdIdleMin);
        Assert.Null(MaintenanceService.RoundParams(new MaintenanceRun { Kind = "maintenance", NotBefore = now }, both, profile, Platforms.Windows, 1, now).UpdIdleMin);
    }

    [Fact]
    public void LastKnownUsesInventoryWhenHealthWasReset()
    {
        var inv = DateTime.UtcNow.AddHours(-1);
        Assert.Equal(inv, MaintenanceService.LastKnown(new Machine { LastHealth = null, LastInventory = inv }));
        Assert.False(UpdateWake.Stale(MaintenanceService.LastKnown(new Machine { LastInventory = DateTime.UtcNow }), DateTime.UtcNow));
    }
}
