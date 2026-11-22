using System.Text.Json;
using System.Text.RegularExpressions;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Unix;

/// <summary>
/// Afinstallerer et program fra programlisten på webben (jobbet remove_app), uden spørgsmål.
/// Systempakker simuleres først: fjerner det for meget eller noget vigtigt, stoppes der, før noget er rørt.
/// </summary>
public static class RemoveApp
{
    /// <summary>Højst så mange pakker må forsvinde i alt (programmet selv medregnet).</summary>
    private const int MaxPackages = 15;

    /// <summary>Pakker, der aldrig fjernes herfra (* = hvad som helst). Holdt kort: det vigtigste for at maskinen starter og kan nås.</summary>
    private static readonly Regex[] Protected = new[]
    {
        // fælles
        "systemd*", "sudo", "bash", "coreutils", "util-linux", "openssh-server", "openssh", "network-manager", "NetworkManager*", "networkmanager",
        "grub*", "shim*", "*fleetdeck*", "filesystem", "base-files", "login", "passwd", "init",
        // kerne og C-bibliotek
        "linux-image*", "linux-generic*", "linux-headers*", "linux", "linux-lts", "linux-zen", "linux-hardened", "kernel*", "libc6", "glibc*", "musl",
        // pakkehåndtering
        "apt", "dpkg", "dnf*", "yum", "rpm", "zypper", "libzypp", "pacman", "apk-tools", "xbps", "eopkg", "rpm-ostree", "flatpak", "snapd",
        // skrivebord og login
        "ubuntu-desktop*", "ubuntu-minimal", "ubuntu-standard", "gnome-shell", "gdm", "gdm3", "lightdm", "sddm", "xserver-xorg*", "xorg-server",
        "plasma-workspace", "plasma-desktop", "cinnamon", "xfce4-session", "mate-session-manager", "budgie-desktop",
        // distroernes grundpakker
        "base", "alpine-base", "busybox", "openrc", "base-system", "runit-void", "setup", "mkinitcpio", "dracut", "initramfs-tools",
    }.Select(p => new Regex("^" + Regex.Escape(p).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase)).ToArray();

    private static readonly string[] ProtectedSnaps = ["snapd", "core", "core18", "core20", "core22", "core24", "bare"];

    private static string Q(string s) => Pkg.Q(s);
    private static JobResult Fail(string text) => new() { Status = JobStatus.Failed, ExitCode = 1, Output = text };
    private static JobResult Ok(string text) => new() { Status = JobStatus.Success, ExitCode = 0, Output = text };
    private static JobResult Done(ShellResult r, string okText) => new()
    {
        Status = r.ExitCode == 0 ? JobStatus.Success : JobStatus.Failed, ExitCode = r.ExitCode,
        Output = (r.TimedOut ? "[Agent] Tiden løb ud.\n" : "") + (r.Output.Length > 0 ? r.Output : okText),
    };

    private static bool IsProtected(string pkg) => Protected.Any(r => r.IsMatch(pkg));
    private static bool ValidName(string s) => s.Length is > 0 and < 200 && s[0] != '-' && s.All(c => char.IsLetterOrDigit(c) || c is '-' or '.' or '+' or '_' or ':' or '@');

    public static JobResult Run(JobParams p, TimeSpan timeout)
    {
        var name = (p.Name ?? "").Trim();
        var scope = (p.PackageType ?? "").Trim().ToLowerInvariant();
        if (Unraid.Is) return Fail("På Unraid fjernes plugins og containere i Unraids egen webside.");
        if (Mac.Is) return RunMac(p, name, scope, timeout);
        if (!ValidName(name)) return Fail($"Ugyldigt pakkenavn: {name}");
        if (name.Contains("fleetdeck", StringComparison.OrdinalIgnoreCase)) return Fail("Fleetdeck-agenten fjernes med \"Fjern agent\", ikke herfra.");
        switch (scope)
        {
            case "snap":
                if (!Shell.Has("snap")) return Fail("snap findes ikke på denne maskine.");
                if (ProtectedSnaps.Contains(name)) return Fail($"Afinstallation stoppet: {name} er en del af snap-systemet.");
                return Done(Shell.Run($"snap remove {Q(name)}", timeout), $"{name} er afinstalleret.");
            case "flatpak":
                return Flatpak(name, timeout);
            case "rpm-ostree":
                return Native == Pkg.Manager.RpmOstree ? RpmOstree(name, timeout) : Unknown(scope);
            default:
                if (scope.Length == 0 || scope != Pkg.NativeName || Pkg.Native is Pkg.Manager.None or Pkg.Manager.SteamOS) return Unknown(scope);
                if (IsProtected(name)) return Fail($"Afinstallation stoppet: {name} er en vigtig systempakke.");
                var (sim, err) = Simulate(name, timeout);
                if (err is not null) return Fail(err);
                if (sim!.Count == 0) return Ok($"{name} er ikke installeret.");
                if (Refuse(name, sim) is { } why) return Fail(why);
                return Done(Shell.Run(Pkg.RemoveCmd(name), timeout), $"{name} er afinstalleret.");
        }
    }

    private static Pkg.Manager Native => Pkg.Native;
    private static JobResult Unknown(string scope) => Fail($"Ved ikke hvordan programmet afinstalleres (\"{scope}\").");

    /// <summary>Tjekker listen af pakker, der ville blive fjernet. Null = i orden.</summary>
    private static string? Refuse(string name, List<string> removed)
    {
        var others = removed.Where(r => !r.Equals(name, StringComparison.OrdinalIgnoreCase)).Distinct().ToList();
        var bad = removed.Where(IsProtected).ToList();
        if (Native == Pkg.Manager.Apt)
        {
            // Essential: yes eller Priority: required er Debians egne "rør ikke"-pakker
            var q = Shell.Out($"dpkg-query -W -f='${{Package}} ${{Essential}} ${{Priority}}\\n' {string.Join(' ', removed.Select(Q))} 2>/dev/null", 30);
            foreach (var l in q.Split('\n'))
                if (l.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var pk, ..] f && (f.Contains("yes") || f.Contains("required"))) bad.Add(pk);
        }
        string List() => string.Join(", ", others.Take(10)) + (others.Count > 10 ? ", …" : "");
        if (bad.Count > 0)
        {
            var b = string.Join(", ", bad.Distinct().Take(10));
            return others.Count == 0 ? $"Afinstallation stoppet: {b} er en vigtig systempakke."
                : $"Afinstallation stoppet: ville også fjerne {others.Count} pakker ({List()}), heriblandt systempakken {b}.";
        }
        if (removed.Distinct().Count() > MaxPackages)
            return $"Afinstallation stoppet: ville også fjerne {others.Count} pakker ({List()}).";
        return null;
    }

    /// <summary>Kører en prøve-afinstallation og returnerer pakkerne, der ville blive fjernet (tom liste = ikke installeret).</summary>
    private static (List<string>? Removed, string? Error) Simulate(string name, TimeSpan timeout)
    {
        var t = timeout < TimeSpan.FromMinutes(5) ? timeout : TimeSpan.FromMinutes(5);
        var list = new List<string>();
        ShellResult r;
        switch (Native)
        {
            case Pkg.Manager.Apt:
                r = Shell.Run($"apt-get -s remove {Q(name)}", t, maxChars: int.MaxValue);
                if (r.ExitCode != 0) return (null, "Prøve-afinstallationen fejlede:\n" + r.Output);
                foreach (var l in r.Output.Split('\n'))
                    if (l.StartsWith("Remv ") && l.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [_, var pk, ..]) list.Add(pk.Split(':')[0]);
                return (list, null);
            case Pkg.Manager.Dnf or Pkg.Manager.Yum:
            {
                // --assumeno afslutter altid med fejlkode; det er listen, der tæller
                var tool = Native == Pkg.Manager.Yum ? "yum" : Shell.Has("dnf") ? "dnf" : "dnf5";
                r = Shell.Run($"{tool} remove --assumeno {Q(name)}", t, maxChars: int.MaxValue);
                var o = r.Output;
                if (Regex.IsMatch(o, @"No match for argument|No packages marked for removal|No packages to remove|Nothing to do", RegexOptions.IgnoreCase)) return (list, null);
                var arches = new HashSet<string> { "x86_64", "noarch", "i686", "i386", "aarch64", "armv7hl", "ppc64le", "s390x", "riscv64" };
                var inRemove = false;
                foreach (var raw in o.Split('\n'))
                {
                    var l = raw.TrimEnd();
                    var trimmed = l.TrimStart();
                    if (trimmed.StartsWith("Removing", StringComparison.Ordinal) && trimmed.EndsWith(':') && !trimmed.Contains("  ")) { inRemove = true; continue; }
                    if (l.Length == 0 || !char.IsWhiteSpace(l[0]) || trimmed.StartsWith("Transaction", StringComparison.Ordinal)) { inRemove = false; continue; }
                    if (!inRemove) continue;
                    var tok = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                    if (!arches.Contains(tok) && !tok.EndsWith(':')) list.Add(tok);
                }
                if (list.Count == 0) return (null, "Kunne ikke afgøre, hvad afinstallationen ville fjerne:\n" + o);
                // sammendraget ("Remove  3 Packages" / "Removing: 3 packages") vinder, hvis listen er ombrudt anderledes
                if (Regex.Match(o, @"^\s*Remov(?:e|ing):?\s+(\d+)\s+Packages?", RegexOptions.IgnoreCase | RegexOptions.Multiline) is { Success: true } m
                    && int.Parse(m.Groups[1].Value) > list.Count)
                    while (list.Count < int.Parse(m.Groups[1].Value)) list.Add("?");
                return (list, null);
            }
            case Pkg.Manager.Zypper or Pkg.Manager.Transactional:
            {
                r = Shell.Run($"zypper -n rm --dry-run {Q(name)}", t, maxChars: int.MaxValue);
                if (Regex.IsMatch(r.Output, "not installed|not found", RegexOptions.IgnoreCase) && !r.Output.Contains("REMOVED")) return (list, null);
                var lines = r.Output.Split('\n');
                for (var i = 0; i < lines.Length; i++)
                {
                    if (!lines[i].Contains("going to be REMOVED")) continue;
                    for (var k = i + 1; k < lines.Length && lines[k].Trim().Length > 0; k++) list.AddRange(lines[k].Split(' ', StringSplitOptions.RemoveEmptyEntries));
                }
                if (list.Count == 0) return (null, "Kunne ikke afgøre, hvad afinstallationen ville fjerne:\n" + r.Output);
                return (list, null);
            }
            case Pkg.Manager.Pacman:
                r = Shell.Run($"pacman -Rp --print-format '%n' {Q(name)}", t, maxChars: int.MaxValue);
                if (r.ExitCode != 0) return r.Output.Contains("target not found") ? (list, null) : (null, "Prøve-afinstallationen fejlede:\n" + r.Output);
                list.AddRange(r.Output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.Contains(' ')));
                return (list, null);
            case Pkg.Manager.Apk:
                r = Shell.Run($"apk del -s {Q(name)}", t, maxChars: int.MaxValue);
                if (r.ExitCode != 0) return (null, "Prøve-afinstallationen fejlede:\n" + r.Output);
                foreach (Match m in Regex.Matches(r.Output, @"^\(\d+/\d+\)\s+Purging\s+(\S+)", RegexOptions.Multiline)) list.Add(m.Groups[1].Value);
                return (list, null);
            case Pkg.Manager.Xbps:
                r = Shell.Run($"xbps-remove -n {Q(name)}", t, maxChars: int.MaxValue);
                if (r.ExitCode != 0 && !r.Output.Contains(" remove ")) return r.Output.Contains("not currently installed") ? (list, null) : (null, "Prøve-afinstallationen fejlede:\n" + r.Output);
                foreach (var l in r.Output.Split('\n'))
                    if (l.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var pv, "remove", ..])
                        list.Add(Regex.Match(pv, @"^(.+)-[^-]+_\d+$") is { Success: true } m ? m.Groups[1].Value : pv);
                return (list, null);
            case Pkg.Manager.Eopkg:
                return ([name], null);   // eopkg har ingen prøvekørsel – kun listen over vigtige pakker gælder
            default:
                return (null, "Ved ikke hvordan programmet afinstalleres.");
        }
    }

    /// <summary>rpm-ostree: kun pakker, der er lagt oven på systembilledet, kan fjernes.</summary>
    private static JobResult RpmOstree(string name, TimeSpan timeout)
    {
        var layered = false;
        try
        {
            using var doc = JsonDocument.Parse(Shell.Out("rpm-ostree status --json", 60) is { Length: > 0 } js ? js : "{}");
            if (doc.RootElement.TryGetProperty("deployments", out var deps))
                foreach (var d in deps.EnumerateArray())
                    if (d.TryGetProperty("booted", out var b) && b.ValueKind == JsonValueKind.True)
                        foreach (var key in new[] { "requested-packages", "packages" })
                            if (d.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
                                layered |= arr.EnumerateArray().Any(x => x.GetString() == name);
        }
        catch (JsonException) { }
        if (!layered) return Fail("Systembilledet kan ikke ændres herfra – kun pakker, der er lagt på bagefter, kan afinstalleres.");
        if (IsProtected(name)) return Fail($"Afinstallation stoppet: {name} er en vigtig systempakke.");
        return Done(Shell.Run($"{Pkg.RemoveCmd(name)} && echo 'Fjernet fra systemet – væk efter genstart.'", timeout), $"{name} er afinstalleret.");
    }

    /// <summary>flatpak: først installationen for hele maskinen, ellers brugerens egen.</summary>
    private static JobResult Flatpak(string id, TimeSpan timeout)
    {
        if (!Shell.Has("flatpak")) return Fail("flatpak findes ikke på denne maskine.");
        if (!id.Contains('.')) return Fail($"Ugyldigt flatpak-id: {id}");
        if (Shell.Run($"flatpak info --system {Q(id)} >/dev/null 2>&1", TimeSpan.FromSeconds(30)).ExitCode == 0)
            return Done(Shell.Run($"flatpak uninstall -y --noninteractive --system {Q(id)}", timeout), $"{id} er afinstalleret.");
        if (UserSession.Find() is { } s && Shell.Run($"flatpak info --user {Q(id)} >/dev/null 2>&1", TimeSpan.FromSeconds(30), s.User).ExitCode == 0)
            return Done(Shell.Run($"flatpak uninstall -y --noninteractive --user {Q(id)}", timeout, s.User), $"{id} er afinstalleret for {s.User}.");
        return Ok($"{id} er ikke installeret (eller hører til en bruger, der ikke er logget ind).");
    }

    // ------------------------------------------------------------------ Mac

    private static readonly Regex AppPath = new(@"^(/Applications|/Users/[^/]+/Applications)(/[^/]+)?/[^/]+\.app$");

    private static JobResult RunMac(JobParams p, string name, string scope, TimeSpan timeout)
    {
        switch (scope)
        {
            case "brew":
            case "brew-cask" or "cask":
            {
                if (Mac.Brew is null) return Fail("Homebrew mangler på denne Mac.");
                if (name.Length is 0 or > 200 || name[0] == '-' || !name.All(c => char.IsLetterOrDigit(c) || c is '-' or '.' or '+' or '_' or '@' or '/'))
                    return Fail($"Ugyldigt pakkenavn: {name}");
                if (scope == "brew" && name == "homebrew") return Fail("Homebrew selv afinstalleres ikke herfra.");
                if (name.Contains("fleetdeck", StringComparison.OrdinalIgnoreCase)) return Fail("Fleetdeck-agenten fjernes med \"Fjern agent\", ikke herfra.");
                var cask = scope != "brew" ? "--cask " : "";
                return Done(Mac.AsBrewUser($"{Mac.Brew} uninstall {cask}{Q(name)}", timeout), $"{name} er afinstalleret.");
            }
            case "app" or "mas":
                return RemoveMacApp(p, timeout);
            default:
                return Unknown(scope);
        }
    }

    /// <summary>Lukker programmet og sletter .app-mappen – kun i /Applications eller ~/Applications, og aldrig Apples egne.</summary>
    private static JobResult RemoveMacApp(JobParams p, TimeSpan timeout)
    {
        var raw = (p.Identifier is { Length: > 0 } id ? id : p.Path ?? "").Trim().TrimEnd('/');
        if (raw.Length == 0 || raw.Contains('\0') || raw.Contains("/../") || raw.Contains("/./") || raw.EndsWith("/..")) return Fail($"Ugyldig programsti: {raw}");
        string path;
        try { path = Path.GetFullPath(raw).TrimEnd('/'); } catch (Exception) { return Fail($"Ugyldig programsti: {raw}"); }
        if (path != raw || !AppPath.IsMatch(path) || path.Contains(".app/")) return Fail($"Afinstallation stoppet: {raw} er ikke et program i Programmer-mappen.");
        if (path.Contains("fleetdeck", StringComparison.OrdinalIgnoreCase)) return Fail("Fleetdeck-agenten fjernes med \"Fjern agent\", ikke herfra.");
        if (!Directory.Exists(path)) return Ok($"{path} findes ikke længere.");
        // mappen over (fx /Applications/Utilities) må ikke være et link ud af Programmer-mappen
        var parent = Path.GetDirectoryName(path)!;
        if (new DirectoryInfo(parent).LinkTarget is not null) return Fail($"Afinstallation stoppet: {parent} er et link.");
        var isLink = new DirectoryInfo(path).LinkTarget is not null;
        if (!isLink)
        {
            // Apples egne programmer er signeret "Software Signing" (fx Safari) – dem rører vi ikke
            var sign = Shell.Run($"codesign -dv --verbose=2 {Q(path)} 2>&1", TimeSpan.FromSeconds(30), maxChars: int.MaxValue).Output;
            if (Regex.IsMatch(sign, @"^Authority=Software Signing\s*$", RegexOptions.Multiline)) return Fail($"Afinstallation stoppet: {Path.GetFileName(path)} er en del af macOS.");
        }
        // luk programmet pænt først (som brugeren på skærmen)
        var appName = Path.GetFileNameWithoutExtension(path);
        if (Mac.Session() is { } s)
        {
            var script = $"quit app \"{appName.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
            Shell.Run($"osascript -e {Q(script)}", TimeSpan.FromSeconds(20), s.User);
        }
        var r = Shell.Run($"rm -rf -- {Q(path)}", timeout);
        if (r.ExitCode == 0 && (Directory.Exists(path) || File.Exists(path))) r = r with { ExitCode = 1, Output = r.Output + $"\n{path} kunne ikke slettes helt." };
        return Done(r, $"{appName} er afinstalleret.");
    }
}
