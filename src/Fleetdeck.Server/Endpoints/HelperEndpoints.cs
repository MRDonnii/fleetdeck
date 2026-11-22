using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;

namespace Fleetdeck.Server.Endpoints;

public sealed record RunFixRequest(string? Confirm);

public static class HelperEndpoints
{
    public static void MapHelperEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/helper").RequireAuthorization("user");
        var adm = app.MapGroup("/api").RequireAuthorization("admin");

        // Katalog over rettelser og værktøjer (uden selve scripts)
        api.MapGet("/catalog", () => HelperCatalog.All.Select(f => new { f.Id, f.Title, f.Description, f.Risk, f.Group, f.Tool, f.AutoAllowed, f.RunAs,
            platforms = new[] { Platforms.Windows, Platforms.Mac, Platforms.Linux }.Where(p => HelperService.PlatformBlocker(f, p) is null) }));

        // Statistik: hvad har hjælperen lavet de sidste 7 dage
        api.MapGet("/stats", async (AppDb db) =>
        {
            var since = DateTime.UtcNow.AddDays(-7);
            var q = db.Jobs.AsNoTracking().Where(j => j.Source == HelperService.Source && j.Created >= since);
            return new
            {
                total = await q.CountAsync(),
                ok = await q.CountAsync(j => j.Status == JobStatus.Success),
                failed = await q.CountAsync(j => j.Status == JobStatus.Failed),
                automatic = await q.CountAsync(j => j.CreatedBy.StartsWith("hjælper")),
                recent = await q.OrderByDescending(j => j.Id).Take(10)
                    .Select(j => new { j.Id, j.MachineId, machine = j.Machine!.DisplayName ?? j.Machine.Name, j.Title, j.Status, j.Created, j.CreatedBy }).ToListAsync(),
            };
        });

        // "Ret nu" på en alarm
        adm.MapPost("/alerts/{id:int}/fix", async (int id, AppDb db, HelperService helper, AuditService audit, ClaimsPrincipal u) =>
        {
            var a = await db.Alerts.AsNoTracking().Include(x => x.Machine).FirstOrDefaultAsync(x => x.Id == id);
            if (a is null) return Results.NotFound();
            var fixId = HelperCatalog.AdviceFor(a.Key, a.Severity, a.Machine?.Platform, a.Detail)?.FixId;
            if (fixId is null) return Results.Problem("Hjælperen har ingen rettelse til denne alarm", statusCode: 400);
            var r = await helper.Run(a.MachineId, fixId, u.UserName(), a.Id);
            await audit.Log(u.UserName(), "helper.fix", $"{fixId} på maskine {a.MachineId} (alarm {a.Key}): {r.Message}");
            return r.Ok ? Results.Ok(r) : Results.Problem(r.Message, statusCode: 400);
        });

        // Kør et værktøj / en rettelse direkte på en maskine
        adm.MapPost("/machines/{id:int}/helper/{fixId}", async (int id, string fixId, RunFixRequest? body, AppDb db, HelperService helper, AuditService audit, ClaimsPrincipal u) =>
        {
            var fix = HelperCatalog.Get(fixId);
            var m = await db.Machines.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (fix is null || m is null) return Results.NotFound();
            if (fix.Risk == Risk.Danger && !string.Equals(body?.Confirm?.Trim(), m.Label, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(body?.Confirm?.Trim(), m.Name, StringComparison.OrdinalIgnoreCase))
                return Results.Problem($"Skriv maskinens navn ({m.Label}) for at bekræfte", statusCode: 400);
            var r = await helper.Run(id, fixId, u.UserName());
            await audit.Log(u.UserName(), "helper.run", $"{fixId} på {m.Label}: {r.Message}");
            return r.Ok ? Results.Ok(r) : Results.Problem(r.Message, statusCode: 400);
        });
    }
}
