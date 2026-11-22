using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fleetdeck.Shared;

// BIOS-opdatering inde fra styresystemet (bios_update). Kun på et udtrykkeligt tryk fra en administrator, aldrig i "Opdatér alt",
// og altid med en kopi af BIOS-indstillingerne i Fleetdeck først. Agenten flasher aldrig selv: på Linux lægges en UEFI-kapsel klar
// via fwupd, og på Windows skriver Lenovos eget program første fase. Selve opdateringen sker i firmwaren ved næste genstart, som
// brugeren selv vælger. Efter genstarten kontrollerer agenten versionen og ESRT og melder resultatet (også efter en genstart af agenten).

public static class BiosUpdatePhases
{
    /// <summary>check: kun forudsætningerne. stage: kopi → forudsætninger → pakke → læg klar. verify: kontrollér efter genstart.</summary>
    public const string Check = "check", Stage = "stage", Verify = "verify";
    public static readonly string[] All = [Check, Stage, Verify];
}

public static class BiosUpdateRoutes
{
    /// <summary>capsule: Linux, fwupd lægger leverandørens ROM klar som UEFI-kapsel (Secure Boot kan blive slået til).
    /// lenovo-exe: Windows, Lenovos pakke pakkes ud og wFlashGUIx64.exe /quiet skriver første fase.</summary>
    public const string Capsule = "capsule", LenovoExe = "lenovo-exe";
}

public static class BiosCheckStates
{
    public const string Ok = "ok", Warn = "warn", Fail = "fail";
}

public static class BiosUpdateOutcomes
{
    public const string Checked = "checked", Blocked = "blocked", Staged = "staged", Failed = "failed",
        Updated = "updated", Rejected = "rejected", NotApplied = "not_applied", Unknown = "unknown";
    /// <summary>Resultater, der kun kan komme efter en genstart.</summary>
    public static readonly string[] AfterBoot = [Updated, Rejected, NotApplied, Unknown];
}

/// <summary>Det, serveren beder agenten om (JobParams.BiosUpdate).</summary>
public sealed class BiosUpdateOrder
{
    public int RunId { get; set; }
    public string Phase { get; set; } = BiosUpdatePhases.Check;
    public string Route { get; set; } = "";
    public string Vendor { get; set; } = "";
    /// <summary>BIOS-familien (Lenovo: de første 4 tegn af BIOS-versionen, fx "q9zk"). Skal passe til maskinen.</summary>
    public string Family { get; set; } = "";
    public string? FromVersion { get; set; }
    /// <summary>Versionen, der skal ende på maskinen (fx "Q9ZKT79A").</summary>
    public string TargetVersion { get; set; } = "";
    public string? TargetNumeric { get; set; }
    /// <summary>stage: agentens adresse til pakken (kun til netop denne kørsel), filnavn, størrelse og SHA-256.</summary>
    public string? PackageUrl { get; set; }
    public string? FileName { get; set; }
    public long? Size { get; set; }
    public string? Sha256 { get; set; }
    /// <summary>usb-zip eller win-exe.</summary>
    public string? PackageKind { get; set; }
}

/// <summary>Én forudsætning, som den vises i UI'en.</summary>
public sealed class BiosCheck
{
    public string Id { get; set; } = "";
    public string State { get; set; } = BiosCheckStates.Ok;
    public string Text { get; set; } = "";
    public static BiosCheck Ok(string id, string text) => new() { Id = id, State = BiosCheckStates.Ok, Text = text };
    public static BiosCheck Warn(string id, string text) => new() { Id = id, State = BiosCheckStates.Warn, Text = text };
    public static BiosCheck Fail(string id, string text) => new() { Id = id, State = BiosCheckStates.Fail, Text = text };
}

/// <summary>Agentens melding: forudsætninger, klargøring eller resultat efter genstart.</summary>
public sealed class BiosUpdateReport
{
    public int RunId { get; set; }
    public string Phase { get; set; } = BiosUpdatePhases.Check;
    public string Outcome { get; set; } = BiosUpdateOutcomes.Checked;
    public string Message { get; set; } = "";
    public List<BiosCheck> Checks { get; set; } = [];
    public string? CurrentVersion { get; set; }
    public string? TargetVersion { get; set; }
    public string? DeviceId { get; set; }
    public long? VersionRaw { get; set; }
    public long? TargetRaw { get; set; }
    public string? FwupdState { get; set; }
    public string? FwupdError { get; set; }
    /// <summary>ESRT last_attempt_status (0 = lykkedes) og last_attempt_version.</summary>
    public int? EsrtStatus { get; set; }
    public long? EsrtAttemptVersion { get; set; }
    public bool? BootOrderChanged { get; set; }
    public DateTime? StagedAt { get; set; }
    public DateTime? BootAt { get; set; }
    /// <summary>Kontrollen efter en genstart fandt opdateringen stadig ventende (Outcome er så staged, med et nyt StagedAt).</summary>
    public bool StillPending { get; set; }
}

public static class BiosUpdatePostKinds
{
    public const string Snapshot = "snapshot", Report = "report";
}

/// <summary>/api/agent/bios/update: kopien før opdateringen (snapshot) eller en melding (report), også efter genstart.</summary>
public sealed class BiosUpdatePost
{
    public int RunId { get; set; }
    public int? JobId { get; set; }
    public string Kind { get; set; } = BiosUpdatePostKinds.Report;
    public BiosSettingsReport? Settings { get; set; }
    public BiosUpdateReport? Report { get; set; }
}

public sealed class BiosUpdatePostResponse
{
    public bool Ok { get; set; }
    public int? SnapshotId { get; set; }
    public string? Message { get; set; }
}

/// <summary>Det, agenten kan se på maskinen før opdateringen.</summary>
public sealed class BiosProbe
{
    public bool? Virtual { get; set; }
    public string? BiosVersion { get; set; }
    public string? Model { get; set; }
    public bool? OnAc { get; set; }
    /// <summary>Batteriets niveau i procent. Null = intet batteri (stationær).</summary>
    public int? Battery { get; set; }
    public bool SettingsReadable { get; set; }
    public bool? PendingSettings { get; set; }
    public bool AdminPassword { get; set; }
    // Linux / fwupd
    public bool Fwupd { get; set; }
    public string? DeviceId { get; set; }
    /// <summary>ESRT-GUID'en for systemfirmwaren (fwupd's "System Firmware" / Windows' ESRT-type 1).</summary>
    public string? DeviceGuid { get; set; }
    public long? VersionRaw { get; set; }
    public long? VersionLowestRaw { get; set; }
    public bool Updatable { get; set; }
    public string? UpdateState { get; set; }
    public long? EspFree { get; set; }
    public bool? SecureBoot { get; set; }
    /// <summary>Disken låses op med TPM (LUKS med tpm2-device/clevis). En BIOS-opdatering ændrer PCR0.</summary>
    public bool? TpmUnlock { get; set; }
    public string? BootOrder { get; set; }
    // Windows
    public bool? BitLocker { get; set; }
}

/// <summary>Lenovos BIOS-koder "XXXXTnnA": de første 4 tegn er familien, de to tegn efter T er niveauet i hex (79 = 0x79).</summary>
public static partial class LenovoBiosCode
{
    [GeneratedRegex("^([A-Z0-9]{4})[A-Z]([0-9A-F]{2})[A-Z]$", RegexOptions.IgnoreCase)]
    private static partial Regex CodeRx();

    public static string? Family(string? version) => version?.Trim() is { Length: >= 4 } v ? v[..4].ToLowerInvariant() : null;

    public static int? Level(string? code) =>
        code?.Trim() is { } c && CodeRx().Match(c) is { Success: true } m ? int.Parse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture) : null;

    /// <summary>Fortegn for target − current inden for samme familie; null hvis de ikke kan sammenlignes sikkert.</summary>
    public static int? Compare(string? current, string? target)
    {
        if (Family(current) is not { } a || a != Family(target)) return null;
        if (Level(current) is not int c || Level(target) is not int t) return null;
        return Math.Sign(t - c);
    }

    /// <summary>
    /// Firmwarens rå version for målet, regnet ud fra den nuværende: fwupd/ESRT viser fx 0x00010075 for niveau 75, så målet 79 er
    /// 0x00010079. Kun når den nuværende rå version faktisk ender på det nuværende niveau; ellers null (så kontrolleres med BIOS-versionen).
    /// </summary>
    public static long? TargetRaw(string? current, long? currentRaw, string? target)
    {
        if (currentRaw is not long raw || Level(current) is not int c || Level(target) is not int t) return null;
        if ((raw & 0xFF) != c) return null;
        return (raw & ~0xFFL) | (uint)t;
    }
}

/// <summary>Forudsætningerne. Én "fail" stopper opdateringen; "warn" vises men stopper ikke.</summary>
public static class BiosPreconditions
{
    public const int MinBattery = 30;
    public const long MinEspFree = 32L * 1024 * 1024;

    public static bool Blocked(IEnumerable<BiosCheck> checks) => checks.Any(c => c.State == BiosCheckStates.Fail);

    /// <param name="snapshotCount">stage: antal indstillinger i kopien, Fleetdeck har gemt (null = ingen kopi). check: ignoreres.</param>
    public static List<BiosCheck> Evaluate(BiosUpdateOrder o, BiosProbe p, int? snapshotCount, bool stage)
    {
        var l = new List<BiosCheck>();
        // kopien af indstillingerne kommer altid først
        if (stage)
            l.Add(snapshotCount is int n
                ? BiosCheck.Ok("snapshot", $"Kopi af {n} BIOS-indstillinger er gemt i Fleetdeck.")
                : BiosCheck.Fail("snapshot", BiosUpdateText.NoSnapshot));
        else
            l.Add(p.SettingsReadable
                ? BiosCheck.Ok("snapshot", "Fleetdeck tager automatisk en kopi af BIOS-indstillingerne som første trin.")
                : BiosCheck.Fail("snapshot", BiosUpdateText.NoSettings));

        l.Add(p.Virtual == true ? BiosCheck.Fail("vm", "Virtuel maskine. BIOS'en styres af værten.") : BiosCheck.Ok("vm", "Fysisk maskine."));

        var fam = LenovoBiosCode.Family(p.BiosVersion);
        l.Add(fam is not null && fam == o.Family.ToLowerInvariant()
            ? BiosCheck.Ok("family", $"BIOS-familien passer ({fam.ToUpperInvariant()}).")
            : BiosCheck.Fail("family", $"BIOS-familien passer ikke: maskinen har {p.BiosVersion ?? "ukendt"}, pakken er til {o.Family.ToUpperInvariant()}."));

        l.Add(LenovoBiosCode.Compare(p.BiosVersion, o.TargetVersion) switch
        {
            1 => BiosCheck.Ok("version", $"{o.TargetVersion} er nyere end {p.BiosVersion}."),
            0 => BiosCheck.Fail("version", $"Maskinen har allerede {p.BiosVersion}."),
            -1 => BiosCheck.Fail("version", $"{o.TargetVersion} er ældre end {p.BiosVersion}. Fleetdeck nedgraderer aldrig en BIOS."),
            _ => BiosCheck.Fail("version", $"Versionerne {p.BiosVersion ?? "ukendt"} og {o.TargetVersion} kan ikke sammenlignes sikkert."),
        });

        l.Add(Power(p));
        l.Add(p.PendingSettings == true
            ? BiosCheck.Fail("pending", "Der venter BIOS-ændringer, som først gælder efter en genstart. Genstart maskinen først.")
            : BiosCheck.Ok("pending", "Ingen ventende BIOS-ændringer."));

        if (o.Route == BiosUpdateRoutes.Capsule) Capsule(o, p, l);
        else if (o.Route == BiosUpdateRoutes.LenovoExe) LenovoExe(p, l);
        else l.Add(BiosCheck.Fail("route", "Fleetdeck kan ikke opdatere BIOS'en på denne maskine endnu."));
        return l;
    }

    private static BiosCheck Power(BiosProbe p)
    {
        if (p.OnAc == false) return BiosCheck.Fail("power", "Maskinen kører på batteri. Sæt laderen i.");
        if (p.Battery is int b && b < MinBattery) return BiosCheck.Fail("power", $"Batteriet er kun på {b} %. Det skal være mindst {MinBattery} %.");
        if (p.Battery is int ok) return p.OnAc == true
            ? BiosCheck.Ok("power", $"Laderen sidder i, og batteriet er på {ok} %.")
            : BiosCheck.Warn("power", $"Batteriet er på {ok} %, men det kan ikke ses, om laderen sidder i.");
        return BiosCheck.Ok("power", "Stationær pc på lysnettet.");
    }

    private static void Capsule(BiosUpdateOrder o, BiosProbe p, List<BiosCheck> l)
    {
        if (!p.Fwupd) l.Add(BiosCheck.Fail("fwupd", "fwupd er ikke installeret. Installér pakken fwupd."));
        else if (p.DeviceId is null) l.Add(BiosCheck.Fail("fwupd", "fwupd fandt ingen UEFI-kapsel til systemfirmwaren. Brug USB-stikket i stedet."));
        else if (!p.Updatable) l.Add(BiosCheck.Fail("fwupd", "fwupd siger, at systemfirmwaren ikke kan opdateres lige nu."));
        else if (p.UpdateState == FwupdJson.Pending) l.Add(BiosCheck.Fail("fwupd", "Der ligger allerede en firmwareopdatering klar til næste genstart. Genstart først."));
        else l.Add(BiosCheck.Ok("fwupd", "fwupd kan lægge opdateringen klar som UEFI-kapsel."));

        var targetRaw = LenovoBiosCode.TargetRaw(p.BiosVersion, p.VersionRaw, o.TargetVersion);
        if (targetRaw is long t && p.VersionLowestRaw is long low && t < low)
            l.Add(BiosCheck.Fail("lowest", "Firmwaren tillader ikke så gammel en version."));
        else if (targetRaw is null && p.DeviceId is not null)
            l.Add(BiosCheck.Warn("lowest", "Firmwarens versionsnummer kunne ikke regnes ud. Fleetdeck kontrollerer i stedet BIOS-versionen efter genstarten."));

        l.Add(p.EspFree switch
        {
            null => BiosCheck.Warn("esp", "Ledig plads på EFI-partitionen kunne ikke læses."),
            < MinEspFree => BiosCheck.Fail("esp", $"Der er kun {p.EspFree / (1024 * 1024)} MB ledigt på EFI-partitionen. Der skal være mindst {MinEspFree / (1024 * 1024)} MB."),
            _ => BiosCheck.Ok("esp", "Der er plads på EFI-partitionen."),
        });
        if (p.AdminPassword)
            l.Add(BiosCheck.Warn("password", "BIOS'en har en administratoradgangskode. Firmwaren kan afvise opdateringen; så bruges USB-stikket."));
        if (p.TpmUnlock == true)
            l.Add(BiosCheck.Warn("tpm", "Disken låses op med TPM. En BIOS-opdatering ændrer TPM-målingerne, så hav gendannelsesnøglen klar."));
        l.Add(p.SecureBoot == true
            ? BiosCheck.Ok("secureboot", "Secure Boot kan blive slået til.")
            : BiosCheck.Ok("secureboot", "Secure Boot behøver ikke ændres."));
    }

    private static void LenovoExe(BiosProbe p, List<BiosCheck> l)
    {
        if (p.Model?.Contains("ThinkPad", StringComparison.OrdinalIgnoreCase) == true)
            l.Add(BiosCheck.Fail("model", "ThinkPad bruger et andet opdateringsprogram, som Fleetdeck ikke kører endnu."));
        if (p.AdminPassword)
            l.Add(BiosCheck.Fail("password", "BIOS'en har en administratoradgangskode. Opdatering med adgangskode er ikke med endnu. Brug USB-stikket."));
        l.Add(p.BitLocker switch
        {
            true => BiosCheck.Warn("bitlocker", BiosUpdateText.BitLockerPause),
            false => BiosCheck.Ok("bitlocker", "BitLocker er ikke slået til."),
            null => BiosCheck.Warn("bitlocker", "BitLocker-status kunne ikke læses. Hav gendannelsesnøglen klar."),
        });
    }
}

/// <summary>Faste danske tekster (samme tone som resten af Fleetdeck).</summary>
public static class BiosUpdateText
{
    public const string NotSupported = "Fleetdeck kan ikke opdatere BIOS'en på denne maskine inde fra styresystemet. Brug USB-stikket.";
    public const string NoSettings = "Fleetdeck kan ikke læse BIOS-indstillingerne på denne maskine, så der kan ikke tages en kopi først. Opdateringen køres ikke.";
    public const string NoSnapshot = "Kopien af BIOS-indstillingerne kunne ikke gemmes i Fleetdeck. Intet er ændret.";
    public const string BitLockerPause = "BitLocker sættes på pause i to genstarter, så Windows ikke beder om gendannelsesnøglen.";
    public const string PowerWarning = "Strømmen må ikke gå under en BIOS-opdatering. Bærbare skal sidde i laderen.";
    public const string Staged = "BIOS-opdateringen er lagt klar. Den installeres, når maskinen genstartes. Sluk ikke for strømmen under genstarten; maskinen kan være væk i flere minutter.";
    public const string Interrupted = "Klargøringen blev afbrudt, før noget blev lagt klar. Intet er ændret.";
    public const string OtherPending = "En anden BIOS-opdatering venter allerede på en genstart.";
}

/// <summary>Kontrol af selve firmwarefilen (ROM): størrelse, versionskode og ESRT-GUID. Firmwaren tjekker desuden leverandørens signatur.</summary>
public static class BiosRom
{
    public const long MinSize = 1L << 20, MaxSize = 64L << 20;

    /// <summary>ROM-filen i en pakke: præcis én *.rom (eller den der hedder image*.rom, hvis der er flere). Null = ingen sikker fil.</summary>
    public static string? Pick(IEnumerable<string> names)
    {
        var roms = names.Where(n => !n.EndsWith('/') && n.EndsWith(".rom", StringComparison.OrdinalIgnoreCase)).ToList();
        if (roms.Count == 1) return roms[0];
        var image = roms.Where(n => Path.GetFileName(n.Replace('\\', '/')).StartsWith("image", StringComparison.OrdinalIgnoreCase)).ToList();
        return image.Count == 1 ? image[0] : null;
    }

    public static BiosCheck Check(ReadOnlySpan<byte> rom, string target, string? esrtGuid)
    {
        var body = HeaderSize(rom, esrtGuid) is int h ? rom[h..] : rom;   // en kapsels header tæller ikke med i størrelsen
        if (body.Length < MinSize || body.Length > MaxSize || body.Length % 4096 != 0)
            return BiosCheck.Fail("rom", $"Firmwarefilen har en usædvanlig størrelse ({rom.Length} byte).");
        if (target.Length < 4 || rom.IndexOf(Encoding.ASCII.GetBytes(target.ToUpperInvariant())) < 0)
            return BiosCheck.Fail("rom", $"Firmwarefilen indeholder ikke versionen {target}.");
        if (esrtGuid is { Length: > 0 } g && Guid.TryParse(g, out var guid) && rom.IndexOf(guid.ToByteArray()) < 0)
            return BiosCheck.Fail("rom", "Firmwarefilen passer ikke til maskinens firmware (ESRT-GUID mangler).");
        return BiosCheck.Ok("rom", $"Firmwarefilen indeholder {target}" + (esrtGuid is { Length: > 0 } ? " og maskinens ESRT-GUID." : "."));
    }

    /// <summary>EFI_FIRMWARE_MANAGEMENT_CAPSULE_ID_GUID (FMP-kapsel).</summary>
    public static readonly Guid FmpCapsule = Guid.Parse("6dcbd5ed-e82d-4c44-bda1-7194199ad92a");

    /// <summary>
    /// Er filen allerede en UEFI-kapsel (EFI_CAPSULE_HEADER med maskinens ESRT-GUID eller FMP-GUID'en forrest)? fwupd sætter ellers kun en
    /// tom header foran, og en rå ROM til leverandørens USB-værktøj er ikke det signerede kapselformat, firmwaren forventer.
    /// Indtil kapsel-vejen er afprøvet med en rå ROM, lægges kun rigtige kapsler klar.
    /// </summary>
    public static BiosCheck Capsule(ReadOnlySpan<byte> file, string? esrtGuid)
    {
        if (HeaderSize(file, esrtGuid) is not null) return BiosCheck.Ok("capsule", "Firmwarefilen er en UEFI-kapsel til maskinens firmware.");
        return BiosCheck.Fail("capsule",
            "Firmwarefilen er leverandørens rå ROM til USB-værktøjet, ikke en signeret UEFI-kapsel. Fleetdeck lægger den ikke klar inde fra Linux, før den vej er afprøvet. Brug USB-stikket. Intet er ændret.");
    }

    /// <summary>Størrelsen af en EFI_CAPSULE_HEADER (GUID, HeaderSize, Flags, CapsuleImageSize) forrest i filen, eller null.</summary>
    public static int? HeaderSize(ReadOnlySpan<byte> file, string? esrtGuid)
    {
        if (file.Length < 28) return null;
        var head = new Guid(file[..16]);
        var headerSize = BitConverter.ToUInt32(file.Slice(16, 4));
        var size = BitConverter.ToUInt32(file.Slice(24, 4));
        var known = head == FmpCapsule || esrtGuid is { Length: > 0 } g && Guid.TryParse(g, out var guid) && head == guid;
        return known && headerSize >= 28 && headerSize < file.Length && size == file.Length ? (int)headerSize : null;
    }

    /// <summary>
    /// Ligner starten af filen en EFI_CAPSULE_HEADER (GUID, HeaderSize, Flags, CapsuleImageSize = hele filen)? Serveren kender ikke maskinens
    /// ESRT-GUID, så her tjekkes kun opbygningen; agenten kontrollerer GUID'en, før noget lægges klar (Capsule).
    /// </summary>
    public static bool LooksLikeCapsule(ReadOnlySpan<byte> head, long length)
    {
        if (head.Length < 28 || length < 28) return false;
        if (new Guid(head[..16]) == Guid.Empty) return false;
        var headerSize = BitConverter.ToUInt32(head.Slice(16, 4));
        var size = BitConverter.ToUInt32(head.Slice(24, 4));
        return headerSize >= 28 && headerSize < length && size == length;
    }

    /// <summary>Indeholder pakken (zip) en rigtig UEFI-kapsel? false = rå ROM (fx Lenovos USB-pakke til ThinkCentre), null = ingen sikker ROM-fil.</summary>
    public static bool? ZipHasCapsule(string zip)
    {
        try
        {
            using var z = ZipFile.OpenRead(zip);
            if (Pick(z.Entries.Select(e => e.FullName)) is not { } name || z.GetEntry(name) is not { } e) return null;
            using var s = e.Open();
            var head = new byte[28];
            var n = 0;
            while (n < head.Length && s.Read(head, n, head.Length - n) is var r and > 0) n += r;
            return LooksLikeCapsule(head.AsSpan(0, n), e.Length);
        }
        catch (Exception x) when (x is IOException or InvalidDataException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Pak ROM-filen ud af en zip uden at stole på stierne i zip'en (kun ét kendt filnavn skrives).</summary>
    public static (string? Path, BiosCheck? Error) FromZip(string zip, string outDir)
    {
        using var z = ZipFile.OpenRead(zip);
        if (Pick(z.Entries.Select(e => e.FullName)) is not { } name) return (null, BiosCheck.Fail("rom", "Pakken indeholder ingen entydig firmwarefil (*.rom)."));
        var e = z.GetEntry(name)!;
        if (e.Length > MaxSize) return (null, BiosCheck.Fail("rom", "Firmwarefilen i pakken er for stor."));
        var dest = Path.Combine(outDir, "firmware.rom");
        e.ExtractToFile(dest, overwrite: true);
        return (dest, null);
    }
}

/// <summary>Pakken fra Fleetdeck: størrelse og SHA-256 lige før brug.</summary>
public static class BiosPackageCheck
{
    public static BiosCheck Check(string file, string? sha256, long? size)
    {
        if (!File.Exists(file)) return BiosCheck.Fail("package", "Pakken kunne ikke hentes fra Fleetdeck.");
        var len = new FileInfo(file).Length;
        if (size is long s && s > 0 && len != s) return BiosCheck.Fail("package", $"Pakken har en forkert størrelse ({len} i stedet for {s} byte).");
        if (sha256 is not { Length: 64 }) return BiosCheck.Fail("package", "Pakken har ingen kendt kontrolsum.");
        string got;
        using (var f = File.OpenRead(file)) got = Convert.ToHexStringLower(SHA256.HashData(f));
        return string.Equals(got, sha256, StringComparison.OrdinalIgnoreCase)
            ? BiosCheck.Ok("package", "Pakkens kontrolsum (SHA-256) passer.")
            : BiosCheck.Fail("package", "Pakkens kontrolsum (SHA-256) passer ikke. Intet er ændret.");
    }
}

/// <summary>fwupdmgr get-devices --json: systemfirmwarens kapsel-enhed.</summary>
public static class FwupdJson
{
    public const string Pending = "pending", Success = "success", Failed = "failed", NeedsReboot = "needs-reboot", FailedTransient = "failed-transient";

    public sealed record Device(string DeviceId, string Name, string? Guid, long? VersionRaw, long? VersionLowestRaw, bool Updatable,
        string? UpdateState, string? UpdateError, string? Version);

    /// <summary>Enheden fra plugin'et uefi_capsule, der er systemfirmwaren ("System Firmware" eller flaget internal). Null = ingen.</summary>
    public static Device? SystemFirmware(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); } catch (JsonException) { return null; }
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("Devices", out var devs) || devs.ValueKind != JsonValueKind.Array) return null;
            var all = devs.EnumerateArray().Where(d => Str(d, "Plugin") == "uefi_capsule").Select(Parse).OfType<(Device Dev, bool Internal)>().ToList();
            return all.FirstOrDefault(d => d.Dev.Name == "System Firmware").Dev ?? all.FirstOrDefault(d => d.Internal).Dev;
        }
    }

    private static (Device Dev, bool Internal)? Parse(JsonElement d)
    {
        if (Str(d, "DeviceId") is not { Length: > 0 } id) return null;
        var flags = d.TryGetProperty("Flags", out var f) && f.ValueKind == JsonValueKind.Array ? f.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];
        string? guid = null;
        if (d.TryGetProperty("Guid", out var g))
            guid = g.ValueKind == JsonValueKind.Array ? g.EnumerateArray().Select(x => x.GetString()).FirstOrDefault(x => x is { Length: > 0 })
                : g.ValueKind == JsonValueKind.String ? g.GetString() : null;
        var dev = new Device(id, Str(d, "Name") ?? "", guid?.ToLowerInvariant(), Num(d, "VersionRaw") ?? NumText(Str(d, "Version")),
            Num(d, "VersionLowestRaw") ?? NumText(Str(d, "VersionLowest")), flags.Contains("updatable"), State(d), Str(d, "UpdateError"), Str(d, "Version"));
        return (dev, flags.Contains("internal"));
    }

    private static string? State(JsonElement d)
    {
        if (!d.TryGetProperty("UpdateState", out var s)) return null;
        if (s.ValueKind == JsonValueKind.String) return s.GetString();
        return s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var n) ? n switch
        {
            1 => Pending, 2 => Success, 3 => Failed, 4 => NeedsReboot, 5 => FailedTransient, _ => null,
        } : null;
    }

    private static string? Str(JsonElement d, string n) => d.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static long? Num(JsonElement d, string n) =>
        d.TryGetProperty(n, out var v) ? v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var x) ? x : v.ValueKind == JsonValueKind.String ? NumText(v.GetString()) : null : null;

    /// <summary>"65653" eller "0x00010075" → tal; alt andet (fx "1.0.0.117") → null.</summary>
    public static long? NumText(string? s)
    {
        s = s?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h) ? h : null;
        return long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var d) ? d : null;
    }
}

/// <summary>ESRT (/sys/firmware/efi/esrt/entries/*, kun root): sidste forsøg pr. firmware-GUID.</summary>
public static class Esrt
{
    public const string Dir = "sys/firmware/efi/esrt/entries";

    public sealed record Entry(string Guid, long? Version, long? LastAttemptVersion, int? LastAttemptStatus);

    public static List<Entry> Read(FirmwareFs fs)
    {
        var l = new List<Entry>();
        if (!fs.DirExists(Dir)) return l;
        foreach (var e in fs.Dirs(Dir))
        {
            if (fs.Read($"{Dir}/{e}/fw_class")?.Trim().ToLowerInvariant() is not { Length: > 0 } g) continue;
            var st = FwupdJson.NumText(fs.Read($"{Dir}/{e}/last_attempt_status"));
            l.Add(new Entry(g, FwupdJson.NumText(fs.Read($"{Dir}/{e}/fw_version")), FwupdJson.NumText(fs.Read($"{Dir}/{e}/last_attempt_version")), st is long s ? (int)s : null));
        }
        return l;
    }

    /// <summary>UEFI-specifikationens koder for sidste forsøg.</summary>
    public static string StatusText(int s) => s switch
    {
        0 => "lykkedes",
        1 => "mislykkedes",
        2 => "ikke nok ressourcer",
        3 => "forkert version",
        4 => "ugyldigt format",
        5 => "signaturen blev afvist",
        6 => "strømforsyningen (lysnet)",
        7 => "batteriet var for lavt",
        _ => $"kode {s}",
    };
}

/// <summary>Små læsere til Linux-sider af sagen (testbare uden hardware).</summary>
public static class BiosLinux
{
    /// <summary>/sys/class/power_supply: lysnet tilsluttet og batteriniveau (batterier i mus og tastaturer, scope=Device, tæller ikke).</summary>
    public static (bool? OnAc, int? Battery) Power(FirmwareFs fs)
    {
        const string dir = "sys/class/power_supply";
        bool? ac = null; int? bat = null;
        if (!fs.DirExists(dir)) return (null, null);
        foreach (var n in fs.Dirs(dir))
        {
            var type = fs.Read($"{dir}/{n}/type")?.Trim();
            if (type is "Mains" or "USB" && fs.Read($"{dir}/{n}/online")?.Trim() is { } on) ac = ac == true || on == "1";
            if (type == "Battery" && fs.Read($"{dir}/{n}/scope")?.Trim() != "Device"
                && int.TryParse(fs.Read($"{dir}/{n}/capacity")?.Trim(), out var c)) bat = bat is int b ? Math.Min(b, c) : c;
        }
        return (ac, bat);
    }

    /// <summary>efibootmgr: "BootOrder: 0001,0000".</summary>
    public static string? BootOrder(string? efibootmgr) =>
        efibootmgr?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("BootOrder:", StringComparison.OrdinalIgnoreCase))?[10..].Trim();

    /// <summary>mokutil --sb-state: "SecureBoot enabled"/"SecureBoot disabled".</summary>
    public static bool? SecureBoot(string? mokutil) =>
        mokutil is null ? null : mokutil.Contains("SecureBoot enabled", StringComparison.OrdinalIgnoreCase) ? true
            : mokutil.Contains("SecureBoot disabled", StringComparison.OrdinalIgnoreCase) ? false : null;

    /// <summary>/etc/crypttab med tpm2-device (systemd-cryptenroll) eller clevis: disken låses op med TPM.</summary>
    public static bool? TpmUnlock(string? crypttab) =>
        crypttab is null ? null : crypttab.Split('\n').Select(l => l.Trim()).Any(l => !l.StartsWith('#') && (l.Contains("tpm2-device") || l.Contains("clevis")));
}

/// <summary>Det, agenten læser efter genstarten.</summary>
public sealed class BiosAfterBoot
{
    public string? BiosVersion { get; set; }
    public long? VersionRaw { get; set; }
    public string? FwupdState { get; set; }
    public string? FwupdError { get; set; }
    public int? EsrtStatus { get; set; }
    public long? EsrtAttemptVersion { get; set; }
    public string? BootOrder { get; set; }
}

/// <summary>Er opdateringen gået igennem? Rent opslag ud fra versionen, fwupd og ESRT.</summary>
public static class BiosVerify
{
    public static BiosUpdateReport Classify(BiosUpdateState s, BiosAfterBoot a)
    {
        var r = new BiosUpdateReport
        {
            RunId = s.RunId, Phase = BiosUpdatePhases.Verify, CurrentVersion = a.BiosVersion, TargetVersion = s.TargetVersion, DeviceId = s.DeviceId,
            VersionRaw = a.VersionRaw, TargetRaw = s.TargetRaw, FwupdState = a.FwupdState, FwupdError = a.FwupdError, EsrtStatus = a.EsrtStatus,
            EsrtAttemptVersion = a.EsrtAttemptVersion, StagedAt = s.StagedAt,
            BootOrderChanged = s.BootOrder is { Length: > 0 } b && a.BootOrder is { Length: > 0 } n ? b != n : null,
        };
        var atTarget = string.Equals(a.BiosVersion?.Trim(), s.TargetVersion, StringComparison.OrdinalIgnoreCase)
            || s.TargetRaw is long t && a.VersionRaw == t;
        // et forsøg ved denne genstart: ESRT har netop målversionen, eller ESRT har ændret sig siden klargøringen
        // (en afvist kapsel efterlader ofte 0 eller den gamle version i last_attempt_version)
        var attempted = a.EsrtStatus is int && (s.TargetRaw is long tr && a.EsrtAttemptVersion == tr
            || s.EsrtBaseline && (a.EsrtStatus != s.EsrtStatusBefore || a.EsrtAttemptVersion != s.EsrtAttemptBefore));
        if (atTarget)
        {
            r.Outcome = BiosUpdateOutcomes.Updated;
            r.Message = $"BIOS'en er opdateret til {s.TargetVersion}.";
            if (r.BootOrderChanged == true) r.Message += " Bootrækkefølgen er ændret; kontrollér den.";
        }
        else if (attempted && a.EsrtStatus is int st && st != 0)
        {
            r.Outcome = BiosUpdateOutcomes.Rejected;
            r.Message = $"Firmwaren afviste opdateringen ({Esrt.StatusText(st)}). BIOS'en er stadig {a.BiosVersion ?? s.FromVersion}.";
        }
        else if (a.FwupdState is FwupdJson.Failed or FwupdJson.FailedTransient)
        {
            r.Outcome = BiosUpdateOutcomes.Rejected;
            r.Message = $"Firmwaren afviste opdateringen{(a.FwupdError is { Length: > 0 } e ? $" ({e})" : "")}. BIOS'en er stadig {a.BiosVersion ?? s.FromVersion}.";
        }
        else if (a.FwupdState == FwupdJson.Pending)
        {
            // kapslen ligger der stadig: kørslen er ikke slut, og den installeres ved en senere genstart
            r.Outcome = BiosUpdateOutcomes.Staged;
            r.StillPending = true;
            r.Message = $"BIOS'en er stadig {a.BiosVersion ?? s.FromVersion}. Opdateringen blev ikke kørt ved genstarten (fx fordi maskinen blev slukket i stedet for genstartet), men den ligger stadig klar og installeres ved næste genstart.";
        }
        else if (string.Equals(a.BiosVersion?.Trim(), s.FromVersion, StringComparison.OrdinalIgnoreCase))
        {
            r.Outcome = BiosUpdateOutcomes.NotApplied;
            r.Message = $"BIOS'en er stadig {a.BiosVersion ?? s.FromVersion}. Opdateringen blev ikke kørt ved genstarten (fx fordi maskinen blev slukket i stedet for genstartet, eller BIOS'en ignorerede startvalget).";
        }
        else
        {
            r.Outcome = BiosUpdateOutcomes.Unknown;
            r.Message = $"BIOS'en er nu {a.BiosVersion ?? "ukendt"}, men Fleetdeck forventede {s.TargetVersion}. Kontrollér maskinen.";
        }
        return r;
    }
}

/// <summary>Kørslens tilstand på agenten. Gemmes på disken, så den overlever en genstart af agenten og af maskinen.</summary>
public sealed class BiosUpdateState
{
    public const string Staging = "staging", Staged = "staged";
    public int RunId { get; set; }
    public int JobId { get; set; }
    public string Phase { get; set; } = Staging;
    public string Route { get; set; } = "";
    public string? FromVersion { get; set; }
    public string TargetVersion { get; set; } = "";
    public long? TargetRaw { get; set; }
    public string? DeviceId { get; set; }
    public string? DeviceGuid { get; set; }
    public string? BootOrder { get; set; }
    /// <summary>ESRT's last_attempt_status/-version lige før installationen (så et gammelt forsøg ikke forveksles med dette).</summary>
    public int? EsrtStatusBefore { get; set; }
    public long? EsrtAttemptBefore { get; set; }
    public bool EsrtBaseline { get; set; }
    public DateTime StartedAt { get; set; }
    /// <summary>Installationstrinnet er startet (herefter kan der ligge noget klar, selv hvis agenten dør).</summary>
    public bool InstallStarted { get; set; }
    public DateTime? StagedAt { get; set; }
    public string? WorkDir { get; set; }
    /// <summary>En melding, serveren ikke har kvitteret for endnu.</summary>
    public BiosUpdateReport? Pending { get; set; }
}

/// <summary>bios-update.json i agentens beskyttede mappe (kun root/SYSTEM og administratorer). Skrives atomisk.</summary>
public sealed class BiosUpdateStore(string dir)
{
    private static readonly JsonSerializerOptions Opt = new(JsonSerializerDefaults.Web);
    public string Dir { get; } = dir;
    public string FilePath => Path.Combine(Dir, "bios-update.json");

    public BiosUpdateState? Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<BiosUpdateState>(File.ReadAllText(FilePath), Opt) : null; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public void Save(BiosUpdateState s)
    {
        Directory.CreateDirectory(Dir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, Opt));
        File.Move(tmp, FilePath, overwrite: true);
    }

    public void Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public sealed record BiosCmdResult(int ExitCode, string Output);

/// <summary>Kør et program. <paramref name="kill"/> = false: ved tidsudløb ventes der ikke længere, men programmet stoppes ikke (et flash-program må aldrig afbrydes).</summary>
public delegate Task<BiosCmdResult> BiosCmd(string file, IReadOnlyList<string> args, TimeSpan timeout, string? workDir, bool kill, CancellationToken ct);

public static class BiosProcess
{
    public const int NotFound = 127, TimedOut = -1;

    public static async Task<BiosCmdResult> Run(string file, IReadOnlyList<string> args, TimeSpan timeout, string? workDir, bool kill, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(file)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = workDir ?? "",
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        System.Diagnostics.Process p;
        try { p = System.Diagnostics.Process.Start(psi)!; }
        catch (System.ComponentModel.Win32Exception) { return new(NotFound, $"{file} findes ikke."); }
        using (p)
        {
            p.StandardInput.Close();   // ingen spørgsmål: et program, der spørger, får "nej"
            var outTask = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var errTask = p.StandardError.ReadToEndAsync(CancellationToken.None);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                if (kill) try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                ct.ThrowIfCancellationRequested();
                return new(TimedOut, $"{file}: tiden løb ud efter {timeout.TotalMinutes:0} minutter.");
            }
            var text = (await outTask + "\n" + await errTask).Trim();
            return new(p.ExitCode, text.Length > 20000 ? text[^20000..] : text);
        }
    }
}

/// <summary>Platformens del: se maskinen, læg opdateringen klar og læs resultatet efter genstarten.</summary>
public interface IBiosUpdater
{
    string Route { get; }
    /// <summary>Beskyttet arbejdsmappe (kun root/SYSTEM og administratorer).</summary>
    string WorkRoot { get; }
    Task<BiosProbe> Probe(BiosUpdateOrder o, CancellationToken ct);
    /// <summary>Pak ud, kontrollér filen og læg den klar. Kaldes først efter kopien og forudsætningerne, og aldrig med et token, der kan annulleres.</summary>
    Task<BiosStageResult> Stage(BiosUpdateOrder o, string package, string workDir, BiosProbe p, CancellationToken ct);
    Task<BiosAfterBoot> AfterBoot(BiosUpdateState s, CancellationToken ct);
    /// <summary>Ligger der en opdatering klar til næste genstart? Null = kan ikke ses.</summary>
    Task<bool?> Pending(BiosUpdateState s, CancellationToken ct);
    /// <summary>Hvornår maskinen sidst startede (UTC).</summary>
    DateTime? BootAt();
}

public sealed class BiosStageResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public string Output { get; set; } = "";
    public List<BiosCheck> Checks { get; set; } = [];
    /// <summary>Selve installationen blev startet (før det er intet ændret på maskinen).</summary>
    public bool Installed { get; set; }
    /// <summary>Programmet kørte færdig og meldte selv en fejl (ikke en timeout eller en undtagelse).</summary>
    public bool ToolFailed { get; set; }
}

/// <summary>Det, jobbet skal bruge fra serveren: melde (kopi og resultat) og hente pakken.</summary>
public sealed record BiosUpdateIo(
    Func<BiosUpdatePost, CancellationToken, Task<BiosUpdatePostResponse?>> Post,
    Func<string, string, CancellationToken, Task> Download);

/// <summary>bios_update på agenten (fælles for Linux og Windows).</summary>
public static class BiosUpdateJob
{
    private static int _active;
    /// <summary>Et bios_update-job kører lige nu i denne proces (så genoptagelsen ikke blander sig).</summary>
    public static bool Active => Volatile.Read(ref _active) > 0;

    public static async Task<JobResult> Run(AgentJob j, IBiosUpdater? up, IBiosSettingsBackend? settings, BiosUpdateIo io, BiosUpdateStore store,
        CancellationToken ct, Func<DateTime>? clock = null)
    {
        var now = clock ?? (() => DateTime.UtcNow);
        if (j.Params.BiosUpdate is not { } o) return Result(JobStatus.Failed, null, "Jobbet mangler oplysninger om BIOS-opdateringen.");
        if (up is null || up.Route != o.Route)
            return Result(JobStatus.Failed, Report(o, BiosUpdateOutcomes.Blocked, BiosUpdateText.NotSupported, [BiosCheck.Fail("route", BiosUpdateText.NotSupported)]),
                BiosUpdateText.NotSupported);
        Interlocked.Increment(ref _active);
        try
        {
            return o.Phase switch
            {
                BiosUpdatePhases.Check => await Check(o, up, settings, store, ct),
                BiosUpdatePhases.Stage => await Stage(j, o, up, settings, io, store, now, ct),
                BiosUpdatePhases.Verify => await VerifyNow(o, up, store, ct),
                _ => Result(JobStatus.Failed, null, $"Ukendt trin: {o.Phase}"),
            };
        }
        finally { Interlocked.Decrement(ref _active); }
    }

    private static async Task<BiosProbe> Probe(BiosUpdateOrder o, IBiosUpdater up, IBiosSettingsBackend? settings, CancellationToken ct)
    {
        var p = await up.Probe(o, ct);
        if (settings is not null)
        {
            var info = await Task.Run(settings.Info, ct);
            p.SettingsReadable = true;
            if (info.PendingReboot is bool pending) p.PendingSettings = pending || p.PendingSettings == true;
            p.AdminPassword |= info.AdminPassword;
        }
        return p;
    }

    private static async Task<JobResult> Check(BiosUpdateOrder o, IBiosUpdater up, IBiosSettingsBackend? settings, BiosUpdateStore store, CancellationToken ct)
    {
        LiveJob.Phase(0, 100, "Kontrollerer forudsætningerne");
        var p = await Probe(o, up, settings, ct);
        var checks = BiosPreconditions.Evaluate(o, p, null, stage: false);
        if (store.Load() is { } other && other.RunId != o.RunId) checks.Add(BiosCheck.Fail("other", BiosUpdateText.OtherPending));
        var blocked = BiosPreconditions.Blocked(checks);
        var msg = blocked ? "BIOS'en kan ikke opdateres lige nu. Se forudsætningerne." : "Alle forudsætninger er opfyldt.";
        var rep = Report(o, blocked ? BiosUpdateOutcomes.Blocked : BiosUpdateOutcomes.Checked, msg, checks, p);
        return Result(JobStatus.Success, rep, msg + "\n\n" + Lines(checks));
    }

    private static async Task<JobResult> Stage(AgentJob j, BiosUpdateOrder o, IBiosUpdater up, IBiosSettingsBackend? settings, BiosUpdateIo io,
        BiosUpdateStore store, Func<DateTime> now, CancellationToken ct)
    {
        if (store.Load() is { } existing && (existing.RunId != o.RunId || existing.Phase == BiosUpdateState.Staged))
            return Blocked(o, [BiosCheck.Fail("other", BiosUpdateText.OtherPending)], null);
        if (o.PackageUrl is not { Length: > 0 }) return Result(JobStatus.Failed, null, "Jobbet mangler pakken.");
        var work = Path.Combine(up.WorkRoot, "run-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)));
        Directory.CreateDirectory(work);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(work, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var st = new BiosUpdateState { RunId = o.RunId, JobId = j.Id, Route = o.Route, TargetVersion = o.TargetVersion, FromVersion = o.FromVersion, StartedAt = now(), WorkDir = work };
        store.Save(st);
        var checks = new List<BiosCheck>();
        try
        {
            // 1. kopien af indstillingerne – ingen kopi i Fleetdeck, ingen opdatering
            LiveJob.Phase(0, 15, "Tager en kopi af BIOS-indstillingerne");
            if (settings is null) return Abort(store, st, Blocked(o, [BiosCheck.Fail("snapshot", BiosUpdateText.NoSettings)], null));
            var rep = await Task.Run(settings.Read, ct);
            BiosUpdatePostResponse? saved;
            try { saved = await io.Post(new BiosUpdatePost { RunId = o.RunId, JobId = j.Id, Kind = BiosUpdatePostKinds.Snapshot, Settings = rep }, ct); }
            catch (HttpRequestException) { saved = null; }
            int? count = saved?.Ok == true ? rep.Items.Count : null;

            // 2. forudsætningerne
            LiveJob.Phase(15, 25, "Kontrollerer forudsætningerne");
            var p = await Probe(o, up, settings, ct);
            checks.AddRange(BiosPreconditions.Evaluate(o, p, count, stage: true));
            if (BiosPreconditions.Blocked(checks)) return Abort(store, st, Blocked(o, checks, p));

            // 3. pakken fra Fleetdeck, kontrolleret lige før brug
            LiveJob.Phase(25, 55, "Henter BIOS-pakken fra Fleetdeck");
            var file = Path.Combine(work, SafeName(o.FileName));
            try { await io.Download(o.PackageUrl!, file, ct); }
            catch (HttpRequestException e) { checks.Add(BiosCheck.Fail("package", $"Pakken kunne ikke hentes fra Fleetdeck ({e.Message}).")); return Abort(store, st, Blocked(o, checks, p)); }
            checks.Add(BiosPackageCheck.Check(file, o.Sha256, o.Size));
            if (BiosPreconditions.Blocked(checks)) return Abort(store, st, Blocked(o, checks, p));

            st.FromVersion = p.BiosVersion; st.DeviceId = p.DeviceId; st.DeviceGuid = p.DeviceGuid; st.BootOrder = p.BootOrder;
            st.TargetRaw = LenovoBiosCode.TargetRaw(p.BiosVersion, p.VersionRaw, o.TargetVersion);
            try
            {
                var esrt = await up.AfterBoot(st, ct);
                st.EsrtStatusBefore = esrt.EsrtStatus; st.EsrtAttemptBefore = esrt.EsrtAttemptVersion; st.EsrtBaseline = true;
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException) { }
            st.InstallStarted = true;
            store.Save(st);

            // 4. læg klar – herfra kan jobbet ikke annulleres
            LiveJob.Phase(55, 95, "Lægger BIOS-opdateringen klar. Sluk ikke for strømmen.");
            var sr = await up.Stage(o, file, work, p, CancellationToken.None);
            checks.AddRange(sr.Checks);
            if (!sr.Ok)
            {
                if (!sr.Installed) return Abort(store, st, Blocked(o, checks, p, sr.Message, sr.Output));
                var rec = await Recover(up, st, now, CancellationToken.None, toolFailed: sr.ToolFailed);
                rec.Checks = checks;
                rec.Message = sr.Message + " " + rec.Message;
                if (rec.Outcome != BiosUpdateOutcomes.Staged) Cleanup(store, st); else store.Save(st);
                return Result(rec.Outcome == BiosUpdateOutcomes.Staged ? JobStatus.Success : JobStatus.Failed, rec, rec.Message + "\n\n" + sr.Output);
            }
            st.Phase = BiosUpdateState.Staged;
            st.StagedAt = now();
            store.Save(st);
            var ok = Report(o, BiosUpdateOutcomes.Staged, BiosUpdateText.Staged, checks, p);
            ok.StagedAt = st.StagedAt; ok.TargetRaw = st.TargetRaw;
            return Result(JobStatus.Success, ok, BiosUpdateText.Staged + "\n\n" + Lines(checks) + (sr.Output.Length > 0 ? "\n\n" + sr.Output : ""));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && !st.InstallStarted)
        {
            Cleanup(store, st);
            return Result(JobStatus.Cancelled, Report(o, BiosUpdateOutcomes.Failed, "Annulleret fra Fleetdeck. Intet er ændret.", checks), "Annulleret fra Fleetdeck. Intet er ændret.");
        }
        catch (Exception e)
        {
            if (!st.InstallStarted)
            {
                Cleanup(store, st);
                var msg = BiosUpdateText.Interrupted + $" ({e.Message})";
                return Result(JobStatus.Failed, Report(o, BiosUpdateOutcomes.Failed, msg, checks), msg);
            }
            var rec = await Recover(up, st, now, CancellationToken.None);
            rec.Checks = checks;
            if (rec.Outcome != BiosUpdateOutcomes.Staged) Cleanup(store, st); else store.Save(st);
            return Result(rec.Outcome == BiosUpdateOutcomes.Staged ? JobStatus.Success : JobStatus.Failed, rec, rec.Message + "\n\n" + e.Message);
        }
    }

    /// <summary>
    /// Efter et afbrudt installationstrin: ligger der noget klar? Ja (eller kan ikke ses) → behandles som klar, så resultatet kontrolleres
    /// efter næste genstart. Nej → mislykkedes, intet ændret.
    /// </summary>
    public static async Task<BiosUpdateReport> Recover(IBiosUpdater up, BiosUpdateState st, Func<DateTime> now, CancellationToken ct, bool toolFailed = false)
    {
        bool? pending;
        try { pending = st.InstallStarted ? await up.Pending(st, ct) : false; }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException) { pending = null; }
        var r = new BiosUpdateReport { RunId = st.RunId, Phase = BiosUpdatePhases.Stage, TargetVersion = st.TargetVersion, CurrentVersion = st.FromVersion, DeviceId = st.DeviceId, TargetRaw = st.TargetRaw };
        // programmet meldte selv fejl: kun hvis der beviseligt ligger noget klar, behandles det som klar
        if (pending == false || toolFailed && pending != true)
        {
            r.Outcome = BiosUpdateOutcomes.Failed;
            r.Message = toolFailed ? "Opdateringen er ikke lagt klar." : BiosUpdateText.Interrupted;
            return r;
        }
        st.Phase = BiosUpdateState.Staged;
        st.StagedAt ??= now();
        r.Outcome = BiosUpdateOutcomes.Staged;
        r.StagedAt = st.StagedAt;
        r.Message = pending == true
            ? toolFailed ? "Der ligger alligevel en opdatering klar til næste genstart." : "Klargøringen blev afbrudt, men opdateringen ligger klar. Den installeres ved næste genstart."
            : "Klargøringen blev afbrudt. Fleetdeck kan ikke se, om opdateringen ligger klar; den kan blive installeret ved næste genstart. Resultatet kontrolleres efter genstarten.";
        return r;
    }

    private static async Task<JobResult> VerifyNow(BiosUpdateOrder o, IBiosUpdater up, BiosUpdateStore store, CancellationToken ct)
    {
        LiveJob.Phase(0, 100, "Kontrollerer BIOS-versionen");
        var st = store.Load() is { } s && s.RunId == o.RunId ? s
            : new BiosUpdateState { RunId = o.RunId, Phase = BiosUpdateState.Staged, Route = o.Route, FromVersion = o.FromVersion, TargetVersion = o.TargetVersion };
        if (st.Phase == BiosUpdateState.Staged && st.StagedAt is DateTime at && up.BootAt() is DateTime boot && boot <= at)
        {
            var wait = new BiosUpdateReport { RunId = o.RunId, Phase = BiosUpdatePhases.Verify, Outcome = BiosUpdateOutcomes.Staged, StagedAt = at, TargetVersion = o.TargetVersion,
                Message = "Maskinen er ikke genstartet, siden opdateringen blev lagt klar. Den venter stadig." };
            return Result(JobStatus.Success, wait, wait.Message);
        }
        var rep = BiosVerify.Classify(st, await up.AfterBoot(st, ct));
        rep.BootAt = up.BootAt();
        if (rep.StillPending)
        {
            // venter stadig: næste genstart skal kontrolleres igen
            st.StagedAt = rep.StagedAt = DateTime.UtcNow;
            if (store.Load() is { } cur && cur.RunId == o.RunId) { cur.StagedAt = st.StagedAt; store.Save(cur); }
            return Result(JobStatus.Success, rep, rep.Message);
        }
        if (store.Load() is { } mine && mine.RunId == o.RunId) Cleanup(store, mine);
        return Result(rep.Outcome == BiosUpdateOutcomes.Updated ? JobStatus.Success : JobStatus.Failed, rep, rep.Message);
    }

    /// <summary>
    /// Kaldes med jævne mellemrum (og lige efter start): send en melding, serveren ikke har fået; meld et afbrudt job; og efter genstarten:
    /// kontrollér resultatet. Gør intet, mens et bios_update-job kører. Giver true, når der blev sendt noget.
    /// </summary>
    public static async Task<bool> Resume(IBiosUpdater up, BiosUpdateIo io, BiosUpdateStore store, CancellationToken ct, Func<DateTime>? clock = null)
    {
        var now = clock ?? (() => DateTime.UtcNow);
        if (Active || store.Load() is not { } st) return false;
        if (st.Pending is null)
        {
            if (st.Phase == BiosUpdateState.Staging)
            {
                // agenten (eller maskinen) blev genstartet midt i klargøringen
                var rec = await Recover(up, st, now, ct);
                st.Pending = rec;
            }
            else if (st.StagedAt is DateTime at && up.BootAt() is DateTime boot && boot > at)
            {
                var rep = BiosVerify.Classify(st, await up.AfterBoot(st, ct));
                rep.BootAt = boot;
                if (rep.StillPending) st.StagedAt = rep.StagedAt = now();   // kontrolleres igen efter næste genstart
                st.Pending = rep;
            }
            else return false;
            store.Save(st);
        }
        BiosUpdatePostResponse? res;
        try { res = await io.Post(new BiosUpdatePost { RunId = st.RunId, Kind = BiosUpdatePostKinds.Report, Report = st.Pending }, ct); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException) { return false; }
        if (res is null) return false;   // prøv igen senere – meldingen ligger på disken
        // serveren har svaret (også hvis den ikke kender kørslen længere): færdig med denne melding
        if (st.Pending!.Outcome == BiosUpdateOutcomes.Staged) { st.Pending = null; store.Save(st); }
        else Cleanup(store, st);
        return true;
    }

    /// <summary>Genoptagelsen i baggrunden: 20 sekunder efter start og derefter hvert andet minut.</summary>
    public static async Task ResumeLoop(Func<IBiosUpdater?> updater, BiosUpdateIo io, BiosUpdateStore store, Action<string> log, CancellationToken stop)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20), stop); } catch (OperationCanceledException) { return; }
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(store.FilePath) && updater() is { } up && await Resume(up, io, store, stop)) log("BIOS-opdatering: status sendt til Fleetdeck");
            }
            catch (Exception e) when (e is not OperationCanceledException) { log($"BIOS-opdatering: {e.Message}"); }
            try { await Task.Delay(TimeSpan.FromMinutes(2), stop); } catch (OperationCanceledException) { return; }
        }
    }

    private static JobResult Abort(BiosUpdateStore store, BiosUpdateState st, JobResult r) { Cleanup(store, st); return r; }

    private static void Cleanup(BiosUpdateStore store, BiosUpdateState st)
    {
        store.Clear();
        if (st.WorkDir is { Length: > 0 } w)
            try { if (Directory.Exists(w)) Directory.Delete(w, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string SafeName(string? n)
    {
        var s = new string((Path.GetFileName(n ?? "") ?? "").Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_').ToArray()).TrimStart('.');
        return s.Length is > 0 and <= 100 ? s : "package.bin";
    }

    private static JobResult Blocked(BiosUpdateOrder o, List<BiosCheck> checks, BiosProbe? p, string? msg = null, string? output = null)
    {
        msg ??= "BIOS'en blev ikke opdateret. Se forudsætningerne. Intet er ændret.";
        return Result(JobStatus.Failed, Report(o, BiosUpdateOutcomes.Blocked, msg, checks, p), msg + "\n\n" + Lines(checks) + (output is { Length: > 0 } ? "\n\n" + output : ""));
    }

    private static BiosUpdateReport Report(BiosUpdateOrder o, string outcome, string msg, List<BiosCheck> checks, BiosProbe? p = null) => new()
    {
        RunId = o.RunId, Phase = o.Phase, Outcome = outcome, Message = msg, Checks = checks, TargetVersion = o.TargetVersion,
        CurrentVersion = p?.BiosVersion, DeviceId = p?.DeviceId, VersionRaw = p?.VersionRaw, FwupdState = p?.UpdateState,
    };

    private static JobResult Result(string status, BiosUpdateReport? r, string output) => new()
    {
        Status = status, ExitCode = status == JobStatus.Success ? 0 : 1, Output = output, BiosUpdate = r,
    };

    public static string Lines(IEnumerable<BiosCheck> checks) =>
        string.Join("\n", checks.Select(c => $"{(c.State == BiosCheckStates.Ok ? "OK " : c.State == BiosCheckStates.Warn ? "OBS" : "NEJ")}  {c.Text}"));
}
