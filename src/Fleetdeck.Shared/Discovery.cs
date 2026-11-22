using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Fleetdeck.Shared;

/// <summary>
/// "Find automatisk": agenten sender en lille UDP-broadcast på det lokale net, og Fleetdeck-serveren svarer med sin adresse.
/// Virker kun på samme net (broadcast går ikke gennem routere); ellers skriver man adressen selv.
/// </summary>
public static class Discovery
{
    public const int Port = 8586;
    public const string Request = "FLEETDECK-DISCOVER/1";

    /// <param name="AccessRequests">Serveren tager imod "Anmod om adgang" (null = ældre server, der ikke siger det).</param>
    public sealed record Found(string Name, string Url, string Version, bool? AccessRequests = null);

    /// <summary>
    /// Spørger på alle netkort og returnerer de servere, der svarer inden for <paramref name="wait"/>. Svarer ingen (fx en server i
    /// Docker med bridge-net, hvor broadcasten aldrig når containeren), prøves det lokale net (/24) på TCP 8585 med GET /api/health
    /// (højst <see cref="ScanTime"/> ekstra).
    /// </summary>
    public static async Task<List<Found>> Search(TimeSpan wait, CancellationToken ct = default)
    {
        var found = await SearchUdp(wait, ct);
        if (found.Count > 0 || ct.IsCancellationRequested) return found;
        try { return await Scan(ScanTargets(LocalNetworks()), ScanTime, ct); }
        catch (Exception e) when (e is NetworkInformationException or SocketException or InvalidOperationException) { return found; }
    }

    // ---------------------------------------------------------------- reserve: led efter serveren på det lokale net

    /// <summary>Serverens web-port (som i <see cref="Normalize"/>).</summary>
    public const int HttpPort = 8585;
    public static readonly TimeSpan ScanTime = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan ProbeConnectTimeout = TimeSpan.FromMilliseconds(400);
    public const int ScanParallel = 64, ScanMaxInterfaces = 2;

    /// <summary>Et netkort med en IPv4-adresse (det, <see cref="ScanTargets"/> har brug for – så det kan testes uden netkort).</summary>
    public sealed record LocalNet(string Name, string Description, NetworkInterfaceType Type, bool Up, IPAddress Address, int PrefixLength, bool HasGateway);

    private static readonly string[] VirtualNames =
        ["docker", "br-", "veth", "virbr", "vmnet", "vboxnet", "utun", "tun", "tap", "wg", "zt", "tailscale", "awdl", "llw", "bridge", "lxc", "lxd", "cni", "flannel", "podman", "vethernet", "anpi", "ap1"];
    private static readonly string[] VirtualDescriptions = ["virtual", "vmware", "virtualbox", "hyper-v", "wsl", "tap-", "wireguard", "tailscale", "zerotier", "vpn", "pseudo", "loopback"];

    /// <summary>Er netkortet et virtuelt/VPN-kort (Docker, VM, VPN, AirDrop) – dem leder vi ikke på.</summary>
    public static bool IsVirtual(LocalNet n)
    {
        var name = n.Name.ToLowerInvariant(); var desc = n.Description.ToLowerInvariant();
        return n.Type is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp
            || VirtualNames.Any(name.StartsWith) || VirtualDescriptions.Any(desc.Contains);
    }

    /// <summary>
    /// Adresserne, der prøves: for højst <see cref="ScanMaxInterfaces"/> aktive, ikke-virtuelle netkort (dem med en gateway først)
    /// de andre adresser i /24'en omkring maskinens egen adresse (eller hele nettet, hvis det er mindre). Net på /16 eller større
    /// (og link-local 169.254.x.x) springes over – der er for mange adresser til at prøve.
    /// </summary>
    public static List<IPAddress> ScanTargets(IEnumerable<LocalNet> nets)
    {
        var picked = nets.Where(n => n.Up && n.Address.AddressFamily == AddressFamily.InterNetwork && !IsVirtual(n)
                                     && n.PrefixLength is > 16 and <= 30 && !IPAddress.IsLoopback(n.Address) && n.Address.GetAddressBytes()[0] != 169)
            .OrderByDescending(n => n.HasGateway).ThenBy(n => n.Type == NetworkInterfaceType.Wireless80211)
            .DistinctBy(n => Net24(n.Address)).Take(ScanMaxInterfaces);
        var list = new List<IPAddress>();
        var seen = new HashSet<uint>();
        foreach (var n in picked)
        {
            var me = ToUInt(n.Address);
            var prefix = Math.Max(n.PrefixLength, 24);
            var mask = prefix == 32 ? uint.MaxValue : ~((1u << (32 - prefix)) - 1);
            var net = me & mask; var bcast = net | ~mask;
            for (var a = net + 1; a < bcast; a++)
                if (a != me && seen.Add(a)) list.Add(FromUInt(a));
        }
        return list;
    }

    private static uint Net24(IPAddress a) => ToUInt(a) & 0xFFFFFF00;
    private static uint ToUInt(IPAddress a) { var b = a.GetAddressBytes(); return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]); }
    private static IPAddress FromUInt(uint v) => new([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);

    /// <summary>Maskinens netkort med IPv4-adresser.</summary>
    public static IEnumerable<LocalNet> LocalNetworks()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            IPInterfaceProperties props;
            try { props = nic.GetIPProperties(); } catch (NetworkInformationException) { continue; }
            var gw = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            foreach (var ua in props.UnicastAddresses)
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork && ua.PrefixLength > 0)
                    yield return new LocalNet(nic.Name, nic.Description, nic.NetworkInterfaceType, nic.OperationalStatus == OperationalStatus.Up, ua.Address, ua.PrefixLength, gw);
        }
    }

    /// <summary>
    /// Er svaret fra /api/health en Fleetdeck-server? Nye servere siger "app": "fleetdeck"; ældre (før 3.45) svarer præcis
    /// {"ok":true,"version":…,"release":…}. Returnerer versionen, ellers null.
    /// </summary>
    public static string? FleetdeckHealthVersion(string? body)
    {
        if (string.IsNullOrWhiteSpace(body) || body.Length > 4096) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            string? Str(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var version = Str("version");
            if (version is not { Length: > 0 }) return null;
            if (string.Equals(Str("app"), HealthApp, StringComparison.Ordinal)) return version;
            var keys = r.EnumerateObject().Select(p => p.Name).OrderBy(k => k, StringComparer.Ordinal).ToList();
            return keys.SequenceEqual(["ok", "release", "version"]) && r.GetProperty("ok").ValueKind == JsonValueKind.True && Str("release") is { Length: > 0 }
                ? version : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Markøren i serverens /api/health ("app").</summary>
    public const string HealthApp = "fleetdeck";

    /// <summary>Prøv adresserne på TCP 8585 (<see cref="ScanParallel"/> ad gangen); stopper ved første Fleetdeck-server eller efter <paramref name="budget"/>.</summary>
    public static async Task<List<Found>> Scan(IReadOnlyList<IPAddress> targets, TimeSpan budget, CancellationToken ct = default,
        Func<IPAddress, CancellationToken, Task<string?>>? probe = null)
    {
        var found = new List<Found>();
        if (targets.Count == 0) return found;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(budget);
        using var http = probe is null ? new HttpClient(new SocketsHttpHandler { ConnectTimeout = ProbeConnectTimeout, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(1.5) } : null;
        probe ??= async (ip, t) =>
        {
            using var res = await http!.GetAsync($"http://{ip}:{HttpPort}/api/health", t);
            return res.IsSuccessStatusCode ? await res.Content.ReadAsStringAsync(t) : null;
        };
        try
        {
            await Parallel.ForEachAsync(targets, new ParallelOptions { MaxDegreeOfParallelism = ScanParallel, CancellationToken = cts.Token }, async (ip, t) =>
            {
                string? body;
                try { body = await probe(ip, t); }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException or SocketException or IOException) { return; }
                if (FleetdeckHealthVersion(body) is not { } v) return;
                lock (found) found.Add(new Found("Fleetdeck", $"http://{ip}:{HttpPort}", v));
                cts.Cancel();   // én server er nok – brugeren kan stadig skrive en anden adresse
            });
        }
        catch (OperationCanceledException) { }
        lock (found) return [.. found.DistinctBy(f => f.Url)];
    }

    private static async Task<List<Found>> SearchUdp(TimeSpan wait, CancellationToken ct)
    {
        var found = new Dictionary<string, Found>(StringComparer.OrdinalIgnoreCase);
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var msg = Encoding.UTF8.GetBytes(Request);
        foreach (var target in Targets())
        {
            try { await udp.SendAsync(msg, msg.Length, new IPEndPoint(target, Port)); }
            catch (SocketException) { }
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(wait);
        try
        {
            while (true)
            {
                var r = await udp.ReceiveAsync(cts.Token);
                try
                {
                    var f = JsonSerializer.Deserialize<Found>(r.Buffer, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    if (f is { Url.Length: > 0 }) found[f.Url] = f;
                }
                catch (JsonException) { }
            }
        }
        catch (OperationCanceledException) { }
        return [.. found.Values];
    }

    /// <summary>255.255.255.255 plus hvert IPv4-nets egen broadcastadresse (nogle routere/Wi-Fi videresender kun den).</summary>
    private static IEnumerable<IPAddress> Targets()
    {
        yield return IPAddress.Broadcast;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask is null) continue;
                var ip = ua.Address.GetAddressBytes(); var mask = ua.IPv4Mask.GetAddressBytes();
                var b = new byte[4];
                for (var i = 0; i < 4; i++) b[i] = (byte)(ip[i] | ~mask[i]);
                yield return new IPAddress(b);
            }
        }
    }

    /// <summary>Gør "192.168.1.10" til "http://192.168.1.10:8585". Hele adresser (med http/https) bruges som de er.</summary>
    public static string Normalize(string input)
    {
        var s = input.Trim().TrimEnd('/');
        if (s.Length == 0) return s;
        if (!s.Contains("://")) s = "http://" + s;
        if (Uri.TryCreate(s, UriKind.Absolute, out var u) && u.IsDefaultPort && u.Scheme == "http" && !input.Contains(":80"))
            s = $"{u.Scheme}://{u.Host}:8585{u.AbsolutePath.TrimEnd('/')}";
        return s.TrimEnd('/');
    }
}
