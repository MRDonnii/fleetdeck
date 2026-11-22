using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Hubs;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;

namespace Fleetdeck.Server.Endpoints;

public sealed record HelpdeskSendRequest(List<int>? MachineIds, string? Tag, int? ProfileId, string Text, List<HelpdeskButton>? Buttons);

/// <summary>Beskeder (helpdesk): kun administratorer læser og skriver; agenten kvitterer og sender svar med sit eget token.</summary>
public static class HelpdeskEndpoints
{
    public static void MapHelpdeskEndpoints(this WebApplication app)
    {
        var adm = app.MapGroup("/api").RequireAuthorization("admin");

        // oversigten: alle samtaler (åbne, lukkede eller alle) med ulæste svar
        adm.MapGet("/helpdesk", async (string? status, AppDb db, SettingsService s) =>
        {
            var poll = await s.GetNum("poll_seconds", 30);
            var since = DateTime.UtcNow.AddSeconds(-poll * 3 - 60);
            var q = db.HelpdeskConversations.AsNoTracking();
            if (status is HelpdeskStatus.Open or HelpdeskStatus.Closed) q = q.Where(c => c.Status == status);
            var rows = await q.OrderByDescending(c => c.LastActivity).Take(500).Select(c => new
            {
                c.MachineId, machine = c.Machine!.DisplayName ?? c.Machine.Name, c.Machine.Platform, online = c.Machine.LastSeen > since, user = c.Machine.User,
                c.Status, c.LastActivity, c.Preview, c.LastFromMachine,
                unread = db.HelpdeskMessages.Count(x => x.ConversationId == c.Id && x.FromMachine && x.ReadAt == null),
                waiting = db.HelpdeskMessages.Count(x => x.ConversationId == c.Id && !x.FromMachine && x.DeliveredAt == null),
            }).ToListAsync();
            var unread = await db.HelpdeskMessages.CountAsync(x => x.FromMachine && x.ReadAt == null);
            return Results.Ok(new { unread, conversations = rows });
        });

        // til menuen: antal ulæste svar
        adm.MapGet("/helpdesk/unread", async (AppDb db) => Results.Ok(new { unread = await db.HelpdeskMessages.CountAsync(x => x.FromMachine && x.ReadAt == null) }));

        adm.MapGet("/machines/{id:int}/helpdesk", async (int id, AppDb db, HelpdeskService hd) =>
        {
            if (await db.Machines.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id) is not { } m) return Results.NotFound();
            var c = await db.HelpdeskConversations.AsNoTracking().FirstOrDefaultAsync(x => x.MachineId == id);
            var msgs = c is null ? [] : await db.HelpdeskMessages.AsNoTracking().Where(x => x.ConversationId == c.Id)
                .OrderByDescending(x => x.Id).Take(200).ToListAsync();
            var set = await hd.GetSettings();
            // logfiler (uden selve data): dem brugeren sendte med en besked, og dem admin hentede
            var files = (await db.HelpdeskAttachments.AsNoTracking().Where(a => a.MachineId == id).OrderBy(a => a.Id)
                .Select(a => new { a.Id, a.MessageId, a.JobId, a.Kind, a.Name, a.Size, a.EntriesJson, a.By, a.Created }).ToListAsync())
                .Select(a => new { a.Id, a.MessageId, a.JobId, a.Kind, a.Name, a.Size, entries = Json.Des<List<LogEntry>>(a.EntriesJson), a.By, a.Created }).ToList();
            var logsJob = await db.Jobs.AsNoTracking().Where(j => j.MachineId == id && j.Type == JobTypes.CollectLogs).OrderByDescending(j => j.Id)
                .Select(j => new { j.Id, j.Status, j.Created, j.Finished, j.Output, j.CreatedBy }).FirstOrDefaultAsync();
            return Results.Ok(new
            {
                machineId = id, machine = m.Label, status = c?.Status, sender = set.Sender, platform = m.Platform,
                canCollectLogs = HelpdeskRules.CanCollectLogs(m.Platform),
                messages = msgs.OrderBy(x => x.Id).Select(x => new
                {
                    x.Id, x.FromMachine, x.Author, x.Text, buttons = Json.Des<List<HelpdeskButton>>(x.ButtonsJson), x.Answer, x.AnswerAt,
                    x.ReplyToId, x.Created, x.DeliveredAt, x.ReadAt, x.Info, x.HasScreenshot,
                }),
                attachments = files,
                logsJob,
            });
        });

        adm.MapPost("/machines/{id:int}/helpdesk/read", async (int id, HelpdeskService hd) => { await hd.MarkRead(id); return Results.Ok(); });
        adm.MapPost("/machines/{id:int}/helpdesk/close", async (int id, HelpdeskService hd, ClaimsPrincipal u) =>
            await hd.SetStatus(id, true, u.UserName()) ? Results.Ok() : Results.NotFound());
        adm.MapPost("/machines/{id:int}/helpdesk/reopen", async (int id, HelpdeskService hd, ClaimsPrincipal u) =>
            await hd.SetStatus(id, false, u.UserName()) ? Results.Ok() : Results.NotFound());

        adm.MapPost("/helpdesk/send", async (HelpdeskSendRequest r, HelpdeskService hd, ClaimsPrincipal u) =>
        {
            try
            {
                var ids = await hd.Send(new HelpdeskTarget { MachineIds = r.MachineIds, Tag = r.Tag, ProfileId = r.ProfileId }, r.Text, r.Buttons, u.UserName());
                return Results.Ok(new { sent = ids.Count, machineIds = ids });
            }
            catch (ArgumentException e) { return Results.Problem(e.Message, statusCode: 400); }
        });

        // skærmbilledet vises kun for administratorer – som billede, aldrig som en side
        adm.MapGet("/helpdesk/screenshots/{messageId:int}", async (int messageId, AppDb db, HttpContext ctx) =>
        {
            var s = await db.HelpdeskScreenshots.AsNoTracking().FirstOrDefaultAsync(x => x.MessageId == messageId);
            if (s is null || HelpdeskText.ImageType(s.Data) is not { } type) return Results.NotFound();
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers.CacheControl = "private, max-age=3600";
            return Results.File(s.Data, type);
        });

        // "Hent logfiler": et job, der samler agentens log, seneste fejl og systeminfo – brugeren skal ikke gøre noget
        adm.MapPost("/machines/{id:int}/helpdesk/logs", async (int id, HelpdeskService hd, ClaimsPrincipal u) =>
        {
            var (job, err) = await hd.RequestLogs(id, u.UserName());
            return job is null ? Results.Problem(err, statusCode: 400) : Results.Ok(new { jobId = job.Id, job.Status });
        });

        // logfilerne hentes som fil (zip) – aldrig vist som en side
        adm.MapGet("/helpdesk/attachments/{attId:int}", async (int attId, AppDb db, HttpContext ctx) =>
        {
            var a = await db.HelpdeskAttachments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == attId);
            if (a is null || !LogBundle.IsZip(a.Data)) return Results.NotFound();
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers.CacheControl = "private, no-store";
            return Results.File(a.Data, LogBundle.ContentType, a.Name);
        });

        adm.MapGet("/helpdesk/settings", async (HelpdeskService hd) => Results.Ok(await hd.GetSettings()));
        adm.MapPut("/helpdesk/settings", async (HelpdeskSettings v, HelpdeskService hd, AuditService audit, Live live, ClaimsPrincipal u) =>
        {
            await hd.SaveSettings(v);
            await audit.Log(u.UserName(), "helpdesk.settings", $"afsender \"{HelpdeskRules.Sender(v.Sender)}\", {HelpdeskRules.Templates(v.Templates).Count} skabeloner, notifikation {(v.Notify ? "til" : "fra")}");
            await live.Changed("helpdesk");
            return Results.Ok(await hd.GetSettings());
        });

        // ---- agenten (token i Authorization: Bearer)
        app.MapPost("/api/agent/helpdesk/ack", async (HelpdeskAck a, HttpContext ctx, AppDb db, HelpdeskService hd) =>
        {
            if (await Agent(ctx, db) is not { } m) return Results.Unauthorized();
            await hd.Ack(m, a);
            return Results.Ok();
        });

        app.MapPost("/api/agent/helpdesk/reply", async (HelpdeskReply r, HttpContext ctx, AppDb db, HelpdeskService hd) =>
        {
            if (await Agent(ctx, db) is not { } m) return Results.Unauthorized();
            if (m.ReplacesId is not null) return Results.Problem("Maskinen venter på godkendelse.", statusCode: 409);
            var (msg, err) = await hd.FromMachine(m, r);
            return msg is null ? Results.Problem(err, statusCode: 400) : Results.Ok(new { id = msg.Id });
        });

        // logfiler til "Hent logfiler" (jobbet collect_logs)
        app.MapPost("/api/agent/logs", async (LogUpload up, HttpContext ctx, AppDb db, HelpdeskService hd) =>
        {
            if (await Agent(ctx, db) is not { } m) return Results.Unauthorized();
            var (file, err) = await hd.FromJob(m, up);
            return file is null ? Results.Problem(err, statusCode: 400) : Results.Ok(new { id = file.Id, size = file.Size });
        });
    }

    private static async Task<Machine?> Agent(HttpContext ctx, AppDb db)
    {
        var h = ctx.Request.Headers.Authorization.ToString();
        return h.StartsWith("Bearer ", StringComparison.Ordinal) ? await AgentAuth.Resolve(db, h[7..].Trim()) : null;
    }
}
