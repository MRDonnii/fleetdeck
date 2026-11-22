using System.Security.Claims;
using Fleetdeck.Server.Services;

namespace Fleetdeck.Server.Endpoints;

public sealed record InstallLocaleInput(string? Language, string? Keyboard, string? TimeZone);

/// <summary>
/// Opsætningsguiden ved første start (/api/setup – kun mens der ingen brugere er, ellers 404) og
/// Indstillinger → Netværksinstallationer (sprog, tastatur og tidszone til maskiner, Fleetdeck installerer).
/// </summary>
public static class SetupEndpoints
{
    private static object Options() => new
    {
        languages = InstallLocale.Languages.Select(l => new { id = l.Id, name = l.Name, keyboard = l.Keyboard }),
        keyboards = InstallLocale.Keyboards.Select(k => new { id = k.Id, name = k.Name }),
        serverTimeZone = InstallLocale.ServerTimeZone(),
    };

    public static void MapSetupEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/setup");

        // Forslag til guiden. Adressen: SERVER_URL, hvis den er sat, ellers den adresse browseren bruger nu.
        g.MapGet("", async (SetupService setup, SettingsService s, HttpContext ctx, string? lang) =>
        {
            if (!await setup.IsOpen()) return Results.NotFound();
            var saved = await s.Get("server_url");
            var guess = InstallLocale.GuessLanguage(lang);
            return Results.Ok(new
            {
                detectedUrl = saved.Length > 0 ? saved : $"{ctx.Request.Scheme}://{ctx.Request.Host}",
                fromEnv = Environment.GetEnvironmentVariable("SERVER_URL") is { Length: > 0 },
                suggested = new { language = guess.Id, keyboard = guess.Keyboard, timeZone = InstallLocale.ServerTimeZone() },
                options = Options(),
            });
        });

        g.MapPost("", async (SetupInput i, SetupService setup, SettingsService s, WinSetupStore store, AgentBinary agent, HttpContext ctx) =>
        {
            var r = await setup.Complete(i);
            if (r.Status == SetupStatus.Closed) return Results.NotFound();
            if (r.Status == SetupStatus.Invalid) return Results.Problem(r.Error, statusCode: 400);
            store.PublishAgent(await s.Get("server_url"), await s.Get("enroll_key"), agent.ExePath);
            await AuthEndpoints.SignIn(ctx, r.User!);
            return Results.Ok(new { username = r.User!.Username, role = r.User.Role });
        }).RequireRateLimiting("login");

        var api = app.MapGroup("/api").RequireAuthorization("user");
        var adm = app.MapGroup("/api").RequireAuthorization("admin");

        api.MapGet("/settings/install-locale", async (SettingsService s) =>
        {
            var l = await InstallLocale.Load(s);
            return new { language = l.Language.Id, keyboard = l.Keyboard.Id, timeZone = l.TimeZone, windowsTimeZone = l.WindowsTimeZone, options = Options() };
        });
        adm.MapPut("/settings/install-locale", async (InstallLocaleInput i, SettingsService s, DeployService deploy, AuditService audit, ClaimsPrincipal u) =>
        {
            if (InstallLocale.FindLanguage(i.Language) is not { } lang) return Results.Problem("Vælg et sprog", statusCode: 400);
            if (InstallLocale.FindKeyboard(i.Keyboard) is not { } kb) return Results.Problem("Vælg et tastaturlayout", statusCode: 400);
            if (!InstallLocale.ValidTimeZone(i.TimeZone)) return Results.Problem("Ukendt tidszone", statusCode: 400);
            await s.Set(InstallLocale.KLanguage, lang.Id);
            await s.Set(InstallLocale.KKeyboard, kb.Id);
            await s.Set(InstallLocale.KTimeZone, i.TimeZone!.Trim());
            await deploy.Sync();   // defaults.cmd og locale.json på sharet med det samme
            await audit.Log(u.UserName(), "settings.install_locale", $"{lang.Id} · {kb.Id} · {i.TimeZone!.Trim()}");
            return Results.Ok();
        });
    }
}
