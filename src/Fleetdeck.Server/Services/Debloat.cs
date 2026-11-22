using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Shared;

namespace Fleetdeck.Server.Services;

// ====================================================================== tilstand, før-værdier og rapport

public static class DebloatStates
{
    public const string Applied = "applied", Partial = "partial", NotApplied = "not_applied", NotApplicable = "na", Error = "error";
}

/// <summary>Senest kendte tilstand for ét punkt på én maskine.</summary>
public sealed class DebloatItemState
{
    public string State { get; set; } = "";
    public int Changed { get; set; }
    public List<string> Errors { get; set; } = [];
    /// <summary>Status pr. brugerprofil (HKCU-værdier), fra seneste kørsel.</summary>
    public List<DebloatHiveState> Hives { get; set; } = [];
    /// <summary>Ændringen venter på en genstart.</summary>
    public bool Reboot { get; set; }
    public DateTime At { get; set; }
}

/// <summary>Én brugerprofil: H = SID eller DEFAULT, N = profilmappens navn, Ok = alle punktets værdier er på plads.</summary>
public sealed class DebloatHiveState { public string H { get; set; } = ""; public string? N { get; set; } public bool Ok { get; set; } }

/// <summary>En registreringsværdi, som den var, før Fleetdeck ændrede den (pr. profil/hive).</summary>
public sealed class RegBefore
{
    /// <summary>Værdien fandtes.</summary>
    public bool E { get; set; }
    /// <summary>Nøglen fandtes (ellers fjernes den igen ved fortryd, hvis den er tom).</summary>
    public bool Ke { get; set; }
    /// <summary>Typen (RegistryValueKind: DWord, String, ExpandString, QWord, MultiString, Binary).</summary>
    public string? T { get; set; }
    public JsonElement? V { get; set; }
}

/// <summary>Før-billedet for ét punkt. Kun det FØRSTE billede gemmes – en genanvendelse efter en Windows-opdatering må ikke overskrive det.</summary>
public sealed class DebloatSnapshot
{
    /// <summary>"hive|sti|navn@profil" → værdien før.</summary>
    public Dictionary<string, RegBefore> Reg { get; set; } = [];
    public Dictionary<string, int> Svc { get; set; } = [];
    /// <summary>"sti+navn" → var opgaven slået til.</summary>
    public Dictionary<string, bool> Task { get; set; } = [];
    /// <summary>Apps, der faktisk blev fjernet (til geninstallation).</summary>
    public List<string> Appx { get; set; } = [];
    /// <summary>Strømplan, strømindstillinger, dvale og Windows-funktioner: nøgle → værdien før.</summary>
    public Dictionary<string, JsonElement> Ext { get; set; } = [];
    public DateTime At { get; set; }
}

public sealed class DebloatRegRow
{
    public string K { get; set; } = "";
    public string H { get; set; } = "";
    public bool E { get; set; }
    public bool Ke { get; set; }
    public string? T { get; set; }
    public JsonElement? V { get; set; }
}
public sealed class DebloatSvcRow { public string Name { get; set; } = ""; public int? Start { get; set; } }
public sealed class DebloatTaskRow { public string Path { get; set; } = ""; public string Name { get; set; } = ""; public bool Enabled { get; set; } }
public sealed class DebloatExtRow { public string K { get; set; } = ""; public JsonElement V { get; set; } }
public sealed class DebloatBeforeReport
{
    public List<DebloatRegRow> Reg { get; set; } = [];
    public List<DebloatSvcRow> Svc { get; set; } = [];
    public List<DebloatTaskRow> Task { get; set; } = [];
    public List<string> Appx { get; set; } = [];
    public List<DebloatExtRow> Ext { get; set; } = [];
}
public sealed class DebloatItemReport
{
    public string Id { get; set; } = "";
    public string State { get; set; } = "";
    public int Changed { get; set; }
    public int Skipped { get; set; }
    public bool Reboot { get; set; }
    public List<string> Errors { get; set; } = [];
    public List<DebloatHiveState> Hives { get; set; } = [];
    public DebloatBeforeReport Before { get; set; } = new();
}
/// <summary>Strømplanerne på pc'en, som motoren så dem sidst.</summary>
public sealed class DebloatPowerInfo
{
    public string? Active { get; set; }
    public string? Name { get; set; }
    public List<DebloatPowerPlan> Plans { get; set; } = [];
}
public sealed class DebloatPowerPlan { public string Guid { get; set; } = ""; public string Name { get; set; } = ""; }
/// <summary>Motorens rapport (sidste linje i jobbets output).</summary>
public sealed class DebloatReport
{
    public int V { get; set; }
    public string Action { get; set; } = "";
    public int Build { get; set; }
    public string? Edition { get; set; }
    public string? EditionGroup { get; set; }
    public bool Server { get; set; }
    public string? RestorePoint { get; set; }
    public int Profiles { get; set; }
    public DebloatPowerInfo? Power { get; set; }
    public List<DebloatItemReport> Items { get; set; } = [];
}

// ====================================================================== scriptet til agenten

public static class DebloatActions { public const string Detect = "detect", Apply = "apply", Undo = "undo"; }

/// <summary>Bygger script-jobbet (motoren + en plan som JSON) og læser rapporten igen.</summary>
public static class DebloatScript
{
    public const string Marker = "##FLEETDECK-DEBLOAT##";
    /// <summary>Før-værdiens nøgle. Planerne deler nøgle, så den oprindelige plan huskes, når man skifter mellem dem.</summary>
    public const string PlanKey = "plan", HibernateKey = "hibernate";

    private static readonly Lazy<string> EngineText = new(() =>
    {
        using var s = typeof(DebloatScript).Assembly.GetManifestResourceStream("DebloatEngine.ps1")
            ?? throw new InvalidOperationException("DebloatEngine.ps1 mangler som indlejret ressource");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    });
    public static string Engine => EngineText.Value;

    // Planen skrives som ren ASCII (\uXXXX for æøå) på én linje, så den trygt kan stå i en PowerShell here-string
    private static readonly JsonSerializerOptions PlanJson = new(JsonSerializerDefaults.Web);

    public sealed record PlanReg(string Key, string Hive, string Path, string Name, string Kind, object? Value, object? Def, int Min, int Max, bool Bits,
        Dictionary<string, RegBefore>? Before);
    public sealed record PlanSvc(string Name, int Start, int Def, int? Before);
    public sealed record PlanTask(string Path, string Name, bool? Before);
    public sealed record PlanAppx(string Name);
    public sealed record PlanPowerPlan(string Base, string? Copy, JsonElement? Before);
    public sealed record PlanPower(string Key, string Sub, string Setting, int? Ac, int? Dc, JsonElement? Before);
    public sealed record PlanHibernate(JsonElement? Before);
    public sealed record PlanFeature(string Key, string Name, JsonElement? Before);
    public sealed record PlanItem(string Id, string Title, string Label, string NotLabel, bool NeedsReboot, int MinBuild, int MaxBuild, string[] Editions,
        List<PlanReg> Reg, List<PlanSvc> Svc, List<PlanTask> Task, List<PlanAppx> Appx,
        PlanPowerPlan? PowerPlan, List<PlanPower> Power, PlanHibernate? Hibernate, bool Wol, List<PlanFeature> Feature);
    public sealed record Plan(string Action, bool RestorePoint, bool LoadOffline, string[] Protected, string AlreadyGone, string BalancedPlan, string[] FleetPlans, List<PlanItem> Items);

    /// <summary>
    /// Planen for en handling. Fortryd får før-værdierne med; apps fjernes kun ved "apply" (fortryd af apps sker som Store-installationer).
    /// </summary>
    public static Plan BuildPlan(string action, IEnumerable<DebloatItem> items, IReadOnlyDictionary<string, DebloatSnapshot>? before = null)
    {
        if (action is not (DebloatActions.Detect or DebloatActions.Apply or DebloatActions.Undo)) throw new ArgumentException($"Ukendt handling: {action}");
        var list = new List<PlanItem>();
        foreach (var i in items.DistinctBy(i => i.Id))
        {
            var snap = action == DebloatActions.Undo ? before?.GetValueOrDefault(i.Id) : null;
            JsonElement? Ext(string key) => snap is not null && snap.Ext.TryGetValue(key, out var v) ? v : null;
            var reg = i.Registry.Select(r => new PlanReg(r.Key, r.Hive, r.Path, r.Name, r.Kind, r.Value, r.WindowsDefault, r.MinBuild, r.MaxBuild, r.Bits,
                snap is null ? null : snap.Reg.Where(kv => kv.Key.StartsWith(r.Key + "@", StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(kv => kv.Key[(r.Key.Length + 1)..], kv => kv.Value))).ToList();
            var svc = i.Services.Select(s => new PlanSvc(s.Name, s.Start, s.WindowsDefault, snap?.Svc.TryGetValue(s.Name, out var b) == true ? b : null)).ToList();
            var task = i.Tasks.Select(t => new PlanTask(t.Path, t.Name, snap?.Task.TryGetValue(t.Path + t.Name, out var e) == true ? e : null)).ToList();
            // beskyttede pakker kommer aldrig med i en plan – motoren tjekker også selv
            var appx = i.Apps.Where(a => !DebloatCatalog.IsProtected(a.Name)).Select(a => new PlanAppx(a.Name)).ToList();
            // jobbets output skriver tilstanden med punktets egne ord: "Reklame-id: fra", "Filtypenavne: vises"
            list.Add(new PlanItem(i.Id, i.Subject, DebloatCatalog.StateOn(i), DebloatCatalog.StateOff(i), i.NeedsReboot, i.MinBuild, i.MaxBuild, i.Editions, reg, svc, task, appx,
                i.PowerPlan is { } pp ? new PlanPowerPlan(pp.Base, pp.Copy, Ext(PlanKey)) : null,
                i.PowerSettings.Select(w => new PlanPower(w.Key, w.Subgroup, w.Setting, w.Ac, w.Dc, Ext(w.Key))).ToList(),
                i.HibernateOff ? new PlanHibernate(Ext(HibernateKey)) : null,
                i.WakeOnLan,
                i.Features.Select(f => new PlanFeature(f.Key, f.Name, Ext(f.Key))).ToList()));
        }
        // et skift af strømplan er ikke et gendannelsespunkt værd
        var restore = action != DebloatActions.Detect && list.Count > 0 && !list.All(p => p.PowerPlan is not null);
        return new Plan(action, restore, action != DebloatActions.Detect, DebloatCatalog.ProtectedApps, DebloatCatalog.AlreadyGonePattern,
            DebloatPower.Balanced, DebloatPower.FleetdeckCopies, list);
    }

    public static string PlanToJson(Plan p) => JsonSerializer.Serialize(p, PlanJson);

    /// <summary>Hele PowerShell-scriptet: planen som here-string + motoren.</summary>
    public static string Build(Plan p)
    {
        var json = PlanToJson(p);
        if (json.Contains('\n') || json.Contains('\r')) throw new InvalidOperationException("Planen skal være én linje");
        return "$plan = @'\r\n" + json + "\r\n'@ | ConvertFrom-Json\r\n" + Engine;
    }

    /// <summary>Finder rapporten i jobbets output (sidste markør-linje). null = ingen rapport (fx afbrudt job).</summary>
    public static DebloatReport? Parse(string? output)
    {
        if (string.IsNullOrEmpty(output)) return null;
        var at = output.LastIndexOf(Marker, StringComparison.Ordinal);
        if (at < 0) return null;
        var line = output[(at + Marker.Length)..];
        var end = line.IndexOf('\n');
        if (end >= 0) line = line[..end];
        try { return JsonSerializer.Deserialize<DebloatReport>(line.Trim(), PlanJson); }
        catch (JsonException) { return null; }
    }

    /// <summary>Jobbets output uden den maskinlæsbare linje (den står ikke pænt i jobhistorikken).</summary>
    public static string StripReport(string output)
    {
        var at = output.LastIndexOf(Marker, StringComparison.Ordinal);
        if (at < 0) return output;
        var end = output.IndexOf('\n', at);
        return (output[..at] + (end >= 0 ? output[(end + 1)..] : "")).TrimEnd();
    }

    /// <summary>Lægger nye før-værdier ind i det gemte billede. Eksisterende værdier overskrives ALDRIG.</summary>
    public static DebloatSnapshot Merge(DebloatSnapshot? snap, DebloatBeforeReport b, DateTime now)
    {
        snap ??= new DebloatSnapshot { At = now };
        foreach (var r in b.Reg)
            snap.Reg.TryAdd($"{r.K}@{r.H}", new RegBefore { E = r.E, Ke = r.Ke, T = r.T, V = r.V });
        foreach (var s in b.Svc)
            if (s.Start is int st) snap.Svc.TryAdd(s.Name, st);
        foreach (var t in b.Task)
            snap.Task.TryAdd(t.Path + t.Name, t.Enabled);
        foreach (var a in b.Appx)
            if (!snap.Appx.Contains(a, StringComparer.OrdinalIgnoreCase)) snap.Appx.Add(a);
        foreach (var e in b.Ext)
            snap.Ext.TryAdd(e.K, e.V);
        return snap;
    }

    /// <summary>Apps fra punktet, der faktisk blev fjernet, og som kan hentes igen fra Store.</summary>
    public static List<AppxRemoval> Reinstallable(DebloatItem item, DebloatSnapshot? snap) =>
        snap is null ? [] : item.Apps.Where(a => a.StoreId is not null
            && snap.Appx.Any(r => DebloatCatalog.Like(r.Replace(" (standardbillede)", ""), a.Name))).ToList();
}

// ====================================================================== serverens logik

/// <summary><paramref name="UpgradeProfile"/>: tag profilens nye punkter med på en maskine, der valgte profilen i en ældre version.</summary>
public sealed record DebloatConfig(string? Profile, List<string>? Include, List<string>? Exclude, bool? Enforce, string? EnforceMode, bool? UpgradeProfile = null);

public static class DebloatEnforce { public const string Alert = "alert", Reapply = "reapply"; }

public sealed class DebloatService(AppDb db, JobService jobs, Live live)
{
    public const string Source = "debloat";
    public const string EnforcerName = "Fleetdeck (håndhævelse)";
    /// <summary>Genanvend højst én gang i døgnet – bliver det ved at vende tilbage, er det en alarm.</summary>
    public static readonly TimeSpan ReapplyPause = TimeSpan.FromHours(20);
    public static readonly TimeSpan ScanEvery = TimeSpan.FromHours(24);

    public async Task<MachineDebloat> Get(int machineId) =>
        await db.MachineDebloats.FirstOrDefaultAsync(d => d.MachineId == machineId) ?? new MachineDebloat { MachineId = machineId };

    private async Task<MachineDebloat> Tracked(int machineId)
    {
        var d = await db.MachineDebloats.FirstOrDefaultAsync(x => x.MachineId == machineId);
        if (d is null) { d = new MachineDebloat { MachineId = machineId }; db.MachineDebloats.Add(d); }
        return d;
    }

    public static string? Validate(DebloatConfig c)
    {
        if (c.Profile is { Length: > 0 } p && !DebloatProfiles.All.Contains(p)) return $"Ukendt profil: {p}";
        if (c.EnforceMode is { } m && m is not (DebloatEnforce.Alert or DebloatEnforce.Reapply)) return $"Ukendt håndhævelse: {m}";
        var bad = (c.Include ?? []).Concat(c.Exclude ?? []).FirstOrDefault(id => DebloatCatalog.Find(id) is null);
        return bad is null ? null : $"Ukendt punkt: {bad}";
    }

    public async Task<MachineDebloat> Configure(int machineId, DebloatConfig c)
    {
        var d = await Tracked(machineId);
        // at vælge en profil er at sige ja til dens nuværende indhold; ellers beholder maskinen den version, den valgte
        if (c.Profile is not null) { d.Profile = c.Profile; d.ProfileVersion = DebloatProfiles.Current; }
        if (c.UpgradeProfile == true) d.ProfileVersion = DebloatProfiles.Current;
        if (c.Include is not null) d.Include = DebloatCatalog.KeepOnePerGroup(Canon(c.Include));
        if (c.Exclude is not null) d.Exclude = Canon(c.Exclude);
        if (c.Enforce is bool e) d.Enforce = e;
        if (c.EnforceMode is not null) d.EnforceMode = c.EnforceMode;
        await db.SaveChangesAsync();
        return d;
    }

    private static List<string> Canon(IEnumerable<string> ids) =>
        ids.Select(DebloatCatalog.Find).OfType<DebloatItem>().Select(i => i.Id).Distinct().ToList();

    public static List<DebloatItem> Selection(MachineDebloat d) => DebloatCatalog.Resolve(d.Profile, d.Include, d.Exclude, d.ProfileVersion);

    /// <summary>Skift strømplan: planen bliver maskinens valg og anvendes med det samme.</summary>
    public async Task<List<Job>> SetPowerPlan(Machine m, string itemId, string by)
    {
        var d = await Tracked(m.Id);
        var group = DebloatCatalog.ById[itemId].Exclusive;
        d.Include = d.Include.Where(id => DebloatCatalog.Find(id)?.Exclusive != group).Append(itemId).ToList();
        d.Exclude = d.Exclude.Where(id => !id.Equals(itemId, StringComparison.OrdinalIgnoreCase)).ToList();
        await db.SaveChangesAsync();
        return await Apply(m, [itemId], by);
    }

    /// <summary>Kører der allerede et debloat-script på maskinen? (<paramref name="except"/> = jobbet, der er ved at blive afsluttet.)</summary>
    public Task<bool> Busy(int machineId, int? except = null) =>
        db.Jobs.AnyAsync(j => j.MachineId == machineId && j.Source == Source && j.Type == JobTypes.Script && JobStatus.Active.Contains(j.Status) && j.Id != except);

    private static string Title(string action, int n, string? profile) => action switch
    {
        DebloatActions.Apply => $"Debloat & privatliv: anvend {n} punkter" + (profile is { Length: > 0 } p ? $" ({DebloatProfiles.Info[p].Title})" : ""),
        DebloatActions.Undo => $"Debloat & privatliv: fortryd {n} punkter",
        _ => "Debloat & privatliv: tjek tilstanden",
    };

    private Task<Job> ScriptJob(Machine m, string action, IEnumerable<DebloatItem> items, IReadOnlyDictionary<string, DebloatSnapshot>? before, string title, string by)
    {
        var body = DebloatScript.Build(DebloatScript.BuildPlan(action, items, before));
        return jobs.Create(m.Id, JobTypes.Script, title, new JobParams { Body = body, TimeoutMin = 60 }, RunAs.System, null, Source, by, save: false);
    }

    /// <summary>Tjek hele kataloget (læser kun).</summary>
    public async Task<Job> Scan(Machine m, string by)
    {
        var j = await ScriptJob(m, DebloatActions.Detect, DebloatCatalog.Items, null, Title(DebloatActions.Detect, 0, null), by);
        await jobs.Commit(m.Id);
        return j;
    }

    /// <summary>Anvend punkterne (null = maskinens valg). Programmer via winget afinstalleres i brugerens session.</summary>
    public async Task<List<Job>> Apply(Machine m, IEnumerable<string>? ids, string by)
    {
        var d = await Get(m.Id);
        var items = ids is null ? Selection(d) : DebloatCatalog.KeepOnePerGroup(Canon(ids)).Select(id => DebloatCatalog.ById[id]).ToList();
        if (items.Count == 0) return [];
        var list = new List<Job> { await ScriptJob(m, DebloatActions.Apply, items, null, Title(DebloatActions.Apply, items.Count, ids is null ? d.Profile : null), by) };
        foreach (var w in items.SelectMany(i => i.Winget))
            list.Add(await jobs.Create(m.Id, JobTypes.Uninstall, $"Debloat: afinstallér {w.Title}",
                new JobParams { Name = w.Title, PackageType = PackageTypes.Winget, Identifier = w.Id }, RunAs.User, null, Source, by, save: false));
        await jobs.Commit(m.Id);
        return list;
    }

    /// <summary>Fortryd punkterne (null = alt, Fleetdeck har anvendt). Apps hentes igen fra Microsoft Store i brugerens session.</summary>
    public async Task<List<Job>> Undo(Machine m, IEnumerable<string>? ids, string by)
    {
        var d = await Get(m.Id);
        var snaps = Json.Des<Dictionary<string, DebloatSnapshot>>(d.BeforeJson);
        var wanted = ids is null ? d.Applied.Union(snaps.Keys, StringComparer.OrdinalIgnoreCase) : Canon(ids);
        var items = wanted.Select(DebloatCatalog.Find).OfType<DebloatItem>().DistinctBy(i => i.Id).ToList();
        if (items.Count == 0) return [];
        var list = new List<Job> { await ScriptJob(m, DebloatActions.Undo, items, snaps, Title(DebloatActions.Undo, items.Count, null), by) };
        foreach (var i in items)
        {
            foreach (var a in DebloatScript.Reinstallable(i, snaps.GetValueOrDefault(i.Id)))
                list.Add(await jobs.Create(m.Id, JobTypes.Install, $"Debloat fortrudt: installér {a.Title}",
                    new JobParams { Name = a.Title, PackageType = PackageTypes.MsStore, Identifier = a.StoreId }, RunAs.User, null, Source, by, save: false));
            if (d.Applied.Contains(i.Id, StringComparer.OrdinalIgnoreCase))
                foreach (var w in i.Winget)
                    list.Add(await jobs.Create(m.Id, JobTypes.Install, $"Debloat fortrudt: installér {w.Title}",
                        new JobParams { Name = w.Title, PackageType = PackageTypes.Winget, Identifier = w.Id }, RunAs.User, null, Source, by, save: false));
        }
        await jobs.Commit(m.Id);
        return list;
    }

    /// <summary>Et færdigt debloat-job: gem tilstand og før-værdier, find afvigelser og håndhæv. Returnerer jobbets output uden rapportlinjen.</summary>
    public async Task<string> HandleResult(Machine m, Job j, string output)
    {
        if (j.Type != JobTypes.Script || DebloatScript.Parse(output) is not { } rep) return output;
        var d = await Tracked(m.Id);
        var now = DateTime.UtcNow;
        Record(d, rep, now);
        d.LastJobId = j.Id;
        if (rep.Action == DebloatActions.Detect && d.Enforce && d.Drift.Count > 0 && d.EnforceMode == DebloatEnforce.Reapply
            && (d.LastReapply is null || now - d.LastReapply > ReapplyPause) && !await Busy(m.Id, j.Id))
        {
            d.LastReapply = now;
            await db.SaveChangesAsync();
            await Apply(m, d.Drift.ToList(), EnforcerName);
        }
        else await db.SaveChangesAsync();
        await live.Changed("debloat", m.Id);
        return DebloatScript.StripReport(output);
    }

    /// <summary>Lægger en rapport ind i maskinens tilstand (ren logik – testes direkte).</summary>
    public static void Record(MachineDebloat d, DebloatReport rep, DateTime now)
    {
        var states = Json.Des<Dictionary<string, DebloatItemState>>(d.StateJson);
        var snaps = Json.Des<Dictionary<string, DebloatSnapshot>>(d.BeforeJson);
        var applied = new HashSet<string>(d.Applied, StringComparer.OrdinalIgnoreCase);
        foreach (var r in rep.Items)
        {
            if (DebloatCatalog.Find(r.Id) is not { } item) continue;
            states[item.Id] = new DebloatItemState { State = r.State, Changed = r.Changed, Errors = r.Errors, Hives = r.Hives, Reboot = r.Reboot, At = now };
            switch (rep.Action)
            {
                case DebloatActions.Apply:
                    // kun ét punkt i en gruppe kan gælde: det nye overtager det første før-billede, så fortryd når helt tilbage
                    if (item.Exclusive is { } group && r.State is DebloatStates.Applied or DebloatStates.Partial)
                        foreach (var other in DebloatCatalog.Items.Where(i => i.Exclusive == group && i.Id != item.Id))
                        {
                            applied.Remove(other.Id);
                            if (states.ContainsKey(other.Id)) states[other.Id] = new DebloatItemState { State = DebloatStates.NotApplied, At = now };
                            d.Include.RemoveAll(id => id.Equals(other.Id, StringComparison.OrdinalIgnoreCase));
                            if (snaps.Remove(other.Id, out var old)) snaps.TryAdd(item.Id, old);
                        }
                    if (r.Before.Reg.Count + r.Before.Svc.Count + r.Before.Task.Count + r.Before.Appx.Count + r.Before.Ext.Count > 0)
                        snaps[item.Id] = DebloatScript.Merge(snaps.GetValueOrDefault(item.Id), r.Before, now);
                    if (r.State is DebloatStates.Applied or DebloatStates.Partial) applied.Add(item.Id);
                    break;
                case DebloatActions.Undo:
                    if (r.Errors.Count == 0) { applied.Remove(item.Id); snaps.Remove(item.Id); }
                    break;
            }
        }
        d.Applied = DebloatCatalog.Items.Where(i => applied.Contains(i.Id)).Select(i => i.Id).ToList();
        // afvigelse: noget Fleetdeck har slået fra, som er slået til igen (fx efter en stor Windows-opdatering)
        d.Drift = d.Applied.Where(id => states.TryGetValue(id, out var s) && s.State is DebloatStates.NotApplied or DebloatStates.Partial).ToList();
        d.StateJson = Json.Ser(states);
        d.BeforeJson = Json.Ser(snaps);
        d.Build = rep.Build;
        d.Edition = rep.Edition;
        if (rep.Power is { Active.Length: > 0 }) d.PowerJson = Json.Ser(rep.Power);
        if (rep.Action == DebloatActions.Detect) d.LastScan = now; else d.LastApplied = now;
        if (!string.IsNullOrEmpty(rep.RestorePoint)) d.RestorePoint = rep.RestorePoint.Length > 300 ? rep.RestorePoint[..300] : rep.RestorePoint;
    }

    /// <summary>Alarm, når håndhævede punkter er vendt tilbage og ikke bliver (eller ikke kunne blive) rettet automatisk.</summary>
    public static Finding? DriftFinding(MachineDebloat? d, DateTime now)
    {
        if (d is null || !d.Enforce || d.Drift.Count == 0) return null;
        // "genanvend": ingen alarm, før Fleetdeck har prøvet – kun hvis genanvendelsen ikke hjalp, eller det vendte tilbage inden for pausen
        if (d.EnforceMode == DebloatEnforce.Reapply)
        {
            var tried = d.LastReapply is { } r && ((d.LastApplied ?? DateTime.MinValue) >= r || (d.LastScan ?? DateTime.MinValue) > r && now - r < ReapplyPause);
            if (!tried) return null;
        }
        return DriftText(d.Drift.Select(DebloatCatalog.Find).OfType<DebloatItem>().ToList(), d.LastApplied, d.Drift.Count);
    }

    /// <summary>
    /// Alarmens tekst: ét punkt nævnes i titlen ("Microsoft 365-appen er kommet tilbage"), flere tælles. Detaljen siger, hvornår Fleetdeck
    /// fjernede/ændrede det, og at Windows typisk lægger det tilbage ved en opdatering. Ikke alt er "slået fra" (fx vis filtypenavne),
    /// så indstillinger "er ændret tilbage", mens apps "er kommet tilbage".
    /// </summary>
    public static Finding DriftText(IReadOnlyList<DebloatItem> items, DateTime? lastApplied, int count)
    {
        static bool IsApp(DebloatItem i) => i.Apps.Length > 0 || i.Winget.Length > 0;
        var when = lastApplied is { } la ? $" {la.ToLocalTime():d'/'M}" : "";
        if (items.Count == 1)
        {
            var i = items[0];
            return IsApp(i)
                ? new("debloat_drift", "warning", $"{i.Subject} er kommet tilbage",
                    $"Fleetdeck fjernede den{when}. Windows har lagt den ind igen – typisk efter en Windows-opdatering.")
                : new("debloat_drift", "warning", $"{i.Subject} er ændret tilbage",
                    $"Fleetdeck ændrede indstillingen{when}. Windows har sat den tilbage – typisk efter en Windows-opdatering.");
        }
        var n = Math.Max(count, items.Count);
        var names = string.Join(", ", items.Take(5).Select(i => i.Subject)) + (items.Count > 5 ? " …" : "");
        var verb = items.All(IsApp) ? "fjernede" : "ændrede";
        return new("debloat_drift", "warning", $"{n} debloat-punkter er vendt tilbage",
            $"{names}. Fleetdeck {verb} dem{when}. Windows har lagt dem tilbage – typisk efter en Windows-opdatering.");
    }

    /// <summary>Håndhævelse: tjek maskiner med "hold håndhævet" én gang i døgnet, og med det samme efter en ny Windows-build.</summary>
    public async Task Tick(DateTime now)
    {
        var due = await db.MachineDebloats.Where(d => d.Enforce).Join(db.Machines, d => d.MachineId, m => m.Id, (d, m) => new { d, m })
            .Where(x => x.m.Platform == Platforms.Windows && x.m.LastSeen > now.AddMinutes(-10)).ToListAsync();
        foreach (var x in due)
        {
            var build = BuildOf(Json.Des<SystemFacts>(x.m.FactsJson).Build);
            var newBuild = build is int b && x.d.Build is int known && known > 0 && b != known;
            if (!newBuild && x.d.LastScan is { } ls && now - ls < ScanEvery) continue;
            if (await Busy(x.m.Id)) continue;
            await Scan(x.m, EnforcerName);
        }
    }

    public static int? BuildOf(string? s) =>
        s is null ? null : int.TryParse(new string(s.TrimStart().TakeWhile(char.IsDigit).ToArray()), out var b) ? b : null;
}
