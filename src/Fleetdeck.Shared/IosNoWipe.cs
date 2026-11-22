using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fleetdeck.Shared;

/// <summary>
/// "Overvåg uden datatab": iPad'en slettes, gøres overvåget og får sin backup tilbage – via Mac'en (cfgutil + pymobiledevice3) eller en pc
/// med udvidelsen "iPad via USB" (kun pymobiledevice3). Selve forløbet står i <see cref="NoWipeFlow"/>, så begge veje følger de samme regler.
/// </summary>
public static class NoWipeMethods
{
    /// <summary>
    /// Metode A: slet → overvåg (prepare --supervised / profile supervise) → gendan backuppen uden systemfiler (backup2 restore --no-system, afprøves).
    /// På iPadOS 27+ bruges en almindelig fuld gendannelse (backups har ikke længere overvågningsstatus med). isSupervised tjekkes altid til sidst.
    /// </summary>
    public const string RestoreNoSystem = "restore_no_system";
    /// <summary>Reserven: slet → overvåg, uden at lægge backuppen tilbage. Apps hentes igen, og iCloud-data synkroniseres efter login.</summary>
    public const string SuperviseOnly = "supervise_only";
    public static readonly string[] All = [RestoreNoSystem, SuperviseOnly];
    public static string Norm(string? m) => m == SuperviseOnly ? SuperviseOnly : RestoreNoSystem;
    public static string Title(string? m) => Norm(m) == SuperviseOnly ? "Overvåget uden gendannelse" : "Gendan uden systemfiler (afprøves)";

    /// <summary>Hvad "Overvåget uden gendannelse" koster – vises før start og i jobbets resultat.</summary>
    public const string SuperviseOnlyLoss =
        "Overvåget uden gendannelse: iPad'en slettes og gøres overvåget, men backuppen lægges IKKE tilbage. " +
        "Det kommer igen: apps hentes fra App Store, og det, der ligger i iCloud (fotos med iCloud-fotos, kontakter, kalender, noter, " +
        "beskeder i iCloud, app-data, der synkroniseres med iCloud), kommer tilbage, når der logges ind på Apple-kontoen under Indstillinger. " +
        "Det går tabt på iPad'en: alt, der kun lå lokalt – fotos og videoer uden iCloud-fotos, spil-fremskridt og app-data uden iCloud, " +
        "indstillinger, wifi-koder og hjemmeskærmens opstilling. Backuppen bliver liggende i Fleetdeck, så intet er tabt for altid.";
}

/// <summary>Et backup-sæt i Fleetdeck, som kan lægges tilbage. Store: "mac" (sendt fra Mac'en) eller "usb" (fra en pc med "iPad via USB").</summary>
public sealed class NoWipeCandidate
{
    public const string Mac = "mac", Usb = "usb";
    public string Store { get; set; } = Mac;
    public string Set { get; set; } = "";
    public DateTime Created { get; set; }
    public long Bytes { get; set; }
    public int Parts { get; set; }
    /// <summary>null = ukendt (ældre backup, hvor det ikke blev tjekket).</summary>
    public bool? Encrypted { get; set; }
    /// <summary>true = husstandens backup-kode er prøvet mod backuppens nøglering og passer.</summary>
    public bool? Verified { get; set; }
    /// <summary>true = iPad'en var tom eller stod i opsætningen, da backuppen blev taget (sådan en backup bruges aldrig af sig selv).</summary>
    public bool? SourceEmpty { get; set; }
    public string? SourceUdid { get; set; }
    public string? DeviceName { get; set; }
    public bool Pinned { get; set; }

    public string Key => $"{Store}:{Set}";
    public string Describe() =>
        $"{Set} ({Created.ToLocalTime():dd-MM-yyyy HH:mm}, {NoWipeSafety.Gb(Bytes)}, " +
        $"{(Encrypted == true ? "krypteret" : Encrypted == false ? "IKKE krypteret" : "kryptering ukendt")}" +
        $"{(Verified == true ? ", koden passer" : "")})";
}

/// <summary>iPad'ens tilstand lige nu, læst med pymobiledevice3 (lockdown-domænerne com.apple.purplebuddy og com.apple.disk_usage og app-listen).</summary>
public sealed record NoWipeDeviceState(bool? SetupDone, long? CapacityBytes, long? AvailableBytes, int? UserApps, long? AppStaticBytes, string? ActivationState = null)
{
    public long? UsedBytes => CapacityBytes is long c && AvailableBytes is long a && c >= a ? c - a : null;
    /// <summary>Det, en backup kan forventes at fylde: brugt plads minus selve app-programmerne (de er ikke med i en backup – de hentes igen).</summary>
    public long? ComparableBytes => UsedBytes is long u ? Math.Max(0, u - (AppStaticBytes ?? 0)) : null;
}

public sealed record NoWipeChoice(NoWipeCandidate? Chosen, string? Error, IReadOnlyList<string> Notes);

/// <summary>Sikkerhedsreglerne: hvornår der må tages en ny backup, og hvilken backup der lægges tilbage.</summary>
public static class NoWipeSafety
{
    /// <summary>Under så meget brugt plads (uden apps) regnes iPad'en for næsten tom.</summary>
    public const long NearlyEmptyBytes = 2L << 30;
    /// <summary>En backup under denne størrelse er af en tom iPad og vælges aldrig af sig selv.</summary>
    public const long MinBackupBytes = 256L << 20;
    /// <summary>Backuppen skal fylde mindst denne andel af det, iPad'en har brugt (uden apps), for at blive valgt af sig selv.</summary>
    public const double MinShareOfUsed = 0.5;

    public static string Gb(long bytes) => bytes >= 1L << 30
        ? (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.GetCultureInfo("da-DK")) + " GB"
        : $"{bytes / (1L << 20)} MB";

    /// <summary>null = der må tages backup. Ellers hvorfor ikke (på dansk). En backup af en tom iPad må aldrig erstatte en god.</summary>
    public static string? RefuseBackup(NoWipeDeviceState s)
    {
        if (s.ActivationState is "Unactivated")
            return "iPad'en er ikke aktiveret (den er slettet og står i opsætningen), så der tages ikke en ny backup – den ville være tom.";
        if (s.SetupDone == false)
            return "iPad'en står i opsætningsassistenten (\"Hej\"-skærmen), så der tages ikke en ny backup – den ville være tom og kunne skubbe den gode backup ud.";
        if (s.UserApps == 0)
            return "iPad'en har ingen apps installeret og ligner en tom iPad, så der tages ikke en ny backup.";
        if (s.ComparableBytes is long b && b < NearlyEmptyBytes)
            return $"iPad'en er næsten tom ({Gb(b)} brugt ud over apps), så der tages ikke en ny backup.";
        return null;
    }

    public static bool LooksEmpty(NoWipeDeviceState s) => RefuseBackup(s) is not null;

    /// <summary>Hvorfor et sæt ikke vælges af sig selv (null = det kan vælges). <paramref name="comparable"/> = iPad'ens brugte plads uden apps.</summary>
    public static string? Ineligible(NoWipeCandidate c, long? comparable)
    {
        if (c.Parts < 1) return "backuppen er ikke færdig";
        if (c.Encrypted != true) return c.Encrypted == false ? "ikke krypteret" : "ukendt om den er krypteret";
        if (c.Verified != true) return "backup-koden er ikke tjekket mod den";
        if (c.SourceEmpty == true) return "taget af en tom iPad";
        if (c.Bytes < MinBackupBytes) return $"for lille ({Gb(c.Bytes)}) – ligner en tom iPad";
        if (comparable is long u && c.Bytes < u * MinShareOfUsed) return $"for lille ({Gb(c.Bytes)}) i forhold til iPad'ens {Gb(u)}";
        return null;
    }

    /// <summary>
    /// Vælg backuppen: det sæt, administratoren har valgt (skal findes og være færdigt), ellers det nyeste krypterede, tjekkede sæt, der
    /// fylder mindst halvdelen af det, iPad'en har brugt (uden apps). Kan iPad'ens forbrug ikke læses, skal sættet vælges udtrykkeligt.
    /// </summary>
    public static NoWipeChoice Select(IEnumerable<NoWipeCandidate> candidates, long? comparable, string? store, string? set)
    {
        var all = candidates.Where(c => c.Set.Length > 0).ToList();
        var notes = new List<string>();
        if (set is { Length: > 0 })
        {
            var hit = all.FirstOrDefault(c => c.Set == set && (store is not { Length: > 0 } || c.Store == store));
            if (hit is null) return new(null, $"Den valgte backup ({set}) findes ikke i Fleetdeck.", notes);
            if (hit.Parts < 1) return new(null, $"Den valgte backup ({set}) er ikke færdig.", notes);
            if (Ineligible(hit, comparable) is { } why) notes.Add($"Bemærk: den valgte backup ville ikke være valgt automatisk ({why}) – den bruges, fordi den er valgt udtrykkeligt.");
            return new(hit, null, notes);
        }
        if (comparable is null)
            return new(null, "iPad'ens brugte plads kunne ikke læses, så Fleetdeck kan ikke afgøre, hvilken backup der er god. Vælg backuppen udtrykkeligt.", notes);
        var best = all.Where(c => Ineligible(c, comparable) is null).OrderByDescending(c => c.Created).ThenByDescending(c => c.Set, StringComparer.Ordinal).FirstOrDefault();
        foreach (var c in all.OrderByDescending(c => c.Created).TakeWhile(c => c != best))
            notes.Add($"Springer {c.Set} over: {Ineligible(c, comparable)}.");
        if (best is null)
            return new(null, all.Count == 0 ? "Der er ingen backup af iPad'en i Fleetdeck."
                : "Ingen backup i Fleetdeck opfylder reglerne (krypteret, koden tjekket, ikke af en tom iPad og mindst halvt så stor som iPad'ens data). " +
                  "Vælg en backup udtrykkeligt, hvis du er sikker på den.", notes);
        return new(best, null, notes);
    }
}

/// <summary>Kommandoer og fortolkning til "Overvåg uden datatab" (pymobiledevice3 11.58: cli/backup.py restore, cli/lockdown.py get, cli/apps.py list).</summary>
public static class NoWipeCommands
{
    /// <summary>
    /// Gendan backuppen (pymobiledevice3 11.58 cli/backup.py: Typer-flag --system/--no-system → RestoreSystemFiles, --settings → RestorePreserveSettings,
    /// --remove/--no-remove, --reboot/--no-reboot, --password/-p, --source &lt;udid&gt;; BACKUP_DIRECTORY er forældremappen med &lt;udid&gt;/).
    /// <paramref name="system"/> = false: metode A, uden systemfiler (iPadOS 26 og ældre – afprøves). true: almindelig fuld gendannelse
    /// (iPadOS 27+, hvor backups ikke længere har overvågningsstatus med). Koden kommer fra stdin (<see cref="Pmd3.Secret1"/>).
    /// </summary>
    public static string[] Restore(string dir, string sourceUdid, bool system) =>
        ["backup2", "restore", system ? "--system" : "--no-system", "--settings", "--no-remove", "--reboot", "--password", Pmd3.Secret1, "--source", sourceUdid, dir];

    /// <summary>Første iPadOS-version, hvor en backup ikke længere bærer overvågningsstatus (Apple) – så kan den gendannes helt efter prepare.</summary>
    public const int FullRestoreFromMajor = 27;

    /// <summary>true = brug den almindelige fulde gendannelse (--system); false = metode A (--no-system). Ukendt version → metode A.</summary>
    public static bool FullRestore(string? firmware) =>
        firmware is { Length: > 0 } f && int.TryParse(f.Split('.')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var major) && major >= FullRestoreFromMajor;

    /// <summary>Er opsætningsassistenten færdig (SetupDone)?</summary>
    public static string[] SetupState() => ["lockdown", "get", "com.apple.purplebuddy"];
    /// <summary>Plads på iPad'en (TotalDataCapacity / TotalDataAvailable).</summary>
    public static string[] DiskUsage() => ["lockdown", "get", "com.apple.disk_usage"];
    /// <summary>Brugerens apps med størrelse (StaticDiskUsage = selve appen, som ikke er med i en backup).</summary>
    public static string[] UserApps() => ["apps", "list", "--type", "User", "--calculate-sizes"];

    public static bool? SetupDone(JsonElement d) =>
        d.ValueKind == JsonValueKind.Object && d.TryGetProperty("SetupDone", out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    public static (long? Capacity, long? Available) DiskUsage(JsonElement d)
    {
        long? L(params string[] keys)
        {
            if (d.ValueKind != JsonValueKind.Object) return null;
            foreach (var k in keys)
                if (d.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) && n >= 0) return n;
            return null;
        }
        return (L("TotalDataCapacity", "TotalDiskCapacity"), L("TotalDataAvailable", "AmountDataAvailable"));
    }

    public static (int Count, long StaticBytes) UserApps(JsonElement d)
    {
        if (d.ValueKind != JsonValueKind.Object) return (0, 0);
        var n = 0; long st = 0;
        foreach (var app in d.EnumerateObject())
        {
            n++;
            if (app.Value.ValueKind == JsonValueKind.Object && app.Value.TryGetProperty("StaticDiskUsage", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var b))
                st += Math.Max(0, b);
        }
        return (n, st);
    }

    /// <summary>Læs iPad'ens tilstand (fejler en del, står den som ukendt).</summary>
    public static async Task<NoWipeDeviceState> Probe(string udid, string? activation, CancellationToken ct)
    {
        bool? setup = null; long? cap = null, avail = null; int? apps = null; long? st = null;
        var s = await Pmd3Runner.Run(udid, SetupState(), TimeSpan.FromSeconds(60), ct);
        if (s.Ok && s.Json is { } sj) setup = SetupDone(sj);
        var du = await Pmd3Runner.Run(udid, DiskUsage(), TimeSpan.FromSeconds(60), ct);
        if (du.Ok && du.Json is { } dj) (cap, avail) = DiskUsage(dj);
        var a = await Pmd3Runner.Run(udid, UserApps(), TimeSpan.FromMinutes(3), ct);
        if (a.Ok && a.Json is { ValueKind: JsonValueKind.Object } aj) { var (c, b) = UserApps(aj); apps = c; st = b; }
        return new NoWipeDeviceState(setup, cap, avail, apps, st, activation);
    }

    public static string Describe(NoWipeDeviceState s) =>
        $"iPad'en: {(s.UsedBytes is long u ? NoWipeSafety.Gb(u) + " brugt" : "brugt plads ukendt")}" +
        $"{(s.ComparableBytes is long cb && s.AppStaticBytes is > 0 ? $" ({NoWipeSafety.Gb(cb)} uden apps)" : "")}" +
        $", {(s.UserApps is int n ? $"{n} apps" : "antal apps ukendt")}" +
        $", opsætning {(s.SetupDone switch { true => "færdig", false => "IKKE færdig", _ => "ukendt" })}.";
}

/// <summary>Mac'ens egen backup (MobileSync/Backup/&lt;UDID&gt;) gemmes som en kopi, før cfgutil tager en ny – så en tom backup aldrig overskriver en god.</summary>
public static class MobileSyncSnapshot
{
    public const string Marker = ".fleetdeck-";
    public static string Name(string udid, DateTime at) => $"{udid}{Marker}{at:yyyyMMdd-HHmmss}";
    public static bool IsSnapshot(string name) => name.Contains(Marker, StringComparison.Ordinal);
}

/// <summary>Husstandens overvågningsidentitet som PEM til pymobiledevice3 (--keybag): RSA-nøglen (PKCS#1) og certifikatet i én fil.</summary>
public static class IosKeybagPem
{
    public static string FromDer(byte[] certDer, byte[] keyDer)
    {
        using var rsa = RSA.Create(); rsa.ImportRSAPrivateKey(keyDer, out _);
        return rsa.ExportRSAPrivateKeyPem() + "\n" + PemEncoding.WriteString("CERTIFICATE", certDer) + "\n";
    }
}

/// <summary>Tjek af en udpakket backup-mappe (Manifest.plist): er den krypteret, og passer husstandens kode til nøgleringen?</summary>
public static class IosBackupManifest
{
    public sealed record Info(bool? Encrypted, bool? Verified, string? Udid);

    public static Info Inspect(string backupDir, string? password)
    {
        var mf = Path.Combine(backupDir, "Manifest.plist");
        if (!File.Exists(mf)) return new(null, null, null);
        Dictionary<string, object?>? d;
        try { d = ApplePlist.ReadDict(File.ReadAllBytes(mf)); }
        catch (Exception e) when (e is InvalidDataException or FormatException or System.Xml.XmlException or OverflowException) { return new(null, null, null); }
        if (d is null) return new(null, null, null);
        var enc = d.TryGetValue("IsEncrypted", out var e1) && e1 is bool b ? b : (bool?)null;
        bool? ok = null;
        if (enc == true && password is { Length: > 0 } && d.TryGetValue("BackupKeyBag", out var kb) && kb is byte[] bag) ok = IosBackupKeybag.VerifyPassword(bag, password);
        string? udid = d.TryGetValue("Lockdown", out var l) && l is Dictionary<string, object?> ld && ld.TryGetValue("UniqueDeviceID", out var u) ? u as string : null;
        return new(enc, ok, udid);
    }

    /// <summary>Mappen med Manifest.plist i en udpakket backup (navnet er UDID'en på den iPad, den er taget af). null = ingen.</summary>
    public static string? FindBackupDir(string root)
    {
        if (!Directory.Exists(root)) return null;
        if (File.Exists(Path.Combine(root, "Manifest.plist"))) return root;
        return Directory.GetDirectories(root).Where(d => !Path.GetFileName(d).StartsWith('.')).FirstOrDefault(d => File.Exists(Path.Combine(d, "Manifest.plist")));
    }
}

/// <summary>Trinene i "Overvåg uden datatab". Agenten skriver "Trin n/N: …" i trinteksten, så websiden kan vise dem.</summary>
public static class NoWipePlan
{
    public const string Check = "check", Backup = "backup", Fetch = "fetch", Erase = "erase", Wait = "wait", Supervise = "supervise",
        Restore = "restore", Profile = "profile", Verify = "verify";

    public sealed record Step(string Id, string Title, int From, int To);

    public static IReadOnlyList<Step> Steps(string? method, bool freshBackup)
    {
        var restore = NoWipeMethods.Norm(method) == NoWipeMethods.RestoreNoSystem;
        var ids = new List<string> { Check };
        if (freshBackup) ids.Add(Backup);
        if (restore) ids.Add(Fetch);
        ids.AddRange([Erase, Wait, Supervise]);
        if (restore) ids.Add(Restore);
        ids.AddRange([Profile, Verify]);
        var weight = new Dictionary<string, int> { [Check] = 3, [Backup] = 25, [Fetch] = 12, [Erase] = 5, [Wait] = 7, [Supervise] = 6, [Restore] = 30, [Profile] = 5, [Verify] = 5 };
        var total = ids.Sum(i => weight[i]);
        var list = new List<Step>(); var at = 0; var sum = 0;
        for (var n = 0; n < ids.Count; n++)
        {
            sum += weight[ids[n]];
            var to = n == ids.Count - 1 ? 100 : (int)Math.Round(100.0 * sum / total);
            list.Add(new Step(ids[n], $"Trin {n + 1}/{ids.Count}: {Title(ids[n])}", at, to));
            at = to;
        }
        return list;
    }

    public static string Title(string id) => id switch
    {
        Check => "Tjekker iPad'en og backupperne",
        Backup => "Tager en ny krypteret backup",
        Fetch => "Henter og tjekker backuppen",
        Erase => "Sletter iPad'en",
        Wait => "Venter på, at iPad'en starter igen",
        Supervise => "Gør iPad'en overvåget",
        Restore => "Lægger backuppen tilbage",
        Profile => "Installerer den låste profil",
        _ => "Tjekker resultatet",
    };
}

/// <summary>Hvad forløbet skal bruge fra platformen (Mac: cfgutil + pymobiledevice3, pc: pymobiledevice3). Alle trin melder selv deres fejl med Line.</summary>
public sealed class NoWipeOps
{
    public required Action<string> Line { get; init; }
    public required Action<int, int, string> Phase { get; init; }
    public required Func<CancellationToken, Task<IosDeviceInfo?>> Read { get; init; }
    public required Func<IosDeviceInfo, CancellationToken, Task<NoWipeDeviceState>> Probe { get; init; }
    /// <summary>Ny krypteret backup, sendt til Fleetdeck. null = fejl.</summary>
    public required Func<IosDeviceInfo, int, int, CancellationToken, Task<NoWipeCandidate?>> FreshBackup { get; init; }
    /// <summary>Fortæl Fleetdeck, hvilket sæt der bruges (det fastholdes og ryddes aldrig op).</summary>
    public required Func<NoWipeCandidate, CancellationToken, Task> Chosen { get; init; }
    /// <summary>Hent sættets dele og pak dem ud i mappen. false = fejl.</summary>
    public required Func<NoWipeCandidate, string, int, int, CancellationToken, Task<bool>> Fetch { get; init; }
    public required Func<int, int, CancellationToken, Task<bool>> Erase { get; init; }
    public required Func<int, int, string, CancellationToken, Task<IosDeviceInfo?>> WaitBack { get; init; }
    /// <summary>Overvåg den slettede iPad. skipSetup = spring opsætningen over (kun uden gendannelse).</summary>
    public required Func<bool, int, int, CancellationToken, Task<bool>> Supervise { get; init; }
    /// <summary>Sørg for, at værten er parret med iPad'en (efter sletning/overvågning), før backuppen lægges tilbage.</summary>
    public required Func<CancellationToken, Task<bool>> EnsurePaired { get; init; }
    /// <summary>pymobiledevice3 backup2 restore fra mappen (forælder) med kilde-UDID'en; bool = med systemfiler (<see cref="NoWipeCommands.Restore"/>).</summary>
    public required Func<string, string, bool, int, int, CancellationToken, Task<bool>> Restore { get; init; }
    /// <summary>Parring med husstandens overvågningscertifikat (uden "Stol på"). null = ikke prøvet.</summary>
    public required Func<CancellationToken, Task<bool?>> PairSupervised { get; init; }
    public required Func<IosDeviceInfo, int, int, CancellationToken, Task<bool>> InstallProfile { get; init; }
}

public sealed record NoWipeRequest(string Method, bool FreshBackup, IReadOnlyList<NoWipeCandidate> Candidates, string? Store, string? Set, string WorkDir,
    string? Password, string Organization);

public sealed record NoWipeResult(bool Ok, IosDeviceInfo? Device, bool Erased, bool Restored, bool? Supervised, bool OfferSuperviseOnly);

/// <summary>Selve forløbet – det samme på Mac og pc. Intet slettes, før backuppen er valgt efter reglerne, hentet og tjekket.</summary>
public static class NoWipeFlow
{
    public static async Task<NoWipeResult> Run(NoWipeRequest req, NoWipeOps o, CancellationToken ct)
    {
        var method = NoWipeMethods.Norm(req.Method);
        var restoreMode = method == NoWipeMethods.RestoreNoSystem;
        var full = false;
        var steps = NoWipePlan.Steps(method, req.FreshBackup).ToDictionary(s => s.Id);
        void Phase(string id, string? extra = null) => o.Phase(steps[id].From, steps[id].To, extra is null ? steps[id].Title : $"{steps[id].Title} – {extra}");
        (int, int) R(string id) => (steps[id].From, steps[id].To);
        o.Line($"Overvåg uden datatab – {NoWipeMethods.Title(method)}. Organisation: {req.Organization}.");

        // 1. iPad'en og backupperne
        Phase(NoWipePlan.Check);
        var dev = await o.Read(ct);
        if (dev is null) return Fail(null, false, false, null, "Intet er slettet.");
        if (dev.Paired == false && !await o.EnsurePaired(ct)) return Fail(dev, false, false, null, "Intet er slettet.");
        full = NoWipeCommands.FullRestore(dev.Firmware);
        var state = await o.Probe(dev, ct);
        o.Line(NoWipeCommands.Describe(state));
        var candidates = req.Candidates.ToList();
        if (req.FreshBackup)
        {
            if (NoWipeSafety.RefuseBackup(state) is { } why)
                o.Line($"Ny backup springes over: {why} Den seneste gode backup i Fleetdeck bruges i stedet.");
            else
            {
                var (f, t) = R(NoWipePlan.Backup);
                Phase(NoWipePlan.Backup, "lås iPad'en op og lad den være tilsluttet");
                var fresh = await o.FreshBackup(dev, f, t, ct);
                if (fresh is null) return Fail(dev, false, false, null, "Den nye backup mislykkedes, så iPad'en slettes ikke. Intet er slettet.");
                fresh.SourceEmpty = false;
                o.Line($"Ny backup gemt i Fleetdeck: {fresh.Describe()}.");
                candidates.Insert(0, fresh);
            }
        }
        var choice = NoWipeSafety.Select(candidates, state.ComparableBytes, req.Store, req.Set);
        foreach (var n in choice.Notes) o.Line(n);
        if (choice.Chosen is not { } chosen) return Fail(dev, false, false, null, choice.Error + " Intet er slettet.");
        o.Line($"Backuppen, der bruges: {chosen.Describe()}. Den fastholdes i Fleetdeck og ryddes ikke op.");
        await o.Chosen(chosen, ct);

        // 2. hent og tjek backuppen FØR sletningen (kun når den skal lægges tilbage)
        string? root = null, source = null;
        if (restoreMode)
        {
            var (f, t) = R(NoWipePlan.Fetch);
            Phase(NoWipePlan.Fetch);
            root = Path.Combine(req.WorkDir, "backup");
            if (!await o.Fetch(chosen, root, f, t, ct)) return Fail(dev, false, false, null, "Backuppen kunne ikke hentes fra Fleetdeck, så iPad'en slettes ikke. Intet er slettet.");
            var dir = IosBackupManifest.FindBackupDir(root);
            if (dir is null) return Fail(dev, false, false, null, "Backuppen indeholder ingen Manifest.plist (den er ufuldstændig), så iPad'en slettes ikke. Intet er slettet.");
            source = Path.GetFileName(dir);
            if (dir == root) return Fail(dev, false, false, null, "Backuppen ligger ikke i en mappe med iPad'ens UDID, så den kan ikke gendannes. Intet er slettet.");
            var info = IosBackupManifest.Inspect(dir, req.Password);
            if (info.Encrypted == true && info.Verified == false)
                return Fail(dev, false, false, null, "Husstandens backup-kode låser ikke backuppen op, så den kan ikke lægges tilbage. Intet er slettet.");
            if (info.Encrypted == false) o.Line("Advarsel: backuppen er ikke krypteret – adgangskoder, sundhedsdata og wifi-koder kommer ikke med.");
            o.Line(info.Verified == true ? "Backuppen er hentet og tjekket: den er krypteret, og husstandens kode passer." : "Backuppen er hentet.");
        }

        // 3. slet og vent
        var (ef, et) = R(NoWipePlan.Erase);
        Phase(NoWipePlan.Erase, "rør den ikke, og tag ikke kablet ud");
        if (!await o.Erase(ef, et, ct)) return Fail(dev, false, false, null, "Intet er slettet.");
        var (wf, wt) = R(NoWipePlan.Wait);
        if (await o.WaitBack(wf, wt, $"{steps[NoWipePlan.Wait].Title} – lad den stå på \"Hej\"-skærmen", ct) is null)
            return Fail(dev, true, false, null, $"iPad'en er slettet, men kom ikke tilbage i tide. Backuppen {chosen.Set} ligger urørt i Fleetdeck – start igen, når iPad'en er tilsluttet.");

        // 4. overvåg
        var (sf, st) = R(NoWipePlan.Supervise);
        Phase(NoWipePlan.Supervise, restoreMode ? "lad iPad'en stå på opsætningsskærmen" : "rør ikke iPad'en");
        var supervisedNow = await o.Supervise(!restoreMode, sf, st, ct);
        if (!supervisedNow && !restoreMode)
            return Fail(dev, true, false, false, $"iPad'en er slettet, men blev ikke overvåget. Backuppen {chosen.Set} ligger urørt i Fleetdeck. Prøv igen.");
        if (!supervisedNow) o.Line("Overvågningen lykkedes ikke – backuppen lægges tilbage alligevel, så iPad'en får sine data igen.");

        // 5. gendan uden systemfiler
        var restored = false;
        IosDeviceInfo final = dev;
        if (restoreMode)
        {
            var (rf, rt) = R(NoWipePlan.Restore);
            Phase(NoWipePlan.Restore, "rør ikke iPad'en; den genstarter, når den er færdig");
            o.Line(full ? $"iPadOS {dev.Firmware}: backups har ikke overvågningsstatus med, så der bruges en almindelig fuld gendannelse."
                : $"iPadOS {dev.Firmware ?? "(ukendt version)"}: backuppen lægges tilbage uden systemfiler (--no-system), så overvågningen forhåbentlig bliver (afprøves).");
            if (!await o.EnsurePaired(ct))
                return Fail(dev, true, false, supervisedNow, $"Værten kunne ikke parre med iPad'en efter sletningen, så backuppen blev ikke lagt tilbage. Backuppen {chosen.Set} ligger urørt i Fleetdeck – " +
                                                             "brug \"Gendan\" via USB eller Mac'en, eller gendan i Finder med husstandens backup-kode.");
            if (!await o.Restore(root!, source!, full, rf, rt, ct))
                return Fail(dev, true, false, supervisedNow, $"Gendannelsen mislykkedes. iPad'en er slettet{(supervisedNow ? " og overvåget" : "")}, men tom. Backuppen {chosen.Set} ligger urørt " +
                                                             "i Fleetdeck – prøv igen, brug \"Gendan\" via USB eller Mac'en, eller gendan i Finder med husstandens backup-kode.");
            restored = true;
            o.Line(full ? "Backuppen er lagt tilbage." : "Backuppen er lagt tilbage (uden systemfiler).");
            final = await o.WaitBack(rt, rt, "Venter på, at iPad'en genstarter efter gendannelsen – rør den ikke", ct) ?? dev;
        }
        else final = await o.Read(ct) ?? dev;

        // 6. tjek: overvåget? parring med husstandens certifikat? låst profil
        var (vf, vt) = R(NoWipePlan.Verify);
        o.Phase(vf, vt, steps[NoWipePlan.Verify].Title);
        final = await o.Read(ct) ?? final;
        bool? pair = null, profile = null;
        if (final.Supervised == true)
        {
            pair = await o.PairSupervised(ct);
            var (pf, pt) = R(NoWipePlan.Profile);
            Phase(NoWipePlan.Profile);
            profile = await o.InstallProfile(final, pf, pt, ct);
            final = await o.Read(ct) ?? final;
        }
        o.Phase(vf, vt, steps[NoWipePlan.Verify].Title);
        o.Line("");
        o.Line("Resultat:");
        o.Line($"• Backup: {chosen.Describe()}");
        o.Line($"• Gendannet: {(restoreMode ? restored ? full ? "ja, fuld gendannelse (iPadOS 27+)" : "ja, uden systemfiler" : "nej" : "nej (overvåget uden gendannelse)")}");
        o.Line($"• Overvåget: {(final.Supervised switch { true => "ja", false => "NEJ", _ => "ukendt" })}");
        o.Line($"• Parring med husstandens overvågningscertifikat: {(pair switch { true => "virker", false => "virker ikke", _ => "ikke prøvet" })}");
        o.Line($"• Låst profil: {(profile switch { true => "installeret", false => "ikke installeret – kan installeres bagefter", _ => "ikke prøvet" })}");

        if (final.Supervised != true)
        {
            if (restoreMode)
            {
                o.Line("");
                o.Line("iPad'en har sine data igen, men overvågningen fulgte ikke med gendannelsen. Vil du have den overvåget, så vælg \"Overvåget uden gendannelse\".");
                o.Line(NoWipeMethods.SuperviseOnlyLoss);
            }
            return new NoWipeResult(false, final, true, restored, final.Supervised, restoreMode);
        }
        o.Line(restoreMode
            ? "Færdig: iPad'en er overvåget og har sine data igen. Log ind på Apple-kontoen igen (Indstillinger → [navn]) og slå Find min iPad til."
            : "Færdig: iPad'en er overvåget. Log ind på Apple-kontoen under Indstillinger – apps hentes igen, og iCloud-data synkroniseres. Vælg IKKE \"gendan fra iCloud\" (det fjerner overvågningen).");
        return new NoWipeResult(true, final, true, restored, true, false);

        NoWipeResult Fail(IosDeviceInfo? d, bool erased, bool rest, bool? sup, string text)
        {
            o.Line(text);
            return new NoWipeResult(false, d, erased, rest, sup, false);
        }
    }

    /// <summary>Hent et sæts dele (rå dele af én tar.gz) og pak dem ud. Fælles for Mac og pc: kun hentningen er forskellig.</summary>
    public static async Task<bool> FetchParts(NoWipeCandidate c, int jobId, string dest, Func<string, string, CancellationToken, Task> download,
        Action<int, int, string> phase, Action<string> line, int from, int to, CancellationToken ct)
    {
        var down = dest + ".parts";
        try
        {
            if (Directory.Exists(dest)) Directory.Delete(dest, true);
            if (Directory.Exists(down)) Directory.Delete(down, true);
            Directory.CreateDirectory(down);
            var files = new List<string>();
            for (var i = 1; i <= c.Parts; i++)
            {
                phase(from + (to - from) * 2 * (i - 1) / (3 * c.Parts), to, $"Henter backuppen fra Fleetdeck ({i} af {c.Parts})");
                var f = Path.Combine(down, $"part{i:000}");
                await download(PartUrl(jobId, c, i), f, ct);
                files.Add(f);
            }
            phase(from + (to - from) * 2 / 3, to, "Pakker backuppen ud");
            await Task.Run(() => UsbIpadJobs.Unpack(files, dest), ct);
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            line($"Backuppen kunne ikke hentes: {e.Message}");
            return false;
        }
        finally { try { if (Directory.Exists(down)) Directory.Delete(down, true); } catch (IOException) { } }
    }

    public static string PartUrl(int jobId, NoWipeCandidate c, int part) =>
        $"/api/agent/nowipe/{jobId}/backup?store={Uri.EscapeDataString(c.Store)}&set={Uri.EscapeDataString(c.Set)}&part={part}";

    /// <summary>Teksten til hemmeligheder i logning: sikrer, at en kode aldrig står i output.</summary>
    public static string Mask(string text, string? password) => password is { Length: >= 4 } p ? text.Replace(p, "***", StringComparison.Ordinal) : text;

}
