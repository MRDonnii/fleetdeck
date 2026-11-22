using Fleetdeck.Server.Data;

namespace Fleetdeck.Server.Services;

public sealed record GuideLink(string Label, string Url);
public sealed record GuideStep(string Key, string Title, string[] Lines, string? Note, GuideLink[] Links, bool Auto = false);
/// <summary>Et trin med sin tilstand. Source: "auto" = registreret af Fleetdeck, "manual" = markeret af en voksen (aldrig kontrolleret), null = ikke gjort.</summary>
public sealed record GuideStepState(string Key, string Title, string[] Lines, string? Note, GuideLink[] Links, bool Auto, bool Done, string? Source);

/// <summary>
/// Tjeklisten pr. barn. Navnene følger Apples danske vejledninger til iPadOS 27 (support.apple.com, læst november 2026).
/// Apples vejledninger nævner hverken "VPN og DNS" under ændringer af indstillinger eller en spærring af at slette profiler,
/// så teksterne lover ikke, at Skærmtid kan låse profilen.
/// </summary>
public static class IpadSetupGuide
{
    public const string Profile = "profile", ScreenTime = "screentime", Accounts = "accounts", Family = "family";
    private const string Child = "Indstillinger → Familie → barnets navn";

    public static readonly GuideStep[] Steps =
    [
        new(Profile, "Installér DNS-profilen",
            ["Lav QR-koden under Opsætning, og scan den med iPad'ens Kamera-app.",
             "Tryk på Tillad, når Safari spørger.",
             "Åbn Indstillinger → Profil hentet → Installér, og skriv iPad'ens kode."],
            "Kun en administrator kan lave QR-koden. Trinnet bliver flueben af sig selv, når iPad'en bruger profilen.", [], Auto: true),
        new(ScreenTime, "Lås Skærmtid med en kode, barnet ikke kender",
            [$"På din egen enhed: {Child} → Kode og administration → Lås indstillinger til Skærmtid.",
             "Skriv en firecifret kode, som barnet ikke kan gætte. Undgå fødselsdage og 1234.",
             $"{Child} → Indhold og anonymitet → Tillad ændringer af indstillinger.",
             "Står VPN og DNS på listen, så vælg Tillad ikke."],
            "Apples vejledning nævner ikke VPN og DNS, og listen afhænger af iPadOS-versionen. Findes punktet ikke på din iPad, kan det ikke spærres med Skærmtid.",
            [new("Apple: Kode til Skærmtid", "https://support.apple.com/da-dk/guide/child-safety/ty3v2rlzjie5/web"),
             new("Apple: Bloker ændringer af indstillinger", "https://support.apple.com/da-dk/guide/child-safety/nzglr8yl0hwv/web")]),
        new(Accounts, "Spær ændring af konti, og kend grænsen for profilen",
            [$"{Child} → Indhold og anonymitet → Tillad ændringer af indstillinger.",
             "Bloker ændringer af Konti og af Face ID og kode."],
            "Apple beskriver ingen indstilling i Skærmtid, der forhindrer sletning af profiler. Profilen kan stadig fjernes under Indstillinger → Generelt → VPN og Administration af enhed. "
            + "Derfor advarer Fleetdeck, når styringen ikke bruges. Kun en overvåget iPad med den låste profil kan låses helt.",
            [new("Apple: Bloker ændringer af indstillinger", "https://support.apple.com/da-dk/guide/child-safety/nzglr8yl0hwv/web"),
             new("Apple: Gennemse og slet profiler", "https://support.apple.com/da-dk/guide/personal-safety/ips327569a75/web")]),
        new(Family, "Familiedeling med børnekonto",
            ["Barnet skal have sin egen børnekonto i din Familiedeling. Vælg Opret en ny børnekonto, når iPad'en sættes op.",
             $"Tid pr. app: {Child} → Tidsgrænser → Tilføj.",
             $"Nedetid hedder nu Tidsplan for skærmtid: {Child} → Tidsplan for skærmtid → slå Tidsplan til → Tilføj tidsplan.",
             $"Spørg før køb: {Child} → Apps og websteder → Begrænsninger → Spørg før køb → slå Kræv købsgodkendelse til."],
            "Navnene gælder iPadOS 27. På ældre versioner kan punkterne hedde noget andet.",
            [new("Apple: Indstil en iPad til dit barn", "https://support.apple.com/da-dk/guide/child-safety/grzxvj0yrbnm/web"),
             new("Apple: Tidsplaner og tidsgrænser", "https://support.apple.com/da-dk/guide/child-safety/azgjjn208bw9/web"),
             new("Apple: Børnesikring og Spørg før køb", "https://support.apple.com/da-dk/105121")]),
    ];

    /// <summary>Tjeklisten gælder børns iPads og iPhones.</summary>
    public static bool Applies(Ipad p) => DeviceTypes.IsIos(p.DeviceType) && p.Kind == IpadKinds.Child;

    public static GuideStepState[] For(Ipad p) => !Applies(p) ? [] : [.. Steps.Select(s =>
    {
        var done = s.Auto ? p.ProfileSeen is not null : p.SetupDone.Contains(s.Key);
        return new GuideStepState(s.Key, s.Title, s.Lines, s.Note, s.Links, s.Auto, done, !done ? null : s.Auto ? "auto" : "manual");
    })];

    /// <summary>Markerer et trin. Falsk for ukendte trin og for det trin, Fleetdeck selv registrerer.</summary>
    public static bool Mark(Ipad p, string key, bool done)
    {
        if (Steps.FirstOrDefault(s => s.Key == key) is not { Auto: false }) return false;
        p.SetupDone = done ? [.. p.SetupDone.Append(key).Distinct()] : [.. p.SetupDone.Where(x => x != key)];
        return true;
    }
}
