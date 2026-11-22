using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Fleetdeck.Server.Services.Bios;

/// <summary>Én BIOS i Lenovos katalog (catalogv2.xml): version, billedpræfiks, dato, SHA-256 for .exe'en og adressen.</summary>
public sealed record LenovoCatalogBios(string Version, string Image, DateOnly? Date, string? Sha256, string Url)
{
    public LenovoBiosVersion Parsed => LenovoBiosVersion.FromCatalog(Version);
    /// <summary>Filnavnet uden .exe, fx "m1ujy79usa" – nøglen til pakke-XML og readme.</summary>
    public string Stem => Path.GetFileNameWithoutExtension(new Uri(Url).AbsolutePath).ToLowerInvariant();
}

/// <summary>En model i catalogv2.xml: navn ("ThinkCentre M720q"), maskintyper (MTM, 4 tegn) og BIOS'en.</summary>
public sealed record LenovoModel(string Name, List<string> Types, LenovoCatalogBios? Bios);

/// <summary>En linje i BIOS'ens readme ("CHANGES for M1UKT79A / WU Version 1.0.0.121").</summary>
public sealed record BiosChange(string Version, string? Wu, List<BiosChangeLine> Lines);
public sealed record BiosChangeLine(string Text, bool Important);

/// <summary>Lenovos pakke-XML (fx m1ujy79usa_2_.xml): hvilke BIOS-niveauer pakken må opgradere fra, filerne og alvoren.</summary>
public sealed record LenovoPackage(string Version, DateOnly? ReleaseDate, List<string> DependsOn, List<string> DetectInstall, int? Severity,
    LenovoPackageFile? Installer, LenovoPackageFile? Readme)
{
    /// <summary>Lenovos "Severity type=1" er kritisk (2 = anbefalet, 3 = valgfri).</summary>
    public bool Critical => Severity == 1;
}
public sealed record LenovoPackageFile(string Name, string? Sha256, long? Size);

/// <summary>Parsere til Lenovos filer. Ingen netværk her – testene bruger små, opdigtede eksempler i Fixtures/bios.</summary>
public static partial class LenovoCatalog
{
    public const string CatalogUrl = "https://download.lenovo.com/cdrt/td/catalogv2.xml";
    public static string ModelCatalogUrl(string mtm, string os = "Win10") => $"https://download.lenovo.com/catalog/{mtm.ToUpperInvariant()}_{os}.xml";

    private static XDocument Load(Stream s) => XDocument.Load(XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }));
    private static XDocument Load(string s) => Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(s.TrimStart('﻿', ' ', '\r', '\n', '\t'))));

    public static List<LenovoModel> ParseCatalog(string xml) => ParseCatalog(Load(xml));
    public static List<LenovoModel> ParseCatalog(Stream s) => ParseCatalog(Load(s));

    private static List<LenovoModel> ParseCatalog(XDocument doc) => doc.Root?.Elements("Model").Select(m =>
    {
        var types = m.Element("Types")?.Elements("Type").Select(t => t.Value.Trim().ToUpperInvariant()).Where(t => t.Length == 4).Distinct().ToList() ?? [];
        var b = m.Element("BIOS");
        LenovoCatalogBios? bios = null;
        if (b is not null && Uri.TryCreate(b.Value.Trim(), UriKind.Absolute, out var url) && (string?)b.Attribute("version") is { Length: > 0 } ver
            && (string?)b.Attribute("image") is { Length: > 0 } image)
            bios = new(ver.Trim(), image.Trim().ToLowerInvariant(), Date((string?)b.Attribute("date")), Sha((string?)b.Attribute("crc")), url.ToString());
        return new LenovoModel(((string?)m.Attribute("name") ?? "").Trim(), types, bios);
    }).ToList() ?? [];

    /// <summary>Modelkataloget (&lt;MTM&gt;_Win10.xml): adressen og SHA-256 for pakke-XML'en til en bestemt BIOS (filnavnets stamme).</summary>
    public static (string Url, string? Sha256)? FindPackage(string modelCatalogXml, string stem)
    {
        var doc = Load(modelCatalogXml);
        foreach (var p in doc.Root?.Elements("package") ?? [])
        {
            var loc = p.Element("location")?.Value.Trim();
            if (loc is null || !Uri.TryCreate(loc, UriKind.Absolute, out var u)) continue;
            var file = Path.GetFileName(u.AbsolutePath).ToLowerInvariant();
            // m1ujy79usa.xml eller m1ujy79usa_2_.xml (revision)
            if (file == stem + ".xml" || (file.StartsWith(stem + "_", StringComparison.Ordinal) && file.EndsWith(".xml", StringComparison.Ordinal)))
                return (u.ToString(), Sha(p.Element("checksum")?.Value));
        }
        return null;
    }

    public static LenovoPackage ParsePackage(string xml)
    {
        var root = Load(xml).Root ?? throw new FormatException("Tom pakke-XML");
        List<string> Levels(XElement? e) => e?.Descendants("_Bios").Elements("Level").Select(l => l.Value.Trim()).Where(l => l.Length > 0).ToList() ?? [];
        LenovoPackageFile? File(XElement? f) => f?.Element("Name")?.Value.Trim() is { Length: > 0 } n
            ? new(n, Sha(f.Element("CRC")?.Value), long.TryParse(f.Element("Size")?.Value, out var sz) ? sz : null) : null;
        var files = root.Element("Files");
        return new LenovoPackage(
            ((string?)root.Attribute("version") ?? "").Trim(),
            Date(root.Element("ReleaseDate")?.Value),
            Levels(root.Element("Dependencies")),
            Levels(root.Element("DetectInstall")),
            int.TryParse((string?)root.Element("Severity")?.Attribute("type"), out var sev) ? sev : null,
            File(files?.Element("Installer")?.Element("File")),
            File(files?.Element("Readme")?.Elements("File").FirstOrDefault()));
    }

    /// <summary>"CHANGES for M1UKT79A / WU Version 1.0.0.121" efterfulgt af "- …"-linjer, indtil næste overskrift.</summary>
    public static List<BiosChange> ParseReadme(string text)
    {
        var list = new List<BiosChange>();
        BiosChange? cur = null;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (ChangesHead().Match(line) is { Success: true } h)
            {
                cur = new BiosChange(h.Groups[1].Value.ToUpperInvariant(), h.Groups[2].Success ? h.Groups[2].Value : null, []);
                list.Add(cur);
                continue;
            }
            if (cur is null) continue;
            if (line.StartsWith('-') || line.StartsWith('*') || line.StartsWith('•'))
            {
                var t = line.TrimStart('-', '*', '•', ' ').Trim();
                if (t.Length > 0) cur.Lines.Add(new(t.Length > 300 ? t[..300] + " …" : t, Important(t)));
            }
            else if (line.Length > 0 && cur.Lines.Count > 0 && raw.StartsWith("  ", StringComparison.Ordinal) && !line.Contains("CHANGES", StringComparison.Ordinal))
            {
                var last = cur.Lines[^1];   // fortsættelse af forrige punkt
                var t = (last.Text + " " + line).Trim();
                cur.Lines[^1] = new(t.Length > 300 ? t[..300] + " …" : t, Important(t));
            }
            else if (line.Length > 0 && !line.StartsWith("CHANGES", StringComparison.OrdinalIgnoreCase)) cur = null;   // et nyt afsnit
        }
        return list;
    }

    /// <summary>Linjer om Secure Boot, CA/KEK-certifikater, TPM eller sikkerhed vises som "vigtig".</summary>
    public static bool Important(string line) => ImportantRx().IsMatch(line);

    public static string? Sha(string? s)
    {
        s = s?.Trim();
        return s is { Length: 64 } && s.All(Uri.IsHexDigit) ? s.ToLowerInvariant() : null;
    }

    public static DateOnly? Date(string? s) =>
        DateOnly.TryParseExact(s?.Trim(), ["yyyy-MM-dd", "yyyy/MM/dd", "MM/dd/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    /// <summary>Lenovos USB-pakke står ikke i kataloget. Navnet afledes af .exe'en: femte tegn y → t og .zip (m1ujy79usa.exe → m1ujt79usa.zip).
    /// Kun ThinkCentre-pakker (fem tegn med "y" på femtepladsen) har en USB-pakke.</summary>
    public static string? UsbZipUrl(string exeUrl)
    {
        if (!Uri.TryCreate(exeUrl, UriKind.Absolute, out var u)) return null;
        var file = Path.GetFileName(u.AbsolutePath);
        if (file.Length < 6 || char.ToLowerInvariant(file[4]) != 'y' || !file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        var zip = file[..4] + (char.IsUpper(file[4]) ? 'T' : 't') + file[5..^4] + ".zip";
        return new Uri(u, zip).ToString();
    }

    [GeneratedRegex(@"^CHANGES\s+for\s+([A-Z0-9]{8})(?:\s*/\s*WU\s+Version\s+([0-9.]+))?", RegexOptions.IgnoreCase)]
    private static partial Regex ChangesHead();
    [GeneratedRegex(@"secure\s*boot|\bUEFI CA\b|\bKEK\b|\bCA 20\d\d\b|\bdbx?\b|\bTPM\b|\bsecurity\b|\bvulnerab|\bCVE-|sikkerhed", RegexOptions.IgnoreCase)]
    private static partial Regex ImportantRx();
}
