using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Unix;

/// <summary>
/// Netværksadaptere på Linux (/sys/class/net, ip, ethtool, iw/nmcli hvis de findes) og Mac (networksetup, ifconfig,
/// system_profiler). Samles med helbredet; en fejl giver kun en fejltekst og vælter aldrig agenten.
/// </summary>
public static class NetCollect
{
    private static readonly NetTracker Tracker = new();
    private static bool? portable;
    private static bool portableRead;
    private static (DateTime At, Dictionary<string, string> Ports) macPorts = (DateTime.MinValue, []);
    private static (DateTime At, WifiLink? Link) macWifi = (DateTime.MinValue, null);
    private static bool? hasEthtool, hasIw, hasNmcli;

    public static NetworkInfo Status()
    {
        try
        {
            var samples = NetworkInterface.GetAllNetworkInterfaces().Select(NetParse.Snapshot).ToList();
            if (!portableRead) { portable = DiskCrypto.Portable(); portableRead = true; }
            return Mac.Is ? MacInfo(samples) : LinuxInfo(samples);
        }
        catch (Exception e) when (e is NetworkInformationException or IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException)
        {
            return new NetworkInfo { Source = Mac.Is ? Platforms.Mac : Platforms.Linux, Error = $"Netværksadapterne kunne ikke læses ({e.GetType().Name})." };
        }
    }

    private static bool SafeName(string n) => Regex.IsMatch(n, @"^[A-Za-z0-9._@:-]{1,32}$");

    // ------------------------------------------------------------------ Linux

    private static NetworkInfo LinuxInfo(List<NicSample> samples)
    {
        var dhcp = NetParse.LinuxDhcp(Shell.Out("ip -o -4 addr show 2>/dev/null", 5));
        var vlans = NetParse.LinuxVlans(Read("/proc/net/vlan/config"));
        var list = new List<NicSample>();
        foreach (var s0 in samples)
        {
            var dir = Path.Combine("/sys/class/net", s0.Name);
            var s = s0;
            if (Directory.Exists(dir))
            {
                string? R(string f) => Read(Path.Combine(dir, f));
                var dev = Regex.Match(R("uevent") ?? "", @"^DEVTYPE=(\S+)", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value : null;
                string? driver = null;
                try { driver = new DirectoryInfo(Path.Combine(dir, "device", "driver")).ResolveLinkTarget(false)?.Name; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                s = NetParse.WithSysfs(s, new NetParse.SysfsNic(s.Name, R("operstate"), R("carrier"), R("speed"), R("duplex"), R("type"), R("flags"), R("mtu"),
                    Directory.Exists(Path.Combine(dir, "wireless")) || Directory.Exists(Path.Combine(dir, "phy80211")), Directory.Exists(Path.Combine(dir, "device")), dev, driver));
                long? N(string f) => long.TryParse(R(Path.Combine("statistics", f)), out var v) ? v : null;
                s = s with
                {
                    Counters = new NetCounters(N("rx_bytes"), N("tx_bytes"), N("rx_packets"), N("tx_packets"), N("rx_errors"), N("tx_errors"), N("rx_dropped"), N("tx_dropped"),
                        long.TryParse(R("carrier_changes"), out var cc) ? cc : null),
                };
            }
            s = s with
            {
                Dhcp = dhcp.TryGetValue(s.Name, out var d) ? d : null,
                VlanId = vlans.TryGetValue(s.Name, out var vl) ? vl : s.VlanId,
            };
            if (s.Hardware == true && s.Wireless != true && s.Carrier == true && SafeName(s.Name) && (hasEthtool ??= Shell.Has("ethtool")))
                s = s with { MaxSpeedMbps = NetParse.EthtoolMax(Shell.Out($"ethtool {s.Name} 2>/dev/null", 5)) ?? s.MaxSpeedMbps };
            list.Add(s);
        }
        var adapters = list.Select(NetParse.Adapter).ToList();
        var wifi = adapters.Where(a => a.Kind == NetKinds.Wifi && !a.Virtual && a.Status is NetStates.Connected or NetStates.Limited && SafeName(a.Name)).ToList();
        if (wifi.Count > 0)
        {
            var nm = (hasNmcli ??= Shell.Has("nmcli"))
                ? NetParse.Nmcli(Shell.Out("nmcli -t -f ACTIVE,DEVICE,SIGNAL,FREQ,CHAN,RATE,BSSID,SECURITY,SSID dev wifi list --rescan no 2>/dev/null", 10))
                : [];
            foreach (var a in wifi)
            {
                var iw = (hasIw ??= Shell.Has("iw")) ? NetParse.IwLink(Shell.Out($"iw dev {a.Name} link 2>/dev/null", 5)) : null;
                var n = nm.GetValueOrDefault(a.Name);
                a.Wifi = iw is null ? n : n is null ? iw : Merge(iw, n);
            }
        }
        var info = NetParse.Finish(adapters, NetParse.LinuxDefaultRoute(Read("/proc/net/route")), portable, Platforms.Linux);
        Tracker.Apply(info.Adapters, list.Where(s => s.Counters is not null).GroupBy(s => s.Name).ToDictionary(g => g.Key, g => g.First().Counters!), DateTime.UtcNow);
        return info;
    }

    /// <summary>iw er mest præcis (dBm, bitrate); nmcli kender sikkerheden.</summary>
    private static WifiLink Merge(WifiLink iw, WifiLink nm)
    {
        iw.Ssid ??= nm.Ssid; iw.Bssid ??= nm.Bssid; iw.Security ??= nm.Security; iw.Channel ??= nm.Channel; iw.Band ??= nm.Band;
        iw.FrequencyMhz ??= nm.FrequencyMhz; iw.SignalPct ??= nm.SignalPct; iw.RxMbps ??= nm.RxMbps;
        return iw;
    }

    private static string? Read(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }   // fx speed/carrier på et interface, der er nede
    }

    // ------------------------------------------------------------------ Mac

    private static NetworkInfo MacInfo(List<NicSample> samples)
    {
        if (DateTime.UtcNow - macPorts.At > TimeSpan.FromHours(1))
            macPorts = (DateTime.UtcNow, NetParse.MacPorts(Shell.Out("/usr/sbin/networksetup -listallhardwareports 2>/dev/null", 10)));
        var ports = macPorts.Ports;
        var ifc = NetParse.MacIfconfig(Shell.Out("/sbin/ifconfig -a -m 2>/dev/null", 10));
        var list = new List<NicSample>();
        foreach (var s0 in samples)
        {
            var port = ports.GetValueOrDefault(s0.Name);
            var wifiPort = port is not null && Regex.IsMatch(port, @"Wi-?Fi|AirPort", RegexOptions.IgnoreCase);
            var s = s0 with
            {
                Description = port ?? s0.Description,
                Wireless = wifiPort ? true : null,
                Mobile = port is not null && Regex.IsMatch(port, @"iPhone|iPad|Modem|Cellular", RegexOptions.IgnoreCase) ? true : null,
                Hardware = port is not null ? !port.Contains("Bridge", StringComparison.OrdinalIgnoreCase) : s0.Name.StartsWith("en", StringComparison.Ordinal) ? null : false,
                Type = s0.Type is null or NetworkInterfaceType.Unknown && port is not null && !wifiPort ? NetworkInterfaceType.Ethernet : s0.Type,
            };
            if (ifc.TryGetValue(s.Name, out var f))
                s = s with
                {
                    AdminUp = f.Up, Carrier = f.Up ? f.Active : null, SpeedBps = f.SpeedMbps is long sp ? sp * 1_000_000 : s.SpeedBps,
                    FullDuplex = f.FullDuplex, MaxSpeedMbps = wifiPort ? null : f.MaxSpeedMbps, Mtu = f.Mtu ?? s.Mtu, VlanId = f.VlanId,
                };
            if (s.Hardware == true && s.Ipv4.Count > 0 && SafeName(s.Name))
                s = s with { Dhcp = Shell.Out($"/usr/sbin/ipconfig getpacket {s.Name} 2>/dev/null", 5).Length > 0 };
            list.Add(s);
        }
        var adapters = list.Select(NetParse.Adapter).ToList();
        var wifi = adapters.FirstOrDefault(a => a.Kind == NetKinds.Wifi && !a.Virtual && a.Status is NetStates.Connected or NetStates.Limited);
        if (wifi is not null)
        {
            // system_profiler tager nogle sekunder – højst hvert 5. minut
            if (DateTime.UtcNow - macWifi.At > TimeSpan.FromMinutes(5))
                macWifi = (DateTime.UtcNow, NetParse.MacAirport(Shell.Out("/usr/sbin/system_profiler SPAirPortDataType 2>/dev/null", 30)));
            wifi.Wifi = macWifi.Link;
            if (wifi.Wifi?.TxMbps is double tx && wifi.SpeedMbps is null) wifi.SpeedMbps = (long)tx;
        }
        var info = NetParse.Finish(adapters, NetParse.MacDefaultRoute(Shell.Out("/sbin/route -n get default 2>/dev/null", 5)), portable, Platforms.Mac);
        if (wifi is not null && wifi.Wifi?.Ssid is null && wifi.Wifi is not null)
            info.WifiNote = "macOS viser kun netværksnavnet (SSID) til programmer med placeringsadgang.";
        Tracker.Apply(info.Adapters, list.Where(s => s.Counters is not null).GroupBy(s => s.Name).ToDictionary(g => g.Key, g => g.First().Counters!), DateTime.UtcNow);
        return info;
    }
}
