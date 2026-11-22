using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Fleetdeck.Server.Data;
using Fleetdeck.Shared;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Server.Services;

/// <summary>
/// Statisk tilstand: én låst konfigurationsprofil pr. iPad (.mobileconfig), som installeres med Apple Configurator/cfgutil på en
/// overvåget (supervised) iPad – uden MDM. Den indeholder DNS-låsen (krypteret DNS til AdGuard, kan ikke slås fra), begrænsninger
/// (skjulte apps, App Store, køb i apps, konti, aldersgrænse) og en kode, der skal til for at fjerne profilen. Sengetid, pause,
/// YouTube og aktivitet styres stadig dynamisk gennem AdGuard.
/// Nøglerne er fra Apples device-management-schema (com.apple.applicationaccess, com.apple.dnsSettings.managed,
/// com.apple.profileRemovalPassword). De fleste begrænsninger virker kun på overvågede iPads; ikke-overvågede ignorerer dem.
/// </summary>
public static partial class LockedProfile
{
    public const string Prefix = "dk.fleetdeck.ipad.";
    /// <summary>Aldersgrænser i App Store siden iPadOS 26 (år) og ratingApps-værdien for hver.</summary>
    public static readonly IReadOnlyDictionary<int, int> AgeRatings = new Dictionary<int, int> { [4] = 100, [9] = 200, [13] = 313, [16] = 416, [18] = 618 };

    /// <summary>Profilens faste id pr. iPad (kun reverse-DNS-tegn), så en ny version erstatter den gamle.</summary>
    public static string Identifier(Ipad p) => $"{Prefix}{Safe(p.ClientId)}.locked";
    /// <summary>Den almindelige DNS-profil fra QR-koden (samme id som <see cref="IpadProfile"/>) – erstattes af den låste.</summary>
    public static string DnsLinkIdentifier(Ipad p) => $"{Prefix}{Safe(p.ClientId)}";

    private static string Safe(string s) => new(s.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').ToArray());

    public static int? RatingValue(int? age) => age is { } a && AgeRatings.TryGetValue(a, out var v) ? v : null;

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9\-]*(\.[A-Za-z0-9\-]+)+$")] private static partial Regex BundleIdRx();
    /// <summary>Gyldigt bundle-id (fx com.google.ios.youtube). Store og små bogstaver bevares – Apple skelner mellem dem.</summary>
    public static bool IsBundleId(string s) => s.Length is > 2 and <= 155 && BundleIdRx().IsMatch(s);

    public static List<string> CleanApps(IEnumerable<string> apps) =>
        apps.Select(a => a.Trim()).Where(IsBundleId).Distinct(StringComparer.Ordinal).Take(200).ToList();

    /// <summary>Begrænsningerne (com.apple.applicationaccess). Kun nøgler, der begrænser, kommer med – Apples standard er "tilladt".</summary>
    public static List<(string Key, object Value)> Restrictions(Ipad p)
    {
        var r = new List<(string, object)>();
        if (!p.AllowAppInstall)
        {
            // App Store skjules, men Apple Configurator kan stadig installere og opdatere apps (allowAppInstallation=false ville spærre alt)
            r.Add(("allowUIAppInstallation", false));
            r.Add(("allowMarketplaceAppInstallation", false));       // alternative markedspladser (EU)
            r.Add(("allowWebDistributionAppInstallation", false));   // apps fra hjemmesider (EU)
        }
        if (!p.AllowInAppPurchases) r.Add(("allowInAppPurchases", false));
        if (!p.AllowAccountChanges) r.Add(("allowAccountModification", false));
        if (RatingValue(p.AgeRatingApps) is { } rating) r.Add(("ratingApps", rating));   // ratingRegion udelades: den bruges kun af profilværktøjer
        if (p.Kind == IpadKinds.Child)
        {
            r.Add(("allowEraseContentAndSettings", false));             // "Slet alt indhold og indstillinger" fjernes
            r.Add(("allowVPNCreation", false));                         // ingen VPN uden om DNS-filteret
            r.Add(("allowUIConfigurationProfileInstallation", false));  // ingen andre profiler (QR-linket virker heller ikke længere)
            r.Add(("allowCloudPrivateRelay", false));                   // iCloud Privat relæ fra (mulig vej uden om filteret – ikke bekræftet)
        }
        if (CleanApps(p.HiddenApps) is { Count: > 0 } apps) r.Add(("blockedAppBundleIDs", apps));
        return r;
    }

    /// <summary>
    /// Hele profilen som XML-plist. <paramref name="dohHost"/> tom = ingen DNS-lås i profilen.
    /// <paramref name="mdm"/>: samme DNS-lås og begrænsninger, men leveret via MDM (Apple-styring) – så udelades koden til fjernelse og
    /// PayloadRemovalDisallowed, for en MDM-installeret profil kan kun fjernes af MDM (eller sammen med MDM-profilen).
    /// </summary>
    public static string Build(Ipad p, string? dohHost, string removalPassword, int version, bool mdm = false)
    {
        var id = Identifier(p);
        string Uuid(string part) => new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{id}|{version}|{part}"))[..16]).ToString().ToUpperInvariant();
        var payloads = new List<XElement>();
        if (!string.IsNullOrWhiteSpace(dohHost))
            payloads.Add(Dict([
                ("DNSSettings", Dict([("DNSProtocol", "HTTPS"), ("ServerURL", $"https://{dohHost.Trim()}/dns-query/{p.ClientId}")])),
                ("PayloadDisplayName", "Fleetdeck – sikker DNS"),
                ("PayloadIdentifier", $"{id}.dns"),
                ("PayloadType", "com.apple.dnsSettings.managed"),
                ("PayloadUUID", Uuid("dns")),
                ("PayloadVersion", 1),
                ("ProhibitDisablement", true),
            ]));
        if (Restrictions(p) is { Count: > 0 } rs)
            payloads.Add(Dict([
                .. rs,
                ("PayloadDisplayName", "Fleetdeck – begrænsninger"),
                ("PayloadIdentifier", $"{id}.restrictions"),
                ("PayloadType", "com.apple.applicationaccess"),
                ("PayloadUUID", Uuid("restrictions")),
                ("PayloadVersion", 1),
            ]));
        if (!mdm)
            payloads.Add(Dict([
                ("PayloadDisplayName", "Fleetdeck – kode til fjernelse"),
                ("PayloadIdentifier", $"{id}.removal"),
                ("PayloadType", "com.apple.profileRemovalPassword"),
                ("PayloadUUID", Uuid("removal")),
                ("PayloadVersion", 1),
                ("RemovalPassword", removalPassword),
            ]));
        var top = Dict([
            ("PayloadContent", payloads),
            ("PayloadDescription", mdm
                ? $"Fleetdecks låste profil til {p.Name}: {Summary(p, dohHost)}. Installeret og opdateret af Fleetdeck via Apple-styring (MDM)."
                : $"Fleetdecks låste profil til {p.Name}: {Summary(p, dohHost)}. Den kan kun fjernes med koden eller fra den Mac, der overvåger iPad'en."),
            // versionen står i navnet: cfgutil viser kun navn, id og PayloadVersion (som altid skal være 1)
            ("PayloadDisplayName", $"Fleetdeck – {p.Name} (låst v{version})"),
            ("PayloadIdentifier", id),
            ("PayloadOrganization", "Fleetdeck"),
            .. (mdm ? Array.Empty<(string, object)>() : [("PayloadRemovalDisallowed", true)]),
            ("PayloadType", "Configuration"),
            ("PayloadUUID", Uuid("profile")),
            ("PayloadVersion", 1),
        ]);
        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement("plist", new XAttribute("version", "1.0"), top));
        using var ms = new MemoryStream();
        using (var w = XmlWriter.Create(ms, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, IndentChars = "  " })) doc.Save(w);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string Summary(Ipad p, string? dohHost)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(dohHost)) parts.Add("DNS-lås");
        if (p.HiddenApps.Count > 0) parts.Add("skjulte apps");
        if (!p.AllowAppInstall) parts.Add("App Store skjult");
        if (p.AgeRatingApps is { } a) parts.Add($"apps til {a}+");
        if (p.Kind == IpadKinds.Child) parts.Add("børnesikring");
        return parts.Count > 0 ? string.Join(", ", parts) : "kode til fjernelse";
    }

    /// <summary>Fingeraftryk af indholdet uden versionsnummer: ændrer det sig, er der en ny version.</summary>
    public static string Fingerprint(Ipad p, string? dohHost, string removalPassword) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Build(p, dohHost, removalPassword, 0))))[..32];

    /// <summary>Tæl versionen op, hvis indholdet er ændret siden sidst (første gang gemmes kun fingeraftrykket). Returnerer true, hvis rækken er ændret.</summary>
    public static bool Track(Ipad p, string? dohHost, string removalPassword)
    {
        var fp = Fingerprint(p, dohHost, removalPassword);
        if (fp == p.LockedProfileHash) return false;
        if (p.LockedProfileHash is not null) p.LockedProfileVersion++;
        p.LockedProfileHash = fp;
        return true;
    }

    [GeneratedRegex(@"\(låst v(\d+)\)")] private static partial Regex VersionRx();

    /// <summary>Den låste profils version blandt iPad'ens installerede profiler: null = ikke installeret, 0 = installeret, men ukendt version.</summary>
    public static int? InstalledVersion(Ipad p, IEnumerable<IosProfileInfo> profiles)
    {
        var id = Identifier(p);
        var hit = profiles.FirstOrDefault(x => x.Identifier == id);
        if (hit is null) return null;
        return VersionRx().Match(hit.Name ?? "") is { Success: true } m && int.TryParse(m.Groups[1].Value, out var v) ? v : 0;
    }

    // ------------------------------------------------------------------ plist
    private static XElement Dict(IEnumerable<(string Key, object Value)> kv) =>
        new("dict", kv.SelectMany(x => new object[] { new XElement("key", x.Key), Val(x.Value) }));

    private static XElement Val(object v) => v switch
    {
        XElement e => e,
        bool b => new XElement(b ? "true" : "false"),
        int i => new XElement("integer", i),
        string s => new XElement("string", Clean(s)),
        IEnumerable<XElement> l => new XElement("array", l),
        IEnumerable<string> l => new XElement("array", l.Select(x => new XElement("string", Clean(x)))),
        _ => throw new ArgumentException($"Ukendt plist-værdi: {v.GetType().Name}"),
    };

    /// <summary>Fjern tegn, der ikke må stå i XML (fx kontroltegn i et navn) – resten escapes af XmlWriter.</summary>
    private static string Clean(string s) => s.All(XmlConvert.IsXmlChar) ? s : new string(s.Where(XmlConvert.IsXmlChar).ToArray());
}

/// <summary>Den låste profil med hemmeligheder: fjernelseskoden (krypteret med DataProtection) og versionsstyring.</summary>
public sealed class LockedProfiles(FamilySettings fs, IDataProtectionProvider dp)
{
    private readonly IDataProtector protector = dp.CreateProtector("Fleetdeck.Ipad.RemovalPassword");

    /// <summary>Let at skrive på en iPad: 4 × 4 små bogstaver/tal uden forvekslinger (fx "k7pm-…").</summary>
    public static string NewPassword()
    {
        const string a = "abcdefghijkmnpqrstuvwxyz23456789";
        return string.Join('-', Enumerable.Range(0, 4).Select(_ => new string(Enumerable.Range(0, 4).Select(_ => a[RandomNumberGenerator.GetInt32(a.Length)]).ToArray())));
    }

    /// <summary>Koden til at fjerne profilen. Laves (og gemmes på iPad-rækken), hvis den mangler eller ikke kan læses.</summary>
    public string Password(Ipad p)
    {
        if (!string.IsNullOrEmpty(p.RemovalPassword))
            try { return protector.Unprotect(p.RemovalPassword); } catch (CryptographicException) { }
        var pw = NewPassword();
        p.RemovalPassword = protector.Protect(pw);
        return pw;
    }

    /// <summary>Den gemte kode, uden at lave en ny (null: ingen kode endnu, eller den kan ikke læses). Til nødkortet.</summary>
    public string? Existing(Ipad p)
    {
        if (string.IsNullOrEmpty(p.RemovalPassword)) return null;
        try { return protector.Unprotect(p.RemovalPassword); } catch (CryptographicException) { return null; }
    }

    public string Rotate(Ipad p)
    {
        var pw = NewPassword();
        p.RemovalPassword = protector.Protect(pw);
        return pw;
    }

    /// <summary>Tæl versionen op, hvis profilens indhold er ændret siden sidst. Returnerer true, hvis rækken er ændret (skal gemmes).</summary>
    public bool Sync(Ipad p, string dohHost)
    {
        var had = p.RemovalPassword;
        return LockedProfile.Track(p, dohHost, Password(p)) || had != p.RemovalPassword;
    }

    public async Task<bool> Sync(Ipad p) => Sync(p, await fs.DohHost());

    /// <summary>Profilen klar til installation (versionen er ajour).</summary>
    public async Task<string> Build(Ipad p)
    {
        var host = await fs.DohHost();
        Sync(p, host);
        return LockedProfile.Build(p, host, Password(p), p.LockedProfileVersion);
    }

    /// <summary>
    /// Samme låste profil, men til levering via MDM (Apple-styring): DNS-lås og begrænsninger er de samme som i statisk tilstand.
    /// Null, hvis profilen ikke ville indeholde noget (ingen DNS-adresse og ingen begrænsninger).
    /// </summary>
    public (string Xml, string Hash)? BuildForMdm(Ipad p, string dohHost)
    {
        Sync(p, dohHost);
        if (string.IsNullOrWhiteSpace(dohHost) && LockedProfile.Restrictions(p).Count == 0) return null;
        var xml = LockedProfile.Build(p, dohHost, "", p.LockedProfileVersion, mdm: true);
        return (xml, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(xml)))[..32]);
    }
}

/// <summary>iPads set via kabel på en Mac (ios-job): gem listen på Mac'en og opdatér de linkede iPads.</summary>
public static class IosInventory
{
    public static List<IosDeviceInfo> Devices(Machine m) => Json.Des<List<IosDeviceInfo>>(m.IosDevicesJson);

    /// <summary>Returnerer true, hvis en iPad i Fleetdeck blev opdateret.</summary>
    public static async Task<bool> Apply(AppDb db, Machine m, IosReport r)
    {
        var now = DateTime.UtcNow;
        var incoming = r.Devices.Where(d => Cfgutil.NormEcid(d.Ecid).Length > 0).Select(d => { d.Ecid = Cfgutil.NormEcid(d.Ecid); return d; }).ToList();
        var cur = Devices(m);
        List<IosDeviceInfo> next;
        if (r.Full) next = incoming.Select(d => cur.FirstOrDefault(c => c.Ecid == d.Ecid) is { } old ? Cfgutil.Merge(old, d) : d).ToList();
        else
        {
            next = [.. cur];
            foreach (var d in incoming)
            {
                var i = next.FindIndex(c => c.Ecid == d.Ecid);
                if (i >= 0) next[i] = Cfgutil.Merge(next[i], d); else next.Add(d);
            }
        }
        m.IosDevicesJson = Json.Ser(next); m.IosDevicesAt = now;

        var ipads = await db.Ipads.Where(i => i.Ecid != null || i.Serial != null).ToListAsync();
        var changed = false;
        foreach (var d in next.Where(n => incoming.Any(x => x.Ecid == n.Ecid)))
        {
            var p = ipads.FirstOrDefault(i => i.Ecid == d.Ecid) ?? (d.Serial is { Length: > 0 } sn ? ipads.FirstOrDefault(i => i.Serial == sn) : null);
            if (p is null) continue;
            Link(p, d, now); changed = true;
        }
        return changed;
    }

    /// <summary>Kobl en iPad i Fleetdeck til en enhed, Mac'en har set, og tag dens oplysninger.</summary>
    public static void Link(Ipad p, IosDeviceInfo d, DateTime seen)
    {
        p.Ecid = Cfgutil.NormEcid(d.Ecid);
        if (d.Serial is { Length: > 0 } sn) p.Serial = Cut(sn);
        if (d.Firmware is { Length: > 0 } fw) p.OsVersion = Cut(fw);
        if (d.Supervised is bool s) p.Supervised = s;
        if (string.IsNullOrWhiteSpace(p.Model) && d.DeviceType is { Length: > 0 } dt) p.Model = dt.Length > 80 ? dt[..80] : dt;
        if (d.Profiles is { } profiles) p.LockedProfileApplied = LockedProfile.InstalledVersion(p, profiles);
        p.DeviceSeen = seen;
    }

    private static string Cut(string s) => s.Length > 40 ? s[..40] : s;
}
