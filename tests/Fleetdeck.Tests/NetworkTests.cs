using System.Net.NetworkInformation;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;

namespace Fleetdeck.Tests;

/// <summary>Netværksadaptere: .NET-data, netsh (engelsk/dansk), sysfs, iw/nmcli, Mac, virtuelle adaptere, målinger og helbredsreglerne.</summary>
public class NetworkTests
{
    private static NicSample Eth(string name = "Ethernet", string? desc = "Intel(R) Ethernet Connection (7) I219-V", long mbps = 1000, OperationalStatus oper = OperationalStatus.Up) => new()
    {
        Name = name, Description = desc, Id = "{6A1B2C3D-0000-4000-8000-0000000000A1}", Type = NetworkInterfaceType.Ethernet, Oper = oper, SpeedBps = mbps * 1_000_000,
        Mac = "00155DAABB01", Mtu = 1500, Ipv4 = ["192.0.2.10"], Ipv6 = ["fe80::1%12", "2001:db8::10"], Ipv6Enabled = true,
        Gateways = ["192.0.2.1"], Dns = ["192.0.2.1", "fe80::1%12"], DnsSuffix = "lan", Dhcp = true,
    };

    // ------------------------------------------------------------------ .NET → kontrakt

    [Fact]
    public void Ethernet_sample_becomes_connected_adapter()
    {
        var a = NetParse.Adapter(Eth());
        Assert.Equal(NetKinds.Ethernet, a.Kind);
        Assert.Equal(NetStates.Connected, a.Status);
        Assert.False(a.Virtual);
        Assert.Equal(1000, a.SpeedMbps);
        Assert.Equal("00-15-5D-AA-BB-01", a.Mac);
        Assert.Equal(["192.0.2.10"], a.Ipv4);
        Assert.Equal("2001:db8::10", a.Ipv6[0]);   // global før link-local, uden %zone
        Assert.Equal("fe80::1", a.Ipv6[1]);
        Assert.Equal(["192.0.2.1", "fe80::1"], a.Dns);
        Assert.True(a.Dhcp);
        Assert.True(a.Ipv6Enabled);
        Assert.Equal(1500, a.Mtu);
        Assert.Equal("lan", a.DnsSuffix);
    }

    [Fact]
    public void Statuses_unplugged_disconnected_disabled_limited()
    {
        Assert.Equal(NetStates.Unplugged, NetParse.Adapter(Eth(oper: OperationalStatus.Down) with { Ipv4 = [] }).Status);
        var wifi = new NicSample { Name = "Wi-Fi", Type = NetworkInterfaceType.Wireless80211, Oper = OperationalStatus.Down };
        Assert.Equal(NetKinds.Wifi, NetParse.Adapter(wifi).Kind);
        Assert.Equal(NetStates.Disconnected, NetParse.Adapter(wifi).Status);
        Assert.Equal(NetStates.Disabled, NetParse.Adapter(Eth() with { AdminUp = false }).Status);
        Assert.Equal(NetStates.Limited, NetParse.Adapter(Eth() with { Ipv4 = ["169.254.10.2"], Ipv6 = ["fe80::2"] }).Status);
        Assert.Equal(NetStates.Unplugged, NetParse.Adapter(Eth() with { Carrier = false }).Status);   // WMI: kablet ude vinder over .NET
        Assert.Null(NetParse.Adapter(Eth(oper: OperationalStatus.Down)).SpeedMbps);   // ingen hastighed uden link
    }

    [Fact]
    public void Mobile_broadband_is_mobile()
    {
        Assert.Equal(NetKinds.Mobile, NetParse.Adapter(new NicSample { Name = "Mobilt bredbånd", Description = "Quectel EM05-G", Type = NetworkInterfaceType.Wwanpp }).Kind);
        Assert.Equal(NetKinds.Mobile, NetParse.Adapter(new NicSample { Name = "wwan0", Type = NetworkInterfaceType.Unknown }).Kind);
        Assert.Equal(NetKinds.Mobile, NetParse.Adapter(new NicSample { Name = "en8", Description = "iPhone USB", Type = NetworkInterfaceType.Ethernet }).Kind);
    }

    // ------------------------------------------------------------------ virtuelle adaptere

    [Theory]
    [InlineData("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter", true)]
    [InlineData("Ethernet 3", "VirtualBox Host-Only Ethernet Adapter", true)]
    [InlineData("VMware Network Adapter VMnet8", "VMware Virtual Ethernet Adapter for VMnet8", true)]
    [InlineData("Local Area Connection", "TAP-Windows Adapter V9", true)]
    [InlineData("wg0", null, true)]
    [InlineData("docker0", null, true)]
    [InlineData("veth3f2a1b", null, true)]
    [InlineData("br-1a2b3c", null, true)]
    [InlineData("tailscale0", null, true)]
    [InlineData("utun3", null, true)]
    [InlineData("awdl0", null, true)]
    [InlineData("Bluetooth-netværksforbindelse", "Bluetooth Device (Personal Area Network)", true)]
    [InlineData("Ethernet", "Microsoft Hyper-V Network Adapter", false)]   // netkortet inde i en VM
    [InlineData("Ethernet", "Realtek USB GbE Family Controller", false)]
    [InlineData("eth0", null, false)]
    [InlineData("enp3s0", null, false)]
    [InlineData("wlan0", null, false)]
    [InlineData("en0", null, false)]
    public void Virtual_adapters_by_name(string name, string? desc, bool virt) => Assert.Equal(virt, NetParse.IsVirtualName(name, desc));

    [Fact]
    public void Platform_answer_wins_over_name()
    {
        Assert.False(NetParse.IsVirtual(new NicSample { Name = "vEthernet (x)", Hardware = true }));
        Assert.True(NetParse.IsVirtual(new NicSample { Name = "Ethernet 2", Hardware = false }));
        Assert.True(NetParse.IsVirtual(new NicSample { Name = "lo", Type = NetworkInterfaceType.Loopback, Hardware = true }));
    }

    [Fact]
    public void Finish_marks_primary_sorts_and_caps_virtual()
    {
        var list = new List<NetAdapter>
        {
            NetParse.Adapter(new NicSample { Name = "Wi-Fi", Type = NetworkInterfaceType.Wireless80211, Oper = OperationalStatus.Up, Ipv4 = ["192.0.2.20"], Gateways = ["192.0.2.1"] }),
            NetParse.Adapter(Eth()),
            NetParse.Adapter(new NicSample { Name = "Ethernet 2", Type = NetworkInterfaceType.Ethernet, Oper = OperationalStatus.Down }),
        };
        for (var i = 0; i < 20; i++) list.Add(NetParse.Adapter(new NicSample { Name = $"veth{i}", Type = NetworkInterfaceType.Ethernet, Oper = OperationalStatus.Up }));
        var net = NetParse.Finish(list, null, false, Platforms.Windows);
        Assert.Equal("Ethernet", net.Adapters[0].Name);   // kablet med gateway foretrækkes
        Assert.True(net.Adapters[0].Primary);
        Assert.Equal("Wi-Fi", net.Adapters[1].Name);
        Assert.Equal("Ethernet 2", net.Adapters[2].Name);
        Assert.Equal(20, net.VirtualCount);
        Assert.Equal(3 + NetParse.MaxVirtual, net.Adapters.Count);
        Assert.Single(net.Adapters, a => a.Primary);

        var byRoute = NetParse.Finish(list.Select(Clone), "Wi-Fi", false, Platforms.Windows);
        Assert.Equal("Wi-Fi", byRoute.Adapters[0].Name);   // styresystemets standardrute vinder
    }

    [Fact]
    public void Finish_uses_virtual_route_only_when_nothing_physical_is_up()
    {
        var vpn = NetParse.Adapter(new NicSample { Name = "wg0", Type = NetworkInterfaceType.Unknown, Oper = OperationalStatus.Up, Ipv4 = ["192.168.1.2"], Gateways = ["192.168.1.1"] });
        var net = NetParse.Finish([vpn], "wg0", null, Platforms.Linux);
        Assert.True(net.Adapters[0].Primary);
        Assert.Equal(1, net.VirtualCount);
    }

    private static NetAdapter Clone(NetAdapter a) => new()
    {
        Name = a.Name, Kind = a.Kind, Status = a.Status, Virtual = a.Virtual, Gateways = a.Gateways, Ipv4 = a.Ipv4, SpeedMbps = a.SpeedMbps,
    };

    // ------------------------------------------------------------------ netsh

    private const string NetshEn = """

        There is 2 interfaces on the system:

            Name                   : Wi-Fi
            Description            : Intel(R) Wi-Fi 6E AX211 160MHz
            GUID                   : 3f2b1c4d-1111-4222-8333-944455566677
            Physical address       : 8c:17:59:aa:bb:cc
            Interface type         : Primary
            State                  : connected
            SSID                   : TestNet
            AP BSSID               : 02:11:22:33:44:55
            Band                   : 5 GHz
            Channel                : 44
            Network type           : Infrastructure
            Radio type             : 802.11ax
            Authentication         : WPA3-Personal
            Cipher                 : CCMP
            Connection mode        : Auto Connect
            Receive rate (Mbps)    : 1201
            Transmit rate (Mbps)   : 960
            Signal                 : 84%
            Rssi                   : -56
            Profile                : TestNet

            QoS MSCS Configured         : 0
            QoS Map Configured          : 0
            QoS Map Allowed by Policy   : 0

            Name                   : Wi-Fi 2
            Description            : TP-Link Wireless USB Adapter
            GUID                   : 3f2b1c4d-1111-4222-8333-944455566688
            Physical address       : 50:3e:aa:00:11:22
            State                  : disconnected
            Radio status           : Hardware On
                                     Software On

            Hosted network status  : Not available
        """;

    [Fact]
    public void Netsh_english()
    {
        var r = NetParse.Netsh(NetshEn.Replace("\n", "\r\n"));
        Assert.Equal(2, r.Count);
        var w = r[0];
        Assert.Equal("Wi-Fi", w.Name);
        Assert.Equal("Intel(R) Wi-Fi 6E AX211 160MHz", w.Description);
        Assert.Equal("3f2b1c4d-1111-4222-8333-944455566677", w.Guid);
        Assert.Equal("8C-17-59-AA-BB-CC", w.Mac);
        Assert.True(w.Connected);
        Assert.Equal("TestNet", w.Link.Ssid);
        Assert.Equal("02-11-22-33-44-55", w.Link.Bssid);
        Assert.Equal(84, w.Link.SignalPct);
        Assert.Equal(-56, w.Link.Dbm);
        Assert.Equal("5 GHz", w.Link.Band);
        Assert.Equal(44, w.Link.Channel);
        Assert.Equal(5220, w.Link.FrequencyMhz);
        Assert.Equal(1201, w.Link.RxMbps);
        Assert.Equal(960, w.Link.TxMbps);
        Assert.Equal("802.11ax", w.Link.Radio);
        Assert.Equal("WPA3-Personal", w.Link.Security);
        Assert.False(r[1].Connected);
    }

    private const string NetshDa = """
        Der er 1 grænseflade på systemet:

            Navn                   : Wi-Fi
            Beskrivelse            : Realtek RTL8852BE WiFi 6 802.11ax PCIe Adapter
            GUID                   : 0a1b2c3d-aaaa-4bbb-8ccc-0d0e0f101112
            Fysisk adresse         : 10:68:38:01:02:03
            Grænsefladetype        : Primær
            Tilstand               : tilsluttet
            SSID                   : Hjemme Netværk
            AP BSSID               : 0a:11:22:33:44:66
            Bånd                   : 2,4 GHz
            Kanal                  : 6
            Netværkstype           : Infrastruktur
            Radiotype              : 802.11n
            Godkendelse            : WPA2-Personal
            Kryptering             : CCMP
            Forbindelsestilstand   : Opret automatisk forbindelse
            Modtagelseshastighed (Mbps)  : 144,4
            Afsendelseshastighed (Mbps)  : 72
            Signal                 : 35%
            Profil                 : Hjemme Netværk

            Status for hostet netværk  : Ikke tilgængelig
        """;

    [Fact]
    public void Netsh_danish()
    {
        var w = Assert.Single(NetParse.Netsh(NetshDa));
        Assert.Equal("Wi-Fi", w.Name);
        Assert.True(w.Connected);
        Assert.Equal("Hjemme Netværk", w.Link.Ssid);
        Assert.Equal(35, w.Link.SignalPct);
        Assert.Equal(-83, w.Link.Dbm);   // uden Rssi: regnet fra procent
        Assert.Equal("2,4 GHz", w.Link.Band);
        Assert.Equal(6, w.Link.Channel);
        Assert.Equal(2437, w.Link.FrequencyMhz);
        Assert.Equal(144.4, w.Link.RxMbps);
        Assert.Equal(72, w.Link.TxMbps);
        Assert.Equal("WPA2-Personal", w.Link.Security);
        Assert.Equal("802.11n", w.Link.Radio);
    }

    [Theory]
    [InlineData("connected", true)] [InlineData("disconnected", false)] [InlineData("tilsluttet", true)] [InlineData("forbundet", true)]
    [InlineData("afbrudt", false)] [InlineData("ikke tilsluttet", false)] [InlineData("verbunden", true)] [InlineData("getrennt", false)]
    [InlineData("connecté", true)] [InlineData("déconnecté", false)] [InlineData("authenticating", null)]
    public void Netsh_state_in_several_languages(string v, bool? connected) => Assert.Equal(connected, NetParse.WlanConnected(v));

    [Fact]
    public void Netsh_location_permission_and_garbage()
    {
        const string loc = "Network shell commands need location permission to access WLAN information. Turn on Location services on the Location page in Privacy & security settings.\r\n\r\nHere is the URI for the Location page in the Settings app:\r\nms-settings:privacy-location";
        Assert.True(NetParse.NetshNeedsLocation(loc));
        Assert.Empty(NetParse.Netsh(loc));
        Assert.Empty(NetParse.Netsh("The Wireless AutoConfig Service (wlansvc) is not running."));
        Assert.Empty(NetParse.Netsh(null));
        Assert.False(NetParse.NetshNeedsLocation(NetshEn));
    }

    [Theory]
    [InlineData("WPA2-Personal", "WPA2-Personal")]
    [InlineData("WPA3-Personal", "WPA3-Personal")]
    [InlineData("WPA2-Enterprise", "WPA2-Enterprise")]
    [InlineData("WPA2 WPA3", "WPA2/WPA3-Personal")]
    [InlineData("WPA3 Personal Transition", "WPA2/WPA3-Personal")]
    [InlineData("WPA2 Personal", "WPA2-Personal")]
    [InlineData("WPA2 802.1X", "WPA2-Enterprise")]
    [InlineData("Open", "Åben")]
    [InlineData("Åben", "Åben")]
    [InlineData("WEP", "WEP")]
    public void Security_text(string raw, string text) => Assert.Equal(text, NetParse.SecurityText(raw));

    [Theory]
    [InlineData(2412, "2,4 GHz", 1)] [InlineData(2484, "2,4 GHz", 14)] [InlineData(5180, "5 GHz", 36)] [InlineData(5745, "5 GHz", 149)] [InlineData(5975, "6 GHz", 5)]
    public void Band_and_channel_from_frequency(int mhz, string band, int ch)
    {
        Assert.Equal(band, NetParse.BandFromFreq(mhz));
        Assert.Equal(ch, NetParse.ChannelFromFreq(mhz));
        Assert.Equal(mhz, NetParse.FreqFromChannel(ch, band));
    }

    [Fact]
    public void Signal_percent_and_dbm()
    {
        Assert.Equal(100, NetParse.PercentFromDbm(-45));
        Assert.Equal(60, NetParse.PercentFromDbm(-70));
        Assert.Equal(0, NetParse.PercentFromDbm(-105));
        Assert.Equal(-80, NetParse.DbmFromPercent(40));
    }

    // ------------------------------------------------------------------ Linux

    [Fact]
    public void Sysfs_ethernet_at_100_half_duplex()
    {
        var s = NetParse.WithSysfs(new NicSample { Name = "eth0", Oper = OperationalStatus.Up, Ipv4 = ["192.0.2.30"] },
            new NetParse.SysfsNic("eth0", "up", "1", "100", "half", "1", "0x1003", "1500", false, true, null, "e1000e"));
        var a = NetParse.Adapter(s);
        Assert.Equal(NetKinds.Ethernet, a.Kind);
        Assert.Equal(NetStates.Connected, a.Status);
        Assert.Equal(100, a.SpeedMbps);
        Assert.False(a.FullDuplex);
        Assert.Equal(1000, a.MaxSpeedMbps);   // e1000e kan gigabit
        Assert.False(a.Virtual);
    }

    [Fact]
    public void Sysfs_down_wifi_bridge_and_admin_down()
    {
        var wl = NetParse.Adapter(NetParse.WithSysfs(new NicSample { Name = "wlan0" },
            new NetParse.SysfsNic("wlan0", "dormant", null, null, null, "1", "0x1003", "1500", true, true, "wlan", "brcmfmac")));
        Assert.Equal(NetKinds.Wifi, wl.Kind);
        Assert.Equal(NetStates.Disconnected, wl.Status);
        var unplugged = NetParse.Adapter(NetParse.WithSysfs(new NicSample { Name = "enp3s0", Type = NetworkInterfaceType.Ethernet },
            new NetParse.SysfsNic("enp3s0", "down", "0", "-1", "unknown", "1", "0x1003", "1500", false, true, null, "r8169")));
        Assert.Equal(NetStates.Unplugged, unplugged.Status);
        Assert.Null(unplugged.SpeedMbps);
        var off = NetParse.Adapter(NetParse.WithSysfs(new NicSample { Name = "eth1" },
            new NetParse.SysfsNic("eth1", "down", null, null, null, "1", "0x1002", "1500", false, true, null, null)));
        Assert.Equal(NetStates.Disabled, off.Status);
        var br = NetParse.Adapter(NetParse.WithSysfs(new NicSample { Name = "lanbr", Oper = OperationalStatus.Up },
            new NetParse.SysfsNic("lanbr", "up", "1", null, null, "1", "0x1003", "1500", false, false, "bridge", null)));
        Assert.True(br.Virtual);
        var lo = NetParse.Adapter(NetParse.WithSysfs(new NicSample { Name = "lo" },
            new NetParse.SysfsNic("lo", "unknown", "1", null, null, "772", "0x9", "65536", false, false, null, null)));
        Assert.True(lo.Virtual);
    }

    [Fact]
    public void Linux_default_route_lowest_metric()
    {
        const string route = "Iface\tDestination\tGateway \tFlags\tRefCnt\tUse\tMetric\tMask\t\tMTU\tWindow\tIRTT\n"
            + "wlan0\t00000000\t0102000A\t0003\t0\t0\t600\t00000000\t0\t0\t0\n"
            + "eth0\t00000000\t010200C0\t0003\t0\t0\t100\t00000000\t0\t0\t0\n"
            + "eth0\t000200C0\t00000000\t0001\t0\t0\t100\t00FFFFFF\t0\t0\t0\n";
        Assert.Equal("eth0", NetParse.LinuxDefaultRoute(route));
        Assert.Null(NetParse.LinuxDefaultRoute("Iface\tDestination\n"));
    }

    [Fact]
    public void Linux_dhcp_vlan_and_ethtool()
    {
        const string ip = """
            1: lo    inet 127.0.0.1/8 scope host lo\       valid_lft forever preferred_lft forever
            2: eth0    inet 192.0.2.30/24 brd 192.0.2.255 scope global dynamic noprefixroute eth0\       valid_lft 85000sec preferred_lft 85000sec
            3: eth0.20@eth0    inet 198.51.100.4/24 brd 198.51.100.255 scope global eth0.20\       valid_lft forever preferred_lft forever
            """;
        var d = NetParse.LinuxDhcp(ip);
        Assert.True(d["eth0"]);
        Assert.False(d["eth0.20"]);
        Assert.False(d.ContainsKey("lo"));
        Assert.Equal(20, NetParse.LinuxVlans("VLAN Dev name    | VLAN ID\nName-Type: VLAN_NAME_TYPE_RAW_PLUS_VID_NO_PAD\neth0.20        | 20  | eth0\n")["eth0.20"]);
        const string eth = """
            Settings for eth0:
            	Supported ports: [ TP ]
            	Supported link modes:   10baseT/Half 10baseT/Full
            	                        100baseT/Half 100baseT/Full
            	                        1000baseT/Full
            	Supported pause frame use: No
            	Advertised link modes:  10baseT/Half 10baseT/Full
            	Speed: 100Mb/s
            	Duplex: Full
            """;
        Assert.Equal(1000, NetParse.EthtoolMax(eth));
        Assert.Null(NetParse.EthtoolMax("Settings for wlan0:\n\tLink detected: yes"));
    }

    [Fact]
    public void Iw_link_connected_and_not()
    {
        const string iw = """
            Connected to 02:11:22:33:44:55 (on wlan0)
            	SSID: TestNet
            	freq: 5180.0
            	RX: 123456 bytes (789 packets)
            	TX: 23456 bytes (123 packets)
            	signal: -67 dBm
            	rx bitrate: 433.3 MBit/s VHT-MCS 9 80MHz short GI VHT-NSS 1
            	tx bitrate: 390.0 MBit/s VHT-MCS 8 80MHz short GI VHT-NSS 1
            """;
        var w = NetParse.IwLink(iw)!;
        Assert.Equal("TestNet", w.Ssid);
        Assert.Equal("02-11-22-33-44-55", w.Bssid);
        Assert.Equal(-67, w.Dbm);
        Assert.Equal(66, w.SignalPct);
        Assert.Equal("5 GHz", w.Band);
        Assert.Equal(36, w.Channel);
        Assert.Equal(5180, w.FrequencyMhz);
        Assert.Equal(433.3, w.RxMbps);
        Assert.Equal(390.0, w.TxMbps);
        Assert.Equal("802.11ac", w.Radio);
        Assert.Null(NetParse.IwLink("Not connected."));
    }

    [Fact]
    public void Nmcli_active_row_with_escaped_colons()
    {
        const string nm = "no:wlan0:40:2437 MHz:6:130 Mbit/s:0A\\:11\\:22\\:33\\:44\\:77:WPA2:Naboen\n"
            + "yes:wlan0:72:5180 MHz:36:540 Mbit/s:02\\:11\\:22\\:33\\:44\\:55:WPA2 WPA3:Net\\:Hjem\n";
        var w = NetParse.Nmcli(nm)["wlan0"];
        Assert.Equal("Net:Hjem", w.Ssid);
        Assert.Equal("02-11-22-33-44-55", w.Bssid);
        Assert.Equal(72, w.SignalPct);
        Assert.Equal(36, w.Channel);
        Assert.Equal("5 GHz", w.Band);
        Assert.Equal(540, w.RxMbps);
        Assert.Equal("WPA2/WPA3-Personal", w.Security);
    }

    // ------------------------------------------------------------------ Mac

    private const string Ports = """

        Hardware Port: Ethernet
        Device: en0
        Ethernet Address: 3c:22:fb:00:00:01

        Hardware Port: Wi-Fi
        Device: en1
        Ethernet Address: 3c:22:fb:00:00:02

        Hardware Port: USB 10/100/1000 LAN
        Device: en7
        Ethernet Address: 00:e0:4c:00:00:03

        Hardware Port: Thunderbolt Bridge
        Device: bridge0
        Ethernet Address: 36:00:00:00:00:04

        VLAN Configurations
        ===================
        """;

    private const string Ifconfig = """
        lo0: flags=8049<UP,LOOPBACK,RUNNING,MULTICAST> mtu 16384
        	inet 127.0.0.1 netmask 0xff000000
        en0: flags=8863<UP,BROADCAST,SMART,RUNNING,SIMPLEX,MULTICAST> mtu 1500
        	ether 3c:22:fb:00:00:01
        	media: autoselect (none)
        	status: inactive
        	supported media:
        		media autoselect
        		media 10baseT/UTP mediaopt half-duplex
        		media 100baseTX mediaopt full-duplex
        		media 1000baseT mediaopt full-duplex
        		media 10GbaseT mediaopt full-duplex
        en1: flags=8863<UP,BROADCAST,SMART,RUNNING,SIMPLEX,MULTICAST> mtu 1500
        	ether 3c:22:fb:00:00:02
        	inet 192.0.2.40 netmask 0xffffff00 broadcast 192.0.2.255
        	media: autoselect
        	status: active
        	supported media:
        		media autoselect
        en7: flags=8863<UP,BROADCAST,SMART,RUNNING,SIMPLEX,MULTICAST> mtu 1500
        	ether 00:e0:4c:00:00:03
        	media: autoselect (100baseTX <full-duplex,flow-control>)
        	status: active
        	supported media:
        		media autoselect
        		media 1000baseT mediaopt full-duplex
        		media 100baseTX mediaopt full-duplex
        vlan0: flags=8842<BROADCAST,RUNNING,SIMPLEX,MULTICAST> mtu 1496
        	vlan: 30 parent interface: en7
        	status: inactive
        """;

    [Fact]
    public void Mac_ports_and_ifconfig()
    {
        var p = NetParse.MacPorts(Ports);
        Assert.Equal("Wi-Fi", p["en1"]);
        Assert.Equal("USB 10/100/1000 LAN", p["en7"]);
        Assert.Equal("Thunderbolt Bridge", p["bridge0"]);
        var f = NetParse.MacIfconfig(Ifconfig);
        Assert.False(f["en0"].Active);
        Assert.Equal(10000, f["en0"].MaxSpeedMbps);
        Assert.True(f["en1"].Active);
        Assert.Null(f["en1"].SpeedMbps);
        Assert.Equal(100, f["en7"].SpeedMbps);
        Assert.True(f["en7"].FullDuplex);
        Assert.Equal(1000, f["en7"].MaxSpeedMbps);
        Assert.Equal(30, f["vlan0"].VlanId);
        Assert.False(f["vlan0"].Up);
        Assert.Equal(1496, f["vlan0"].Mtu);
    }

    [Theory]
    [InlineData("1000baseT", 1000L)] [InlineData("100baseTX", 100L)] [InlineData("10baseT/UTP", 10L)] [InlineData("10GbaseT", 10000L)]
    [InlineData("2500Base-T", 2500L)] [InlineData("autoselect", null)]
    public void Mac_media_speed(string media, long? mbps) => Assert.Equal(mbps, NetParse.MediaSpeed(media));

    [Fact]
    public void Mac_airport_and_route()
    {
        const string sp = """
            Wi-Fi:

                  Software Versions:
                      CoreWLAN: 16.0 (1657)
                  Interfaces:
                    en1:
                      Card Type: Wi-Fi  (0x14E4, 0x4387)
                      Status: Connected
                      Current Network Information:
                        <redacted>:
                          PHY Mode: 802.11ax
                          Channel: 149 (5GHz, 80MHz)
                          Country Code: DK
                          Network Type: Infrastructure
                          Security: WPA2 Personal
                          Signal / Noise: -61 dBm / -92 dBm
                          Transmit Rate: 864
                          MCS Index: 9
                      Other Local Wi-Fi Networks:
                        Naboen:
                          PHY Mode: 802.11n
            """;
        var w = NetParse.MacAirport(sp)!;
        Assert.Null(w.Ssid);   // macOS skjuler SSID uden placeringsadgang
        Assert.Equal("802.11ax", w.Radio);
        Assert.Equal(149, w.Channel);
        Assert.Equal("5 GHz", w.Band);
        Assert.Equal(5745, w.FrequencyMhz);
        Assert.Equal(-61, w.Dbm);
        Assert.Equal(78, w.SignalPct);
        Assert.Equal(864, w.TxMbps);
        Assert.Equal("WPA2-Personal", w.Security);
        Assert.Null(NetParse.MacAirport("Wi-Fi:\n  Interfaces:\n    en1:\n      Status: Off\n"));
        Assert.Equal("en7", NetParse.MacDefaultRoute("   route to: default\ndestination: default\n       mask: default\n    gateway: 192.0.2.1\n  interface: en7\n"));
    }

    [Theory]
    [InlineData("Intel(R) Ethernet Connection (7) I219-V", 1000L)]
    [InlineData("Realtek PCIe GbE Family Controller", 1000L)]
    [InlineData("Intel(R) Ethernet Controller I225-V", 2500L)]
    [InlineData("Realtek Gaming 2.5GbE Family Controller", 2500L)]
    [InlineData("Realtek PCIe FE Family Controller", null)]
    [InlineData("Realtek RTL8139 Fast Ethernet", 100L)]
    [InlineData("bcmgenet", 1000L)]
    [InlineData("smsc95xx", 100L)]
    [InlineData("r8169", null)]   // både 100 Mbit og gigabit – gæt ikke
    public void Max_speed_from_known_chip(string text, long? max) => Assert.Equal(max, NetParse.MaxFromModel(text));

    // ------------------------------------------------------------------ målinger mellem to rapporter

    [Fact]
    public void Tracker_rates_errors_and_reset()
    {
        var t = new NetTracker();
        var t0 = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
        var a = NetParse.Adapter(Eth());
        t.Apply([a], new Dictionary<string, NetCounters> { ["Ethernet"] = new(1000, 500, 100, 50, 0, 0, 3, 0) }, t0);
        Assert.Null(a.RxRate);   // første måling: intet "nu"
        Assert.Null(a.LinkChanges24h);
        var b = NetParse.Adapter(Eth());
        t.Apply([b], new Dictionary<string, NetCounters> { ["Ethernet"] = new(61000, 3500, 10100, 5050, 80, 0, 3, 0) }, t0.AddMinutes(1));
        Assert.Equal(1000, b.RxRate);
        Assert.Equal(50, b.TxRate);
        Assert.Equal(80, b.NewErrors);
        Assert.Equal(15000, b.NewPackets);
        Assert.Equal(60, b.SampleSeconds);
        Assert.Equal(0, b.LinkChanges24h);
        var c = NetParse.Adapter(Eth());   // tælleren nulstillet (driveren genstartet): ingen tal frem for forkerte
        t.Apply([c], new Dictionary<string, NetCounters> { ["Ethernet"] = new(10, 10, 1, 1, 0, 0, 0, 0) }, t0.AddMinutes(2));
        Assert.Null(c.RxRate);
        Assert.Null(c.NewErrors);
    }

    [Fact]
    public void Tracker_counts_link_flaps_in_24_hours()
    {
        var t = new NetTracker();
        var t0 = new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);
        NetAdapter Run(OperationalStatus st, DateTime at, long? carrier = null)
        {
            var a = NetParse.Adapter(Eth(oper: st));
            t.Apply([a], carrier is null ? new Dictionary<string, NetCounters>() : new() { ["Ethernet"] = new(0, 0, 0, 0, 0, 0, 0, 0, carrier) }, at);
            return a;
        }
        Run(OperationalStatus.Up, t0);
        Run(OperationalStatus.Down, t0.AddMinutes(5));
        var up = Run(OperationalStatus.Up, t0.AddMinutes(10));
        Assert.Equal(2, up.LinkChanges24h);
        Assert.Equal(t0.AddMinutes(10), up.LinkChangedAt);
        Assert.Equal(0, Run(OperationalStatus.Up, t0.AddHours(25)).LinkChanges24h);   // ældre end et døgn tæller ikke

        var lx = new NetTracker();
        var a1 = NetParse.Adapter(Eth());
        lx.Apply([a1], new Dictionary<string, NetCounters> { ["Ethernet"] = new(0, 0, 0, 0, 0, 0, 0, 0, 4) }, t0);
        var a2 = NetParse.Adapter(Eth());   // carrier_changes ser også skift mellem målingerne
        lx.Apply([a2], new Dictionary<string, NetCounters> { ["Ethernet"] = new(0, 0, 0, 0, 0, 0, 0, 0, 16) }, t0.AddMinutes(5));
        Assert.Equal(12, a2.LinkChanges24h);
    }

    // ------------------------------------------------------------------ helbredsreglerne

    private static NetworkInfo Net(NetAdapter a, bool? portable = false)
    {
        a.Primary = true;
        return new NetworkInfo { Adapters = [a], Portable = portable };
    }

    private static NetAdapter Wired(long speed, long? max) => new()
    {
        Name = "Ethernet", Description = "Intel(R) Ethernet Connection I219-V", Kind = NetKinds.Ethernet, Status = NetStates.Connected, SpeedMbps = speed, MaxSpeedMbps = max,
    };

    private static NetAdapter Wireless(int pct) => new()
    {
        Name = "Wi-Fi", Kind = NetKinds.Wifi, Status = NetStates.Connected, Wifi = new WifiLink { Ssid = "TestNet", SignalPct = pct, Dbm = NetParse.DbmFromPercent(pct) },
    };

    [Fact]
    public void Slow_wired_link_warns_only_when_card_can_more()
    {
        var f = Assert.Single(HealthService.NetworkFindings(Net(Wired(100, 1000))));
        Assert.Equal("net:speed", f.Key);
        Assert.Equal("warning", f.Severity);
        Assert.Contains("100 Mbit/s", f.Title);
        Assert.Contains("1 Gbit/s", f.Detail);
        Assert.Empty(HealthService.NetworkFindings(Net(Wired(100, null))));   // ukendt evne: ingen alarm
        Assert.Empty(HealthService.NetworkFindings(Net(Wired(100, 100))));    // kortet kan ikke mere
        Assert.Empty(HealthService.NetworkFindings(Net(Wired(1000, 2500))));  // gigabit er nok
        var notPrimary = Net(Wired(100, 1000)); notPrimary.Adapters[0].Primary = false;
        Assert.Empty(HealthService.NetworkFindings(notPrimary));
        var unplugged = Wired(100, 1000); unplugged.Status = NetStates.Unplugged;
        Assert.Empty(HealthService.NetworkFindings(Net(unplugged)));
    }

    [Fact]
    public void Weak_wifi_warns_only_on_a_stationary_machine()
    {
        Assert.Equal("net:wifi", Assert.Single(HealthService.NetworkFindings(Net(Wireless(35), portable: false))).Key);
        Assert.Empty(HealthService.NetworkFindings(Net(Wireless(35), portable: true)));
        Assert.Empty(HealthService.NetworkFindings(Net(Wireless(35), portable: null)));   // ukendt (fx Raspberry Pi)
        Assert.Empty(HealthService.NetworkFindings(Net(Wireless(40), portable: false)));
        Assert.Empty(HealthService.NetworkFindings(null));
    }

    [Fact]
    public void Error_burst_and_link_flaps()
    {
        var a = Wired(1000, 1000); a.NewErrors = 80; a.NewPackets = 15000; a.SampleSeconds = 300;
        Assert.Equal("net:errors:Ethernet", Assert.Single(HealthService.NetworkFindings(Net(a))).Key);
        a.NewErrors = 40;   // under grænsen
        Assert.Empty(HealthService.NetworkFindings(Net(a)));
        a.NewErrors = 60; a.NewPackets = 10_000_000;   // under 1 promille af trafikken
        Assert.Empty(HealthService.NetworkFindings(Net(a)));

        var f = Wired(1000, 1000); f.LinkChanges24h = NetHealth.FlapsPerDay;
        Assert.Equal("net:flaps:Ethernet", Assert.Single(HealthService.NetworkFindings(Net(f))).Key);
        f.LinkChanges24h = NetHealth.FlapsPerDay - 1;
        Assert.Empty(HealthService.NetworkFindings(Net(f)));
        f.LinkChanges24h = 30;   // bærbar i dockingstation: ingen alarm
        Assert.Empty(HealthService.NetworkFindings(Net(f, portable: true)));
    }

    [Fact]
    public void Brief_for_the_machine_list()
    {
        var b = NetParse.Brief(Net(Wired(100, 1000)))!;
        Assert.Equal(NetKinds.Ethernet, b.Kind);
        Assert.Equal(100, b.SpeedMbps);
        Assert.True(b.Warn);
        Assert.False(NetParse.Brief(Net(Wireless(72)))!.Warn);
        Assert.Equal(72, NetParse.Brief(Net(Wireless(72)))!.SignalPct);
        Assert.Null(NetParse.Brief(new NetworkInfo()));
    }

    [Fact]
    public void Snapshot_of_this_machine_never_throws()
    {
        var all = NetworkInterface.GetAllNetworkInterfaces().Select(NetParse.Snapshot).Select(NetParse.Adapter).ToList();
        var net = NetParse.Finish(all, null, null, Platforms.Current);
        Assert.All(net.Adapters.Where(a => a.Name is "lo" or "lo0"), a => Assert.True(a.Virtual));
    }

    [Fact]
    public void Speed_text_in_danish()
    {
        Assert.Equal("100 Mbit/s", NetHealth.SpeedText(100));
        Assert.Equal("1 Gbit/s", NetHealth.SpeedText(1000));
        Assert.Equal("2,5 Gbit/s", NetHealth.SpeedText(2500));
    }
}
