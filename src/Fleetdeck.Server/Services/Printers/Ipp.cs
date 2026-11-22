using System.Buffers.Binary;
using System.Text;
using Fleetdeck.Shared;

namespace Fleetdeck.Server.Services.Printers;

/// <summary>
/// Det lille stykke IPP (RFC 8010/8011), Fleetdeck bruger: Get-Printer-Attributes. Kodning af forespørgslen og læsning af svaret.
/// Samlinger (media-col …) læses lineært og bruges ikke.
/// </summary>
public static class Ipp
{
    public const byte OperationTag = 0x01, PrinterTag = 0x04, EndTag = 0x03;
    private const byte Integer = 0x21, Boolean = 0x22, Enum = 0x23, Keyword = 0x44, Uri = 0x45, Charset = 0x47, Language = 0x48;

    public static readonly string[] Requested =
    [
        "printer-make-and-model", "printer-info", "printer-location", "printer-name", "printer-uuid", "printer-state", "printer-state-reasons",
        "printer-state-message", "color-supported", "sides-supported", "sides-default", "media-supported", "media-default", "document-format-supported",
        "ipp-features-supported", "marker-names", "marker-levels", "marker-colors", "marker-types", "marker-low-levels", "printer-more-info",
    ];

    /// <summary>Get-Printer-Attributes (0x000B), IPP 2.0.</summary>
    public static byte[] GetPrinterAttributes(string printerUri, int requestId = 1)
    {
        using var ms = new MemoryStream();
        ms.Write([0x02, 0x00, 0x00, 0x0B]);
        Span<byte> id = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(id, requestId);
        ms.Write(id);
        ms.WriteByte(OperationTag);
        Attr(ms, Charset, "attributes-charset", "utf-8");
        Attr(ms, Language, "attributes-natural-language", "en");
        Attr(ms, Uri, "printer-uri", printerUri);
        for (var i = 0; i < Requested.Length; i++) Attr(ms, Keyword, i == 0 ? "requested-attributes" : "", Requested[i]);
        ms.WriteByte(EndTag);
        return ms.ToArray();
    }

    private static void Attr(Stream s, byte tag, string name, string value)
    {
        var n = Encoding.UTF8.GetBytes(name);
        var v = Encoding.UTF8.GetBytes(value);
        s.WriteByte(tag);
        Short(s, n.Length); s.Write(n);
        Short(s, v.Length); s.Write(v);
    }

    private static void Short(Stream s, int v) { s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }

    public sealed record Response(int Status, int RequestId, Dictionary<string, List<object>> Operation, Dictionary<string, List<object>> Printer)
    {
        public bool Ok => Status < 0x0100;   // successful-ok*
    }

    /// <summary>Læs et IPP-svar. Kaster FormatException, hvis det ikke er IPP.</summary>
    public static Response Parse(ReadOnlySpan<byte> b)
    {
        if (b.Length < 9) throw new FormatException("for kort til et IPP-svar");
        if (b[0] is < 1 or > 2) throw new FormatException("ikke et IPP-svar");
        var status = BinaryPrimitives.ReadUInt16BigEndian(b[2..]);
        var reqId = BinaryPrimitives.ReadInt32BigEndian(b[4..]);
        var op = new Dictionary<string, List<object>>(StringComparer.Ordinal);
        var pr = new Dictionary<string, List<object>>(StringComparer.Ordinal);
        Dictionary<string, List<object>>? group = null;
        List<object>? last = null;
        var i = 8;
        while (i < b.Length)
        {
            var tag = b[i++];
            if (tag == EndTag) break;
            if (tag < 0x10)
            {
                // ny gruppe: operation og printer gemmes, resten (job, ikke-understøttet) springes over
                group = tag switch { OperationTag => op, PrinterTag => pr, _ => null };
                last = null;
                continue;
            }
            if (i + 2 > b.Length) throw new FormatException("afkortet IPP-svar");
            int nl = BinaryPrimitives.ReadUInt16BigEndian(b[i..]); i += 2;
            if (i + nl + 2 > b.Length) throw new FormatException("afkortet IPP-svar");
            var name = Encoding.UTF8.GetString(b.Slice(i, nl)); i += nl;
            int vl = BinaryPrimitives.ReadUInt16BigEndian(b[i..]); i += 2;
            if (i + vl > b.Length) throw new FormatException("afkortet IPP-svar");
            var v = b.Slice(i, vl); i += vl;
            object? value = tag switch
            {
                Integer or Enum when vl == 4 => BinaryPrimitives.ReadInt32BigEndian(v),
                Boolean when vl == 1 => v[0] != 0,
                >= 0x10 and < 0x20 => null,                               // out-of-band (unknown, no-value …)
                >= 0x30 and <= 0x4A and not 0x31 and not 0x32 and not 0x33 and not 0x34 and not 0x37 => Encoding.UTF8.GetString(v),
                _ => v.ToArray(),
            };
            if (group is null) continue;
            if (nl > 0)
            {
                last = [];
                group[name] = last;
            }
            if (last is not null && value is not null) last.Add(value);
        }
        return new(status, reqId, op, pr);
    }
}

/// <summary>En printers egenskaber og tilstand fra IPP (gemmes i Printer.StatusJson).</summary>
public sealed class PrinterInfo
{
    public string? MakeModel { get; set; }
    public string? Info { get; set; }
    public string? Location { get; set; }
    public string? Uuid { get; set; }
    public bool? Color { get; set; }
    public bool? Duplex { get; set; }
    /// <summary>Virker uden driver: IPP Everywhere/AirPrint (PWG-raster, URF eller PDF).</summary>
    public bool? Driverless { get; set; }
    public List<string> Media { get; set; } = [];
    public string? MediaDefault { get; set; }
    /// <summary>idle | processing | stopped | off (svarer ikke).</summary>
    public string State { get; set; } = "off";
    public List<string> Reasons { get; set; } = [];
    public string? Message { get; set; }
    public List<PrinterMarker> Markers { get; set; } = [];
    public string? Error { get; set; }
    public DateTime? At { get; set; }
}

public sealed class PrinterMarker
{
    public string Name { get; set; } = "";
    /// <summary>0–100, eller null når printeren ikke ved det.</summary>
    public int? Level { get; set; }
    public string? Color { get; set; }
    public string? Type { get; set; }
    public int Low { get; set; } = 15;
}

public static class PrinterInfoReader
{
    private static List<object> Get(Dictionary<string, List<object>> a, string k) => a.TryGetValue(k, out var v) ? v : [];
    private static string? Str(Dictionary<string, List<object>> a, string k) => Get(a, k).OfType<string>().FirstOrDefault() is { Length: > 0 } s ? s : null;
    private static List<string> Strs(Dictionary<string, List<object>> a, string k) => Get(a, k).OfType<string>().ToList();
    private static List<int> Ints(Dictionary<string, List<object>> a, string k) => Get(a, k).OfType<int>().ToList();

    public static PrinterInfo From(Ipp.Response r)
    {
        var a = r.Printer;
        var formats = Strs(a, "document-format-supported");
        var features = Strs(a, "ipp-features-supported");
        var sides = Strs(a, "sides-supported");
        var state = Ints(a, "printer-state").FirstOrDefault();
        var info = new PrinterInfo
        {
            MakeModel = Str(a, "printer-make-and-model"),
            Info = Str(a, "printer-info"),
            Location = Str(a, "printer-location"),
            Uuid = Str(a, "printer-uuid"),
            Color = Get(a, "color-supported").OfType<bool>().Cast<bool?>().FirstOrDefault(),
            Duplex = sides.Count == 0 ? null : sides.Any(s => s.StartsWith("two-sided", StringComparison.Ordinal)),
            Driverless = formats.Count == 0 && features.Count == 0 ? null
                : features.Contains("ipp-everywhere") || formats.Any(f => f is "image/pwg-raster" or "image/urf" or "application/pdf"),
            Media = Strs(a, "media-supported").Take(40).ToList(),
            MediaDefault = Str(a, "media-default"),
            State = state switch { 3 => "idle", 4 => "processing", 5 => "stopped", _ => "idle" },
            Reasons = Strs(a, "printer-state-reasons").Select(Reason).Where(x => x is not ("none" or "")).Distinct().ToList(),
            Message = Str(a, "printer-state-message"),
            At = DateTime.UtcNow,
        };
        var names = Strs(a, "marker-names");
        var levels = Ints(a, "marker-levels");
        var colors = Strs(a, "marker-colors");
        var types = Strs(a, "marker-types");
        var lows = Ints(a, "marker-low-levels");
        for (var i = 0; i < names.Count && i < 16; i++)
            info.Markers.Add(new PrinterMarker
            {
                Name = names[i],
                Level = i < levels.Count && levels[i] is >= 0 and <= 100 ? levels[i] : null,
                Color = i < colors.Count ? colors[i].Split('#', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() is { } c ? "#" + c : null : null,
                Type = i < types.Count ? types[i] : null,
                Low = i < lows.Count && lows[i] is > 0 and < 100 ? lows[i] : 15,
            });
        return info;
    }

    /// <summary>"media-jam-error" → "media-jam" (alvorligheden står i Describe).</summary>
    public static string Reason(string r)
    {
        foreach (var suf in new[] { "-error", "-warning", "-report" })
            if (r.EndsWith(suf, StringComparison.Ordinal)) return r[..^suf.Length];
        return r;
    }
}

/// <summary>Kort status på dansk til siden, sensorerne og advarslerne.</summary>
public static class PrinterStatus
{
    public const int LowPct = 15;

    /// <summary>Danske navne på de almindelige tilstande fra printer-state-reasons.</summary>
    public static string? ReasonText(string r) => r switch
    {
        "media-jam" => "Papirstop",
        "door-open" or "cover-open" or "interlock-open" => "Låg åbent",
        "media-empty" or "media-needed" => "Mangler papir",
        "media-low" => "Lidt papir",
        "toner-empty" or "marker-supply-empty" => "Tom for blæk/toner",
        "toner-low" or "marker-supply-low" => "Lav på blæk/toner",
        "marker-waste-full" or "marker-waste-almost-full" => "Spildbeholder fuld",
        "input-tray-missing" => "Papirbakke mangler",
        "output-area-full" => "Udbakken er fuld",
        "offline" or "shutdown" => "Offline",
        "paused" => "Sat på pause",
        "other" => null,
        _ => null,
    };

    public static bool IsError(string r) => r is "media-jam" or "door-open" or "cover-open" or "interlock-open" or "media-empty" or "media-needed"
        or "toner-empty" or "marker-supply-empty" or "marker-waste-full" or "input-tray-missing" or "output-area-full" or "offline" or "shutdown";

    /// <summary>"sort", "cyan", "magenta", "gul" ud fra navnet eller farven.</summary>
    public static string ColorName(PrinterMarker m)
    {
        var n = m.Name.ToLowerInvariant();
        if (n.Contains("black") || n.Contains("sort") || n.Contains("schwarz") || m.Color?.ToUpperInvariant() == "#000000") return "sort";
        if (n.Contains("cyan") || m.Color?.ToUpperInvariant() == "#00FFFF") return "cyan";
        if (n.Contains("magenta") || m.Color?.ToUpperInvariant() == "#FF00FF") return "magenta";
        if (n.Contains("yellow") || n.Contains("gul") || m.Color?.ToUpperInvariant() == "#FFFF00") return "gul";
        return m.Name;
    }

    public static List<PrinterMarker> Low(PrinterInfo i) =>
        i.Markers.Where(m => m.Level is int l && l < LowPct && (m.Type is null || !m.Type.Contains("waste", StringComparison.Ordinal))).ToList();

    /// <summary>(tekst, niveau): niveau er ok | warn | error | off.</summary>
    public static (string Text, string Level) Describe(PrinterInfo? i)
    {
        if (i is null) return ("Ikke tjekket endnu", "off");
        if (i.State == "off") return ("Slukket", "off");
        var errors = i.Reasons.Where(IsError).Select(ReasonText).OfType<string>().Distinct().ToList();
        if (errors.Count > 0) return (string.Join(", ", errors), "error");
        if (i.State == "stopped") return (i.Reasons.Select(ReasonText).OfType<string>().FirstOrDefault() ?? "Stoppet", "error");
        var low = Low(i);
        if (low.Count > 0) return ("Lav på " + string.Join(" og ", low.Select(ColorName).Distinct()), "warn");
        var warn = i.Reasons.Select(ReasonText).OfType<string>().FirstOrDefault();
        if (warn is not null) return (warn, "warn");
        return (i.State == "processing" ? "Printer" : "Klar", "ok");
    }
}
