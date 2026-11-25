using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services;

namespace Fleetdeck.Server.Endpoints;

public sealed record BackupCreateRequest(string? Password, bool UseStored, bool IncludeKits, bool IncludeIpad);
public sealed record BackupRestoreRequest(string? Password, string Confirm);

/// <summary>Backup og gendannelse af Fleetdeck selv (Indstillinger → Backup, og "Gendan fra backup" ved første start).</summary>
public static class BackupEndpoints
{
    public const string ConfirmWord = "GENDAN";
    private const long MaxUpload = 64L * 1024 * 1024 * 1024;

    private static IResult AlreadySetUp() => Results.Problem("Serveren er allerede sat op – log ind og gendan under Indstillinger → Backup", statusCode: 403);
    private static IResult Bad(string msg) => Results.Problem(msg, statusCode: 400);
    private static IResult Fail(BackupException e) => Results.Problem(e.Message, statusCode: e.Code == "password" ? 422 : 400, extensions: new Dictionary<string, object?> { ["code"] = e.Code });

    /// <summary>
    /// En hentet backup indeholder hele serveren og skrives derfor i audit-loggen ligesom sletning og gendannelse. En delhentning,
    /// der fortsætter (Range, der ikke starter ved 0), er samme hentning og logges ikke igen.
    /// </summary>
    public static async Task LogDownload(AuditService audit, string user, string name, string? range)
    {
        if (range is { Length: > 0 } && !range.Replace(" ", "").StartsWith("bytes=0-", StringComparison.OrdinalIgnoreCase)) return;
        await audit.Log(user, "backup.download", name);
    }

    public static void MapBackupEndpoints(this WebApplication app)
    {
        var adm = app.MapGroup("/api/backup").RequireAuthorization("admin");

        adm.MapGet("", async (SelfBackupService b) =>
        {
            var (kits, ipad) = b.LargeSizes();
            return Results.Ok(new
            {
                schedule = await b.Schedule(), status = await b.Status(), files = await b.List(),
                sizes = new { kits, ipad }, pending = SelfBackup.Pending(b.DataDir), lastRestore = SelfBackup.LastResult(b.DataDir),
                dataDir = b.DataDir, minPassword = SelfBackup.MinPasswordLength, confirmWord = ConfirmWord,
            });
        });

        // Kun alarmen (til banneret i toppen af siden)
        adm.MapGet("/alert", async (SelfBackupService b) => Results.Ok(new { alert = (await b.Status()).Alert }));

        adm.MapPut("/schedule", async (BackupScheduleUpdate r, SelfBackupService b, AuditService audit, ClaimsPrincipal u) =>
        {
            if (await b.SaveSchedule(r) is { } err) return Bad(err);
            await audit.Log(u.UserName(), "backup.schedule", $"{(r.Enabled ? "til" : "fra")} kl. {r.Hour}, behold {r.Keep}" + (r.Password is null ? "" : r.Password.Length == 0 ? ", adgangskode fjernet" : ", ny adgangskode"));
            await b.CheckAlert();
            return Results.Ok(await b.Schedule());
        });

        adm.MapPost("/create", async (BackupCreateRequest r, SelfBackupService b, AuditService audit, ClaimsPrincipal u) =>
        {
            try
            {
                var name = await b.CreateManual(r.Password, r.UseStored, new(r.IncludeKits, r.IncludeIpad));
                await audit.Log(u.UserName(), "backup.create", name);
                return Results.Ok(new { name });
            }
            catch (BackupException e) { return Fail(e); }
        });

        adm.MapPost("/run", async (SelfBackupService b, AuditService audit, ClaimsPrincipal u) =>
        {
            var err = await b.RunScheduled();
            await audit.Log(u.UserName(), "backup.run", err ?? "ok");
            return err is null ? Results.Ok() : Bad(err);
        });

        adm.MapGet("/files/{name}", async (string name, HttpContext ctx, SelfBackupService b, AuditService audit, ClaimsPrincipal u) =>
        {
            if (await b.Find(name) is not { } f) return Results.NotFound();
            await LogDownload(audit, u.UserName(), name, ctx.Request.Headers.Range.ToString());
            return Results.File(f, "application/octet-stream", name, enableRangeProcessing: true);
        });

        adm.MapDelete("/files/{name}", async (string name, SelfBackupService b, AuditService audit, ClaimsPrincipal u) =>
        {
            if (await b.Find(name) is not { } f) return Results.NotFound();
            File.Delete(f);
            await audit.Log(u.UserName(), "backup.delete", name);
            return Results.Ok();
        });

        // Gendan fra en gemt backup
        adm.MapPost("/files/{name}/restore", async (string name, BackupRestoreRequest r, SelfBackupService b, AppDb db, AuditService audit, ClaimsPrincipal u) =>
        {
            if (r.Confirm?.Trim() != ConfirmWord) return Bad($"Skriv {ConfirmWord} for at bekræfte");
            if (await b.Find(name) is not { } f) return Results.NotFound();
            return await Restore(b, db, audit, u.UserName(), f, r.Password, name);
        });

        // Gendan fra en uploadet fil: 1) upload (rå fil i body) → hovedet vises, 2) adgangskode + bekræftelse
        adm.MapPost("/upload", (HttpContext ctx, SelfBackupService b) => Upload(ctx, b))
            .DisableAntiforgery().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxUpload));

        adm.MapPost("/uploads/{id}/restore", async (string id, BackupRestoreRequest r, SelfBackupService b, AppDb db, AuditService audit, ClaimsPrincipal u) =>
        {
            if (r.Confirm?.Trim() != ConfirmWord) return Bad($"Skriv {ConfirmWord} for at bekræfte");
            if (b.UploadFile(id) is not { } f) return Results.NotFound();
            return await Restore(b, db, audit, u.UserName(), f, r.Password, "upload");
        }).RequireRateLimiting("login");

        adm.MapDelete("/pending", (SelfBackupService b) => { SelfBackup.CancelPending(b.DataDir); return Results.Ok(); });
        adm.MapDelete("/last-restore", (SelfBackupService b) => { SelfBackup.ClearResult(b.DataDir); return Results.Ok(); });

        // ---- Første start: en tom server (ingen brugere) kan gendannes fra login-siden ----
        var first = app.MapGroup("/api/backup/first-run");
        first.MapGet("", async (AppDb db) => Results.Ok(new { available = !await db.Users.AnyAsync(), confirmWord = ConfirmWord }));
        first.MapPost("/upload", async (HttpContext ctx, SelfBackupService b, AppDb db) =>
            await db.Users.AnyAsync() ? AlreadySetUp() : await Upload(ctx, b))
            .DisableAntiforgery().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxUpload));
        first.MapPost("/uploads/{id}/restore", async (string id, BackupRestoreRequest r, SelfBackupService b, AppDb db, AuditService audit) =>
        {
            if (await db.Users.AnyAsync()) return AlreadySetUp();
            if (r.Confirm?.Trim() != ConfirmWord) return Bad($"Skriv {ConfirmWord} for at bekræfte");
            if (b.UploadFile(id) is not { } f) return Results.NotFound();
            return await Restore(b, db, audit, "første start", f, r.Password, "upload");
        }).RequireRateLimiting("login");
    }

    private static async Task<IResult> Upload(HttpContext ctx, SelfBackupService b)
    {
        try
        {
            var (id, h) = await b.SaveUpload(ctx.Request.Body, ctx.RequestAborted);
            return Results.Ok(new
            {
                id, kind = h.Kind, created = h.Created, appVersion = h.AppVersion, contents = h.Contents,
                encryption = h.Kdf == "local" ? "local" : "password",
            });
        }
        catch (BackupException e) { return Fail(e); }
    }

    private static async Task<IResult> Restore(SelfBackupService b, AppDb db, AuditService audit, string user, string file, string? password, string source)
    {
        try
        {
            var plan = await b.Stage(file, password, source, db);
            await audit.Log(user, "backup.restore", $"{source}: backup fra {plan.BackupCreated:u} (v{plan.BackupVersion}) – genstarter");
            if (source == "upload") SelfBackup.TryDeleteDir(b.UploadFolder);
            b.RequestRestart();
            return Results.Ok(new { restarting = true, plan });
        }
        catch (BackupException e) { return Fail(e); }
    }
}
