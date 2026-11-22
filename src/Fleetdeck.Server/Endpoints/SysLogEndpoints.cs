using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;

namespace Fleetdeck.Server.Endpoints;

public sealed record SysLogIgnoreRequest(string? Key, int? MachineId, bool Ignore);
public sealed record LiveLogOpenRequest(int MachineId, string? Level);
public sealed record SysLogFleetGroup(int MachineId, string Machine, string Platform, bool Online, string Key, string Level, string Log, string Source,
    int? EventId, string? Kind, int Count, DateTime First, DateTime Last, string Message, string? Ignored, List<DateTime> Recent);

/// <summary>
/// Fejlloggen: maskinens fane "Logs", den samlede side "Hændelser", skjul/vis af hændelsestyper og live-loggen.
/// Læsning for alle med adgang til maskinerne; skjul og live kun for administratorer (live også kun over https, hjemmenet eller VPN).
/// </summary>
public static class SysLogEndpoints
{
    private const int MaxRows = SysLogPolicy.MaxPerMachine, MaxEntries = 300, MaxFleetRows = 20000;

    public static void MapSysLogEndpoints(this WebApplication app)
    {
        var adm = app.MapGroup("/api").RequireAuthorization("admin");

        adm.MapGet("/machines/{id:int}/syslog", async (int id, int? days, string? level, string? source, string? q, bool? hidden, AppDb db, SettingsService s) =>
        {
            if (await db.Machines.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id) is not { } m) return Results.NotFound();
            var d = Math.Clamp(days ?? 7, 1, SysLogPolicy.RetentionDays);
            var since = DateTime.UtcNow.AddDays(-d);
            var all = await db.SysLog.AsNoTracking().Where(x => x.MachineId == id && x.Time >= since).OrderByDescending(x => x.Time).Take(MaxRows).ToListAsync();
            var ignM = await SysLogIgnore.OnMachine(s, id); var ignG = await SysLogIgnore.Global(s);
            var showHidden = hidden == true;
            var rows = all.Where(r => SysLogGroups.LevelMatches(r.Level, level) && (string.IsNullOrEmpty(source) || r.Source.Equals(source, StringComparison.OrdinalIgnoreCase))
                && SysLogGroups.TextMatches(r, q) && (showHidden || (!ignM.Contains(r.GroupKey) && !ignG.Contains(r.GroupKey)))).ToList();
            var hiddenCount = all.Count(r => ignM.Contains(r.GroupKey) || ignG.Contains(r.GroupKey));
            return Results.Ok(new
            {
                days = d,
                total = rows.Count,
                hiddenCount,
                groups = SysLogGroups.Build(rows, ignM, ignG),
                entries = rows.Take(MaxEntries).Select(r => new { r.Id, r.Time, r.Level, r.Log, r.Source, r.EventId, r.Message, r.Kind, key = r.GroupKey }),
                sources = all.GroupBy(r => r.Source, StringComparer.OrdinalIgnoreCase).Select(g => new { source = g.First().Source, count = g.Count() }).OrderByDescending(x => x.count).Take(60),
                levels = all.GroupBy(r => r.Level).ToDictionary(g => g.Key, g => g.Count()),
                lastReceived = await db.SysLog.Where(x => x.MachineId == id).MaxAsync(x => (DateTime?)x.Received),
                collects = m.LiveLogCapable,
                live = LiveLogEligibility.Why(m, await s.Get("signed_jobs")),
            });
        });

        adm.MapGet("/syslog/events", async (int? days, string? level, int? machineId, string? q, bool? hidden, AppDb db, SettingsService s) =>
        {
            var d = Math.Clamp(days ?? 7, 1, SysLogPolicy.RetentionDays);
            var since = DateTime.UtcNow.AddDays(-d);
            var serious = SysLogKinds.Serious.ToList();
            var errors = level == "errors";
            var query = db.SysLog.AsNoTracking().Where(x => x.Time >= since
                && ((x.Kind != null && serious.Contains(x.Kind)) || x.Level == SysLogLevels.Critical || (errors && x.Level == SysLogLevels.Error)));
            if (machineId is int mid) query = query.Where(x => x.MachineId == mid);
            var rows = await query.OrderByDescending(x => x.Time).Take(MaxFleetRows).ToListAsync();
            var machines = await db.Machines.AsNoTracking().Select(m => new { m.Id, m.Name, m.DisplayName, m.Platform, m.LastSeen }).ToDictionaryAsync(m => m.Id);
            var poll = await s.GetNum("poll_seconds", 30);
            var ignG = await SysLogIgnore.Global(s);
            var showHidden = hidden == true;
            var groups = new List<SysLogFleetGroup>();
            var hiddenCount = 0;
            foreach (var g in rows.Where(r => SysLogGroups.TextMatches(r, q) && machines.ContainsKey(r.MachineId)).GroupBy(r => r.MachineId))
            {
                var ignM = await SysLogIgnore.OnMachine(s, g.Key);
                var mm = machines[g.Key];
                foreach (var grp in SysLogGroups.Build(g, ignM, ignG))
                {
                    if (grp.Ignored is not null) { hiddenCount += grp.Count; if (!showHidden) continue; }
                    groups.Add(new SysLogFleetGroup(g.Key, mm.DisplayName ?? mm.Name, mm.Platform,
                        mm.LastSeen is { } ls && ls > DateTime.UtcNow.AddSeconds(-Math.Max(3 * poll + 30, 120)),
                        grp.Key, grp.Level, grp.Log, grp.Source, grp.EventId, grp.Kind, grp.Count, grp.First, grp.Last, grp.Message, grp.Ignored, grp.Recent));
                }
            }
            return Results.Ok(new
            {
                days = d, hiddenCount, truncated = rows.Count >= MaxFleetRows,
                groups = groups.OrderByDescending(x => x.Last).ToList(),
                machines = machines.Values.Select(m => new { m.Id, name = m.DisplayName ?? m.Name, m.Platform }).OrderBy(m => m.name),
            });
        });

        adm.MapPost("/syslog/ignore", async (SysLogIgnoreRequest r, ClaimsPrincipal p, AppDb db, SettingsService s, HealthService health, AuditService audit, Live live) =>
        {
            var key = (r.Key ?? "").Trim();
            if (key.Length is 0 or > 200 || !key.Contains('|')) return Results.Problem("Ukendt hændelsestype.", statusCode: 400);
            Machine? m = null;
            if (r.MachineId is int mid && (m = await db.Machines.AsNoTracking().FirstOrDefaultAsync(x => x.Id == mid)) is null) return Results.NotFound();
            if (!await SysLogIgnore.Set(s, m?.Id, key, r.Ignore)) return Results.Ok(new { changed = false });
            await audit.Log(p.UserName(), r.Ignore ? "syslog.ignore" : "syslog.unignore", $"{(m is null ? "alle maskiner" : m.Label)}: {key}", m?.Id);
            // advarsler fra den skjulte type forsvinder (eller kommer igen) med det samme
            var affected = m is not null ? [m.Id] : await db.SysLog.Where(x => x.GroupKey == key).Select(x => x.MachineId).Distinct().ToListAsync();
            foreach (var id in affected)
                await health.Evaluate(id);
            await live.Changed("alerts", m?.Id);
            return Results.Ok(new { changed = true });
        });

        adm.MapPost("/syslog/live", async (LiveLogOpenRequest r, HttpContext ctx, ClaimsPrincipal p, AppDb db, SettingsService s, TerminalBroker broker, Live live) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            if (!TerminalSecurity.SecureChannel(ctx)) return Results.Problem("Live-loggen kan kun bruges over https, fra hjemmenettet eller via VPN.", statusCode: 403);
            if (await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == p.UserId()) is not { } u) return Results.Unauthorized();
            if (await db.Machines.AsNoTracking().FirstOrDefaultAsync(m => m.Id == r.MachineId) is not { } machine) return Results.NotFound();
            if (LiveLogEligibility.Why(machine, await s.Get("signed_jobs")) is { } why) return Results.Problem(why, statusCode: 409);
            if (broker.LiveLogFull(u.Id, machine.Id)) return Results.Problem("Der er for mange åbne live-logge. Luk en af dem først.", statusCode: 429);
            var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            var ticket = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var level = r.Level == "all" ? "all" : "warning";
            broker.Add(new TerminalSession
            {
                Id = id, UserId = u.Id, UserName = u.Username, Stamp = u.SecurityStamp, Label = machine.Label, Kind = TerminalSession.LiveLogKind,
                MachineId = machine.Id, Platform = machine.Platform, UnlockToken = "", TicketHash = TerminalBroker.Hash(ticket), AgentSecretHash = TerminalBroker.Hash(secret),
                LiveLog = new LiveLogTicket { SessionId = id, Secret = secret, Level = level }, Created = broker.Now, LastInput = broker.Now,
            });
            await live.Wake(machine.Id);      // agenten henter billetten i sit næste signerede check-in, som startes nu
            return Results.Ok(new { id, ticket, label = machine.Label, platform = machine.Platform, level });
        }).RequireRateLimiting(TerminalSecurity.RateLimitPolicy);

        adm.MapDelete("/syslog/live/{id}", (string id, ClaimsPrincipal p, TerminalBroker broker) =>
        {
            if (broker.Find(id) is { IsLiveLog: true } s && s.UserId == p.UserId()) broker.Close(s, "Stoppet.");
            return Results.Ok();
        });

        // Browserens forbindelse: engangsbillet fra det CSRF-beskyttede kald ovenfor, og Origin skal være Fleetdecks egen
        adm.Map("/syslog/live/ws/{id}", async (string id, string? ticket, HttpContext ctx, ClaimsPrincipal p, TerminalBroker broker) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) return Results.BadRequest();
            if (!TerminalSecurity.SecureChannel(ctx) || !TerminalSecurity.OriginAllowed(ctx.Request.Headers.Origin.ToString(), ctx.Request.Host.Value ?? "")) return Results.StatusCode(403);
            if (broker.Find(id) is not { IsLiveLog: true } s || string.IsNullOrEmpty(ticket) || !broker.ClaimBrowser(s, ticket, p.UserId())) return Results.StatusCode(403);
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            await broker.Run(s, ws, async ct => { var b = new AgentTerminalBackend(s); await b.Attach(ct); return b; });
            return Results.Empty;
        });
    }
}
