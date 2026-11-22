using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;

namespace Fleetdeck.Server.Endpoints;

/// <summary>"Anmod om adgang" (se VEJLEDNING.md): anonyme kald fra agenten og administratorens liste, godkend, afvis og send nøgle.</summary>
public static class AccessRequestEndpoints
{
    public sealed record ApproveBody(int? ProfileId, string? Name);
    public sealed record RejectBody(string? Reason);
    public sealed record SendKeyBody(int? ProfileId, double? Hours);
    public sealed record BlockBody(bool Ip, bool Mac, bool Name, double? Hours, string? Reason);

    /// <summary>Klientens adresse; "socket" uden IP (socket-filen på Unraid-værten, som tæller som hjemmenet).</summary>
    private static string Ip(HttpContext ctx) => ApiKeyGuard.ClientIp(ctx) ?? "socket";

    public static void MapAccessRequestEndpoints(this WebApplication app)
    {
        // Agenten (uden nøgle og login): opret og spørg. Alt i POST-body, så hemmeligheden ikke havner i adresser eller logs.
        // Første skridt: forpligtelsen (uden hemmelighed) – svaret er serverens nonce. Højst 5 i timen pr. adresse.
        app.MapPost("/api/agent/access-requests", async (AccessRequestCreate r, HttpContext ctx, AccessRequestService svc, SettingsService s, TlsInfo tls) =>
        {
            var directTls = ctx.Features.Get<ITlsConnectionFeature>() is not null;
            var pin = AccessRequestService.ChoosePin(directTls, tls.Pin, await s.Get("tls_pin"), r.ObservedPin);
            var o = await svc.Start(r, Ip(ctx), await ApiKeyGuard.FromHomeNetwork(ctx), pin);
            // 403 med "reason": "outside" (kun hjemmenet, kaldet kom udefra) eller "off" (slået fra/blokeret) – ældre agenter ser bare 403 som før
            return o.Started is { } started ? Results.Ok(started)
                : Results.Problem(o.Error, statusCode: o.Status, extensions: o.Reason is { } why ? new Dictionary<string, object?> { ["reason"] = why } : null);
        }).RequireRateLimiting(AccessRequestService.RateLimitPolicy);

        // Andet skridt: hemmeligheden og agentens offentlige nøgle – først nu ser administratoren anmodningen
        app.MapPost("/api/agent/access-requests/reveal", async (AccessReveal r, HttpContext ctx, AccessRequestService svc, Notifier notifier, Live live) =>
        {
            var o = await svc.Reveal(r, Ip(ctx));
            if (o.Row is not { } row) return Results.Problem(o.Error, statusCode: o.Status);
            if (o.Fresh)
            {
                if (o.Notice is { } n) notifier.Send(n);
                await live.Changed("access-requests");
            }
            return Results.Ok(new AccessRequestCreated(row.Id, row.Code, row.Expires, 30));
        }).RequireRateLimiting(AccessRequestService.PollRateLimitPolicy);

        app.MapPost("/api/agent/access-requests/{id:int}/poll", async (int id, AccessPollRequest r, HttpContext ctx, AccessRequestService svc, Live live) =>
        {
            // slået fra, eller "kun hjemmenet" og kaldet kommer udefra: afvis tidligt (agenten prøver selv igen senere)
            if (!await svc.Accepts(await ApiKeyGuard.FromHomeNetwork(ctx))) return Results.StatusCode(403);
            var res = await svc.Poll(id, r.Secret, Ip(ctx));
            if (res is null) return Results.NotFound();
            if (res.State is AccessStates.Issued or AccessStates.Expired) await live.Changed("access-requests");
            return Results.Ok(res);
        }).RequireRateLimiting(AccessRequestService.PollRateLimitPolicy);

        var adm = app.MapGroup("/api/access-requests").RequireAuthorization("admin");
        adm.MapGet("", async (AccessRequestService svc) => await svc.List());
        adm.MapPost("/{id:int}/approve", async (int id, ApproveBody b, AccessRequestService svc, Live live, ClaimsPrincipal u) =>
        {
            var d = await svc.Approve(id, b.ProfileId, b.Name, u.UserName());
            if (d.Status != 200) return Results.Problem(d.Error, statusCode: d.Status);
            await live.Changed("access-requests");
            return Results.Ok(new { id });
        });
        adm.MapPost("/{id:int}/reject", async (int id, RejectBody b, AccessRequestService svc, Live live, ClaimsPrincipal u) =>
        {
            var d = await svc.Reject(id, b.Reason, u.UserName());
            if (d.Status != 200) return Results.Problem(d.Error, statusCode: d.Status);
            await live.Changed("access-requests");
            return Results.Ok(new { id });
        });
        // "Bloker…": IP (og evt. MAC/navn) med udløb; åbne anmodninger, der passer, afvises med det samme
        adm.MapPost("/{id:int}/block", async (int id, BlockBody b, AccessRequestService svc, Live live, ClaimsPrincipal u) =>
        {
            var d = await svc.Block(id, b.Ip, b.Mac, b.Name, b.Hours, b.Reason, u.UserName());
            if (d.Status != 200) return Results.Problem(d.Error, statusCode: d.Status);
            await live.Changed("access-requests");
            return Results.Ok(new { blocks = d.Blocks!.Count, closed = d.Closed });
        });
        var blocks = app.MapGroup("/api/access-blocks").RequireAuthorization("admin");
        blocks.MapGet("", async (AccessRequestService svc) => await svc.Blocks());
        blocks.MapDelete("/{id:int}", async (int id, AccessRequestService svc, Live live, ClaimsPrincipal u) =>
        {
            if (!await svc.Unblock(id, u.UserName())) return Results.NotFound();
            await live.Changed("access-requests");
            return Results.Ok(new { id });
        });
        adm.MapPost("/{id:int}/send-key", async (int id, SendKeyBody b, HttpContext ctx, AccessRequestService svc, SettingsService s, Live live, ClaimsPrincipal u) =>
        {
            var d = await svc.SendKey(id, b.ProfileId, b.Hours, u.UserName());
            if (d.Status != 200) return Results.Problem(d.Error, statusCode: d.Status);
            await live.Changed("access-requests");
            var url = await s.Get("server_url") is { Length: > 0 } su ? su.TrimEnd('/') : $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            // nøglen står i fragmentet (#k=), så den ikke sendes til serveren eller havner i logs, når linket åbnes
            return Results.Ok(new
            {
                code = d.Key, link = $"{url}/hent#k={d.Key}",
                linux = $"curl -fsSL \"{url}/install.sh?key={d.Key}\" | sudo bash",
                windows = $"irm \"{url}/install.ps1?key={d.Key}\" | iex",
            });
        });
    }
}
