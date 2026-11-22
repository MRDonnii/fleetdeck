using System.Text.Json.Nodes;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services;

namespace Fleetdeck.Server.Endpoints;

/// <summary>Opsætningsfilen (Services/SiteConfig.cs): hent den som JSON, eller læs en ind igen. Kun administrator; begge dele i revisionsloggen.</summary>
public static class SiteConfigEndpoints
{
    public static void MapSiteConfigEndpoints(this WebApplication app)
    {
        var adm = app.MapGroup("/api/site-config").RequireAuthorization("admin");
        adm.MapGet("", async (HttpContext ctx, AppDb db, Paths paths, AuditService audit) =>
        {
            var doc = await SiteConfig.Export(db, paths);
            await audit.Log(ctx.User.UserName(), "siteconfig.export", $"{(doc["settings"] as JsonObject)?.Count} indstillinger, {(doc["profiles"] as JsonArray)?.Count} profiler");
            ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"fleetdeck-opsaetning-{DateTime.Now:yyyy-MM-dd}.json\"";
            return Results.Text(doc.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), "application/json; charset=utf-8");
        });
        adm.MapPost("", async (JsonObject doc, HttpContext ctx, AppDb db, AuditService audit) =>
        {
            try
            {
                var r = await SiteConfig.Import(db, doc);
                await audit.Log(ctx.User.UserName(), "siteconfig.import", $"{r.Settings} indstillinger, {r.ProfilesChanged} profiler ændret, {r.ProfilesCreated} oprettet, {r.Machines} maskiner, {r.Printers} printere, {r.Apps} apps");
                return Results.Ok(r);
            }
            catch (InvalidDataException e) { return Results.Problem(e.Message, statusCode: 400); }
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(4 * 1024 * 1024));
    }
}
