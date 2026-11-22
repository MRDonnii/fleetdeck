using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Shared;

namespace Fleetdeck.Server.Services.Reports;

/// <summary>
/// Færdige rapporter (Drift → Rapporter). Hver rapport er en forespørgsel, der giver tabeller med rene værdier; eksport (CSV/PDF)
/// og visning sker andre steder. Rapporterne må aldrig tage hemmeligheder med: ingen adgangskoder, tokens, nøgler,
/// gendannelsesnøgler, TOTP-hemmeligheder eller fjernskrivebordets adgangskode – kun status og antal.
/// </summary>
public static class ReportCatalog
{
    public static readonly ReportDef[] All =
    [
        new("inventory", "Inventar", "Maskiner med OS, hardware, IP, bruger, agent og profil", "Maskiner", true, false, false, 0, []),
        new("software", "Software", "Installerede programmer pr. maskine og antal pr. program og version (licensoversigt)", "Maskiner", true, false, true, 0, []),
        new("updates", "Opdateringer", "Ventende opdateringer, hvor længe de har ventet, og fejlede opdateringer", "Drift", true, true, false, 30, [7, 30, 90]),
        new("security", "Sikkerhed", "Firewall, antivirus, diskkryptering, totrinsbekræftelse, agent-tillid og åbne alarmer", "Drift", true, false, false, 0, []),
        new("health", "Sundhed", "Helbredsscore over tid, hyppigste alarmer, diske og temperaturer", "Drift", true, true, false, 30, [30, 90, 365]),
        new("jobs", "Jobs", "Kørte jobs i perioden med succesrate pr. type og maskine", "Drift", true, true, false, 30, [7, 30, 90, 365]),
        new("ipads", "iPads/enheder", "Forbrug i minutter pr. barn og tjeneste pr. uge", "Familie", false, true, false, 28, [7, 14, 28, 35]),
    ];

    public static ReportDef? Find(string name) => All.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static async Task<ReportResult> Run(AppDb db, ReportDef def, ReportFilter f, DateTime now)
    {
        var days = def.Period ? f.Days ?? def.DefaultDays : 0;
        var r = def.Name switch
        {
            "inventory" => await Inventory(db, f, now),
            "software" => await Software(db, f, now),
            "updates" => await Updates(db, f, days, now),
            "security" => await Security(db, f, now),
            "health" => await Health(db, f, days, now),
            "jobs" => await Jobs(db, f, days, now),
            "ipads" => await Ipads(db, Math.Min(days, 35), now),
            _ => throw new ArgumentException(def.Name),
        };
        r.Filter = await Describe(db, def, f, days);
        return r;
    }

    private static async Task<string> Describe(AppDb db, ReportDef def, ReportFilter f, int days)
    {
        var parts = new List<string>();
        if (def.Period) parts.Add($"Seneste {(def.Name == "ipads" ? Math.Min(days, 35) : days)} dage");
        if (def.Machines && f.Profile is int p) parts.Add("profil " + (await db.Profiles.AsNoTracking().Where(x => x.Id == p).Select(x => x.Name).FirstOrDefaultAsync() ?? $"#{p}"));
        if (def.Machines && f.Machines is { Count: > 0 } ms) parts.Add(ms.Count == 1 ? "1 maskine" : $"{ms.Count} maskiner");
        if (def.Search && f.Q is { } q) parts.Add($"navn indeholder \"{q}\"");
        return parts.Count == 0 ? "Alle maskiner" : string.Join(" · ", parts);
    }

    private static ReportResult New(string name, DateTime now, List<ReportStat> stats, params ReportSection[] sections)
    {
        var d = Find(name)!;
        return new ReportResult { Name = d.Name, Title = d.Title, Description = d.Description, Generated = now, Stats = stats, Sections = [.. sections] };
    }

    private static List<ReportColumn> Cols(params (string Key, string Label, string Kind)[] c) => c.Select(x => new ReportColumn(x.Key, x.Label, x.Kind)).ToList();

    /// <summary>Maskinerne i filteret (profil og/eller udvalgte id'er), sorteret efter navn.</summary>
    public static async Task<List<Machine>> Scope(AppDb db, ReportFilter f)
    {
        var q = db.Machines.AsNoTracking().Include(m => m.Profile).AsQueryable();
        if (f.Profile is int p) q = q.Where(m => m.ProfileId == p);
        if (f.Machines is { Count: > 0 } ids) q = q.Where(m => ids.Contains(m.Id));
        return (await q.ToListAsync()).OrderBy(m => m.Label, StringComparer.Create(ReportFormat.Da, true)).ToList();
    }

    private static string PlatformName(string p) => p switch { Platforms.Windows => "Windows", Platforms.Linux => "Linux", Platforms.Mac => "macOS", _ => p };

    // ------------------------------------------------------------------------------------------------ Inventar
    private static async Task<ReportResult> Inventory(AppDb db, ReportFilter f, DateTime now)
    {
        var ms = await Scope(db, f);
        var rows = new List<object?[]>();
        foreach (var m in ms)
        {
            var facts = Json.Des<SystemFacts>(m.FactsJson);
            var live = Json.Des<HealthReport>(m.HealthJson).Live;
            var size = live.Volumes.Sum(v => v.SizeGb);
            var free = live.Volumes.Sum(v => v.FreeGb);
            var version = string.Join(" ", new[] { facts.OsVersion, facts.Build is { Length: > 0 } b && b != facts.OsVersion ? $"({b})" : null }.Where(x => x is { Length: > 0 }));
            rows.Add([m.Label, PlatformName(m.Platform), facts.Os ?? facts.Distro, version.Length > 0 ? version : null,
                facts.Manufacturer is { Length: > 0 } mf ? $"{mf} {facts.Model}".Trim() : facts.Model,
                facts.CpuName, facts.RamGb is double r ? Math.Round(r, 1) : null,
                size > 0 ? Math.Round(size, 0) : null, size > 0 ? Math.Round((size - free) / size * 100, 0) : null,
                m.Ip, m.LastSeen, m.User, m.AgentVersion, m.Profile?.Name]);
        }
        var newest = ms.Select(m => Version.TryParse(m.AgentVersion, out var v) ? v : null).Where(v => v is not null).Max();
        var oldAgents = newest is null ? 0 : ms.Count(m => Version.TryParse(m.AgentVersion, out var v) && v < newest);
        var unseen = ms.Count(m => m.LastSeen is null || now - m.LastSeen > TimeSpan.FromHours(24));
        var byOs = string.Join(" / ", new[] { Platforms.Windows, Platforms.Linux, Platforms.Mac }.Select(p => ms.Count(m => m.Platform == p)));
        return New("inventory", now,
            [new("Maskiner", ms.Count.ToString()), new("Windows / Linux / Mac", byOs), new("Ældre agent", oldAgents.ToString(), oldAgents > 0 ? "yellow" : null),
             new("Ikke set i 24 t", unseen.ToString(), unseen > 0 ? "yellow" : null)],
            new ReportSection
            {
                Key = "machines", Title = "Maskiner",
                Columns = Cols(("machine", "Maskine", "text"), ("platform", "Platform", "text"), ("os", "OS", "text"), ("version", "Version", "text"),
                    ("model", "Model", "text"), ("cpu", "CPU", "text"), ("ram", "RAM (GB)", "num"), ("disk", "Disk (GB)", "num"), ("diskUsed", "Disk brugt", "pct"),
                    ("ip", "IP", "text"), ("lastSeen", "Sidst set", "date"), ("user", "Bruger", "text"), ("agent", "Agent", "text"), ("profile", "Profil", "text")),
                Rows = rows,
            });
    }

    // ------------------------------------------------------------------------------------------------ Software
    private static async Task<ReportResult> Software(AppDb db, ReportFilter f, DateTime now)
    {
        var ms = await Scope(db, f);
        var names = ms.ToDictionary(m => m.Id, m => m.Label);
        var ids = names.Keys.ToList();
        var items = await db.Software.AsNoTracking().Where(s => ids.Contains(s.MachineId))
            .Select(s => new { s.MachineId, s.Name, s.Version, s.Publisher, s.Scope }).ToListAsync();
        if (f.Q is { } q) items = items.Where(s => s.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || (s.Publisher ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        var cmp = StringComparer.Create(ReportFormat.Da, true);
        var licenses = items.GroupBy(s => (s.Name, Version: s.Version ?? ""))
            .Select(g => new object?[]
            {
                g.Key.Name, g.Key.Version.Length > 0 ? g.Key.Version : null, g.Select(x => x.Publisher).FirstOrDefault(p => p is { Length: > 0 }),
                g.Select(x => x.MachineId).Distinct().Count(),
                string.Join(", ", g.Select(x => names[x.MachineId]).Distinct().Order(cmp)),
            })
            .OrderBy(r => (string)r[0]!, cmp).ThenBy(r => (string?)r[1], cmp).ToList();
        var perMachine = items.OrderBy(s => names[s.MachineId], cmp).ThenBy(s => s.Name, cmp)
            .Select(s => new object?[] { names[s.MachineId], s.Name, s.Version, s.Publisher, s.Scope }).ToList();
        var programs = items.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var multi = items.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Count(g => g.Select(x => x.Version).Distinct().Count() > 1);
        return New("software", now,
            [new("Programmer", programs.ToString()), new("Installationer", items.Count.ToString()), new("Maskiner", items.Select(s => s.MachineId).Distinct().Count().ToString()),
             new("Flere versioner", multi.ToString(), multi > 0 ? "yellow" : null)],
            new ReportSection
            {
                Key = "licenses", Title = "Licensoversigt (pr. program og version)",
                Columns = Cols(("name", "Program", "text"), ("version", "Version", "text"), ("publisher", "Udgiver", "text"), ("count", "Antal maskiner", "num"), ("machines", "Maskiner", "text")),
                Rows = licenses,
            },
            new ReportSection
            {
                Key = "installed", Title = "Installeret pr. maskine",
                Columns = Cols(("machine", "Maskine", "text"), ("name", "Program", "text"), ("version", "Version", "text"), ("publisher", "Udgiver", "text"), ("scope", "Omfang", "text")),
                Rows = perMachine,
            });
    }

    // ------------------------------------------------------------------------------------------------ Opdateringer
    private static readonly string[] UpdateJobTypes = [JobTypes.Upgrade, JobTypes.WindowsUpdate];

    private static async Task<ReportResult> Updates(AppDb db, ReportFilter f, int days, DateTime now)
    {
        var ms = await Scope(db, f);
        var ids = ms.Select(m => m.Id).ToList();
        var ups = await db.Upgrades.AsNoTracking().Where(u => ids.Contains(u.MachineId)).ToListAsync();
        var pending = new List<object?[]>();
        var summary = new List<object?[]>();
        double? Waited(DateTime? since) => since is { } s ? Math.Max(0, Math.Floor((now - s).TotalDays)) : null;
        foreach (var m in ms)
        {
            var h = Json.Des<HealthReport>(m.HealthJson);
            var apps = ups.Where(u => u.MachineId == m.Id).OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var u in apps) pending.Add([m.Label, "Program", u.Name, u.Current, u.Available, u.FirstSeen, Waited(u.FirstSeen)]);
            var os = h.Updates?.Pending ?? 0;
            if (os > 0)
            {
                var titles = h.Updates!.Titles.Count > 0 ? h.Updates.Titles : [$"{os} systemopdatering(er)"];
                foreach (var t in titles) pending.Add([m.Label, "System", t, null, null, m.OsUpdatesSince, Waited(m.OsUpdatesSince)]);
            }
            var oldest = apps.Select(a => a.FirstSeen).Append(os > 0 ? m.OsUpdatesSince : null).Where(d => d is not null).Min();
            if (apps.Count > 0 || os > 0 || h.Live.PendingReboot)
                summary.Add([m.Label, apps.Count, os, Waited(oldest), h.Live.PendingReboot, h.LastUpdate]);
        }
        var from = now.AddDays(-days);
        var failed = await db.Jobs.AsNoTracking().Where(j => ids.Contains(j.MachineId) && UpdateJobTypes.Contains(j.Type) && j.Status == JobStatus.Failed && (j.Finished ?? j.Created) >= from)
            .OrderByDescending(j => j.Finished ?? j.Created).Select(j => new { j.MachineId, j.Type, j.Title, j.Finished, j.Created, j.ExitCode, j.Source }).ToListAsync();
        var names = ms.ToDictionary(m => m.Id, m => m.Label);
        var failedRows = failed.Select(j => new object?[] { j.Finished ?? j.Created, names[j.MachineId], j.Type == JobTypes.Upgrade ? "Programmer" : "System", j.Title, j.ExitCode, j.Source }).ToList();
        var maxWait = pending.Select(r => r[6] as double?).Max();
        return New("updates", now,
            [new("Ventende", pending.Count.ToString(), pending.Count > 0 ? "yellow" : null), new("Maskiner med ventende", summary.Count(r => (int)r[1]! + (int)r[2]! > 0).ToString()),
             new("Ældste (dage)", maxWait is double w ? w.ToString(CultureInfo.InvariantCulture) : "–", maxWait > 14 ? "red" : null),
             new($"Fejlede ({days} dage)", failedRows.Count.ToString(), failedRows.Count > 0 ? "red" : null)],
            new ReportSection
            {
                Key = "pending", Title = "Ventende opdateringer",
                Columns = Cols(("machine", "Maskine", "text"), ("type", "Type", "text"), ("name", "Navn", "text"), ("current", "Nuværende", "text"), ("available", "Ny version", "text"),
                    ("since", "Først set", "day"), ("waited", "Venter (dage)", "num")),
                Rows = pending,
                Note = "Ventetiden måles fra første gang Fleetdeck så opdateringen.",
            },
            new ReportSection
            {
                Key = "machines", Title = "Pr. maskine",
                Columns = Cols(("machine", "Maskine", "text"), ("apps", "Programmer", "num"), ("os", "System", "num"), ("oldest", "Ældste (dage)", "num"),
                    ("reboot", "Genstart mangler", "bool"), ("lastUpdate", "Seneste systemopdatering", "text")),
                Rows = summary,
            },
            new ReportSection
            {
                Key = "failed", Title = $"Fejlede opdateringer (seneste {days} dage)",
                Columns = Cols(("ts", "Tid", "date"), ("machine", "Maskine", "text"), ("type", "Type", "text"), ("title", "Job", "text"), ("exit", "Exitkode", "num"), ("source", "Kilde", "text")),
                Rows = failedRows,
            });
    }

    // ------------------------------------------------------------------------------------------------ Sikkerhed
    public static string Firewall(HealthReport h) => h.Firewall.Count == 0 ? "–"
        : h.Firewall.Values.All(v => v) ? "Til" : "Fra: " + string.Join(", ", h.Firewall.Where(kv => !kv.Value).Select(kv => kv.Key));

    public static (string Text, bool Ok) Antivirus(HealthReport h, string platform)
    {
        var other = h.Antivirus.Where(a => a.Enabled && !a.Name.Contains("Defender", StringComparison.OrdinalIgnoreCase)).Select(a => a.Name).ToList();
        if (h.Defender is { } d)
        {
            if (d.Enabled == true) return ($"Defender{(d.Realtime == false ? " (realtid fra)" : "")}", d.Realtime != false);
            return other.Count > 0 ? (string.Join(", ", other), true) : ("Intet aktivt", false);
        }
        if (other.Count > 0) return (string.Join(", ", other), true);
        return (platform == Platforms.Windows ? "Ukendt" : "–", platform != Platforms.Windows);
    }

    public static (string Text, bool? Encrypted) Encryption(HealthReport h)
    {
        if (h.Encryption is { } e)
        {
            var kind = e.Kind switch { EncryptionKinds.BitLocker => "BitLocker", EncryptionKinds.FileVault => "FileVault", EncryptionKinds.Luks => "LUKS", "" => "Kryptering", var k => k };
            var v = e.Volumes.FirstOrDefault(x => x.System) ?? e.Volumes.FirstOrDefault();
            if (v is null) return ($"{kind}: {(e.Error is { Length: > 0 } ? "kunne ikke læses" : "ukendt")}", null);
            var state = v.Encrypted switch
            {
                true when v.Protected == false => "krypteret, beskyttelse slået fra",
                true => "krypteret",
                false when v.Percent is > 0 and < 100 => $"krypterer ({v.Percent:0} %)",
                false => "ikke krypteret",
                null => v.Status ?? "ukendt",
            };
            return ($"{kind}: {state}", v.Encrypted);
        }
        return h.BitLocker is { Length: > 0 } b ? ($"BitLocker: {b}", null) : ("–", null);
    }

    public static string Trust(Machine m) => m.JobTrust switch
    {
        AgentTrust.Pinned => "Nøgle pinnet",
        AgentTrust.Unpinned => "Ikke pinnet",
        AgentTrust.Mismatch => "Afviser nøglen",
        _ => "Ældre agent",
    };

    private static async Task<ReportResult> Security(AppDb db, ReportFilter f, DateTime now)
    {
        var ms = await Scope(db, f);
        var ids = ms.Select(m => m.Id).ToList();
        // kun antal gemte nøgler – selve nøglerne (Protected) læses aldrig her
        var keys = await db.RecoveryKeys.AsNoTracking().Where(k => ids.Contains(k.MachineId) && k.Superseded == null)
            .GroupBy(k => k.MachineId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        var alerts = await db.Alerts.AsNoTracking().Where(a => ids.Contains(a.MachineId) && a.Resolved == null)
            .Select(a => new { a.MachineId, a.Severity, a.Title, a.FirstSeen, a.Acknowledged }).ToListAsync();
        var rows = new List<object?[]>();
        int fwOff = 0, noAv = 0, unenc = 0, untrusted = 0;
        foreach (var m in ms)
        {
            var h = Json.Des<HealthReport>(m.HealthJson);
            var fw = Firewall(h); if (fw.StartsWith("Fra", StringComparison.Ordinal)) fwOff++;
            var av = Antivirus(h, m.Platform); if (!av.Ok) noAv++;
            var enc = Encryption(h); if (enc.Encrypted == false) unenc++;
            if (m.JobTrust != AgentTrust.Pinned) untrusted++;
            var mine = alerts.Where(a => a.MachineId == m.Id).ToList();
            rows.Add([m.Label, PlatformName(m.Platform), fw, av.Text, h.Defender?.SigAgeDays, enc.Text, keys.GetValueOrDefault(m.Id), Trust(m), m.TlsPinned,
                mine.Count(a => a.Severity == "critical"), mine.Count(a => a.Severity == "warning"), mine.Count(a => a.Severity == "info")]);
        }
        var users = (await db.Users.AsNoTracking().Select(u => new { u.Username, u.Role, u.TotpEnabledAt, u.RecoveryCodes, u.LastLogin, u.LockoutUntil }).ToListAsync())
            .OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase)
            .Select(u => new object?[] { u.Username, u.Role switch { Roles.Admin => "Administrator", Roles.Parent => "Forælder", Roles.Viewer => "Læser", var r => r },
                u.TotpEnabledAt is not null, u.TotpEnabledAt, u.TotpEnabledAt is null ? null : u.RecoveryCodes.Count, u.LastLogin, u.LockoutUntil > now }).ToList();
        var sevRank = new Dictionary<string, int> { ["critical"] = 0, ["warning"] = 1, ["info"] = 2 };
        var names = ms.ToDictionary(m => m.Id, m => m.Label);
        var alertRows = alerts.OrderBy(a => sevRank.GetValueOrDefault(a.Severity, 3)).ThenBy(a => a.FirstSeen)
            .Select(a => new object?[] { a.Severity, names[a.MachineId], a.Title, a.FirstSeen, a.Acknowledged is not null }).ToList();
        var no2fa = users.Count(u => !(bool)u[2]!);
        var crit = alerts.Count(a => a.Severity == "critical");
        return New("security", now,
            [new("Firewall fra", fwOff.ToString(), fwOff > 0 ? "red" : null), new("Uden antivirus", noAv.ToString(), noAv > 0 ? "red" : null),
             new("Ukrypteret", unenc.ToString(), unenc > 0 ? "yellow" : null), new("Uden 2FA", $"{no2fa} af {users.Count}", no2fa > 0 ? "yellow" : null),
             new("Agent ikke pinnet", untrusted.ToString(), untrusted > 0 ? "yellow" : null), new("Kritiske alarmer", crit.ToString(), crit > 0 ? "red" : null)],
            new ReportSection
            {
                Key = "machines", Title = "Maskiner",
                Columns = Cols(("machine", "Maskine", "text"), ("platform", "Platform", "text"), ("firewall", "Firewall", "text"), ("av", "Antivirus", "text"),
                    ("sigAge", "Definitioner (dage)", "num"), ("encryption", "Diskkryptering", "text"), ("keys", "Gendannelsesnøgler gemt", "num"),
                    ("trust", "Agent-tillid", "text"), ("tls", "TLS pinnet", "bool"), ("critical", "Kritiske", "num"), ("warning", "Advarsler", "num"), ("info", "Info", "num")),
                Rows = rows,
                Note = "Gendannelsesnøgler vises kun som antal. Selve nøglerne kommer aldrig med i en rapport.",
            },
            new ReportSection
            {
                Key = "users", Title = "Fleetdeck-brugere",
                Columns = Cols(("user", "Bruger", "text"), ("role", "Rolle", "text"), ("totp", "Totrinsbekræftelse", "bool"), ("totpSince", "Slået til", "day"),
                    ("codes", "Gendannelseskoder tilbage", "num"), ("lastLogin", "Sidst logget ind", "date"), ("locked", "Låst ude", "bool")),
                Rows = users,
            },
            new ReportSection
            {
                Key = "alerts", Title = "Åbne alarmer",
                Columns = Cols(("severity", "Alvor", "sev"), ("machine", "Maskine", "text"), ("title", "Alarm", "text"), ("since", "Siden", "date"), ("ack", "Kvitteret", "bool")),
                Rows = alertRows,
            });
    }

    // ------------------------------------------------------------------------------------------------ Sundhed
    private static async Task<ReportResult> Health(AppDb db, ReportFilter f, int days, DateTime now)
    {
        var ms = await Scope(db, f);
        var ids = ms.Select(m => m.Id).ToList();
        var names = ms.ToDictionary(m => m.Id, m => m.Label);
        var from = now.AddDays(-days);
        var fromDay = DateOnly.FromDateTime(ReportFormat.Local(from));
        var hist = await db.HealthDaily.AsNoTracking().Where(h => ids.Contains(h.MachineId) && h.Day >= fromDay).ToListAsync();
        var alerts = await db.Alerts.AsNoTracking().Where(a => ids.Contains(a.MachineId) && (a.Resolved == null || a.Resolved >= from))
            .Select(a => new { a.MachineId, a.Key, a.Severity, a.Title, a.FirstSeen, a.LastSeen, a.Resolved }).ToListAsync();

        var machineRows = ms.Select(m =>
        {
            var mine = hist.Where(h => h.MachineId == m.Id).ToList();
            return new object?[]
            {
                m.Label, m.HealthScore, m.HealthState, mine.Count > 0 ? mine.Min(h => h.Score) : null,
                mine.Sum(h => h.Samples) > 0 ? Math.Round((double)mine.Sum(h => h.ScoreSum) / mine.Sum(h => h.Samples), 0) : null,
                mine.Count(h => h.State == "critical"), alerts.Count(a => a.MachineId == m.Id && a.Resolved == null),
            };
        }).ToList();

        var history = hist.GroupBy(h => h.Day).OrderBy(g => g.Key)
            .Select(g => new object?[] { g.Key, Math.Round(g.Average(h => h.Samples > 0 ? (double)h.ScoreSum / h.Samples : h.Score), 0), g.Min(h => h.Score), g.Count(h => h.State == "critical"), g.Count() })
            .ToList();

        // samme slags alarm samles (fx "disk:C:" og "disk:D:" er begge "disk")
        var top = alerts.GroupBy(a => a.Key.Split(':')[0])
            .Select(g =>
            {
                var latest = g.MaxBy(a => a.LastSeen)!;
                var worst = g.Any(a => a.Severity == "critical") ? "critical" : g.Any(a => a.Severity == "warning") ? "warning" : "info";
                return new object?[] { latest.Title, worst, g.Count(), g.Select(a => a.MachineId).Distinct().Count(), g.Count(a => a.Resolved == null), latest.LastSeen };
            })
            .OrderByDescending(r => (int)r[2]!).ThenByDescending(r => (int)r[3]!).Take(25).ToList();

        var disks = new List<object?[]>();
        var temps = new List<object?[]>();
        var metricFrom = now.AddDays(-7);
        var maxTemps = await db.Metrics.AsNoTracking().Where(x => ids.Contains(x.MachineId) && x.Ts >= metricFrom)
            .GroupBy(x => x.MachineId).Select(g => new { g.Key, Cpu = g.Max(x => x.CpuTemp), Gpu = g.Max(x => x.GpuTemp) }).ToDictionaryAsync(x => x.Key);
        int worn = 0, failing = 0;
        foreach (var m in ms)
        {
            var h = Json.Des<HealthReport>(m.HealthJson);
            foreach (var d in h.Disks)
            {
                if (d.Wear >= 80) worn++;
                if (d.Health is { } hh && !hh.Equals("Healthy", StringComparison.OrdinalIgnoreCase)) failing++;
                disks.Add([m.Label, d.Name, d.Media, d.SizeGb is double s ? Math.Round(s, 0) : null, d.Health, d.Wear, d.Temp, d.PowerOnHours, d.ReadErrors]);
            }
            maxTemps.TryGetValue(m.Id, out var mt);
            if (h.Live.Temps.Cpu is not null || h.Live.Temps.Gpu is not null || mt?.Cpu is not null)
                temps.Add([m.Label, h.Live.Temps.Cpu, h.Live.Temps.Gpu, mt?.Cpu, mt?.Gpu]);
        }
        var scored = ms.Where(m => m.HealthScore is not null).ToList();
        return New("health", now,
            [new("Gns. score", scored.Count > 0 ? $"{scored.Average(m => m.HealthScore!.Value):0} / 100" : "–"),
             new("Åbne alarmer", alerts.Count(a => a.Resolved == null).ToString()),
             new("Slidte diske", worn.ToString(), worn > 0 ? "yellow" : null), new("Diskfejl", failing.ToString(), failing > 0 ? "red" : null)],
            new ReportSection
            {
                Key = "machines", Title = $"Helbred pr. maskine (seneste {days} dage)",
                Columns = Cols(("machine", "Maskine", "text"), ("score", "Score nu", "num"), ("state", "Tilstand", "text"), ("min", "Laveste", "num"), ("avg", "Gennemsnit", "num"),
                    ("critDays", "Dage med kritisk", "num"), ("open", "Åbne alarmer", "num")),
                Rows = machineRows,
            },
            new ReportSection
            {
                Key = "history", Title = "Scorehistorik (hele flåden pr. dag)",
                Columns = Cols(("day", "Dag", "day"), ("avg", "Gennemsnit", "num"), ("min", "Laveste", "num"), ("critical", "Maskiner kritiske", "num"), ("machines", "Maskiner målt", "num")),
                Rows = history,
            },
            new ReportSection
            {
                Key = "topAlerts", Title = $"Hyppigste alarmer (seneste {days} dage)",
                Columns = Cols(("title", "Alarm", "text"), ("severity", "Alvor", "sev"), ("count", "Gange", "num"), ("machines", "Maskiner", "num"), ("open", "Stadig åbne", "num"), ("last", "Senest", "date")),
                Rows = top,
            },
            new ReportSection
            {
                Key = "disks", Title = "Diske (SMART og slid)",
                Columns = Cols(("machine", "Maskine", "text"), ("disk", "Disk", "text"), ("media", "Type", "text"), ("size", "Størrelse (GB)", "num"), ("health", "Helbred", "text"),
                    ("wear", "Slid", "pct"), ("temp", "Temperatur (°C)", "num"), ("hours", "Driftstimer", "num"), ("readErrors", "Læsefejl", "num")),
                Rows = disks,
            },
            new ReportSection
            {
                Key = "temps", Title = "Temperaturer",
                Columns = Cols(("machine", "Maskine", "text"), ("cpu", "CPU nu (°C)", "num"), ("gpu", "GPU nu (°C)", "num"), ("cpuMax", "CPU maks 7 dage", "num"), ("gpuMax", "GPU maks 7 dage", "num")),
                Rows = temps,
                Note = "Målinger gemmes i 8 dage, så maksimum dækker højst den seneste uge.",
            });
    }

    // ------------------------------------------------------------------------------------------------ Jobs
    private static async Task<ReportResult> Jobs(AppDb db, ReportFilter f, int days, DateTime now)
    {
        var ms = await Scope(db, f);
        var ids = ms.Select(m => m.Id).ToList();
        var names = ms.ToDictionary(m => m.Id, m => m.Label);
        var from = now.AddDays(-days);
        var jobs = await db.Jobs.AsNoTracking().Where(j => ids.Contains(j.MachineId) && j.Created >= from).OrderByDescending(j => j.Created)
            .Select(j => new { j.MachineId, j.Type, j.Title, j.Status, j.Source, j.CreatedBy, j.Created, j.Started, j.Finished }).ToListAsync();
        object?[] Rate<T>(string label, IEnumerable<T> g, Func<T, string> status)
        {
            var l = g.Select(status).ToList();
            int ok = l.Count(s => s == JobStatus.Success), bad = l.Count(s => s == JobStatus.Failed);
            int stopped = l.Count(s => s is JobStatus.Cancelled or JobStatus.Expired), active = l.Count(s => JobStatus.Active.Contains(s));
            return [label, l.Count, ok, bad, stopped, active, ok + bad > 0 ? Math.Round(100.0 * ok / (ok + bad), 1) : null];
        }
        var rateCols = new Func<string, List<ReportColumn>>(first => Cols((first, first == "type" ? "Type" : "Maskine", "text"), ("total", "I alt", "num"), ("success", "Lykkedes", "num"),
            ("failed", "Fejlede", "num"), ("stopped", "Annulleret/udløbet", "num"), ("active", "Venter/kører", "num"), ("rate", "Succesrate", "pct")));
        var byType = jobs.GroupBy(j => j.Type).Select(g => Rate(g.Key, g, j => j.Status)).OrderByDescending(r => (int)r[1]!).ToList();
        var byMachine = jobs.GroupBy(j => j.MachineId).Select(g => Rate(names[g.Key], g, j => j.Status)).OrderByDescending(r => (int)r[1]!).ToList();
        var list = jobs.Select(j => new object?[]
        {
            j.Created, names[j.MachineId], j.Type, j.Title, j.Status, j.Source, j.CreatedBy,
            j.Started is { } s && j.Finished is { } e ? Math.Round((e - s).TotalMinutes, 1) : null,
        }).ToList();
        int okAll = jobs.Count(j => j.Status == JobStatus.Success), badAll = jobs.Count(j => j.Status == JobStatus.Failed);
        return New("jobs", now,
            [new("Jobs", jobs.Count.ToString()), new("Lykkedes", okAll + badAll > 0 ? $"{100.0 * okAll / (okAll + badAll):0} %" : "–"),
             new("Fejlede", badAll.ToString(), badAll > 0 ? "red" : null), new("Maskiner", jobs.Select(j => j.MachineId).Distinct().Count().ToString())],
            new ReportSection { Key = "types", Title = "Pr. type", Columns = rateCols("type"), Rows = byType },
            new ReportSection { Key = "machines", Title = "Pr. maskine", Columns = rateCols("machine"), Rows = byMachine },
            new ReportSection
            {
                Key = "jobs", Title = $"Jobs (seneste {days} dage)",
                Columns = Cols(("created", "Oprettet", "date"), ("machine", "Maskine", "text"), ("type", "Type", "text"), ("title", "Titel", "text"), ("status", "Status", "text"),
                    ("source", "Kilde", "text"), ("by", "Af", "text"), ("minutes", "Varighed (min)", "num")),
                Rows = list,
            });
    }

    // ------------------------------------------------------------------------------------------------ iPads
    public static string IpadLabel(Ipad p) => p.Owner is { Length: > 0 } o && !o.Equals(p.Name, StringComparison.OrdinalIgnoreCase) ? $"{o} ({p.Name})" : p.Name;

    public static (int Year, int Week) WeekOf(DateTime utc)
    {
        var d = ReportFormat.Local(utc);
        return (ISOWeek.GetYear(d), ISOWeek.GetWeekOfYear(d));
    }

    /// <summary>Minutter pr. iPad (børn), uge og tjeneste. "*" er al aktivitet (summen er ikke tjenesterne lagt sammen – de overlapper).</summary>
    public static async Task<List<(Ipad Ipad, int Year, int Week, string Service, int Minutes)>> IpadUsage(AppDb db, DateTime from)
    {
        var ipads = await db.Ipads.AsNoTracking().Where(p => p.Kind == IpadKinds.Child).ToListAsync();
        var pids = ipads.Select(p => p.Id).ToList();
        var slots = await db.IpadSlots.AsNoTracking().Where(s => pids.Contains(s.IpadId) && s.Slot >= from && !s.Service.StartsWith(IpadPolicy.AppPrefix)).Select(s => new { s.IpadId, s.Service, s.Slot }).ToListAsync();
        var byId = ipads.ToDictionary(p => p.Id);
        return slots.GroupBy(s => (s.IpadId, W: WeekOf(s.Slot), s.Service))
            .Select(g => (byId[g.Key.IpadId], g.Key.W.Year, g.Key.W.Week, g.Key.Service, g.Count() * 5)).ToList();
    }

    private static async Task<ReportResult> Ipads(AppDb db, int days, DateTime now)
    {
        var usage = await IpadUsage(db, now.AddDays(-days));
        var cmp = StringComparer.Create(ReportFormat.Da, true);
        var weeks = usage.Where(u => u.Service == "*").OrderBy(u => IpadLabel(u.Ipad), cmp).ThenByDescending(u => (u.Year, u.Week))
            .Select(u =>
            {
                var top = usage.Where(x => x.Ipad.Id == u.Ipad.Id && x.Year == u.Year && x.Week == u.Week && x.Service != "*").MaxBy(x => x.Minutes);
                return new object?[] { IpadLabel(u.Ipad), $"{u.Year}-U{u.Week:00}", u.Minutes, (int)Math.Round(u.Minutes / 7.0), top.Service is { } s ? $"{s} ({ReportFormat.Minutes(top.Minutes)})" : null };
            }).ToList();
        var services = usage.Where(u => u.Service != "*").OrderBy(u => IpadLabel(u.Ipad), cmp).ThenByDescending(u => (u.Year, u.Week)).ThenByDescending(u => u.Minutes)
            .Select(u => new object?[] { IpadLabel(u.Ipad), $"{u.Year}-U{u.Week:00}", u.Service, u.Minutes }).ToList();
        var thisWeek = WeekOf(now);
        var nowTotal = usage.Where(u => u.Service == "*" && (u.Year, u.Week) == thisWeek).Sum(u => u.Minutes);
        var topSvc = usage.Where(u => u.Service != "*").GroupBy(u => u.Service).Select(g => (g.Key, M: g.Sum(x => x.Minutes))).OrderByDescending(x => x.M).FirstOrDefault();
        return New("ipads", now,
            [new("Børn", usage.Select(u => u.Ipad.Id).Distinct().Count().ToString()), new("Denne uge", ReportFormat.Minutes(nowTotal)),
             new("Mest brugt", topSvc.Key ?? "–")],
            new ReportSection
            {
                Key = "weeks", Title = "Pr. barn og uge",
                Columns = Cols(("child", "Barn", "text"), ("week", "Uge", "text"), ("minutes", "Minutter i alt", "min"), ("perDay", "Gns. pr. dag (min)", "num"), ("top", "Mest brugt", "text")),
                Rows = weeks,
                Note = "Aktivitet måles i perioder på 5 minutter og gemmes i 35 dage.",
            },
            new ReportSection
            {
                Key = "services", Title = "Pr. tjeneste",
                Columns = Cols(("child", "Barn", "text"), ("week", "Uge", "text"), ("service", "Tjeneste", "text"), ("minutes", "Minutter", "min")),
                Rows = services,
            });
    }
}
