using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;

namespace Fleetdeck.Server.Endpoints;

public sealed record DebloatRunRequest(List<string>? Items);
public sealed record DebloatBulkRequest(List<int> MachineIds, string Action);
public sealed record DebloatPowerPlanRequest(string Plan);

/// <summary>Debloat og privatliv (kun administratorer). Alt, der ændrer noget, står i revisionsloggen og som jobs i jobhistorikken.</summary>
public static class DebloatEndpoints
{
    public static object CatalogView() => new
    {
        categories = DebloatCatalog.Categories,
        risks = DebloatRisk.All,
        profileVersion = DebloatProfiles.Current,
        powerPlans = DebloatPower.Choices,
        profiles = DebloatProfiles.All.Select(p => new
        {
            id = p, title = DebloatProfiles.Info[p].Title, description = DebloatProfiles.Info[p].Description,
            items = DebloatCatalog.ForProfile(p).Select(i => i.Id),
        }),
        protectedApps = DebloatCatalog.ProtectedApps,
        items = DebloatCatalog.Items.Select(i => new
        {
            i.Id, i.Category, i.Title, i.Subject, i.Description, i.Risk, i.NeedsLogoff,
            // tilstanden med punktets egne ord og hvad Fortryd gør – samme tekster som i jobbets output
            stateOn = DebloatCatalog.StateOn(i), stateOff = DebloatCatalog.StateOff(i), undo = DebloatCatalog.UndoText(i), i.Profiles, i.MinBuild, i.MaxBuild, i.Editions, i.Note, i.FullyReversible,
            notReinstallable = i.NotReinstallable.Select(a => a.Title),
            registry = i.Registry.Select(r => new { r.Hive, r.Path, r.Name, r.Kind, r.Value }),
            services = i.Services.Select(s => new { s.Name, s.Start }),
            tasks = i.Tasks.Select(t => t.Path + t.Name),
            apps = i.Apps.Select(a => new { a.Name, a.Title, reinstallable = a.StoreId is not null }),
            winget = i.Winget.Select(w => new { w.Id, w.Title }),
            i.Exclusive, i.NeedsReboot, i.ProfileSince, i.HibernateOff, i.WakeOnLan,
            powerPlan = i.PowerPlan is { } pp ? new { pp.Base, pp.Copy } : null,
            powerSettings = i.PowerSettings.Select(w => new { w.Subgroup, w.Setting, w.Ac, w.Dc }),
            features = i.Features.Select(f => f.Name),
        }),
    };

    public static object MachineView(Machine m, MachineDebloat d, bool busy, IEnumerable<Job>? jobs = null)
    {
        var states = Json.Des<Dictionary<string, DebloatItemState>>(d.StateJson);
        var snaps = Json.Des<Dictionary<string, DebloatSnapshot>>(d.BeforeJson);
        return new
        {
            machineId = m.Id, name = m.Label, d.Profile, d.ProfileVersion, profileCurrent = DebloatProfiles.Current,
            profileNew = DebloatCatalog.NewInProfile(d.Profile, d.ProfileVersion).Select(i => i.Id),
            power = Json.Des<DebloatPowerInfo>(d.PowerJson), d.Include, d.Exclude, d.Enforce, d.EnforceMode, d.Applied, d.Drift,
            selected = DebloatService.Selection(d).Select(i => i.Id),
            states, undoable = snaps.Keys, d.Build, d.Edition, d.LastScan, d.LastApplied, d.LastReapply, d.LastJobId, d.RestorePoint, busy,
            // før-værdierne til detaljevisningen: registrering pr. profil (k = hive|sti|navn i små bogstaver, h = profil), tjenester og opgaver
            before = snaps.ToDictionary(kv => kv.Key, kv => new
            {
                reg = kv.Value.Reg.Select(r => { var at = r.Key.LastIndexOf('@'); return new { k = r.Key[..at], h = r.Key[(at + 1)..], e = r.Value.E, v = r.Value.V?.ToString() }; }),
                svc = kv.Value.Svc, task = kv.Value.Task, appx = kv.Value.Appx, ext = kv.Value.Ext.ToDictionary(e => e.Key, e => e.Value.ToString()), at = kv.Value.At,
            }),
            jobs = (jobs ?? []).Select(j => new { j.Id, j.Type, j.Title, j.Status, j.Created, j.CreatedBy, j.Started, j.Finished, j.ExitCode, j.Progress, j.Step, j.StepSince }),
        };
    }

    /// <summary>Tilstand pr. kategori til flådeoversigten: off (intet valgt) | ok | drift | pending.</summary>
    public static Dictionary<string, string> CategoryStates(MachineDebloat d)
    {
        var states = Json.Des<Dictionary<string, DebloatItemState>>(d.StateJson);
        var sel = DebloatService.Selection(d);
        return DebloatCatalog.Categories.ToDictionary(c => c.Id, c =>
        {
            var items = sel.Where(i => i.Category == c.Id).ToList();
            if (items.Count == 0) return "off";
            if (items.Any(i => d.Drift.Contains(i.Id))) return "drift";
            return items.All(i => states.TryGetValue(i.Id, out var s) && s.State is DebloatStates.Applied or DebloatStates.NotApplicable) ? "ok" : "pending";
        });
    }

    private static Task<List<Job>> RecentJobs(AppDb db, int machineId) =>
        db.Jobs.AsNoTracking().Where(j => j.MachineId == machineId && j.Source == DebloatService.Source).OrderByDescending(j => j.Id).Take(10).ToListAsync();

    public static void MapDebloatEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/debloat").RequireAuthorization("admin");

        g.MapGet("/catalog", () => Results.Ok(CatalogView()));

        // flådeoversigt: alle Windows-maskiner med deres valg og tilstand
        g.MapGet("/fleet", async (AppDb db) =>
        {
            var machines = await db.Machines.AsNoTracking().Where(m => m.Platform == Platforms.Windows).OrderBy(m => m.DisplayName ?? m.Name).ToListAsync();
            var all = await db.MachineDebloats.AsNoTracking().ToDictionaryAsync(d => d.MachineId);
            var busy = (await db.Jobs.AsNoTracking().Where(j => j.Source == DebloatService.Source && j.Type == JobTypes.Script && JobStatus.Active.Contains(j.Status))
                .Select(j => j.MachineId).ToListAsync()).ToHashSet();
            return Results.Ok(machines.Select(m =>
            {
                var d = all.GetValueOrDefault(m.Id) ?? new MachineDebloat { MachineId = m.Id };
                var states = Json.Des<Dictionary<string, DebloatItemState>>(d.StateJson);
                var sel = DebloatService.Selection(d);
                return new
                {
                    machineId = m.Id, name = m.Label, m.LastSeen, d.Profile, profileNew = DebloatCatalog.NewInProfile(d.Profile, d.ProfileVersion).Count(),
                    powerPlan = Json.Des<DebloatPowerInfo>(d.PowerJson).Name, d.Enforce, d.EnforceMode, selected = sel.Count,
                    applied = d.Applied.Count, drift = d.Drift.Count,
                    pending = sel.Count(i => !states.TryGetValue(i.Id, out var s) || s.State is DebloatStates.NotApplied or DebloatStates.Partial),
                    errors = states.Values.Count(s => s.State == DebloatStates.Error),
                    d.Build, d.Edition, d.LastScan, d.LastApplied, busy = busy.Contains(m.Id), online = m.LastSeen > DateTime.UtcNow.AddMinutes(-5),
                    cats = CategoryStates(d),
                };
            }));
        });

        g.MapGet("/machines/{id:int}", async (int id, AppDb db, DebloatService svc) =>
            await db.Machines.FindAsync(id) is { Platform: Platforms.Windows } m
                ? Results.Ok(MachineView(m, await svc.Get(id), await svc.Busy(id), await RecentJobs(db, id)))
                : Results.NotFound());

        g.MapPut("/machines/{id:int}", async (int id, DebloatConfig c, HttpContext ctx, AppDb db, DebloatService svc, AuditService audit) =>
        {
            if (await db.Machines.FindAsync(id) is not { Platform: Platforms.Windows } m) return Results.NotFound();
            if (DebloatService.Validate(c) is { } err) return Results.BadRequest(new { error = err });
            var d = await svc.Configure(id, c);
            await audit.Log(ctx.User.UserName(), "debloat.config", $"{m.Label}: profil {(d.Profile is { Length: > 0 } p ? p : "ingen")}, +{d.Include.Count}/−{d.Exclude.Count}, håndhæv {(d.Enforce ? d.EnforceMode : "fra")}");
            return Results.Ok(MachineView(m, d, await svc.Busy(id), await RecentJobs(db, id)));
        });

        g.MapPost("/machines/{id:int}/{action:regex(^(scan|apply|undo)$)}", async (int id, string action, DebloatRunRequest? r, HttpContext ctx, AppDb db, DebloatService svc, AuditService audit) =>
        {
            if (await db.Machines.FindAsync(id) is not { Platform: Platforms.Windows } m) return Results.NotFound();
            if (r?.Items?.FirstOrDefault(i => DebloatCatalog.Find(i) is null) is { } bad) return Results.BadRequest(new { error = $"Ukendt punkt: {bad}" });
            if (await svc.Busy(id)) return Results.Conflict(new { error = "Der kører allerede et debloat-job på maskinen." });
            var user = ctx.User.UserName();
            var jobs = action switch
            {
                "scan" => [await svc.Scan(m, user)],
                "apply" => await svc.Apply(m, r?.Items, user),
                _ => await svc.Undo(m, r?.Items, user),
            };
            if (jobs.Count == 0) return Results.BadRequest(new { error = action == "undo" ? "Der er intet at fortryde." : "Ingen punkter valgt." });
            await audit.Log(user, $"debloat.{action}", $"{m.Label}: {jobs[0].Title}" + (r?.Items is { Count: > 0 } it ? $" [{string.Join(", ", it)}]" : ""));
            return Results.Ok(new { jobIds = jobs.Select(j => j.Id) });
        });

        // strømprofilen: vælg planen og anvend den med det samme (også fra maskinens Strøm-menu)
        g.MapPost("/machines/{id:int}/powerplan", async (int id, DebloatPowerPlanRequest r, HttpContext ctx, AppDb db, DebloatService svc, AuditService audit) =>
        {
            if (await db.Machines.FindAsync(id) is not { Platform: Platforms.Windows } m) return Results.NotFound();
            if (!DebloatPower.Choices.TryGetValue(r.Plan ?? "", out var itemId)) return Results.BadRequest(new { error = $"Ukendt strømplan: {r.Plan}" });
            if (await svc.Busy(id)) return Results.Conflict(new { error = "Der kører allerede et debloat-job på maskinen." });
            var user = ctx.User.UserName();
            var jobs = await svc.SetPowerPlan(m, itemId, user);
            await audit.Log(user, "debloat.powerplan", $"{m.Label}: {DebloatCatalog.ById[itemId].Title}");
            return Results.Ok(new { jobIds = jobs.Select(j => j.Id) });
        });

        // flere maskiner på én gang fra flådeoversigten: tjek eller anvend hver maskines eget valg
        g.MapPost("/bulk", async (DebloatBulkRequest r, HttpContext ctx, AppDb db, DebloatService svc, AuditService audit) =>
        {
            if (r.Action is not ("scan" or "apply")) return Results.BadRequest(new { error = "Handlingen skal være scan eller apply." });
            var user = ctx.User.UserName();
            var done = new List<int>(); var skipped = new List<int>();
            foreach (var m in await db.Machines.Where(m => r.MachineIds.Contains(m.Id) && m.Platform == Platforms.Windows).ToListAsync())
            {
                if (await svc.Busy(m.Id)) { skipped.Add(m.Id); continue; }
                var jobs = r.Action == "scan" ? [await svc.Scan(m, user)] : await svc.Apply(m, null, user);
                (jobs.Count > 0 ? done : skipped).Add(m.Id);
            }
            await audit.Log(user, $"debloat.bulk.{r.Action}", $"{done.Count} maskiner" + (skipped.Count > 0 ? $", {skipped.Count} sprunget over" : ""));
            return Results.Ok(new { done, skipped });
        });
    }
}
