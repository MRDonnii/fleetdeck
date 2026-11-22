using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;

namespace Fleetdeck.Server.Services;

public sealed record EmergencyRevealInput(string? Password);
public sealed record EmergencyP12Input(string? Password, string? P12Password);

/// <summary>
/// Nødkortet (Indstillinger → Nødkort): hemmelighederne vises kun for en administrator, der skriver sin egen adgangskode igen.
/// Tjekket slår brugeren op i databasen (ikke kun cookiens rolle), og hvert forsøg – godkendt eller ej – skrives i loggen.
/// </summary>
public static class EmergencyAccess
{
    public const string RateLimitPolicy = "emergency";
    public const string AuditReveal = "emergency.reveal", AuditDenied = "emergency.denied";

    public sealed record Denial(int Status, string Message, string Reason);

    /// <summary>Null = godkendt. Ellers status (400/403) og en besked til brugerfladen.</summary>
    public static Denial? Check(User? u, string? password)
    {
        if (u is null) return new(403, "Kun administratorer kan se nødkortet", "ukendt bruger");
        if (u.Role != Roles.Admin) return new(403, "Kun administratorer kan se nødkortet", $"rolle {u.Role}");
        if (string.IsNullOrEmpty(password)) return new(400, "Skriv din adgangskode for at vise nødkortet", "ingen adgangskode");
        if (!Crypto.VerifyPassword(password, u.PasswordHash)) return new(403, "Forkert adgangskode", "forkert adgangskode");
        return null;
    }

    /// <summary>Slå den indloggede bruger op i databasen, tjek rolle og adgangskode, og skriv forsøget i loggen.</summary>
    public static Task<Denial?> Verify(AppDb db, AuditService audit, ClaimsPrincipal p, string? password, string what) =>
        Verify(p, password, what, id => db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id), (n, a, d) => audit.Log(n, a, d));

    public static async Task<Denial?> Verify(ClaimsPrincipal p, string? password, string what,
        Func<int, Task<User?>> findUser, Func<string, string, string, Task> log)
    {
        var id = int.TryParse(p.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var i) ? i : 0;
        var u = id > 0 ? await findUser(id) : null;
        var d = Check(u, password);
        await log(u?.Username ?? p.Identity?.Name ?? "?", d is null ? AuditReveal : AuditDenied, d is null ? what : $"{what}: {d.Reason}");
        return d;
    }
}

public sealed record EmergencyMdm(string State, bool? Supervised, bool? ActivationLock, DateTime? LastCheckin, bool LockedViaMdm, string ProfileName);

/// <summary>Én iPad på nødkortet. Locked: "static" (låst profil lagt på fra Mac'en), "mdm" (via Apple-styring) eller "none".</summary>
public sealed record EmergencyIpad(
    int Id, string Name, string? Owner, string Kind, string? Model, string? Serial, bool Supervised,
    string Locked, string LockedProfileName, string? RemovalPassword, string? BackupPassword, EmergencyMdm? Mdm);

public sealed record EmergencyTunnel(bool Configured, string Container, string? TunnelId, string? Machine);

/// <summary>Overvågningsidentiteten (Apple Configurator / cfgutil). Fingeraftrykket er SHA-256 af certifikatet.</summary>
public sealed record EmergencySupervision(bool Available, string? Fingerprint, string? Subject, DateTime? NotAfter, string Note);

/// <param name="BackupPassword">Husstandens backup-adgangskode (den samme for alle iPads – Mac og USB). null = ikke lavet endnu.</param>
/// <param name="PreviousBackupPassword">"iPad via USB"s gamle kode fra før sammenlægningen, hvis den er en anden (ældre USB-backupper).</param>
public sealed record EmergencySheetData(
    DateTime Generated, string ServerUrl, string DohHost, string AdguardUrl, string MdmHost, EmergencyTunnel Tunnel,
    EmergencySupervision Supervision, List<EmergencyIpad> Ipads, string? BackupPassword = null, string? PreviousBackupPassword = null);

/// <summary>Kilden til overvågningsidentiteten (certifikat + privat nøgle) til nødkortet og .p12-hentningen.</summary>
public interface ISupervisionIdentitySource
{
    Task<X509Certificate2?> Get(CancellationToken ct);
}

/// <summary>Husstandens identitet fra <see cref="IpadSupervision"/> (den samme, Mac'en og "iPad via USB" bruger). Laver aldrig en ny.</summary>
public sealed class HouseholdSupervisionIdentity(IpadSupervision sv) : ISupervisionIdentitySource
{
    public async Task<X509Certificate2?> Get(CancellationToken ct)
    {
        if (await sv.Existing() is not { } id) return null;
        return EmergencyExport.WithKey(id.Cert, id.Key);
    }
}

public sealed class NoSupervisionIdentity : ISupervisionIdentitySource
{
    public Task<X509Certificate2?> Get(CancellationToken ct) => Task.FromResult<X509Certificate2?>(null);
}

public static class EmergencyExport
{
    public const int MinP12Password = 8;

    public static string Fingerprint(X509Certificate2 c) =>
        string.Join(':', Convert.ToHexString(SHA256.HashData(c.RawData)).Chunk(2).Select(x => new string(x)));

    /// <summary>Certifikat (DER) + RSA-nøgle (PKCS#1 DER) → certifikat med privat nøgle (kan eksporteres som .p12).</summary>
    public static X509Certificate2 WithKey(byte[] certDer, byte[] keyDer)
    {
        using var cert = X509CertificateLoader.LoadCertificate(certDer);
        using var rsa = RSA.Create(); rsa.ImportRSAPrivateKey(keyDer, out _);
        return cert.CopyWithPrivateKey(rsa);
    }

    public static string? ValidateP12Password(string? pw) =>
        string.IsNullOrEmpty(pw) || pw.Length < MinP12Password ? $"Vælg en adgangskode til filen på mindst {MinP12Password} tegn" : null;

    /// <summary>Identiteten som .p12, krypteret med AES-256 og den valgte adgangskode (kræver den private nøgle).</summary>
    public static byte[] Pkcs12(X509Certificate2 c, string password)
    {
        if (!c.HasPrivateKey) throw new InvalidOperationException("Identiteten har ingen privat nøgle");
        return c.ExportPkcs12(new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000), password);
    }

    /// <summary>Tunnelens id fra Cloudflare-nøglen (base64-JSON med a/t/s). Kun id'et – aldrig hemmeligheden "s".</summary>
    public static string? TunnelId(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        try
        {
            var t = token.Trim().Replace('-', '+').Replace('_', '/');
            t = t.PadRight(t.Length + (4 - t.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(t)));
            return doc.RootElement.TryGetProperty("t", out var id) && id.ValueKind == JsonValueKind.String && Guid.TryParse(id.GetString(), out var g) ? g.ToString() : null;
        }
        catch (Exception e) when (e is FormatException or JsonException or ArgumentException) { return null; }
    }
}

/// <summary>Samler nødkortet. Kaldes først, når <see cref="EmergencyAccess.Verify"/> har godkendt brugeren.</summary>
public sealed class EmergencySheetService(AppDb db, LockedProfiles locked, FamilySettings fs, MdmSettings ms, PublicAccessSettings pa,
    SettingsService s, ISupervisionIdentitySource supervision, IpadSupervision household)
{
    public async Task<EmergencySheetData> Build(CancellationToken ct)
    {
        var backupPw = await household.ExistingBackupPassword();
        var ipads = await db.Ipads.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct);
        var mdm = await db.MdmDevices.AsNoTracking().ToDictionaryAsync(x => x.IpadId, ct);
        var rows = ipads.Select(p =>
        {
            var d = mdm.GetValueOrDefault(p.Id);
            var lockedKind = d is { LockedViaMdm: true } ? "mdm" : p.LockedProfileApplied is not null || p.RemovalPassword is not null ? "static" : "none";
            return new EmergencyIpad(p.Id, p.Name, p.Owner, p.Kind, p.Model, p.Serial ?? d?.Serial, p.Supervised || d?.Supervised == true,
                lockedKind, $"Fleetdeck – {p.Name} (låst v{p.LockedProfileApplied ?? p.LockedProfileVersion})",
                locked.Existing(p), backupPw,
                d is null ? null : new EmergencyMdm(d.State, d.Supervised, d.ActivationLock, d.LastCheckin, d.LockedViaMdm, $"Fleetdeck – {p.Name}"));
        }).ToList();

        var tc = await pa.Config();
        var host = tc.MachineId is int mid ? await db.Machines.AsNoTracking().Where(m => m.Id == mid).Select(m => m.DisplayName ?? m.Name).FirstOrDefaultAsync(ct) : null;
        var tunnel = new EmergencyTunnel(await pa.HasToken(), tc.Container, EmergencyExport.TunnelId(await pa.Token()), host);

        using var id = await supervision.Get(ct);
        var sup = id is null
            ? new EmergencySupervision(false, null, null, null,
                "Fleetdeck har endnu ingen overvågningsidentitet – den laves første gang en iPad gøres overvåget eller får den låste profil (via Mac eller USB). " +
                "Er en iPad overvåget med Apple Configurator på en Mac, ligger den identitet kun dér: tag en kopi og gem den sikkert.")
            : new EmergencySupervision(true, EmergencyExport.Fingerprint(id), id.Subject, id.NotAfter.ToUniversalTime(),
                "Husstandens identitet – den samme til Mac (Apple Configurator/cfgutil) og iPad via USB. Hent den som krypteret .p12 og gem den i en adgangskodeboks " +
                "sammen med filens adgangskode. Uden den kan de overvågede iPads ikke styres fra en ny server uden at blive nulstillet.");

        return new EmergencySheetData(DateTime.UtcNow, await s.Get("server_url"), await fs.DohHost(), await s.Get("adguard_url"),
            await ms.PublicHost(), tunnel, sup, rows, backupPw, await household.PreviousUsbBackupPassword());
    }
}
