using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Fleetdeck.Shared;

/// <summary>Maskinens netværksadaptere (netkort) og deres status. En del af helbredet; indeholder aldrig adgangskoder.</summary>
public sealed class NetworkInfo
{
    /// <summary>Fysiske adaptere og Wi-Fi (primær først) samt op til <see cref="NetParse.MaxVirtual"/> virtuelle.</summary>
    public List<NetAdapter> Adapters { get; set; } = [];
    /// <summary>Virtuelle adaptere i alt (loopback, Hyper-V, VPN, docker, veth …), også dem der ikke er sendt med.</summary>
    public int VirtualCount { get; set; }
    /// <summary>Bærbar maskine (batteri/bærbart kabinet). Null = ukendt – så advares der ikke om svagt Wi-Fi.</summary>
    public bool? Portable { get; set; }
    /// <summary>Wi-Fi-detaljerne kunne ikke læses (fx Windows uden placeringstilladelse til netsh).</summary>
    public string? WifiNote { get; set; }
    /// <summary>windows | linux | mac.</summary>
    public string? Source { get; set; }
    public string? Error { get; set; }
}

public sealed class NetAdapter
{
    /// <summary>Navnet i styresystemet (fx "Ethernet", "Wi-Fi", "eth0", "en0").</summary>
    public string Name { get; set; } = "";
    /// <summary>Hardwarens navn (fx "Intel(R) Ethernet Connection I219-V") eller Mac'ens porte (fx "Wi-Fi").</summary>
    public string? Description { get; set; }
    /// <summary><see cref="NetKinds"/>.</summary>
    public string Kind { get; set; } = NetKinds.Other;
    public bool Virtual { get; set; }
    /// <summary><see cref="NetStates"/>.</summary>
    public string Status { get; set; } = NetStates.Unknown;
    /// <summary>Har standard-gatewayen (den forbindelse, maskinen bruger til internettet).</summary>
    public bool Primary { get; set; }
    /// <summary>Forhandlet hastighed i Mbit/s (kun når forbindelsen er oppe).</summary>
    public long? SpeedMbps { get; set; }
    /// <summary>Det, kortet kan (Mbit/s), hvis det kan læses (ethtool, ifconfig -m eller en kendt chip). Null = ukendt.</summary>
    public long? MaxSpeedMbps { get; set; }
    public bool? FullDuplex { get; set; }
    public string? Mac { get; set; }
    public int? Mtu { get; set; }
    public int? VlanId { get; set; }
    public List<string> Ipv4 { get; set; } = [];
    public List<string> Ipv6 { get; set; } = [];
    /// <summary>IPv6 slået til på adapteren. Null = ukendt.</summary>
    public bool? Ipv6Enabled { get; set; }
    public List<string> Gateways { get; set; } = [];
    public List<string> Dns { get; set; } = [];
    public string? DnsSuffix { get; set; }
    public bool? Dhcp { get; set; }
    public WifiLink? Wifi { get; set; }
    /// <summary>Trafik i alt (siden opstart eller siden driveren startede), bytes.</summary>
    public long? RxBytes { get; set; }
    public long? TxBytes { get; set; }
    /// <summary>Trafik nu: gennemsnit siden forrige måling (bytes/s).</summary>
    public double? RxRate { get; set; }
    public double? TxRate { get; set; }
    /// <summary>Fejl og tabte pakker i alt.</summary>
    public long? RxErrors { get; set; }
    public long? TxErrors { get; set; }
    public long? RxDrops { get; set; }
    public long? TxDrops { get; set; }
    /// <summary>Nye fejl (rx+tx) og pakker siden forrige måling (<see cref="SampleSeconds"/> sekunder).</summary>
    public long? NewErrors { get; set; }
    public long? NewPackets { get; set; }
    public int? SampleSeconds { get; set; }
    /// <summary>Gange linket er gået op eller ned de sidste 24 timer (det, agenten har set). Null = ikke målt endnu.</summary>
    public int? LinkChanges24h { get; set; }
    /// <summary>Sidst agenten så linket gå op eller ned (UTC).</summary>
    public DateTime? LinkChangedAt { get; set; }
}

public sealed class WifiLink
{
    public string? Ssid { get; set; }
    /// <summary>Access point'ets MAC-adresse.</summary>
    public string? Bssid { get; set; }
    public int? SignalPct { get; set; }
    public int? Dbm { get; set; }
    /// <summary>"2,4 GHz", "5 GHz" eller "6 GHz".</summary>
    public string? Band { get; set; }
    public int? Channel { get; set; }
    public int? FrequencyMhz { get; set; }
    public double? RxMbps { get; set; }
    public double? TxMbps { get; set; }
    /// <summary>Fx "802.11ax" eller "Wi-Fi 6".</summary>
    public string? Radio { get; set; }
    /// <summary>Fx "WPA2-Personal", "WPA3-Personal", "Åben".</summary>
    public string? Security { get; set; }
}

public static class NetKinds
{
    public const string Ethernet = "ethernet", Wifi = "wifi", Mobile = "mobile", Other = "other";
}

public static class NetStates
{
    /// <summary>Forbundet med en IP-adresse.</summary>
    public const string Connected = "connected";
    /// <summary>Linket er oppe, men der er ingen IP-adresse (fx DHCP svarer ikke).</summary>
    public const string Limited = "limited";
    /// <summary>Kablet adapter uden kabel/link.</summary>
    public const string Unplugged = "unplugged";
    /// <summary>Wi-Fi/mobil uden forbindelse.</summary>
    public const string Disconnected = "disconnected";
    /// <summary>Slået fra i styresystemet.</summary>
    public const string Disabled = "disabled";
    public const string Unknown = "unknown";
}

/// <summary>Rå oplysninger om én adapter fra .NET (NetworkInterface) plus det, platformen kan tilføje (WMI, sysfs, ifconfig).</summary>
public sealed record NicSample
{
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    /// <summary>Windows: interface-GUID (NetworkInterface.Id). Linux/Mac: navnet.</summary>
    public string? Id { get; init; }
    public NetworkInterfaceType? Type { get; init; }
    public OperationalStatus? Oper { get; init; }
    public long? SpeedBps { get; init; }
    public string? Mac { get; init; }
    public int? Mtu { get; init; }
    public List<string> Ipv4 { get; init; } = [];
    public List<string> Ipv6 { get; init; } = [];
    public bool? Ipv6Enabled { get; init; }
    public List<string> Gateways { get; init; } = [];
    public List<string> Dns { get; init; } = [];
    public string? DnsSuffix { get; init; }
    public bool? Dhcp { get; init; }
    public NetCounters? Counters { get; init; }
    // det platformen ved ud over .NET
    /// <summary>False = slået fra (administrativt nede).</summary>
    public bool? AdminUp { get; init; }
    /// <summary>Link/kabel (Windows MediaConnectState, Linux carrier).</summary>
    public bool? Carrier { get; init; }
    public bool? Wireless { get; init; }
    public bool? Mobile { get; init; }
    /// <summary>True = rigtig hardware (WMI HardwareInterface, Linux /sys/class/net/X/device). False = virtuel.</summary>
    public bool? Hardware { get; init; }
    public bool? FullDuplex { get; init; }
    public long? MaxSpeedMbps { get; init; }
    public int? VlanId { get; init; }
}

/// <summary>Tællere fra styresystemet (bytes, pakker, fejl, tabte pakker og Linux' carrier_changes).</summary>
public sealed record NetCounters(long? RxBytes, long? TxBytes, long? RxPackets, long? TxPackets, long? RxErrors, long? TxErrors, long? RxDrops, long? TxDrops,
    long? CarrierChanges = null);

/// <summary>Én adapter fra "netsh wlan show interfaces".</summary>
public sealed record NetshWlan(string Name, string? Description, string? Guid, string? Mac, bool? Connected, WifiLink Link);

/// <summary>Ét interface fra macOS' ifconfig.</summary>
public sealed record MacIf(string Name, bool Up, bool? Active, long? SpeedMbps, bool? FullDuplex, long? MaxSpeedMbps, int? Mtu, int? VlanId);

public static partial class NetParse
{
    public const int MaxVirtual = 12, MaxPhysical = 24, MaxAddresses = 4;

    // ------------------------------------------------------------------ .NET → NicSample

    /// <summary>Det .NET kan læse om adapteren. Alt, platformen ikke understøtter, bliver null – kaster aldrig.</summary>
    public static NicSample Snapshot(NetworkInterface n)
    {
        IPInterfaceProperties? p = Try(n.GetIPProperties);
        var uni = p is null ? [] : Try(() => p.UnicastAddresses.Select(a => a.Address).ToList()) ?? [];
        var v4 = p is null ? null : Try(p.GetIPv4Properties);
        var v6 = p is null ? null : Try(p.GetIPv6Properties);
        var st = Try(n.GetIPStatistics);
        return new NicSample
        {
            Name = n.Name, Description = Try(() => n.Description), Id = Try(() => n.Id), Type = Try(() => (NetworkInterfaceType?)n.NetworkInterfaceType),
            Oper = Try(() => (OperationalStatus?)n.OperationalStatus), SpeedBps = Try(() => (long?)n.Speed),
            Mac = Try(() => FormatMac(n.GetPhysicalAddress().GetAddressBytes())),
            Mtu = v4 is null ? null : Try(() => (int?)v4.Mtu) is int m and > 0 ? m : null,
            Ipv4 = uni.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).ToList(),
            Ipv6 = uni.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6).Select(a => a.ToString()).ToList(),
            Ipv6Enabled = Try(() => (bool?)n.Supports(NetworkInterfaceComponent.IPv6)),
            Gateways = p is null ? [] : Try(() => p.GatewayAddresses.Select(g => g.Address).Where(a => !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.IPv6Any)).Select(a => a.ToString()).ToList()) ?? [],
            Dns = p is null ? [] : Try(() => p.DnsAddresses.Select(a => a.ToString()).ToList()) ?? [],
            DnsSuffix = p is null ? null : Try(() => p.DnsSuffix) is { Length: > 0 } sfx ? sfx : null,
            Dhcp = v4 is null ? null : Try(() => (bool?)v4.IsDhcpEnabled),
            Counters = st is null ? null : new NetCounters(Try(() => (long?)st.BytesReceived), Try(() => (long?)st.BytesSent),
                Try(() => (long?)(st.UnicastPacketsReceived + st.NonUnicastPacketsReceived)), Try(() => (long?)(st.UnicastPacketsSent + st.NonUnicastPacketsSent)),
                Try(() => (long?)st.IncomingPacketsWithErrors), Try(() => (long?)st.OutgoingPacketsWithErrors),
                Try(() => (long?)st.IncomingPacketsDiscarded), Try(() => (long?)st.OutgoingPacketsDiscarded)),
        };
    }

    private static T? Try<T>(Func<T> f)
    {
        try { return f(); }
        catch (Exception e) when (e is NetworkInformationException or PlatformNotSupportedException or NotImplementedException or InvalidOperationException
                                       or IOException or UnauthorizedAccessException or FormatException or OverflowException or ArgumentException) { return default; }
    }

    public static string? FormatMac(byte[]? b) => b is { Length: 6 } && b.Any(x => x != 0) ? string.Join('-', b.Select(x => x.ToString("X2", CultureInfo.InvariantCulture))) : null;

    /// <summary>"aa:bb:cc:dd:ee:ff" / "AA-BB-…" / "aabbccddeeff" → "AA-BB-CC-DD-EE-FF".</summary>
    public static string? NormMac(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var hex = Regex.Replace(s, "[^0-9A-Fa-f]", "");
        return hex.Length == 12 && hex.Any(c => c != '0') ? string.Join('-', Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2).ToUpperInvariant())) : null;
    }

    // ------------------------------------------------------------------ klassificering

    [GeneratedRegex(@"^(lo\d*|veth|docker|br-|virbr|vnet|vmnet|vboxnet|tun|tap|wg|utun|awdl|llw|gif|stf|anpi|bridge|ap\d|zt|tailscale|ts\d|lxc|lxd|cni|flannel|cali|kube|vxlan|genev|ipsec|ppp|isatap|teredo|nordlynx|proton|cscotun|ham)",
        RegexOptions.IgnoreCase)]
    private static partial Regex VirtualName();

    [GeneratedRegex(@"Hyper-V Virtual|vEthernet|VirtualBox|VMware Virtual|VMnet|Loopback|Pseudo-Interface|Teredo|ISATAP|6to4|IP-HTTPS|WAN Miniport|Kernel Debug|Microsoft Wi-?Fi Direct Virtual|Hosted Network Virtual|TAP-Windows|TAP-NordVPN|Wintun|WireGuard|OpenVPN|Tailscale|ZeroTier|Npcap|Cisco AnyConnect|Fortinet|SonicWall|Juniper|Check Point|Bluetooth|Microsoft KM-TEST|Filter|Scheduler|Container|Docker|Windows Subsystem|WSL",
        RegexOptions.IgnoreCase)]
    private static partial Regex VirtualDescription();

    [GeneratedRegex(@"WWAN|Mobile Broadband|Mobilt bredbånd|Cellular|\bLTE\b|\b5G\b|\b4G\b|\b3G\b|Snapdragon X\d+|Sierra Wireless|Quectel|Fibocom|Telit|Huawei Mobile|iPhone|Android|RNDIS|Remote NDIS|^wwan|^wwp|^usb\d",
        RegexOptions.IgnoreCase)]
    private static partial Regex MobileText();

    /// <summary>Virtuel adapter (loopback, Hyper-V, VPN, docker, veth …) ud fra navn og beskrivelse – bruges, når platformen ikke selv ved det.</summary>
    public static bool IsVirtualName(string name, string? description) =>
        VirtualName().IsMatch(name) || (description is { Length: > 0 } d && VirtualDescription().IsMatch(d)) || VirtualDescription().IsMatch(name);

    public static string KindOf(NicSample s)
    {
        var text = $"{s.Name} {s.Description}";
        if (s.Mobile == true || s.Type is NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 || MobileText().IsMatch(s.Name) || (s.Description is { } d && MobileText().IsMatch(d)))
            return NetKinds.Mobile;
        if (s.Wireless == true || s.Type == NetworkInterfaceType.Wireless80211 || Regex.IsMatch(text, @"\b(Wi-?Fi|WLAN|Wireless|802\.11)\b|^wl", RegexOptions.IgnoreCase))
            return NetKinds.Wifi;
        if (s.Type is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx
            or NetworkInterfaceType.Ethernet3Megabit) return NetKinds.Ethernet;
        return NetKinds.Other;
    }

    /// <summary>Skal adapteren skjules som virtuel? Platformens eget svar (WMI/sysfs) vinder over navnet.</summary>
    public static bool IsVirtual(NicSample s)
    {
        if (s.Type is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) return true;
        if (s.Hardware is bool hw) return !hw;
        if (s.Type is NetworkInterfaceType.Ppp) return true;
        return IsVirtualName(s.Name, s.Description);
    }

    private static bool UsableV4(string ip) => !ip.StartsWith("169.254.", StringComparison.Ordinal) && !ip.StartsWith("127.", StringComparison.Ordinal) && ip != "0.0.0.0";
    private static bool GlobalV6(string ip) => !ip.StartsWith("fe80", StringComparison.OrdinalIgnoreCase) && ip != "::1";

    /// <summary>Status ud fra slået til/fra, link og adresser.</summary>
    public static string StatusOf(NicSample s, string kind)
    {
        if (s.AdminUp == false) return NetStates.Disabled;
        if (s.Type == NetworkInterfaceType.Loopback) return NetStates.Connected;
        bool? link = s.Carrier ?? s.Oper switch
        {
            OperationalStatus.Up => true,
            OperationalStatus.Down or OperationalStatus.Dormant or OperationalStatus.LowerLayerDown => false,
            _ => null,
        };
        if (link is null) return NetStates.Unknown;
        if (link == true)
            return s.Ipv4.Any(UsableV4) || s.Ipv6.Any(GlobalV6) ? NetStates.Connected : NetStates.Limited;
        return kind == NetKinds.Ethernet ? NetStates.Unplugged : NetStates.Disconnected;
    }

    /// <summary>NicSample → det, der sendes til serveren.</summary>
    public static NetAdapter Adapter(NicSample s)
    {
        var kind = KindOf(s);
        var status = StatusOf(s, kind);
        var up = status is NetStates.Connected or NetStates.Limited;
        long? speed = up && s.SpeedBps is > 0 and < 1_000_000_000_000 ? s.SpeedBps / 1_000_000 : null;
        var v6 = s.Ipv6.Where(GlobalV6).Concat(s.Ipv6.Where(a => !GlobalV6(a))).Select(a => a.Split('%')[0]).Distinct().Take(MaxAddresses).ToList();
        return new NetAdapter
        {
            Name = s.Name, Description = s.Description is { Length: > 0 } d && d != s.Name ? d : null, Kind = kind, Virtual = IsVirtual(s), Status = status,
            SpeedMbps = speed is > 0 ? speed : null, MaxSpeedMbps = s.MaxSpeedMbps, FullDuplex = up ? s.FullDuplex : null,
            Mac = NormMac(s.Mac), Mtu = s.Mtu, VlanId = s.VlanId is > 0 ? s.VlanId : null,
            Ipv4 = s.Ipv4.Where(a => a != "0.0.0.0").Distinct().Take(MaxAddresses).ToList(), Ipv6 = v6,
            Ipv6Enabled = s.Ipv6Enabled == false ? false : s.Ipv6.Count > 0 ? true : s.Ipv6Enabled,
            Gateways = s.Gateways.Distinct().Take(MaxAddresses).ToList(),
            Dns = s.Dns.Select(a => a.Split('%')[0]).Distinct().Take(MaxAddresses).ToList(), DnsSuffix = s.DnsSuffix, Dhcp = s.Dhcp,
            RxBytes = s.Counters?.RxBytes, TxBytes = s.Counters?.TxBytes,
            RxErrors = s.Counters?.RxErrors, TxErrors = s.Counters?.TxErrors, RxDrops = s.Counters?.RxDrops, TxDrops = s.Counters?.TxDrops,
        };
    }

    private static int KindRank(string k) => k switch { NetKinds.Ethernet => 0, NetKinds.Wifi => 1, NetKinds.Mobile => 2, _ => 3 };
    private static int StateRank(string s) => s switch { NetStates.Connected => 0, NetStates.Limited => 1, NetStates.Unplugged or NetStates.Disconnected => 2, NetStates.Unknown => 3, _ => 4 };

    /// <summary>
    /// Saml listen: marker den primære (har standard-gatewayen; <paramref name="primaryName"/> er styresystemets svar, hvis det
    /// kendes), sortér (primær, forbundne, kablet før Wi-Fi), tæl de virtuelle og send kun et begrænset antal af dem.
    /// </summary>
    public static NetworkInfo Finish(IEnumerable<NetAdapter> adapters, string? primaryName, bool? portable, string source)
    {
        var all = adapters.Where(a => a.Name.Length > 0).GroupBy(a => a.Name, StringComparer.Ordinal).Select(g => g.First()).ToList();
        bool Up(NetAdapter a) => a.Status is NetStates.Connected or NetStates.Limited;
        var primary = all.FirstOrDefault(a => primaryName is not null && a.Name == primaryName && !a.Virtual && Up(a))
            ?? all.Where(a => !a.Virtual && a.Status == NetStates.Connected && a.Gateways.Count > 0).OrderBy(a => KindRank(a.Kind)).FirstOrDefault()
            ?? all.FirstOrDefault(a => primaryName is not null && a.Name == primaryName && Up(a))   // fx kun en VM-/VPN-adapter
            ?? all.Where(a => a.Status == NetStates.Connected && a.Gateways.Count > 0).OrderBy(a => KindRank(a.Kind)).FirstOrDefault();
        if (primary is not null) primary.Primary = true;
        var ordered = all.OrderByDescending(a => a.Primary).ThenBy(a => a.Virtual).ThenBy(a => StateRank(a.Status)).ThenBy(a => KindRank(a.Kind))
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var virt = ordered.Count(a => a.Virtual);
        return new NetworkInfo
        {
            Adapters = [.. ordered.Where(a => !a.Virtual || a.Primary).Take(MaxPhysical), .. ordered.Where(a => a.Virtual && !a.Primary).Take(MaxVirtual)],
            VirtualCount = virt, Portable = portable, Source = source,
        };
    }

    /// <summary>Det kortet kan, gættet ud fra en kendt chip/driver (bruges kun, når ethtool/ifconfig ikke kan svare).</summary>
    public static long? MaxFromModel(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (Regex.IsMatch(text, @"\b10\s?G(bE|bit|BASE)|\b10-Gigabit|AQC1\d\d|X5[45]0|X710|82599", RegexOptions.IgnoreCase)) return 10000;
        if (Regex.IsMatch(text, @"2[.,]5\s?G(bE|bit|BASE)?|\bI22[56]\b|RTL8125|\bigc\b|\br8125\b|Killer E3[01]00", RegexOptions.IgnoreCase)) return 2500;
        if (Regex.IsMatch(text, @"(?<![\d.,])5\s?G(bE|bit)|AQC111", RegexOptions.IgnoreCase)) return 5000;
        if (Regex.IsMatch(text, @"Gigabit|\bGbE\b|\b1000\s?(Base|Mbit)|\bI21[0179]\b|\bI350\b|8257[0-9]|8258[0-9]|RTL81(11|68|53)|Killer E2[2-6]00|BCM57|NetXtreme|\b(e1000e|igb|bcmgenet|macb|tg3|alx|atl1c|lan78xx)\b",
                RegexOptions.IgnoreCase)) return 1000;
        if (Regex.IsMatch(text, @"Fast Ethernet|10/100(?!/1000)|\bRTL81(00|01|02|03|05|39)|\bsmsc95xx\b|\b8139", RegexOptions.IgnoreCase)) return 100;
        return null;
    }

    // ------------------------------------------------------------------ Windows: netsh wlan show interfaces

    [GeneratedRegex(@"^\s*(\S.*?)\s+:\s?(.*)$")]
    private static partial Regex KeyValue();

    /// <summary>
    /// "netsh wlan show interfaces" på ethvert sprog: felterne findes ud fra deres værdi (MAC, GUID, "%", "GHz", "802.11…")
    /// og de få etiketter, der er ens eller kendte på engelsk, dansk, tysk, fransk, svensk og norsk.
    /// </summary>
    public static List<NetshWlan> Netsh(string? text)
    {
        var o = new List<NetshWlan>();
        if (string.IsNullOrWhiteSpace(text)) return o;
        var blocks = new List<List<(string K, string V)>>();
        List<(string K, string V)>? cur = null;
        var blank = true;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            if (raw.Trim().Length == 0) { blank = true; continue; }
            var m = KeyValue().Match(raw);
            if (!m.Success) { blank = true; continue; }
            if (blank || cur is null) { cur = []; blocks.Add(cur); }
            blank = false;
            cur.Add((m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim()));
        }
        foreach (var b in blocks)
        {
            string? guid = null, mac = null, ssid = null, bssid = null, band = null, radio = null, auth = null;
            bool? connected = null; int? signal = null, rssi = null, channel = null; double? rx = null, tx = null;
            foreach (var (k, v) in b)
            {
                var kl = k.ToLowerInvariant();
                if (Regex.IsMatch(v, @"^\{?[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}?$")) guid ??= v.Trim('{', '}').ToLowerInvariant();
                else if (kl.Contains("bssid")) bssid = NormMac(v);
                else if (kl == "ssid") ssid = v.Length > 0 ? v : null;
                else if (Regex.IsMatch(v, @"^([0-9a-fA-F]{2}[:-]){5}[0-9a-fA-F]{2}$")) mac ??= NormMac(v);
                else if (Regex.Match(v, @"^(\d{1,3})\s?%$") is { Success: true } sm) signal = int.Parse(sm.Groups[1].Value, CultureInfo.InvariantCulture);
                else if (kl == "rssi" && int.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var r)) rssi = r;
                else if (Regex.Match(v, @"^(\d(?:[.,]\d)?)\s?GHz$", RegexOptions.IgnoreCase) is { Success: true } bm) band = BandText(bm.Groups[1].Value);
                else if (Regex.IsMatch(v, @"^802\.11\w+$", RegexOptions.IgnoreCase)) radio = v;
                else if (kl.Contains("mbps") || kl.Contains("mbit"))
                {
                    if (double.TryParse(v.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var rate))
                        if (rx is null) rx = rate; else tx ??= rate;   // modtage kommer før sende på alle sprog
                }
                else if (kl is "channel" or "kanal" or "canal" or "kanaal" or "canale" && int.TryParse(v, out var ch)) channel = ch;
                else if (kl is "authentication" or "godkendelse" or "authentifizierung" or "authentification" or "autentisering" or "autentisering" or "autenticación" or "verificatie")
                    auth = v;
                else if (kl is "state" or "tilstand" or "status" or "zustand" or "état" or "etat" or "estado" or "läge" or "tillstånd" or "staat")
                    connected = WlanConnected(v);
            }
            if (guid is null && mac is null) continue;   // fx "Hosted network status"
            var link = new WifiLink
            {
                Ssid = ssid, Bssid = bssid, SignalPct = signal, Dbm = rssi is < 0 and > -120 ? rssi : signal is int s ? DbmFromPercent(s) : null,
                Band = band, Channel = channel, RxMbps = rx, TxMbps = tx, Radio = radio, Security = auth is { Length: > 0 } ? SecurityText(auth) : null,
            };
            if (link.Band is null && channel is int c) link.Band = c <= 14 ? "2,4 GHz" : null;
            if (channel is int cc && link.Band is { } bd) link.FrequencyMhz = FreqFromChannel(cc, bd);
            o.Add(new NetshWlan(b[0].V, b.Count > 1 ? b[1].V : null, guid, mac, connected, link));
        }
        return o;
    }

    /// <summary>Netsh-tilstanden på flere sprog. "disconnected"/"afbrudt" tjekkes før "connected"/"tilsluttet".</summary>
    public static bool? WlanConnected(string v)
    {
        var s = v.ToLowerInvariant();
        if (Regex.IsMatch(s, @"disconn|not conn|afbrudt|ikke (til|for)|getrennt|nicht verb|déconn|deconn|frånkopp|inte ansl|frakobl|ikke tilk|desconect|niet verb")) return false;
        if (Regex.IsMatch(s, @"connect|tilslut|forbund|verbunden|ansluten|tilkoblet|conectad|verbonden|connecté")) return true;
        return null;
    }

    /// <summary>Netsh' tekst for Wi-Fi uden adgang (Windows 11 24H2 kræver placeringstilladelse).</summary>
    public static bool NetshNeedsLocation(string? text) =>
        text is not null && Regex.IsMatch(text, @"location permission|placeringstilladelse|Standortberechtigung|ms-settings:privacy-location", RegexOptions.IgnoreCase);

    // ------------------------------------------------------------------ Wi-Fi-hjælpere

    /// <summary>Windows' signalprocent ↔ dBm (100 % ≈ -50 dBm, 0 % ≈ -100 dBm).</summary>
    public static int PercentFromDbm(int dbm) => Math.Clamp(2 * (dbm + 100), 0, 100);
    public static int DbmFromPercent(int pct) => Math.Clamp(pct, 0, 100) / 2 - 100;

    public static string? BandText(string ghz) => ghz.Replace(',', '.') switch
    {
        var g when g.StartsWith('2') => "2,4 GHz",
        var g when g.StartsWith('5') => "5 GHz",
        var g when g.StartsWith('6') => "6 GHz",
        var g when g.StartsWith("60", StringComparison.Ordinal) => "60 GHz",
        _ => null,
    };

    public static string? BandFromFreq(double mhz) => mhz switch
    {
        >= 2400 and < 2500 => "2,4 GHz",
        >= 4900 and < 5925 => "5 GHz",
        >= 5925 and < 7200 => "6 GHz",
        _ => null,
    };

    public static int? ChannelFromFreq(double mhz) => mhz switch
    {
        2484 => 14,
        >= 2412 and < 2484 => (int)Math.Round((mhz - 2407) / 5),
        >= 5955 and < 7200 => (int)Math.Round((mhz - 5950) / 5),
        >= 4900 and < 5925 => (int)Math.Round((mhz - 5000) / 5),
        _ => null,
    };

    public static int? FreqFromChannel(int ch, string band) => band switch
    {
        "2,4 GHz" => ch == 14 ? 2484 : 2407 + 5 * ch,
        "5 GHz" => 5000 + 5 * ch,
        "6 GHz" => 5950 + 5 * ch,
        _ => null,
    };

    /// <summary>Ensartet sikkerhedstekst: "WPA2-Personal", "WPA3-Personal", "WPA2-Enterprise", "Åben" …</summary>
    public static string SecurityText(string raw)
    {
        var s = raw.Trim();
        if (Regex.IsMatch(s, @"^(open|åben|offen|ouvert|öppen|åpen|abierta|none|--|)$", RegexOptions.IgnoreCase)) return "Åben";
        var wpa = Regex.Match(s, @"WPA\s?([123])", RegexOptions.IgnoreCase) is { Success: true } w ? $"WPA{w.Groups[1].Value}" : s.Contains("SAE", StringComparison.OrdinalIgnoreCase) ? "WPA3" : null;
        if (wpa is null) return s.Contains("WEP", StringComparison.OrdinalIgnoreCase) ? "WEP" : s;
        if (Regex.IsMatch(s, @"WPA2.*WPA3|WPA3.*WPA2|SAE.*PSK|PSK.*SAE|Transition", RegexOptions.IgnoreCase)) wpa = "WPA2/WPA3";
        var ent = Regex.IsMatch(s, @"Enterprise|802\.1X|EAP|Virksomhed|Unternehmen|Entreprise", RegexOptions.IgnoreCase);
        return $"{wpa}-{(ent ? "Enterprise" : "Personal")}";
    }

    // ------------------------------------------------------------------ Linux

    /// <summary>Det, /sys/class/net/&lt;navn&gt; fortæller. Null-felter kunne ikke læses.</summary>
    public sealed record SysfsNic(string Name, string? Operstate, string? Carrier, string? Speed, string? Duplex, string? Type, string? Flags, string? Mtu,
        bool Wireless, bool HasDevice, string? DevType, string? Driver);

    /// <summary>sysfs → de felter i NicSample, platformen bidrager med.</summary>
    public static NicSample WithSysfs(NicSample s, SysfsNic f)
    {
        bool? adminUp = TryHex(f.Flags) is long fl ? (fl & 1) == 1 : null;
        var oper = f.Operstate?.Trim().ToLowerInvariant();
        bool? carrier = f.Carrier?.Trim() switch { "1" => true, "0" => false, _ => null };
        if (adminUp == false) carrier = null;
        else if (carrier is null) carrier = oper switch { "up" => true, "down" or "lowerlayerdown" or "dormant" or "notpresent" => false, _ => null };
        if (oper == "dormant") carrier = false;   // Wi-Fi der endnu ikke er forbundet
        var type = int.TryParse(f.Type, out var t) ? t : (int?)null;
        long? speed = long.TryParse(f.Speed?.Trim(), out var sp) && sp is > 0 and < 4_000_000 ? sp * 1_000_000 : null;
        var dev = f.DevType?.Trim().ToLowerInvariant();
        return s with
        {
            AdminUp = adminUp, Carrier = carrier,
            Wireless = f.Wireless || dev == "wlan" ? true : s.Wireless,
            Mobile = dev == "wwan" || type == 519 ? true : s.Mobile,   // ARPHRD_RAWIP
            Hardware = type == 772 || dev is "bridge" or "vlan" or "bond" or "team" or "macvlan" or "wireguard" or "vxlan" ? false : f.HasDevice,
            Type = type switch { 772 => NetworkInterfaceType.Loopback, 1 when s.Type is null or NetworkInterfaceType.Unknown => NetworkInterfaceType.Ethernet, _ => s.Type },
            SpeedBps = speed ?? (s.Oper == OperationalStatus.Up && s.SpeedBps is > 0 ? s.SpeedBps : null),
            FullDuplex = f.Duplex?.Trim().ToLowerInvariant() switch { "full" => true, "half" => false, _ => null },
            Mtu = int.TryParse(f.Mtu, out var mtu) && mtu > 0 ? mtu : s.Mtu,
            MaxSpeedMbps = s.MaxSpeedMbps ?? MaxFromModel(f.Driver),
        };
    }

    private static long? TryHex(string? s)
    {
        var v = s?.Trim();
        if (string.IsNullOrEmpty(v)) return null;
        if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) v = v[2..];
        return long.TryParse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    /// <summary>/proc/net/route → interfacet med standardruten (laveste metric).</summary>
    public static string? LinuxDefaultRoute(string? procNetRoute)
    {
        if (string.IsNullOrWhiteSpace(procNetRoute)) return null;
        return procNetRoute.Split('\n').Skip(1).Select(l => l.Split('\t', StringSplitOptions.TrimEntries))
            .Where(c => c.Length >= 8 && c[1] == "00000000" && c[7] == "00000000" && (TryHex(c[3]) ?? 0) % 2 == 1)
            .OrderBy(c => int.TryParse(c[6], out var m) ? m : int.MaxValue).Select(c => c[0]).FirstOrDefault();
    }

    /// <summary>"ip -o -4 addr show" → de interfaces, der har en DHCP-adresse ("dynamic") og dem med en fast adresse.</summary>
    public static Dictionary<string, bool> LinuxDhcp(string? ipAddr)
    {
        var o = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var l in (ipAddr ?? "").Split('\n'))
        {
            var m = Regex.Match(l, @"^\d+:\s+([^\s:@]+)(?:@\S+)?\s+inet\s+\S+.*?\bscope\s+global\b(.*)$");
            if (!m.Success) continue;
            var dyn = Regex.IsMatch(m.Groups[2].Value, @"\bdynamic\b");
            o[m.Groups[1].Value] = o.GetValueOrDefault(m.Groups[1].Value) || dyn;
        }
        return o;
    }

    /// <summary>/proc/net/vlan/config → VLAN-id pr. interface.</summary>
    public static Dictionary<string, int> LinuxVlans(string? config)
    {
        var o = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var l in (config ?? "").Split('\n'))
        {
            var c = l.Split('|', StringSplitOptions.TrimEntries);
            if (c.Length >= 3 && int.TryParse(c[1], out var id)) o[c[0]] = id;
        }
        return o;
    }

    /// <summary>ethtool: det højeste, kortet understøtter ("Supported link modes"), i Mbit/s.</summary>
    public static long? EthtoolMax(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Regex.Match(text, @"Supported link modes:(.*?)(?:\n\s*[A-Z][\w -]+:|\z)", RegexOptions.Singleline);
        if (!m.Success) return null;
        var speeds = Regex.Matches(m.Groups[1].Value, @"(\d+)base", RegexOptions.IgnoreCase).Select(x => long.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
        return speeds.Count > 0 ? speeds.Max() : null;
    }

    /// <summary>"iw dev &lt;if&gt; link" → Wi-Fi-forbindelsen. Null = ikke forbundet.</summary>
    public static WifiLink? IwLink(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.Contains("Connected to", StringComparison.Ordinal)) return null;
        string? F(string key) => Regex.Match(text, @"^\s*" + Regex.Escape(key) + @":\s*(.+)$", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value.Trim() : null;
        var freq = double.TryParse(F("freq")?.Split(' ')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : (double?)null;
        int? dbm = Regex.Match(F("signal") ?? "", @"^(-?\d+)") is { Success: true } sm ? int.Parse(sm.Groups[1].Value, CultureInfo.InvariantCulture) : null;
        double? Rate(string? s) => Regex.Match(s ?? "", @"^([\d.]+)\s*MBit/s", RegexOptions.IgnoreCase) is { Success: true } r ? double.Parse(r.Groups[1].Value, CultureInfo.InvariantCulture) : null;
        var rxs = F("rx bitrate"); var txs = F("tx bitrate");
        return new WifiLink
        {
            Ssid = F("SSID"), Bssid = NormMac(Regex.Match(text, @"Connected to ([0-9a-fA-F:]{17})").Groups[1].Value),
            Dbm = dbm, SignalPct = dbm is int d ? PercentFromDbm(d) : null,
            FrequencyMhz = freq is double fq ? (int)Math.Round(fq) : null, Band = freq is double b ? BandFromFreq(b) : null, Channel = freq is double c ? ChannelFromFreq(c) : null,
            RxMbps = Rate(rxs), TxMbps = Rate(txs), Radio = RadioFromIw($"{rxs} {txs}"),
        };
    }

    private static string? RadioFromIw(string s) =>
        s.Contains("EHT", StringComparison.Ordinal) ? "802.11be" : s.Contains("HE-", StringComparison.Ordinal) ? "802.11ax"
        : s.Contains("VHT", StringComparison.Ordinal) ? "802.11ac" : s.Contains("MCS", StringComparison.Ordinal) ? "802.11n" : null;

    /// <summary>
    /// "nmcli -t -f ACTIVE,DEVICE,SIGNAL,FREQ,CHAN,RATE,BSSID,SECURITY,SSID dev wifi list --rescan no" → den aktive forbindelse pr. enhed.
    /// nmcli skriver ':' i værdier som '\:'.
    /// </summary>
    public static Dictionary<string, WifiLink> Nmcli(string? text)
    {
        var o = new Dictionary<string, WifiLink>(StringComparer.Ordinal);
        foreach (var line in (text ?? "").Split('\n'))
        {
            var c = SplitNmcli(line.TrimEnd('\r'));
            if (c.Count < 9 || !c[0].Equals("yes", StringComparison.OrdinalIgnoreCase) || c[1].Length == 0) continue;
            var freq = Regex.Match(c[3], @"(\d+)") is { Success: true } fm ? int.Parse(fm.Groups[1].Value, CultureInfo.InvariantCulture) : (int?)null;
            int? pct = int.TryParse(c[2], out var p) ? p : null;
            o[c[1]] = new WifiLink
            {
                SignalPct = pct, Dbm = pct is int q ? DbmFromPercent(q) : null, FrequencyMhz = freq, Band = freq is int f ? BandFromFreq(f) : null,
                Channel = int.TryParse(c[4], out var ch) ? ch : null,
                RxMbps = Regex.Match(c[5], @"([\d.]+)") is { Success: true } rm ? double.Parse(rm.Groups[1].Value, CultureInfo.InvariantCulture) : null,
                Bssid = NormMac(c[6]), Security = c[7] is { Length: > 0 } sec ? SecurityText(sec) : "Åben",
                Ssid = string.Join(':', c.Skip(8)) is { Length: > 0 } ss ? ss : null,
            };
        }
        return o;
    }

    private static List<string> SplitNmcli(string line)
    {
        var o = new List<string>(); var cur = new System.Text.StringBuilder();
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '\\' && i + 1 < line.Length) { cur.Append(line[++i]); continue; }
            if (line[i] == ':') { o.Add(cur.ToString()); cur.Clear(); continue; }
            cur.Append(line[i]);
        }
        o.Add(cur.ToString());
        return o;
    }

    // ------------------------------------------------------------------ Mac

    /// <summary>"networksetup -listallhardwareports" → enhed (en0) → portens navn ("Wi-Fi", "Ethernet", "USB 10/100/1000 LAN").</summary>
    public static Dictionary<string, string> MacPorts(string? text)
    {
        var o = new Dictionary<string, string>(StringComparer.Ordinal);
        string? port = null;
        foreach (var l in (text ?? "").Split('\n'))
        {
            if (Regex.Match(l, @"^Hardware Port:\s*(.+)$") is { Success: true } p) port = p.Groups[1].Value.Trim();
            else if (Regex.Match(l, @"^Device:\s*(\S+)") is { Success: true } d && port is not null) { o[d.Groups[1].Value] = port; port = null; }
        }
        return o;
    }

    /// <summary>"ifconfig -a -m" (macOS) → status, forhandlet medie, duplex, understøttede hastigheder, MTU og VLAN pr. interface.</summary>
    public static Dictionary<string, MacIf> MacIfconfig(string? text)
    {
        var o = new Dictionary<string, MacIf>(StringComparer.Ordinal);
        string? name = null; var block = new List<string>();
        void Flush()
        {
            if (name is null) return;
            var all = string.Join('\n', block);
            var head = block.FirstOrDefault() ?? "";
            var up = Regex.IsMatch(head, @"<[^>]*\bUP\b");
            int? mtu = Regex.Match(head, @"\bmtu (\d+)") is { Success: true } mm ? int.Parse(mm.Groups[1].Value, CultureInfo.InvariantCulture) : null;
            bool? active = Regex.Match(all, @"^\s*status:\s*(\w+)", RegexOptions.Multiline) is { Success: true } sm ? sm.Groups[1].Value == "active" : null;
            var media = Regex.Match(all, @"^\s*media:\s*(.+)$", RegexOptions.Multiline) is { Success: true } md ? md.Groups[1].Value : "";
            var cur = Regex.Match(media, @"\(([^)]*)") is { Success: true } paren ? paren.Groups[1].Value : media;
            long? speed = MediaSpeed(cur);
            bool? duplex = cur.Contains("full-duplex", StringComparison.Ordinal) ? true : cur.Contains("half-duplex", StringComparison.Ordinal) ? false : null;
            var sup = Regex.Matches(all, @"^\s*media\s+(\S+)", RegexOptions.Multiline).Select(x => MediaSpeed(x.Groups[1].Value)).Where(x => x is not null).Select(x => x!.Value).ToList();
            int? vlan = Regex.Match(all, @"^\s*vlan:\s*(\d+)", RegexOptions.Multiline) is { Success: true } vm ? int.Parse(vm.Groups[1].Value, CultureInfo.InvariantCulture) : null;
            o[name] = new MacIf(name, up, active, speed, duplex, sup.Count > 0 ? sup.Max() : null, mtu, vlan);
        }
        foreach (var l in (text ?? "").Split('\n'))
        {
            if (Regex.Match(l, @"^([A-Za-z][\w.]*):\s+flags=") is { Success: true } h) { Flush(); name = h.Groups[1].Value; block = [l]; }
            else block.Add(l);
        }
        Flush();
        return o;
    }

    /// <summary>"1000baseT", "100baseTX", "10GbaseT", "2500Base-T", "10baseT/UTP" → Mbit/s.</summary>
    public static long? MediaSpeed(string s)
    {
        var m = Regex.Match(s, @"\b(\d+(?:\.\d+)?)(G)?[Bb]ase", RegexOptions.None);
        if (!m.Success) return null;
        var n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return (long)(m.Groups[2].Success ? n * 1000 : n);
    }

    /// <summary>"system_profiler SPAirPortDataType" → den aktuelle Wi-Fi-forbindelse (macOS skjuler SSID uden placeringsadgang).</summary>
    public static WifiLink? MacAirport(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var i = text.IndexOf("Current Network Information:", StringComparison.Ordinal);
        if (i < 0) return null;
        var lines = text[(i + "Current Network Information:".Length)..].Split('\n').Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count == 0) return null;
        var indent = lines[0].Length - lines[0].TrimStart().Length;
        var ssidLine = lines[0].Trim();
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in lines.Skip(1))
        {
            if (l.Length - l.TrimStart().Length <= indent) break;
            var kv = l.Trim().Split(':', 2);
            if (kv.Length == 2) props[kv[0].Trim()] = kv[1].Trim();
        }
        var ssid = ssidLine.EndsWith(':') ? ssidLine[..^1] : ssidLine;
        int? dbm = Regex.Match(props.GetValueOrDefault("Signal / Noise") ?? "", @"(-\d+)\s*dBm") is { Success: true } sm ? int.Parse(sm.Groups[1].Value, CultureInfo.InvariantCulture) : null;
        var chText = props.GetValueOrDefault("Channel") ?? "";
        int? ch = Regex.Match(chText, @"^(\d+)") is { Success: true } cm ? int.Parse(cm.Groups[1].Value, CultureInfo.InvariantCulture) : null;
        var band = Regex.Match(chText, @"(\d(?:\.\d)?)\s?GHz") is { Success: true } bm ? BandText(bm.Groups[1].Value) : ch is <= 14 ? "2,4 GHz" : null;
        return new WifiLink
        {
            Ssid = ssid.Length == 0 || ssid.Contains("redacted", StringComparison.OrdinalIgnoreCase) ? null : ssid,
            Bssid = NormMac(props.GetValueOrDefault("BSSID")), Dbm = dbm, SignalPct = dbm is int d ? PercentFromDbm(d) : null,
            Channel = ch, Band = band, FrequencyMhz = ch is int c && band is { } b ? FreqFromChannel(c, b) : null,
            TxMbps = double.TryParse(props.GetValueOrDefault("Transmit Rate"), NumberStyles.Float, CultureInfo.InvariantCulture, out var tx) ? tx : null,
            Radio = props.GetValueOrDefault("PHY Mode"), Security = props.GetValueOrDefault("Security") is { Length: > 0 } sec ? SecurityText(sec) : null,
        };
    }

    /// <summary>"route -n get default" (macOS) → interfacet med standardruten.</summary>
    public static string? MacDefaultRoute(string? text) =>
        Regex.Match(text ?? "", @"^\s*interface:\s*(\S+)", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value : null;

    // ------------------------------------------------------------------ til maskinoversigten

    /// <summary>Kort status til maskinlisten: den primære adapter (type, hastighed, signal) og om den er værd at kigge på.</summary>
    public static NetBrief? Brief(NetworkInfo? net)
    {
        var p = net?.Adapters.FirstOrDefault(a => a.Primary);
        if (p is null) return null;
        return new NetBrief(p.Kind, p.SpeedMbps, p.Wifi?.SignalPct, NetHealth.Findings(net).Count > 0 || p.FullDuplex == false);
    }
}

/// <summary>Maskinlistens lille netværksstatus.</summary>
public sealed record NetBrief(string Kind, long? SpeedMbps, int? SignalPct, bool Warn);

/// <summary>
/// Trafik nu, nye fejl og link op/ned mellem to målinger. Agenten holder én pr. proces; tilstanden lever kun i hukommelsen,
/// så en genstart af agenten starter forfra (første måling har ingen "nu"-tal).
/// </summary>
public sealed class NetTracker
{
    private sealed class State
    {
        public NetCounters? Counters;
        public DateTime At;
        public bool? Up;
        public long? CarrierChanges;
        public readonly List<DateTime> Changes = [];
        public DateTime? ChangedAt;
    }

    private readonly Dictionary<string, State> states = new(StringComparer.Ordinal);
    private DateTime started = DateTime.MinValue;

    /// <summary>Udfyld rater, nye fejl og link-skift på adapterne. <paramref name="counters"/> er nøglet på adapterens navn.</summary>
    public void Apply(IEnumerable<NetAdapter> adapters, IReadOnlyDictionary<string, NetCounters> counters, DateTime now)
    {
        if (started == DateTime.MinValue) started = now;
        foreach (var a in adapters)
        {
            var key = a.Name;
            counters.TryGetValue(key, out var c);
            var up = a.Status switch { NetStates.Connected or NetStates.Limited => true, NetStates.Unplugged or NetStates.Disconnected or NetStates.Disabled => false, _ => (bool?)null };
            if (!states.TryGetValue(key, out var s))
            {
                states[key] = new State { Counters = c, At = now, Up = up, CarrierChanges = c?.CarrierChanges };
                a.LinkChanges24h = null;
                continue;
            }
            var secs = (now - s.At).TotalSeconds;
            if (c is not null && s.Counters is { } p && secs >= 1 && !Reset(p, c))
            {
                a.SampleSeconds = (int)Math.Round(secs);
                a.RxRate = Rate(p.RxBytes, c.RxBytes, secs);
                a.TxRate = Rate(p.TxBytes, c.TxBytes, secs);
                var de = Delta(p.RxErrors, c.RxErrors) is long re && Delta(p.TxErrors, c.TxErrors) is long te ? re + te
                    : Delta(p.RxErrors, c.RxErrors) ?? Delta(p.TxErrors, c.TxErrors);
                a.NewErrors = de;
                a.NewPackets = Delta(p.RxPackets, c.RxPackets) is long rp && Delta(p.TxPackets, c.TxPackets) is long tp ? rp + tp : null;
            }
            // link-skift: Linux' carrier_changes tæller også dem mellem målingerne; ellers ses kun skift i status
            var flips = c?.CarrierChanges is long cc && s.CarrierChanges is long pc && cc >= pc ? (int)Math.Min(cc - pc, 50)
                : up is bool u && s.Up is bool pu && u != pu ? 1 : 0;
            for (var i = 0; i < flips; i++) s.Changes.Add(now);
            if (flips > 0) s.ChangedAt = now;
            s.Changes.RemoveAll(t => t < now.AddHours(-24));
            s.Counters = c ?? s.Counters; s.At = now; s.Up = up ?? s.Up; s.CarrierChanges = c?.CarrierChanges ?? s.CarrierChanges;
            a.LinkChanges24h = s.Changes.Count;
            a.LinkChangedAt = s.ChangedAt;
        }
    }

    /// <summary>En tæller er gået baglæns (driveren/adapteren er genstartet): ingen tal frem for forkerte.</summary>
    private static bool Reset(NetCounters p, NetCounters c) =>
        new[] { (p.RxBytes, c.RxBytes), (p.TxBytes, c.TxBytes), (p.RxPackets, c.RxPackets), (p.TxPackets, c.TxPackets), (p.RxErrors, c.RxErrors), (p.TxErrors, c.TxErrors) }
            .Any(x => x.Item1 is long b && x.Item2 is long n && n < b);

    private static long? Delta(long? before, long? now) => before is long b && now is long n && n >= b ? n - b : null;   // nulstillet tæller: ukendt
    private static double? Rate(long? before, long? now, double secs) => Delta(before, now) is long d ? Math.Round(d / secs, 1) : null;
}

/// <summary>Helbredsregler for netværket – konservative, så de ikke giver falske alarmer.</summary>
public static class NetHealth
{
    /// <summary>Wi-Fi under denne signalstyrke er svagt (Windows' procent).</summary>
    public const int WeakWifiPct = 40;
    /// <summary>Mindst så mange nye fejl og mindst 1 promille af pakkerne mellem to målinger.</summary>
    public const int ErrorBurst = 50;
    /// <summary>Så mange link-skift (op eller ned) på et døgn er ustabilt.</summary>
    public const int FlapsPerDay = 10;

    public sealed record NetFinding(string Key, string Title, string Detail);

    public static string SpeedText(long mbps) => mbps >= 1000 ? $"{(mbps / 1000.0).ToString(mbps % 1000 == 0 ? "0" : "0.#", CultureInfo.GetCultureInfo("da-DK"))} Gbit/s" : $"{mbps} Mbit/s";

    public static List<NetFinding> Findings(NetworkInfo? net)
    {
        var o = new List<NetFinding>();
        if (net is null) return o;
        var p = net.Adapters.FirstOrDefault(a => a.Primary && !a.Virtual);
        if (p is { Kind: NetKinds.Ethernet, Status: NetStates.Connected, SpeedMbps: long sp, MaxSpeedMbps: long max } && sp < 1000 && max >= 1000 && sp < max)
            o.Add(new("net:speed", $"Netkortet kører kun {SpeedText(sp)}",
                $"{p.Description ?? p.Name} kan {SpeedText(max)}, men forbindelsen er kun {SpeedText(sp)}{(p.FullDuplex == false ? " (half duplex)" : "")}. Tjek kablet (Cat5e eller bedre) og porten på switchen/routeren."));
        if (p is { Kind: NetKinds.Wifi, Status: NetStates.Connected, Wifi.SignalPct: int pct } && pct < WeakWifiPct && net.Portable == false)
            o.Add(new("net:wifi", "Svagt Wi-Fi-signal",
                $"{p.Wifi.Ssid ?? p.Name}: {pct} %{(p.Wifi.Dbm is int d ? $" ({d} dBm)" : "")}. Maskinen står fast – flyt routeren/et access point tættere på, eller brug kabel."));
        foreach (var a in net.Adapters.Where(a => !a.Virtual && a.Status == NetStates.Connected))
            if (a.NewErrors is long ne && ne >= ErrorBurst && (a.NewPackets is not long np || ne * 1000 >= np))
                o.Add(new($"net:errors:{a.Name}", $"Fejl på netværksforbindelsen ({a.Name})",
                    $"{ne} fejlramte pakker på {a.SampleSeconds ?? 0} sekunder{(a.NewPackets is long n ? $" (af {n})" : "")}. Ofte et dårligt kabel, stik eller switch-port."));
        foreach (var a in net.Adapters.Where(a => !a.Virtual && a.Kind == NetKinds.Ethernet && net.Portable != true))
            if (a.LinkChanges24h is int fl && fl >= FlapsPerDay)
                o.Add(new($"net:flaps:{a.Name}", $"Netværksforbindelsen falder ud ({a.Name})",
                    $"Linket er gået op eller ned {fl} gange det seneste døgn. Tjek kablet, stikket og switch-porten."));
        return o;
    }
}
