using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Fleetdeck.Server.Services;

/// <summary>
/// Kommer et kald fra hjemmenettet/VPN? Ud over private adresser regnes to slags kald fra samme net med (ejerens valg 2026-11-22:
/// "så længe den kommer fra en IP-adresse på samme netværk"):
/// 1) IPv6 på hjemmenettet: en global IPv6-adresse inde i et af serverens egne on-link-præfikser (/48–/127).
/// 2) Gennem Cloudflare-tunnelen hjemmefra: TCP-modparten er privat (cloudflared på hjemmenettet/i containeren), og
///    CF-Connecting-IP er serverens egen offentlige IPv4 (samme router/NAT) eller ligger i serverens eget IPv6-præfiks.
///    Cloudflare-headere fra en offentlig modpart stoles der aldrig på.
/// Den rene beslutning er <see cref="IsHome"/>; netkortene og den offentlige IP slås op i <see cref="OwnNetwork"/>.
/// </summary>
public static class HomeNetwork
{
    /// <summary>Nøglen i HttpContext.Items: TCP-modparten, før X-Forwarded-For fra betroede proxyer er læst (sat i Program.cs).</summary>
    public const string PeerKey = "fd.tcp-peer";

    public static readonly string[] CloudflareHeaders = ["CF-Connecting-IP", "CF-Ray", "CF-Connecting-IPv6", "CDN-Loop"];

    /// <summary>Det, serveren ved om sit eget net: globale IPv6-præfikser og den offentlige IPv4 (null = ukendt).</summary>
    public sealed record Facts(IReadOnlyList<IPNetwork> OwnV6Prefixes, IPAddress? PublicV4)
    {
        public static readonly Facts None = new([], null);
    }

    /// <summary>Indholdet af et kald, som beslutningen bygger på.</summary>
    /// <param name="Peer">TCP-modparten (null = lokal unix-socket).</param>
    /// <param name="Client">Klientens IP, efter X-Forwarded-For fra betroede proxyer (= Peer uden proxy).</param>
    /// <param name="ViaCloudflare">Et af Cloudflares headere er sat.</param>
    /// <param name="CfConnectingIp">CF-Connecting-IP, som den står.</param>
    /// <param name="MdmPublic">Kaldet kom ind på den offentlige MDM-adresse.</param>
    public sealed record Call(IPAddress? Peer, IPAddress? Client, bool ViaCloudflare, string? CfConnectingIp, bool MdmPublic);

    public static Call Read(HttpContext ctx)
    {
        var h = ctx.Request.Headers;
        var client = ctx.Connection.RemoteIpAddress;
        var peer = ctx.Items.TryGetValue(PeerKey, out var p) ? p as IPAddress : client;
        return new Call(peer, client, CloudflareHeaders.Any(h.ContainsKey), h["CF-Connecting-IP"].ToString(), MdmPublicHost.IsPublic(ctx.Request));
    }

    /// <summary>
    /// Beslutningen. <paramref name="trustTunnel"/>: må et kald gennem Cloudflare-tunnelen regnes hjemme, når det kommer fra serverens
    /// egen offentlige adresse? (Nej for nøgler "kun fra hjemmenettet": bag CGNAT deler flere kunder samme offentlige IP.)
    /// </summary>
    public static bool IsHome(Call c, Facts f, bool trustTunnel = true)
    {
        // den offentlige MDM-adresse: aldrig hjemme (Fleetdeck svarer kun på /enroll/… dér)
        if (c.MdmPublic) return false;
        if (!c.ViaCloudflare) return TerminalSecurity.IsPrivate(c.Client) || InOwnV6(c.Client, f);
        if (!trustTunnel) return false;
        // gennem tunnelen: kun når cloudflared selv står på hjemmenettet/i containeren
        var peer = Map(c.Peer);
        if (!TerminalSecurity.IsPrivate(peer)) return false;
        if (!IPAddress.TryParse(c.CfConnectingIp?.Trim(), out var cf)) return false;
        cf = Map(cf)!;
        // betroede proxyer har læst X-Forwarded-For: så skal den klient være den samme som Cloudflares (ellers er headeren sat af
        // en anden end Cloudflare, fx en klient udefra gennem en proxy på hjemmenettet)
        var client = Map(c.Client);
        if (client is not null && !client.Equals(peer) && !client.Equals(cf)) return false;
        if (cf.AddressFamily == AddressFamily.InterNetwork) return f.PublicV4 is { } pub && Map(pub)!.Equals(cf);
        return InOwnV6(cf, f);
    }

    /// <summary>Er adressen en IPv6-adresse i et af serverens egne præfikser?</summary>
    public static bool InOwnV6(IPAddress? ip, Facts f)
    {
        ip = Map(ip);
        return ip is { AddressFamily: AddressFamily.InterNetworkV6 } && f.OwnV6Prefixes.Any(n => n.Contains(ip));
    }

    /// <summary>
    /// Serverens globale IPv6-præfikser ud fra dens egne adresser og præfikslængder: kun globale adresser (ikke link-local, ULA,
    /// loopback eller IPv4-mappede – de er private i forvejen), og kun /48–/127 (/128 er én adresse, kortere er for bredt).
    /// </summary>
    public static List<IPNetwork> GlobalV6Prefixes(IEnumerable<(IPAddress Address, int PrefixLength)> addresses) =>
        addresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6 && !a.Address.IsIPv4MappedToIPv6
                && a.PrefixLength is >= 48 and <= 127 && !TerminalSecurity.IsPrivate(a.Address) && !IPAddress.IPv6Any.Equals(a.Address)
                && !a.Address.IsIPv6Multicast)
            .Select(a => new IPNetwork(Mask(a.Address, a.PrefixLength), a.PrefixLength))
            .Distinct().ToList();

    /// <summary>"ip=…" fra Cloudflares /cdn-cgi/trace (kun en offentlig IPv4), ellers null.</summary>
    public static IPAddress? ParseTrace(string? body)
    {
        foreach (var line in (body ?? "").Split('\n'))
            if (line.Trim() is var l && l.StartsWith("ip=", StringComparison.Ordinal) && IPAddress.TryParse(l[3..], out var ip)
                && ip.AddressFamily == AddressFamily.InterNetwork && !TerminalSecurity.IsPrivate(ip))
                return ip;
        return null;
    }

    private static IPAddress? Map(IPAddress? ip) => ip is { IsIPv4MappedToIPv6: true } ? ip.MapToIPv4() : ip;

    private static IPAddress Mask(IPAddress ip, int bits)
    {
        var b = ip.GetAddressBytes();
        for (var i = 0; i < b.Length; i++)
        {
            var keep = Math.Clamp(bits - i * 8, 0, 8);
            b[i] &= (byte)(0xFF << (8 - keep));
        }
        return new IPAddress(b);
    }
}

/// <summary>Serverens egne net: IPv6-præfikserne fra netkortene (gemmes 1 minut) og den offentlige IPv4 (gemmes 10 minutter).</summary>
public static class OwnNetwork
{
    public const string HttpClientName = "public-ip";
    /// <summary>IPv4-adressen tvinger svaret til at være serverens offentlige IPv4 (ikke IPv6).</summary>
    public const string TraceUrl = "https://1.1.1.1/cdn-cgi/trace";

    private static readonly TimeSpan PrefixesFor = TimeSpan.FromMinutes(1), PublicFor = TimeSpan.FromMinutes(10), FailedFor = TimeSpan.FromMinutes(1);
    private static volatile Tuple<DateTime, IReadOnlyList<IPNetwork>>? prefixes;
    private static volatile Tuple<DateTime, IPAddress?>? publicV4;
    private static readonly SemaphoreSlim gate = new(1, 1);

    public static IReadOnlyList<IPNetwork> V6Prefixes()
    {
        var now = DateTime.UtcNow;
        if (prefixes is { } p && now - p.Item1 < PrefixesFor) return p.Item2;
        IReadOnlyList<IPNetwork> list;
        try
        {
            list = HomeNetwork.GlobalV6Prefixes(NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(u => (u.Address, u.PrefixLength)));
        }
        catch (Exception e) when (e is NetworkInformationException or PlatformNotSupportedException or InvalidOperationException) { list = []; }
        prefixes = Tuple.Create(now, list);
        return list;
    }

    /// <summary>Serverens offentlige IPv4 (null, hvis opslaget fejler – så gælder reglen om tunnelen ikke).</summary>
    public static async Task<IPAddress?> PublicV4(IHttpClientFactory? http, CancellationToken ct = default)
    {
        if (http is null) return null;
        if (publicV4 is { } c && DateTime.UtcNow - c.Item1 < (c.Item2 is null ? FailedFor : PublicFor)) return c.Item2;
        await gate.WaitAsync(ct);
        try
        {
            if (publicV4 is { } again && DateTime.UtcNow - again.Item1 < (again.Item2 is null ? FailedFor : PublicFor)) return again.Item2;
            IPAddress? ip = null;
            try { ip = HomeNetwork.ParseTrace(await http.CreateClient(HttpClientName).GetStringAsync(TraceUrl, ct)); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { }
            publicV4 = Tuple.Create(DateTime.UtcNow, ip);
            return ip;
        }
        finally { gate.Release(); }
    }

    /// <summary>Det, <see cref="HomeNetwork.IsHome"/> skal bruge for dette kald – den offentlige IP slås kun op, når kaldet kom gennem tunnelen.</summary>
    public static async Task<HomeNetwork.Facts> For(HttpContext ctx, HomeNetwork.Call c, bool trustTunnel)
    {
        var v6 = V6Prefixes();
        IPAddress? pub = null;
        if (trustTunnel && c.ViaCloudflare && !c.MdmPublic && TerminalSecurity.IsPrivate(c.Peer)
            && IPAddress.TryParse(c.CfConnectingIp?.Trim(), out var cf) && (cf.IsIPv4MappedToIPv6 ? cf.MapToIPv4() : cf).AddressFamily == AddressFamily.InterNetwork)
            pub = await PublicV4(ctx.RequestServices?.GetService<IHttpClientFactory>(), ctx.RequestAborted);
        return new(v6, pub);
    }
}
