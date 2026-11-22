using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Unix;

/// <summary>
/// macOS: samme agent som på Linux, men med macOS' egne værktøjer – system_profiler, sw_vers, softwareupdate,
/// Homebrew (som brugeren på skærmen) og App Store (mas). Kører som launchd-tjenesten com.fleetdeck.agent.
/// </summary>
public static class Mac
{
    public static bool Is => OperatingSystem.IsMacOS();
    public const string Label = "com.fleetdeck.agent";
    public const string Dir = "/usr/local/fleetdeck";
    public const string Plist = "/Library/LaunchDaemons/" + Label + ".plist";
    private static string Q(string s) => Pkg.Q(s);

    // ------------------------------------------------------------------ bruger på skærmen
    public static UserSession? Session()
    {
        var user = Shell.Out("stat -f %Su /dev/console");
        if (user.Length == 0 || user is "root" or "_mbsetupuser" or "loginwindow") return null;
        if (!int.TryParse(Shell.Out($"id -u {Q(user)}"), out var uid)) return null;
        var home = Shell.Out($"dscl . -read /Users/{Q(user)} NFSHomeDirectory | awk '{{print $2}}'") is { Length: > 0 } h ? h : $"/Users/{user}";
        return new UserSession(user, uid, home, "", "");
    }

    /// <summary>
    /// Alle, der er logget ind (den ved skærmen først) – også brugere i baggrunden ved hurtigt brugerskift. Hver loginsession har sin
    /// egen loginwindow-proces, der kører som brugeren.
    /// </summary>
    public static List<UserSession> Sessions(UserSession? console = null)
    {
        console ??= Session();
        var list = new List<UserSession>();
        if (console is not null) list.Add(console);
        foreach (var uid in MacLoginSessions.Uids(Shell.Out(MacLoginSessions.PsCommand), console?.Uid))
        {
            if (uid == console?.Uid) continue;
            var user = Shell.Out($"id -nu {uid}").Trim();
            if (user.Length == 0 || user is "root" or "_mbsetupuser" or "loginwindow") continue;
            var home = Shell.Out($"dscl . -read /Users/{Q(user)} NFSHomeDirectory | awk '{{print $2}}'") is { Length: > 0 } h ? h : $"/Users/{user}";
            list.Add(new UserSession(user, uid, home, "", ""));
        }
        return list;
    }

    /// <summary>Homebrew findes ét af to steder (Apple Silicon / Intel).</summary>
    public static string? Brew => File.Exists("/opt/homebrew/bin/brew") ? "/opt/homebrew/bin/brew" : File.Exists("/usr/local/bin/brew") ? "/usr/local/bin/brew" : null;
    public static string? Mas => File.Exists("/opt/homebrew/bin/mas") ? "/opt/homebrew/bin/mas" : File.Exists("/usr/local/bin/mas") ? "/usr/local/bin/mas" : null;

    /// <summary>Homebrew må ikke køre som root: kør som brugeren på skærmen (eller ejeren af Homebrew-mappen).</summary>
    public static ShellResult AsBrewUser(string cmd, TimeSpan timeout)
    {
        var owner = Session()?.User ?? (Brew is { } b ? Shell.Out($"stat -f %Su {Q(Path.GetDirectoryName(Path.GetDirectoryName(b))!)}") : "");
        if (owner.Length == 0 || owner == "root") return new ShellResult(1, "Ingen bruger at køre Homebrew som (log ind på Mac'en).", false);
        return Shell.Run($"sudo -u {Q(owner)} -H env HOMEBREW_NO_AUTO_UPDATE=1 HOMEBREW_NO_INSTALL_CLEANUP=1 NONINTERACTIVE=1 {cmd}", timeout);
    }

    /// <summary>
    /// mas install/upgrade (mas 2+) kræver root og kalder ellers selv sudo, som beder om en adgangskode. Tjenesten er root: kør mas direkte
    /// i brugerens session (launchctl asuser), så App Store-kontoen på skærmen bruges – uden at nogen skal skrive koden.
    /// </summary>
    public static ShellResult MasAsRoot(string args, TimeSpan timeout)
    {
        var u = Session();
        if (u is null || u.User == "root") return new ShellResult(1, "Log ind på Mac'en (App Store bruger kontoen på skærmen).", false);
        var uid = Shell.Out($"id -u {Q(u.User)}").Trim(); var gid = Shell.Out($"id -g {Q(u.User)}").Trim();
        if (uid.Length == 0) return new ShellResult(1, $"Ukendt bruger {u.User}", false);
        return Shell.Run($"launchctl asuser {uid} env SUDO_USER={Q(u.User)} SUDO_UID={uid} SUDO_GID={gid} MAS_NO_AUTO_INDEX=1 {Mas} {args}", timeout);
    }

    // ------------------------------------------------------------------ fakta
    public static SystemFacts SystemInfo()
    {
        var hw = SpJson("SPHardwareDataType");
        var h = hw is { } d && d.TryGetProperty("SPHardwareDataType", out var arr) && arr.GetArrayLength() > 0 ? arr[0] : default;
        string P(string k) => h.ValueKind == JsonValueKind.Object && h.TryGetProperty(k, out var v) ? v.ToString() : "";
        var ram = long.TryParse(Shell.Out("sysctl -n hw.memsize"), out var mem) ? Math.Round(mem / 1073741824.0, 1) : (double?)null;
        var boot = Regex.Match(Shell.Out("sysctl -n kern.boottime"), @"sec = (\d+)") is { Success: true } m
            ? DateTimeOffset.FromUnixTimeSeconds(long.Parse(m.Groups[1].Value)).ToLocalTime().ToString("yyyy-MM-dd HH:mm") : null;
        return new SystemFacts
        {
            Hostname = Shell.Out("scutil --get ComputerName") is { Length: > 0 } cn ? cn : Environment.MachineName,
            Os = $"macOS {Shell.Out("sw_vers -productVersion")}", OsVersion = Shell.Out("sw_vers -productVersion"), Build = Shell.Out("sw_vers -buildVersion"),
            LastBoot = boot, Manufacturer = "Apple", Model = P("machine_name") is { Length: > 0 } mn ? $"{mn} ({P("machine_model")})" : Shell.Out("sysctl -n hw.model"),
            CpuName = P("chip_type") is { Length: > 0 } chip ? chip : Shell.Out("sysctl -n machdep.cpu.brand_string"),
            Cores = int.TryParse(Shell.Out("sysctl -n hw.physicalcpu"), out var c) ? c : null, Threads = int.TryParse(Shell.Out("sysctl -n hw.logicalcpu"), out var t) ? t : null,
            RamGb = ram, Gpus = [P("chip_type")],
            Nics = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.Name.StartsWith("en"))
                .Select(n => new NicInfo { Name = n.Name, Status = n.OperationalStatus.ToString(), Mac = n.GetPhysicalAddress().GetAddressBytes() is { Length: 6 } b ? string.Join('-', b.Select(x => x.ToString("X2"))) : null }).ToList(),
            TimeZone = Shell.Out("readlink /etc/localtime | sed 's#.*/zoneinfo/##'"), Firmware = "Apple", SecureBoot = true, Edition = "macOS", AgentBuild = "mac",
        };
    }

    public static LiveFacts Live()
    {
        var mem = long.TryParse(Shell.Out("sysctl -n hw.memsize"), out var total) ? total : 0;
        // vm_stat: sider i brug = aktive + wired + komprimerede
        var vm = Shell.Out("vm_stat");
        long Pages(string k) => Regex.Match(vm, k + @":\s+(\d+)") is { Success: true } m ? long.Parse(m.Groups[1].Value) : 0;
        var pageSize = Regex.Match(vm, @"page size of (\d+)") is { Success: true } ps ? long.Parse(ps.Groups[1].Value) : 16384;
        var used = (Pages("Pages active") + Pages("Pages wired down") + Pages("Pages occupied by compressor")) * pageSize;
        var cpu = Regex.Match(Shell.Out("top -l 2 -n 0 -s 1 | grep 'CPU usage' | tail -1", 10), @"([\d.]+)% idle") is { Success: true } ci
            ? Math.Round(100 - double.Parse(ci.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), 1) : (double?)null;
        var boot = Regex.Match(Shell.Out("sysctl -n kern.boottime"), @"sec = (\d+)") is { Success: true } bm ? long.Parse(bm.Groups[1].Value) : 0;
        var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up && n.Name.StartsWith("en")
            && n.GetIPProperties().UnicastAddresses.Any(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork));
        var vols = new List<VolumeInfo>();
        foreach (var path in new[] { "/System/Volumes/Data", "/" })
        {
            try
            {
                var di = new DriveInfo(path);
                if (!di.IsReady || di.TotalSize == 0) continue;
                var size = di.TotalSize / 1073741824.0; var free = di.AvailableFreeSpace / 1073741824.0;
                vols.Add(new VolumeInfo { Drive = "/", SizeGb = Math.Round(size, 1), FreeGb = Math.Round(free, 1), PctUsed = Math.Round(100 - free / size * 100, 1) });
                break;   // APFS: data-volumet er det, der fyldes
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return new LiveFacts
        {
            Hostname = Environment.MachineName, User = Session()?.User,
            Ip = nic?.GetIPProperties().UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.Address.ToString(),
            Mac = nic?.GetPhysicalAddress().GetAddressBytes() is { Length: 6 } b ? string.Join('-', b.Select(x => x.ToString("X2"))) : null,
            Cpu = cpu, Ram = mem > 0 ? Math.Round(used * 100.0 / mem, 1) : null, RamUsedGb = Math.Round(used / 1073741824.0, 1), RamTotalGb = Math.Round(mem / 1073741824.0, 1),
            UptimeH = boot > 0 ? Math.Round((DateTimeOffset.UtcNow.ToUnixTimeSeconds() - boot) / 3600.0, 1) : null,
            BootAt = boot > 0 ? DateTime.UnixEpoch.AddSeconds(boot) : null,
            PendingReboot = false,
            Volumes = vols,
            KeepAwake = KeepAwakeMac.Current,
            Temps = MacTempReader.Current(),   // Apple silicon: CPU-kernernes målere; Intel: SMC/powermetrics; "off", når den er slået fra
        };
    }

    public static HealthReport Health()
    {
        var disks = new List<DiskHealth>();
        try
        {
            if (DiskSmart.FromDiskutil(Shell.Out("diskutil info disk0 2>/dev/null")) is { } disk)
            {
                // smartmontools (Homebrew) giver slid, temperatur og fejl; ellers kun diskutils SMART-status
                var smartctl = new[] { "/opt/homebrew/bin/smartctl", "/usr/local/bin/smartctl" }.FirstOrDefault(File.Exists);
                if (smartctl is not null && Shell.Out($"{smartctl} -a -j disk0 2>/dev/null || true", 20) is { Length: > 0 } sj)
                {
                    var name = disk.Name;
                    disk = DiskSmart.FromSmartctl(sj, disk);
                    if (string.IsNullOrEmpty(disk.Name)) disk.Name = name;
                }
                disks.Add(disk);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException) { Log.Warn($"Diskhelbred: {e.Message}"); }
        var fw = Shell.Out("/usr/libexec/ApplicationFirewall/socketfilterfw --getglobalstate 2>/dev/null");
        var updates = PendingUpdates();
        var panics = Shell.Out("find /Library/Logs/DiagnosticReports -name '*.panic' -mtime -7 2>/dev/null | wc -l");
        return new HealthReport
        {
            Live = Live(), Disks = disks,
            Antivirus = [new AntivirusProduct { Name = "XProtect (indbygget)", Enabled = Shell.Out("spctl --status 2>/dev/null").Contains("enabled"), UpToDate = true }],
            Firewall = new Dictionary<string, bool> { ["Firewall"] = fw.Contains("enabled") || fw.Contains("State = 1") },
            BitLocker = Shell.Out("fdesetup status 2>/dev/null") switch { var s when s.Contains("On") => "FileVault: On", var s when s.Contains("Off") => "FileVault: Off", _ => null },
            Events = new EventSummary { Crashes7d = int.TryParse(panics.Trim(), out var pn) ? pn : null },
            Updates = new UpdateSummary { Pending = updates.Count, Titles = updates.Take(10).ToList() },
            MacAccess = MacSign.Access(),
            MacMenu = MacMenuBar.Report(),
        };
    }

    private const string RebootFlag = "/Library/Application Support/Fleetdeck/reboot-needed";

    /// <summary>Venter Mac'en på en genstart efter en macOS-opdatering? Markøren gælder kun den opstart, den blev skrevet i.</summary>
    public static bool RebootNeeded()
    {
        try { return File.Exists(RebootFlag) && File.ReadAllText(RebootFlag).Trim() == Shell.Out("sysctl -n kern.boottime").Trim(); }
        catch (IOException) { return false; }
    }

    public static void MarkRebootNeeded()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(RebootFlag)!); File.WriteAllText(RebootFlag, Shell.Out("sysctl -n kern.boottime").Trim()); }
        catch (IOException e) { Log.Warn($"Genstartsmarkør: {e.Message}"); }
        catch (UnauthorizedAccessException e) { Log.Warn($"Genstartsmarkør: {e.Message}"); }
    }

    private static (DateTime At, List<string> List)? _updates;
    /// <summary>macOS-opdateringer (softwareupdate -l er langsom og kontakter Apple: højst hver 6. time).</summary>
    public static List<string> PendingUpdates(bool force = false)
    {
        if (!force && _updates is { } u && DateTime.UtcNow - u.At < TimeSpan.FromHours(6)) return u.List;
        var list = Shell.Out("softwareupdate -l 2>/dev/null", 180).Split('\n').Select(l => l.Trim())
            .Where(l => l.StartsWith("* Label:")).Select(l => l["* Label:".Length..].Trim()).ToList();
        _updates = (DateTime.UtcNow, list);
        return list;
    }

    // ------------------------------------------------------------------ programmer
    public static List<SoftwareEntry> Installed()
    {
        var sw = new List<SoftwareEntry>();
        if (SpJson("SPApplicationsDataType", 120) is { } d && d.TryGetProperty("SPApplicationsDataType", out var apps))
        {
            foreach (var a in apps.EnumerateArray())
            {
                var path = a.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
                // kun brugerens programmer (/Applications og ~/Applications), ikke systemets egne
                if (!path.StartsWith("/Applications") && !Regex.IsMatch(path, "^/Users/[^/]+/Applications/")) continue;
                sw.Add(new SoftwareEntry
                {
                    Name = a.TryGetProperty("_name", out var n) ? n.GetString() ?? "" : "", Version = a.TryGetProperty("version", out var v) ? v.GetString() : null,
                    Publisher = a.TryGetProperty("obtained_from", out var o) ? o.GetString() : null, Scope = "app",
                    InstallPath = path, Exe = path, UninstallId = path,
                });
            }
            try { AppInfo(sw); }
            catch (Exception e) { Log.Warn($"Programoplysninger fra Info.plist: {e.Message}"); }   // må aldrig vælte inventaret
        }
        if (Brew is not null)
        {
            foreach (var l in AsBrewUser($"{Brew} list --versions --formula", TimeSpan.FromMinutes(2)).Output.Split('\n'))
                if (l.Split(' ', 2) is [var name, var ver]) sw.Add(new SoftwareEntry { Name = name, Version = ver.Trim(), Scope = "brew" });
            foreach (var l in AsBrewUser($"{Brew} list --versions --cask", TimeSpan.FromMinutes(2)).Output.Split('\n'))
                if (l.Split(' ', 2) is [var name, var ver]) sw.Add(new SoftwareEntry { Name = name, Version = ver.Trim(), Scope = "brew-cask" });
            sw.Add(new SoftwareEntry { Name = "homebrew", Scope = "brew" });
        }
        return sw;
    }

    // Info.plist → (beskrivelse, ikonfil), husket pr. fil og ændringstid
    private static readonly Dictionary<string, (DateTime At, string? Desc, string? Icon)> PlistCache = new();

    /// <summary>
    /// Beskrivelse (CFBundleGetInfoString) og ikon (CFBundleIconFile → .icns → PNG med sips) for programmerne.
    /// XML-plister læses direkte; binære konverteres med plutil – alle i ét shell-kald. Ikonerne huskes, så kun nye programmer koster processer.
    /// </summary>
    private static void AppInfo(List<SoftwareEntry> apps)
    {
        var info = new Dictionary<string, (string? Desc, string? Icon)>();
        var binary = new List<(string Plist, DateTime At)>();
        foreach (var s in apps)
        {
            var plist = s.InstallPath + "/Contents/Info.plist";
            DateTime at;
            try { at = File.GetLastWriteTimeUtc(plist); } catch (Exception) { continue; }
            if (PlistCache.TryGetValue(plist, out var c) && c.At == at) { info[plist] = (c.Desc, c.Icon); continue; }
            try
            {
                var text = File.ReadAllText(plist);
                if (text.StartsWith("bplist")) binary.Add((plist, at));
                else info[plist] = Remember(plist, at, text);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        if (binary.Count > 0)
        {
            const string Sep = "@@fleetdeck-plist@@ ";
            var script = string.Concat(binary.Select(b => $"echo {Q(Sep + b.Plist)}; plutil -convert xml1 -o - {Q(b.Plist)} 2>/dev/null; ")) + "true";
            var parts = Shell.Run(script, TimeSpan.FromSeconds(60), null, int.MaxValue).Output.Split(Sep);
            foreach (var part in parts.Skip(1))
            {
                var nl = part.IndexOf('\n');
                if (nl < 0) continue;
                var plist = part[..nl].Trim();
                var b = binary.FirstOrDefault(x => x.Plist == plist);
                if (b.Plist is not null) info[plist] = Remember(plist, b.At, part[(nl + 1)..]);
            }
        }

        // ikonfilen i Contents/Resources (navnet står med eller uden .icns)
        var sources = new List<(SoftwareEntry S, string Src)>();
        foreach (var s in apps)
        {
            if (!info.TryGetValue(s.InstallPath + "/Contents/Info.plist", out var i)) continue;
            if (string.IsNullOrWhiteSpace(s.Description)) s.Description = SoftwareIcons.Clip(i.Desc);
            if (i.Icon is not { Length: > 0 } icon) continue;
            var res = s.InstallPath + "/Contents/Resources/";
            var file = new[] { res + icon, res + icon + ".icns" }.FirstOrDefault(f => Path.HasExtension(f) && File.Exists(f));
            if (file is not null) sources.Add((s, file));
        }
        if (sources.Count == 0) return;
        var hashes = AppIcons.Load(sources.Select(x => x.Src), (src, outFile) => $"/usr/bin/sips -s format png --resampleHeightWidthMax 48 {Q(src)} --out {Q(outFile)}");
        foreach (var (s, src) in sources)
            if (hashes.GetValueOrDefault(src) is { } h) s.IconHash = h;
    }

    private static (string? Desc, string? Icon) Remember(string plist, DateTime at, string xml)
    {
        string? Key(string k) => Regex.Match(xml, $@"<key>{k}</key>\s*<string>([^<]*)</string>") is { Success: true } m
            ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim() : null;
        var desc = Key("CFBundleGetInfoString");
        var icon = Key("CFBundleIconFile") is { Length: > 0 } f ? f : Key("CFBundleIconName");
        PlistCache[plist] = (at, desc, icon);
        return (desc, icon);
    }

    public static List<UpgradeEntry> Upgrades()
    {
        var list = new List<UpgradeEntry>();
        if (Brew is not null)
        {
            var r = AsBrewUser($"{Brew} outdated --json=v2 --greedy-auto-updates", TimeSpan.FromMinutes(3));
            try
            {
                using var doc = JsonDocument.Parse(r.Output.Length > 0 && r.Output.TrimStart().StartsWith('{') ? r.Output : "{}");
                foreach (var kind in new[] { "formulae", "casks" })
                    if (doc.RootElement.TryGetProperty(kind, out var arr))
                        foreach (var x in arr.EnumerateArray())
                        {
                            var name = x.GetProperty("name").GetString() ?? "";
                            var cur = x.TryGetProperty("installed_versions", out var iv) && iv.GetArrayLength() > 0 ? iv[0].GetString() : null;
                            list.Add(new UpgradeEntry { Name = name, Id = (kind == "casks" ? "cask:" : "") + name, Current = cur,
                                Available = x.TryGetProperty("current_version", out var cv) ? cv.GetString() : null });
                        }
            }
            catch (JsonException) { }
        }
        if (Mas is not null)
            foreach (var l in AsBrewUser($"{Mas} outdated", TimeSpan.FromMinutes(2)).Output.Split('\n'))
                if (Regex.Match(l, @"^(\d+)\s+(.+?)\s+\((.+?)\s+->\s+(.+?)\)") is { Success: true } m)
                    list.Add(new UpgradeEntry { Name = m.Groups[2].Value + " (App Store)", Id = "mas:" + m.Groups[1].Value, Current = m.Groups[3].Value, Available = m.Groups[4].Value });
        foreach (var u in PendingUpdates())
            list.Add(new UpgradeEntry { Name = u + " (macOS)", Id = "macos:" + u });
        return list;
    }

    /// <summary>Installér/fjern: brew (formula), cask:navn, mas:id. Uden præfiks prøves en cask først, så en formula.</summary>
    public static ShellResult InstallOrRemove(string id, bool install, TimeSpan timeout)
    {
        if (id.StartsWith("mas:"))
        {
            if (Mas is null) return new ShellResult(1, "App Store-værktøjet mas mangler – installér udvidelsen \"App Store (mas)\".", false);
            var appId = id[4..];
            return MasAsRoot($"{(install ? "install" : "uninstall")} {Q(appId)}", timeout);
        }
        if (Brew is null) return new ShellResult(1, "Homebrew mangler – installér udvidelsen \"Homebrew\" først.", false);
        var (kind, name) = id.StartsWith("cask:") ? ("--cask", id[5..]) : id.StartsWith("formula:") ? ("--formula", id[8..]) : ("", id);
        if (install)
            return kind.Length > 0 ? AsBrewUser($"{Brew} install {kind} {Q(name)}", timeout)
                : AsBrewUser($"{Brew} install --cask {Q(name)} 2>/dev/null || {Brew} install {Q(name)}", timeout);
        return AsBrewUser($"{Brew} uninstall {kind} {Q(name)}", timeout);
    }

    public static ShellResult UpgradeOne(string id, TimeSpan timeout) =>
        id.StartsWith("macos:") ? InstallMacOs(Q(id[6..]), timeout)
        : id.StartsWith("mas:") ? (Mas is null ? new ShellResult(1, "mas mangler", false) : MasAsRoot($"upgrade {Q(id[4..])}", timeout))
        : Brew is null ? new ShellResult(1, "Homebrew mangler", false)
        : AsBrewUser($"{Brew} upgrade {(id.StartsWith("cask:") ? "--cask " + Q(id[5..]) : Q(id))}", timeout);

    /// <summary>Opdatér alt: Homebrew, App Store og (hvis valgt) macOS. '::ws a-b tekst' = fremdrift.</summary>
    public static ShellResult UpgradeAll(IReadOnlyList<string> exclude, bool macos, TimeSpan timeout)
    {
        var outp = new System.Text.StringBuilder(); var rc = 0;
        void Add(ShellResult r) { outp.AppendLine(r.Output); if (r.ExitCode != 0) rc = r.ExitCode; }
        if (Brew is not null)
        {
            LiveJob.Line(LiveJob.Current, "::ws 0-50 Opdaterer Homebrew-programmer");
            var pins = exclude.Where(x => x.Length > 0).Select(x => Q(x.Replace("cask:", ""))).ToList();
            // undtagelser fra profilen: "brew pin" holder en formula fast under opdateringen
            var pin = pins.Count > 0 ? $"{Brew} pin {string.Join(' ', pins)} 2>/dev/null; " : "";
            var unpin = pins.Count > 0 ? $"; {Brew} unpin {string.Join(' ', pins)} 2>/dev/null" : "";
            Add(AsBrewUser($"bash -c {Q($"{pin}{Brew} update --quiet; {Brew} upgrade --greedy-auto-updates{unpin}")}", timeout));
        }
        if (Mas is not null) { LiveJob.Line(LiveJob.Current, "::ws 50-70 Opdaterer App Store-programmer"); Add(MasAsRoot("upgrade", timeout)); }
        if (macos)
        {
            LiveJob.Line(LiveJob.Current, "::ws 70-100 Installerer macOS-opdateringer");
            var r = InstallMacOs("-a", timeout);
            Add(r);
            if (r.ExitCode == 0 && System.Text.RegularExpressions.Regex.IsMatch(r.Output, "restart", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) MarkRebootNeeded();
            PendingUpdates(true);
        }
        return new ShellResult(rc, outp.ToString(), false);
    }

    /// <summary>
    /// macOS-opdateringer: på Apple Silicon kræver selve installationen en ejers adgangskode ("Failed to authenticate"),
    /// så root alene kan kun hente dem. Fleetdeck henter derfor opdateringen og beder brugeren på skærmen om at trykke
    /// "Genstart nu" i Softwareopdatering (én adgangskode). Med MDM kan det senere ske helt uden bruger.
    /// </summary>
    public static ShellResult InstallMacOs(string what, TimeSpan timeout)
    {
        var r = Shell.Run($"softwareupdate -i {what} --agree-to-license", timeout);
        var needsOwner = r.ExitCode != 0 && (r.Output.Contains("Failed to authenticate", StringComparison.OrdinalIgnoreCase)
            || r.Output.Contains("authenticat", StringComparison.OrdinalIgnoreCase) && r.Output.Contains("Password", StringComparison.Ordinal));
        if (!needsOwner) return r;
        var shown = OpenSoftwareUpdate("Fleetdeck har hentet macOS-opdateringen. Åbn Softwareopdatering og tryk \"Genstart nu\" – den beder om din adgangskode én gang.");
        var clean = r.Output.Replace("Password:", "").TrimEnd();
        return new ShellResult(0, clean + "\nmacOS-opdateringen er hentet. På en Mac med Apple-chip skal ejeren selv godkende installationen med sin adgangskode"
            + (shown ? " – Softwareopdatering er åbnet på skærmen med en besked." : " – der var ingen logget ind, så den venter til næste gang."), false);
    }

    /// <summary>Viser en besked og åbner Softwareopdatering for den, der er logget ind.</summary>
    public static bool OpenSoftwareUpdate(string message)
    {
        var s = Session();
        if (s is null) return false;
        Notify(message);
        Shell.Run($"launchctl asuser {s.Uid} sudo -u {Q(s.User)} open 'x-apple.systempreferences:com.apple.Software-Update-Settings.extension'", TimeSpan.FromSeconds(20));
        return true;
    }

    // ------------------------------------------------------------------ handlinger
    public static bool Notify(string text)
    {
        var s = Session();
        if (s is null || text.Length == 0) return false;
        var esc = text.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return Shell.Run($"launchctl asuser {s.Uid} sudo -u {Q(s.User)} osascript -e {Q($"display dialog \"{esc}\" with title \"Fleetdeck\" buttons {{\"OK\"}} default button 1 giving up after 600")} >/dev/null 2>&1 &",
            TimeSpan.FromSeconds(10)).ExitCode == 0;
    }

    public static ShellResult Lock() => Session() is { } s
        ? Shell.Run($"launchctl asuser {s.Uid} sudo -u {Q(s.User)} pmset displaysleepnow", TimeSpan.FromSeconds(15))
        : new ShellResult(0, "Ingen er logget ind.", false);

    public static ShellResult Logoff() => Session() is { } s
        ? Shell.Run($"launchctl bootout gui/{s.Uid}", TimeSpan.FromSeconds(30))
        : new ShellResult(0, "Ingen er logget ind.", false);

    /// <summary>Installér som launchd-tjeneste (kaldes som root): kopiér agenten, skriv plist og start den.</summary>
    public static int InstallService()
    {
        var exe = Dir + "/fleetdeck-agent";
        Directory.CreateDirectory(Dir);
        Shell.Run($"launchctl bootout system/{Label} 2>/dev/null || true", TimeSpan.FromSeconds(30));
        // altid via en kopi: signér kopien med den faste lokale identitet (Fuld diskadgang overlever så opdateringer) og læg den på plads.
        // Den kørende fil må ikke ændres direkte – macOS dræber et program, hvis signatur ændres under det.
        var self = Environment.ProcessPath!;
        var version = MacSign.ReportedVersion(self);
        File.Copy(self, exe + ".new", true);
        File.SetUnixFileMode(exe + ".new", (UnixFileMode)0b111_101_101);
        MacSign.Sign(exe + ".new", version);
        File.Move(exe + ".new", exe, true);
        File.WriteAllText(Plist, $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0"><dict>
              <key>Label</key><string>{Label}</string>
              <key>ProgramArguments</key><array><string>{exe}</string></array>
              <key>RunAtLoad</key><true/>
              <key>KeepAlive</key><true/>
              <key>ProcessType</key><string>Background</string>
              <key>LowPriorityIO</key><true/>
              <key>Nice</key><integer>5</integer>
              <key>StandardOutPath</key><string>/var/log/fleetdeck-agent.log</string>
              <key>StandardErrorPath</key><string>/var/log/fleetdeck-agent.log</string>
            </dict></plist>
            """);
        var r = Shell.Run($"chown root:wheel {Plist}; chmod 644 {Plist}; launchctl bootstrap system {Plist} && launchctl enable system/{Label}", TimeSpan.FromSeconds(60));
        KeepAwakeMac.EnsureWatchdog();   // vagten for "Hold vågen på strøm" (gør intet, før Fleetdeck har slået dvale fra)
        Console.WriteLine(r.ExitCode == 0 ? "Fleetdeck: færdig. Agenten kører som launchd-tjenesten com.fleetdeck.agent (log: /var/log/fleetdeck-agent.log)." : r.Output);
        return r.ExitCode == 0 ? 0 : 4;
    }

    /// <summary>Fjernet fra Fleetdeck: stop tjenesten, slå dvale til igen, hvis Fleetdeck satte den ("Hold vågen"), fjern vagten og alt andet.</summary>
    public static void StartRemoval()
    {
        var script = $"sleep 5; launchctl bootout system/{Label}; {MacKeepAwake.RemovalScript()}rm -f {Plist}; rm -rf {Dir} /etc/fleetdeck";
        Shell.Run($"nohup /bin/sh -c {Q(script)} >/dev/null 2>&1 &", TimeSpan.FromSeconds(10));
    }

    private static JsonElement? SpJson(string type, int seconds = 60)
    {
        var o = Shell.Out($"system_profiler {type} -json 2>/dev/null", seconds);
        if (o.Length == 0) return null;
        try { using var d = JsonDocument.Parse(o); return d.RootElement.Clone(); } catch (JsonException) { return null; }
    }
}
