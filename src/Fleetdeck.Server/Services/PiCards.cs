using System.Collections.Concurrent;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fleetdeck.Server.Services;

/// <summary>Et basisbillede fra downloads.raspberrypi.com (kataloget Services/pi-images.json). "firstrun": "systemd-run" = Fleetdecks
/// førstestart er afprøvet på netop dette billede; "unverified" = vises, men kan ikke bruges til et kort endnu.</summary>
public sealed record PiImage(string Id, string Title, string Debian, string Arch, List<string> Models, string Url, string Sha256, long Size, string Firstrun)
{
    public bool Usable => Firstrun == "systemd-run";
}

/// <summary>Indstillingerne, der skrives på kortet (fleetdeck/settings.env). Ingen hemmeligheder: engangskoden ligger i sin egen fil.</summary>
public sealed record PiCardSettings(string Server, string TlsPin, string Name, string User, string Mode, int ProfileId,
    string IpMode, string Iface, string Address, string Gateway, string Dns, string Timezone, string Keymap, string WlanCountry);

/// <summary>Ren logik for SD-kort til Raspberry Pi – ingen filer, ingen processer – så det hele kan testes.</summary>
public static partial class PiCardLogic
{
    public static IReadOnlyList<PiImage> Catalog { get; } = LoadCatalog();

    private static List<PiImage> LoadCatalog()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("pi-images.json")
                      ?? throw new InvalidOperationException("pi-images.json mangler i programmet");
        return JsonSerializer.Deserialize<List<PiImage>>(s, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
    }

    public static PiImage? Image(string? id) => Catalog.FirstOrDefault(i => i.Id == id);

    // ---------------------------------------------------------------- gendannelsespunktets system

    /// <summary>Hvad gendannelsespunktet blev taget på: Debian-udgave og pakkearkitektur.</summary>
    public sealed record KitSystem(string? Debian, string? Arch, string Raw, bool ArchCertain);

    private static readonly Dictionary<string, string> DebianNames = new() { ["11"] = "bullseye", ["12"] = "bookworm", ["13"] = "trixie", ["14"] = "forky" };

    /// <summary>
    /// system.txt fra gendannelsespunktet: "PRETTY_NAME uname-m værtsnavn dato", fx "Raspbian GNU/Linux 12 (bookworm) armv7l minpi 2026-11-19T01:30:00+01:00".
    /// Nyere punkter har også "dpkg=armhf" (pakkearkitekturen – en 32-bit Pi OS kan køre en 64-bit kerne, så uname -m siger ikke alt).
    /// </summary>
    public static KitSystem ParseSystem(string systemTxt)
    {
        var raw = systemTxt.Trim();
        string? debian = CodeName().Match(raw) is { Success: true } c ? c.Groups[1].Value.ToLowerInvariant() : null;
        if (debian is null && DebianVersion().Match(raw) is { Success: true } v) DebianNames.TryGetValue(v.Groups[1].Value, out debian);
        if (DpkgArch().Match(raw) is { Success: true } d) return new(debian, d.Groups[1].Value, raw, true);
        string? arch = null; var certain = false;
        if (Regex.IsMatch(raw, @"\barmv(6|7)\w*\b")) { arch = "armhf"; certain = true; }   // en 32-bit kerne kører kun 32-bit pakker
        else if (Regex.IsMatch(raw, @"\baarch64\b")) arch = "arm64";                        // kan være en 32-bit Pi OS med 64-bit kerne
        return new(debian, arch, raw, certain);
    }

    /// <summary>Basisbilledet til et nyt kort ud fra gendannelsespunktet. Et forkert valg afvises med en forklaring: Python-miljøer og
    /// pakker fra én udgave eller arkitektur virker ikke på en anden.</summary>
    public static (PiImage? Image, string? Error) MatchBase(KitSystem sys, string? wantedId)
    {
        if (sys.Debian is null || sys.Arch is null)
            return (null, $"Gendannelsespunktet siger ikke, hvilket system det er taget på (\"{Short(sys.Raw)}\"). Tag et nyt punkt, så Fleetdeck kan vælge det rigtige basisbillede.");
        var match = Catalog.Where(i => i.Debian == sys.Debian && i.Arch == sys.Arch).ToList();
        if (wantedId is { Length: > 0 })
        {
            var w = Image(wantedId);
            if (w is null) return (null, "Basisbilledet findes ikke i kataloget.");
            if (w.Debian != sys.Debian || w.Arch != sys.Arch)
                return (null, $"{w.Title} passer ikke til gendannelsespunktet, som er taget på {Pretty(sys)}. Programmer og Python-miljøer fra punktet virker kun på samme udgave og arkitektur.");
            if (!w.Usable) return (null, $"{w.Title} kan ikke bruges til et kort endnu: Fleetdecks førstestart er ikke afprøvet på det billede.");
            return (w, null);
        }
        var usable = match.FirstOrDefault(i => i.Usable);
        if (usable is not null) return (usable, null);
        if (match.Count > 0) return (null, $"Der findes et basisbillede til {Pretty(sys)} ({match[0].Title}), men Fleetdecks førstestart er ikke afprøvet på det endnu.");
        return (null, $"Fleetdeck har intet basisbillede til {Pretty(sys)}.");
    }

    public static string Pretty(KitSystem s) => $"Debian {s.Debian ?? "?"} ({(s.Arch == "armhf" ? "32-bit" : s.Arch == "arm64" ? "64-bit" : s.Arch ?? "?")})";
    private static string Short(string s) => s.Length > 80 ? s[..80] + "…" : s;

    // ---------------------------------------------------------------- boot-opsætningen (flettes, kopieres aldrig)

    /// <summary>Flag, der må lægges til cmdline.txt. Alt andet afvises – især root=, init= og ip=-varianter fra et gammelt netboot.</summary>
    [GeneratedRegex(@"^(cfg80211\.ieee80211_regdom=[A-Z]{2}|consoleblank=\d{1,5}|net\.ifnames=[01]|usbcore\.autosuspend=-?\d{1,4}|usb-storage\.quirks=[0-9a-fA-F:,]{4,64}|dwc_otg\.[a-z_]{2,32}=\w{1,16}|smsc95xx\.[a-z_]{2,32}=\w{1,16}|logo\.nologo|fsck\.mode=(auto|force|skip))$")]
    private static partial Regex AllowedFlag();

    public static (List<string> Ok, List<string> Rejected) SplitFlags(string? text)
    {
        var ok = new List<string>(); var bad = new List<string>();
        foreach (var f in (text ?? "").Split([' ', '\n', '\r', '\t', '·'], StringSplitOptions.RemoveEmptyEntries))
            (AllowedFlag().IsMatch(f) ? ok : bad).Add(f);
        return (ok.Distinct().ToList(), bad);
    }

    public const string FirstrunFlags = "systemd.run=/boot/firmware/firstrun.sh systemd.run_success_action=reboot systemd.unit=kernel-command-line.target";

    /// <summary>Det nye billedes cmdline.txt med dets egen root=PARTUUID og standardflag. Kun tilladte flag lægges til; findes et flag
    /// allerede med en anden værdi, vinder det nye. Til sidst førstestarten (fjernes igen af pi-firstrun.sh).</summary>
    public static string MergeCmdline(string baseCmdline, IEnumerable<string> extra, bool firstrun = true)
    {
        var parts = baseCmdline.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var f in extra.Where(x => AllowedFlag().IsMatch(x)))
        {
            var key = f.Contains('=') ? f[..(f.IndexOf('=') + 1)] : f;
            parts.RemoveAll(p => p == f || (key.EndsWith('=') && p.StartsWith(key, StringComparison.Ordinal)));
            parts.Add(f);
        }
        parts.RemoveAll(p => p.StartsWith("systemd.run", StringComparison.Ordinal) || p == "systemd.unit=kernel-command-line.target");
        if (firstrun) parts.AddRange(FirstrunFlags.Split(' '));
        return string.Join(' ', parts) + "\n";
    }

    /// <summary>Linjer, der aldrig må komme fra en gammel opsætning: de bestemmer, hvordan Pi'en finder kernen, og gør et SD-kort
    /// ustartbart (fx auto_initramfs=0 fra netboot).</summary>
    private static readonly string[] DeniedConfigKeys = ["auto_initramfs", "kernel", "initramfs", "os_prefix", "cmdline", "device_tree", "start_file",
        "fixup_file", "boot_load_flags", "arm_64bit", "uart_2ndstage", "boot_delay", "include"];

    public static (List<string> Ok, List<string> Rejected) SplitConfigLines(string? text)
    {
        var ok = new List<string>(); var bad = new List<string>();
        foreach (var raw in (text ?? "").Split(['\n', '·'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.StartsWith('#')) continue;
            var key = raw.Split('=', 2)[0].Trim();
            var good = ConfigLine().IsMatch(raw) && !DeniedConfigKeys.Contains(key, StringComparer.OrdinalIgnoreCase);
            (good ? ok : bad).Add(raw);
        }
        return (ok.Distinct().ToList(), bad);
    }

    [GeneratedRegex(@"^[a-z][a-z0-9_]{1,40}=[A-Za-z0-9_.,:=\-/ ]{1,120}$")] private static partial Regex ConfigLine();

    /// <summary>Det nye billedes config.txt beholdes; profilens ekstra linjer lægges til under [all] til sidst.</summary>
    public static string MergeConfig(string baseConfig, IEnumerable<string> extraLines)
    {
        var lines = extraLines.ToList();
        if (lines.Count == 0) return baseConfig;
        var sb = new StringBuilder(baseConfig.TrimEnd('\n', '\r')).Append('\n');
        sb.Append("\n[all]\n# Fleetdeck: ekstra linjer fra profilen\n");
        foreach (var l in lines) sb.Append(l).Append('\n');
        return sb.ToString();
    }

    // ---------------------------------------------------------------- "udelad ved nyt kort"

    /// <summary>Mønstre som "etc/systemd/system/x.service.requires/*" eller "*.bak*": stier i gendannelsespunktet uden indledende /.</summary>
    public static bool SafeExclude(string p) => p.Length is > 0 and < 160 && Regex.IsMatch(p, @"^[\w./*\-+@]+$") && !p.Contains("..") && !p.StartsWith('/');

    public static List<string> CleanExcludes(IEnumerable<string>? patterns) =>
        (patterns ?? []).Select(p => p.Trim().TrimStart('/')).Where(SafeExclude).Distinct().ToList();

    // ---------------------------------------------------------------- navne og adresser

    [GeneratedRegex(@"^[a-zA-Z0-9](?:[a-zA-Z0-9\-]{0,61}[a-zA-Z0-9])?$")] public static partial Regex HostName();
    [GeneratedRegex(@"^[a-z_][a-z0-9_\-]{0,30}$")] public static partial Regex UserName();
    [GeneratedRegex(@"^(ssh-(ed25519|rsa)|ecdsa-sha2-nistp(256|384|521)|sk-ssh-ed25519@openssh\.com) [A-Za-z0-9+/=]{40,800}( [^\r\n]{0,120})?$")] public static partial Regex SshKey();

    public static string? CheckAddress(string? cidr)
    {
        if (cidr is null || cidr.Split('/') is not [var ip, var len] || !System.Net.IPAddress.TryParse(ip, out var a) || a.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
            || !int.TryParse(len, out var n) || n is < 8 or > 30) return "Skriv IP-adressen med netmaske, fx 192.168.1.50/24";
        return null;
    }

    public static bool Ipv4(string? s) => s is { Length: > 0 } && System.Net.IPAddress.TryParse(s, out var a) && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;

    /// <summary>settings.env til kortet. Hver værdi i enkelte anførselstegn, så filen kan læses af bash uden at noget bliver til en kommando.</summary>
    public static string SettingsEnv(PiCardSettings s)
    {
        static string Q(string v) => "'" + v.Replace("'", "'\\''") + "'";
        var sb = new StringBuilder("# Fleetdeck: SD-kortets indstillinger (ingen hemmeligheder – engangskoden står i filen \"code\")\n");
        void Add(string k, string v) => sb.Append(k).Append('=').Append(Q(v)).Append('\n');
        Add("FD_SERVER", s.Server.TrimEnd('/')); Add("FD_TLSPIN", s.TlsPin); Add("FD_NAME", s.Name); Add("FD_USER", s.User);
        Add("FD_MODE", s.Mode); Add("FD_PROFILE", s.ProfileId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add("FD_IP_MODE", s.IpMode); Add("FD_IFACE", s.Iface); Add("FD_ADDRESS", s.Address); Add("FD_GATEWAY", s.Gateway); Add("FD_DNS", s.Dns);
        Add("FD_TIMEZONE", s.Timezone); Add("FD_KEYMAP", s.Keymap); Add("FD_WLAN_COUNTRY", s.WlanCountry);
        return sb.ToString();
    }

    public const string FirstbootUnit = """
        [Unit]
        Description=Fleetdeck: tilmeld det nye SD-kort
        After=network-online.target
        Wants=network-online.target
        ConditionPathExists=/var/lib/fleetdeck-firstboot/settings.env

        [Service]
        Type=oneshot
        ExecStart=/usr/local/sbin/fleetdeck-firstboot
        TimeoutStartSec=45min
        RemainAfterExit=no

        [Install]
        WantedBy=multi-user.target

        """;

    /// <summary>Tekst, der aldrig må stå på et kort (private nøgler, tokens). Engangskoden er den eneste nøgle, der må være der.</summary>
    [GeneratedRegex(@"PRIVATE KEY|BEGIN OPENSSH|ghp_[A-Za-z0-9]{20}|xox[bap]-|AKIA[0-9A-Z]{16}|eyJ[A-Za-z0-9_\-]{20,}\.[A-Za-z0-9_\-]{20,}|password\s*=|passwd\s*=|kr_dev_[A-Za-z0-9_\-]{20}", RegexOptions.IgnoreCase)]
    public static partial Regex SecretLike();

    // ---------------------------------------------------------------- gendannelsespunktet

    public sealed record KitInfo(string Name, KitSystem System, List<string> MaskedUnits, List<string> EnabledUnits);

    /// <summary>Læser manifestet (system.txt og units.txt) fra et gendannelsespunkt uden at pakke resten ud.</summary>
    public static KitInfo? ReadKit(string file)
    {
        try
        {
            using var fs = File.OpenRead(file);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var tr = new TarReader(gz);
            string? system = null, units = null;
            while (tr.GetNextEntry(copyData: false) is { } e && (system is null || units is null))
            {
                if (e.DataStream is not { } ds) continue;
                if (e.Name.EndsWith("manifest/system.txt", StringComparison.Ordinal)) system = new StreamReader(ds).ReadToEnd();
                else if (e.Name.EndsWith("manifest/units.txt", StringComparison.Ordinal)) units = new StreamReader(ds).ReadToEnd();
                else ds.CopyTo(Stream.Null);
            }
            if (system is null) return null;
            var rows = (units ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(r => r.Length >= 2).ToList();
            return new(Path.GetFileName(file), ParseSystem(system),
                rows.Where(r => r[1] == "masked").Select(r => r[0]).ToList(),
                rows.Where(r => r[1] == "enabled").Select(r => r[0]).ToList());
        }
        catch (Exception e) when (e is InvalidDataException or IOException or FormatException or ArgumentException) { return null; }
    }

    [GeneratedRegex(@"\(([a-z]+)\)")] private static partial Regex CodeName();
    [GeneratedRegex(@"Linux (\d+)\b")] private static partial Regex DebianVersion();
    [GeneratedRegex(@"\bdpkg=(armhf|arm64|armel)\b")] private static partial Regex DpkgArch();
}

/// <summary>Basisbillederne: hentes én gang til data/pi-images/ og tjekkes mod SHA-256 – både ved hentning og før hver brug.</summary>
public sealed class PiImageLibrary(Paths paths, IHttpClientFactory http, ILogger<PiImageLibrary> log)
{
    private sealed class Progress { public volatile string State = "idle"; public long Bytes, Total; public string Message = ""; }
    private readonly ConcurrentDictionary<string, Progress> _progress = new();

    public string Dir => Path.Combine(paths.Data, "pi-images");
    public string FileOf(PiImage i) => Path.Combine(Dir, i.Id + ".img.xz");
    public bool Ready(PiImage i) => File.Exists(FileOf(i)) && File.Exists(FileOf(i) + ".ok");

    public object Status() => PiCardLogic.Catalog.Select(i =>
    {
        var p = _progress.GetValueOrDefault(i.Id);
        return new { i.Id, i.Title, i.Debian, i.Arch, i.Models, i.Size, usable = i.Usable, ready = Ready(i), state = p?.State ?? "idle", bytes = p?.Bytes ?? 0, total = p?.Total ?? i.Size, message = p?.Message ?? "" };
    }).ToList();

    public bool Start(PiImage item)
    {
        var p = _progress.GetOrAdd(item.Id, _ => new Progress());
        lock (p)
        {
            if (p.State == "downloading") return false;
            p.State = "downloading"; p.Bytes = 0; p.Total = item.Size; p.Message = "Starter …";
        }
        _ = Task.Run(async () =>
        {
            try { await Download(item, p); p.State = "idle"; p.Message = ""; }
            catch (Exception e) { log.LogWarning(e, "Hentning af {Id} fejlede", item.Id); p.State = "failed"; p.Message = e.Message; }
        });
        return true;
    }

    public async Task Ensure(PiImage item, Action<string> say, CancellationToken ct)
    {
        if (Ready(item) && await Verified(item, ct)) return;
        var p = _progress.GetOrAdd(item.Id, _ => new Progress());
        say($"Henter {item.Title} …");
        await Download(item, p, ct);
    }

    /// <summary>Tjek filen mod SHA-256 igen (filen på disken kan være ændret, siden den blev hentet).</summary>
    public async Task<bool> Verified(PiImage item, CancellationToken ct)
    {
        await using var f = File.OpenRead(FileOf(item));
        var got = Convert.ToHexStringLower(await SHA256.HashDataAsync(f, ct));
        if (got == item.Sha256) return true;
        try { File.Delete(FileOf(item) + ".ok"); } catch (IOException) { }
        return false;
    }

    private async Task Download(PiImage item, Progress p, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Dir);
        var part = FileOf(item) + ".partial";
        var c = http.CreateClient("os");
        using var res = await c.GetAsync(item.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        p.Total = res.Content.Headers.ContentLength ?? item.Size;
        p.Message = $"Henter {item.Title} …";
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var s = await res.Content.ReadAsStreamAsync(ct))
        await using (var o = File.Create(part))
        {
            var buf = new byte[1 << 20];
            int n;
            while ((n = await s.ReadAsync(buf, ct)) > 0)
            {
                hash.AppendData(buf, 0, n);
                await o.WriteAsync(buf.AsMemory(0, n), ct);
                p.Bytes += n;
            }
        }
        if (!string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), item.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(part);
            throw new InvalidOperationException($"{item.Title} var beskadiget (forkert SHA-256). Prøv igen.");
        }
        File.Move(part, FileOf(item), true);
        await File.WriteAllTextAsync(FileOf(item) + ".ok", item.Sha256, ct);
    }
}

/// <summary>
/// Bygger selve billedet uden root og uden loop-mount: basisbilledet pakkes ud til en arbejdsfil, boot-partitionen (FAT) findes med
/// sfdisk --json, og kun den ændres med mtools (mtype/mcopy/mmd -i fil@@offset). Rodfilsystemet røres ikke. Til sidst xz og SHA-256.
/// </summary>
public static class PiImageBuilder
{
    /// <summary>Hvor sfdisk, mtools og xz findes (tom = PATH). Bruges af testene, når værktøjerne ikke er installeret i systemet.</summary>
    public static string ToolDir { get; set; } = Environment.GetEnvironmentVariable("FLEETDECK_TOOLS_DIR") ?? "";

    private static string Tool(string name)
    {
        if (ToolDir.Length > 0 && File.Exists(Path.Combine(ToolDir, name))) return Path.Combine(ToolDir, name);
        foreach (var d in new[] { "/usr/bin", "/usr/sbin", "/bin", "/sbin" }) if (File.Exists(Path.Combine(d, name))) return Path.Combine(d, name);
        return name;
    }

    public static bool ToolsPresent() => new[] { "sfdisk", "mcopy", "mtype", "mmd", "mdir", "xz" }.All(t => Tool(t) != t || Which(t));
    private static bool Which(string t) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(d => File.Exists(Path.Combine(d, t)));

    public sealed record Result(string File, string Sha256, long Size);

    public static async Task<(int Code, string Out)> Run(string tool, IEnumerable<string> args, CancellationToken ct, Stream? stdout = null, Stream? stdin = null)
    {
        var psi = new ProcessStartInfo(Tool(tool)) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin is not null, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["MTOOLS_SKIP_CHECK"] = "1";
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"{tool} kunne ikke startes");
        var err = p.StandardError.ReadToEndAsync(ct);
        // ind og ud samtidig: ellers venter vi på xz's output, mens xz venter på vores input
        var feed = stdin is null ? Task.CompletedTask : Task.Run(async () => { await stdin.CopyToAsync(p.StandardInput.BaseStream, ct); p.StandardInput.Close(); }, ct);
        var outText = stdout is not null
            ? p.StandardOutput.BaseStream.CopyToAsync(stdout, ct).ContinueWith(_ => "", ct)
            : p.StandardOutput.ReadToEndAsync(ct);
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch (InvalidOperationException) { } throw; }
        await feed;
        return (p.ExitCode, (await outText) + (await err));
    }

    private static async Task<string> Must(string tool, IEnumerable<string> args, CancellationToken ct)
    {
        var (code, o) = await Run(tool, args, ct);
        if (code != 0) throw new InvalidOperationException($"{tool} fejlede: {o.Trim()}");
        return o;
    }

    /// <summary>Boot-partitionens start i byte (første FAT-partition).</summary>
    public static async Task<long> BootOffset(string img, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await Must("sfdisk", ["--json", img], ct));
        var table = doc.RootElement.GetProperty("partitiontable");
        var sector = table.TryGetProperty("sectorsize", out var ss) ? ss.GetInt64() : 512;
        foreach (var part in table.GetProperty("partitions").EnumerateArray())
        {
            var type = part.GetProperty("type").GetString() ?? "";
            if (type is "c" or "b" or "e" or "0c" or "0b" || type.Equals("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7", StringComparison.OrdinalIgnoreCase))
                return part.GetProperty("start").GetInt64() * sector;
        }
        throw new InvalidOperationException("Billedet har ingen FAT-boot-partition");
    }

    public static string Fat(string img, long offset) => $"{img}@@{offset}";
    public static Task<string> ReadFile(string img, long off, string name, CancellationToken ct) => Must("mtype", ["-i", Fat(img, off), "::/" + name], ct);

    public static async Task WriteFile(string img, long off, string name, string content, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"fd-pi-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(tmp, content.Replace("\r\n", "\n"), new UTF8Encoding(false), ct);
        try { await Must("mcopy", ["-o", "-i", Fat(img, off), tmp, "::/" + name], ct); }
        finally { File.Delete(tmp); }
    }

    public sealed record Files(string Cmdline, string Config, string SettingsEnv, string Code, string AuthorizedKeys, string Firstrun, string Firstboot);

    /// <summary>Kopiér basisbilledet, skriv filerne på boot-partitionen, komprimér og regn SHA-256. <paramref name="say"/> får fremdriften.</summary>
    public static async Task<Result> Build(string baseXz, string outDir, string fileName, Func<string, string, Files> files, Action<int, string> say, CancellationToken ct)
    {
        Directory.CreateDirectory(outDir);
        var work = Path.Combine(outDir, "arbejde.img");
        try
        {
            say(10, "Pakker basisbilledet ud");
            await using (var o = File.Create(work))
            {
                var (code, err) = await Run("xz", ["-dc", baseXz], ct, o);
                if (code != 0) throw new InvalidOperationException($"xz fejlede: {err.Trim()}");
            }
            say(35, "Finder boot-partitionen");
            var off = await BootOffset(work, ct);
            var f = files(await ReadFile(work, off, "cmdline.txt", ct), await ReadFile(work, off, "config.txt", ct));
            say(45, "Skriver førstestart-filerne på boot-partitionen");
            await WriteFile(work, off, "cmdline.txt", f.Cmdline, ct);
            await WriteFile(work, off, "config.txt", f.Config, ct);
            await WriteFile(work, off, "firstrun.sh", f.Firstrun, ct);
            await Must("mmd", ["-D", "s", "-i", Fat(work, off), "::/fleetdeck"], ct);
            await WriteFile(work, off, "fleetdeck/settings.env", f.SettingsEnv, ct);
            await WriteFile(work, off, "fleetdeck/code", f.Code + "\n", ct);
            await WriteFile(work, off, "fleetdeck/authorized_keys", f.AuthorizedKeys, ct);
            await WriteFile(work, off, "fleetdeck/firstboot.sh", f.Firstboot, ct);
            await WriteFile(work, off, "fleetdeck/fleetdeck-firstboot.service", PiCardLogic.FirstbootUnit, ct);
            await Check(work, off, f, ct);
            say(55, "Komprimerer til .img.xz");
            var final = Path.Combine(outDir, fileName);
            await using (var o = File.Create(final + ".partial"))
            await using (var input = File.OpenRead(work))
            {
                var (code, err) = await Run("xz", ["-T0", "-6", "-c"], ct, o, input);
                if (code != 0) throw new InvalidOperationException($"xz fejlede: {err.Trim()}");
            }
            File.Move(final + ".partial", final, true);
            say(95, "Regner SHA-256");
            await using var fs = File.OpenRead(final);
            var sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, ct));
            return new(final, sha, new FileInfo(final).Length);
        }
        finally { try { File.Delete(work); } catch (IOException) { } }
    }

    /// <summary>Læs filerne tilbage fra billedet: de skal være dér, cmdline.txt skal have sin root= og førstestarten, og intet må ligne en hemmelighed.</summary>
    private static async Task Check(string img, long off, Files f, CancellationToken ct)
    {
        var list = await Must("mdir", ["-b", "-i", Fat(img, off), "::/fleetdeck"], ct);
        foreach (var name in new[] { "settings.env", "code", "authorized_keys", "firstboot.sh", "fleetdeck-firstboot.service" })
            if (!list.Contains(name, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"{name} kom ikke med på kortet");
        var cmd = await ReadFile(img, off, "cmdline.txt", ct);
        if (!cmd.Contains("root=", StringComparison.Ordinal) || !cmd.Contains(PiCardLogic.FirstrunFlags, StringComparison.Ordinal))
            throw new InvalidOperationException("cmdline.txt blev ikke skrevet rigtigt");
        foreach (var name in new[] { "fleetdeck/settings.env", "fleetdeck/authorized_keys", "firstrun.sh", "fleetdeck/firstboot.sh", "cmdline.txt", "config.txt" })
            if (PiCardLogic.SecretLike().IsMatch(await ReadFile(img, off, name, ct))) throw new InvalidOperationException($"{name} indeholder noget, der ligner en hemmelighed – kortet er ikke bygget");
    }
}
