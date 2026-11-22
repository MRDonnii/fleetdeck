using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Fleetdeck.Shared;

public static class SysLogLevels
{
    public const string Critical = "critical", Error = "error", Warning = "warning", Info = "info";

    public static int Rank(string? level) => level switch { Critical => 3, Error => 2, Warning => 1, _ => 0 };
    public static string Normalize(string? level) => level?.ToLowerInvariant() switch
    {
        Critical => Critical, Error => Error, Warning => Warning, _ => Info,
    };
}

/// <summary>Én fejl eller advarsel fra maskinens systemlog. Tiden er UTC. Beskeden er renset for hemmeligheder (<see cref="SysLogPolicy.Clean"/>).</summary>
public sealed class SysLogEvent
{
    public DateTime Time { get; set; }
    /// <summary>critical, error, warning eller info (<see cref="SysLogLevels"/>).</summary>
    public string Level { get; set; } = SysLogLevels.Error;
    /// <summary>Hvilken log: System, Application, WindowsUpdateClient, journal, kernel, syslog, unified, panic.</summary>
    public string Log { get; set; } = "";
    /// <summary>Kilden: Windows-udbyder (fx Microsoft-Windows-Kernel-Power), systemd-enhed/program eller Mac-proces.</summary>
    public string Source { get; set; } = "";
    /// <summary>Windows' hændelses-id. Null på Linux og Mac.</summary>
    public int? EventId { get; set; }
    public string Message { get; set; } = "";
    /// <summary>Windows: postens nummer i loggen (gør hændelsen entydig).</summary>
    public long? RecordId { get; set; }
    /// <summary>Kendt hændelsestype (<see cref="SysLogKinds"/>), sat af serveren og live-loggen. Null = ukendt.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Kind { get; set; }
}

/// <summary>Grænser og rensning for systemloggen: hvor meget, hvor ofte, og at intet hemmeligt forlader maskinen.</summary>
public static partial class SysLogPolicy
{
    /// <summary>Højst så mange hændelser pr. helbredsrunde (de alvorligste og nyeste vælges).</summary>
    public const int MaxPerReport = 200;
    public const int MaxMessage = 600, MaxSource = 100, MaxLog = 60;
    /// <summary>Serveren gemmer hændelser så længe og højst så mange pr. maskine.</summary>
    public const int RetentionDays = 30, MaxPerMachine = 5000;
    /// <summary>Indsamling højst så ofte (helbred sendes ofte, fx når et netværksdrev ændrer sig).</summary>
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(10), EveryMac = TimeSpan.FromMinutes(30);
    /// <summary>Første gang efter start kigges et døgn tilbage; aldrig mere end to døgn (fx efter lang tid slukket).</summary>
    public static readonly TimeSpan FirstLookback = TimeSpan.FromHours(24), MaxLookback = TimeSpan.FromHours(48);

    [GeneratedRegex(@"[ \t]+")] private static partial Regex Spaces();
    [GeneratedRegex(@"(\r?\n\s*){2,}")] private static partial Regex BlankLines();
    [GeneratedRegex(@"(?i)\b(https?://[^\s?#""']+)\?[^\s""']*")] private static partial Regex UrlQuery();
    [GeneratedRegex(@"0x[0-9a-f]+|\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b|\d+", RegexOptions.IgnoreCase)] private static partial Regex Numbers();
    [GeneratedRegex(@"\s+")] private static partial Regex AnySpace();

    /// <summary>Fjerner hemmeligheder (adgangskoder, tokens, URL-parametre, nøgler) og begrænser størrelsen. Ændrer hændelsen og giver den tilbage.</summary>
    public static SysLogEvent Clean(SysLogEvent e)
    {
        var m = LogBundle.Redact(e.Message ?? "");
        m = UrlQuery().Replace(m, "$1?" + LogBundle.Hidden);
        m = BlankLines().Replace(Spaces().Replace(m.Replace("\0", ""), " "), "\n").Trim();
        e.Message = Cut(m, MaxMessage);
        e.Source = Cut(LogBundle.Redact((e.Source ?? "").Trim()), MaxSource);
        e.Log = Cut((e.Log ?? "").Trim(), MaxLog);
        e.Level = SysLogLevels.Normalize(e.Level);
        e.Time = e.Time.Kind switch { DateTimeKind.Utc => e.Time, DateTimeKind.Local => e.Time.ToUniversalTime(), _ => DateTime.SpecifyKind(e.Time, DateTimeKind.Utc) };
        return e;
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>
    /// Nøglen, ens hændelser samles under (og som kan skjules): kilde + hændelses-id på Windows, ellers kilde + kendt type
    /// eller kilde + beskeden uden tal (pid, adresser og tider varierer fra gang til gang).
    /// </summary>
    public static string GroupKey(SysLogEvent e, string? kind = null)
    {
        var src = (e.Source ?? "").Trim().ToLowerInvariant();
        if (e.EventId is int id) return $"{src}|{id}";
        kind ??= e.Kind ?? SysLogKinds.Of(e);
        if (kind is not null) return $"{src}|{kind}";
        var norm = AnySpace().Replace(Numbers().Replace((e.Message ?? "").ToLowerInvariant(), "#"), " ").Trim();
        return $"{src}|{(norm.Length > 80 ? norm[..80] : norm)}";
    }

    /// <summary>Entydig nøgle til at kende en hændelse igen, hvis agenten sender den to gange (fx efter en genstart).</summary>
    public static string Hash(SysLogEvent e) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{e.Time.Ticks}|{e.Log}|{e.Source}|{e.EventId}|{e.RecordId}|{e.Message}")))[..32];

    /// <summary>Højst <paramref name="max"/> hændelser: de alvorligste og kendte først, så de nyeste. Gives tilbage i tidsorden.</summary>
    public static List<SysLogEvent> Prioritize(IEnumerable<SysLogEvent> events, int max = MaxPerReport) =>
        events.OrderByDescending(e => SysLogLevels.Rank(e.Level)).ThenByDescending(e => SysLogKinds.Of(e) is { } k && k != SysLogKinds.DcomNoise).ThenByDescending(e => e.Time)
            .Take(max).OrderBy(e => e.Time).ToList();
}

/// <summary>
/// Kendte hændelser, som Fleetdeck kan forklare på dansk og bruge til helbredsadvarsler. Teksterne ligger i web (syslogText.ts);
/// her afgøres kun typen.
/// </summary>
public static partial class SysLogKinds
{
    public const string UnexpectedShutdown = "unexpected-shutdown", Shutdown6008 = "shutdown-6008", BugCheck = "bugcheck",
        WheaFatal = "whea-fatal", WheaCorrected = "whea-corrected", DiskBadBlock = "disk-bad-block", DiskPaging = "disk-paging",
        DiskRetry = "disk-retry", DiskController = "disk-controller", NtfsCorrupt = "ntfs-corrupt", StorportReset = "storport-reset",
        UpdateFailed = "wu-failed", DcomNoise = "dcom-noise", Oom = "oom", IoError = "io-error", KernelPanic = "kernel-panic",
        FsError = "fs-error", Segfault = "segfault", MacPanic = "mac-panic";

    /// <summary>Typer, der hører hjemme på den samlede side "Hændelser" (alt andet vises kun på maskinens egen fane).</summary>
    public static readonly IReadOnlySet<string> Serious = new HashSet<string>
    {
        UnexpectedShutdown, Shutdown6008, BugCheck, WheaFatal, WheaCorrected, DiskBadBlock, DiskPaging, DiskRetry, DiskController,
        NtfsCorrupt, StorportReset, UpdateFailed, Oom, IoError, KernelPanic, FsError, MacPanic,
    };

    [GeneratedRegex(@"Out of memory: Kill(ed)? process|Memory cgroup out of memory: Kill(ed)? process", RegexOptions.IgnoreCase)] private static partial Regex OomRx();
    [GeneratedRegex(@"\bI/O error\b|critical medium error|Unrecovered read error|failed command: (READ|WRITE)|\bUNC\b.*error", RegexOptions.IgnoreCase)] private static partial Regex IoRx();
    // "traps: prog[123] general protection fault ip:…" er et program, der går ned (ikke kernen): kun kernens egen GPF-linje tæller
    [GeneratedRegex(@"Kernel panic|kernel BUG at|\bOops:|general protection fault(, probably for non-canonical address|:\s*[0-9a-f]{4}\s*\[#)", RegexOptions.IgnoreCase)] private static partial Regex PanicRx();
    [GeneratedRegex(@"EXT4-fs error|BTRFS (error|critical)|XFS .*(corruption|Corruption)|F2FS-fs .*error", RegexOptions.IgnoreCase)] private static partial Regex FsRx();
    [GeneratedRegex(@"\bsegfault at\b", RegexOptions.IgnoreCase)] private static partial Regex SegfaultRx();
    /// <summary>Diskettestation, cd/dvd-drev og loop-enheder: I/O-fejl her er støj (fx VM'er, der leder efter en diskette, eller et tomt drev).</summary>
    [GeneratedRegex(@"\b(fd|sr|loop)\d+\b")] private static partial Regex NoiseDevRx();

    private static bool Src(string s, params string[] names) => names.Any(n => s.Equals(n, StringComparison.OrdinalIgnoreCase));

    public static string? Of(SysLogEvent e)
    {
        var s = (e.Source ?? "").Trim();
        if (e.EventId is int id)
        {
            if (Src(s, "Microsoft-Windows-Kernel-Power") && id == 41) return UnexpectedShutdown;
            if (Src(s, "EventLog") && id == 6008) return Shutdown6008;
            if (Src(s, "BugCheck", "Microsoft-Windows-WER-SystemErrorReporting") && id == 1001) return BugCheck;
            if (Src(s, "Microsoft-Windows-WHEA-Logger"))
                return id is 1 or 18 or 20 or 46 || SysLogLevels.Rank(e.Level) >= SysLogLevels.Rank(SysLogLevels.Error) ? WheaFatal : WheaCorrected;
            if (Src(s, "disk")) return id switch { 7 => DiskBadBlock, 51 => DiskPaging, 153 => DiskRetry, 11 => DiskController, _ => null };
            if (Src(s, "Ntfs", "Microsoft-Windows-Ntfs") && id == 55) return NtfsCorrupt;
            if (Src(s, "storport", "Microsoft-Windows-StorPort") && id == 129) return StorportReset;
            if (Src(s, "Microsoft-Windows-WindowsUpdateClient") && id == 20) return UpdateFailed;
            if (Src(s, "Microsoft-Windows-DistributedCOM", "DCOM") && id is 10016 or 10010 or 10005) return DcomNoise;
            return null;
        }
        if (e.Log == "panic") return MacPanic;
        var m = e.Message ?? "";
        if (OomRx().IsMatch(m)) return Oom;
        if (PanicRx().IsMatch(m)) return KernelPanic;
        if (FsRx().IsMatch(m)) return FsError;
        if (IoRx().IsMatch(m)) return NoiseDevRx().IsMatch(m) ? null : IoError;
        if (SegfaultRx().IsMatch(m)) return Segfault;
        return null;
    }
}

/// <summary>
/// Hvornår agenten skal indsamle, og hvorfra: højst hvert <c>every</c>, fra der hvor serveren sidst fik hændelserne.
/// Mislykkes afsendelsen, hentes samme tidsrum igen næste gang (serveren kender dubletter).
/// </summary>
public sealed class SysLogSchedule(TimeSpan every)
{
    private DateTime? _cursor;
    private DateTime _lastTry = DateTime.MinValue;

    public bool Due(DateTime nowUtc) => nowUtc - _lastTry >= every;
    public DateTime Since(DateTime nowUtc)
    {
        var floor = nowUtc - SysLogPolicy.MaxLookback;
        var from = _cursor ?? nowUtc - SysLogPolicy.FirstLookback;
        return from < floor ? floor : from;
    }
    public void Tried(DateTime nowUtc) => _lastTry = nowUtc;
    /// <summary>Serveren har fået alt til og med <paramref name="upToUtc"/>.</summary>
    public void Sent(DateTime upToUtc) => _cursor = upToUtc;
}

/// <summary>journald: uddata fra <c>journalctl -o json</c> (én JSON-linje pr. post).</summary>
public static class JournalLog
{
    /// <summary>Kommandoen til indsamling: fejl og advarsler (prioritet 0-4) siden et tidspunkt, også kernen.</summary>
    public static string Command(DateTime sinceUtc, int max = 2000) =>
        $"journalctl -o json --no-pager -q -p warning --since @{new DateTimeOffset(DateTime.SpecifyKind(sinceUtc, DateTimeKind.Utc)).ToUnixTimeSeconds()} -n {max}";

    public static List<SysLogEvent> Parse(string output)
    {
        var o = new List<SysLogEvent>();
        foreach (var line in output.Split('\n'))
            if (Line(line) is { } e) o.Add(e);
        return o;
    }

    public static SysLogEvent? Line(string line)
    {
        line = line.Trim();
        if (line.Length < 2 || line[0] != '{') return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            if (Str(r, "__REALTIME_TIMESTAMP") is not { } ts || !long.TryParse(ts, out var us)) return null;
            var prio = int.TryParse(Str(r, "PRIORITY"), out var p) ? p : 6;
            var kernel = Str(r, "_TRANSPORT") == "kernel";
            var src = kernel ? "kernel" : Str(r, "SYSLOG_IDENTIFIER") ?? Unit(Str(r, "_SYSTEMD_UNIT")) ?? Str(r, "_COMM") ?? "ukendt";
            return new SysLogEvent
            {
                Time = DateTime.UnixEpoch.AddTicks(checked(us * 10)),
                Level = prio <= 2 ? SysLogLevels.Critical : prio == 3 ? SysLogLevels.Error : prio == 4 ? SysLogLevels.Warning : SysLogLevels.Info,
                Log = kernel ? "kernel" : "journal",
                Source = src,
                Message = Str(r, "MESSAGE") ?? "",
            };
        }
        // én mærkelig linje må aldrig stoppe resten af indsamlingen
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or ArgumentException or OverflowException) { return null; }
    }

    private static string? Unit(string? u) => u is null ? null : u.EndsWith(".service", StringComparison.Ordinal) ? u[..^8] : u;

    /// <summary>Tekst eller (ved ugyldig UTF-8) en liste af bytes, som journald skriver dem.</summary>
    private static string? Str(JsonElement r, string name)
    {
        if (!r.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.Array when v.EnumerateArray().All(x => x.ValueKind == JsonValueKind.Number) =>
                Encoding.UTF8.GetString(v.EnumerateArray().Select(x => (byte)Math.Clamp(x.GetInt32(), 0, 255)).ToArray()),
            _ => null,
        };
    }
}

/// <summary>Klassisk syslog-tekst (fx Unraid, der ikke har journald): kun linjer, der ligner fejl eller advarsler.</summary>
public static partial class SyslogText
{
    [GeneratedRegex(@"^(?<mon>[A-Z][a-z]{2})\s+(?<day>\d{1,2}) (?<time>\d\d:\d\d:\d\d) \S+ (?<src>[^:\[\s]+)(\[\d+\])?: ?(?<msg>.*)$")]
    private static partial Regex LineRx();
    [GeneratedRegex(@"Kernel panic|kernel BUG|\bOops:|Call Trace|Out of memory|Killed process|segfault|I/O error|critical medium error|\bfail(ed|ure)?\b|\berror\b|\bcrit(ical)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorRx();
    [GeneratedRegex(@"\bwarn(ing)?\b", RegexOptions.IgnoreCase)] private static partial Regex WarnRx();

    private static readonly string[] Months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    public static List<SysLogEvent> Parse(string text, DateTime sinceUtc, DateTime nowUtc, TimeZoneInfo tz)
    {
        var o = new List<SysLogEvent>();
        foreach (var line in text.Split('\n'))
            if (Line(line.TrimEnd('\r'), nowUtc, tz) is { } e && e.Time >= sinceUtc) o.Add(e);
        return o;
    }

    /// <param name="all">Også almindelige linjer (som information) – til live-visningen.</param>
    public static SysLogEvent? Line(string line, DateTime nowUtc, TimeZoneInfo tz, bool all = false)
    {
        var m = LineRx().Match(line);
        if (!m.Success) return null;
        var msg = m.Groups["msg"].Value;
        var level = ErrorRx().IsMatch(msg) ? SysLogLevels.Error : WarnRx().IsMatch(msg) ? SysLogLevels.Warning : all ? SysLogLevels.Info : null;
        if (level is null) return null;
        var mon = Array.IndexOf(Months, m.Groups["mon"].Value) + 1;
        if (mon == 0 || !TimeSpan.TryParse(m.Groups["time"].Value, CultureInfo.InvariantCulture, out var tod)) return null;
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
        var year = localNow.Year;
        DateTime local;
        try { local = new DateTime(year, mon, int.Parse(m.Groups["day"].Value), 0, 0, 0, DateTimeKind.Unspecified) + tod; }
        catch (ArgumentOutOfRangeException) { return null; }
        if (local > localNow.AddDays(1)) local = local.AddYears(-1);   // december-linjer læst i januar
        var src = m.Groups["src"].Value;
        return new SysLogEvent
        {
            Time = TimeZoneInfo.ConvertTimeToUtc(local, tz), Level = level, Log = src == "kernel" ? "kernel" : "syslog", Source = src, Message = msg,
        };
    }
}

/// <summary>macOS: <c>log show/stream --style ndjson</c> og panikrapporter fra /Library/Logs/DiagnosticReports.</summary>
public static partial class MacLog
{
    /// <summary>Kun det billige: fejl (fault) fra alt og fejl fra kernen. Almindelige "error" fra programmer er for mange til at være nyttige.</summary>
    public const string Predicate = "messageType == fault OR (messageType == error AND (processImagePath == \"/kernel\" OR senderImagePath CONTAINS \"/kernel\"))";
    public const string LivePredicate = "messageType == fault OR messageType == error";
    /// <summary>"Alt" på en Mac er tusindvis af linjer i sekundet; derfor kun fejl plus kernens almindelige linjer.</summary>
    public const string LivePredicateAll = "messageType == fault OR messageType == error OR processImagePath == \"/kernel\"";

    public static string ShowCommand(TimeSpan window) =>
        $"log show --style ndjson --no-info --no-debug --last {Math.Clamp((int)Math.Ceiling(window.TotalMinutes), 1, 120)}m --predicate '{Predicate}'";

    [GeneratedRegex(@"^(?<dt>\d{4}-\d\d-\d\d \d\d:\d\d:\d\d(\.\d+)?)\s?(?<sign>[+-])(?<hh>\d\d):?(?<mm>\d\d)$")] private static partial Regex StampRx();

    public static List<SysLogEvent> Parse(string output)
    {
        var o = new List<SysLogEvent>();
        foreach (var line in output.Split('\n'))
            if (Line(line) is { } e) o.Add(e);
        return o;
    }

    public static SysLogEvent? Line(string line)
    {
        line = line.Trim();
        if (line.Length < 2 || line[0] != '{') return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            if (Get(r, "timestamp") is not { } ts || Stamp(ts) is not { } time) return null;
            var type = Get(r, "messageType") ?? "";
            var level = type.ToLowerInvariant() switch { "fault" => SysLogLevels.Error, "error" => SysLogLevels.Warning, _ => SysLogLevels.Info };
            var proc = Get(r, "processImagePath") is { Length: > 0 } pp ? pp[(pp.LastIndexOf('/') + 1)..] : Get(r, "subsystem") ?? "ukendt";
            return new SysLogEvent
            {
                Time = time, Level = level, Log = "unified", Source = proc == "" ? "ukendt" : proc,
                Message = (Get(r, "subsystem") is { Length: > 0 } sub ? $"[{sub}] " : "") + (Get(r, "eventMessage") ?? ""),
            };
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or ArgumentException) { return null; }
    }

    public static DateTime? Stamp(string s)
    {
        var m = StampRx().Match(s.Trim());
        if (!m.Success) return null;
        var iso = $"{m.Groups["dt"].Value.Replace(' ', 'T')}{m.Groups["sign"].Value}{m.Groups["hh"].Value}:{m.Groups["mm"].Value}";
        return DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.UtcDateTime : null;
    }

    private static string? Get(JsonElement r, string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// En panikrapport: .panic (ældre Intel-Mac'er) eller .ips med bug_type 210 (kernepanik). Andre .ips (programnedbrud) springes over.
    /// </summary>
    public static SysLogEvent? Panic(string fileName, string content, DateTime modifiedUtc)
    {
        string? text = null;
        var time = modifiedUtc;
        if (fileName.EndsWith(".ips", StringComparison.OrdinalIgnoreCase))
        {
            var nl = content.IndexOf('\n');
            var head = nl > 0 ? content[..nl] : content;
            try
            {
                using var h = JsonDocument.Parse(head);
                if (Get(h.RootElement, "bug_type") is not ("210" or "110")) return null;
                if (Get(h.RootElement, "timestamp") is { } ts && Stamp(ts) is { } t) time = t;
            }
            catch (JsonException) { return null; }
            if (nl > 0)
                try
                {
                    using var body = JsonDocument.Parse(content[(nl + 1)..]);
                    text = Get(body.RootElement, "panicString");
                }
                catch (JsonException) { }
        }
        else if (fileName.EndsWith(".panic", StringComparison.OrdinalIgnoreCase))
            text = content.Split('\n').FirstOrDefault(l => l.Contains("panic(", StringComparison.Ordinal));
        else return null;
        var first = (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return new SysLogEvent
        {
            Time = time, Level = SysLogLevels.Critical, Log = "panic", Source = "kernel",
            Message = first is { Length: > 0 } ? $"Kernepanik: {first}" : $"Kernepanik ({fileName})",
        };
    }
}

/// <summary>Windows' hændelseslog: hvilke logge, forespørgslen og oversættelsen af en post (selve læsningen sker i Windows-agenten).</summary>
public static class WinEventLog
{
    /// <summary>System har Kernel-Power, WHEA-Logger, BugCheck, disk, Ntfs og storport; Windows Update har sin egen.</summary>
    public static readonly IReadOnlyList<string> Channels = ["System", "Application", "Microsoft-Windows-WindowsUpdateClient/Operational"];

    /// <summary>XPath: kritisk/fejl/advarsel (eller alt med info) efter et tidspunkt, eller efter et postnummer (live).</summary>
    public static string Query(DateTime? sinceUtc, long? afterRecord = null, bool includeInfo = false, long? upToRecord = null)
    {
        var parts = new List<string> { includeInfo ? "(Level=0 or Level=1 or Level=2 or Level=3 or Level=4)" : "(Level=1 or Level=2 or Level=3)" };
        if (sinceUtc is { } s) parts.Add($"TimeCreated[@SystemTime>='{DateTime.SpecifyKind(s, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)}']");
        if (afterRecord is { } r) parts.Add($"EventRecordID>{r}");
        if (upToRecord is { } u) parts.Add($"EventRecordID<={u}");
        return $"*[System[{string.Join(" and ", parts)}]]";
    }

    /// <summary>Kort navn på loggen (Microsoft-Windows-WindowsUpdateClient/Operational → WindowsUpdateClient).</summary>
    public static string ShortLog(string channel) =>
        channel.StartsWith("Microsoft-Windows-", StringComparison.OrdinalIgnoreCase) ? channel["Microsoft-Windows-".Length..].Split('/')[0] : channel;

    /// <summary>Level 1 = kritisk, 2 = fejl, 3 = advarsel, 0 og 4 = information.</summary>
    public static SysLogEvent Map(string? provider, int id, byte? level, DateTime? created, string channel, string? message, long? record) => new()
    {
        Time = (created ?? DateTime.UtcNow).ToUniversalTime(),
        Level = level switch { 1 => SysLogLevels.Critical, 2 => SysLogLevels.Error, 3 => SysLogLevels.Warning, _ => SysLogLevels.Info },
        Log = ShortLog(channel), Source = provider ?? "", EventId = id, RecordId = record,
        Message = message is { Length: > 0 } ? message : $"(Hændelse {id} fra {provider} – Windows har ingen beskrivelse)",
    };
}
