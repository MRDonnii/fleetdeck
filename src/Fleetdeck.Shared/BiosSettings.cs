using System.Text.RegularExpressions;

namespace Fleetdeck.Shared;

// ------------------------------------------------------------------ kontrakter
/// <summary>Driverne, BIOS-indstillinger kan læses med. Linux: firmware-attributes i sysfs; Windows: leverandørens WMI.</summary>
public static class BiosSettingsDrivers
{
    public const string ThinkLmi = "thinklmi", DellSysman = "dell-wmi-sysman", HpBioscfg = "hp-bioscfg", LenovoWmi = "lenovo-wmi";

    /// <summary>Linux-driverne i den rækkefølge, de prøves (mappenavnet under /sys/class/firmware-attributes).</summary>
    public static readonly string[] Sysfs = [ThinkLmi, DellSysman, HpBioscfg];

    /// <summary>Kan Fleetdeck skrive med denne driver i denne version? Dell og HP læses kun, indtil de er afprøvet.</summary>
    public static bool Writable(string? driver) => driver is ThinkLmi or LenovoWmi;
}

/// <summary>Én BIOS-indstilling. Passwords er aldrig med.</summary>
public sealed class BiosSettingItem
{
    public string Name { get; set; } = "";
    public string? Display { get; set; }
    /// <summary>enumeration, integer, string, ordered-list (som driveren melder den).</summary>
    public string Type { get; set; } = "enumeration";
    public string Value { get; set; } = "";
    public List<string>? Possible { get; set; }
    public long? Min { get; set; }
    public long? Max { get; set; }
    /// <summary>Regelklassen (common, ask, never), sat af serveren, når snapshottet gemmes.</summary>
    public string? Rule { get; set; }
    /// <summary>Indstillingen findes, men værdien kunne ikke læses (think-lmi giver EINVAL for en tom værdi). Value er så "".</summary>
    public bool? Unreadable { get; set; }
}

/// <summary>Kort om BIOS-indstillingerne til maskinens fakta (billigt at læse ved hvert inventar).</summary>
public sealed class BiosSettingsInfo
{
    public string Driver { get; set; } = "";
    public int Count { get; set; }
    public bool AdminPassword { get; set; }
    /// <summary>think-lmi: single eller bulk (null når driveren ikke har save_settings).</summary>
    public string? SaveMode { get; set; }
    /// <summary>Der venter BIOS-ændringer, som først gælder efter en genstart (null = driveren kan ikke sige det).</summary>
    public bool? PendingReboot { get; set; }
    public bool Writable { get; set; }
}

/// <summary>Alle indstillinger, som agenten læste dem.</summary>
public sealed class BiosSettingsReport
{
    public string Driver { get; set; } = "";
    public bool AdminPassword { get; set; }
    public string? SaveMode { get; set; }
    public bool? PendingReboot { get; set; }
    public bool Writable { get; set; }
    public List<BiosSettingItem> Items { get; set; } = [];
}

/// <summary>Én værdi, der skal skrives: kun hvis den nuværende værdi stadig er <see cref="ExpectOld"/>.</summary>
public sealed class BiosSettingChange
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public string ExpectOld { get; set; } = "";
}

public static class BiosApplyStatus
{
    public const string Written = "written", Rejected = "rejected", Denied = "denied", Changed = "changed", Invalid = "invalid",
        Missing = "missing", NotWritten = "not_written";
}

public sealed class BiosApplyLine
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public string Status { get; set; } = BiosApplyStatus.NotWritten;
    public string? Detail { get; set; }
}

/// <summary>Resultatet af bios_settings_apply.</summary>
public sealed class BiosApplyResult
{
    public bool Ok { get; set; }
    /// <summary>Gemt i BIOS'en (én gemning). False betyder, at intet er gemt.</summary>
    public bool Saved { get; set; }
    public bool? PendingReboot { get; set; }
    /// <summary>Administratoradgangskoden kunne ikke ryddes i driveren (ligger i kernens hukommelse til næste genstart).</summary>
    public bool PasswordNotCleared { get; set; }
    public string Message { get; set; } = "";
    public List<BiosApplyLine> Lines { get; set; } = [];
}

public sealed class BiosSecretRequest
{
    public int JobId { get; set; }
    public string? Ticket { get; set; }
}

public sealed class BiosSecretResponse
{
    public string Value { get; set; } = "";
}

// ------------------------------------------------------------------ værdier
/// <summary>Fælles regler for værdier: rensning, gyldighed og bootlister.</summary>
public static class BiosValues
{
    /// <summary>Navne under attributes/, der ikke er indstillinger.</summary>
    public static readonly HashSet<string> NotSettings = new(StringComparer.OrdinalIgnoreCase)
        { "save_settings", "pending_reboot", "debug_cmd", "reset_bios", "bios_admin_password", "system_password" };

    /// <summary>Fjern Lenovos endelse ";[Optional:...]" og blanktegn.</summary>
    public static string Clean(string? v)
    {
        var s = (v ?? "").Trim();
        var i = s.IndexOf(";[", StringComparison.Ordinal);
        return (i >= 0 ? s[..i] : s).Trim();
    }

    /// <summary>Er værdien en liste af elementer (bootrækkefølge), hvor hvert element står i de mulige værdier?</summary>
    public static bool IsList(BiosSettingItem item) =>
        item.Type == "ordered-list" || (item.Possible is { Count: > 0 } p && item.Value.Contains(':') && !p.Contains(item.Value, StringComparer.OrdinalIgnoreCase)
            && item.Value.Split(':').All(x => p.Contains(x, StringComparer.OrdinalIgnoreCase)));

    public static string[] ListParts(string v) => v.Split(v.Contains(':') ? ':' : ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Må <paramref name="value"/> skrives til indstillingen, som den ser ud nu?</summary>
    public static bool IsValid(BiosSettingItem item, string value)
    {
        value = Clean(value);
        switch (item.Type)
        {
            case "integer":
                return long.TryParse(value, out var n) && (item.Min is not { } lo || n >= lo) && (item.Max is not { } hi || n <= hi);
            case "string":
                return value.Length <= (item.Max ?? 512) && value.Length >= (item.Min ?? 0) && !value.Contains('\n');
        }
        if (item.Possible is not { Count: > 0 } p) return false;
        if (p.Contains(value, StringComparer.Ordinal)) return true;
        // bootlister: alle elementer skal findes i de nye mulige værdier
        return (IsList(item) || item.Type == "ordered-list") && ListParts(value) is { Length: > 0 } parts
            && parts.All(x => p.Contains(x, StringComparer.Ordinal)) && parts.Distinct().Count() == parts.Length;
    }

    /// <summary>Samme værdi, når store/små bogstaver og blanktegn ses bort fra (BIOS'en har "normaliseret" den).</summary>
    public static bool SameNormalized(string a, string b) =>
        string.Equals(Regex.Replace(Clean(a), @"\s+", ""), Regex.Replace(Clean(b), @"\s+", ""), StringComparison.OrdinalIgnoreCase);
}

// ------------------------------------------------------------------ fælles backend
/// <summary>Læs og skriv BIOS-indstillinger. Passwordet gives kun til <see cref="Apply"/> og gemmes ingen steder.</summary>
public interface IBiosSettingsBackend
{
    string Driver { get; }
    bool CanWrite { get; }
    BiosSettingsInfo Info();
    BiosSettingsReport Read();
    BiosApplyResult Apply(IReadOnlyList<BiosSettingChange> changes, string? password);
}

/// <summary>Fælles forkontrol før skrivning: findes indstillingen, står den stadig på den gamle værdi, og er den nye gyldig?</summary>
public static class BiosApplyCheck
{
    public static List<BiosApplyLine> Validate(IReadOnlyList<BiosSettingChange> changes, IReadOnlyDictionary<string, BiosSettingItem> now)
    {
        var lines = new List<BiosApplyLine>();
        foreach (var c in changes)
        {
            var l = new BiosApplyLine { Name = c.Name, Value = c.Value };
            if (!now.TryGetValue(c.Name, out var it)) { l.Status = BiosApplyStatus.Missing; l.Detail = "Findes ikke i denne BIOS."; }
            else if (!string.Equals(BiosValues.Clean(it.Value), BiosValues.Clean(c.ExpectOld), StringComparison.Ordinal))
            { l.Status = BiosApplyStatus.Changed; l.Detail = $"Værdien er ændret siden sammenligningen (nu: {it.Value})."; }
            else if (!BiosValues.IsValid(it, c.Value)) { l.Status = BiosApplyStatus.Invalid; l.Detail = "Værdien er ikke gyldig i denne BIOS."; }
            lines.Add(l);
        }
        return lines;
    }

    public static bool AllOk(List<BiosApplyLine> lines) => lines.All(l => l.Status == BiosApplyStatus.NotWritten);
}

// ------------------------------------------------------------------ Linux: firmware-attributes
/// <summary>Filadgang til sysfs gennem en indskydelig rod, så testene kan bruge et falsk filtræ.</summary>
public class FirmwareFs(string root = "/")
{
    public string Root { get; } = root;
    protected string Full(string rel) => Path.Combine(Root, rel.TrimStart('/'));
    public virtual bool DirExists(string rel) => Directory.Exists(Full(rel));
    public virtual bool FileExists(string rel) => File.Exists(Full(rel));
    public virtual IEnumerable<string> Dirs(string rel) =>
        DirExists(rel) ? Directory.GetDirectories(Full(rel)).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal) : [];
    /// <summary>Filens indhold uden afsluttende linjeskift, eller null hvis den mangler eller ikke kan læses.</summary>
    public virtual string? Read(string rel)
    {
        try { return File.ReadAllText(Full(rel)).TrimEnd('\n', '\r', '\0'); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
    /// <summary>Kan filen åbnes til læsning? (En sysfs-fil kan åbnes, selv om selve læsningen giver EINVAL.)</summary>
    public virtual bool CanOpen(string rel)
    {
        try { using var _ = File.OpenRead(Full(rel)); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Skriv én værdi (sysfs: én write). Kernen afviser med IOException (EINVAL) eller UnauthorizedAccessException (EACCES/EPERM).</summary>
    public virtual void Write(string rel, string value) => File.WriteAllText(Full(rel), value);
}

/// <summary>
/// BIOS-indstillinger via /sys/class/firmware-attributes (think-lmi, dell-wmi-sysman, hp-bioscfg). Læsning er fælles for alle tre;
/// skrivning er kun slået til for think-lmi: bulk → hver værdi → én save, stop ved første afvisning, ryd passwordet bagefter.
/// </summary>
public sealed class FirmwareAttributes : IBiosSettingsBackend
{
    public const string Base = "sys/class/firmware-attributes";
    private readonly FirmwareFs fs;
    private readonly string dir;

    private FirmwareAttributes(FirmwareFs fs, string driver) { this.fs = fs; Driver = driver; dir = $"{Base}/{driver}"; }

    public string Driver { get; }
    public bool CanWrite => BiosSettingsDrivers.Writable(Driver);

    /// <summary>Den første driver med en attributes-mappe, eller null (ingen firmware-attributes på denne maskine).</summary>
    public static FirmwareAttributes? Detect(FirmwareFs? fs = null)
    {
        fs ??= new FirmwareFs();
        foreach (var d in BiosSettingsDrivers.Sysfs)
            if (fs.DirExists($"{Base}/{d}/attributes")) return new FirmwareAttributes(fs, d);
        return null;
    }

    private string A(string rel) => $"{dir}/attributes/{rel}";
    private IEnumerable<string> SettingDirs() => fs.Dirs($"{dir}/attributes").Where(n => !BiosValues.NotSettings.Contains(n));

    private bool AdminPassword()
    {
        foreach (var a in new[] { "Admin", "Setup Password" })
            if (fs.Read($"{dir}/authentication/{a}/is_enabled")?.Trim() == "1") return true;
        return false;
    }

    private bool? PendingReboot() => fs.Read(A("pending_reboot"))?.Trim() is { } p ? p == "1" : null;
    private string? SaveMode() => fs.FileExists(A("save_settings")) ? fs.Read(A("save_settings"))?.Trim() is { Length: > 0 } m ? m : "single" : null;

    public BiosSettingsInfo Info() => new()
    {
        Driver = Driver, Count = SettingDirs().Count(), AdminPassword = AdminPassword(), SaveMode = SaveMode(), PendingReboot = PendingReboot(), Writable = CanWrite,
    };

    public BiosSettingsReport Read()
    {
        var r = new BiosSettingsReport { Driver = Driver, AdminPassword = AdminPassword(), SaveMode = SaveMode(), PendingReboot = PendingReboot(), Writable = CanWrite };
        foreach (var n in SettingDirs())
            if (ReadItem(n) is { } it) r.Items.Add(it);
        return r;
    }

    private BiosSettingItem? ReadItem(string n)
    {
        var value = fs.Read(A($"{n}/current_value"));
        // findes filen, men kan den ikke læses (tom værdi i BIOS'en), tages indstillingen med som ulæselig i stedet for at forsvinde stille
        // (uden adgang – ikke root – kan filen slet ikke åbnes; så springes den over som før)
        if (value is null && !(fs.FileExists(A($"{n}/current_value")) && fs.CanOpen(A($"{n}/current_value")))) return null;
        var possible = fs.Read(A($"{n}/possible_values")) is { Length: > 0 } pv
            ? pv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(x => !x.StartsWith('[')).ToList() : null;
        var type = fs.Read(A($"{n}/type"))?.Trim() is { Length: > 0 } t ? t : possible is { Count: > 0 } ? "enumeration" : "string";
        if (type == "password") return null;   // aldrig passwords
        long? Num(string f) => long.TryParse(fs.Read(A($"{n}/{f}"))?.Trim(), out var x) ? x : null;
        return new BiosSettingItem
        {
            Name = n, Display = fs.Read(A($"{n}/display_name"))?.Trim() is { Length: > 0 } d ? d : null, Type = type,
            Value = value is null ? "" : BiosValues.Clean(value), Possible = possible, Unreadable = value is null ? true : null,
            Min = type == "string" ? Num("min_length") : Num("min_value"), Max = type == "string" ? Num("max_length") : Num("max_value"),
        };
    }

    public BiosApplyResult Apply(IReadOnlyList<BiosSettingChange> changes, string? password)
    {
        var res = new BiosApplyResult();
        if (!CanWrite) return Fail(res, $"Fleetdeck kan endnu kun læse BIOS-indstillinger med {Driver}.");
        if (changes.Count == 0) return Fail(res, "Ingen indstillinger at skrive.");
        if (PendingReboot() == true) return Fail(res, "Der venter allerede BIOS-ændringer. Genstart maskinen først.");
        var admin = AdminPassword();
        if (admin && string.IsNullOrEmpty(password))
            return Fail(res, "BIOS'en har en administratoradgangskode. Skriv den i Fleetdeck for at gendanne (den huskes ikke).");
        var now = Read().Items.ToDictionary(i => i.Name, StringComparer.Ordinal);
        res.Lines = BiosApplyCheck.Validate(changes, now);
        if (!BiosApplyCheck.AllOk(res.Lines)) return Fail(res, "Intet er skrevet: nogle værdier passer ikke længere. Læs indstillingerne igen og sammenlign på ny.");

        var pwFile = $"{dir}/authentication/Admin/current_password";
        var pwWritten = false;
        var bulk = false;
        var prevMode = SaveMode();
        try
        {
            if (admin)
            {
                if (!fs.FileExists(pwFile)) return Fail(res, "Driveren har ingen plads til administratoradgangskoden. Intet er skrevet.");
                fs.Write(pwFile, password!);
                pwWritten = true;
            }
            bulk = fs.FileExists(A("save_settings"));
            if (bulk) fs.Write(A("save_settings"), "bulk");   // ellers gemmer Lenovo efter hver skrivning (ca. 48 gemninger pr. opstart)
            var pairs = changes.Zip(res.Lines).ToList();
            foreach (var (c, line) in pairs)
            {
                // tjek lige før skrivningen (en anden kan have ændret den)
                if (!bulk) MarkSaved(res);
                if (ReadItem(c.Name) is not { } it || !string.Equals(it.Value, BiosValues.Clean(c.ExpectOld), StringComparison.Ordinal))
                { line.Status = BiosApplyStatus.Changed; line.Detail = "Værdien blev ændret undervejs."; return Abort(Stopped(bulk)); }
                try { fs.Write(A($"{c.Name}/current_value"), BiosValues.Clean(c.Value)); line.Status = BiosApplyStatus.Written; }
                catch (UnauthorizedAccessException)
                {
                    // adgang nægtet: stop straks, så tælleren for forkerte passwords ikke løber op
                    line.Status = BiosApplyStatus.Denied; line.Detail = "Adgang nægtet.";
                    return Abort((admin ? "BIOS'en afviste adgangskoden. " : "BIOS'en nægtede adgang. ") + Stopped(bulk));
                }
                catch (IOException e)
                {
                    line.Status = BiosApplyStatus.Rejected; line.Detail = Errno(e);
                    // Lenovo afviser en værdi, der strider mod en anden indstilling (fx Secure Boot mod CSM/Boot Mode)
                    var hint = changes.Count > 1 && e.HResult == 22 ? " Værdien kan afhænge af en anden indstilling; gendan dem hver for sig med en genstart imellem." : "";
                    return Abort($"BIOS'en afviste {c.Name}.{hint} " + Stopped(bulk));
                }
            }
            if (bulk)
            {
                try { fs.Write(A("save_settings"), "save"); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Abort($"BIOS'en ville ikke gemme ({Errno(e)}). Intet er gemt."); }
            }
            res.Saved = true;

            // Bulk: de skrevne værdier ligger klar i BIOS'en (save_required). Skriv de gamle værdier tilbage, så en senere
            // save ikke gemmer noget, der ikke blev godkendt. Lykkes det ikke, skal maskinen genstartes før næste skrivning.
            BiosApplyResult Abort(string msg)
            {
                if (bulk)
                {
                    var stuck = new List<string>();
                    foreach (var (c, line) in pairs.Where(p => p.Second.Status == BiosApplyStatus.Written).Reverse())
                    {
                        try { fs.Write(A($"{c.Name}/current_value"), BiosValues.Clean(c.ExpectOld)); line.Detail = "Skrevet og trukket tilbage. Intet er gemt."; }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { stuck.Add(c.Name); line.Detail = "Skrevet, men ikke gemt. Kunne ikke trækkes tilbage."; }
                    }
                    if (stuck.Count > 0)
                    {
                        res.PendingReboot = true;
                        msg += $" {string.Join(", ", stuck)} ligger klar i BIOS'en uden at være gemt. Genstart maskinen før næste BIOS-ændring.";
                    }
                }
                return Fail(res, msg);
            }
        }
        finally
        {
            // think-lmi: en tom skrivning når ikke kernen (ingen write()), så koden ville blive i kernens hukommelse og blive brugt
            // ved alle senere skrivninger frem til genstart. "\n" gennemgår længdetjekket, og kernen erstatter linjeskift med 0.
            if (pwWritten && !ClearPassword(pwFile))
            {
                res.Message = (res.Message is { Length: > 0 } m0 ? m0 + " " : "") +
                    "Adgangskoden kunne ikke ryddes i driveren og ligger i kernens hukommelse indtil næste genstart. Genstart maskinen.";
                res.PasswordNotCleared = true;
            }
            // tilbage til den tilstand, maskinen havde (andre værktøjer forventer at hver skrivning gemmes)
            if (bulk && prevMode is { } pm && pm != "bulk")
                try { fs.Write(A("save_settings"), pm); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        if (!res.Saved) return res;
        res.PendingReboot = PendingReboot();
        res.Ok = true;
        var msgOk = res.PendingReboot == false
            ? $"Gemt ({res.Lines.Count} indstillinger), men BIOS'en melder ingen ventende ændringer. Læs indstillingerne igen efter næste genstart."
            : $"Gemt ({res.Lines.Count} indstillinger). Træder i kraft ved næste genstart.";
        res.Message = res.PasswordNotCleared ? msgOk + " " + res.Message : msgOk;
        return res;
    }

    /// <summary>
    /// Ryd administratoradgangskoden i think-lmi. Kernen afviser en kode kortere end min_password_length (undtagen længde 0, som
    /// aldrig når kernen); en streng af linjeskift med netop den længde bliver til lutter 0-tegn.
    /// </summary>
    private bool ClearPassword(string pwFile)
    {
        var min = int.TryParse(fs.Read($"{dir}/authentication/Admin/min_password_length")?.Trim(), out var n) && n > 1 ? Math.Min(n, 64) : 1;
        try { fs.Write(pwFile, new string('\n', min)); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string Stopped(bool bulk) => bulk ? "Stoppet uden at gemme." : "Stoppet. De værdier, der allerede er skrevet, er gemt (driveren gemmer hver værdi for sig).";
    /// <summary>Uden save_settings gemmer driveren hver skrivning med det samme.</summary>
    private static void MarkSaved(BiosApplyResult r)
    {
        foreach (var l in r.Lines.Where(l => l.Status == BiosApplyStatus.Written)) l.Detail ??= "Gemt.";
    }

    private static string Errno(Exception e) => e.HResult switch { 22 => "ugyldig værdi", 16 => "optaget", 5 => "I/O-fejl", _ => e.Message };

    private static BiosApplyResult Fail(BiosApplyResult r, string msg)
    {
        r.Ok = false; r.Message = msg;
        foreach (var l in r.Lines.Where(l => l.Status == BiosApplyStatus.Written && !r.Saved)) l.Detail ??= "Skrevet, men ikke gemt.";
        return r;
    }
}

// ------------------------------------------------------------------ Windows: Lenovo WMI
/// <summary>Lenovos WMI-klasser i root\wmi bag et interface, så logikken kan testes uden en Lenovo-pc.</summary>
public interface ILenovoWmi
{
    /// <summary>Lenovo_BiosSetting.CurrentSetting for alle instanser ("Navn,Værdi" – tomme springes over).</summary>
    IReadOnlyList<string> CurrentSettings();
    /// <summary>Lenovo_GetBiosSelections("Navn") – kommasepareret, eller null hvis klassen mangler.</summary>
    string? Selections(string name);
    /// <summary>Lenovo_BiosPasswordSettings.PasswordState (bit 1 = power-on, bit 2 = supervisor/admin), eller null.</summary>
    int? PasswordState();
    /// <summary>Findes Lenovo_WmiOpcodeInterface (nyere modeller: password via opcode)?</summary>
    bool HasOpcode { get; }
    string Opcode(string parameter);
    string Set(string parameter);
    string Save(string? parameter);
    /// <summary>Lenovo_DiscardBiosSettings, hvis den findes (null ellers).</summary>
    string? Discard(string? parameter);
}

/// <summary>BIOS-indstillinger på Lenovo-pc'er med Windows: læs, valider mod GetBiosSelections, skriv hver værdi, gem én gang.</summary>
public sealed class LenovoWmiSettings(ILenovoWmi wmi) : IBiosSettingsBackend
{
    public string Driver => BiosSettingsDrivers.LenovoWmi;
    public bool CanWrite => true;

    public const string Success = "Success", AccessDenied = "Access Denied", SystemBusy = "System Busy", NotSupported = "Not Supported", InvalidParameter = "Invalid Parameter";

    private bool AdminPassword() => wmi.PasswordState() is int s && (s & 2) != 0;

    public BiosSettingsInfo Info() => new() { Driver = Driver, Count = wmi.CurrentSettings().Count(s => s.Contains(',')), AdminPassword = AdminPassword(), Writable = true };

    public BiosSettingsReport Read()
    {
        var r = new BiosSettingsReport { Driver = Driver, AdminPassword = AdminPassword(), Writable = true };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in wmi.CurrentSettings())
        {
            var i = raw.IndexOf(',');
            if (i <= 0) continue;
            var name = raw[..i].Trim();
            if (!seen.Add(name) || BiosValues.NotSettings.Contains(name) || name.Contains("Password", StringComparison.OrdinalIgnoreCase)) continue;
            var possible = ParseSelections(wmi.Selections(name));
            r.Items.Add(new BiosSettingItem
            {
                Name = name, Value = BiosValues.Clean(raw[(i + 1)..]), Possible = possible,
                Type = possible is { Count: > 0 } ? "enumeration" : "string",
            });
        }
        return r;
    }

    /// <summary>Valgene er kommaseparerede; bootlister kan komme som ét kolonsepareret element.</summary>
    public static List<string>? ParseSelections(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var parts = BiosValues.Clean(s).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (parts.Count == 1 && parts[0].Contains(':')) parts = parts[0].Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return parts.Count > 0 ? parts : null;
    }

    public BiosApplyResult Apply(IReadOnlyList<BiosSettingChange> changes, string? password)
    {
        var res = new BiosApplyResult();
        if (changes.Count == 0) return Fail(res, "Ingen indstillinger at skrive.");
        var admin = AdminPassword();
        if (admin && string.IsNullOrEmpty(password))
            return Fail(res, "BIOS'en har en administratoradgangskode. Skriv den i Fleetdeck for at gendanne (den huskes ikke).");
        var now = Read().Items.ToDictionary(i => i.Name, StringComparer.Ordinal);
        res.Lines = BiosApplyCheck.Validate(changes, now);
        if (!BiosApplyCheck.AllOk(res.Lines)) return Fail(res, "Intet er skrevet: nogle værdier passer ikke længere. Læs indstillingerne igen og sammenlign på ny.");

        // nyere modeller: password via opcode-interfacet; ældre: "Navn,Værdi,<pw>,ascii,us" på både Set og Save
        var legacy = admin && !wmi.HasOpcode;
        var suffix = legacy ? $",{password},ascii,us" : "";
        if (admin && !legacy)
        {
            var o = Norm(wmi.Opcode("WmiOpcodePasswordAdmin:" + password));
            if (o != Success) return Discard(res, o == AccessDenied ? "BIOS'en afviste adgangskoden. Intet er gemt." : $"BIOS'en svarede \"{o}\" på adgangskoden. Intet er gemt.", password, legacy);
        }
        foreach (var (c, line) in changes.Zip(res.Lines))
        {
            var r = Norm(wmi.Set($"{c.Name},{BiosValues.Clean(c.Value)}{suffix}"));
            if (r == Success) { line.Status = BiosApplyStatus.Written; continue; }
            line.Status = r == AccessDenied ? BiosApplyStatus.Denied : BiosApplyStatus.Rejected;
            line.Detail = r;
            return Discard(res, r switch
            {
                AccessDenied => (admin ? "BIOS'en afviste adgangskoden. " : "BIOS'en nægtede adgang. ") + "Stoppet uden at gemme.",
                SystemBusy => "Der venter allerede ændringer. Genstart først. Intet er gemt.",
                _ => $"BIOS'en afviste {c.Name} (\"{r}\"). Stoppet uden at gemme.",
            }, password, legacy);
        }
        var s = Norm(wmi.Save(legacy ? $"{password},ascii,us" : null));
        if (s != Success)
            return Discard(res, s == SystemBusy ? "Der venter allerede ændringer. Genstart først. Intet er gemt." : $"BIOS'en ville ikke gemme (\"{s}\"). Intet er gemt.", password, legacy);
        res.Saved = true; res.Ok = true; res.PendingReboot = true;
        res.Message = $"Gemt ({res.Lines.Count} indstillinger). Træder i kraft ved næste genstart.";
        return res;
    }

    /// <summary>WMI-svaret uden blanktegn; passwordet kan aldrig stå i det, vi viser.</summary>
    private static string Norm(string? r) => (r ?? "").Trim() is { Length: > 0 } t ? t : "intet svar";

    private BiosApplyResult Discard(BiosApplyResult r, string msg, string? password, bool legacy)
    {
        try { wmi.Discard(legacy ? $"{password},ascii,us" : null); } catch (Exception) { /* findes ikke på alle modeller */ }
        return Fail(r, msg);
    }

    private static BiosApplyResult Fail(BiosApplyResult r, string msg)
    {
        r.Ok = false; r.Message = msg;
        foreach (var l in r.Lines.Where(l => l.Status == BiosApplyStatus.Written)) l.Detail ??= "Skrevet, men ikke gemt.";
        return r;
    }
}

// ------------------------------------------------------------------ jobbene
/// <summary>bios_settings_read og bios_settings_apply på agenten (fælles for Linux og Windows).</summary>
public static class BiosSettingsJob
{
    public const string NotSupported = "Denne maskine har ingen BIOS-indstillinger, Fleetdeck kan læse. Det kræver Lenovo (think-lmi eller WMI), Dell eller HP.";

    /// <param name="secret">Byt jobbets engangsbillet til BIOS-adgangskoden (kun i hukommelsen).</param>
    public static async Task<JobResult> Run(AgentJob j, IBiosSettingsBackend? backend, Func<BiosSecretRequest, CancellationToken, Task<BiosSecretResponse?>> secret, CancellationToken ct)
    {
        if (backend is null) return new JobResult { Status = JobStatus.Failed, ExitCode = 1, Output = NotSupported };
        if (j.Type == JobTypes.BiosSettingsRead)
        {
            LiveJob.Phase(0, 100, "Læser BIOS-indstillingerne");
            var rep = await Task.Run(backend.Read, ct);
            return new JobResult
            {
                Status = JobStatus.Success, ExitCode = 0, BiosSettings = rep,
                Output = $"Læste {rep.Items.Count} BIOS-indstillinger ({rep.Driver})." + (rep.PendingReboot == true ? " Der venter ændringer, som gælder efter næste genstart." : ""),
            };
        }
        if (j.Type != JobTypes.BiosSettingsApply) return new JobResult { Status = JobStatus.Failed, ExitCode = -1, Output = $"Ukendt jobtype: {j.Type}" };
        var changes = j.Params.BiosSettings ?? [];
        if (changes.Count == 0) return new JobResult { Status = JobStatus.Failed, ExitCode = 1, Output = "Jobbet har ingen indstillinger at skrive." };
        if (!backend.CanWrite) return new JobResult { Status = JobStatus.Failed, ExitCode = 1, Output = $"Fleetdeck kan endnu kun læse BIOS-indstillinger med {backend.Driver}." };
        LiveJob.Phase(0, 30, "Tager en kopi af indstillingerne");
        var before = await Task.Run(backend.Read, ct);
        string? pw = null;
        if (j.Params.Ticket is { Length: > 0 } t)
        {
            try { pw = (await secret(new BiosSecretRequest { JobId = j.Id, Ticket = t }, ct))?.Value; }
            catch (HttpRequestException) { pw = null; }
            if (string.IsNullOrEmpty(pw))
                return new JobResult { Status = JobStatus.Failed, ExitCode = 1, BiosSettingsBefore = before,
                    Output = "Adgangskoden er ikke længere i Fleetdeck (den huskes kun kort). Start gendannelsen igen. Intet er skrevet." };
        }
        LiveJob.Phase(30, 90, $"Skriver {changes.Count} indstillinger");
        BiosApplyResult res;
        try { res = await Task.Run(() => backend.Apply(changes, pw), ct); }
        finally { pw = null; }
        LiveJob.Phase(90, 100, "Læser indstillingerne igen");
        var after = await Task.Run(backend.Read, ct);
        var lines = res.Lines.Select(l => $"{l.Name} = {l.Value}: {Label(l.Status)}{(l.Detail is { } d ? $" ({d})" : "")}");
        return new JobResult
        {
            Status = res.Ok ? JobStatus.Success : JobStatus.Failed, ExitCode = res.Ok ? 0 : 1, BiosSettings = after, BiosSettingsBefore = before, BiosApply = res,
            Output = res.Message + "\n\n" + string.Join("\n", lines),
        };
    }

    public static string Label(string status) => status switch
    {
        BiosApplyStatus.Written => "skrevet", BiosApplyStatus.Rejected => "afvist", BiosApplyStatus.Denied => "adgang nægtet",
        BiosApplyStatus.Changed => "ændret siden", BiosApplyStatus.Invalid => "ugyldig", BiosApplyStatus.Missing => "findes ikke", _ => "ikke skrevet",
    };
}
