using System.Globalization;
using System.Security.Cryptography;
using System.Formats.Asn1;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Fleetdeck.Server.Data;

namespace Fleetdeck.Server.Services;

// Ren logik til Apple-styring (MDM) uden database og netværk, så den kan testes:
// plist, MDM-kommandoer, tilmeldingsprofil, certifikater (push, enheds-CA) og NanoHUBs webhooks.

/// <summary>En plist-dict, der bevarer rækkefølgen: <c>new PDict { { "RequestType", "DeviceLock" } }</c>.</summary>
public sealed class PDict : List<KeyValuePair<string, object>>
{
    public void Add(string key, object? value) { if (value is not null) Add(new KeyValuePair<string, object>(key, value)); }
}

/// <summary>XML-plist: skriv (PDict, lister, string, bool, tal, data, dato) og læs (dict → Dictionary, array → List).</summary>
public static class Plist
{
    public static string Write(object root)
    {
        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement("plist", new XAttribute("version", "1.0"), Val(root)));
        using var ms = new MemoryStream();
        using (var w = XmlWriter.Create(ms, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, IndentChars = "  " })) doc.Save(w);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static XElement Val(object v) => v switch
    {
        PDict d => new XElement("dict", d.SelectMany(kv => new object[] { new XElement("key", kv.Key), Val(kv.Value) })),
        bool b => new XElement(b ? "true" : "false"),
        int i => new XElement("integer", i),
        long l => new XElement("integer", l),
        double x => new XElement("real", x.ToString("R", CultureInfo.InvariantCulture)),
        string s => new XElement("string", Clean(s)),
        byte[] data => new XElement("data", Convert.ToBase64String(data)),
        DateTime t => new XElement("date", t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)),
        IEnumerable<object> l => new XElement("array", l.Select(Val)),
        _ => throw new ArgumentException($"Ukendt plist-værdi: {v.GetType().Name}"),
    };

    private static string Clean(string s) => s.All(XmlConvert.IsXmlChar) ? s : new string(s.Where(XmlConvert.IsXmlChar).ToArray());

    /// <summary>Læs en XML-plist. Kaster <see cref="FormatException"/>, hvis det ikke er en gyldig plist.</summary>
    public static object Parse(string xml)
    {
        try
        {
            using var r = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            var doc = XDocument.Load(r);
            if (doc.Root?.Name.LocalName != "plist" || doc.Root.Elements().FirstOrDefault() is not { } first) throw new FormatException("Ikke en plist");
            return Read(first);
        }
        catch (XmlException e) { throw new FormatException(e.Message, e); }
    }

    public static Dictionary<string, object> ParseDict(string xml) =>
        Parse(xml) as Dictionary<string, object> ?? throw new FormatException("Plist'en er ikke en dict");

    private static object Read(XElement e) => e.Name.LocalName switch
    {
        "dict" => e.Elements().Chunk(2).Where(kv => kv.Length == 2).GroupBy(kv => kv[0].Value).ToDictionary(g => g.Key, g => Read(g.Last()[1])),
        "array" => e.Elements().Select(Read).ToList(),
        "string" => e.Value,
        "integer" => long.Parse(e.Value.Trim(), CultureInfo.InvariantCulture),
        "real" => double.Parse(e.Value.Trim(), CultureInfo.InvariantCulture),
        "true" => true,
        "false" => false,
        "date" => DateTime.Parse(e.Value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
        "data" => Convert.FromBase64String(new string(e.Value.Where(c => !char.IsWhiteSpace(c)).ToArray())),
        _ => throw new FormatException($"Ukendt plist-element: {e.Name.LocalName}"),
    };

    public static string? Str(this Dictionary<string, object> d, string key) => d.TryGetValue(key, out var v) ? v as string : null;
    public static bool? Bool(this Dictionary<string, object> d, string key) => d.TryGetValue(key, out var v) && v is bool b ? b : null;
    public static double? Num(this Dictionary<string, object> d, string key) => d.TryGetValue(key, out var v) ? v switch { long l => l, double x => x, _ => null } : null;
    public static Dictionary<string, object>? Dict(this Dictionary<string, object> d, string key) => d.TryGetValue(key, out var v) ? v as Dictionary<string, object> : null;
}

/// <summary>MDM-kommandoer som plist med Fleetdecks eget CommandUUID.</summary>
public static class MdmCommands
{
    public sealed record Built(string Uuid, string RequestType, string Xml);

    public static readonly string[] InfoQueries =
    [
        "DeviceName", "OSVersion", "BuildVersion", "ModelName", "Model", "ProductName", "SerialNumber", "UDID",
        "DeviceCapacity", "AvailableDeviceCapacity", "BatteryLevel", "IsSupervised", "IsActivationLockEnabled",
        "IsCloudBackupEnabled", "LastCloudBackupDate", "WiFiMAC",
    ];

    private static Built Make(string requestType, PDict command, string? uuid = null)
    {
        uuid ??= Guid.NewGuid().ToString().ToUpperInvariant();
        var cmd = new PDict { { "RequestType", requestType } };
        cmd.AddRange(command);
        return new(uuid, requestType, Plist.Write(new PDict { { "Command", cmd }, { "CommandUUID", uuid } }));
    }

    public static Built DeviceInformation() => Make("DeviceInformation", new PDict { { "Queries", InfoQueries.Cast<object>().ToList() } });

    /// <summary>Lås skærmen. Besked og telefonnummer vises på låseskærmen (højst 200/40 tegn).</summary>
    public static Built DeviceLock(string? message, string? phone) => Make("DeviceLock", new PDict
    {
        { "Message", Trim(message, 200) },
        { "PhoneNumber", Trim(phone, 40) },
    });

    public static Built InstallProfile(string profileXml) => Make("InstallProfile", new PDict { { "Payload", Encoding.UTF8.GetBytes(profileXml) } });
    public static Built RemoveProfile(string identifier) => Make("RemoveProfile", new PDict { { "Identifier", identifier } });
    public static Built EraseDevice() => Make("EraseDevice", new PDict { { "PreserveDataPlan", true } });

    private static string? Trim(string? s, int max) => (s ?? "").Trim() is { Length: > 0 } t ? t[..Math.Min(max, t.Length)] : null;

    /// <summary>Kort, menneskelig fejltekst fra ErrorChain.</summary>
    public static string? ErrorText(Dictionary<string, object> report)
    {
        if (report.TryGetValue("ErrorChain", out var ec) && ec is List<object> chain && chain.Count > 0)
            return string.Join(" – ", chain.OfType<Dictionary<string, object>>()
                .Select(e => e.Str("LocalizedDescription") ?? e.Str("USEnglishDescription") ?? $"{e.Str("ErrorDomain")} {e.Num("ErrorCode")}").Where(x => x.Trim().Length > 0));
        return report.Str("Status") is "CommandFormatError" ? "Kommandoen blev afvist (forkert format)" : null;
    }
}

/// <summary>DDM-erklæringer (KMFDDM i NanoHUB): OS-opdatering inden en frist og statusabonnement.</summary>
public static partial class MdmDeclarations
{
    public const string StatusIdentifier = "dk.fleetdeck.status";
    public static string OsUpdateIdentifier(Ipad p) => $"dk.fleetdeck.osupdate.{p.ClientId}";
    public static string DeviceSet(Ipad p) => $"fd-dev-{p.Id}";

    [GeneratedRegex(@"^\d{1,2}(\.\d{1,3}){1,2}\z")] private static partial Regex VersionRx();
    [GeneratedRegex(@"^[0-9A-Za-z]{3,20}\z")] private static partial Regex BuildRx();

    public static string? ValidateVersion(string? v) => v is { Length: > 0 } && VersionRx().IsMatch(v) ? null : "Skriv en iPadOS-version som 26.1 eller 26.1.2";
    public static string? ValidateBuild(string? b) => string.IsNullOrEmpty(b) || BuildRx().IsMatch(b) ? null : "Build-nummeret skal være bogstaver og tal (fx 23B85)";

    public static object Status() => new
    {
        Identifier = StatusIdentifier,
        Type = "com.apple.configuration.management.status-subscriptions",
        Payload = new
        {
            StatusItems = new[]
            {
                "softwareupdate.install-state", "softwareupdate.pending-version", "softwareupdate.failure-reason",
                "device.operating-system.version", "device.operating-system.build-version",
            }.Select(n => new { Name = n }),
        },
    };

    /// <summary>com.apple.configuration.softwareupdate.enforcement.specific: iPad'en installerer senest <paramref name="localDeadline"/> (iPad'ens lokale tid).</summary>
    public static object OsUpdate(Ipad p, string version, string? build, DateTime localDeadline)
    {
        var payload = new Dictionary<string, object>
        {
            ["TargetOSVersion"] = version,
            ["TargetLocalDateTime"] = localDeadline.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrEmpty(build)) payload["TargetBuildVersion"] = build;
        return new { Identifier = OsUpdateIdentifier(p), Type = "com.apple.configuration.softwareupdate.enforcement.specific", Payload = payload };
    }
}

/// <summary>Tilmeldingsprofilen (.mobileconfig) til én iPad: enhedsidentitet (PKCS#12) + MDM-payload med SignMessage.</summary>
public static class MdmEnrollmentProfile
{
    public const string Prefix = "dk.fleetdeck.mdm.";
    public const int AccessRights = 8191;

    public static string Identifier(Ipad p) => $"{Prefix}{new string(p.ClientId.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').ToArray())}";
    public static string ServerUrl(string publicHost, string enrollKey) => $"https://{publicHost}/mdm?fd={Uri.EscapeDataString(enrollKey)}";

    public static string Build(Ipad p, string publicHost, string topic, string enrollKey, byte[] pkcs12, string pkcs12Password, string org)
    {
        var id = Identifier(p);
        string Uuid(string part) => new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{id}|{part}"))[..16]).ToString().ToUpperInvariant();
        var identityUuid = Guid.NewGuid().ToString().ToUpperInvariant();
        var url = ServerUrl(publicHost, enrollKey);
        var root = new PDict
        {
            { "PayloadContent", new List<object>
                {
                    new PDict
                    {
                        { "Password", pkcs12Password },
                        { "PayloadCertificateFileName", "fleetdeck-identitet.p12" },
                        { "PayloadContent", pkcs12 },
                        { "PayloadDescription", "iPad'ens identitet over for Fleetdecks MDM-server" },
                        { "PayloadDisplayName", "Fleetdeck – enhedsidentitet" },
                        { "PayloadIdentifier", $"{id}.identity" },
                        { "PayloadType", "com.apple.security.pkcs12" },
                        { "PayloadUUID", identityUuid },
                        { "PayloadVersion", 1 },
                    },
                    new PDict
                    {
                        { "AccessRights", AccessRights },
                        { "CheckInURL", url },
                        { "CheckOutWhenRemoved", true },
                        { "IdentityCertificateUUID", identityUuid },
                        { "PayloadDescription", "Gør det muligt for Fleetdeck at administrere iPad'en (opdateringer, lås, profiler)" },
                        { "PayloadDisplayName", "Fleetdeck – Apple-styring" },
                        { "PayloadIdentifier", $"{id}.mdm" },
                        { "PayloadType", "com.apple.mdm" },
                        { "PayloadUUID", Uuid("mdm") },
                        { "PayloadVersion", 1 },
                        { "ServerCapabilities", new List<object> { "com.apple.mdm.per-user-connections" } },
                        { "ServerURL", url },
                        { "SignMessage", true },
                        { "Topic", topic },
                    },
                }
            },
            { "PayloadDescription", $"Tilmelder {p.Name} til Fleetdecks Apple-styring. Profilen kan fjernes igen under Indstillinger → Generelt → VPN og enhedsadministration." },
            { "PayloadDisplayName", $"Fleetdeck – {p.Name}" },
            { "PayloadIdentifier", id },
            { "PayloadOrganization", string.IsNullOrWhiteSpace(org) ? "Fleetdeck" : org.Trim() },
            { "PayloadType", "Configuration" },
            { "PayloadUUID", Uuid("profile") },
            { "PayloadVersion", 1 },
        };
        return Plist.Write(root);
    }
}

/// <summary>Certifikater til Apple-styring: push-CSR, mdmcert.download-kryptering, push-certifikatet og enheds-CA'en.</summary>
public static class MdmPki
{
    public const string UidOid = "0.9.2342.19200300.100.1.1";

    public sealed record KeyAndPem(string KeyPem, string Pem);
    public sealed record PushCertInfo(string CertPem, string Topic, DateTime NotAfter, string Subject);
    public sealed record DeviceIdentity(byte[] Pkcs12, string Password, string Serial, DateTime NotAfter, string CommonName);

    private static string KeyPem(RSA rsa) => rsa.ExportRSAPrivateKeyPem();

    /// <summary>Push-nøgle (RSA 2048) og CSR til Apples MDM-push-certifikat.</summary>
    public static KeyAndPem PushCsr(string org)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(new X500DistinguishedName(Dn("Fleetdeck MDM push", org)), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return new(KeyPem(rsa), req.CreateSigningRequestPem());
    }

    /// <summary>Krypteringsnøgle til mdmcert.download: den underskrevne anmodning sendes krypteret til dette certifikat.</summary>
    public static KeyAndPem EncryptionPair()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=mdmcert.download", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DataEncipherment, true));
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(365));
        return new(KeyPem(rsa), cert.ExportCertificatePem());
    }

    /// <summary>
    /// Pak filen fra mdmcert.download ud (…plist.b64.p7: hex-kodet PKCS#7, krypteret til krypteringscertifikatet).
    /// Resultatet er den underskrevne anmodning, der uploades hos Apple. PEM og base64 godtages også.
    /// </summary>
    public static byte[] DecryptSignedRequest(string fileText, string encCertPem, string encKeyPem)
    {
        var t = fileText.Trim();
        byte[] der;
        if (t.Length > 0 && t.Length % 2 == 0 && t.All(Uri.IsHexDigit)) der = Convert.FromHexString(t);
        else if (t.StartsWith("-----BEGIN", StringComparison.Ordinal) && PemEncoding.TryFind(t, out var f)) der = Convert.FromBase64String(t[f.Base64Data]);
        else
        {
            try { der = Convert.FromBase64String(new string(t.Where(c => !char.IsWhiteSpace(c)).ToArray())); }
            catch (FormatException) { throw new FormatException("Filen ligner ikke svaret fra mdmcert.download (…plist.b64.p7)"); }
        }
        using var rsa = RSA.Create();
        rsa.ImportFromPem(encKeyPem);
        try { return Cms.DecryptEnveloped(der, rsa); }
        catch (Exception e) when (e is AsnContentException or CryptographicException or FormatException)
        {
            throw new FormatException(e is CryptographicException
                ? "Filen kunne ikke låses op – den er krypteret til en anden nøgle (lav anmodningen igen fra Fleetdeck)"
                : "Filen ligner ikke svaret fra mdmcert.download (…plist.b64.p7)");
        }
    }

    /// <summary>Kontrollér push-certifikatet fra Apple: passer det til nøglen, og hvilket topic/udløb har det.</summary>
    public static PushCertInfo ParsePushCert(string pemText, string keyPem)
    {
        var certPem = FirstCertPem(pemText) ?? throw new FormatException("Filen indeholder intet certifikat (forventede en .pem fra Apple)");
        using var cert = X509Certificate2.CreateFromPem(certPem);
        var topic = Uid(cert) ?? throw new FormatException("Certifikatet har intet topic (UID) – er det et MDM-push-certifikat fra Apple?");
        if (!topic.StartsWith("com.apple.mgmt.", StringComparison.Ordinal)) throw new FormatException($"Certifikatets topic ({topic}) er ikke et MDM-push-topic");
        using var key = RSA.Create();
        key.ImportFromPem(keyPem);
        using var pub = cert.GetRSAPublicKey() ?? throw new FormatException("Certifikatet har ikke en RSA-nøgle");
        if (!pub.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(key.ExportSubjectPublicKeyInfo()))
            throw new FormatException("Certifikatet passer ikke til Fleetdecks push-nøgle – brug den anmodning, Fleetdeck har lavet");
        return new(certPem, topic, cert.NotAfter.ToUniversalTime(), cert.Subject);
    }

    /// <summary>Minimal PKCS#7/CMS EnvelopedData (RSA-nøgletransport + 3DES/AES-CBC) – nok til mdmcert.download, uden ekstra pakker.</summary>
    internal static class Cms
    {
        public static byte[] DecryptEnveloped(byte[] der, RSA key)
        {
            var ci = new AsnReader(der, AsnEncodingRules.BER).ReadSequence();
            if (ci.ReadObjectIdentifier() != "1.2.840.113549.1.7.3") throw new FormatException("Ikke krypterede data (envelopedData)");
            var env = ci.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence();
            env.ReadInteger();
            if (env.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0))) env.ReadEncodedValue();   // originatorInfo
            var recipients = env.ReadSetOf(skipSortOrderValidation: true);
            var keys = new List<(string Alg, byte[] Enc)>();
            while (recipients.HasData)
            {
                if (recipients.PeekTag().TagClass != TagClass.Universal) { recipients.ReadEncodedValue(); continue; }   // kun KeyTransRecipientInfo
                var ri = recipients.ReadSequence();
                ri.ReadInteger();
                ri.ReadEncodedValue();   // rid
                var alg = ri.ReadSequence().ReadObjectIdentifier();
                keys.Add((alg, ri.ReadOctetString()));
            }
            var eci = env.ReadSequence();
            eci.ReadObjectIdentifier();
            var calg = eci.ReadSequence();
            var cipherOid = calg.ReadObjectIdentifier();
            var iv = calg.HasData ? calg.ReadOctetString() : [];
            var tag0 = new Asn1Tag(TagClass.ContextSpecific, 0);
            byte[] cipherText;
            if (eci.PeekTag().IsConstructed)
            {
                var parts = eci.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true));
                using var ms = new MemoryStream();
                while (parts.HasData) ms.Write(parts.ReadOctetString());
                cipherText = ms.ToArray();
            }
            else cipherText = eci.ReadOctetString(tag0);

            CryptographicException? last = null;
            foreach (var (alg, enc) in keys)
            {
                try
                {
                    var cek = key.Decrypt(enc, alg == "1.2.840.113549.1.1.7" ? RSAEncryptionPadding.OaepSHA1 : RSAEncryptionPadding.Pkcs1);
                    using SymmetricAlgorithm sym = cipherOid switch
                    {
                        "1.2.840.113549.3.7" => TripleDES.Create(),
                        "2.16.840.1.101.3.4.1.2" or "2.16.840.1.101.3.4.1.22" or "2.16.840.1.101.3.4.1.42" => Aes.Create(),
                        _ => throw new FormatException($"Ukendt krypteringsalgoritme ({cipherOid})"),
                    };
                    sym.Key = cek;
                    return sym.DecryptCbc(cipherText, iv, PaddingMode.PKCS7);
                }
                catch (CryptographicException e) { last = e; }
            }
            throw last ?? new CryptographicException("Ingen modtager i filen");
        }
    }

    public static string? FirstCertPem(string text)
    {
        var rest = text.AsSpan();
        while (PemEncoding.TryFind(rest, out var f))
        {
            if (rest[f.Label].SequenceEqual("CERTIFICATE")) return rest[f.Location].ToString();
            rest = rest[f.Location.End..];
        }
        return null;
    }

    public static string? Uid(X509Certificate2 cert)
    {
        foreach (var rdn in cert.SubjectName.EnumerateRelativeDistinguishedNames())
            if (rdn.HasMultipleElements == false && rdn.GetSingleElementType().Value == UidOid) return rdn.GetSingleElementValue();
        return null;
    }

    /// <summary>Enheds-CA'en (RSA 2048, 10 år): NanoHUB godkender kun iPads med et certifikat fra den.</summary>
    public static KeyAndPem CreateCa(string org)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(new X500DistinguishedName(Dn($"Fleetdeck enheds-CA {Convert.ToHexString(RandomNumberGenerator.GetBytes(3))}", org)),
            rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddYears(10));
        return new(KeyPem(rsa), cert.ExportCertificatePem());
    }

    /// <summary>
    /// Identitet til én iPad: RSA 2048, udstedt af enheds-CA'en, som PKCS#12 med 3DES/SHA1 (det format iOS altid kan læse) og en ny adgangskode pr. profil.
    /// </summary>
    public static DeviceIdentity IssueDevice(string caCertPem, string caKeyPem, int ipadId, int years = 5)
    {
        using var ca = X509Certificate2.CreateFromPem(caCertPem, caKeyPem);
        using var rsa = RSA.Create(2048);
        var cn = $"fd-dev-{ipadId}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}";
        var req = new CertificateRequest($"CN={cn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], false));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        req.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, true, false));
        var notAfter = DateTimeOffset.UtcNow.AddYears(years);
        if (notAfter > ca.NotAfter) notAfter = ca.NotAfter.ToUniversalTime();
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        using var leaf = req.Create(ca, DateTimeOffset.UtcNow.AddHours(-1), notAfter, serial);
        using var withKey = leaf.CopyWithPrivateKey(rsa);
        var pw = Crypto.Token(18);
        var p12 = withKey.ExportPkcs12(Pkcs12ExportPbeParameters.Pkcs12TripleDesSha1, pw);
        return new(p12, pw, leaf.SerialNumber, leaf.NotAfter.ToUniversalTime(), cn);
    }

    public static string Thumbprint(string certPem)
    {
        using var c = X509Certificate2.CreateFromPem(certPem);
        return c.GetCertHashString(HashAlgorithmName.SHA256);
    }

    private static string Dn(string cn, string org)
    {
        var o = new string((org ?? "").Where(c => !char.IsControl(c) && c is not (',' or '+' or '"' or '\\' or '<' or '>' or ';' or '=')).ToArray()).Trim();
        return o.Length > 0 ? $"CN={cn}, O={o[..Math.Min(60, o.Length)]}" : $"CN={cn}";
    }
}

/// <summary>Én hændelse fra NanoHUBs webhook (MicroMDM-format).</summary>
public sealed record MdmEvent(string Topic, string? Udid, string? EnrollmentId, string? Status, string? CommandUuid,
    IReadOnlyDictionary<string, string> Params, Dictionary<string, object>? Payload, int? TokenUpdateTally, DateTime? CreatedAt)
{
    public string? EnrollKey => Params.TryGetValue("fd", out var k) && k.Length > 0 ? k : null;
}

public static class MdmHook
{
    public const string Authenticate = "mdm.Authenticate", TokenUpdate = "mdm.TokenUpdate", CheckOut = "mdm.CheckOut",
        Connect = "mdm.Connect", DeclarativeManagement = "mdm.DeclarativeManagement";

    /// <summary>Læs webhookens JSON. raw_payload (base64) læses som plist; kan den ikke læses, er Payload null.</summary>
    public static MdmEvent Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var topic = S(root, "topic") ?? throw new FormatException("Hændelsen har intet topic");
        var ev = root.TryGetProperty("acknowledge_event", out var a) && a.ValueKind == JsonValueKind.Object ? a
            : root.TryGetProperty("checkin_event", out var c) && c.ValueKind == JsonValueKind.Object ? c : default;
        var prms = new Dictionary<string, string>(StringComparer.Ordinal);
        Dictionary<string, object>? payload = null;
        string? udid = null, eid = null, status = null, cmd = null;
        int? tally = null;
        if (ev.ValueKind == JsonValueKind.Object)
        {
            udid = S(ev, "udid"); eid = S(ev, "enrollment_id"); status = S(ev, "status"); cmd = S(ev, "command_uuid");
            foreach (var name in new[] { "url_params", "params" })
                if (ev.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Object)
                    foreach (var kv in p.EnumerateObject())
                        if (kv.Value.ValueKind == JsonValueKind.String) prms[kv.Name] = kv.Value.GetString()!;
            if (ev.TryGetProperty("token_update_tally", out var t) && t.ValueKind == JsonValueKind.Number) tally = t.GetInt32();
            if (ev.TryGetProperty("raw_payload", out var raw) && raw.ValueKind == JsonValueKind.String)
            {
                try { payload = Plist.ParseDict(Encoding.UTF8.GetString(raw.GetBytesFromBase64())); }
                catch (Exception e) when (e is FormatException or InvalidOperationException) { payload = null; }
            }
        }
        udid ??= payload?.Str("UDID");
        cmd ??= payload?.Str("CommandUUID");
        status ??= payload?.Str("Status");
        DateTime? created = root.TryGetProperty("created_at", out var ca) && ca.ValueKind == JsonValueKind.String && ca.TryGetDateTime(out var dt) ? dt.ToUniversalTime() : null;
        return new(topic, udid, eid ?? udid, status, cmd, prms, payload, tally, created);
    }

    private static string? S(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>NanoHUB kan underskrive webhooken (X-Hmac-Signature: base64 HMAC-SHA256 af kroppen).</summary>
    public static bool HmacValid(byte[] body, string key, string? header)
    {
        if (string.IsNullOrEmpty(header) || key.Length == 0) return false;
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), body);
        byte[] got;
        try { got = Convert.FromBase64String(header.Trim()); } catch (FormatException) { return false; }
        return CryptographicOperations.FixedTimeEquals(mac, got);
    }

    /// <summary>DeviceInformation-svaret skrevet ind på iPad'ens række.</summary>
    public static void ApplyInfo(MdmDevice d, Dictionary<string, object> q)
    {
        static string? Cut(string? s, int n) => s is null ? null : s.Length > n ? s[..n] : s;
        d.DeviceName = Cut(q.Str("DeviceName"), 80) ?? d.DeviceName;
        d.OsVersion = Cut(q.Str("OSVersion"), 40) ?? d.OsVersion;
        d.BuildVersion = Cut(q.Str("BuildVersion"), 40) ?? d.BuildVersion;
        d.ModelName = Cut(q.Str("ModelName"), 80) ?? d.ModelName;
        d.ProductName = Cut(q.Str("ProductName"), 80) ?? d.ProductName;
        d.Serial = Cut(q.Str("SerialNumber"), 40) ?? d.Serial;
        if (q.Num("DeviceCapacity") is { } cap) d.StorageTotalGb = Math.Round(cap, 1);
        if (q.Num("AvailableDeviceCapacity") is { } free) d.StorageFreeGb = Math.Round(free, 1);
        if (q.Num("BatteryLevel") is { } bat and >= 0) d.BatteryLevel = Math.Round(bat, 2);
        if (q.Bool("IsSupervised") is { } sup) d.Supervised = sup;
        if (q.Bool("IsActivationLockEnabled") is { } al) d.ActivationLock = al;
    }

    /// <summary>Oplysningerne fra en check-in (Authenticate/TokenUpdate).</summary>
    public static void ApplyCheckin(MdmDevice d, Dictionary<string, object> p)
    {
        static string? Cut(string? s, int n) => s is null ? null : s.Length > n ? s[..n] : s;
        d.Serial = Cut(p.Str("SerialNumber"), 40) ?? d.Serial;
        d.ProductName = Cut(p.Str("ProductName"), 80) ?? d.ProductName;
        d.ModelName = Cut(p.Str("ModelName"), 80) ?? d.ModelName;
        d.OsVersion = Cut(p.Str("OSVersion"), 40) ?? d.OsVersion;
        d.BuildVersion = Cut(p.Str("BuildVersion"), 40) ?? d.BuildVersion;
        d.DeviceName = Cut(p.Str("DeviceName"), 80) ?? d.DeviceName;
    }

    /// <summary>Kort resultat til historikken (uden store data).</summary>
    public static string? ResultJson(Dictionary<string, object> report)
    {
        if (report.Dict("QueryResponses") is { } q)
            return JsonSerializer.Serialize(q.Where(kv => kv.Value is string or bool or long or double).ToDictionary(kv => kv.Key, kv => kv.Value));
        return null;
    }
}

/// <summary>Hvilken forældre-rettighed hver MDM-handling kræver (administratorer må alt).</summary>
public static class MdmPerms
{
    public const string Refresh = "refresh", Lock = "lock", Erase = "erase", LockedProfile = "locked", OsUpdate = "osupdate";

    public static string For(string action) => action switch
    {
        Refresh or OsUpdate => FamilyPerms.Update,
        Lock => FamilyPerms.Lock,
        Erase => FamilyPerms.Erase,
        LockedProfile => FamilyPerms.Rules,
        _ => throw new ArgumentException($"Ukendt handling: {action}"),
    };

    /// <summary>null = tilladt; ellers (HTTP-status, besked).</summary>
    public static (int Status, string Message)? Check(FamilyScope sc, int ipadId, string action)
    {
        if (!sc.Sees(ipadId)) return (404, "iPad'en findes ikke");
        if (!sc.Can(For(action))) return (403, "Det har du ikke adgang til");
        return null;
    }
}
