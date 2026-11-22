using System.Text;
using System.Text.RegularExpressions;
using Fleetdeck.Shared;

namespace Fleetdeck.Server.Services;

/// <summary>
/// Stoppede tjenester: alarmens tekst og rettelsen "Start tjenesten". Navnene tages altid fra helbredsdataene (HealthReport.FailedServices)
/// og valideres med en streng whitelist, så intet fra en alarmtekst eller en agent kan sprøjtes ind i scriptet.
/// </summary>
public static partial class ServiceStart
{
    /// <summary>Så mange tjenester nævnes i alarmen og startes højst ad gangen.</summary>
    public const int Max = 8;

    /// <summary>En tjeneste, der kan startes. Init: systemd | user | openrc | runit | windows.</summary>
    public sealed record Unit(string Name, string Init, string? User, string Entry)
    {
        /// <summary>Navnet uden ".service", som folk kender det (fx "cups" og "kiosk-browser (bruger kiosk)").</summary>
        public string Display => (Name.EndsWith(".service", StringComparison.Ordinal) ? Name[..^8] : Name) + (User is null ? "" : $" (bruger {User})");
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9:_.@\\-]{0,200}$")] private static partial Regex SafeUnit();
    [GeneratedRegex(@"^[a-z_][a-z0-9_.-]{0,31}$")] private static partial Regex SafeUser();
    [GeneratedRegex(@"^[A-Za-z0-9_.\- ]{1,128}$")] private static partial Regex SafeWinService();
    [GeneratedRegex(@"^(?<n>[^ ]+) \((?<x>[^)]+)\)$")] private static partial Regex WithSuffix();

    /// <summary>Unraid melder arrayet og pariteten i samme liste – de er ikke tjenester og kan ikke startes herfra.</summary>
    public static bool IsUnraid(string entry) => entry.StartsWith("Unraid-", StringComparison.Ordinal);

    /// <summary>En linje fra helbredsdataene som tjeneste, eller null hvis den ikke kan (eller ikke må) startes.</summary>
    public static Unit? Parse(string entry, string platform)
    {
        if (string.IsNullOrWhiteSpace(entry) || IsUnraid(entry)) return null;
        if (platform == Platforms.Windows) return SafeWinService().IsMatch(entry) ? new(entry, "windows", null, entry) : null;
        if (platform != Platforms.Linux) return null;
        if (WithSuffix().Match(entry) is { Success: true } m)
        {
            var n = m.Groups["n"].Value; var x = m.Groups["x"].Value;
            if (!SafeUnit().IsMatch(n)) return null;
            return x switch
            {
                "OpenRC" => new(n, "openrc", null, entry),
                "runit" => new(n, "runit", null, entry),
                _ when SafeUser().IsMatch(x) && n.Contains('.') => new(n, "user", x, entry),
                _ => null,
            };
        }
        return SafeUnit().IsMatch(entry) && entry.Contains('.') ? new(entry, "systemd", null, entry) : null;
    }

    /// <summary>De tjenester fra helbredsdataene, rettelsen må starte (de samme, alarmen nævner).</summary>
    public static List<Unit> Startable(IEnumerable<string> failed, string platform) =>
        failed.Where(e => !IsUnraid(e)).Take(Max).Select(e => Parse(e, platform)).OfType<Unit>().ToList();

    /// <summary>Hvad det betyder, at en kendt tjeneste ikke kører (null = ukendt tjeneste).</summary>
    public static string? Consequence(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.EndsWith(".service", StringComparison.Ordinal)) n = n[..^8];
        return n switch
        {
            "cups" or "cups-browsed" or "spooler" => "Uden den kan maskinen ikke printe.",
            "ssh" or "sshd" or "openssh-server" => "Uden den kan du ikke logge ind med SSH.",
            "networkmanager" or "systemd-networkd" or "dhcpcd" or "wpa_supplicant" => "Uden den kan netværket falde ud.",
            "docker" or "containerd" or "podman" => "Uden den kører containerne ikke.",
            "smbd" or "nmbd" or "samba" or "nfs-server" => "Uden den virker fildelingen ikke.",
            "nginx" or "apache2" or "httpd" or "caddy" => "Uden den svarer webserveren ikke.",
            "cron" or "crond" or "cronie" => "Uden den kører planlagte opgaver ikke.",
            "bluetooth" => "Uden den virker Bluetooth ikke.",
            "pipewire" or "pulseaudio" or "wireplumber" => "Uden den er der ingen lyd.",
            "fwupd-refresh" or "fwupd" or "apt-daily" or "apt-daily-upgrade" or "man-db" or "logrotate" => "Den er ikke vigtig – maskinen virker fint uden.",
            _ => null,
        };
    }

    /// <summary>Alarmen for stoppede tjenester. Nøglen er altid "services"; titlen nævner tjenesten, når der kun er én.</summary>
    public static Finding? Finding(IReadOnlyList<string> failed, string platform)
    {
        if (failed.Count == 0) return null;
        var services = failed.Where(e => !IsUnraid(e)).ToList();
        var unraid = failed.Where(IsUnraid).ToList();
        var names = services.Take(Max).Select(e => Parse(e, platform)?.Display ?? e).ToList();
        string title;
        var detail = new StringBuilder();
        if (services.Count == 1)
        {
            title = $"Tjenesten {names[0]} kører ikke";
            detail.Append("Den er gået ned og er ikke startet igen. ");
            detail.Append(Consequence(Parse(services[0], platform)?.Name ?? services[0]) ?? "Det, der bruger den, virker ikke, før den kører igen.");
        }
        else if (services.Count > 1)
        {
            title = $"{services.Count} tjenester er stoppet";
            detail.Append(string.Join(", ", names)).Append(services.Count > Max ? " …" : "").Append(". De er gået ned og er ikke startet igen.");
            var known = services.Take(Max).Select(e => Consequence(Parse(e, platform)?.Name ?? e)).OfType<string>().Distinct().Where(c => !c.StartsWith("Den er ikke vigtig", StringComparison.Ordinal)).ToList();
            if (known.Count > 0) detail.Append(' ').Append(string.Join(" ", known.Select(c => c.Replace("Uden den", "Uden dem", StringComparison.Ordinal))));
        }
        else title = unraid.Any(u => u.StartsWith("Unraid-array", StringComparison.Ordinal)) ? "Unraid-arrayet er ikke startet" : "Paritetsfejl på Unraid";
        // Unraid sidst, så "Unraid-" først i teksten betyder: kun Unraid (se HelperCatalog.AdviceFor)
        if (unraid.Count > 0) detail.Append(detail.Length > 0 ? " " : "").Append(string.Join(". ", unraid)).Append('.');
        return new Finding("services", "warning", title, detail.ToString());
    }

    private static string Sh(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>Scriptet, der starter tjenesterne (sh på Linux, PowerShell på Windows). Null, hvis der intet er at starte.</summary>
    public static string? Script(IReadOnlyList<Unit> units, string platform)
    {
        if (units.Count == 0) return null;
        var sb = new StringBuilder();
        if (platform == Platforms.Windows)
        {
            sb.AppendLine("$rc = 0");
            foreach (var u in units)
                sb.AppendLine($"try {{ Start-Service -Name '{u.Name}' -ErrorAction Stop; 'Startet: {u.Name}' }} catch {{ \"Kunne ikke starte {u.Name}: $($_.Exception.Message)\"; $rc = 1 }}");
            sb.AppendLine("if ($rc -eq 0) { 'RESULTAT: Tjenesterne er startet.' } else { 'RESULTAT: Mindst én tjeneste kunne ikke startes – se fejlen ovenfor.' }");
            sb.AppendLine("exit $rc");
            return sb.ToString();
        }
        sb.AppendLine("rc=0");
        sb.AppendLine("ok() { echo \"Startet: $1\"; }");
        sb.AppendLine("bad() { echo \"Kunne ikke starte $1\"; rc=1; }");
        foreach (var u in units)
        {
            var n = Sh(u.Name);
            switch (u.Init)
            {
                case "systemd":
                    sb.AppendLine($"systemctl reset-failed {n} 2>/dev/null; if systemctl start {n}; then ok {n}; else bad {n}; systemctl status --no-pager -n 10 {n} 2>&1 || true; fi");
                    break;
                case "user":
                    var who = Sh(u.User! + "@");
                    sb.AppendLine($"systemctl --user -M {who} reset-failed {n} 2>/dev/null; if systemctl --user -M {who} start {n}; then ok {n}; else bad {n}; systemctl --user -M {who} status --no-pager -n 10 {n} 2>&1 || true; fi");
                    break;
                case "openrc":
                    sb.AppendLine($"if rc-service {n} restart; then ok {n}; else bad {n}; fi");
                    break;
                case "runit":
                    sb.AppendLine("d=; for x in /var/service /run/runit/service /etc/runit/runsvdir/default /service; do [ -d \"$x\" ] && { d=$x; break; }; done");
                    sb.AppendLine($"if [ -n \"$d\" ] && sv up \"$d\"/{n}; then ok {n}; else bad {n}; fi");
                    break;
            }
        }
        sb.AppendLine("if [ $rc -eq 0 ]; then echo 'RESULTAT: Tjenesterne er startet.'; else echo 'RESULTAT: Mindst én tjeneste kunne ikke startes – se fejlen ovenfor.'; fi");
        sb.AppendLine("exit $rc");
        return sb.ToString();
    }
}
