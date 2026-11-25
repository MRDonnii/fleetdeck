using Fleetdeck.Server.Data;
using Fleetdeck.Server.Hubs;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Fleetdeck.Tests;

/// <summary>Rettelser fra kodegennemgangen (3.45.3): forældet "OK" i overholdelse og nye planer, der startede med det samme.</summary>
public sealed class ReviewFixesTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly AppDb _db;
    private readonly ComplianceService _comp;
    private static readonly DateTime T0 = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

    public ReviewFixesTests()
    {
        _conn.Open();
        _db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
        _comp = new ComplianceService(_db, new JobService(_db, new Live(new NoHub<UiHub>(), new NoHub<AgentHub>())));
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    /// <summary>Maskine med en påkrævet winget-pakke, som inventaret (på <paramref name="inventory"/>) ikke ser; installationsjobs lykkedes på de givne tidspunkter.</summary>
    private async Task<int> Setup(DateTime inventory, params DateTime[] installsFinished)
    {
        var prof = new Profile { Name = "P", Enforce = true };
        var pkg = new Package { Name = "Foo", Type = PackageTypes.Winget, Identifier = "Foo.Bar" };
        _db.AddRange(prof, pkg);
        await _db.SaveChangesAsync();
        _db.Add(new ProfilePackage { ProfileId = prof.Id, PackageId = pkg.Id, Intent = Intents.Required });
        var m = new Machine { Name = "PC1", ProfileId = prof.Id, LastInventory = inventory };
        _db.Add(m);
        await _db.SaveChangesAsync();
        foreach (var f in installsFinished)
            _db.Add(new Job { MachineId = m.Id, PackageId = pkg.Id, Type = JobTypes.Install, Status = JobStatus.Success, Source = "compliance", Created = f.AddMinutes(-5), Finished = f });
        await _db.SaveChangesAsync();
        return m.Id;
    }

    [Fact]
    public async Task NewerInventoryWithoutTheProgramBeatsAnOlderSuccessfulInstall()
    {
        var id = await Setup(T0.AddHours(1), T0);
        Assert.Equal("missing", Assert.Single(await _comp.Evaluate(id)).State);
        // ejerens regel: det nyere inventar viser kun den rigtige status – ingen automatisk (gen)installation
        Assert.Equal(0, await _comp.Enforce(id));
        Assert.Equal(1, await _db.Jobs.CountAsync());
    }

    [Fact]
    public async Task NeverInstalledRequiredProgramIsStillInstalledAsBefore()
    {
        var id = await Setup(T0);
        Assert.Equal("missing", Assert.Single(await _comp.Evaluate(id)).State);
        Assert.Equal(1, await _comp.Enforce(id));   // uændret: profilen gennemtvinges som hidtil
    }

    [Fact]
    public async Task OlderInventoryDoesNotOverruleAFreshInstall()
    {
        var id = await Setup(T0.AddHours(-1), T0);
        Assert.Equal("ok", Assert.Single(await _comp.Evaluate(id)).State);
        Assert.Equal(0, await _comp.Enforce(id));
    }

    [Fact]
    public void NewPlanWaitsForItsNextTimeEvenWithEmptyStamp()
    {
        // SettingsService.Get giver "" for en nøgle, der aldrig er gemt: en ny plan til 02:30 oprettet kl. 12 må ikke starte med det samme
        var now = new DateTime(2026, 11, 20, 12, 0, 0);
        Assert.Null(Schedule.DueOccurrence("daily", "02:30", 6, now, "", TimeSpan.FromDays(3)));
        Assert.NotNull(Schedule.DueOccurrence("daily", "02:30", 6, new DateTime(2026, 11, 20, 2, 40, 0), "", TimeSpan.FromDays(3)));
    }

    [Fact]
    public async Task UnsentResultIsKeptOnDiskAndRetriedUntilAcknowledged()
    {
        var dir = Path.Combine(Directory.CreateTempSubdirectory("fd-res-").FullName, "results");
        try
        {
            var q = new PendingResults(dir);
            var log = new List<string>();
            var sent = new List<int>();
            Task Down(int id, JobResult r, CancellationToken ct) => throw new HttpRequestException("netværket er nede");
            Task Up(int id, JobResult r, CancellationToken ct) { sent.Add(id); return Task.CompletedTask; }

            Assert.False(await q.Send(7, new JobResult { Status = JobStatus.Success, ExitCode = 0, Output = "ok" }, Down, log.Add, default));
            Assert.Equal([7], q.Waiting());
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));   // skrevet atomisk
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(dir, "7.json")));
            Assert.Equal(0, await q.Flush(Down, log.Add, default));   // stadig nede: bliver liggende
            Assert.Equal([7], q.Waiting());
            Assert.Equal(1, await q.Flush(Up, log.Add, default));     // serveren kvitterer: væk
            Assert.Equal([7], sent);
            Assert.Empty(q.Waiting());
            // serveren kender ikke jobbet (slettet): smides ud i stedet for at blive sendt for evigt
            q.Save(8, new JobResult());
            Assert.Equal(0, await q.Flush((_, _, _) => throw new HttpRequestException("nej", null, System.Net.HttpStatusCode.NotFound), log.Add, default));
            Assert.Empty(q.Waiting());
        }
        finally { Directory.Delete(Path.GetDirectoryName(dir)!, true); }
    }

    [Fact]
    public async Task ResultIsOnDiskBeforeItIsSentAndRemovedOnlyAfterTheAcknowledgement()
    {
        var dir = Path.Combine(Directory.CreateTempSubdirectory("fd-res-").FullName, "results");
        try
        {
            var q = new PendingResults(dir);
            var onDiskWhileSending = false;
            // agenten kan stoppe midt i kaldet: resultatet skal allerede ligge på disken
            Assert.True(await q.Send(9, new JobResult { Status = JobStatus.Success }, (id, r, ct) => { onDiskWhileSending = q.Waiting().Contains(id); return Task.CompletedTask; }, _ => { }, default));
            Assert.True(onDiskWhileSending);
            Assert.Empty(q.Waiting());   // kvitteret: væk
        }
        finally { Directory.Delete(Path.GetDirectoryName(dir)!, true); }
    }

    [Fact]
    public void DuplicateResultIsIgnoredButALateOneAfterTheTimeoutIsAccepted()
    {
        Assert.True(JobResults.AlreadyFinal(new Job { Status = JobStatus.Success }));
        Assert.True(JobResults.AlreadyFinal(new Job { Status = JobStatus.Failed, Output = "fejl" }));
        Assert.False(JobResults.AlreadyFinal(new Job { Status = JobStatus.Running }));
        Assert.False(JobResults.AlreadyFinal(new Job { Status = JobStatus.Failed, Output = "..." + JobResults.NoAnswer }));
    }

    [Fact]
    public void DuplicateRebootResultDoesNotOverwriteTheFirstBootTime()
    {
        // agenten sender resultatet igen (kvitteringen gik tabt), efter maskinen er genstartet: den første opstartstid skal blive stående
        var oldBoot = T0.AddDays(-3);
        var m = new Machine { Name = "PC1", LastSeen = T0, HealthJson = Json.Ser(new HealthReport { Live = new LiveFacts { BootAt = oldBoot } }) };
        var j = new Job { Type = JobTypes.Reboot, Status = JobStatus.Running, Started = T0.AddMinutes(-2) };
        var r = new JobResult { Status = JobStatus.Success, PowerAt = T0, PowerMode = JobTypes.Reboot };
        Assert.False(JobResults.AlreadyFinal(j));
        j.Status = r.Status;
        Assert.True(PowerWatchService.StartWatch(j, m, r, T0));
        Assert.True(JobResults.AlreadyFinal(j));   // "genstarter": et resultat mere ændrer intet
        j.Status = JobStatus.Hung;
        Assert.True(JobResults.AlreadyFinal(j));   // heller ikke, når den er meldt "hænger"
        Assert.Equal(oldBoot, j.BootBefore);
    }

    [Fact]
    public async Task NewerInventoryWithTheProgramBeatsAnOlderSuccessfulUninstallButUninstallsNothing()
    {
        var id = await Setup(T0.AddHours(1));
        var pp = await _db.ProfilePackages.SingleAsync();
        pp.Intent = Intents.Uninstall;
        _db.Add(new MachineWingetId { MachineId = id, Value = "Foo.Bar" });   // programmet er der (geninstalleret)
        _db.Add(new Job { MachineId = id, PackageId = pp.PackageId, Type = JobTypes.Uninstall, Status = JobStatus.Success, Source = "compliance", Created = T0.AddMinutes(-5), Finished = T0 });
        await _db.SaveChangesAsync();
        Assert.Equal("present", Assert.Single(await _comp.Evaluate(id)).State);
        // ejerens regel: kun den rigtige status – ingen automatisk afinstallation
        Assert.Equal(0, await _comp.Enforce(id));
        Assert.Equal(1, await _db.Jobs.CountAsync());
    }

    [Fact]
    public async Task OlderInventoryDoesNotOverruleAFreshUninstall()
    {
        var id = await Setup(T0.AddHours(-1));
        var pp = await _db.ProfilePackages.SingleAsync();
        pp.Intent = Intents.Uninstall;
        _db.Add(new MachineWingetId { MachineId = id, Value = "Foo.Bar" });
        _db.Add(new Job { MachineId = id, PackageId = pp.PackageId, Type = JobTypes.Uninstall, Status = JobStatus.Success, Source = "compliance", Created = T0.AddMinutes(-5), Finished = T0 });
        await _db.SaveChangesAsync();
        Assert.Equal("ok", Assert.Single(await _comp.Evaluate(id)).State);
    }

    [Fact]
    public async Task NeverUninstalledProgramIsStillUninstalledAsBefore()
    {
        var id = await Setup(T0);
        (await _db.ProfilePackages.SingleAsync()).Intent = Intents.Uninstall;
        _db.Add(new MachineWingetId { MachineId = id, Value = "Foo.Bar" });
        await _db.SaveChangesAsync();
        Assert.Equal(1, await _comp.Enforce(id));   // uændret: profilen gennemtvinges som hidtil
    }

    [Fact]
    public async Task CleanupOfOldJobsDoesNotTriggerAnAutomaticInstall()
    {
        // rigtig database med migreringer: PackageOutcomes fyldes ud fra jobhistorikken, og "Ryd op" må ikke fjerne beskyttelsen
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        using var db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite(conn).Options);
        await db.Database.MigrateAsync();
        var comp = new ComplianceService(db, new JobService(db, new Live(new NoHub<UiHub>(), new NoHub<AgentHub>())));
        var prof = new Profile { Name = "P", Enforce = true };
        var pkg = new Package { Name = "Foo", Type = PackageTypes.Winget, Identifier = "Foo.Bar" };
        db.AddRange(prof, pkg);
        await db.SaveChangesAsync();
        db.Add(new ProfilePackage { ProfileId = prof.Id, PackageId = pkg.Id, Intent = Intents.Required });
        var m = new Machine { Name = "PC1", ProfileId = prof.Id, LastInventory = T0.AddDays(-40).AddHours(1) };
        db.Add(m);
        await db.SaveChangesAsync();
        db.Add(new Job { MachineId = m.Id, PackageId = pkg.Id, Type = JobTypes.Install, Status = JobStatus.Success, Created = T0.AddDays(-40), Finished = T0.AddDays(-40) });
        await db.SaveChangesAsync();
        // jobbet fra før opdateringen: migreringen fylder PackageOutcomes ud
        var migrator = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        await migrator.MigrateAsync("20261120235245_AccessBlocks");
        await migrator.MigrateAsync();
        Assert.Equal(1, await db.PackageOutcomes.CountAsync());
        // "Ryd op": lykkede jobs ældre end 30 dage slettes
        await db.Jobs.Where(j => j.Status == JobStatus.Success && j.Finished < T0.AddDays(-30)).ExecuteDeleteAsync();
        db.ChangeTracker.Clear();
        Assert.Equal("missing", Assert.Single(await comp.Evaluate(m.Id)).State);
        Assert.Equal(0, await comp.Enforce(m.Id));
        Assert.Equal(0, await db.Jobs.CountAsync());
    }

    // ---- 3.45.7 ----

    [Fact]
    public void LateResultIsAcceptedAfterServerClosedTheJobOnReboot()
    {
        // serveren lukkede jobbet ved check-in efter genstarten: agentens gemte, rigtige resultat skal stadig tages imod
        Assert.False(JobResults.AlreadyFinal(new Job { Status = JobStatus.Failed, Output = "halvvejs" + JobResults.Interrupted }));
        Assert.False(JobResults.AlreadyFinal(new Job { Status = JobStatus.Failed, Output = JobResults.NoAnswer }));
        Assert.True(JobResults.AlreadyFinal(new Job { Status = JobStatus.Failed, Output = "fejl fra agenten" }));
        Assert.True(JobResults.AlreadyFinal(new Job { Status = JobStatus.Success, Output = JobResults.Interrupted }));
    }

    [Fact]
    public void ProtectedMachinesAreNotOfferedANewAgentVersion()
    {
        Assert.Equal("3.45.7", AgentCheckin.OfferedVersion(new Machine { Name = "PC1" }, new Profile { Name = "P" }, "3.45.7"));
        Assert.Equal("", AgentCheckin.OfferedVersion(new Machine { Name = "PC1", Protected = true }, null, "3.45.7"));
        Assert.Equal("", AgentCheckin.OfferedVersion(new Machine { Name = "PC1" }, new Profile { Name = "P", UpdNeverAuto = true }, "3.45.7"));
    }

    [Fact]
    public void ShareLoginIsOnlySentWhenTheAgentLacksTheCurrentOne()
    {
        var share = new ShareInfo { Path = "\\\\server\\share", User = "u", Password = "p1" };
        Assert.False(AgentCheckin.ShareKept(share, null));   // ældre agent (intet aftryk): får det som før
        Assert.False(AgentCheckin.ShareKept(share, ""));
        Assert.False(AgentCheckin.ShareKept(null, ShareInfo.HashOf(share)));   // ventende tilmelding: intet at beholde
        // agenten husker det første svar og melder aftrykket; så sendes login ikke igen, og agenten bruger det, den har
        var mem = new ShareMemory();
        Assert.Null(mem.Hash);
        mem.Apply(new CheckinResponse { Share = share });
        Assert.True(AgentCheckin.ShareKept(share, mem.Hash));
        var kept = new CheckinResponse { ShareKept = true };
        mem.Apply(kept);
        Assert.Equal("p1", kept.Share!.Password);
        // adgangskoden ændres på serveren: aftrykket passer ikke længere, så det nye sendes
        Assert.False(AgentCheckin.ShareKept(new ShareInfo { Path = share.Path, User = "u", Password = "p2" }, mem.Hash));
    }

    [Fact]
    public async Task CheckinDoesNotOverwriteANewerHealthReport()
    {
        var m = new Machine { Name = "PC1", HealthJson = Json.Ser(new HealthReport { Live = new LiveFacts { Cpu = 1 } }) };
        _db.Add(m);
        await _db.SaveChangesAsync();
        // en nyere helbredsrapport gemmes, efter at check-in har læst maskinen
        var newer = Json.Ser(new HealthReport { Live = new LiveFacts { Cpu = 2 }, Kept = [new KeptService()] });
        await _db.Machines.Where(x => x.Id == m.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.HealthJson, newer));
        await LiveFactsStore.Save(_db, m, new LiveFacts { Cpu = 3 });
        m.LastSeen = T0;
        await _db.SaveChangesAsync();
        var stored = Json.Des<HealthReport>(await _db.Machines.AsNoTracking().Where(x => x.Id == m.Id).Select(x => x.HealthJson).SingleAsync());
        Assert.Single(stored.Kept!);          // den nyere rapport står der stadig
        Assert.Equal(3, stored.Live.Cpu);     // med de løbende tal fra check-in
    }

    [Fact]
    public async Task OneFailingMachineDoesNotDropTheRestOfTheSchedule()
    {
        var started = new List<int>();
        var failed = new List<int>();
        var errors = await Schedule.ForEach([1, 2, 3], mid =>
        {
            if (mid == 2) throw new InvalidOperationException("dårlig maskine");
            started.Add(mid);
            return Task.CompletedTask;
        }, (mid, _) => failed.Add(mid));
        Assert.Equal(1, errors);
        Assert.Equal([1, 3], started);
        Assert.Equal([2], failed);
    }

    [Fact]
    public async Task BackupDownloadIsWrittenToTheAuditLog()
    {
        var audit = new AuditService(_db);
        await Fleetdeck.Server.Endpoints.BackupEndpoints.LogDownload(audit, "anna", "fleetdeck-backup.fdb", "");
        await Fleetdeck.Server.Endpoints.BackupEndpoints.LogDownload(audit, "anna", "fleetdeck-backup.fdb", "bytes=1048576-");   // fortsat delhentning: samme hentning
        var row = Assert.Single(await _db.Audit.ToListAsync());
        Assert.Equal(("anna", "backup.download", "fleetdeck-backup.fdb"), (row.User, row.Action, row.Detail));
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
