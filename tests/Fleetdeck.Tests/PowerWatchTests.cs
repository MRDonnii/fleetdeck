using Microsoft.AspNetCore.DataProtection;
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

/// <summary>"Genstart nu" (punkt 5), vagten over genstart/sluk (punkt 6), strøm via Home Assistant og "Gør klar til WoL".</summary>
public class PowerWatchTests
{
    private static readonly DateTime T0 = new(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);   // planlagt tidspunkt
    private static readonly DateTime OldBoot = T0.AddDays(-3);

    private static PowerWatchInput In(string type = JobTypes.Reboot, DateTime? now = null, DateTime? bootNow = null, DateTime? lastSeen = null,
        bool precise = true, double? watts = null, bool wolTest = false, DateTime? wakeSent = null, DateTime? livePowerAt = null, string? livePowerMode = null,
        DateTime? bootBefore = null, bool beforeKnown = true) =>
        new(type, T0, T0.AddMinutes(-2), T0.AddMinutes(-2), beforeKnown ? bootBefore ?? OldBoot : null, precise, bootNow ?? OldBoot, precise,
            lastSeen ?? T0.AddSeconds(-30), livePowerMode, livePowerAt, now ?? T0.AddMinutes(1), 5, 30, watts, 15, wolTest, wakeSent);

    // ---------------------------------------------------------------- "Genstart nu"
    [Fact]
    public void Now_only_for_the_planned_action()
    {
        var plan = (JobTypes.Reboot, T0);
        Assert.Equal(JobTypes.Reboot, PlannedPower.NowAllowed(plan));
        Assert.Equal(JobTypes.Reboot, PlannedPower.NowAllowed(plan, JobTypes.Reboot));
        Assert.Null(PlannedPower.NowAllowed(plan, JobTypes.Shutdown));   // knappen kan ikke slukke, når der er planlagt en genstart
        Assert.Null(PlannedPower.NowAllowed(null));                      // intet planlagt: intet at sige ja til
        Assert.Null(PlannedPower.NowAllowed(("lock", T0)));
        Assert.Equal(JobTypes.Shutdown, PlannedPower.NowAllowed((JobTypes.Shutdown, T0)));
    }

    [Fact]
    public void Now_labels_and_note()
    {
        Assert.Equal("Genstart nu", PlannedPower.NowLabel(JobTypes.Reboot));
        Assert.Equal("Sluk nu", PlannedPower.NowLabel(JobTypes.Shutdown));
        var t = new DateTime(2026, 11, 19, 14, 7, 0);
        Assert.Equal($"Brugeren valgte Genstart nu kl. {t:HH:mm}.", PlannedPower.NowNote(JobTypes.Reboot, t));   // ":" følger sproget, som resten af Fleetdeck
    }

    [Fact]
    public void Windows_shutdown_args_are_forced()
    {
        // "Genstart nu": samme kommando som det planlagte tidspunkt, bare /t 0 og altid /f
        Assert.Equal("/r /f /t 0 /d p:4:1 /c \"x\"", PlannedPower.WindowsArgs(JobTypes.Reboot, 0, "x", force: true));
        Assert.Equal("/s /f /t 120 /d p:4:1 /c \"Gem 'det'\"", PlannedPower.WindowsArgs(JobTypes.Shutdown, 120, "Gem \"det\"", force: true));
        Assert.Equal("/r /t 60 /d p:4:1 /c \"a b\"", PlannedPower.WindowsArgs(JobTypes.Reboot, 60, "a\r\nb".Replace("\r\n", "\n"), force: false));
        Assert.StartsWith("/r /f /t 0 ", PlannedPower.WindowsArgs(JobTypes.Reboot, -5, "", true));
    }

    [Fact]
    public void Boot_time_from_uptime_is_whole_seconds()
    {
        var b = BootTime.FromUptime(new DateTime(2026, 11, 19, 12, 0, 0, 500, DateTimeKind.Utc), 3600.2);
        Assert.Equal(new DateTime(2026, 11, 19, 11, 0, 0, DateTimeKind.Utc), b);   // 12:00:00,5 − 3600,2 s = 11:00:00,3 → hele sekunder
        Assert.Null(BootTime.FromUptime(T0, null));
        Assert.Null(BootTime.FromUptime(T0, -1));
    }

    [Fact]
    public void Boot_prefers_agent_value_else_uptime()
    {
        var seen = T0;
        Assert.Equal(((DateTime?)OldBoot, true), PowerWatch.Boot(new LiveFacts { BootAt = OldBoot, UptimeH = 1 }, seen));
        var (at, precise) = PowerWatch.Boot(new LiveFacts { UptimeH = 2 }, seen);
        Assert.Equal(T0.AddHours(-2), at); Assert.False(precise);
        Assert.Equal(((DateTime?)null, false), PowerWatch.Boot(new LiveFacts(), seen));
    }

    // ---------------------------------------------------------------- vagten: genstart
    [Fact]
    public void Reboot_is_not_success_before_machine_is_back()
    {
        // agenten er stoppet, maskinen svarer ikke endnu: jobbet venter (står som "genstarter"), det er ikke lykkedes
        Assert.Equal(PowerVerdictKind.Wait, PowerWatch.Evaluate(In(now: T0.AddMinutes(2), lastSeen: T0.AddSeconds(-5))).Kind);
        // stadig online lige efter tidspunktet med samme opstart (Windows lukker ned): venter
        Assert.Equal(PowerVerdictKind.Wait, PowerWatch.Evaluate(In(now: T0.AddSeconds(40), lastSeen: T0.AddSeconds(30))).Kind);
    }

    [Fact]
    public void Reboot_back_with_new_boot_time_is_success()
    {
        var v = PowerWatch.Evaluate(In(now: T0.AddMinutes(3), bootNow: T0.AddMinutes(1), lastSeen: T0.AddMinutes(2)));
        Assert.Equal(PowerVerdictKind.Back, v.Kind);
        Assert.Contains("ny opstartstid", PowerWatch.Describe(v, JobTypes.Reboot, 5));
        // også efter fristen (den kom sent, men den kom)
        Assert.Equal(PowerVerdictKind.Back, PowerWatch.Evaluate(In(now: T0.AddMinutes(20), bootNow: T0.AddMinutes(12), lastSeen: T0.AddMinutes(19))).Kind);
    }

    [Fact]
    public void Reboot_not_back_after_deadline_hangs()
    {
        var v = PowerWatch.Evaluate(In(now: T0.AddMinutes(6), lastSeen: T0.AddSeconds(-5), watts: 100));
        Assert.Equal(PowerVerdictKind.Hung, v.Kind);
        Assert.Equal(HangReason.StillPowered, v.Reason);
        Assert.Contains("100 W", PowerWatch.Describe(v, JobTypes.Reboot, 5));
        var nometer = PowerWatch.Evaluate(In(now: T0.AddMinutes(6), lastSeen: T0.AddSeconds(-5)));
        Assert.Equal(HangReason.NotBack, nometer.Reason);
    }

    [Fact]
    public void Reboot_still_online_with_same_boot_hangs()
    {
        var v = PowerWatch.Evaluate(In(now: T0.AddMinutes(6), lastSeen: T0.AddMinutes(5.8)));
        Assert.Equal(PowerVerdictKind.Hung, v.Kind);
        Assert.Equal(HangReason.SameBoot, v.Reason);
        Assert.Contains("samme opstartstid", PowerWatch.Describe(v, JobTypes.Reboot, 5));
    }

    [Fact]
    public void Imprecise_boot_from_old_agent_needs_a_clear_difference()
    {
        // ældre agent: oppetiden er afrundet (±3 min) – 3 minutter forskel er ikke en ny opstart
        Assert.False(PowerWatch.NewBoot(In(precise: false, bootNow: OldBoot.AddMinutes(3))));
        Assert.True(PowerWatch.NewBoot(In(precise: false, bootNow: T0.AddMinutes(1))));
        // præcis opstartstid: et minuts forskel er nok
        Assert.True(PowerWatch.NewBoot(In(bootNow: OldBoot.AddMinutes(2))));
        Assert.False(PowerWatch.NewBoot(In(bootNow: OldBoot.AddSeconds(20))));
        // ukendt "før": opstart efter jobbet blev startet
        Assert.True(PowerWatch.NewBoot(In(beforeKnown: false, bootNow: T0)));
        Assert.False(PowerWatch.NewBoot(In(beforeKnown: false, bootNow: OldBoot)));
    }

    [Fact]
    public void Postponed_or_now_moves_the_watch()
    {
        // Udsæt 60 min.: planen er flyttet
        var later = PowerWatch.Evaluate(In(now: T0.AddMinutes(-1), lastSeen: T0.AddMinutes(-1), livePowerAt: T0.AddMinutes(60), livePowerMode: JobTypes.Reboot));
        Assert.Equal(PowerVerdictKind.Moved, later.Kind);
        Assert.Equal(T0.AddMinutes(60), later.NewPlannedAt);
        // samme plan: venter
        Assert.Equal(PowerVerdictKind.Wait, PowerWatch.Evaluate(In(now: T0.AddMinutes(-1), lastSeen: T0.AddMinutes(-1), livePowerAt: T0, livePowerMode: JobTypes.Reboot)).Kind);
    }

    [Fact]
    public void Cancelled_on_machine_before_time()
    {
        var v = PowerWatch.Evaluate(In(now: T0.AddMinutes(-1), lastSeen: T0.AddMinutes(-1)));
        Assert.Equal(PowerVerdictKind.Cancelled, v.Kind);
    }

    // ---------------------------------------------------------------- vagten: sluk og Wake-on-LAN-test
    [Fact]
    public void Shutdown_gone_without_meter_is_off()
    {
        Assert.Equal(PowerVerdictKind.Off, PowerWatch.Evaluate(In(JobTypes.Shutdown, now: T0.AddMinutes(3), lastSeen: T0.AddSeconds(-10))).Kind);
        // stadig online efter fristen: hænger
        Assert.Equal(PowerVerdictKind.Hung, PowerWatch.Evaluate(In(JobTypes.Shutdown, now: T0.AddMinutes(6), lastSeen: T0.AddMinutes(5.9))).Kind);
    }

    [Fact]
    public void Shutdown_with_meter_waits_for_power_to_drop()
    {
        Assert.Equal(PowerVerdictKind.Wait, PowerWatch.Evaluate(In(JobTypes.Shutdown, now: T0.AddMinutes(3), lastSeen: T0.AddSeconds(-10), watts: 95)).Kind);
        var hung = PowerWatch.Evaluate(In(JobTypes.Shutdown, now: T0.AddMinutes(6), lastSeen: T0.AddSeconds(-10), watts: 95));
        Assert.Equal(PowerVerdictKind.Hung, hung.Kind); Assert.Equal(HangReason.StillPowered, hung.Reason);
        Assert.Equal(PowerVerdictKind.Off, PowerWatch.Evaluate(In(JobTypes.Shutdown, now: T0.AddMinutes(3), lastSeen: T0.AddSeconds(-10), watts: 1.5)).Kind);
    }

    [Fact]
    public void Wol_test_sends_wake_then_measures()
    {
        // slukket for nylig: vent lidt endnu
        Assert.Equal(PowerVerdictKind.Wait, PowerWatch.Evaluate(In(JobTypes.Shutdown, now: T0.AddMinutes(2.4), lastSeen: T0.AddSeconds(-5), wolTest: true)).Kind);
        Assert.Equal(PowerVerdictKind.SendWake, PowerWatch.Evaluate(In(JobTypes.Shutdown, now: T0.AddMinutes(3), lastSeen: T0.AddSeconds(-5), wolTest: true)).Kind);
        var sent = T0.AddMinutes(3);
        Assert.Equal(PowerVerdictKind.Wait, PowerWatch.Evaluate(In(JobTypes.Shutdown, now: sent.AddMinutes(1), lastSeen: T0.AddSeconds(-5), wolTest: true, wakeSent: sent)).Kind);
        var ok = PowerWatch.Evaluate(In(JobTypes.Shutdown, now: sent.AddMinutes(2), bootNow: sent.AddSeconds(30), lastSeen: sent.AddSeconds(75), wolTest: true, wakeSent: sent));
        Assert.Equal(PowerVerdictKind.WakeOk, ok.Kind);
        Assert.Equal(75, ok.Value);
        Assert.Contains("75 sek.", PowerWatch.Describe(ok, JobTypes.Shutdown, 5));
        var failed = PowerWatch.Evaluate(In(JobTypes.Shutdown, now: sent.AddMinutes(6), lastSeen: T0.AddSeconds(-5), wolTest: true, wakeSent: sent));
        Assert.Equal(PowerVerdictKind.WakeFailed, failed.Kind);
        Assert.Contains("ErP = Off", PowerWatch.Describe(failed, JobTypes.Shutdown, 5));
    }

    // ---------------------------------------------------------------- strøm via Home Assistant
    [Fact]
    public void Power_cycle_only_with_switch_meter_and_power()
    {
        Assert.Null(PowerWatch.CycleRefusal("switch.kontor_pc", "sensor.kontor_pc_power", 100, 15));
        Assert.NotNull(PowerWatch.CycleRefusal(null, "sensor.kontor_pc_power", 100, 15));
        Assert.NotNull(PowerWatch.CycleRefusal("light.kontor", "sensor.kontor_pc_power", 100, 15));   // kun switch.*
        Assert.NotNull(PowerWatch.CycleRefusal("switch.kontor_pc", null, 100, 15));                   // ingen måler: ingen strøm-genstart
        Assert.NotNull(PowerWatch.CycleRefusal("switch.kontor_pc", "sensor.kontor_pc_power", null, 15));
        Assert.Contains("slukket", PowerWatch.CycleRefusal("switch.kontor_pc", "sensor.kontor_pc_power", 2, 15));
    }

    [Fact]
    public void Power_cycle_needs_confirmation_and_name_on_protected()
    {
        Assert.NotNull(PowerWatch.ConfirmRefusal(false, null, false, "ARBEJDS-PC"));
        Assert.Null(PowerWatch.ConfirmRefusal(true, null, false, "ARBEJDS-PC"));
        Assert.NotNull(PowerWatch.ConfirmRefusal(true, "forkert", true, "ARBEJDS-PC"));
        Assert.Null(PowerWatch.ConfirmRefusal(true, " arbejds-pc ", true, "ARBEJDS-PC"));
    }

    [Fact]
    public void Entities_and_watts()
    {
        Assert.True(HaEntity.IsSwitch("switch.kontor_pc"));
        Assert.False(HaEntity.IsSwitch("switch.Kontor PC"));
        Assert.False(HaEntity.IsSensor("switch.kontor_pc"));
        Assert.Equal("sensor.a_power", HaEntity.Clean(" Sensor.A_Power ", HaEntity.IsSensor));
        Assert.Null(HaEntity.Clean("  ", HaEntity.IsSensor));
        Assert.Throws<ArgumentException>(() => HaEntity.Clean("sensor.a; rm", HaEntity.IsSensor));
        Assert.Equal(100.5, HaEntity.Watts("100.5", "W"));
        Assert.Equal(120, HaEntity.Watts("0.12", "kW"));
        Assert.Null(HaEntity.Watts("unavailable", "W"));
    }

    [Fact]
    public void Ha_url_is_normalized()
    {
        Assert.Equal("http://192.0.2.10:8123", HomeAssistantApi.Normalize(" http://192.0.2.10:8123/api/ "));
        Assert.Equal("https://ha.example.org", HomeAssistantApi.Normalize("https://ha.example.org/"));
        Assert.Null(HomeAssistantApi.Normalize("ftp://192.0.2.10"));
        Assert.Null(HomeAssistantApi.Normalize("192.0.2.10:8123"));
    }

    // ---------------------------------------------------------------- Gør klar til WoL
    [Fact]
    public void Wol_scripts_back_up_and_set_the_known_values()
    {
        var p = WolReady.PrepareScript();
        foreach (var x in new[] { "*WakeOnMagicPacket", "EnablePME", "WakeOnLink", "HiberbootEnabled", "deviceenablewake", "wol-backup", "ConvertTo-Json", "-NoRestart" })
            Assert.Contains(x, p);
        Assert.True(p.IndexOf("ConvertTo-Json", StringComparison.Ordinal) > 0);
        Assert.Contains("ErP / EuP Ready = Off", p);
        Assert.Contains("Restore on AC Power Loss = Power On", p);
        var r = WolReady.RestoreScript();
        Assert.Contains("ConvertFrom-Json", r);
        Assert.Contains("devicedisablewake", r);
        Assert.DoesNotContain("192.168.", p + r);   // neutral kode
    }

    // ---------------------------------------------------------------- jobbet: står som "genstarter", til maskinen er tilbage
    [Fact]
    public void Result_starts_watch_only_for_a_real_plan()
    {
        var m = new Machine { Name = "ARBEJDS-PC", LastSeen = T0.AddMinutes(-3), HealthJson = Json.Ser(new HealthReport { Live = new LiveFacts { BootAt = OldBoot } }) };
        var j = new Job { Type = JobTypes.Reboot, Status = JobStatus.Success, Started = T0.AddMinutes(-2), Finished = T0.AddMinutes(-2) };
        Assert.True(PowerWatchService.StartWatch(j, m, new JobResult { Status = JobStatus.Success, PowerAt = T0, PowerMode = JobTypes.Reboot }, T0.AddMinutes(-2)));
        Assert.Equal(JobStatus.Restarting, j.Status);
        Assert.Null(j.Finished);
        Assert.Equal(OldBoot, j.BootBefore); Assert.True(j.BootBeforePrecise);
        Assert.Contains(JobStatus.Restarting, JobStatus.Active);

        var none = new Job { Type = JobTypes.Reboot, Status = JobStatus.Success, Started = T0 };
        Assert.False(PowerWatchService.StartWatch(none, m, new JobResult { Status = JobStatus.Success, Output = "Ingen genstart nødvendig." }, T0));
        Assert.Equal(JobStatus.Success, none.Status);
        // en gammel plan fra før jobbet blev startet tæller ikke
        Assert.False(PowerWatchService.StartWatch(none, m, new JobResult { Status = JobStatus.Success, PowerAt = T0.AddMinutes(-30), PowerMode = JobTypes.Reboot }, T0));
        Assert.False(PowerWatchService.StartWatch(new Job { Type = JobTypes.Script, Status = JobStatus.Success }, m, new JobResult { Status = JobStatus.Success, PowerAt = T0 }, T0));
    }
}

/// <summary>Vagten mod databasen: jobbet lykkes, når maskinen er tilbage, og hænger ellers (én besked).</summary>
public sealed class PowerWatchServiceTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly AppDb _db;
    private readonly PowerWatchService _watch;

    public PowerWatchServiceTests()
    {
        _conn.Open();
        _db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
        var s = new SettingsService(_db);
        var sp = new ServiceCollection().AddHttpClient().BuildServiceProvider();
        var http = sp.GetRequiredService<IHttpClientFactory>();
        var ha = new HomeAssistantApi(s, new EphemeralDataProtectionProvider(), http);
        _watch = new PowerWatchService(_db, s, ha, new Notifier(http, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<Notifier>.Instance),
            new AuditService(_db), new Live(new NoHub<UiHub>(), new NoHub<AgentHub>()), NullLogger<PowerWatchService>.Instance);
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    private async Task<(Machine M, Job J)> Add(DateTime powerAt, DateTime lastSeen, DateTime boot)
    {
        var m = new Machine { Name = $"M{Guid.NewGuid():N}"[..10], LastSeen = lastSeen, HealthJson = Json.Ser(new HealthReport { Live = new LiveFacts { BootAt = boot } }) };
        _db.Machines.Add(m);
        await _db.SaveChangesAsync();
        var j = new Job { MachineId = m.Id, Type = JobTypes.Reboot, Title = "Genstart", Status = JobStatus.Restarting, Started = powerAt.AddMinutes(-2),
            ProgressAt = powerAt.AddMinutes(-2), PowerAt = powerAt, BootBefore = boot.AddDays(-1), BootBeforePrecise = true };
        if (boot > powerAt.AddMinutes(-10)) j.BootBefore = boot.AddDays(-2);
        _db.Jobs.Add(j);
        await _db.SaveChangesAsync();
        return (m, j);
    }

    [Fact]
    public async Task Back_with_new_boot_becomes_success()
    {
        var now = DateTime.UtcNow;
        var (_, j) = await Add(now.AddMinutes(-3), now.AddSeconds(-10), now.AddMinutes(-2));
        await _watch.Tick();
        var after = await _db.Jobs.AsNoTracking().FirstAsync(x => x.Id == j.Id);
        Assert.Equal(JobStatus.Success, after.Status);
        Assert.Contains("ny opstartstid", after.Output);
    }

    [Fact]
    public async Task Not_back_hangs_once_and_is_logged()
    {
        var now = DateTime.UtcNow;
        var (m, j) = await Add(now.AddMinutes(-8), now.AddMinutes(-9), now.AddDays(-3));
        j.BootBefore = now.AddDays(-3); await _db.SaveChangesAsync();
        await _watch.Tick();
        var after = await _db.Jobs.AsNoTracking().FirstAsync(x => x.Id == j.Id);
        Assert.Equal(JobStatus.Hung, after.Status);
        Assert.NotNull(after.HungAt);
        Assert.Equal(1, await _db.Audit.CountAsync(a => a.Action == "power.hung" && a.MachineId == m.Id));
        await _watch.Tick();   // meldes kun én gang
        Assert.Equal(1, await _db.Audit.CountAsync(a => a.Action == "power.hung" && a.MachineId == m.Id));
        // kommer den tilbage senere (fx efter strøm-genstart), lykkes jobbet alligevel
        var mm = await _db.Machines.FirstAsync(x => x.Id == m.Id);
        mm.LastSeen = DateTime.UtcNow; mm.HealthJson = Json.Ser(new HealthReport { Live = new LiveFacts { BootAt = DateTime.UtcNow.AddMinutes(-1) } });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        await _watch.Tick();
        Assert.Equal(JobStatus.Success, (await _db.Jobs.AsNoTracking().FirstAsync(x => x.Id == j.Id)).Status);
    }

    [Fact]
    public async Task User_chose_now_is_written_to_job_and_audit()
    {
        var now = DateTime.UtcNow;
        var (m, j) = await Add(now.AddMinutes(10), now, now.AddDays(-3));
        Assert.True(await _watch.UserChoseNow(m, JobTypes.Reboot));
        var after = await _db.Jobs.AsNoTracking().FirstAsync(x => x.Id == j.Id);
        Assert.Contains("Brugeren valgte Genstart nu kl.", after.Output);
        Assert.True(after.PowerAt <= DateTime.UtcNow && after.PowerAt > now.AddMinutes(-1));
        Assert.Equal(1, await _db.Audit.CountAsync(a => a.Action == "power.now"));
        Assert.False(await _watch.UserChoseNow(m, JobTypes.Shutdown));   // ingen sluk planlagt
        Assert.False(await _watch.UserChoseNow(m, "lock"));
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
