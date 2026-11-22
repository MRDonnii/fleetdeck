using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;

namespace Fleetdeck.Server.Services;

/// <summary>Kroppen i <c>POST /api/auth/kontrolrum</c>: hvem der logges ind, gyldighed, engangsværdi og signatur.</summary>
public sealed record KontrolrumAssertion(int? V, string? Aud, string? Sub, long? Iat, long? Exp, string? Nonce, string? Sig);

/// <summary>
/// Single sign-on fra et dashboard (fx Kontrolrum), der viser Fleetdeck indlejret og selv kræver login. Dashboardet signerer
/// en kortlivet påstand ("log ind som brugeren X") med en delt nøgle, i stedet for at gemme brugerens adgangskode. Fleetdeck
/// stoler dermed på dashboardet som identitetsudbyder for de brugere, der har fluebenet "Må logges ind fra Kontrolrum".
/// <para>
/// Formatet (docs/kontrolrum-sso.md): <c>sig</c> = hex(HMAC-SHA256(nøgle, tekst)), hvor nøglen er den viste hex-tekst som
/// UTF-8 (præcis som den indsættes i dashboardet), og tekst = <c>kontrolrum-sso\n{v}\n{aud}\n{sub}\n{iat}\n{exp}\n{nonce}</c>.
/// </para>
/// </summary>
public sealed class KontrolrumSso(IDataProtectionProvider dp, TimeProvider clock)
{
    public const string KeySetting = "kontrolrum_sso_key", CreatedSetting = "kontrolrum_sso_created";
    /// <summary>Brugte engangsværdier ligger i Settings-tabellen (overlever en genstart), til påstanden er udløbet.</summary>
    public const string NoncePrefix = "krsso:";
    public const string Context = "kontrolrum-sso", Audience = "fleetdeck", RateLimitPolicy = "kontrolrum-sso";
    public const int Version = 1;
    /// <summary>Længste tilladte levetid (exp - iat) og tilladt urforskel i sekunder.</summary>
    public const int MaxLifetime = 120, Skew = 60;
    public const int RatePermit = 10;
    public static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(5);

    private readonly IDataProtector protector = dp.CreateProtector("Fleetdeck.Kontrolrum.SsoKey");

    public long Now => clock.GetUtcNow().ToUnixTimeSeconds();

    /// <summary>32 tilfældige bytes som hex (64 tegn) – vises én gang og indsættes i dashboardet.</summary>
    public static string NewKey() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    public string Protect(string key) => protector.Protect(key);

    /// <summary>Den gemte nøgle i klartekst; null når SSO er slået fra eller nøglen ikke kan læses (fx efter flytning uden nøgler).</summary>
    public string? ReadKey(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        try { return protector.Unprotect(stored); }
        catch (CryptographicException) { return null; }
    }

    /// <summary>Kort fingeraftryk, så man kan se hvilken nøgle der er aktiv, uden at nøglen vises.</summary>
    public static string Fingerprint(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..8];

    public static string Text(KontrolrumAssertion a) => string.Join('\n', Context,
        a.V?.ToString(CultureInfo.InvariantCulture), a.Aud, a.Sub,
        a.Iat?.ToString(CultureInfo.InvariantCulture), a.Exp?.ToString(CultureInfo.InvariantCulture), a.Nonce);

    public static string Sign(string key, KontrolrumAssertion a) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(Text(a))));

    /// <summary>Alle felter er der, og engangsværdien har et fornuftigt format (den bruges i en nøgle i databasen).</summary>
    public static bool WellFormed(KontrolrumAssertion? a) =>
        a is { V: not null, Aud: not null, Sub.Length: > 0, Iat: not null, Exp: not null, Sig.Length: > 0 }
        && a.Nonce is { Length: >= 16 and <= 48 } n && n.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>Signaturen sammenlignet i konstant tid (hex, store eller små bogstaver).</summary>
    public static bool SignatureValid(string key, KontrolrumAssertion a)
    {
        var want = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(Text(a)));
        byte[] given;
        try { given = Convert.FromHexString(a.Sig ?? ""); }
        catch (FormatException) { return false; }
        return given.Length == want.Length && CryptographicOperations.FixedTimeEquals(given, want);
    }

    /// <summary>Version, modtager og tid. Null = i orden, ellers fejlkoden til svaret.</summary>
    public string? CheckClaims(KontrolrumAssertion a)
    {
        if (a.V != Version) return "bad_version";
        if (a.Aud != Audience) return "bad_audience";
        long iat = a.Iat!.Value, exp = a.Exp!.Value, now = Now;
        if (exp < iat || exp - iat > MaxLifetime || iat > now + Skew || exp + Skew < now) return "expired";
        return null;
    }

    /// <summary>Brug engangsværdien. false = den er brugt før (genafspilning). Gemmes, til påstanden ikke længere kan godkendes.</summary>
    public async Task<bool> TakeNonce(AppDb db, string nonce, long exp)
    {
        var now = Now;
        var like = NoncePrefix + "%";
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM Settings WHERE Key LIKE {like} AND CAST(Value AS INTEGER) < {now}");
        var until = Math.Max(exp, now) + Skew + 1;
        var added = await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT OR IGNORE INTO Settings (Key, Value) VALUES ({NoncePrefix + nonce}, {until.ToString(CultureInfo.InvariantCulture)})");
        return added == 1;
    }

    /// <summary>Rate limit pr. klient-IP (den rigtige IP: X-Forwarded-For læses kun fra betroede proxyer).</summary>
    public static void AddRateLimit(RateLimiterOptions o) =>
        o.AddPolicy(RateLimitPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
            "krsso|" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "?"),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = RatePermit, Window = RateWindow }));
}
