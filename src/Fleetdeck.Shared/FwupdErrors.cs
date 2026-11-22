using System.Text.RegularExpressions;

namespace Fleetdeck.Shared;

/// <summary>Særlige grunde, agenten kan melde med et jobresultat (<see cref="JobResult.Reason"/>), så serveren kan handle på dem.</summary>
public static class JobReasons
{
    /// <summary>Firmwaren afviser opdateringen (Secure Boot-nøgle). Hjælper ikke at prøve igen fra Linux.</summary>
    public const string FirmwareRejected = "firmware_rejected";

    /// <summary>Grunde, der betyder "kan ikke installeres": serveren markerer opdateringen og foreslår den ikke igen.</summary>
    public static readonly string[] CannotInstall = [FirmwareRejected];

    public static bool IsCannotInstall(string? reason) => reason is not null && CannotInstall.Contains(reason);
}

/// <summary>
/// fwupd-fejl, der ikke kan løses ved at prøve igen. Agenten kører allerede fwupdmgr med LC_ALL=C.UTF-8 (Shell.Run), men fejlen
/// kommer fra fwupd-dæmonen (systemtjenesten), som skriver på systemets sprog: strerror og GIO's "Error writing to file
/// descriptor" oversættes – fx "Fejl under skrivning til fildeskriptor: Adgang nægtet".
/// Derfor bygger mønstrene på de dele, der ikke oversættes (efivarsfs, KEK, dbx …), og kender både den engelske og den danske tekst.
/// Kilder (fwupd 2.1.6): libfwupdplugin/fu-efivars-linux.c "failed to write data to efivarsfs: ", plugins/uefi-dbx
/// "System firmware cannot accept DBX updates". Kernen (efivarfs) oversætter UEFI's EFI_SECURITY_VIOLATION til EACCES
/// ("Permission denied") og EFI_WRITE_PROTECTED til EROFS ("Read-only file system").
/// </summary>
public static class FwupdErrors
{
    public const string RejectedText =
        "Firmwaren afviser opdateringen (Secure Boot-nøgle). Den kan ikke installeres fra Linux – kræver en BIOS-opdatering fra producenten. Maskinen starter fint uden.";

    private const RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // skrivning til en UEFI-variabel, som firmwaren nægter (EACCES/EPERM/EROFS) – på engelsk og dansk
    private static readonly Regex Efivars = new(@"efivars?fs", O);
    private static readonly Regex Denied = new(
        @"permission denied|adgang nægtet|operation not permitted|operationen er ikke tilladt|read-only file system|filsystem med kun læseadgang|\bEACCES\b|\bEPERM\b|\bEROFS\b", O);
    // firmwaren tager ikke imod dbx-opdateringer (fwupd's uefi-dbx-plugin)
    private static readonly Regex NoDbx = new(@"system firmware cannot accept dbx updates", O);
    // signatur/sikkerhedsbrud på en Secure Boot-database (KEK, db, dbx, PK) i samme linje. Kun fejlformuleringer – ikke fx
    // "signature database" i en beskrivelse, så en fejl af en anden grund (netværk …) ikke bliver til "kan ikke installeres"
    private static readonly Regex SecureBootVar = new(@"\b(KEK|db|dbx|PK)\b|secure ?boot", O);
    private static readonly Regex Signature = new(
        @"security violation|sikkerhedsbrud|not signed|ikke signeret|invalid signature|ugyldig signatur|signature (is )?(invalid|not valid|verification failed|check failed)|signaturen er ugyldig", O);

    /// <summary>Grunden (<see cref="JobReasons"/>), hvis en fejlet fwupdmgr-kørsel ikke kan lykkes ved at prøve igen; ellers null.</summary>
    public static string? Classify(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        foreach (var raw in output.Split('\n'))
        {
            var l = raw.Trim();
            if (Efivars.IsMatch(l) && Denied.IsMatch(l)) return JobReasons.FirmwareRejected;
            if (NoDbx.IsMatch(l)) return JobReasons.FirmwareRejected;
            if (SecureBootVar.IsMatch(l) && Signature.IsMatch(l)) return JobReasons.FirmwareRejected;
        }
        return null;
    }

    /// <summary>Forklaringen til brugeren for en grund.</summary>
    public static string Text(string reason) => reason switch
    {
        JobReasons.FirmwareRejected => RejectedText,
        _ => "Opdateringen kan ikke installeres på denne maskine.",
    };
}
