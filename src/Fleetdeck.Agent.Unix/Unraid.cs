using System.Text.Json;
using System.Text.RegularExpressions;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Unix;

/// <summary>
/// Unraid: Slackware uden systemd, og hele systemet køres fra RAM – kun USB-stikket (/boot) overlever en genstart.
/// Agenten og dens konfiguration gemmes derfor i /boot/config/plugins/fleetdeck og startes fra /boot/config/go.
/// "Programmer" er plugins og Docker-containere; opdateringer er containere med en nyere version.
/// </summary>
public static class Unraid
{
    public static bool Is => File.Exists("/etc/unraid-version");
    public const string Flash = "/boot/config/plugins/fleetdeck";
    public const string Ram = "/usr/local/fleetdeck";
    private const string Go = "/boot/config/go";
    private const string GoLine = "bash /boot/config/plugins/fleetdeck/start.sh &   # Fleetdeck-agenten";
    private static string Q(string s) => Pkg.Q(s);

    public static string Version => Regex.Match(Read("/etc/unraid-version"), "version=\"([^\"]+)\"") is { Success: true } m ? m.Groups[1].Value : "";

    /// <summary>Installér: agent og konfiguration på USB-stikket, start-script, linje i go-filen – og start nu.</summary>
    public static int InstallService()
    {
        Directory.CreateDirectory(Flash);
        Directory.CreateDirectory(Ram);
        var self = Path.GetFullPath(Environment.ProcessPath!);
        if (self != Ram + "/fleetdeck-agent")
        {
            File.Copy(self, Ram + "/fleetdeck-agent.new", true);
            File.SetUnixFileMode(Ram + "/fleetdeck-agent.new", (UnixFileMode)0b111_101_101);
            File.Move(Ram + "/fleetdeck-agent.new", Ram + "/fleetdeck-agent", true);
        }
        File.Copy(Ram + "/fleetdeck-agent", Flash + "/fleetdeck-agent", true);
        if (File.Exists("/etc/fleetdeck/agent.json")) File.Copy("/etc/fleetdeck/agent.json", Flash + "/agent.json", true);
        EnsureScripts();
        var go = File.Exists(Go) ? File.ReadAllText(Go) : "#!/bin/bash\n";
        if (!go.Contains("plugins/fleetdeck/start.sh"))
            File.WriteAllText(Go, go.TrimEnd('\n') + "\n" + GoLine + "\n");
        // stop en tidligere agent via PID-filerne (aldrig pkill -f: det rammer også den skal, der kører kommandoen)
        Shell.Run($"{StopCmd}; sleep 1; bash {Flash}/start.sh", TimeSpan.FromSeconds(30));
        Console.WriteLine("Fleetdeck: færdig. Agenten kører på Unraid og starter selv efter genstart (log: /var/log/fleetdeck-agent.log).");
        return 0;
    }

    /// <summary>Stopper agenten via PID-filerne (sløjfen og agenten).</summary>
    public static void Stop() => Shell.Run(StopCmd, TimeSpan.FromSeconds(30));

    public static void StartRemoval() =>
        Shell.Run($"nohup /bin/bash -c 'sleep 5; sed -i \"/plugins\\/fleetdeck\\/start.sh/d\" {Go}; rm -rf /etc/fleetdeck; {StopCmd}; rm -rf {Flash} {Ram}' >/dev/null 2>&1 &",
            TimeSpan.FromSeconds(10));

    private const string StopCmd = "for f in /var/run/fleetdeck-loop.pid /var/run/fleetdeck-agent.pid; do [ -f $f ] && kill $(cat $f) 2>/dev/null; rm -f $f; done";

    /// <summary>Skriver start- og sløjfe-scriptet (USB-stik og RAM), hvis de mangler eller er ændret – også ved hver opstart.</summary>
    public static void EnsureScripts()
    {
        try
        {
            Directory.CreateDirectory(Flash); Directory.CreateDirectory(Ram);
            foreach (var (path, text) in new[] { (Flash + "/start.sh", StartScript), (Flash + "/loop.sh", LoopScript), (Ram + "/loop.sh", LoopScript) })
            {
                var want = text.Replace("\r\n", "\n") + "\n";
                if (File.Exists(path) && File.ReadAllText(path) == want) continue;
                // ny fil + omdøbning: et kørende bash-script beholder sin gamle kopi (bash læser scriptet undervejs)
                File.WriteAllText(path + ".new", want);
                File.Move(path + ".new", path, true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warn($"Unraid-scripts kunne ikke skrives: {e.Message}"); }
    }

    /// <summary>Kopierer agent og konfiguration fra USB-stikket til RAM og starter sløjfen (højst én ad gangen).</summary>
    private const string StartScript = """
        #!/bin/bash
        # Fleetdeck-agenten på Unraid – startes fra /boot/config/go (lagt der af agentens installation)
        F=/boot/config/plugins/fleetdeck; R=/usr/local/fleetdeck
        mkdir -p $R /etc/fleetdeck && chmod 700 /etc/fleetdeck
        [ -x $R/fleetdeck-agent ] || { cp $F/fleetdeck-agent $R/fleetdeck-agent && chmod 755 $R/fleetdeck-agent; }
        [ -f /etc/fleetdeck/agent.json ] || { cp $F/agent.json /etc/fleetdeck/agent.json 2>/dev/null && chmod 600 /etc/fleetdeck/agent.json; }
        cp $F/loop.sh $R/loop.sh 2>/dev/null
        P=/var/run/fleetdeck-loop.pid
        [ -f $P ] && kill -0 $(cat $P) 2>/dev/null && exit 0   # kører allerede
        nohup setsid /bin/bash $R/loop.sh >/dev/null 2>&1 < /dev/null &
        """;

    /// <summary>Holder agenten kørende (som systemd's Restart=always) og gemmer ny version/konfiguration på USB-stikket.</summary>
    private const string LoopScript = """
        #!/bin/bash
        F=/boot/config/plugins/fleetdeck; R=/usr/local/fleetdeck; L=/var/log/fleetdeck-agent.log
        echo $$ > /var/run/fleetdeck-loop.pid
        trap '[ -f /var/run/fleetdeck-agent.pid ] && kill $(cat /var/run/fleetdeck-agent.pid) 2>/dev/null; rm -f /var/run/fleetdeck-*.pid; exit 0' TERM INT
        while true; do
          [ -f $L ] && [ $(stat -c %s $L) -gt 5000000 ] && mv -f $L $L.1
          nice -n 10 ionice -c 3 $R/fleetdeck-agent >> $L 2>&1 &
          echo $! > /var/run/fleetdeck-agent.pid
          wait $!
          cmp -s /etc/fleetdeck/agent.json $F/agent.json || cp /etc/fleetdeck/agent.json $F/agent.json
          cmp -s $R/fleetdeck-agent $F/fleetdeck-agent || cp $R/fleetdeck-agent $F/fleetdeck-agent
          [ -f /etc/fleetdeck/agent.json ] || { rm -f /var/run/fleetdeck-*.pid; exit 0; }   # agenten er fjernet
          sleep 5
        done
        """;

    // ------------------------------------------------------------------ programmer: plugins og containere
    public static List<SoftwareEntry> Installed()
    {
        var sw = new List<SoftwareEntry> { new() { Name = "Unraid", Version = Version, Publisher = "Lime Technology", Scope = "unraid" } };
        foreach (var plg in Directory.Exists("/boot/config/plugins") ? Directory.GetFiles("/boot/config/plugins", "*.plg") : [])
        {
            var head = string.Join("", File.ReadLines(plg).Take(40));
            string Ent(string n) => Regex.Match(head, $"<!ENTITY\\s+{n}\\s+\"([^\"]*)\"") is { Success: true } m ? m.Groups[1].Value : "";
            sw.Add(new SoftwareEntry { Name = Path.GetFileNameWithoutExtension(plg), Version = Ent("version"), Publisher = Ent("author"), Scope = "plugin" });
        }
        foreach (var l in Shell.Out("docker ps -a --format '{{.Names}}\t{{.Image}}\t{{.State}}' 2>/dev/null", 30).Split('\n'))
            if (l.Split('\t') is [var name, var image, var state])
                sw.Add(new SoftwareEntry { Name = name, Version = image, Publisher = state, Scope = "docker" });
        return sw;
    }

    /// <summary>Containere, som Unraid har fundet en nyere version til (Docker-fanens "update ready").</summary>
    public static List<UpgradeEntry> Upgrades()
    {
        var list = new List<UpgradeEntry>();
        const string status = "/var/lib/docker/unraid-update-status.json";
        if (!File.Exists(status)) return list;
        var running = Shell.Out("docker ps -a --format '{{.Names}}\t{{.Image}}' 2>/dev/null", 30).Split('\n')
            .Select(l => l.Split('\t')).Where(f => f.Length == 2).ToList();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(status));
            foreach (var (name, image) in running.Select(f => (f[0], f[1])))
            {
                var key = image.Contains(':') ? image : image + ":latest";
                if (doc.RootElement.TryGetProperty(key, out var e) && e.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String && st.GetString() == "false")
                    list.Add(new UpgradeEntry { Name = $"{name} (container)", Id = "docker:" + name, Current = image });
            }
        }
        catch (Exception e) when (e is JsonException or IOException) { }
        return list;
    }

    public static ShellResult UpgradeOne(string id, TimeSpan timeout) =>
        id.StartsWith("docker:") ? Shell.Run($"/usr/local/emhttp/plugins/dynamix.docker.manager/scripts/update_container {Q(id[7..])}", timeout)
        : id.StartsWith("plugin:") ? Shell.Run($"/usr/local/sbin/plugin update {Q(id[7..] + ".plg")}", timeout)
        : new ShellResult(1, "Ukendt opdatering på Unraid: " + id, false);

    /// <summary>Opdatér alt: plugins og containere med en nyere version (undtagelser springes over).</summary>
    public static ShellResult UpgradeAll(IReadOnlyList<string> exclude, TimeSpan timeout)
    {
        var skip = new HashSet<string>(exclude, StringComparer.OrdinalIgnoreCase);
        var outp = new System.Text.StringBuilder(); var rc = 0;
        LiveJob.Line(LiveJob.Current, "::ws 0-40 Opdaterer plugins");
        foreach (var plg in Directory.GetFiles("/boot/config/plugins", "*.plg").Select(Path.GetFileName))
        {
            if (skip.Contains("plugin:" + Path.GetFileNameWithoutExtension(plg))) continue;
            var r = Shell.Run($"/usr/local/sbin/plugin update {Q(plg!)}", TimeSpan.FromMinutes(10));
            if (r.Output.Contains("updated", StringComparison.OrdinalIgnoreCase) || r.Output.Contains("installing", StringComparison.OrdinalIgnoreCase))
                outp.AppendLine($"Plugin {plg}: opdateret");
        }
        var ups = Upgrades().Where(u => !skip.Contains(u.Id)).ToList();
        var n = 0;
        foreach (var u in ups)
        {
            LiveJob.Line(LiveJob.Current, $"::ws {40 + 60 * n / Math.Max(1, ups.Count)}-{40 + 60 * ++n / Math.Max(1, ups.Count)} Opdaterer {u.Name}");
            var r = UpgradeOne(u.Id, timeout);
            outp.AppendLine(r.ExitCode == 0 ? $"OK  {u.Name}" : $"!!  {u.Name}: kode {r.ExitCode}");
            if (r.ExitCode != 0) rc = r.ExitCode;
        }
        if (ups.Count == 0) outp.AppendLine("Ingen containere med en nyere version.");
        return new ShellResult(rc, outp.ToString(), false);
    }

    /// <summary>Array-status til helbred: kører arrayet, og hvor mange paritetsfejl er der fundet.</summary>
    public static (string State, int? SyncErrors) Array()
    {
        var ini = Read("/var/local/emhttp/var.ini");
        string V(string k) => Regex.Match(ini, $"^{k}=\"([^\"]*)\"", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value : "";
        return (V("mdState"), int.TryParse(V("sbSyncErrs"), out var e) ? e : null);
    }

    // ------------------------------------------------------------------ oversigt til Fleetdecks Unraid-fane

    /// <summary>Array, diske, containere (med ikon, WebUI, opdatering, CPU/RAM) og VM'er.</summary>
    public static UnraidInfo Info()
    {
        var (state, errs) = Array();
        var info = new UnraidInfo { Version = Version, ArrayState = state, SyncErrors = errs, Collected = DateTime.UtcNow };
        var var = Read("/var/local/emhttp/var.ini");
        string V(string k) => Regex.Match(var, $"^{k}=\"([^\"]*)\"", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value : "";
        if (long.TryParse(V("sbSynced"), out var synced) && synced > 0) info.ParityLast = DateTimeOffset.FromUnixTimeSeconds(synced).UtcDateTime;
        if (long.TryParse(V("mdResyncPos"), out var pos) && long.TryParse(V("mdResyncSize"), out var size) && pos > 0 && size > 0)
            info.ParityProgress = (int)(pos * 100 / size);

        // diske fra disks.ini (en sektion pr. disk)
        foreach (Match sec in Regex.Matches(Read("/var/local/emhttp/disks.ini"), @"\[""?([^\]""]+)""?\]([^\[]*)"))
        {
            var body = sec.Groups[2].Value;
            string D(string k) => Regex.Match(body, $"^{k}=\"([^\"]*)\"", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value : "";
            var status = D("status");
            if (status.StartsWith("DISK_NP")) continue;   // tom plads (fx ingen paritetsdisk)
            double? Gb(string k) => long.TryParse(D(k), out var kb) && kb > 0 ? Math.Round(kb / 1_048_576.0, 1) : null;
            var used = Gb("fsUsed") ?? (Gb("fsSize") is { } fs && Gb("fsFree") is { } ff ? Math.Round(fs - ff, 1) : null);
            info.Disks.Add(new UnraidDisk
            {
                Name = D("name"), Type = D("type") is { Length: > 0 } ty ? ty : sec.Groups[1].Value == "flash" ? "Flash" : "", Device = D("device") is { Length: > 0 } dv ? dv : null,
                Status = status, TempC = int.TryParse(D("temp"), out var tc) ? tc : null, SizeGb = Gb("fsSize") ?? Gb("size"), UsedGb = used,
                FsType = D("fsType") is { Length: > 0 } ft ? ft : null, Ssd = D("rotational") switch { "0" => true, "1" => false, _ => null },
            });
        }

        // containere: docker inspect (alle på én gang) + skabelonernes ikon og WebUI + opdateringsstatus + forbrug
        var templates = Templates();
        var updates = Upgrades().Select(u => u.Id[7..]).ToHashSet();
        var autostart = File.Exists("/var/lib/docker/unraid-autostart")
            ? File.ReadAllLines("/var/lib/docker/unraid-autostart").Select(l => l.Split(' ')[0]).ToHashSet() : [];
        var stats = Shell.Out("docker stats --no-stream --format '{{.Name}}\t{{.CPUPerc}}\t{{.MemUsage}}' 2>/dev/null", 30).Split('\n')
            .Select(l => l.Split('\t')).Where(f => f.Length == 3).ToDictionary(f => f[0], f => f);
        var hostIp = Collect.Live().Ip ?? "";
        var ids = Shell.Out("docker ps -aq 2>/dev/null", 20).Replace('\n', ' ').Trim();
        if (ids.Length > 0 && Shell.Out($"docker inspect {ids} 2>/dev/null", 60) is { Length: > 0 } json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var c in doc.RootElement.EnumerateArray())
                {
                    var name = c.GetProperty("Name").GetString()!.TrimStart('/');
                    var net = c.GetProperty("HostConfig").GetProperty("NetworkMode").GetString() ?? "";
                    string? ip = null;
                    if (c.GetProperty("NetworkSettings").TryGetProperty("Networks", out var nets))
                        foreach (var n in nets.EnumerateObject()) if (n.Value.TryGetProperty("IPAddress", out var a) && a.GetString() is { Length: > 0 } ipv) { ip = ipv; break; }
                    var ports = new Dictionary<string, string>();
                    if (c.GetProperty("NetworkSettings").TryGetProperty("Ports", out var ps) && ps.ValueKind == JsonValueKind.Object)
                        foreach (var p in ps.EnumerateObject())
                            if (p.Value.ValueKind == JsonValueKind.Array && p.Value.GetArrayLength() > 0) ports[p.Name.Split('/')[0]] = p.Value[0].GetProperty("HostPort").GetString() ?? "";
                    templates.TryGetValue(name, out var tpl);
                    var st = c.GetProperty("State");
                    stats.TryGetValue(name, out var sf);
                    info.Containers.Add(new UnraidContainer
                    {
                        Name = name, Id = c.TryGetProperty("Id", out var cidp) ? cidp.GetString() : null, Image = c.GetProperty("Config").GetProperty("Image").GetString() ?? "", State = st.GetProperty("Status").GetString() ?? "",
                        Started = st.GetProperty("Running").GetBoolean() && DateTime.TryParse(st.GetProperty("StartedAt").GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var sa) ? sa : null,
                        Icon = tpl.Icon, WebUi = WebUi(tpl.WebUi, net, ip, hostIp, ports), UpdateAvailable = updates.Contains(name), Autostart = autostart.Contains(name),
                        Cpu = sf is not null && double.TryParse(sf[1].TrimEnd('%'), System.Globalization.CultureInfo.InvariantCulture, out var cpu) ? Math.Round(cpu, 1) : null,
                        MemMb = sf is not null ? MemMb(sf[2].Split('/')[0].Trim()) : null,
                        Network = net, Ip = net is "host" or "bridge" ? hostIp : ip,
                    });
                }
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { Log.Warn($"Unraid-containere: {e.Message}"); }
        }
        info.Containers = info.Containers.OrderByDescending(c => c.State == "running").ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();

        // VM'er (libvirt)
        if (Shell.Has("virsh"))
            foreach (var vm in Shell.Out("virsh list --all --name 2>/dev/null", 20).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var di = Shell.Out($"virsh dominfo {Q(vm)} 2>/dev/null", 15);
                string I(string k) => Regex.Match(di, $"^{k}:\\s+(.+)$", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value.Trim() : "";
                info.Vms.Add(new UnraidVm
                {
                    Name = vm, State = I("State"), Vcpus = int.TryParse(I("CPU\\(s\\)"), out var cpus) ? cpus : null,
                    MemoryMb = Regex.Match(I("Max memory"), @"(\d+)") is { Success: true } mm ? (int)(long.Parse(mm.Value) / 1024) : null,
                    Autostart = I("Autostart") == "enable",
                });
            }
        info.Shares = Shares();
        return info;
    }

    /// <summary>Delte mapper fra /boot/config/shares/*.cfg (filnavn = navn); ellers mapperne i /mnt/user. Ingen processer.</summary>
    public static List<UnraidShare> Shares()
    {
        var list = new List<UnraidShare>();
        try
        {
            const string cfgDir = "/boot/config/shares";
            if (Directory.Exists(cfgDir))
                foreach (var f in Directory.GetFiles(cfgDir, "*.cfg"))
                {
                    var cfg = Read(f);
                    string C(string k) => Regex.Match(cfg, $"^{k}=\"([^\"]*)\"", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value.Trim() : "";
                    var export = C("shareExport");
                    list.Add(new UnraidShare { Name = Path.GetFileNameWithoutExtension(f), Comment = C("shareComment") is { Length: > 0 } c ? c : null, Smb = export != "-" });
                }
            // Det, Samba faktisk deler (også ekstra afsnit fra smb-extra.conf, fx en mappe inde i en anden):
            // en Unraid-share kan stå som delt i sin .cfg uden at være med, og omvendt
            var smb = Shell.Out("testparm -s 2>/dev/null | grep -o '^\\[[^]]*\\]'", 20)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim('[', ']')).Where(x => x is not ("global" or "homes" or "printers" or "print$")).ToList();
            if (smb.Count > 0)
            {
                var comments = list.ToDictionary(x => x.Name, x => x.Comment, StringComparer.OrdinalIgnoreCase);
                list = smb.Select(n => new UnraidShare { Name = n, Comment = comments.GetValueOrDefault(n), Smb = true }).ToList();
            }
            if (list.Count == 0 && Directory.Exists("/mnt/user"))
                list.AddRange(Directory.GetDirectories("/mnt/user").Select(d => new UnraidShare { Name = Path.GetFileName(d), Smb = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warn($"Unraid-shares: {e.Message}"); }
        return list.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static Dictionary<string, (string? Icon, string? WebUi)> Templates()
    {
        var map = new Dictionary<string, (string?, string?)>(StringComparer.OrdinalIgnoreCase);
        const string dir = "/boot/config/plugins/dockerMan/templates-user";
        if (!Directory.Exists(dir)) return map;
        foreach (var f in Directory.GetFiles(dir, "my-*.xml"))
        {
            var x = Read(f);
            string? T(string tag) => Regex.Match(x, $"<{tag}>([^<]*)</{tag}>") is { Success: true } m && m.Groups[1].Value.Trim() is { Length: > 0 } v ? System.Net.WebUtility.HtmlDecode(v) : null;
            if (T("Name") is { } n) map[n] = (T("Icon"), T("WebUI"));
        }
        return map;
    }

    /// <summary>Skabelonens WebUI ("http://[IP]:[PORT:8080]/") gjort til en rigtig adresse.</summary>
    private static string? WebUi(string? tpl, string net, string? ip, string hostIp, Dictionary<string, string> ports)
    {
        if (string.IsNullOrWhiteSpace(tpl)) return null;
        var useHost = net is "bridge" or "host" || ip is null;
        var url = tpl.Replace("[IP]", useHost ? hostIp : ip);
        url = Regex.Replace(url, @"\[PORT:(\d+)\]", m => useHost && net == "bridge" && ports.TryGetValue(m.Groups[1].Value, out var hp) && hp.Length > 0 ? hp : m.Groups[1].Value);
        return url.Contains('[') ? null : url;
    }

    private static double? MemMb(string s)
    {
        var m = Regex.Match(s, @"([\d.]+)\s*([KMG]i?B)", RegexOptions.IgnoreCase);
        if (!m.Success || !double.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var v)) return null;
        return Math.Round(m.Groups[2].Value.ToUpperInvariant()[0] switch { 'K' => v / 1024, 'G' => v * 1024, _ => v }, 0);
    }

    /// <summary>Genstart/sluk via Unraids eget powerdown, så arrayet stoppes korrekt.</summary>
    public static string PowerCmd(bool reboot) => File.Exists("/usr/local/sbin/powerdown") ? $"/usr/local/sbin/powerdown{(reboot ? " -r" : "")}" : reboot ? "reboot" : "poweroff";

    private static string Read(string f) { try { return File.ReadAllText(f); } catch (Exception) { return ""; } }
}
