using System.Security.Cryptography;
using System.Text;
using Fleetdeck.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Server.Services;

/// <summary>
/// Tilmeldingen i en netværksinstallation (Linux-svarfilen, som PXE-klienten henter uden login) bruger en engangskode pr. udrulning
/// i stedet for den globale tilmeldingsnøgle. Den, der opsnapper svarfilen, får derfor kun en kode, der virker én gang, udløber
/// og ved en geninstallation kun gælder netop den maskine – aldrig nøglen til hele flåden.
/// </summary>
/// <remarks>
/// Koden afledes af en hemmelighed på serveren og udrulningens id (HMAC), så samme svarfil kan hentes flere gange med samme kode,
/// uden at klarteksten gemmes. Kun en hash gemmes som en almindelig engangskode.
/// </remarks>
public static class DeployInstallKey
{
    public const string SecretSetting = "deploy_code_secret";
    public static readonly TimeSpan Valid = TimeSpan.FromDays(2);

    public static string Code(string secret, Deployment d)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"deploy|{d.Id}|{d.Created.Ticks}"));
        return EnrollmentService.CodePrefix + Convert.ToBase64String(mac).TrimEnd('=').Replace('+', '-').Replace('/', '_')[..20];
    }

    /// <summary>Koden til udrulningen. Første gang registreres den som engangskode; så længe den ikke er brugt, holdes den gyldig.</summary>
    public static async Task<string> Get(AppDb db, SettingsService settings, Deployment d, DateTime? at = null)
    {
        var now = at ?? DateTime.UtcNow;
        var secret = await settings.Get(SecretSetting);
        if (secret.Length < 32) { secret = Crypto.Token(32); await settings.Set(SecretSetting, secret); }
        var code = Code(secret, d);
        var hash = Crypto.Sha256(code);
        var row = await db.EnrollCodes.FirstOrDefaultAsync(c => c.CodeHash == hash);
        if (row is null)
            db.EnrollCodes.Add(new EnrollCode
            {
                CodeHash = hash, Hint = code[..7], Label = $"Netværksinstallation af {d.PcName}".Trim() is { Length: > 100 } l ? l[..100] : $"Netværksinstallation af {d.PcName}".Trim(),
                ReplaceMachineId = d.MachineId, ProfileId = d.ProfileId, CreatedBy = "netværksinstallation", Expires = now + Valid,
            });
        else if (row.UsedAt is null && row.Expires < now + Valid / 2) row.Expires = now + Valid;   // udrulningen er stadig i gang
        await db.SaveChangesAsync();
        return code;
    }
}
