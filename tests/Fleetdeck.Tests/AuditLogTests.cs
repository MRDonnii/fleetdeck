using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Endpoints;
using Fleetdeck.Server.Hubs;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;

namespace Fleetdeck.Tests;

/// <summary>Revisionslog: filtre, sider, CSV, oprydning, kun administratorer og at jobs skrives i loggen.</summary>
public sealed class AuditLogTests : IDisposable
{
    private readonly SqliteConnection conn = new("Data Source=:memory:");
    private readonly AppDb db;
    private static readonly DateTime T0 = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    public AuditLogTests()
    {
        conn.Open();
        db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        db.Machines.Add(new Machine { Id = 1, Name = "STUE-PC", DisplayName = "Stue-pc" });
        db.Machines.Add(new Machine { Id = 2, Name = "KONTOR" });
        db.Audit.AddRange(
            new AuditEntry { Ts = T0.AddDays(-400), User = "anna", Action = "login", Detail = "192.168.1.5 · adgangskode" },
            new AuditEntry { Ts = T0.AddDays(-10), User = "anna", Action = "login.failed", Detail = "192.168.1.9" },
            new AuditEntry { Ts = T0.AddDays(-5), User = "bent", Action = "job.create", Detail = "Genstart (reboot)", MachineId = 1 },
            new AuditEntry { Ts = T0.AddDays(-4), User = "anna", Action = "machine.rename", Detail = "STUE-PC → Stue-pc" },   // ældre række uden id
            new AuditEntry { Ts = T0.AddDays(-3), User = "anna", Action = "debloat.apply", Detail = "KONTOR: 100% færdig", MachineId = 2 },
            new AuditEntry { Ts = T0.AddDays(-1), User = "agent:Stue-pc", Action = "recovery_key.escrow", Detail = "1 ny(e)", MachineId = 1 },
            new AuditEntry { Ts = T0, User = "anna", Action = "loginx", Detail = "=HYPERLINK(\"http://x\";\"klik\")" });
        db.SaveChanges();
    }

    public void Dispose() { db.Dispose(); conn.Dispose(); }

    private async Task<List<string>> Actions(AuditFilter f) => await (await AuditLog.Query(db, f)).Select(a => a.Action).ToListAsync();

    [Fact]
    public async Task Filters_by_time_user_action_prefix_machine_and_text()
    {
        Assert.Equal(7, (await Actions(new())).Count);
        Assert.Equal(["loginx", "recovery_key.escrow", "debloat.apply"], await Actions(new(From: T0.AddDays(-3))));
        Assert.Equal(["login.failed", "login"], await Actions(new(To: T0.AddDays(-5))));
        Assert.Equal(["recovery_key.escrow", "machine.rename"], await Actions(new(From: T0.AddDays(-6), User: null, Q: "stue")));
        Assert.Equal(["loginx", "debloat.apply", "machine.rename", "login.failed", "login"], await Actions(new(User: "anna")));
        // "login" er både login og login.*, men ikke loginx
        Assert.Equal(["login.failed", "login"], await Actions(new(Action: "login")));
        Assert.Equal(["login.failed"], await Actions(new(Action: "login.failed")));
        // maskine: id'et og ældre rækker, hvor kun navnet står i teksten
        Assert.Equal(["recovery_key.escrow", "machine.rename", "job.create"], await Actions(new(MachineId: 1)));
        Assert.Equal(["debloat.apply"], await Actions(new(MachineId: 2)));
        // fri tekst: %-tegnet er et bogstav, ikke et jokertegn
        Assert.Equal(["debloat.apply"], await Actions(new(Q: "100%")));
        Assert.Empty(await Actions(new(Q: "10%0")));
        Assert.Equal(["recovery_key.escrow"], await Actions(new(Q: "recovery_key")));
        Assert.Equal(["login"], await Actions(new(User: "anna", Action: "login", To: T0.AddDays(-100))));
    }

    [Fact]
    public async Task Pages_and_machine_names()
    {
        var q = await AuditLog.Query(db, new());
        var p1 = await AuditLog.Rows(db, q, 0, 3);
        var p3 = await AuditLog.Rows(db, q, 6, 3);
        Assert.Equal(3, p1.Count);
        Assert.Single(p3);
        Assert.Equal("login", p3[0].Action);
        Assert.Equal("Stue-pc", p1.Single(r => r.Action == "recovery_key.escrow").Machine);
        Assert.Equal("KONTOR", p1.Single(r => r.Action == "debloat.apply").Machine);
        Assert.Null(p1.Single(r => r.Action == "loginx").Machine);
    }

    [Fact]
    public async Task Csv_export_quotes_and_neutralises_formulas()
    {
        var csv = AuditLog.Csv(await AuditLog.Rows(db, await AuditLog.Query(db, new()), 0, 100));
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("﻿Tid (UTC);Bruger;Handling;Maskine;Detaljer", lines[0]);
        Assert.Equal(8, lines.Length);
        Assert.Equal("2026-11-01 12:00:00;anna;loginx;;\"'=HYPERLINK(\"\"http://x\"\";\"\"klik\"\")\"", lines[1]);
        Assert.Contains("2026-10-31 12:00:00;agent:Stue-pc;recovery_key.escrow;Stue-pc;1 ny(e)", lines);
        Assert.Equal("\"a;b\"", AuditLog.Cell("a;b"));
        Assert.Equal("'+45 1234", AuditLog.Cell("+45 1234"));
        Assert.Equal("\"linje 1\nlinje 2\"", AuditLog.Cell("linje 1\nlinje 2"));
    }

    [Fact]
    public async Task Retention_defaults_to_365_days_and_can_be_changed()
    {
        Assert.Equal(365, await AuditLog.RetentionDays(db));
        Assert.Equal(1, await AuditLog.Prune(db, T0));
        Assert.Equal(7, await db.Audit.CountAsync());   // 6 tilbage + rækken om oprydningen
        Assert.Equal("audit.prune", (await db.Audit.OrderByDescending(a => a.Id).FirstAsync()).Action);

        db.Settings.Add(new Setting { Key = AuditLog.RetentionKey, Value = "7" });   // for lidt: mindst 30 dage
        await db.SaveChangesAsync();
        Assert.Equal(30, await AuditLog.RetentionDays(db));
        Assert.Equal(0, await AuditLog.Prune(db, T0));

        (await db.Settings.FindAsync(AuditLog.RetentionKey))!.Value = "0";   // gem altid
        await db.SaveChangesAsync();
        Assert.Equal(0, await AuditLog.Prune(db, T0.AddYears(10)));
        Assert.Equal(0, AuditLog.Normalize(-5));
        Assert.Equal(400, AuditLog.Normalize(400));
    }

    [Fact]
    public async Task Jobs_created_by_a_person_are_audited_but_scheduled_jobs_are_not()
    {
        var jobs = new JobService(db, new Live(new NoHub<UiHub>(), new NoHub<AgentHub>()));
        await jobs.Create(1, JobTypes.Reboot, "Genstart", null, RunAs.System, null, "manual", "anna");
        await jobs.Create(1, JobTypes.Upgrade, "Planlagt opdatering", null, RunAs.System, null, "schedule", "planlægger");
        await jobs.Create(1, JobTypes.Health, "Tjek helbred", null, RunAs.System, null, "manual", "system");
        var rows = await db.Audit.Where(a => a.Ts > T0).ToListAsync();
        var r = Assert.Single(rows);
        Assert.Equal(("anna", "job.create", 1), (r.User, r.Action, r.MachineId));
        Assert.Contains("Genstart", r.Detail);
    }

    [Fact]
    public void Audit_endpoints_are_admin_only()
    {
        var b = WebApplication.CreateBuilder();
        b.Services.AddDbContext<AppDb>(o => o.UseSqlite("Data Source=:memory:"));
        b.Services.AddScoped<AuditService>();
        b.Services.AddScoped<SettingsService>();
        b.Services.AddAuthorization();
        var app = b.Build();
        app.MapAuditEndpoints();
        var eps = ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().ToList();
        Assert.Equal(4, eps.Count);
        foreach (var e in eps)
        {
            Assert.StartsWith("/api/audit", e.RoutePattern.RawText);
            Assert.Contains(e.Metadata.GetOrderedMetadata<IAuthorizeData>(), a => a.Policy == "admin");
            Assert.Null(e.Metadata.GetMetadata<IAllowAnonymous>());
        }
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
