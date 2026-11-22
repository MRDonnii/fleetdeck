using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services;

namespace Fleetdeck.Server.Endpoints;

public sealed record AuditRetentionRequest(int Days);

/// <summary>Revisionslog (Drift → Revisionslog): kun administratorer. Filtre, sider, CSV-eksport og hvor længe rækkerne gemmes.</summary>
public static class AuditEndpoints
{
    public const int MaxPageSize = 500;

    public static void MapAuditEndpoints(this WebApplication app)
    {
        var adm = app.MapGroup("/api/audit").RequireAuthorization("admin");

        adm.MapGet("", async (DateTime? from, DateTime? to, string? user, string? action, int? machineId, string? q, int? page, int? pageSize, int? limit, AppDb db) =>
        {
            var size = Math.Clamp(pageSize ?? limit ?? 100, 1, MaxPageSize);
            var pg = Math.Max(page ?? 1, 1);
            var query = await AuditLog.Query(db, new AuditFilter(Utc(from), Utc(to), user, action, machineId, q));
            var total = await query.CountAsync();
            return Results.Ok(new { total, page = pg, pageSize = size, rows = await AuditLog.Rows(db, query, (pg - 1) * size, size) });
        });

        // valgmuligheder til filtrene
        adm.MapGet("/meta", async (AppDb db) =>
        {
            var users = await db.Audit.AsNoTracking().Select(a => a.User).Distinct().OrderBy(u => u).Take(500).ToListAsync();
            var actions = (await db.Audit.AsNoTracking().Select(a => a.Action).Distinct().ToListAsync())
                .Select(a => a.Split('.')[0]).Distinct().Order(StringComparer.Ordinal).ToList();
            var machines = await db.Machines.AsNoTracking().Select(m => new { m.Id, label = m.DisplayName ?? m.Name }).OrderBy(m => m.label).ToListAsync();
            return Results.Ok(new { users, actions, machines, retentionDays = await AuditLog.RetentionDays(db), minRetention = AuditLog.MinRetention });
        });

        adm.MapGet("/export.csv", async (DateTime? from, DateTime? to, string? user, string? action, int? machineId, string? q, AppDb db, AuditService audit, ClaimsPrincipal u) =>
        {
            var f = new AuditFilter(Utc(from), Utc(to), user, action, machineId, q);
            var rows = await AuditLog.Rows(db, await AuditLog.Query(db, f), 0, AuditLog.MaxExport);
            await audit.Log(u.UserName(), "audit.export", $"{rows.Count} rækker{Describe(f)}");
            return Results.File(Encoding.UTF8.GetBytes(AuditLog.Csv(rows)), "text/csv; charset=utf-8", $"revisionslog-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv");
        });

        adm.MapPut("/retention", async (AuditRetentionRequest r, SettingsService s, AppDb db, AuditService audit, ClaimsPrincipal u) =>
        {
            var days = AuditLog.Normalize(r.Days);
            var before = await AuditLog.RetentionDays(db);
            await s.Set(AuditLog.RetentionKey, days.ToString());
            if (before != days) await audit.Log(u.UserName(), "settings.audit_retention", $"{Days(before)} → {Days(days)}");
            return Results.Ok(new { retentionDays = days });
        });
    }

    private static string Days(int d) => d == 0 ? "altid" : $"{d} dage";

    /// <summary>Datoer fra browseren er UTC (ISO med Z); uden zone antages UTC.</summary>
    private static DateTime? Utc(DateTime? d) => d is { } x ? x.Kind == DateTimeKind.Local ? x.ToUniversalTime() : DateTime.SpecifyKind(x, DateTimeKind.Utc) : null;

    public static string Describe(AuditFilter f)
    {
        var parts = new List<string>();
        if (f.From is { } a) parts.Add($"fra {a:yyyy-MM-dd}");
        if (f.To is { } b) parts.Add($"til {b:yyyy-MM-dd}");
        if (f.User is { Length: > 0 }) parts.Add($"bruger {f.User}");
        if (f.Action is { Length: > 0 }) parts.Add($"handling {f.Action}");
        if (f.MachineId is int m) parts.Add($"maskine #{m}");
        if (f.Q is { Length: > 0 }) parts.Add($"søgning \"{f.Q}\"");
        return parts.Count == 0 ? "" : " (" + string.Join(", ", parts) + ")";
    }
}
