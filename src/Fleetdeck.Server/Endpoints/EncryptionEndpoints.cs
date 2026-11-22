using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;

namespace Fleetdeck.Server.Endpoints;

public sealed record RevealRequest(string? Reauth);

/// <summary>
/// Kryptering på maskinens side: status pr. drev, gemte gendannelsesnøgler (uden selve nøglen), "Vis nøgle" og
/// ny FileVault-nøgle. Selve nøglen sendes kun ud af <c>/reveal</c>: administrator + bekræftelse inden for 5 minutter.
/// </summary>
public static class EncryptionEndpoints
{
    public static void MapEncryptionEndpoints(this WebApplication app)
    {
        // Bekræft at det er dig (adgangskode eller kode fra appen): giver et bevis, der gælder i 5 minutter
        app.MapPost("/api/account/reauth", async (ProofRequest r, ClaimsPrincipal p, AppDb db, TwoFactorService tf, ReauthTokens tokens, AuditService audit, HttpContext ctx) =>
        {
            NoStore(ctx);
            var u = await db.Users.FindAsync(p.UserId());
            if (u is null) return Results.Unauthorized();
            if (await TwoFactorEndpoints.Prove(u, r, db, tf, audit, ctx, "account.reauth_failed") is { } denied) return denied;
            await db.SaveChangesAsync();
            var (token, expires) = tokens.Issue(u);
            await audit.Log(u.Username, "account.reauth", AuthEndpoints.ClientIp(ctx));
            return Results.Ok(new { token, expires });
        }).RequireAuthorization("any").RequireRateLimiting("login");

        app.MapGet("/api/machines/{id:int}/encryption", async (int id, AppDb db, RecoveryKeyVault vault) =>
        {
            var m = await db.Machines.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (m is null) return Results.NotFound();
            var h = Json.Des<HealthReport>(m.HealthJson);
            var enc = h.Encryption;
            if (enc is not null) enc.KeysHash = null;
            var job = await db.Jobs.AsNoTracking().Where(j => j.MachineId == id && j.Type == JobTypes.FileVaultKey).OrderByDescending(j => j.Id)
                .Select(j => new { j.Id, j.Status, j.Created, j.Finished }).FirstOrDefaultAsync();
            return Results.Ok(new
            {
                encryption = enc, legacy = h.BitLocker, keys = await vault.List(id), rotateJob = job,
                canRotate = m.Platform == Platforms.Mac && enc?.Kind == EncryptionKinds.FileVault && enc.Volumes.Any(v => v.Protected == true),
            });
        }).RequireAuthorization("user");

        app.MapPost("/api/machines/{id:int}/recovery-keys/{keyId:int}/reveal", async (int id, int keyId, RevealRequest r, ClaimsPrincipal p,
            RecoveryKeyVault vault, ReauthTokens tokens, AuditService audit, Notifier notifier, HttpContext ctx) =>
        {
            NoStore(ctx);
            var res = await vault.Reveal(p, id, keyId, r.Reauth, tokens, audit.Log, t => notifier.Send(t, "warning"));
            return res.Status == 200
                ? Results.Ok(new { key = res.Key, view = res.View })
                : Results.Problem(res.Error, statusCode: res.Status, extensions: res.NeedsReauth ? new Dictionary<string, object?> { ["reauth"] = true } : null);
        }).RequireAuthorization("admin").RequireRateLimiting(EmergencyAccess.RateLimitPolicy);

        // Mac: ny personlig FileVault-nøgle – brugeren på Mac'en godkender med sin egen adgangskode
        app.MapPost("/api/machines/{id:int}/encryption/rotate", async (int id, AppDb db, JobService jobs, AuditService audit, ClaimsPrincipal u) =>
        {
            var m = await db.Machines.FindAsync(id);
            if (m is null) return Results.NotFound();
            if (m.Platform != Platforms.Mac) return Bad("Ny gendannelsesnøgle kan kun bestilles til en Mac. Windows sender selv BitLocker-nøglen.");
            var enc = Json.Des<HealthReport>(m.HealthJson).Encryption;
            if (enc?.Kind != EncryptionKinds.FileVault || !enc.Volumes.Any(v => v.Protected == true)) return Bad("FileVault er ikke slået til på Mac'en");
            if (await db.Jobs.AnyAsync(j => j.MachineId == id && j.Type == JobTypes.FileVaultKey && JobStatus.Active.Contains(j.Status)))
                return Bad("Der venter allerede et job til en ny nøgle på denne Mac");
            var j = await jobs.Create(id, JobTypes.FileVaultKey, "Ny gendannelsesnøgle til Fleetdeck", new JobParams { TimeoutMin = 10 }, RunAs.System, null, "manual", u.UserName());
            await audit.Log(u.UserName(), RecoveryKeyVault.AuditRotate, $"{m.Label}: ny FileVault-nøgle bestilt (job {j.Id})", m.Id);
            return Results.Ok(new { j.Id });
        }).RequireAuthorization("admin");
    }

    private static IResult Bad(string msg) => Results.Problem(msg, statusCode: 400);

    private static void NoStore(HttpContext ctx)
    {
        ctx.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate, max-age=0";
        ctx.Response.Headers.Pragma = "no-cache";
    }
}
