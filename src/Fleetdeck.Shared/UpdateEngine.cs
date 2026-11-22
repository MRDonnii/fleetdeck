using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Fleetdeck.Shared;

/// <summary>
/// Opdateringsmotorens faste rækkefølge i én runde. Serveren styrer runderne og genstarterne (MaintenanceService);
/// agenten kører faserne i denne rækkefølge og stopper Windows Update-delen, når en fase kræver genstart.
/// </summary>
public static class UpdatePhases
{
    public const string Prepare = "prepare", Exclusive = "exclusive", Software = "software", Drivers = "drivers", Apps = "apps", Firmware = "firmware";

    /// <summary>Firmware kommer altid sidst – efter programmerne – og kun når intet andet venter på en genstart.</summary>
    public static readonly string[] Order = [Prepare, Exclusive, Software, Drivers, Apps, Firmware];

    public static string Label(string phase, string? platform = Platforms.Windows) => phase switch
    {
        Prepare => "Forberedelse",
        Exclusive => "Eksklusive opdateringer",
        Software => platform == Platforms.Windows ? "Windows-opdateringer" : platform == Platforms.Mac ? "macOS-opdateringer" : "Systempakker",
        Drivers => "Drivere",
        Apps => "Programmer",
        Firmware => "Firmware",
        _ => phase,
    };

    /// <summary>Faser, der skal stoppe, når en tidligere fase har bedt om genstart (Windows Update vil have en ren tilstand).</summary>
    public static bool NeedsCleanState(string phase) => phase is Exclusive or Software or Drivers or Firmware;

    /// <summary>Efter disse faser afsluttes Windows Update-delen af runden, hvis der kræves genstart (servicing stack → kumulativ osv.).</summary>
    public static bool StopsForReboot(string phase) => phase is Exclusive or Software;
}

public static class UpdateKinds
{
    public const string Software = "software", Driver = "driver", Firmware = "firmware", App = "app";
}

/// <summary>Hvorfor en opdatering ikke blev installeret i denne runde. De blokerende gør kørslen "delvis".</summary>
public static class DeferReasons
{
    public const string Power = "kræver netstrøm (og mindst 50 % batteri)";
    public const string User = "kræver en indlogget bruger";
    public const string Owner = "venter på ejerens godkendelse";
    public const string Reboot = "venter på genstart";
    public const string Locked = "låst eller udeladt i Fleetdeck";
    public const string SelfUpdating = "opdaterer sig selv";
    public const string DriversOff = "drivere er slået fra i profilen";
    public const string FirmwareOff = "firmware er slået fra i profilen";
    public const string FeatureOff = "funktionsopdatering – slået fra i profilen";
    public const string Bios = "BIOS – kun via BIOS-opdateringen, aldrig automatisk her";
    public const string Idle = "venter på at pc'en er ledig (indhentning, mens nogen bruger den)";
    public const string BitLocker = "BitLocker kunne ikke sættes på pause – firmware springes over";

    public static bool Blocking(string reason) => reason is Power or User or Owner or Reboot or Idle or BitLocker;
}

/// <summary>Én ventende opdatering, som agenten fandt (Windows Update, winget, apt …).</summary>
public sealed class UpdateCandidate
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Kind { get; set; } = UpdateKinds.Software;
    /// <summary>Windows: InstallationBehavior.Impact = RequiresExclusiveHandling, eller en servicing stack-opdatering.</summary>
    public bool Exclusive { get; set; }
    /// <summary>Windows: funktionsopdatering (ny Windows-version, kategorien "Upgrades").</summary>
    public bool FeatureUpdate { get; set; }
    /// <summary>Firmware, der er selve BIOS/UEFI – den flashes aldrig af motoren.</summary>
    public bool Bios { get; set; }
    /// <summary>Driveren anbefales ikke (gammel, nedgradering, erstatter Windows' egen) – se DriverAdvice.</summary>
    public string? Advice { get; set; }
    /// <summary>Låst ("Opdatér aldrig"), skjult i Fleetdeck eller undtaget i profilen.</summary>
    public bool Locked { get; set; }
}

public sealed class UpdateItem
{
    public string? Id { get; set; }
    public string Title { get; set; } = "";
    /// <summary>Fasen, den blev installeret i (UpdatePhases) – programmer tæller ikke som fremgang for Windows Update.</summary>
    public string? Phase { get; set; }
    [JsonIgnore] public string Key => string.IsNullOrEmpty(Id) ? Title : Id;
}

public sealed class UpdateFailure
{
    public string? Id { get; set; }
    public string Title { get; set; } = "";
    /// <summary>Fejlkoden som tekst (fx 0x80240022) – eller en kort grund.</summary>
    public string Code { get; set; } = "";
    [JsonIgnore] public string Key => string.IsNullOrEmpty(Id) ? Title : Id;
}

public sealed class UpdateDeferred
{
    public string? Id { get; set; }
    public string Title { get; set; } = "";
    public string Reason { get; set; } = "";
    public bool Blocking { get; set; }
    [JsonIgnore] public string Key => string.IsNullOrEmpty(Id) ? Title : Id;
}

/// <summary>Det strukturerede resultat af én runde (og, lagt sammen, af hele kørslen).</summary>
public sealed class UpdateReport
{
    public int Round { get; set; }
    /// <summary>Den sidste fase, runden nåede (UpdatePhases).</summary>
    public string? Phase { get; set; }
    public List<UpdateItem> Installed { get; set; } = [];
    public List<UpdateFailure> Failed { get; set; } = [];
    public List<UpdateDeferred> Deferred { get; set; } = [];
    /// <summary>Venter til næste runde (fx fordi runden stoppede for at genstarte).</summary>
    public List<UpdateItem> Remaining { get; set; } = [];
    public bool RebootPending { get; set; }
    /// <summary>
    /// Kun programmerne har efterladt filer, der skiftes ved næste opstart (PendingFileRenameOperations): ikke grund til en ny runde,
    /// men til én afsluttende genstart.
    /// </summary>
    public bool RebootSuggested { get; set; }
    /// <summary>Det, søgningen fandt i denne runde (Windows Update-id'er med "wu:"). null = ingen søgning (eller den fejlede).</summary>
    public List<string>? Offered { get; set; }
    /// <summary>Søgningen selv fejlede (fx Windows Update svarede ikke).</summary>
    public string? Error { get; set; }

    public void Add(string phase, string? id, string title) { Phase = phase; Installed.Add(new UpdateItem { Id = id, Title = title, Phase = phase }); }
    public void Fail(string phase, string? id, string title, string code) { Phase = phase; Failed.Add(new UpdateFailure { Id = id, Title = title, Code = code }); }
    public void Defer(string? id, string title, string reason) =>
        Deferred.Add(new UpdateDeferred { Id = id, Title = title, Reason = reason, Blocking = DeferReasons.Blocking(reason) });
    public void Later(string? id, string title) => Remaining.Add(new UpdateItem { Id = id, Title = title });
}

public sealed class UpdateOptions
{
    public bool System { get; set; } = true;
    public bool Apps { get; set; } = true;
    public bool Drivers { get; set; } = true;
    public bool Firmware { get; set; } = true;
    public bool FeatureUpdates { get; set; }
    /// <summary>Indhentning, mens nogen bruger pc'en: drivere og firmware venter (skærm, netværk og enheder må ikke blinke midt i arbejdet).</summary>
    public bool UserActive { get; set; }
    /// <summary>Strøm: null = ingen batteri (stationær), ellers om opladeren sidder i.</summary>
    public bool? OnAc { get; set; }
    public int? BatteryPct { get; set; }
    /// <summary>Maskinen ventede allerede på en genstart, da runden begyndte.</summary>
    public bool RebootPending { get; set; }
}

public sealed record UpdatePhasePlan(string Phase, List<UpdateCandidate> Items)
{
    /// <summary>Eksklusive opdateringer og firmware installeres én ad gangen.</summary>
    [JsonIgnore] public bool OneByOne => Phase is UpdatePhases.Exclusive or UpdatePhases.Firmware;
}

public sealed record RoundPlan(List<UpdatePhasePlan> Phases, List<UpdateDeferred> Deferred, List<UpdateItem> Later);

/// <summary>Ren planlægning: givet de ventende opdateringer og maskinens tilstand, hvilke faser køres i hvilken rækkefølge.</summary>
public static partial class UpdatePlanner
{
    public const int MinBatteryPct = 50;

    /// <summary>Firmware kun på netstrøm: stationær (intet batteri), eller oplader sidder i og batteriet er mindst 50 %.</summary>
    public static bool PowerOk(bool? onAc, int? batteryPct) =>
        onAc switch { null => batteryPct is null, false => false, true => batteryPct is null || batteryPct >= MinBatteryPct };

    /// <summary>Strøm ud fra batterilisten (BatteryCollect): (null, null) = intet batteri.</summary>
    public static (bool? OnAc, int? Pct) Power(IReadOnlyList<BatteryInfo>? batteries)
    {
        if (batteries is not { Count: > 0 }) return (null, null);
        var pct = batteries.Where(b => b.ChargePct is not null).Select(b => (int?)Math.Round(b.ChargePct!.Value)).DefaultIfEmpty(null).Min();
        bool? ac = batteries.Any(b => b.Status == "discharging") ? false
            : batteries.Any(b => b.Status is "ac" or "full" or "charging") ? true : null;
        return (ac, pct);
    }

    [GeneratedRegex(@"\bBIOS\b|System Firmware|\bUEFI\b", RegexOptions.IgnoreCase)] private static partial Regex BiosRx();
    [GeneratedRegex(@"\bfirmware\b", RegexOptions.IgnoreCase)] private static partial Regex FirmwareRx();
    [GeneratedRegex(@"servicing stack|\bSSU\b", RegexOptions.IgnoreCase)] private static partial Regex SsuRx();

    public static bool LooksLikeBios(string title) => BiosRx().IsMatch(title);

    [GeneratedRegex(@"UEFI\\RES_\{?([0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})\}?", RegexOptions.IgnoreCase)] private static partial Regex EsrtRx();

    /// <summary>GUID'en i en firmware-opdaterings hardware-id ("UEFI\RES_{…}") – slås op i ESRT.</summary>
    public static string? EsrtGuid(string? hardwareId) => hardwareId is { Length: > 0 } && EsrtRx().Match(hardwareId) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>ESRT-typen for enhedsfirmware (dock, touchpad, SSD …). 1 = systemfirmware (BIOS/UEFI), 0 = ukendt, 3 = UEFI-driver.</summary>
    public const int EsrtDeviceFirmware = 2;

    /// <summary>
    /// Er en firmware-opdatering fra Windows Update selve BIOS'en? Producenterne kalder den typisk bare "Dell Inc. - Firmware - 1.21.0",
    /// så titlen er ikke nok: kun firmware, som ESRT siger er enhedsfirmware (type 2), regnes for andet end BIOS.
    /// Ukendt type eller manglende hardware-id = BIOS (hellere for forsigtig).
    /// </summary>
    public static bool IsBiosFirmware(string title, string? hardwareId, Func<string, int?> esrtType) =>
        LooksLikeBios(title) || EsrtGuid(hardwareId) is not { } g || esrtType(g) != EsrtDeviceFirmware;

    private static readonly HashSet<string> FwupdSystemPlugins = new(StringComparer.OrdinalIgnoreCase)
        { "uefi_capsule", "uefi_dbx", "uefi_pk", "uefi_kek", "flashrom", "intel_spi", "intel_me", "coreboot", "bios" };

    /// <summary>fwupd: systemfirmware (UEFI-kapsel, flashrom, Intel ME …) er BIOS – uanset hvad enheden hedder.</summary>
    public static bool FwupdIsBios(string? plugin, IEnumerable<string>? flags, string name) =>
        LooksLikeBios(name) || plugin is { Length: > 0 } && FwupdSystemPlugins.Contains(plugin)
        || flags?.Any(f => f.Equals("is-bootloader", StringComparison.OrdinalIgnoreCase)) == true;
    public static bool LooksLikeFirmware(string title) => FirmwareRx().IsMatch(title) || BiosRx().IsMatch(title);
    public static bool LooksLikeServicingStack(string title) => SsuRx().IsMatch(title);

    public static RoundPlan Plan(IEnumerable<UpdateCandidate> items, UpdateOptions o)
    {
        var phases = UpdatePhases.Order.ToDictionary(p => p, _ => new List<UpdateCandidate>());
        var deferred = new List<UpdateDeferred>();
        var later = new List<UpdateItem>();
        void Defer(UpdateCandidate c, string reason) =>
            deferred.Add(new UpdateDeferred { Id = c.Id, Title = c.Title, Reason = reason, Blocking = DeferReasons.Blocking(reason) });
        var power = PowerOk(o.OnAc, o.BatteryPct);
        foreach (var c in items.DistinctBy(c => c.Id.Length > 0 ? c.Id : c.Title))
        {
            if (c.Locked) { Defer(c, DeferReasons.Locked); continue; }
            switch (c.Kind)
            {
                case UpdateKinds.App:
                    if (o.Apps) phases[UpdatePhases.Apps].Add(c);
                    break;
                case UpdateKinds.Firmware:
                    if (!o.System) break;
                    if (c.Bios) Defer(c, DeferReasons.Bios);
                    else if (!o.Firmware) Defer(c, DeferReasons.FirmwareOff);
                    else if (!power) Defer(c, DeferReasons.Power);
                    else if (o.UserActive) Defer(c, DeferReasons.Idle);
                    else if (o.RebootPending) later.Add(new UpdateItem { Id = c.Id, Title = c.Title });
                    else phases[UpdatePhases.Firmware].Add(c);
                    break;
                case UpdateKinds.Driver:
                    if (!o.System) break;
                    if (!o.Drivers) Defer(c, DeferReasons.DriversOff);
                    else if (c.Advice is { Length: > 0 } a) Defer(c, a);
                    else if (o.UserActive) Defer(c, DeferReasons.Idle);
                    else if (o.RebootPending) later.Add(new UpdateItem { Id = c.Id, Title = c.Title });
                    else phases[UpdatePhases.Drivers].Add(c);
                    break;
                default:
                    if (!o.System) break;
                    if (c.FeatureUpdate && !o.FeatureUpdates) Defer(c, DeferReasons.FeatureOff);
                    else if (o.RebootPending) later.Add(new UpdateItem { Id = c.Id, Title = c.Title });
                    else phases[c.Exclusive || LooksLikeServicingStack(c.Title) ? UpdatePhases.Exclusive : UpdatePhases.Software].Add(c);
                    break;
            }
        }
        var list = UpdatePhases.Order.Where(p => phases[p].Count > 0 || (p == UpdatePhases.Apps && o.Apps))
            .Select(p => new UpdatePhasePlan(p, phases[p])).ToList();
        return new(list, deferred, later);
    }
}

public enum UpdateNext { Round, Reboot, Finish, Retry }

/// <param name="FinalReboot">Må den afsluttende genstart ske? Nej, når genstart beviseligt ikke hjælper.</param>
public sealed record RoundDecision(UpdateNext Next, string Why, bool FinalReboot = true);

/// <summary>
/// Runderne: efter hver runde (og hver genstart) søges der igen, indtil intet er tilbage, der ikke sker fremskridt,
/// eller grænsen for runder/genstarter er nået. Aldrig en uendelig genstartsløkke.
/// </summary>
public static class UpdateRounds
{
    public const int DefaultMax = 4, Limit = 8;
    /// <summary>Nye forsøg pr. kørsel, når en runde blev afbrudt eller søgningen fejlede.</summary>
    public const int MaxRetries = 2;

    public static int Clamp(int max) => Math.Clamp(max, 1, Limit);

    /// <summary>Det, der stadig mangler efter en runde: fejlede og udsatte-til-næste-runde (ikke de bevidst udeladte).</summary>
    public static HashSet<string> Left(UpdateReport r) =>
        r.Failed.Select(f => f.Key).Concat(r.Remaining.Select(x => x.Key)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <param name="round">Runden, der lige er afsluttet (1 = første).</param>
    /// <param name="reboots">Genstarter indtil nu i kørslen.</param>
    /// <param name="previousLeft">Det, der manglede efter forrige runde (tomt efter første runde).</param>
    /// <param name="mayReboot">Må motoren genstarte (profilens "Bagefter" er ikke "intet")?</param>
    /// <param name="retries">Nye forsøg brugt indtil nu i kørslen (afbrudte runder, fejlede søgninger).</param>
    /// <param name="rolledBack">Opdateringer, der blev tilbudt igen efter genstarten – de blev rullet tilbage.</param>
    public static RoundDecision Decide(int round, int maxRounds, int reboots, IReadOnlyCollection<string> previousLeft, UpdateReport r, bool mayReboot = true,
        int retries = 0, IReadOnlyCollection<string>? rolledBack = null)
    {
        var max = Clamp(maxRounds);
        // en opdatering, der kom igen efter genstarten, blev rullet tilbage: endnu en genstart gentager bare det samme
        if (rolledBack is { Count: > 0 }) return new(UpdateNext.Finish, "opdatering rullet tilbage efter genstart", FinalReboot: false);
        if (r.RebootPending)
        {
            if (!mayReboot) return new(UpdateNext.Finish, "genstart kræves, men genstart er slået fra");
            // efter første runde: venter Windows stadig på en genstart, uden at runden har installeret noget fra systemet, hjælper en genstart ikke
            if (round > 1 && !SystemProgress(r)) return new(UpdateNext.Finish, "genstart løser ikke det ventende (intet nyt installeret efter genstarten)", FinalReboot: false);
            if (reboots >= max) return new(UpdateNext.Finish, $"højst {max} genstarter pr. kørsel");
            if (round >= max) return new(UpdateNext.Finish, $"højst {max} runder pr. kørsel");
            return new(UpdateNext.Reboot, "genstart kræves");
        }
        // søgningen fejlede (netværk, Windows Update nede eller optaget): prøv igen om lidt – højst MaxRetries gange
        if (r.Error is not null && retries < MaxRetries && round < max) return new(UpdateNext.Retry, "søgningen fejlede – nyt forsøg om lidt");
        var left = Left(r);
        if (left.Count == 0) return new(UpdateNext.Finish, "intet tilbage");
        if (round >= max) return new(UpdateNext.Finish, $"højst {max} runder pr. kørsel");
        if (r.Installed.Count == 0) return new(UpdateNext.Finish, "ingen fremgang");
        if (previousLeft.Count > 0 && left.SetEquals(previousLeft)) return new(UpdateNext.Finish, "ingen fremgang (samme opdateringer mangler)");
        return new(UpdateNext.Round, "nye forsøg efter fremgang");
    }

    /// <summary>Læg en runde oven i hele kørslens resultat: det installerede samles, det fejlede forsvinder, når det lykkes senere.</summary>
    public static UpdateReport Merge(UpdateReport? total, UpdateReport round)
    {
        total ??= new UpdateReport();
        var installed = total.Installed.Concat(round.Installed).DistinctBy(i => i.Key, StringComparer.OrdinalIgnoreCase).ToList();
        var ok = installed.Select(i => i.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // en Windows Update-fejl fra en tidligere runde, som søgningen ikke længere finder, er klaret (installeret eller erstattet)
        var offered = round.Offered?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var roundFailed = round.Failed.Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Gone(UpdateFailure f) => offered is not null && !roundFailed.Contains(f.Key)
            && f.Key.StartsWith(WuPrefix, StringComparison.OrdinalIgnoreCase) && !offered.Contains(f.Key);
        var failed = round.Failed.Concat(total.Failed.Where(f => !Gone(f))).Where(f => !ok.Contains(f.Key)).DistinctBy(f => f.Key, StringComparer.OrdinalIgnoreCase).ToList();
        return new UpdateReport
        {
            Round = round.Round, Phase = round.Phase ?? total.Phase, Installed = installed, Failed = failed,
            Deferred = round.Deferred.Where(d => !ok.Contains(d.Key)).ToList(),
            Remaining = round.Remaining.Where(x => !ok.Contains(x.Key)).ToList(),
            RebootPending = round.RebootPending, RebootSuggested = total.RebootSuggested || round.RebootSuggested, Error = round.Error,
        };
    }

    public const string WuPrefix = "wu:";

    /// <summary>Installerede runden noget andet end programmer (Windows Update, drivere, systempakker, firmware)?</summary>
    public static bool SystemProgress(UpdateReport r) => r.Installed.Any(i => i.Phase is not UpdatePhases.Apps);

    /// <summary>Det, der kun kan genkendes sikkert efter en genstart: system-opdateringer med et rigtigt id (ikke programmer).</summary>
    public static List<UpdateItem> RebootSensitive(UpdateReport r) =>
        r.Installed.Where(i => !string.IsNullOrEmpty(i.Id) && i.Phase is not UpdatePhases.Apps).Select(i => new UpdateItem { Id = i.Id, Title = i.Title, Phase = i.Phase }).ToList();

    /// <summary>
    /// Opdateringer, der blev installeret før genstarten og nu tilbydes igen (installeret, fejlet, venter eller udsat igen – samme id
    /// og samme titel): de blev rullet tilbage under genstarten (typisk 0x800f0922). Titlen skal også passe, fordi fx Defenders
    /// definitioner beholder deres id, men får en ny version i titlen.
    /// </summary>
    public static List<string> RolledBack(IReadOnlyCollection<UpdateItem> installedBeforeReboot, UpdateReport after)
    {
        if (installedBeforeReboot.Count == 0) return [];
        static string K(string? id, string title) => $"{id}\n{title}".ToLowerInvariant();
        var before = installedBeforeReboot.Where(i => !string.IsNullOrEmpty(i.Id)).Select(i => K(i.Id, i.Title)).ToHashSet();
        return after.Installed.Select(i => (i.Id, i.Title)).Concat(after.Failed.Select(f => (f.Id, f.Title)))
            .Concat(after.Remaining.Select(x => (x.Id, x.Title))).Concat(after.Deferred.Select(d => (d.Id, d.Title)))
            .Where(x => !string.IsNullOrEmpty(x.Id) && before.Contains(K(x.Id, x.Title))).Select(x => x.Id!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public const string RolledBackCode = "rullet tilbage efter genstart";

    /// <summary>Flyt de tilbagerullede fra "installeret" til "fejlede" – både i runden og i hele kørslen.</summary>
    public static void MarkRolledBack(UpdateReport report, IReadOnlyCollection<string> ids, IReadOnlyDictionary<string, string>? titles = null)
    {
        if (ids.Count == 0) return;
        var set = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = report.Installed.Concat(report.Remaining).Where(i => i.Id is not null && set.Contains(i.Id)).GroupBy(i => i.Id!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Title, StringComparer.OrdinalIgnoreCase);
        report.Installed.RemoveAll(i => i.Id is not null && set.Contains(i.Id));
        report.Remaining.RemoveAll(i => i.Id is not null && set.Contains(i.Id));
        report.Failed.RemoveAll(f => f.Id is not null && set.Contains(f.Id));
        report.Deferred.RemoveAll(d => d.Id is not null && set.Contains(d.Id));
        foreach (var id in set)
            report.Failed.Add(new UpdateFailure { Id = id, Title = names.GetValueOrDefault(id) ?? titles?.GetValueOrDefault(id) ?? id, Code = RolledBackCode });
    }
}

/// <summary>Ærlig status: kun grøn, når alt lykkedes; "delvis" med en liste, når noget mangler.</summary>
public static class UpdateStatus
{
    /// <summary>Status for én runde (job). En runde, der stoppede for at genstarte, er ikke "delvis" – resten kommer i næste runde.</summary>
    public static string Of(UpdateReport r)
    {
        var blocking = r.Deferred.Any(d => d.Blocking) || (!r.RebootPending && r.Remaining.Count > 0);
        if (r.Error is not null && r.Installed.Count == 0 && r.Failed.Count == 0) return JobStatus.Failed;
        if (r.Failed.Count == 0 && !blocking && r.Error is null) return JobStatus.Success;
        if (r.Failed.Count > 0 && r.Installed.Count == 0 && !blocking) return JobStatus.Failed;
        return JobStatus.Partial;
    }

    /// <summary>Status for hele kørslen, når den er slut. En genstart, der aldrig blev til noget, gør den delvis.</summary>
    public static string Final(UpdateReport total)
    {
        var missing = total.Failed.Count > 0 || total.Remaining.Count > 0 || total.Deferred.Any(d => d.Blocking) || total.RebootPending;
        if (!missing && total.Error is null) return JobStatus.Success;
        if (total.Installed.Count == 0 && total.Failed.Count > 0 && total.Remaining.Count == 0 && !total.Deferred.Any(d => d.Blocking)) return JobStatus.Failed;
        if (total.Error is not null && total.Installed.Count == 0 && total.Failed.Count == 0) return JobStatus.Failed;
        return JobStatus.Partial;
    }

    /// <summary>Kort tekst til rapporter: "12 installeret · 1 fejlede · 2 venter".</summary>
    public static string Text(UpdateReport r)
    {
        var parts = new List<string>();
        parts.Add(r.Installed.Count == 0 ? "intet nyt installeret" : $"{r.Installed.Count} installeret");
        if (r.Failed.Count > 0) parts.Add($"{r.Failed.Count} fejlede ({string.Join(", ", r.Failed.Take(3).Select(f => $"{f.Title}: {f.Code}"))})");
        var waiting = r.Deferred.Where(d => d.Blocking).ToList();
        if (waiting.Count > 0) parts.Add($"{waiting.Count} venter ({string.Join(", ", waiting.Take(3).Select(d => $"{d.Title}: {d.Reason}"))})");
        if (r.Remaining.Count > 0) parts.Add($"{r.Remaining.Count} mangler stadig");
        if (r.RebootPending) parts.Add("mangler genstart");
        if (r.Error is { Length: > 0 } e) parts.Add($"fejl: {e}");
        return string.Join(" · ", parts);
    }

    /// <summary>Det, der mangler, som linjer til morgenrapporten.</summary>
    public static List<string> Missing(UpdateReport r) =>
        r.Failed.Select(f => $"{f.Title} – fejlede ({f.Code})")
            .Concat(r.Deferred.Where(d => d.Blocking).Select(d => $"{d.Title} – {d.Reason}"))
            .Concat(r.Remaining.Select(x => $"{x.Title} – mangler stadig"))
            .Concat(r.RebootPending ? ["genstart mangler"] : [])
            .ToList();
}
