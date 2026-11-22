using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Shared;

namespace Fleetdeck.Server.Services;

/// <summary>Gemmer fejlloggen fra agenterne: renset igen, uden dubletter, og begrænset i antal og alder.</summary>
public static class SysLogStore
{
    /// <summary>Gemmer nye hændelser. Giver antallet, der var nye. En fejl her må aldrig vælte resten af helbredet.</summary>
    public static async Task<int> Ingest(AppDb db, int machineId, IReadOnlyList<SysLogEvent> events, DateTime nowUtc, ILogger? log = null)
    {
        if (events.Count == 0) return 0;
        var oldest = nowUtc.AddDays(-SysLogPolicy.RetentionDays);
        var rows = new Dictionary<string, SysLogRow>();
        foreach (var e in SysLogPolicy.Prioritize(events.Where(e => e is not null), SysLogPolicy.MaxPerReport))
        {
            SysLogPolicy.Clean(e);
            if (e.Time < oldest || e.Time > nowUtc.AddDays(1) || e.Source.Length == 0) continue;
            var kind = SysLogKinds.Of(e);
            var hash = SysLogPolicy.Hash(e);
            rows.TryAdd(hash, new SysLogRow
            {
                MachineId = machineId, Time = e.Time, Level = e.Level, Log = e.Log, Source = e.Source, EventId = e.EventId, Message = e.Message,
                Kind = kind, GroupKey = Cut(SysLogPolicy.GroupKey(e, kind), 200), Hash = hash, Received = nowUtc,
            });
        }
        if (rows.Count == 0) return 0;
        var keys = rows.Keys.ToList();
        var known = await db.SysLog.Where(x => x.MachineId == machineId && keys.Contains(x.Hash)).Select(x => x.Hash).ToListAsync();
        var fresh = rows.Where(kv => !known.Contains(kv.Key)).Select(kv => kv.Value).ToList();
        if (fresh.Count == 0) return 0;
        db.SysLog.AddRange(fresh);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException e)
        {
            // samtidige helbredsrunder med de samme hændelser: drop denne gang (de kommer igen næste runde)
            foreach (var r in fresh) db.Entry(r).State = EntityState.Detached;
            log?.LogWarning("Fejllog: {N} hændelser blev ikke gemt ({Msg})", fresh.Count, e.InnerException?.Message ?? e.Message);
            return 0;
        }
        foreach (var r in fresh) db.Entry(r).State = EntityState.Detached;
        await Trim(db, machineId);
        return fresh.Count;
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>Højst <see cref="SysLogPolicy.MaxPerMachine"/> pr. maskine: de ældste går først.</summary>
    public static async Task Trim(AppDb db, int machineId)
    {
        var n = await db.SysLog.CountAsync(x => x.MachineId == machineId);
        if (n <= SysLogPolicy.MaxPerMachine) return;
        var cut = await db.SysLog.Where(x => x.MachineId == machineId).OrderByDescending(x => x.Time).ThenByDescending(x => x.Id)
            .Skip(SysLogPolicy.MaxPerMachine).Select(x => x.Id).FirstOrDefaultAsync();
        var cutTime = await db.SysLog.Where(x => x.Id == cut).Select(x => x.Time).FirstOrDefaultAsync();
        await db.SysLog.Where(x => x.MachineId == machineId && (x.Time < cutTime || (x.Time == cutTime && x.Id <= cut))).ExecuteDeleteAsync();
    }

    public static Task<int> Prune(AppDb db, DateTime nowUtc)
    {
        var cut = nowUtc.AddDays(-SysLogPolicy.RetentionDays);
        return db.SysLog.Where(x => x.Time < cut).ExecuteDeleteAsync();
    }
}

/// <summary>Hændelsestyper, der er skjult: på én maskine eller på alle. Skjulte hændelser gemmes stadig, men giver ingen advarsler.</summary>
public static class SysLogIgnore
{
    public const string AllSetting = "syslog_ignore";
    public static string MachineSetting(int machineId) => $"syslog_ignore_m{machineId}";

    public static async Task<HashSet<string>> Global(SettingsService s) => Json.Des<List<string>>(await s.Get(AllSetting)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    public static async Task<HashSet<string>> OnMachine(SettingsService s, int machineId) =>
        Json.Des<List<string>>(await s.Get(MachineSetting(machineId))).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Slår en type til eller fra. Giver true, hvis listen blev ændret.</summary>
    public static async Task<bool> Set(SettingsService s, int? machineId, string key, bool ignore)
    {
        var setting = machineId is int id ? MachineSetting(id) : AllSetting;
        var list = Json.Des<List<string>>(await s.Get(setting)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = ignore ? list.Add(key) : list.Remove(key);
        if (changed) await s.Set(setting, Json.Ser(list.Order(StringComparer.OrdinalIgnoreCase).ToList()));
        return changed;
    }
}

/// <summary>En gruppe af ens hændelser: hvor mange, første og seneste gang, og den seneste besked.</summary>
public sealed record SysLogGroup(string Key, string Level, string Log, string Source, int? EventId, string? Kind, int Count,
    DateTime First, DateTime Last, string Message, string? Ignored, List<DateTime> Recent);

public static class SysLogGroups
{
    /// <summary>Samler rækkerne efter <see cref="SysLogRow.GroupKey"/>; den alvorligste niveau og den seneste besked vises. Nyeste grupper først.</summary>
    public static List<SysLogGroup> Build(IEnumerable<SysLogRow> rows, ISet<string> ignoredMachine, ISet<string> ignoredGlobal) =>
        rows.GroupBy(r => r.GroupKey, StringComparer.OrdinalIgnoreCase).Select(g =>
        {
            var latest = g.MaxBy(r => r.Time)!;
            var worst = g.MaxBy(r => SysLogLevels.Rank(r.Level))!.Level;
            return new SysLogGroup(g.Key, worst, latest.Log, latest.Source, latest.EventId, g.Select(r => r.Kind).FirstOrDefault(k => k is not null),
                g.Count(), g.Min(r => r.Time), latest.Time, latest.Message,
                ignoredMachine.Contains(g.Key) ? "machine" : ignoredGlobal.Contains(g.Key) ? "global" : null,
                g.Select(r => r.Time).OrderDescending().Take(10).ToList());
        }).OrderByDescending(x => x.Last).ToList();

    /// <summary>Niveaufilter: critical = kun kritiske, error = kritiske og fejl, warning = også advarsler, ellers alt.</summary>
    public static bool LevelMatches(string rowLevel, string? filter) => filter switch
    {
        SysLogLevels.Critical or SysLogLevels.Error or SysLogLevels.Warning => SysLogLevels.Rank(rowLevel) >= SysLogLevels.Rank(filter),
        _ => true,
    };

    public static bool TextMatches(SysLogRow r, string? q) =>
        string.IsNullOrWhiteSpace(q) || r.Message.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase) || r.Source.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase)
        || (r.EventId?.ToString() == q.Trim());

    /// <summary>Hører hændelsen til på den samlede side "Hændelser"?</summary>
    public static bool Serious(string? kind, string level) => kind is not null && SysLogKinds.Serious.Contains(kind) || level == SysLogLevels.Critical;
}

/// <summary>En hændelse, som helbredsreglerne ser på (skjulte typer er allerede sorteret fra).</summary>
public sealed record SysLogHit(DateTime Time, string? Kind, string Level);

/// <summary>
/// Helbredsadvarsler fra fejlloggen. Konservative, så én enkelt hændelse ikke giver falsk alarm, hvor den kan være harmløs
/// (fx én uventet nedlukning = strømsvigt). Ved de sjældne og alvorlige (nedbrud, alvorlig WHEA, dårlige sektorer, nedlukninger) slutter
/// detaljen med "senest …", så en kvitteret advarsel kommer igen ved en ny hændelse. De hyppige (rettede WHEA-fejl, gentagne diskfejl, OOM)
/// har intet "senest" og kommer kun igen, hvis de bliver alvorligere – ellers ville en kvittering aldrig holde.
/// </summary>
public static class SysLogRules
{
    public static readonly TimeSpan CrashWindow = TimeSpan.FromDays(3), Week = TimeSpan.FromDays(7), Day = TimeSpan.FromDays(1);
    public const int WheaCorrectedPerDay = 50, DiskSoftPerDay = 5, ShutdownsPerWeek = 2, OomPerDay = 2;

    private static readonly HashSet<string> Crash = [SysLogKinds.BugCheck, SysLogKinds.KernelPanic, SysLogKinds.MacPanic];
    private static readonly HashSet<string> DiskHard = [SysLogKinds.DiskBadBlock, SysLogKinds.NtfsCorrupt, SysLogKinds.FsError];
    private static readonly HashSet<string> DiskSoft = [SysLogKinds.DiskPaging, SysLogKinds.DiskRetry, SysLogKinds.DiskController, SysLogKinds.StorportReset, SysLogKinds.IoError];

    /// <summary>Alle hændelsestyper, reglerne bruger (resten hentes ikke fra databasen).</summary>
    public static readonly IReadOnlyList<string> Kinds =
        [.. Crash, .. DiskHard, .. DiskSoft, SysLogKinds.WheaFatal, SysLogKinds.WheaCorrected, SysLogKinds.UnexpectedShutdown, SysLogKinds.Oom];

    public static List<Finding> Findings(IReadOnlyList<SysLogHit> hits, DateTime nowUtc)
    {
        var o = new List<Finding>();
        List<SysLogHit> In(TimeSpan w, IReadOnlySet<string> kinds) => hits.Where(h => h.Kind is { } k && kinds.Contains(k) && h.Time >= nowUtc - w && h.Time <= nowUtc.AddHours(1)).ToList();
        static string Latest(List<SysLogHit> l) => $"senest {l.Max(h => h.Time).ToLocalTime():dd.MM. HH:mm}";

        var crash = In(CrashWindow, Crash);
        if (crash.Count > 0)
            o.Add(new("syslog:crash", crash.Count >= 2 ? "critical" : "warning", "Systemnedbrud (blå skærm eller kernepanik)",
                $"{Times(crash.Count)} de sidste 3 døgn – {Latest(crash)}"));

        var whea = In(Week, new HashSet<string> { SysLogKinds.WheaFatal });
        var wheaSoft = In(Day, new HashSet<string> { SysLogKinds.WheaCorrected });
        if (whea.Count > 0)
            o.Add(new("syslog:whea", "critical", "Hardwarefejl (WHEA)", $"Windows har meldt en alvorlig hardwarefejl {Times(whea.Count)} den sidste uge – {Latest(whea)}"));
        else if (wheaSoft.Count >= WheaCorrectedPerDay)
            o.Add(new("syslog:whea-corrected", "warning", "Mange rettede hardwarefejl (WHEA)", $"{wheaSoft.Count} rettede fejl det sidste døgn"));

        var hard = In(Week, DiskHard);
        var soft = In(Day, DiskSoft);
        if (hard.Count > 0)
            o.Add(new("syslog:disk", "warning", "Diskfejl i systemloggen", $"Dårlige sektorer eller beskadiget filsystem {Times(hard.Count)} den sidste uge – {Latest(hard)}"));
        else if (soft.Count >= DiskSoftPerDay)
            o.Add(new("syslog:disk-errors", "warning", "Gentagne diskfejl", $"{soft.Count} læse-/skrivefejl eller nulstillinger det sidste døgn"));

        var power = In(Week, new HashSet<string> { SysLogKinds.UnexpectedShutdown });
        if (power.Count >= ShutdownsPerWeek)
            o.Add(new("syslog:power", "warning", "Gentagne uventede nedlukninger", $"{power.Count} gange den sidste uge – {Latest(power)}"));

        var oom = In(Day, new HashSet<string> { SysLogKinds.Oom });
        if (oom.Count >= OomPerDay)
            o.Add(new("syslog:oom", "warning", "Hukommelsen løber tør", $"Systemet har lukket programmer {oom.Count} gange det sidste døgn for at frigøre hukommelse"));
        return o;
    }

    private static string Times(int n) => n == 1 ? "1 gang" : $"{n} gange";

    /// <summary>Reglerne for én maskine ud fra databasen (skjulte typer tæller ikke med).</summary>
    public static async Task<List<Finding>> For(AppDb db, SettingsService s, int machineId, DateTime nowUtc)
    {
        var since = nowUtc - Week;
        var rows = await db.SysLog.AsNoTracking().Where(x => x.MachineId == machineId && x.Time >= since && x.Kind != null && Kinds.Contains(x.Kind))
            .Select(x => new { x.Time, x.Kind, x.Level, x.GroupKey }).ToListAsync();
        if (rows.Count == 0) return [];
        var ign = await SysLogIgnore.OnMachine(s, machineId);
        ign.UnionWith(await SysLogIgnore.Global(s));
        return Findings(rows.Where(r => !ign.Contains(r.GroupKey)).Select(r => new SysLogHit(r.Time, r.Kind, r.Level)).ToList(), nowUtc);
    }
}

/// <summary>Kan maskinen sende loggen live? Null = ja, ellers årsagen til brugeren. Samme krav som terminalen (signeret, https).</summary>
public static class LiveLogEligibility
{
    public static string? Why(Machine m, string? signedJobsMode) =>
        m.ReplacesId is not null ? "Maskinen venter på at blive godkendt."
        : !m.LiveLogCapable ? "Agenten på maskinen skal opdateres, før live-loggen kan bruges."
        : TerminalEligibility.Channel(m, signedJobsMode);
}
