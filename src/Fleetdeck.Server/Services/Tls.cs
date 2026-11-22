using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Fleetdeck.Shared;

namespace Fleetdeck.Server.Services;

/// <summary>
/// Valgfri https direkte i Fleetdeck: HTTPS_PORT=8586 slår det til (http-porten kører videre, så ingen agent mister forbindelsen).
/// Certifikatet er HTTPS_CERT + HTTPS_KEY (PEM), ellers laves et selvsigneret certifikat i data/keys første gang.
/// Agenterne fastlåser certifikatets offentlige nøgle (<see cref="Pin"/>), så et selvsigneret certifikat er nok på et hjemmenet.
/// </summary>
public sealed class TlsInfo
{
    public int? HttpsPort { get; init; }
    private X509Certificate2? certificate;
    /// <summary>Certifikatet, Kestrel bruger nu. Kan skiftes uden genstart (<see cref="AddName"/>); nøglen – og dermed pin – er den samme.</summary>
    public X509Certificate2? Certificate { get => Volatile.Read(ref certificate); init => certificate = value; }
    /// <summary>Fleetdecks eget certifikat i data/keys (ikke HTTPS_CERT): kan få flere navne.</summary>
    public string? OwnDir { get; init; }
    /// <summary>SHA-256 af certifikatets SubjectPublicKeyInfo (base64) – samme format som curl --pinnedpubkey sha256//…</summary>
    public string? Pin { get; init; }
    public bool SelfSigned { get; init; }
    public string? Error { get; init; }

    public const string CertFile = "tls-cert.pem", KeyFile = "tls-key.pem";

    public static TlsInfo Load(Paths paths, ILogger? log = null)
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("HTTPS_PORT"), out var port) || port is <= 0 or > 65535) return new();
        try
        {
            var certPath = Environment.GetEnvironmentVariable("HTTPS_CERT");
            var keyPath = Environment.GetEnvironmentVariable("HTTPS_KEY");
            var own = string.IsNullOrEmpty(certPath);
            if (own)
            {
                var dir = Path.Combine(paths.Data, "keys");
                certPath = Path.Combine(dir, CertFile); keyPath = Path.Combine(dir, KeyFile);
                if (!File.Exists(certPath) || !File.Exists(keyPath)) CreateSelfSigned(dir, Hosts());
            }
            using var pem = X509Certificate2.CreateFromPemFile(certPath!, string.IsNullOrEmpty(keyPath) ? null : keyPath);
            // via PKCS#12, så nøglen også kan bruges af SslStream på Windows
            var cert = X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
            var pin = TlsPinning.Of(cert);
            log?.LogInformation("https på port {Port} ({Kind}, nøgle {Pin})", port, own ? "selvsigneret" : "eget certifikat", pin);
            return new() { HttpsPort = port, Certificate = cert, Pin = pin, SelfSigned = own || cert.Subject == cert.Issuer,
                OwnDir = own ? Path.GetDirectoryName(certPath) : null };
        }
        catch (Exception e) when (e is CryptographicException or IOException or ArgumentException or UnauthorizedAccessException)
        {
            log?.LogError("https kunne ikke startes: {Msg}", e.Message);
            return new() { Error = e.Message };
        }
    }

    /// <summary>Navne og adresser i det selvsignerede certifikat (ikke afgørende: agenterne kontrollerer den fastlåste nøgle).</summary>
    private static IEnumerable<string> Hosts()
    {
        yield return "localhost";
        yield return Environment.MachineName;
        if (Uri.TryCreate(Environment.GetEnvironmentVariable("SERVER_URL"), UriKind.Absolute, out var u)) yield return u.Host;
    }

    public static void CreateSelfSigned(string dir, IEnumerable<string> hosts)
    {
        Directory.CreateDirectory(dir);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Write(Path.Combine(dir, KeyFile), key.ExportPkcs8PrivateKeyPem());
        Write(Path.Combine(dir, CertFile), Issue(key, hosts));
    }

    /// <summary>Navnene (DNS og IP) i certifikatets Subject Alternative Name.</summary>
    public static List<string> Names(X509Certificate2 cert)
    {
        var names = new List<string>();
        foreach (var ext in cert.Extensions.OfType<X509SubjectAlternativeNameExtension>())
        {
            names.AddRange(ext.EnumerateDnsNames());
            names.AddRange(ext.EnumerateIPAddresses().Select(ip => ip.ToString()));
        }
        return names;
    }

    /// <summary>Dækker certifikatet adressen, browseren bruger? (IP præcist; DNS uden forskel på store/små bogstaver, *.domæne ét led.)</summary>
    public static bool Covers(IEnumerable<string> names, string host)
    {
        host = host.Trim().TrimStart('[').TrimEnd(']').TrimEnd('.');
        if (IPAddress.TryParse(host, out var ip)) return names.Any(n => IPAddress.TryParse(n, out var o) && o.Equals(ip));
        return names.Any(n => n.Equals(host, StringComparison.OrdinalIgnoreCase)
            || (n.StartsWith("*.", StringComparison.Ordinal) && host.IndexOf('.') is var dot and > 0
                && host[(dot + 1)..].Equals(n[2..], StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Et navn eller en IP, som kan stå i certifikatet (ingen jokertegn, port eller sti).</summary>
    public static bool ValidHost(string? host) =>
        host is { Length: > 0 and <= 253 } h && (IPAddress.TryParse(h, out _)
            || System.Text.RegularExpressions.Regex.IsMatch(h, @"^(?=.{1,253}$)([A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)(\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$"));

    /// <summary>
    /// Tilføjer et navn til Fleetdecks eget certifikat: nyt certifikat med samme nøgle (agenternes fastlåste pin ændres ikke),
    /// gemt i data/keys og taget i brug med det samme. Returnerer fejlen eller null.
    /// </summary>
    public string? AddName(string host)
    {
        if (OwnDir is null || Certificate is not { } cur) return "Kun Fleetdecks eget certifikat kan få flere navne";
        if (!ValidHost(host)) return "Ugyldigt navn eller IP-adresse";
        var names = Names(cur);
        if (Covers(names, host)) return null;
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(File.ReadAllText(Path.Combine(OwnDir, KeyFile)));
            var pem = Issue(key, [.. names, host]);
            using var fresh = X509Certificate2.CreateFromPem(pem, File.ReadAllText(Path.Combine(OwnDir, KeyFile)));
            var next = X509CertificateLoader.LoadPkcs12(fresh.Export(X509ContentType.Pkcs12), null);
            if (TlsPinning.Of(next) != Pin) return "Den nye nøgle passer ikke – certifikatet er ikke ændret";
            Write(Path.Combine(OwnDir, CertFile), pem);
            Volatile.Write(ref certificate, next);
            return null;
        }
        catch (Exception e) when (e is CryptographicException or IOException or ArgumentException or UnauthorizedAccessException)
        {
            return "Certifikatet kunne ikke ændres: " + e.Message;
        }
    }

    private static string Issue(ECDsa key, IEnumerable<string> hosts)
    {
        var req = new CertificateRequest("CN=Fleetdeck", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var h in hosts.Where(h => h.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            if (IPAddress.TryParse(h, out var ip)) san.AddIpAddress(ip); else san.AddDnsName(h);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
        return cert.ExportCertificatePem();
    }

    private static void Write(string file, string text)
    {
        File.WriteAllText(file, text);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
