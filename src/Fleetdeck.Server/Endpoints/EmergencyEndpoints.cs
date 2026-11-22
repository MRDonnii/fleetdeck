using System.Security.Claims;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services;

namespace Fleetdeck.Server.Endpoints;

/// <summary>
/// Nødkortet (Indstillinger → Nødkort): kun administratorer, kun med adgangskoden skrevet igen, få forsøg ad gangen,
/// og svaret må aldrig gemmes af browseren eller en proxy.
/// </summary>
public static class EmergencyEndpoints
{
    public static void MapEmergencyEndpoints(this WebApplication app)
    {
        var adm = app.MapGroup("/api/emergency").RequireAuthorization("admin").RequireRateLimiting(EmergencyAccess.RateLimitPolicy);

        adm.MapPost("/reveal", async (EmergencyRevealInput i, ClaimsPrincipal u, AppDb db, AuditService audit, EmergencySheetService sheet,
            HttpContext ctx, CancellationToken ct) =>
        {
            NoStore(ctx);
            if (await EmergencyAccess.Verify(db, audit, u, i.Password, "nødkort vist") is { } d) return Results.Problem(d.Message, statusCode: d.Status);
            return Results.Ok(await sheet.Build(ct));
        });

        // Husstandens overvågningsidentitet som krypteret .p12 (kun hvis den findes – den laves ikke her)
        adm.MapPost("/supervision.p12", async (EmergencyP12Input i, ClaimsPrincipal u, AppDb db, AuditService audit,
            ISupervisionIdentitySource source, HttpContext ctx, CancellationToken ct) =>
        {
            NoStore(ctx);
            if (EmergencyExport.ValidateP12Password(i.P12Password) is { } bad) return Results.Problem(bad, statusCode: 400);
            if (await EmergencyAccess.Verify(db, audit, u, i.Password, "overvågningsidentitet som .p12") is { } d)
                return Results.Problem(d.Message, statusCode: d.Status);
            using var id = await source.Get(ct);
            if (id is null || !id.HasPrivateKey) return Results.Problem("Fleetdeck har endnu ingen overvågningsidentitet", statusCode: 404);
            return Results.File(EmergencyExport.Pkcs12(id, i.P12Password!), "application/x-pkcs12", "overvaagningsidentitet.p12");
        });
    }

    private static void NoStore(HttpContext ctx)
    {
        ctx.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate, max-age=0";
        ctx.Response.Headers.Pragma = "no-cache";
    }
}
