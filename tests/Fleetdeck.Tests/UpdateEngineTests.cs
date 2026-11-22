using System.Globalization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Hubs;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;

namespace Fleetdeck.Tests;

/// <summary>Opdateringsmotoren: rækkefølge, runder, ærlig status, indhentning, genstartsregler og den hårde spærre.</summary>
public class UpdatePlannerTests
{
    private static UpdateCandidate C(string id, string title, string kind = UpdateKinds.Software, bool exclusive = false, bool feature = false, bool bios = false, string? advice = null, bool locked = false) =>
        new() { Id = id, Title = title, Kind = kind, Exclusive = exclusive, FeatureUpdate = feature, Bios = bios, Advice = advice, Locked = locked };

    private static readonly UpdateCandidate[] Mixed =
    [
        C("fw", "HP Dock Firmware", UpdateKinds.Firmware),
        C("app", "7-Zip", UpdateKinds.App),
        C("drv", "Intel - Display", UpdateKinds.Driver),
        C("cu", "2026-11 Kumulativ opdatering (KB5132061)"),
        C("ssu", "Servicing Stack Update for Windows 11"),
        C("ex", "Opdatering med eksklusiv håndtering", exclusive: true),
    ];

    [Fact]
    public void PhasesComeInFixedOrderWithFirmwareLast()
    {
        var plan = UpdatePlanner.Plan(Mixed, new UpdateOptions());
        Assert.Equal([UpdatePhases.Exclusive, UpdatePhases.Software, UpdatePhases.Drivers, UpdatePhases.Apps, UpdatePhases.Firmware], plan.Phases.Select(p => p.Phase));
        Assert.Equal(["ex", "ssu"], plan.Phases[0].Items.Select(i => i.Id).Order());
        Assert.True(plan.Phases[0].OneByOne);
        Assert.False(plan.Phases[1].OneByOne);
        Assert.True(plan.Phases[^1].OneByOne);
        Assert.Empty(plan.Deferred);
    }

    [Fact]
    public void DriversAndFirmwareAreOnByDefault()
    {
        var o = new UpdateOptions();
        Assert.True(o.Drivers);
        Assert.True(o.Firmware);
        Assert.False(o.FeatureUpdates);
    }

    [Fact]
    public void ProfileSwitchesAndAdviceDeferWithoutBlocking()
    {
        var items = new[]
        {
            C("d1", "Dell Monitor (2015)", UpdateKinds.Driver, advice: "Driveren er fra 2015 – over 3 år gammel."),
            C("d2", "Realtek Audio", UpdateKinds.Driver),
            C("f1", "Windows 11, version 26H2", feature: true),
            C("b1", "HP System BIOS Update", UpdateKinds.Firmware, bios: true),
            C("l1", "Easy Cut Studio", UpdateKinds.App, locked: true),
        };
        var off = UpdatePlanner.Plan(items, new UpdateOptions { Drivers = false });
        Assert.DoesNotContain(off.Phases, p => p.Phase == UpdatePhases.Drivers);
        Assert.Contains(off.Deferred, d => d.Id == "d2" && d.Reason == DeferReasons.DriversOff);
        var plan = UpdatePlanner.Plan(items, new UpdateOptions());
        Assert.Equal("d2", Assert.Single(plan.Phases, p => p.Phase == UpdatePhases.Drivers).Items.Single().Id);
        Assert.Contains(plan.Deferred, d => d.Id == "d1" && d.Reason.Contains("2015"));
        Assert.Contains(plan.Deferred, d => d.Id == "f1" && d.Reason == DeferReasons.FeatureOff);
        Assert.Contains(plan.Deferred, d => d.Id == "b1" && d.Reason == DeferReasons.Bios);
        Assert.Contains(plan.Deferred, d => d.Id == "l1" && d.Reason == DeferReasons.Locked);
        Assert.All(plan.Deferred, d => Assert.False(d.Blocking));   // bevidste valg gør ikke kørslen "delvis"
    }

    [Theory]
    [InlineData(null, null, true)]      // stationær uden batteri
    [InlineData(true, 80, true)]
    [InlineData(true, 49, false)]       // oplader, men for lidt strøm på batteriet
    [InlineData(false, 100, false)]     // på batteri
    [InlineData(null, 90, false)]       // batteri, men ukendt om opladeren sidder i
    public void FirmwareOnlyOnMainsPower(bool? onAc, int? pct, bool ok)
    {
        Assert.Equal(ok, UpdatePlanner.PowerOk(onAc, pct));
        var plan = UpdatePlanner.Plan([C("fw", "Dock Firmware", UpdateKinds.Firmware)], new UpdateOptions { OnAc = onAc, BatteryPct = pct, Apps = false });
        Assert.Equal(ok, plan.Phases.Any(p => p.Phase == UpdatePhases.Firmware));
        if (!ok) Assert.True(Assert.Single(plan.Deferred).Blocking);
    }

    [Fact]
    public void PowerFromBatteryList()
    {
        Assert.Equal(((bool?)null, (int?)null), UpdatePlanner.Power([]));
        Assert.Equal(((bool?)false, (int?)63), UpdatePlanner.Power([new BatteryInfo { Status = "discharging", ChargePct = 63.4 }]));
        Assert.Equal(((bool?)true, (int?)100), UpdatePlanner.Power([new BatteryInfo { Status = "full", ChargePct = 100 }]));
    }

    [Fact]
    public void PendingRebootAtStartLeavesWindowsUpdateForNextRoundButStillDoesApps()
    {
        var plan = UpdatePlanner.Plan(Mixed, new UpdateOptions { RebootPending = true });
        Assert.Equal([UpdatePhases.Apps], plan.Phases.Select(p => p.Phase));
        Assert.Equal(5, plan.Later.Count);
    }

    [Fact]
    public void SystemOffMeansOnlyApps()
    {
        var plan = UpdatePlanner.Plan(Mixed, new UpdateOptions { System = false });
        Assert.Equal([UpdatePhases.Apps], plan.Phases.Select(p => p.Phase));
        Assert.Equal("app", Assert.Single(plan.Phases[0].Items).Id);
    }

    [Theory]
    [InlineData("HP System Firmware 01.23", true)]
    [InlineData("Dell BIOS Update", true)]
    [InlineData("Lenovo Thunderbolt Dock Firmware", false)]
    public void BiosIsRecognised(string title, bool bios) => Assert.Equal(bios, UpdatePlanner.LooksLikeBios(title));

    [Fact]
    public void ServicingStackIsExclusiveByTitle() =>
        Assert.Equal(UpdatePhases.Exclusive, UpdatePlanner.Plan([C("s", "2026-11 Servicing Stack Update")], new UpdateOptions { Apps = false }).Phases.Single().Phase);

    [Fact]
    public void StopRulesForRebootAndCleanState()
    {
        Assert.True(UpdatePhases.StopsForReboot(UpdatePhases.Software));
        Assert.False(UpdatePhases.StopsForReboot(UpdatePhases.Drivers));
        Assert.True(UpdatePhases.NeedsCleanState(UpdatePhases.Firmware));
        Assert.False(UpdatePhases.NeedsCleanState(UpdatePhases.Apps));   // programmer kører også, når Windows venter på genstart
    }
}

public class UpdateRoundTests
{
    private static UpdateReport R(int installed = 0, string[]? failed = null, string[]? later = null, bool reboot = false) => new()
    {
        Installed = Enumerable.Range(0, installed).Select(i => new UpdateItem { Id = $"ok{i}", Title = $"OK {i}" }).ToList(),
        Failed = (failed ?? []).Select(f => new UpdateFailure { Id = f, Title = f, Code = "0x80240022" }).ToList(),
        Remaining = (later ?? []).Select(f => new UpdateItem { Id = f, Title = f }).ToList(),
        RebootPending = reboot,
    };

    [Fact] public void RebootWhenRequired() => Assert.Equal(UpdateNext.Reboot, UpdateRounds.Decide(1, 4, 0, [], R(3, reboot: true)).Next);
    [Fact] public void NothingLeftFinishes() => Assert.Equal("intet tilbage", UpdateRounds.Decide(2, 4, 1, [], R(2)).Why);
    [Fact] public void ProgressGivesAnotherRound() => Assert.Equal(UpdateNext.Round, UpdateRounds.Decide(1, 4, 0, [], R(2, failed: ["kb1"])).Next);
    [Fact] public void NoInstallsIsNoProgress() => Assert.StartsWith("ingen fremgang", UpdateRounds.Decide(1, 4, 0, [], R(0, failed: ["kb1"])).Why);
    [Fact] public void SameLeftoversIsNoProgress() => Assert.StartsWith("ingen fremgang", UpdateRounds.Decide(2, 4, 1, ["kb1"], R(1, failed: ["kb1"])).Why);
    [Fact] public void MaxRoundsStops() => Assert.Equal(UpdateNext.Finish, UpdateRounds.Decide(4, 4, 1, [], R(2, failed: ["kb1"])).Next);
    [Fact] public void MaxRebootsStops() => Assert.Equal(UpdateNext.Finish, UpdateRounds.Decide(3, 4, 4, [], R(1, reboot: true)).Next);
    [Fact] public void NoRebootWhenAfterIsNone() => Assert.Equal(UpdateNext.Finish, UpdateRounds.Decide(1, 4, 0, [], R(1, reboot: true), mayReboot: false).Next);

    [Fact]
    public void AnAgentThatAlwaysWantsARebootCannotLoopForever()
    {
        int round = 1, reboots = 0, steps = 0;
        HashSet<string> left = [];
        while (steps++ < 100)
        {
            var rep = R(1, reboot: true);
            var d = UpdateRounds.Decide(round, 4, reboots, left, rep);
            left = UpdateRounds.Left(rep);
            if (d.Next == UpdateNext.Finish) break;
            if (d.Next == UpdateNext.Reboot) reboots++;
            round++;
        }
        Assert.True(steps <= 5, $"stoppede først efter {steps} skridt");
        Assert.True(reboots <= 4);
    }

    [Fact]
    public void ClampKeepsRoundsSane()
    {
        Assert.Equal(1, UpdateRounds.Clamp(0));
        Assert.Equal(UpdateRounds.Limit, UpdateRounds.Clamp(99));
    }

    [Fact]
    public void MergeForgetsFailuresThatSucceedLater()
    {
        var total = UpdateRounds.Merge(null, R(1, failed: ["kb1"], reboot: true));
        total = UpdateRounds.Merge(total, new UpdateReport { Installed = [new UpdateItem { Id = "kb1", Title = "kb1" }] });
        Assert.Empty(total.Failed);
        Assert.Equal(2, total.Installed.Count);
        Assert.False(total.RebootPending);
    }
}

public class UpdateStatusTests
{
    [Fact]
    public void GreenOnlyWhenEverythingSucceeded()
    {
        var r = new UpdateReport { Installed = [new() { Title = "KB1" }] };
        Assert.Equal(JobStatus.Success, UpdateStatus.Of(r));
        r.Failed.Add(new UpdateFailure { Title = "KB2", Code = "0x80070643" });   // ResultCode 3: "gennemført med fejl"
        Assert.Equal(JobStatus.Partial, UpdateStatus.Of(r));
    }

    [Fact]
    public void SelfUpdatingEdgeIsNotAFailure()
    {
        var r = new UpdateReport { Installed = [new() { Title = "7-Zip" }] };
        r.Defer("Microsoft.Edge", "Microsoft Edge", DeferReasons.SelfUpdating);
        Assert.Equal(JobStatus.Success, UpdateStatus.Of(r));
        Assert.Equal(JobStatus.Success, UpdateStatus.Final(r));
    }

    [Fact]
    public void WaitingForPowerOrOwnerIsPartial()
    {
        var r = new UpdateReport();
        r.Defer(null, "macOS 27.1", DeferReasons.Owner);
        Assert.Equal(JobStatus.Partial, UpdateStatus.Of(r));
        Assert.Contains("venter på ejerens godkendelse", UpdateStatus.Text(r));
    }

    [Fact]
    public void ARoundThatStopsForRebootIsNotPartialButTheRunIsIfTheRebootNeverHappens()
    {
        var r = new UpdateReport { Installed = [new() { Title = "SSU" }], Remaining = [new() { Title = "KB5132061" }], RebootPending = true };
        Assert.Equal(JobStatus.Success, UpdateStatus.Of(r));
        Assert.Equal(JobStatus.Partial, UpdateStatus.Final(r));
        Assert.Contains("genstart mangler", UpdateStatus.Missing(r));
    }

    [Fact]
    public void OnlyFailuresIsFailed()
    {
        var r = new UpdateReport { Failed = [new UpdateFailure { Title = "KB1", Code = "0x1" }] };
        Assert.Equal(JobStatus.Failed, UpdateStatus.Of(r));
        Assert.Equal(JobStatus.Failed, UpdateStatus.Final(r));
    }

    [Fact]
    public void SearchErrorIsFailed() => Assert.Equal(JobStatus.Failed, UpdateStatus.Of(new UpdateReport { Error = "0x8024402C" }));
}

public class UpdateScheduleTests
{
    private static DateTime T(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture);
    private static readonly TimeSpan ThreeDays = TimeSpan.FromDays(3);

    [Fact]
    public void LastOccurrenceDailyAndWeekly()
    {
        Assert.Equal(T("2026-11-20 02:30"), Schedule.LastOccurrence("daily", "02:30", 6, T("2026-11-20 09:00")));
        Assert.Equal(T("2026-11-19 02:30"), Schedule.LastOccurrence("daily", "02:30", 6, T("2026-11-20 02:00")));
        Assert.Equal(T("2026-11-15 02:30"), Schedule.LastOccurrence("weekly", "02:30", 6, T("2026-11-20 09:00")));   // søndag
        Assert.Null(Schedule.LastOccurrence("off", "02:30", 6, T("2026-11-20 09:00")));
    }

    [Fact]
    public void MissedWindowIsCaughtUpWhenTheServerWasDown()
    {
        // vinduet 02:30 blev misset (serveren var nede); kl. 09 er det stadig inden for indhentningen
        Assert.Equal(T("2026-11-20 02:30"), Schedule.DueOccurrence("daily", "02:30", 6, T("2026-11-20 09:00"), "2026-11-19T02:30", ThreeDays));
        Assert.Null(Schedule.DueOccurrence("daily", "02:30", 6, T("2026-11-20 09:00"), "2026-11-20T02:30", ThreeDays));
        // ugentlig: missede søndag, indhentes onsdag (inden for 3 døgn), ikke torsdag
        Assert.NotNull(Schedule.DueOccurrence("weekly", "02:30", 6, T("2026-11-18 01:00"), "2026-11-08T02:30", ThreeDays));
        Assert.Null(Schedule.DueOccurrence("weekly", "02:30", 6, T("2026-11-19 03:00"), "2026-11-08T02:30", ThreeDays));
    }

    [Fact]
    public void OldDateStampAndNeverRunAreRespected()
    {
        Assert.Null(Schedule.DueOccurrence("daily", "02:30", 6, T("2026-11-20 09:00"), "2026-11-20", ThreeDays));   // gammelt format: kørt i dag
        Assert.NotNull(Schedule.DueOccurrence("daily", "02:30", 6, T("2026-11-20 09:00"), "2026-11-19", ThreeDays));
        // aldrig kørt / lige slået til: kun inden for en time – ellers kører den, i det øjeblik planen slås til
        Assert.Null(Schedule.DueOccurrence("daily", "02:30", 6, T("2026-11-20 09:00"), null, ThreeDays));
        Assert.NotNull(Schedule.DueOccurrence("daily", "02:30", 6, T("2026-11-20 02:40"), Schedule.Off, ThreeDays));
    }

    [Theory]
    [InlineData("08:00", "17:00", "2026-11-20 12:00", true)]
    [InlineData("08:00", "17:00", "2026-11-20 17:00", false)]
    [InlineData("22:00", "06:00", "2026-11-20 23:30", true)]    // over midnat
    [InlineData("22:00", "06:00", "2026-11-20 07:00", false)]
    [InlineData("", "", "2026-11-20 12:00", false)]
    public void QuietHoursInside(string from, string to, string now, bool inside) => Assert.Equal(inside, QuietHours.Inside(from, to, T(now)));

    [Fact]
    public void QuietHoursEnd() => Assert.Equal(T("2026-11-21 06:00"), QuietHours.End("06:00", T("2026-11-20 23:30")));

    [Fact]
    public void PatchTuesdayAndStaleData()
    {
        var now = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2026, 11, 10, 17, 0, 0, DateTimeKind.Utc), UpdateWake.PatchTuesday(now));
        Assert.Equal(new DateTime(2026, 10, 13, 17, 0, 0, DateTimeKind.Utc), UpdateWake.PatchTuesday(new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc)));
        Assert.True(UpdateWake.Stale(null, now));
        Assert.True(UpdateWake.Stale(new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc), now));   // før Patch Tuesday
        Assert.False(UpdateWake.Stale(new DateTime(2026, 11, 19, 0, 0, 0, DateTimeKind.Utc), now));
    }
}

public class AutoUpdateGuardTests
{
    [Fact]
    public void VentilationProfileProtectedMachinesAndNeverAutoAreBlocked()
    {
        Assert.NotNull(AutoUpdateGuard.Blocked(new Machine(), new Profile { Name = "Dantherm" }));
        Assert.NotNull(AutoUpdateGuard.Blocked(new Machine { Protected = true }, new Profile { Name = "Standart maskine" }));
        Assert.NotNull(AutoUpdateGuard.Blocked(new Machine(), new Profile { Name = "Styring", UpdNeverAuto = true }));
        Assert.Null(AutoUpdateGuard.Blocked(new Machine(), new Profile { Name = "Standart maskine" }));
        Assert.Null(AutoUpdateGuard.Blocked(new Machine(), null));
    }

    [Fact]
    public void RebootRules()
    {
        var now = DateTime.UtcNow;
        var run = new MaintenanceRun { Kind = "maintenance", NotBefore = now.AddMinutes(-30) };
        var own = MaintenanceService.RebootParams(run, new Profile { UpdRebootNoWarning = true }, Platforms.Windows, now);
        Assert.Equal(0, own.Delay);
        Assert.Null(own.OnlyIfIdleMin);
        Assert.NotNull(own.NotAfter);   // en genstart, der ikke hentes i tide, udløber
        var family = MaintenanceService.RebootParams(run, new Profile(), Platforms.Windows, now);
        Assert.Equal(15, family.OnlyIfIdleMin);
        Assert.True(family.Delay >= 60);
        Assert.True(family.OnlyIfPending);
        // indhentning midt på dagen: aldrig uden varsel – heller ikke på ejerens egne pc'er
        var late = MaintenanceService.RebootParams(new MaintenanceRun { CatchUp = true, NotBefore = now }, new Profile { UpdRebootNoWarning = true }, Platforms.Windows, now);
        Assert.Equal(15, late.OnlyIfIdleMin);
        Assert.Equal(300, late.Delay);
        // kørslen er trukket ind i dagtimerne (afgjort ved hver genstart, ikke kun da den begyndte)
        var dragged = MaintenanceService.RebootParams(run, new Profile { UpdRebootNoWarning = true }, Platforms.Windows, now.AddHours(4));
        Assert.Equal(15, dragged.OnlyIfIdleMin);
        // efter et nyt forsøg (pc'en var i brug eller sov) heller ikke uden varsel
        var retried = MaintenanceService.RebootParams(new MaintenanceRun { Kind = "maintenance", NotBefore = now, RebootTries = 1 }, new Profile { UpdRebootNoWarning = true }, Platforms.Windows, now);
        Assert.Equal(15, retried.OnlyIfIdleMin);
        // "Opdatér alt" lover en nedtælling – også på ejerens egne pc'er
        var manual = MaintenanceService.RebootParams(new MaintenanceRun { Kind = "manual", NotBefore = now }, new Profile { UpdRebootNoWarning = true }, Platforms.Windows, now);
        Assert.Equal(300, manual.Delay);
    }

    [Fact]
    public void PhaseTextShowsRoundAndPhase()
    {
        var r = new MaintenanceRun { Step = "round", Round = 2, MaxRounds = 4 };
        Assert.Equal("Runde 2/4 · Drivere", MaintenanceService.PhaseText(r, new Job { Status = JobStatus.Running, Step = "Runde 2/4 · Drivere: 3 opdatering(er)" }, DateTime.UtcNow));
        Assert.Equal("Runde 2/4 · Genstarter", MaintenanceService.PhaseText(new MaintenanceRun { Step = "reboot", Round = 2, MaxRounds = 4 }, null, DateTime.UtcNow));
        Assert.Equal("Venter på maskinen", MaintenanceService.PhaseText(new MaintenanceRun { Step = "", NotBefore = DateTime.UtcNow.AddHours(-1) }, null, DateTime.UtcNow));
    }

    [Fact]
    public void RebootOutcomes()
    {
        var m = new Machine();
        Assert.Equal("back", MaintenanceService.RebootOutcome(new Job { Status = JobStatus.Success, Output = "Genstarter kl. 03:01 (om 60 sek.)." }, m, DateTime.UtcNow));
        Assert.Equal("skipped", MaintenanceService.RebootOutcome(new Job { Status = JobStatus.Success, Output = "Pc'en er i brug (sidste aktivitet for 2 min. siden) – genstarter ikke." }, m, DateTime.UtcNow));
        Assert.Equal("not_needed", MaintenanceService.RebootOutcome(new Job { Status = JobStatus.Success, Output = "Ingen genstart nødvendig." }, m, DateTime.UtcNow));
        Assert.Equal("wait", MaintenanceService.RebootOutcome(new Job { Status = JobStatus.Hung, HungAt = DateTime.UtcNow.AddMinutes(-10) }, m, DateTime.UtcNow));
        Assert.Equal("lost", MaintenanceService.RebootOutcome(new Job { Status = JobStatus.Hung, HungAt = DateTime.UtcNow.AddHours(-25) }, m, DateTime.UtcNow));
        Assert.Equal("cancelled", MaintenanceService.RebootOutcome(new Job { Status = JobStatus.Cancelled }, m, DateTime.UtcNow));
        Assert.Equal("expired", MaintenanceService.RebootOutcome(new Job { Status = JobStatus.Success, Output = "Udløbet – bestilt for længe siden; genstarter ikke." }, m, DateTime.UtcNow));
        Assert.Equal("expired", MaintenanceService.RebootOutcome(new Job { Status = JobStatus.Cancelled, Output = MaintenanceService.RebootExpired }, m, DateTime.UtcNow));
    }
}

/// <summary>Hele motoren mod en database: runder, genstart, fortsættelse, indhentning, spærren.</summary>
public sealed class MaintenanceEngineTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly AppDb _db;
    private readonly MaintenanceService _maint;

    public MaintenanceEngineTests()
    {
        _conn.Open();
        _db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
        var sp = new ServiceCollection().AddHttpClient().BuildServiceProvider();
        var live = new Live(new NoHub<UiHub>(), new NoHub<AgentHub>());
        _maint = new MaintenanceService(_db, new JobService(_db, live), new SettingsService(_db),
            new Notifier(sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<Notifier>.Instance), live, NullLogger<MaintenanceService>.Instance);
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    private static readonly MaintPlan Plan = new(true, ["windows_update", "upgrade"], "reboot", false);

    private async Task<Machine> Add(string profile = "Standart maskine", bool online = true, bool isProtected = false, bool neverAuto = false)
    {
        var p = new Profile { Name = profile, UpdNeverAuto = neverAuto };
        var m = new Machine { Name = $"M{Guid.NewGuid():N}"[..10], Platform = Platforms.Windows, Profile = p, Protected = isProtected, LastSeen = online ? DateTime.UtcNow : DateTime.UtcNow.AddDays(-2) };
        _db.Machines.Add(m);
        await _db.SaveChangesAsync();
        return m;
    }

    private async Task<MaintenanceRun> Run(int id) => await _db.MaintenanceRuns.AsNoTracking().SingleAsync(r => r.Id == id);

    private async Task<Job> Current(MaintenanceRun r) => await _db.Jobs.SingleAsync(j => j.Id == r.CurrentJobId);

    private async Task Finish(Job j, string status, UpdateReport? rep = null, string output = "")
    {
        j.Status = status; j.Started ??= DateTime.UtcNow; j.Finished = DateTime.UtcNow; j.Output = output;
        if (rep is not null) j.ResultJson = Json.Ser(rep);
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task VentilationProfileIsNeverScheduledOrTouched()
    {
        foreach (var m in new[] { await Add("Dantherm"), await Add(isProtected: true), await Add(neverAuto: true) })
        {
            var run = await _maint.Start(m.Id, Plan, "p:silent", "planlægger", "maintenance");
            Assert.Equal("blocked", run!.State);
            Assert.NotNull(run.Finished);
            await _maint.Tick();
            Assert.False(await _db.Jobs.AnyAsync(j => j.MachineId == m.Id), "ingen jobs må oprettes på en spærret maskine");
        }
    }

    [Fact]
    public async Task ProfileChangedToNeverAutoStopsAWaitingRun()
    {
        var m = await Add(online: false);
        var run = await _maint.Start(m.Id, Plan, "p:silent", "planlægger");
        Assert.Equal("waiting", run!.State);
        m.Profile!.UpdNeverAuto = true; m.LastSeen = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _maint.Tick();
        Assert.Equal("blocked", (await Run(run.Id)).State);
        Assert.False(await _db.Jobs.AnyAsync(j => j.MachineId == m.Id));
    }

    [Fact]
    public async Task RoundsContinueAfterRebootUntilNothingIsLeft()
    {
        var m = await Add();
        var run = (await _maint.Start(m.Id, Plan, "p:silent", "ejer", "manual"))!;
        var j1 = await Current(await Run(run.Id));
        Assert.Equal(JobTypes.UpdateRound, j1.Type);
        var p1 = Json.Des<JobParams>(j1.ParamsJson);
        Assert.Equal(1, p1.UpdRound);
        Assert.True(p1.IncludeDrivers);
        Assert.False(p1.IncludeFirmware ?? false);   // firmware er fra som standard

        // runde 1: servicing stack installeret, kumulativ venter på genstart
        await Finish(j1, JobStatus.Success, new UpdateReport { Round = 1, Installed = [new() { Id = "wu:ssu", Title = "SSU" }], Remaining = [new() { Id = "wu:cu", Title = "Kumulativ" }], RebootPending = true });
        await _maint.Tick();
        var r = await Run(run.Id);
        Assert.Equal("reboot", r.Step);
        var reboot = await Current(r);
        Assert.Equal(JobTypes.Reboot, reboot.Type);
        Assert.True(Json.Des<JobParams>(reboot.ParamsJson).OnlyIfPending);

        // vagten melder maskinen tilbage efter genstarten → runde 2
        await Finish(reboot, JobStatus.Success, output: "Genstarter kl. 03:01 (om 300 sek.).");
        await _maint.Tick();
        r = await Run(run.Id);
        Assert.Equal(1, r.Reboots);
        Assert.Equal(2, r.Round);
        var j2 = await Current(r);
        Assert.Equal(2, Json.Des<JobParams>(j2.ParamsJson).UpdRound);
        Assert.Contains("runde 2/4", j2.Title);

        // runde 2: resten installeret, intet tilbage → færdig og grøn
        await Finish(j2, JobStatus.Success, new UpdateReport { Round = 2, Installed = [new() { Id = "wu:cu", Title = "Kumulativ" }, new() { Id = "wu:drv", Title = "Intel Driver" }] });
        await _maint.Tick();
        r = await Run(run.Id);
        Assert.Equal("done", r.State);
        Assert.NotNull(r.Finished);
        Assert.Contains("3 installeret", r.Summary);
        Assert.Equal(3, await _db.Jobs.CountAsync(j => j.MachineId == m.Id));
    }

    [Theory]
    [InlineData(true)]    // genstart mellem runderne
    [InlineData(false)]   // den sidste genstart ("Bagefter")
    public async Task AStagedBiosIsNeverInstalledByAnAutomaticReboot(bool between)
    {
        var m = await Add();
        _db.BiosUpdateRuns.Add(new Fleetdeck.Server.Data.BiosUpdateRun { MachineId = m.Id, State = BiosUpdateStates.Staged, TargetVersion = "M1UKT79A" });
        await _db.SaveChangesAsync();
        var run = (await _maint.Start(m.Id, Plan, "p:silent", "ejer", "manual"))!;
        var j1 = await Current(await Run(run.Id));
        await Finish(j1, JobStatus.Success, between
            ? new UpdateReport { Round = 1, Installed = [new() { Id = "wu:ssu", Title = "SSU" }], Remaining = [new() { Id = "wu:cu", Title = "Kumulativ" }], RebootPending = true }
            : new UpdateReport { Round = 1, Installed = [new() { Id = "wu:cu", Title = "Kumulativ" }], RebootSuggested = true });
        await _maint.Tick();
        var r = await Run(run.Id);
        Assert.NotNull(r.Finished);
        Assert.Contains(MaintenanceService.BiosWaitingWhy, r.StopReason);
        Assert.False(await _db.Jobs.AnyAsync(j => j.MachineId == m.Id && j.Type == JobTypes.Reboot), "en klar BIOS må aldrig installeres af en automatisk genstart");
    }

    [Fact]
    public async Task AStagedBiosIsNeverInstalledByAShutdownAndAnAutomaticWake()
    {
        // slukket pc med klar BIOS: motoren tænder den ikke (en kold opstart installerer også kapslen / Lenovos flash)
        var off = await Add(online: false);
        off.Mac = "02:00:00:00:00:01";
        _db.BiosUpdateRuns.Add(new Fleetdeck.Server.Data.BiosUpdateRun { MachineId = off.Id, State = BiosUpdateStates.Staged, TargetVersion = "M1UKT79A" });
        await _db.SaveChangesAsync();
        var sleeping = (await _maint.Start(off.Id, Plan with { After = "shutdown_if_woken" }, "p:silent", "planlægger"))!;
        Assert.False(sleeping.WakeSent);
        Assert.Contains(MaintenanceService.BiosNoWakeWhy, sleeping.Summary);

        // tændt pc med klar BIOS og "sluk bagefter": den slukkes ikke (næste opstart ville flashe, uanset hvem der tænder den)
        var m = await Add();
        _db.BiosUpdateRuns.Add(new Fleetdeck.Server.Data.BiosUpdateRun { MachineId = m.Id, State = BiosUpdateStates.Staged, TargetVersion = "M1UKT79A" });
        await _db.SaveChangesAsync();
        var run = (await _maint.Start(m.Id, Plan with { After = "shutdown" }, "p:silent", "planlægger"))!;
        await Finish(await Current(await Run(run.Id)), JobStatus.Success, new UpdateReport { Round = 1, Installed = [new() { Id = "wu:cu", Title = "Kumulativ" }] });
        await _maint.Tick();
        var r = await Run(run.Id);
        Assert.NotNull(r.Finished);
        Assert.Contains(MaintenanceService.BiosNoShutdownWhy, r.StopReason);
        Assert.False(await _db.Jobs.AnyAsync(j => j.MachineId == m.Id && (j.Type == JobTypes.Shutdown || j.Type == JobTypes.Reboot)));
    }

    [Fact]
    public async Task SomethingMissingMakesTheRunPartialWithAList()
    {
        var m = await Add();
        var run = (await _maint.Start(m.Id, Plan, "p:silent", "ejer"))!;
        var j1 = await Current(await Run(run.Id));
        var rep = new UpdateReport { Round = 1, Installed = [new() { Id = "a", Title = "7-Zip" }], Failed = [new() { Id = "wu:x", Title = "HP Firmware", Code = "0x80070643" }] };
        rep.Defer("Microsoft.Edge", "Microsoft Edge", DeferReasons.SelfUpdating);
        await Finish(j1, JobStatus.Partial, rep);
        await _maint.Tick();
        // fremgang (7-Zip) men noget fejlede → én runde mere; samme fejl igen uden fremgang → stop
        var r = await Run(run.Id);
        var j2 = await Current(r);
        await Finish(j2, JobStatus.Failed, new UpdateReport { Round = 2, Failed = [new() { Id = "wu:x", Title = "HP Firmware", Code = "0x80070643" }] });
        await _maint.Tick();
        r = await Run(run.Id);
        Assert.Equal("partial", r.State);
        Assert.Contains("HP Firmware", r.Summary);
        Assert.Contains("ingen fremgang", r.Summary);
        var report = MaintenanceService.MorningReport([r]);
        Assert.Contains("HP Firmware – fejlede (0x80070643)", report);
        Assert.DoesNotContain("Edge", report);   // opdaterer sig selv – ikke noget, der mangler
    }

    [Fact]
    public async Task SkippedRebootIsRetriedEveryHalfHour()
    {
        var m = await Add();
        var run = (await _maint.Start(m.Id, Plan with { After = "reboot" }, "p:silent", "planlægger"))!;
        await Finish(await Current(await Run(run.Id)), JobStatus.Success, new UpdateReport { Round = 1, Installed = [new() { Title = "KB" }], RebootPending = true });
        await _maint.Tick();
        var reboot = await Current(await Run(run.Id));
        await Finish(reboot, JobStatus.Success, output: "Pc'en er i brug (sidste aktivitet for 1 min. siden) – genstarter ikke.");
        await _maint.Tick();
        var r = await Run(run.Id);
        Assert.Equal("reboot_wait", r.State);
        Assert.Null(r.CurrentJobId);
        await _maint.Tick(DateTime.UtcNow.AddMinutes(10));
        Assert.Null((await Run(run.Id)).CurrentJobId);
        await _maint.Tick(DateTime.UtcNow.AddMinutes(31));
        r = await Run(run.Id);
        Assert.Equal("reboot", r.Step);
        Assert.NotEqual(reboot.Id, r.CurrentJobId);
    }

    [Fact]
    public async Task OfflineMachineIsCaughtUpQuietlyWhenItComesOnline()
    {
        var m = await Add(online: false);
        var run = (await _maint.Start(m.Id, Plan, "p:silent", "planlægger", "maintenance", DateTime.UtcNow.AddHours(-5)))!;
        Assert.Equal("waiting", run.State);
        Assert.False(await _db.Jobs.AnyAsync(j => j.MachineId == m.Id));   // ingen 5-timers annullering – den venter
        await _maint.Tick();
        Assert.Equal("waiting", (await Run(run.Id)).State);

        m.LastSeen = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _maint.Tick();
        var r = await Run(run.Id);
        Assert.True(r.CatchUp);
        Assert.Equal("round", r.Step);
        await Finish(await Current(r), JobStatus.Success, new UpdateReport { Round = 1, Installed = [new() { Title = "KB" }], RebootPending = true });
        await _maint.Tick();
        var reboot = Json.Des<JobParams>((await Current(await Run(run.Id))).ParamsJson);
        Assert.Equal(15, reboot.OnlyIfIdleMin);   // indhentning: genstart kun, når pc'en er ledig
    }

    [Fact]
    public async Task GivesUpAfterTheCatchUpWindow()
    {
        var m = await Add(online: false);
        var run = (await _maint.Start(m.Id, Plan, "p:silent", "planlægger"))!;
        await _maint.Tick(DateTime.UtcNow.AddDays(4));
        var r = await Run(run.Id);
        Assert.Equal("offline", r.State);
        Assert.NotNull(r.Finished);
    }

    [Fact]
    public async Task ANewerRunIsMergedIntoOneStillWaiting()
    {
        var m = await Add(online: false);
        var first = (await _maint.Start(m.Id, Plan, "p1:silent", "planlægger"))!;
        var second = (await _maint.Start(m.Id, Plan, "p2:silent", "planlægger"))!;
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, await _db.MaintenanceRuns.CountAsync(r => r.MachineId == m.Id && r.Finished == null));
    }

    [Fact]
    public async Task AnInterruptedRoundIsRepeatedWhenTheMachineIsBack()
    {
        var m = await Add();
        var run = (await _maint.Start(m.Id, Plan, "p:silent", "planlægger"))!;
        var j1 = await Current(await Run(run.Id));
        // maskinen blev genstartet midt i runden (serveren sætter jobbet til fejlet uden resultat)
        await Finish(j1, JobStatus.Failed, output: "[Server] Afbrudt: maskinen blev genstartet midt i jobbet.");
        await _maint.Tick();
        var r = await Run(run.Id);
        Assert.Equal("round_wait", r.Step);
        Assert.Null(r.Finished);
        m.LastSeen = DateTime.UtcNow; await _db.SaveChangesAsync();
        await _maint.Tick(DateTime.UtcNow.AddMinutes(3));
        r = await Run(run.Id);
        Assert.Equal("round", r.Step);
        Assert.Equal(1, r.Round);   // samme runde igen
        Assert.NotEqual(j1.Id, r.CurrentJobId);
        // en runde med et rigtigt resultat fortsætter som normalt
        await Finish(await Current(r), JobStatus.Success, new UpdateReport { Round = 1, Installed = [new() { Id = "wu:cu", Title = "Kumulativ", Phase = UpdatePhases.Software }] });
        await _maint.Tick(DateTime.UtcNow.AddMinutes(4));
        Assert.Equal("done", (await Run(run.Id)).State);
    }

    [Fact]
    public async Task TwoSchedulesForASleepingMachineAreBothKept()
    {
        var m = await Add(online: false);
        var at = DateTime.UtcNow;
        var a = (await _maint.Start(m.Id, new MaintPlan(false, ["upgrade"], "none", false), "s1:upgrade:silent", "planlægger", "schedule", at))!;
        var b = (await _maint.Start(m.Id, new MaintPlan(false, ["windows_update"], "reboot", false), "s1:wu:silent", "planlægger", "schedule", at))!;
        var c = (await _maint.Start(m.Id, new MaintPlan(true, ["windows_update", "upgrade", "disk_cleanup"], "shutdown_if_woken", true), "p1:x", "planlægger", "maintenance", at))!;
        Assert.Equal(a.Id, b.Id);
        Assert.Equal(a.Id, c.Id);
        var r = await Run(a.Id);
        var plan = System.Text.Json.JsonSerializer.Deserialize<MaintPlan>(r.PlanJson, Json.Web)!;
        Assert.Equal(["upgrade", "windows_update", "disk_cleanup"], plan.Tasks);
        Assert.Equal("shutdown_if_woken", plan.After);
        Assert.Equal("maintenance", r.Kind);
        Assert.Equal("p1:x", r.Batch);   // morgenrapporten følger med
        // maskinen kommer online: én kørsel med det hele
        m.LastSeen = DateTime.UtcNow; await _db.SaveChangesAsync();
        await _maint.Tick();
        r = await Run(a.Id);
        Assert.Equal("tasks", r.Step);
    }

    [Fact]
    public async Task ARolledBackUpdateEndsAsFailedNotGreenAndStopsRebooting()
    {
        var m = await Add();
        var run = (await _maint.Start(m.Id, Plan, "p:silent", "planlægger"))!;
        var cu = new UpdateItem { Id = "wu:cu", Title = "2026-11 Kumulativ (KB1)", Phase = UpdatePhases.Software };
        await Finish(await Current(await Run(run.Id)), JobStatus.Success, new UpdateReport { Round = 1, Installed = [cu], RebootPending = true });
        await _maint.Tick();
        await Finish(await Current(await Run(run.Id)), JobStatus.Success, output: "Genstarter kl. 03:01 (om 60 sek.).");
        await _maint.Tick();
        // efter genstarten: samme opdatering tilbydes og "lykkes" igen → den blev rullet tilbage
        var r = await Run(run.Id);
        Assert.Equal(2, r.Round);
        await Finish(await Current(r), JobStatus.Success, new UpdateReport { Round = 2, Installed = [cu], RebootPending = true });
        await _maint.Tick();
        r = await Run(run.Id);
        Assert.Equal("failed", r.State);   // aldrig grøn: det eneste, der blev "installeret", kom ikke ind
        Assert.Contains("rullet tilbage", r.Summary);
        Assert.Equal(1, await _db.Jobs.CountAsync(j => j.MachineId == m.Id && j.Type == JobTypes.Reboot));   // ingen ny genstart
        Assert.Contains(UpdateRounds.RolledBackCode, MaintenanceService.MorningReport([r]));
    }

    [Fact]
    public async Task TheFinalRebootWaitsForQuietHoursInsteadOfBeingDropped()
    {
        var m = await Add();
        var local = DateTime.Now;
        m.Profile!.UpdQuietFrom = local.AddHours(-1).ToString("HH:mm"); m.Profile.UpdQuietTo = local.AddHours(2).ToString("HH:mm");
        await _db.SaveChangesAsync();
        // højst 1 runde: runden kræver genstart → afsluttende genstart – men det er stilletid
        var run = (await _maint.Start(m.Id, Plan, "p:silent", "ejer", "manual"))!;
        var rr = await _db.MaintenanceRuns.SingleAsync(x => x.Id == run.Id); rr.MaxRounds = 1; await _db.SaveChangesAsync();
        await Finish(await Current(await Run(run.Id)), JobStatus.Success, new UpdateReport { Round = 1, Installed = [new() { Id = "wu:cu", Title = "Kumulativ", Phase = UpdatePhases.Software }], RebootPending = true });
        await _maint.Tick();
        var r = await Run(run.Id);
        Assert.Equal("final_wait", r.Step);
        Assert.Null(r.Finished);
        Assert.False(await _db.Jobs.AnyAsync(j => j.MachineId == m.Id && j.Type == JobTypes.Reboot));
        m.LastSeen = DateTime.UtcNow.AddHours(3); await _db.SaveChangesAsync();
        await _maint.Tick(DateTime.UtcNow.AddHours(3));
        r = await Run(run.Id);
        Assert.Equal("final", r.Step);
        Assert.Equal(JobTypes.Reboot, (await Current(r)).Type);
    }

    [Fact]
    public async Task ACancelledRebootIsNotRetried()
    {
        var m = await Add();
        var run = (await _maint.Start(m.Id, Plan, "p:silent", "planlægger"))!;
        await Finish(await Current(await Run(run.Id)), JobStatus.Success, new UpdateReport { Round = 1, Installed = [new() { Id = "wu:cu", Title = "KB", Phase = UpdatePhases.Software }], RebootPending = true });
        await _maint.Tick();
        await Finish(await Current(await Run(run.Id)), JobStatus.Cancelled);
        await _maint.Tick();
        await _maint.Tick(DateTime.UtcNow.AddMinutes(31));
        var r = await Run(run.Id);
        Assert.NotNull(r.Finished);
        Assert.Contains("annulleret", r.Summary);
        Assert.Equal(1, await _db.Jobs.CountAsync(j => j.MachineId == m.Id && j.Type == JobTypes.Reboot));
    }

    [Fact]
    public async Task AFailedSearchGetsAnotherRoundLater()
    {
        var m = await Add();
        var run = (await _maint.Start(m.Id, Plan, "p:silent", "planlægger"))!;
        await Finish(await Current(await Run(run.Id)), JobStatus.Partial, new UpdateReport
        {
            Round = 1, Installed = [new() { Id = "7zip", Title = "7-Zip", Phase = UpdatePhases.Apps }], Error = "Windows Update-søgningen fejlede: 0x8024402C",
        });
        await _maint.Tick();
        var r = await Run(run.Id);
        Assert.Equal("round_wait", r.Step);
        Assert.Null(r.Finished);
        await _maint.Tick(DateTime.UtcNow.AddMinutes(10));
        Assert.Equal("round_wait", (await Run(run.Id)).Step);
        await _maint.Tick(DateTime.UtcNow.AddMinutes(31));
        r = await Run(run.Id);
        Assert.Equal("round", r.Step);
        Assert.Equal(2, r.Round);
    }

    [Fact]
    public async Task ProtectingAMachineMidRunCancelsItsQueuedJobs()
    {
        var m = await Add();
        var run = (await _maint.Start(m.Id, Plan, "p:silent", "planlægger"))!;
        await Finish(await Current(await Run(run.Id)), JobStatus.Success, new UpdateReport { Round = 1, Installed = [new() { Id = "wu:a", Title = "KB", Phase = UpdatePhases.Software }], RebootPending = true });
        await _maint.Tick();
        var reboot = await Current(await Run(run.Id));
        Assert.Equal(JobStatus.Pending, reboot.Status);
        m.Protected = true; await _db.SaveChangesAsync();
        await _maint.Tick();
        Assert.Equal("blocked", (await Run(run.Id)).State);
        await _db.Entry(reboot).ReloadAsync();
        Assert.Equal(JobStatus.Cancelled, reboot.Status);
    }

    [Fact]
    public async Task ARebootTheSleepingPcNeverFetchedIsReorderedWithFreshRules()
    {
        var m = await Add();
        m.Profile!.UpdRebootNoWarning = true; await _db.SaveChangesAsync();
        var run = (await _maint.Start(m.Id, Plan, "p:silent", "planlægger"))!;
        await Finish(await Current(await Run(run.Id)), JobStatus.Success, new UpdateReport { Round = 1, Installed = [new() { Id = "wu:a", Title = "KB", Phase = UpdatePhases.Software }], RebootPending = true });
        await _maint.Tick();
        var first = await Current(await Run(run.Id));
        Assert.Equal(0, Json.Des<JobParams>(first.ParamsJson).Delay);   // om natten: uden varsel
        // pc'en faldt i søvn, før den hentede genstarten; den vågner om morgenen
        m.LastSeen = DateTime.UtcNow.AddHours(5); await _db.SaveChangesAsync();
        await _maint.Tick(DateTime.UtcNow.AddHours(5));
        await _db.Entry(first).ReloadAsync();
        Assert.Equal(JobStatus.Cancelled, first.Status);
        await _maint.Tick(DateTime.UtcNow.AddHours(5));
        var r = await Run(run.Id);
        Assert.Equal("reboot", r.Step);
        var again = Json.Des<JobParams>((await Current(r)).ParamsJson);
        Assert.Equal(15, again.OnlyIfIdleMin);   // nu med krav om, at pc'en er ledig
        Assert.NotEqual(0, again.Delay);
    }

    [Fact]
    public async Task AnOldAgentGetsTheOldJobsInstead()
    {
        var m = await Add();
        var run = (await _maint.Start(m.Id, Plan, "p:silent", "ejer", "manual"))!;
        await Finish(await Current(await Run(run.Id)), JobStatus.Failed, output: "Ukendt jobtype: update_round");
        await _maint.Tick();
        var r = await Run(run.Id);
        Assert.Equal("legacy", r.Step);
        var types = await _db.Jobs.Where(j => j.MachineId == m.Id).OrderBy(j => j.Id).Select(j => j.Type).ToListAsync();
        Assert.Equal([JobTypes.UpdateRound, JobTypes.WindowsUpdate, JobTypes.Upgrade, JobTypes.Reboot], types);
    }

    [Fact]
    public async Task AHangingRoundIsGivenUpSoTheMachineIsNotLockedForever()
    {
        var m = await Add();
        var run = (await _maint.Start(m.Id, Plan, "p:silent", "planlægger"))!;
        var j = await Current(await Run(run.Id));
        j.Status = JobStatus.Running; j.Started = DateTime.UtcNow; await _db.SaveChangesAsync();
        await _maint.Tick(DateTime.UtcNow.AddHours(7));
        var r = await Run(run.Id);
        Assert.NotNull(r.Finished);
        Assert.Contains("6 timer", r.Summary);
    }

    [Fact]
    public async Task AfterTheFinalRebootAShortCheckFindsWhatDidNotComeIn()
    {
        var m = await Add();
        var run = (await _maint.Start(m.Id, Plan, "p:silent", "planlægger"))!;
        var rr = await _db.MaintenanceRuns.SingleAsync(x => x.Id == run.Id); rr.MaxRounds = 1; await _db.SaveChangesAsync();
        var cu = new UpdateItem { Id = "wu:cu", Title = "Kumulativ", Phase = UpdatePhases.Software };
        await Finish(await Current(await Run(run.Id)), JobStatus.Success, new UpdateReport { Round = 1, Installed = [cu], RebootPending = true });
        await _maint.Tick();
        var fin = await Current(await Run(run.Id));
        Assert.Equal(JobTypes.Reboot, fin.Type);
        await Finish(fin, JobStatus.Success, output: "Genstarter kl. 03:01 (om 60 sek.).");
        await _maint.Tick();
        var r = await Run(run.Id);
        Assert.Equal("verify", r.Step);
        Assert.True(Json.Des<JobParams>((await Current(r)).ParamsJson).UpdVerify);
        await Finish(await Current(r), JobStatus.Partial, new UpdateReport { Round = 1, Remaining = [new() { Id = "wu:cu", Title = "Kumulativ" }] });
        await _maint.Tick();
        r = await Run(run.Id);
        Assert.NotEqual("done", r.State);
        Assert.Contains("rullet tilbage", MaintenanceService.MorningReport([r]));
    }

    private sealed class NoHub<T> : IHubContext<T> where T : Hub
    {
        public IHubClients Clients { get; } = new NoClients();
        public IGroupManager Groups { get; } = new NoGroups();
    }
    private sealed class NoClients : IHubClients
    {
        private static readonly IClientProxy P = new NoProxy();
        public IClientProxy All => P;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => P;
        public IClientProxy Client(string connectionId) => P;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => P;
        public IClientProxy Group(string groupName) => P;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => P;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => P;
        public IClientProxy User(string userId) => P;
        public IClientProxy Users(IReadOnlyList<string> userIds) => P;
    }
    private sealed class NoProxy : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class NoGroups : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

public sealed class EngineCountTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly AppDb _db;

    public EngineCountTests()
    {
        _conn.Open();
        _db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    [Fact]
    public async Task WakeCountsOnlyWhatTheEngineWillInstall()
    {
        var m = new Machine { Name = "KONTOR", Platform = Platforms.Windows };
        _db.Machines.Add(m);
        await _db.SaveChangesAsync();
        _db.Upgrades.AddRange(
            new UpgradeItem { MachineId = m.Id, Name = "Dell Monitor", WingetId = "wu:a", Driver = true, Advice = "Driveren er fra 2015 – over 3 år gammel." },
            new UpgradeItem { MachineId = m.Id, Name = "Intel Driver", WingetId = "wu:b", Driver = true },
            new UpgradeItem { MachineId = m.Id, Name = "HP System BIOS", WingetId = "wu:c", Driver = true },
            new UpgradeItem { MachineId = m.Id, Name = "Dock Firmware", WingetId = "wu:d", Driver = true },
            new UpgradeItem { MachineId = m.Id, Name = "7-Zip", WingetId = "7zip.7zip" });
        await _db.SaveChangesAsync();
        Assert.Equal(5, await UpgradeLocks.OpenCount(_db, m.Id));
        // firmware er fra som standard, og Windows Update-firmware kan være BIOS uden at hedde det – den tænder aldrig pc'en
        Assert.Equal(2, await UpgradeLocks.EngineCount(_db, m.Id));
        Assert.Equal(2, await UpgradeLocks.EngineCount(_db, m.Id, firmware: true));
        Assert.Equal(1, await UpgradeLocks.EngineCount(_db, m.Id, drivers: false, firmware: false));
    }
}
