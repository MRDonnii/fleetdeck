using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Fleetdeck.Server.Data;
using Fleetdeck.Shared;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Server.Services.Printers;

/// <summary>En printer fundet på netværket (endnu ikke nødvendigvis tilføjet).</summary>
public sealed class FoundPrinter
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public string Uri { get; set; } = "";
    /// <summary>bonjour | scan | agent (den bedste kilde)</summary>
    public string Via { get; set; } = "";
    /// <summary>Maskinen, der så den (Via = agent).</summary>
    public string? Machine { get; set; }
    /// <summary>Alle kilder: bonjour, scan, wsd (fra en pc), installed (installeret på en pc).</summary>
    public List<string> Sources { get; set; } = [];
    /// <summary>Pc'erne, der har set printeren.</summary>
    public List<string> Machines { get; set; } = [];
    public string? MakeModel { get; set; }
    public bool? Driverless { get; set; }
    public bool? Color { get; set; }
    public bool? Duplex { get; set; }
    public string? Location { get; set; }
    public DateTime Seen { get; set; } = DateTime.UtcNow;
}

/// <summary>Ren logik til fund: adresser fra Bonjour-svar, netværksområder og sammenfletning.</summary>
public static class PrinterFinds
{
    private static FoundPrinter From(SeenPrinter s, string via, string? machine = null, DateTime? seen = null) => new()
    {
        Name = s.Name, Host = s.Host, Uri = s.Uri, Via = via, Machine = machine, Sources = [s.Via], Machines = machine is null ? [] : [machine],
        MakeModel = s.MakeModel, Driverless = s.Driverless, Color = s.Color, Duplex = s.Duplex, Location = s.Location, Seen = seen ?? DateTime.UtcNow,
    };

    /// <summary>Adressen ud fra tjenesten (se PrinterSeen.UriFor).</summary>
    public static string? UriFor(Mdns.Service s) => PrinterSeen.UriFor(s);

    /// <summary>Én post pr. printer (IP), den bedste tjeneste først (ipp før ipps før rå 9100 før lpd).</summary>
    public static List<FoundPrinter> FromMdns(IEnumerable<Mdns.Service> services) => PrinterSeen.FromMdns(services).Select(s => From(s, "bonjour")).ToList();

    /// <summary>Værter i et IPv4-område ("192.0.2.0/24"). Højst /22 (1022 værter), så en scanning ikke tager evigheder.</summary>
    public static List<IPAddress>? Hosts(string? cidr)
    {
        var s = (cidr ?? "").Trim();
        var parts = s.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != AddressFamily.InterNetwork
            || !int.TryParse(parts[1], out var bits) || bits is < 22 or > 32) return null;
        var b = ip.GetAddressBytes();
        var v = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        var mask = bits == 0 ? 0u : uint.MaxValue << (32 - bits);
        var net = v & mask;
        var size = 1u << (32 - bits);
        var list = new List<IPAddress>();
        for (uint i = 0; i < size; i++)
        {
            if (size > 2 && (i == 0 || i == size - 1)) continue;   // net- og broadcastadresse
            var x = net + i;
            list.Add(new IPAddress([(byte)(x >> 24), (byte)(x >> 16), (byte)(x >> 8), (byte)x]));
        }
        return list;
    }

    /// <summary>Private IPv4-adresser (10/8, 172.16/12, 192.168/16) – kun dem scanner Fleetdeck af sig selv.</summary>
    public static bool IsPrivate(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }

    /// <summary>Er hele området privat (både første og sidste adresse)?</summary>
    public static bool IsPrivateRange(string? cidr) => Hosts(cidr) is { Count: > 0 } h && IsPrivate(h[0]) && IsPrivate(h[^1]);

    private static readonly string[] VirtualIfaces = ["lo", "docker", "br-", "veth", "virbr", "tailscale", "zt", "wg", "tun", "tap", "cni", "flannel", "kube"];

    /// <summary>
    /// Serverens egne net som /24 (fx 192.0.2.17 → 192.0.2.0/24): kun private adresser, ikke Docker-broer, VPN og lignende. Højst 4.
    /// Er nettet større end /24, scannes kun den /24, serveren selv står i (så en scanning er lille og hurtig).
    /// </summary>
    public static List<string> OwnSubnets(IEnumerable<(string Iface, IPAddress Address)> addresses) =>
        addresses.Where(a => IsPrivate(a.Address) && !VirtualIfaces.Any(v => a.Iface.StartsWith(v, StringComparison.OrdinalIgnoreCase)))
            .Select(a => { var b = a.Address.GetAddressBytes(); return $"{b[0]}.{b[1]}.{b[2]}.0/24"; })
            .Distinct().Take(4).ToList();

    /// <summary>Serverens egne adresser (op, ikke loopback).</summary>
    public static IEnumerable<(string, IPAddress)> LocalAddresses()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            IPInterfaceProperties props;
            try { props = ni.GetIPProperties(); } catch (NetworkInformationException) { continue; }
            foreach (var u in props.UnicastAddresses)
                if (u.Address.AddressFamily == AddressFamily.InterNetwork) yield return (ni.Name, u.Address);
        }
    }

    /// <summary>Hvor god adressen er: Bonjour fra serveren, Bonjour fra en pc, scanning, installeret på en pc, WSD (gættet sti).</summary>
    public static int Score(FoundPrinter f) => f.Via switch
    {
        "bonjour" => 0,
        "scan" => 2,
        _ => f.Sources.Contains("bonjour") ? 1 : f.Sources.Contains("installed") ? 3 : 4,
    };

    /// <summary>Flet fund: én pr. vært (samme IP/adresse); den bedste adresse vinder, og kilder, pc'er og model samles.</summary>
    public static List<FoundPrinter> Merge(params IEnumerable<FoundPrinter>[] sources)
    {
        return sources.SelectMany(x => x).Where(f => f.Host.Length > 0)
            .GroupBy(f => (PrinterUri.Host(f.Uri) ?? f.Host).ToLowerInvariant())
            .Select(g =>
            {
                var best = g.OrderBy(Score).ThenByDescending(f => f.Seen).First();
                var res = new FoundPrinter
                {
                    Name = best.Name, Host = best.Host, Uri = best.Uri, Via = best.Via, Machine = best.Machine ?? g.Select(f => f.Machine).FirstOrDefault(m => m is not null),
                    Sources = g.SelectMany(f => f.Sources.Count > 0 ? f.Sources : [f.Via]).Distinct().OrderBy(SourceOrder).ToList(),
                    Machines = g.SelectMany(f => f.Machines.Count > 0 ? f.Machines : f.Machine is { } m ? [m] : []).Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(m => m, StringComparer.CurrentCultureIgnoreCase).ToList(),
                    MakeModel = best.MakeModel ?? g.Select(f => f.MakeModel).FirstOrDefault(m => m is not null),
                    Driverless = best.Driverless ?? g.OrderBy(Score).Select(f => f.Driverless).FirstOrDefault(m => m is not null),
                    Color = best.Color ?? g.Select(f => f.Color).FirstOrDefault(m => m is not null),
                    Duplex = best.Duplex ?? g.Select(f => f.Duplex).FirstOrDefault(m => m is not null),
                    Location = best.Location ?? g.Select(f => f.Location).FirstOrDefault(m => m is not null),
                    Seen = g.Max(f => f.Seen),
                };
                // et IP som navn (scanning/WSD) erstattes af et rigtigt navn fra en anden kilde
                if (IPAddress.TryParse(res.Name, out _) && g.Select(f => f.Name).FirstOrDefault(n => !IPAddress.TryParse(n, out _)) is { Length: > 0 } nice) res.Name = nice;
                return res;
            })
            .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static int SourceOrder(string s) => s switch { "bonjour" => 0, "scan" => 1, "wsd" => 2, "installed" => 3, _ => 4 };

    /// <summary>Er fundet allerede tilføjet? Samme vært som en printer i Fleetdeck.</summary>
    public static bool Known(FoundPrinter f, IEnumerable<string> printerUris) =>
        printerUris.Any(u => string.Equals(PrinterUri.Host(u), f.Host, StringComparison.OrdinalIgnoreCase));

    /// <summary>Netværksprintere, en agent kender: dem den har installeret (ipp/ipps/socket/lpd med en vært) og dem den har fundet (Bonjour/WSD).</summary>
    public static IEnumerable<FoundPrinter> FromAgent(string machine, IEnumerable<LocalPrinter> printers, IEnumerable<SeenPrinter>? seen = null, DateTime? at = null)
    {
        var installed = printers.Where(p => !p.Virtual && !p.Fleetdeck && PrinterUri.Normalize(p.Uri) is { } u && !u.StartsWith("http", StringComparison.Ordinal) && PrinterUri.Host(u) is { Length: > 0 } h && h != "localhost")
            .Select(p => new FoundPrinter
            {
                Name = p.Name, Host = PrinterUri.Host(p.Uri)!, Uri = PrinterUri.Normalize(p.Uri)!, Via = "agent", Machine = machine, Sources = ["installed"], Machines = [machine],
                MakeModel = p.Driver, Seen = at ?? DateTime.UtcNow,
            });
        var found = (seen ?? []).Take(PrinterSeen.Max)
            .Where(s => PrinterUri.Normalize(s.Uri) is { } u && PrinterUri.Host(u) is { Length: > 0 } h && h != "localhost" && IPAddress.TryParse(h, out var ip) && IsPrivate(ip))
            .Select(s =>
            {
                var f = From(s, "agent", machine, at);
                f.Uri = PrinterUri.Normalize(s.Uri)!; f.Host = PrinterUri.Host(f.Uri)!;
                f.Name = Cut(PrinterNames.Clean(f.Name), 100) is { Length: > 0 } n ? n : f.Host;
                f.MakeModel = f.MakeModel is null ? null : Cut(PrinterNames.Clean(f.MakeModel), 200);
                f.Location = f.Location is null ? null : Cut(PrinterNames.Clean(f.Location), 100);
                return f;
            });
        return installed.Concat(found);
    }

    private static string Cut(string s, int n) => s.Length > n ? s[..n] : s;

    /// <summary>Skal den automatiske søgning køre nu? Aldrig søgt, eller sidste søgning er ældre end intervallet.</summary>
    public static bool Due(DateTime? lastRun, double hours, DateTime now) => lastRun is null || now - lastRun.Value >= TimeSpan.FromHours(Math.Clamp(hours, 1, 168));
}

/// <summary>Hvad en søgning gjorde (til statuslinjen på siden).</summary>
public sealed class PrinterSearchSummary
{
    public int Bonjour { get; set; }
    public int Scan { get; set; }
    public int Agents { get; set; }
    public List<string> Subnets { get; set; } = [];
    /// <summary>auto | manual | first</summary>
    public string Reason { get; set; } = "";
    public bool NetworkOff { get; set; }
}

/// <summary>
/// Finder printere fra serveren: Bonjour (mDNS), en scanning af serverens eget net på 631 (IPP) og evt. et område, brugeren selv har
/// skrevet (631/9100), plus de printere, agenterne melder. Fundne IPP-printere spørges om model og egenskaber.
/// Søger selv ved første åbning af siden og i baggrunden (hver 6. time, kan slås fra). Seneste fund gemmes, så de overlever en genstart.
/// </summary>
public sealed class PrinterDiscovery(ILogger<PrinterDiscovery> log, IServiceScopeFactory scopes)
{
    public const string FoundSetting = "printer_found", SearchAtSetting = "printer_search_at", AutoSetting = "printer_auto_search",
        HoursSetting = "printer_search_hours", SubnetSetting = "printer_scan_subnet";

    /// <summary>PRINTER_DISCOVERY=off: ingen Bonjour og ingen scanning fra serveren (fx en testserver); agenternes fund vises stadig.</summary>
    public static bool NetworkOff => string.Equals(Environment.GetEnvironmentVariable("PRINTER_DISCOVERY"), "off", StringComparison.OrdinalIgnoreCase);

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        // printere har næsten altid selvsignerede certifikater; forbindelsen bruges kun til at læse status
        SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        ConnectTimeout = TimeSpan.FromSeconds(4),
    }) { Timeout = TimeSpan.FromSeconds(8) };

    private readonly SemaphoreSlim _one = new(1, 1);
    public List<FoundPrinter> Last { get; private set; } = [];
    public DateTime? LastRun { get; private set; }
    public PrinterSearchSummary? Summary { get; private set; }
    public string? LastError { get; private set; }
    /// <summary>Sidst en søgning blev startet (også en, der mislykkedes).</summary>
    public DateTime? LastAttempt { get; private set; }
    public bool Running => _one.CurrentCount == 0;

    private sealed class Saved
    {
        public List<FoundPrinter> Found { get; set; } = [];
        public DateTime? At { get; set; }
        public PrinterSearchSummary? Summary { get; set; }
    }

    /// <summary>Seneste fund fra databasen (ved opstart).</summary>
    public void Restore(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || LastRun is not null) return;
        try
        {
            var s = System.Text.Json.JsonSerializer.Deserialize<Saved>(json, Json.Web);
            if (s is null) return;
            Last = s.Found; LastRun = s.At; Summary = s.Summary;
        }
        catch (System.Text.Json.JsonException) { }
    }

    /// <summary>Spørg printeren med IPP Get-Printer-Attributes. Kaster ikke: fejl står i PrinterInfo.Error (State = off).</summary>
    public static async Task<PrinterInfo> Probe(string uri, CancellationToken ct)
    {
        if (PrinterUri.Normalize(uri) is not { } n || !PrinterUri.IsIpp(n) || PrinterUri.ToHttp(n) is not { } http)
            return new PrinterInfo { Error = "status kan kun læses fra ipp:// og ipps://", At = DateTime.UtcNow };
        try
        {
            using var body = new ByteArrayContent(Ipp.GetPrinterAttributes(n, Random.Shared.Next(1, int.MaxValue)));
            body.Headers.ContentType = new MediaTypeHeaderValue("application/ipp");
            using var res = await Http.PostAsync(http, body, ct);
            if (!res.IsSuccessStatusCode) return new PrinterInfo { Error = $"printeren svarede {(int)res.StatusCode}", At = DateTime.UtcNow };
            var bytes = await res.Content.ReadAsByteArrayAsync(ct);
            var r = Ipp.Parse(bytes);
            if (!r.Ok) return new PrinterInfo { Error = $"IPP-fejl 0x{r.Status:X4}", At = DateTime.UtcNow };
            return PrinterInfoReader.From(r);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or FormatException or IOException)
        {
            if (ct.IsCancellationRequested) throw;
            return new PrinterInfo { Error = e is TaskCanceledException ? "svarer ikke" : e.Message, At = DateTime.UtcNow };
        }
    }

    /// <summary>Scanning: åbne porte (631 = IPP, 9100 = rå). Få forbindelser ad gangen og kort ventetid, så nettet ikke belastes.</summary>
    public static async Task<List<FoundPrinter>> Scan(IReadOnlyList<IPAddress> hosts, bool raw, int parallel, CancellationToken ct)
    {
        var found = new System.Collections.Concurrent.ConcurrentBag<FoundPrinter>();
        await Parallel.ForEachAsync(hosts, new ParallelOptions { MaxDegreeOfParallelism = parallel, CancellationToken = ct }, async (ip, c) =>
        {
            var ipp = await Open(ip, 631, c);
            var r9100 = raw && !ipp && await Open(ip, 9100, c);
            if (!ipp && !r9100) return;
            found.Add(new FoundPrinter
            {
                Name = ip.ToString(), Host = ip.ToString(), Via = "scan", Sources = ["scan"],
                Uri = ipp ? $"ipp://{ip}/ipp/print" : $"socket://{ip}:9100", Driverless = r9100 ? false : null,
            });
        });
        return [.. found];
    }

    private static async Task<bool> Open(IPAddress ip, int port, CancellationToken ct)
    {
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
        t.CancelAfter(TimeSpan.FromMilliseconds(600));
        try { await s.ConnectAsync(ip, port, t.Token); return true; }
        catch (Exception e) when (e is SocketException or OperationCanceledException) { ct.ThrowIfCancellationRequested(); return false; }
    }

    /// <summary>De printere, agenterne melder (installeret på pc'en eller fundet med Bonjour/WSD).</summary>
    public static async Task<List<FoundPrinter>> AgentFinds(AppDb db, CancellationToken ct = default)
    {
        var rows = await db.Machines.AsNoTracking().Where(m => m.HealthJson != null).Select(m => new { m.Name, m.DisplayName, m.HealthJson, m.LastHealth }).ToListAsync(ct);
        return rows.SelectMany(m =>
        {
            var h = Json.Des<HealthReport>(m.HealthJson);
            return PrinterFinds.FromAgent(m.DisplayName ?? m.Name, h.LocalPrinters ?? [], h.NetPrinters, h.NetPrintersAt ?? m.LastHealth);
        }).ToList();
    }

    /// <summary>
    /// Én søgning: beder agenterne søge (ved næste check-in), Bonjour og scanning fra serveren, flet med agenternes fund og spørg
    /// IPP-printerne. Returnerer null, hvis en søgning allerede kører.
    /// </summary>
    public async Task<List<FoundPrinter>?> Search(string reason, CancellationToken ct)
    {
        if (!await _one.WaitAsync(0, ct)) return null;
        LastAttempt = DateTime.UtcNow;
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            var s = scope.ServiceProvider.GetRequiredService<SettingsService>();
            // agenterne søger selv, når de ser en ny værdi (de ser netværket direkte, også når containeren ikke kan se multicast)
            await s.Set(SearchAtSetting, DateTime.UtcNow.ToString("O"));
            var manual = (await s.Get(SubnetSetting)).Trim();
            var summary = new PrinterSearchSummary { Reason = reason, NetworkOff = NetworkOff };
            List<FoundPrinter> bonjour = [], scan = [];
            if (!NetworkOff)
            {
                bonjour = PrinterFinds.FromMdns(await PrinterScout.BrowseMdns(TimeSpan.FromSeconds(3), m => log.LogWarning("Printere: {Msg}", m), ct));
                var own = PrinterFinds.OwnSubnets(PrinterFinds.LocalAddresses());
                summary.Subnets = [.. own];
                foreach (var net in own)
                    if (PrinterFinds.Hosts(net) is { } hosts) scan.AddRange(await Scan(hosts, raw: false, parallel: 8, ct));
                if (manual.Length > 0 && !own.Contains(manual) && PrinterFinds.IsPrivateRange(manual) && PrinterFinds.Hosts(manual) is { } mh)
                {
                    summary.Subnets.Add(manual);
                    scan.AddRange(await Scan(mh, raw: true, parallel: 16, ct));
                }
            }
            var agents = await AgentFinds(db, ct);
            var all = PrinterFinds.Merge(bonjour, scan, agents);
            if (!NetworkOff)
                await Parallel.ForEachAsync(all.Where(f => PrinterUri.IsIpp(f.Uri)), new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct }, async (f, c) =>
                {
                    var info = await Probe(f.Uri, c);
                    if (info.Error is not null) return;
                    f.MakeModel = info.MakeModel ?? f.MakeModel;
                    f.Driverless = info.Driverless ?? f.Driverless;
                    f.Color = info.Color ?? f.Color;
                    f.Duplex = info.Duplex ?? f.Duplex;
                    f.Location ??= info.Location;
                    if (IPAddress.TryParse(f.Name, out _) && (info.Info ?? info.MakeModel) is { Length: > 0 } nm) f.Name = PrinterNames.Clean(nm);
                });
            summary.Bonjour = bonjour.Count; summary.Scan = scan.Count; summary.Agents = agents.Select(a => a.Machine).Distinct().Count();
            Last = all; LastRun = DateTime.UtcNow; Summary = summary; LastError = null;
            await s.Set(FoundSetting, System.Text.Json.JsonSerializer.Serialize(new Saved { Found = all, At = LastRun, Summary = summary }, Json.Web));
            log.LogInformation("Printere: {N} fundet ({B} via Bonjour, {S} via scanning, fra {A} pc'er)", all.Count, bonjour.Count, scan.Count, summary.Agents);
            await scope.ServiceProvider.GetRequiredService<Live>().Changed("printers");
            return all;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LastError = e.Message;
            log.LogWarning("Printere: søgningen mislykkedes ({Msg})", e.Message);
            return [];
        }
        finally { _one.Release(); }
    }

    /// <summary>Start en søgning i baggrunden (fx ved første åbning af siden). False, hvis en allerede kører.</summary>
    public bool Kick(string reason)
    {
        if (Running) return false;
        using (ExecutionContext.SuppressFlow())
            _ = Task.Run(async () =>
            {
                try { await Search(reason, CancellationToken.None); }
                catch (Exception e) { log.LogWarning("Printere: {Msg}", e.Message); }
            });
        return true;
    }
}

/// <summary>Automatisk søgning efter printere: kort efter start (hvis den ikke er kørt før) og derefter hver 6. time. Kan slås fra.</summary>
public sealed class PrinterAutoSearch(IServiceScopeFactory scopes, PrinterDiscovery disc, ILogger<PrinterAutoSearch> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try
        {
            using var scope = scopes.CreateScope();
            disc.Restore(await scope.ServiceProvider.GetRequiredService<SettingsService>().Get(PrinterDiscovery.FoundSetting));
        }
        catch (Exception e) { log.LogWarning("Printere: seneste fund kunne ikke læses ({Msg})", e.Message); }
        try { await Task.Delay(TimeSpan.FromSeconds(90), stop); } catch (OperationCanceledException) { return; }
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var s = scope.ServiceProvider.GetRequiredService<SettingsService>();
                if (await s.Get(PrinterDiscovery.AutoSetting) != "0" && PrinterFinds.Due(disc.LastRun, await s.GetNum(PrinterDiscovery.HoursSetting, 6), DateTime.UtcNow))
                    await disc.Search("auto", stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception e) { log.LogWarning("Printere: automatisk søgning mislykkedes ({Msg})", e.Message); }
            try { await Task.Delay(TimeSpan.FromMinutes(10), stop); } catch (OperationCanceledException) { return; }
        }
    }
}
