using System.Security.Claims;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services;

namespace Fleetdeck.Server.Endpoints;

public sealed record EnrollConfirm(string Token, string Code);
/// <summary>Bevis for at det er brugeren selv: en kode fra appen/en gendannelseskode eller adgangskoden.</summary>
public sealed record ProofRequest(string? Code, string? Password);
public sealed record SecuritySettings(string TwofaPolicy, string TrustedProxies);

/// <summary>Totrinsbekræftelse under "Min konto", nulstilling for administratoren og sikkerhedsindstillingerne.</summary>
public static class TwoFactorEndpoints
{
    private static IResult Bad(string msg) => Results.Problem(msg, statusCode: 400);

    public static void MapTwoFactorEndpoints(this WebApplication app)
    {
        // "session": også en bruger, der endnu ikke har påkrævet totrinsbekræftelse, skal kunne sætte den op
        var me = app.MapGroup("/api/account/2fa").RequireAuthorization("session");

        me.MapGet("", async (ClaimsPrincipal p, AppDb db) =>
        {
            var u = await db.Users.FindAsync(p.UserId());
            if (u is null) return Results.NotFound();
            var required = TwoFactorPolicy.RequiredFor(TwoFactorPolicy.Current, u.Role);
            return Results.Ok(new { enabled = u.TotpEnabled, enabledAt = u.TotpEnabledAt, recoveryLeft = u.RecoveryCodes.Count, required, policy = TwoFactorPolicy.Current });
        });

        me.MapPost("/start", async (ClaimsPrincipal p, AppDb db, TwoFactorService tf) =>
        {
            var u = await db.Users.FindAsync(p.UserId());
            if (u is null) return Results.NotFound();
            if (u.TotpEnabled) return Bad("Totrinsbekræftelse er allerede slået til. Slå den fra først for at skifte telefon.");
            var (secret, uri, token) = tf.StartEnroll(u);
            return Results.Ok(new { secret, uri, token });
        });

        me.MapPost("/confirm", async (EnrollConfirm r, ClaimsPrincipal p, AppDb db, TwoFactorService tf, AuditService audit, HttpContext ctx) =>
        {
            var u = await db.Users.FindAsync(p.UserId());
            if (u is null) return Results.NotFound();
            if (u.TotpEnabled) return Bad("Totrinsbekræftelse er allerede slået til");
            if (tf.ConfirmEnroll(u, r.Token, r.Code) is not { } codes)
                return Bad("Koden passer ikke. Tjek at telefonens ur går rigtigt, og prøv med den næste kode.");
            await db.SaveChangesAsync();
            await AuthEndpoints.SignIn(ctx, u);   // ny SecurityStamp: andre sessioner logges ud, denne fortsætter
            await audit.Log(u.Username, "2fa.enabled", AuthEndpoints.ClientIp(ctx));
            return Results.Ok(new { recoveryCodes = codes });
        }).RequireRateLimiting("login");

        me.MapPost("/disable", async (ProofRequest r, ClaimsPrincipal p, AppDb db, TwoFactorService tf, AuditService audit, HttpContext ctx) =>
        {
            var u = await db.Users.FindAsync(p.UserId());
            if (u is null) return Results.NotFound();
            if (!u.TotpEnabled) return Bad("Totrinsbekræftelse er ikke slået til");
            if (await Prove(u, r, db, tf, audit, ctx) is { } denied) return denied;
            TwoFactorService.Disable(u);
            await db.SaveChangesAsync();
            await AuthEndpoints.SignIn(ctx, u);
            await audit.Log(u.Username, "2fa.disabled", AuthEndpoints.ClientIp(ctx));
            return Results.Ok();
        }).RequireRateLimiting("login");

        me.MapPost("/recovery-codes", async (ProofRequest r, ClaimsPrincipal p, AppDb db, TwoFactorService tf, AuditService audit, HttpContext ctx) =>
        {
            var u = await db.Users.FindAsync(p.UserId());
            if (u is null) return Results.NotFound();
            if (!u.TotpEnabled) return Bad("Totrinsbekræftelse er ikke slået til");
            if (await Prove(u, r, db, tf, audit, ctx) is { } denied) return denied;
            var codes = tf.NewRecoveryCodes(u);
            await db.SaveChangesAsync();
            await audit.Log(u.Username, "2fa.recovery_regenerated", AuthEndpoints.ClientIp(ctx));
            return Results.Ok(new { recoveryCodes = codes });
        }).RequireRateLimiting("login");

        // Administrator: nulstil en anden brugers totrinsbekræftelse (fx mistet telefon). Brugeren logges ud overalt.
        app.MapPost("/api/users/{id:int}/2fa/reset", async (int id, ClaimsPrincipal p, AppDb db, AuditService audit) =>
        {
            if (id == p.UserId()) return Bad("Brug Min konto til din egen totrinsbekræftelse");
            var u = await db.Users.FindAsync(id);
            if (u is null) return Results.NotFound();
            if (!u.TotpEnabled) return Bad($"{u.Username} har ikke totrinsbekræftelse slået til");
            TwoFactorService.Disable(u);
            await db.SaveChangesAsync();
            await audit.Log(p.UserName(), "2fa.reset", u.Username);
            return Results.Ok();
        }).RequireAuthorization("admin");

        var adm = app.MapGroup("/api/settings/security").RequireAuthorization("admin");
        adm.MapGet("", (HttpContext ctx) => new
        {
            twofaPolicy = TwoFactorPolicy.Current,
            trustedProxies = TrustedProxies.Current.Text,
            trustedProxiesDefault = TrustedProxies.Defaults,
            // Hjælp til opsætningen: hvad serveren ser for netop denne forespørgsel
            clientIp = AuthEndpoints.ClientIp(ctx),
            viaProxy = ctx.Request.Headers.ContainsKey("X-Original-For"),
        });
        adm.MapPut("", async (SecuritySettings r, SettingsService s, AuditService audit, ClaimsPrincipal p) =>
        {
            if (!TwoFactorPolicy.Valid(r.TwofaPolicy)) return Bad("Ugyldig politik");
            var proxies = (r.TrustedProxies ?? "").Trim();
            if (proxies.Length > 0 && TrustedProxies.Parse(proxies, out var err) is null) return Bad(err!);
            var before = $"{TwoFactorPolicy.Current} | {TrustedProxies.Current.Text}";
            await s.Set(TwoFactorPolicy.SettingKey, r.TwofaPolicy);
            await s.Set(TrustedProxies.SettingKey, proxies);
            TwoFactorPolicy.Current = r.TwofaPolicy;
            TrustedProxies.Set(proxies);
            var after = $"{TwoFactorPolicy.Current} | {TrustedProxies.Current.Text}";
            if (before != after) await audit.Log(p.UserName(), "settings.security", $"{before} → {after}");
            return Results.Ok();
        });
    }

    /// <summary>Kræv adgangskoden eller en gyldig kode. Fejl tæller med i spærringen; null = godkendt.</summary>
    internal static async Task<IResult?> Prove(User u, ProofRequest r, AppDb db, TwoFactorService tf, AuditService audit, HttpContext ctx, string failAction = "2fa.proof_failed")
    {
        if (Lockout.Remaining(u, tf.Now) is { } wait) return Results.Problem(Lockout.Message(wait), statusCode: 429);
        var ok = (!string.IsNullOrEmpty(r.Password) && Crypto.VerifyPassword(r.Password, u.PasswordHash))
                 || (!string.IsNullOrWhiteSpace(r.Code) && tf.Verify(u, r.Code) != CodeKind.Invalid);
        if (ok) { Lockout.Success(u); return null; }
        Lockout.Fail(u, tf.Now);
        await db.SaveChangesAsync();
        await audit.Log(u.Username, failAction, AuthEndpoints.ClientIp(ctx));
        return Bad("Forkert kode eller adgangskode");
    }
}
