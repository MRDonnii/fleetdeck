using Fleetdeck.Server.Data;
using Fleetdeck.Server.Hubs;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

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
        Assert.Equal(1, await _comp.Enforce(id));   // påkrævet og væk: installeres igen
    }

    [Fact]
    public async Task OlderInventoryDoesNotOverruleAFreshInstall()
    {
        var id = await Setup(T0.AddHours(-1), T0);
        Assert.Equal("ok", Assert.Single(await _comp.Evaluate(id)).State);
        Assert.Equal(0, await _comp.Enforce(id));
    }

    [Fact]
    public async Task NoReinstallLoopWhenInventoryNeverSeesTheProgram()
    {
        // allerede geninstalleret én gang, og inventaret ser den stadig ikke: vis "mangler", men installér ikke igen og igen
        var id = await Setup(T0.AddHours(1), T0.AddHours(-8), T0);
        Assert.Equal("missing", Assert.Single(await _comp.Evaluate(id)).State);
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
    public void DuplicateResultIsIgnoredButALateOneAfterTheTimeoutIsAccepted()
    {
        Assert.True(JobResults.AlreadyFinal(new Job { Status = JobStatus.Success }));
        Assert.True(JobResults.AlreadyFinal(new Job { Status = JobStatus.Failed, Output = "fejl" }));
        Assert.False(JobResults.AlreadyFinal(new Job { Status = JobStatus.Running }));
        Assert.False(JobResults.AlreadyFinal(new Job { Status = JobStatus.Failed, Output = "..." + JobResults.NoAnswer }));
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
