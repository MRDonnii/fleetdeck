using System.Text.Json;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Windows;

/// <summary>
/// Printere fra Fleetdeck: lægges på uden driver (IPP Everywhere, "Microsoft IPP Class Driver") eller med en driver over en
/// TCP/IP-port, og holdes på: ved hver helbredsrunde (og når listen ændres) tjekkes det, at de findes; mangler en, lægges den på igen.
/// Kun printere, Fleetdeck selv har lagt på (husket i agent.json), ændres eller fjernes. Kører i baggrunden, så check-in ikke venter.
/// </summary>
public sealed class PrinterKeeper(Runner runner, AgentOptions opt, ILogger<PrinterKeeper> log)
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);
    private int _busy;
    private string? _hash;
    private DateTime _at = DateTime.MinValue;
    private volatile List<PrinterState>? _states;
    private volatile List<LocalPrinter>? _local;

    public List<PrinterState>? States => _states is { } s ? [.. s] : null;
    public List<LocalPrinter>? Local => _local is { } l ? [.. l] : null;
    /// <summary>Netværksprintere, pc'en selv har fundet (Bonjour/WSD). Null = ikke søgt endnu.</summary>
    public List<SeenPrinter>? Net => _net is { } n ? [.. n] : null;
    public DateTime? NetAt { get; private set; }

    private volatile List<SeenPrinter>? _net;
    private DateTime? _searchAt;
    private int _scouting;

    /// <summary>
    /// Fra check-in: har Fleetdeck startet en ny søgning, finder pc'en printerne på netværket (Bonjour og WSD, ca. 4 sek.) og sender helbredet.
    /// Pc'en ser netværket direkte, så det virker, selv om serverens container ikke kan se multicast. Installerede printere med IP meldes i forvejen.
    /// </summary>
    public void Scout(DateTime? searchAt, Func<CancellationToken, Task> sendHealth, CancellationToken stop)
    {
        if (searchAt is null || searchAt == _searchAt) return;
        if (Interlocked.CompareExchange(ref _scouting, 1, 0) != 0) return;
        using (ExecutionContext.SuppressFlow())
            _ = Task.Run(async () =>
            {
                try
                {
                    var found = await PrinterScout.Find(TimeSpan.FromSeconds(4), null, m => log.LogInformation("Printere: {Msg}", m), stop);
                    _net = found; NetAt = DateTime.UtcNow; _searchAt = searchAt;
                    log.LogInformation("Printere: {N} fundet på netværket", found.Count);
                    await sendHealth(stop);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch (Exception e) { log.LogWarning("Printere: søgning på netværket: {Msg}", e.Message); _searchAt = searchAt; }
                finally { Interlocked.Exchange(ref _scouting, 0); }
            }, CancellationToken.None);
    }

    /// <summary>Fra check-in. want = null: ældre server eller afvist svar – rør ingenting.</summary>
    public void Tick(List<PrinterSpec>? want, bool healthDue, AgentConfig cfg, Func<CancellationToken, Task> sendHealth, CancellationToken stop)
    {
        if (want is null) return;
        var hash = JsonSerializer.Serialize(want);
        if (hash == _hash && !healthDue && DateTime.UtcNow - _at < Every) return;
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        using (ExecutionContext.SuppressFlow())
            _ = Task.Run(async () =>
            {
                try
                {
                    if (await Apply(want, cfg, stop)) await sendHealth(stop);
                    _hash = hash;
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch (Exception e) { log.LogWarning("Printere: {Msg}", e.Message); }
                finally { _at = DateTime.UtcNow; Interlocked.Exchange(ref _busy, 0); }
            }, CancellationToken.None);
    }

    private async Task<List<LocalPrinter>?> List(CancellationToken stop)
    {
        var r = await runner.PowerShell(PrinterCommands.WindowsList, RunAs.System, TimeSpan.FromMinutes(1), stop);
        if (r.TimedOut || r.Cancelled) return null;
        return PrinterCommands.ParseWindows(r.Output);
    }

    /// <summary>Én runde. Returnerer true, hvis status har ændret sig (så helbredet sendes med det samme).</summary>
    private async Task<bool> Apply(List<PrinterSpec> want, AgentConfig cfg, CancellationToken stop)
    {
        if (await List(stop) is not { } present) { log.LogWarning("Printere: listen over printere kunne ikke hentes"); return false; }
        var owned = cfg.Printers ?? [];
        // standardprinteren er pr. bruger, og listen her er SYSTEM's: brugerens script afgør selv, om der allerede er en rigtig standard
        var forPlan = present.Select(p => new LocalPrinter { Name = p.Name, Uri = p.Uri, Driver = p.Driver, Virtual = p.Virtual }).ToList();
        var steps = PrinterPlan.Make(want, forPlan, owned, cups: false);
        var results = new Dictionary<string, PrinterResult>();
        if (opt.DryRun) { foreach (var s in steps) log.LogInformation("Printere (prøvekørsel): {Op} {Name}", s.Op, s.LocalName); steps = []; }
        var work = steps.Where(s => s.Op is not PrinterOp.Forget and not PrinterOp.SetDefault).ToList();
        if (work.Count > 0)
        {
            var r = await runner.PowerShell(PrinterCommands.Windows(work), RunAs.System, Timeout, stop);
            foreach (var (k, v) in PrinterCommands.Results(r.Output)) results[k] = v;
            if (r.TimedOut) foreach (var s in work) results.TryAdd(s.Key, new(false, "tidsgrænsen blev overskredet"));
        }
        // standardprinteren er pr. bruger: sættes i den indloggede brugers session
        foreach (var s in steps.Where(s => s.Op is PrinterOp.SetDefault))
        {
            if (results.TryGetValue(s.Key, out var add) && !add.Ok) continue;
            var r = await runner.PowerShell(PrinterCommands.WindowsDefault(s), RunAs.User, TimeSpan.FromMinutes(1), stop);
            if (r.NoUser) continue;   // prøves igen ved næste runde, når nogen er logget ind
            foreach (var (k, v) in PrinterCommands.Results(r.Output)) results[k] = v;
        }
        foreach (var s in steps.Where(s => s.Op is not PrinterOp.SetDefault))
        {
            var ok = s.Op == PrinterOp.Forget || (results.TryGetValue(s.Key, out var res) && res.Ok);
            if (ok) log.LogInformation("Printere: {Op} {Name}", s.Op switch
            {
                PrinterOp.Add => "lagt på", PrinterOp.Readd => "lagt på igen (var fjernet)", PrinterOp.Replace => "lagt på med ny adresse/navn",
                PrinterOp.Configure => "indstillinger rettet", PrinterOp.Remove => "fjernet", _ => "glemt (Hold på er slået fra)",
            }, s.LocalName);
            else log.LogWarning("Printere: {Name}: {Err}", s.LocalName, results.GetValueOrDefault(s.Key)?.Error ?? "intet svar");
        }
        var next = PrinterPlan.Commit(owned, steps, results, DateTime.UtcNow);
        if (JsonSerializer.Serialize(next) != JsonSerializer.Serialize(owned))
        {
            cfg.Printers = next.Count == 0 ? null : next;
            try { cfg.Save(opt.ConfigFile); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.LogWarning("Kunne ikke gemme printerne i konfigurationen: {Msg}", e.Message); }
        }
        var after = steps.Count > 0 ? await List(stop) ?? present : present;
        var mine = next.Values.Select(o => o.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var p in after) p.Fleetdeck = mine.Contains(p.Name);
        var states = PrinterPlan.Report(want, after, next, results, cups: false);
        foreach (var st in states)
            if (results.TryGetValue(st.Key + ":d", out var d)) st.IsDefault = d.Ok && !d.Skipped;   // brugerens standard, ikke SYSTEM's
        var changed = JsonSerializer.Serialize(states) != JsonSerializer.Serialize(_states) || JsonSerializer.Serialize(after) != JsonSerializer.Serialize(_local);
        _states = states;
        _local = after;
        return changed;
    }
}
