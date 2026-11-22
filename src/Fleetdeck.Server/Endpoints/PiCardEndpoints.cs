using Fleetdeck.Server.Services;

namespace Fleetdeck.Server.Endpoints;

/// <summary>"Lav SD-kort" til Raspberry Pi (Services/PiCardService.cs). Alt kræver administrator; hentning skrives i revisionsloggen.
/// Pi'en selv må kun melde "startet"/"fejl" med sin engangskode.</summary>
public static class PiCardEndpoints
{
    public static void MapPiCardEndpoints(this WebApplication app)
    {
        var adm = app.MapGroup("/api/picard").RequireAuthorization("admin");
        adm.MapGet("/images", (PiImageLibrary lib) => Results.Ok(lib.Status()));
        adm.MapPost("/images/{id}", (string id, PiImageLibrary lib) =>
            PiCardLogic.Image(id) is { } i ? Results.Ok(new { started = lib.Start(i) }) : Results.NotFound());
        adm.MapGet("/plan/{machineId:int}", async (int machineId, PiCardService svc) => await svc.Plan(machineId) is { } p ? Results.Ok(p) : Results.NotFound());
        adm.MapGet("/cards", async (int? machineId, PiCardService svc) => Results.Ok(await svc.Cards(machineId)));
        adm.MapPost("/build", async (PiCardRequest r, HttpContext ctx, PiCardService svc) =>
        {
            var s = await svc.Start(r, ctx.User.UserName(), ctx);
            return s.Card is { } c ? Results.Ok(await svc.View(c)) : Results.Problem(s.Error, statusCode: 400);
        });
        adm.MapGet("/{id:int}", async (int id, Data.AppDb db, PiCardService svc) =>
            await db.PiCards.FindAsync(id) is { } c ? Results.Ok(await svc.View(c)) : Results.NotFound());
        adm.MapGet("/{id:int}/download", async (int id, HttpContext ctx, PiCardService svc) =>
        {
            var (path, name) = await svc.FileFor(id, ctx.User.UserName());
            return path is null ? Results.NotFound() : Results.File(path, "application/x-xz", name, enableRangeProcessing: true);
        });
        adm.MapDelete("/{id:int}", async (int id, HttpContext ctx, PiCardService svc) => { await svc.Delete(id, ctx.User.UserName()); return Results.Ok(); });

        // netboot som nødstart (Fleetdecks egen dnsmasq svarer kun de valgte Pi'er)
        adm.MapGet("/netboot/{machineId:int}", async (int machineId, PiNetboot nb) => await nb.Status(machineId) is { } st ? Results.Ok(st) : Results.NotFound());
        adm.MapPut("/netboot/{machineId:int}", async (int machineId, NetbootInput i, HttpContext ctx, PiNetboot nb) =>
            await nb.Set(machineId, i.On, i.Serial, ctx.User.UserName()) is { } err ? Results.Problem(err, statusCode: 400) : Results.Ok());

        // fra kortet under førstestarten: engangskoden i en header (ikke i adressen, så den ikke står i en log)
        app.MapPost("/api/picard/status", async (string? step, HttpContext ctx, PiCardService svc) =>
            await svc.FromCard(ctx.Request.Headers["X-Fleetdeck-Code"], step ?? "") ? Results.Ok() : Results.NotFound()).AllowAnonymous().RequireRateLimiting("login");
    }
}

public sealed record NetbootInput(bool On, string? Serial);
