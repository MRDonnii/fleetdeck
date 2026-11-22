using Fleetdeck.Server.Data;
using Fleetdeck.Server.Hubs;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;

namespace Fleetdeck.Server.Endpoints;

/// <summary>Reservekort på Raspberry Pi (Services/SpareCards.cs). Valg, indstillinger og "Kopiér nu" kræver administrator;
/// agenten melder kun resultatet af sit eget kopijob.</summary>
public static class SpareCardEndpoints
{
    public static void MapSpareCardEndpoints(this WebApplication app)
    {
        var adm = app.MapGroup("/api/sparecard").RequireAuthorization("admin");
        adm.MapGet("/{machineId:int}", async (int machineId, SpareCardService svc) => await svc.View(machineId) is { } v ? Results.Ok(v) : Results.NotFound());
        adm.MapPut("/{machineId:int}", async (int machineId, SpareCardService.Input i, HttpContext ctx, SpareCardService svc) =>
            await svc.Update(machineId, i, ctx.User.UserName()) is { } err ? Results.Problem(err, statusCode: 400) : Results.Ok());
        adm.MapPut("/{machineId:int}/disk", async (int machineId, SpareCardService.Choose c, HttpContext ctx, SpareCardService svc) =>
            await svc.Pick(machineId, c, ctx.User.UserName()) is { } err ? Results.Problem(err, statusCode: 400) : Results.Ok());
        adm.MapDelete("/{machineId:int}/disk", async (int machineId, HttpContext ctx, SpareCardService svc) => { await svc.Clear(machineId, ctx.User.UserName()); return Results.Ok(); });
        adm.MapPost("/{machineId:int}/run", async (int machineId, HttpContext ctx, SpareCardService svc) =>
        {
            var (job, err) = await svc.Start(machineId, "manual", ctx.User.UserName());
            return job is null ? Results.Problem(err, statusCode: 400) : Results.Ok(new { job.Id });
        });

        app.MapPost("/api/agent/sparecard", async (SpareCardReport r, HttpContext ctx, AppDb db, SpareCardService svc) =>
            await Auth(ctx, db) is not { } m ? Results.Unauthorized() : await svc.FromAgent(m, r) ? Results.Ok() : Results.NotFound());
        app.MapPut("/api/agent/sparecard/netboot", async (int jobId, string signature, string kernel, HttpContext ctx, AppDb db, SpareCardService svc, CancellationToken ct) =>
        {
            if (await Auth(ctx, db) is not { } m) return Results.Unauthorized();
            var (ok, err) = await svc.NetbootUpload(m, jobId, signature, kernel, ctx.Request.Body, ct);
            return ok ? Results.Ok() : Results.Problem(err, statusCode: 400);
        }).DisableAntiforgery().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(1L * 1024 * 1024 * 1024));
    }

    private static async Task<Machine?> Auth(HttpContext ctx, AppDb db)
    {
        var h = ctx.Request.Headers.Authorization.ToString();
        return h.StartsWith("Bearer ", StringComparison.Ordinal) ? await AgentAuth.Resolve(db, h[7..].Trim()) : null;
    }
}
