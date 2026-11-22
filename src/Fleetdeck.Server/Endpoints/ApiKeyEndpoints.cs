using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;

namespace Fleetdeck.Server.Endpoints;

public sealed record ApiKeyCreate(string? Name, string? Scope, bool? LanOnly, int? ExpiresDays);
public sealed record ApiKeyChange(bool LanOnly);

/// <summary>
/// Indstillinger → Adgang for programmer → API-nøgler (kun administratorer). Selve nøglen står kun i svaret på POST;
/// listen og revisionsloggen har kun navn, rettighed og de sidste 4 tegn.
/// </summary>
public static class ApiKeyEndpoints
{
    public const int MaxKeys = 50, MaxName = 64;
    public static readonly int[] ExpiryDays = [30, 90, 365];

    public static readonly Dictionary<string, string> ScopeNames = new()
    {
        [ApiScopes.Read] = "kun læse", [ApiScopes.Control] = "læse og styre", [ApiScopes.Admin] = "administrator",
    };

    private static DateTime? Utc(DateTime? d) => d is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;

    public static object Row(ApiKey k) => new
    {
        k.Id, k.Name, k.Scope, shown = ApiKeyFormat.Display(k), k.Hint,
        created = Utc(k.Created), k.CreatedBy, lastUsed = Utc(k.LastUsed), k.LastIp, k.Uses,
        k.LanOnly, expires = Utc(k.Expires), k.Migrated,
    };

    /// <summary>Fejl i en ny nøgle (null = i orden).</summary>
    public static string? Validate(ApiKeyCreate d)
    {
        var name = d.Name?.Trim() ?? "";
        if (name.Length == 0) return "Giv nøglen et navn (programmet, der skal bruge den).";
        if (name.Length > MaxName) return $"Navnet må højst være {MaxName} tegn.";
        if (name.Any(char.IsControl)) return "Navnet må ikke indeholde styretegn.";
        if (ApiScopes.Level(d.Scope) == 0) return "Vælg en rettighed: kun læse, læse og styre eller administrator.";
        if (d.ExpiresDays is { } days && days != 0 && !ExpiryDays.Contains(days)) return "Udløb skal være aldrig, 30, 90 eller 365 dage.";
        return null;
    }

    public static void MapApiKeyEndpoints(this WebApplication app)
    {
        var adm = app.MapGroup("/api/settings/api-keys").RequireAuthorization("admin");

        adm.MapGet("", async (AppDb db, ApiKeyGuard guard, SettingsService s, EndpointDataSource endpoints) =>
        {
            var keys = await db.ApiKeys.AsNoTracking().Where(k => k.Revoked == null).OrderBy(k => k.Created).ThenBy(k => k.Id).ToListAsync();
            // Seneste afvisning: fra hukommelsen, ellers den gemte (overlever en genstart)
            var rejected = guard.LastRejected;
            if (rejected is null && Json.Des<RejectedRow>(await s.Get(ApiKeyGuard.RejectedSetting)) is { Ip.Length: > 0 } r)
                rejected = new ApiKeyRejection(r.At, r.Ip, r.Reason, r.Name, r.Hint);
            return new
            {
                prefix = ApiKeyFormat.Prefix,
                keys = keys.Select(Row),
                lastRejected = rejected is null ? null : new { at = Utc(rejected.At), rejected.Ip, rejected.Reason, rejected.Name, rejected.Hint },
                // Hvad hver rettighed giver adgang til: læst fra de endpoints, der faktisk kræver en nøgle
                endpoints = endpoints.ApiKeyEndpoints().Select(e => new { method = e.Method, path = e.Path, scope = e.Scope, description = e.Description }),
                limits = new { readPerMinute = ApiKeyGuard.ReadPerMinute, controlPerMinute = ApiKeyGuard.ControlPerMinute },
            };
        });

        adm.MapPost("", async (ApiKeyCreate d, AppDb db, AuditService audit, ClaimsPrincipal u, ApiKeyGuard guard) =>
        {
            if (Validate(d) is { } err) return Results.Problem(err, statusCode: 400);
            var name = d.Name!.Trim();
            var active = await db.ApiKeys.Where(k => k.Revoked == null).Select(k => k.Name).ToListAsync();
            if (active.Count >= MaxKeys) return Results.Problem($"Der kan højst være {MaxKeys} nøgler. Slet en, du ikke bruger.", statusCode: 400);
            if (active.Contains(name, StringComparer.OrdinalIgnoreCase)) return Results.Problem($"Der er allerede en nøgle, der hedder \"{name}\".", statusCode: 400);
            var now = guard.Now.UtcDateTime;
            var key = ApiKeyFormat.NewKey();
            var k = new ApiKey
            {
                Name = name, Scope = d.Scope!, Sha256 = ApiKeyFormat.Hash(key), Hint = ApiKeyFormat.Hint(key),
                Created = now, CreatedBy = u.UserName(), LanOnly = d.LanOnly ?? true,
                Expires = d.ExpiresDays is > 0 and var days ? now.AddDays(days) : null,
            };
            db.ApiKeys.Add(k);
            await db.SaveChangesAsync();
            await audit.Log(u.UserName(), "api_key.created",
                $"{k.Name} (#{k.Id}) · {ScopeNames[k.Scope]} · {ApiKeyFormat.Display(k)}{(k.LanOnly ? " · kun hjemmenettet" : "")}{(k.Expires is { } e ? $" · udløber {e:yyyy-MM-dd}" : "")}");
            // Den eneste gang, nøglen forlader serveren
            return Results.Ok(new { key, item = Row(k) });
        });

        adm.MapPut("/{id:int}", async (int id, ApiKeyChange d, AppDb db, AuditService audit, ClaimsPrincipal u) =>
        {
            var k = await db.ApiKeys.FirstOrDefaultAsync(x => x.Id == id && x.Revoked == null);
            if (k is null) return Results.NotFound();
            if (k.LanOnly == d.LanOnly) return Results.Ok(Row(k));
            k.LanOnly = d.LanOnly;
            await db.SaveChangesAsync();
            await audit.Log(u.UserName(), "api_key.changed", $"{k.Name} (#{k.Id}) · {(d.LanOnly ? "kun fra hjemmenettet" : "også udefra")}");
            return Results.Ok(Row(k));
        });

        adm.MapDelete("/{id:int}", async (int id, AppDb db, AuditService audit, ClaimsPrincipal u, ApiKeyGuard guard) =>
        {
            var k = await db.ApiKeys.FirstOrDefaultAsync(x => x.Id == id && x.Revoked == null);
            if (k is null) return Results.NotFound();
            k.Revoked = guard.Now.UtcDateTime;
            await db.SaveChangesAsync();
            guard.Forget(k.Id);
            await audit.Log(u.UserName(), "api_key.deleted", $"{k.Name} (#{k.Id}) · {ApiKeyFormat.Display(k)}");
            return Results.Ok();
        });
    }

    private sealed class RejectedRow
    {
        public DateTime At { get; set; }
        public string Ip { get; set; } = "";
        public string Reason { get; set; } = "";
        public string? Name { get; set; }
        public string? Hint { get; set; }
    }
}
