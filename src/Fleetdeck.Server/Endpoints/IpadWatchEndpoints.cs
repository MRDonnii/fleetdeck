using System.Security.Claims;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services;

namespace Fleetdeck.Server.Endpoints;

public sealed record IpadWatchInput(bool? Enabled, int? SnoozeDays);
public sealed record IpadSetupStepInput(bool Done);

public static partial class IpadEndpoints
{
    /// <summary>404 for enheder, brugeren ikke har adgang til; 403 uden retten til at ændre barnets regler.</summary>
    public static int? WatchDenied(FamilyScope sc, int id) => !sc.Sees(id) ? 404 : !sc.Can(FamilyPerms.Rules) ? 403 : null;

    private static void MapWatch(RouteGroupBuilder fam)
    {
        fam.MapPut("/ipads/{id:int}/watch", async (int id, IpadWatchInput i, ClaimsPrincipal u, AppDb db, AuditService audit, Live live) =>
        {
            var sc = await FamilyScope.Of(u, db);
            var p = await db.Ipads.FindAsync(id);
            if (p is null || WatchDenied(sc, id) == 404) return Results.NotFound();
            if (WatchDenied(sc, id) == 403) return Results.Problem("Du må ikke ændre advarslerne", statusCode: 403);
            IpadWatch.Configure(p, i.Enabled, i.SnoozeDays, DateTime.UtcNow);
            await db.SaveChangesAsync();
            await audit.Log(u.UserName(), "ipad.watch", $"{p.Name}: advarsel {(IpadWatch.Enabled(p) ? "til" : "fra")}{(p.WatchSnoozeUntil is { } s ? $", snooze til {s.ToLocalTime():dd-MM-yyyy}" : "")}");
            await live.Changed("ipads");
            return Results.Ok();
        });

        // Tjeklisten: en voksen markerer selv et trin som gjort. Fleetdeck kan ikke kontrollere det, og profil-trinnet kan ikke markeres i hånden.
        fam.MapPut("/ipads/{id:int}/setup-steps/{key}", async (int id, string key, IpadSetupStepInput i, ClaimsPrincipal u, AppDb db, AuditService audit, Live live) =>
        {
            var sc = await FamilyScope.Of(u, db);
            var p = await db.Ipads.FindAsync(id);
            if (p is null || WatchDenied(sc, id) == 404) return Results.NotFound();
            if (WatchDenied(sc, id) == 403) return Results.Problem("Du må ikke ændre tjeklisten", statusCode: 403);
            if (!IpadSetupGuide.Mark(p, key, i.Done)) return Results.Problem("Det trin kan ikke markeres i hånden", statusCode: 400);
            await db.SaveChangesAsync();
            await audit.Log(u.UserName(), "ipad.setup", $"{p.Name}: {key} {(i.Done ? "markeret" : "afmarkeret")}");
            await live.Changed("ipads");
            return Results.Ok();
        });
    }
}
