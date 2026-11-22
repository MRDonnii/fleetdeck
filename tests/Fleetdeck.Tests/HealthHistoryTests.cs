using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;
using Xunit;

namespace Fleetdeck.Tests;

/// <summary>Diskhelbred: smartctl -j (ATA og NVMe), Windows' Storage-WMI og diskutil – opdigtede eksempler i samme form som de rigtige værktøjer.</summary>
public class DiskSmartParseTests
{
    private const string AtaSsd = """
    {
      "smartctl": { "version": [7, 4], "exit_status": 0 },
      "device": { "name": "/dev/sda", "type": "sat", "protocol": "ATA" },
      "model_name": "Example SSD 860 500GB", "serial_number": "S0EXAMPLE0001",
      "user_capacity": { "blocks": 976773168, "bytes": 500107862016 },
      "rotation_rate": 0,
      "smart_status": { "passed": true },
      "ata_smart_attributes": { "table": [
        { "id": 5, "name": "Reallocated_Sector_Ct", "value": 100, "worst": 100, "thresh": 10, "raw": { "value": 0, "string": "0" } },
        { "id": 9, "name": "Power_On_Hours", "value": 95, "worst": 95, "thresh": 0, "raw": { "value": 21034, "string": "21034" } },
        { "id": 177, "name": "Wear_Leveling_Count", "value": 91, "worst": 91, "thresh": 0, "raw": { "value": 97, "string": "97" } },
        { "id": 194, "name": "Temperature_Celsius", "value": 66, "worst": 50, "thresh": 0, "raw": { "value": 158914887714, "string": "34 (Min/Max 18/50)" } },
        { "id": 197, "name": "Current_Pending_Sector", "value": 100, "worst": 100, "thresh": 0, "raw": { "value": 0, "string": "0" } }
      ] },
      "power_on_time": { "hours": 21034 },
      "temperature": { "current": 34 }
    }
    """;

    private const string AtaHddBad = """
    {
      "smartctl": { "exit_status": 72 },
      "device": { "name": "/dev/sdb", "type": "sat", "protocol": "ATA" },
      "model_name": "Example HDD 4TB", "serial_number": "WD-EXAMPLE42",
      "user_capacity": { "bytes": 4000787030016 },
      "rotation_rate": 5400,
      "smart_status": { "passed": false },
      "ata_smart_attributes": { "table": [
        { "id": 5, "name": "Reallocated_Sector_Ct", "value": 180, "worst": 180, "thresh": 140, "raw": { "value": 112, "string": "112" } },
        { "id": 197, "name": "Current_Pending_Sector", "value": 200, "worst": 200, "thresh": 0, "raw": { "value": 8, "string": "8" } },
        { "id": 198, "name": "Offline_Uncorrectable", "value": 200, "worst": 200, "thresh": 0, "raw": { "value": 2, "string": "2" } },
        { "id": 177, "name": "Not_A_Wear_On_HDD", "value": 50, "worst": 50, "thresh": 0, "raw": { "value": 0, "string": "0" } }
      ] },
      "power_on_time": { "hours": 48211 },
      "temperature": { "current": 41 }
    }
    """;

    private const string Nvme = """
    {
      "smartctl": { "exit_status": 0 },
      "device": { "name": "/dev/nvme0", "type": "nvme", "protocol": "NVMe" },
      "model_name": "Example NVMe 1TB", "serial_number": "NV0000EXAMPLE",
      "user_capacity": { "bytes": 1000204886016 },
      "smart_status": { "passed": true, "nvme": { "value": 0 } },
      "nvme_smart_health_information_log": {
        "critical_warning": 0, "temperature": 38, "available_spare": 100, "available_spare_threshold": 10,
        "percentage_used": 7, "power_on_hours": 5120, "unsafe_shutdowns": 31, "media_errors": 0
      },
      "temperature": { "current": 38 },
      "power_on_time": { "hours": 5120 }
    }
    """;

    private const string Standby = """
    {
      "smartctl": { "exit_status": 2, "messages": [ { "string": "Device is in STANDBY mode, exit(2)", "severity": "information" } ] },
      "device": { "name": "/dev/sdc", "type": "sat", "protocol": "ATA" }
    }
    """;

    [Fact] public void Ata_ssd_gives_model_kind_life_and_counters_without_the_serial()
    {
        var d = DiskSmart.FromSmartctl(AtaSsd, new DiskHealth { Name = "sda", Media = "SSD", Bus = "SATA" });
        Assert.Equal("Example SSD 860 500GB", d.Name);
        Assert.Equal(DiskKinds.Ssd, d.Kind);
        Assert.Equal("Healthy", d.Health);
        Assert.Equal(34, d.Temp);
        Assert.Equal(21034, d.PowerOnHours);
        Assert.Equal(91, d.LifeLeft);
        Assert.Equal(9, d.Wear);
        Assert.Equal(0, d.Reallocated);
        Assert.Equal(0, d.Pending);
        Assert.Equal(500, d.SizeGb);
        Assert.Equal("SATA", d.Bus);
        Assert.Equal(12, d.Id!.Length);
        Assert.DoesNotContain("EXAMPLE", d.Id, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DiskSmart.IdOf("S0EXAMPLE0001"), d.Id);
        Assert.Equal(DiskStates.Ok, DiskSmart.Classify(d, 70, DateTime.UtcNow));
    }

    [Fact] public void Failing_hdd_counts_pending_and_uncorrectable_and_ignores_ssd_life_attributes()
    {
        var d = DiskSmart.FromSmartctl(AtaHddBad);
        Assert.Equal(DiskKinds.Hdd, d.Kind);
        Assert.Equal("Unhealthy", d.Health);
        Assert.Equal(112, d.Reallocated);
        Assert.Equal(10, d.Pending);
        Assert.Null(d.LifeLeft);
        Assert.Null(d.Wear);
        Assert.Equal(DiskStates.Failing, DiskSmart.Classify(d, 70, DateTime.UtcNow));
    }

    [Fact] public void Nvme_log_gives_percentage_used_spare_and_media_errors()
    {
        var d = DiskSmart.FromSmartctl(Nvme, new DiskHealth { Name = "nvme0n1", Media = "SSD" });
        Assert.Equal(DiskKinds.Nvme, d.Kind);
        Assert.Equal("NVMe", d.Bus);
        Assert.Equal(7, d.Wear);
        Assert.Equal(93, d.LifeLeft);
        Assert.Equal(0, d.MediaErrors);
        Assert.Equal(0, d.CriticalWarning);
        Assert.Equal(100, d.Spare);
        Assert.Equal(5120, d.PowerOnHours);
    }

    [Fact] public void Sleeping_disk_is_marked_standby_and_keeps_what_the_agent_knew()
    {
        var d = DiskSmart.FromSmartctl(Standby, new DiskHealth { Name = "Example HDD 8TB", Media = "HDD", SizeGb = 8001 });
        Assert.True(d.Standby);
        Assert.Equal("Example HDD 8TB", d.Name);
        Assert.Null(d.Health);
        Assert.Equal(8001, d.SizeGb);
    }

    [Fact] public void Broken_json_returns_the_fallback()
    {
        var f = new DiskHealth { Name = "sdz" };
        Assert.Same(f, DiskSmart.FromSmartctl("ikke json", f));
        Assert.Null(DiskSmart.IdOf("00000000"));
        Assert.Null(DiskSmart.IdOf("  "));
    }

    [Fact] public void Windows_nvme_with_reliability_counters()
    {
        var rel = new Dictionary<string, object?> { ["Wear"] = (byte)4, ["Temperature"] = (byte)41, ["TemperatureMax"] = (byte)70, ["ReadErrorsUncorrected"] = 0UL, ["WriteErrorsUncorrected"] = 1UL, ["PowerOnHours"] = 3200UL };
        var d = DiskSmart.FromWindows(new WinDiskRaw("Example NVMe 1TB", "Example NVMe 1TB", "  NV0000EXAMPLE ", 4, 17, 1_000_204_886_016, 0, [2], rel));
        Assert.Equal(DiskKinds.Nvme, d.Kind);
        Assert.Equal("Healthy", d.Health);
        Assert.Equal("OK", d.Status);
        Assert.Equal(4, d.Wear);
        Assert.Equal(96, d.LifeLeft);
        Assert.Equal(41, d.Temp);
        Assert.Equal(70, d.TempMax);
        Assert.Equal(1, d.MediaErrors);
        Assert.Equal(3200, d.PowerOnHours);
        Assert.Equal(DiskSmart.IdOf("NV0000EXAMPLE"), d.Id);
    }

    [Fact] public void Windows_hdd_without_wear_and_predictive_failure_is_a_warning()
    {
        var rel = new Dictionary<string, object?> { ["Wear"] = (byte)0, ["Temperature"] = (byte)0, ["PowerOnHours"] = 900UL };
        var d = DiskSmart.FromWindows(new WinDiskRaw("Example HDD", null, null, 3, 11, 2e12, 0, [2, 5], rel));
        Assert.Equal(DiskKinds.Hdd, d.Kind);
        Assert.Null(d.Wear);                 // 0 % slid på en HDD = ukendt
        Assert.Null(d.Temp);                 // 0 °C = ukendt
        Assert.Equal("Warning", d.Health);   // "Predictive Failure"
        Assert.Equal("OK, Predictive Failure", d.Status);
        Assert.Null(d.Id);
        Assert.Equal(DiskStates.Warning, DiskSmart.Classify(d, 70, DateTime.UtcNow));
    }

    [Fact] public void Mac_diskutil_apple_fabric_is_nvme_and_verified_is_healthy()
    {
        const string info = """
           Device Identifier:         disk0
           Device / Media Name:       APPLE SSD AP0512Z
           Protocol:                  Apple Fabric
           Disk Size:                 500.3 GB (500277790720 Bytes) (exactly 977105060 512-Byte-Units)
           Solid State:               Yes
           SMART Status:              Verified
        """;
        var d = DiskSmart.FromDiskutil(info)!;
        Assert.Equal("APPLE SSD AP0512Z", d.Name);
        Assert.Equal(DiskKinds.Nvme, d.Kind);
        Assert.Equal("Healthy", d.Health);
        Assert.Equal(500.3, d.SizeGb);
        Assert.Null(DiskSmart.FromDiskutil(""));
        var failing = DiskSmart.FromDiskutil("   SMART Status:              Failing\n   Solid State:               No\n")!;
        Assert.Equal("Unhealthy", failing.Health);
        Assert.Equal(DiskKinds.Hdd, failing.Kind);
    }

    [Fact] public void Track_keeps_numbers_for_a_sleeping_disk_and_marks_rising_reallocated_sectors()
    {
        var t0 = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        var before = new List<DiskHealth> { new() { Name = "Example HDD", Id = "abc123abc123", Health = "Healthy", Reallocated = 4, Pending = 0, Temp = 35, PowerOnHours = 100 } };
        var asleep = new List<DiskHealth> { new() { Name = "Example HDD", Id = "abc123abc123", Standby = true } };
        DiskSmart.Track(asleep, before, t0);
        Assert.Equal("Healthy", asleep[0].Health);
        Assert.Equal(4, asleep[0].Reallocated);
        Assert.Null(asleep[0].Temp);                 // temperaturen fra før er ikke "nu"
        Assert.Equal(4, asleep[0].ReallocatedFirst);
        Assert.Null(asleep[0].ReallocatedRoseAt);
        Assert.Equal(DiskStates.Ok, asleep[0].State);

        var worse = new List<DiskHealth> { new() { Name = "Example HDD", Id = "abc123abc123", Health = "Healthy", Reallocated = 9, Pending = 0 } };
        DiskSmart.Track(worse, asleep, t0.AddHours(1));
        Assert.Equal(t0.AddHours(1), worse[0].ReallocatedRoseAt);
        Assert.Equal(4, worse[0].ReallocatedFirst);
        Assert.Equal(DiskStates.Warning, worse[0].State);

        // uændret i 8 dage: ikke længere "stigende"
        var later = new List<DiskHealth> { new() { Name = "Example HDD", Id = "abc123abc123", Health = "Healthy", Reallocated = 9, Pending = 0 } };
        DiskSmart.Track(later, worse, t0.AddDays(8));
        Assert.Equal(t0.AddHours(1), later[0].ReallocatedRoseAt);
        Assert.Equal(DiskStates.Ok, later[0].State);

        // ny disk (andet id) med samme navn starter forfra
        var swapped = new List<DiskHealth> { new() { Name = "Example HDD", Id = "fff000fff000", Health = "Healthy", Reallocated = 0 } };
        DiskSmart.Track(swapped, later, t0.AddDays(9));
        Assert.Equal(0, swapped[0].ReallocatedFirst);
        Assert.Null(swapped[0].ReallocatedRoseAt);
    }

    [Fact] public void Track_never_borrows_numbers_between_disks_of_the_same_model()
    {
        // array med to ens diske uden id: den sovende må ikke få den anden disks ventende sektorer (falsk advarsel)
        var t0 = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        var before = new List<DiskHealth>
        {
            new() { Name = "Example HDD 8TB", Health = "Healthy", Pending = 8, Reallocated = 3 },
            new() { Name = "Example HDD 8TB", Health = "Healthy", Pending = 0, Reallocated = 0 },
        };
        var now = new List<DiskHealth> { new() { Name = "Example HDD 8TB", Standby = true }, new() { Name = "Example HDD 8TB", Standby = true } };
        DiskSmart.Track(now, before, t0);
        Assert.All(now, d => { Assert.Null(d.Pending); Assert.Null(d.State); });

        // med serienummer (lsblk SERIAL) findes hver disk igen
        var withId = new List<DiskHealth> { new() { Name = "Example HDD 8TB", Id = "aaa111aaa111", Pending = 0 }, new() { Name = "Example HDD 8TB", Id = "bbb222bbb222", Pending = 8 } };
        var asleep = new List<DiskHealth> { new() { Name = "Example HDD 8TB", Id = "aaa111aaa111", Standby = true }, new() { Name = "Example HDD 8TB", Id = "bbb222bbb222", Standby = true } };
        DiskSmart.Track(asleep, withId, t0.AddHours(1));
        Assert.Equal(0, asleep[0].Pending);
        Assert.Equal(8, asleep[1].Pending);
    }
}

/// <summary>Helbredsreglerne for diske og batterier: konservative, så en rask maskine aldrig får alarmer.</summary>
public class DiskBatteryRuleTests
{
    private static readonly DateTime Now = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

    [Fact] public void Healthy_disk_and_unknown_health_give_nothing()
    {
        Assert.Empty(HealthService.DiskFindings([new() { Name = "A", Health = "Healthy", Wear = 10, Temp = 40, Reallocated = 3, ReallocatedFirst = 3 }], 70, Now));
        Assert.Empty(HealthService.DiskFindings([new() { Name = "B", Health = "Unknown" }], 70, Now));
        Assert.Empty(HealthService.DiskFindings([new() { Name = "C" }], 70, Now));
    }

    [Fact] public void Smart_failure_and_failing_nvme_are_critical()
    {
        var f = HealthService.DiskFindings([new() { Name = "A", Health = "Unhealthy", Status = "SMART-test fejlet" }], 70, Now).Single();
        Assert.Equal(("smart:A", "critical"), (f.Key, f.Severity));
        var n = HealthService.DiskFindings([new() { Name = "N", Health = "Healthy", CriticalWarning = 0x08 }], 70, Now).Single();
        Assert.Equal("critical", n.Severity);
        Assert.Contains("skrivebeskyttet", n.Detail);
        var spare = HealthService.DiskFindings([new() { Name = "N", Health = "Healthy", CriticalWarning = 0x01 }], 70, Now).Single();
        Assert.Equal("warning", spare.Severity);
        Assert.Contains("reserveblokke", spare.Detail);
        Assert.Equal("warning", HealthService.DiskFindings([new() { Name = "W", Health = "Warning" }], 70, Now).Single().Severity);
    }

    [Fact] public void Pending_and_rising_sectors_wear_and_heat_are_warnings()
    {
        var list = HealthService.DiskFindings([new()
        {
            Name = "H", Health = "Healthy", Pending = 2, Reallocated = 12, ReallocatedFirst = 4, ReallocatedRoseAt = Now.AddDays(-1),
            MediaErrors = 3, MediaErrorsRoseAt = Now.AddDays(-10), Wear = 85, Temp = 72,
        }], 70, Now).ToList();
        Assert.Equal(["sectors:H", "wear:H", "dtemp:H"], list.Select(x => x.Key));
        Assert.All(list, x => Assert.Equal("warning", x.Severity));
        Assert.Equal("2 ulæselige sektorer venter på at blive flyttet, 12 reallokerede sektorer (var 4)", list[0].Detail);
        // gamle reallokerede sektorer, der ikke stiger, giver ingen advarsel
        Assert.Empty(HealthService.DiskFindings([new() { Name = "Old", Health = "Healthy", Reallocated = 40, ReallocatedFirst = 40, ReallocatedRoseAt = Now.AddDays(-30) }], 70, Now));
    }

    [Fact] public void Battery_below_limit_or_service_recommended_warns()
    {
        Assert.Empty(HealthService.BatteryFindings(null, 60));
        Assert.Empty(HealthService.BatteryFindings([new() { HealthPct = 81, Cycles = 300 }], 60));
        var low = HealthService.BatteryFindings([new() { HealthPct = 54, Cycles = 812 }], 60).Single();
        Assert.Equal(("battery:0", "warning"), (low.Key, low.Severity));
        Assert.Equal("Batteriet kan kun rumme 54 % af det, det kunne som nyt (812 opladninger)", low.Detail);
        var mac = HealthService.BatteryFindings([new() { HealthPct = 82, Condition = "Service Recommended" }], 60).Single();
        Assert.Contains("service", mac.Detail);
    }
}

/// <summary>Batterier: Linux sysfs, Mac ioreg/system_profiler og Windows WMI.</summary>
public class BatteryParseTests
{
    [Fact] public void Linux_energy_values_in_micro_watt_hours()
    {
        var b = BatteryParse.FromSysfs("BAT0", new Dictionary<string, string>
        {
            ["type"] = "Battery\n", ["status"] = "Discharging\n", ["capacity"] = "77\n", ["cycle_count"] = "412\n",
            ["energy_full_design"] = "57000000\n", ["energy_full"] = "45030000\n", ["manufacturer"] = "Example\n", ["model_name"] = "EX-57\n", ["technology"] = "Li-poly\n",
        })!;
        Assert.Equal("EX-57", b.Name);
        Assert.Equal(57000, b.Design);
        Assert.Equal(45030, b.Full);
        Assert.Equal("mWh", b.Unit);
        Assert.Equal(79, b.HealthPct);
        Assert.Equal(412, b.Cycles);
        Assert.Equal("discharging", b.Status);
        Assert.Equal(77, b.ChargePct);
    }

    [Fact] public void Linux_charge_values_unknown_cycles_and_peripherals()
    {
        var b = BatteryParse.FromSysfs("BAT1", new Dictionary<string, string>
        { ["type"] = "Battery", ["status"] = "Not charging", ["charge_full_design"] = "4400000", ["charge_full"] = "4100000", ["cycle_count"] = "0" })!;
        Assert.Equal("mAh", b.Unit);
        Assert.Equal(93, b.HealthPct);
        Assert.Null(b.Cycles);
        Assert.Equal("ac", b.Status);
        Assert.Null(BatteryParse.FromSysfs("hidpp_battery_0", new Dictionary<string, string> { ["type"] = "Battery", ["scope"] = "Device" }));
        Assert.Null(BatteryParse.FromSysfs("AC", new Dictionary<string, string> { ["type"] = "Mains" }));
    }

    [Fact] public void Mac_apple_silicon_uses_raw_capacity_and_apples_own_maximum()
    {
        const string ioreg = """
        +-o AppleSmartBattery  <class AppleSmartBattery, id 0x100000001, registered, matched, active, busy 0 (0 ms), retain 7>
            {
              "DesignCapacity" = 4382
              "AppleRawMaxCapacity" = 3796
              "MaxCapacity" = 100
              "CurrentCapacity" = 64
              "CycleCount" = 238
              "IsCharging" = No
              "ExternalConnected" = No
              "FullyCharged" = No
              "DeviceName" = "bq40z651"
            }
        """;
        var b = BatteryParse.FromMac(ioreg)!;
        Assert.Equal(87, b.HealthPct);
        Assert.Equal(238, b.Cycles);
        Assert.Equal(64, b.ChargePct);
        Assert.Equal("discharging", b.Status);
        var withProfiler = BatteryParse.FromMac(ioreg, "      Health Information:\n          Cycle Count: 238\n          Condition: Normal\n          Maximum Capacity: 88%\n")!;
        Assert.Equal(88, withProfiler.HealthPct);
        Assert.Equal("Normal", withProfiler.Condition);
        Assert.Null(BatteryParse.FromMac(""));
    }

    [Fact] public void Mac_intel_gives_max_capacity_in_mah()
    {
        var b = BatteryParse.FromMac("\"DesignCapacity\" = 6669\n\"MaxCapacity\" = 5002\n\"CurrentCapacity\" = 2501\n\"CycleCount\" = 655\n\"ExternalConnected\" = Yes\n\"IsCharging\" = Yes\n")!;
        Assert.Equal(75, b.HealthPct);
        Assert.Equal(50, b.ChargePct);
        Assert.Equal("charging", b.Status);
    }

    [Fact] public void Windows_wmi_values()
    {
        var b = BatteryParse.FromWindows("DELL EXAMPLE", 54000, 32400, 0, 2, 100, 6);
        Assert.Equal(60, b.HealthPct);
        Assert.Null(b.Cycles);   // 0 = ukendt på Windows
        Assert.Equal("ac", b.Status);
        Assert.Equal("Li-ion", b.Chemistry);
        Assert.Null(BatteryParse.FromWindows(null, 0, 32400, 120, 1, 50, null).HealthPct);
    }
}

/// <summary>Ændringsloggen: forskellen mellem to inventarer (programmer og drivere).</summary>
public class ChangeDiffTests
{
    private static List<ChangeItem> L(params (string Name, string? Version)[] items) => items.Select(i => new ChangeItem(i.Name, i.Version)).ToList();

    [Fact] public void First_list_or_empty_new_list_gives_no_changes()
    {
        Assert.Null(ChangeDiff.Diff([], L(("A", "1"))));
        Assert.Null(ChangeDiff.Diff(L(("A", "1")), []));
    }

    [Fact] public void Installed_removed_and_updated()
    {
        var d = ChangeDiff.Diff(L(("Firefox", "120.0"), ("7-Zip", "23.01"), ("Old Tool", "1.0")), L(("firefox", "121.0"), ("7-Zip", "23.01"), ("VLC", "3.0.20")))!;
        Assert.Equal(3, d.Count);
        Assert.Contains(d, c => c is { Action: ChangeActions.Installed, Name: "VLC", To: "3.0.20" });
        Assert.Contains(d, c => c is { Action: ChangeActions.Updated, Name: "firefox", From: "120.0", To: "121.0" });
        Assert.Contains(d, c => c is { Action: ChangeActions.Removed, Name: "Old Tool", From: "1.0" });
    }

    [Fact] public void Several_versions_of_the_same_program_and_missing_versions()
    {
        // 32- og 64-bit af samme program: kun den ene opdateres
        var d = ChangeDiff.Diff(L(("Runtime", "6.0.1"), ("Runtime", "8.0.0")), L(("Runtime", "6.0.1"), ("Runtime", "8.0.2")))!;
        Assert.Equal(new ChangeEntry(ChangeActions.Updated, "Runtime", "8.0.0", "8.0.2", null), d.Single());
        // version mangler på den ene side: indsamlingen, ikke programmet, har ændret sig
        Assert.Empty(ChangeDiff.Diff(L(("Tool", null)), L(("Tool", "2.0")))!);
        Assert.Empty(ChangeDiff.Diff(L(("Tool", "1.0"), ("Tool", "1.0")), L(("Tool", "1.0")))!);
    }

    [Fact] public void Mass_removal_looks_like_a_collection_failure_and_is_skipped()
    {
        var before = Enumerable.Range(0, 40).Select(i => ($"Program {i}", (string?)"1.0")).ToArray();
        Assert.Null(ChangeDiff.Diff(L(before), L(before.Take(10).ToArray())));
        Assert.Equal(5, ChangeDiff.Diff(L(before), L(before.Skip(5).ToArray()))!.Count);
    }

    [Fact] public void Capped_per_run()
    {
        var after = Enumerable.Range(0, 500).Select(i => ($"Pakke {i:000}", (string?)"1")).ToArray();
        Assert.Equal(ChangeDiff.MaxPerRun, ChangeDiff.Diff(L(("Base", "1")), L([.. after, ("Base", "1")]))!.Count);
    }

    [Fact] public void Driver_list_drops_microsoft_inbox_drivers_and_duplicates()
    {
        var clean = DriverList.Clean([
            new() { Name = "Example GPU", Version = "31.0.1", Provider = "Example Graphics", Class = "DISPLAY" },
            new() { Name = "Example GPU", Version = "31.0.1", Provider = "Example Graphics", Class = "DISPLAY" },
            new() { Name = "Standard SATA AHCI Controller", Version = "10.0.1", Provider = "Microsoft" },
            new() { Name = "", Version = "1" },
            new() { Name = "Example LAN", Version = "12.1", Provider = "Example Net", Class = "NET" },
        ]);
        Assert.Equal(["Example GPU", "Example LAN"], clean.Select(d => d.Name));
    }
}

/// <summary>Ændringslog og tidslinje mod en rigtig SQLite-fil.</summary>
public class HealthHistoryStoreTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"fd-history-{Guid.NewGuid():N}.db");
    private AppDb Db() => new(new DbContextOptionsBuilder<AppDb>().UseSqlite($"Data Source={_file}").Options);
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(_file); }

    [Fact] public async Task Drivers_first_inventory_is_the_baseline_then_changes_are_logged()
    {
        await using (var db = Db()) { await db.Database.EnsureCreatedAsync(); db.Machines.Add(new Machine { Id = 5, Name = "PC-1" }); await db.SaveChangesAsync(); }
        var t0 = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);
        await using (var db = Db())
        {
            Assert.Equal(0, await ChangeLogStore.Drivers(db, 5, [new() { Name = "Example GPU", Version = "31.0.1" }, new() { Name = "Example LAN", Version = "12.1" }], t0));
            await db.SaveChangesAsync();
            Assert.Equal(0, await ChangeLogStore.Drivers(db, 5, null, t0));   // ældre agent: intet sker
        }
        await using (var db = Db())
        {
            Assert.Equal(2, await ChangeLogStore.Drivers(db, 5, [new() { Name = "Example GPU", Version = "32.0.0" }, new() { Name = "Example LAN", Version = "12.1" }, new() { Name = "Example Audio", Version = "6.0" }], t0.AddDays(1)));
            await db.SaveChangesAsync();
        }
        await using (var db = Db())
        {
            Assert.Equal(3, await db.Drivers.CountAsync());
            var rows = await db.ChangeLog.OrderBy(x => x.Name).ToListAsync();
            Assert.Equal([("Example Audio", ChangeActions.Installed), ("Example GPU", ChangeActions.Updated)], rows.Select(r => (r.Name, r.Action)));
            Assert.All(rows, r => Assert.Equal(ChangeKinds.Driver, r.Kind));
        }
    }

    [Fact] public async Task Prune_keeps_180_days_and_caps_per_machine()
    {
        await using (var db = Db()) { await db.Database.EnsureCreatedAsync(); db.Machines.Add(new Machine { Id = 1, Name = "PC-1" }); await db.SaveChangesAsync(); }
        var now = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
        await using (var db = Db())
        {
            db.ChangeLog.Add(new ChangeLogRow { MachineId = 1, Time = now.AddDays(-200), Kind = "program", Action = "installed", Name = "Gammel" });
            db.ChangeLog.AddRange(Enumerable.Range(0, ChangeLogStore.MaxPerMachine + 10).Select(i => new ChangeLogRow { MachineId = 1, Time = now.AddMinutes(-i), Kind = "program", Action = "updated", Name = $"P{i}" }));
            db.Timeline.Add(new TimelineMark { MachineId = 1, Kind = TimelineKinds.Boot, Start = now.AddDays(-40) });
            db.Timeline.Add(new TimelineMark { MachineId = 1, Kind = TimelineKinds.Boot, Start = now.AddDays(-2) });
            await db.SaveChangesAsync();
            await ChangeLogStore.Prune(db, now);
            await Timeline.Prune(db, now);
        }
        await using (var db = Db())
        {
            Assert.Equal(ChangeLogStore.MaxPerMachine, await db.ChangeLog.CountAsync());
            Assert.False(await db.ChangeLog.AnyAsync(x => x.Name == "Gammel"));
            Assert.Equal(1, await db.Timeline.CountAsync());
        }
    }

    [Fact] public async Task Checkin_records_offline_periods_and_boots_and_the_view_combines_everything()
    {
        var now = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
        await using (var db = Db()) { await db.Database.EnsureCreatedAsync(); db.Machines.Add(new Machine { Id = 2, Name = "PC-2", Enrolled = now.AddDays(-60) }); await db.SaveChangesAsync(); }
        await using (var db = Db())
        {
            var m = await db.Machines.SingleAsync();
            Timeline.OnCheckin(db, m, null, null, now.AddDays(-4), now.AddDays(-3));          // første gang: tidslinjen begynder
            Assert.Equal(now.AddDays(-3), m.TimelineSince);
            Timeline.OnCheckin(db, m, now.AddDays(-2).AddMinutes(-5), now.AddDays(-4), now.AddDays(-4).AddSeconds(20), now.AddDays(-2));   // 5 min. væk, samme opstart: intet
            Timeline.OnCheckin(db, m, now.AddDays(-1).AddHours(-3), now.AddDays(-4), now.AddDays(-1).AddMinutes(-2), now.AddDays(-1));   // slukket i 3 timer og startet igen
            m.LastSeen = now.AddMinutes(-1);
            db.ChangeLog.AddRange(
                new ChangeLogRow { MachineId = 2, Time = now.AddHours(-20), Kind = "program", Action = "installed", Name = "VLC" },
                new ChangeLogRow { MachineId = 2, Time = now.AddHours(-20), Kind = "program", Action = "updated", Name = "Firefox", From = "120", To = "121" });
            db.SysLog.AddRange(
                new SysLogRow { MachineId = 2, Time = now.AddHours(-10), Level = "critical", Log = "System", Source = "Microsoft-Windows-Kernel-Power", EventId = 41, Message = "Systemet blev genstartet uden at lukke ned.", Kind = SysLogKinds.UnexpectedShutdown, GroupKey = "k", Hash = "h1" },
                new SysLogRow { MachineId = 2, Time = now.AddHours(-10).AddMinutes(1), Level = "critical", Log = "System", Source = "Microsoft-Windows-Kernel-Power", EventId = 41, Message = "Igen.", Kind = SysLogKinds.UnexpectedShutdown, GroupKey = "k", Hash = "h2" },
                new SysLogRow { MachineId = 2, Time = now.AddHours(-9), Level = "error", Log = "System", Source = "DCOM", EventId = 10016, Message = "Støj", Kind = SysLogKinds.DcomNoise, GroupKey = "d", Hash = "h3" });
            db.Jobs.Add(new Job { MachineId = 2, Type = JobTypes.Upgrade, Title = "Opdatér alle programmer", Status = JobStatus.Success, Finished = now.AddHours(-30) });
            await db.SaveChangesAsync();
        }
        await using (var db = Db())
        {
            var marks = await db.Timeline.OrderBy(t => t.Start).ToListAsync();
            Assert.Equal([TimelineKinds.Offline, TimelineKinds.Boot], marks.Select(t => t.Kind));
            var m = await db.Machines.SingleAsync();
            var v = await Timeline.For(db, m, 7, now);
            Assert.Equal([TimelineStates.Unknown, TimelineStates.Online, TimelineStates.Offline, TimelineStates.Online], v.Segments.Select(s => s.State));
            Assert.Equal(now.AddDays(-3), v.Segments[1].From);
            Assert.Equal(1, v.Boots);
            Assert.Equal(1, v.Updates);
            Assert.Equal(2, v.Events);   // DCOM-støj tæller ikke
            Assert.Equal(2, v.Changes);
            var ev = v.Markers.Single(x => x.Kind == TimelineKinds.Event);
            Assert.Equal((2, "critical", SysLogKinds.UnexpectedShutdown), (ev.Count, ev.Severity, ev.Ref));
            var ch = v.Markers.Single(x => x.Kind == TimelineKinds.Change);
            Assert.Equal("Programmer: 1 installeret, 1 opdateret", ch.Title);
            Assert.Equal("+ VLC\n↑ Firefox", ch.Detail);
            Assert.Equal(95.8, v.OnlinePct);
        }
    }
}

/// <summary>Statusbåndet: ukendt, online og offline.</summary>
public class TimelineSegmentTests
{
    private static readonly DateTime T = new(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);

    [Fact] public void Unknown_before_tracking_and_offline_now_when_not_seen()
    {
        var s = Timeline.Segments(T, T.AddHours(24), T.AddHours(6), T.AddHours(20), [(T.AddHours(10), T.AddHours(12))]);
        Assert.Equal([
            new TimelineSegment(T, T.AddHours(6), TimelineStates.Unknown),
            new TimelineSegment(T.AddHours(6), T.AddHours(10), TimelineStates.Online),
            new TimelineSegment(T.AddHours(10), T.AddHours(12), TimelineStates.Offline),
            new TimelineSegment(T.AddHours(12), T.AddHours(20), TimelineStates.Online),
            new TimelineSegment(T.AddHours(20), T.AddHours(24), TimelineStates.Offline),
        ], s);
        Assert.Equal(66.7, Timeline.OnlinePct(s));
    }

    [Fact] public void Overlapping_and_out_of_range_periods_are_merged_and_clipped()
    {
        var s = Timeline.Segments(T, T.AddHours(10), T.AddDays(-5), T.AddHours(10), [(T.AddHours(-3), T.AddHours(1)), (T.AddHours(4), T.AddHours(6)), (T.AddHours(5), T.AddHours(7))]);
        Assert.Equal([
            new TimelineSegment(T, T.AddHours(1), TimelineStates.Offline),
            new TimelineSegment(T.AddHours(1), T.AddHours(4), TimelineStates.Online),
            new TimelineSegment(T.AddHours(4), T.AddHours(7), TimelineStates.Offline),
            new TimelineSegment(T.AddHours(7), T.AddHours(10), TimelineStates.Online),
        ], s);
    }

    [Fact] public void Nothing_known_gives_only_unknown_and_no_percentage()
    {
        var s = Timeline.Segments(T, T.AddDays(1), null, T.AddDays(1), []);
        Assert.Equal([new TimelineSegment(T, T.AddDays(1), TimelineStates.Unknown)], s);
        Assert.Null(Timeline.OnlinePct(s));
        Assert.Empty(Timeline.Segments(T, T, null, null, []));
    }

    [Fact] public void Boot_time_falls_back_to_uptime()
    {
        Assert.Equal(T.AddHours(-2), Timeline.BootOf(new LiveFacts { UptimeH = 2 }, T));
        Assert.Equal(T.AddHours(-1), Timeline.BootOf(new LiveFacts { BootAt = T.AddHours(-1), UptimeH = 2 }, T));
        Assert.Null(Timeline.BootOf(null, T));
    }
}
