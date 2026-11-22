using System.Text.Json;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Unix;

/// <summary>
/// Printere fra Fleetdeck på Linux og Mac (CUPS): lpadmin -m everywhere (uden driver) eller med en model/PPD, og holdes på:
/// ved hver helbredsrunde (og når listen ændres) tjekkes det, at de findes; mangler en, lægges den på igen.
/// Linux: CUPS lægges på, hvis det mangler. Kun køer, Fleetdeck selv har lagt på (husket i printers.json), ændres eller fjernes.
/// </summary>
public static class Printers
{
    private const string OwnedFile = AgentConfig.Dir + "/printers.json";
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(30);
    private static readonly object L = new();
    private static int _busy;
    private static string? _hash;
    private static DateTime _at = DateTime.MinValue;
    private static List<PrinterState>? _states;
    private static List<LocalPrinter>? _local;

    public static List<PrinterState>? States { get { lock (L) return _states is null ? null : [.. _states]; } }
    public static List<LocalPrinter>? Local { get { lock (L) return _local is null ? null : [.. _local]; } }
    /// <summary>Netværksprintere, maskinen selv har fundet (Bonjour/WSD, og avahi-browse hvis det findes). Null = ikke søgt endnu.</summary>
    public static List<SeenPrinter>? Net { get { lock (L) return _net is null ? null : [.. _net]; } }
    public static DateTime? NetAt { get { lock (L) return _netAt; } }

    private static List<SeenPrinter>? _net;
    private static DateTime? _netAt, _searchAt;
    private static int _scouting;

    /// <summary>Fra check-in: har Fleetdeck startet en ny søgning, findes printerne på netværket (ca. 4 sek.), og helbredet sendes.
    /// Også på Unraid (der lægges ingen printere på, men den ser netværket direkte).</summary>
    public static void Scout(DateTime? searchAt, Func<Task> changed, CancellationToken stop)
    {
        if (searchAt is null || searchAt == _searchAt) return;
        if (Interlocked.CompareExchange(ref _scouting, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                // Linux: avahi-browse ser også printere, som kun avahi-dæmonen har i sin cache
                var extra = !Mac.Is && Shell.Has("avahi-browse") ? AvahiBrowse.Parse(Shell.Run(AvahiBrowse.Command, TimeSpan.FromSeconds(30)).Output) : [];
                var found = await PrinterScout.Find(TimeSpan.FromSeconds(4), extra, m => Log.Info($"Printere: {m}"), stop);
                lock (L) { _net = found; _netAt = DateTime.UtcNow; }
                _searchAt = searchAt;
                Log.Info($"Printere: {found.Count} fundet på netværket");
                await changed();
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or HttpRequestException or System.Net.Sockets.SocketException)
            { Log.Warn($"Printere: søgning på netværket: {e.Message}"); _searchAt = searchAt; }
            finally { Interlocked.Exchange(ref _scouting, 0); }
        });
    }

    /// <summary>Fra check-in. Null: ældre server eller afvist svar – rør ingenting.</summary>
    public static void Want(List<PrinterSpec>? want, bool healthDue, Func<Task> changed)
    {
        if (want is null || Unraid.Is) return;
        var hash = JsonSerializer.Serialize(want);
        if (hash == _hash && !healthDue && DateTime.UtcNow - _at < Every) return;
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                if (Apply(want)) await changed();
                _hash = hash;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException or HttpRequestException)
            { Log.Warn($"Printere: {e.Message}"); }
            finally { _at = DateTime.UtcNow; Interlocked.Exchange(ref _busy, 0); }
        });
    }

    private static List<LocalPrinter> List() =>
        Shell.Has("lpstat") ? PrinterCommands.ParseCups(Shell.Run(PrinterCommands.CupsList, TimeSpan.FromSeconds(30)).Output) : [];

    private static Dictionary<string, OwnedPrinter> LoadOwned()
    {
        try { return File.Exists(OwnedFile) ? JsonSerializer.Deserialize<Dictionary<string, OwnedPrinter>>(File.ReadAllText(OwnedFile)) ?? [] : []; }
        catch (JsonException) { return []; }
    }

    private static void SaveOwned(Dictionary<string, OwnedPrinter> owned)
    {
        Directory.CreateDirectory(AgentConfig.Dir);
        var tmp = OwnedFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(owned));
        File.Move(tmp, OwnedFile, true);
    }

    /// <summary>Én runde. Returnerer true, hvis status har ændret sig.</summary>
    private static bool Apply(List<PrinterSpec> want)
    {
        var owned = LoadOwned();
        if (want.Count == 0 && owned.Count == 0 && !Shell.Has("lpstat"))
        {
            lock (L) { var was = _states is { Count: 0 } && _local is { Count: 0 }; _states = []; _local = []; return !was; }
        }
        var present = List();
        var steps = PrinterPlan.Make(want, present, owned, cups: true);
        var results = new Dictionary<string, PrinterResult>();
        var work = steps.Where(s => s.Op is not PrinterOp.Forget).ToList();
        if (work.Count > 0)
        {
            // Linux: CUPS lægges på, hvis det mangler (Mac har det altid)
            string? install = null;
            if (!Mac.Is && !Shell.Has("lpadmin")) { Pkg.Refresh(); install = Pkg.InstallCmd("cups"); }
            var r = Shell.Run(PrinterCommands.Cups(work, install), TimeSpan.FromMinutes(10));
            foreach (var (k, v) in PrinterCommands.Results(r.Output)) results[k] = v;
            if (r.TimedOut) foreach (var s in work) results.TryAdd(PrinterPlan.StepId(s), new(false, "tidsgrænsen blev overskredet"));
        }
        foreach (var s in steps.Where(s => s.Op is not PrinterOp.SetDefault))
        {
            if (s.Op == PrinterOp.Forget || (results.TryGetValue(s.Key, out var res) && res.Ok))
                Log.Info($"Printere: {s.LocalName} {s.Op switch
                {
                    PrinterOp.Add => "lagt på", PrinterOp.Readd => "lagt på igen (var fjernet)", PrinterOp.Replace => "lagt på med ny adresse/navn",
                    PrinterOp.Configure => "indstillinger rettet", PrinterOp.Remove => "fjernet", _ => "glemt (Hold på er slået fra)",
                }}");
            else Log.Warn($"Printere: {s.LocalName}: {results.GetValueOrDefault(s.Key)?.Error ?? "intet svar"}");
        }
        var next = PrinterPlan.Commit(owned, steps, results, DateTime.UtcNow);
        if (JsonSerializer.Serialize(next) != JsonSerializer.Serialize(owned)) SaveOwned(next);
        var after = work.Count > 0 ? List() : present;
        var mine = next.Values.Select(o => o.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var p in after) p.Fleetdeck = mine.Contains(p.Name);
        var states = PrinterPlan.Report(want, after, next, results, cups: true);
        lock (L)
        {
            var changed = JsonSerializer.Serialize(states) != JsonSerializer.Serialize(_states) || JsonSerializer.Serialize(after) != JsonSerializer.Serialize(_local);
            _states = states;
            _local = after;
            return changed;
        }
    }
}
