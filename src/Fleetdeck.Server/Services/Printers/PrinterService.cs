using Fleetdeck.Server.Data;
using Fleetdeck.Shared;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Server.Services.Printers;

/// <summary>En printer på en maskine: fra maskinen selv eller fra dens profil.</summary>
public sealed record MachinePrinter(Printer Printer, PrinterAssignment Assignment, bool FromProfile);

/// <summary>Hvilke printere en maskine skal have, og hvad agenten skal have at vide.</summary>
public static class PrinterService
{
    public static string Key(int printerId) => $"p{printerId}";

    /// <summary>Maskinens egne tildelinger vinder over profilens for samme printer.</summary>
    public static List<MachinePrinter> Resolve(IEnumerable<PrinterAssignment> own, IEnumerable<PrinterAssignment> profile, IReadOnlyDictionary<int, Printer> printers)
    {
        var res = new List<MachinePrinter>();
        var seen = new HashSet<int>();
        foreach (var (a, fromProfile) in own.Select(a => (a, false)).Concat(profile.Select(a => (a, true))))
            if (printers.TryGetValue(a.PrinterId, out var p) && seen.Add(p.Id)) res.Add(new(p, a, fromProfile));
        return res.OrderBy(x => x.Printer.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static async Task<List<MachinePrinter>> For(AppDb db, Machine m)
    {
        var own = await db.PrinterAssignments.AsNoTracking().Where(a => a.MachineId == m.Id).ToListAsync();
        var prof = m.ProfileId is int pid ? await db.PrinterAssignments.AsNoTracking().Where(a => a.ProfileId == pid).ToListAsync() : [];
        var ids = own.Concat(prof).Select(a => a.PrinterId).Distinct().ToList();
        if (ids.Count == 0) return [];
        var printers = await db.Printers.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
        return Resolve(own, prof, printers);
    }

    public static PrinterSpec Spec(MachinePrinter mp) => new()
    {
        Key = Key(mp.Printer.Id), Name = mp.Printer.Name, Uri = mp.Printer.Uri, Location = mp.Printer.Location,
        Paper = mp.Printer.Paper, Duplex = mp.Printer.Duplex, Color = mp.Printer.Color,
        MakeDefault = mp.Assignment.MakeDefault, Keep = mp.Assignment.Keep,
        Driver = mp.Printer.NeedsDriver && mp.Printer.Driver.Trim().Length > 0 ? mp.Printer.Driver.Trim() : null,
    };

    /// <summary>Platforme, hvor agenten kan lægge printere på (Unraid og iPads kan ikke).</summary>
    public static bool Supported(Machine m, HealthReport? h = null) => m.Platform is Platforms.Windows or Platforms.Linux or Platforms.Mac && h?.Unraid is null;

    /// <summary>Til check-in: null for platforme uden printere (så agenten aldrig rører noget).</summary>
    public static async Task<List<PrinterSpec>?> Specs(AppDb db, Machine m) =>
        m.Platform is Platforms.Windows or Platforms.Linux or Platforms.Mac && !IsUnraid(m) ? (await For(db, m)).Select(Spec).ToList() : null;

    /// <summary>Billigt tjek ved hvert check-in (uden at læse hele helbredet).</summary>
    private static bool IsUnraid(Machine m) => m.HealthJson is { } j && j.Contains("\"unraid\":{", StringComparison.Ordinal);

    /// <summary>Driverpakker for de printere, maskinen har, og som kræver driver.</summary>
    public static async Task<List<Package>> DriverPackages(AppDb db, Machine m)
    {
        var ids = (await For(db, m)).Where(x => x.Printer.NeedsDriver && x.Printer.DriverPackageId is not null).Select(x => x.Printer.DriverPackageId!.Value).Distinct().ToList();
        return ids.Count == 0 ? [] : await db.Packages.AsNoTracking().Where(p => ids.Contains(p.Id)).ToListAsync();
    }

    public static PrinterInfo? Info(Printer p) => p.StatusJson is { Length: > 0 } j ? Json.Des<PrinterInfo>(j) : null;

    /// <summary>Printerens status til siden: tekst, niveau, blæk og fejl.</summary>
    public static object StatusView(Printer p)
    {
        var info = Info(p);
        var (text, level) = PrinterUri.IsIpp(p.Uri) || info is not null ? PrinterStatus.Describe(info) : ("Ingen status (ikke IPP)", "off");
        return new
        {
            text, level, state = info?.State, reasons = info?.Reasons ?? [], error = info?.Error, at = p.LastPolled,
            markers = (info?.Markers ?? []).Select(m => new { m.Name, m.Level, m.Color, m.Type, colorName = PrinterStatus.ColorName(m), low = m.Level is int l && l < PrinterStatus.LowPct }),
            low = info is null ? [] : PrinterStatus.Low(info).Select(PrinterStatus.ColorName).Distinct().ToList(),
        };
    }

    /// <summary>
    /// Printerne på maskinens side: dem fra Fleetdeck med status fra maskinen, og maskinens egne ("Ikke fra Fleetdeck").
    /// </summary>
    public static List<object> MachineRows(List<MachinePrinter> want, HealthReport h, bool cups)
    {
        var rows = new List<object>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mp in want)
        {
            var key = Key(mp.Printer.Id);
            var st = h.Printers?.FirstOrDefault(x => x.Key == key);
            names.Add(st?.Name ?? PrinterNames.Local(mp.Printer.Name, cups));
            rows.Add(new
            {
                printerId = mp.Printer.Id, assignmentId = mp.Assignment.Id, name = mp.Printer.Name, uri = mp.Printer.Uri, fromProfile = mp.FromProfile,
                makeDefault = mp.Assignment.MakeDefault, keep = mp.Assignment.Keep, fleetdeck = true,
                state = st is null ? null : new { st.Ok, st.IsDefault, st.Foreign, st.Dropped, st.Error, st.AddedAt, st.ReaddedAt },
            });
        }
        foreach (var lp in (h.LocalPrinters ?? []).Where(lp => !names.Contains(lp.Name)))
            rows.Add(new { printerId = (int?)null, assignmentId = (int?)null, name = lp.Name, uri = lp.Uri, fromProfile = false, makeDefault = false, keep = false,
                fleetdeck = false, state = new { Ok = true, lp.IsDefault, Foreign = true, Dropped = false, Error = (string?)null, AddedAt = (DateTime?)null, ReaddedAt = (DateTime?)null } });
        return rows;
    }

    /// <summary>
    /// Læg en printer på profiler og pc'er ("Opsæt på…"): én tildeling pr. sted, som agenterne installerer og holder på.
    /// Steder, der ikke findes, allerede har printeren, eller ikke kan få printere (iPad, Unraid), springes over. Returnerer antal nye.
    /// </summary>
    public static async Task<int> Place(AppDb db, int printerId, IEnumerable<int>? profileIds, IEnumerable<int>? machineIds, bool makeDefault)
    {
        var have = await db.PrinterAssignments.AsNoTracking().Where(a => a.PrinterId == printerId).Select(a => new { a.ProfileId, a.MachineId }).ToListAsync();
        var n = 0;
        foreach (var pid in (profileIds ?? []).Distinct())
            if (!have.Any(h => h.ProfileId == pid) && await db.Profiles.AnyAsync(x => x.Id == pid))
            { db.PrinterAssignments.Add(new PrinterAssignment { PrinterId = printerId, ProfileId = pid, MakeDefault = makeDefault }); n++; }
        foreach (var mid in (machineIds ?? []).Distinct())
            if (!have.Any(h => h.MachineId == mid) && await db.Machines.AsNoTracking().FirstOrDefaultAsync(x => x.Id == mid) is { } m
                && m.Platform is Platforms.Windows or Platforms.Linux or Platforms.Mac && !IsUnraid(m))
            { db.PrinterAssignments.Add(new PrinterAssignment { PrinterId = printerId, MachineId = mid, MakeDefault = makeDefault }); n++; }
        if (n > 0) await db.SaveChangesAsync();
        return n;
    }

    /// <summary>Gyldige værdier fra brugerfladen (navn, adresse, papir, tosidet).</summary>
    public static string? Validate(string? name, string? uri, string? paper, string? duplex)
    {
        if (PrinterNames.Clean(name).Length is 0 or > 100) return "Skriv et navn (højst 100 tegn)";
        if (PrinterUri.Normalize(uri) is null) return "Skriv adressen som ipp://printer/ipp/print (eller ipps://, socket://, lpd://)";
        if (paper is not null && !PrinterPaper.All.Contains(paper)) return "Ukendt papirstørrelse";
        if (duplex is not null && !PrinterDuplex.All.Contains(duplex)) return "Vælg tosidet: ingen, lang kant eller kort kant";
        return null;
    }
}

/// <summary>Spørger printerne om status (blæk/toner, fejl) med IPP med faste mellemrum og gemmer svaret.</summary>
public sealed class PrinterMonitor(IServiceScopeFactory scopes, ILogger<PrinterMonitor> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20), stop); } catch (OperationCanceledException) { return; }
        while (!stop.IsCancellationRequested)
        {
            var minutes = 10.0;
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDb>();
                minutes = Math.Clamp(await scope.ServiceProvider.GetRequiredService<SettingsService>().GetNum("printer_poll_minutes", 10), 2, 1440);
                if (await PollAll(db, stop)) await scope.ServiceProvider.GetRequiredService<Live>().Changed("printers");
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception e) { log.LogWarning("Printere: status kunne ikke hentes ({Msg})", e.Message); }
            try { await Task.Delay(TimeSpan.FromMinutes(minutes), stop); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Besked, når status bliver værre (ok → lav på blæk, eller → fejl). Ikke når den er slukket eller bliver bedre.</summary>
    public static string? Warn((string Text, string Level) before, (string Text, string Level) now) =>
        now.Level is "warn" or "error" && now != before && !(before.Level == "error" && now.Level == "warn") ? now.Text : null;

    /// <summary>Spørg alle IPP-printere. Returnerer true, hvis en status har ændret sig.</summary>
    public static async Task<bool> PollAll(AppDb db, CancellationToken ct)
    {
        var printers = await db.Printers.ToListAsync(ct);
        var changed = false;
        await Parallel.ForEachAsync(printers.Where(p => PrinterUri.IsIpp(p.Uri)).ToList(), new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct }, async (p, c) =>
        {
            var info = await PrinterDiscovery.Probe(p.Uri, c);
            var before = PrinterStatus.Describe(PrinterService.Info(p));
            lock (printers)
            {
                if (info.Error is not null) info = new PrinterInfo { State = "off", Error = info.Error, At = info.At, Markers = PrinterService.Info(p)?.Markers ?? [] };
                p.StatusJson = System.Text.Json.JsonSerializer.Serialize(info, Json.Web);
                p.LastPolled = DateTime.UtcNow;
                if (p.MakeModel.Length == 0 && info.MakeModel is { Length: > 0 } mm) p.MakeModel = mm.Length > 200 ? mm[..200] : mm;
                var now = PrinterStatus.Describe(info);
                if (now != before) changed = true;
                // advarsel til mobilen (via Home Assistant), når printeren får et problem eller løber tør (under 15 %)
                if (Warn(before, now) is { } msg) HomeAssistantBridge.Notify($"Printer: {p.Name}", msg, "printer");
            }
        });
        await db.SaveChangesAsync(ct);
        return changed;
    }
}
