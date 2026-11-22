using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace Fleetdeck.Shared;

/// <summary>
/// macOS: fast lokal kodesignatur til agenten. macOS knytter "Fuld diskadgang" (TCC) til programmets signatur.
/// En ad hoc-signatur skifter ved hver ny version, så adgangen forsvinder ved hver selvopdatering.
/// Med et selvsigneret kodesigneringscertifikat, der laves én gang pr. Mac og bliver liggende, er kravet
/// "identifier com.fleetdeck.agent and certificate root = H&lt;hash&gt;" det samme for alle versioner, så adgangen bliver.
/// Her er kun det, der kan testes uden en Mac: certifikatet, kommandoerne og tolkning af svarene.
/// </summary>
public static partial class MacCodeSign
{
    public const string Identifier = "com.fleetdeck.agent";
    public const string CommonName = "Fleetdeck Agent (local)";
    public const string Dir = "/usr/local/fleetdeck";
    /// <summary>Egen nøglering (ikke System-nøgleringen: dens adgangskode kendes ikke, så nøglens partitionsliste kan ikke sættes).</summary>
    public const string Keychain = Dir + "/signing.keychain-db";
    /// <summary>Nøgleringens tilfældige adgangskode – kun læsbar for root.</summary>
    public const string PasswordFile = Dir + "/signing.pw";
    /// <summary>"&lt;hash af den signerede fil&gt; &lt;hash af filen fra serveren&gt;": agenten melder serverens version, selv om signaturen har ændret filen.</summary>
    public const string OriginFile = Dir + "/fleetdeck-agent.origin";
    /// <summary>security skal bruge roots egne indstillinger (sudo kan have beholdt brugerens HOME).</summary>
    public const string Env = "HOME=/var/root";

    public static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>Tilfældig adgangskode, der kan stå i en kommandolinje uden citering (hex).</summary>
    public static string NewPassword() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));

    /// <summary>
    /// Selvsigneret kodesigneringscertifikat (RSA 2048, SHA-256, extendedKeyUsage codeSigning) med nøgle som PKCS#12
    /// i det gamle 3DES/SHA-1-format, som "security import" kan læse (det nye AES-format fra OpenSSL 3 afvises af macOS).
    /// </summary>
    public static (byte[] Pfx, string Sha1) CreateIdentity(string password, string commonName = CommonName, int years = 30)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(new X500DistinguishedName($"CN={commonName}"), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.3", "Code Signing")], true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        var now = DateTimeOffset.UtcNow;
        using var cert = req.CreateSelfSigned(now.AddDays(-1), now.AddYears(years));
        return (cert.ExportPkcs12(Pkcs12ExportPbeParameters.Pkcs12TripleDesSha1, password), cert.Thumbprint.ToUpperInvariant());
    }

    // ------------------------------------------------------------------ kommandoer (køres som root i /bin/bash -c)
    public static string CreateKeychainCmd(string password, string keychain = Keychain) =>
        $"{Env} security create-keychain -p {password} {Q(keychain)} && {Env} security set-keychain-settings {Q(keychain)}";   // uden -t/-l: låses aldrig af sig selv

    public static string UnlockCmd(string password, string keychain = Keychain) =>
        $"{Env} security unlock-keychain -p {password} {Q(keychain)}";

    public static string ImportCmd(string pfxFile, string pfxPassword, string keychain = Keychain) =>
        $"{Env} security import {Q(pfxFile)} -k {Q(keychain)} -f pkcs12 -P {pfxPassword} -T /usr/bin/codesign -T /usr/bin/security";

    /// <summary>Uden partitionslisten spørger macOS (i en dialog, ingen kan se) om codesign må bruge nøglen.</summary>
    public static string PartitionCmd(string password, string keychain = Keychain) =>
        $"{Env} security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k {password} {Q(keychain)} >/dev/null";

    public static string FindIdentityCmd(string keychain = Keychain) =>
        $"{Env} security find-identity -p codesigning {Q(keychain)}";

    /// <summary>Signér med den faste identitet (SHA-1 af certifikatet), fast identifier og uden tidsstempel (kræver ikke internet).</summary>
    public static string SignCmd(string file, string sha1, string keychain = Keychain) =>
        $"{Env} codesign -f -s {sha1} --keychain {Q(keychain)} --identifier {Identifier} --timestamp=none {Q(file)}";

    /// <summary>Nødløsning: behold en gyldig signatur (fx den fra serveren), ellers ad hoc – Apple Silicon starter ikke usignerede programmer.</summary>
    public static string AdhocCmd(string file) =>
        $"codesign -v {Q(file)} 2>/dev/null || codesign -f -s - --identifier {Identifier} {Q(file)}";

    public static string DescribeCmd(string file) => $"codesign -dv --verbose=2 {Q(file)} 2>&1";

    // ------------------------------------------------------------------ tolkning
    [GeneratedRegex(@"^\s*\d+\)\s+([0-9A-Fa-f]{40})\s+""(.*)""", RegexOptions.Multiline)]
    private static partial Regex IdentityLine();

    /// <summary>SHA-1 for identiteten med det navn i svaret fra "security find-identity" (også når den ikke er "trusted").</summary>
    public static string? FindIdentity(string output, string commonName = CommonName) =>
        IdentityLine().Matches(output).Where(m => m.Groups[2].Value == commonName).Select(m => m.Groups[1].Value.ToUpperInvariant()).FirstOrDefault();

    /// <summary>Signaturen ifølge "codesign -dv --verbose=2": fast (vores certifikat) eller ad hoc/andet.</summary>
    public sealed record Signature(bool Stable, string Text, string? Identifier);

    public static Signature Describe(string output, string commonName = CommonName)
    {
        string? F(string k) => Regex.Match(output, "^" + k + @"=(.+)$", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value.Trim() : null;
        var id = F("Identifier");
        var authority = F("Authority");
        if (output.Contains("not signed at all", StringComparison.OrdinalIgnoreCase)) return new(false, "ikke signeret", null);
        if (authority == commonName) return new(id == Identifier, $"fast lokal signatur ({commonName})", id);
        if (F("Signature") == "adhoc" || output.Contains("flags=0x2(adhoc)", StringComparison.Ordinal)) return new(false, "ad hoc", id);
        return new(false, authority ?? "ukendt", id);
    }

    public static string OriginLine(string signedHash, string originalHash) => $"{signedHash} {originalHash}";

    /// <summary>
    /// Versionen, agenten skal melde: serverens hash, hvis filen er den, vi selv har signeret (origin-filen passer),
    /// ellers filens egen hash. Så ser serveren ikke en "anden version" og opdaterer i ring.
    /// </summary>
    public static string ReportedVersion(string currentHash, string? originText)
    {
        var p = (originText ?? "").Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
        return p.Length == 2 && p[0] == currentHash && p[1].Length > 0 ? p[1] : currentHash;
    }
}
