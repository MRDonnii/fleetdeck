using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Fleetdeck.Shared;

// Find printere på netværket: Bonjour (mDNS) og WSD (WS-Discovery). Bruges af serveren og af agenterne, som ser netværket
// direkte (også når serverens container ikke kan se multicast). Alt her kaster ikke ved netværksfejl; ren logik kan testes.

/// <summary>En netværksprinter, en agent (eller serveren) har set. Sendes med helbredet (HealthReport.NetPrinters).</summary>
public sealed class SeenPrinter
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    /// <summary>ipp://, ipps://, socket:// eller lpd://.</summary>
    public string Uri { get; set; } = "";
    /// <summary>bonjour | wsd</summary>
    public string Via { get; set; } = "";
    public string? MakeModel { get; set; }
    public bool? Driverless { get; set; }
    public bool? Color { get; set; }
    public bool? Duplex { get; set; }
    public string? Location { get; set; }
}

/// <summary>Fund fra Bonjour-svar og WSD-svar som printere (én pr. vært).</summary>
public static class PrinterSeen
{
    /// <summary>Højst så mange printere i en rapport (så en ond eller stor pakke ikke fylder helbredet).</summary>
    public const int Max = 100;

    private static int Rank(string type) => type.ToLowerInvariant() switch
    {
        "_ipp._tcp.local" => 0, "_ipps._tcp.local" => 1, "_pdl-datastream._tcp.local" => 2, _ => 3,
    };

    /// <summary>Adressen ud fra tjenesten: ipp(s)://ip:port/rp, socket://ip:port eller lpd://ip/kø. Null hvis ingen IPv4-adresse.</summary>
    public static string? UriFor(Mdns.Service s)
    {
        var ip = s.Addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString();
        if (ip is null) return null;
        var rp = (s.Txt.GetValueOrDefault("rp") ?? "").Trim('/');
        return s.Type.ToLowerInvariant() switch
        {
            "_ipp._tcp.local" => PrinterUri.Normalize($"ipp://{ip}:{s.Port}/{(rp.Length > 0 ? rp : "ipp/print")}"),
            "_ipps._tcp.local" => PrinterUri.Normalize($"ipps://{ip}:{s.Port}/{(rp.Length > 0 ? rp : "ipp/print")}"),
            "_pdl-datastream._tcp.local" => PrinterUri.Normalize($"socket://{ip}:{(s.Port > 0 ? s.Port : 9100)}"),
            "_printer._tcp.local" => PrinterUri.Normalize($"lpd://{ip}/{(rp.Length > 0 ? rp : "queue")}"),
            _ => null,
        };
    }

    /// <summary>Én post pr. printer (IP), den bedste tjeneste først (ipp før ipps før rå 9100 før lpd).</summary>
    public static List<SeenPrinter> FromMdns(IEnumerable<Mdns.Service> services)
    {
        var list = new List<SeenPrinter>();
        foreach (var g in services.Select(s => (S: s, Uri: UriFor(s))).Where(x => x.Uri is not null)
                     .GroupBy(x => PrinterUri.Host(x.Uri)!).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var best = g.OrderBy(x => Rank(x.S.Type)).First();
            var txt = g.SelectMany(x => x.S.Txt).GroupBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.First().Value, StringComparer.OrdinalIgnoreCase);
            var pdl = txt.GetValueOrDefault("pdl") ?? "";
            list.Add(new SeenPrinter
            {
                Name = PrinterNames.Clean(best.S.Label) is { Length: > 0 } l ? l : g.Key,
                Host = g.Key, Uri = best.Uri!, Via = "bonjour",
                MakeModel = txt.GetValueOrDefault("ty") is { Length: > 0 } ty ? ty : txt.GetValueOrDefault("product")?.Trim('(', ')'),
                // AirPrint (URF) eller PWG-raster/PDF over IPP: virker uden driver
                Driverless = g.Any(x => Rank(x.S.Type) <= 1) ? txt.ContainsKey("URF") || pdl.Contains("image/pwg-raster") || pdl.Contains("image/urf") || pdl.Contains("application/pdf") : false,
                Color = txt.TryGetValue("Color", out var c) ? c.Equals("T", StringComparison.OrdinalIgnoreCase) : null,
                Duplex = txt.TryGetValue("Duplex", out var d) ? d.Equals("T", StringComparison.OrdinalIgnoreCase) : null,
                Location = txt.GetValueOrDefault("note") is { Length: > 0 } n ? n : null,
            });
        }
        return list;
    }

    /// <summary>WSD-printere: adressen gættes som ipp://ip/ipp/print (de fleste WSD-printere kan også IPP; serveren spørger dem bagefter).</summary>
    public static List<SeenPrinter> FromWsd(IEnumerable<IPAddress> hosts) =>
        hosts.Where(h => h.AddressFamily == AddressFamily.InterNetwork).Select(h => h.ToString()).Distinct().OrderBy(h => h, StringComparer.Ordinal)
            .Select(h => new SeenPrinter { Name = h, Host = h, Uri = $"ipp://{h}/ipp/print", Via = "wsd" }).ToList();

    /// <summary>Én pr. vært: Bonjour før WSD (Bonjour kender navn, model og den rigtige sti).</summary>
    public static List<SeenPrinter> Combine(params IEnumerable<SeenPrinter>[] sources) =>
        sources.SelectMany(x => x).Where(p => p.Host.Length > 0)
            .GroupBy(p => p.Host.ToLowerInvariant())
            .Select(g => g.OrderBy(p => p.Via == "bonjour" ? 0 : 1).First())
            .OrderBy(p => p.Host, StringComparer.Ordinal).Take(Max).ToList();
}

/// <summary>Uddata fra "avahi-browse -rpt" (Linux) som Bonjour-tjenester.</summary>
public static partial class AvahiBrowse
{
    /// <summary>Kommandoen til printertjenesterne (hver afsluttes af -t, når cachen er læst; timeout som sikkerhed).</summary>
    public const string Command = "for t in _ipp._tcp _ipps._tcp _pdl-datastream._tcp; do timeout 8 avahi-browse -rpt \"$t\" 2>/dev/null; done; true";

    [GeneratedRegex("\"((?:[^\"\\\\]|\\\\.)*)\"")] private static partial Regex Quoted();

    /// <summary>"\032" (decimal, én byte – også UTF-8-bytes som "\195\166" for æ) og "\." i avahis navne.</summary>
    public static string Unescape(string s)
    {
        var bytes = new List<byte>();
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 3 < s.Length && char.IsAsciiDigit(s[i + 1]) && char.IsAsciiDigit(s[i + 2]) && char.IsAsciiDigit(s[i + 3])
                && int.Parse(s.AsSpan(i + 1, 3)) is var v and <= 255)
            { bytes.Add((byte)v); i += 3; }
            else if (s[i] == '\\' && i + 1 < s.Length) { bytes.AddRange(Encoding.UTF8.GetBytes(s[i + 1].ToString())); i++; }
            else bytes.AddRange(Encoding.UTF8.GetBytes(s[i].ToString()));
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    /// <summary>Linjer, der starter med "=" (løst op): =;eth0;IPv4;navn;_ipp._tcp;local;vært.local;192.0.2.44;631;"ty=…" "rp=…"</summary>
    public static List<Mdns.Service> Parse(string output)
    {
        var list = new List<Mdns.Service>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!line.StartsWith("=;", StringComparison.Ordinal)) continue;
            var f = line.Split(';', 10);
            if (f.Length < 9 || f[2] != "IPv4" || !IPAddress.TryParse(f[7], out var ip) || ip.AddressFamily != AddressFamily.InterNetwork
                || !int.TryParse(f[8], out var port) || port is < 0 or > 65535) continue;
            var type = $"{f[4]}.{f[5]}";
            var txt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (f.Length > 9)
                foreach (Match m in Quoted().Matches(f[9]))
                {
                    var kv = m.Groups[1].Value.Replace("\\\"", "\"");
                    var eq = kv.IndexOf('=');
                    if (eq > 0) txt.TryAdd(kv[..eq], kv[(eq + 1)..]); else if (kv.Length > 0) txt.TryAdd(kv, "");
                }
            list.Add(new Mdns.Service($"{Unescape(f[3])}.{type}", type, f[6], port, txt, [ip]));
        }
        return list.DistinctBy(s => (s.Instance.ToLowerInvariant(), s.Addresses[0].ToString())).ToList();
    }
}

/// <summary>WS-Discovery (WSD): Windows' egen måde at finde printere på. Probe efter printere og læs svarenes adresser.</summary>
public static class Wsd
{
    public static readonly IPEndPoint Group = new(IPAddress.Parse("239.255.255.250"), 3702);

    public static byte[] Probe(Guid id) => Encoding.UTF8.GetBytes(
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
        "<soap:Envelope xmlns:soap=\"http://www.w3.org/2003/05/soap-envelope\" xmlns:wsa=\"http://schemas.xmlsoap.org/ws/2004/08/addressing\" " +
        "xmlns:wsd=\"http://schemas.xmlsoap.org/ws/2005/04/discovery\" xmlns:wprt=\"http://schemas.microsoft.com/windows/2006/08/wdp/print\">" +
        "<soap:Header><wsa:To>urn:schemas-xmlsoap-org:ws:2005:04:discovery</wsa:To>" +
        "<wsa:Action>http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</wsa:Action>" +
        $"<wsa:MessageID>urn:uuid:{id}</wsa:MessageID></soap:Header>" +
        "<soap:Body><wsd:Probe><wsd:Types>wprt:PrintDeviceType</wsd:Types></wsd:Probe></soap:Body></soap:Envelope>");

    /// <summary>IPv4-værterne i et ProbeMatches-svar (fra XAddrs), kun for printere. Tom ved et ødelagt svar.</summary>
    public static List<IPAddress> Parse(byte[] packet)
    {
        if (packet.Length is 0 or > 64 * 1024) return [];
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64 * 1024 };
            using var r = XmlReader.Create(new MemoryStream(packet), settings);
            var doc = XDocument.Load(r);
            var res = new List<IPAddress>();
            foreach (var m in doc.Descendants().Where(e => e.Name.LocalName == "ProbeMatch"))
            {
                var types = m.Elements().FirstOrDefault(e => e.Name.LocalName == "Types")?.Value ?? "";
                if (types.Length > 0 && !types.Contains("PrintDeviceType", StringComparison.OrdinalIgnoreCase) && !types.Contains("print", StringComparison.OrdinalIgnoreCase)) continue;
                var xaddrs = m.Elements().FirstOrDefault(e => e.Name.LocalName == "XAddrs")?.Value ?? "";
                foreach (var a in xaddrs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    if (System.Uri.TryCreate(a, UriKind.Absolute, out var u) && u.HostNameType == UriHostNameType.IPv4 && IPAddress.TryParse(u.Host, out var ip)) res.Add(ip);
            }
            return res.Distinct().ToList();
        }
        catch (XmlException) { return []; }
    }
}

/// <summary>Søgningen på netværket: Bonjour og WSD samtidig i et par sekunder.</summary>
public static class PrinterScout
{
    /// <summary>Bonjour: spørg efter printertjenesterne og saml svar (fra en tilfældig port, så printerne svarer direkte tilbage).</summary>
    public static async Task<List<Mdns.Service>> BrowseMdns(TimeSpan wait, Action<string>? warn, CancellationToken ct)
    {
        var records = new List<Mdns.Record>();
        try
        {
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            udp.MulticastLoopback = false;
            var q = Mdns.Query(Mdns.PrinterServices);
            var group = new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353);
            await udp.SendAsync(q, group, ct);
            await Collect(udp, wait, q, group, b => { try { records.AddRange(Mdns.Parse(b)); } catch (FormatException) { } }, ct);
        }
        catch (SocketException e) { warn?.Invoke($"Bonjour kunne ikke bruges ({e.Message})"); }
        return Mdns.Resolve(records);
    }

    /// <summary>WSD: send en Probe efter printere og saml værterne, der svarer.</summary>
    public static async Task<List<IPAddress>> BrowseWsd(TimeSpan wait, Action<string>? warn, CancellationToken ct)
    {
        var hosts = new List<IPAddress>();
        try
        {
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            udp.MulticastLoopback = false;
            var probe = Wsd.Probe(Guid.NewGuid());
            await udp.SendAsync(probe, Wsd.Group, ct);
            await Collect(udp, wait, probe, Wsd.Group, b => hosts.AddRange(Wsd.Parse(b)), ct);
        }
        catch (SocketException e) { warn?.Invoke($"WSD kunne ikke bruges ({e.Message})"); }
        return hosts.Distinct().ToList();
    }

    private static async Task Collect(UdpClient udp, TimeSpan wait, byte[] q, IPEndPoint group, Action<byte[]> take, CancellationToken ct)
    {
        using var until = CancellationTokenSource.CreateLinkedTokenSource(ct);
        until.CancelAfter(wait);
        var resent = false;
        var n = 0;
        while (!until.IsCancellationRequested && n++ < 500)
        {
            UdpReceiveResult r;
            try { r = await udp.ReceiveAsync(until.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
            take(r.Buffer);
            // de fleste printere svarer på første forespørgsel; én gang til fanger dem, der var i dvale
            if (!resent) { resent = true; await udp.SendAsync(q, group, ct); }
        }
    }

    /// <summary>Find printere med Bonjour og WSD (og evt. ekstra Bonjour-svar, fx fra avahi-browse).</summary>
    public static async Task<List<SeenPrinter>> Find(TimeSpan wait, IEnumerable<Mdns.Service>? extra, Action<string>? warn, CancellationToken ct)
    {
        var mdns = BrowseMdns(wait, warn, ct);
        var wsd = BrowseWsd(wait, warn, ct);
        await Task.WhenAll(mdns, wsd);
        return PrinterSeen.Combine(PrinterSeen.FromMdns((await mdns).Concat(extra ?? [])), PrinterSeen.FromWsd(await wsd));
    }
}
