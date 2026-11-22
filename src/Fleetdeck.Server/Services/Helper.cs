using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Shared;

namespace Fleetdeck.Server.Services;

public sealed record FixResult(bool Ok, string Message, List<int> JobIds);

/// <summary>Hjælperen: kører rettelser og værktøjer, og retter automatisk når profilen tillader det.</summary>
public sealed class HelperService(AppDb db, JobService jobs, ComplianceService compliance, WinSetupStore store, DeployService deploy, DebloatService debloat, ILogger<HelperService> log)
{
    public const string Source = "helper";
    private static readonly TimeSpan AutoCooldown = TimeSpan.FromHours(6);
    private const int AutoMaxAttempts = 3;

    /// <summary>
    /// Hvorfor en rettelse ikke kan køre på platformen (null = den kan). Hjælperens scripts er PowerShell og WinSetup-trin er Windows:
    /// på Linux og Mac kun de generelle (opdatér, genstart, prøv igen, wake) og dem, der er skrevet til platformen.
    /// </summary>
    public static string? PlatformBlocker(Fix fix, string platform)
    {
        var mac = fix.Id.EndsWith("_mac", StringComparison.Ordinal);
        var linux = fix.Id.EndsWith("_linux", StringComparison.Ordinal);
        if (mac && platform != Platforms.Mac) return "Denne rettelse findes kun til Mac";
        if (fix.Kind == "debloat" && platform != Platforms.Windows) return "Denne rettelse findes kun til Windows";
        if (fix.Kind == "services" && platform is not (Platforms.Windows or Platforms.Linux)) return "Fleetdeck kan ikke starte tjenester på denne platform";
        if (linux && platform != Platforms.Linux) return "Denne rettelse findes kun til Linux";
        if (platform != Platforms.Windows && fix.Kind is "script" or "winsetup" or "multi" && fix.Id is not ("reboot_pending" or "backup_now") && !mac && !linux)
            return "Denne rettelse findes kun til Windows";
        return null;
    }

    /// <summary>Kør en rettelse på en maskine. "by" er brugeren eller "hjælper (auto)".</summary>
    public async Task<FixResult> Run(int machineId, string fixId, string by, int? alertId = null)
    {
        var fix = HelperCatalog.Get(fixId);
        if (fix is null) return new(false, $"Ukendt rettelse: {fixId}", []);
        var m = await db.Machines.Include(x => x.Profile).FirstOrDefaultAsync(x => x.Id == machineId);
        if (m is null) return new(false, "Maskinen findes ikke", []);
        if (PlatformBlocker(fix, m.Platform) is { } blocked) return new(false, blocked, []);

        var ids = new List<int>();
        switch (fix.Kind)
        {
            case "script":
                if (fix.Id == "pxe_reinstall" && ReinstallBlocker(m) is { } why) return new(false, why, []);
                // Læg en udrulning klar, så netværksbootet installerer pc'en med samme navn, bruger og profil uden spørgsmål
                if (fix.Id == "pxe_reinstall") await deploy.ForReinstall(m, by);
                ids.Add((await jobs.Create(machineId, JobTypes.Script, $"Hjælper: {fix.Title}",
                    new JobParams { Body = fix.Script, TimeoutMin = fix.TimeoutMin, Name = fix.Id }, fix.RunAs, null, Source, by)).Id);
                break;
            case "job":
                // en klar BIOS installeres ved næste opstart: den genstart tages fra BIOS-siden (netstrøm, én ad gangen, sporet)
                if (fix.JobType is JobTypes.Reboot or JobTypes.Shutdown && await Bios.BiosUpdateService.Waiting(db, machineId))
                    return new(false, Bios.BiosUpdateService.WaitingText, []);
                var p = fix.JobType switch
                {
                    JobTypes.Reboot => new JobParams { Delay = 300, OnlyIfPending = false, Message = "Fleetdeck: pc'en genstarter om 5 minutter for at gøre opdateringer færdige. Gem dit arbejde." },
                    JobTypes.Upgrade => new JobParams { Exclude = m.Profile?.UpgradeExclude ?? [] },
                    _ => new JobParams(),
                };
                ids.Add((await jobs.Create(machineId, fix.JobType!, $"Hjælper: {fix.Title}", p, RunAs.System, null, Source, by)).Id);
                break;
            case "winsetup":
                ids.Add((await jobs.WinSetup(machineId, fix.Step!, Source, by)).Id);
                break;
            case "multi":   // forbered geninstallation
                foreach (var step in new[] { "Capture", "CaptureDrivers", "Backup" })
                    ids.Add((await jobs.WinSetup(machineId, step, Source, by, save: false)).Id);
                await jobs.Commit(machineId);
                break;
            case "retry":
                foreach (var item in (await compliance.Evaluate(machineId)).Where(i => i.State == "failed" && i.Intent == Intents.Required))
                {
                    var pkg = await db.Packages.FindAsync(item.PackageId);
                    if (pkg is null) continue;
                    var j = await jobs.ForPackage(machineId, pkg, JobTypes.Install, Source, by, save: false);
                    ids.Add(j.Id);
                }
                if (ids.Count == 0) return new(true, "Der er ingen fejlede programmer at prøve igen.", []);
                await jobs.Commit(machineId);
                ids = await db.Jobs.Where(j => j.MachineId == machineId && j.Source == Source && j.Status == JobStatus.Pending)
                    .OrderByDescending(j => j.Id).Take(ids.Count).Select(j => j.Id).ToListAsync();
                break;
            case "debloat":   // anvend præcis de punkter, der er vendt tilbage (som håndhævelsens "Anvend igen", men med brugeren som afsender)
            {
                var d = await debloat.Get(machineId);
                if (d.Drift.Count == 0) return new(true, "Der er intet, der er vendt tilbage – intet at anvende igen.", []);
                if (await debloat.Busy(machineId)) return new(false, "Et debloat-job kører allerede på maskinen. Prøv igen, når det er færdigt.", []);
                ids.AddRange((await debloat.Apply(m, d.Drift.ToList(), by)).Select(j => j.Id));
                if (ids.Count == 0) return new(false, "Punkterne findes ikke længere i Fleetdecks katalog.", []);
                break;
            }
            case "services":  // start de stoppede tjenester – navnene tages fra helbredsdataene, aldrig fra alarmens tekst
            {
                var units = ServiceStart.Startable(Json.Des<HealthReport>(m.HealthJson).FailedServices, m.Platform);
                if (ServiceStart.Script(units, m.Platform) is not { } script) return new(false, "Der er ingen stoppede tjenester, Fleetdeck kan starte.", []);
                var names = string.Join(", ", units.Select(u => u.Display));
                ids.Add((await jobs.Create(machineId, JobTypes.Script, $"Hjælper: Start {names}",
                    new JobParams { Body = script, TimeoutMin = fix.TimeoutMin, Name = fix.Id }, fix.RunAs, null, Source, by)).Id);
                break;
            }
            case "wake":
                return await WakeOnLan.Send(m.Mac, m.Ip) ? new(true, "Magic packet sendt", []) : new(false, "Maskinens MAC-adresse kendes ikke endnu", []);
            default:
                return new(false, $"Ukendt type: {fix.Kind}", []);
        }

        if (alertId is int aid && await db.Alerts.FindAsync(aid) is { } a)
        {
            a.FixJobId = ids.FirstOrDefault(); a.LastFixAt = DateTime.UtcNow; a.FixAttempts++;
            await db.SaveChangesAsync();
        }
        return new(true, ids.Count == 1 ? "Rettelsen er sat i gang" : $"{ids.Count} jobs er sat i gang", ids);
    }

    /// <summary>Geninstallation må kun ske, når der er en frisk backup.</summary>
    private string? ReinstallBlocker(Machine m)
    {
        var facts = Json.Des<SystemFacts>(m.FactsJson);
        var b = store.Backups().GetValueOrDefault((facts.Hostname ?? m.Name).ToUpperInvariant());
        if (b is null) return "Der er ingen backup af denne pc. Kør \"Forbered geninstallation\" først.";
        if (b.AgeDays is null or > 1) return $"Seneste backup er fra {b.Date}. Kør \"Forbered geninstallation\" igen, så backuppen er under et døgn gammel.";
        return null;
    }

    /// <summary>Kaldes efter hver helbredsvurdering: retter de alarmer, som profilen tillader automatisk.</summary>
    public async Task AutoFix(int machineId)
    {
        var m = await db.Machines.AsNoTracking().Include(x => x.Profile).FirstOrDefaultAsync(x => x.Id == machineId);
        if (m?.Profile is null || m.Profile.AutoFix.Count == 0) return;
        var allowed = m.Profile.AutoFix.ToHashSet();
        var open = await db.Alerts.Where(a => a.MachineId == machineId && a.Resolved == null).ToListAsync();
        foreach (var a in open)
        {
            // vigtige opdateringer rettes aldrig automatisk: ejeren vælger selv, hvornår der opdateres
            if (a.Key.StartsWith("important:", StringComparison.Ordinal)) continue;
            var fixId = HelperCatalog.AdviceFor(a.Key, a.Severity, m.Platform, a.Detail)?.FixId;
            if (fixId is null || !allowed.Contains(fixId) || HelperCatalog.Get(fixId) is not { AutoAllowed: true }) continue;
            if (a.FixAttempts >= AutoMaxAttempts) continue;
            if (a.LastFixAt is { } t && DateTime.UtcNow - t < AutoCooldown) continue;
            if (a.FixJobId is int jid && await db.Jobs.AnyAsync(j => j.Id == jid && JobStatus.Active.Contains(j.Status))) continue;
            var r = await Run(machineId, fixId, "hjælper (automatisk)", a.Id);
            log.LogInformation("Automatisk rettelse {Fix} på {Machine}: {Message}", fixId, m.Label, r.Message);
        }
    }
}

public sealed record Insight(string Title, string Text, List<string> Actions);

/// <summary>Oversætter fejlkoder og output fra jobs til en forklaring på dansk med forslag.</summary>
public static class JobInsights
{
    private static readonly Dictionary<long, (string Title, string Text, string[] Actions)> Codes = new()
    {
        [0x8A150014] = ("Pakken blev ikke fundet", "winget kunne ikke finde pakken med dette id. Id'et kan være stavet forkert, eller pakken er fjernet fra winget.", ["tool:winget_repair"]),
        [0x8A150101] = ("Programmet er i brug", "Installationen kunne ikke færdiggøres, fordi programmet kører. Luk det og prøv igen.", ["retry"]),
        [0x8A150102] = ("En anden installation kører", "Windows er i gang med en anden installation eller opdatering. Prøv igen om lidt.", ["retry"]),
        [0x8A150103] = ("En fil er i brug", "En fil, som installationen skulle erstatte, var låst. Genstart pc'en og prøv igen.", ["retry"]),
        [0x8A150104] = ("Mangler en afhængighed", "Installationen kræver noget andet (fx en runtime), som ikke er installeret.", ["retry"]),
        [0x8A150105] = ("Disken er fuld", "Der er ikke plads nok til installationen.", ["tool:disk_cleanup"]),
        [0x8A150106] = ("For lidt hukommelse", "Pc'en havde ikke hukommelse nok under installationen.", ["retry"]),
        [0x8A150107] = ("Intet netværk", "Installationen kunne ikke hente filer.", ["retry"]),
        [0x8A15010B] = ("Genstart kræves først", "Windows skal genstartes, før dette kan installeres.", ["tool:reboot_pending"]),
        [0x8A15010C] = ("Annulleret", "Installationen blev annulleret, måske af en bruger.", ["retry"]),
        [0x8A15010E] = ("En nyere version er installeret", "Pc'en har allerede en nyere version. Det er normalt ikke et problem.", []),
        [0x8A15010F] = ("Blokeret af politik", "En gruppepolitik eller sikkerhedsindstilling blokerer installationen.", []),
        [0x80070057] = ("Installationsprogrammet afviste parametrene", "Fejlkode 0x80070057 (ugyldig parameter). Det ses fx med DirectX-runtime, som normalt allerede følger med spillene. Prøv som bruger, eller sæt pakken til \"Tilgængelig\" i profilen, så hjælperen ikke bliver ved med at prøve.", ["retry_user"]),
        [0x80070005] = ("Adgang nægtet", "Installationsprogrammet fik ikke adgang (0x80070005). Prøv i den anden kontekst, eller luk programmer der kan låse filerne.", ["retry_user", "retry_system"]),
        [1602] = ("Annulleret", "Installationen blev annulleret.", ["retry"]),
        [1603] = ("Generel installationsfejl", "Installationsprogrammet fejlede (MSI 1603). Typisk er der en ventende genstart, en anden version er installeret, eller der mangler rettigheder.", ["tool:reboot_pending", "retry", "retry_user"]),
        [1618] = ("En anden installation kører", "Windows Installer er optaget af en anden installation. Prøv igen om lidt.", ["retry"]),
        [1638] = ("En anden version er installeret", "Afinstallér den eksisterende version først, eller lad den være.", []),
        [-2] = ("winget mangler", "App Installer (winget) findes ikke eller er ikke registreret på pc'en endnu.", ["tool:winget_repair"]),
        [-3] = ("Kan ikke nå WinSetup-sharen", "Agenten (SYSTEM) kunne ikke åbne sharen. Udfyld Unraid-bruger og adgangskode under Indstillinger.", []),
    };

    public static Insight? Explain(Job j)
    {
        if (j.Status is not (JobStatus.Failed or JobStatus.WaitingUser)) return null;
        var o = j.Output ?? "";
        if (j.Status == JobStatus.WaitingUser)
            return new("Venter på brugeren", "Jobbet skal køre i den indloggede brugers session. Det starter af sig selv, når nogen logger ind på pc'en.", []);
        if (o.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
            return new("Tog for lang tid", "Jobbet blev stoppet, fordi det kørte for længe. Installationsprogrammet ventede måske på et svar.", ["retry", "retry_user"]);
        if (o.Contains("administrator", StringComparison.OrdinalIgnoreCase) && o.Contains("not", StringComparison.OrdinalIgnoreCase)
            || o.Contains("som administrator", StringComparison.OrdinalIgnoreCase))
            return new("Må ikke køre som administrator", "Programmet nægter at blive installeret med administratorrettigheder. Prøv som almindelig bruger.", ["retry_user"]);
        if (j.ExitCode is int c)
        {
            long code = c < -1000 ? unchecked((uint)c) : c;
            if (Codes.TryGetValue(code, out var x)) return new(x.Title, x.Text, [.. x.Actions]);
            if ((code & 0xFFFF0000) == 0x8A150000)
                return new($"winget-fejl 0x{code:X8}", "winget kunne ikke gennemføre handlingen. Se outputtet nedenfor. Det hjælper ofte at reparere winget eller prøve i den anden kontekst.",
                    ["retry", j.RunAs == RunAs.System ? "retry_user" : "retry_system", "tool:winget_repair"]);
        }
        if (j.Type == JobTypes.Install)
            return new("Installationen fejlede", "Se outputtet nedenfor. Prøv igen, eller prøv i den anden kontekst (system eller bruger).",
                ["retry", j.RunAs == RunAs.System ? "retry_user" : "retry_system"]);
        return new("Jobbet fejlede", "Se outputtet nedenfor for detaljer.", ["retry"]);
    }
}
