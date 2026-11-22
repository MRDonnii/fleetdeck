using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Hubs;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;

namespace Fleetdeck.Server.Endpoints;

/// <summary>Tillid mellem agent og server: engangskoder, godkendelse af ventende tilmeldinger og status (se docs/sikkerhed-agent.md).</summary>
public static class SecurityEndpoints
{
    public sealed record CodeRequest(string? Label, double? Hours, int? ReplaceMachineId, int? ProfileId);

    /// <summary>Status til Indstillinger: tilstand, nøgle, https og hvor mange agenter der har pinnet nøglen.</summary>
    public static async Task<object> Summary(AppDb db, SettingsService s, JobSigningKey jobKey, TlsInfo tls)
    {
        var rows = await db.Machines.AsNoTracking().Select(m => new { m.JobTrust, m.Transport, m.TlsPinned, m.TrustError, m.ReplacesId }).ToListAsync();
        return new
        {
            mode = SignedJobsMode.Normalize(await s.Get("signed_jobs")),
            jobKey = jobKey.Fingerprint,
            enrollKeyEnabled = await s.Get("enroll_key_enabled") != "0",
            accessRequests = new
            {
                mode = AccessMode.Normalize(await s.Get(AccessRequestService.ModeSetting)),
                max = (int)await s.GetNum(AccessRequestService.MaxSetting, 10),
                hours = (int)await s.GetNum(AccessRequestService.HoursSetting, 24),
            },
            https = new { port = tls.HttpsPort, pin = tls.Pin, selfSigned = tls.SelfSigned, error = tls.Error },
            tlsPin = await AgentEndpoints.TlsPin(s, tls),
            machines = new
            {
                total = rows.Count,
                pinned = rows.Count(r => r.JobTrust == AgentTrust.Pinned),
                unpinned = rows.Count(r => r.JobTrust == AgentTrust.Unpinned),
                mismatch = rows.Count(r => r.JobTrust == AgentTrust.Mismatch),
                old = rows.Count(r => r.JobTrust == null),
                https = rows.Count(r => r.Transport == "https"),
                tlsPinned = rows.Count(r => r.TlsPinned),
                errors = rows.Count(r => r.TrustError != null),
                pending = rows.Count(r => r.ReplacesId != null),
            },
        };
    }

    public static void MapSecurityEndpoints(this WebApplication app)
    {
        var adm = app.MapGroup("/api").RequireAuthorization("admin");

        // Engangskoder til tilmelding (koden vises kun, når den oprettes)
        adm.MapGet("/enroll-codes", async (AppDb db) =>
        {
            var now = DateTime.UtcNow;
            var list = await db.EnrollCodes.AsNoTracking().OrderByDescending(c => c.Id).Take(100).ToListAsync();
            var ids = list.SelectMany(c => new[] { c.ReplaceMachineId, c.UsedByMachineId }).OfType<int>().Distinct().ToList();
            var names = await db.Machines.AsNoTracking().Where(m => ids.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.DisplayName ?? m.Name);
            return list.Select(c => new
            {
                c.Id, c.Hint, c.Label, c.Created, c.CreatedBy, c.Expires, c.UsedAt, c.ProfileId,
                c.ReplaceMachineId, replaceName = c.ReplaceMachineId is int r ? names.GetValueOrDefault(r) : null,
                c.UsedByMachineId, usedByName = c.UsedByMachineId is int u ? names.GetValueOrDefault(u) : null,
                state = c.UsedAt is not null ? "used" : c.Expires <= now ? "expired" : "active",
            });
        });
        adm.MapPost("/enroll-codes", async (CodeRequest r, HttpContext ctx, AppDb db, EnrollmentService enroll, SettingsService s, ClaimsPrincipal u) =>
        {
            if (r.ReplaceMachineId is int rid && !await db.Machines.AnyAsync(m => m.Id == rid)) return Results.Problem("Maskinen findes ikke", statusCode: 400);
            if (r.ProfileId is int pid && pid != 0 && !await db.Profiles.AnyAsync(p => p.Id == pid)) return Results.Problem("Profilen findes ikke", statusCode: 400);
            var c = await enroll.CreateCode(r.Label ?? "", TimeSpan.FromHours(r.Hours is > 0 ? r.Hours.Value : 24), r.ReplaceMachineId, r.ProfileId, u.UserName());
            var url = await s.Get("server_url") is { Length: > 0 } su ? su.TrimEnd('/') : $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            return Results.Ok(new
            {
                code = c.Code, c.Row.Id, c.Row.Expires,
                linux = $"curl -fsSL \"{url}/install.sh?key={c.Code}\" | sudo bash",
                windows = $"irm \"{url}/install.ps1?key={c.Code}\" | iex",
            });
        });
        adm.MapDelete("/enroll-codes/{id:int}", async (int id, AppDb db, AuditService audit, ClaimsPrincipal u) =>
        {
            var c = await db.EnrollCodes.FindAsync(id);
            if (c is null) return Results.NotFound();
            db.EnrollCodes.Remove(c);
            await db.SaveChangesAsync();
            await audit.Log(u.UserName(), "enroll.code_delete", $"{c.Hint}… {c.Label}");
            return Results.Ok();
        });

        // Ventende tilmelding (navnet fandtes i forvejen): godkend som erstatning, behold som ny – eller afvis med "Slet maskine"
        adm.MapPost("/machines/{id:int}/approve-replacement", async (int id, EnrollmentService enroll, ProvisionService prov, AppDb db, Live live, ClaimsPrincipal u) =>
        {
            var provision = await db.Machines.Where(m => m.Id == id).Select(m => m.PendingProvision).FirstOrDefaultAsync();
            var (t, err) = await enroll.ApproveReplacement(id, u.UserName());
            if (t is null) return Results.Problem(err, statusCode: 400);
            if (provision) await prov.Queue(t.Id, "nyinstallation");
            await live.Changed("machines");
            await live.Wake(t.Id);
            return Results.Ok(new { id = t.Id });
        });
        adm.MapPost("/machines/{id:int}/approve-new", async (int id, EnrollmentService enroll, ExtensionService ext, SettingsService s, AuditService audit, Live live, ClaimsPrincipal u) =>
        {
            var (m, err) = await enroll.ApproveAsNew(id, u.UserName());
            if (m is null) return Results.Problem(err, statusCode: 400);
            // som en almindelig tilmelding: standardudvidelserne (de blev ikke installeret, mens maskinen ventede)
            foreach (var x in (await s.Get("default_extensions")).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct())
            {
                try { await ext.Install(m, x, "godkendt som ny", u.UserName()); }
                catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException) { await audit.Log(u.UserName(), "extension.skip", $"{m.Name}: {e.Message}"); }
            }
            await live.Changed("machines", m.Id);
            return Results.Ok(new { id = m.Id });
        });
    }
}
