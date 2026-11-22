using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Fleetdeck.Shared;

/// <summary>Mac: temperaturmålingens tilstand, som tjenesten skriver i status.json (appens Funktioner-vindue viser den).</summary>
public sealed class MacTempState
{
    /// <summary>Slået til (standard). Fra = tjenesten måler ikke og melder "off" til Fleetdeck.</summary>
    public bool On { get; set; } = true;
    public double? Cpu { get; set; }
    public double? Gpu { get; set; }
    /// <summary>Hvor målingen kommer fra (<see cref="MacTemps.AppleSilicon"/>, <see cref="MacTemps.Smc"/>, <see cref="MacTemps.Powermetrics"/>); null uden måling.</summary>
    public string? Source { get; set; }
    /// <summary>Hvorfor der ingen måling er (null, når der er en).</summary>
    public string? Note { get; set; }
    public DateTime? At { get; set; }
}

/// <summary>
/// Temperaturmåling på Mac uden ekstra programmer eller kerneudvidelser:
/// Apple silicon – IOKit's HID-sensorer (MacSensors); Intel – SMC'en via IOKit (AppleSMC), og ellers Apples egen powermetrics (root).
/// Slås fra med en markørfil, som kun root kan lægge (tjenesten selv eller hjælperens script).
/// </summary>
public static partial class MacTemps
{
    public const string FeatureId = "temperatur";
    public const string OffFile = "/etc/fleetdeck/temps-off";
    /// <summary>Temperatures.Source til Fleetdeck: slået fra på Mac'en / slået til, men ingen sensor svarede.</summary>
    public const string Off = "off", None = "none";
    public const string AppleSilicon = "applesilicon", Smc = "smc", Powermetrics = "powermetrics";
    /// <summary>ExtensionInstall.Detect: "installeret" = maskinen melder en CPU-temperatur (ikke et navn i programlisten).</summary>
    public const string Detect = "temps";

    public static string SourceName(string? source) => source switch
    {
        AppleSilicon => "Apple silicon-sensorerne",
        Smc => "SMC (Intel)",
        Powermetrics => "powermetrics",
        null or "" => "–",
        _ => source,
    };

    /// <summary>Teksten under "Temperaturmåling" i appen.</summary>
    public static string Text(MacTempState? t)
    {
        if (t is null) return "Kræver en nyere Fleetdeck-tjeneste på Mac'en (opdatér agenten)";
        if (!t.On) return "Slået fra – Fleetdeck måler ikke temperaturen";
        if (t.Cpu is not double cpu) return t.Note is { Length: > 0 } n ? $"Slået til, men ingen måling: {n}" : "Slået til – venter på første måling";
        var s = $"CPU {cpu.ToString("0", CultureInfo.InvariantCulture)} °C";
        if (t.Gpu is double gpu) s += $" · GPU {gpu.ToString("0", CultureInfo.InvariantCulture)} °C";
        return $"{s} · fra {SourceName(t.Source)}";
    }

    [GeneratedRegex(@"^\s*(CPU|GPU) die temperature:\s*(-?[0-9]+(?:[.,][0-9]+)?)\s*C", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex DieTemp();

    /// <summary>"powermetrics --samplers smc" (Intel): "CPU die temperature: 52.38 C" og "GPU die temperature: 46.00 C".</summary>
    public static (double? Cpu, double? Gpu) ParsePowermetrics(string? text)
    {
        double? cpu = null, gpu = null;
        foreach (Match m in DieTemp().Matches(text ?? ""))
        {
            if (!double.TryParse(m.Groups[2].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !Plausible(v)) continue;
            if (m.Groups[1].Value.Equals("CPU", StringComparison.OrdinalIgnoreCase)) cpu ??= Math.Round(v, 1);
            else gpu ??= Math.Round(v, 1);
        }
        return (cpu, gpu);
    }

    public static bool Plausible(double c) => c is > 5 and < 130;

    // ------------------------------------------------------------------ SMC (Intel)

    /// <summary>SMC-nøglen som tal ("TC0D" → 0x54433044), som IOKit's SMCKeyData_t.key vil have den.</summary>
    public static uint FourCC(string key)
    {
        if (key.Length != 4) throw new ArgumentException("En SMC-nøgle har 4 tegn", nameof(key));
        return (uint)key[0] << 24 | (uint)key[1] << 16 | (uint)key[2] << 8 | key[3];
    }

    public static string FourCCText(uint v) => new([(char)(v >> 24 & 0xff), (char)(v >> 16 & 0xff), (char)(v >> 8 & 0xff), (char)(v & 0xff)]);

    /// <summary>
    /// En SMC-værdi som tal: spXY (fortegn, Y = brøkbits i hex, fx sp78 = grader i 1/256), fpXY (uden fortegn, fx fpe2 = blæser-rpm),
    /// "flt " (float, little-endian – Apple silicon), ui8/ui16/ui32. Null = ukendt type eller for få bytes.
    /// </summary>
    public static double? DecodeSmc(string type, ReadOnlySpan<byte> data)
    {
        if (type.Length == 4 && (type.StartsWith("sp", StringComparison.Ordinal) || type.StartsWith("fp", StringComparison.Ordinal))
            && int.TryParse(type.AsSpan(3, 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var frac) && data.Length >= 2)
        {
            var raw = BinaryPrimitives.ReadUInt16BigEndian(data);
            return (type[0] == 's' ? (short)raw : raw) / (double)(1 << frac);
        }
        return type switch
        {
            "flt " when data.Length >= 4 => BinaryPrimitives.ReadSingleLittleEndian(data),
            "ui8 " when data.Length >= 1 => data[0],
            "ui16" when data.Length >= 2 => BinaryPrimitives.ReadUInt16BigEndian(data),
            "ui32" when data.Length >= 4 => BinaryPrimitives.ReadUInt32BigEndian(data),
            _ => null,
        };
    }

    /// <summary>CPU-kernen/-chippen (die) først; CPU-nærheden (TC0P) kun, når ingen die-måler svarer.</summary>
    public static readonly string[] SmcCpuDie = ["TC0D", "TC0E", "TC0F", "TCXC", "TC0C", "TC1C", "TC2C", "TC3C", "TC4C"];
    public static readonly string[] SmcCpuOther = ["TC0P", "TC0H"];
    public static readonly string[] SmcGpu = ["TG0D", "TG0P", "TGDD", "TG0H"];
    public static IEnumerable<string> SmcKeys => SmcCpuDie.Concat(SmcCpuOther).Concat(SmcGpu);

    /// <summary>CPU = varmeste die-måler (ellers nærheds-måleren); GPU = første, der svarer. Urimelige værdier (0, -127, 255) springes over.</summary>
    public static (double? Cpu, double? Gpu) FromSmc(IReadOnlyDictionary<string, double> values)
    {
        double? Max(IEnumerable<string> keys)
        {
            var v = keys.Where(values.ContainsKey).Select(k => values[k]).Where(Plausible).ToList();
            return v.Count == 0 ? null : Math.Round(v.Max(), 1);
        }
        var cpu = Max(SmcCpuDie) ?? Max(SmcCpuOther);
        var gpu = SmcGpu.Where(values.ContainsKey).Select(k => values[k]).Where(Plausible).Select(x => (double?)Math.Round(x, 1)).FirstOrDefault();
        return (cpu, gpu);
    }
}

/// <summary>
/// "Funktioner" i Mac-appen (som udvidelserne i Windows' statusvindue): temperaturmåling til/fra og hjælpeværktøjerne, der kan installeres.
/// Ændringer sendes som administrator-ordrer til tjenesten (samme kanal som pause og udvidelser), og tjenesten melder dem til Fleetdeck.
/// </summary>
public static class MacFeatures
{
    public enum Kind { Toggle, Install }

    /// <param name="On">Toggle: slået til. Install: installeret.</param>
    /// <param name="Busy">Ændringen er på vej (sendt, men tjenesten/serveren har ikke meldt den nye tilstand endnu).</param>
    /// <param name="CanChange">Kan ændres herfra lige nu.</param>
    public sealed record Row(string Id, string Group, string Title, string Sub, Kind Kind, bool On, bool Busy, bool CanChange);

    public const string Measuring = "Måling", Tools = "Hjælpeværktøjer";
    public const string FeatureAction = "feature";
    /// <summary>Så længe venter appen på, at tjenesten melder en ændring, før den siger, at intet er sket.</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    /// <summary>Hjælpeværktøjer, der installeres med Homebrew (de kan ikke installeres, før Homebrew er der).</summary>
    public static readonly IReadOnlyDictionary<string, string> Requires = new Dictionary<string, string>
    {
        ["diskhelbred"] = "homebrew", ["mas"] = "homebrew", ["fjernskrivebord"] = "homebrew",
    };

    public static TrayCommand Command(string id, bool on) => new() { Action = FeatureAction, Id = id, On = on };

    /// <summary>Rækkerne i vinduet med deres rigtige tilstand. <paramref name="sent"/>: hvad brugeren lige har slået til/fra eller installeret (og hvornår).</summary>
    public static List<Row> Rows(TrayStatus? s, IEnumerable<ExtensionDef> builtIn, IReadOnlyDictionary<string, (bool On, DateTime At)> sent, DateTime now)
    {
        var rows = new List<Row>();
        var t = s?.Temps;
        var waiting = sent.TryGetValue(MacTemps.FeatureId, out var ts) && now - ts.At < Wait && (t is null || t.On != ts.On);
        rows.Add(new(MacTemps.FeatureId, Measuring, "Temperaturmåling",
            waiting ? (ts.On ? "Slås til…" : "Slås fra…") : MacTemps.Text(t), Kind.Toggle,
            On: waiting ? ts.On : t?.On ?? false, Busy: waiting, CanChange: t is not null && !waiting));

        var ext = MacSetup.ExtensionRows(s, builtIn).Where(r => r.Id != MacTemps.FeatureId).ToList();
        foreach (var r in ext)
        {
            var busy = !r.Installed && (r.Pending || sent.TryGetValue(r.Id, out var es) && now - es.At < Wait);
            var sub = r.Installed ? "Installeret" : busy ? "Installeres…" : r.Gives;
            if (!r.Installed && !busy && Requires.TryGetValue(r.Id, out var req) && ext.FirstOrDefault(x => x.Id == req) is { Installed: false } need)
                sub = need.Pending ? $"Venter på {need.Name}" : $"{r.Gives} · kræver {need.Name} (installér det først)";
            rows.Add(new(r.Id, r.Id == "diskhelbred" ? Measuring : Tools, r.Name, sub, Kind.Install,
                On: r.Installed, Busy: busy, CanChange: s is not null && !r.Installed && !busy));
        }
        return rows;
    }

    /// <summary>De to linjer i statusvinduets kort "Funktioner og hjælpeværktøjer".</summary>
    public static (string Temps, string Tools) Summary(TrayStatus? s, IEnumerable<ExtensionDef> builtIn)
    {
        var t = s?.Temps;
        var temps = "Temperaturmåling: " + (t is null ? "kræver en nyere Fleetdeck-tjeneste"
            : !t.On ? "slået fra"
            : t.Cpu is double c ? $"CPU {c.ToString("0", CultureInfo.InvariantCulture)} °C"
            : "slået til, ingen måling endnu");
        var tools = Rows(s, builtIn, new Dictionary<string, (bool, DateTime)>(), DateTime.UtcNow).Where(r => r.Kind == Kind.Install).ToList();
        var busy = tools.Count(r => r.Busy);
        return (temps, $"{tools.Count(r => r.On)} af {tools.Count} hjælpeværktøjer installeret" + (busy > 0 ? $" · {busy} installeres" : ""));
    }

    /// <summary>Så længe efter, at en installation holdt op med at være på vej, ventes der på, at Fleetdeck ser programmet (programlisten sendes bagefter).</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Beskeden, når en ændring er færdig: (tekst, tone 'o'/'w'/'b'), eller null, mens den stadig er på vej.
    /// <paramref name="lastBusy"/>: sidste gang rækken var på vej (null = aldrig set på vej).
    /// </summary>
    public static (string Text, char Tone)? Outcome(Row now, (bool On, DateTime At) sent, DateTime? lastBusy, DateTime nowUtc)
    {
        if (now.Kind == Kind.Toggle)
        {
            if (now.Busy) return null;
            if (now.On != sent.On) return ("Fleetdeck-tjenesten har ikke svaret – kører den? Prøv igen om lidt.", 'w');
            return sent.On ? ($"{now.Title} er slået til: {now.Sub}", now.Sub.StartsWith("CPU ", StringComparison.Ordinal) ? 'o' : 'w')
                : ($"{now.Title} er slået fra.", 'o');
        }
        if (now.On) return ($"{now.Title} er installeret.", 'o');
        if (now.Busy) return null;
        if (lastBusy is { } lb)
            return nowUtc - lb >= Settle ? ($"{now.Title} blev ikke installeret – se jobbet under maskinen i Fleetdeck.", 'b') : null;
        return nowUtc - sent.At >= Wait ? ("Fleetdeck-tjenesten har ikke svaret – kører den? Prøv igen om lidt.", 'w') : null;
    }
}
