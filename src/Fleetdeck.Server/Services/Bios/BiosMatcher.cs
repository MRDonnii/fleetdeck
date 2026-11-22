using System.Text.Json;
using System.Text.RegularExpressions;
using Fleetdeck.Server.Data;
using Fleetdeck.Shared;

namespace Fleetdeck.Server.Services.Bios;

/// <summary>Det, matcheren ved om Lenovos katalog: modellerne, de hentede pakke-XML'er/readmes (efter filstamme) og om hentningen fejler.</summary>
public sealed class LenovoCatalogView
{
    public List<LenovoModel> Models { get; init; } = [];
    public Dictionary<string, LenovoPackageDetails> Packages { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTime? FetchedAt { get; init; }
    public string? Error { get; init; }
    public bool Loaded => FetchedAt is not null;
}

public sealed record LenovoPackageDetails(LenovoPackage Package, List<BiosChange> Changes);

/// <summary>Resultatet for én maskine: hvilken model i kataloget og hvilken BIOS.</summary>
public sealed record LenovoMatch(string Quality, LenovoModel? Model, string? Detail);

/// <summary>
/// Beregner BIOS-status for en maskine ud fra agentens fakta, leverandørens katalog og fwupd. Ren logik uden database og netværk.
/// Et usikkert match viser aldrig en opdatering, og en lavere version vises aldrig som "nyere".
/// </summary>
public static partial class BiosMatcher
{
    public const string Lenovo = "lenovo", Dell = "dell", Hp = "hp", None = "none";
    public const string Exact = "exact", Fallback = "fallback", Uncertain = "uncertain";

    public static string VendorOf(SystemFacts f)
    {
        var v = $"{f.BiosVendor} {f.Manufacturer}".ToLowerInvariant();
        if (v.Contains("lenovo")) return Lenovo;
        if (v.Contains("dell")) return Dell;
        if (Regex.IsMatch(v, @"\bhp\b|hewlett")) return Hp;
        return None;
    }

    /// <summary>SMBIOS-versionen; ældre agenter sender kun "VER (yyyy-MM-dd)" i Bios-feltet.</summary>
    public static string? CurrentVersion(SystemFacts f) =>
        BiosFacts.Clean(f.BiosVersion) ?? (BiosFacts.Clean(f.Bios) is { } b ? DateSuffix().Replace(b, "").Trim() is { Length: > 0 } s ? s : null : null);

    /// <summary>Lenovos maskintype (MTM, 4 tegn) fra product_name ("10T7S0XX00") eller SKU ("LENOVO_MT_10T7_BU_…").</summary>
    public static string? Mtm(SystemFacts f)
    {
        if (f.ProductName is { } pn && MtmName().Match(pn.Trim()) is { Success: true } a) return a.Groups[1].Value.ToUpperInvariant();
        if (f.Model is { } md && MtmName().Match(md.Trim()) is { Success: true } b) return b.Groups[1].Value.ToUpperInvariant();
        if (f.Sku is { } sku && MtmSku().Match(sku) is { Success: true } c) return c.Groups[1].Value.ToUpperInvariant();
        return null;
    }

    /// <summary>Find modellen i Lenovos katalog: BIOS-billedpræfikset er den sikre nøgle; maskintype og modelnavn bekræfter den.</summary>
    public static LenovoMatch MatchLenovo(SystemFacts f, LenovoBiosVersion current, IReadOnlyList<LenovoModel> models)
    {
        var mtm = Mtm(f);
        var name = BiosFacts.Clean(f.ProductVersion);
        var withBios = models.Where(m => m.Bios is not null).ToList();
        var byImage = current.Image is { } img ? withBios.Where(m => m.Bios!.Image == img).ToList() : [];
        var byMtm = mtm is null ? [] : withBios.Where(m => m.Types.Contains(mtm)).ToList();
        var byName = name is null ? [] : withBios.Where(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();

        if (byImage.Count == 0)
        {
            if (byMtm.Count > 0 || byName.Count > 0)
                return new(Uncertain, null, $"BIOS-familien ({current.Image?.ToUpperInvariant() ?? "ukendt"}) passer ikke til modellen i Lenovos katalog.");
            return new(Uncertain, null, "Modellen findes ikke i Lenovos katalog.");
        }
        if (byImage.Select(m => m.Bios!.Version).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            return new(Uncertain, null, "Lenovos katalog har flere forskellige BIOS'er til samme BIOS-familie.");

        var confirmed = byImage.Where(m => byMtm.Contains(m) || byName.Contains(m)).ToList();
        if (confirmed.Count > 0) return new(Exact, confirmed[0], null);
        if (byMtm.Count > 0 || byName.Count > 0)
            return new(Uncertain, null, "Maskintypen og BIOS-familien peger på forskellige modeller i Lenovos katalog.");
        return new(Fallback, byImage[0], null);
    }

    /// <summary>Beregn status. <paramref name="fwupdVersion"/>: den version, fwupd/LVFS tilbyder for "System Firmware", ellers null.</summary>
    public static MachineBiosStatus Evaluate(int machineId, string platform, SystemFacts f, LenovoCatalogView? lenovo, string? fwupdVersion, DateTime now)
    {
        var s = new MachineBiosStatus { MachineId = machineId, CheckedAt = now, Source = "facts", Vendor = VendorOf(f), FwupdVersion = fwupdVersion };
        var current = CurrentVersion(f);
        s.Current = Clip(current, 64);
        s.CurrentDate = f.BiosDate;

        if (platform == Platforms.Mac || (f.Manufacturer ?? "").StartsWith("Apple", StringComparison.OrdinalIgnoreCase))
            return Set(s, BiosStates.Mac, "Firmware opdateres sammen med macOS.");
        if (f.PiSerial is not null || (f.Manufacturer ?? "").StartsWith("Raspberry", StringComparison.OrdinalIgnoreCase))
            return Set(s, BiosStates.Pi, "Bootloaderen opdateres under Firmware.");
        if (f.IsVirtual == true || (f.IsVirtual is null && BiosFacts.Virtual(f.Manufacturer, f.Model, f.BiosVendor)))
            return Set(s, BiosStates.Virtual, "Virtuel maskine. BIOS styres af værten.");
        if (current is null)
            return Set(s, BiosStates.Unchecked, "Ikke tjekket endnu. Agenten har ikke sendt BIOS-oplysninger.");

        if (s.Vendor != Lenovo)
        {
            if (fwupdVersion is not null) { s.Latest = Clip(fwupdVersion, 64); s.Source = "fwupd"; return Set(s, BiosStates.Fwupd, "Kan opdateres med fwupd."); }
            var who = s.Vendor switch { Dell => "Dell", Hp => "HP", _ => BiosFacts.Clean(f.BiosVendor) ?? BiosFacts.Clean(f.Manufacturer) ?? "ukendt" };
            return Set(s, BiosStates.Unsupported, $"Leverandøren understøttes ikke endnu ({Clip(who, 60)}).");
        }

        var cur = LenovoBiosVersion.FromMachine(current, f.BiosRelease);
        s.Current = Clip(cur.Code ?? current, 64);
        s.CurrentNumeric = cur.Numeric;
        if (lenovo is null || !lenovo.Loaded)
        {
            if (fwupdVersion is not null) { s.Latest = Clip(fwupdVersion, 64); s.Source = "fwupd"; return Set(s, BiosStates.Fwupd, "Kan opdateres med fwupd."); }
            return lenovo?.Error is { } err
                ? Set(s, BiosStates.CatalogError, Clip("Lenovos katalog kunne ikke hentes: " + err, 300))
                : Set(s, BiosStates.Unchecked, "Lenovos katalog er ikke hentet endnu.");
        }

        s.Source = "lenovo-catalog";
        var match = MatchLenovo(f, cur, lenovo.Models);
        s.MatchQuality = match.Quality;
        if (match.Model?.Bios is not { } bios) return Set(s, BiosStates.Uncertain, "Modellen kunne ikke genkendes sikkert. Vi viser ingen opdatering. " + match.Detail);

        var latest = bios.Parsed;
        s.CatalogKey = Clip($"{bios.Image}/{Mtm(f) ?? match.Model.Types.FirstOrDefault() ?? "?"}", 64);
        s.Latest = Clip(latest.Code ?? bios.Version, 64);
        s.LatestNumeric = latest.Numeric;
        s.LatestDate = bios.Date?.ToString("yyyy-MM-dd");
        var details = lenovo.Packages.GetValueOrDefault(bios.Stem);

        switch (BiosVersions.Compare(cur, latest))
        {
            case VersionOrder.Same or VersionOrder.Older:
                return Set(s, BiosStates.Newest, "Nyeste version.");
            case VersionOrder.Conflict:
                s.MatchQuality = Uncertain;
                return Set(s, BiosStates.Uncertain, "Byggekoden og versionsnummeret er uenige om, hvilken BIOS der er nyest. Vi viser ingen opdatering.");
            case VersionOrder.Unknown or VersionOrder.OtherFamily:
                s.MatchQuality = Uncertain;
                return Set(s, BiosStates.Uncertain, "Versionerne kunne ikke sammenlignes. Vi viser ingen opdatering.");
        }

        // nyere: Lenovos pakke skal tillade opgradering fra den nuværende version (<Level>-reglerne), når vi kender dem
        if (details is not null && details.Package.DependsOn.Count > 0 && cur.Code is { } code
            && !details.Package.DependsOn.Any(l => BiosVersions.LevelMatches(l, code)))
        {
            s.MatchQuality = Uncertain;
            return Set(s, BiosStates.Uncertain, $"Lenovos pakke kan ikke opgradere direkte fra {code}. Vi viser ingen opdatering.");
        }
        s.Critical = details?.Package.Critical == true;
        if (details is not null) s.ChangeSummaryJson = JsonSerializer.Serialize(ChangesSince(details.Changes, cur, latest), Json.Web);
        var date = bios.Date is { } d ? " fra " + d.ToString("yyyy-MM-dd") : "";
        return Set(s, BiosStates.Newer, $"Nyere findes: {s.Latest}{date}.");
    }

    /// <summary>Ændringerne efter den nuværende version op til og med den nyeste – nyeste først, højst 12 versioner.</summary>
    public static List<BiosChange> ChangesSince(List<BiosChange> changes, LenovoBiosVersion current, LenovoBiosVersion latest)
    {
        var list = new List<BiosChange>();
        foreach (var c in changes)
        {
            var v = LenovoBiosVersion.FromCatalog(c.Version);
            if (v.Image != current.Image || v.Level is not { } lvl || current.Level is not { } cl) continue;
            if (lvl > cl && (latest.Level is not { } ll || lvl <= ll)) list.Add(c);
        }
        return list.OrderByDescending(c => LenovoBiosVersion.FromCatalog(c.Version).Level).Take(12).ToList();
    }

    private static MachineBiosStatus Set(MachineBiosStatus s, string state, string? detail)
    {
        s.State = state;
        s.Detail = Clip(detail?.Trim(), 300);
        return s;
    }

    private static string? Clip(string? s, int max) => s is null ? null : s.Length > max ? s[..max] : s;

    [GeneratedRegex(@"\s*\(\d{4}-\d{2}-\d{2}\)\s*$")]
    private static partial Regex DateSuffix();
    [GeneratedRegex(@"^([0-9]{2}[0-9A-Z]{2})[0-9A-Z]{0,8}$", RegexOptions.IgnoreCase)]
    private static partial Regex MtmName();
    [GeneratedRegex(@"(?:^|_)MT_([0-9A-Z]{4})(?:_|$)", RegexOptions.IgnoreCase)]
    private static partial Regex MtmSku();
}
