using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Fleetdeck.Server.Data;
using Fleetdeck.Shared;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Server.Services;

/// <summary>Resultatet af ét tjek.</summary>
public sealed record ProbeResult(bool Up, int? Ms, string Message, DateTime? CertExpires = null);

/// <summary>Selve tjekkene. HTTP, TCP, ping og DNS ligger i Fleetdeck.Shared.Probe (samme kode som agenterne); container og push kun her.</summary>
public static class CheckProbe
{
    public static AgentCheck ToAgent(ServiceCheck c) => new()
    {
        Id = c.Id, Type = c.Type, Target = c.Target, Keyword = c.Keyword, KeywordInvert = c.KeywordInvert, ExpectStatus = c.ExpectStatus,
        IgnoreTls = c.IgnoreTls, Expect = c.Expect, TimeoutSec = c.TimeoutSec,
    };

    public static async Task<ProbeResult> Run(ServiceCheck c, AppDb db, CancellationToken ct)
    {
        switch (c.Type)
        {
            case CheckTypes.Container: return await Container(c, db, ct);
            case CheckTypes.Push: return PushState(c);
            default:
                var r = await Probe.Run(ToAgent(c), ct);
                return new(r.Up, r.Ms, r.Message, r.CertExpires);
        }
    }

    public static bool StatusOk(int code, string? expect) => Probe.StatusOk(code, expect);

    private static async Task<ProbeResult> Container(ServiceCheck c, AppDb db, CancellationToken ct)
    {
        if (c.MachineId is not int mid) return new(false, null, "Vælg Unraid-serveren");
        var m = await db.Machines.AsNoTracking().Where(x => x.Id == mid).Select(x => new { Name = x.DisplayName ?? x.Name, x.HealthJson, x.LastHealth }).FirstOrDefaultAsync(ct);
        if (m is null) return new(false, null, "Maskinen findes ikke længere");
        var h = Json.Des<HealthReport>(m.HealthJson);
        if (h.Unraid is null) return new(false, null, $"{m.Name} har ikke meldt containere endnu");
        if (m.LastHealth is { } lh && DateTime.UtcNow - lh > TimeSpan.FromMinutes(45))
            return new(false, null, $"Ingen nye oplysninger fra {m.Name} siden {lh.ToLocalTime():HH:mm}");
        var ct2 = h.Unraid.Containers.FirstOrDefault(x => x.Name.Equals(c.Target.Trim(), StringComparison.OrdinalIgnoreCase));
        if (ct2 is null) return new(false, null, $"Containeren {c.Target} findes ikke på {m.Name}");
        return ct2.State == "running"
            ? new(true, null, ct2.Started is { } st ? $"Kører (startet {st.ToLocalTime():d. MMM HH:mm})" : "Kører")
            : new(false, null, $"Containeren er {ct2.State}");
    }

    private static ProbeResult PushState(ServiceCheck c)
    {
        var grace = TimeSpan.FromSeconds(Math.Max(c.IntervalSec, 30) * 1.5);
        if (c.LastPush is not { } lp) return new(false, null, "Har ikke meldt sig endnu");
        return DateTime.UtcNow - lp <= grace ? new(true, null, $"Meldte sig kl. {lp.ToLocalTime():HH:mm:ss}")
            : new(false, null, $"Har ikke meldt sig siden kl. {lp.ToLocalTime():HH:mm}");
    }
}

/// <summary>
/// Kører tjekkene efter deres interval (op til 8 ad gangen), gemmer pulsslag, skifter tilstand efter et antal fejl i træk
/// og giver besked ved nede/oppe igen og certifikater, der snart udløber.
/// </summary>
public sealed class CheckRunner(IServiceScopeFactory scopes, Notifier notify, Live live, ILogger<CheckRunner> log) : BackgroundService
{
    private readonly SemaphoreSlim gate = new(8);
    private readonly HashSet<int> running = [];
    private DateTime lastCleanup = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), stop);
        while (!stop.IsCancellationRequested)
        {
            try { await Tick(stop); }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "Overvågning: fejl i runden"); }
            await Task.Delay(TimeSpan.FromSeconds(2), stop);
        }
    }

    private async Task Tick(CancellationToken stop)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var now = DateTime.UtcNow;
        var all = await db.Checks.AsNoTracking().Where(c => c.Enabled).ToListAsync(stop);
        foreach (var c in all)
        {
            // fejler den, men er ikke regnet som nede endnu: prøv igen hurtigere (højst hvert 20. sek.)
            if (c.RunOnMachineId is not null)
            {
                // køres af en agent: kun hvis den har været tavs længe, regnes tjekket som fejlet her
                var quiet = TimeSpan.FromSeconds(Math.Max(c.IntervalSec * 3, 300));
                if ((c.LastCheck ?? c.Created) > now - quiet) continue;
                lock (running) { if (!running.Add(c.Id)) continue; }
                try { await AgentSilent(c.Id, stop); } finally { lock (running) running.Remove(c.Id); }
                continue;
            }
            var every = c.FailCount > 0 && c.State != CheckStates.Down ? Math.Min(c.IntervalSec, 20) : Math.Max(c.IntervalSec, 20);
            if (c.LastCheck is { } lc && now - lc < TimeSpan.FromSeconds(every)) continue;
            lock (running) { if (!running.Add(c.Id)) continue; }
            _ = Task.Run(async () =>
            {
                await gate.WaitAsync(stop);
                try { await RunOne(c.Id, stop); }
                catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "Overvågning: {Name} fejlede", c.Name); }
                finally { gate.Release(); lock (running) running.Remove(c.Id); }
            }, stop);
        }
        if (now - lastCleanup > TimeSpan.FromHours(1))
        {
            lastCleanup = now;
            var old = now.AddDays(-30);
            await db.CheckBeats.Where(b => b.Ts < old).ExecuteDeleteAsync(stop);
        }
    }

    private async Task AgentSilent(int id, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var c = await db.Checks.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return;
        var name = c.RunOnMachineId is int mid ? await db.Machines.Where(m => m.Id == mid).Select(m => m.DisplayName ?? m.Name).FirstOrDefaultAsync(ct) : null;
        Apply(c, new ProbeResult(false, null, $"Ingen svar fra {name ?? "maskinen"}, der skal køre tjekket (er den slukket?)"), db);
        await db.SaveChangesAsync(ct);
        await live.Changed("checks");
    }

    /// <summary>Resultater fra en agent, der kører tjek fra sin egen maskine.</summary>
    public async Task FromAgent(int machineId, List<AgentCheckResult> results, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var ids = results.Select(r => r.Id).ToList();
        var checks = await db.Checks.Where(c => ids.Contains(c.Id) && c.RunOnMachineId == machineId && c.Enabled).ToListAsync(ct);
        foreach (var c in checks)
        {
            var r = results.First(x => x.Id == c.Id);
            Apply(c, new ProbeResult(r.Up, r.Ms, r.Message ?? "", r.CertExpires), db);
        }
        await db.SaveChangesAsync(ct);
        if (checks.Count > 0) await live.Changed("checks");
    }

    /// <summary>Kør ét tjek nu (også fra "Tjek nu"-knappen) og gem resultatet.</summary>
    public async Task<ProbeResult?> RunOne(int id, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var c = await db.Checks.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return null;
        if (c.RunOnMachineId is int mid)
        {
            // køres af en agent: gør det forfaldent og væk agenten
            c.LastCheck = DateTime.UtcNow.AddSeconds(-c.IntervalSec);
            await db.SaveChangesAsync(ct);
            await live.Wake(mid);
            return new ProbeResult(c.State == CheckStates.Up, c.LastMs, "Maskinen tjekker ved næste kontakt (inden for et halvt minut)");
        }
        var r = await CheckProbe.Run(c, db, ct);
        Apply(c, r, db);
        await db.SaveChangesAsync(ct);
        await live.Changed("checks");
        return r;
    }

    /// <summary>Gem pulsslaget og opdatér tilstanden (bruges også af push).</summary>
    public void Apply(ServiceCheck c, ProbeResult r, AppDb db)
    {
        var now = DateTime.UtcNow;
        db.CheckBeats.Add(new CheckBeat { CheckId = c.Id, Ts = now, Up = r.Up, Ms = r.Ms, Message = Trunc(r.Message) });
        c.LastCheck = now; c.LastMs = r.Ms; c.LastMessage = Trunc(r.Message);
        if (r.CertExpires is { } ce) c.CertExpires = ce;
        var before = c.State;
        if (r.Up) { c.FailCount = 0; c.State = CheckStates.Up; }
        else
        {
            c.FailCount++;
            if (c.FailCount >= Math.Max(1, c.FailThreshold)) c.State = CheckStates.Down;
            else if (before == CheckStates.Pending) c.State = CheckStates.Pending;
        }
        if (c.State != before && before != CheckStates.Paused)
        {
            var since = c.LastChange;
            c.LastChange = now;
            if (c.Notify)
            {
                if (c.State == CheckStates.Down) notify.Send($"🔴 {c.Name} er nede: {r.Message}");
                else if (c.State == CheckStates.Up && before == CheckStates.Down)
                    notify.Send($"🟢 {c.Name} er oppe igen{(since is { } s ? $" (nede i {Duration(now - s)})" : "")}");
            }
        }
        // certifikat: én besked, når det kommer under grænsen; nulstilles når det er fornyet
        if (c.CertExpires is { } end && c.CertWarnDays > 0)
        {
            var days = (end - now).TotalDays;
            if (days <= c.CertWarnDays && !c.CertWarned) { c.CertWarned = true; if (c.Notify) notify.Send($"🔒 {c.Name}: certifikatet udløber om {Math.Max(0, (int)days)} dage ({end.ToLocalTime():d. MMM})"); }
            else if (days > c.CertWarnDays) c.CertWarned = false;
        }
    }

    private static string Trunc(string s) => s.Length > 300 ? s[..300] : s;

    public static string Duration(TimeSpan t) => t.TotalMinutes < 1 ? $"{(int)t.TotalSeconds} sek." : t.TotalHours < 1 ? $"{(int)t.TotalMinutes} min."
        : t.TotalDays < 1 ? $"{(int)t.TotalHours} t. {t.Minutes} min." : $"{(int)t.TotalDays} d. {t.Hours} t.";
}
