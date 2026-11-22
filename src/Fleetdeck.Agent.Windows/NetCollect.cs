using System.Diagnostics;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Windows;

/// <summary>
/// Netværksadaptere: .NET (adresser, gateway, DNS, DHCP, tællere), WMI MSFT_NetAdapter (slået fra, kabel, duplex, VLAN, rigtig
/// hardware eller virtuel) og "netsh wlan show interfaces" (SSID, signal, bånd, kanal, sikkerhed). Samles med helbredet;
/// en fejl giver kun en fejltekst og vælter aldrig agenten.
/// </summary>
public static class NetCollect
{
    private static readonly NetTracker Tracker = new();
    private static bool? portable;
    private static bool portableRead;

    [DllImport("iphlpapi.dll")] private static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);

    public static NetworkInfo Collect()
    {
        try
        {
            var dotnet = NetworkInterface.GetAllNetworkInterfaces().Select(n => (Sample: NetParse.Snapshot(n), Index: V4Index(n))).ToList();
            var byGuid = dotnet.Where(d => d.Sample.Id is not null).GroupBy(d => Guid(d.Sample.Id!)).ToDictionary(g => g.Key, g => g.First().Sample);
            var samples = new List<NicSample>();
            var seen = new HashSet<string>();
            foreach (var w in Collectors.Wmi(@"root\StandardCimv2", "SELECT * FROM MSFT_NetAdapter"))
                using (w)
                {
                    var guid = Guid(Collectors.Prop<string>(w, "InterfaceGuid") ?? "");
                    if (guid.Length == 0 || !seen.Add(guid)) continue;
                    var name = Collectors.Prop<string>(w, "Name") ?? "";
                    var desc = Collectors.Prop<string>(w, "InterfaceDescription");
                    var medium = Collectors.Prop<uint?>(w, "NdisPhysicalMedium");
                    var media = Collectors.Prop<uint?>(w, "MediaConnectState");
                    var state = Collectors.Prop<uint?>(w, "State");
                    var hw = Collectors.Prop<bool?>(w, "HardwareInterface");
                    var virt = Collectors.Prop<bool?>(w, "Virtual");
                    var baseSample = byGuid.GetValueOrDefault(guid) ?? new NicSample
                    {
                        Name = name, Id = guid, Mac = Collectors.Prop<string>(w, "PermanentAddress"),
                        Type = medium == 9 ? NetworkInterfaceType.Wireless80211 : NetworkInterfaceType.Ethernet,
                    };
                    samples.Add(baseSample with
                    {
                        Name = name.Length > 0 ? name : baseSample.Name, Description = desc ?? baseSample.Description,
                        AdminUp = state == 3 ? false : baseSample.Oper is not null || state == 2 ? true : null,
                        Carrier = state == 3 ? null : media switch { 1 => true, 2 => false, _ => null },
                        Hardware = hw is bool h ? h && virt != true : null,
                        Wireless = medium is 1 or 9 ? true : null, Mobile = medium == 8 ? true : null,
                        FullDuplex = Collectors.Prop<bool?>(w, "FullDuplex"),
                        VlanId = Collectors.Prop<int?>(w, "VlanID"),
                        MaxSpeedMbps = NetParse.MaxFromModel(desc),
                    });
                }
            // det, WMI ikke kender (loopback, Teredo …), eller hele listen, hvis WMI ikke svarer
            foreach (var d in dotnet.Where(d => d.Sample.Id is null || !seen.Contains(Guid(d.Sample.Id))))
                samples.Add(d.Sample with { MaxSpeedMbps = NetParse.MaxFromModel(d.Sample.Description) });

            var adapters = samples.Select(NetParse.Adapter).ToList();
            string? note = null;
            if (adapters.Any(a => a.Kind == NetKinds.Wifi && !a.Virtual && a.Status is NetStates.Connected or NetStates.Limited))
                note = AddWifi(adapters, samples);
            string? primary = null;
            try
            {
                if (GetBestInterface(BitConverter.ToUInt32(IPAddress.Parse("1.1.1.1").GetAddressBytes(), 0), out var idx) == 0)
                    primary = dotnet.FirstOrDefault(d => d.Index == (int)idx).Sample?.Name;
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
            if (!portableRead) { portable = DiskCrypto.Portable(); portableRead = true; }
            var info = NetParse.Finish(adapters, primary, portable, Platforms.Windows);
            info.WifiNote = note;
            Tracker.Apply(info.Adapters, samples.Where(s => s.Counters is not null).GroupBy(s => s.Name).ToDictionary(g => g.Key, g => g.First().Counters!), DateTime.UtcNow);
            return info;
        }
        catch (Exception e) when (e is NetworkInformationException or ManagementException or COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new NetworkInfo { Source = Platforms.Windows, Error = $"Netværksadapterne kunne ikke læses ({e.GetType().Name})." };
        }
    }

    private static string Guid(string id) => id.Trim('{', '}').ToLowerInvariant();

    private static int? V4Index(NetworkInterface n)
    {
        try { return n.GetIPProperties().GetIPv4Properties()?.Index; }
        catch (Exception e) when (e is NetworkInformationException or PlatformNotSupportedException or InvalidOperationException) { return null; }
    }

    /// <summary>SSID, signal, bånd, kanal og sikkerhed fra netsh. Returnerer en note, hvis Windows ikke giver adgang.</summary>
    private static string? AddWifi(List<NetAdapter> adapters, List<NicSample> samples)
    {
        var text = Run("cmd.exe", "/d /c chcp 65001 >nul & netsh wlan show interfaces");
        if (NetParse.NetshNeedsLocation(text)) return "Windows viser kun Wi-Fi-detaljer med placeringstjenester slået til.";
        foreach (var w in NetParse.Netsh(text))
        {
            var s = samples.FirstOrDefault(x => w.Guid is not null && x.Id is not null && Guid(x.Id) == w.Guid);
            var a = adapters.FirstOrDefault(x => s is not null && x.Name == s.Name)
                ?? adapters.FirstOrDefault(x => x.Name == w.Name)
                ?? adapters.FirstOrDefault(x => w.Mac is not null && x.Mac == w.Mac);
            if (a is null || w.Connected == false) continue;
            a.Wifi = w.Link;
        }
        return null;
    }

    private static string Run(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            })!;
            var o = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(15000)) { try { p.Kill(true); } catch (InvalidOperationException) { } return ""; }
            return o.Result;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return ""; }
    }
}
