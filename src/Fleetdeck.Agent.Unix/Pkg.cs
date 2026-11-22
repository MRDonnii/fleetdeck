using System.Text.RegularExpressions;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Unix;

/// <summary>
/// Distroens egen pakkehåndtering – plus snap og flatpak, som findes på tværs af distroer. Alt kører uden spørgsmål.
///   apt          Debian, Ubuntu, Linux Mint, Pop!_OS, Zorin, elementary, Raspberry Pi OS, MX, Devuan
///   dnf / yum    Fedora, RHEL, Rocky, AlmaLinux, CentOS Stream, Oracle Linux, Amazon Linux (yum: CentOS 7, Amazon Linux 2)
///   zypper       openSUSE Leap/Tumbleweed, SUSE; transactional-update på openSUSE MicroOS/Aeon/Kalpa
///   pacman       Arch, Manjaro, EndeavourOS, Garuda, CachyOS, Artix
///   apk          Alpine (musl – egen agent)
///   xbps         Void
///   eopkg        Solus
///   rpm-ostree   Fedora Silverblue/Kinoite/Atomic, Bazzite, Bluefin, Aurora (hele systemet opdateres som ét billede)
///   steamos      SteamOS (Steam Deck): systemet med steamos-update, programmer via flatpak
/// </summary>
public static class Pkg
{
    public enum Manager { None, Apt, Dnf, Yum, Zypper, Transactional, Pacman, Apk, Xbps, Eopkg, RpmOstree, SteamOS }

    private static Manager? _native;
    public static Manager Native => _native ??= Detect();

    private static Manager Detect()
    {
        var id = OsRelease.Get("ID");
        if (id == "steamos" && Shell.Has("steamos-update")) return Manager.SteamOS;
        if (File.Exists("/run/ostree-booted") && Shell.Has("rpm-ostree")) return Manager.RpmOstree;
        if (Shell.Has("apt-get")) return Manager.Apt;
        if (Shell.Has("dnf") || Shell.Has("dnf5")) return Manager.Dnf;
        if (Shell.Has("yum")) return Manager.Yum;
        if (Shell.Has("zypper")) return Shell.Has("transactional-update") && ReadOnlyRoot() ? Manager.Transactional : Manager.Zypper;
        if (Shell.Has("pacman")) return Manager.Pacman;
        if (Shell.Has("apk")) return Manager.Apk;
        if (Shell.Has("xbps-install")) return Manager.Xbps;
        if (Shell.Has("eopkg")) return Manager.Eopkg;
        return Manager.None;
    }

    private static bool ReadOnlyRoot() => Shell.Out("findmnt -no OPTIONS / 2>/dev/null").Split(',').Contains("ro");

    public static string NativeName => Native switch
    {
        Manager.Apt => "apt", Manager.Dnf => "dnf", Manager.Yum => "yum", Manager.Zypper or Manager.Transactional => "zypper",
        Manager.Pacman => "pacman", Manager.Apk => "apk", Manager.Xbps => "xbps", Manager.Eopkg => "eopkg",
        Manager.RpmOstree => "rpm-ostree", Manager.SteamOS => "steamos", _ => "ingen",
    };

    /// <summary>Systemet opdateres som ét billede og skal genstartes bagefter (rpm-ostree, transactional-update, SteamOS).</summary>
    public static bool Image => Native is Manager.RpmOstree or Manager.Transactional or Manager.SteamOS;

    private const string AptOpts = "-y -o Dpkg::Options::=--force-confdef -o Dpkg::Options::=--force-confold -o DPkg::Lock::Timeout=600";   // vent på unattended-upgrades i stedet for at fejle
    private static string Dnf => Shell.Has("dnf") ? "dnf" : "dnf5";
    public static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>
    /// Pakkenavnet til denne distro. Et id kan have navne pr. pakkehåndtering, fx
    /// "apt:lm-sensors dnf:lm_sensors pacman:lm_sensors zypper:sensors" – ellers bruges det samme navn overalt.
    /// "rpm:" gælder dnf, yum og zypper; "*:" er reserven.
    /// </summary>
    public static string NameFor(string id)
    {
        if (!id.Contains(':')) return id;
        var map = id.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split(':', 2)).Where(p => p.Length == 2)
            .ToDictionary(p => p[0].ToLowerInvariant(), p => p[1]);
        var rpm = Native is Manager.Dnf or Manager.Yum or Manager.Zypper or Manager.Transactional or Manager.RpmOstree;
        return map.TryGetValue(NativeName, out var n) ? n
            : rpm && map.TryGetValue("dnf", out var d) && Native != Manager.Zypper ? d
            : rpm && map.TryGetValue("rpm", out var r) ? r
            : map.TryGetValue("*", out var all) ? all : "";
    }

    /// <summary>Opdatér pakkelisterne højst hver 6. time (før installation og opdateringstjek).</summary>
    public static void Refresh(bool force = false)
    {
        var stamp = "/var/lib/fleetdeck/pkg-refresh";
        if (!force && File.Exists(stamp) && File.GetLastWriteTimeUtc(stamp) > DateTime.UtcNow.AddHours(-6)) return;
        var cmd = Native switch
        {
            Manager.Apt => "apt-get update -qq",
            Manager.Dnf => $"{Dnf} -q -y makecache",
            Manager.Yum => "yum -q -y makecache",
            Manager.Zypper or Manager.Transactional => "zypper -n --quiet refresh",
            Manager.Pacman => "pacman -Sy --noconfirm >/dev/null",
            Manager.Apk => "apk update -q",
            Manager.Xbps => "xbps-install -S >/dev/null",
            Manager.Eopkg => "eopkg ur >/dev/null",
            _ => "true",
        };
        Shell.Run(cmd, TimeSpan.FromMinutes(10));
        try { Directory.CreateDirectory(Path.GetDirectoryName(stamp)!); File.WriteAllText(stamp, ""); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private const string NotHere = "echo 'Denne distro installerer ikke enkelte systempakker – brug flatpak' >&2; exit 1";

    public static string InstallCmd(string name, string extra = "") => Native switch
    {
        Manager.Apt => $"apt-get install {AptOpts} {extra} {Q(name)}",
        Manager.Dnf => $"{Dnf} install -y {extra} {Q(name)}",
        Manager.Yum => $"yum install -y {extra} {Q(name)}",
        Manager.Zypper => $"zypper -n install --auto-agree-with-licenses {extra} {Q(name)}",
        Manager.Transactional => $"transactional-update -n pkg install {Q(name)} && echo 'Aktiv efter genstart.'",
        Manager.Pacman => $"pacman -S --noconfirm --needed {extra} {Q(name)}",
        Manager.Apk => $"apk add -q {Q(name)}",
        Manager.Xbps => $"xbps-install -y {Q(name)}",
        Manager.Eopkg => $"eopkg it -y {Q(name)}",
        Manager.RpmOstree => $"rpm-ostree install --idempotent --allow-inactive {Q(name)} && echo 'Lagt på systemet – aktiv efter genstart.'",
        Manager.SteamOS => NotHere,
        _ => "echo 'Ingen kendt pakkehåndtering på denne distro' >&2; exit 1",
    };

    public static string RemoveCmd(string name) => Native switch
    {
        Manager.Apt => $"apt-get remove {AptOpts} {Q(name)}",
        Manager.Dnf => $"{Dnf} remove -y {Q(name)}",
        Manager.Yum => $"yum remove -y {Q(name)}",
        Manager.Zypper => $"zypper -n remove {Q(name)}",
        Manager.Transactional => $"transactional-update -n pkg remove {Q(name)}",
        Manager.Pacman => $"pacman -R --noconfirm {Q(name)}",
        Manager.Apk => $"apk del -q {Q(name)}",
        Manager.Xbps => $"xbps-remove -y {Q(name)}",
        Manager.Eopkg => $"eopkg rm -y {Q(name)}",
        Manager.RpmOstree => $"rpm-ostree uninstall {Q(name)}",
        _ => "exit 1",
    };

    public static string UpgradeOneCmd(string name) => Native switch
    {
        Manager.Apt => $"apt-get install --only-upgrade {AptOpts} {Q(name)}",
        Manager.Dnf => $"{Dnf} upgrade -y {Q(name)}",
        Manager.Yum => $"yum update -y {Q(name)}",
        Manager.Zypper => $"zypper -n update {Q(name)}",
        Manager.Pacman => $"pacman -S --noconfirm {Q(name)}",
        Manager.Apk => $"apk add -u -q {Q(name)}",
        Manager.Xbps => $"xbps-install -yu {Q(name)}",
        Manager.Eopkg => $"eopkg up -y {Q(name)}",
        // billed-systemer: hele systemet på én gang
        Manager.RpmOstree => "rpm-ostree upgrade",
        Manager.Transactional => "transactional-update -n dup",
        Manager.SteamOS => "steamos-update",
        _ => "exit 1",
    };

    /// <summary>Opdatér alt: systemets pakker (med undtagelser), derefter snap og flatpak – ikke firmware (se <see cref="Firmware"/>). '::ws a-b tekst' = fremdrift.</summary>
    public static string UpgradeAllScript(IReadOnlyList<string> exclude) => SystemScript(exclude) + ExtrasScript + "exit $rc";

    /// <summary>snap og flatpak (efter systempakkerne). Sætter ikke rc.</summary>
    public const string ExtrasScript =
        "if command -v snap >/dev/null; then echo '::ws 85-92 Opdaterer snaps'; snap refresh; fi; " +
        "if command -v flatpak >/dev/null; then echo '::ws 92-100 Opdaterer flatpaks'; flatpak update -y --noninteractive; fi; ";

    /// <summary>Markør i output: systemet er opdateret, men det nye aktiveres først ved genstart (SteamOS, rpm-ostree, transactional-update).</summary>
    public const string RebootMarker = "::fleetdeck-reboot-needed";

    /// <summary>
    /// Før opdatering: vent på en anden pakkehåndtering (unattended-upgrades, PackageKit) og reparér en afbrudt opdatering
    /// (dpkg --configure -a / apt-get -f install). Andre pakkehåndteringer: kun vent på låsen.
    /// </summary>
    public static string RepairScript => Native switch
    {
        Manager.Apt => "for i in $(seq 1 60); do fuser /var/lib/dpkg/lock-frontend /var/lib/dpkg/lock >/dev/null 2>&1 || break; [ $i = 1 ] && echo 'Venter på en anden pakkeopdatering…'; sleep 10; done; " +
                       "export DEBIAN_FRONTEND=noninteractive; dpkg --configure -a --force-confdef --force-confold && apt-get -f install -y -o Dpkg::Options::=--force-confdef -o Dpkg::Options::=--force-confold",
        Manager.Dnf or Manager.Yum => "for i in $(seq 1 60); do pgrep -x 'dnf|dnf5|yum|packagekitd' >/dev/null || break; sleep 10; done; true",
        Manager.Zypper => "for i in $(seq 1 60); do [ -e /run/zypp.pid ] && kill -0 \"$(cat /run/zypp.pid 2>/dev/null)\" 2>/dev/null || break; sleep 10; done; true",
        Manager.Pacman => "for i in $(seq 1 60); do [ -e /var/lib/pacman/db.lck ] || break; sleep 10; done; true",
        _ => "true",
    };

    /// <summary>Kun systemets pakker (sætter rc).</summary>
    public static string SystemScript(IReadOnlyList<string> exclude)
    {
        var holds = exclude.Where(x => x.Length > 0).Select(Q).ToList();
        var h = string.Join(' ', holds);
        var main = Native switch
        {
            Manager.Apt => (holds.Count > 0 ? $"apt-mark hold {h} >/dev/null 2>&1; " : "") +
                           $"echo '::ws 0-8 Henter pakkelister'; apt-get update -qq && echo '::ws 8-80 Opdaterer pakker' && apt-get full-upgrade {AptOpts} && echo '::ws 80-85 Rydder op' && apt-get autoremove -y; rc=$?; " +
                           (holds.Count > 0 ? $"apt-mark unhold {h} >/dev/null 2>&1; " : ""),
            Manager.Dnf => $"echo '::ws 0-80 Opdaterer pakker'; {Dnf} upgrade -y --refresh {string.Join(' ', holds.Select(x => "--exclude=" + x))}; rc=$?; ",
            Manager.Yum => $"echo '::ws 0-80 Opdaterer pakker'; yum update -y {string.Join(' ', holds.Select(x => "--exclude=" + x))}; rc=$?; ",
            Manager.Zypper => (holds.Count > 0 ? $"zypper -n addlock {h} >/dev/null 2>&1; " : "") +
                              "echo '::ws 0-8 Henter pakkelister'; zypper -n refresh && echo '::ws 8-80 Opdaterer pakker' && zypper -n update --auto-agree-with-licenses; rc=$?; " +
                              (holds.Count > 0 ? $"zypper -n removelock {h} >/dev/null 2>&1; " : ""),
            Manager.Transactional => "echo '::ws 0-80 Opdaterer systemet (aktivt efter genstart)'; transactional-update -n dup; rc=$?; ",
            Manager.Pacman => $"echo '::ws 0-80 Opdaterer pakker'; pacman -Syu --noconfirm {(holds.Count > 0 ? "--ignore " + string.Join(',', exclude.Where(x => x.Length > 0)) : "")}; rc=$?; ",
            Manager.Apk => "echo '::ws 0-8 Henter pakkelister'; apk update -q && echo '::ws 8-80 Opdaterer pakker' && apk upgrade -q; rc=$?; ",
            Manager.Xbps => (holds.Count > 0 ? $"xbps-pkgdb -m hold {h} >/dev/null 2>&1; " : "") +
                            "echo '::ws 0-10 Opdaterer xbps'; xbps-install -Syu xbps >/dev/null 2>&1; echo '::ws 10-80 Opdaterer pakker'; xbps-install -yu; rc=$?; " +
                            (holds.Count > 0 ? $"xbps-pkgdb -m unhold {h} >/dev/null 2>&1; " : ""),
            Manager.Eopkg => "echo '::ws 0-8 Henter pakkelister'; eopkg ur >/dev/null; echo '::ws 8-80 Opdaterer pakker'; eopkg up -y; rc=$?; ",
            Manager.RpmOstree => $"echo '::ws 0-80 Henter nyt systembillede (aktivt efter genstart)'; rpm-ostree upgrade --unchanged-exit-77; rc=$?; [ $rc = 0 ] && echo '{RebootMarker}'; [ $rc = 77 ] && rc=0; ",
            Manager.SteamOS => $"echo '::ws 0-80 Opdaterer SteamOS (aktivt efter genstart)'; steamos-update; rc=$?; [ $rc = 0 ] && echo '{RebootMarker}'; [ $rc = 7 ] && rc=0; ",
            _ => "rc=0; ",
        };
        return main;
    }

    // "name-1.2.3-r0" (apk) / "name-1.2.3_1" (xbps): navnet er alt før sidste bindestreg, der efterfølges af et ciffer
    internal static (string Name, string Version) SplitPkgVer(string pv)
    {
        var m = Regex.Match(pv, @"^(.+?)-(\d[^-]*(?:-r\d+)?)$");
        return m.Success ? (m.Groups[1].Value, m.Groups[2].Value) : (pv, "");
    }

    /// <summary>
    /// Programmer, der ikke kommer fra en pakkehåndtering, men som Fleetdeck skal kunne se (fx som pakke i en profil):
    /// Kiosk Warden (~/kiosk/version hos en bruger).
    /// </summary>
    private static IEnumerable<SoftwareEntry> KnownApps()
    {
        var homes = Directory.Exists("/home") ? Directory.GetDirectories("/home") : [];
        foreach (var h in homes)
        {
            // installationen skriver ~/kiosk/version (projektets VERSION-fil hedder med store bogstaver)
            var v = new[] { "version", "VERSION" }.Select(f => Path.Combine(h, "kiosk", f)).FirstOrDefault(File.Exists);
            if (v is null || !File.Exists(Path.Combine(h, "kiosk", "warden-state.sh"))) continue;
            string? ver = null;
            try { ver = File.ReadAllText(v).Trim(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            yield return new SoftwareEntry
            {
                Name = "Kiosk Warden", Version = ver, Publisher = "MRDonnii", Scope = "app",
                Description = "Styring og selvreparation af kiosk-skærmen (Chrome i kiosktilstand)", InstallPath = Path.Combine(h, "kiosk"),
            };
        }
    }

    /// <summary>
    /// Installerede pakker (navn, version, udgiver, beskrivelse) fra distroens database, snap og flatpak – beskrivelsen
    /// kommer med i det samme kald, der lister pakkerne. Derefter programmernes .desktop-filer (sti, programfil, ikon).
    /// </summary>
    public static List<SoftwareEntry> Installed()
    {
        var sw = new List<SoftwareEntry>();
        static string? D(string[] f, int i) => f.Length > i && f[i] != "(none)" ? SoftwareIcons.Clip(f[i]) : null;
        switch (Native)
        {
            case Manager.Apt:
                foreach (var l in Shell.Out("dpkg-query -W -f='${db:Status-Abbrev}\\t${binary:Package}\\t${Version}\\t${Maintainer}\\t${binary:Summary}\\n' 2>/dev/null", 60).Split('\n'))
                {
                    var f = l.Split('\t');
                    if (f.Length < 3 || !f[0].StartsWith("ii")) continue;
                    sw.Add(new SoftwareEntry { Name = f[1].Split(':')[0], Version = f[2], Publisher = f.Length > 3 ? f[3] : null, Description = D(f, 4), Scope = "apt" });
                }
                break;
            case Manager.Dnf or Manager.Yum or Manager.Zypper or Manager.Transactional or Manager.RpmOstree:
                foreach (var l in Shell.Out("rpm -qa --qf '%{NAME}\\t%{VERSION}-%{RELEASE}\\t%{VENDOR}\\t%{SUMMARY}\\n' 2>/dev/null", 60).Split('\n'))
                {
                    var f = l.Split('\t');
                    if (f.Length < 2 || f[0].StartsWith("gpg-pubkey")) continue;
                    sw.Add(new SoftwareEntry { Name = f[0], Version = f[1], Publisher = f.Length > 2 && f[2] != "(none)" ? f[2] : null, Description = D(f, 3), Scope = NativeName });
                }
                break;
            case Manager.Pacman or Manager.SteamOS:
            {
                // pacman -Qi: blokke med "Name : …", "Version : …", "Description : …", adskilt af en tom linje
                string? n = null, v = null, d = null;
                void Flush() { if (n is { Length: > 0 }) sw.Add(new SoftwareEntry { Name = n, Version = v, Description = d, Scope = "pacman" }); n = v = d = null; }
                foreach (var l in Shell.Out("pacman -Qi 2>/dev/null", 60).Split('\n'))
                {
                    if (l.Trim().Length == 0) { Flush(); continue; }
                    var i = l.IndexOf(':');
                    if (char.IsWhiteSpace(l[0]) || i < 0) continue;   // fortsat linje (fx valgfrie afhængigheder)
                    var val = l[(i + 1)..].Trim();
                    switch (l[..i].Trim())
                    {
                        case "Name": Flush(); n = val; break;
                        case "Version": v = val; break;
                        case "Description": d = val == "None" ? null : SoftwareIcons.Clip(val); break;
                    }
                }
                Flush();
                if (sw.Count == 0)
                    foreach (var l in Shell.Out("pacman -Q 2>/dev/null", 60).Split('\n'))
                        if (l.Split(' ', 2) is [var pn, var pv]) sw.Add(new SoftwareEntry { Name = pn, Version = pv, Scope = "pacman" });
                break;
            }
            case Manager.Apk:
                // "zlib-1.3.2-r1 x86_64 {zlib} (Zlib) [installed]"
                foreach (var l in Shell.Out("apk list -I 2>/dev/null", 60).Split('\n'))
                {
                    var pv = l.Split(' ')[0];
                    if (pv.Length == 0) continue;
                    var (n, v) = SplitPkgVer(pv);
                    sw.Add(new SoftwareEntry { Name = n, Version = v, Scope = "apk" });
                }
                // beskrivelserne står i apk's database (P: navn, T: beskrivelse) – én fil i stedet for "apk info -d" pr. pakke
                var apkDesc = ApkDescriptions();
                foreach (var s in sw) s.Description = SoftwareIcons.Clip(apkDesc.GetValueOrDefault(s.Name));
                break;
            case Manager.Xbps:
                // "ii acl-2.4.0_1      Access Control List filesystem support"
                foreach (var l in Shell.Out("xbps-query -l 2>/dev/null", 60).Split('\n'))
                {
                    var f = l.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                    if (f.Length < 2 || f[0] != "ii") continue;
                    var (n, v) = SplitPkgVer(f[1]);
                    sw.Add(new SoftwareEntry { Name = n, Version = v, Description = f.Length > 2 ? SoftwareIcons.Clip(f[2]) : null, Scope = "xbps" });
                }
                break;
            case Manager.Eopkg:
                // "firefox        - Mozilla Firefox ..." (versionen står ikke i listen)
                foreach (var l in Shell.Out("eopkg li -N 2>/dev/null", 60).Split('\n'))
                {
                    var n = l.Split(' ', 2)[0].Trim();
                    var desc = Regex.Match(l, @"^\S+\s+-\s+(.+)$") is { Success: true } dm ? SoftwareIcons.Clip(dm.Groups[1].Value) : null;
                    if (n.Length > 0 && !n.StartsWith("Installed")) sw.Add(new SoftwareEntry { Name = n, Description = desc, Scope = "eopkg" });
                }
                break;
        }
        if (Shell.Has("snap"))
            foreach (var l in Shell.Out("snap list 2>/dev/null").Split('\n').Skip(1))
            {
                var f = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length >= 2) sw.Add(new SoftwareEntry { Name = f[0], Version = f[1], Publisher = f.Length > 4 ? f[4] : null, Scope = "snap" });
            }
        if (Shell.Has("flatpak"))
        {
            // ældre flatpak kender ikke kolonnen "description" – så uden den
            var fl = Shell.Out("flatpak list --app --columns=application,version,origin,description 2>/dev/null");
            if (fl.Length == 0) fl = Shell.Out("flatpak list --app --columns=application,version,origin 2>/dev/null");
            foreach (var l in fl.Split('\n'))
            {
                var f = l.Split('\t');
                if (f.Length >= 1 && f[0].Contains('.'))
                    sw.Add(new SoftwareEntry { Name = f[0], Version = f.Length > 1 ? f[1] : null, Publisher = f.Length > 2 ? f[2] : null, Description = D(f, 3), Scope = "flatpak" });
            }
        }
        DesktopApps.Enrich(sw);
        sw.AddRange(KnownApps());
        return sw;
    }

    /// <summary>Alpine: navn → beskrivelse fra /lib/apk/db/installed (P: og T: i hver blok).</summary>
    private static Dictionary<string, string> ApkDescriptions()
    {
        var d = new Dictionary<string, string>();
        try
        {
            string? name = null;
            foreach (var l in File.ReadLines("/lib/apk/db/installed"))
            {
                if (l.Length == 0) name = null;
                else if (l.StartsWith("P:")) name = l[2..];
                else if (l.StartsWith("T:") && name is not null) d[name] = l[2..];
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return d;
    }

    /// <summary>Pakker med en nyere version.</summary>
    public static List<UpgradeEntry> Upgrades()
    {
        Refresh();
        var list = new List<UpgradeEntry>();
        switch (Native)
        {
            case Manager.Apt:
                // "firefox/noble-updates 130.0 amd64 [upgradable from: 129.0]"
                foreach (var l in Shell.Out("apt list --upgradable 2>/dev/null", 60).Split('\n'))
                {
                    var m = Regex.Match(l, @"^([^/\s]+)/\S+\s+(\S+)\s+\S+\s+\[upgradable from: ([^\]]+)\]");
                    if (m.Success) list.Add(new UpgradeEntry { Name = m.Groups[1].Value, Id = m.Groups[1].Value, Available = m.Groups[2].Value, Current = m.Groups[3].Value });
                }
                break;
            case Manager.Dnf or Manager.Yum:
                // "firefox.x86_64   131.0-1.fc41   updates"
                foreach (var l in Shell.Out($"{(Native == Manager.Dnf ? Dnf : "yum")} -q check-update 2>/dev/null", 120).Split('\n'))
                {
                    var f = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (f.Length >= 3 && f[0].Contains('.') && !l.StartsWith(' ') && char.IsDigit(f[1][0]))
                    {
                        var name = f[0][..f[0].LastIndexOf('.')];
                        list.Add(new UpgradeEntry { Name = name, Id = name, Available = f[1] });
                    }
                }
                break;
            case Manager.Zypper or Manager.Transactional:
                // "v | repo | firefox | 130.0-1.1 | 131.0-1.1 | x86_64"
                foreach (var l in Shell.Out("zypper -n --quiet list-updates 2>/dev/null", 120).Split('\n'))
                {
                    var f = l.Split('|').Select(x => x.Trim()).ToArray();
                    if (f.Length >= 5 && f[0] == "v") list.Add(new UpgradeEntry { Name = f[2], Id = f[2], Current = f[3], Available = f[4] });
                }
                break;
            case Manager.Pacman:
                // "firefox 130.0-1 -> 131.0-1" (listen fra pacman -Sy i Refresh)
                foreach (var l in Shell.Out("pacman -Qu 2>/dev/null", 60).Split('\n'))
                {
                    var m = Regex.Match(l, @"^(\S+)\s+(\S+)\s+->\s+(\S+)");
                    if (m.Success) list.Add(new UpgradeEntry { Name = m.Groups[1].Value, Id = m.Groups[1].Value, Current = m.Groups[2].Value, Available = m.Groups[3].Value });
                }
                break;
            case Manager.Apk:
                // "zlib-1.4-r0 x86_64 {zlib} (Zlib) [upgradable from: zlib-1.3.2-r1]"
                foreach (var l in Shell.Out("apk list -u 2>/dev/null", 60).Split('\n'))
                {
                    var m = Regex.Match(l, @"^(\S+)\s.*\[upgradable from: ([^\]]+)\]");
                    if (!m.Success) continue;
                    var (n, v) = SplitPkgVer(m.Groups[1].Value);
                    list.Add(new UpgradeEntry { Name = n, Id = n, Available = v, Current = SplitPkgVer(m.Groups[2].Value).Version });
                }
                break;
            case Manager.Xbps:
                // "libarchive-3.8.11_1 update x86_64 https://repo… 825424 383202"
                foreach (var l in Shell.Out("xbps-install -Mnu 2>/dev/null", 60).Split('\n'))
                {
                    var f = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (f.Length < 2 || f[1] != "update") continue;
                    var (n, v) = SplitPkgVer(f[0]);
                    list.Add(new UpgradeEntry { Name = n, Id = n, Available = v });
                }
                break;
            case Manager.Eopkg:
                foreach (var l in Shell.Out("eopkg lu -N 2>/dev/null", 60).Split('\n'))
                {
                    var n = l.Split(' ', 2)[0].Trim();
                    if (n.Length > 0 && !l.StartsWith("No packages")) list.Add(new UpgradeEntry { Name = n, Id = n });
                }
                break;
            case Manager.RpmOstree:
            {
                // exit 0 = nyt systembillede, 77 = intet nyt
                var r = Shell.Run("rpm-ostree upgrade --check 2>&1", TimeSpan.FromMinutes(3));
                if (r.ExitCode == 0 && !r.Output.Contains("No updates available"))
                    list.Add(new UpgradeEntry { Name = "Systemopdatering (rpm-ostree)", Id = "system",
                        Available = Regex.Match(r.Output, @"Version:\s*(\S+)").Groups[1].Value is { Length: > 0 } v ? v : null });
                break;
            }
            case Manager.SteamOS:
            {
                var r = Shell.Run("steamos-update check 2>&1", TimeSpan.FromMinutes(3));
                if (r.ExitCode == 0) list.Add(new UpgradeEntry { Name = "SteamOS-opdatering", Id = "system", Available = r.Output.Trim().Split('\n').LastOrDefault() });
                break;
            }
        }
        if (Shell.Has("snap"))
            foreach (var l in Shell.Out("snap refresh --list 2>/dev/null", 60).Split('\n').Skip(1))
            {
                var f = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length >= 2) list.Add(new UpgradeEntry { Name = f[0] + " (snap)", Id = f[0], Available = f[1] });
            }
        if (Shell.Has("flatpak"))
            foreach (var l in Shell.Out("flatpak remote-ls --updates --app --columns=application,version 2>/dev/null", 60).Split('\n'))
            {
                var f = l.Split('\t');
                if (f[0].Contains('.')) list.Add(new UpgradeEntry { Name = f[0] + " (flatpak)", Id = f[0], Available = f.Length > 1 ? f[1] : null });
            }
        list.AddRange(Firmware());
        return list;
    }

    /// <summary>Firmware (BIOS/UEFI, SSD'er, docks, tastaturer …) fra LVFS via fwupd: "fwupd:&lt;DeviceId&gt;". Installeres kun, når man
    /// selv trykker – aldrig af "Opdatér alt" – fordi mange kræver en genstart, og en BIOS-opdatering må ikke komme bag på nogen.</summary>
    public static List<UpgradeEntry> Firmware()
    {
        var list = new List<UpgradeEntry>();
        if (!Shell.Has("fwupdmgr")) return list;
        Shell.Run("fwupdmgr refresh >/dev/null 2>&1", TimeSpan.FromMinutes(2));   // henter kun nye metadata, hvis de er gamle
        var json = Shell.Out("fwupdmgr get-updates --json 2>/dev/null", 120);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json.TrimStart().StartsWith('{') ? json : "{}");
            if (!doc.RootElement.TryGetProperty("Devices", out var devices)) return list;
            foreach (var d in devices.EnumerateArray())
            {
                if (!d.TryGetProperty("DeviceId", out var id) || id.GetString() is not { Length: > 0 } deviceId) continue;
                var name = d.TryGetProperty("Name", out var n) ? n.GetString() ?? "Enhed" : "Enhed";
                var current = d.TryGetProperty("Version", out var v) ? v.GetString() : null;
                string? available = null;
                if (d.TryGetProperty("Releases", out var rel) && rel.GetArrayLength() > 0 && rel[0].TryGetProperty("Version", out var rv)) available = rv.GetString();
                // systemfirmware (UEFI-kapsel, flashrom, Intel ME …) er BIOS – uanset hvad enheden hedder
                var plugin = d.TryGetProperty("Plugin", out var pl) ? pl.GetString() : null;
                var flags = d.TryGetProperty("Flags", out var fl) && fl.ValueKind == System.Text.Json.JsonValueKind.Array
                    ? fl.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];
                list.Add(new UpgradeEntry { Name = name + " (firmware)", Id = FirmwarePrefix + deviceId, Current = current, Available = available,
                    Bios = UpdatePlanner.FwupdIsBios(plugin, flags, name) });
            }
        }
        catch (System.Text.Json.JsonException) { }
        return list;
    }

    public const string FirmwarePrefix = "fwupd:";

    private static (DateTime At, bool Value)? _reboot;

    /// <summary>Venter systemet på en genstart (ny kerne, systembillede eller biblioteker)? Gemmes i 10 minutter.</summary>
    public static bool RebootNeeded(bool fresh = false)
    {
        if (!fresh && _reboot is { } c && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(10)) return c.Value;
        var v = CheckReboot();
        _reboot = (DateTime.UtcNow, v);
        return v;
    }

    private static bool CheckReboot()
    {
        if (File.Exists("/var/run/reboot-required") || File.Exists("/run/reboot-needed")) return true;
        switch (Native)
        {
            case Manager.Dnf or Manager.Yum:
                // needs-restarting -r: 1 = genstart nødvendig, 0 = ikke (andre koder: værktøjet mangler)
                var nr = Native == Manager.Dnf ? $"{Dnf} needs-restarting -r" : "needs-restarting -r";
                if (Shell.Run($"{nr} >/dev/null 2>&1", TimeSpan.FromMinutes(1)).ExitCode == 1) return true;
                break;
            case Manager.Zypper:
                if (Shell.Run("zypper -n needs-rebooting >/dev/null 2>&1", TimeSpan.FromMinutes(1)).ExitCode == 102) return true;
                break;
            case Manager.RpmOstree:
                return Shell.Out("rpm-ostree status 2>/dev/null").Contains("Staged: yes");
        }
        // ny kerne installeret og den kørende kernes moduler fjernet (Arch, Void, Alpine, Solus m.fl.)
        var kernel = Shell.Out("uname -r");
        var mods = new[] { "/lib/modules", "/usr/lib/modules" }.FirstOrDefault(Directory.Exists);
        return kernel.Length > 0 && mods is not null && Directory.EnumerateDirectories(mods).Any() && !Directory.Exists(Path.Combine(mods, kernel));
    }

    /// <summary>Hvornår pakkerne sidst blev ændret (til "sidst opdateret").</summary>
    public static DateTime? LastChange()
    {
        string[] candidates = ["/var/log/apt/history.log", "/var/log/dpkg.log", "/var/lib/dnf/history.sqlite", "/var/lib/yum/history", "/var/log/zypp/history",
            "/var/log/pacman.log", "/lib/apk/db/installed", "/var/db/xbps", "/var/log/eopkg.log", "/var/lib/rpm-ostree"];
        var times = candidates.Select(p => File.Exists(p) ? File.GetLastWriteTime(p) : Directory.Exists(p) ? Directory.GetLastWriteTime(p) : (DateTime?)null)
            .Where(t => t is not null).ToList();
        return times.Count > 0 ? times.Max() : null;
    }
}

/// <summary>/etc/os-release (ID, ID_LIKE, NAME, PRETTY_NAME, VERSION_ID).</summary>
public static class OsRelease
{
    private static Dictionary<string, string>? _v;
    public static string Get(string key)
    {
        _v ??= Read();
        return _v.GetValueOrDefault(key, "");
    }

    private static Dictionary<string, string> Read()
    {
        var d = new Dictionary<string, string>();
        foreach (var f in new[] { "/etc/os-release", "/usr/lib/os-release" })
        {
            if (!File.Exists(f)) continue;
            foreach (var l in File.ReadAllLines(f))
            {
                var p = l.Split('=', 2);
                if (p.Length == 2) d[p[0].Trim()] = p[1].Trim().Trim('"', '\'');
            }
            break;
        }
        return d;
    }
}
