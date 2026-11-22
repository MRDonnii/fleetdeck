using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services;

namespace Fleetdeck.Server.Endpoints;

/// <summary>Helbred-fanens tidslinje (24 t / 7 / 30 dage) og ændringsloggen (programmer og drivere). Kun læsning, for alle med adgang til maskinerne.</summary>
public static class TimelineEndpoints
{
    private const int MaxChanges = 1000;

    public static void MapTimelineEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization("user");

        api.MapGet("/machines/{id:int}/timeline", async (int id, int? days, AppDb db) =>
        {
            if (await db.Machines.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id) is not { } m) return Results.NotFound();
            var d = days is 1 or 7 or 30 ? days.Value : 7;
            return Results.Ok(await Timeline.For(db, m, d, DateTime.UtcNow));
        });

        api.MapGet("/machines/{id:int}/changes", async (int id, int? days, string? kind, string? q, AppDb db) =>
        {
            if (!await db.Machines.AnyAsync(m => m.Id == id)) return Results.NotFound();
            var d = Math.Clamp(days ?? 30, 1, ChangeLogStore.RetentionDays);
            var since = DateTime.UtcNow.AddDays(-d);
            var query = db.ChangeLog.AsNoTracking().Where(x => x.MachineId == id && x.Time >= since);
            if (kind is ChangeKinds.Program or ChangeKinds.Driver) query = query.Where(x => x.Kind == kind);
            var rows = await query.OrderByDescending(x => x.Time).ThenBy(x => x.Name).Take(MaxChanges * 2).ToListAsync();
            if (q?.Trim() is { Length: > 0 } text)
                rows = rows.Where(x => x.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || x.Publisher?.Contains(text, StringComparison.OrdinalIgnoreCase) == true).ToList();
            return Results.Ok(new
            {
                days = d,
                total = rows.Count,
                counts = rows.GroupBy(x => x.Action).ToDictionary(g => g.Key, g => g.Count()),
                entries = rows.Take(MaxChanges).Select(x => new { x.Id, x.Time, x.Kind, x.Action, x.Name, x.From, x.To, x.Publisher }),
                drivers = await db.Drivers.CountAsync(x => x.MachineId == id),
                tracking = await db.Machines.Where(m => m.Id == id).Select(m => m.LastInventory).FirstAsync(),
            });
        });
    }
}
