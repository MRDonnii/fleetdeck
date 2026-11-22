using System.Security.Claims;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services;

namespace Fleetdeck.Server.Endpoints;

public sealed record KeepAwakeRequest(bool On);

/// <summary>Mac: "Hold vågen på strøm (også med lukket låg)". Kun administrator; logges. Kan også ændres på Mac'en (se <see cref="KeepAwakeSync"/>).</summary>
public static class KeepAwakeEndpoints
{
    public static void MapKeepAwakeEndpoints(this WebApplication app)
    {
        var adm = app.MapGroup("/api").RequireAuthorization("admin");
        adm.MapPut("/machines/{id:int}/keepawake", async (int id, KeepAwakeRequest r, AppDb db, AuditService audit, Live live, ClaimsPrincipal u) =>
        {
            if (await db.Machines.FindAsync(id) is not { } m) return Results.NotFound();
            if (!KeepAwakeSync.Applies(m)) return Results.BadRequest(new { error = "Kun for Mac" });
            var was = m.KeepAwakeOnAc;
            KeepAwakeSync.FromWeb(m, r.On, u.UserName(), DateTime.UtcNow);
            await db.SaveChangesAsync();
            await audit.Log(u.UserName(), "machine.keepawake", $"{m.Label}: hold vågen på strøm {(r.On ? "til" : "fra")}{(was == r.On ? " (uændret)" : "")}");
            await live.Changed("machines", id);
            await live.Wake(id);   // agenten checker ind med det samme og får den nye indstilling
            return Results.Ok(new { on = m.KeepAwakeOnAc, at = m.KeepAwakeAt });
        });
    }
}
