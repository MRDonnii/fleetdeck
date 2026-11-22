using System.Buffers.Binary;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Fleetdeck.Server.Data;

namespace Fleetdeck.Server.Services;

/// <summary>TOTP efter RFC 6238 (HOTP/RFC 4226 med tiden som tæller). Standard: SHA-1, 6 cifre, 30 sekunder – det alle autentificerings-apps kan.</summary>
public static class Totp
{
    public const int Digits = 6, Period = 30;
    private static readonly int[] Pow10 = [1, 10, 100, 1_000, 10_000, 100_000, 1_000_000, 10_000_000, 100_000_000];

    public static long Step(DateTimeOffset t) => t.ToUnixTimeSeconds() / Period;

    /// <summary>Koden for et bestemt tidstrin. <paramref name="alg"/> er "SHA1", "SHA256" eller "SHA512".</summary>
    public static string Code(byte[] key, long step, int digits = Digits, string alg = "SHA1")
    {
        Span<byte> msg = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(msg, step);
        var h = alg switch
        {
            "SHA256" => HMACSHA256.HashData(key, msg),
            "SHA512" => HMACSHA512.HashData(key, msg),
            _ => HMACSHA1.HashData(key, msg),
        };
        var o = h[^1] & 0x0f;
        var bin = ((h[o] & 0x7f) << 24) | (h[o + 1] << 16) | (h[o + 2] << 8) | h[o + 3];
        return (bin % Pow10[digits]).ToString().PadLeft(digits, '0');
    }

    /// <summary>
    /// Finder det tidstrin, koden passer til (±<paramref name="window"/> trin for urforskel), og kun trin efter
    /// <paramref name="lastStep"/>, så en brugt kode ikke kan genbruges. Null = ingen match.
    /// </summary>
    public static long? Match(byte[] key, string? code, DateTimeOffset now, long lastStep, int window = 1)
    {
        var c = new string((code ?? "").Where(char.IsAsciiDigit).ToArray());
        if (c.Length != Digits || (code ?? "").Count(ch => !char.IsWhiteSpace(ch) && ch != '-') != Digits) return null;
        var s = Step(now);
        long? hit = null;
        for (var i = -window; i <= window; i++)
        {
            var st = s + i;
            if (st <= lastStep) continue;
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(key, st)), Encoding.ASCII.GetBytes(c))) hit ??= st;
        }
        return hit;
    }

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(20);   // 160 bit som anbefalet i RFC 4226

    /// <summary>otpauth-adressen, som QR-koden indeholder.</summary>
    public static string Uri(string issuer, string account, string base32) =>
        $"otpauth://totp/{System.Uri.EscapeDataString(issuer)}:{System.Uri.EscapeDataString(account)}" +
        $"?secret={base32}&issuer={System.Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={Period}";
}

/// <summary>Base32 (RFC 4648) uden udfyldning – formatet til manuel indtastning i en autentificerings-app.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b; bits += 8;
            while (bits >= 5) { sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    /// <summary>Tåler små bogstaver, mellemrum, bindestreger og '='. Null ved ugyldige tegn.</summary>
    public static byte[]? Decode(string? s)
    {
        var res = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var ch in (s ?? "").ToUpperInvariant())
        {
            if (ch is ' ' or '-' or '=') continue;
            var v = Alphabet.IndexOf(ch);
            if (v < 0) return null;
            buffer = (buffer << 5) | v; bits += 5;
            if (bits >= 8) { res.Add((byte)(buffer >> (bits - 8))); bits -= 8; }
            buffer &= 0xff;
        }
        return res.ToArray();
    }
}

/// <summary>Engangs-gendannelseskoder: 10 stk. á 50 bit, vist én gang og gemt som PBKDF2-hash.</summary>
public static class RecoveryCodes
{
    public const int Count = 10;
    // Crockford-agtigt alfabet uden i, l, o og u (forveksles let)
    private const string Alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
    private const int Iterations = 20_000;

    public static List<string> Generate(int n = Count) => Enumerable.Range(0, n).Select(_ =>
    {
        var s = new string(Enumerable.Range(0, 10).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray());
        return $"{s[..5]}-{s[5..]}";
    }).ToList();

    public static string Normalize(string? code) => new((code ?? "").ToLowerInvariant()
        .Where(c => c is not ('-' or ' ')).Select(c => c switch { 'o' => '0', 'i' or 'l' => '1', _ => c }).ToArray());

    public static string Hash(string code)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var h = Rfc2898DeriveBytes.Pbkdf2(Normalize(code), salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"rc1${Convert.ToHexString(salt)}${Convert.ToHexString(h)}";
    }

    /// <summary>Indekset på den hash, koden passer til, eller -1.</summary>
    public static int Find(IReadOnlyList<string> hashes, string? code)
    {
        var c = Normalize(code);
        if (c.Length != 10) return -1;
        for (var i = 0; i < hashes.Count; i++)
        {
            var p = hashes[i].Split('$');
            if (p.Length != 3 || p[0] != "rc1") continue;
            var h = Rfc2898DeriveBytes.Pbkdf2(c, Convert.FromHexString(p[1]), Iterations, HashAlgorithmName.SHA256, 32);
            if (CryptographicOperations.FixedTimeEquals(h, Convert.FromHexString(p[2]))) return i;
        }
        return -1;
    }
}

/// <summary>Spærring pr. bruger efter gentagne fejl (adgangskode eller kode): 5 fejl = 1 min, derefter dobbelt så længe, højst 15 min.</summary>
public static class Lockout
{
    public const int Threshold = 5;
    public static readonly TimeSpan Max = TimeSpan.FromMinutes(15);

    public static TimeSpan? Remaining(User u, DateTime now) => u.LockoutUntil is { } t && t > now ? t - now : null;

    public static void Fail(User u, DateTime now)
    {
        u.FailedLogins++;
        if (u.FailedLogins < Threshold) return;
        var minutes = Math.Min(Math.Pow(2, Math.Min(u.FailedLogins - Threshold, 10)), Max.TotalMinutes);
        u.LockoutUntil = now.AddMinutes(minutes);
    }

    public static void Success(User u) { u.FailedLogins = 0; u.LockoutUntil = null; }

    public static string Message(TimeSpan wait) =>
        $"For mange mislykkede forsøg. Prøv igen om {Math.Max(1, (int)Math.Ceiling(wait.TotalMinutes))} min.";
}

public enum LoginStep { SignIn, Code, SignInMustEnroll }

/// <summary>Politik: totrinsbekræftelse valgfri, påkrævet for administratorer eller påkrævet for alle.</summary>
public static class TwoFactorPolicy
{
    public const string SettingKey = "twofa_policy", Optional = "optional", Admins = "admins", All = "all";
    /// <summary>Claim i sessionen: "1" når brugeren har totrinsbekræftelse slået til (sat ved login; skift roterer SecurityStamp).</summary>
    public const string Claim = "tfa";

    private static volatile string current = Optional;
    public static string Current { get => current; set => current = Valid(value) ? value! : Optional; }

    public static bool Valid(string? p) => p is Optional or Admins or All;
    public static bool RequiredFor(string policy, string? role) => policy == All || (policy == Admins && role == Roles.Admin);

    /// <summary>Må sessionen bruge Fleetdeck? Nej, hvis politikken kræver totrinsbekræftelse og brugeren ikke har den endnu.</summary>
    public static bool Satisfied(ClaimsPrincipal p, string policy) =>
        p.Identity?.IsAuthenticated == true && (p.FindFirstValue(Claim) == "1" || !RequiredFor(policy, p.FindFirstValue(ClaimTypes.Role)));

    /// <summary>Hvad sker der, når adgangskoden er rigtig?</summary>
    public static LoginStep Next(bool enabled, bool trustedDevice, string role, string policy) =>
        enabled ? (trustedDevice ? LoginStep.SignIn : LoginStep.Code)
        : RequiredFor(policy, role) ? LoginStep.SignInMustEnroll : LoginStep.SignIn;
}

/// <summary>
/// Kræves af alle politikker undtagen "session": en bruger, der mangler påkrævet totrinsbekræftelse, kan kun se sin status,
/// sætte den op og logge ud. Gælder også SignalR-hubben (standardpolitikken).
/// </summary>
public sealed class TwoFactorRequirement : AuthorizationHandler<TwoFactorRequirement>, IAuthorizationRequirement
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, TwoFactorRequirement requirement)
    {
        if (TwoFactorPolicy.Satisfied(context.User, TwoFactorPolicy.Current)) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

public enum CodeKind { Invalid, Totp, Recovery }

/// <summary>TOTP-hemmeligheder, login-billetter, tilmeldingstokens og "Husk denne enhed"-cookien.</summary>
public sealed class TwoFactorService(IDataProtectionProvider dp, TimeProvider clock)
{
    public const string TrustCookie = "ws_trust";
    public const int RememberDays = 30;
    public const string Issuer = "Fleetdeck";

    private readonly IDataProtector secrets = dp.CreateProtector("Fleetdeck.Totp.Secret");
    private readonly ITimeLimitedDataProtector tickets = dp.CreateProtector("Fleetdeck.Totp.LoginTicket").ToTimeLimitedDataProtector();
    private readonly ITimeLimitedDataProtector enroll = dp.CreateProtector("Fleetdeck.Totp.Enroll").ToTimeLimitedDataProtector();
    private readonly ITimeLimitedDataProtector trust = dp.CreateProtector("Fleetdeck.Totp.TrustedDevice").ToTimeLimitedDataProtector();

    public DateTime Now => clock.GetUtcNow().UtcDateTime;

    public static void RotateStamp(User u) => u.SecurityStamp = Guid.NewGuid().ToString("N");

    public byte[]? Secret(User u)
    {
        if (u.TotpSecret is null) return null;
        try { return Base32.Decode(secrets.Unprotect(u.TotpSecret)); }
        catch (CryptographicException) { return null; }   // nøglerne er mistet: brug en gendannelseskode
    }

    // --- Login: billet mellem adgangskode og kode (i svaret, ikke en cookie), 10 minutter ---
    public string LoginTicket(User u) => tickets.Protect($"{u.Id}|{u.SecurityStamp}", clock.GetUtcNow().AddMinutes(10));

    public (int Id, string Stamp)? ReadTicket(string? ticket) => Read(tickets, ticket) is [var id, var stamp] && int.TryParse(id, out var i) ? (i, stamp) : null;

    // --- Tilmelding: hemmeligheden følger med i et tidsbegrænset, krypteret token, til den er bekræftet med en kode ---
    public (string Secret, string Uri, string Token) StartEnroll(User u)
    {
        var b32 = Base32.Encode(Totp.NewSecret());
        return (b32, Totp.Uri(Issuer, u.Username, b32), enroll.Protect($"{u.Id}|{u.SecurityStamp}|{b32}", clock.GetUtcNow().AddMinutes(15)));
    }

    /// <summary>Bekræft tilmeldingen: koden skal passe til den nye hemmelighed. Returnerer gendannelseskoderne (i klartekst, kun denne ene gang).</summary>
    public List<string>? ConfirmEnroll(User u, string? token, string? code)
    {
        if (Read(enroll, token) is not [var id, var stamp, var b32] || id != u.Id.ToString() || stamp != u.SecurityStamp) return null;
        var key = Base32.Decode(b32);
        if (key is null || Totp.Match(key, code, clock.GetUtcNow(), 0) is not { } step) return null;
        u.TotpSecret = secrets.Protect(b32);
        u.TotpEnabledAt = Now;
        u.TotpLastStep = step;
        RotateStamp(u);   // andre sessioner og huskede enheder ugyldiggøres
        return NewRecoveryCodes(u);
    }

    public List<string> NewRecoveryCodes(User u)
    {
        var codes = RecoveryCodes.Generate();
        u.RecoveryCodes = codes.Select(RecoveryCodes.Hash).ToList();
        return codes;
    }

    public static void Disable(User u)
    {
        u.TotpSecret = null;
        u.TotpEnabledAt = null;
        u.TotpLastStep = 0;
        u.RecoveryCodes = [];
        RotateStamp(u);
    }

    /// <summary>En 6-cifret kode fra appen eller en gendannelseskode (som så er brugt). Ændrer brugeren – husk at gemme.</summary>
    public CodeKind Verify(User u, string? code)
    {
        if (!u.TotpEnabled) return CodeKind.Invalid;
        var compact = new string((code ?? "").Where(c => c is not (' ' or '-')).ToArray());
        if (compact.Length == Totp.Digits && compact.All(char.IsAsciiDigit) && Secret(u) is { } key)
        {
            if (Totp.Match(key, code, clock.GetUtcNow(), u.TotpLastStep) is not { } step) return CodeKind.Invalid;
            u.TotpLastStep = step;
            return CodeKind.Totp;
        }
        var i = RecoveryCodes.Find(u.RecoveryCodes, code);
        if (i < 0) return CodeKind.Invalid;
        u.RecoveryCodes = u.RecoveryCodes.Where((_, j) => j != i).ToList();
        return CodeKind.Recovery;
    }

    // --- "Husk denne enhed i 30 dage": krypteret, signeret cookie bundet til bruger og SecurityStamp ---
    public string TrustValue(User u) => trust.Protect($"{u.Id}|{u.SecurityStamp}", clock.GetUtcNow().AddDays(RememberDays));

    public bool IsTrusted(User u, string? cookie) =>
        u.TotpEnabled && Read(trust, cookie) is [var id, var stamp] && id == u.Id.ToString() && Crypto.FixedEquals(stamp, u.SecurityStamp);

    private static string[]? Read(ITimeLimitedDataProtector p, string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try { return p.Unprotect(value).Split('|'); }
        catch (CryptographicException) { return null; }
    }
}
