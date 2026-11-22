using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Windows;

/// <summary>Indsamler fakta, helbred og software fra Windows (WMI, registreringsdatabase, hændelseslog, COM).</summary>
public sealed partial class Collectors(Runner runner, ILogger<Collectors> log)
{
    internal static List<ManagementObject> Wmi(string scope, string query)
    {
        try
        {
            using var s = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(query));
            return s.Get().Cast<ManagementObject>().ToList();
        }
        catch (Exception) { return []; }
    }
    internal static T? Prop<T>(ManagementBaseObject o, string name)
    {
        try { var v = o[name]; return v is null ? default : (T)Convert.ChangeType(v, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), CultureInfo.InvariantCulture); }
        catch (Exception) { return default; }
    }
    private static string? RegString(RegistryKey root, string path, string name) => root.OpenSubKey(path)?.GetValue(name)?.ToString();

    // ---------------------------------------------------------------- løbende (hvert check-in)
    public LiveFacts Live()
    {
        var mem = Native.Memory();
        var f = new LiveFacts
        {
            Hostname = Environment.MachineName, User = Native.ConsoleUser(), Cpu = Native.CpuPercent(),
            Ram = mem.PctUsed, RamUsedGb = mem.UsedGb, RamTotalGb = mem.TotalGb,
            UptimeH = Math.Round(Environment.TickCount64 / 3_600_000.0, 1), BootAt = BootTime.FromUptime(DateTime.UtcNow, Environment.TickCount64 / 1000.0), PendingReboot = PendingReboot(), Temps = Temperatures(),
            PowerMode = PlannedPower.Current?.Mode, PowerAt = PlannedPower.Current?.At,
            UpdateError = SelfUpdate.Failures >= 3 ? SelfUpdate.LastError : null,
            Volumes = DriveInfo.GetDrives().Where(d => d is { DriveType: DriveType.Fixed, IsReady: true } && d.TotalSize > 0).Select(d => new VolumeInfo
            {
                Drive = d.Name.TrimEnd('\\'), SizeGb = Math.Round(d.TotalSize / 1073741824.0, 1), FreeGb = Math.Round(d.TotalFreeSpace / 1073741824.0, 1),
                PctUsed = Math.Round(100 - 100.0 * d.TotalFreeSpace / d.TotalSize, 1),
            }).ToList(),
        };
        var nic = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(n => (n, p: n.GetIPProperties()))
            .FirstOrDefault(x => x.p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork));
        if (nic.n is not null)
        {
            f.Ip = nic.p.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();
            f.Mac = string.Join("-", nic.n.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")));
        }
        return f;
    }

    public static bool PendingReboot() => WuRebootPending() || FileRenamesPending();

    /// <summary>
    /// Venter Windows Update (CBS eller WU) på en genstart? Kun det må spærre for Windows Update i opdateringsmotoren –
    /// PendingFileRenameOperations sættes af antivirus, drivere og programinstallationer og står ofte der hele tiden.
    /// </summary>
    public static bool WuRebootPending()
    {
        using var lm = Registry.LocalMachine;
        if (lm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending") is not null) return true;
        return lm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired") is not null;
    }

    /// <summary>Filer, der skiftes ved næste opstart (PendingFileRenameOperations).</summary>
    public static bool FileRenamesPending()
    {
        using var lm = Registry.LocalMachine;
        return lm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager")?.GetValue("PendingFileRenameOperations") is string[] { Length: > 0 };
    }

    private static readonly HttpClient Local = new() { Timeout = TimeSpan.FromSeconds(2) };

    /// <summary>Nyere LibreHardwareMonitor har ikke WMI – læs i stedet dens lokale webserver (kun 127.0.0.1).</summary>
    private static Temperatures? LhmHttp()
    {
        try
        {
            using var doc = JsonDocument.Parse(Local.GetStringAsync("http://127.0.0.1:8085/data.json").GetAwaiter().GetResult());
            var temps = new List<(string Id, string Name, double Value)>();
            void Walk(JsonElement n)
            {
                if (n.TryGetProperty("SensorId", out var sid) && sid.GetString() is { } id && id.Contains("/temperature/", StringComparison.Ordinal)
                    && n.TryGetProperty("Value", out var v) && double.TryParse(Regex.Match(v.GetString() ?? "", @"-?\d+([.,]\d+)?").Value.Replace(',', '.'),
                        NumberStyles.Float, CultureInfo.InvariantCulture, out var val))
                    temps.Add((id, n.TryGetProperty("Text", out var t) ? t.GetString() ?? "" : "", val));
                if (n.TryGetProperty("Children", out var ch) && ch.ValueKind == JsonValueKind.Array) foreach (var c in ch.EnumerateArray()) Walk(c);
            }
            Walk(doc.RootElement);
            if (temps.Count == 0) return null;
            bool Cpu(string id) => id.StartsWith("/amdcpu", StringComparison.Ordinal) || id.StartsWith("/intelcpu", StringComparison.Ordinal);
            bool Gpu(string id) => id.StartsWith("/gpu", StringComparison.Ordinal);
            var cpu = temps.Where(x => Cpu(x.Id) && Regex.IsMatch(x.Name, "Package|Tctl|Tdie", RegexOptions.IgnoreCase)).Select(x => (double?)x.Value).FirstOrDefault()
                      ?? temps.Where(x => Cpu(x.Id)).Select(x => (double?)x.Value).Max();
            var gpu = temps.Where(x => Gpu(x.Id) && Regex.IsMatch(x.Name, "Core|Edge", RegexOptions.IgnoreCase)).Select(x => (double?)x.Value).FirstOrDefault()
                      ?? temps.Where(x => Gpu(x.Id)).Select(x => (double?)x.Value).Max();
            return new Temperatures { Cpu = cpu is { } c ? Math.Round(c, 1) : null, Gpu = gpu is { } g ? Math.Round(g, 1) : null, Source = "LibreHardwareMonitor" };
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException) { return null; }
    }

    private static Temperatures Temperatures()
    {
        var t = new Temperatures();
        var sensors = Wmi(@"root\LibreHardwareMonitor", "SELECT Identifier, Name, Value FROM Sensor WHERE SensorType='Temperature'");
        if (sensors.Count > 0)
        {
            var cpu = sensors.FirstOrDefault(s => Prop<string>(s, "Identifier")!.Contains("cpu", StringComparison.OrdinalIgnoreCase)
                                                  && Regex.IsMatch(Prop<string>(s, "Name") ?? "", "Package|Tctl|Tdie"))
                      ?? sensors.Where(s => Prop<string>(s, "Identifier")!.Contains("cpu", StringComparison.OrdinalIgnoreCase)).MaxBy(s => Prop<double>(s, "Value"));
            var gpu = sensors.FirstOrDefault(s => Prop<string>(s, "Identifier")!.Contains("gpu", StringComparison.OrdinalIgnoreCase)
                                                  && Regex.IsMatch(Prop<string>(s, "Name") ?? "", "Core|Edge|GPU"));
            if (cpu is not null) t.Cpu = Math.Round(Prop<double>(cpu, "Value"), 1);
            if (gpu is not null) t.Gpu = Math.Round(Prop<double>(gpu, "Value"), 1);
            t.Source = "LibreHardwareMonitor";
            return t;
        }
        if (LhmHttp() is { } web) return web;
        var z = Wmi(@"root\wmi", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature").FirstOrDefault();
        if (z is not null) { t.Cpu = Math.Round(Prop<double>(z, "CurrentTemperature") / 10 - 273.15, 1); t.Source = "ACPI"; }
        return t;
    }

    // ---------------------------------------------------------------- system
    public SystemFacts System()
    {
        var os = Wmi(@"root\cimv2", "SELECT Caption, InstallDate, LastBootUpTime FROM Win32_OperatingSystem").FirstOrDefault();
        var cs = Wmi(@"root\cimv2", "SELECT Manufacturer, Model, TotalPhysicalMemory, Domain FROM Win32_ComputerSystem").FirstOrDefault();
        var cpu = Wmi(@"root\cimv2", "SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor").FirstOrDefault();
        var bios = Wmi(@"root\cimv2", "SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate, SystemBiosMajorVersion, SystemBiosMinorVersion FROM Win32_BIOS").FirstOrDefault();
        var csp = Wmi(@"root\cimv2", "SELECT Version FROM Win32_ComputerSystemProduct").FirstOrDefault();
        var sku = Wmi(@"root\cimv2", "SELECT SystemSKUNumber FROM Win32_ComputerSystem").FirstOrDefault();
        var board = Wmi(@"root\cimv2", "SELECT Manufacturer, Product FROM Win32_BaseBoard").FirstOrDefault();
        const string cv = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        string Date(string? cim, string fmt) => cim is null ? "" : ManagementDateTimeConverter.ToDateTime(cim).ToString(fmt, CultureInfo.InvariantCulture);
        var sbKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
        var facts = new SystemFacts
        {
            Hostname = Environment.MachineName,
            Os = os is null ? null : Prop<string>(os, "Caption"),
            OsVersion = RegString(Registry.LocalMachine, cv, "DisplayVersion"),
            Build = $"{RegString(Registry.LocalMachine, cv, "CurrentBuild")}.{RegString(Registry.LocalMachine, cv, "UBR")}",
            Edition = RegString(Registry.LocalMachine, cv, "EditionID"),
            Installed = os is null ? null : Date(Prop<string>(os, "InstallDate"), "yyyy-MM-dd"),
            LastBoot = os is null ? null : Date(Prop<string>(os, "LastBootUpTime"), "yyyy-MM-dd HH:mm"),
            Manufacturer = cs is null ? null : Prop<string>(cs, "Manufacturer"), Model = cs is null ? null : Prop<string>(cs, "Model"),
            Domain = cs is null ? null : Prop<string>(cs, "Domain"),
            RamGb = cs is null ? null : Math.Round(Prop<double>(cs, "TotalPhysicalMemory") / 1073741824.0),
            Board = board is null ? null : $"{Prop<string>(board, "Manufacturer")} {Prop<string>(board, "Product")}",
            Bios = bios is null ? null : $"{Prop<string>(bios, "SMBIOSBIOSVersion")} ({Date(Prop<string>(bios, "ReleaseDate"), "yyyy-MM-dd")})",
            CpuName = cpu is null ? null : Prop<string>(cpu, "Name")?.Trim(),
            Cores = cpu is null ? null : Prop<int>(cpu, "NumberOfCores"), Threads = cpu is null ? null : Prop<int>(cpu, "NumberOfLogicalProcessors"),
            Gpus = Wmi(@"root\cimv2", "SELECT Name FROM Win32_VideoController").Select(g => Prop<string>(g, "Name") ?? "").Where(n => n.Length > 0).ToList(),
            Nics = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211
                    && n.OperationalStatus != OperationalStatus.NotPresent && !n.Description.Contains("Kernel Debug", StringComparison.OrdinalIgnoreCase)
                    && !Regex.IsMatch(n.Description, @"Virtual|Hyper-V|Filter|Scheduler|-\d{4}$|Miniport|Loopback|Bluetooth", RegexOptions.IgnoreCase))
                .DistinctBy(n => n.GetPhysicalAddress().ToString())
                .Select(n => new NicInfo
                {
                    Name = n.Description, Status = n.OperationalStatus.ToString(),
                    Speed = n.OperationalStatus == OperationalStatus.Up ? $"{n.Speed / 1_000_000} Mbit/s" : null,
                    Mac = string.Join("-", n.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2"))),
                }).ToList(),
            TimeZone = TimeZoneInfo.Local.Id,
            Firmware = sbKey is not null ? "UEFI" : "BIOS",
            SecureBoot = sbKey?.GetValue("UEFISecureBootEnabled") is int sb ? sb == 1 : null,
            AgentBuild = typeof(Collectors).Assembly.GetName().Version?.ToString(3),
        };
        // BIOS-tjekket (BiosFacts). SystemSKUNumber findes først fra Windows 10; mangler den, er værdien bare null.
        DateTime? released = null;
        try { if (bios is not null && Prop<string>(bios, "ReleaseDate") is { Length: > 0 } rd) released = ManagementDateTimeConverter.ToDateTime(rd); }
        catch (FormatException) { }
        int? Byte(string name) { try { return bios is null ? null : Convert.ToInt32(bios[name], CultureInfo.InvariantCulture); } catch (Exception) { return null; } }
        BiosFacts.FromWmi(facts, bios is null ? null : Prop<string>(bios, "Manufacturer"), bios is null ? null : Prop<string>(bios, "SMBIOSBIOSVersion"), released,
            Byte("SystemBiosMajorVersion"), Byte("SystemBiosMinorVersion"),
            facts.Model, csp is null ? null : Prop<string>(csp, "Version"), sku is null ? null : Prop<string>(sku, "SystemSKUNumber"),
            board is null ? null : Prop<string>(board, "Manufacturer"), board is null ? null : Prop<string>(board, "Product"), facts.Manufacturer);
        // BIOS-indstillinger (kun Lenovo WMI i denne version): antal og adgangskode – værdierne læses først i et bios_settings_read-job
        if (string.Equals(facts.BiosVendor, "LENOVO", StringComparison.OrdinalIgnoreCase))
            try { facts.BiosSettings = LenovoBiosWmi.TryOpen() is { } w ? new LenovoWmiSettings(w).Info() : null; }
            catch (Exception e) when (e is ManagementException or InvalidOperationException) { log.LogDebug("BIOS-indstillinger: {Msg}", e.Message); }
        return facts;
    }

    // ---------------------------------------------------------------- helbred
    public HealthReport Health(bool includeWindowsUpdate)
    {
        var h = new HealthReport { Live = Live() };
        try
        {
            foreach (var d in Wmi(@"root\Microsoft\Windows\Storage", "SELECT * FROM MSFT_PhysicalDisk"))
            {
                try
                {
                    // SMART-tællerne findes kun via associationen fra den fysiske disk (som Get-StorageReliabilityCounter)
                    Dictionary<string, object?>? rel = null;
                    try
                    {
                        if (d.GetRelated("MSFT_StorageReliabilityCounter").Cast<ManagementObject>().FirstOrDefault() is { } r)
                            using (r)
                            {
                                rel = new(StringComparer.Ordinal);
                                foreach (var n in new[] { "Wear", "Temperature", "TemperatureMax", "ReadErrorsUncorrected", "WriteErrorsUncorrected", "PowerOnHours" })
                                    try { rel[n] = r[n]; } catch (ManagementException) { }
                            }
                    }
                    catch (ManagementException) { }
                    h.Disks.Add(DiskSmart.FromWindows(new WinDiskRaw(Prop<string>(d, "FriendlyName"), Prop<string>(d, "Model"), Prop<string>(d, "SerialNumber"),
                        Prop<int>(d, "MediaType"), Prop<int>(d, "BusType"), Prop<double>(d, "Size"), Prop<int>(d, "HealthStatus"),
                        d["OperationalStatus"] as ushort[], rel)));
                }
                catch (Exception e) when (e is ManagementException or InvalidCastException or global::System.Runtime.InteropServices.COMException) { log.LogWarning("SMART: {Message}", e.Message); }
            }
        }
        catch (Exception e) { log.LogWarning("SMART: {Message}", e.Message); }
        try { h.Batteries = BatteryCollect.Collect(); }   // bærbare (kun som SYSTEM)
        catch (Exception e) { log.LogWarning("Batteri: {Msg}", e.GetType().Name); }

        var mp = Wmi(@"root\Microsoft\Windows\Defender", "SELECT * FROM MSFT_MpComputerStatus").FirstOrDefault();
        if (mp is not null)
        {
            int? Age(string n) { var v = Prop<long?>(mp, n); return v is null or >= 65535 ? null : (int)v; }
            h.Defender = new DefenderStatus
            {
                Enabled = Prop<bool?>(mp, "AntivirusEnabled"), Realtime = Prop<bool?>(mp, "RealTimeProtectionEnabled"),
                Mode = Prop<string>(mp, "AMRunningMode"), SigAgeDays = Age("AntivirusSignatureAge"), QuickScanAgeDays = Age("QuickScanAge"),
                Tamper = Prop<bool?>(mp, "IsTamperProtected"), Version = Prop<string>(mp, "AMProductVersion"),
                ThreatsLast30Days = Wmi(@"root\Microsoft\Windows\Defender", "SELECT InitialDetectionTime FROM MSFT_MpThreatDetection")
                    .Count(t => Prop<string>(t, "InitialDetectionTime") is { } s && ManagementDateTimeConverter.ToDateTime(s) > DateTime.Now.AddDays(-30)),
            };
        }
        h.Antivirus = Wmi(@"root\SecurityCenter2", "SELECT displayName, productState FROM AntiVirusProduct").Select(a =>
        {
            var state = Prop<int>(a, "productState");
            return new AntivirusProduct { Name = Prop<string>(a, "displayName") ?? "?", Enabled = ((state >> 8) & 0x10) != 0, UpToDate = (state & 0x10) == 0 };
        }).ToList();

        const string fw = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\";
        foreach (var (name, key) in new[] { ("Domæne", "DomainProfile"), ("Privat", "StandardProfile"), ("Offentlig", "PublicProfile") })
            if (Registry.LocalMachine.OpenSubKey(fw + key)?.GetValue("EnableFirewall") is int v) h.Firewall[name] = v == 1;

        var bl = Wmi(@"root\CIMV2\Security\MicrosoftVolumeEncryption", "SELECT DriveLetter, ProtectionStatus FROM Win32_EncryptableVolume WHERE DriveLetter='C:'").FirstOrDefault();
        h.BitLocker = bl is null ? null : Prop<int>(bl, "ProtectionStatus") switch { 1 => "Til", 0 => "Fra", _ => "Ukendt" };

        h.Events = Events();
        var qfe = Wmi(@"root\cimv2", "SELECT InstalledOn FROM Win32_QuickFixEngineering")
            .Select(q => DateTime.TryParse(Prop<string>(q, "InstalledOn"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : (DateTime?)null)
            .Where(d => d is not null).Max();
        h.LastUpdate = qfe?.ToString("yyyy-MM-dd");
        if (includeWindowsUpdate) h.Updates = WindowsUpdate.Pending(false);
        return h;
    }

    private static EventSummary Events()
    {
        var e = new EventSummary();
        try
        {
            var since7 = DateTime.UtcNow.AddDays(-7).ToString("o");
            var q = new EventLogQuery("System", PathType.LogName,
                $"*[System[(EventID=41 or EventID=6008 or (EventID=1001 and Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'])) and TimeCreated[@SystemTime>='{since7}']]]")
                { ReverseDirection = true };
            using var r = new EventLogReader(q);
            var crashes = new List<DateTime>();
            for (var ev = r.ReadEvent(); ev is not null && crashes.Count < 100; ev = r.ReadEvent())
                using (ev) if (ev.TimeCreated is { } t) crashes.Add(t);
            e.Crashes7d = crashes.Count;
            e.LastCrash = crashes.Count > 0 ? crashes.Max().ToString("yyyy-MM-dd HH:mm") : null;

            var since1 = DateTime.UtcNow.AddDays(-1).ToString("o");
            using var r2 = new EventLogReader(new EventLogQuery("System", PathType.LogName, $"*[System[(Level=1 or Level=2) and TimeCreated[@SystemTime>='{since1}']]]"));
            var n = 0;
            for (var ev = r2.ReadEvent(); ev is not null && n < 500; ev = r2.ReadEvent()) using (ev) n++;
            e.Errors24h = n;
        }
        catch (Exception) { }
        return e;
    }

    // ---------------------------------------------------------------- software
    public async Task<List<SoftwareEntry>> Software(CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<SoftwareEntry>();
        void Scan(RegistryKey? root, string scope, string idPrefix)
        {
            if (root is null) return;
            foreach (var sub in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(sub);
                var name = k?.GetValue("DisplayName")?.ToString();
                if (string.IsNullOrWhiteSpace(name) || k!.GetValue("SystemComponent") is 1 || k.GetValue("ParentKeyName") is not null) continue;
                var ver = k.GetValue("DisplayVersion")?.ToString();
                if (!seen.Add($"{name}|{ver}")) continue;
                var e = new SoftwareEntry { Name = name, Version = ver, Publisher = k.GetValue("Publisher")?.ToString(), Scope = scope, UninstallId = idPrefix + sub };
                // Mappe, programfil, beskrivelse og ikon – kan alle mangle; serveren udfylder beskrivelser fra sit katalog
                ProgramIcons.Fill(e, k.GetValue("InstallLocation")?.ToString(), k.GetValue("DisplayIcon")?.ToString(),
                                  k.GetValue("UninstallString")?.ToString(), k.GetValue("Comments")?.ToString());
                list.Add(e);
            }
        }
        const string un = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        using (var hk64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)) Scan(hk64.OpenSubKey(un), "machine", "HKLM|64|");
        using (var hk32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)) Scan(hk32.OpenSubKey(un), "machine", "HKLM|32|");
        foreach (var sid in Registry.Users.GetSubKeyNames().Where(s => s.StartsWith("S-1-5-21-") && !s.EndsWith("_Classes")))
            Scan(Registry.Users.OpenSubKey($@"{sid}\{un}"), "user", $"HKU|{sid}|");

        // Microsoft Store-apps
        var all = AgentOptions.IsSystem ? "-AllUsers " : "";     // -AllUsers kræver administrator
        var appx = await runner.PowerShell("Get-AppxPackage " + all + "| Where-Object { -not $_.IsFramework -and $_.SignatureKind -eq 'Store' -and -not $_.NonRemovable } | " +
                                           "Select-Object Name, PackageFullName, @{n='Version';e={\"$($_.Version)\"}}, Publisher, InstallLocation | ConvertTo-Json -Compress", RunAs.System, TimeSpan.FromMinutes(2), ct);
        try
        {
            using var doc = JsonDocument.Parse(appx.Output.Length > 0 ? appx.Output : "[]");
            var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
            foreach (var a in items)
            {
                var name = a.GetProperty("Name").GetString() ?? "";
                if (name.Length > 0 && seen.Add(name))
                    list.Add(new SoftwareEntry
                    {
                        Name = name, Version = a.GetProperty("Version").GetString(), Publisher = Regex.Match(a.GetProperty("Publisher").GetString() ?? "", "CN=([^,]+)").Groups[1].Value, Scope = "store",
                        UninstallId = a.TryGetProperty("PackageFullName", out var pf) && pf.GetString() is { Length: > 0 } full ? $"store|{full}" : null,
                        InstallPath = a.TryGetProperty("InstallLocation", out var loc) && loc.ValueKind == JsonValueKind.String ? ProgramIcons.CleanDir(loc.GetString()) : null,
                    });
            }
        }
        catch (Exception) { }

        list.AddRange(SteamGames());
        // Runtime-komponenter uden afinstallations-post: DirectX 9-filerne (d3dx9/xinput) fra "DirectX End-User Runtime"
        if (File.Exists(Path.Combine(Environment.SystemDirectory, "d3dx9_43.dll")) && seen.Add("DirectX End-User Runtime"))
            list.Add(new SoftwareEntry { Name = "DirectX End-User Runtime (Microsoft.DirectX)", Version = "9.29", Publisher = "Microsoft Corporation", Scope = "machine" });
        return list.OrderBy(s => s.Name).ToList();
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)] private static partial Regex VdfPath();
    [GeneratedRegex("\"(appid|name|installdir)\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase)] private static partial Regex AcfField();

    /// <summary>Installerede Steam-spil fra appmanifest_*.acf i alle Steam-biblioteker.</summary>
    public static IEnumerable<SoftwareEntry> SteamGames()
    {
        var steam = RegString(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        if (steam is null || !Directory.Exists(steam)) yield break;
        var libs = new List<string> { steam };
        foreach (var vdf in new[] { Path.Combine(steam, "steamapps", "libraryfolders.vdf"), Path.Combine(steam, "config", "libraryfolders.vdf") })
            if (File.Exists(vdf)) libs.AddRange(VdfPath().Matches(File.ReadAllText(vdf)).Select(m => m.Groups[1].Value.Replace(@"\\", @"\")));
        foreach (var lib in libs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                string? id = null, name = null, dir = null;
                try
                {
                    foreach (Match m in AcfField().Matches(File.ReadAllText(acf)))
                        switch (m.Groups[1].Value.ToLowerInvariant())
                        {
                            case "appid": id ??= m.Groups[2].Value; break;
                            case "name": name ??= m.Groups[2].Value; break;
                            default: dir ??= m.Groups[2].Value; break;
                        }
                }
                catch (IOException) { }
                if (id is not null) yield return new SoftwareEntry
                {
                    Name = $"{name ?? "Steam-spil"} (Steam {id})", Version = id, Publisher = "Steam", Scope = "steam", UninstallId = $"steam|{id}",
                    InstallPath = string.IsNullOrEmpty(dir) ? null : ProgramIcons.CleanDir(Path.Combine(apps, "common", dir)),
                };
            }
        }
    }
}

/// <summary>Windows Update via Windows Update Agent (COM).</summary>
public static class WindowsUpdate
{
    private static T Sta<T>(Func<T> f)
    {
        T result = default!;
        Exception? err = null;
        var t = new Thread(() => { try { result = f(); } catch (Exception e) { err = e; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start(); t.Join();
        if (err is not null) throw err;
        return result;
    }

    private static dynamic Session() => Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session", true)!)!;

    /// <summary>Microsoft Update: Windows Update plus Office (MSI) og andre Microsoft-programmer ("Modtag opdateringer til andre Microsoft-produkter").</summary>
    public const string MicrosoftUpdateService = "7971f918-a847-4430-9279-4a52d1efe18d";

    /// <summary>Slå Microsoft Update til (som kontakten i Indstillinger), hvis den ikke er det. false = kun Windows Update.</summary>
    private static bool EnsureMicrosoftUpdate()
    {
        try
        {
            dynamic sm = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.ServiceManager", true)!)!;
            foreach (dynamic svc in sm.Services)
                if (string.Equals((string)svc.ServiceID, MicrosoftUpdateService, StringComparison.OrdinalIgnoreCase)) return true;
            sm.AddService2(MicrosoftUpdateService, 7 /* afventende + online + registrér hos Automatiske opdateringer */, "");
            return true;
        }
        catch { return false; }
    }

    /// <summary>En søger mod Microsoft Update, når den kan slås til – ellers mod Windows Update som før.</summary>
    private static dynamic Searcher(dynamic session)
    {
        dynamic searcher = session.CreateUpdateSearcher();
        if (EnsureMicrosoftUpdate()) { searcher.ServerSelection = 3 /* ssOthers */; searcher.ServiceID = MicrosoftUpdateService; }
        return searcher;
    }

    /// <summary>
    /// Den dybe scanning: hver ventende opdatering (også drivere og Office) som sin egen linje med id "wu:&lt;UpdateID&gt;".
    /// Skjulte opdateringer (IsHidden) kommer med som Hidden, så de kan vises igen. Drivere får en vurdering (DriverAdvice).
    /// </summary>
    public static List<UpgradeEntry> PendingEntries() => Sta(() =>
    {
        dynamic searcher = Searcher(Session());
        var list = new List<UpgradeEntry>();
        List<DriverAdvice.Installed>? installed = null;
        foreach (var hidden in new[] { false, true })
        {
            dynamic r;
            try { r = searcher.Search($"IsInstalled=0 and IsHidden={(hidden ? 1 : 0)}"); }
            catch when (hidden) { continue; }   // de skjulte er en bonus – den almindelige liste må ikke gå tabt for dem
            foreach (dynamic u in r.Updates)
            {
                string title = u.Title, id = u.Identity.UpdateID;
                string? kb = null;
                foreach (dynamic k in u.KBArticleIDs) { kb = "KB" + (string)k; break; }
                string? available = kb, advice = null;
                var driver = (int)u.Type == 2;
                if (driver)   // en driver: dens dato fortæller mere end et KB-nummer
                {
                    DateTime? date = null;
                    try { date = (DateTime)u.DriverVerDate; available = date.Value.ToString("yyyy-MM-dd"); } catch { available ??= "driver"; }
                    string? provider = null, hwid = null;
                    try { provider = (string)u.DriverProvider; } catch { }
                    try { hwid = (string)u.DriverHardwareID; } catch { }
                    installed ??= InstalledDrivers();
                    advice = DriverAdvice.For(date, provider, DriverAdvice.Match(installed, hwid), DateTime.Now);
                }
                list.Add(new UpgradeEntry { Name = title, Id = DeepUpdates.WuPrefix + id, Available = available, Driver = driver, Advice = advice, Hidden = hidden });
            }
        }
        return list;
    });

    /// <summary>De drivere, der er installeret nu (hardware-id, udgiver, dato) – til at se, om en tilbudt driver er ældre eller erstatter Microsofts egen.</summary>
    private static List<DriverAdvice.Installed> InstalledDrivers()
    {
        var list = new List<DriverAdvice.Installed>();
        foreach (var o in Collectors.Wmi(@"root\cimv2", "SELECT HardWareID, DriverProviderName, DriverDate FROM Win32_PnPSignedDriver"))
        {
            var hw = Collectors.Prop<string>(o, "HardWareID");
            if (string.IsNullOrEmpty(hw)) continue;
            DateTime? date = null;
            if (Collectors.Prop<string>(o, "DriverDate") is { Length: >= 8 } d)
                try { date = ManagementDateTimeConverter.ToDateTime(d); } catch { }
            list.Add(new DriverAdvice.Installed(hw, Collectors.Prop<string>(o, "DriverProviderName"), date));
        }
        return list;
    }

    /// <summary>Skjul (eller vis igen) netop disse opdateringer i Windows Update (id'er uden "wu:"). Kræver administrator – agenten kører som SYSTEM.</summary>
    public static (bool Ok, string Output) SetHidden(IReadOnlyCollection<string> updateIds, bool hide) => Sta(() =>
    {
        dynamic r = Searcher(Session()).Search($"IsInstalled=0 and IsHidden={(hide ? 0 : 1)}");
        var lines = new List<string>(); var found = 0;
        foreach (dynamic u in r.Updates)
        {
            if (!updateIds.Contains((string)u.Identity.UpdateID, StringComparer.OrdinalIgnoreCase)) continue;
            found++;
            try { u.IsHidden = hide; lines.Add($"OK  {(string)u.Title}: {(hide ? "skjult" : "vises igen")}"); }
            catch (Exception e) { lines.Add($"!!  {(string)u.Title}: {e.Message}"); }
        }
        var missing = updateIds.Count - found;
        if (missing > 0) lines.Add($"--  {missing} opdatering(er) blev ikke fundet – de er nok installeret eller {(hide ? "skjult" : "vist")} i forvejen.");
        return (!lines.Any(l => l.StartsWith("!!", StringComparison.Ordinal)), string.Join('\n', lines));
    });

    /// <summary>Installér netop disse opdateringer (id'er uden "wu:").</summary>
    public static (bool Ok, string Output, bool Reboot) InstallIds(IReadOnlyCollection<string> updateIds) =>
        Install("IsInstalled=0 and IsHidden=0", u => updateIds.Contains((string)u.Identity.UpdateID, StringComparer.OrdinalIgnoreCase), "Søger efter opdateringen");

    public static UpdateSummary Pending(bool drivers)
    {
        try
        {
            return Sta(() =>
            {
                dynamic searcher = Session().CreateUpdateSearcher();
                dynamic r = searcher.Search($"IsInstalled=0 and IsHidden=0 and Type='{(drivers ? "Driver" : "Software")}'");
                var titles = new List<string>();
                foreach (dynamic u in r.Updates) titles.Add((string)u.Title);
                return new UpdateSummary { Pending = titles.Count, Titles = titles.Take(25).ToList() };
            });
        }
        catch (Exception e) { return new UpdateSummary { Error = e.Message }; }
    }

    /// <summary>Henter og installerer alle ventende opdateringer. Returnerer (status, output, genstart kræves):
    /// ResultCode 3 ("gennemført med fejl") er delvis – ikke grøn.</summary>
    public static (string Status, string Output, bool Reboot) InstallAll(bool includeDrivers)
    {
        var (_, output, reboot) = Install("IsInstalled=0 and IsHidden=0 and Type='Software'" + (includeDrivers ? " or IsInstalled=0 and IsHidden=0 and Type='Driver'" : ""), _ => true,
            "Søger efter Windows-opdateringer");
        var status = output.Contains("Resultat: gennemført med fejl", StringComparison.Ordinal) ? JobStatus.Partial
            : output.Contains("Resultat: gennemført", StringComparison.Ordinal) || output.StartsWith("Ingen", StringComparison.Ordinal) ? JobStatus.Success : JobStatus.Failed;
        return (status, output, reboot);
    }

    // Windows Update er optaget (fx midt i sin egen automatiske opdatering): vent og prøv igen, så runderne ikke giver op for tidligt
    private static readonly int[] Busy = [unchecked((int)0x8024001E), unchecked((int)0x80240016), unchecked((int)0x80244022), unchecked((int)0x8024401C)];

    public static T Retry<T>(Func<T> f, int tries = 4, int pauseSec = 90)
    {
        for (var i = 1; ; i++)
        {
            try { return f(); }
            catch (System.Runtime.InteropServices.COMException e) when (i < tries && Busy.Contains(e.HResult))
            {
                LiveJob.Line(LiveJob.Current, $"Windows Update er optaget (0x{e.HResult:X8}) – prøver igen om {pauseSec} sek. ({i}/{tries - 1})");
                Thread.Sleep(TimeSpan.FromSeconds(pauseSec));
            }
        }
    }

    /// <summary>
    /// Opdateringsmotoren: alle ventende opdateringer (Microsoft Update) med det, planlæggeren skal vide – type, eksklusiv,
    /// funktionsopdatering, firmware/BIOS og driverens vurdering.
    /// </summary>
    public static List<UpdateCandidate> Candidates() => Retry(() => Sta(() =>
    {
        dynamic r = Searcher(Session()).Search("IsInstalled=0 and IsHidden=0");
        var list = new List<UpdateCandidate>();
        List<DriverAdvice.Installed>? installed = null;
        foreach (dynamic u in r.Updates)
        {
            string title = u.Title, id = u.Identity.UpdateID;
            var c = new UpdateCandidate { Id = id, Title = title };
            try { c.Exclusive = (int)u.InstallationBehavior.Impact == 2; } catch { }
            // funktionsopdateringer: kategorien "Upgrades" – via dens id, ikke det oversatte navn
            try { foreach (dynamic cat in u.Categories) if (string.Equals((string)cat.CategoryID, UpgradesCategory, StringComparison.OrdinalIgnoreCase)) c.FeatureUpdate = true; } catch { }
            if ((int)u.Type == 2)
            {
                string? cls = null, provider = null, hwid = null; DateTime? date = null;
                try { cls = (string)u.DriverClass; } catch { }
                try { provider = (string)u.DriverProvider; } catch { }
                try { hwid = (string)u.DriverHardwareID; } catch { }
                try { date = (DateTime)u.DriverVerDate; } catch { }
                var firmware = string.Equals(cls, "Firmware", StringComparison.OrdinalIgnoreCase) || UpdatePlanner.LooksLikeFirmware(title);
                c.Kind = firmware ? UpdateKinds.Firmware : UpdateKinds.Driver;
                // BIOS hedder typisk bare "<producent> - Firmware - 1.21.0": slå op i ESRT, om det er systemfirmware (ukendt = BIOS)
                c.Bios = firmware && UpdatePlanner.IsBiosFirmware(title, hwid, EsrtType);
                if (!firmware)
                {
                    installed ??= InstalledDrivers();
                    c.Advice = DriverAdvice.For(date, provider, DriverAdvice.Match(installed, hwid), DateTime.Now);
                }
            }
            list.Add(c);
        }
        return list;
    }));

    private const string UpgradesCategory = "3689BDC8-B205-4AF4-8D4A-A63924C5E9D5";

    /// <summary>ESRT-typen for en firmware-GUID (HKLM\HARDWARE\UEFI\ESRT\{GUID}\Type): 1 = system (BIOS), 2 = enhed. null = ukendt.</summary>
    private static int? EsrtType(string guid)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"HARDWARE\UEFI\ESRT\{{{guid}}}") ?? Registry.LocalMachine.OpenSubKey($@"HARDWARE\UEFI\ESRT\{guid}");
            return k?.GetValue("Type") is int t ? t : null;
        }
        catch { return null; }
    }

    /// <summary>Resultatet for én opdatering: 2 = gennemført, 3 = gennemført med fejl (installeret – med en advarsel); alt andet er en fejl.</summary>
    public sealed record WuResult(string Id, string Title, int Code, int HResult)
    {
        public bool Ok => Code is 2 or 3;
        public bool Warn => Code == 3;
        public string Hex => $"0x{HResult:X8}";
    }

    /// <summary>Hent og installér netop disse opdateringer (id'er uden "wu:") i én batch – med resultatet for hver.</summary>
    public static (List<WuResult> Results, bool Reboot) InstallSet(IReadOnlyCollection<string> updateIds, string label) => Retry(() => Sta(() =>
    {
        dynamic session = Session();
        dynamic result = Searcher(session).Search("IsInstalled=0 and IsHidden=0");
        dynamic coll = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl", true)!)!;
        foreach (dynamic u in result.Updates)
        {
            if (!updateIds.Contains((string)u.Identity.UpdateID, StringComparer.OrdinalIgnoreCase)) continue;
            if (!(bool)u.EulaAccepted) u.AcceptEula();
            coll.Add(u);
        }
        var list = new List<WuResult>();
        if ((int)coll.Count == 0) return (list, false);
        LiveJob.Line(LiveJob.Current, $"{label}: henter {(int)coll.Count} opdatering(er)");
        dynamic dl = session.CreateUpdateDownloader(); dl.Updates = coll; dl.Download();
        LiveJob.Line(LiveJob.Current, $"{label}: installerer {(int)coll.Count} opdatering(er)");
        dynamic inst = session.CreateUpdateInstaller(); inst.Updates = coll;
        dynamic res = inst.Install();
        for (var i = 0; i < (int)coll.Count; i++)
        {
            dynamic ur = res.GetUpdateResult(i);
            list.Add(new WuResult((string)coll.Item(i).Identity.UpdateID, (string)coll.Item(i).Title, (int)ur.ResultCode, (int)ur.HResult));
        }
        return (list, (bool)res.RebootRequired);
    }));

    private static (bool Ok, string Output, bool Reboot) Install(string crit, Func<dynamic, bool> want, string searching)
    {
        return Sta(() =>
        {
            var lines = new List<string>();
            dynamic session = Session();
            dynamic searcher = Searcher(session);
            LiveJob.Phase(0, 10, searching);
            dynamic result = searcher.Search(crit);
            dynamic coll = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl", true)!)!;
            foreach (dynamic u in result.Updates)
            {
                if (!want(u)) continue;
                if (!(bool)u.EulaAccepted) u.AcceptEula();
                coll.Add(u);
                lines.Add($"Fundet: {(string)u.Title}");
            }
            if ((int)coll.Count == 0) return (true, "Ingen Windows-opdateringer venter.", false);
            var names = new List<string>();
            for (var k = 0; k < (int)coll.Count; k++) names.Add((string)coll.Item(k).Title);
            LiveJob.Phase(10, 50, $"Henter {names.Count} opdatering(er): {string.Join(", ", names)}");
            dynamic dl = session.CreateUpdateDownloader(); dl.Updates = coll; dl.Download();
            LiveJob.Phase(50, 100, $"Installerer {names.Count} opdatering(er): {string.Join(", ", names)}");
            dynamic inst = session.CreateUpdateInstaller(); inst.Updates = coll;
            dynamic res = inst.Install();
            var code = (int)res.ResultCode;     // 2 = gennemført, 3 = gennemført med fejl
            var reboot = (bool)res.RebootRequired;
            for (var i = 0; i < (int)coll.Count; i++)
                lines.Add($"{((int)res.GetUpdateResult(i).ResultCode == 2 ? "OK " : "!! ")} {(string)coll.Item(i).Title}");
            lines.Add($"\nResultat: {code switch { 2 => "gennemført", 3 => "gennemført med fejl", 4 => "fejlet", 5 => "afbrudt", _ => code.ToString() }}" +
                      (reboot ? " – genstart kræves" : ""));
            return (code is 2 or 3, string.Join('\n', lines), reboot);
        });
    }
}
