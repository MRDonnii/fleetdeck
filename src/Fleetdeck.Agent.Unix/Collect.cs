using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Unix;

public sealed record ShellResult(int ExitCode, string Output, bool TimedOut, bool Cancelled = false);

/// <summary>Kører bash-kommandoer som root eller som den indloggede bruger (skærmen, notifikationer, brugertjenester).</summary>
public static class Shell
{
    /// <summary>C.UTF-8 hvor den findes (de fleste distroer og macOS); ellers C (fx Unraid), så bash ikke advarer i outputtet.</summary>
    private static readonly string Locale = PickLocale();

    private static string PickLocale()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("/bin/sh", "-c \"locale -a 2>/dev/null\"") { RedirectStandardOutput = true, UseShellExecute = false })!;
            var all = p.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            p.WaitForExit(5000);
            foreach (var want in new[] { "C.UTF-8", "C.utf8", "en_US.UTF-8", "en_US.utf8" })
                if (all.Contains(want, StringComparer.Ordinal)) return want;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        return "C";
    }

    /// <summary>Annulleres, når det kørende job stoppes i Fleetdeck (agenten kører ét job ad gangen).</summary>
    public static CancellationToken JobCancel { get; set; }

    /// <summary>bash, hvor den findes; ellers sh (Alpine og andre små systemer har kun busybox-sh).</summary>
    public static readonly string Bash = File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";

    public static ShellResult Run(string script, TimeSpan? timeout = null, string? asUser = null, int maxChars = 60000)
    {
        // stdin lukkes straks: et program, der alligevel spørger om noget, får "intet svar" i stedet for at vente for evigt
        var psi = new ProcessStartInfo(Bash) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        psi.Environment["DEBIAN_FRONTEND"] = "noninteractive";
        psi.Environment["NEEDRESTART_MODE"] = "a";            // Ubuntu: genstart tjenester selv i stedet for at spørge
        psi.Environment["APT_LISTCHANGES_FRONTEND"] = "none";
        psi.Environment["UCF_FORCE_CONFFOLD"] = "1";
        psi.Environment["LC_ALL"] = Locale;
        psi.Environment["PATH"] = UnixRoot.RootPath(Mac.Is);   // på en Mac: systemmapperne før /usr/local/bin
        if (Mac.Is && asUser is { Length: > 0 } && Mac.Session() is { } ms && ms.User == asUser)
        {
            // macOS: i brugerens login-session (adgang til skærm og nøglering)
            psi.FileName = "/bin/launchctl";
            foreach (var a in new[] { "asuser", ms.Uid.ToString(), "sudo", "-u", ms.User, "-H", "/bin/bash", "-c", script }) psi.ArgumentList.Add(a);
        }
        else if (asUser is { Length: > 0 } && UserSession.Find() is { } s && s.User == asUser)
        {
            // som brugeren, med adgang til skærm, D-Bus og systemd --user
            psi.FileName = "/usr/sbin/runuser";
            foreach (var a in new[] { "-u", s.User, "--", "env", $"HOME={s.Home}", $"XDG_RUNTIME_DIR=/run/user/{s.Uid}",
                         $"DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/{s.Uid}/bus", $"DISPLAY={s.Display}", $"XAUTHORITY={s.XAuthority}",
                         Bash, "-c", script })
                psi.ArgumentList.Add(a);
        }
        // bad om en bruger, men vedkommende er ikke (længere) på skærmen: kør aldrig som root i stedet
        else if (asUser is { Length: > 0 }) return new ShellResult(1, $"[Agent] {asUser} er ikke logget ind på skærmen.", false);
        else { psi.ArgumentList.Add("-c"); psi.ArgumentList.Add(script); }
        using var p = Process.Start(psi)!;
        try { p.StandardInput.Close(); } catch (IOException) { }
        var sb = new StringBuilder();
        var job = LiveJob.Current;   // fremdrift til det job, der kører kommandoen
        p.OutputDataReceived += (_, e) => { if (e.Data is not null && LiveJob.Line(job, e.Data)) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null && LiveJob.Line(job, e.Data)) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromMinutes(30));
        var cancel = JobCancel;
        bool ok, cancelled = false;
        while (!(ok = p.WaitForExit(500)))
        {
            if (cancel.IsCancellationRequested) { cancelled = true; break; }
            if (DateTime.UtcNow >= until) break;
        }
        if (!ok) { try { p.Kill(true); } catch (InvalidOperationException) { } }
        // vent på resten af outputtet (stort output fx fra docker inspect), men aldrig uendeligt:
        // en tjeneste, som kommandoen startede, kan have arvet outputtet og holde det åbent
        Task.Run(() => p.WaitForExit()).Wait(TimeSpan.FromSeconds(30));
        string text; lock (sb) text = sb.ToString();
        if (cancelled) text += "\n[Agent] Annulleret fra Fleetdeck.";
        return new ShellResult(ok ? p.ExitCode : -1, text.Length > maxChars ? text[^maxChars..] : text, !ok && !cancelled, cancelled);
    }

    /// <summary>Kort kommando: hele svaret (fx 1.800 pakker), tom streng ved fejl.</summary>
    public static string Out(string script, int seconds = 20)
    {
        try { var r = Run(script, TimeSpan.FromSeconds(seconds), null, int.MaxValue); return r.ExitCode == 0 ? r.Output.Trim() : ""; }
        catch (Exception) { return ""; }
    }

    public static bool Has(string tool) => Out($"command -v {tool}").Length > 0;
}

/// <summary>Den grafiske session på skærmen (seat0): bruger, uid, DISPLAY.</summary>
public sealed record UserSession(string User, int Uid, string Home, string Display, string XAuthority)
{
    public static UserSession? Find()
    {
        if (Mac.Is) return Mac.Session();
        var sid = Shell.Out("loginctl show-seat seat0 -p ActiveSession --value 2>/dev/null");
        if (sid.Length == 0) sid = Shell.Out("loginctl list-sessions --no-legend 2>/dev/null | awk '$3!=\"root\" && $3!=\"gdm\" {print $1; exit}'");
        if (sid.Length == 0) return null;
        var props = Shell.Out($"loginctl show-session {sid} -p Name -p User -p Display 2>/dev/null").Split('\n')
            .Select(l => l.Split('=', 2)).Where(a => a.Length == 2).ToDictionary(a => a[0], a => a[1]);
        if (!props.TryGetValue("Name", out var user) || user.Length == 0 || !int.TryParse(props.GetValueOrDefault("User"), out var uid)) return null;
        var display = props.GetValueOrDefault("Display") is { Length: > 0 } d ? d : ":0";
        var home = Shell.Out($"getent passwd {user} | cut -d: -f6") is { Length: > 0 } h ? h : $"/home/{user}";
        var xauth = File.Exists($"/run/user/{uid}/gdm/Xauthority") ? $"/run/user/{uid}/gdm/Xauthority" : $"{home}/.Xauthority";
        return new UserSession(user, uid, home, display, xauth);
    }
}

public static class Collect
{
    private static (long Idle, long Total)? _cpuLast;

    private static string Read(string path) { try { return File.ReadAllText(path).Trim(); } catch (Exception) { return ""; } }

    private static Dictionary<string, string> OsRelease() => Read("/etc/os-release").Split('\n').Select(l => l.Split('=', 2))
        .Where(a => a.Length == 2).ToDictionary(a => a[0], a => a[1].Trim('"'));

    private static double? Cpu()
    {
        static (long, long)? Sample()
        {
            var f = Read("/proc/stat").Split('\n').FirstOrDefault(l => l.StartsWith("cpu "))?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(long.Parse).ToArray();
            return f is { Length: >= 5 } ? (f[3] + f[4], f.Sum()) : null;
        }
        var now = Sample();
        if (now is null) return null;
        var last = _cpuLast;
        if (last is null) { Thread.Sleep(300); var s2 = Sample(); last = now; now = s2; if (now is null) return null; }
        _cpuLast = now;
        var dt = now.Value.Item2 - last.Value.Total; var di = now.Value.Item1 - last.Value.Idle;
        return dt <= 0 ? null : Math.Round(100.0 * (dt - di) / dt, 1);
    }

    private static Dictionary<string, long> MemInfo() => Read("/proc/meminfo").Split('\n').Select(l => l.Split(':', 2)).Where(a => a.Length == 2)
        .ToDictionary(a => a[0], a => long.TryParse(a[1].Trim().Split(' ')[0], out var v) ? v : 0);

    private static Temperatures Temps()
    {
        var t = new Temperatures();
        foreach (var dir in Directory.Exists("/sys/class/hwmon") ? Directory.GetDirectories("/sys/class/hwmon") : [])
        {
            var name = Read(Path.Combine(dir, "name"));
            var vals = Directory.GetFiles(dir, "temp*_input").Select(f => double.TryParse(Read(f), out var v) ? v / 1000 : (double?)null)
                .Where(v => v is > 0 and < 150).Select(v => v!.Value).ToList();
            if (vals.Count == 0) continue;
            if (name is "coretemp" or "k10temp" or "zenpower" or "cpu_thermal") { t.Cpu = Math.Max(t.Cpu ?? 0, vals.Max()); t.Source = "hwmon"; }
            else if (name is "amdgpu" or "nouveau" or "radeon") t.Gpu = Math.Max(t.Gpu ?? 0, vals.Max());
        }
        if (t.Cpu is null)
            foreach (var z in Directory.Exists("/sys/class/thermal") ? Directory.GetDirectories("/sys/class/thermal", "thermal_zone*") : [])
                if (Read(Path.Combine(z, "type")) is "x86_pkg_temp" or "acpitz" && double.TryParse(Read(Path.Combine(z, "temp")), out var v))
                { t.Cpu = Math.Max(t.Cpu ?? 0, v / 1000); t.Source = "thermal"; }
        return t;
    }

    private static readonly HashSet<string> RealFs = ["ext2", "ext3", "ext4", "xfs", "btrfs", "zfs", "f2fs", "ntfs", "ntfs3", "exfat", "vfat"];

    private static List<VolumeInfo> Volumes() => DriveInfo.GetDrives()
        .Where(d => { try { return d.IsReady && RealFs.Contains(d.DriveFormat) && d.TotalSize > 1L << 30 && !d.Name.StartsWith("/snap") && !d.Name.StartsWith("/boot"); } catch (Exception) { return false; } })
        .Select(d => new VolumeInfo
        {
            Drive = d.Name, SizeGb = Math.Round(d.TotalSize / 1e9, 1), FreeGb = Math.Round(d.AvailableFreeSpace / 1e9, 1),
            PctUsed = Math.Round(100.0 * (d.TotalSize - d.AvailableFreeSpace) / d.TotalSize, 1),
        }).ToList();

    private static NetworkInterface? MainNic() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback && !n.Name.StartsWith("docker") && !n.Name.StartsWith("veth"))
        .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Count > 0).FirstOrDefault();

    public static LiveFacts Live()
    {
        if (Mac.Is) return Mac.Live();
        var mem = MemInfo();
        double total = mem.GetValueOrDefault("MemTotal"), avail = mem.GetValueOrDefault("MemAvailable");
        var nic = MainNic();
        var mac = nic?.GetPhysicalAddress().GetAddressBytes();
        return new LiveFacts
        {
            Hostname = Environment.MachineName,
            User = UserSession.Find()?.User,
            Ip = nic?.GetIPProperties().UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString(),
            Mac = mac is { Length: 6 } ? string.Join('-', mac.Select(b => b.ToString("X2"))) : null,
            Cpu = Cpu(),
            Ram = total > 0 ? Math.Round(100 * (total - avail) / total, 1) : null,
            RamUsedGb = Math.Round((total - avail) / 1048576, 1), RamTotalGb = Math.Round(total / 1048576, 1),
            UptimeH = double.TryParse(Read("/proc/uptime").Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture, out var up) ? Math.Round(up / 3600, 1) : null,
            BootAt = up > 0 ? BootTime.FromUptime(DateTime.UtcNow, up) : null,
            PendingReboot = Pkg.RebootNeeded(),
            PowerMode = Power.Current()?.Mode, PowerAt = Power.Current()?.At,
            UpdateError = SelfUpdate.Failures >= 3 ? SelfUpdate.LastError : null,
            RemoteActive = RemoteDesk.Active(),
            HeadlessBrowsers = Mac.Is ? null : HeadlessBrowsers.Count(),
            Temps = Temps(),
            Volumes = Volumes(),
        };
    }

    public static SystemFacts SystemInfo()
    {
        if (Mac.Is) return Mac.SystemInfo();
        var os = OsRelease();
        var cpuinfo = Read("/proc/cpuinfo").Split('\n');
        string? Field(string k) => cpuinfo.FirstOrDefault(l => l.StartsWith(k))?.Split(':', 2)[1].Trim();
        var up = double.TryParse(Read("/proc/uptime").Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture, out var u) ? u : 0;
        var sb = Directory.Exists("/sys/firmware/efi/efivars")
            ? Directory.GetFiles("/sys/firmware/efi/efivars", "SecureBoot-*").Select(f => { try { var b = File.ReadAllBytes(f); return b.Length >= 5 ? b[4] == 1 : (bool?)null; } catch (Exception) { return null; } }).FirstOrDefault()
            : null;
        var facts = new SystemFacts
        {
            Hostname = Environment.MachineName,
            Os = Unraid.Is ? $"Unraid {Unraid.Version}" : os.GetValueOrDefault("PRETTY_NAME") ?? "Linux",
            Distro = Unraid.Is ? "unraid" : os.GetValueOrDefault("ID"), DistroLike = os.GetValueOrDefault("ID_LIKE"),
            PackageManager = Unraid.Is ? null : Pkg.NativeName, InitSystem = Unraid.Is ? null : Init.Label, OsVersion = os.GetValueOrDefault("VERSION_ID"), Build = Shell.Out("uname -r"),
            Edition = os.GetValueOrDefault("NAME"),
            LastBoot = DateTime.Now.AddSeconds(-up).ToString("yyyy-MM-dd HH:mm"),
            Manufacturer = Read("/sys/class/dmi/id/sys_vendor") is { Length: > 0 } v ? v : Read("/proc/device-tree/model").StartsWith("Raspberry") ? "Raspberry Pi Ltd" : "",
            Model = Read("/sys/class/dmi/id/product_name") is { Length: > 0 } pn ? pn : Read("/proc/device-tree/model").TrimEnd('\0'),
            PiSerial = PiSerialNumber(),
            Board = Read("/sys/class/dmi/id/board_name"), Bios = Read("/sys/class/dmi/id/bios_version"),
            CpuName = Field("model name"), Cores = int.TryParse(Field("cpu cores"), out var c) ? c : null, Threads = Environment.ProcessorCount,
            RamGb = Math.Round(MemInfo().GetValueOrDefault("MemTotal") / 1048576.0, 1),
            Gpus = Shell.Out("lspci 2>/dev/null | grep -Ei 'vga|3d|display' | cut -d: -f3-").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList(),
            Nics = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback && !n.Name.StartsWith("veth") && !n.Name.StartsWith("docker"))
                .Select(n => new NicInfo
                {
                    Name = n.Name, Status = n.OperationalStatus.ToString(), Speed = n.Speed > 0 ? $"{n.Speed / 1_000_000} Mbit/s" : null,
                    Mac = n.GetPhysicalAddress().GetAddressBytes() is { Length: 6 } m ? string.Join('-', m.Select(b => b.ToString("X2"))) : null,
                }).ToList(),
            TimeZone = Read("/etc/timezone") is { Length: > 0 } tz ? tz : TimeZoneInfo.Local.Id,
            Firmware = Directory.Exists("/sys/firmware/efi") ? "UEFI" : "BIOS", SecureBoot = sb,
            AgentBuild = "linux",
        };
        if (Directory.Exists(BiosFacts.DmiRoot)) BiosFacts.FromDmi(facts, BiosFacts.DirReader());   // Pi'er har ingen DMI
        // BIOS-indstillinger (firmware-attributes): kun antal, driver og flag – selve værdierne læses først i et bios_settings_read-job
        try { facts.BiosSettings = FirmwareAttributes.Detect()?.Info(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { facts.BiosSettings = null; }
        return facts;
    }

    // ------------------------------------------------------------------ pakker
    /// <summary>apt-get update højst hver 6. time (bruges før installation og opdateringstjek).</summary>
    public static void AptRefresh(bool force = false) => Pkg.Refresh(force);

    public static List<UpgradeEntry> Upgrades() => Mac.Is ? Mac.Upgrades() : Unraid.Is ? Unraid.Upgrades() : Pkg.Upgrades();

    public static InventoryReport Inventory()
    {
        var sw = Mac.Is ? Mac.Installed() : Unraid.Is ? Unraid.Installed() : Pkg.Installed();
        return new InventoryReport { Facts = SystemInfo(), Health = Health(), Software = sw, WingetIds = sw.Select(s => s.Name).Distinct().ToList(), Upgrades = Upgrades() };
    }

    // ------------------------------------------------------------------ helbred
    public static HealthReport Health()
    {
        if (Mac.Is) return Mac.Health();
        var h = new HealthReport { Live = Live() };
        // Diske (smartctl giver helbred og temperatur, hvis det er installeret)
        try
        {
            // SERIAL: diske af samme model (fx et Unraid-array) kendes fra hinanden, også når de sover (smartctl -n standby giver intet serienummer).
            // Meget gammelt lsblk uden SERIAL: prøv uden.
            var lsblk = Shell.Out("lsblk -J -b -d -o NAME,MODEL,SERIAL,SIZE,ROTA,TRAN,TYPE 2>/dev/null") is { Length: > 0 } j1 ? j1
                : Shell.Out("lsblk -J -b -d -o NAME,MODEL,SIZE,ROTA,TRAN,TYPE 2>/dev/null");
            using var doc = JsonDocument.Parse(lsblk is { Length: > 0 } j ? j : "{}");
            if (doc.RootElement.TryGetProperty("blockdevices", out var devs))
                foreach (var d in devs.EnumerateArray())
                {
                    if (d.GetProperty("type").GetString() != "disk") continue;
                    var name = d.GetProperty("name").GetString()!;
                    if (name.StartsWith("loop") || name.StartsWith("zram")) continue;
                    var dh = new DiskHealth
                    {
                        Name = (d.TryGetProperty("model", out var mo) ? mo.GetString()?.Trim() : null) is { Length: > 0 } model ? model : name,
                        Media = d.TryGetProperty("rota", out var ro) && ro.ValueKind == JsonValueKind.True ? "HDD" : "SSD",
                        Bus = d.TryGetProperty("tran", out var tr) ? tr.GetString()?.ToUpperInvariant() : null,
                        SizeGb = d.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var b) ? Math.Round(b / 1e9, 0) : null,
                        Source = "sysfs",
                        Id = DiskSmart.IdOf(d.TryGetProperty("serial", out var se) && se.ValueKind == JsonValueKind.String ? se.GetString() : null),
                    };
                    if (name.StartsWith("nvme", StringComparison.Ordinal)) dh.Bus = "NVMe";
                    dh.Kind = DiskSmart.KindOf(dh.Media, dh.Bus);
                    // -n standby: vækker ikke en sovende disk (Unraid-arrayet); så beholder serveren de sidst kendte tal.
                    // smartctl giver en exit-kode ≠ 0 netop for syge diske (bitmaske), derfor "|| true"
                    if (Shell.Has("smartctl"))
                        try
                        {
                            if (Shell.Out($"smartctl -a -j -n standby /dev/{name} 2>/dev/null || true", 30) is { Length: > 0 } sj) dh = DiskSmart.FromSmartctl(sj, dh);
                        }
                        catch (Exception e) when (e is not OperationCanceledException) { Log.Warn($"SMART {name}: {e.Message}"); }
                    h.Disks.Add(dh);
                }
        }
        catch (Exception) { }
        var up = Upgrades();
        h.Updates = new UpdateSummary { Pending = up.Count, Titles = up.Take(25).Select(u => $"{u.Name} {u.Available}").ToList() };
        h.LastUpdate = Pkg.LastChange()?.ToString("yyyy-MM-dd HH:mm");
        // Nedbrud: rapporter i /var/crash fra den sidste uge
        var crashes = Directory.Exists("/var/crash") ? new DirectoryInfo("/var/crash").GetFiles("*.crash").Where(f => f.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-7)).ToList() : [];
        h.Events = new EventSummary { Crashes7d = crashes.Count, LastCrash = crashes.OrderByDescending(f => f.LastWriteTime).FirstOrDefault()?.Name };
        // Fejlede tjenester: system + den indloggede brugers (fx kiosk-*)
        var failed = Shell.Out("systemctl list-units --failed --no-legend --plain 2>/dev/null | awk '{print $1}'").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (UserSession.Find() is { } us)
            failed.AddRange(Shell.Out($"systemctl --user -M {us.User}@ list-units --failed --no-legend --plain 2>/dev/null | awk '{{print $1}}'")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(n => $"{n} ({us.User})"));
        failed.AddRange(Init.FailedServices());
        h.FailedServices = failed.Where(n => n.Contains('.') || n.Contains('(')).Distinct().ToList();
        if (Unraid.Is)
        {
            // Unraid: arrayet skal køre, og paritetstjekket må ikke have fundet fejl
            var (state, errs) = Unraid.Array();
            if (state.Length > 0 && state != "STARTED") h.FailedServices.Add($"Unraid-array: {state}");
            if (errs > 0) h.FailedServices.Add($"Unraid-paritet: {errs} fejl ved seneste tjek");
            try { h.Unraid = Unraid.Info(); } catch (Exception e) when (e is IOException or InvalidOperationException or FormatException) { Log.Warn($"Unraid-oversigt: {e.Message}"); }
        }
        return h;
    }

    /// <summary>Raspberry Pi: de sidste 8 hex-tegn af serienummeret (det navn, Pi'ens bootloader leder efter ved netboot), ellers null.</summary>
    public static string? PiSerialNumber()
    {
        var model = Read("/proc/device-tree/model").TrimEnd('\0');
        if (!model.Contains("Raspberry Pi", StringComparison.OrdinalIgnoreCase)) return null;
        var sn = Read("/proc/device-tree/serial-number").TrimEnd('\0').Trim().ToLowerInvariant();
        if (sn.Length == 0) sn = File.Exists("/proc/cpuinfo") ? File.ReadAllLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("Serial", StringComparison.Ordinal))?.Split(':').Last().Trim().ToLowerInvariant() ?? "" : "";
        return System.Text.RegularExpressions.Regex.IsMatch(sn, "^[0-9a-f]{8,16}$") ? sn[^8..] : null;
    }
}
