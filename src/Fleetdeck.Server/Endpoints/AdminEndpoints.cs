using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;

namespace Fleetdeck.Server.Endpoints;

public sealed record MachineUpdate(int? ProfileId, List<string>? Tags, string? Notes);
public sealed record MachineRename(string? Name);
public sealed record WebUrlRequest(string? Url);
public sealed record UpgradeLockRequest(int? MachineId, string? WingetId, string? Name, string? Scope, bool Locked);
public sealed record UpdateHideRequest(List<string>? Ids, bool Hide);

public sealed class ActionParams
{
    public int? PackageId { get; set; }
    public int? ScriptId { get; set; }
    public string? Body { get; set; }
    public string? RunAs { get; set; }
    public string? Name { get; set; }
    public int? TimeoutMin { get; set; }
    public string? WingetId { get; set; }
    public int? Delay { get; set; }
    public bool? OnlyIfPending { get; set; }
    public int? OnlyIfIdleMin { get; set; }
    public int? FromMachineId { get; set; }       // kit_restore: gendan fra en anden maskines punkt (fx en ny Pi)
    public string? Mode { get; set; }             // update_all: auto (system + programmer + genstart) | manual (kun programmer)
    public string? After { get; set; }            // vedligeholdelse: none | reboot | shutdown_if_woken | shutdown
    public List<string>? Tasks { get; set; }      // vedligeholdelse: windows_update | upgrade | disk_cleanup | defender_signatures
    public string? Message { get; set; }
    public string? Text { get; set; }
    public string? Step { get; set; }
    public int? ProfileId { get; set; }
    public bool? IncludeDrivers { get; set; }
}
public sealed record ActionRequest(List<int> MachineIds, string Action, ActionParams? Params);
public sealed record UnraidActionRequest(string Kind, string? Name, string? Action);
public sealed record IgnoreAlertRequest(string Key, string? Title, bool All);
public sealed record PackageDto(string Name, string Type, string? Identifier, string? Args, string? RunAs, string? Detect, string? Category, string? Notes);
public sealed record ProfilePackageDto(int PackageId, string Intent);
public sealed record ProfileDto(string Name, string? Description, bool Enforce, bool IsDefault,
    string UpgradeMode, string UpgradeTime, int UpgradeDay, List<string>? UpgradeExclude, string RebootPolicy,
    string WindowsUpdateMode, string WindowsUpdateTime, int WindowsUpdateDay,
    string BackupMode, string BackupTime, int BackupDay, bool ProvDrivers, bool ProvRestore, bool ProvAppearance, bool ProvWindowsUpdate,
    List<ProfilePackageDto>? Packages, List<string>? AutoFix = null,
    string? MaintMode = null, string? MaintTime = null, int? MaintDay = null, bool? MaintWake = null, List<string>? MaintTasks = null,
    string? MaintAfter = null, bool? MaintReport = null,
    List<string>? KeepRunning = null, List<string>? KitPaths = null, string? KitMode = null, string? KitTime = null, int? KitDay = null,
    bool? UpdDrivers = null, bool? UpdFirmware = null, bool? UpdFeatureUpdates = null, int? UpdMaxRounds = null, int? UpdCatchUpDays = null,
    bool? UpdNeverAuto = null, bool? UpdRebootNoWarning = null, string? UpdQuietFrom = null, string? UpdQuietTo = null);
public sealed record NetworkDriveDto(int? ProfileId, int? MachineId, string Host, string Share, string? Letter, string? Name, bool? OwnLogin = null);
public sealed record ScriptDto(string Name, string? Description, string? RunAs, string? Body);
public sealed record UserCreate(string Username, string Password, string Role, bool? IpadAll, List<int>? Ipads, List<string>? Perms);
public sealed record UserUpdate(string Role, bool? IpadAll, List<int>? Ipads, List<string>? Perms);
public sealed record PasswordChange(string? Current, string Password);

/// <summary>Byg visninger af maskiner til websiden.</summary>
public sealed class MachineViews(AppDb db, ComplianceService compliance, AgentBinary agent, SettingsService settings)
{
    public async Task<double> Poll() => await settings.GetNum("poll_seconds", 30);
    public static bool Online(Machine m, double poll) => m.LastSeen is { } s && s > DateTime.UtcNow.AddSeconds(-Math.Max(3 * poll + 30, 120));

    public async Task<Dictionary<string, object?>> Row(Machine m, double poll)
    {
        var facts = Json.Des<SystemFacts>(m.FactsJson);
        var h = Json.Des<HealthReport>(m.HealthJson);
        return new()
        {
            ["id"] = m.Id, ["name"] = m.Label, ["hostname"] = m.Name, ["renamed"] = m.DisplayName is not null, ["platform"] = m.Platform, ["online"] = Online(m, poll), ["lastSeen"] = m.LastSeen, ["onlineSince"] = m.OnlineSince, ["ip"] = m.Ip, ["mac"] = m.Mac,
            ["user"] = m.User, ["profileId"] = m.ProfileId, ["profile"] = m.Profile?.Name, ["tags"] = m.Tags,
            ["webUrl"] = m.WebUrl ?? (facts.Os is { } fos && fos.Contains("unraid", StringComparison.OrdinalIgnoreCase) && m.Ip is { Length: > 0 } wip ? $"http://{wip}" : null),
            ["os"] = facts.Os, ["osVersion"] = facts.OsVersion, ["model"] = facts.Model, ["distro"] = facts.Distro,
            ["agentVersion"] = m.AgentVersion, ["agentCurrent"] = m.AgentVersion == agent.VersionFor(m.Platform),
            ["compliance"] = ComplianceService.Summarize(await compliance.Evaluate(m.Id)),
            ["upgrades"] = await UpgradeLocks.OpenCount(db, m.Id),
            ["jobsActive"] = await db.Jobs.CountAsync(j => j.MachineId == m.Id && JobStatus.Active.Contains(j.Status)),
            ["healthScore"] = m.HealthScore, ["healthState"] = m.HealthState,
            ["cpu"] = h.Live.Cpu, ["ram"] = h.Live.Ram,
            // forsidens kort: fuldeste drev, CPU-temperatur, oppetid og fjernskrivebord (uden adgangskode)
            ["disk"] = h.Live.Volumes.Count > 0 ? h.Live.Volumes.Max(v => v.PctUsed) : (double?)null,
            ["temp"] = h.Live.Temps.Cpu is double tc ? Math.Round(tc) : null,
            ["uptimeH"] = h.Live.UptimeH,
            ["headless"] = h.Live.HeadlessBrowsers,
            ["net"] = NetParse.Brief(h.Network),   // primær adapter: kablet/Wi-Fi, hastighed, signal
            ["remote"] = h.Remote is { Id.Length: > 0 } rd ? new { rd.Id, rd.Error } : null,
            ["power"] = Online(m, poll) && h.Live.PowerAt is { } pa && pa > DateTime.UtcNow.AddMinutes(-2) ? new { mode = h.Live.PowerMode, at = pa } : null,
            ["running"] = await db.Jobs.AsNoTracking().Where(j => j.MachineId == m.Id && j.Status == JobStatus.Running).OrderBy(j => j.Started)
                .Select(j => new { j.Id, j.Title, j.Progress, j.Step }).FirstOrDefaultAsync(),
            ["alerts"] = await db.Alerts.CountAsync(a => a.MachineId == m.Id && a.Resolved == null && a.Acknowledged == null && a.Severity != "info"),
            // tillid mellem agent og server (docs/sikkerhed-agent.md)
            ["replacesId"] = m.ReplacesId,
            ["replacesName"] = m.ReplacesId is int rid ? await db.Machines.Where(x => x.Id == rid).Select(x => x.DisplayName ?? x.Name).FirstOrDefaultAsync() : null,
            ["enrollMethod"] = m.EnrollMethod, ["jobTrust"] = m.JobTrust, ["trustError"] = m.TrustError, ["trustErrorAt"] = m.TrustErrorAt,
            ["transport"] = m.Transport, ["tlsPinned"] = m.TlsPinned,
        };
    }

    public async Task<List<Dictionary<string, object?>>> Rows()
    {
        var poll = await Poll();
        var list = new List<Dictionary<string, object?>>();
        foreach (var m in await db.Machines.AsNoTracking().Include(x => x.Profile).OrderBy(x => x.DisplayName ?? x.Name).ToListAsync())
            list.Add(await Row(m, poll));
        return list;
    }
}

public static partial class AdminEndpoints
{
    [GeneratedRegex(@"[^\w.\- ()+]")] private static partial Regex BadFileChars();

    /// <summary>Alarmer med hjælperens forklaring, anbefalet rettelse og status for seneste rettelse.</summary>
    private static async Task<List<object>> AlertList(AppDb db, int? machineId, bool openOnly, int limit)
    {
        var q = db.Alerts.AsNoTracking().Include(a => a.Machine).AsQueryable();
        if (machineId is int id) q = q.Where(a => a.MachineId == id);
        if (openOnly) q = q.Where(a => a.Resolved == null);
        var rows = await q.OrderBy(a => a.Resolved != null).ThenBy(a => a.Severity == "critical" ? 0 : a.Severity == "warning" ? 1 : 2)
            .ThenByDescending(a => a.LastSeen).Take(limit).ToListAsync();
        var jobIds = rows.Where(a => a.FixJobId != null).Select(a => a.FixJobId!.Value).ToList();
        var fixJobs = await db.Jobs.AsNoTracking().Where(j => jobIds.Contains(j.Id)).ToDictionaryAsync(j => j.Id, j => new { j.Id, j.Status, j.Finished, j.CreatedBy });
        return rows.Select(a =>
        {
            var adv = HelperCatalog.AdviceFor(a.Key, a.Severity, a.Machine?.Platform, a.Detail);
            var fix = adv?.FixId is { } f ? HelperCatalog.Get(f) : null;
            return (object)new
            {
                a.Id, a.MachineId, machine = a.Machine!.DisplayName ?? a.Machine.Name, a.Key, a.Severity, a.Title, a.Detail, a.FirstSeen, a.LastSeen, a.Resolved, a.Acknowledged, a.AckBy,
                why = adv?.Why, what = adv?.What, steps = adv?.Steps ?? [],
                fix = fix is null ? null : new { fix.Id, fix.Title, fix.Risk, fix.Description, button = adv?.Button ?? fix.Button },
                fixJob = a.FixJobId is int fj && fixJobs.TryGetValue(fj, out var job) ? job : null,
                a.FixAttempts,
            };
        }).ToList();
    }

    /// <summary>Seneste vedligeholdelse/opdateringskørsel med runde, fase og hvad der mangler.</summary>
    public static async Task<object?> MaintenanceView(AppDb db, int machineId)
    {
        var r = await db.MaintenanceRuns.AsNoTracking().Where(x => x.MachineId == machineId).OrderByDescending(x => x.Id).FirstOrDefaultAsync();
        if (r is null) return null;
        var cur = r.CurrentJobId is int cj ? await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == cj) : null;
        var rep = r.ReportJson.Length > 0 ? Json.Des<UpdateReport>(r.ReportJson) : null;
        return new
        {
            r.Id, r.Created, r.Finished, r.State, r.Summary, r.WasOffline, r.WakeSent, r.Engine, r.Kind, r.Step, r.Round, r.MaxRounds, r.Reboots,
            r.CatchUp, r.NotBefore, r.CatchUpUntil, r.NextAt, r.StopReason,
            phase = r.Finished is null ? MaintenanceService.PhaseText(r, cur, DateTime.UtcNow) : null,
            currentJob = cur is null ? null : new { cur.Id, cur.Title, cur.Status, cur.Progress, cur.Step },
            report = rep is null ? null : new
            {
                installed = rep.Installed.Select(i => i.Title), failed = rep.Failed.Select(f => new { f.Title, f.Code }),
                waiting = rep.Deferred.Where(d => d.Blocking).Select(d => new { d.Title, d.Reason }),
                skipped = rep.Deferred.Where(d => !d.Blocking).Select(d => new { d.Title, d.Reason }),
                remaining = rep.Remaining.Select(x => x.Title), rep.RebootPending, rep.Error,
            },
        };
    }

    /// <summary>Kører intet af sig selv? Vedligeholdelse tæller kun, hvis den også opdaterer (ikke kun oprydning).</summary>
    public static bool AutoUpdateOff(Profile p) =>
        (p.MaintMode == "off" || !MaintPlan.From(p).HasUpdates) && p.UpgradeMode == "off" && p.WindowsUpdateMode == "off";

    private static object ProfileOut(Profile p, int machines, int packages, int? defaultId) => new
    {
        p.Id, p.Name, p.Description, p.Enforce, p.UpgradeMode, p.UpgradeTime, p.UpgradeDay, p.UpgradeExclude, p.RebootPolicy,
        p.WindowsUpdateMode, p.WindowsUpdateTime, p.WindowsUpdateDay, p.BackupMode, p.BackupTime, p.BackupDay,
        p.ProvDrivers, p.ProvRestore, p.ProvAppearance, p.ProvWindowsUpdate, p.AutoFix,
        p.MaintMode, p.MaintTime, p.MaintDay, p.MaintWake, p.MaintTasks, p.MaintAfter, p.MaintReport,
        p.UpdDrivers, p.UpdFirmware, p.UpdFeatureUpdates, p.UpdMaxRounds, p.UpdCatchUpDays, p.UpdNeverAuto, p.UpdRebootNoWarning, p.UpdQuietFrom, p.UpdQuietTo,
        autoUpdateOff = AutoUpdateOff(p),
        p.KeepRunning, p.KitPaths, p.KitMode, p.KitTime, p.KitDay, nextKit = Schedule.Next(p.KitMode, p.KitTime, p.KitDay),
        nextMaint = Schedule.Next(p.MaintMode, p.MaintTime, p.MaintDay),
        machines, packageCount = packages, isDefault = defaultId == p.Id,
        nextUpgrade = Schedule.Next(p.UpgradeMode, p.UpgradeTime, p.UpgradeDay),
        nextWindowsUpdate = Schedule.Next(p.WindowsUpdateMode, p.WindowsUpdateTime, p.WindowsUpdateDay),
        nextBackup = Schedule.Next(p.BackupMode, p.BackupTime, p.BackupDay),
    };

    private static IResult Bad(string msg) => Results.Problem(msg, statusCode: 400);

    private static void ApplyAccess(User x, string role, bool? all, List<int>? ipads, List<string>? perms)
    {
        x.Role = role;
        if (role == Roles.Parent)
        {
            x.IpadAll = all ?? x.IpadAll;
            if (ipads is not null) x.Ipads = ipads.Distinct().ToList();
            if (perms is not null) x.Perms = perms.Where(FamilyPerms.All.Contains).Distinct().ToList();
            else if (x.Perms.Count == 0) x.Perms = [.. FamilyPerms.Default];
        }
        else { x.IpadAll = false; x.Ipads = []; x.Perms = []; }
    }

    private static async Task<object[]> Week(AppDb db)
    {
        var from = DateTime.UtcNow.Date.AddDays(-6);
        var done = await db.Jobs.AsNoTracking().Where(j => j.Finished >= from && (j.Type == JobTypes.Upgrade || j.Type == JobTypes.Install))
            .Select(j => new { j.Finished, j.Status }).ToListAsync();
        return Enumerable.Range(0, 7).Select(i => from.AddDays(i)).Select(d => (object)new
        {
            day = d, ok = done.Count(j => j.Finished!.Value.Date == d && j.Status == JobStatus.Success),
            failed = done.Count(j => j.Finished!.Value.Date == d && j.Status == JobStatus.Failed),
        }).ToArray();
    }

    public static void MapAdminEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization("user");
        var adm = app.MapGroup("/api").RequireAuthorization("admin");

        // Alle ventende opdateringer på tværs af maskinerne (siden Opdateringer)
        api.MapGet("/upgrades", async (AppDb db, SettingsService s) =>
        {
            var poll = await s.GetNum("poll_seconds", 30);
            var ms = await db.Machines.AsNoTracking().Select(m => new { m.Id, Name = m.DisplayName ?? m.Name, m.Platform, m.LastSeen, m.FactsJson }).ToListAsync();
            var ups = await db.Upgrades.AsNoTracking().ToListAsync();
            var global = await UpgradeLocks.Global(db);
            var locks = new Dictionary<int, Dictionary<string, string>>();
            foreach (var m in ms) locks[m.Id] = await UpgradeLocks.For(db, m.Id, global);
            return ups.Join(ms, u => u.MachineId, m => m.Id, (u, m) => new
            {
                machineId = m.Id, machine = m.Name, m.Platform, online = m.LastSeen > DateTime.UtcNow.AddSeconds(-poll * 3 - 60),
                u.Name, u.WingetId, u.Current, u.Available, locked = locks[m.Id].GetValueOrDefault(u.WingetId),
            }).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.machine);
        });

        // "Opdatér aldrig": lås et program på én maskine eller alle; lås op fjerner det fra begge (profilens liste redigeres i profilen)
        adm.MapPost("/upgrades/lock", async (UpgradeLockRequest r, AppDb db, AuditService audit, Live live, ClaimsPrincipal u) =>
        {
            var id = (r.WingetId ?? "").Trim();
            if (id.Length == 0) return Results.BadRequest(new { error = "Mangler program-id" });
            var m = r.MachineId is int mid ? await db.Machines.FindAsync(mid) : null;
            var global = await UpgradeLocks.Global(db);
            if (r.Locked)
            {
                if (r.Scope == "all") { global.Add(id); await UpgradeLocks.SetGlobal(db, global); }
                else if (m is not null) { if (!m.UpgradeExclude.Contains(id, StringComparer.OrdinalIgnoreCase)) m.UpgradeExclude = [.. m.UpgradeExclude, id]; }
                else return Results.BadRequest(new { error = "Vælg en maskine" });
            }
            else
            {
                await UpgradeLocks.SetGlobal(db, global.Where(x => !x.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList());
                if (m is not null) m.UpgradeExclude = m.UpgradeExclude.Where(x => !x.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList();
                else foreach (var x in await db.Machines.Where(x => x.UpgradeExclude.Count > 0).ToListAsync())
                    x.UpgradeExclude = x.UpgradeExclude.Where(y => !y.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            await db.SaveChangesAsync();
            await audit.Log(u.UserName(), r.Locked ? "upgrade.lock" : "upgrade.unlock", $"{r.Name ?? id}{(r.Locked ? (r.Scope == "all" ? " (alle maskiner)" : $" ({m?.Name})") : "")}");
            await live.Changed("machines");
            return Results.Ok();
        });

        // Skjul opdateringer, der aldrig skal installeres, eller vis dem igen. Windows Update: på pc'en (IsHidden, et job).
        // Alt andet (firmware, macOS, Linux-pakker …): i Fleetdeck selv (UpgradeMarks) – huskes, også når agenten sender et nyt inventar.
        adm.MapPost("/machines/{id:int}/updates/hide", async (int id, UpdateHideRequest r, AppDb db, JobService jobs, AuditService audit, Live live, ClaimsPrincipal u) =>
        {
            if (await db.Machines.FindAsync(id) is not { } m) return Results.NotFound();
            if (m.Platform != Platforms.Windows)
            {
                var own = UpgradeMarks.Valid(r.Ids);
                var hidden = own.Count == 0 ? [] : await UpgradeMarks.SetHidden(db, id, own, r.Hide, u.UserName(), DateTime.UtcNow);
                if (hidden.Count == 0) return Results.BadRequest(new { error = "Vælg en opdatering" });
                var t = UpdateHide.Title(r.Hide, hidden, hidden.Count, inFleetdeck: true);
                await audit.Log(u.UserName(), r.Hide ? "update.hide" : "update.unhide", $"{m.Label}: {string.Join(", ", hidden)}", m.Id);
                await live.Changed("machines", id);
                return Results.Ok(new { title = t, done = true });
            }
            var ids = UpdateHide.Valid(r.Ids);
            if (ids.Count == 0) return Results.BadRequest(new { error = "Vælg en Windows Update-opdatering" });
            var names = await db.Upgrades.IgnoreQueryFilters().AsNoTracking().Where(x => x.MachineId == id && ids.Contains(x.WingetId)).Select(x => x.Name).ToListAsync();
            var title = UpdateHide.Title(r.Hide, names, ids.Count);
            await jobs.Create(id, JobTypes.WuHide, title, new JobParams { UpdateIds = ids, Hide = r.Hide }, RunAs.System, null, "manual", u.UserName());
            await audit.Log(u.UserName(), r.Hide ? "update.hide" : "update.unhide", $"{m.Label}: {string.Join(", ", names.DefaultIfEmpty(string.Join(", ", ids)))}", m.Id);
            await live.Changed("machines", id);
            return Results.Ok(new { title });
        });

        // Unraid: start/stop/genstart/opdatér containere og VM'er. Hver handling er et script-job; bagefter hentes helbred,
        // så Unraid-fanen viser den nye tilstand.
        adm.MapPost("/machines/{id:int}/unraid", async (int id, UnraidActionRequest r, AppDb db, JobService jobs, AuditService audit, ClaimsPrincipal u) =>
        {
            if (await db.Machines.FindAsync(id) is not { } m) return Results.NotFound();
            if (r.Kind != "refresh" && (string.IsNullOrWhiteSpace(r.Name) || !System.Text.RegularExpressions.Regex.IsMatch(r.Name, @"^[\w .()\-]{1,80}$")))
                return Results.Problem("Ugyldigt navn", statusCode: 400);
            var q = "'" + (r.Name ?? "").Replace("'", "'\\''") + "'";
            const string upd = "/usr/local/emhttp/plugins/dynamix.docker.manager/scripts/update_container";
            (string Title, string Body)? job = (r.Kind, r.Action) switch
            {
                ("docker", "start") => ($"Start container {r.Name}", $"docker start {q}"),
                ("docker", "stop") => ($"Stop container {r.Name}", $"docker stop {q}"),
                ("docker", "restart") => ($"Genstart container {r.Name}", $"docker restart {q}"),
                ("docker", "update") => ($"Opdatér container {r.Name}", $"{upd} {q}"),
                ("vm", "start") => ($"Start VM {r.Name}", $"virsh start {q}"),
                ("vm", "shutdown") => ($"Luk VM {r.Name} ned", $"virsh shutdown {q}"),
                ("vm", "reboot") => ($"Genstart VM {r.Name}", $"virsh reboot {q}"),
                ("vm", "destroy") => ($"Tving VM {r.Name} slukket", $"virsh destroy {q}"),
                ("vm", "resume") => ($"Genoptag VM {r.Name}", $"virsh resume {q}"),
                ("docker", "update_all") => null,   // agentens "Opdatér alt" på Unraid: plugins og containere med en ny version
                ("refresh", _) => null,
                _ => throw new BadHttpRequestException("Ukendt handling"),
            };
            if (r.Kind == "docker" && r.Action == "update_all")
                await jobs.Create(m.Id, JobTypes.Upgrade, "Opdatér alle containere og plugins", new JobParams(), RunAs.System, null, "manual", u.UserName(), save: false);
            if (job is { } j)
                await jobs.Create(m.Id, JobTypes.Script, j.Title, new JobParams { Body = j.Body, TimeoutMin = r.Action is "update" or "update_all" ? 60 : 10 }, RunAs.System, null, "manual", u.UserName(), save: false);
            await jobs.Create(m.Id, JobTypes.Health, "Opdatér Unraid-oversigt", new JobParams(), RunAs.System, null, "manual", u.UserName(), save: false);
            await jobs.Commit(m.Id);
            await audit.Log(u.UserName(), "unraid.action", $"{m.Label}: {r.Kind} {r.Action} {r.Name}".Trim());
            return Results.Ok(new { ok = true });
        });

        // Udvidelser: hvad findes til maskinen, hvad er installeret – og installér med ét klik
        api.MapGet("/machines/{id:int}/extensions", async (int id, AppDb db, ExtensionService ext) =>
            await db.Machines.FindAsync(id) is { } m ? Results.Ok(await ext.For(m)) : Results.NotFound());
        adm.MapPost("/machines/{id:int}/extensions/{ext}", async (int id, string ext, AppDb db, ExtensionService svc, AuditService audit, Live live, ClaimsPrincipal u) =>
        {
            if (await db.Machines.FindAsync(id) is not { } m) return Results.NotFound();
            try
            {
                var n = await svc.Install(m, ext, "manual", u.UserName());
                await audit.Log(u.UserName(), "extension.install", $"{m.Label}: {ext}");
                await live.Changed("jobs", m.Id);
                return Results.Ok(new { jobs = n });
            }
            catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException) { return Results.Problem(e.Message, statusCode: 400); }
        });

        // -------------------------------------------------------------- oversigt
        api.MapGet("/dashboard", async (AppDb db, MachineViews views, WinSetupStore store) =>
        {
            var ms = await views.Rows();
            var since = DateTime.UtcNow.AddDays(-1);
            var jobs24 = await db.Jobs.Where(j => j.Created >= since).GroupBy(j => j.Status).Select(g => new { g.Key, n = g.Count() }).ToListAsync();
            return new
            {
                machines = ms,
                totals = new
                {
                    machines = ms.Count, online = ms.Count(m => (bool)m["online"]!),
                    compliant = ms.Count(m => m["compliance"] is ComplianceSummary c && c.Missing + c.Failed == 0),
                    upgrades = ms.Sum(m => (int)m["upgrades"]!), healthy = ms.Count(m => (string?)m["healthState"] == "ok"),
                    jobsActive = await db.Jobs.CountAsync(j => JobStatus.Active.Contains(j.Status)),
                    jobsFailed24h = jobs24.Where(x => x.Key == JobStatus.Failed).Sum(x => x.n),
                    jobsOk24h = jobs24.Where(x => x.Key == JobStatus.Success).Sum(x => x.n),
                },
                alerts = await AlertList(db, null, true, 12),
                running = await db.Jobs.AsNoTracking().Where(j => j.Status == JobStatus.Running).OrderBy(j => j.Started)
                    .Select(j => new { j.Id, j.Title, j.Type, j.Status, j.Created, j.Started, j.MachineId, machine = j.Machine!.DisplayName ?? j.Machine.Name, j.Progress, j.Step, j.StepSince }).ToListAsync(),
                queued = await db.Jobs.AsNoTracking().Where(j => j.Status == JobStatus.Pending || j.Status == JobStatus.WaitingUser).OrderBy(j => j.Id).Take(20)
                    .Select(j => new { j.Id, j.Title, j.Type, j.Status, j.Created, j.MachineId, machine = j.Machine!.DisplayName ?? j.Machine.Name }).ToListAsync(),
                recentJobs = await db.Jobs.AsNoTracking().OrderByDescending(j => j.Id).Take(12)
                    .Select(j => new { j.Id, j.Title, j.Status, j.Created, j.Finished, j.MachineId, machine = j.Machine!.DisplayName ?? j.Machine.Name }).ToListAsync(),
                backups = store.Backups().Values.OrderBy(b => b.Name),
                // gennemførte installationer/opdateringer pr. dag de sidste 7 dage (til ugegrafen)
                week = await Week(db),
            };
        });

        // -------------------------------------------------------------- maskiner
        api.MapGet("/machines", (MachineViews v) => v.Rows());
        // Netværksdrev: delte mapper at vælge imellem, og drev pr. profil eller maskine
        api.MapGet("/drives/shares", (AppDb db) => Drives.Shares(db));
        api.MapGet("/drives", async (int? profileId, int? machineId, AppDb db) =>
            await db.NetworkDrives.AsNoTracking().Where(d => !d.Ignored && ((profileId != null && d.ProfileId == profileId) || (machineId != null && d.MachineId == machineId)))
                .OrderBy(d => d.Letter ?? "~").ThenBy(d => d.Name).ToListAsync());
        adm.MapPost("/drives", async (NetworkDriveDto d, AppDb db, AuditService audit, Live live, ClaimsPrincipal u) =>
        {
            if ((d.ProfileId is null) == (d.MachineId is null)) return Bad("Vælg enten en profil eller en maskine");
            if (string.IsNullOrWhiteSpace(d.Host) || string.IsNullOrWhiteSpace(d.Share)) return Bad("Vælg en delt mappe, eller skriv \\\\server\\mappe");
            // server: navn eller IP; mappe: ét navn (ingen undermapper)
            if (!System.Text.RegularExpressions.Regex.IsMatch(d.Host.Trim(), @"^[A-Za-z0-9]([A-Za-z0-9.\-]{0,252})$") || d.Share.Trim().IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0)
                return Bad("Skriv stien som \\\\server\\mappe (fx \\\\nas\\fotos)");
            var letter = string.IsNullOrWhiteSpace(d.Letter) ? null : d.Letter.Trim().TrimEnd(':').ToUpperInvariant();
            if (letter is not null && (letter.Length != 1 || letter[0] < 'D' || letter[0] > 'Z')) return Bad("Drevbogstavet skal være D–Z");
            var same = db.NetworkDrives.Where(x => x.ProfileId == d.ProfileId && x.MachineId == d.MachineId);
            if (letter is not null && await same.AnyAsync(x => x.Letter == letter)) return Bad($"{letter}: er allerede brugt");
            if (await same.AnyAsync(x => x.Host == d.Host.Trim() && x.Share == d.Share.Trim())) return Bad("Den mappe er allerede tilføjet");
            var nd = new NetworkDrive
            {
                ProfileId = d.ProfileId, MachineId = d.MachineId, Host = d.Host.Trim(), Share = d.Share.Trim(), Letter = letter,
                Name = Drives.SafeName(string.IsNullOrWhiteSpace(d.Name) ? d.Share : d.Name), OwnLogin = d.OwnLogin == true, Created = DateTime.UtcNow,
            };
            db.NetworkDrives.Add(nd);
            await db.SaveChangesAsync();
            await audit.Log(u.UserName(), "drive.add", $"\\\\{nd.Host}\\{nd.Share} som {(letter is null ? nd.Name : letter + ":")} ({(d.ProfileId is not null ? $"profil {d.ProfileId}" : $"maskine {d.MachineId}")})");
            await live.Changed("machines");
            return Results.Ok(nd);
        });
        adm.MapDelete("/drives/{id:int}", async (int id, AppDb db, AuditService audit, Live live, ClaimsPrincipal u) =>
        {
            var d = await db.NetworkDrives.FindAsync(id);
            if (d is null) return Results.NotFound();
            // et fundet drev slettes ikke på maskinen – Fleetdeck holder bare op med at holde øje (og samler det ikke op igen)
            if (d.Adopted) d.Ignored = true; else db.NetworkDrives.Remove(d);
            await db.SaveChangesAsync();
            await audit.Log(u.UserName(), "drive.remove", $"\\\\{d.Host}\\{d.Share}");
            await live.Changed("machines");
            return Results.Ok();
        });

        // Fjernskrivebord: ID, fast adgangskode og et rustdesk://-link, der forbinder direkte (kun administratorer, logges)
        adm.MapGet("/machines/{id:int}/remote", async (int id, AppDb db, AuditService audit, JobService jobs, ClaimsPrincipal u) =>
        {
            var m = await db.Machines.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (m is null) return Results.NotFound();
            var r = Json.Des<HealthReport>(m.HealthJson).Remote;
            if (r is not { Id.Length: > 0 }) return Bad("Fjernskrivebord er ikke sat op på maskinen endnu (installér udvidelsen Fjernskrivebord)");
            await audit.Log(u.UserName(), "remote.connect", $"{m.Label} (RustDesk {r.Id})");
            // væk skærmen først (en sovende kiosk viser ellers sort), medmindre det allerede er bedt om
            if (!await db.Jobs.AnyAsync(j => j.MachineId == id && j.Type == JobTypes.WakeScreen && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running)))
                await jobs.Create(id, JobTypes.WakeScreen, "Væk skærmen (fjernskrivebord)", new JobParams(), RunAs.System, null, "remote", u.UserName());
            var link = $"rustdesk://connection/new/{r.Id}" + (r.Password is { Length: > 0 } pw ? $"?password={Uri.EscapeDataString(pw)}" : "");
            return Results.Ok(new { r.Tool, r.Id, r.Password, link });
        });

        // Programikon (delt mellem maskiner, ændres aldrig for samme id)
        api.MapGet("/icons/{hash}", async (string hash, AppDb db, HttpContext ctx) =>
        {
            var png = await db.SoftwareIcons.AsNoTracking().Where(i => i.Hash == hash).Select(i => i.Png).FirstOrDefaultAsync();
            if (png is null) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
            return Results.File(png, "image/png");
        });

        api.MapGet("/machines/{id:int}", async (int id, AppDb db, MachineViews views, ComplianceService comp, AgentBinary agent, SettingsService settings) =>
        {
            var m = await db.Machines.AsNoTracking().Include(x => x.Profile).FirstOrDefaultAsync(x => x.Id == id);
            if (m is null) return Results.NotFound();
            var row = await views.Row(m, await views.Poll());
            row["facts"] = Json.Des<SystemFacts>(m.FactsJson);
            var health = Json.Des<HealthReport>(m.HealthJson);
            if (health.Remote is { } rd) rd.Password = null;   // adgangskoden hentes kun via "Forbind" (administrator)
            row["health"] = health;
            // netværksdrev: hvad maskinen skal have (profil + egne) og status fra maskinen
            row["drives"] = (await Drives.For(db, m)).Select(d => new
            {
                d.Id, d.Host, d.Share, d.Letter, d.Name, fromProfile = d.ProfileId != null, adopted = d.Adopted, ownLogin = d.OwnLogin,
                state = health.Drives.FirstOrDefault(x => x.Key == Drives.Key(d)),
            }).ToList();
            // printere: dem fra Fleetdeck med status fra maskinen, og maskinens egne
            row["printers"] = Fleetdeck.Server.Services.Printers.PrinterService.Supported(m, health)
                ? Fleetdeck.Server.Services.Printers.PrinterService.MachineRows(await Fleetdeck.Server.Services.Printers.PrinterService.For(db, m), health, m.Platform != Platforms.Windows)
                : null;
            row["notes"] = m.Notes; row["enrolled"] = m.Enrolled; row["lastInventory"] = m.LastInventory; row["lastHealth"] = m.LastHealth;
            row["complianceItems"] = await comp.Evaluate(id);
            // beskrivelse fra agenten, ellers fra kataloget (Windows-programmer har sjældent en)
            row["software"] = (await db.Software.Where(s => s.MachineId == id).OrderBy(s => s.Name)
                .Select(s => new { s.Name, s.Version, s.Publisher, s.Scope, s.Description, s.InstallPath, s.Exe, s.IconHash, s.UninstallId }).ToListAsync())
                .Select(s => new { s.Name, s.Version, s.Publisher, s.Scope, Description = s.Description ?? AppCatalog.DescriptionFor(s.Name), s.InstallPath, s.Exe, s.IconHash,
                    canRemove = s.Scope is not ("unraid" or "plugin" or "container") });
            var locks = await UpgradeLocks.For(db, id);
            // dem, der ikke kan installeres, står med (med grunden), men tæller ikke med og har ingen opdatér-knap
            row["upgradeList"] = (await db.Upgrades.IgnoreQueryFilters().Where(u => u.MachineId == id && !u.Hidden).OrderBy(u => u.Name)
                .Select(u => new { u.Name, u.WingetId, u.Current, u.Available, u.Driver, u.Advice, u.Blocked }).ToListAsync())
                .Select(u => new { u.Name, u.WingetId, u.Current, u.Available, u.Driver, u.Advice, u.Blocked, locked = locks.GetValueOrDefault(u.WingetId) });
            // skjult i Windows Update eller i Fleetdeck – kan vises igen
            row["hiddenUpgrades"] = await db.Upgrades.IgnoreQueryFilters().AsNoTracking().Where(u => u.MachineId == id && u.Hidden).OrderBy(u => u.Name)
                .Select(u => new { u.Name, u.WingetId, u.Current, u.Available, u.Driver, u.Advice }).ToListAsync();
            // "Opdatér alt" bagefter: det, der blev sprunget over, og hvorfor
            row["upgradeAll"] = await UpgradeAllReport.ForMachine(db, id, locks);
            row["jobs"] = (await db.Jobs.Where(j => j.MachineId == id).OrderByDescending(j => j.Id).Take(100)
                .Select(j => new { j.Id, j.MachineId, j.Type, j.Title, j.Status, j.Source, j.RunAs, j.Created, j.Started, j.Finished, j.ExitCode, j.CreatedBy, j.Progress, j.Step, j.StepSince, j.ParamsJson }).ToListAsync())
                .Select(j => new
                {
                    j.Id, j.MachineId, j.Type, j.Title, j.Status, j.Source, j.RunAs, j.Created, j.Started, j.Finished, j.ExitCode, j.CreatedBy, j.Progress, j.Step,
                    // hvilket program et "Opdatér"-job gælder (null = alle), så Opdateringer kan vise status pr. program
                    wingetId = j.Type is JobTypes.Upgrade or JobTypes.Install or JobTypes.Uninstall ? Json.Des<JobParams>(j.ParamsJson).WingetId : null,
                }).ToList();
            row["alertList"] = await AlertList(db, id, false, 50);
            row["maintenance"] = await MaintenanceView(db, id);
            row["autoUpdate"] = new
            {
                off = m.Profile is not { } ap || AutoUpdateOff(ap),
                noProfile = m.Profile is null,
                blocked = AutoUpdateGuard.Blocked(m, m.Profile),
            };
            row["ignoredAlerts"] = IgnoredAlerts.OnMachine(m).Select(x => new { x.Key, x.Title, x.By, x.At, all = false })
                .Concat((await IgnoredAlerts.OnAll(settings)).Select(x => new { x.Key, x.Title, x.By, x.At, all = true })).ToList();
            row["serverAgentVersion"] = agent.VersionFor(m.Platform);
            if (KeepAwakeSync.Applies(m))
                row["keepAwake"] = new { on = m.KeepAwakeOnAc, at = m.KeepAwakeAt, source = m.KeepAwakeSource, by = m.KeepAwakeBy, live = health.Live.KeepAwake };
            return Results.Ok(row);
        });

        api.MapGet("/machines/{id:int}/metrics", async (int id, int? hours, AppDb db) =>
        {
            var since = DateTime.UtcNow.AddHours(-(hours ?? 24));
            return await db.Metrics.Where(x => x.MachineId == id && x.Ts >= since).OrderBy(x => x.Ts)
                .Select(x => new { x.Ts, x.Cpu, x.Ram, x.Disk, x.CpuTemp, x.GpuTemp }).ToListAsync();
        });

        adm.MapPut("/machines/{id:int}", async (int id, MachineUpdate r, AppDb db, ComplianceService comp, HealthService health, AuditService audit, Live live, ClaimsPrincipal u) =>
        {
            var m = await db.Machines.FindAsync(id);
            if (m is null) return Results.NotFound();
            var profileChanged = m.ProfileId != r.ProfileId;
            m.ProfileId = r.ProfileId;
            if (r.Tags is not null) m.Tags = r.Tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().ToList();
            if (r.Notes is not null) m.Notes = r.Notes;
            await db.SaveChangesAsync();
            var created = profileChanged ? await comp.Enforce(id, u.UserName()) : 0;
            await health.Evaluate(id);
            await audit.Log(u.UserName(), "machine.update", $"{m.Label}: profil {r.ProfileId}, tags {string.Join(",", m.Tags)}");
            await live.Changed("machines", id);
            return Results.Ok(new { jobsCreated = created });
        });

        // Omdøb: et visningsnavn, som agenten aldrig overskriver (værtsnavnet er stadig maskinens identitet)
        adm.MapPut("/machines/{id:int}/name", async (int id, MachineRename r, AppDb db, AuditService audit, Live live, ClaimsPrincipal u) =>
        {
            var res = await MachineNames.Rename(db, id, r.Name);
            if (res.Status == 404) return Results.NotFound();
            if (res.Error is not null) return Results.Problem(res.Error, statusCode: res.Status);
            var m = res.Machine!;
            if (res.Old != m.Label)
                await audit.Log(u.UserName(), "machine.rename", $"{res.Old} → {m.Label}{(m.DisplayName is null ? " (værtsnavnet igen)" : $" (værtsnavn {m.Name})")}");
            await live.Changed("machines", id);
            return Results.Ok(new { name = m.Label, hostname = m.Name, renamed = m.DisplayName is not null });
        });

        adm.MapDelete("/machines/{id:int}", async (int id, AppDb db, AuditService audit, Live live, ClaimsPrincipal u) =>
        {
            var m = await db.Machines.FindAsync(id);
            if (m is null) return Results.NotFound();
            db.Machines.Remove(m);
            await db.SaveChangesAsync();
            await audit.Log(u.UserName(), "machine.delete", m.Label);
            await live.Changed("machines");
            return Results.Ok();
        });

        // Webflade, som "Forbind" åbner (tom = ingen / Unraids egen)
        adm.MapPut("/machines/{id:int}/weburl", async (int id, WebUrlRequest r, AppDb db, AuditService audit, Live live, ClaimsPrincipal user) =>
        {
            if (await db.Machines.FindAsync(id) is not { } m) return Results.NotFound();
            var u = (r.Url ?? "").Trim();
            if (u.Length > 0 && !(Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")) return Results.BadRequest(new { error = "Skriv en hel adresse, fx http://192.168.1.12:8080" });
            m.WebUrl = u.Length == 0 ? null : u;
            await db.SaveChangesAsync();
            await audit.Log(user.UserName(), "machine.weburl", $"{m.Label}: {(u.Length == 0 ? "fjernet" : u)}", m.Id);
            await live.Changed("machines", id);
            return Results.Ok();
        });

        // Gendannelsespunkter for en maskine
        api.MapGet("/machines/{id:int}/kits", (int id, Paths paths) => Results.Ok(Kits.List(paths, id)));
        // alle maskiners gendannelsespunkter (til gendannelse på en ny maskine)
        api.MapGet("/kits", async (AppDb db, Paths paths) =>
        {
            var names = await db.Machines.AsNoTracking().Select(m => new { m.Id, Name = m.DisplayName ?? m.Name }).ToDictionaryAsync(m => m.Id, m => m.Name);
            return Results.Ok(names.Select(kv => new { machineId = kv.Key, machine = kv.Value, kits = Kits.List(paths, kv.Key) }).Where(x => x.kits.Any()));
        });

        // Intel AMT: status (fra maskinen) og strømtilstand (direkte fra AMT)
        api.MapGet("/machines/{id:int}/amt", async (int id, AppDb db, AmtService amt, CancellationToken ct) =>
            await db.Machines.FindAsync([id], ct) is { } m ? Results.Ok(await amt.Status(m, ct)) : Results.NotFound());

        adm.MapPost("/machines/actions", async (ActionRequest r, AppDb db, JobService jobs, ComplianceService comp, ProvisionService prov,
            MaintenanceService maint, AuditService audit, Live live, AmtService amt, Paths paths, ClaimsPrincipal u) =>
        {
            var batch = $"m:{DateTime.UtcNow.Ticks}";
            var p = r.Params ?? new ActionParams();
            var by = u.UserName();
            var created = 0;
            var blocked = new List<string>();
            if (r.MachineIds is not { Count: > 0 }) return Bad("Vælg mindst én maskine");
            foreach (var mid in r.MachineIds.Distinct())
            {
                var m = await db.Machines.Include(x => x.Profile).FirstOrDefaultAsync(x => x.Id == mid);
                if (m is null) continue;
                switch (r.Action)
                {
                    case JobTypes.Install or JobTypes.Uninstall:
                        var pkg = await db.Packages.FindAsync(p.PackageId ?? 0);
                        if (pkg is null) return Bad("Vælg en pakke");
                        await jobs.ForPackage(mid, pkg, r.Action, "manual", by); created++;
                        break;
                    case JobTypes.Upgrade:
                        await jobs.Create(mid, JobTypes.Upgrade, p.WingetId is null ? "Opdatér alle programmer" : $"Opdatér {p.Name ?? p.WingetId}",
                            new JobParams { WingetId = p.WingetId, Name = p.Name, Exclude = m.Profile?.UpgradeExclude ?? [] }, RunAs.System, null, "manual", by); created++;
                        break;
                    case "update_all":
                    {
                        // "Opdatér alt" med valg: automatisk = system + programmer + genstart (med nedtælling), manuelt = kun programmer.
                        // Unraid-servere genstartes aldrig af sig selv og har ingen systemopdatering her.
                        var win = m.Platform == Platforms.Windows; var mac = m.Platform == Platforms.Mac;
                        var nas = m.FactsJson?.Contains("unraid", StringComparison.OrdinalIgnoreCase) == true;
                        var auto = p.Mode != "manual" && !nas;
                        var sys = win ? "Windows-opdateringer" : mac ? "macOS-opdateringer" : "systemopdateringer";
                        var (dev, devL) = win ? ("Pc'en", "pc'en") : mac ? ("Mac'en", "Mac'en") : ("Maskinen", "maskinen");
                        var rounds = UpdateRounds.Clamp(m.Profile?.UpdMaxRounds ?? UpdateRounds.DefaultMax);
                        // samme regler som MaintenanceService.RebootParams for "manual": altid 5 minutters nedtælling før hver genstart
                        var text = auto
                            ? $"{dev} bliver opdateret nu: {sys} og alle programmer. Åbne programmer kan blive lukket et øjeblik. Kræver opdateringerne en genstart, genstarter {devL} selv – op til {rounds} gange, til alt er installeret – og hver gang får du først en nedtælling på 5 minutter på skærmen, så du kan gemme dit arbejde."
                            : $"Programmerne på {devL} bliver opdateret nu. Åbne programmer kan blive lukket et øjeblik. {dev} genstarter ikke af sig selv.";
                        // beskyttede maskiner (og profiler med "aldrig automatisk") genstartes aldrig af "Opdatér alt"
                        if (auto && AutoUpdateGuard.Blocked(m, m.Profile) is { } blockedWhy)
                        {
                            if (r.MachineIds.Count == 1) return Bad($"{m.Label}: {blockedWhy}. Brug Opdatér ud for den enkelte linje.");
                            continue;
                        }
                        await jobs.Create(mid, JobTypes.Message, "Opdatér alt: besked på skærmen", new JobParams { Text = text, Notice = true }, RunAs.System, null, "manual", by, false);
                        if (auto)
                        {
                            await jobs.Commit(mid);
                            // automatisk = opdateringsmotoren: fast rækkefølge, runder og genstart med nedtælling, indtil intet er tilbage
                            var updRun = await maint.Start(mid, new MaintPlan(false, ["windows_update", "upgrade"], "reboot", false), $"{batch}:silent", by, "manual");
                            created += 1 + (updRun?.JobIds.Count ?? 0);
                            break;
                        }
                        await jobs.Create(mid, JobTypes.Upgrade, "Opdatér alt: alle programmer", new JobParams { Exclude = m.Profile?.UpgradeExclude ?? [] }, RunAs.System, null, "manual", by, false);
                        await jobs.Commit(mid);
                        created += 2;
                        break;
                    }
                    case JobTypes.WindowsUpdate:
                        await jobs.Create(mid, JobTypes.WindowsUpdate, m.Platform == Platforms.Windows ? "Installér Windows-opdateringer" : m.Platform == Platforms.Mac ? "Installér macOS-opdateringer" : "Installér systemopdateringer",
                            new JobParams { IncludeDrivers = p.IncludeDrivers },
                            RunAs.System, null, "manual", by); created++;
                        break;
                    case JobTypes.Script:
                        string name, body, runAs;
                        if (p.ScriptId is int sid)
                        {
                            var s = await db.Scripts.FindAsync(sid);
                            if (s is null) return Bad("Scriptet findes ikke");
                            (name, body, runAs) = (s.Name, s.Body, s.RunAs);
                        }
                        else (name, body, runAs) = (p.Name ?? "Ad hoc-script", p.Body ?? "", p.RunAs ?? RunAs.System);
                        if (!RunAs.All.Contains(runAs)) return Bad("Ugyldig kørselskontekst");
                        await jobs.Create(mid, JobTypes.Script, $"Script: {name}", new JobParams { Body = body, TimeoutMin = p.TimeoutMin ?? 30 }, runAs, null, "manual", by); created++;
                        break;
                    case JobTypes.Reboot or JobTypes.Shutdown:
                        // en klar BIOS installeres ved næste opstart: den genstart tages fra BIOS-siden (netstrøm, én ad gangen, sporet)
                        if (await Services.Bios.BiosUpdateService.Waiting(db, mid))
                        {
                            if (r.MachineIds.Count == 1) return Bad($"{m.Label}: {Services.Bios.BiosUpdateService.WaitingText}");
                            blocked.Add($"{m.Label}: en BIOS-opdatering ligger klar – genstart den fra BIOS-siden");
                            continue;
                        }
                        await jobs.Create(mid, r.Action, r.Action == JobTypes.Reboot ? "Genstart" : "Sluk",
                            new JobParams { Delay = p.Delay ?? 60, OnlyIfPending = p.OnlyIfPending ?? false, Message = p.Message }, RunAs.System, null, "manual", by); created++;
                        break;
                    case JobTypes.Message:
                        await jobs.Create(mid, JobTypes.Message, "Besked til brugeren", new JobParams { Text = p.Text }, RunAs.System, null, "manual", by); created++;
                        break;
                    case JobTypes.Lock or JobTypes.Logoff:
                        await jobs.Create(mid, r.Action, r.Action == JobTypes.Lock ? "Lås skærmen" : "Log brugeren af", new JobParams(), RunAs.System, null, "manual", by); created++;
                        break;
                    case JobTypes.Inventory or JobTypes.Health:
                        await jobs.Create(mid, r.Action, r.Action == JobTypes.Inventory ? "Synkronisér inventar og helbred" : "Tjek helbred", new JobParams(), RunAs.System, null, "manual", by); created++;
                        break;
                    case JobTypes.WinSetup:
                        if (p.Step is null || !JobService.WinSetupSteps.ContainsKey(p.Step)) return Bad("Ukendt WinSetup-trin");
                        await jobs.WinSetup(mid, p.Step, "manual", by); created++;
                        break;
                    case JobTypes.KillHeadless:
                        await jobs.Create(mid, JobTypes.KillHeadless, "Luk hovedløse browsere", new JobParams(), RunAs.System, null, "manual", by); created++;
                        break;
                    case JobTypes.RemoveApp:
                    {
                        // afinstallér et program fra programlisten: agenten finder selv metoden ud fra registreringen/pakken
                        if (string.IsNullOrWhiteSpace(p.Name)) return Bad("Vælg et program");
                        var sw = await db.Software.AsNoTracking().FirstOrDefaultAsync(x => x.MachineId == mid && x.Name == p.Name);
                        if (sw is null) return Bad($"{p.Name} findes ikke længere på {m.Label} – synkronisér inventaret");
                        await jobs.Create(mid, JobTypes.RemoveApp, $"Afinstallér {sw.Name}", new JobParams
                        {
                            Name = sw.Name, PackageType = sw.Scope, Identifier = sw.UninstallId, Path = sw.Exe, TimeoutMin = 60,
                        }, RunAs.System, null, "manual", by); created++;
                        break;
                    }
                    case JobTypes.CancelPower:
                        await jobs.Create(mid, JobTypes.CancelPower, p.Delay is > 0 ? $"Udsæt genstart/slukning {p.Delay / 60} min." : "Annullér genstart/slukning",
                            new JobParams { Delay = p.Delay }, RunAs.System, null, "manual", by); created++;
                        break;
                    case JobTypes.RemoveAgent:
                        await jobs.Create(mid, JobTypes.RemoveAgent, "Fjern agenten fra maskinen", new JobParams(), RunAs.System, null, "manual", by); created++;
                        break;
                    case "provision":
                        created += await prov.Queue(mid, by);
                        break;
                    case "enforce":
                        m.InventoryRequested = true; await db.SaveChangesAsync();
                        created += await comp.Enforce(mid, by);
                        break;
                    case "assign_profile":
                        m.ProfileId = p.ProfileId; await db.SaveChangesAsync();
                        created += await comp.Enforce(mid, by);
                        break;
                    case "wake":
                    {
                        var sent = await WakeOnLan.Send(m.Mac, m.Ip);
                        // har den Intel AMT sat op, tændes den også den vej (virker selv hvis Wake-on-LAN ikke gør)
                        var viaAmt = !string.IsNullOrEmpty(m.AmtPassword) && (await amt.Power(m, AmtClient.On)).Ok;
                        if (!sent && !viaAmt) return Bad($"{m.Label} har ingen kendt MAC-adresse endnu");
                        break;
                    }
                    case "kit_backup":
                    {
                        if (m.Platform == Platforms.Windows) return Bad("Gendannelsespunkter findes til Linux-maskiner");
                        var kp = m.Profile?.KitPaths ?? [];
                        await jobs.Create(mid, JobTypes.KitBackup, "Tag gendannelsespunkt", new JobParams { Paths = kp }, RunAs.System, null, "manual", by); created++;
                        break;
                    }
                    case "kit_restore":
                    {
                        if (Kits.SafeName(p.Name) is not { } kn) return Bad("Vælg et gendannelsespunkt");
                        if (m.Platform == Platforms.Windows) return Bad("Gendannelsespunkter findes til Linux-maskiner");
                        if (p.FromMachineId is int src && src != mid)
                        {
                            // pakken kopieres over til den nye maskine, så den kan hente den
                            var from = Path.Combine(Kits.Dir(paths, src), kn);
                            if (!File.Exists(from)) return Bad("Gendannelsespunktet findes ikke");
                            Directory.CreateDirectory(Kits.Dir(paths, mid));
                            File.Copy(from, Path.Combine(Kits.Dir(paths, mid), kn), true);
                            if (File.Exists(from + ".check.json")) File.Copy(from + ".check.json", Path.Combine(Kits.Dir(paths, mid), kn + ".check.json"), true);
                        }
                        else if (!File.Exists(Path.Combine(Kits.Dir(paths, mid), kn))) return Bad("Gendannelsespunktet findes ikke");
                        await jobs.Create(mid, JobTypes.KitRestore, $"Gendan fra {kn} (overskriver intet)", new JobParams { Name = kn }, RunAs.System, null, "manual", by); created++;
                        break;
                    }
                    case "amt_activate":
                    {
                        var info = AmtService.Info(m);
                        if (info is not { Present: true }) return Bad($"{m.Label} har ikke Intel AMT (eller har ikke meldt det endnu)");
                        if (info.Activated && !string.IsNullOrEmpty(m.AmtPassword)) return Bad($"Intel AMT er allerede sat op på {m.Label}");
                        if (info.Activated) return Bad($"Intel AMT er allerede aktiveret på {m.Label} med en anden adgangskode – nulstil den i MEBx (Ctrl+P ved opstart) først");
                        var pw = AmtClient.NewPassword();
                        amt.SetPassword(m, pw);
                        await jobs.Create(mid, JobTypes.AmtActivate, "Opsæt Intel AMT (klient-tilstand)", new JobParams { Text = pw }, RunAs.System, null, "manual", by); created++;
                        break;
                    }
                    case "amt_deactivate":
                    {
                        if (amt.Password(m) is not { } pw) return Bad($"Intel AMT er ikke sat op fra Fleetdeck på {m.Label}");
                        await jobs.Create(mid, JobTypes.AmtDeactivate, "Fjern Intel AMT-opsætning", new JobParams { Text = pw }, RunAs.System, null, "manual", by); created++;
                        break;
                    }
                    case "amt_on" or "amt_off" or "amt_reset" or "amt_cycle":
                    {
                        var state = r.Action switch { "amt_on" => AmtClient.On, "amt_off" => AmtClient.OffHard, "amt_reset" => AmtClient.Reset, _ => AmtClient.PowerCycle };
                        // midt i en BIOS-flash må strømmen aldrig afbrydes (sluk, nulstil, sluk og tænd)
                        if (r.Action != "amt_on" && await Services.Bios.BiosUpdateService.MayBeFlashing(db, mid, DateTime.UtcNow))
                        {
                            if (r.MachineIds.Count == 1) return Bad($"{m.Label}: {Services.Bios.BiosUpdateService.FlashingText}");
                            blocked.Add($"{m.Label}: en BIOS-opdatering kan være ved at blive installeret – strømmen afbrydes ikke");
                            continue;
                        }
                        var (ok, msg) = await amt.Power(m, state);
                        if (!ok) return Bad(msg);
                        await audit.Log(by, $"amt.{r.Action[4..]}", m.Label);
                        created++;
                        break;
                    }
                    case "maintenance":
                        var plan = MaintPlan.From(m.Profile);
                        if (p.Tasks is not null) plan = plan with { Tasks = p.Tasks };
                        if (p.After is { } after && MaintPlan.AllAfter.Contains(after)) plan = plan with { After = after };
                        if (await maint.Start(mid, plan, batch, by) is { } run)
                        {
                            // spærret (beskyttet, "aldrig automatisk" eller et beskyttet profilnavn): sig hvorfor i stedet for "0 jobs"
                            if (run.State == "blocked")
                            {
                                if (r.MachineIds.Count == 1) return Bad($"{m.Label}: {run.Summary}.");
                                blocked.Add($"{m.Label}: {run.Summary}");
                            }
                            else created += Math.Max(1, run.JobIds.Count);
                        }
                        break;
                    case "shutdown_when_done":
                        if (await Services.Bios.BiosUpdateService.Waiting(db, mid))
                        {
                            if (r.MachineIds.Count == 1) return Bad($"{m.Label}: {Services.Bios.BiosUpdateService.WaitingText}");
                            blocked.Add($"{m.Label}: en BIOS-opdatering ligger klar – slukkes ikke");
                            continue;
                        }
                        // agenten tager jobs i rækkefølge, så slukningen kommer efter alt det, der allerede står i kø
                        await jobs.Create(mid, JobTypes.Shutdown, "Sluk når opgaverne er færdige",
                            new JobParams { Delay = 120, OnlyIfIdleMin = p.OnlyIfIdleMin, Message = "Fleetdeck: opgaverne er færdige. Pc'en slukker om 2 minutter." },
                            RunAs.System, null, "manual", by); created++;
                        break;
                    default:
                        return Bad("Ugyldig handling");
                }
            }
            await audit.Log(by, $"action.{r.Action}", $"{r.MachineIds.Count} maskine(r) {Json.Ser(p)}");
            await live.Changed("machines");
            return Results.Ok(new { jobsCreated = created, blocked });
        });

        // -------------------------------------------------------------- pakker
        api.MapGet("/packages", async (AppDb db, Paths paths) =>
        {
            var usage = await db.ProfilePackages.GroupBy(pp => pp.PackageId).Select(g => new { g.Key, n = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.n);
            return (await db.Packages.AsNoTracking().OrderBy(p => p.Category).ThenBy(p => p.Name).ToListAsync()).Select(p => new
            {
                p.Id, p.Name, p.Type, p.Identifier, p.Args, p.RunAs, p.Detect, p.Category, p.Notes,
                profiles = usage.GetValueOrDefault(p.Id),
                fileOk = p.Type != PackageTypes.File || (p.Identifier.Length > 0 && File.Exists(Path.Combine(paths.Installers, p.Identifier))),
            });
        });

        static IResult? Validate(PackageDto d)
        {
            if (string.IsNullOrWhiteSpace(d.Name) || !PackageTypes.All.Contains(d.Type)) return Bad("Navn og gyldig type skal udfyldes");
            if (d.RunAs is { Length: > 0 } ra && !RunAs.All.Contains(ra)) return Bad("Ugyldig kørselskontekst");
            if (d.Type == PackageTypes.Steam && !Regex.IsMatch(d.Identifier ?? "", @"^\d+$")) return Bad("Steam-spil skal have et app-id (kun tal), fx 730");
            if (!string.IsNullOrEmpty(d.Detect)) try { _ = new Regex(d.Detect); } catch (ArgumentException e) { return Bad($"Ugyldigt regulært udtryk: {e.Message}"); }
            return null;
        }
        static void Apply(Package p, PackageDto d)
        {
            p.Name = d.Name.Trim(); p.Type = d.Type; p.Identifier = d.Identifier?.Trim() ?? ""; p.Args = d.Args ?? "";
            p.RunAs = PackageTypes.IsUserOnly(d.Type) ? RunAs.User : d.RunAs is { Length: > 0 } ra ? ra : RunAs.System;
            p.Detect = d.Detect ?? ""; p.Category = d.Category ?? (d.Type == PackageTypes.Steam ? "Spil" : ""); p.Notes = d.Notes ?? "";
        }

        adm.MapPost("/packages", async (PackageDto d, AppDb db, AuditService audit, ClaimsPrincipal u) =>
        {
            if (Validate(d) is { } err) return err;
            var p = new Package(); Apply(p, d);
            db.Packages.Add(p);
            await db.SaveChangesAsync();
            await audit.Log(u.UserName(), "package.create", p.Name);
            return Results.Ok(new { p.Id });
        });
        adm.MapPut("/packages/{id:int}", async (int id, PackageDto d, AppDb db, AuditService audit, ClaimsPrincipal u) =>
        {
            if (Validate(d) is { } err) return err;
            var p = await db.Packages.FindAsync(id);
            if (p is null) return Results.NotFound();
            Apply(p, d);
            await db.SaveChangesAsync();
            await audit.Log(u.UserName(), "package.update", p.Name);
            return Results.Ok();
        });
        adm.MapDelete("/packages/{id:int}", async (int id, AppDb db, AuditService audit, ClaimsPrincipal u) =>
        {
            var p = await db.Packages.FindAsync(id);
            if (p is null) return Results.NotFound();
            db.Packages.Remove(p);
            await db.SaveChangesAsync();
            await audit.Log(u.UserName(), "package.delete", p.Name);
            return Results.Ok();
        });

        adm.MapPost("/installers", async (IFormFile file, int? packageId, AppDb db, Paths paths, AuditService audit, ClaimsPrincipal u) =>
        {
            var name = BadFileChars().Replace(Path.GetFileName(file.FileName), "_");
            if (name.Length == 0 || name.StartsWith('.')) return Bad("Ugyldigt filnavn");
            Directory.CreateDirectory(paths.Installers);
            await using (var fs = File.Create(Path.Combine(paths.Installers, name))) await file.CopyToAsync(fs);
            if (packageId is int pid && await db.Packages.FindAsync(pid) is { } p) { p.Identifier = name; p.Type = PackageTypes.File; await db.SaveChangesAsync(); }
            await audit.Log(u.UserName(), "installer.upload", name);
            return Results.Ok(new { name });
        }).DisableAntiforgery();

        api.MapGet("/winget/search", async (string q, WingetIndex idx, AppDb db) =>
        {
            if (q.Trim().Length < 2) return Results.Ok(Array.Empty<object>());
            try
            {
                var known = (await db.Packages.Select(p => p.Identifier).ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var res = await idx.Search(q.Trim());
                return Results.Ok(res.Select(h => new { h.Id, h.Name, h.Version, inCatalog = known.Contains(h.Id), perUser = SeedService.IsPerUser(h.Id) }));
            }
            catch (Exception e) { return Results.Problem($"winget-søgning er ikke tilgængelig: {e.Message}", statusCode: 503); }
        });

        // -------------------------------------------------------------- profiler
        api.MapGet("/profiles", async (AppDb db, SettingsService s) =>
        {
            var def = await s.DefaultProfileId();
            var list = new List<object>();
            foreach (var p in await db.Profiles.AsNoTracking().OrderBy(p => p.Name).ToListAsync())
                list.Add(ProfileOut(p, await db.Machines.CountAsync(m => m.ProfileId == p.Id), await db.ProfilePackages.CountAsync(x => x.ProfileId == p.Id), def));
            return list;
        });
        api.MapGet("/profiles/{id:int}", async (int id, AppDb db, SettingsService s) =>
        {
            var p = await db.Profiles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (p is null) return Results.NotFound();
            var pkgs = await db.ProfilePackages.Where(x => x.ProfileId == id).Include(x => x.Package).OrderBy(x => x.Package!.Category).ThenBy(x => x.Package!.Name)
                .Select(x => new { x.PackageId, x.Intent, x.Package!.Name, x.Package.Type, x.Package.Identifier, x.Package.Category }).ToListAsync();
            var machines = await db.Machines.Where(m => m.ProfileId == id).OrderBy(m => m.DisplayName ?? m.Name).Select(m => new { m.Id, Name = m.DisplayName ?? m.Name }).ToListAsync();
            return Results.Ok(new { profile = ProfileOut(p, machines.Count, pkgs.Count, await s.DefaultProfileId()), packages = pkgs, machineList = machines });
        });

        static IResult? ValidateProfile(ProfileDto d)
        {
            string[] modes = ["off", "daily", "weekly"];
            if (string.IsNullOrWhiteSpace(d.Name)) return Bad("Profilen skal have et navn");
            if (!modes.Contains(d.UpgradeMode) || !modes.Contains(d.BackupMode) || !modes.Contains(d.WindowsUpdateMode)) return Bad("Ugyldig planlægning");
            if (d.MaintMode is { } mm && !modes.Contains(mm)) return Bad("Ugyldig planlægning af vedligeholdelse");
            if (d.MaintAfter is { } ma && !MaintPlan.AllAfter.Contains(ma)) return Bad("Ugyldigt valg efter vedligeholdelse");
            if (d.UpdMaxRounds is < 1 or > UpdateRounds.Limit) return Bad($"Runder skal være mellem 1 og {UpdateRounds.Limit}");
            if (d.UpdCatchUpDays is < 0 or > 14) return Bad("Indhentning skal være mellem 0 og 14 døgn");
            foreach (var q in new[] { d.UpdQuietFrom, d.UpdQuietTo })
                if (q is { Length: > 0 } && !TimeOnly.TryParse(q, out _)) return Bad("Stilletid skal være et klokkeslæt (fx 08:00)");
            if (d.Packages?.Any(x => !Intents.All.Contains(x.Intent)) == true) return Bad("Ugyldigt krav på en pakke");
            return null;
        }
        static void ApplyProfile(Profile p, ProfileDto d)
        {
            p.Name = d.Name.Trim(); p.Description = d.Description ?? ""; p.Enforce = d.Enforce;
            p.UpgradeMode = d.UpgradeMode; p.UpgradeTime = d.UpgradeTime; p.UpgradeDay = d.UpgradeDay;
            p.UpgradeExclude = (d.UpgradeExclude ?? []).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToList();
            p.RebootPolicy = d.RebootPolicy is "after_upgrade" ? "after_upgrade" : "never";
            p.WindowsUpdateMode = d.WindowsUpdateMode; p.WindowsUpdateTime = d.WindowsUpdateTime; p.WindowsUpdateDay = d.WindowsUpdateDay;
            p.BackupMode = d.BackupMode; p.BackupTime = d.BackupTime; p.BackupDay = d.BackupDay;
            p.ProvDrivers = d.ProvDrivers; p.ProvRestore = d.ProvRestore; p.ProvAppearance = d.ProvAppearance; p.ProvWindowsUpdate = d.ProvWindowsUpdate;
            static List<string> Clean(List<string>? l) => (l ?? []).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToList();
            if (d.KeepRunning is not null) p.KeepRunning = Clean(d.KeepRunning).Where(x => System.Text.RegularExpressions.Regex.IsMatch(x, @"^[\w@.:\-]+$")).ToList();
            if (d.KitPaths is not null) p.KitPaths = Clean(d.KitPaths).Where(x => x.StartsWith('/') && !x.Contains("..")).ToList();
            if (d.KitMode is { } km && km is "off" or "daily" or "weekly") p.KitMode = km;
            if (d.KitTime is not null) p.KitTime = d.KitTime;
            if (d.KitDay is not null) p.KitDay = d.KitDay.Value;
            if (d.MaintMode is not null) p.MaintMode = d.MaintMode;
            if (d.MaintTime is not null) p.MaintTime = d.MaintTime;
            if (d.MaintDay is not null) p.MaintDay = d.MaintDay.Value;
            if (d.MaintWake is not null) p.MaintWake = d.MaintWake.Value;
            if (d.MaintTasks is not null) p.MaintTasks = d.MaintTasks.Where(MaintPlan.AllTasks.Contains).Distinct().ToList();
            if (d.MaintAfter is not null) p.MaintAfter = d.MaintAfter;
            if (d.MaintReport is not null) p.MaintReport = d.MaintReport.Value;
            if (d.UpdDrivers is not null) p.UpdDrivers = d.UpdDrivers.Value;
            if (d.UpdFirmware is not null) p.UpdFirmware = d.UpdFirmware.Value;
            if (d.UpdFeatureUpdates is not null) p.UpdFeatureUpdates = d.UpdFeatureUpdates.Value;
            if (d.UpdMaxRounds is not null) p.UpdMaxRounds = d.UpdMaxRounds.Value;
            if (d.UpdCatchUpDays is not null) p.UpdCatchUpDays = d.UpdCatchUpDays.Value;
            if (d.UpdNeverAuto is not null) p.UpdNeverAuto = d.UpdNeverAuto.Value;
            if (d.UpdRebootNoWarning is not null) p.UpdRebootNoWarning = d.UpdRebootNoWarning.Value;
            if (d.UpdQuietFrom is not null) p.UpdQuietFrom = d.UpdQuietFrom.Trim();
            if (d.UpdQuietTo is not null) p.UpdQuietTo = d.UpdQuietTo.Trim();
            if (d.AutoFix is not null)   // kun rettelser der er godkendt til automatisk kørsel
                p.AutoFix = d.AutoFix.Where(id => HelperCatalog.Get(id) is { AutoAllowed: true }).Distinct().ToList();
        }
        static async Task SavePackages(AppDb db, int pid, List<ProfilePackageDto>? pkgs)
        {
            if (pkgs is null) return;
            db.ProfilePackages.RemoveRange(db.ProfilePackages.Where(x => x.ProfileId == pid));
            foreach (var x in pkgs.DistinctBy(x => x.PackageId))
                db.ProfilePackages.Add(new ProfilePackage { ProfileId = pid, PackageId = x.PackageId, Intent = x.Intent });
            await db.SaveChangesAsync();
        }

        adm.MapPost("/profiles", async (ProfileDto d, AppDb db, SettingsService s, AuditService audit, ClaimsPrincipal u) =>
        {
            if (ValidateProfile(d) is { } err) return err;
            var p = new Profile(); ApplyProfile(p, d);
            db.Profiles.Add(p);
            await db.SaveChangesAsync();
            await SavePackages(db, p.Id, d.Packages);
            if (d.IsDefault) await s.Set("default_profile_id", p.Id.ToString());
            await audit.Log(u.UserName(), "profile.create", p.Name);
            return Results.Ok(new { p.Id });
        });
        adm.MapPut("/profiles/{id:int}", async (int id, ProfileDto d, AppDb db, SettingsService s, ComplianceService comp, AuditService audit, ClaimsPrincipal u) =>
        {
            if (ValidateProfile(d) is { } err) return err;
            var p = await db.Profiles.FindAsync(id);
            if (p is null) return Results.NotFound();
            ApplyProfile(p, d);
            await db.SaveChangesAsync();
            await SavePackages(db, id, d.Packages);
            if (d.IsDefault) await s.Set("default_profile_id", id.ToString());
            else if (await s.DefaultProfileId() == id) await s.Set("default_profile_id", "");
            var created = 0;
            foreach (var mid in await db.Machines.Where(m => m.ProfileId == id).Select(m => m.Id).ToListAsync()) created += await comp.Enforce(mid, u.UserName());
            await audit.Log(u.UserName(), "profile.update", $"{p.Name} ({created} nye jobs)");
            return Results.Ok(new { jobsCreated = created });
        });
        adm.MapDelete("/profiles/{id:int}", async (int id, AppDb db, SettingsService s, AuditService audit, ClaimsPrincipal u) =>
        {
            var p = await db.Profiles.FindAsync(id);
            if (p is null) return Results.NotFound();
            db.Profiles.Remove(p);
            await db.SaveChangesAsync();
            if (await s.DefaultProfileId() == id) await s.Set("default_profile_id", "");
            await audit.Log(u.UserName(), "profile.delete", p.Name);
            return Results.Ok();
        });

        // -------------------------------------------------------------- jobs
        api.MapGet("/jobs", async (string? status, int? machineId, int? limit, AppDb db) =>
        {
            var q = db.Jobs.AsNoTracking().AsQueryable();
            if (status == "active") q = q.Where(j => JobStatus.Active.Contains(j.Status));
            else if (!string.IsNullOrEmpty(status)) q = q.Where(j => j.Status == status);
            if (machineId is int mid) q = q.Where(j => j.MachineId == mid);
            return await q.OrderByDescending(j => j.Id).Take(Math.Min(limit ?? 300, 2000))
                .Select(j => new { j.Id, j.MachineId, machine = j.Machine!.DisplayName ?? j.Machine.Name, j.Type, j.Title, j.Status, j.Source, j.RunAs, j.Created, j.CreatedBy, j.Started, j.Finished, j.ExitCode, j.Progress, j.Step })
                .ToListAsync();
        });
        api.MapGet("/jobs/{id:int}", async (int id, AppDb db) =>
        {
            var j = await db.Jobs.AsNoTracking().Include(x => x.Machine).FirstOrDefaultAsync(x => x.Id == id);
            return j is null ? Results.NotFound() : Results.Ok(new
            {
                j.Id, j.MachineId, machine = j.Machine!.DisplayName ?? j.Machine.Name, j.Type, j.Title, j.Status, j.Source, j.RunAs, j.Created, j.CreatedBy,
                j.Started, j.Finished, j.ExitCode, j.Progress, j.Step, j.Output,
                // AMT-jobbenes parametre er selve AMT-adgangskoden, mens jobbet venter/kører – den må aldrig vises (heller ikke for læsere)
                @params = j.Type is JobTypes.AmtActivate or JobTypes.AmtDeactivate ? new JobParams() : Json.Des<JobParams>(j.ParamsJson),
                insight = JobInsights.Explain(j),
            });
        });
        adm.MapPost("/jobs/{id:int}/cancel", async (int id, AppDb db, AuditService audit, Live live, ClaimsPrincipal u) =>
        {
            // Også kørende jobs: agenten får besked ved næste fremdriftsmelding (højst ca. 10 sek.) og lukker installationen.
            var n = await db.Jobs.Where(j => j.Id == id && (j.Status == JobStatus.Pending || j.Status == JobStatus.WaitingUser || j.Status == JobStatus.Running))
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Cancelled).SetProperty(j => j.Finished, DateTime.UtcNow)
                    .SetProperty(j => j.Step, ""));
            var cj = await db.Jobs.AsNoTracking().Where(j => j.Id == id).Select(j => new { j.MachineId, j.Title, j.Status }).FirstOrDefaultAsync();
            await audit.Log(u.UserName(), "job.cancel", cj is null ? id.ToString() : $"{id}: {cj.Title}{(n == 0 ? $" (allerede {cj.Status})" : "")}", cj?.MachineId);
            await live.Changed("jobs");
            return Results.Ok(new { cancelled = n });
        });
        adm.MapPost("/jobs/{id:int}/retry", async (int id, string? runAs, AppDb db, JobService jobs, AuditService audit, ClaimsPrincipal u) =>
        {
            var j = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (j is null) return Results.NotFound();
            // BIOS- og USB-job bærer en kørsel, en bekræftelse eller en engangsbillet: de startes forfra fra BIOS-siden med serverens kontroller
            if (JobTypes.NoRetry.Contains(j.Type)) return Results.Problem("Start igen fra BIOS-siden.", statusCode: 409);
            var ctx = runAs is { Length: > 0 } && RunAs.All.Contains(runAs) ? runAs : j.RunAs;
            var title = ctx == j.RunAs ? j.Title : $"{j.Title} (som {(ctx == RunAs.System ? "system" : "bruger")})";
            var n = await jobs.Create(j.MachineId, j.Type, title, Json.Des<JobParams>(j.ParamsJson), ctx, j.PackageId, "manual", u.UserName());
            await audit.Log(u.UserName(), "job.retry", $"{id} → {n.Id} ({ctx})");
            return Results.Ok(new { n.Id });
        });
        adm.MapPost("/jobs/cleanup", async (AppDb db, AuditService audit, ClaimsPrincipal u) =>
        {
            var cutoff = DateTime.UtcNow.AddDays(-30);
            var n = await db.Jobs.Where(j => (j.Status == JobStatus.Success || j.Status == JobStatus.Cancelled || j.Status == JobStatus.Expired) && j.Finished < cutoff).ExecuteDeleteAsync();
            await audit.Log(u.UserName(), "jobs.cleanup", $"{n} gamle jobs slettet");
            return Results.Ok(new { deleted = n });
        });

        // -------------------------------------------------------------- scripts
        api.MapGet("/scripts", (AppDb db) => db.Scripts.AsNoTracking().OrderBy(s => s.Name).ToListAsync());
        adm.MapPost("/scripts", async (ScriptDto d, AppDb db, AuditService audit, ClaimsPrincipal u) =>
        {
            if (string.IsNullOrWhiteSpace(d.Name)) return Bad("Scriptet skal have et navn");
            var s = new Script { Name = d.Name.Trim(), Description = d.Description ?? "", RunAs = RunAs.All.Contains(d.RunAs) ? d.RunAs! : RunAs.System, Body = d.Body ?? "" };
            db.Scripts.Add(s);
            await db.SaveChangesAsync();
            await audit.Log(u.UserName(), "script.create", s.Name);
            return Results.Ok(new { s.Id });
        });
        adm.MapPut("/scripts/{id:int}", async (int id, ScriptDto d, AppDb db, AuditService audit, ClaimsPrincipal u) =>
        {
            var s = await db.Scripts.FindAsync(id);
            if (s is null) return Results.NotFound();
            s.Name = d.Name.Trim(); s.Description = d.Description ?? ""; s.RunAs = RunAs.All.Contains(d.RunAs) ? d.RunAs! : RunAs.System; s.Body = d.Body ?? "";
            await db.SaveChangesAsync();
            await audit.Log(u.UserName(), "script.update", s.Name);
            return Results.Ok();
        });
        adm.MapDelete("/scripts/{id:int}", async (int id, AppDb db, AuditService audit, ClaimsPrincipal u) =>
        {
            await db.Scripts.Where(s => s.Id == id).ExecuteDeleteAsync();
            await audit.Log(u.UserName(), "script.delete", id.ToString());
            return Results.Ok();
        });

        // -------------------------------------------------------------- alarmer
        api.MapGet("/alerts", (bool? openOnly, AppDb db) => AlertList(db, null, openOnly ?? true, 500));
        adm.MapPost("/alerts/{id:int}/ack", async (int id, AppDb db, AuditService audit, HealthService health, Live live, ClaimsPrincipal u) =>
        {
            await db.Alerts.Where(a => a.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.Acknowledged, DateTime.UtcNow).SetProperty(a => a.AckBy, u.UserName()));
            if (await db.Alerts.AsNoTracking().Where(a => a.Id == id).Select(a => new { a.MachineId, a.Title }).FirstOrDefaultAsync() is { } al)
            {
                await audit.Log(u.UserName(), "alert.ack", al.Title, al.MachineId);
                await health.Evaluate(al.MachineId);      // maskinens tilstand følger kvitteringen med det samme
                await live.Changed("alerts", al.MachineId);
                await live.Changed("machines", al.MachineId);
            }
            return Results.Ok();
        });
        // Ignorér en alarm (fx Defender, der er slået fra med vilje) – på maskinen eller på alle maskiner
        adm.MapPost("/machines/{id:int}/ignore-alert", async (int id, IgnoreAlertRequest r, AppDb db, SettingsService s, HealthService health, AuditService audit, Live live, ClaimsPrincipal u) =>
        {
            var m = await db.Machines.FindAsync(id);
            if (m is null || string.IsNullOrWhiteSpace(r.Key)) return Results.NotFound();
            var item = new IgnoredAlert { Key = r.Key.Trim(), Title = (r.Title ?? r.Key).Trim(), By = u.UserName() };
            if (r.All)
            {
                var list = await IgnoredAlerts.OnAll(s);
                if (list.All(x => !x.Key.Equals(item.Key, StringComparison.OrdinalIgnoreCase))) { list.Add(item); await s.Set(IgnoredAlerts.AllSetting, Json.Ser(list)); }
            }
            else
            {
                var list = IgnoredAlerts.OnMachine(m);
                if (list.All(x => !x.Key.Equals(item.Key, StringComparison.OrdinalIgnoreCase))) { list.Add(item); m.IgnoredAlertsJson = Json.Ser(list); await db.SaveChangesAsync(); }
            }
            await audit.Log(u.UserName(), "alert.ignore", $"{(r.All ? "alle maskiner" : m.Label)}: {item.Key} ({item.Title})");
            foreach (var mid in r.All ? await db.Machines.Select(x => x.Id).ToListAsync() : [id]) await health.Evaluate(mid);
            await live.Changed("alerts");
            return Results.Ok();
        });
        adm.MapDelete("/machines/{id:int}/ignore-alert/{key}", async (int id, string key, bool? all, AppDb db, SettingsService s, HealthService health, AuditService audit, Live live, ClaimsPrincipal u) =>
        {
            var m = await db.Machines.FindAsync(id);
            if (m is null) return Results.NotFound();
            if (all == true)
            {
                var list = await IgnoredAlerts.OnAll(s);
                list.RemoveAll(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase)); await s.Set(IgnoredAlerts.AllSetting, Json.Ser(list));
            }
            else
            {
                var list = IgnoredAlerts.OnMachine(m);
                list.RemoveAll(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase)); m.IgnoredAlertsJson = Json.Ser(list); await db.SaveChangesAsync();
            }
            await audit.Log(u.UserName(), "alert.unignore", $"{(all == true ? "alle maskiner" : m.Label)}: {key}");
            foreach (var mid in all == true ? await db.Machines.Select(x => x.Id).ToListAsync() : [id]) await health.Evaluate(mid);
            await live.Changed("alerts");
            return Results.Ok();
        });
        adm.MapPost("/alerts/test-webhook", async (SettingsService s, Notifier n) =>
        {
            if (string.IsNullOrWhiteSpace(await s.Get("webhook_url"))) return Bad("Der er ingen webhook sat op");
            n.Send("✅ Test fra Fleetdeck – beskeder virker.");
            return Results.Ok();
        });

        // -------------------------------------------------------------- WinSetup-share
        api.MapGet("/winsetup", async (WinSetupStore store, SettingsService s) => store.Status(await s.Get("share_path")));
        api.MapGet("/winsetup/wallpaper", (WinSetupStore store) => store.WallpaperPath() is { } p ? Results.File(p) : Results.NotFound());
        api.MapGet("/winsetup/logs/{name}", (string name, WinSetupStore store) =>
        {
            if (store.SafeFile("Logs", name) is not { } p) return Results.NotFound();
            var raw = File.ReadAllBytes(p);
            var text = TextCodec.Decode(raw);
            return Results.Text(text.TrimStart('﻿'), "text/plain; charset=utf-8");
        });
        adm.MapPost("/winsetup/import", async (SeedService seed, ClaimsPrincipal u) =>
        {
            try { var (added, pid) = await seed.ImportAppsJson(u.UserName()); return Results.Ok(new { added, profileId = pid }); }
            catch (FileNotFoundException e) { return Bad(e.Message); }
        });

        // -------------------------------------------------------------- indstillinger, brugere, log
        api.MapGet("/settings", async (SettingsService s, ClaimsPrincipal u, AgentBinary agent, AppDb db, JobSigningKey jobKey, TlsInfo tls) =>
        {
            var d = new Dictionary<string, object?>();
            var isAdmin = u.IsInRole(Roles.Admin);
            foreach (var k in SettingsService.Public) d[k] = isAdmin || !SettingsService.AdminOnly.Contains(k) ? await s.Get(k) : "";
            d["hasWebhook"] = (await s.Get("webhook_url")).Trim().Length > 0;
            d["hasSharePass"] = (await s.Get("share_pass")).Length > 0;
            d["hasMqttPass"] = (await s.Get("mqtt_pass")).Length > 0;
            d["mqttStatus"] = HomeAssistantBridge.Status;
            if (u.IsInRole(Roles.Admin)) d["enrollKey"] = await s.Get("enroll_key");
            d["signed_jobs"] = SignedJobsMode.Normalize(await s.Get("signed_jobs"));
            d["security"] = await SecurityEndpoints.Summary(db, s, jobKey, tls);
            d["agentVersion"] = agent.Version; d["agentAvailable"] = agent.ExePath is not null; d["version"] = ThisVersion.Value;
            return d;
        });
        adm.MapPut("/settings", async ([FromBody] Dictionary<string, string> b, SettingsService s, WinSetupStore store, AgentBinary agent, AuditService audit, ClaimsPrincipal u) =>
        {
            foreach (var (k, v) in b)
                if (SettingsService.Public.Contains(k)) await s.Set(k, k switch
                {
                    "signed_jobs" => SignedJobsMode.Normalize(v.Trim()),
                    "enroll_key_enabled" => v.Trim() == "0" ? "0" : "1",
                    AccessRequestService.ModeSetting => AccessMode.Normalize(v),
                    AccessRequestService.MaxSetting => int.TryParse(v.Trim(), out var am) ? Math.Clamp(am, 1, 100).ToString() : "10",
                    AccessRequestService.HoursSetting => int.TryParse(v.Trim(), out var ah) ? Math.Clamp(ah, 1, 168).ToString() : "24",
                    "tls_pin" => TlsPinning.Normalize(v) ?? "",
                    "agent_https_url" => Uri.TryCreate(v.Trim().TrimEnd('/'), UriKind.Absolute, out var hu) && hu.Scheme == "https" ? v.Trim().TrimEnd('/') : "",
                    _ => v.Trim(),
                });
                else if (k is "share_pass" or "mqtt_pass" && v.Length > 0) await s.Set(k, v);
            store.PublishAgent(await s.Get("server_url"), await s.Get("enroll_key"), agent.ExePath);
            await audit.Log(u.UserName(), "settings.update", string.Join(", ", b.Keys.Where(k => k is not ("share_pass" or "mqtt_pass"))));
            return Results.Ok();
        });
        adm.MapPost("/settings/rotate-key", async (SettingsService s, WinSetupStore store, AgentBinary agent, AuditService audit, ClaimsPrincipal u) =>
        {
            var key = Crypto.Token(18);
            await s.Set("enroll_key", key);
            store.PublishAgent(await s.Get("server_url"), key, agent.ExePath);
            await audit.Log(u.UserName(), "settings.rotate_key");
            return Results.Ok(new { enrollKey = key });
        });

        adm.MapGet("/users", (AppDb db) => db.Users.AsNoTracking().OrderBy(x => x.Username)
            .Select(x => new { x.Id, x.Username, x.Role, x.Created, x.IpadAll, x.Ipads, x.Perms, x.LastLogin, twoFactor = x.TotpSecret != null }).ToListAsync());
        adm.MapPost("/users", async (UserCreate d, AppDb db, AuditService audit, ClaimsPrincipal u) =>
        {
            if (d.Username.Trim().Length < 2 || d.Password.Length < 8 || d.Role is not (Roles.Admin or Roles.Viewer or Roles.Parent))
                return Bad("Brugernavn min. 2 tegn, adgangskode min. 8 tegn og en gyldig rolle");
            if (await db.Users.AnyAsync(x => x.Username == d.Username.Trim())) return Bad("Brugernavnet findes allerede");
            var nu = new User { Username = d.Username.Trim(), PasswordHash = Crypto.HashPassword(d.Password), Role = d.Role };
            ApplyAccess(nu, d.Role, d.IpadAll, d.Ipads, d.Perms);
            db.Users.Add(nu);
            await db.SaveChangesAsync();
            await audit.Log(u.UserName(), "user.create", $"{nu.Username} ({nu.Role})");
            return Results.Ok(new { nu.Id });
        });
        adm.MapPut("/users/{id:int}", async (int id, UserUpdate d, AppDb db, AuditService audit, ClaimsPrincipal u) =>
        {
            await LastAdmin.Gate.WaitAsync();
            try
            {
            var x = await db.Users.FindAsync(id);
            if (x is null) return Results.NotFound();
            if (d.Role is not (Roles.Admin or Roles.Viewer or Roles.Parent)) return Bad("Ugyldig rolle");
            if (id == u.UserId() && d.Role != Roles.Admin) return Bad("Du kan ikke fjerne din egen administratorrolle");
            if (await LastAdmin.WouldRemove(db, x, d.Role)) return Bad(LastAdmin.Message);
            var before = $"{x.Role}|{x.IpadAll}|{string.Join(',', x.Ipads)}|{string.Join(',', x.Perms)}";
            ApplyAccess(x, d.Role, d.IpadAll, d.Ipads, d.Perms);
            var after = $"{x.Role}|{x.IpadAll}|{string.Join(',', x.Ipads)}|{string.Join(',', x.Perms)}";
            if (before.Split('|')[0] != x.Role) x.SecurityStamp = Guid.NewGuid().ToString("N");   // ny rolle: log ind igen
            await db.SaveChangesAsync();
            if (before != after) await audit.Log(u.UserName(), "user.update", $"{x.Username}: {after}");
            return Results.Ok();
            }
            finally { LastAdmin.Gate.Release(); }
        });
        adm.MapDelete("/users/{id:int}", async (int id, AppDb db, AuditService audit, ClaimsPrincipal u) =>
        {
            if (id == u.UserId()) return Bad("Du kan ikke slette dig selv");
            await LastAdmin.Gate.WaitAsync();
            try
            {
                var x = await db.Users.FindAsync(id);
                if (x is null) return Results.NotFound();
                if (await LastAdmin.WouldRemove(db, x, null)) return Bad(LastAdmin.Message);
                db.Users.Remove(x);
                await db.SaveChangesAsync();
                await audit.Log(u.UserName(), "user.delete", x.Username);
                return Results.Ok();
            }
            finally { LastAdmin.Gate.Release(); }
        });
        app.MapPost("/api/users/{id:int}/password", async (int id, PasswordChange d, AppDb db, AuditService audit, ClaimsPrincipal u, HttpContext ctx) =>
        {
            if (id != u.UserId() && !u.IsInRole(Roles.Admin)) return Results.Forbid();
            var x = await db.Users.FindAsync(id);
            if (x is null) return Results.NotFound();
            if (id == u.UserId() && !Crypto.VerifyPassword(d.Current ?? "", x.PasswordHash)) return Bad("Den nuværende adgangskode er forkert");
            if (d.Password.Length < 8) return Bad("Adgangskoden skal være mindst 8 tegn");
            x.PasswordHash = Crypto.HashPassword(d.Password);
            TwoFactorService.RotateStamp(x);     // logger andre sessioner ud og glemmer huskede enheder
            await db.SaveChangesAsync();
            if (id == u.UserId()) await AuthEndpoints.SignIn(ctx, x);
            await audit.Log(u.UserName(), "user.password", x.Username);
            return Results.Ok();
        }).RequireAuthorization("any").RequireRateLimiting("login");

        // Revisionsloggen (kun administratorer): AuditEndpoints
    }
}