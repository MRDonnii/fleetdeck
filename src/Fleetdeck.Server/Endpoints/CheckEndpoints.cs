using System.Security.Claims;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Server.Endpoints;

public sealed record CheckInput(string Name, string Type, string? Target, int? MachineId, int? RunOnMachineId, string? Group, string? Keyword, bool KeywordInvert,
    string? ExpectStatus, bool IgnoreTls, int? CertWarnDays, string? Expect, int? IntervalSec, int? TimeoutSec, int? FailThreshold, bool? Notify, bool? Enabled);

/// <summary>Overvågning (à la Uptime Kuma): tjek af hjemmesider, porte, adresser, containere og push-signaler.</summary>
public static class CheckEndpoints
{
    public static void MapCheckEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization("user");
        var adm = app.MapGroup("/api").RequireAuthorization("admin");

        api.MapGet("/checks", async (AppDb db, ClaimsPrincipal u) =>
        {
            var admin = u.IsInRole(Roles.Admin);   // push-adressen er en hemmelighed: med den kan man melde status for tjekket
            var now = DateTime.UtcNow;
            var checks = await db.Checks.AsNoTracking().OrderBy(c => c.Group).ThenBy(c => c.Name).ToListAsync();
            var since30 = now.AddDays(-30); var since7 = now.AddDays(-7); var since24 = now.AddHours(-24);
            var stats = await db.CheckBeats.AsNoTracking().Where(b => b.Ts >= since30)
                .GroupBy(b => b.CheckId)
                .Select(g => new
                {
                    Id = g.Key,
                    N24 = g.Count(b => b.Ts >= since24), Up24 = g.Count(b => b.Ts >= since24 && b.Up),
                    N7 = g.Count(b => b.Ts >= since7), Up7 = g.Count(b => b.Ts >= since7 && b.Up),
                    N30 = g.Count(), Up30 = g.Count(b => b.Up),
                    Ms24 = g.Where(b => b.Ts >= since24 && b.Ms != null).Average(b => (double?)b.Ms),
                }).ToDictionaryAsync(x => x.Id);
            // de seneste 60 pulsslag pr. tjek til bjælken
            var ids = checks.Select(c => c.Id).ToList();
            var recent = await db.CheckBeats.AsNoTracking().Where(b => ids.Contains(b.CheckId) && b.Ts >= now.AddDays(-2))
                .OrderByDescending(b => b.Id).Select(b => new { b.CheckId, b.Ts, b.Up, b.Ms, b.Message }).Take(ids.Count * 60 + 200).ToListAsync();
            var machines = await db.Machines.AsNoTracking().Select(m => new { m.Id, Name = m.DisplayName ?? m.Name }).ToDictionaryAsync(m => m.Id, m => m.Name);
            static double? Pct(int up, int n) => n == 0 ? null : Math.Round(100.0 * up / n, 2);
            return Results.Ok(checks.Select(c =>
            {
                stats.TryGetValue(c.Id, out var s);
                return new
                {
                    c.Id, c.Name, c.Type, c.Target, c.MachineId, machine = c.MachineId is int mid && machines.TryGetValue(mid, out var mn) ? mn : null,
                    c.RunOnMachineId, runOn = c.RunOnMachineId is int rid && machines.TryGetValue(rid, out var rn) ? rn : null,
                    c.Group, c.Keyword, c.KeywordInvert, c.ExpectStatus, c.IgnoreTls, c.CertWarnDays, c.Expect, c.IntervalSec, c.TimeoutSec, c.FailThreshold,
                    c.Notify, c.Enabled, state = c.Enabled ? c.State : CheckStates.Paused, c.LastCheck, c.LastChange, c.LastMs, c.LastMessage, c.CertExpires, c.LastPush,
                    pushUrl = c.Type == CheckTypes.Push && admin ? $"/api/push/{c.PushToken}" : null,
                    uptime24 = s is null ? null : Pct(s.Up24, s.N24), uptime7 = s is null ? null : Pct(s.Up7, s.N7), uptime30 = s is null ? null : Pct(s.Up30, s.N30),
                    avgMs = s?.Ms24 is double d ? (int?)Math.Round(d) : null,
                    beats = recent.Where(b => b.CheckId == c.Id).Take(60).Reverse().Select(b => new { b.Ts, b.Up, b.Ms, b.Message }),
                };
            }));
        });

        // historik til grafen: højst ~300 punkter (gennemsnit pr. tidsrum)
        api.MapGet("/checks/{id:int}/beats", async (int id, int? hours, AppDb db) =>
        {
            var h = Math.Clamp(hours ?? 24, 1, 24 * 30);
            var since = DateTime.UtcNow.AddHours(-h);
            var beats = await db.CheckBeats.AsNoTracking().Where(b => b.CheckId == id && b.Ts >= since).OrderBy(b => b.Ts)
                .Select(b => new { b.Ts, b.Up, b.Ms, b.Message }).ToListAsync();
            var bucket = TimeSpan.FromSeconds(Math.Max(60, h * 3600 / 300));
            var points = beats.GroupBy(b => b.Ts.Ticks / bucket.Ticks).Select(g => new
            {
                ts = new DateTime(g.Key * bucket.Ticks, DateTimeKind.Utc),
                ms = g.Any(b => b.Ms != null) ? (int?)Math.Round(g.Where(b => b.Ms != null).Average(b => b.Ms!.Value)) : null,
                up = g.All(b => b.Up), down = g.Count(b => !b.Up),
            });
            // hændelser: hvornår den gik ned og kom op igen
            var events = new List<object>(); bool? prev = null;
            foreach (var b in beats)
            {
                if (prev != b.Up) events.Add(new { b.Ts, b.Up, b.Message });
                prev = b.Up;
            }
            return Results.Ok(new { points, events = events.TakeLast(50) });
        });

        adm.MapPost("/checks", async (CheckInput i, AppDb db, AuditService audit, Live live, ClaimsPrincipal u) =>
        {
            var c = new ServiceCheck();
            if (Fill(c, i) is { } err) return Results.BadRequest(new { error = err });
            if (c.Type == CheckTypes.Push) c.PushToken = Crypto.Token(18);
            db.Checks.Add(c);
            await db.SaveChangesAsync();
            await audit.Log(u.UserName(), "check.create", c.Name);
            await live.Changed("checks");
            return Results.Ok(new { c.Id });
        });

        adm.MapPut("/checks/{id:int}", async (int id, CheckInput i, AppDb db, AuditService audit, Live live, ClaimsPrincipal u) =>
        {
            var c = await db.Checks.FindAsync(id);
            if (c is null) return Results.NotFound();
            if (Fill(c, i) is { } err) return Results.BadRequest(new { error = err });
            if (c.Type == CheckTypes.Push && string.IsNullOrEmpty(c.PushToken)) c.PushToken = Crypto.Token(18);
            c.LastCheck = null;   // tjek med det samme med de nye indstillinger
            await db.SaveChangesAsync();
            await audit.Log(u.UserName(), "check.update", c.Name);
            await live.Changed("checks");
            return Results.Ok();
        });

        adm.MapDelete("/checks/{id:int}", async (int id, AppDb db, AuditService audit, Live live, ClaimsPrincipal u) =>
        {
            var c = await db.Checks.FindAsync(id);
            if (c is null) return Results.NotFound();
            await db.CheckBeats.Where(b => b.CheckId == id).ExecuteDeleteAsync();
            db.Checks.Remove(c);
            await db.SaveChangesAsync();
            await audit.Log(u.UserName(), "check.delete", c.Name);
            await live.Changed("checks");
            return Results.Ok();
        });

        adm.MapPost("/checks/{id:int}/run", async (int id, CheckRunner runner, CancellationToken ct) =>
            await runner.RunOne(id, ct) is { } r ? Results.Ok(r) : Results.NotFound());

        adm.MapPost("/checks/{id:int}/pause", async (int id, AppDb db, Live live) =>
        {
            var c = await db.Checks.FindAsync(id);
            if (c is null) return Results.NotFound();
            c.Enabled = !c.Enabled;
            if (c.Enabled) { c.State = CheckStates.Pending; c.FailCount = 0; c.LastCheck = null; }
            await db.SaveChangesAsync();
            await live.Changed("checks");
            return Results.Ok(new { c.Enabled });
        });

        // Push: en tjeneste (cron-job, backup-script …) melder selv, at den lever. ?status=down&msg=…&ping=123
        async Task<IResult> Push(string token, string? status, string? msg, int? ping, AppDb db, CheckRunner runner, Live live)
        {
            if (token.Length < 10) return Results.NotFound();
            var c = await db.Checks.FirstOrDefaultAsync(x => x.PushToken == token && x.Type == CheckTypes.Push);
            if (c is null) return Results.NotFound(new { ok = false, msg = "Ukendt push-adresse" });
            var up = !string.Equals(status, "down", StringComparison.OrdinalIgnoreCase);
            c.LastPush = DateTime.UtcNow;
            if (c.Enabled) runner.Apply(c, new ProbeResult(up, ping, string.IsNullOrWhiteSpace(msg) ? (up ? "Meldte sig" : "Meldte fejl") : msg.Trim()), db);
            await db.SaveChangesAsync();
            await live.Changed("checks");
            return Results.Ok(new { ok = true });
        }
        app.MapGet("/api/push/{token}", Push).AllowAnonymous();
        app.MapPost("/api/push/{token}", Push).AllowAnonymous();
    }

    private static string? Fill(ServiceCheck c, CheckInput i)
    {
        var name = (i.Name ?? "").Trim();
        if (name.Length == 0) return "Giv tjekket et navn";
        if (!CheckTypes.All.Contains(i.Type)) return "Ukendt type";
        var target = (i.Target ?? "").Trim();
        if (i.Type != CheckTypes.Push && target.Length == 0) return "Udfyld adressen";
        if (i.Type == CheckTypes.Http && !(Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"))
            return "Skriv en hel adresse, fx https://eksempel.dk";
        if (i.Type == CheckTypes.Tcp && !target.Contains(':')) return "Skriv vært:port, fx 192.168.1.25:8123";
        if (i.Type == CheckTypes.Container && i.MachineId is null) return "Vælg Unraid-serveren";
        if (i.RunOnMachineId is not null && i.Type is CheckTypes.Container or CheckTypes.Push) return "Container- og push-tjek køres altid af serveren";
        c.Name = name[..Math.Min(120, name.Length)]; c.Type = i.Type; c.Target = target; c.MachineId = i.MachineId; c.RunOnMachineId = i.RunOnMachineId;
        c.Group = string.IsNullOrWhiteSpace(i.Group) ? null : i.Group.Trim();
        c.Keyword = string.IsNullOrWhiteSpace(i.Keyword) ? null : i.Keyword.Trim(); c.KeywordInvert = i.KeywordInvert;
        c.ExpectStatus = string.IsNullOrWhiteSpace(i.ExpectStatus) ? null : i.ExpectStatus.Trim(); c.IgnoreTls = i.IgnoreTls;
        c.CertWarnDays = Math.Clamp(i.CertWarnDays ?? 14, 0, 365);
        c.Expect = string.IsNullOrWhiteSpace(i.Expect) ? null : i.Expect.Trim();
        c.IntervalSec = Math.Clamp(i.IntervalSec ?? 60, 20, 86400);
        c.TimeoutSec = Math.Clamp(i.TimeoutSec ?? 10, 1, 120);
        c.FailThreshold = Math.Clamp(i.FailThreshold ?? 2, 1, 10);
        if (i.Notify is bool n) c.Notify = n;
        if (i.Enabled is bool e) c.Enabled = e;
        return null;
    }
}
