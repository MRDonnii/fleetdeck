using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;

namespace Fleetdeck.Server.Services;

public static partial class MacAddr
{
    [GeneratedRegex("^[0-9A-F]{12}$")] private static partial Regex Hex12();
    [GeneratedRegex("[-:. ]")] private static partial Regex Separators();

    /// <summary>"aa:bb:cc:dd:ee:ff", "AABBCCDDEEFF" ... → "AA-BB-CC-DD-EE-FF" (som ipconfig viser den), ellers null.</summary>
    public static string? Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var h = Separators().Replace(s.Trim(), "").ToUpperInvariant();
        return Hex12().IsMatch(h) ? string.Join('-', Enumerable.Range(0, 6).Select(i => h.Substring(i * 2, 2))) : null;
    }
    public static string Colon(string mac) => mac.Replace('-', ':').ToLowerInvariant();   // GRUB / dnsmasq
    public static string Dash(string mac) => mac.ToLowerInvariant();                       // URL og GRUB-filnavn
}

/// <summary>SHA-512-crypt ("$6$"), som Linux bruger til adgangskoder (Ubuntu autoinstall kræver det).</summary>
public static class ShaCrypt
{
    private const string Itoa = "./0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    public static string Sha512(string password, string? salt = null)
    {
        salt ??= new string(RandomNumberGenerator.GetItems<char>(Itoa, 16));
        if (salt.Length > 16) salt = salt[..16];
        var p = Encoding.UTF8.GetBytes(password);
        var s = Encoding.UTF8.GetBytes(salt);
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);

        h.AppendData(p); h.AppendData(s); h.AppendData(p);
        var b = h.GetHashAndReset();
        h.AppendData(p); h.AppendData(s);
        for (var n = p.Length; n > 0; n -= 64) h.AppendData(b, 0, Math.Min(64, n));
        for (var n = p.Length; n > 0; n >>= 1) h.AppendData((n & 1) != 0 ? b : p);
        var c = h.GetHashAndReset();

        for (var i = 0; i < p.Length; i++) h.AppendData(p);
        var pBytes = Repeat(h.GetHashAndReset(), p.Length);
        for (var i = 0; i < 16 + c[0]; i++) h.AppendData(s);
        var sBytes = Repeat(h.GetHashAndReset(), s.Length);

        for (var r = 0; r < 5000; r++)
        {
            h.AppendData((r & 1) != 0 ? pBytes : c);
            if (r % 3 != 0) h.AppendData(sBytes);
            if (r % 7 != 0) h.AppendData(pBytes);
            h.AppendData((r & 1) != 0 ? c : pBytes);
            c = h.GetHashAndReset();
        }

        var sb = new StringBuilder("$6$").Append(salt).Append('$');
        void B64(int b2, int b1, int b0, int n)
        {
            var w = (b2 << 16) | (b1 << 8) | b0;
            for (var i = 0; i < n; i++) { sb.Append(Itoa[w & 0x3f]); w >>= 6; }
        }
        for (var i = 0; i < 21; i++)
        {
            int x = c[i], y = c[i + 21], z = c[i + 42];
            switch (i % 3)
            {
                case 0: B64(x, y, z, 4); break;
                case 1: B64(y, z, x, 4); break;
                default: B64(z, x, y, 4); break;
            }
        }
        B64(0, 0, c[63], 2);
        return sb.ToString();
    }

    private static byte[] Repeat(byte[] src, int len)
    {
        var r = new byte[len];
        for (var i = 0; i < len; i++) r[i] = src[i % src.Length];
        return r;
    }
}

public sealed class OsInfo
{
    public string Version { get; set; } = "";
    public string Iso { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public DateTime Downloaded { get; set; }
    /// <summary>Netboot (Debian, Fedora, Rocky, Alma): installationstræet, installeren henter pakkerne fra.</summary>
    public string Repo { get; set; } = "";
}

/// <summary>
/// Styresystemer der kan installeres fra netværket (<see cref="OsCatalog"/>). Ubuntu hentes fra releases.ubuntu.com, Debian/Fedora/Rocky/Alma
/// som netboot-kerne + initrd fra deres egne spejle, når man trykker "Hent". Alt kontrolleres mod SHA-256.
/// Windows: indholdet af ens egen ISO lægges i PXE\win11, PXE\win10 eller PXE\ws2025 (Microsoft har intet stabilt direkte link).
/// </summary>
public sealed partial class OsLibrary(Paths paths, IHttpClientFactory http, ILogger<OsLibrary> log)
{
    private static readonly string[] NetbootFiles = ["bootx64.efi", "grubx64.efi", "linux", "initrd"];
    private const string FedoraReleases = "https://dl.fedoraproject.org/pub/fedora/linux/releases/";

    private sealed class Progress
    {
        public volatile string State = "idle";   // idle | downloading | failed
        public long Bytes, Total;
        public string Message = "";
    }
    private readonly ConcurrentDictionary<string, Progress> _progress = new();

    [GeneratedRegex(@"ubuntu-([0-9.]+)-live-server-amd64\.iso")] private static partial Regex IsoName();

    public string OsDir(string id) => Path.Combine(paths.Pxe, "os", id);
    public string TftpDir(string series) => Path.Combine(paths.Pxe, "tftp", "ubuntu", series);
    /// <summary>Netboot-kerne og initrd for andre Linux-familier (GRUB: /linux/&lt;id&gt;/linux og /linux/&lt;id&gt;/initrd).</summary>
    public string LinuxTftpDir(string id) => Path.Combine(paths.Pxe, "tftp", "linux", id);
    public static OsEntry? Get(string id) => OsCatalog.Get(id);
    private bool GrubLoader() => new[] { "bootx64.efi", "grubx64.efi" }.All(f => File.Exists(Path.Combine(paths.Pxe, "tftp", "ubuntu", f)));

    public OsInfo? Ready(string id)
    {
        var item = Get(id);
        if (item is null) return null;
        if (item.IsWindows)
            return File.Exists(Path.Combine(paths.Pxe, item.WinDir!, "setup.exe")) ? new OsInfo { Version = item.Version } : null;
        var info = Json.Des<OsInfo>(File.Exists(Path.Combine(OsDir(id), "info.json")) ? File.ReadAllText(Path.Combine(OsDir(id), "info.json")) : null);
        if (item.Method == AnswerMethod.Autoinstall)
            return info.Iso.Length > 0 && File.Exists(Path.Combine(OsDir(id), info.Iso))
                   && NetbootFiles.All(f => File.Exists(Path.Combine(TftpDir(item.Series!), f))) ? info : null;
        if (item.Netboot is null) return null;
        return info.Repo.Length > 0 && GrubLoader() && new[] { "linux", "initrd" }.All(f => File.Exists(Path.Combine(LinuxTftpDir(id), f))) ? info : null;
    }

    /// <summary>Hvad der mangler, før styresystemet kan bruges (vises i Udrulning).</summary>
    public static string Missing(OsEntry e) => e switch
    {
        { IsWindows: true } => $"Mangler: læg indholdet af din egen {e.Title}-ISO i WinSetup\\PXE\\{e.WinDir}",
        { Method: AnswerMethod.Autoinstall } => "Ikke hentet (ca. 3 GB fra releases.ubuntu.com)",
        { Netboot: not null } => $"Ikke hentet (kerne og initrd fra {new Uri(e.Netboot.TreeUrl.Replace("{version}", "0")).Host})",
        _ => "Kan ikke hentes",
    };

    public object Status() => OsCatalog.All.Select(i =>
    {
        var r = Ready(i.Id);
        var p = _progress.GetValueOrDefault(i.Id);
        return new
        {
            i.Id, i.Title, kind = i.IsWindows ? "windows" : i.Family, i.Series, i.Family, i.FamilyTitle, osVersion = i.Version, i.Variants, i.Method,
            i.Availability, i.Note, i.Warning, i.CanEncrypt, missing = Missing(i),
            blank = DeployField.All.ToDictionary(f => f, f => i.Blank(f)),
            ready = r is not null, version = r?.Version, sizeGb = r is null || r.Size == 0 ? (double?)null : Math.Round(r.Size / 1073741824.0, 2), downloaded = r is { Downloaded.Year: > 2000 } ? r.Downloaded : (DateTime?)null,
            state = p?.State ?? "idle", bytes = p?.Bytes ?? 0, total = p?.Total ?? 0, message = p?.Message ?? "",
            canDownload = CanDownload(i),
        };
    }).ToList();

    private static bool CanDownload(OsEntry? e) => e is not null && e.Availability != OsAvailability.Omitted && (e.Method == AnswerMethod.Autoinstall || e.Netboot is not null);

    public bool Start(string id)
    {
        var item = Get(id);
        if (!CanDownload(item)) return false;
        var p = _progress.GetOrAdd(id, _ => new Progress());
        lock (p)
        {
            if (p.State == "downloading") return false;
            p.State = "downloading"; p.Bytes = 0; p.Total = 0; p.Message = "Starter …";
        }
        _ = Task.Run(async () =>
        {
            try
            {
                if (item!.Method == AnswerMethod.Autoinstall) await Download(item, p); else await DownloadNetboot(item, p);
                p.State = "idle"; p.Message = "";
            }
            catch (Exception e)
            {
                log.LogWarning(e, "Hentning af {Id} fejlede", id);
                p.State = "failed"; p.Message = e.Message;
            }
        });
        return true;
    }

    public void Remove(string id)
    {
        var item = Get(id);
        if (!CanDownload(item) || _progress.GetValueOrDefault(id)?.State == "downloading") return;
        try { if (Directory.Exists(OsDir(id))) Directory.Delete(OsDir(id), true); } catch (IOException) { }
        var tftp = item!.Method == AnswerMethod.Autoinstall ? TftpDir(item.Series!) : LinuxTftpDir(id);
        try { if (Directory.Exists(tftp)) Directory.Delete(tftp, true); } catch (IOException) { }
    }

    /// <summary>Hent en fil og kontrollér SHA-256 undervejs. Forkert SHA-256 = filen slettes.</summary>
    private static async Task DownloadVerified(HttpClient c, string url, string path, string sha, Progress p)
    {
        using var res = await c.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        res.EnsureSuccessStatusCode();
        p.Total = res.Content.Headers.ContentLength ?? 0; p.Bytes = 0;
        await using var s = await res.Content.ReadAsStreamAsync();
        await using var o = File.Create(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[1 << 20];
        int n;
        while ((n = await s.ReadAsync(buf)) > 0)
        {
            hash.AppendData(buf, 0, n);
            await o.WriteAsync(buf.AsMemory(0, n));
            p.Bytes += n;
        }
        var got = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (!string.Equals(got, sha, StringComparison.OrdinalIgnoreCase))
        {
            o.Close(); File.Delete(path);
            throw new InvalidOperationException($"{Path.GetFileName(url)} var beskadiget (forkert SHA-256). Prøv igen.");
        }
    }

    private async Task Download(OsEntry item, Progress p)
    {
        var c = http.CreateClient("os");
        var baseUrl = $"https://releases.ubuntu.com/{item.Series}/";
        p.Message = "Finder nyeste version…";
        var cfg = await c.GetStringAsync(baseUrl + "netboot/amd64/grub/grub.cfg");
        var m = IsoName().Match(cfg);
        if (!m.Success) throw new InvalidOperationException("Kunne ikke finde ISO-navnet i Ubuntus netboot-opsætning");
        var iso = m.Value;
        var sums = await c.GetStringAsync(baseUrl + "SHA256SUMS");
        var sha = OsCatalog.SumsSha256(sums, iso) ?? throw new InvalidOperationException($"{iso} står ikke i SHA256SUMS");

        // 1) Netboot-filer (shim og GRUB signeret af Canonical/Microsoft, så Secure Boot kan blive slået til)
        var tftp = TftpDir(item.Series!);
        var tmp = tftp + ".new";
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        Directory.CreateDirectory(tmp);
        foreach (var f in NetbootFiles)
        {
            p.Message = $"Henter {f} …";
            await using var s = await c.GetStreamAsync(baseUrl + "netboot/amd64/" + f);
            await using var o = File.Create(Path.Combine(tmp, f));
            await s.CopyToAsync(o);
        }

        // 2) ISO'en (installeren henter den over HTTP fra Fleetdeck) – kontrolleres mod SHA256SUMS
        var dir = OsDir(item.Id);
        Directory.CreateDirectory(dir);
        var part = Path.Combine(dir, iso + ".partial");
        p.Message = $"Henter {iso} …";
        try { await DownloadVerified(c, baseUrl + iso, part, sha, p); }
        catch (InvalidOperationException) { throw new InvalidOperationException("ISO'en var beskadiget (forkert SHA-256). Prøv igen."); }
        foreach (var old in Directory.GetFiles(dir, "*.iso")) File.Delete(old);
        File.Move(part, Path.Combine(dir, iso));
        if (Directory.Exists(tftp)) Directory.Delete(tftp, true);
        Directory.Move(tmp, tftp);
        // shim + GRUB som dnsmasq peger på (ubuntu/bootx64.efi); GRUB kan starte alle versioners kerner
        foreach (var f in new[] { "bootx64.efi", "grubx64.efi" })
            File.Copy(Path.Combine(tftp, f), Path.Combine(paths.Pxe, "tftp", "ubuntu", f), true);
        File.WriteAllText(Path.Combine(dir, "info.json"), Json.Ser(new OsInfo
        {
            Version = m.Groups[1].Value, Iso = iso, Sha256 = sha, Size = new FileInfo(Path.Combine(dir, iso)).Length, Downloaded = DateTime.UtcNow,
        }));
        log.LogInformation("Ubuntu {Version} er klar til netværksinstallation", m.Groups[1].Value);
    }

    /// <summary>Debian, Fedora, Rocky, Alma: kerne og initrd fra distributionens eget spejl (SHA-256 fra .treeinfo eller SHA256SUMS).</summary>
    private async Task DownloadNetboot(OsEntry item, Progress p)
    {
        var c = http.CreateClient("os");
        var nb = item.Netboot!;
        var tree = nb.TreeUrl;
        var version = item.Version;
        if (tree.Contains("{version}"))
        {
            p.Message = "Finder nyeste version…";
            var n = OsCatalog.LatestFedora(await c.GetStringAsync(FedoraReleases)) ?? throw new InvalidOperationException("Kunne ikke finde den nyeste Fedora-udgave");
            tree = tree.Replace("{version}", n.ToString()); version = n.ToString();
        }
        var sums = await c.GetStringAsync(tree + (nb.Checksums == "treeinfo" ? ".treeinfo" : nb.SumsPath));
        string Sha(string f) => (nb.Checksums == "treeinfo" ? OsCatalog.TreeinfoSha256(sums, f) : OsCatalog.SumsSha256(sums, f))
                                ?? throw new InvalidOperationException($"{f} står ikke i kontrolsummerne");

        var dir = LinuxTftpDir(item.Id);
        var tmp = dir + ".new";
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        Directory.CreateDirectory(tmp);
        foreach (var (remote, local) in new[] { (nb.Kernel, "linux"), (nb.Initrd, "initrd") })
        {
            p.Message = $"Henter {Path.GetFileName(remote)} …";
            await DownloadVerified(c, tree + remote, Path.Combine(tmp, local), Sha(remote), p);
        }

        // GRUB-starteren (shim + GRUB) kommer fra Ubuntus netboot, som Fleetdeck også bruger til Ubuntu
        if (!GrubLoader())
        {
            Directory.CreateDirectory(Path.Combine(paths.Pxe, "tftp", "ubuntu"));
            foreach (var f in new[] { "bootx64.efi", "grubx64.efi" })
            {
                p.Message = $"Henter {f} …";
                await using var s = await c.GetStreamAsync("https://releases.ubuntu.com/24.04/netboot/amd64/" + f);
                await using var o = File.Create(Path.Combine(paths.Pxe, "tftp", "ubuntu", f));
                await s.CopyToAsync(o);
            }
        }
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.Move(tmp, dir);
        Directory.CreateDirectory(OsDir(item.Id));
        File.WriteAllText(Path.Combine(OsDir(item.Id), "info.json"), Json.Ser(new OsInfo
        {
            Version = version, Repo = tree, Sha256 = Sha(nb.Kernel), Size = new FileInfo(Path.Combine(dir, "initrd")).Length + new FileInfo(Path.Combine(dir, "linux")).Length,
            Downloaded = DateTime.UtcNow,
        }));
        log.LogInformation("{Title} {Version} er klar til netværksinstallation", item.Title, version);
    }
}

/// <param name="Os">Id fra <see cref="OsCatalog"/> (fx "debian-13").</param>
/// <param name="Language">null = Indstillingernes sprog, "" = spørg på maskinen (det samme for Keyboard og TimeZone).</param>
public sealed record DeployRequest(string? Mac, string? Os, string? Mode, int? ProfileId, string? PcName, string? UserName,
    string? FullName, string? Password, string? Variant, string? Packages, int? MachineId, string? Notes, bool? AutoDisk = null,
    string? SshKey = null, bool? Admin = null, string? Language = null, string? Keyboard = null, string? TimeZone = null, bool? Encrypt = null);

/// <summary>
/// Udrulning over netværket. Serveren skriver små filer på sharen, som netværksbootet læser:
///   PXE\deploy\&lt;MAC&gt;.cmd        Windows: hvad WinPE-menuen skal installere (læses efter login på sharen)
///   PXE\deploy\&lt;MAC&gt;.xml        Windows: færdig svarfil, når guiden har valgt noget, skabelonen ikke kan (tomme felter, disk, andre udgaver)
///   PXE\deploy\hosts\&lt;mac&gt;     dnsmasq: denne pc skal have GRUB-bootfilen (Linux) i stedet for Windows
///   PXE\tftp\grub\...             GRUB-menu pr. pc (Linux)
/// </summary>
public sealed partial class DeployService(AppDb db, Paths paths, SettingsService settings, OsLibrary os, ILogger<DeployService> log)
{
    [GeneratedRegex("^[A-Z0-9][A-Z0-9-]{0,14}$")] private static partial Regex WinPcName();
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9 ._-]{0,19}$")] private static partial Regex WinUser();
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}$")] private static partial Regex LinuxHost();
    [GeneratedRegex("^[a-z][a-z0-9_-]{0,31}$")] private static partial Regex LinuxUser();
    [GeneratedRegex("^[a-z0-9][a-z0-9+.-]*$")] private static partial Regex AptName();
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9+._-]*$")] private static partial Regex RpmName();
    [GeneratedRegex(@"^(ssh-ed25519|ssh-rsa|ecdsa-sha2-nistp(256|384|521)|sk-ssh-ed25519@openssh\.com|sk-ecdsa-sha2-nistp256@openssh\.com) [A-Za-z0-9+/]+={0,3}( [A-Za-z0-9@._+-]{1,100})?$")]
    private static partial Regex SshPublicKey();

    /// <summary>Slået til under Indstillinger → Udrulning: styresystemer, der ikke er afprøvet endnu, kan vælges (kun til afprøvning i en VM).</summary>
    public const string KUntested = "deploy_untested";

    private string Dir => Path.Combine(paths.Pxe, "deploy");
    private string HostsDir => Path.Combine(Dir, "hosts");
    private string GrubDir => Path.Combine(paths.Pxe, "tftp", "grub");

    /// <summary>Navne til WinPE (cmd): kun ASCII og ingen tegn der betyder noget i en .cmd-fil.</summary>
    public static string Ascii(string s)
    {
        var t = s.Replace("æ", "ae").Replace("ø", "oe").Replace("å", "aa").Replace("Æ", "Ae").Replace("Ø", "Oe").Replace("Å", "Aa");
        return new string(t.Where(ch => ch is >= ' ' and <= '~' && "%!^&|<>\"".IndexOf(ch) < 0).ToArray()).Trim();
    }

    /// <summary>Katalog-id for en udrulning (ældre rækker har kun Os og Series).</summary>
    public static string OsIdOf(Deployment d) => d.OsId.Length > 0 ? d.OsId : d.Os == DeployOs.Ubuntu ? $"ubuntu-{d.Series}" : "windows-11";

    /// <summary>Kort beskrivelse, fx "Debian 13 Trixie Server" eller "Windows 11 (med mine ting)".</summary>
    public static string Describe(Deployment d)
    {
        var e = OsCatalog.Get(OsIdOf(d));
        var title = e?.Title ?? d.Os;
        if (e is { IsWindows: true, IsWindowsServer: false } || e is null && d.Os == DeployOs.Windows)
            return d.Mode == DeployMode.Clean ? $"{title} (ren)" : d.Mode == DeployMode.Profile ? $"{title} (profil)" : $"{title} (med mine ting)";
        if (e is { Variants.Length: > 1 }) return $"{title} {(d.Variant == "desktop" ? (e.IsWindows ? "med skrivebord" : "Desktop") : (e.IsWindows ? "Core" : "Server"))}";
        return title;
    }

    public async Task<(Deployment? D, string? Error)> Create(DeployRequest r, string by)
    {
        var mac = MacAddr.Normalize(r.Mac);
        if (mac is null) return (null, "Skriv pc'ens MAC-adresse (fx 52-54-00-12-34-56). Den står i Unraids VM-indstillinger eller på netværksboot-skærmen.");
        var osId = r.Os ?? "windows-11";
        var item = OsCatalog.Get(osId);
        if (item is null) return (null, "Ukendt styresystem");
        if (item.Availability == OsAvailability.Omitted) return (null, item.Note ?? $"{item.Title} kan ikke vælges");
        if (item.Availability == OsAvailability.Later && await settings.Get(KUntested) != "1")
            return (null, $"{item.Title} kommer senere – den automatiske installation er ikke afprøvet i en VM endnu.");
        if (item.Method == AnswerMethod.Proxmox) return (null, "Proxmox kan ikke installeres over netværket endnu.");
        if (os.Ready(osId) is null)
            return (null, item.IsWindows ? $"{item.Title}-installationen mangler på serveren (PXE\\{item.WinDir})." : $"{item.Title} er ikke hentet endnu – tryk \"Hent\" under Styresystemer.");
        var d = new Deployment { Mac = mac, CreatedBy = by, MachineId = r.MachineId, Notes = (r.Notes ?? "").Trim(), OsId = item.Id };
        d.Variant = item.Variants.Contains(r.Variant) ? r.Variant! : item.Variants[0];
        var asks = new List<string>();
        string? Blank(string field, string label)
        {
            if (!item.Blank(field).MayBeBlank) return $"{label}: {item.Blank(field).Text}.";
            if (item.Blank(field).Kind != BlankRule.Na) asks.Add(field);
            return null;
        }

        if (item.IsWindows)
        {
            d.Os = DeployOs.Windows;
            d.Mode = r.Mode ?? (item.IsWindowsServer ? DeployMode.Clean : DeployMode.Managed);
            if (!DeployMode.All.Contains(d.Mode)) return (null, "Ukendt installationstype");
            if (d.Mode == DeployMode.Profile)
            {
                if (r.ProfileId is not int pid || !await db.Profiles.AnyAsync(p => p.Id == pid)) return (null, "Vælg en profil");
                d.ProfileId = pid;
            }
            d.PcName = (r.PcName ?? "").Trim().ToUpperInvariant();
            if (d.PcName.Length == 0) { if (Blank(DeployField.Name, "Pc-navn") is { } e) return (null, e); }
            else if (!WinPcName().IsMatch(d.PcName)) return (null, "Pc-navnet må have 1-15 tegn: A-Z, 0-9 og bindestreg.");
            d.UserName = (r.UserName ?? "").Trim();
            if (d.UserName.Length == 0) { if (Blank(DeployField.User, "Bruger") is { } e) return (null, e); }
            else if (!WinUser().IsMatch(d.UserName) || d.UserName.EndsWith('.') || d.UserName.EndsWith(' '))
                return (null, "Windows-brugernavnet må have 1-20 tegn: bogstaver a-z, tal, mellemrum, punktum, - og _ (ingen æøå).");
            Blank(DeployField.Password, "Adgangskode");
        }
        else
        {
            d.Os = item.Family == "ubuntu" ? DeployOs.Ubuntu : item.Family;
            d.Series = item.Series ?? "";
            d.Mode = DeployMode.Clean;
            if (r.ProfileId is int pid)
            {
                if (!await db.Profiles.AnyAsync(p => p.Id == pid)) return (null, "Profilen findes ikke");
                d.ProfileId = pid;
            }
            d.PcName = (r.PcName ?? "").Trim().ToLowerInvariant();
            if (d.PcName.Length == 0) { if (Blank(DeployField.Name, "Værtsnavn") is { } e) return (null, e); }
            else if (!LinuxHost().IsMatch(d.PcName)) return (null, "Værtsnavnet må have a-z, 0-9 og bindestreg.");
            d.UserName = (r.UserName ?? "").Trim();
            if (d.UserName.Length == 0) { if (Blank(DeployField.User, "Brugernavn") is { } e) return (null, e); }
            else if (!LinuxUser().IsMatch(d.UserName)) return (null, "Linux-brugernavnet skal starte med et lille bogstav og må have a-z, 0-9, - og _.");
            d.FullName = (r.FullName ?? "").Trim();
            if (d.FullName.Length == 0) d.FullName = d.UserName;
            if (d.FullName.Length > 100 || d.FullName.IndexOfAny(['"', '\\', '\n', '\r', '\'']) >= 0) return (null, "Det fulde navn må ikke indeholde \", ' eller \\.");
            var pw = r.Password ?? "";
            if (pw.Length == 0) { if (Blank(DeployField.Password, "Adgangskode") is { } e) return (null, e); }
            else if (pw.Length < 6) return (null, "Adgangskoden skal have mindst 6 tegn.");
            else d.PasswordHash = ShaCrypt.Sha512(pw);
            d.SshKey = string.Join(' ', (r.SshKey ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (d.SshKey.Length > 0 && !SshPublicKey().IsMatch(d.SshKey)) return (null, "SSH-nøglen skal være en offentlig nøgle på én linje, fx \"ssh-ed25519 AAAA… navn\".");
            if (d.SshKey.Length > 0 && d.UserName.Length == 0 && item.Method != AnswerMethod.Proxmox) return (null, "En SSH-nøgle kræver et brugernavn.");
            d.NoAdmin = r.Admin == false && d.UserName.Length > 0;
            var names = item.Method == AnswerMethod.Kickstart ? RpmName() : AptName();
            var pk = (r.Packages ?? "").Split([' ', ',', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).Distinct().ToList();
            if (pk.FirstOrDefault(x => !names.IsMatch(x)) is { } bad) return (null, $"Ugyldigt pakkenavn: {bad}");
            d.Packages = string.Join(' ', pk);
        }

        // Disk og kryptering (nøglen skrives altid på maskinen)
        d.AutoDisk = r.AutoDisk == true;
        if (!d.AutoDisk && Blank(DeployField.Disk, "Disk") is { } de) return (null, de);
        d.Encrypt = r.Encrypt == true;
        if (d.Encrypt && !item.CanEncrypt) return (null, $"{item.Title} kan ikke krypteres af Fleetdeck endnu.");

        // Sprog, tastatur og tidszone: null = Indstillingerne, "" = spørg på maskinen
        var locale = await InstallLocale.Load(settings);
        if (r.Language is { } lang)
        {
            if (lang.Trim().Length == 0) { if (Blank(DeployField.Language, "Sprog") is { } e) return (null, e); }
            else if (InstallLocale.FindLanguage(lang) is not { } l) return (null, "Ukendt sprog");
            else if (l.Id != locale.Language.Id) d.Language = l.Id;
        }
        if (r.Keyboard is { } kb)
        {
            if (kb.Trim().Length == 0) { if (Blank(DeployField.Keyboard, "Tastatur") is { } e) return (null, e); }
            else if (InstallLocale.FindKeyboard(kb) is not { } k) return (null, "Ukendt tastatur");
            else if (k.Id != locale.Keyboard.Id) d.Keyboard = k.Id;
        }
        if (r.TimeZone is { } tz)
        {
            if (tz.Trim().Length == 0) { if (Blank(DeployField.TimeZone, "Tidszone") is { } e) return (null, e); }
            else if (!InstallLocale.ValidTimeZone(tz)) return (null, "Ukendt tidszone");
            else if (tz.Trim() != locale.TimeZone) d.TimeZone = tz.Trim();
        }
        d.AskFields = asks.Distinct().ToList();

        // Kun én aktiv udrulning pr. pc
        var now = DateTime.UtcNow;
        await db.Deployments.Where(x => x.Mac == mac && DeployStatus.Active.Contains(x.Status))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, DeployStatus.Cancelled).SetProperty(x => x.Finished, now));
        db.Deployments.Add(d);
        await db.SaveChangesAsync();
        await Sync();
        return (d, null);
    }

    /// <summary>"Geninstallér fra netværket": læg en udrulning klar, så installationen kører uden spørgsmål.</summary>
    public async Task<Deployment?> ForReinstall(Machine m, string by)
    {
        var mac = MacAddr.Normalize(m.Mac);
        if (mac is null || os.Ready("windows-11") is null) return null;
        var user = (m.User ?? "").Split('\\').Last();
        if (!WinUser().IsMatch(user)) user = Ascii(await settings.Get("pxe_default_user"));
        var name = m.Name.Length <= 15 ? m.Name : m.Name[..15];
        var (d, _) = await Create(new DeployRequest(mac, "windows-11", DeployMode.Managed, null, name, user, null, null, null, null, m.Id,
            "Oprettet af \"Geninstallér fra netværket\"", AutoDisk: false), by);
        return d;
    }

    /// <summary>Kaldes når agenten tilmelder sig efter en installation.</summary>
    public async Task Completed(int? deployId, string pcName, int machineId)
    {
        var now = DateTime.UtcNow;
        var q = db.Deployments.Where(x => x.Os == DeployOs.Windows && DeployStatus.Active.Contains(x.Status));
        q = deployId is int id ? q.Where(x => x.Id == id || x.PcName == pcName) : q.Where(x => x.PcName == pcName);
        var n = await q.ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, DeployStatus.Done).SetProperty(x => x.Finished, now).SetProperty(x => x.MachineId, machineId));
        if (n > 0) await Sync();
    }

    /// <summary>Aktiv Linux-udrulning (Ubuntu, Debian, Fedora …) til en MAC-adresse.</summary>
    public async Task<Deployment?> ActiveLinux(string? mac)
    {
        var m = MacAddr.Normalize(mac);
        return m is null ? null : await db.Deployments.Where(x => x.Mac == m && x.Os != DeployOs.Windows && DeployStatus.Active.Contains(x.Status))
            .OrderByDescending(x => x.Id).FirstOrDefaultAsync();
    }

    public async Task<Deployment?> ActiveUbuntu(string? mac) => await ActiveLinux(mac) is { Os: DeployOs.Ubuntu } d ? d : null;

    public async Task MarkStarted(Deployment d)
    {
        if (d.Status != DeployStatus.Ready) return;
        d.Status = DeployStatus.Installing; d.Started = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await Sync();
    }

    public async Task MarkDone(Deployment d)
    {
        d.Status = DeployStatus.Done; d.Finished = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await Sync();
    }

    /// <summary>Adressen Linux-installeren bruger (helst en IP – installeren kender ikke altid navnet). Tom = serveradressen.</summary>
    public async Task<string> PxeHttp()
    {
        var v = (await settings.Get("pxe_http")).TrimEnd('/');
        return v.Length > 0 ? v : (await settings.Get("server_url")).TrimEnd('/');
    }

    /// <summary>Det, svarfilen skal indeholde. Felter i <see cref="Deployment.AskFields"/> gives ikke med, så de spørges om på maskinen.</summary>
    public async Task<InstallSpec> Spec(Deployment d)
    {
        var baseUrl = await PxeHttp();
        var locale = await InstallLocale.Load(settings);
        bool Ask(string f) => d.AskFields.Contains(f);
        return new InstallSpec
        {
            Hostname = d.PcName, UserName = d.UserName, FullName = d.FullName, PasswordHash = d.PasswordHash, SshKey = d.SshKey, Admin = !d.NoAdmin,
            Language = Ask(DeployField.Language) ? null : InstallLocale.FindLanguage(d.Language) ?? locale.Language,
            Keyboard = Ask(DeployField.Keyboard) ? null : InstallLocale.FindKeyboard(d.Keyboard) ?? locale.Keyboard,
            TimeZone = Ask(DeployField.TimeZone) ? null : InstallLocale.ValidTimeZone(d.TimeZone) ? d.TimeZone : locale.TimeZone,
            AutoDisk = d.AutoDisk, Encrypt = d.Encrypt, Variant = d.Variant,
            Packages = d.Packages.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            ProfileId = d.ProfileId, Mode = d.Mode, DeployId = d.Id,
            // engangskode pr. udrulning – aldrig den globale nøgle i en svarfil, der hentes uden login
            InstallUrl = $"{baseUrl}/install.sh?key={Uri.EscapeDataString(await DeployInstallKey.Get(db, settings, d))}",
            DoneUrl = $"{baseUrl}/pxe/{(d.Os == DeployOs.Ubuntu ? "ubuntu" : "linux")}/{MacAddr.Dash(d.Mac)}/done",
        };
    }

    // ------------------------------------------------------------------ svarfilerne
    public async Task<string> UserData(Deployment d) => AnswerFiles.Autoinstall(await Spec(d));

    /// <summary>Svarfilen til en Linux-udrulning, der ikke er Ubuntu: "preseed.cfg" (Debian) eller "ks.cfg" (Fedora, Rocky, Alma). Ellers null.</summary>
    public async Task<string?> LinuxAnswer(Deployment d, string file)
    {
        var e = OsCatalog.Get(OsIdOf(d));
        if (e is null) return null;
        var s = await Spec(d);
        if (file == "preseed.cfg" && e.Method == AnswerMethod.Preseed) return AnswerFiles.Preseed(s);
        if (file == "ks.cfg" && e.Method == AnswerMethod.Kickstart)
        {
            var repo = os.Ready(e.Id)?.Repo ?? e.Netboot!.TreeUrl;
            return AnswerFiles.Kickstart(s, e.Family, repo, e.Netboot?.AppStream);
        }
        return null;
    }

    /// <summary>Windows: skal udrulningen have sin egen svarfil? (Ellers bruger WinPE-menuen skabelonen USB\autounattend-pxe.xml som før – den spørger også om disken.)</summary>
    public static bool NeedsOwnAnswerFile(Deployment d) =>
        d.Os == DeployOs.Windows && (OsIdOf(d) != "windows-11" || d.AskFields.Any(f => f != DeployField.Disk) || d.AutoDisk || d.Encrypt
                                     || d.Language.Length > 0 || d.Keyboard.Length > 0 || d.TimeZone.Length > 0);

    public async Task<string> WindowsAnswer(Deployment d)
    {
        var e = OsCatalog.Get(OsIdOf(d))!;
        var s = await Spec(d);
        int? index = e.IsWindowsServer ? (d.Variant == "desktop" ? 2 : 1) : null;   // Standard: 1 = Server Core, 2 = Desktop Experience
        return AnswerFiles.Autounattend(s with { Hostname = Ascii(s.Hostname), UserName = Ascii(s.UserName) }, e, index);
    }

    private async Task<string> GrubFor(Deployment d)
    {
        var baseUrl = await PxeHttp();
        var installing = d.Status == DeployStatus.Installing;
        var e = OsCatalog.Get(OsIdOf(d));
        var mac = MacAddr.Dash(d.Mac);
        string title, kernel, initrd;
        if (d.Os == DeployOs.Ubuntu)
        {
            var info = os.Ready($"ubuntu-{d.Series}");
            var iso = info is null ? "" : $"{baseUrl}/pxe/files/ubuntu-{d.Series}/{info.Iso}";
            title = $"Installer Ubuntu {info?.Version ?? d.Series} {(d.Variant == "desktop" ? "Desktop" : "Server")} som {Or(d.PcName, "(navn vælges på maskinen)")} (WinSetup)";
            kernel = $"/ubuntu/{d.Series}/linux iso-url={iso} ip=dhcp autoinstall \"ds=nocloud;s={baseUrl}/pxe/ubuntu/{mac}/\" ---";
            initrd = $"/ubuntu/{d.Series}/initrd";
        }
        else
        {
            var id = e?.Id ?? d.OsId;
            title = $"Installer {(e is null ? id : Describe(d))} som {Or(d.PcName, "(navn vælges på maskinen)")} (Fleetdeck)";
            initrd = $"/linux/{id}/initrd";
            if (e?.Method == AnswerMethod.Preseed)
            {
                var s = await Spec(d);
                var host = d.PcName.Length > 0 ? $" hostname={d.PcName} domain=" : "";
                kernel = $"/linux/{id}/linux auto=true priority={(AnswerFiles.PreseedAsks(s) ? "high" : "critical")} url={baseUrl}/pxe/linux/{mac}/preseed.cfg interface=auto{host} ---";
            }
            else
                kernel = $"/linux/{id}/linux inst.repo={os.Ready(id)?.Repo ?? e?.Netboot?.TreeUrl} inst.ks={baseUrl}/pxe/linux/{mac}/ks.cfg ip=dhcp";
        }
        return $"""
            set timeout={(installing ? 30 : 10)}
            set default={(installing ? 1 : 0)}
            menuentry "{title}" {"{"}
              set gfxpayload=keep
              echo "Henter {(d.Os == DeployOs.Ubuntu ? "Ubuntu" : e?.FamilyTitle ?? "installationen")} fra serveren - det tager et par minutter ..."
              linux {kernel}
              initrd {initrd}
            {"}"}
            menuentry "Start fra disken" {"{"} exit {"}"}

            """.Replace("\r\n", "\n");
    }

    private const string GrubDefault = """
        set timeout=10
        set default=0
        # [ -f ] virker ikke over TFTP: indlæs pc'ens egen menu direkte (findes den ikke, fortsætter GRUB her)
        configfile "/grub/mac/${net_default_mac}.cfg"
        configfile "/grub/mac/${net_efinet0_mac}.cfg"
        menuentry "Ingen Linux-udrulning til denne pc - start fra disken" { exit }
        menuentry "Windows-installation (WinSetup)" { chainloader /Boot/bootmgfw.efi }

        """;

    /// <summary>PXE\deploy\&lt;MAC&gt;.cmd: hvad WinPE-menuen viser og installerer (D_WINDIR = mappen med setup.exe).</summary>
    public static string WinCmd(Deployment d, string? profileName)
    {
        var e = OsCatalog.Get(OsIdOf(d));
        var s = $"set \"D_ID={d.Id}\"\r\nset \"D_MODE={d.Mode}\"\r\nset \"D_PROFILE={d.ProfileId}\"\r\nset \"D_PROFILENAME={Ascii(profileName ?? "")}\"\r\n" +
                $"set \"PCNAME={d.PcName}\"\r\nset \"WUSER={d.UserName}\"\r\n";
        if (NeedsOwnAnswerFile(d))
            s += $"set \"D_WINDIR={e?.WinDir ?? "win11"}\"\r\nset \"D_TITLE={Ascii(Describe(d))}\"\r\nset \"D_XML=1\"\r\n";
        return s;
    }

    /// <summary>Skriv filerne til netværksbootet ud fra databasen. Køres efter hver ændring og hvert halve minut.</summary>
    public async Task Sync()
    {
        var locale = await InstallLocale.Load(settings);
        // locale.json: USB-nøglen (Lib.ps1) og pxe-build.ps1 læser sprog, tastatur og tidszone herfra
        if (Directory.Exists(paths.WinSetup)) WriteIfChanged(Path.Combine(paths.WinSetup, "locale.json"), locale.ShareJson());
        if (!Directory.Exists(paths.Pxe)) return;
        try
        {
            foreach (var dir in new[] { Dir, HostsDir, GrubDir, Path.Combine(GrubDir, "mac") }) Directory.CreateDirectory(dir);
            var now = DateTime.UtcNow;
            await db.Deployments.Where(x => DeployStatus.Active.Contains(x.Status) && x.Created < now.AddDays(-7))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, DeployStatus.Expired).SetProperty(x => x.Finished, now));
            var active = await db.Deployments.Where(x => DeployStatus.Active.Contains(x.Status)).ToListAsync();

            // WinPE skriver <MAC>.used, når en Windows-udrulning tages i brug
            foreach (var d in active.Where(x => x.Os == DeployOs.Windows && x.Status == DeployStatus.Ready))
            {
                var used = Path.Combine(Dir, d.Mac + ".used");
                if (!File.Exists(used)) continue;
                d.Status = DeployStatus.Installing; d.Started = now;
                TryDelete(used);
            }
            await db.SaveChangesAsync();

            var profiles = await db.Profiles.AsNoTracking().OrderBy(p => p.Name).Select(p => new { p.Id, p.Name }).ToListAsync();
            var defId = await settings.DefaultProfileId();
            var want = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [Path.Combine(Dir, "profiles.txt")] = string.Concat(profiles.Select(p => $"{p.Id}|{Ascii(p.Name)}\r\n")),
                [Path.Combine(Dir, "defaults.cmd")] =
                    $"set \"DEF_USER={Ascii(await settings.Get("pxe_default_user"))}\"\r\n" +
                    $"set \"DEF_PROFILENAME={Ascii(profiles.FirstOrDefault(p => p.Id == defId)?.Name ?? "")}\"\r\n" +
                    locale.WinPeCmd(),
                [Path.Combine(GrubDir, "grub.cfg")] = GrubDefault.Replace("\r\n", "\n"),
            };
            foreach (var d in active.Where(x => x.Os == DeployOs.Windows && x.Status == DeployStatus.Ready))
            {
                want[Path.Combine(Dir, d.Mac + ".cmd")] = WinCmd(d, profiles.FirstOrDefault(p => p.Id == d.ProfileId)?.Name);
                if (NeedsOwnAnswerFile(d)) want[Path.Combine(Dir, d.Mac + ".xml")] = await WindowsAnswer(d);
            }
            foreach (var d in active.Where(x => x.Os != DeployOs.Windows))
            {
                var colon = MacAddr.Colon(d.Mac);
                want[Path.Combine(HostsDir, MacAddr.Dash(d.Mac))] = $"{colon},set:ubuntu\n";   // tagget giver GRUB-bootfilen (også til Debian/Fedora …)
                var g = await GrubFor(d);
                want[Path.Combine(GrubDir, "grub.cfg-01-" + MacAddr.Dash(d.Mac))] = g;
                want[Path.Combine(GrubDir, "mac", colon + ".cfg")] = g;
            }

            foreach (var (path, content) in want)
                try { if (!File.Exists(path) || File.ReadAllText(path) != content) File.WriteAllText(path, content, new UTF8Encoding(false)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.LogWarning("Kunne ikke skrive {Path}: {Message}", path, e.Message); }
            IEnumerable<string> Files(string dir, string pattern) => Directory.Exists(dir) ? Directory.GetFiles(dir, pattern) : [];
            foreach (var f in Files(Dir, "*.cmd").Concat(Files(Dir, "*.xml")).Concat(Files(HostsDir, "*")).Concat(Files(GrubDir, "grub.cfg-01-*")).Concat(Files(Path.Combine(GrubDir, "mac"), "*.cfg")))
                if (!want.ContainsKey(f)) TryDelete(f);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.LogWarning("Kunne ikke skrive udrulningsfiler: {Message}", e.Message); }
    }

    private void WriteIfChanged(string path, string content)
    {
        try { if (!File.Exists(path) || File.ReadAllText(path) != content) File.WriteAllText(path, content, new UTF8Encoding(false)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.LogWarning("Kunne ikke skrive {Path}: {Message}", path, e.Message); }
    }

    private static string Or(string a, string b) => a.Length > 0 ? a : b;
    private static void TryDelete(string f) { try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
