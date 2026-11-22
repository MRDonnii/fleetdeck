using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services;

namespace Fleetdeck.Server.Endpoints;

public sealed record LoginRequest(string Username, string Password);
public sealed record CodeRequest(string Ticket, string Code, bool Remember);

public static class AuthEndpoints
{
    private static readonly string DummyHash = Crypto.HashPassword(Guid.NewGuid().ToString("N"));
    public static int UserId(this ClaimsPrincipal p) => int.TryParse(p.FindFirstValue(ClaimTypes.NameIdentifier), out var i) ? i : 0;
    public static string UserName(this ClaimsPrincipal p) => p.Identity?.Name ?? "?";

    public static async Task SignIn(HttpContext ctx, User u)
    {
        var id = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, u.Id.ToString()), new Claim(ClaimTypes.Name, u.Username),
            new Claim(ClaimTypes.Role, u.Role), new Claim("stamp", u.SecurityStamp),
            new Claim(TwoFactorPolicy.Claim, u.TotpEnabled ? "1" : "0")], CookieAuthenticationDefaults.AuthenticationScheme);
        await ctx.SignInAsync(new ClaimsPrincipal(id), new AuthenticationProperties { IsPersistent = true });
    }

    /// <summary>Klientens IP (fra X-Forwarded-For kun via betroede proxyer).</summary>
    public static string ClientIp(HttpContext ctx) => ctx.Connection.RemoteIpAddress is { } ip ? (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString() : "?";

    private static IResult Locked(TimeSpan wait) => Results.Problem(Lockout.Message(wait), statusCode: 429);

    /// <summary>
    /// Fuldfør login: nulstil fejltælleren, opret sessionen og skriv i audit. Sessionen er den samme, uanset hvordan brugeren
    /// kom ind (også Kontrolrum-SSO): har brugeren totrinsbekræftelse, er den opfyldt i sessionen.
    /// </summary>
    private static async Task<IResult> Complete(HttpContext ctx, AppDb db, AuditService audit, TwoFactorService tf, User u, string how,
        string action = "login", bool resetLockout = true)
    {
        // SSO bruger ingen adgangskode og nulstiller derfor ikke tælleren for forkerte adgangskoder
        if (resetLockout) Lockout.Success(u);
        u.LastLogin = tf.Now;
        await db.SaveChangesAsync();
        await SignIn(ctx, u);
        var mustEnroll = !u.TotpEnabled && TwoFactorPolicy.RequiredFor(TwoFactorPolicy.Current, u.Role);
        await audit.Log(u.Username, action, $"{ClientIp(ctx)} · {how}{(mustEnroll ? " · skal sætte totrinsbekræftelse op" : "")}");
        return Results.Ok(new { username = u.Username, role = u.Role, mustEnroll });
    }

    public static void MapAuthEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/auth");

        g.MapGet("/setup-needed", async (AppDb db) => new { needed = !await db.Users.AnyAsync() });

        // Den første administrator oprettes af opsætningsguiden (SetupEndpoints, /api/setup)

        g.MapPost("/login", async (LoginRequest r, AppDb db, HttpContext ctx, AuditService audit, TwoFactorService tf) =>
        {
            var name = (r.Username ?? "").Trim();
            var pw = r.Password ?? "";
            var u = await db.Users.FirstOrDefaultAsync(x => x.Username == name);
            var ip = ClientIp(ctx);
            if (u is not null && Lockout.Remaining(u, tf.Now) is { } wait)
            {
                Crypto.VerifyPassword(pw, DummyHash);   // samme svartid som ellers
                await audit.Log(u.Username, "login.locked", ip);
                return Locked(wait);
            }
            // Ukendt brugernavn: regn alligevel en hash, så svartiden ikke afslører om brugeren findes
            if (u is null) Crypto.VerifyPassword(pw, DummyHash);
            if (u is null || !Crypto.VerifyPassword(pw, u.PasswordHash))
            {
                if (u is not null) { Lockout.Fail(u, tf.Now); await db.SaveChangesAsync(); }
                await audit.Log(name, "login.failed", ip);
                return Results.Problem("Forkert brugernavn eller adgangskode", statusCode: 401);
            }
            var trusted = tf.IsTrusted(u, ctx.Request.Cookies[TwoFactorService.TrustCookie]);
            return TwoFactorPolicy.Next(u.TotpEnabled, trusted, u.Role, TwoFactorPolicy.Current) switch
            {
                // Adgangskoden er rigtig, men koden mangler: ingen session endnu, kun en kortlivet billet
                LoginStep.Code => Results.Ok(new { twoFactor = "code", ticket = tf.LoginTicket(u) }),
                _ => await Complete(ctx, db, audit, tf, u, trusted ? "husket enhed" : "adgangskode"),
            };
        }).RequireRateLimiting("login");

        // Trin 2: kode fra appen eller en gendannelseskode
        g.MapPost("/2fa", async (CodeRequest r, AppDb db, HttpContext ctx, AuditService audit, TwoFactorService tf) =>
        {
            var expired = Results.Problem("Login er udløbet. Start forfra.", statusCode: 401);
            if (tf.ReadTicket(r.Ticket) is not { } t) return expired;
            var u = await db.Users.FindAsync(t.Id);
            if (u is null || u.SecurityStamp != t.Stamp || !u.TotpEnabled) return expired;
            var ip = ClientIp(ctx);
            if (Lockout.Remaining(u, tf.Now) is { } wait) { await audit.Log(u.Username, "login.locked", ip); return Locked(wait); }
            var kind = tf.Verify(u, r.Code);
            if (kind == CodeKind.Invalid)
            {
                Lockout.Fail(u, tf.Now);
                await db.SaveChangesAsync();
                await audit.Log(u.Username, "login.2fa_failed", ip);
                return Results.Problem("Forkert kode", statusCode: 401);
            }
            if (kind == CodeKind.Recovery) await audit.Log(u.Username, "2fa.recovery_used", $"{ip} · {u.RecoveryCodes.Count} tilbage");
            if (r.Remember)
                ctx.Response.Cookies.Append(TwoFactorService.TrustCookie, tf.TrustValue(u), new CookieOptions
                {
                    HttpOnly = true, SameSite = SameSiteMode.Lax, Path = "/", Secure = ctx.Request.IsHttps, IsEssential = true,
                    MaxAge = TimeSpan.FromDays(TwoFactorService.RememberDays),
                });
            return await Complete(ctx, db, audit, tf, u, kind == CodeKind.Recovery ? "gendannelseskode" : "totrinsbekræftelse" + (r.Remember ? " · enheden huskes" : ""));
        }).RequireRateLimiting("login");

        // Single sign-on fra Kontrolrum (eller et lignende dashboard): en signeret, kortlivet engangspåstand i stedet for
        // adgangskode og kode. Kun for brugere med fluebenet "Må logges ind fra Kontrolrum" (docs/kontrolrum-sso.md).
        g.MapPost("/kontrolrum", async (KontrolrumAssertion? a, AppDb db, HttpContext ctx, AuditService audit, TwoFactorService tf,
            KontrolrumSso sso, SettingsService s) =>
        {
            var ip = ClientIp(ctx);
            async Task<IResult> Fail(int status, string error)
            {
                await audit.Log(a?.Sub is { Length: > 0 and <= 64 } sub ? sub : "?", "login.kontrolrum_failed", $"{ip} · {error}");
                return Results.Json(new { error }, statusCode: status);
            }
            if (sso.ReadKey(await s.Get(KontrolrumSso.KeySetting)) is not { } key) return Results.Json(new { error = "sso_disabled" }, statusCode: 403);
            if (!KontrolrumSso.WellFormed(a)) return await Fail(400, "bad_request");
            if (!KontrolrumSso.SignatureValid(key, a!)) return await Fail(401, "bad_signature");
            if (sso.CheckClaims(a!) is { } claimError) return await Fail(401, claimError);
            if (!await sso.TakeNonce(db, a!.Nonce!, a.Exp!.Value)) return await Fail(401, "replay");
            var u = await db.Users.FirstOrDefaultAsync(x => x.Username == a.Sub);
            if (u is null || !u.KontrolrumSso) return await Fail(401, "unknown_user");
            if (Lockout.Remaining(u, tf.Now) is { } wait) { await audit.Log(u.Username, "login.locked", $"{ip} · Kontrolrum-SSO"); return Locked(wait); }
            return await Complete(ctx, db, audit, tf, u, "Kontrolrum-SSO", action: "login.kontrolrum", resetLockout: false);
        }).RequireRateLimiting(KontrolrumSso.RateLimitPolicy);

        g.MapPost("/logout", async (HttpContext ctx, AuditService audit) =>
        {
            if (ctx.User.Identity?.IsAuthenticated == true) await audit.Log(ctx.User.UserName(), "logout", ClientIp(ctx));
            await ctx.SignOutAsync();
            return Results.Ok();
        });

        // "session": virker også, før en påkrævet totrinsbekræftelse er sat op (så brugerfladen kan vise opsætningen)
        g.MapGet("/me", async (ClaimsPrincipal p, AppDb db) =>
        {
            var role = p.FindFirstValue(ClaimTypes.Role);
            var enabled = p.FindFirstValue(TwoFactorPolicy.Claim) == "1";
            var required = TwoFactorPolicy.RequiredFor(TwoFactorPolicy.Current, role);
            return new
            {
                id = p.UserId(), username = p.UserName(), role, version = ThisVersion.Value,
                perms = (await FamilyScope.Of(p, db)).PermList,
                twoFactor = new { enabled, required, mustEnroll = required && !enabled },
            };
        }).RequireAuthorization("session");
    }
}
