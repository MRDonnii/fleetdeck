using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Shared;

namespace Fleetdeck.Server.Services;

/// <summary>
/// API-nøgler til programmer (Indstillinger → Adgang for programmer): format, hash og hvor nøglen læses fra.
/// Nøglen er <c>fd_</c> + 32 tilfældige bytes i base64url (43 tegn). Kun SHA-256 (hex) og de sidste 4 tegn gemmes.
/// Den sendes som <c>Authorization: Bearer &lt;nøgle&gt;</c> eller <c>X-Api-Key: &lt;nøgle&gt;</c> – aldrig i adressen.
/// </summary>
public static class ApiKeyFormat
{
    public const string Prefix = "fd_", Header = "X-Api-Key";
    /// <summary>Længere end det er ingen nøgle (spild ikke tid på at hashe store headere).</summary>
    public const int MaxLength = 256;

    public static string NewKey() => Prefix + Crypto.Token(32);
    public static string Hash(string key) => Crypto.Sha256(key);
    public static string Hint(string key) => key.Length <= 4 ? "" : key[^4..];

    /// <summary>Sådan vises nøglen i listen: <c>fd_…ab12</c>. Den flyttede gamle nøgle havde intet præfiks.</summary>
    public static string Display(ApiKey k) => (k.Migrated ? "" : Prefix) + "…" + k.Hint;

    /// <summary>Nøglen fra <c>Authorization: Bearer</c> eller <c>X-Api-Key</c>. Null = ingen nøgle. Adressens query-streng læses aldrig.</summary>
    public static string? FromRequest(HttpRequest r)
    {
        var auth = r.Headers.Authorization.ToString().Trim();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && auth[7..].Trim() is { Length: > 0 } bearer) return bearer;
        return r.Headers[Header].ToString().Trim() is { Length: > 0 } x ? x : null;
    }
}

/// <summary>Endpointet kræver en API-nøgle med mindst denne rettighed. Bruges også til at vise, hvad hver rettighed giver adgang til.</summary>
public sealed record ApiKeyScopeMetadata(string Scope, string Description);

/// <summary>Den godkendte nøgle (ligger i HttpContext.Items, så endpointet kan se hvem der kalder).</summary>
public sealed record ApiCaller(int Id, string Name, string Scope);

public sealed record ApiKeyRejection(DateTime At, string Ip, string Reason, string? Name, string? Hint);

/// <summary>
/// Godkender kald med API-nøgle og holder styr på grænserne i hukommelsen:
/// <list type="bullet">
/// <item>pr. nøgle højst 60 læse-kald og 10 styre-kald i minuttet (Kontrolrums værdier),</item>
/// <item>pr. IP bremses forkerte nøgler efter 10 forsøg på 5 minutter (429 med Retry-After),</item>
/// <item>"sidst brugt" skrives højst hvert 60. sekund pr. nøgle (eller når IP'en skifter), så databasen ikke slides.</item>
/// </list>
/// Loggen og revisionsloggen får nøglens navn og id – aldrig selve nøglen.
/// </summary>
public sealed class ApiKeyGuard(TimeProvider clock, ILogger<ApiKeyGuard> log)
{
    public const string RejectedSetting = "api_key_rejected";
    public const int ReadPerMinute = 60, ControlPerMinute = 10, FailuresPerIp = 10;
    public static readonly TimeSpan Minute = TimeSpan.FromMinutes(1), FailureWindow = TimeSpan.FromMinutes(5), UseWriteEvery = TimeSpan.FromSeconds(60);

    private sealed class Window { public DateTimeOffset Start; public int Count; }
    private sealed class Use { public DateTimeOffset? Written; public string? Ip; public long Pending; }

    private readonly ConcurrentDictionary<string, Window> failures = new();
    private readonly ConcurrentDictionary<string, Window> calls = new();
    private readonly ConcurrentDictionary<int, Use> uses = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> rejectWritten = new();
    private ApiKeyRejection? lastRejected;

    public DateTimeOffset Now => clock.GetUtcNow();
    public ApiKeyRejection? LastRejected => lastRejected;

    /// <summary>Fast vindue: true = kaldet må gå igennem; ellers sekunder til vinduet starter forfra.</summary>
    private bool Take(ConcurrentDictionary<string, Window> map, string key, int limit, TimeSpan window, out int retryAfter, bool count = true)
    {
        var now = Now;
        var w = map.GetOrAdd(key, _ => new Window { Start = now });
        lock (w)
        {
            if (now - w.Start >= window) { w.Start = now; w.Count = 0; }
            retryAfter = Math.Max(1, (int)Math.Ceiling((w.Start + window - now).TotalSeconds));
            if (w.Count >= limit) return false;
            if (count) w.Count++;
            return true;
        }
    }

    /// <summary>Er IP'en bremset efter for mange forkerte nøgler?</summary>
    public bool Blocked(string ip, out int retryAfter) => !Take(failures, ip, FailuresPerIp, FailureWindow, out retryAfter, count: false);

    /// <summary>Et forkert forsøg fra IP'en (tæller mod bremsen).</summary>
    public void Failed(string ip) => Take(failures, ip, int.MaxValue, FailureWindow, out _);

    /// <summary>Pr. nøgle: læse-kald og styre-kald (control/admin) har hver sin grænse.</summary>
    public bool TakeCall(int keyId, string need, out int retryAfter) => ApiScopes.Level(need) >= ApiScopes.Level(ApiScopes.Control)
        ? Take(calls, $"{keyId}|control", ControlPerMinute, Minute, out retryAfter)
        : Take(calls, $"{keyId}|read", ReadPerMinute, Minute, out retryAfter);

    /// <summary>Tæl brugen. Returnerer antallet af kald, der skal skrives nu (0 = vent, der er skrevet for nylig fra samme IP).</summary>
    public long TakeUse(int keyId, string ip)
    {
        var now = Now;
        var u = uses.GetOrAdd(keyId, _ => new Use());
        lock (u)
        {
            u.Pending++;
            if (u.Written is { } w && now - w < UseWriteEvery && u.Ip == ip) return 0;
            var n = u.Pending;
            u.Pending = 0; u.Written = now; u.Ip = ip;
            return n;
        }
    }

    /// <summary>Glem tællerne for en slettet nøgle.</summary>
    public void Forget(int keyId)
    {
        uses.TryRemove(keyId, out _);
        calls.TryRemove($"{keyId}|read", out _);
        calls.TryRemove($"{keyId}|control", out _);
    }

    /// <summary>Husk afvisningen. true = skriv den også i databasen/revisionsloggen (højst hvert 60. sekund pr. IP).</summary>
    public bool Rejected(ApiKeyRejection r)
    {
        lastRejected = r;
        var now = Now;
        var write = true;
        rejectWritten.AddOrUpdate(r.Ip, now, (_, prev) => { write = now - prev >= UseWriteEvery; return write ? now : prev; });
        return write;
    }

    public static string? ClientIp(HttpContext ctx) => ctx.Connection.RemoteIpAddress is { } ip
        ? (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString() : null;

    /// <summary>
    /// Kommer kaldet fra hjemmenettet/VPN? Klientens IP (X-Forwarded-For læses kun fra betroede proxyer) skal være privat eller i
    /// serverens eget IPv6-præfiks, og kaldet må ikke være kommet ind på den offentlige MDM-adresse. Gennem Cloudflare-tunnelen
    /// (cloudflared forbinder fra 127.0.0.1, men Cloudflare sætter altid CF-Connecting-IP/CF-Ray) regnes kaldet ALDRIG hjemme –
    /// heller ikke fra hjemmets egen offentlige IP, som andre kunder kan dele (CGNAT). Udefra kræver "Overalt" (ejerens valg, 3.44.2).
    /// </summary>
    public static async Task<bool> FromHomeNetwork(HttpContext ctx)
    {
        var call = HomeNetwork.Read(ctx);
        return HomeNetwork.IsHome(call, await OwnNetwork.For(ctx, call, trustTunnel: false), trustTunnel: false);
    }

    /// <summary>Tjek nøglen for et endpoint, der kræver <paramref name="need"/>. Null = godkendt (ApiCaller ligger i Items), ellers svaret.</summary>
    public async Task<IResult?> Check(HttpContext ctx, string need)
    {
        var ip = ClientIp(ctx) ?? "lokal";
        if (Blocked(ip, out var wait))
        {
            ctx.Response.Headers.RetryAfter = wait.ToString(CultureInfo.InvariantCulture);
            return Error(429, "too_many_attempts");
        }
        var given = ApiKeyFormat.FromRequest(ctx.Request);
        if (given is null) return Error(401, "api_key_missing");

        var db = ctx.RequestServices.GetRequiredService<AppDb>();
        ApiKey? key = null;
        if (given.Length <= ApiKeyFormat.MaxLength)
        {
            var hash = ApiKeyFormat.Hash(given);
            key = await db.ApiKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Sha256 == hash);
        }
        var now = Now.UtcDateTime;
        string? reason = key switch
        {
            null => "ukendt nøgle",
            { Revoked: not null } => "slettet nøgle",
            { Expires: { } e } when e <= now => "udløbet nøgle",
            _ => null,
        };
        if (reason is not null)
        {
            Failed(ip);
            await Reject(ctx, new(now, ip, reason, key?.Name, given.Length <= ApiKeyFormat.MaxLength ? ApiKeyFormat.Hint(given) : null), key?.Id);
            return Error(401, "api_key_invalid");
        }
        // nøgler "kun fra hjemmenettet": ikke gennem tunnelen, selv fra serverens offentlige IP (bag CGNAT deles den af flere)
        if (key!.LanOnly && !await FromHomeNetwork(ctx))
        {
            await Reject(ctx, new(now, ip, "kun fra hjemmenettet", key.Name, key.Hint), key.Id);
            return Error(403, "api_key_home_network_only");
        }
        if (!ApiScopes.Allows(key.Scope, need))
        {
            await Reject(ctx, new(now, ip, $"mangler rettigheden {need}", key.Name, key.Hint), key.Id);
            return Error(403, "api_key_scope");
        }
        if (!TakeCall(key.Id, need, out wait))
        {
            ctx.Response.Headers.RetryAfter = wait.ToString(CultureInfo.InvariantCulture);
            log.LogWarning("API-nøglen {Name} (#{Id}) har ramt grænsen for kald", key.Name, key.Id);
            return Error(429, "rate_limited");
        }
        if (TakeUse(key.Id, ip) is > 0 and var n)
            await db.ApiKeys.Where(k => k.Id == key.Id).ExecuteUpdateAsync(s => s
                .SetProperty(k => k.LastUsed, now).SetProperty(k => k.LastIp, ip).SetProperty(k => k.Uses, k => k.Uses + n));
        ctx.Items[typeof(ApiCaller)] = new ApiCaller(key.Id, key.Name, key.Scope);
        log.LogDebug("API-nøglen {Name} (#{Id}) kaldte {Path}", key.Name, key.Id, ctx.Request.Path.Value);
        return null;
    }

    private async Task Reject(HttpContext ctx, ApiKeyRejection r, int? keyId)
    {
        if (r.Name is { } name) log.LogWarning("API-nøglen {Name} (#{Id}) blev afvist fra {Ip}: {Reason}", name, keyId, r.Ip, r.Reason);
        else log.LogWarning("API-nøgle afvist fra {Ip}: {Reason}", r.Ip, r.Reason);
        if (!Rejected(r)) return;
        var sp = ctx.RequestServices;
        await sp.GetRequiredService<SettingsService>().Set(RejectedSetting, Json.Ser(r));
        var who = r.Name is { } n2 ? $"{n2} (#{keyId})" : (r.Hint is { Length: > 0 } h ? $"…{h}" : "nøgle");
        await sp.GetRequiredService<AuditService>().Log("api", "api_key.rejected", $"{who} · {r.Reason} · fra {r.Ip}");
    }

    private static IResult Error(int status, string code) => Results.Json(new { error = code }, statusCode: status);
}

public static class ApiKeyEndpointExtensions
{
    /// <summary>Endpointet kan kun kaldes med en API-nøgle, der har mindst rettigheden <paramref name="scope"/>.</summary>
    public static TBuilder RequireApiKey<TBuilder>(this TBuilder builder, string scope, string description) where TBuilder : IEndpointConventionBuilder
    {
        if (ApiScopes.Level(scope) == 0) throw new ArgumentException($"Ukendt rettighed: {scope}", nameof(scope));
        builder.WithMetadata(new ApiKeyScopeMetadata(scope, description));
        return builder.AddEndpointFilter(async (fc, next) =>
            await fc.HttpContext.RequestServices.GetRequiredService<ApiKeyGuard>().Check(fc.HttpContext, scope) ?? await next(fc));
    }

    /// <summary>Alle endpoints med API-nøgle og den rettighed, de kræver (til siden og testene).</summary>
    public static List<(string Method, string Path, string Scope, string Description)> ApiKeyEndpoints(this EndpointDataSource source) =>
        source.Endpoints.OfType<RouteEndpoint>()
            .Select(e => (e, meta: e.Metadata.GetMetadata<ApiKeyScopeMetadata>()))
            .Where(x => x.meta is not null)
            .Select(x => (string.Join(",", x.e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"]), "/" + (x.e.RoutePattern.RawText ?? "").TrimStart('/'), x.meta!.Scope, x.meta.Description))
            .OrderBy(x => ApiScopes.Level(x.Scope)).ThenBy(x => x.Item2).ToList();
}

/// <summary>
/// Flytter den gamle enkelt-nøgle til Kontrolrum (<c>kontrolrum_key</c>, gemt i klartekst og sendt i <c>X-Api-Key</c>) over i
/// tabellen som nøglen "Kontrolrum" med samme rettighed som før (kun læse status). Kun hashen gemmes, og klarteksten slettes,
/// så den ikke længere kan ses nogen steder. Kontrolrum kalder videre med samme nøgle, så intet skal ændres dér.
/// Den gamle nøgle kunne bruges udefra, så "kun hjemmenettet" er slået fra på den – ejeren kan slå det til på siden.
/// </summary>
public static class ApiKeyMigration
{
    public const string OldSetting = "kontrolrum_key", MigratedName = "Kontrolrum";

    /// <summary>true = en nøgle blev flyttet. Kan køres ved hver opstart (gør intet, når der ikke er en gammel nøgle).</summary>
    public static async Task<bool> FromKontrolrumKey(AppDb db, SettingsService settings, DateTime now)
    {
        var old = (await settings.Get(OldSetting)).Trim();
        if (old.Length == 0) return false;
        var hash = ApiKeyFormat.Hash(old);
        var added = false;
        if (!await db.ApiKeys.AnyAsync(k => k.Sha256 == hash))
        {
            db.ApiKeys.Add(new ApiKey
            {
                Name = MigratedName, Scope = ApiScopes.Read, Sha256 = hash, Hint = ApiKeyFormat.Hint(old),
                Created = now, CreatedBy = "system", LanOnly = false, Migrated = true,
            });
            db.Audit.Add(AuditService.Entry("system", "api_key.migrated", $"{MigratedName} · kun læse · …{ApiKeyFormat.Hint(old)} · flyttet fra den gamle Kontrolrum-nøgle"));
            await db.SaveChangesAsync();
            added = true;
        }
        await settings.Set(OldSetting, "");
        return added;
    }
}
