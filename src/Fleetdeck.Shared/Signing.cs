using System.Security.Cryptography;

namespace Fleetdeck.Shared;

/// <summary>Seneste fejl ved selvopdatering (sendes med ved check-in, så serveren kan vise en alarm). Null = ingen fejl.</summary>
public static class SelfUpdate
{
    public static string? LastError { get; set; }
    public static int Failures { get; set; }
    public static void Failed(string why) { LastError = why; Failures++; }
    public static void Ok() { LastError = null; Failures = 0; }
}

/// <summary>
/// Signerede agenter: serveren signerer SHA-256 af hver agentfil med sin egen nøgle (ECDSA P-256).
/// Agenten husker serverens offentlige nøgle fra første kontakt og opdaterer kun til filer, der er signeret med den.
/// Så kan en falsk server på netværket ikke skubbe en anden agent ud.
/// </summary>
public static class AgentSigning
{
    public const string SignatureHeader = "X-Fleetdeck-Signature", HashHeader = "X-Fleetdeck-Sha256";

    /// <summary>Kort fingeraftryk af en offentlig nøgle (til log og websiden).</summary>
    public static string Fingerprint(string publicPem)
    {
        using var k = ECDsa.Create();
        k.ImportFromPem(publicPem);
        return Convert.ToHexStringLower(SHA256.HashData(k.ExportSubjectPublicKeyInfo()))[..16];
    }

    public static string Sign(ECDsa key, byte[] sha256) => Convert.ToBase64String(key.SignHash(sha256));

    /// <summary>Er filen med denne SHA-256 signeret af nøglen? Falsk ved enhver fejl.</summary>
    public static bool Verify(string publicPem, byte[] sha256, string? signatureB64)
    {
        if (string.IsNullOrEmpty(signatureB64)) return false;
        try
        {
            using var k = ECDsa.Create();
            k.ImportFromPem(publicPem);
            return k.VerifyHash(sha256, Convert.FromBase64String(signatureB64));
        }
        catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException) { return false; }
    }

    /// <summary>Tjek en hentet fil mod den huskede nøgle: kontrolsum fra serveren skal passe med filen, og signaturen med nøglen.</summary>
    /// <summary>
    /// Selvopdatering: kræver altid en husket nøgle. Uden den installeres intet (før blev en usigneret fil accepteret, indtil
    /// nøglen var husket – nu henter agenten nøglen først og afviser opdateringen, hvis det ikke lykkes).
    /// </summary>
    public static (bool Ok, string Why) CheckPinned(string file, string? pinnedPem, string? hashHeader, string? signature) =>
        pinnedPem is { Length: > 0 } key ? Check(file, key, hashHeader, signature)
            : (false, "serverens nøgle er ikke husket endnu – en usigneret opdatering installeres ikke");

    public static (bool Ok, string Why) Check(string file, string pinnedPem, string? hashHeader, string? signature)
    {
        byte[] hash;
        using (var fs = File.OpenRead(file)) hash = SHA256.HashData(fs);
        if (hashHeader is { Length: > 0 } h && !string.Equals(h, Convert.ToHexStringLower(hash), StringComparison.OrdinalIgnoreCase))
            return (false, "filen passer ikke med serverens kontrolsum");
        if (string.IsNullOrEmpty(signature)) return (false, "filen er ikke signeret");
        return Verify(pinnedPem, hash, signature) ? (true, "") : (false, "signaturen passer ikke med serverens nøgle");
    }
}
