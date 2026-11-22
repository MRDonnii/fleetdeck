using System.Net.WebSockets;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Hubs;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;

namespace Fleetdeck.Server.Endpoints;

public sealed record TerminalCodeRequest(string? Code);
public sealed record TerminalPinRequest(string? Password, string? Pin);
public sealed record TerminalOpenRequest(int? MachineId, int? HostId, int? Cols, int? Rows);
public sealed record TerminalMachinePrefs(bool? Favorite, bool? Protected);
public sealed record TerminalCredentialRequest(string? Name, string? Kind, string? Password, string? PrivateKey, string? Passphrase);
public sealed record TerminalAcceptKey(string? Fingerprint);
public sealed record TerminalInstallKey(int CredentialId);
public sealed record TerminalSnippetRequest(string? Name, string? Command, string? Description, string? Platform, bool? Risky);
public sealed record TerminalSnippetRun(List<string>? SessionIds, Dictionary<string, string>? Values, bool? Confirm, Dictionary<string, string>? ConfirmNames);
public sealed record TerminalHostRequest(string? Name, string? Address, int? Port, string? Username, string? Group, bool? Favorite, bool? Protected, int? CredentialId);

/// <summary>Terminalen: kun administratorer, kun over https, hjemmenet eller VPN, og kun efter oplåsning med brugerens egen pinkode.</summary>
public static partial class TerminalEndpoints
{
    private static IResult Denied(string detail, int status, string code) =>
        Results.Problem(detail, statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code });

    /// <summary>Fælles krav til alt undtagen status og oplåsning. Null = adgang.</summary>
    internal static async Task<IResult?> Require(HttpContext ctx, ClaimsPrincipal p, AppDb db, TerminalGate gate)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        if (!TerminalSecurity.SecureChannel(ctx)) return Denied("Terminalen kan kun bruges over https, fra hjemmenettet eller via VPN.", 403, "https");
        if (await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == p.UserId()) is not { } u) return Results.Unauthorized();
        var token = ctx.Request.Headers[TerminalGate.Header].ToString();
        switch (gate.State(token, u))
        {
            case UnlockState.Active: gate.Touch(token); return null;
            case UnlockState.NeedsReauth: return Denied("Skriv din pinkode igen.", 423, "reauth");
            default: return Denied("Lås terminalen op med din pinkode.", 423, "locked");
        }
    }

    public static void MapTerminalEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/terminal").RequireAuthorization("admin");

        g.MapGet("/state", async (HttpContext ctx, ClaimsPrincipal p, AppDb db, TerminalGate gate, SettingsService s) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == p.UserId());
            if (u is null) return Results.Unauthorized();
            var state = gate.State(ctx.Request.Headers[TerminalGate.Header].ToString(), u);
            return Results.Ok(new { https = TerminalSecurity.SecureChannel(ctx), pinSet = (await s.Get(TerminalPin.Key(u.Id))).Length > 0, unlock = state switch { UnlockState.Active => "active", UnlockState.NeedsReauth => "reauth", _ => "locked" } });
        });

        // Vælg eller skift pinkode: kræver kontoens adgangskode, så en glemt pinkode kan erstattes
        g.MapPost("/pin", async (TerminalPinRequest r, HttpContext ctx, ClaimsPrincipal p, AppDb db, TwoFactorService tf, TerminalGate gate, AuditService audit, SettingsService s) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            if (!TerminalSecurity.SecureChannel(ctx)) return Denied("Terminalen kan kun bruges over https, fra hjemmenettet eller via VPN.", 403, "https");
            if (await db.Users.FirstOrDefaultAsync(x => x.Id == p.UserId()) is not { } u) return Results.Unauthorized();
            if (!TerminalPin.Valid(r.Pin)) return Results.Problem("Pinkoden skal være 4 til 8 cifre.", statusCode: 400);
            if (string.IsNullOrEmpty(r.Password)) return Results.Problem("Skriv din adgangskode.", statusCode: 400);
            if (await TwoFactorEndpoints.Prove(u, new ProofRequest(null, r.Password), db, tf, audit, ctx, "terminal.pin_failed") is { } denied) return denied;
            await db.SaveChangesAsync();
            await s.Set(TerminalPin.Key(u.Id), Crypto.HashPassword(r.Pin!));
            await audit.Log(u.Username, "terminal.pin_set", AuthEndpoints.ClientIp(ctx));
            return Results.Ok(new { token = gate.Issue(u) });
        }).RequireRateLimiting("login");

        // Oplåsning med brugerens pinkode
        g.MapPost("/unlock", async (TerminalCodeRequest r, HttpContext ctx, ClaimsPrincipal p, AppDb db, TwoFactorService tf, TerminalGate gate, AuditService audit, SettingsService s) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            if (!TerminalSecurity.SecureChannel(ctx)) return Denied("Terminalen kan kun bruges over https, fra hjemmenettet eller via VPN.", 403, "https");
            if (await db.Users.FirstOrDefaultAsync(x => x.Id == p.UserId()) is not { } u) return Results.Unauthorized();
            var hash = await s.Get(TerminalPin.Key(u.Id));
            if (hash.Length == 0) return Denied("Vælg en pinkode for at bruge terminalen.", 403, "needspin");
            if (Lockout.Remaining(u, tf.Now) is { } wait) return Results.Problem(Lockout.Message(wait), statusCode: 429);
            if (!TerminalPin.Valid(r.Code) || !Crypto.VerifyPassword(r.Code!, hash))
            {
                Lockout.Fail(u, tf.Now);
                await db.SaveChangesAsync();
                await audit.Log(u.Username, "terminal.unlock_failed", AuthEndpoints.ClientIp(ctx));
                return Results.Problem("Forkert pinkode.", statusCode: 400);
            }
            Lockout.Success(u);
            await db.SaveChangesAsync();
            await audit.Log(u.Username, "terminal.unlock", AuthEndpoints.ClientIp(ctx));
            return Results.Ok(new { token = gate.Issue(u) });
        }).RequireRateLimiting("login");

        // Efter 15 minutter uden brug: pinkode igen
        g.MapPost("/reauth", async (TerminalCodeRequest r, HttpContext ctx, ClaimsPrincipal p, AppDb db, TwoFactorService tf, TerminalGate gate, AuditService audit, SettingsService s) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            if (!TerminalSecurity.SecureChannel(ctx)) return Denied("Terminalen kan kun bruges over https, fra hjemmenettet eller via VPN.", 403, "https");
            if (await db.Users.FirstOrDefaultAsync(x => x.Id == p.UserId()) is not { } u) return Results.Unauthorized();
            var token = ctx.Request.Headers[TerminalGate.Header].ToString();
            if (gate.State(token, u) == UnlockState.None) return Denied("Lås terminalen op med din pinkode.", 423, "locked");
            if (Lockout.Remaining(u, tf.Now) is { } wait) return Results.Problem(Lockout.Message(wait), statusCode: 429);
            var hash = await s.Get(TerminalPin.Key(u.Id));
            if (hash.Length == 0 || !TerminalPin.Valid(r.Code) || !Crypto.VerifyPassword(r.Code!, hash))
            {
                Lockout.Fail(u, tf.Now);
                await db.SaveChangesAsync();
                await audit.Log(u.Username, "terminal.reauth_failed", AuthEndpoints.ClientIp(ctx));
                return Results.Problem("Forkert pinkode.", statusCode: 400);
            }
            Lockout.Success(u);
            await db.SaveChangesAsync();
            gate.Renew(token, u);
            await audit.Log(u.Username, "terminal.reauth", AuthEndpoints.ClientIp(ctx));
            return Results.Ok(new { ok = true });
        }).RequireRateLimiting("login");

        g.MapPost("/lock", (HttpContext ctx, TerminalGate gate) => { gate.Revoke(ctx.Request.Headers[TerminalGate.Header].ToString()); return Results.Ok(); });

        g.MapGet("/targets", async (HttpContext ctx, ClaimsPrincipal p, AppDb db, TerminalGate gate, SettingsService s) =>
        {
            if (await Require(ctx, p, db, gate) is { } no) return no;
            var mode = await s.Get("signed_jobs");
            var cutoff = DateTime.UtcNow.AddMinutes(-3);
            var machines = (await db.Machines.AsNoTracking().Include(m => m.Profile).ToListAsync()).OrderBy(m => m.Label, StringComparer.OrdinalIgnoreCase)
                .Select(m => new { m.Id, name = m.Label, m.Platform, os = m.Platform, online = m.LastSeen >= cutoff, favorite = m.TerminalFavorite, @protected = m.Protected, group = m.Profile?.Name, unavailable = TerminalEligibility.Why(m, mode) });
            var hosts = await db.TerminalHosts.AsNoTracking().OrderBy(h => h.Name).Select(h => new { h.Id, h.Name, h.Address, h.Port, h.Username, h.Group, h.Favorite, @protected = h.Protected, h.CredentialId, h.HostKeyFingerprint, h.PendingKeyFingerprint, h.LastConnected }).ToListAsync();
            return Results.Ok(new { machines, hosts });
        });

        g.MapPut("/machines/{id:int}", async (int id, TerminalMachinePrefs r, HttpContext ctx, ClaimsPrincipal p, AppDb db, TerminalGate gate, AuditService audit) =>
        {
            if (await Require(ctx, p, db, gate) is { } no) return no;
            if (await db.Machines.FindAsync(id) is not { } m) return Results.NotFound();
            if (r.Favorite is { } f) m.TerminalFavorite = f;
            if (r.Protected is { } b && b != m.Protected) { m.Protected = b; await audit.Log(p.UserName(), "terminal.protected", $"{m.Label}: {(b ? "beskyttet" : "ikke længere beskyttet")}", m.Id); }
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        g.MapPost("/hosts", async (TerminalHostRequest r, HttpContext ctx, ClaimsPrincipal p, AppDb db, TerminalGate gate, AuditService audit) =>
        {
            if (await Require(ctx, p, db, gate) is { } no) return no;
            var h = new TerminalHost();
            if (TerminalHostRules.Apply(h, r) is { } bad) return Results.Problem(bad, statusCode: 400);
            db.TerminalHosts.Add(h);
            await db.SaveChangesAsync();
            await audit.Log(p.UserName(), "terminal.host_added", $"{h.Name} ({h.Address}:{h.Port})");
            return Results.Ok(new { h.Id });
        });

        g.MapPut("/hosts/{id:int}", async (int id, TerminalHostRequest r, HttpContext ctx, ClaimsPrincipal p, AppDb db, TerminalGate gate, AuditService audit) =>
        {
            if (await Require(ctx, p, db, gate) is { } no) return no;
            if (await db.TerminalHosts.FindAsync(id) is not { } h) return Results.NotFound();
            var (addr, port) = (h.Address, h.Port);
            if (TerminalHostRules.Apply(h, r) is { } bad) return Results.Problem(bad, statusCode: 400);
            // Ny adresse = ny vært: en fastlåst nøgle gælder ikke længere
            if (addr != h.Address || port != h.Port) { h.HostKeyType = h.HostKeyFingerprint = h.PendingKeyType = h.PendingKeyFingerprint = null; }
            await db.SaveChangesAsync();
            await audit.Log(p.UserName(), "terminal.host_changed", $"{h.Name} ({h.Address}:{h.Port})");
            return Results.Ok();
        });

        g.MapDelete("/hosts/{id:int}", async (int id, HttpContext ctx, ClaimsPrincipal p, AppDb db, TerminalGate gate, AuditService audit) =>
        {
            if (await Require(ctx, p, db, gate) is { } no) return no;
            if (await db.TerminalHosts.FindAsync(id) is not { } h) return Results.NotFound();
            db.TerminalHosts.Remove(h);
            await db.SaveChangesAsync();
            await audit.Log(p.UserName(), "terminal.host_removed", h.Name);
            return Results.Ok();
        });

        // Godkend en ændret værtsnøgle: fingeraftrykket skal være det, administratoren har set
        g.MapPost("/hosts/{id:int}/accept-hostkey", async (int id, TerminalAcceptKey r, HttpContext ctx, ClaimsPrincipal p, AppDb db, TerminalGate gate, AuditService audit) =>
        {
            if (await Require(ctx, p, db, gate) is { } no) return no;
            if (await db.TerminalHosts.FindAsync(id) is not { } h) return Results.NotFound();
            if (h.PendingKeyFingerprint is null || h.PendingKeyFingerprint != r.Fingerprint) return Results.Problem("Nøglen er ændret igen. Se den nye nøgle, og prøv igen.", statusCode: 409);
            var old = h.HostKeyFingerprint;
            (h.HostKeyType, h.HostKeyFingerprint, h.PendingKeyType, h.PendingKeyFingerprint) = (h.PendingKeyType, h.PendingKeyFingerprint, null, null);
            await db.SaveChangesAsync();
            await audit.Log(p.UserName(), "terminal.hostkey_accepted", $"{h.Name}: {old} → {h.HostKeyFingerprint}");
            return Results.Ok();
        });

        // Nøgleboksen: hemmeligheder går kun ind. Svarene indeholder aldrig andet end navn, type og offentlig nøgle.
        g.MapGet("/credentials", async (HttpContext ctx, ClaimsPrincipal p, AppDb db, TerminalGate gate) =>
        {
            if (await Require(ctx, p, db, gate) is { } no) return no;
            var used = await db.TerminalHosts.AsNoTracking().Where(h => h.CredentialId != null).GroupBy(h => h.CredentialId).Select(x => new { x.Key, n = x.Count() }).ToDictionaryAsync(x => x.Key!.Value, x => x.n);
            var rows = await db.TerminalCredentials.AsNoTracking().OrderBy(c => c.Name).Select(c => new { c.Id, c.Name, c.Kind, c.PublicKey, c.Fingerprint, c.Created, c.LastUsed }).ToListAsync();
            return Results.Ok(rows.Select(c => new { c.Id, c.Name, c.Kind, c.PublicKey, c.Fingerprint, c.Created, c.LastUsed, hosts = used.GetValueOrDefault(c.Id) }));
        });

        g.MapPost("/credentials", async (TerminalCredentialRequest r, HttpContext ctx, ClaimsPrincipal p, AppDb db, TerminalGate gate, TerminalVault vault, AuditService audit) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            if (await Require(ctx, p, db, gate) is { } no) return no;
            var name = (r.Name ?? "").Trim();
            if (name.Length is 0 or > 64) return Results.Problem("Skriv et navn på højst 64 tegn.", statusCode: 400);
            var c = new TerminalCredential { Name = name, Kind = r.Kind == "password" ? "password" : "key" };
            VaultSecret secret;
            switch (r.Kind)
            {
                case "password":
                    if (string.IsNullOrEmpty(r.Password) || r.Password.Length > 1024) return Results.Problem("Skriv en adgangskode.", statusCode: 400);
                    secret = new VaultSecret { Password = r.Password };
                    break;
                case "generate":
                    var made = SshKeys.Generate(name);
                    (c.PublicKey, c.Fingerprint) = (made.PublicKey, made.Fingerprint);
                    secret = new VaultSecret { PrivateKey = made.PrivateKey };
                    break;
                case "import":
                    if (SshKeys.Inspect(r.PrivateKey, r.Passphrase, name) is not { } seen) return Results.Problem("Nøglen kunne ikke læses. Er løsenet rigtigt?", statusCode: 400);
                    (c.PublicKey, c.Fingerprint) = (seen.PublicKey, seen.Fingerprint);
                    secret = new VaultSecret { PrivateKey = r.PrivateKey, Passphrase = string.IsNullOrEmpty(r.Passphrase) ? null : r.Passphrase };
                    break;
                default: return Results.Problem("Ukendt type.", statusCode: 400);
            }
            c.Cipher = vault.Seal(secret);
            db.TerminalCredentials.Add(c);
            await db.SaveChangesAsync();
            await audit.Log(p.UserName(), "terminal.credential_added", $"{c.Name} ({c.Kind})");
            return Results.Ok(new { c.Id, c.Name, c.Kind, c.PublicKey, c.Fingerprint });
        });

        g.MapDelete("/credentials/{id:int}", async (int id, HttpContext ctx, ClaimsPrincipal p, AppDb db, TerminalGate gate, AuditService audit) =>
        {
            if (await Require(ctx, p, db, gate) is { } no) return no;
            if (await db.TerminalCredentials.FindAsync(id) is not { } c) return Results.NotFound();
            await db.TerminalHosts.Where(h => h.CredentialId == id).ExecuteUpdateAsync(x => x.SetProperty(h => h.CredentialId, (int?)null));
            db.TerminalCredentials.Remove(c);
            await db.SaveChangesAsync();
            await audit.Log(p.UserName(), "terminal.credential_removed", c.Name);
            return Results.Ok();
        });

        // Læg en offentlig nøgle på værten med det login, værten bruger nu, og skift derefter til nøglen
        g.MapPost("/hosts/{id:int}/install-key", async (int id, TerminalInstallKey r, HttpContext ctx, ClaimsPrincipal p, AppDb db, TerminalGate gate, TerminalVault vault, AuditService audit, IServiceScopeFactory scopes) =>
        {
            if (await Require(ctx, p, db, gate) is { } no) return no;
            if (await db.TerminalHosts.FindAsync(id) is not { } h) return Results.NotFound();
            if (await db.TerminalCredentials.FindAsync(r.CredentialId) is not { PublicKey: { Length: > 0 } pub } key) return Results.Problem("Vælg en SSH-nøgle.", statusCode: 400);
            try { await SshTerminal.InstallKey(scopes, vault, h.Id, pub, ctx.RequestAborted); }
            catch (TerminalConnectException e) { await audit.Log(p.UserName(), "terminal.key_install_failed", $"{h.Name}: {e.Audit}"); return Results.Problem(e.Message, statusCode: 409); }
            await db.Entry(h).ReloadAsync();
            h.CredentialId = key.Id;
            await db.SaveChangesAsync();
            await audit.Log(p.UserName(), "terminal.key_installed", $"{key.Name} på {h.Name}");
            return Results.Ok();
        });

        g.MapGet("/snippets", async (HttpContext ctx, ClaimsPrincipal p, AppDb db, TerminalGate gate) =>
        {
            if (await Require(ctx, p, db, gate) is { } no) return no;
            var rows = await db.TerminalSnippets.AsNoTracking().OrderBy(x => x.Name).ToListAsync();
            return Results.Ok(rows.Select(x => new { x.Id, x.Name, x.Command, x.Description, x.Platform, x.Risky, risk = x.Risky || SnippetRisk.Detect(x.Command), variables = SnippetTemplate.Variables(x.Command) }));
        });

        g.MapPost("/snippets", async (TerminalSnippetRequest r, HttpContext ctx, ClaimsPrincipal p, AppDb db, TerminalGate gate, AuditService audit) =>
        {
            if (await Require(ctx, p, db, gate) is { } no) return no;
            var x = new TerminalSnippet();
            if (SnippetTemplate.Apply(x, r) is { } bad) return Results.Problem(bad, statusCode: 400);
            db.TerminalSnippets.Add(x);
            await db.SaveChangesAsync();
            await audit.Log(p.UserName(), "terminal.snippet_added", x.Name);
            return Results.Ok(new { x.Id });
        });

        g.MapPut("/snippets/{id:int}", async (int id, TerminalSnippetRequest r, HttpContext ctx, ClaimsPrincipal p, AppDb db, TerminalGate gate, AuditService audit) =>
        {
            if (await Require(ctx, p, db, gate) is { } no) return no;
            if (await db.TerminalSnippets.FindAsync(id) is not { } x) return Results.NotFound();
            if (SnippetTemplate.Apply(x, r) is { } bad) return Results.Problem(bad, statusCode: 400);
            await db.SaveChangesAsync();
            await audit.Log(p.UserName(), "terminal.snippet_changed", x.Name);
            return Results.Ok();
        });

        g.MapDelete("/snippets/{id:int}", async (int id, HttpContext ctx, ClaimsPrincipal p, AppDb db, TerminalGate gate, AuditService audit) =>
        {
            if (await Require(ctx, p, db, gate) is { } no) return no;
            if (await db.TerminalSnippets.FindAsync(id) is not { } x) return Results.NotFound();
            db.TerminalSnippets.Remove(x);
            await db.SaveChangesAsync();
            await audit.Log(p.UserName(), "terminal.snippet_removed", x.Name);
            return Results.Ok();
        });

        // Kør en gemt kommando i åbne sessioner. Bekræftelsen håndhæves her, ikke kun i browseren, og alt tjekkes, før noget køres.
        g.MapPost("/snippets/{id:int}/run", async (int id, TerminalSnippetRun r, HttpContext ctx, ClaimsPrincipal p, AppDb db, TerminalGate gate, TerminalBroker broker, AuditService audit) =>
        {
            if (await Require(ctx, p, db, gate) is { } no) return no;
            if (await db.TerminalSnippets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id) is not { } sn) return Results.NotFound();
            var (text, problem) = SnippetTemplate.Render(sn.Command, r.Values);
            if (problem is not null) return Results.Problem(problem, statusCode: 400);
            var targets = (r.SessionIds ?? []).Distinct().Select(broker.Find).OfType<TerminalSession>().Where(s => s.UserId == p.UserId() && s.Backend is not null).ToList();
            if (targets.Count == 0 || targets.Count != (r.SessionIds ?? []).Distinct().Count()) return Results.Problem("En af sessionerne er ikke længere åben.", statusCode: 409);
            if (targets.FirstOrDefault(s => !SnippetGuard.PlatformFits(sn.Platform, s.Platform)) is { } wrong) return Results.Problem($"Kommandoen passer ikke til {wrong.Label}.", statusCode: 409);
            var risky = sn.Risky || SnippetRisk.Detect(text);
            var machineIds = targets.Where(s => s.MachineId != null).Select(s => s.MachineId!.Value).ToList();
            var hostIds = targets.Where(s => s.HostId != null).Select(s => s.HostId!.Value).ToList();
            var protM = await db.Machines.AsNoTracking().Where(m => machineIds.Contains(m.Id) && m.Protected).Select(m => m.Id).ToListAsync();
            var protH = await db.TerminalHosts.AsNoTracking().Where(h => hostIds.Contains(h.Id) && h.Protected).Select(h => h.Id).ToListAsync();
            bool Prot(TerminalSession s) => s.MachineId is { } m ? protM.Contains(m) : s.HostId is { } h && protH.Contains(h);
            var needs = targets.Where(s => !SnippetGuard.Allowed(risky, Prot(s), s.Label, r.Confirm == true, r.ConfirmNames?.GetValueOrDefault(s.Id))).ToList();
            if (needs.Count > 0)
                return Results.Json(new { code = "confirm", risky, needs = needs.Select(s => new { sessionId = s.Id, label = s.Label, @protected = Prot(s) }) }, statusCode: 409);
            var bytes = System.Text.Encoding.UTF8.GetBytes(text + "\r");
            foreach (var s in targets)
            {
                s.LastInput = broker.Now;
                await s.Input(bytes, ctx.RequestAborted);
                // Kun navnene på variablerne: værdierne kan være følsomme
                await audit.Log(p.UserName(), "terminal.snippet_run", $"{sn.Name} på {s.Label}{(risky ? " (risikabel)" : "")}{(Prot(s) ? " · beskyttet maskine" : "")}", s.MachineId);
            }
            return Results.Ok(new { ran = targets.Count });
        }).RequireRateLimiting(TerminalSecurity.RateLimitPolicy);

        g.MapPost("/sessions", async (TerminalOpenRequest r, HttpContext ctx, ClaimsPrincipal p, AppDb db, TerminalGate gate, TerminalBroker broker, SettingsService s, Live live) =>
        {
            if (await Require(ctx, p, db, gate) is { } no) return no;
            var u = await db.Users.AsNoTracking().FirstAsync(x => x.Id == p.UserId());
            if (broker.Full(u.Id)) return Results.Problem("Der er for mange åbne terminaler. Luk en af dem først.", statusCode: 429);
            var cols = Math.Clamp(r.Cols ?? 120, 10, 500); var rows = Math.Clamp(r.Rows ?? 32, 4, 200);
            var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            var ticket = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var unlock = ctx.Request.Headers[TerminalGate.Header].ToString();
            if (r.HostId is { } hid)
            {
                if (await db.TerminalHosts.AsNoTracking().FirstOrDefaultAsync(h => h.Id == hid) is not { } host) return Results.NotFound();
                if (host.CredentialId is null) return Results.Problem("Vælg et login fra nøgleboksen til denne vært.", statusCode: 409);
                if (host.PendingKeyFingerprint is not null) return Denied("Værtsnøglen er ændret. En administrator skal godkende den nye nøgle, før der kan forbindes.", 409, "hostkey");
                broker.Add(new TerminalSession
                {
                    Id = id, UserId = u.Id, UserName = u.Username, Stamp = u.SecurityStamp, Label = host.Name, Kind = "ssh", HostId = host.Id, Protected = host.Protected,
                    UnlockToken = unlock, TicketHash = TerminalBroker.Hash(ticket), Cols = cols, Rows = rows, Created = broker.Now, LastInput = broker.Now,
                });
                return Results.Ok(new { id, ticket, label = host.Name, @protected = host.Protected });
            }
            if (r.MachineId is not { } mid) return Results.Problem("Vælg en maskine.", statusCode: 400);
            if (await db.Machines.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mid) is not { } machine) return Results.NotFound();
            if (TerminalEligibility.Why(machine, await s.Get("signed_jobs")) is { } why) return Results.Problem(why, statusCode: 409);
            var secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            broker.Add(new TerminalSession
            {
                Id = id, UserId = u.Id, UserName = u.Username, Stamp = u.SecurityStamp, Label = machine.Label, Kind = "agent", MachineId = machine.Id, Protected = machine.Protected, Platform = machine.Platform,
                UnlockToken = unlock, TicketHash = TerminalBroker.Hash(ticket), AgentSecretHash = TerminalBroker.Hash(secret), Cols = cols, Rows = rows,
                Ticket = new TerminalTicket { SessionId = id, Secret = secret, Cols = cols, Rows = rows }, Created = broker.Now, LastInput = broker.Now,
            });
            await live.Wake(machine.Id);      // agenten henter billetten i sit næste signerede check-in, som startes nu
            return Results.Ok(new { id, ticket, label = machine.Label, @protected = machine.Protected, platform = machine.Platform });
        }).RequireRateLimiting(TerminalSecurity.RateLimitPolicy);

        g.MapDelete("/sessions/{id}", async (string id, HttpContext ctx, ClaimsPrincipal p, AppDb db, TerminalGate gate, TerminalBroker broker) =>
        {
            if (await Require(ctx, p, db, gate) is { } no) return no;
            if (broker.Find(id) is { } s && s.UserId == p.UserId()) broker.Close(s, "Lukket.");
            return Results.Ok();
        });

        // Browserens forbindelse. Engangsbilletten kommer fra et CSRF-beskyttet kald, og Origin skal være Fleetdecks egen.
        g.Map("/ws/{id}", async (string id, string? ticket, HttpContext ctx, ClaimsPrincipal p, TerminalBroker broker, IServiceScopeFactory scopes, TerminalVault vault) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) return Results.BadRequest();
            if (!TerminalSecurity.SecureChannel(ctx) || !TerminalSecurity.OriginAllowed(ctx.Request.Headers.Origin.ToString(), ctx.Request.Host.Value ?? "")) return Results.StatusCode(403);
            if (broker.Find(id) is not { } s || string.IsNullOrEmpty(ticket) || !broker.ClaimBrowser(s, ticket, p.UserId())) return Results.StatusCode(403);
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            await broker.Run(s, ws, async ct =>
            {
                if (s.Kind == "ssh") return await SshTerminal.Open(scopes, vault, s, ct);
                var b = new AgentTerminalBackend(s);
                await b.Attach(ct);
                return b;
            });
            return Results.Empty;
        });

        // Agentens forbindelse: maskinens eget token og sessionens engangshemmelighed
        app.Map("/api/agent/terminal/{id}", async (string id, HttpContext ctx, AppDb db, TerminalBroker broker) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) return Results.BadRequest();
            var h = ctx.Request.Headers.Authorization.ToString();
            if (!h.StartsWith("Bearer ", StringComparison.Ordinal) || await AgentAuth.Resolve(db, h[7..].Trim()) is not { } m) return Results.Unauthorized();
            // Samme krav som ved udleveringen: https, hjemmenet eller VPN
            if (!TerminalSecurity.SecureChannel(ctx) && ctx.Connection.LocalIpAddress is not null) return Results.StatusCode(403);
            if (broker.Find(id) is not { } s || !broker.ClaimAgent(s, m.Id, ctx.Request.Headers[TerminalPolicy.SecretHeader].ToString())) return Results.StatusCode(403);
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            s.AgentSocket.TrySetResult(ws);
            await s.AgentDone.Task;
            return Results.Empty;
        });
    }
}

public static partial class TerminalHostRules
{
    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9.\-]*[A-Za-z0-9])?$|^[0-9A-Fa-f:]+$")] private static partial System.Text.RegularExpressions.Regex AddressRx();
    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.\-]*$")] private static partial System.Text.RegularExpressions.Regex UserRx();

    /// <summary>Må serveren forbinde hertil? Serverens egne interne adresser er aldrig et mål.</summary>
    public static string? AddressProblem(string? address)
    {
        var a = (address ?? "").Trim();
        if (a.Length is 0 or > 253 || !AddressRx().IsMatch(a)) return "Skriv en gyldig adresse.";
        if (a.Equals("localhost", StringComparison.OrdinalIgnoreCase) || a.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return "Serveren må ikke forbinde til sig selv.";
        if (System.Net.IPAddress.TryParse(a, out var ip))
        {
            if (System.Net.IPAddress.IsLoopback(ip) || ip.Equals(System.Net.IPAddress.Any) || ip.Equals(System.Net.IPAddress.IPv6Any)) return "Serveren må ikke forbinde til sig selv.";
            var b = ip.GetAddressBytes();
            if (ip.IsIPv6LinkLocal || (b.Length == 4 && b[0] == 169 && b[1] == 254)) return "Link-local-adresser kan ikke bruges.";
        }
        return null;
    }

    public static string? Apply(TerminalHost h, TerminalHostRequest r)
    {
        var name = (r.Name ?? "").Trim();
        if (name.Length is 0 or > 64) return "Skriv et navn på højst 64 tegn.";
        if (AddressProblem(r.Address) is { } bad) return bad;
        var port = r.Port ?? 22;
        if (port is < 1 or > 65535) return "Porten skal være mellem 1 og 65535.";
        var user = (r.Username ?? "").Trim();
        if (user.Length is 0 or > 64 || !UserRx().IsMatch(user)) return "Skriv et gyldigt brugernavn.";
        h.Name = name; h.Address = r.Address!.Trim(); h.Port = port; h.Username = user;
        h.Group = string.IsNullOrWhiteSpace(r.Group) ? null : r.Group.Trim()[..Math.Min(r.Group.Trim().Length, 60)];
        h.Favorite = r.Favorite ?? h.Favorite; h.Protected = r.Protected ?? h.Protected; h.CredentialId = r.CredentialId;
        return null;
    }
}

public static partial class SnippetTemplate
{
    // {{.Names}} (fx docker --format) er ikke en variabel
    [System.Text.RegularExpressions.GeneratedRegex(@"\{\{\s*([\p{L}_][\p{L}\p{N}_-]*)\s*\}\}")] private static partial System.Text.RegularExpressions.Regex VarRx();

    public static List<string> Variables(string command) => VarRx().Matches(command).Select(m => m.Groups[1].Value).Distinct().ToList();

    /// <summary>Indsætter værdierne. En værdi må ikke kunne starte en ny kommandolinje.</summary>
    public static (string Text, string? Problem) Render(string command, IReadOnlyDictionary<string, string>? values)
    {
        foreach (var v in Variables(command))
        {
            if (values is null || !values.TryGetValue(v, out var val) || string.IsNullOrWhiteSpace(val)) return ("", $"Udfyld {v}.");
            if (val.Length > 500 || val.Any(char.IsControl)) return ("", $"{v} indeholder ugyldige tegn.");
        }
        return (VarRx().Replace(command, m => values![m.Groups[1].Value]), null);
    }

    public static string? Apply(TerminalSnippet x, TerminalSnippetRequest r)
    {
        var name = (r.Name ?? "").Trim(); var cmd = (r.Command ?? "").Trim();
        if (name.Length is 0 or > 80) return "Skriv et navn på højst 80 tegn.";
        if (cmd.Length is 0 or > 4000) return "Skriv en kommando på højst 4000 tegn.";
        if (r.Platform is not (null or "" or "windows" or "linux" or "mac")) return "Ukendt platform.";
        x.Name = name; x.Command = cmd; x.Description = (r.Description ?? "").Trim(); x.Platform = r.Platform ?? ""; x.Risky = r.Risky == true;
        if (x.Description.Length > 300) x.Description = x.Description[..300];
        return null;
    }
}

public static partial class SnippetRisk
{
    [System.Text.RegularExpressions.GeneratedRegex(@"(^|[\s;&|(])(reboot|poweroff|halt|shutdown|init\s+[06]|telinit\s+[06]|mkfs(\.\w+)?|fdisk|parted|wipefs|Restart-Computer|Stop-Computer|Format-Volume|Clear-Disk|Remove-Partition)(\s|$|[;&|)])|systemctl\s+(\S+\s+)*(reboot|poweroff|halt|kexec|stop|restart|disable|mask)\b|\brm\s+(-\S*\s+)*-\S*[rf]\S*\s|\bdd\s+.*\bof=|>\s*/dev/(sd|nvme|mmcblk|hd)|\bdocker\s+(rm|rmi|system\s+prune|stop|restart|kill)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)] private static partial System.Text.RegularExpressions.Regex Rx();

    /// <summary>Kendte kommandoer, der kan afbryde eller slette noget. Et sikkerhedsnet: brugeren kan også selv markere en kommando.</summary>
    public static bool Detect(string command) => Rx().IsMatch(command);
}

public static class SnippetGuard
{
    public static bool PlatformFits(string snippetPlatform, string? sessionPlatform) =>
        snippetPlatform.Length == 0 || sessionPlatform is null || string.Equals(snippetPlatform, sessionPlatform, StringComparison.OrdinalIgnoreCase);

    /// <summary>Må kommandoen køre her? Risikable kræver bekræftelse; på en beskyttet maskine skal maskinens navn være skrevet.</summary>
    public static bool Allowed(bool risky, bool isProtected, string label, bool confirmed, string? typedName)
    {
        if (!risky) return true;
        if (!confirmed) return false;
        return !isProtected || string.Equals(typedName?.Trim(), label, StringComparison.OrdinalIgnoreCase);
    }
}
