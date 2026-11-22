using System.Globalization;
using System.Text.RegularExpressions;

namespace Fleetdeck.Shared;

/// <summary>Fremdrift for et kørende job: procent (null = ukendt) og hvad der sker lige nu.</summary>
public sealed class JobProgressPost
{
    public int? Percent { get; set; }
    public string Step { get; set; } = "";
    /// <summary>Hvornår trinnet sidst ændrede sig (UTC) – så Fleetdeck kan vise "uændret i 12 min".</summary>
    public DateTime? StepSince { get; set; }
    /// <summary>"Opdatér alt": programmer (winget-id), der er opdateret siden sidste melding – serveren fjerner dem fra listen med det samme.</summary>
    public List<string>? Upgraded { get; set; }
}

/// <summary>Svar på fremdrift: Cancel = jobbet er annulleret i Fleetdeck, så agenten skal stoppe det nu.</summary>
public sealed class JobProgressResponse
{
    public bool Cancel { get; set; }
}

/// <summary>
/// Agentens fremdrift for det job, der kører lige nu. Kommandoernes output-linjer læses løbende
/// (apt, winget, scripts), og agenten sender status til serveren med få sekunders mellemrum.
/// Scripts kan selv sætte et trin med en linje som "::ws 30-60 Installerer pakker" (vises ikke i outputtet).
/// </summary>
public static partial class LiveJob
{
    private static readonly AsyncLocal<int> Ambient = new();
    private static readonly object L = new();
    private static int _id, _from, _to = 100, _aptTotal, _aptDone;
    private static bool _ranged, _dirty;
    private static double? _sub;
    private static string _phase = "", _step = "", _lastStep = "";
    private static DateTime _stepSince = DateTime.UtcNow;
    private static readonly List<string> _upgraded = [];

    /// <summary>Et program i "Opdatér alt" er opdateret: meldes med næste fremdrift.</summary>
    public static void Upgraded(string id) { lock (L) { _upgraded.Add(id); _dirty = true; } }

    /// <summary>Jobbet der kører i denne tråd/async-kæde (0 = intet). Fanges af kommando-kørerne ved start.</summary>
    public static int Current => Ambient.Value;

    public static void Begin(int jobId)
    {
        Ambient.Value = jobId;
        lock (L) { _id = jobId; _from = 0; _to = 100; _ranged = false; _sub = null; _aptTotal = _aptDone = 0; _phase = _step = _lastStep = ""; _stepSince = DateTime.UtcNow; _dirty = false; }
    }

    public static void End(int jobId)
    {
        if (Ambient.Value == jobId) Ambient.Value = 0;
        lock (L) if (_id == jobId) _id = 0;
    }

    /// <summary>Nyt trin: fremdriften går fra <paramref name="from"/> til <paramref name="to"/> procent, mens trinnet kører.</summary>
    public static void Phase(int from, int to, string text)
    {
        lock (L)
        {
            if (_id == 0) return;
            _from = Math.Clamp(from, 0, 100); _to = Math.Clamp(to, _from, 100); _ranged = true;
            _sub = null; _aptTotal = _aptDone = 0; _phase = Trim(text); _step = ""; _dirty = true;
        }
    }

    /// <summary>Kaldes af kommando-kørerne for hver output-linje. Returnerer false, hvis linjen er en ::ws-markør (skal ikke i outputtet).</summary>
    public static bool Line(int jobId, string raw)
    {
        // winget/apt tegner fremdrift med \r på samme linje – det seneste stykke tæller
        var s = raw.Split('\r').LastOrDefault(x => x.Trim().Length > 0)?.Trim() ?? "";
        if (MarkerRx().Match(s) is { Success: true } mk)
        {
            if (jobId != 0 && jobId == _id) Phase(int.Parse(mk.Groups[1].Value), int.Parse(mk.Groups[2].Value), mk.Groups[3].Value);
            return false;
        }
        if (jobId == 0 || s.Length == 0) return true;
        lock (L)
        {
            if (jobId != _id) return true;
            Match m;
            if ((m = AptSummaryRx().Match(s)).Success)
            {
                _aptTotal += int.Parse(m.Groups[1].Value) + int.Parse(m.Groups[2].Value); _dirty = true;
            }
            else if ((m = AptStepRx().Match(s)).Success)
            {
                var verb = m.Groups[1].Value; var pkg = m.Groups[2].Value.Split(':')[0];
                if (verb == "Setting up") _aptDone++;
                _step = (verb switch { "Unpacking" => "Pakker ud: ", "Setting up" => "Sætter op: ", "Removing" => "Fjerner: ", _ => "" }) + pkg;
                if (_aptTotal > 0) _sub = Math.Min(99.0, 100.0 * _aptDone / _aptTotal);
                _dirty = true;
            }
            else if (AptGetRx().IsMatch(s)) { _step = "Henter pakker"; _dirty = true; }
            else if (s.StartsWith("== ", StringComparison.Ordinal)) { _phase = Trim(s[3..]); _step = ""; _dirty = true; }
            else
            {
                if (PercentRx().Match(s) is { Success: true } p && double.TryParse(p.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v <= 100)
                    _sub = v;
                else if (SizeRx().Match(s) is { Success: true } z && Bytes(z.Groups[1].Value, z.Groups[2].Value) is var a && Bytes(z.Groups[3].Value, z.Groups[4].Value) is var b && b > 0)
                    _sub = Math.Min(100.0, 100.0 * a / b);
                var text = BarRx().Replace(s, "").Trim();
                if (text.Length > 1 && !JunkRx().IsMatch(text)) _step = Trim(text);
                _dirty = true;
            }
            return true;
        }
    }

    /// <summary>Ny status siden sidst (eller null). Med <paramref name="force"/> sendes den nuværende status alligevel (livstegn under lange trin).</summary>
    public static (int JobId, JobProgressPost Post)? Take(bool force = false)
    {
        lock (L)
        {
            if (_id == 0 || (!_dirty && !force)) return null;
            _dirty = false;
            int? pct = _sub is { } sub
                ? (int)Math.Round(_ranged ? _from + (_to - _from) * sub / 100.0 : sub)
                : _ranged ? _from : null;
            var step = _phase.Length > 0 && _step.Length > 0 ? $"{_phase} · {_step}" : _phase.Length > 0 ? _phase : _step;
            if (step != _lastStep) { _lastStep = step; _stepSince = DateTime.UtcNow; }
            var up = _upgraded.Count > 0 ? new List<string>(_upgraded) : null;
            _upgraded.Clear();
            return (_id, new JobProgressPost { Percent = pct is { } x ? Math.Clamp(x, 0, 100) : null, Step = Trim(step), StepSince = _stepSince, Upgraded = up });
        }
    }

    private static double Bytes(string n, string unit) =>
        double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v * (unit[0] switch { 'K' => 1e3, 'M' => 1e6, 'G' => 1e9, _ => 1 }) : 0;

    private static string Trim(string s) => s.Length > 200 ? s[..197] + "…" : s;

    [GeneratedRegex(@"^::ws (\d{1,3})-(\d{1,3}) (.+)$")] private static partial Regex MarkerRx();
    [GeneratedRegex(@"^(\d+) upgraded, (\d+) newly installed")] private static partial Regex AptSummaryRx();
    [GeneratedRegex(@"^(Unpacking|Setting up|Removing) (\S+)")] private static partial Regex AptStepRx();
    [GeneratedRegex(@"^(Get|Hit|Ign):\d+ ")] private static partial Regex AptGetRx();
    [GeneratedRegex(@"(\d{1,3}(?:\.\d+)?)\s?%")] private static partial Regex PercentRx();
    [GeneratedRegex(@"([\d.]+) (KB|MB|GB) / ([\d.]+) (KB|MB|GB)")] private static partial Regex SizeRx();
    [GeneratedRegex(@"[█▓▒░■□\-\\|/]{3,}")] private static partial Regex BarRx();
    [GeneratedRegex(@"^[\s\-\\|/█▓▒░.%\d]+$")] private static partial Regex JunkRx();
}
