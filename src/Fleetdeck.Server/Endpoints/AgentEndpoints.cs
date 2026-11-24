using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Hubs;
using Fleetdeck.Server.Services;
using Fleetdeck.Server.Services.Reports;
using Fleetdeck.Shared;

namespace Fleetdeck.Server.Endpoints;

public static partial class AgentEndpoints
{
    [GeneratedRegex("^[A-Z0-9][A-Z0-9_-]{0,30}$")] private static partial Regex ValidName();

    private static async Task<Machine?> Auth(HttpContext ctx, AppDb db)
    {
        var h = ctx.Request.Headers.Authorization.ToString();
        return h.StartsWith("Bearer ", StringComparison.Ordinal) ? await AgentAuth.Resolve(db, h[7..].Trim()) : null;
    }

    /// <summary>Kører der et ios-job med en af handlingerne for iPad'en på denne Mac (og evt. for netop dét backup-sæt)?</summary>
    internal static async Task<bool> IosJobFor(AppDb db, Machine m, int ipadId, string[] actions, string? backupSet = null)
    {
        if (m.Platform != Platforms.Mac) return false;
        var running = await db.Jobs.AsNoTracking().Where(j => j.MachineId == m.Id && j.Type == JobTypes.Ios && j.Status == JobStatus.Running)
            .Select(j => j.ParamsJson).ToListAsync();
        return running.Select(Json.Des<JobParams>).Any(p => p.IpadId == ipadId && actions.Contains(p.Action) && (backupSet is null || p.BackupSet == backupSet));
    }

    /// <summary>Profilen fra ?profile= til installationsscriptet: kun tal (0 = ingen profil), ellers tom = spørg.</summary>
    /// <summary>Logger en afvist tilmeldingsnøgle (uden selve nøglen), så man kan se, hvad der blev sendt.</summary>
    private static async Task BadKey(HttpContext ctx, string? key, string where) =>
        await ctx.RequestServices.GetRequiredService<AuditService>().Log(ctx.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "?", "enroll.bad_key", $"{where} · {KeyHint(key)}");

    private static string KeyHint(string? key) => string.IsNullOrEmpty(key) ? "tom nøgle"
        : $"{key.Length} tegn (nøglen har 24)" + (key.Contains("://") || key.Contains('/') ? ", ligner en adresse/kommando" : "")
          + (key.Any(char.IsWhiteSpace) ? ", indeholder mellemrum" : "") + (key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')) ? ", har andre tegn end en nøgle" : "");

    private static string ProfileArg(string? profile) => int.TryParse(profile, out var v) && v >= 0 ? v.ToString() : "";

    /// <summary>Adressen, agenter og SD-kort skal bruge (som i installationsscripterne).</summary>
    internal static string AgentBase(HttpContext ctx, string configured) => Base(ctx, configured);

    private static string Base(HttpContext ctx, string configured) =>
        configured.Length > 0 ? configured.TrimEnd('/') : $"{ctx.Request.Scheme}://{ctx.Request.Host}";

    /// <summary>Installationsscriptet med adresse, nøgle/kode og profil. TLS-fingeraftrykket kommer kun med, når adressen er https.</summary>
    private static string Fill(string tpl, HttpContext ctx, string configured, string key, string? profile, string? pin, bool request = false)
    {
        var server = Base(ctx, configured);
        var usePin = server.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? pin ?? "" : "";
        return tpl.Replace("{{SERVER}}", server).Replace("{{KEY}}", request ? "" : key.Trim()).Replace("{{PROFILE}}", request ? "" : ProfileArg(profile))
            .Replace("{{TLSPIN}}", usePin).Replace("{{REQUEST}}", request ? "1" : "");
    }

    /// <summary>
    /// Installationsscriptet uden nøgle: "Anmod om adgang" er slået til (og ved "kun hjemmenet" kommer kaldet derfra).
    /// Scriptet installerer agenten og beder administratoren om lov – det får hverken profiler eller nøgler.
    /// </summary>
    internal static async Task<bool> RequestInstall(HttpContext ctx, SettingsService s, string? key) =>
        string.IsNullOrWhiteSpace(key) && AccessMode.Normalize(await s.Get(AccessRequestService.ModeSetting)) is var mode
        && (mode == AccessMode.On || (mode == AccessMode.Lan && await ApiKeyGuard.FromHomeNetwork(ctx)));

    /// <summary>Fingeraftrykket, agenterne fastlåser: fra Indstillinger (fx en proxy med eget certifikat) eller serverens eget https-certifikat.</summary>
    internal static async Task<string?> TlsPin(SettingsService s, TlsInfo tls) => TlsPinning.Normalize(await s.Get("tls_pin")) ?? tls.Pin;

    public static void MapAgentEndpoints(this WebApplication app)
    {
        // PowerShell-installer: irm "<server>/install.ps1?key=..." | iex
        app.MapGet("/install.ps1", async (string? key, string? profile, HttpContext ctx, SettingsService s, EnrollmentService enroll, TlsInfo tls) =>
        {
            var request = await RequestInstall(ctx, s, key);
            if (!request && (await enroll.CheckKey(key)).Kind == EnrollmentService.KeyKind.Invalid)
            {
                await BadKey(ctx, key, "install.ps1");
                return Results.Text("Write-Host 'Fleetdeck: forkert tilmeldingsnøgle' -ForegroundColor Red", "text/plain; charset=utf-8", statusCode: 403);
            }
            var tpl = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Templates", "install.ps1"));
            return Results.Text(Fill(tpl, ctx, await s.Get("server_url"), key ?? "", profile, await TlsPin(s, tls), request), "text/plain; charset=utf-8");
        });

        // Profilerne, man vælger mellem ved installationen (kræver tilmeldingsnøglen)
        app.MapGet("/api/agent/profiles", async (string? key, AppDb db, EnrollmentService enroll, HttpContext ctx) =>
        {
            if ((await enroll.CheckKey(key)).Kind == EnrollmentService.KeyKind.Invalid) { await BadKey(ctx, key, "profiler"); return Results.Problem("Forkert tilmeldingsnøgle", statusCode: 403); }
            var list = await db.Profiles.AsNoTracking().OrderBy(p => p.Name)
                .Select(p => new ProfileOption(p.Id, p.Name, db.Machines.Count(m => m.ProfileId == p.Id), db.ProfilePackages.Count(x => x.ProfileId == p.Id))).ToListAsync();
            return Results.Ok(list);
        });

        // Linux: curl -fsSL "<server>/install.sh?key=..." | sudo bash
        app.MapGet("/install.sh", async (string? key, string? profile, HttpContext ctx, SettingsService s, EnrollmentService enroll, TlsInfo tls) =>
        {
            var request = await RequestInstall(ctx, s, key);
            if (!request && (await enroll.CheckKey(key)).Kind == EnrollmentService.KeyKind.Invalid)
            {
                await BadKey(ctx, key, "install.sh");
                return Results.Text("echo 'Fleetdeck: forkert tilmeldingsnøgle' >&2; exit 1", "text/plain; charset=utf-8", statusCode: 403);
            }
            var tpl = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Templates", "install.sh"));
            return Results.Text(Fill(tpl, ctx, await s.Get("server_url"), key ?? "", profile, await TlsPin(s, tls), request), "text/plain; charset=utf-8");
        });

        // Resultater af overvågningstjek, som agenten har kørt fra sin maskine
        app.MapPost("/api/agent/checks", async (List<AgentCheckResult> results, HttpContext ctx, AppDb db, CheckRunner runner, CancellationToken ct) =>
        {
            if (await Auth(ctx, db) is not { } m) return Results.Unauthorized();
            await runner.FromAgent(m.Id, results, ct);
            return Results.Ok();
        });

        // Gendannelsespunkter: agenten uploader sin pakke og henter den igen ved gendannelse (kun sin egen maskines)
        app.MapPut("/api/agent/kit", async (string? name, HttpContext ctx, AppDb db, Paths paths, Live live, CancellationToken ct) =>
        {
            if (await Auth(ctx, db) is not { } m) return Results.Unauthorized();
            // iPad-backups (ios-job på en Mac): egen mappe pr. iPad, uden om gendannelsespunkternes oprydning
            if (IpadBackups.IsIpad(name))
            {
                if (IpadBackups.ParsePart(name) is not { } part) return Results.BadRequest("Ugyldigt navn på iPad-backup");
                if (!await IosJobFor(db, m, part.IpadId, [IosActions.Backup, IosActions.Supervise, IosActions.Release, IosActions.SuperviseNoWipe])) return Results.Forbid();
                var len = await IpadBackups.Receive(paths, part, ctx.Request.Body, ct);
                return len == 0 ? Results.BadRequest("Tom del") : Results.Ok(new { file = name, size = len });
            }
            var file = Kits.SafeName(name) ?? $"{DateTime.Now:yyyyMMdd-HHmmss}.tar.gz";
            var dir = Kits.Dir(paths, m.Id); Directory.CreateDirectory(dir);
            var tmp = Path.Combine(dir, file + ".upload");
            await using (var f = File.Create(tmp)) await ctx.Request.Body.CopyToAsync(f, ct);
            if (new FileInfo(tmp).Length < 100) { File.Delete(tmp); return Results.BadRequest("Tom pakke"); }
            File.Move(tmp, Path.Combine(dir, file), true);
            var check = Kits.Verify(Path.Combine(dir, file));
            Kits.Prune(dir, 14);
            await live.Changed("machines", m.Id);
            return Results.Ok(new { file, check.Ok, check.Files, check.Error });
        }).DisableAntiforgery().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(4L * 1024 * 1024 * 1024));
        app.MapGet("/api/agent/kit/{name}", async (string name, HttpContext ctx, AppDb db, Paths paths) =>
        {
            if (await Auth(ctx, db) is not { } m) return Results.Unauthorized();
            if (IpadBackups.IsIpad(name))
            {
                // kun til et kørende gendannelsesjob for netop det sæt
                if (IpadBackups.ParsePart(name) is not { } part || !await IosJobFor(db, m, part.IpadId, [IosActions.Restore], part.Set)) return Results.NotFound();
                var pf = IpadBackups.PartFile(paths, part);
                return File.Exists(pf) ? Results.File(pf, "application/octet-stream", name) : Results.NotFound();
            }
            if (Kits.SafeName(name) is not { } n) return Results.NotFound();
            var f = Path.Combine(Kits.Dir(paths, m.Id), n);
            return File.Exists(f) ? Results.File(f, "application/gzip", n) : Results.NotFound();
        });

        // Agentfilen med kontrolsum og signatur i svarets headere (agenten tjekker dem mod den nøgle, den husker)
        app.MapGet("/api/agent/download", (string? platform, string? arch, string? kind, AgentBinary bin, SigningKeys keys, HttpContext ctx) =>
        {
            // Mac-appen som disk-image (kind=dmg): træk Fleetdeck.app til Programmer, åbn den, og forbind med et vindue
            if (platform == Platforms.Mac && kind == "dmg")
            {
                var dmg = bin.MacDmgPathFor(arch);
                if (dmg is null) return Results.NotFound($"Mac-appen til {AgentBinary.NormalizeArch(arch)} er ikke bygget ind i serveren");
                var name = AgentBinary.MacDmgFile(arch).Download;
                // signeret som agentfilen: Mac-agenten henter selv appen, når den er ældre end agenten (se MacAppSync)
                var (dh, ds) = keys.For(dmg);
                ctx.Response.Headers[AgentSigning.HashHeader] = dh;
                ctx.Response.Headers[AgentSigning.SignatureHeader] = ds;
                return Results.File(dmg, "application/x-apple-diskimage", name);
            }
            var unix = platform is Platforms.Linux or Platforms.Mac;
            var path = unix ? bin.UnixPathFor(platform, arch) : bin.ExePath;
            if (path is null)
                return Results.NotFound(unix ? $"{(platform == Platforms.Mac ? "Mac" : "Linux")}-agenten til {AgentBinary.NormalizeArch(arch)} er ikke bygget ind i serveren"
                    : "Agenten er ikke bygget ind i serveren");
            var (hash, sig) = keys.For(path);
            ctx.Response.Headers[AgentSigning.HashHeader] = hash;
            ctx.Response.Headers[AgentSigning.SignatureHeader] = sig;
            return Results.File(path, "application/octet-stream", unix ? "fleetdeck-agent" : "Fleetdeck.exe");
        });

        // Serverens offentlige nøgle (agenterne husker den ved første kontakt)
        app.MapGet("/api/agent/signing-key", (SigningKeys keys) => Results.Text(keys.PublicPem, "application/x-pem-file"));

        app.MapGet("/api/agent/version", (string? platform, string? arch, AgentBinary bin) => new { version = bin.VersionFor(platform, arch) });

        // Downloadsiden (/hent, uden login): hvilke agenter kan hentes, og hvilken adresse skal de forbinde til
        app.MapGet("/api/agent/downloads", async (HttpContext ctx, AgentBinary bin, SettingsService s) => new
        {
            server = Base(ctx, await s.Get("server_url")),
            version = ThisVersion.Value,
            // "Anmod om adgang" kan bruges herfra (installationen uden nøgle beder administratoren om lov)
            accessRequests = await RequestInstall(ctx, s, null),
            windows = bin.ExePath is not null,
            linux = new { x64 = bin.LinuxPathFor("x64") is not null, arm64 = bin.LinuxPathFor("arm64") is not null, arm = bin.LinuxPathFor("arm") is not null },
            mac = new { arm64 = bin.UnixPathFor(Platforms.Mac, "arm64") is not null, x64 = bin.UnixPathFor(Platforms.Mac, "x64") is not null,
                x64Legacy = bin.UnixPathFor(Platforms.Mac, MacCompat.LegacyArch) is not null },
            macApp = new { arm64 = bin.MacDmgPathFor("arm64") is not null, x64 = bin.MacDmgPathFor("x64") is not null,
                x64Legacy = bin.MacDmgPathFor(MacCompat.LegacyArch) is not null },
        });

        app.MapPost("/api/agent/enroll", async (EnrollRequest r, AppDb db, SettingsService s, EnrollmentService enroll, ProvisionService prov, DeployService deploy,
            AuditService audit, Live live, ExtensionService ext, JobSigningKey jobKey, TlsInfo tls, Notifier notifier, PiCardService picards) =>
        {
            var o = await enroll.Enroll(r);
            if (o.Status == 403) { await audit.Log(r.Name ?? "?", "enroll.bad_key", $"tilmelding · {KeyHint(r.Key)}{(r.AccessSecret is { Length: > 0 } ? " · med anmodningens hemmelighed" : "")}"); return Results.Problem(o.Error, statusCode: 403); }
            if (o.Ok && r.AccessSecret is { Length: > 0 }) await live.Changed("access-requests");
            if (!o.Ok) return Results.Problem(o.Error, statusCode: o.Status);
            var m = o.Machine!;
            if (o.Repeat) { }   // samme maskine igen (svaret gik tabt): alt er allerede sat i gang første gang
            else if (o.Pending)
            {
                // venter på godkendelse: ingen udvidelser, ingen genskabelse og ingen udrulning før en administrator har sagt god for den
                notifier.Send($"🟠 Ny tilmelding med navnet {o.Existing!.Label}\nNavnet findes allerede. Godkend den som erstatning eller afvis den under Maskiner.");
            }
            else
            {
                await deploy.Completed(r.DeployId, m.Name, m.Id);
                // et SD-kort fra "Lav SD-kort": genskab fra netop det gendannelsespunkt (tilstanden "nyt kort") i stedet for den almindelige nyinstallation
                var card = await picards.OnEnrolled(r.Key, m);
                if (r.Provision && !card) await prov.Queue(m.Id, "nyinstallation");
                // Udvidelser valgt i opsætningsvinduet
                // installeret fra kommandolinjen (intet valg): standardudvidelserne fra Indstillinger
                var wanted = r.Extensions ?? (await s.Get("default_extensions")).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                foreach (var id in wanted.Distinct())
                {
                    try { await ext.Install(m, id, "opsætning", "agent"); }
                    catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException) { await audit.Log("agent", "extension.skip", $"{m.Name}: {e.Message}"); }
                }
            }
            await live.Changed("machines", m.Id);
            // Nye agenter pinner jobnøglen med det samme (kræver signerede jobs fra start), medmindre signering er slået fra
            var signing = SignedJobsMode.Normalize(await s.Get("signed_jobs")) != SignedJobsMode.Off;
            return Results.Ok(new EnrollResponse(m.Id, $"{m.Id}.{o.Secret}", (int)await s.GetNum("poll_seconds", 30),
                signing ? jobKey.PublicPem : null, await TlsPin(s, tls), o.Pending));
        });

        app.MapPost("/api/agent/checkin", async (CheckinRequest r, HttpContext ctx, AppDb db, SettingsService s, HealthService health, AgentBinary bin, Live live,
            JobSigningKey jobKey, SigningKeys fileKeys, TlsInfo tls, AuditService audit, Notifier notifier, TerminalBroker terminals) =>
        {
            if (await Auth(ctx, db) is not { } m) return Results.Unauthorized();
            m.TerminalCapable = r.Caps?.Contains(TerminalPolicy.Capability) == true;
            m.LiveLogCapable = r.Caps?.Contains(LiveLogPolicy.Capability) == true;
            // agentens melding om tillid (pinnet jobnøgle, afviste svar, https) – vises pr. maskine
            var trustBefore = (m.JobTrust, m.TrustError);
            AgentTrust.Record(m, r, ctx.Request.IsHttps, ctx.Connection.LocalIpAddress is null, jobKey.Fingerprint, DateTime.UtcNow);
            if (m.TrustError is { } terr && terr != trustBefore.TrustError)
            {
                await audit.Log($"agent:{m.Label}", "agent.trust_error", terr);
                notifier.Send($"🔴 {m.Label}: agenten afviste svar fra serveren\n{terr}");
            }
            if (m.JobTrust == AgentTrust.Pinned && trustBefore.JobTrust != AgentTrust.Pinned)
                await audit.Log($"agent:{m.Label}", "agent.job_key_pinned", r.Trust?.JobKeyId ?? "");
            var wasAway = m.LastSeen is null || m.LastSeen < DateTime.UtcNow.AddMinutes(-10);
            // Genstartet midt i et job: agenten genoptager det ikke, så luk det nu i stedet for efter 4 timer
            if (r.Facts.UptimeH is double up && up >= 0)
            {
                var boot = DateTime.UtcNow.AddHours(-up).AddMinutes(-5);   // oppetiden er afrundet til 0,1 time (±3 min)
                // en genstart, der nåede at ske, før agenten kunne melde tilbage (fx "Genstart nu" uden varsel), er lykkedes
                var rebooted = await db.Jobs.Where(j => j.MachineId == m.Id && j.Status == JobStatus.Running && j.Type == JobTypes.Reboot && j.Started != null && j.Started < boot)
                    .ExecuteUpdateAsync(x => x.SetProperty(j => j.Status, JobStatus.Success).SetProperty(j => j.Finished, DateTime.UtcNow).SetProperty(j => j.ExitCode, 0)
                        .SetProperty(j => j.Output, j => j.Output + "\n[Server] Tilbage med ny opstartstid – genstarten er lykkedes."));
                var lost = rebooted + await db.Jobs.Where(j => j.MachineId == m.Id && j.Status == JobStatus.Running && j.Type != JobTypes.Reboot && j.Started != null && j.Started < boot)
                    .ExecuteUpdateAsync(x => x.SetProperty(j => j.Status, JobStatus.Failed).SetProperty(j => j.Finished, DateTime.UtcNow)
                        .SetProperty(j => j.Output, j => j.Output + "\n[Server] Afbrudt: maskinen blev genstartet midt i jobbet."));
                if (lost > 0) await live.Changed("jobs", m.Id);
            }
            var f = r.Facts;
            var h = Json.Des<HealthReport>(m.HealthJson);
            var powerMoved = h.Live.PowerAt is { } was ? f.PowerAt is not { } now || Math.Abs((now - was).TotalSeconds) > 30 : f.PowerAt is not null;
            // Mac "Hold vågen på strøm": ny tilstand (strøm/batteri) vises med det samme, og en ændring fra menulinjen gemmes (sidste ændring vinder)
            powerMoved |= h.Live.KeepAwake?.State != f.KeepAwake?.State || h.Live.KeepAwake?.OnAc != f.KeepAwake?.OnAc;
            // temperaturmålingen slået til/fra (Mac: Funktioner i appen) – maskinens side følger med med det samme
            powerMoved |= h.Live.Temps?.Source != f.Temps?.Source;
            var keepAwakeFromMac = m.ReplacesId is null && r.KeepAwake is { } kc && KeepAwakeSync.FromMac(m, kc, DateTime.UtcNow);
            // tidslinjen: en periode uden kontakt og en ny opstart gemmes, før LastSeen og opstartstiden overskrives
            Timeline.OnCheckin(db, m, m.LastSeen, Timeline.BootOf(h.Live, m.LastSeen), Timeline.BootOf(f, DateTime.UtcNow), DateTime.UtcNow);
            h.Live = f;
            m.HealthJson = Json.Ser(h);
            // var den væk (mere end 2 min. uden check-in), starter en ny online-periode
            if (m.OnlineSince is null || m.LastSeen is null || m.LastSeen < DateTime.UtcNow.AddSeconds(-120)) m.OnlineSince = DateTime.UtcNow;
            m.LastSeen = DateTime.UtcNow; m.AgentVersion = r.AgentVersion; m.User = f.User;
            if (f.Ip is not null) m.Ip = f.Ip;
            if (f.Mac is not null) m.Mac = f.Mac;
            await db.SaveChangesAsync();
            await health.RecordMetric(m.Id, f);
            if (keepAwakeFromMac) await audit.Log($"agent:{m.Label}", "machine.keepawake", $"{m.Label}: hold vågen på strøm {(m.KeepAwakeOnAc ? "til" : "fra")} (ændret på Mac'en)");
            if (wasAway) { await health.Evaluate(m.Id); await live.Changed("machines", m.Id); }
            else if (powerMoved || keepAwakeFromMac) await live.Changed("machines", m.Id);

            var tokenId = CommandSigning.TokenId(ctx.Request.Headers.Authorization.ToString()[7..].Trim());
            // Terminaler leveres kun i det signerede svar til en agent med pinnet nøgle over en krypteret forbindelse
            var signedMode = await s.Get("signed_jobs");
            var channelOk = r.Nonce is { Length: >= 16 and <= 128 } && r.Trust?.JobKeyPinned == true;
            var tickets = channelOk && TerminalEligibility.Why(m, signedMode) is null ? terminals.TakeTickets(m.Id) : [];
            // live-log: samme signerede kanal, men egne billetter (en agent uden live-log får dem aldrig)
            var liveLogs = channelOk && LiveLogEligibility.Why(m, signedMode) is null ? terminals.TakeLiveLogs(m.Id) : [];
            var resp = await AgentCheckin.Build(db, s, bin, fileKeys, jobKey, tls, m, r, tokenId, Base(ctx, await s.Get("server_url")), tickets, liveLogs);
            return Results.Ok(resp);
        });

        // Fra ikonet i proceslinjen (lokal administrator har bedt om det): udvidelser og "opdatér nu"
        app.MapGet("/api/agent/extensions", async (HttpContext ctx, AppDb db, ExtensionService ext) =>
            await Auth(ctx, db) is { } m ? Results.Ok((await ext.For(m)).Select(e => new ExtensionView { Id = e.Id, Name = e.Name, Gives = e.Gives, Installed = e.Installed, Pending = e.Pending })) : Results.Unauthorized());
        app.MapPost("/api/agent/action", async (AgentActionRequest r, HttpContext ctx, AppDb db, JobService jobs, ExtensionService ext, AuditService audit, Live live) =>
        {
            if (await Auth(ctx, db) is not { } m) return Results.Unauthorized();
            switch (r.Action)
            {
                case "upgrade_all":
                    if (await db.Jobs.AnyAsync(j => j.MachineId == m.Id && j.Type == JobTypes.Upgrade && JobStatus.Active.Contains(j.Status)))
                        return Results.Ok(new { ok = true, note = "Opdatér alt er allerede i gang" });
                    await jobs.Create(m.Id, JobTypes.Upgrade, "Opdatér alle programmer", new JobParams(), RunAs.System, null, "ikon", "bruger på maskinen");
                    break;
                case "sync":
                    await db.Machines.Where(x => x.Id == m.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.InventoryRequested, true));
                    await live.Wake(m.Id);
                    break;
                case "extension":
                    try { await ext.Install(m, r.Id ?? "", "ikon", "bruger på maskinen"); }
                    catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException) { return Results.Problem(e.Message, statusCode: 400); }
                    break;
                case "power_now":
                    // "Genstart nu"/"Sluk nu" på skærmen: står i jobbet og revisionsloggen (PowerWatchService skriver selv i loggen)
                    await ctx.RequestServices.GetRequiredService<PowerWatchService>().UserChoseNow(m, r.Id);
                    return Results.Ok(new { ok = true });
                default: return Results.Problem("Ukendt handling", statusCode: 400);
            }
            await audit.Log($"agent:{m.Label}", "agent.action", $"{r.Action} {r.Id}".Trim());
            await live.Changed("jobs", m.Id);
            return Results.Ok(new { ok = true });
        });

        app.MapPost("/api/agent/health", async (HealthPost r, HttpContext ctx, AppDb db, HealthService health, RecoveryKeyVault vault, Live live, SettingsService s, ILogger<HealthService> log) =>
        {
            if (await Auth(ctx, db) is not { } m) return Results.Unauthorized();
            // fejlloggen gemmes i sin egen tabel (ikke i helbredets JSON); en fejl her må ikke stoppe resten
            if (r.Health.SysLog is { } sysLog)
            {
                try { await SysLogStore.Ingest(db, m.Id, sysLog, DateTime.UtcNow, log); }
                catch (Exception e) when (e is DbUpdateException or InvalidOperationException) { log.LogWarning("Fejllog fra {Machine}: {Msg}", m.Label, e.Message); }
                r.Health.SysLog = null;
            }
            var before = Json.Des<HealthReport>(m.HealthJson);
            Drives.Stamp(r.Health.Drives, before.Drives);
            // diskhelbred: sovende diske beholder deres tal, og stigende reallokerede sektorer/mediefejl får et tidspunkt
            DiskSmart.Track(r.Health.Disks, before.Disks, DateTime.UtcNow, await s.GetNum("alert_disk_temp", 70));
            r.Health.Batteries ??= before.Batteries;            // ældre agent / ikke SYSTEM: behold det sidst kendte
            r.Health.Printers ??= before.Printers;               // ældre agent / helbred uden printere: behold det sidst kendte
            r.Health.LocalPrinters ??= before.LocalPrinters;
            if (r.Health.NetPrinters is null) { r.Health.NetPrinters = before.NetPrinters; r.Health.NetPrintersAt = before.NetPrintersAt; }
            else if (r.Health.NetPrinters.Count > PrinterSeen.Max) r.Health.NetPrinters = r.Health.NetPrinters.Take(PrinterSeen.Max).ToList();
            // nye printere fundet af pc'en: siden Printere opdateres med det samme
            var netChanged = r.Health.NetPrintersAt != before.NetPrintersAt;
            // BitLocker: har agenten andre nøgler end Fleetdeck? Så skal den sende dem. Hashen gemmes ikke.
            var wanted = false;
            if (r.Health.Encryption is { KeysHash: { Length: > 0 } kh } enc)
            {
                wanted = enc.Kind == EncryptionKinds.BitLocker && kh != await vault.CurrentHash(m.Id, EncryptionKinds.BitLocker);
                enc.KeysHash = null;
            }
            ReportTracking.TrackOsUpdates(m, r.Health, DateTime.UtcNow);
            m.HealthJson = Json.Ser(r.Health);
            m.LastHealth = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await health.RecordMetric(m.Id, r.Health.Live);
            await health.Evaluate(m.Id);
            await live.Changed("machines", m.Id);
            if (netChanged) await live.Changed("printers");
            return Results.Ok(new HealthAck { EscrowWanted = wanted });
        });

        // Gendannelsesnøgler (BitLocker fra helbredet, FileVault fra jobbet "Ny gendannelsesnøgle"): gemmes krypteret, logges uden nøglen
        app.MapPost("/api/agent/recovery-keys", async (RecoveryKeyPost r, HttpContext ctx, AppDb db, RecoveryKeyVault vault, HealthService health, AuditService audit, Live live) =>
        {
            if (await Auth(ctx, db) is not { } m) return Results.Unauthorized();
            if (r.JobId is int jid && !await db.Jobs.AnyAsync(j => j.Id == jid && j.MachineId == m.Id && j.Type == JobTypes.FileVaultKey)) r.JobId = null;
            var res = await vault.Store(m.Id, r);
            if (res.Added > 0 || res.Superseded > 0 || res.Rejected > 0)
                await audit.Log($"agent:{m.Label}", RecoveryKeyVault.AuditEscrow,
                    $"{res.Added} ny(e), {res.Superseded} udskiftet, {res.Confirmed} uændret{(res.Rejected > 0 ? $", {res.Rejected} afvist (ugyldigt format)" : "")}: " +
                    string.Join(", ", r.Keys.Select(k => $"{RecoveryKeyVault.Name(k.Kind)} {k.Volume}").Distinct().Take(10)), m.Id);
            await health.Evaluate(m.Id);
            await live.Changed("machines", m.Id);
            return Results.Ok(new { res.Added, res.Confirmed, res.Superseded, res.Rejected });
        });

        // Netværksdrev, brugeren selv har koblet på: gem dem, så agenten holder dem forbundet
        app.MapPost("/api/agent/drives/found", async (FoundDrivesRequest r, HttpContext ctx, AppDb db, AuditService audit, Live live) =>
        {
            if (await Auth(ctx, db) is not { } m) return Results.Unauthorized();
            var n = await Drives.Adopt(db, m, r.Drives.Take(50));
            if (n > 0)
            {
                await audit.Log($"agent:{m.Label}", "drive.adopt", string.Join(", ", r.Drives.Select(d => $"\\\\{d.Host}\\{d.Share} ({d.Target})")));
                await live.Changed("machines", m.Id);
            }
            return Results.Ok(new { adopted = n });
        });

        // Programikoner: agenten spørger, hvilke der mangler, og sender kun dem
        app.MapPost("/api/agent/icons/missing", async (IconHashesRequest r, HttpContext ctx, AppDb db) =>
        {
            if (await Auth(ctx, db) is null) return Results.Unauthorized();
            var want = r.Hashes.Where(h => h is { Length: 32 } && h.All(Uri.IsHexDigit)).Select(h => h.ToLowerInvariant()).Distinct().Take(2000).ToList();
            var have = await db.SoftwareIcons.Where(i => want.Contains(i.Hash)).Select(i => i.Hash).ToListAsync();
            return Results.Ok(new IconHashesResponse { Missing = want.Except(have).ToList() });
        });
        app.MapPost("/api/agent/icons", async (IconUploadRequest r, HttpContext ctx, AppDb db) =>
        {
            if (await Auth(ctx, db) is null) return Results.Unauthorized();
            var saved = 0;
            foreach (var i in r.Icons.Take(SoftwareIcons.PerRequest))
            {
                byte[] png;
                try { png = Convert.FromBase64String(i.Png); } catch (FormatException) { continue; }
                // kun rigtige PNG-filer, der passer med deres id
                if (png.Length is 0 or > SoftwareIcons.MaxBytes || png.Length < 8 || png[0] != 0x89 || png[1] != (byte)'P' || png[2] != (byte)'N' || png[3] != (byte)'G') continue;
                var hash = SoftwareIcons.HashOf(png);
                if (hash != i.Hash.ToLowerInvariant() || await db.SoftwareIcons.AnyAsync(x => x.Hash == hash)) continue;
                db.SoftwareIcons.Add(new SoftwareIcon { Hash = hash, Png = png, Created = DateTime.UtcNow });
                saved++;
            }
            await db.SaveChangesAsync();
            return Results.Ok(new { saved });
        });

        app.MapPost("/api/agent/inventory", async (InventoryReport r, HttpContext ctx, AppDb db, ComplianceService comp, HealthService health, Live live, SettingsService s,
            ILogger<HealthService> log, Services.Bios.BiosCatalog bios, JobService jobs) =>
        {
            if (await Auth(ctx, db) is not { } m) return Results.Unauthorized();
            // ændringsloggen: sammenlign med det forrige inventar (første gang er grundlaget). En fejl her må ikke stoppe inventaret.
            var now = DateTime.UtcNow;
            if (m.LastInventory is not null)
                try
                {
                    var oldSw = await db.Software.AsNoTracking().Where(x => x.MachineId == m.Id).Select(x => new ChangeItem(x.Name, x.Version, x.Publisher)).ToListAsync();
                    ChangeLogStore.Add(db, m.Id, ChangeKinds.Program, oldSw, r.Software.Where(x => x.Name.Length > 0).Select(x => new ChangeItem(x.Name, x.Version, x.Publisher)).ToList(), now);
                }
                catch (Exception e) when (e is InvalidOperationException or DbUpdateException) { log.LogWarning("Ændringslog for {Machine}: {Msg}", m.Label, e.Message); }
            try { await ChangeLogStore.Drivers(db, m.Id, r.Drivers, now); }
            catch (Exception e) when (e is InvalidOperationException or DbUpdateException) { log.LogWarning("Drivere fra {Machine}: {Msg}", m.Label, e.Message); }
            await db.Software.Where(x => x.MachineId == m.Id).ExecuteDeleteAsync();
            await db.WingetIds.Where(x => x.MachineId == m.Id).ExecuteDeleteAsync();
            var oldUpgrades = await db.Upgrades.IgnoreQueryFilters().AsNoTracking().Where(x => x.MachineId == m.Id).ToListAsync();
            await db.Upgrades.IgnoreQueryFilters().Where(x => x.MachineId == m.Id).ExecuteDeleteAsync();
            db.Software.AddRange(r.Software.Where(x => x.Name.Length > 0).Select(x => new SoftwareItem
            {
                MachineId = m.Id, Name = x.Name, Version = x.Version, Publisher = x.Publisher, Scope = x.Scope,
                Description = SoftwareIcons.Clip(x.Description), InstallPath = SoftwareIcons.Clip(x.InstallPath, 400), Exe = SoftwareIcons.Clip(x.Exe, 400),
                IconHash = x.IconHash is { Length: 32 } h && h.All(Uri.IsHexDigit) ? h.ToLowerInvariant() : null,
                UninstallId = SoftwareIcons.Clip(x.UninstallId, 400),
            }));
            db.WingetIds.AddRange(r.WingetIds.Where(x => x.Length > 0).Distinct().Select(x => new MachineWingetId { MachineId = m.Id, Value = x }));
            var newUpgrades = r.Upgrades.Select(x => new UpgradeItem { MachineId = m.Id, Name = x.Name, WingetId = x.Id, Current = x.Current, Available = x.Available,
                Driver = x.Driver, Advice = SoftwareIcons.Clip(x.Advice, 200), Hidden = x.Hidden }).ToList();
            ReportTracking.CarryFirstSeen(newUpgrades, oldUpgrades, DateTime.UtcNow);
            await UpgradeMarks.ApplyInventory(db, m.Id, newUpgrades);   // skjult i Fleetdeck / kan ikke installeres følger med
            db.Upgrades.AddRange(newUpgrades);
            var oldFacts = Json.Des<SystemFacts>(m.FactsJson);
            m.FactsJson = Json.Ser(r.Facts);
            var prev = Json.Des<HealthReport>(m.HealthJson);
            r.Health.Remote ??= prev.Remote;                                   // inventaret har ikke fjernskrivebordet med
            if (r.Health.Drives.Count == 0) r.Health.Drives = prev.Drives;     // … og heller ikke netværksdrevene
            r.Health.Printers ??= prev.Printers;                               // … eller printerne
            r.Health.LocalPrinters ??= prev.LocalPrinters;
            if (r.Health.NetPrinters is null) { r.Health.NetPrinters = prev.NetPrinters; r.Health.NetPrintersAt = prev.NetPrintersAt; }
            r.Health.Tpm ??= prev.Tpm;                                         // … eller TPM'en (kun i helbredet som SYSTEM)
            r.Health.Batteries ??= prev.Batteries;
            DiskSmart.Track(r.Health.Disks, prev.Disks, now, await s.GetNum("alert_disk_temp", 70));
            ReportTracking.TrackOsUpdates(m, r.Health, DateTime.UtcNow);
            m.HealthJson = Json.Ser(r.Health);
            m.LastInventory = m.LastHealth = DateTime.UtcNow;
            m.InventoryRequested = false;
            await db.SaveChangesAsync();
            var created = await comp.Enforce(m.Id);
            await health.Evaluate(m.Id);
            // BIOS-status ud fra de nye fakta og det katalog, serveren allerede har (ingen netværk her); en fejl må aldrig vælte inventaret
            try { await bios.Rematch(db, [m.Id]); }
            catch (Exception e) when (e is DbUpdateException or InvalidOperationException) { ctx.RequestServices.GetRequiredService<ILogger<Services.Bios.BiosCatalog>>().LogWarning(e, "BIOS-status kunne ikke beregnes"); }
            // ny BIOS-version eller genstart efter en gendannelse: tag en kopi af indstillingerne (kun læsning)
            try { await Services.Bios.BiosSettingsService.AfterInventory(db, jobs, m, oldFacts, r.Facts, DateTime.UtcNow); }
            catch (Exception e) when (e is DbUpdateException or InvalidOperationException) { ctx.RequestServices.GetRequiredService<ILogger<Services.Bios.BiosCatalog>>().LogWarning(e, "BIOS-kopi kunne ikke bestilles"); }
            await live.Changed("machines", m.Id);
            return Results.Ok(new InventoryResponse { JobsCreated = created });
        });

        app.MapPost("/api/agent/jobs/{id:int}/start", async (int id, HttpContext ctx, AppDb db, Live live) =>
        {
            if (await Auth(ctx, db) is not { } m) return Results.Unauthorized();
            var n = await db.Jobs.Where(j => j.Id == id && j.MachineId == m.Id && (j.Status == JobStatus.Pending || j.Status == JobStatus.WaitingUser))
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Running).SetProperty(j => j.Started, DateTime.UtcNow)
                    .SetProperty(j => j.Progress, (int?)null).SetProperty(j => j.Step, "").SetProperty(j => j.ProgressAt, (DateTime?)null).SetProperty(j => j.StepSince, (DateTime?)null));
            if (n > 0) await live.Changed("jobs", m.Id);
            return Results.Ok(new JobStartResponse { Ok = n > 0 });
        });

        // Fremdrift mens jobbet kører (agenten sender højst hvert 4. sekund)
        app.MapPost("/api/agent/jobs/{id:int}/progress", async (int id, JobProgressPost r, HttpContext ctx, AppDb db, Live live) =>
        {
            if (await Auth(ctx, db) is not { } m) return Results.Unauthorized();
            var pct = r.Percent is { } p ? Math.Clamp(p, 0, 100) : (int?)null;
            var step = RecoveryKeys.Scrub(ReserveDns.Mask(PublicAccess.Mask((r.Step ?? "").Trim()))); if (step.Length > 300) step = step[..300];
            var n = await db.Jobs.Where(j => j.Id == id && j.MachineId == m.Id && j.Status == JobStatus.Running)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.Progress, pct).SetProperty(j => j.Step, step).SetProperty(j => j.ProgressAt, DateTime.UtcNow)
                    .SetProperty(j => j.StepSince, r.StepSince));
            if (n > 0) await live.Progress(id, m.Id, pct, step, r.StepSince);
            // "Opdatér alt": fjern opdaterede programmer fra listen med det samme, så man kan se det gå fremad
            if (r.Upgraded is { Count: > 0 } done)
            {
                var gone = await db.Upgrades.Where(u => u.MachineId == m.Id && done.Contains(u.WingetId)).ExecuteDeleteAsync();
                if (gone > 0) await live.Changed("machines", m.Id);
            }
            // agenten er i live, selv om den ikke tjekker ind, mens jobbet kører
            await db.Machines.Where(x => x.Id == m.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.LastSeen, DateTime.UtcNow));
            // Annulleret i Fleetdeck, mens det kørte: agenten stopper jobbet (og lukker installationen)
            var cancel = n == 0 && await db.Jobs.AnyAsync(j => j.Id == id && j.MachineId == m.Id && j.Status == JobStatus.Cancelled);
            return Results.Ok(new JobProgressResponse { Cancel = cancel });
        });

        app.MapPost("/api/agent/jobs/{id:int}/result", async (int id, JobResult r, HttpContext ctx, AppDb db, Notifier notifier, Live live) =>
        {
            if (await Auth(ctx, db) is not { } m) return Results.Unauthorized();
            var j = await db.Jobs.FirstOrDefaultAsync(x => x.Id == id && x.MachineId == m.Id);
            if (j is null) return Results.NotFound();
            // samme resultat igen (agenten sender igen, til den får svar): det er allerede gemt – kvittér uden at gøre noget to gange
            if (JobResults.AlreadyFinal(j)) return Results.Ok();
            var status = r.Status is JobStatus.Success or JobStatus.Failed or JobStatus.WaitingUser or JobStatus.Cancelled or JobStatus.Partial ? r.Status : JobStatus.Failed;
            // opdateringsmotoren: rundens strukturerede resultat (MaintenanceService læser det)
            if (j.Type == JobTypes.UpdateRound && r.Update is { } upd) j.ResultJson = Json.Ser(upd);
            // gendannelsesnøgler (BitLocker/FileVault) må aldrig stå i et jobs output
            j.Output = RecoveryKeys.Scrub(r.Output.Length > 60000 ? r.Output[^60000..] : r.Output);
            // offentlig adgang: tunnel-nøglen må aldrig stå i et jobs output
            if (j.Source is PublicAccess.Source or ReserveDns.Source) j.Output = await ctx.RequestServices.GetRequiredService<PublicAccessSettings>().Mask(j.Output);
            if (j.Source == ReserveDns.Source) j.Output = ReserveDns.Mask(j.Output);
            // iPads: backup-adgangskoden og overvågningsnøglen må heller ikke stå i outputtet
            if (j.Type == JobTypes.Ios) j.Output = await ctx.RequestServices.GetRequiredService<IpadSupervision>().Mask(j.Output);
            if (j.Status == JobStatus.Cancelled) status = JobStatus.Cancelled;   // annulleret fra Fleetdeck: det står fast
            if (status == JobStatus.WaitingUser) { j.Status = status; j.Started = null; }
            else { j.Status = status; j.Finished = DateTime.UtcNow; j.ExitCode = r.ExitCode; }
            await ComplianceService.RecordOutcome(db, j);   // overholdelsen husker det, også når "Ryd op" har slettet jobbet
            // debloat: gem tilstand og før-værdier fra motorens rapport (og håndhæv); rapportlinjen står ikke i historikken
            if (j.Source == DebloatService.Source && j.Type == JobTypes.Script && status != JobStatus.WaitingUser)
            {
                var clean = RecoveryKeys.Scrub(await ctx.RequestServices.GetRequiredService<DebloatService>().HandleResult(m, j, r.Output));
                j.Output = clean.Length > 60000 ? clean[^60000..] : clean;
            }
            // AMT-adgangskoden skal ikke ligge i jobbet bagefter (den er gemt krypteret på maskinen)
            if (j.Type is JobTypes.AmtActivate or JobTypes.AmtDeactivate && status != JobStatus.WaitingUser)
            {
                j.ParamsJson = "{}";
                if (j.Type == JobTypes.AmtDeactivate && status == JobStatus.Success) m.AmtPassword = null;
                m.LastHealth = null;   // hent ny status (helbred) med det samme
            }
            if (j.Type is JobTypes.Install or JobTypes.Uninstall or JobTypes.Upgrade or JobTypes.WindowsUpdate or JobTypes.UpdateRound or JobTypes.RemoveApp or JobTypes.WuHide && status != JobStatus.WaitingUser)
                m.InventoryRequested = true;
            // skjul/vis igen: ret listen med det samme – næste inventar bekræfter det
            if (j.Type == JobTypes.WuHide && status == JobStatus.Success && Json.Des<JobParams>(j.ParamsJson) is { UpdateIds: { Count: > 0 } hideIds, Hide: bool hide })
                await db.Upgrades.IgnoreQueryFilters().Where(u => u.MachineId == m.Id && hideIds.Contains(u.WingetId)).ExecuteUpdateAsync(x => x.SetProperty(u => u.Hidden, hide));
            // én opdatering, der ikke kan installeres (fx firmware, som maskinens firmware afviser): forklar det, og foreslå den ikke igen.
            // Lykkes den senere alligevel (fx efter en BIOS-opdatering), glemmes det.
            if (j.Type == JobTypes.Upgrade && status is JobStatus.Failed or JobStatus.Success && Json.Des<JobParams>(j.ParamsJson).WingetId is { Length: > 0 } upId)
            {
                if (status == JobStatus.Success) await UpgradeMarks.Unblock(db, m.Id, upId);
                else if (UpgradeMarks.CannotInstallReason(upId, r.Reason, j.Output) is { } why)
                {
                    var text = FwupdErrors.Text(why);
                    if (!j.Output.Contains(text, StringComparison.Ordinal)) j.Output = text + "\n\n" + j.Output;
                    await UpgradeMarks.Block(db, m.Id, upId, text, DateTime.UtcNow);
                }
            }
            // iPads via Mac'en: gem hvad Mac'en så, og opdatér de linkede iPads (overvåget, iPadOS, låst profil)
            var ipadsChanged = false;
            if (j.Type == JobTypes.Ios && r.Ios is { } ios && m.Platform == Platforms.Mac)
            {
                await IosInventory.Apply(db, m, ios);
                ipadsChanged = true;
            }
            // apps på en iPad via Mac'en (Apps-fanen): kun for den iPad, jobbet blev oprettet til, og kun hvis ECID'en passer
            if (j.Type == JobTypes.IosApps && m.Platform == Platforms.Mac && status != JobStatus.WaitingUser
                && Json.Des<JobParams>(j.ParamsJson) is { IpadId: int appsIpad } appsPrm && await db.Ipads.FindAsync(appsIpad) is { } appsP)
            {
                if (status == JobStatus.Success && r.IosApps is { } ar && Cfgutil.NormEcid(ar.Ecid) is { Length: > 0 } are
                    && are == Cfgutil.NormEcid(appsPrm.Ecid) && are == Cfgutil.NormEcid(appsP.Ecid))
                {
                    await IpadAppStore.Apply(db, appsP.Id, IpadAppStore.ViaMac, ar.Apps, DateTime.UtcNow);
                    ctx.RequestServices.GetRequiredService<AppStoreEngine>().Kick();
                }
                else if (status is JobStatus.Failed or JobStatus.Success)
                    await IpadAppStore.Failed(db, appsP.Id, status == JobStatus.Success ? "iPad'en på Mac'en passer ikke længere til denne iPad – link den igen" : r.Output);
                ipadsChanged = true;
            }
            // BIOS-indstillinger: gem kopien; engangsbilletten og adgangskoden (kun i hukommelsen) fjernes, når jobbet er slut
            if (j.Type is JobTypes.BiosSettingsRead or JobTypes.BiosSettingsApply && status != JobStatus.WaitingUser)
                await Services.Bios.BiosSettingsService.OnResult(db, ctx.RequestServices.GetRequiredService<Services.Bios.BiosSecrets>(), m, j, r, DateTime.UtcNow);
            // BIOS-opdatering: kørslens tilstand følger agentens melding (forudsætninger, klar, resultat)
            if (j.Type == JobTypes.BiosUpdate && status != JobStatus.WaitingUser)
                await Services.Bios.BiosUpdateService.OnResult(db, m, j, r, DateTime.UtcNow);
            // USB-stik til BIOS: diskene på pc'en eller resultatet af skrivningen (guidens session i serverens hukommelse)
            if (j.Type is JobTypes.UsbDisks or JobTypes.BiosUsbWrite && status != JobStatus.WaitingUser)
                await ctx.RequestServices.GetRequiredService<Services.Bios.BiosUsbSessions>().OnResult(db, j, r, status);
            // iPad via USB: engangsbilletten skal ikke ligge i jobbet bagefter; nye oplysninger om iPad'en gemmes som fra vagten
            if (j.Type == JobTypes.UsbIpad && status != JobStatus.WaitingUser)
            {
                var up = Json.Des<JobParams>(j.ParamsJson);
                if (up.Ticket is not null) { up.Ticket = null; j.ParamsJson = Json.Ser(up); }
                if (r.Ios is { } usb)
                {
                    var store = ctx.RequestServices.GetRequiredService<UsbIpadStore>();
                    foreach (var d in usb.Devices) store.Merge(m.Id, d);
                    await UsbIpadLink.Apply(db, usb.Devices, DateTime.UtcNow);
                    ipadsChanged = true;
                }
            }
            // genstart/sluk er først lykkedes, når maskinen er tilbage (eller slukket): vagten følger jobbet (Services/PowerWatch.cs)
            if (j.Status == JobStatus.Success) PowerWatchService.StartWatch(j, m, r, DateTime.UtcNow);
            // planlagt genstart/slukning: vis nedtællingen med det samme (check-in holder den ajour bagefter)
            var powerChanged = j.Type is JobTypes.Reboot or JobTypes.Shutdown or JobTypes.CancelPower && status == JobStatus.Success;
            if (powerChanged)
            {
                var h = Json.Des<HealthReport>(m.HealthJson);
                h.Live.PowerAt = r.PowerAt; h.Live.PowerMode = r.PowerAt is null ? null : r.PowerMode;
                m.HealthJson = Json.Ser(h);
            }
            await db.SaveChangesAsync();
            // "Frigiv helt" er lykkedes (iPad'en er gendannet uden overvågning): Apple-styring, AdGuard og Fleetdeck glemmer den
            if (status == JobStatus.Success && j.Type is JobTypes.Ios or JobTypes.UsbIpad
                && Json.Des<JobParams>(j.ParamsJson) is { Action: IosActions.Release, IpadId: int releasedId })
            {
                await IpadRelease.Finish(ctx.RequestServices, db, releasedId, m.Label);
                ipadsChanged = true;
            }
            if (powerChanged) await live.Changed("machines", m.Id);
            if (ipadsChanged) await live.Changed("ipads");
            if (status == JobStatus.Failed && j.Source != "manual") notifier.Send($"🟠 {m.Label}: job fejlede – {j.Title}");
            await live.Changed("jobs", m.Id);
            return Results.Ok();
        });

        // Linux: genskab-kit (pakker, opsætning, tjenester). Gemmes i WinSetup\Backup\<NAVN>\linux, de 7 seneste.
        app.MapPost("/api/agent/backup", async (HttpContext ctx, AppDb db, Paths paths, AuditService audit, Live live) =>
        {
            if (await Auth(ctx, db) is not { } m) return Results.Unauthorized();
            var dir = Path.Combine(paths.WinSetup, "Backup", m.Name, "linux");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"kit-{DateTime.Now:yyyyMMdd-HHmmss}.tar.gz");
            await using (var fs = File.Create(file)) await ctx.Request.Body.CopyToAsync(fs);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);   // indeholder adgangsoplysninger
            foreach (var old in new DirectoryInfo(dir).GetFiles("kit-*.tar.gz").OrderByDescending(f => f.Name).Skip(7)) old.Delete();
            var size = new FileInfo(file).Length;
            await File.WriteAllTextAsync(Path.Combine(paths.WinSetup, "Backup", m.Name, "backup.json"),
                Json.Ser(new Dictionary<string, object> { ["Dato"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm"), ["Computer"] = m.Name, ["Type"] = "linux-kit",
                    ["Fil"] = Path.GetFileName(file), ["MB"] = Math.Round(size / 1048576.0, 1) }));
            await audit.Log("agent", "backup.linux", $"{m.Label}: {size / 1048576.0:0.#} MB");
            await live.Changed("machines", m.Id);
            return Results.Ok();
        });

        app.MapGet("/api/agent/backup/latest", async (HttpContext ctx, AppDb db, Paths paths) =>
        {
            if (await Auth(ctx, db) is not { } m) return Results.Unauthorized();
            var dir = Path.Combine(paths.WinSetup, "Backup", m.Name, "linux");
            var f = Directory.Exists(dir) ? new DirectoryInfo(dir).GetFiles("kit-*.tar.gz").OrderByDescending(x => x.Name).FirstOrDefault() : null;
            return f is null ? Results.NotFound() : Results.File(f.FullName, "application/gzip", f.Name);
        });

        app.MapGet("/api/agent/file/{pid:int}", async (int pid, HttpContext ctx, AppDb db, WinSetupStore store) =>
        {
            if (await Auth(ctx, db) is null) return Results.Unauthorized();
            var p = await db.Packages.FindAsync(pid);
            if (p is not { Type: PackageTypes.File } || p.Identifier.Length == 0) return Results.NotFound();
            return store.SafeFile("Installers", p.Identifier) is { } path
                ? Results.File(path, "application/octet-stream", Path.GetFileName(path), enableRangeProcessing: true) : Results.NotFound();
        });
    }
}
