using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Shared;

namespace Fleetdeck.Server.Services.Bios;

public static class BiosSnapshotReasons
{
    public const string Manual = "manual", PreUpdate = "pre-update", PostBoot = "post-boot", PreRestore = "pre-restore", PostRestore = "post-restore";
    /// <summary>De grunde, en person selv kan vælge, når indstillingerne læses.</summary>
    public static readonly string[] ByUser = [Manual, PreUpdate];
}

/// <summary>
/// BIOS-adgangskoder til ét bios_settings_apply-job. Kun i serverens hukommelse (aldrig i databasen, jobbet eller loggen), højst 30 minutter,
/// og kun én gang: agenten bytter jobbets engangsbillet til koden, mens jobbet kører.
/// </summary>
public sealed class BiosSecrets(TimeProvider? clock = null)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<int, (string Ticket, string Password, DateTimeOffset Expires)> items = new();

    /// <summary>Gem koden til jobbet og giv billetten, der står i jobbet.</summary>
    public string Put(int jobId, string password)
    {
        Purge();
        var ticket = Crypto.Token(24);
        items[jobId] = (ticket, password, clock.GetUtcNow() + Lifetime);
        return ticket;
    }

    /// <summary>Koden, hvis billetten passer og ikke er udløbet. Den fjernes med det samme (kan kun hentes én gang).</summary>
    public string? Take(int jobId, string? ticket)
    {
        Purge();
        if (ticket is not { Length: > 0 } || !items.TryGetValue(jobId, out var e) || !Crypto.FixedEquals(e.Ticket, ticket)) return null;
        return items.TryRemove(jobId, out var taken) ? taken.Password : null;
    }

    public void Forget(int jobId) => items.TryRemove(jobId, out _);
    public bool Has(int jobId) => items.ContainsKey(jobId);

    private void Purge()
    {
        var now = clock.GetUtcNow();
        foreach (var kv in items.Where(kv => kv.Value.Expires < now).ToList()) items.TryRemove(kv.Key, out _);
    }
}

/// <summary>Snapshots af BIOS-indstillinger, sammenligning og gendannelse (bios_settings_read/apply).</summary>
public static class BiosSettingsService
{
    /// <summary>Så mange snapshots gemmes pr. maskine (de ældste slettes; det seneste før en opdatering og en gendannelse beholdes altid).</summary>
    public const int Keep = 40;
    /// <summary>"Nu"-snapshottet må højst være så gammelt, når der gendannes (agenten tjekker desuden hver værdi lige før skrivningen).</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromHours(24);
    public const string Source = "bios";

    public static bool Active(Job j) => j.Type is JobTypes.BiosSettingsRead or JobTypes.BiosSettingsApply && JobStatus.Active.Contains(j.Status);

    private static Task<bool> Busy(AppDb db, int machineId) =>
        db.Jobs.AnyAsync(j => j.MachineId == machineId && (j.Type == JobTypes.BiosSettingsRead || j.Type == JobTypes.BiosSettingsApply) && JobStatus.Active.Contains(j.Status));

    public static List<BiosSettingItem> Items(BiosSettingsSnapshot s) => Json.Des<List<BiosSettingItem>>(s.ItemsJson);

    /// <summary>Gem et snapshot. Regelklassen sættes på hver indstilling; passwordfelter tages aldrig med.</summary>
    public static async Task<BiosSettingsSnapshot> Store(AppDb db, Machine m, BiosSettingsReport rep, string reason, string by, int? jobId, DateTime now)
    {
        var facts = Json.Des<SystemFacts>(m.FactsJson);
        var rules = BiosSettingsRules.Default;
        var items = rep.Items
            .Where(i => i.Name.Length > 0 && i.Type != "password" && !BiosValues.NotSettings.Contains(i.Name))
            .Select(i => new BiosSettingItem
            {
                Name = i.Name, Display = i.Display, Type = i.Type, Value = BiosValues.Clean(i.Value), Possible = i.Possible, Min = i.Min, Max = i.Max,
                Rule = rules.Classify(rep.Driver, i.Name, i.Display).Class,
            }).ToList();
        var json = Json.Ser(items);
        var sb = items.FirstOrDefault(i => BiosSettingsRules.Key(i.Name) == "secureboot")?.Value;
        var s = new BiosSettingsSnapshot
        {
            MachineId = m.Id, TakenAt = now, Reason = reason, Driver = rep.Driver, BiosVersion = facts.BiosVersion,
            SecureBoot = sb is null ? facts.SecureBoot : sb.StartsWith("Enable", StringComparison.OrdinalIgnoreCase) || sb == "On",
            ItemsJson = json, Sha256 = Crypto.Sha256(json), By = by.Length > 100 ? by[..100] : by,
            Model = Clip(facts.ProductVersion is { Length: > 0 } pv ? pv : facts.Model, 64), Count = items.Count,
            AdminPassword = rep.AdminPassword, PendingReboot = rep.PendingReboot, JobId = jobId,
        };
        db.BiosSnapshots.Add(s);
        await db.SaveChangesAsync();
        await Prune(db, m.Id);
        return s;
    }

    private static string? Clip(string? s, int n) => s is null ? null : s.Length > n ? s[..n] : s;

    private static async Task Prune(AppDb db, int machineId)
    {
        var all = await db.BiosSnapshots.Where(x => x.MachineId == machineId).OrderByDescending(x => x.TakenAt).ThenByDescending(x => x.Id)
            .Select(x => new { x.Id, x.Reason }).ToListAsync();
        if (all.Count <= Keep) return;
        var keep = all.Take(Keep).Select(x => x.Id).ToHashSet();
        foreach (var r in new[] { BiosSnapshotReasons.PreUpdate, BiosSnapshotReasons.PreRestore })
            if (all.FirstOrDefault(x => x.Reason == r) is { } newest) keep.Add(newest.Id);
        var drop = all.Where(x => !keep.Contains(x.Id)).Select(x => x.Id).ToList();
        await db.BiosSnapshots.Where(x => drop.Contains(x.Id)).ExecuteDeleteAsync();
    }

    /// <summary>Kan maskinen overhovedet læse BIOS-indstillinger? Null = ja; ellers en forklaring.</summary>
    public static string? Unsupported(Machine m)
    {
        var f = Json.Des<SystemFacts>(m.FactsJson);
        if (f.IsVirtual == true) return "En virtuel maskine har ingen BIOS-indstillinger, Fleetdeck kan læse.";
        return f.BiosSettings is null ? BiosSettingsJob.NotSupported : null;
    }

    /// <summary>Læs alle indstillinger → snapshot. Kun læsning.</summary>
    public static async Task<(Job? Job, string? Error)> RequestRead(AppDb db, JobService jobs, Machine m, string reason, string by, string source = "manual")
    {
        if (Unsupported(m) is { } why) return (null, why);
        if (await Busy(db, m.Id)) return (null, "Der kører allerede et BIOS-job på maskinen.");
        var title = reason switch
        {
            BiosSnapshotReasons.PreUpdate => "Kopi af BIOS-indstillinger før opdatering",
            BiosSnapshotReasons.PostBoot => "Kopi af BIOS-indstillinger efter BIOS-opdatering",
            BiosSnapshotReasons.PostRestore => "Kontrol af gendannede BIOS-indstillinger",
            _ => "Kopi af BIOS-indstillinger",
        };
        return (await jobs.Create(m.Id, JobTypes.BiosSettingsRead, title, new JobParams { BiosReason = reason }, RunAs.System, null, source, by), null);
    }

    public sealed record RestoreRequest(int From, int? To, List<string>? Names, List<string>? Acknowledged, string? Password);

    /// <summary>
    /// Gendan valgte værdier fra snapshottet <c>From</c>. "Nu" er maskinens nyeste snapshot (højst 24 timer gammelt). Kun ændrede, stadig
    /// gyldige værdier; "aldrig" afvises; "kræver eget valg" skal være bekræftet. Én skrivning, én gemning. Adgangskoden bliver i hukommelsen.
    /// </summary>
    public static async Task<(Job? Job, List<string> Errors)> RequestRestore(AppDb db, JobService jobs, BiosSecrets secrets, Machine m, RestoreRequest r, string by, DateTime now)
    {
        if (Unsupported(m) is { } why) return (null, [why]);
        var info = Json.Des<SystemFacts>(m.FactsJson).BiosSettings!;
        if (!BiosSettingsDrivers.Writable(info.Driver)) return (null, [$"Fleetdeck kan endnu kun læse BIOS-indstillinger med {info.Driver}."]);
        if (await Busy(db, m.Id)) return (null, ["Der kører allerede et BIOS-job på maskinen."]);
        if (await BiosUpdateService.UpdateBusy(db, m.Id)) return (null, [BiosUpdateService.UpdateBusyText]);
        var from = await db.BiosSnapshots.AsNoTracking().FirstOrDefaultAsync(s => s.Id == r.From && s.MachineId == m.Id);
        if (from is null) return (null, ["Kopien findes ikke."]);
        var latest = await db.BiosSnapshots.AsNoTracking().Where(s => s.MachineId == m.Id).OrderByDescending(s => s.TakenAt).ThenByDescending(s => s.Id).FirstAsync();
        if (r.To is int to && to != latest.Id) return (null, ["Der er kommet en nyere kopi. Sammenlign igen."]);
        if (latest.Id == from.Id) return (null, ["Vælg en ældre kopi at gendanne fra."]);
        if (now - latest.TakenAt > FreshFor) return (null, ["Den nyeste kopi er for gammel. Læs indstillingerne igen først."]);
        if (latest.PendingReboot == true || info.PendingReboot == true) return (null, ["Der venter allerede BIOS-ændringer. Genstart maskinen først."]);
        if (latest.Driver != from.Driver) return (null, ["Kopierne er læst med forskellige drivere og kan ikke gendannes på hinanden."]);
        var diff = BiosRestorePlanner.Diff(Items(from), Items(latest), latest.Driver);
        var (changes, errors) = BiosRestorePlanner.Plan(diff, r.Names ?? [], r.Acknowledged);
        if (errors.Count > 0) return (null, errors);
        var admin = latest.AdminPassword || info.AdminPassword;
        if (admin && string.IsNullOrEmpty(r.Password)) return (null, ["BIOS'en har en administratoradgangskode. Skriv den for at gendanne (den huskes ikke)."]);
        var title = $"Gendan {changes.Count} BIOS-indstilling{(changes.Count == 1 ? "" : "er")}";
        var prm = new JobParams { BiosSettings = changes };
        var job = await jobs.Create(m.Id, JobTypes.BiosSettingsApply, title, prm, RunAs.System, null, "manual", by, save: false);
        db.Audit.Add(AuditService.Entry(by, "bios.settings.restore",
            $"{title} fra kopien {from.TakenAt:yyyy-MM-dd HH:mm} ({from.BiosVersion}): {string.Join(", ", changes.Select(c => c.Name))}", m.Id));
        await db.SaveChangesAsync();   // job-id'et skal kendes, før billetten laves
        if (admin)
        {
            prm.Ticket = secrets.Put(job.Id, r.Password!);
            job.ParamsJson = Json.Ser(prm);
        }
        await jobs.Commit(m.Id);
        return (job, []);
    }

    /// <summary>Gem det, agenten sendte. Engangsbilletten og koden fjernes, når jobbet er slut.</summary>
    public static async Task OnResult(AppDb db, BiosSecrets secrets, Machine m, Job j, JobResult r, DateTime now)
    {
        if (j.Type is not (JobTypes.BiosSettingsRead or JobTypes.BiosSettingsApply) || JobStatus.Active.Contains(j.Status)) return;
        var prm = Json.Des<JobParams>(j.ParamsJson);
        if (prm.Ticket is not null) { prm.Ticket = null; j.ParamsJson = Json.Ser(prm); }
        secrets.Forget(j.Id);
        if (j.Type == JobTypes.BiosSettingsRead && j.Status == JobStatus.Success && r.BiosSettings is { } rep)
            await Store(db, m, rep, prm.BiosReason is { Length: > 0 and <= 16 } why ? why : BiosSnapshotReasons.Manual, j.CreatedBy, j.Id, now);
        if (j.Type == JobTypes.BiosSettingsApply && r.BiosSettingsBefore is { } before)
            await Store(db, m, before, BiosSnapshotReasons.PreRestore, j.CreatedBy, j.Id, now);
        // ventende ændringer: vis det med det samme (næste inventar bekræfter det)
        // (også når en afbrudt kørsel ikke kunne trække klargjorte værdier tilbage: så skal der genstartes før næste skrivning)
        if (j.Type == JobTypes.BiosSettingsApply && r.BiosApply is { } ap && (ap.Saved || ap.PendingReboot == true)
            && Json.Des<SystemFacts>(m.FactsJson) is { BiosSettings: { } bi } f)
        {
            bi.PendingReboot = ap.PendingReboot ?? true;
            m.FactsJson = Json.Ser(f);
        }
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Efter hvert inventar: ny BIOS-version → automatisk kopi (post-boot). Efter en gendannelse og en genstart → kontrolkopi (post-restore).
    /// Begge er kun læsning, og der oprettes højst ét BIOS-job ad gangen.
    /// </summary>
    public static async Task<Job?> AfterInventory(AppDb db, JobService jobs, Machine m, SystemFacts before, SystemFacts now, DateTime utcNow)
    {
        if (now.BiosSettings is null || now.IsVirtual == true || await Busy(db, m.Id)) return null;
        if (before.BiosVersion is { Length: > 0 } ov && now.BiosVersion is { Length: > 0 } nv && !string.Equals(ov, nv, StringComparison.OrdinalIgnoreCase)
            && await db.BiosSnapshots.AnyAsync(s => s.MachineId == m.Id))
            return (await RequestRead(db, jobs, m, BiosSnapshotReasons.PostBoot, "system", Source)).Job;
        var apply = await db.Jobs.AsNoTracking().Where(j => j.MachineId == m.Id && j.Type == JobTypes.BiosSettingsApply)
            .OrderByDescending(j => j.Id).FirstOrDefaultAsync();
        if (apply is not { Status: JobStatus.Success, Finished: DateTime done } || utcNow - done > TimeSpan.FromDays(14)) return null;
        if (await db.BiosSnapshots.AnyAsync(s => s.MachineId == m.Id && s.Reason == BiosSnapshotReasons.PostRestore && s.TakenAt > done)) return null;
        var bootAt = Json.Des<HealthReport>(m.HealthJson).Live.BootAt;
        var booted = bootAt is DateTime b ? b > done : now.BiosSettings.PendingReboot == false;
        return booted ? (await RequestRead(db, jobs, m, BiosSnapshotReasons.PostRestore, "system", Source)).Job : null;
    }

    public sealed record SnapshotMeta(int Id, DateTime TakenAt, string Reason, string Driver, string? BiosVersion, string? Model, int Count,
        bool AdminPassword, bool? PendingReboot, string By);

    public static SnapshotMeta Meta(BiosSettingsSnapshot s) =>
        new(s.Id, s.TakenAt, s.Reason, s.Driver, s.BiosVersion, s.Model, s.Count, s.AdminPassword, s.PendingReboot, s.By);

    /// <summary>Standardvalget til sammenligningen: den nyeste kopi mod den seneste "før opdatering"/"før gendannelse" (ellers den forrige).</summary>
    public static (BiosSettingsSnapshot? From, BiosSettingsSnapshot? To) DefaultPair(IReadOnlyList<BiosSettingsSnapshot> newestFirst)
    {
        if (newestFirst.Count < 2) return (null, newestFirst.FirstOrDefault());
        var to = newestFirst[0];
        var from = newestFirst.Skip(1).FirstOrDefault(s => s.Reason == BiosSnapshotReasons.PreUpdate && s.BiosVersion != to.BiosVersion)
            ?? newestFirst.Skip(1).FirstOrDefault(s => s.Sha256 != to.Sha256) ?? newestFirst[1];
        return (from, to);
    }
}
