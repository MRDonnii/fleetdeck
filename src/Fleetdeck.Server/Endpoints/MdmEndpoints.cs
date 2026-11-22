using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Hubs;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Server.Endpoints;

public sealed record MdmSettingsInput(string? PublicHost, bool? UnlockHost, string? Org, string? AppleId, int? MachineId, string? Network, string? Ip, string? Container,
    string? Image, string? DataDir, string? ApiUrl, string? WebhookBase);
public sealed record MdmTextInput(string? Text, string? AppleId = null);
public sealed record MdmEmailInput(string? Email);
public sealed record MdmLinkInput(string? ExpectedSerial);
public sealed record MdmLockInput(string? Message, string? Phone);
public sealed record MdmLockedInput(bool ViaMdm, bool? Force);
public sealed record MdmOsUpdateInput(string? Version, string? Build, string? Deadline);
public sealed record MdmEraseInput(string? ConfirmName, string? Password);

/// <summary>Apple-styring (MDM via NanoHUB): opsætning, push-certifikat, tilmelding, webhooks og kommandoer pr. iPad.</summary>
public static class MdmEndpoints
{
    private static IResult Bad(string msg, int status = 400) => Results.Problem(msg, statusCode: status);
    private const string MdmcertUrl = "https://mdmcert.download/api/v1/signrequest";
    /// <summary>Den offentlige API-nøgle, mdmcert.download udleverer til alle klienter (samme som i MicroMDMs mdmctl).</summary>
    private const string MdmcertKey = "f847aea2ba06b41264d587b229e2712c89b1490a1208b7ff1aafab5bb40d47bc";

    public static void MapMdmEndpoints(this WebApplication app)
    {
        var adm = app.MapGroup("/api/mdm").RequireAuthorization("admin");
        // Apple-styring pr. iPad er administratorens (forældre ser kun det, der styrer barnet – se FamilyScope)
        var fam = app.MapGroup("/api/ipads/{id:int}/mdm").RequireAuthorization("admin");
        var famAdm = app.MapGroup("/api/ipads/{id:int}/mdm").RequireAuthorization("admin");

        // ------------------------------------------------------------------ opsætning (Indstillinger → Apple-styring)
        adm.MapGet("", async (MdmSettings ms, SettingsService s, AppDb db, MdmEngine engine) =>
        {
            var c = await ms.Sidecar();
            var poll = await s.GetNum("poll_seconds", 30);
            var machines = await db.Machines.AsNoTracking().Where(m => m.Platform == "linux").OrderBy(m => m.Name).ToListAsync();
            var push = await ms.Push();
            var caCert = await ms.Get("mdm_ca_cert");
            var lan = await ms.FleetdeckLanUrl();
            var host = await ms.PublicHost();
            var counts = await db.MdmDevices.AsNoTracking().GroupBy(d => d.State).Select(g => new { g.Key, N = g.Count() }).ToListAsync();
            return Results.Ok(new
            {
                publicHost = host, hostLocked = await ms.HostLocked(), org = await ms.Org(), appleId = push.AppleId,
                machineId = c.MachineId, network = c.Network, ip = c.Ip, container = c.Container, image = c.Image, defaultImage = MdmSidecar.Image, dataDir = c.DataDir,
                apiUrl = await ms.Get("mdm_api_url"), webhookBase = await ms.Get("mdm_webhook_base"), effectiveApiUrl = await ms.ApiUrl(), fleetdeckUrl = lan,
                machines = machines.Select(m => new { m.Id, m.Name, online = MachineViews.Online(m, poll), unraid = Json.Des<HealthReport>(m.HealthJson).Unraid is not null }),
                push = new
                {
                    push.Topic, push.Expires, push.HasCert, push.HasPendingCsr, push.HasEncryption, push.HasSignedRequest, push.Subject,
                    daysLeft = push.Expires is { } e ? (int?)Math.Floor((e - DateTime.UtcNow).TotalDays) : null,
                    warn = push.Expires is { } e2 && MdmEngine.ExpiryThreshold(e2, DateTime.UtcNow) is not null,
                },
                ca = caCert.Length > 0 ? new { thumbprint = MdmPki.Thumbprint(caCert), started = await ms.Get("mdm_ca_started") == MdmPki.Thumbprint(caCert) } : null,
                server = new { ok = engine.ServerOk, version = engine.ServerVersion, error = engine.ServerError, checkedAt = engine.ServerChecked, lastError = engine.LastError },
                lastHook = await ms.Get("mdm_last_hook") is { Length: > 0 } lh ? lh : null,
                hmac = await ms.Get("mdm_hmac_seen") == "1",
                devices = counts.ToDictionary(x => x.Key, x => x.N),
                // det, der skal sættes op i Cloudflare (samme tunnel som krypteret DNS)
                cloudflare = new[]
                {
                    new { hostname = host.Length > 0 ? host : "mdm.ditdomæne.dk", path = "^/mdm$", service = c.Ip.Length > 0 ? $"http://{c.Ip}:{MdmSidecar.Port}" : $"http://<MDM-serverens IP>:{MdmSidecar.Port}", what = "MDM-protokollen (NanoHUB)" },
                    new { hostname = host.Length > 0 ? host : "mdm.ditdomæne.dk", path = "^/enroll/", service = lan is not null ? Origin(lan) : "http://<Fleetdecks IP>:8585", what = "Tilmeldingslinks (Fleetdeck)" },
                },
            });
        });

        adm.MapPut("", async (MdmSettingsInput i, MdmSettings ms, AppDb db, AuditService audit, ClaimsPrincipal u) =>
        {
            if (i.PublicHost is { } h)
            {
                var host = h.Trim().ToLowerInvariant().Replace("https://", "").Replace("http://", "").Split('/')[0];
                if (host.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(host, MdmSettings.HostRx)) return Bad("Skriv adressen som fx mdm.ditdomæne.dk");
                var cur = await ms.PublicHost();
                if (host != cur)
                {
                    if (await ms.HostLocked() && i.UnlockHost != true)
                        return Bad("Adressen er låst, fordi iPads er tilmeldt med den. Ændres den, skal alle iPads tilmeldes igen.", 409);
                    await ms.Set("mdm_public_host", host);
                    await ms.Set("mdm_host_locked", "");
                    MdmPublicHost.Set(host);
                }
            }
            if (i.Org is { } org) await ms.Set("mdm_org", org.Trim()[..Math.Min(60, org.Trim().Length)]);
            if (i.AppleId is { } aid) await ms.Set("push_apple_id", aid.Trim()[..Math.Min(120, aid.Trim().Length)]);
            if (i.ApiUrl is { } au)
            {
                var a = au.Trim().TrimEnd('/');
                if (a.Length > 0 && !(Uri.TryCreate(a, UriKind.Absolute, out var x) && x.Scheme is "http" or "https")) return Bad("Den interne adresse skal være fx http://192.168.1.60:9004");
                await ms.Set("mdm_api_url", a);
            }
            if (i.WebhookBase is { } wb)
            {
                var w = wb.Trim().TrimEnd('/');
                if (w.Length > 0 && !(Uri.TryCreate(w, UriKind.Absolute, out var x) && x.Scheme is "http" or "https")) return Bad("Fleetdecks adresse skal være fx http://192.168.1.50:8585");
                await ms.Set("mdm_webhook_base", w);
            }
            var c = await ms.Sidecar();
            if (i.MachineId is { } mid)
            {
                if (mid <= 0) c = c with { MachineId = null };
                else if (await db.Machines.AsNoTracking().AnyAsync(m => m.Id == mid && m.Platform == "linux")) c = c with { MachineId = mid };
                else return Bad("Maskinen findes ikke eller er ikke en Linux/Unraid-maskine");
            }
            c = c with
            {
                Network = i.Network?.Trim() ?? c.Network, Ip = i.Ip?.Trim() ?? c.Ip, Container = i.Container?.Trim() ?? c.Container,
                Image = i.Image?.Trim() is { Length: > 0 } img ? img : i.Image is not null ? MdmSidecar.Image : c.Image, DataDir = i.DataDir?.Trim().TrimEnd('/') ?? c.DataDir,
            };
            if (MdmSidecar.Validate(c) is { } err) return Bad(err);
            await ms.SaveSidecar(c);
            await audit.Log(u.UserName(), "settings.apple_mdm", $"adresse {await ms.PublicHost()}, maskine {c.MachineId?.ToString() ?? "-"}, {c.Container} på {c.Network}{(c.Ip.Length > 0 ? $" ({c.Ip})" : "")}");
            return Results.Ok();
        });

        // ---- MDM-serveren (container) – som tunnelen
        adm.MapPost("/start", async (MdmSettings ms, AppDb db, JobService jobs, AuditService audit, ClaimsPrincipal u) =>
        {
            var c = await ms.Sidecar();
            if (await Host(db, c) is not { } m) return Bad("Vælg den Linux/Unraid-maskine, MDM-serveren skal køre på, og gem");
            if (MdmSidecar.Validate(c) is { } err) return Bad(err);
            if (await ms.FleetdeckLanUrl() is null) return Bad("Fleetdecks egen adresse på hjemmenettet mangler (Indstillinger → Generelt → serveradresse, eller feltet herunder)");
            if (await db.Jobs.AnyAsync(j => j.MachineId == m.Id && j.Source == MdmSidecar.Source && j.Status == JobStatus.Running && j.Title != MdmSidecar.SuggestTitle))
                return Bad($"{m.Name} er i gang med MDM-serveren – vent til jobbet er færdigt");
            await ms.EnsureSecrets();
            var (caCert, _) = await ms.EnsureCa();
            await CancelPending(db, m.Id);
            int jobId;
            var nonce = Crypto.Token();
            await using (var tx = await db.Database.BeginTransactionAsync())
            {
                var job = await jobs.Create(m.Id, JobTypes.Script, MdmSidecar.StartTitle, MdmSidecar.StartJob(c, nonce, caCert), RunAs.System, null, MdmSidecar.Source, u.UserName(), save: false);
                await db.SaveChangesAsync();
                await ms.SetNonce(job.Id, nonce);
                await tx.CommitAsync();
                jobId = job.Id;
            }
            await jobs.Commit(m.Id);
            await ms.Set("mdm_ca_started", MdmPki.Thumbprint(caCert));
            await ms.Set("push_synced", "");   // push-certifikatet lægges på igen, når serveren svarer
            await audit.Log(u.UserName(), "apple_mdm.start", $"{m.Name}: {c.Container} ({c.Image}) på {c.Network}{(c.Ip.Length > 0 ? $" ({c.Ip})" : "")}");
            return Results.Ok(new { jobId });
        });

        adm.MapPost("/stop", async (MdmSettings ms, AppDb db, JobService jobs, AuditService audit, ClaimsPrincipal u) =>
        {
            var c = await ms.Sidecar();
            if (await Host(db, c) is not { } m) return Bad("Vælg den maskine, MDM-serveren kører på");
            if (PublicAccess.ValidateContainer(c.Container) is { } err) return Bad(err);
            await CancelPending(db, m.Id);
            await ms.ClearNonce();
            var j = await jobs.Create(m.Id, JobTypes.Script, MdmSidecar.StopTitle, MdmSidecar.StopJob(c), RunAs.System, null, MdmSidecar.Source, u.UserName());
            await audit.Log(u.UserName(), "apple_mdm.stop", $"{m.Name}: {c.Container}");
            return Results.Ok(new { jobId = j.Id });
        });

        adm.MapPost("/suggest-ip", async (MdmSettings ms, AppDb db, JobService jobs, ClaimsPrincipal u) =>
        {
            var c = await ms.Sidecar();
            if (await Host(db, c) is not { } m) return Bad("Vælg maskinen og gem først");
            if (PublicAccess.ValidateNetwork(c.Network) is { } err) return Bad(err);
            if (c.Network is "host" or "bridge") return Bad($"På \"{c.Network}\" bruges ingen fast IP – lad feltet være tomt");
            var tc = new TunnelConfig(m.Id, c.Network, "", c.Container);
            var j = await jobs.Create(m.Id, JobTypes.Script, MdmSidecar.SuggestTitle, PublicAccess.SuggestJob(tc), RunAs.System, null, MdmSidecar.Source, u.UserName());
            return Results.Ok(new { jobId = j.Id });
        });

        adm.MapGet("/status", async (MdmSettings ms, AppDb db, CancellationToken ct) =>
        {
            var c = await ms.Sidecar();
            var key = await ms.Secret("mdm_api_key");
            var host = c.MachineId is int mid ? await db.Machines.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mid, ct) : null;
            object? job = null, suggest = null, container = null;
            if (host is not null)
            {
                var recent = await db.Jobs.AsNoTracking().Where(j => j.MachineId == host.Id && j.Source == MdmSidecar.Source).OrderByDescending(j => j.Id).Take(20).ToListAsync(ct);
                if (recent.FirstOrDefault(j => j.Title != MdmSidecar.SuggestTitle) is { } lj)
                    job = new { lj.Id, lj.Title, lj.Status, lj.Created, lj.Finished, output = MdmSidecar.Mask(lj.Output.Length > 3000 ? lj.Output[^3000..] : lj.Output, key) };
                if (recent.FirstOrDefault(j => j.Title == MdmSidecar.SuggestTitle) is { } sj)
                    suggest = new { sj.Id, sj.Status, ip = PublicAccess.SuggestedIp(sj.Output) };
                if (Json.Des<HealthReport>(host.HealthJson).Unraid is { } ur)
                {
                    var ctr = ur.Containers.FirstOrDefault(x => x.Name.Equals(c.Container, StringComparison.OrdinalIgnoreCase));
                    container = new { reported = true, found = ctr is not null, running = ctr?.State == "running", state = ctr?.State, ip = ctr?.Ip, at = host.LastHealth };
                }
            }
            return Results.Ok(new { machine = host is null ? null : new { host.Id, host.Name }, job, suggest, container });
        });

        // Selvtjek: MDM-serveren internt og den offentlige adresse udefra (kun de rigtige stier må svare)
        adm.MapPost("/test", async (MdmSettings ms, CancellationToken ct) =>
        {
            var steps = new List<object>();
            var ok = true;
            void Step(bool good, string text) { steps.Add(new { ok = good, text }); ok &= good; }
            if (await ms.Client() is { } client)
            {
                try { Step(true, $"MDM-serveren svarer på hjemmenettet (NanoHUB {await client.Version(ct)})"); }
                catch (Exception e) when (e is MdmException or HttpRequestException or TaskCanceledException) { Step(false, $"MDM-serveren kan ikke nås på {await ms.ApiUrl()}: {e.Message}"); }
            }
            else Step(false, "MDM-serveren er ikke sat op (fast IP eller intern adresse mangler, eller den er ikke startet fra Fleetdeck)");
            var host = await ms.PublicHost();
            if (host.Length == 0) { Step(false, "Den offentlige MDM-adresse mangler"); return Results.Ok(new { ok, steps }); }
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MDMClient/1.0");
            async Task<(int Code, bool Challenge, bool Fleetdeck, string? Err)> Probe(string path)
            {
                try
                {
                    using var res = await http.GetAsync($"https://{host}{path}", ct);
                    var challenge = res.Headers.Contains("cf-mitigated") || (res.StatusCode == HttpStatusCode.Forbidden && res.Headers.Server.ToString().Contains("cloudflare", StringComparison.OrdinalIgnoreCase)
                        && (await res.Content.ReadAsStringAsync(ct)).Contains("challenge", StringComparison.OrdinalIgnoreCase));
                    return ((int)res.StatusCode, challenge, res.Headers.Contains("X-Fleetdeck-Enroll"), null);
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return (0, false, false, e.Message); }
            }
            var mdm = await Probe("/mdm");
            if (mdm.Err is not null) Step(false, $"https://{host} kunne ikke nås: {mdm.Err}");
            else if (mdm.Challenge) Step(false, "Cloudflare udfordrer iPad'ernes forespørgsler (Bot Fight Mode/udfordring). Slå Bot Fight Mode fra for domænet – den kan ikke undtages på gratis-planen.");
            else if (mdm.Code is 502 or 503 or 504 or 530) Step(false, $"https://{host}/mdm svarede {mdm.Code}: tunnelen kan ikke nå MDM-serveren (tjek service-adressen i Cloudflare)");
            else Step(true, $"https://{host}/mdm når MDM-serveren (svar {mdm.Code})");
            var enroll = await Probe($"/enroll/fleetdeck-selvtjek-{Crypto.Token(8)}");
            Step(enroll.Fleetdeck, enroll.Fleetdeck ? $"https://{host}/enroll/… når Fleetdeck" : $"https://{host}/enroll/… når ikke Fleetdeck (svar {enroll.Code}{(enroll.Err is null ? "" : $": {enroll.Err}")}) – tjek reglen for /enroll/ i Cloudflare");
            var api = await Probe("/api/health");
            Step(api.Code is not (>= 200 and < 300), api.Code is >= 200 and < 300 ? $"Fleetdecks brugerflade/API kan nås på https://{host} – den må kun have reglerne for /mdm og /enroll/" : "Fleetdecks brugerflade er ikke offentlig");
            var nano = await Probe("/api/v1/nanomdm/push/x");
            Step(nano.Code is not 401 and not (>= 200 and < 300), nano.Code is 401 or (>= 200 and < 300) ? $"MDM-serverens API kan nås på https://{host}/api/v1 – fjern den regel i Cloudflare" : "MDM-serverens API er ikke offentlig");
            return Results.Ok(new { ok, steps });
        });

        // ---- push-certifikat (APNs) via mdmcert.download og Apple Push Certificates Portal
        adm.MapPost("/push/csr", async (MdmSettings ms, AuditService audit, ClaimsPrincipal u) =>
        {
            var csr = MdmPki.PushCsr(await ms.Org());
            var enc = MdmPki.EncryptionPair();
            await ms.SetSecret("push_new_key", csr.KeyPem);
            await ms.Set("push_csr", csr.Pem);
            await ms.SetSecret("push_enc_key", enc.KeyPem);
            await ms.Set("push_enc_cert", enc.Pem);
            await ms.Set("push_signed_request", "");
            await audit.Log(u.UserName(), "apple_mdm.push_csr", "ny push-nøgle og anmodning");
            return Results.Ok();
        });

        adm.MapGet("/push/csr", async (MdmSettings ms) =>
            await ms.Get("push_csr") is { Length: > 0 } pem ? Results.File(Encoding.ASCII.GetBytes(pem), "application/pkcs10", "fleetdeck-push.csr") : Results.NotFound());
        adm.MapGet("/push/encrypt", async (MdmSettings ms) =>
            await ms.Get("push_enc_cert") is { Length: > 0 } pem ? Results.File(Encoding.ASCII.GetBytes(pem), "application/x-pem-file", "fleetdeck-kryptering.pem") : Results.NotFound());

        // Send anmodningen til mdmcert.download (gratis for organisationer): svaret kommer krypteret pr. e-mail
        adm.MapPost("/push/mdmcert", async (MdmEmailInput i, MdmSettings ms, AuditService audit, ClaimsPrincipal u, CancellationToken ct) =>
        {
            var email = (i.Email ?? "").Trim();
            if (!System.Net.Mail.MailAddress.TryCreate(email, out _)) return Bad("Skriv den e-mailadresse, der er registreret hos mdmcert.download");
            var csr = await ms.Get("push_csr"); var enc = await ms.Get("push_enc_cert");
            if (csr.Length == 0 || enc.Length == 0) return Bad("Lav en ny anmodning først (trin 1)");
            var body = new JsonObject
            {
                ["csr"] = Convert.ToBase64String(Encoding.ASCII.GetBytes(csr)), ["email"] = email, ["key"] = MdmcertKey,
                ["encrypt"] = Convert.ToBase64String(Encoding.ASCII.GetBytes(enc)),
            };
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("fleetdeck/certhelper");
                using var res = await http.PostAsync(MdmcertUrl, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);
                var text = (await res.Content.ReadAsStringAsync(ct)).Trim();
                await audit.Log(u.UserName(), "apple_mdm.mdmcert", $"anmodning sendt ({(int)res.StatusCode})");
                if (!res.IsSuccessStatusCode) return Bad($"mdmcert.download svarede {(int)res.StatusCode}: {text[..Math.Min(300, text.Length)]}", 502);
                return Results.Ok(new { msg = $"Sendt. mdmcert.download e-mailer en fil (…plist.b64.p7) til {email} – upload den i næste trin." });
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return Bad($"mdmcert.download kunne ikke nås: {e.Message}", 502); }
        });

        adm.MapPost("/push/decrypt", async (MdmTextInput i, MdmSettings ms, AuditService audit, ClaimsPrincipal u) =>
        {
            var enc = await ms.Get("push_enc_cert");
            if (enc.Length == 0 || await ms.Secret("push_enc_key") is not { } key) return Bad("Krypteringsnøglen mangler – lav en ny anmodning (trin 1)");
            if (string.IsNullOrWhiteSpace(i.Text) || i.Text.Length > 200_000) return Bad("Vælg filen fra mdmcert.download");
            try
            {
                var req = MdmPki.DecryptSignedRequest(i.Text, enc, key);
                await ms.Set("push_signed_request", Convert.ToBase64String(req));
                await audit.Log(u.UserName(), "apple_mdm.push_request", "underskrevet anmodning pakket ud");
                return Results.Ok();
            }
            catch (FormatException e) { return Bad(e.Message); }
        });

        adm.MapGet("/push/request", async (MdmSettings ms) =>
            await ms.Get("push_signed_request") is { Length: > 0 } b64 ? Results.File(Convert.FromBase64String(b64), "application/octet-stream", "fleetdeck-push.req") : Results.NotFound());

        // Upload certifikatet fra Apple: det skal passe til nøglen, og topic må ikke skifte, når iPads er tilmeldt
        adm.MapPost("/push/cert", async (MdmTextInput i, MdmSettings ms, AppDb db, AuditService audit, ClaimsPrincipal u, MdmEngine engine, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(i.Text) || i.Text.Length > 100_000) return Bad("Vælg .pem-filen fra Apple");
            MdmPki.PushCertInfo? info = null; string? usedKey = null; bool newKey = false; string? lastErr = null;
            foreach (var (name, isNew) in new[] { ("push_new_key", true), ("push_key", false) })
            {
                if (await ms.Secret(name) is not { } k) continue;
                try { info = MdmPki.ParsePushCert(i.Text, k); usedKey = k; newKey = isNew; break; }
                catch (FormatException e) { lastErr = e.Message; }
            }
            if (info is null || usedKey is null) return Bad(lastErr ?? "Lav en anmodning i Fleetdeck først (trin 1)");
            if (info.NotAfter < DateTime.UtcNow) return Bad($"Certifikatet udløb {info.NotAfter.ToLocalTime():d. MMM yyyy}");
            var oldTopic = (await ms.Push()).Topic;
            if (oldTopic is not null && oldTopic != info.Topic
                && await db.MdmDevices.AnyAsync(d => d.State == MdmStates.Enrolled || d.State == MdmStates.Authenticated))
                return Bad($"Certifikatet har et andet topic ({info.Topic}) end det, iPad'erne er tilmeldt med ({oldTopic}). Brug \"Renew\" på det eksisterende certifikat hos Apple med samme Apple-konto – ellers skal alle iPads tilmeldes igen.", 409);
            await ms.Set("push_cert", info.CertPem);
            await ms.Set("push_topic", info.Topic);
            await ms.Set("push_expires", info.NotAfter.ToString("O"));
            await ms.Set("push_subject", info.Subject);
            await ms.Set("push_warned", "");
            await ms.Set("push_synced", "");
            if (newKey) { await ms.SetSecret("push_key", usedKey); await ms.SetSecret("push_new_key", null); }
            if (i.AppleId is { } aid) await ms.Set("push_apple_id", aid.Trim()[..Math.Min(120, aid.Trim().Length)]);
            await audit.Log(u.UserName(), "apple_mdm.push_cert", $"{info.Topic}, udløber {info.NotAfter:yyyy-MM-dd}");
            string? sync = null;
            if (await ms.Client() is { } client)
            {
                try { await client.PutPushCert(info.CertPem, usedKey, ct); await ms.Set("push_synced", Crypto.Sha256(info.CertPem)[..16]); await ms.Set("push_synced_at", DateTime.UtcNow.ToString("O")); sync = "Lagt på MDM-serveren."; }
                catch (Exception e) when (e is MdmException or HttpRequestException or TaskCanceledException) { sync = $"MDM-serveren kunne ikke nås ({e.Message}) – Fleetdeck prøver igen, når den svarer."; }
            }
            engine.Kick();
            return Results.Ok(new { topic = info.Topic, expires = info.NotAfter, msg = sync });
        });

        // Til manuel opsætning (compose/Unraid-skabelon) – vises kun efter klik og logges
        adm.MapPost("/manual", async (MdmSettings ms, AuditService audit, ClaimsPrincipal u) =>
        {
            if (await ms.FleetdeckLanUrl() is not { } lan) return Bad("Fleetdecks adresse på hjemmenettet mangler");
            var (api, hook, hmac) = await ms.EnsureSecrets();
            await ms.EnsureCa();
            await audit.Log(u.UserName(), "apple_mdm.manual", "nøgler vist til manuel opsætning");
            return Results.Ok(new { apiKey = api, webhookUrl = $"{lan}/api/mdm/hook/{hook}", hmacKey = hmac });
        });

        adm.MapGet("/ca.pem", async (MdmSettings ms) =>
        {
            var (cert, _) = await ms.EnsureCa();
            return Results.File(Encoding.ASCII.GetBytes(cert), "application/x-pem-file", MdmSidecar.CaFile);
        });

        adm.MapPost("/ca", async (MdmSettings ms) =>
        {
            var (cert, _) = await ms.EnsureCa();
            return Results.Ok(new { thumbprint = MdmPki.Thumbprint(cert) });
        });

        // ------------------------------------------------------------------ agenten henter hemmelighederne (som tunnel-nøglen)
        app.MapGet(MdmSidecar.SecretsPath, async (string? nonce, HttpContext ctx, AppDb db, MdmSettings ms, AuditService audit) =>
        {
            var h = ctx.Request.Headers.Authorization.ToString();
            if (!h.StartsWith("Bearer ", StringComparison.Ordinal) || await AgentAuth.Resolve(db, h[7..].Trim()) is not { } m) return Results.Unauthorized();
            ctx.Response.Headers.CacheControl = "no-store";
            var c = await ms.Sidecar();
            if (c.MachineId != m.Id) return Results.Text("MDM-serveren er ikke sat op til at køre på denne maskine", statusCode: 403);
            if (await ms.TakeNonce(nonce) is not int jobId
                || !await db.Jobs.AnyAsync(j => j.Id == jobId && j.MachineId == m.Id && j.Source == MdmSidecar.Source && j.Title == MdmSidecar.StartTitle
                    && (j.Status == JobStatus.Running || j.Status == JobStatus.Pending)))
            {
                await audit.Log($"agent:{m.Name}", "apple_mdm.secrets_denied", "ugyldig eller brugt engangskode");
                return Results.Text("Engangskoden er ugyldig eller allerede brugt – start MDM-serveren igen fra Fleetdeck", statusCode: 403);
            }
            var (api, hook, hmac) = await ms.EnsureSecrets();
            if (await ms.FleetdeckLanUrl() is not { } lan) return Results.Text("Fleetdecks adresse mangler", statusCode: 404);
            await audit.Log($"agent:{m.Name}", "apple_mdm.secrets", $"nøglerne er hentet af job {jobId}");
            return Results.Text($"NANOHUB_API_KEY={api}\nNANOHUB_WEBHOOK_URL={lan}/api/mdm/hook/{hook}\nNANOHUB_WEBHOOK_HMAC_KEY={hmac}\n", "text/plain");
        });

        // ------------------------------------------------------------------ webhook fra NanoHUB (kun hjemmenettet – må aldrig gå gennem tunnelen)
        app.MapPost("/api/mdm/hook/{secret}", async (string secret, HttpContext ctx, MdmSettings ms, MdmService svc, MdmEngine engine, ILogger<MdmService> log) =>
        {
            if (MdmPublicHost.IsPublic(ctx.Request)) return Results.NotFound();
            if (await ms.Secret("mdm_hook_secret") is not { } want || !Crypto.FixedEquals(secret, want)) return Results.NotFound();
            using var mem = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(mem);
            if (mem.Length > 4_000_000) return Results.StatusCode(413);
            var body = mem.ToArray();
            var sig = ctx.Request.Headers["X-Hmac-Signature"].ToString();
            if (sig.Length > 0)
            {
                if (await ms.Secret("mdm_hmac_key") is not { } hk || !MdmHook.HmacValid(body, hk, sig)) return Results.Unauthorized();
                if (await ms.Get("mdm_hmac_seen") != "1") await ms.Set("mdm_hmac_seen", "1");
            }
            else if (await ms.Get("mdm_hmac_seen") == "1") return Results.Unauthorized();   // har serveren først underskrevet, skal den blive ved
            MdmEvent ev;
            try { ev = MdmHook.Parse(Encoding.UTF8.GetString(body)); }
            catch (Exception e) when (e is FormatException or System.Text.Json.JsonException) { log.LogWarning("Apple-styring: ugyldig webhook: {Msg}", e.Message); return Results.BadRequest(); }
            await svc.Handle(ev);
            if (ev.Topic is MdmHook.TokenUpdate or MdmHook.Authenticate) engine.Kick();
            return Results.Ok();
        }).AllowAnonymous();

        // ------------------------------------------------------------------ tilmeldingslink (offentligt via tunnelen): landingsside + POST
        app.MapGet("/enroll/{token}", async (string token, HttpContext ctx, MdmService svc, AppDb db) =>
        {
            NoCache(ctx);
            var t = await svc.FindLink(token);
            if (t is null) return Page("Linket virker ikke længere", "Linket er udløbet eller allerede brugt. Lav et nyt i Fleetdeck.", null, 410);
            var name = await db.Ipads.AsNoTracking().Where(p => p.Id == t.IpadId).Select(p => p.Name).FirstOrDefaultAsync() ?? "iPad";
            return Page($"Apple-styring til {name}",
                "Tryk på knappen på den iPad, der skal tilmeldes. Åbn derefter Indstillinger → Profil hentet → Installér. Bagefter: Indstillinger → Generelt → VPN og enhedsadministration.",
                "Hent profil", 200);
        }).AllowAnonymous();

        app.MapPost("/enroll/{token}", async (string token, HttpContext ctx, MdmService svc, AppDb db, AuditService audit) =>
        {
            NoCache(ctx);
            var t = await svc.FindLink(token);
            if (t is null || svc.LinkProfile(t) is not { } xml) return Page("Linket virker ikke længere", "Linket er udløbet eller allerede brugt. Lav et nyt i Fleetdeck.", null, 410);
            t.Fetches++;
            await db.SaveChangesAsync();
            await audit.Log("iPad", "apple_mdm.profile", $"iPad {t.IpadId}: tilmeldingsprofil hentet ({t.Fetches}. gang)");
            return Results.File(Encoding.UTF8.GetBytes(xml), "application/x-apple-aspen-config", $"Fleetdeck-{t.IpadId}.mobileconfig");
        }).AllowAnonymous();

        // ------------------------------------------------------------------ pr. iPad (forældre med rettigheder + administratorer)
        fam.MapGet("", async (int id, bool? ddm, ClaimsPrincipal u, AppDb db, MdmSettings ms, MdmService svc, CancellationToken ct) =>
        {
            var sc = await FamilyScope.Of(u, db);
            var p = await db.Ipads.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (p is null || !sc.Sees(id)) return Results.NotFound();
            var d = await db.MdmDevices.AsNoTracking().FirstOrDefaultAsync(x => x.IpadId == id, ct);
            var cmds = await db.MdmCommands.AsNoTracking().Where(c => c.IpadId == id).OrderByDescending(c => c.Created).Take(50).ToListAsync(ct);
            var push = await ms.Push();
            var link = sc.Admin ? await db.MdmEnrollTokens.AsNoTracking().Where(t => t.IpadId == id).OrderByDescending(t => t.Id).FirstOrDefaultAsync(ct) : null;
            object? ddmStatus = null;
            if (ddm == true && d?.EnrollmentId is { } eid && await ms.Client() is { } client)
            {
                try
                {
                    var vals = await client.StatusValues(eid, ct);
                    ddmStatus = vals.Where(v => v.Path.Contains("softwareupdate", StringComparison.Ordinal) || v.Path.Contains("operating-system", StringComparison.Ordinal))
                        .Select(v => new { path = v.Path, value = v.Value, at = v.At });
                }
                catch (Exception e) when (e is MdmException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { ddmStatus = new { error = e.Message }; }
            }
            var locked = d is not null ? await svc.LockedForMdm(await db.Ipads.FirstAsync(x => x.Id == id, ct)) : null;
            if (locked is not null) await db.SaveChangesAsync(ct);   // låst profil-version kan være talt op
            return Results.Ok(new
            {
                configured = push.Topic is not null && (await ms.PublicHost()).Length > 0 && await ms.ApiUrl() is not null,
                pushExpires = push.Expires,
                pushWarn = push.Expires is { } pe && MdmEngine.ExpiryThreshold(pe, DateTime.UtcNow) is not null,
                device = d is null ? null : new
                {
                    d.State, d.Approved, d.ApprovalNote, d.ExpectedSerial, d.Serial, d.Udid, d.DeviceName, d.ProductName, d.ModelName, d.OsVersion, d.BuildVersion,
                    d.Supervised, d.ActivationLock, d.BatteryLevel, d.StorageTotalGb, d.StorageFreeGb, d.LastCheckin, d.LastInventory, d.EnrolledAt, d.CheckedOutAt,
                    d.IdentityExpires, d.LockedViaMdm, d.LockedInstalledVersion, lockedCurrent = locked is { } l && l.Hash == d.LockedInstalledHash,
                    lockedVersion = p.LockedProfileVersion, staticInstalled = p.LockedProfileApplied, d.OsTargetVersion, d.OsTargetBuild, d.OsTargetLocal, d.PushError,
                },
                link = link is null ? null : new { link.Expires, link.UsedAt, link.Fetches, active = link.UsedAt == null && link.Expires > DateTime.UtcNow && link.Fetches < MdmService.MaxFetches },
                commands = cmds.Select(c => new { c.CommandUuid, c.RequestType, c.Status, c.RequestedBy, c.Detail, c.Created, c.Completed, c.Error }),
                ddm = ddmStatus,
                can = sc.PermList, admin = sc.Admin,
            });
        });

        fam.MapPost("/refresh", (int id, ClaimsPrincipal u, AppDb db, MdmService svc, AuditService audit) =>
            Command(id, u, db, svc, audit, MdmPerms.Refresh, (p, d) => Done(MdmCommands.DeviceInformation(), "Opdatér oplysninger", null)));

        fam.MapPost("/lock", (int id, MdmLockInput i, ClaimsPrincipal u, AppDb db, MdmService svc, AuditService audit) =>
            Command(id, u, db, svc, audit, MdmPerms.Lock, (p, d) => Done(MdmCommands.DeviceLock(i.Message, i.Phone),
                (i.Message ?? "").Trim() is { Length: > 0 } msg ? $"Lås skærmen: \"{msg[..Math.Min(200, msg.Length)]}\"" : "Lås skærmen", null)));

        // Den låste profil (samme indhold som statisk tilstand) leveres via MDM – motoren sender den og nye versioner
        fam.MapPut("/locked", async (int id, MdmLockedInput i, ClaimsPrincipal u, AppDb db, MdmEngine engine, AuditService audit, Live live) =>
        {
            var sc = await FamilyScope.Of(u, db);
            if (MdmPerms.Check(sc, id, MdmPerms.LockedProfile) is { } deny) return Bad(deny.Message, deny.Status);
            var p = await db.Ipads.FindAsync(id);
            var d = await db.MdmDevices.FirstOrDefaultAsync(x => x.IpadId == id);
            if (p is null || d is null) return Bad("iPad'en er ikke tilmeldt Apple-styring");
            if (i.ViaMdm && p.LockedProfileApplied is not null && i.Force != true)
                return Bad("Den låste profil er allerede installeret via Mac (statisk tilstand). Fjern den først med Mac'en – to profiler med samme indhold kan ikke ligge på iPad'en samtidig.", 409);
            d.LockedViaMdm = i.ViaMdm;
            await db.SaveChangesAsync();
            await audit.Log(u.UserName(), "apple_mdm.locked", $"{p.Name}: låst profil via MDM {(i.ViaMdm ? "til" : "fra")}");
            engine.Kick(); await live.Changed("ipads");
            return Results.Ok();
        });

        fam.MapPost("/locked/resend", (int id, ClaimsPrincipal u, AppDb db, MdmService svc, AuditService audit) =>
            Command(id, u, db, svc, audit, MdmPerms.LockedProfile, async (p, d) =>
            {
                if (!d.LockedViaMdm) throw new MdmException("Slå \"låst profil via MDM\" til først");
                var built = await svc.LockedForMdm(p) ?? throw new MdmException("Profilen er tom: der er hverken DNS-adresse eller begrænsninger");
                return (MdmCommands.InstallProfile(built.Xml), $"{MdmService.LockedDetailPrefix}{p.LockedProfileVersion}", built.Hash);
            }));

        fam.MapPut("/os-update", async (int id, MdmOsUpdateInput i, ClaimsPrincipal u, AppDb db, MdmService svc, AuditService audit, Live live, CancellationToken ct) =>
        {
            var sc = await FamilyScope.Of(u, db);
            if (MdmPerms.Check(sc, id, MdmPerms.OsUpdate) is { } deny) return Bad(deny.Message, deny.Status);
            var p = await db.Ipads.FindAsync([id], ct);
            var d = await db.MdmDevices.FirstOrDefaultAsync(x => x.IpadId == id, ct);
            if (p is null || d is null) return Bad("iPad'en er ikke tilmeldt Apple-styring");
            var version = (i.Version ?? "").Trim(); var build = (i.Build ?? "").Trim();
            if ((MdmDeclarations.ValidateVersion(version) ?? MdmDeclarations.ValidateBuild(build)) is { } verr) return Bad(verr);
            if (!DateTime.TryParse(i.Deadline, CultureInfo.InvariantCulture, DateTimeStyles.None, out var deadline)) return Bad("Vælg en frist (dato og tidspunkt)");
            deadline = DateTime.SpecifyKind(deadline, DateTimeKind.Unspecified);
            try
            {
                var c = await svc.SetOsUpdate(p, d, version, build, deadline, u.UserName(), ct);
                await audit.Log(u.UserName(), "apple_mdm.os_update", $"{p.Name}: {c.Detail}");
                await live.Changed("ipads");
                return c.Status == MdmCommandStates.Failed ? Bad(c.Error ?? "Fejl", 502) : Results.Ok(new { msg = c.Detail });
            }
            catch (MdmException e) { return Bad(e.Message); }
        });

        fam.MapDelete("/os-update", async (int id, ClaimsPrincipal u, AppDb db, MdmService svc, AuditService audit, Live live, CancellationToken ct) =>
        {
            var sc = await FamilyScope.Of(u, db);
            if (MdmPerms.Check(sc, id, MdmPerms.OsUpdate) is { } deny) return Bad(deny.Message, deny.Status);
            var p = await db.Ipads.FindAsync([id], ct);
            var d = await db.MdmDevices.FirstOrDefaultAsync(x => x.IpadId == id, ct);
            if (p is null || d is null) return Bad("iPad'en er ikke tilmeldt Apple-styring");
            try
            {
                var c = await svc.ClearOsUpdate(p, d, u.UserName(), ct);
                await audit.Log(u.UserName(), "apple_mdm.os_update", $"{p.Name}: {c.Detail}");
                await live.Changed("ipads");
                return c.Status == MdmCommandStates.Failed ? Bad(c.Error ?? "Fejl", 502) : Results.Ok();
            }
            catch (MdmException e) { return Bad(e.Message); }
        });

        // Slet iPad'en: kræver rettigheden "erase", iPad'ens navn skrevet og brugerens egen adgangskode
        fam.MapPost("/erase", async (int id, MdmEraseInput i, ClaimsPrincipal u, AppDb db, MdmService svc, AuditService audit, Notifier notifier) =>
        {
            var sc = await FamilyScope.Of(u, db);
            if (MdmPerms.Check(sc, id, MdmPerms.Erase) is { } deny) return Bad(deny.Message, deny.Status);
            var p = await db.Ipads.FindAsync(id);
            if (p is null) return Results.NotFound();
            if (!string.Equals((i.ConfirmName ?? "").Trim(), p.Name.Trim(), StringComparison.CurrentCultureIgnoreCase)) return Bad("Skriv iPad'ens navn præcis for at bekræfte");
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == u.UserId());
            if (user is null || string.IsNullOrEmpty(i.Password) || !Crypto.VerifyPassword(i.Password, user.PasswordHash)) return Bad("Forkert adgangskode", 403);
            var r = await Command(id, u, db, svc, audit, MdmPerms.Erase, (pp, d) => Done(MdmCommands.EraseDevice(), "Slet iPad'en (alt indhold og alle indstillinger)", null));
            notifier.Send($"🔴 {p.Name}: sletning sendt\n{u.UserName()} har bedt om at slette iPad'en via Apple-styring.", "critical");
            return r;
        });

        // ---- kun administratorer
        famAdm.MapPost("/enroll-link", async (int id, MdmLinkInput i, AppDb db, MdmService svc, AuditService audit, ClaimsPrincipal u) =>
        {
            var p = await db.Ipads.FindAsync(id);
            if (p is null) return Results.NotFound();
            try
            {
                var (url, expires) = await svc.CreateLink(p, i.ExpectedSerial);
                await audit.Log(u.UserName(), "apple_mdm.enroll_link", p.Name);
                return Results.Ok(new { url, expires });
            }
            catch (MdmException e) { return Bad(e.Message); }
        });

        // Til Apple Configurator: hent profilen direkte (kræver login)
        famAdm.MapGet("/enroll-profile", async (int id, AppDb db, MdmService svc, AuditService audit, ClaimsPrincipal u, HttpContext ctx) =>
        {
            var p = await db.Ipads.FindAsync(id);
            if (p is null) return Results.NotFound();
            try
            {
                var d = await svc.Device(p);
                var xml = await svc.BuildEnrollProfile(p, d);
                await db.SaveChangesAsync();
                await audit.Log(u.UserName(), "apple_mdm.enroll_profile", p.Name);
                NoCache(ctx);
                return Results.File(Encoding.UTF8.GetBytes(xml), "application/x-apple-aspen-config", $"Fleetdeck-MDM-{p.Id}.mobileconfig");
            }
            catch (MdmException e) { return Bad(e.Message); }
        });

        famAdm.MapPost("/approve", async (int id, AppDb db, AuditService audit, ClaimsPrincipal u, MdmEngine engine, Live live) =>
        {
            var d = await db.MdmDevices.FirstOrDefaultAsync(x => x.IpadId == id);
            if (d is null) return Results.NotFound();
            d.Approved = true; d.ApprovalNote = null;
            if (d.Serial is { Length: > 0 } sn) d.ExpectedSerial = sn;
            await db.SaveChangesAsync();
            await audit.Log(u.UserName(), "apple_mdm.approve", $"iPad {id} ({d.Serial})");
            engine.Kick(); await live.Changed("ipads");
            return Results.Ok();
        });

        // Fjern Apple-styring fra iPad'en (MDM-profilen fjernes – og dermed alt, der er installeret via MDM)
        famAdm.MapPost("/unenroll", (int id, ClaimsPrincipal u, AppDb db, MdmService svc, AuditService audit) =>
            Command(id, u, db, svc, audit, MdmPerms.Refresh, (p, d) => Done(MdmCommands.RemoveProfile(MdmEnrollmentProfile.Identifier(p)), MdmService.Unenroll, null)));

        // Glem iPad'en i Apple-styring (kun i Fleetdeck – fx når den er slettet eller solgt)
        famAdm.MapDelete("", async (int id, AppDb db, AuditService audit, ClaimsPrincipal u, Live live) =>
        {
            await db.MdmEnrollTokens.Where(t => t.IpadId == id).ExecuteDeleteAsync();
            await db.MdmCommands.Where(c => c.IpadId == id).ExecuteDeleteAsync();
            var n = await db.MdmDevices.Where(d => d.IpadId == id).ExecuteDeleteAsync();
            if (n > 0) await audit.Log(u.UserName(), "apple_mdm.forget", $"iPad {id}");
            await live.Changed("ipads");
            return Results.Ok();
        });
    }

    private static Task<(MdmCommands.Built Cmd, string Detail, string? Hash)> Done(MdmCommands.Built cmd, string detail, string? hash) => Task.FromResult((cmd, detail, hash));

    private static string Origin(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? $"{u.Scheme}://{u.Host}:{u.Port}" : url;

    /// <summary>Fælles for kommandoer: rettighed, tilmeldt iPad, send og log.</summary>
    private static async Task<IResult> Command(int id, ClaimsPrincipal u, AppDb db, MdmService svc, AuditService audit, string action,
        Func<Ipad, MdmDevice, Task<(MdmCommands.Built Cmd, string Detail, string? Hash)>> build)
    {
        var sc = await FamilyScope.Of(u, db);
        if (MdmPerms.Check(sc, id, action) is { } deny) return Bad(deny.Message, deny.Status);
        var p = await db.Ipads.FindAsync(id);
        var d = await db.MdmDevices.FirstOrDefaultAsync(x => x.IpadId == id);
        if (p is null || d is null) return Bad("iPad'en er ikke tilmeldt Apple-styring");
        try
        {
            var (cmd, detail, hash) = await build(p, d);
            var c = await svc.Send(p, d, cmd, u.UserName(), detail, hash);
            await audit.Log(u.UserName(), "apple_mdm.command", $"{p.Name}: {detail} ({c.Status})");
            return c.Status == MdmCommandStates.Failed ? Bad(c.Error ?? "Kommandoen kunne ikke sendes", 502) : Results.Ok(new { c.CommandUuid, c.Status, msg = c.Error });
        }
        catch (MdmException e) { return Bad(e.Message); }
    }

    private static void NoCache(HttpContext ctx)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
        ctx.Response.Headers["X-Fleetdeck-Enroll"] = "1";
    }

    /// <summary>En lille, selvstændig side (ingen scripts, intet fra brugerfladen).</summary>
    private static IResult Page(string title, string text, string? button, int status)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        var form = button is null ? "" : $"<form method=\"post\"><button type=\"submit\">{E(button)}</button></form>";
        var html = $"""
            <!doctype html><html lang="da"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <meta name="robots" content="noindex"><title>{E(title)}</title>
            <style>body{"{"}font-family:-apple-system,system-ui,sans-serif;max-width:32rem;margin:3rem auto;padding:0 1.2rem;color:#1d1d1f{"}"}
            button{"{"}font-size:1.1rem;padding:.8rem 1.6rem;border-radius:.7rem;border:0;background:#0071e3;color:#fff{"}"}</style></head>
            <body><h1>{E(title)}</h1><p>{E(text)}</p>{form}</body></html>
            """;
        return Results.Content(html, "text/html; charset=utf-8", Encoding.UTF8, status);
    }

    private static async Task<Machine?> Host(AppDb db, MdmSidecarConfig c) =>
        c.MachineId is int mid ? await db.Machines.FirstOrDefaultAsync(m => m.Id == mid && m.Platform == "linux") : null;

    private static Task<int> CancelPending(AppDb db, int machineId) =>
        db.Jobs.Where(j => j.MachineId == machineId && j.Source == MdmSidecar.Source && j.Status == JobStatus.Pending && j.Title != MdmSidecar.SuggestTitle)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Cancelled).SetProperty(j => j.Finished, DateTime.UtcNow));
}
