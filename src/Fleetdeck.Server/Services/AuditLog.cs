using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;

namespace Fleetdeck.Server.Services;

/// <summary>Filtre i Revisionslog. Action matcher både "login" og "login.failed" osv.</summary>
public sealed record AuditFilter(DateTime? From = null, DateTime? To = null, string? User = null, string? Action = null, int? MachineId = null, string? Q = null);

public sealed record AuditRow(long Id, DateTime Ts, string User, string Action, string Detail, int? MachineId, string? Machine);

/// <summary>Revisionsloggen: filtrering, CSV-eksport og oprydning efter det antal dage, der er valgt.</summary>
public static class AuditLog
{
    public const string RetentionKey = "audit_retention_days";
    public const int DefaultRetention = 365, MinRetention = 30, MaxExport = 100_000;

    public static async Task<IQueryable<AuditEntry>> Query(AppDb db, AuditFilter f)
    {
        var q = db.Audit.AsNoTracking().AsQueryable();
        if (f.From is { } from) q = q.Where(a => a.Ts >= from);
        if (f.To is { } to) q = q.Where(a => a.Ts < to);
        if (f.User is { Length: > 0 } user) q = q.Where(a => a.User == user);
        if (f.Action?.Trim() is { Length: > 0 } act)
        {
            var prefix = act + ".";
            q = q.Where(a => a.Action == act || a.Action.StartsWith(prefix));
        }
        if (f.MachineId is int mid)
        {
            // nyere rækker har maskinens id; ældre kun navnet i teksten
            var names = await db.Machines.AsNoTracking().Where(m => m.Id == mid).Select(m => new { m.Name, m.DisplayName }).FirstOrDefaultAsync();
            if (names is null) q = q.Where(a => a.MachineId == mid);
            else
            {
                var p1 = Like(names.Name);
                var p2 = Like(names.DisplayName ?? names.Name);
                q = q.Where(a => a.MachineId == mid || (a.MachineId == null && (EF.Functions.Like(a.Detail, p1, "\\") || EF.Functions.Like(a.Detail, p2, "\\"))));
            }
        }
        if (f.Q?.Trim() is { Length: > 0 } text)
        {
            var p = Like(text);
            q = q.Where(a => EF.Functions.Like(a.Detail, p, "\\") || EF.Functions.Like(a.User, p, "\\") || EF.Functions.Like(a.Action, p, "\\"));
        }
        return q.OrderByDescending(a => a.Id);
    }

    private static string Like(string s) => "%" + s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    /// <summary>Rækkerne med maskinens navn (fra id'et).</summary>
    public static async Task<List<AuditRow>> Rows(AppDb db, IQueryable<AuditEntry> q, int skip, int take)
    {
        var page = await q.Skip(skip).Take(take).ToListAsync();
        var ids = page.Where(a => a.MachineId is not null).Select(a => a.MachineId!.Value).Distinct().ToList();
        var names = await db.Machines.AsNoTracking().Where(m => ids.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.DisplayName ?? m.Name);
        return page.Select(a => new AuditRow(a.Id, a.Ts, a.User, a.Action, a.Detail, a.MachineId, a.MachineId is int id ? names.GetValueOrDefault(id) : null)).ToList();
    }

    /// <summary>CSV med semikolon (som dansk Excel forventer) og UTF-8 med BOM. Celler, der ligner formler, neutraliseres.</summary>
    public static string Csv(IEnumerable<AuditRow> rows)
    {
        var sb = new StringBuilder("﻿Tid (UTC);Bruger;Handling;Maskine;Detaljer\r\n");
        foreach (var r in rows)
            sb.Append(r.Ts.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(';')
              .Append(Cell(r.User)).Append(';').Append(Cell(r.Action)).Append(';').Append(Cell(r.Machine ?? "")).Append(';')
              .Append(Cell(r.Detail)).Append("\r\n");
        return sb.ToString();
    }

    public static string Cell(string s) => ExcelCsv.Cell(s);

    /// <summary>Dage, rækkerne gemmes. 0 = altid; ellers mindst <see cref="MinRetention"/>.</summary>
    public static int Normalize(int days) => days <= 0 ? 0 : Math.Max(days, MinRetention);

    public static async Task<int> RetentionDays(AppDb db)
    {
        var v = await db.Settings.AsNoTracking().Where(s => s.Key == RetentionKey).Select(s => s.Value).FirstOrDefaultAsync();
        return int.TryParse(v, out var d) ? Normalize(d) : DefaultRetention;
    }

    /// <summary>Slet rækker, der er ældre end perioden (kaldes dagligt af baggrundstjenesten).</summary>
    public static async Task<int> Prune(AppDb db, DateTime now)
    {
        var days = await RetentionDays(db);
        if (days == 0) return 0;
        var cutoff = now.AddDays(-days);
        var n = await db.Audit.Where(a => a.Ts < cutoff).ExecuteDeleteAsync();
        if (n > 0)
        {
            db.Audit.Add(AuditService.Entry("system", "audit.prune", $"{n} rækker ældre end {days} dage slettet"));
            await db.SaveChangesAsync();
        }
        return n;
    }
}
