using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Fleetdeck.Shared;

/// <summary>En del af logpakken (fx agentens log, hændelsesloggen eller systeminfo) som ren tekst.</summary>
public sealed record LogPart(string Name, string Text);

/// <summary>En fil i logpakken, som serveren viser den (navn og størrelse udpakket).</summary>
public sealed record LogEntry(string Name, long Size);

/// <summary>
/// Logfiler fra en maskine (Beskeder → "Send logfiler" og "Hent logfiler"): tekstfiler samlet i en zip på højst 5 MB.
/// Alt, der ligner en hemmelighed (tokens, adgangskoder, nøgler, lange tilfældige strenge), skjules, før det pakkes.
/// Agentens egen opsætning (config.json med token) kommer aldrig med.
/// </summary>
public static partial class LogBundle
{
    public const int MaxBytes = 5 * 1024 * 1024;
    public const string ContentType = "application/zip";
    /// <summary>Højst så meget tekst pr. del, før den pakkes (den nyeste ende beholdes).</summary>
    public const int MaxPartChars = 6 * 1024 * 1024;
    public const string Hidden = "[skjult]";

    [GeneratedRegex(@"(?i)\b(authorization|proxy-authorization)(\s*[:=]\s*)(?:(?:bearer|basic|negotiate|ntlm)\s+)?[^\s""',;]+")]
    private static partial Regex AuthHeader();
    [GeneratedRegex(@"(?i)\bbearer\s+[A-Za-z0-9._~+/=-]{6,}")]
    private static partial Regex BearerToken();
    [GeneratedRegex(@"(?i)(""?\b(?:[a-z_]*token|api[_-]?key|apikey|client[_-]?secret|secret|password|passwd|pwd|adgangskode|kodeord|enroll[_-]?key|key|sig|signature)""?\s*[:=]\s*)(""[^""]*""|'[^']*'|[^\s,;&""']+)")]
    private static partial Regex KeyValue();
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{6,}")]
    private static partial Regex Jwt();
    [GeneratedRegex(@"(?i)\b([a-z][a-z0-9+.-]*://)[^/\s:@]+:[^/\s@]+@")]
    private static partial Regex UrlCredentials();
    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?(-----END [A-Z ]*PRIVATE KEY-----|$)")]
    private static partial Regex PrivateKey();
    [GeneratedRegex(@"\b[A-Za-z0-9+/_-]{40,}={0,2}")]
    private static partial Regex LongBlob();
    [GeneratedRegex(@"\b[A-Z0-9]{5}(-[A-Z0-9]{5}){4}\b")]
    private static partial Regex ProductKey();
    [GeneratedRegex(@"[^a-z0-9æøå._-]+")]
    private static partial Regex UnsafeName();

    /// <summary>Skjul alt, der ligner en hemmelighed. Resten af linjen bevares, så loggen stadig kan læses.</summary>
    public static string Redact(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var r = PrivateKey().Replace(s, "-----BEGIN PRIVATE KEY----- " + Hidden + " -----END PRIVATE KEY-----");
        r = AuthHeader().Replace(r, m => m.Groups[1].Value + m.Groups[2].Value + Hidden);
        r = BearerToken().Replace(r, "Bearer " + Hidden);
        r = Jwt().Replace(r, Hidden);
        r = UrlCredentials().Replace(r, m => m.Groups[1].Value + Hidden + "@");
        r = KeyValue().Replace(r, m => m.Groups[1].Value + (m.Groups[2].Value.StartsWith('"') ? $"\"{Hidden}\"" : Hidden));
        r = ProductKey().Replace(r, Hidden);
        // lange tilfældige strenge (nøgler og tokens i base64 eller hex); almindelige ord, stier og versionsnavne får lov at stå
        r = LongBlob().Replace(r, m => LooksRandom(m.Value) ? Hidden : m.Value);
        return r;
    }

    private static bool LooksRandom(string s)
    {
        if (s.Count(c => c == '/') > 2) return false;   // en sti eller adresse
        var digits = s.Any(char.IsAsciiDigit);
        if (s.All(char.IsAsciiHexDigit)) return digits;   // hex (fx en nøgle eller et fingeraftryk)
        return digits && s.Any(char.IsAsciiLetterUpper) && s.Any(char.IsAsciiLetterLower);   // base64
    }

    /// <summary>Den nyeste ende af en tekst (fra et linjeskift), med en note om, at begyndelsen er skåret væk.</summary>
    public static string Tail(string? s, int maxChars)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= maxChars) return s ?? "";
        var cut = s[^Math.Max(0, maxChars)..];
        var nl = cut.IndexOf('\n');
        if (nl >= 0 && nl < cut.Length - 1 && nl < 2000) cut = cut[(nl + 1)..];
        return "[… begyndelsen er skåret væk …]\n" + cut;
    }

    /// <summary>
    /// Den nyeste ende af en logfil (og evt. den forrige, roterede fil før den), læst uden at låse den for den, der skriver.
    /// Tom tekst, hvis filen ikke findes eller ikke kan læses.
    /// </summary>
    public static string ReadTail(string path, int maxBytes, string? previous = null)
    {
        var now = ReadEnd(path, maxBytes);
        if (previous is null || now.Length >= maxBytes) return now.Length == 0 ? "" : DecodeTail(now, cut: new FileInfo(path).Length > maxBytes);
        var before = ReadEnd(previous, maxBytes - now.Length);
        var all = before.Concat(now).ToArray();
        return all.Length == 0 ? "" : DecodeTail(all, cut: before.Length > 0 && new FileInfo(previous).Length > before.Length);
    }

    private static byte[] ReadEnd(string path, int max)
    {
        if (max <= 0) return [];
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint)) return [];
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var len = (int)Math.Min(fs.Length, max);
            fs.Seek(-len, SeekOrigin.End);
            var buf = new byte[len];
            fs.ReadExactly(buf);
            return buf;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    private static string DecodeTail(byte[] b, bool cut)
    {
        var s = Encoding.UTF8.GetString(b).TrimStart('﻿', '�');
        if (!cut) return s;
        var nl = s.IndexOf('\n');
        return "[… begyndelsen er skåret væk …]\n" + (nl >= 0 && nl < 2000 ? s[(nl + 1)..] : s);
    }

    /// <summary>Et sikkert filnavn i pakken: små bogstaver, ingen mapper, altid .txt.</summary>
    public static string SafeName(string? name)
    {
        var n = UnsafeName().Replace((name ?? "").Trim().ToLowerInvariant().Replace(' ', '-'), "");
        n = n.Trim('.', '-');
        if (n.EndsWith(".txt")) n = n[..^4];
        if (n.Length == 0) n = "del";
        return (n.Length > 60 ? n[..60] : n) + ".txt";
    }

    /// <summary>
    /// Pakken: hver del som tekstfil (hemmeligheder skjult) og LÆS-MIG.txt med, hvad der er med. Bliver den for stor,
    /// skæres de største dele til deres nyeste ende, til den passer.
    /// </summary>
    public static byte[] Build(IEnumerable<LogPart> parts, string title, DateTime atUtc, int maxBytes = MaxBytes)
    {
        var list = new List<LogPart>();
        var names = new HashSet<string>();
        foreach (var p in parts)
        {
            var name = SafeName(p.Name);
            for (var i = 2; !names.Add(name); i++) name = SafeName($"{p.Name}-{i}");
            list.Add(new LogPart(name, Redact(Tail(p.Text, MaxPartChars))));
        }
        var cut = false;
        for (var round = 0; round < 20; round++)
        {
            var zip = Zip(list, title, atUtc, cut);
            if (zip.Length <= maxBytes) return zip;
            // de største dele halveres (den nyeste ende bliver)
            var max = list.Max(p => p.Text.Length);
            if (max < 1000) break;
            list = list.Select(p => p.Text.Length >= max / 2 ? p with { Text = Tail(p.Text, p.Text.Length / 2) } : p).ToList();
            cut = true;
        }
        return Zip([new LogPart("note.txt", "Logfilerne var for store til at blive sendt.")], title, atUtc, true);
    }

    private static byte[] Zip(List<LogPart> parts, string title, DateTime atUtc, bool cut)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var readme = new StringBuilder()
                .AppendLine(Redact(title))
                .AppendLine($"Samlet {atUtc:yyyy-MM-dd HH:mm:ss} UTC af Fleetdeck.")
                .AppendLine("Hemmeligheder (tokens, adgangskoder og nøgler) er skjult som " + Hidden + ".")
                .AppendLine(cut ? "Nogle filer er skåret til deres nyeste ende for at holde pakken under 5 MB." : "")
                .AppendLine()
                .AppendLine("Indhold:");
            foreach (var p in parts) readme.AppendLine($"- {p.Name} ({p.Text.Length:N0} tegn)");
            Add(z, "LÆS-MIG.txt", readme.ToString(), atUtc);
            foreach (var p in parts) Add(z, p.Name, p.Text, atUtc);
        }
        return ms.ToArray();
    }

    private static void Add(ZipArchive z, string name, string text, DateTime atUtc)
    {
        var e = z.CreateEntry(name, CompressionLevel.SmallestSize);
        e.LastWriteTime = new DateTimeOffset(DateTime.SpecifyKind(atUtc, DateTimeKind.Utc));
        using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
        w.Write(text.Replace("\r\n", "\n").Replace("\n", "\r\n"));
    }

    /// <summary>Er det en zip, vi kan tage imod (starter med PK, ikke for stor)?</summary>
    public static bool IsZip(ReadOnlySpan<byte> b) => b.Length is >= 22 and <= MaxBytes && b[0] == (byte)'P' && b[1] == (byte)'K' && b[2] == 3 && b[3] == 4;

    /// <summary>Filerne i pakken (kun tekstfiler i roden; null, hvis det ikke er en gyldig pakke).</summary>
    public static List<LogEntry>? Entries(byte[] zip)
    {
        if (!IsZip(zip)) return null;
        try
        {
            using var z = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
            var res = new List<LogEntry>();
            foreach (var e in z.Entries)
            {
                if (e.FullName.Contains('/') || e.FullName.Contains('\\') || !e.FullName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return null;
                if (e.Length > 64L * 1024 * 1024) return null;   // pakkebombe
                res.Add(new LogEntry(e.FullName, e.Length));
            }
            return res.Count is > 0 and <= 50 ? res : null;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException) { return null; }
    }

    /// <summary>"312 kB" / "1,4 MB".</summary>
    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{Math.Max(1, bytes / 1024)} kB",
        _ => $"{bytes / 1024d / 1024d:0.0} MB".Replace('.', ','),
    };
}
