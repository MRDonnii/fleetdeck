using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Shared;

namespace Fleetdeck.Server.Services;

public sealed record Finding(string Key, string Severity, string Title, string Detail);

/// <summary>Helbredsregler, alarmer og helbredsscore.</summary>
public sealed class HealthService(AppDb db, SettingsService settings, ComplianceService compliance, WinSetupStore store,
    Notifier notifier, Live live, HelperService helper)
{
    public async Task RecordMetric(int machineId, LiveFacts f)
    {
        var last = await db.Metrics.Where(x => x.MachineId == machineId).OrderByDescending(x => x.Ts).Select(x => x.Ts).FirstOrDefaultAsync();
        if (last > DateTime.UtcNow.AddSeconds(-280)) return;
        var c = f.Volumes.FirstOrDefault(v => v.Drive.StartsWith('C') || v.Drive == "/");
        db.Metrics.Add(new Metric { MachineId = machineId, Cpu = f.Cpu, Ram = f.Ram, Disk = c?.PctUsed, CpuTemp = f.Temps.Cpu, GpuTemp = f.Temps.Gpu });
        await db.SaveChangesAsync();
    }

    public async Task<List<Finding>> Rules(Machine m)
    {
        var h = Json.Des<HealthReport>(m.HealthJson);
        var facts = Json.Des<SystemFacts>(m.FactsJson);
        var o = new List<Finding>();
        // Offline-advarslen er valgfri (standard fra). Uden den regnes en maskine stadig som "ikke til at vurdere" efter et døgn,
        // så de øvrige alarmer ikke løses eller fyrer på gamle data – men der vises og sendes ingen offline-advarsel.
        var offlineDays = await OfflineDays();
        if (m.LastSeen is null || m.LastSeen < DateTime.UtcNow.AddDays(-(offlineDays ?? 1)))
        {
            var ago = m.LastSeen is null ? 0 : (DateTime.UtcNow - m.LastSeen.Value).TotalDays;
            o.Add(new("offline", "warning", "Maskinen har ikke meldt sig",
                m.LastSeen is null ? "Aldrig set" : ago < 2 ? $"Sidst set for {ago * 24:0} timer siden" : $"Sidst set for {ago:0} dage siden"));
            return o;
        }
        if (ReserveDnsStatus.Finding(m.Id, DateTime.UtcNow) is { } rdns) o.Add(rdns);
        if (SpareCardService.Finding(await db.SpareCards.AsNoTracking().FirstOrDefaultAsync(s => s.MachineId == m.Id), DateTime.UtcNow) is { } spare) o.Add(spare);
        if (m.Platform == Platforms.Windows && DebloatService.DriftFinding(await db.MachineDebloats.AsNoTracking().FirstOrDefaultAsync(d => d.MachineId == m.Id), DateTime.UtcNow) is { } drift)
            o.Add(drift);
        var diskLim = await settings.GetNum("alert_disk_pct", 10);
        foreach (var v in h.Live.Volumes)
        {
            var free = 100 - v.PctUsed;
            if (free < diskLim / 2) o.Add(new($"disk:{v.Drive}", "critical", $"Næsten fuld disk {v.Drive}", $"{v.FreeGb:0.#} GB fri ({free:0} %)"));
            else if (free < diskLim) o.Add(new($"disk:{v.Drive}", "warning", $"Lidt plads på {v.Drive}", $"{v.FreeGb:0.#} GB fri ({free:0} %)"));
        }
        var diskTemp = await settings.GetNum("alert_disk_temp", 70);
        o.AddRange(DiskFindings(h.Disks, diskTemp, DateTime.UtcNow));
        o.AddRange(BatteryFindings(h.Batteries, await settings.GetNum("alert_battery_pct", 60)));
        // diske uden helbredsdata (fx smartmontools mangler på Linux): advar med en knap til at installere værktøjet
        // (kun diske, der kan give SMART: et SD-kort/eMMC som på en Raspberry Pi har ingen, så der foreslås intet)
        if (m.Platform == Platforms.Linux && h.Disks.Any(d => DiskSmart.CanHaveSmart(d.Name)) && h.Disks.All(d => d.Health is null && d.Wear is null && d.Temp is null)
            && Json.Des<HealthReport>(m.HealthJson).Unraid is null)
            o.Add(new("diskdata", "info", "Diskhelbred kan ikke læses", $"{string.Join(", ", h.Disks.Select(d => d.Name).Take(3))} – værktøjet til SMART-data mangler"));
        var cpuLim = await settings.GetNum("alert_cpu_temp", 90);
        if (h.Live.Temps.Cpu >= cpuLim) o.Add(new("cputemp", "warning", "Høj CPU-temperatur", $"{h.Live.Temps.Cpu:0} °C"));
        // Mac: temperaturmålingen er slået fra på Mac'en (Funktioner i appen) – knap til at slå den til igen
        if (m.Platform == Platforms.Mac && h.Live.Temps.Source == MacTemps.Off)
            o.Add(new("notemp", "info", "Temperaturmåling er slået fra", "Slået fra på Mac'en – Fleetdeck kan ikke advare ved varme"));
        var otherAv = h.Antivirus.Where(a => a.Enabled && !a.Name.Contains("Defender", StringComparison.OrdinalIgnoreCase)).Select(a => a.Name).ToList();
        if (h.Defender is { } df)
        {
            if (df.Enabled == false || df.Realtime == false)
                o.Add(otherAv.Count > 0
                    ? new("defender", "info", "Andet antivirus er aktivt", string.Join(", ", otherAv))
                    : new("defender", "critical", "Intet aktivt antivirus", $"Microsoft Defender kører ikke ({df.Mode ?? "ukendt"}), og der er ikke andet antivirus"));
            else if (df.SigAgeDays > 3) o.Add(new("defsig", "warning", "Gamle virusdefinitioner", $"{df.SigAgeDays} dage gamle"));
            if (df.ThreatsLast30Days > 0) o.Add(new("threats", "critical", "Trusler fundet", $"{df.ThreatsLast30Days} de sidste 30 dage"));
        }
        var fwOff = h.Firewall.Where(kv => !kv.Value).Select(kv => kv.Key).ToList();
        if (fwOff.Count > 0) o.Add(new("firewall", "warning", "Firewall er slået fra", "Profiler: " + string.Join(", ", fwOff)));
        if (ServiceStart.Finding(h.FailedServices, m.Platform) is { } svc) o.Add(svc);
        // agenter med fejllog (3.40.0+) melder nedbrud mere præcist via SysLogRules nedenfor
        if (h.Events.Crashes7d > 0 && !m.LiveLogCapable) o.Add(new("crash", "warning", "Nedbrud eller blå skærm", $"{h.Events.Crashes7d} de sidste 7 dage (senest {h.Events.LastCrash})"));
        if (h.Updates?.Pending > 0) o.Add(new("winupdate", "info", m.Platform == Platforms.Mac ? "macOS-opdateringer venter" : m.Platform == Platforms.Linux ? "Systemopdateringer venter" : "Windows-opdateringer venter", $"{h.Updates.Pending} opdatering(er)"));
        foreach (var d in h.Drives.Where(d => !d.Ok && d.Error is { Length: > 0 } e && !e.StartsWith("ingen bruger")))
            o.Add(new($"drive:{d.Key}", "warning", "Netværksdrev virker ikke", $"{d.Name} ({d.Target}): {d.Error}"));
        if (h.Live.UpdateError is { Length: > 0 } ue) o.Add(new("agentupdate", "warning", "Agenten kan ikke opdatere sig selv", ue));
        if (h.Autostart is { Ok: false } au) o.Add(new("autostart", "warning", "Fleetdeck starter ikke selv ved opstart", au.Detail));
        o.AddRange(MacAccessFindings(h.MacAccess));
        if (h.Encryption is { } enc)
        {
            var escrowed = await db.RecoveryKeys.AsNoTracking().Where(k => k.MachineId == m.Id && k.Superseded == null).Select(k => new { k.Kind, k.Volume }).ToListAsync();
            o.AddRange(RecoveryKeyVault.Findings(enc, escrowed.Select(k => (k.Kind, k.Volume)).ToList()));
        }
        if (TpmFinding(m.Platform, h.Tpm, h.Encryption, facts) is { } tpm) o.Add(tpm);
        o.AddRange(NetworkFindings(h.Network));
        o.AddRange(await SysLogRules.For(db, settings, m.Id, DateTime.UtcNow));
        foreach (var k in h.Kept ?? [])
        {
            if (k.Skipped) continue;   // bevidst sprunget over (betingelse ikke opfyldt) – ingen fejl
            if (KeptService.NeedsAlert(k)) o.Add(new($"kept:{k.Name}", "critical", $"{k.Name} kører ikke", $"Tilstand: {k.State}. Agenten prøver at starte den hvert minut.{(k.LastError is { Length: > 0 } le ? $" Seneste fejl: {le}" : "")}"));
            else if (k.Restarts24h > 0) o.Add(new($"kept:{k.Name}", "info", $"{k.Name} er startet igen", $"Stoppede og blev startet igen {k.Restarts24h} gang(e) det sidste døgn – senest {k.LastRestart?.ToLocalTime():HH:mm}."));
        }
        if (h.Live.PendingReboot && h.Live.UptimeH > 7 * 24)
            o.Add(new("reboot", "warning", "Genstart mangler", $"Oppe i {h.Live.UptimeH / 24:0} dage med ventende genstart"));
        var failed = (await compliance.Evaluate(m.Id)).Where(i => i.State == "failed").Select(i => i.Name).ToList();
        if (failed.Count > 0) o.Add(new("compliance", "warning", "Programmer kunne ikke installeres", string.Join(", ", failed.Take(6))));
        if (m.ProfileId is int pid && await db.Profiles.Where(p => p.Id == pid).Select(p => p.BackupMode).FirstOrDefaultAsync() is { } bm && bm != "off")
        {
            var b = store.Backups().GetValueOrDefault((facts.Hostname ?? m.Name).ToUpperInvariant());
            var lim = await settings.GetNum("alert_backup_days", 7);
            if (b is null || b.AgeDays > lim) o.Add(new("backup", "warning", "Backup er for gammel", $"Seneste: {b?.Date ?? "ingen"}"));
        }
        if ((await settings.Get("notify_important")) != "0" && await Important(m, h) is { Count: > 0 } imp)
        {
            // nøglen følger indholdet, så nye vigtige opdateringer giver en ny besked
            var key = "important:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', imp.Order()))))[..8];
            o.Add(new(key, "warning", ImportantTitle(imp),
                string.Join(", ", imp.Take(6)) + (imp.Count > 6 ? $" (+{imp.Count - 6} flere)" : "") + ". Sikkerhedsrettelser eller programmer, som angribere ofte går efter."));
        }
        return o;
    }

    /// <summary>
    /// Diskhelbred (konservativt): SMART fejlet eller NVMe, der svigter = kritisk. Advarsel ved Windows' "Warning", andre NVMe-advarsler,
    /// sektorer der venter på at blive flyttet, reallokerede sektorer eller mediefejl, der er steget den sidste uge, slid over 80 % og høj temperatur.
    /// </summary>
    public static IEnumerable<Finding> DiskFindings(IEnumerable<DiskHealth> disks, double tempLimit, DateTime nowUtc)
    {
        foreach (var d in disks)
        {
            var status = d.Status is { Length: > 0 } st ? $" ({st})" : "";
            if (DiskSmart.IsUnhealthy(d.Health))
                yield return new($"smart:{d.Name}", "critical", $"Disk-fejl: {d.Name}", $"Helbred: {d.Health}{status}");
            else if (DiskSmart.NvmeFailing(d.CriticalWarning))
                yield return new($"smart:{d.Name}", "critical", $"Disk-fejl: {d.Name}", $"NVMe-advarsel: {DiskSmart.NvmeWarningText(d.CriticalWarning!.Value)}");
            else if (d.Health?.Equals("Warning", StringComparison.OrdinalIgnoreCase) == true)
                yield return new($"smart:{d.Name}", "warning", $"Disken advarer: {d.Name}", $"Helbred: {d.Health}{status}");
            else if (d.CriticalWarning is > 0 and var cw)
                yield return new($"smart:{d.Name}", "warning", $"Disken advarer: {d.Name}", $"NVMe-advarsel: {DiskSmart.NvmeWarningText(cw)}");
            var sectors = new List<string>();
            if (d.Pending > 0) sectors.Add($"{d.Pending} ulæselige sektorer venter på at blive flyttet");
            if (DiskSmart.RoseWithin(d.ReallocatedRoseAt, nowUtc))
                sectors.Add($"{d.Reallocated} reallokerede sektorer" + (d.ReallocatedFirst is { } first && first < d.Reallocated ? $" (var {first})" : ""));
            if (DiskSmart.RoseWithin(d.MediaErrorsRoseAt, nowUtc)) sectors.Add($"{d.MediaErrors} mediefejl");
            if (sectors.Count > 0)
                yield return new($"sectors:{d.Name}", "warning", $"Disken får dårlige sektorer: {d.Name}", string.Join(", ", sectors));
            if (d.Wear >= 80) yield return new($"wear:{d.Name}", "warning", $"Slidt SSD: {d.Name}", d.Wear >= 100 ? $"Slid {d.Wear:0} % – levetiden er brugt op" : $"Slid {d.Wear:0} %");
            if (d.Temp >= tempLimit) yield return new($"dtemp:{d.Name}", "warning", $"Varm disk: {d.Name}", $"{d.Temp:0} °C");
        }
    }

    /// <summary>Batteriet bør skiftes (under grænsen af den oprindelige kapacitet, eller Mac'en anbefaler service). Kun en advarsel.</summary>
    public static IEnumerable<Finding> BatteryFindings(IEnumerable<BatteryInfo>? batteries, double limitPct) =>
        (batteries ?? []).Select((b, i) => (b, i, problem: BatteryParse.Problem(b, limitPct)))
            .Where(x => x.problem is not null)
            .Select(x => new Finding($"battery:{x.i}", "warning", "Slidt batteri", x.problem!));

    /// <summary>
    /// Windows med BitLocker slået til eller Windows 11: TPM'en mangler, er slået fra eller er ikke klar (eller er kun 1.2 på Windows 11).
    /// Kun en advarsel – maskinen virker, men BitLocker falder tilbage til gendannelsesnøglen, og Windows 11 kræver TPM 2.0.
    /// </summary>
    public static Finding? TpmFinding(string platform, TpmInfo? tpm, EncryptionInfo? enc, SystemFacts facts)
    {
        if (platform != Platforms.Windows || tpm is null || tpm.Error is not null) return null;
        var bitLocker = enc is { Kind: EncryptionKinds.BitLocker } && enc.Volumes.Any(v => v.Encrypted == true);
        var win11 = IsWindows11(facts);
        if (!bitLocker && !win11) return null;
        string? problem = !tpm.Present ? "Ingen TPM fundet – den er måske slået fra i BIOS/UEFI"
            : tpm.Enabled == false || tpm.Activated == false ? "TPM'en er slået fra"
            : tpm.Ready == false ? "TPM'en er ikke klar" + (tpm.Attention is { Count: > 0 } a ? $" ({string.Join(", ", a).ToLowerInvariant()})" : "")
            : win11 && tpm.Version == "1.2" ? "TPM'en er kun version 1.2"
            : null;
        if (problem is null) return null;
        var why = bitLocker && win11 ? "BitLocker er slået til, og Windows 11 kræver TPM 2.0."
            : bitLocker ? "BitLocker er slået til og bruger normalt TPM'en til at låse disken op."
            : "Windows 11 kræver TPM 2.0.";
        return new("tpm", "warning", "TPM mangler eller er ikke klar", $"{problem}. {why}");
    }

    /// <summary>
    /// Netværket: primær kablet adapter under 1 Gbit/s, når kortet kan mere; svagt Wi-Fi på en stationær maskine; mange nye fejl;
    /// ustabilt link. Kun advarsler – reglerne ligger i <see cref="NetHealth"/>, så agent og server deler dem.
    /// </summary>
    public static IEnumerable<Finding> NetworkFindings(NetworkInfo? net) =>
        NetHealth.Findings(net).Select(f => new Finding(f.Key, "warning", f.Title, f.Detail));

    /// <summary>Windows 11 = klient-Windows med build 22000 eller nyere (Windows Server har også høje build-numre).</summary>
    public static bool IsWindows11(SystemFacts f)
    {
        if (f.Os?.Contains("Server", StringComparison.OrdinalIgnoreCase) == true || f.Edition?.StartsWith("Server", StringComparison.OrdinalIgnoreCase) == true) return false;
        return f.Build is { Length: > 0 } b && int.TryParse(b.Split('.')[0], out var n) ? n >= 22000
            : f.Os?.Contains("Windows 11", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// macOS: Fuld diskadgang mangler (iPad-backup fejler med "Operation not permitted"), eller agenten er kun ad hoc-signeret,
    /// så adgangen forsvinder igen ved næste opdatering.
    /// </summary>
    public static IEnumerable<Finding> MacAccessFindings(MacAccessInfo? a)
    {
        if (a is null) yield break;
        if (a.FullDiskAccess == false)
            yield return new("fulldisk", a.NeededForIpadBackup ? "warning" : "info", "Fuld diskadgang mangler",
                "Agenten har ikke Fuld diskadgang på Mac'en, så iPad-backup og andet i brugerens Bibliotek-mappe fejler med \"Operation not permitted\"." +
                (a.StableSignature ? "" : $" Agenten er kun signeret {a.Signature ?? "ad hoc"}, så adgangen forsvinder igen ved næste opdatering."));
        else if (!a.StableSignature)
            yield return new("macsign", "info", "Agenten har ingen fast signatur",
                $"Signatur: {a.Signature ?? "ukendt"}. Fuld diskadgang forsvinder ved hver opdatering af agenten.");
    }

    /// <summary>
    /// Vigtige opdateringer: sikkerheds- og kumulative opdateringer til styresystemet og nye versioner af programmer, der ofte angribes
    /// (browsere, pdf-læsere, fjernadgang, pakkeprogrammer …). Låste programmer ("Opdatér aldrig") tæller ikke med.
    /// </summary>
    public async Task<List<string>> Important(Machine m, HealthReport h)
    {
        var o = new List<string>();
        foreach (var t in h.Updates?.Titles ?? [])
            if (ImportantOs.IsMatch(t) && !NotImportant.IsMatch(t)) o.Add(t.Length > 90 ? t[..90] + "…" : t);
        var locks = await UpgradeLocks.For(db, m.Id);
        foreach (var u in await db.Upgrades.AsNoTracking().Where(x => x.MachineId == m.Id).ToListAsync())
            if (ImportantApp.IsMatch($"{u.Name} {u.WingetId}") && !locks.ContainsKey(u.WingetId) && !locks.ContainsKey(u.Name)) o.Add($"{u.Name} {u.Available}".Trim());
        return o;
    }

    /// <summary>Titlen på "vigtige opdateringer": nævner opdateringen, når der kun er én.</summary>
    public static string ImportantTitle(IReadOnlyList<string> imp) => imp.Count == 1 ? $"Vigtig opdatering venter: {imp[0]}" : $"{imp.Count} vigtige opdateringer venter";

    /// <summary>Er det en opdatering til styresystemet (sikkerhed/kumulativ), og ikke et program? Bruges til at vælge Windows Update frem for winget.</summary>
    public static bool IsImportantOs(string text) => ImportantOs.IsMatch(text);

    private static readonly System.Text.RegularExpressions.Regex ImportantOs = new(@"security|sikkerhed|cumulative|kumulativ|critical|kritisk", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex NotImportant = new(@"intelligence|intelligens|definition|Malicious Software|skadelig software|KB2267602|KB890830|\.NET Framework.*Preview", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex ImportantApp = new(
        @"\b(chrome|firefox|brave|opera|vivaldi|zoom|teams|7-?zip|winrar|vlc|acrobat|adobe reader|java|jre|jdk|notepad\+\+|putty|winscp|filezilla|thunderbird|teamviewer|anydesk|rustdesk|keepass|bitwarden|openssh|git|powershell|office|libreoffice|wireshark)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Dage før offline-advarslen, eller null når advarslen er slået fra (standard).</summary>
    public Task<double?> OfflineDays() => OfflineDaysFor(settings);
    public static async Task<double?> OfflineDaysFor(SettingsService s) =>
        await s.Get("alert_offline_enabled") == "1" ? Math.Max(1, await s.GetNum("alert_offline_days", 1)) : null;

    public async Task Evaluate(int machineId)
    {
        var m = await db.Machines.FirstOrDefaultAsync(x => x.Id == machineId);
        if (m is null) return;
        // Ignorerede alarmer (fx Defender, som er slået fra med vilje) tæller ikke: ingen alarm, besked, helbredsfradrag eller rettelse
        var ignored = await IgnoredAlerts.Keys(m, settings);
        var firing = (await Rules(m)).Where(f => !ignored.Contains(f.Key)).ToList();
        // Offline uden slået-til advarsel: de øvrige alarmer står stadig urørte, men selve offline-advarslen vises ikke
        var offline = firing.Any(f => f.Key == "offline");
        if (offline && await OfflineDays() is null) firing.RemoveAll(f => f.Key == "offline");
        var now = DateTime.UtcNow;
        var open = await db.Alerts.Where(a => a.MachineId == machineId && a.Resolved == null).ToListAsync();
        var changed = false;
        foreach (var f in firing)
        {
            var a = open.FirstOrDefault(x => x.Key == f.Key);
            if (a is not null)
            {
                if (a.Acknowledged is not null && AlertAck.Reopens(a.Key, a.Severity, f.Severity, a.Detail, f.Detail)) { a.Acknowledged = null; a.AckBy = null; changed = true; }
                a.Severity = f.Severity; a.Title = f.Title; a.Detail = f.Detail; a.LastSeen = now; continue;
            }
            db.Alerts.Add(new Alert { MachineId = machineId, Key = f.Key, Severity = f.Severity, Title = f.Title, Detail = f.Detail });
            changed = true;
            if (f.Severity is "critical" or "warning")
                notifier.Send(f.Key.StartsWith("important:", StringComparison.Ordinal) ? $"⬆️ {m.Label}: {f.Title}\n{f.Detail}" : $"{(f.Severity == "critical" ? "🔴" : "🟠")} {m.Label}: {f.Title} – {f.Detail}",
                    f.Key.StartsWith("important:", StringComparison.Ordinal) ? "update" : f.Severity);
        }
        // Offline: de øvrige alarmer kan ikke vurderes – lad dem stå åbne (ellers "løses" de og fyrer igen, når maskinen er tilbage)
        foreach (var a in open.Where(a => (!offline || a.Key == "offline") && firing.All(f => f.Key != a.Key)))
        {
            a.Resolved = now; changed = true;
            if (a.Severity == "critical" && !ignored.Contains(a.Key)) notifier.Send($"🟢 {m.Label}: løst – {a.Title}", "resolved");
        }
        var w = new Dictionary<string, int> { ["critical"] = 30, ["warning"] = 10, ["info"] = 2 };
        // En kvitteret alarm er set: den tæller ikke med i maskinens tilstand, før den bliver værre eller sker igen
        var seen = open.Where(a => a.Acknowledged is not null && a.Resolved is null).Select(a => a.Key).ToHashSet();
        var counted = firing.Where(f => !seen.Contains(f.Key)).ToList();
        m.HealthScore = Math.Max(0, 100 - counted.Sum(f => w[f.Severity]));
        m.HealthState = m.LastHealth is null && m.LastInventory is null ? "unknown"
            : counted.Any(f => f.Severity == "critical") ? "critical" : counted.Any(f => f.Severity == "warning") ? "warning" : "ok";
        if (m.HealthState != "unknown") await Reports.ReportTracking.RecordHealth(db, m.Id, m.HealthScore ?? 0, m.HealthState, now);
        await db.SaveChangesAsync();
        if (changed) await live.Changed("alerts", machineId);
        await helper.AutoFix(machineId);      // hjælperen retter det, profilen tillader automatisk
    }
}

/// <summary>Hvornår en kvitteret alarm skal vises igen: når den bliver alvorligere, eller når et nyt nedbrud er sket.</summary>
public static class AlertAck
{
    private static int Rank(string severity) => severity switch { "critical" => 2, "warning" => 1, _ => 0 };
    private static string Latest(string detail) => detail.IndexOf("senest", StringComparison.Ordinal) is var i and >= 0 ? detail[i..] : detail;

    public static bool Reopens(string key, string oldSeverity, string newSeverity, string oldDetail, string newDetail) =>
        Rank(newSeverity) > Rank(oldSeverity) || ((key == "crash" || key.StartsWith("syslog:", StringComparison.Ordinal) && newDetail.Contains("senest", StringComparison.Ordinal))
            && Latest(oldDetail) != Latest(newDetail));
}

/// <summary>Beskeder til Discord-webhook eller ntfy.</summary>
public sealed class Notifier(IHttpClientFactory http, IServiceScopeFactory scopes, ILogger<Notifier> log)
{
    public void Send(string text, string kind = "alert")
    {
        // første linje bliver titel på mobilen, resten er selve beskeden
        var lines = text.Split('\n', 2);
        HomeAssistantBridge.Notify(lines.Length > 1 ? lines[0] : "Fleetdeck", lines.Length > 1 ? lines[1] : text, kind);
        Webhook(text);
    }

    private void Webhook(string text) => _ = Task.Run(async () =>
    {
        try
        {
            using var scope = scopes.CreateScope();
            var url = await scope.ServiceProvider.GetRequiredService<SettingsService>().Get("webhook_url");
            if (string.IsNullOrWhiteSpace(url)) return;
            var c = http.CreateClient("webhook");
            HttpContent body = url.Contains("discord", StringComparison.OrdinalIgnoreCase)
                ? new StringContent(Json.Ser(new { content = text }), Encoding.UTF8, "application/json")
                : new StringContent(text, Encoding.UTF8, "text/plain");
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = body };
            req.Headers.TryAddWithoutValidation("Title", "Fleetdeck");
            (await c.SendAsync(req)).EnsureSuccessStatusCode();
        }
        catch (Exception e) { log.LogWarning("Webhook fejlede: {Message}", e.Message); }
    });
}

public static class WakeOnLan
{
    public static async Task<bool> Send(string? mac, string? ip)
    {
        var hex = new string((mac ?? "").Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 12) return false;
        var macBytes = Convert.FromHexString(hex);
        var pkt = new byte[6 + 16 * 6];
        for (var i = 0; i < 6; i++) pkt[i] = 0xFF;
        for (var i = 0; i < 16; i++) Buffer.BlockCopy(macBytes, 0, pkt, 6 + i * 6, 6);
        using var u = new UdpClient { EnableBroadcast = true };
        var targets = new List<IPAddress> { IPAddress.Broadcast };
        if (IPAddress.TryParse(ip, out var a) && a.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = a.GetAddressBytes(); b[3] = 255; targets.Add(new IPAddress(b));
        }
        foreach (var t in targets) foreach (var port in new[] { 9, 7 })
            try { await u.SendAsync(pkt, pkt.Length, new IPEndPoint(t, port)); } catch (SocketException) { }
        return true;
    }
}

/// <summary>Alarmer der ignoreres – på én maskine (Machine.IgnoredAlertsJson) eller på alle (indstillingen "ignored_alerts").</summary>
public sealed class IgnoredAlert
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string By { get; set; } = "";
    public DateTime At { get; set; } = DateTime.UtcNow;
}

public static class IgnoredAlerts
{
    public const string AllSetting = "ignored_alerts";

    public static List<IgnoredAlert> OnMachine(Machine m) => Json.Des<List<IgnoredAlert>>(m.IgnoredAlertsJson);
    public static async Task<List<IgnoredAlert>> OnAll(SettingsService s) => Json.Des<List<IgnoredAlert>>(await s.Get(AllSetting));

    public static async Task<HashSet<string>> Keys(Machine m, SettingsService s) =>
        OnMachine(m).Concat(await OnAll(s)).Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
}
