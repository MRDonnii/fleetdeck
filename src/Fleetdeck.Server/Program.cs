using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Endpoints;
using Fleetdeck.Server.Hubs;
using Fleetdeck.Server.Services;

if (args.Contains("--healthcheck"))   // bruges af Docker HEALTHCHECK
{
    try
    {
        using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        var r = await hc.GetAsync($"http://127.0.0.1:{Environment.GetEnvironmentVariable("PORT") ?? "8585"}/api/health");
        // svaret (med versionen) skrives ud, så opdateringsjobbet på Docker-værten kan læse det med docker exec
        if (r.IsSuccessStatusCode) Console.WriteLine(await r.Content.ReadAsStringAsync());
        return r.IsSuccessStatusCode ? 0 : 1;
    }
    catch { return 1; }
}

var paths = new Paths();
Directory.CreateDirectory(paths.Data);
// Glemt administrator-adgangskode: dotnet Fleetdeck.Server.dll reset-admin [brugernavn] (se docs/backup.md)
if (args.FirstOrDefault() == "reset-admin") return await AdminReset.Cli(args[1..], paths);
if (args.FirstOrDefault() == "kontrolrum-sso") return await KontrolrumSsoCli.Run(args[1..], paths);
// En gendannelse fra backup lægges ind nu – før databasen og nøglerne åbnes
SelfBackup.ApplyPending(paths.Data, ThisVersion.Full, m => Console.WriteLine("[backup] " + m));

var builder = WebApplication.CreateBuilder(args);

var port = Environment.GetEnvironmentVariable("PORT") ?? "8585";
var urls = Environment.GetEnvironmentVariable("URLS") ?? $"http://0.0.0.0:{port}";
// Agenten på selve værten (fx Unraid) kan ikke nå containerens egen IP på et macvlan-net: den taler i stedet gennem
// en socket-fil i datamappen (/data/fleetdeck.sock = /mnt/user/appdata/fleetdeck/fleetdeck.sock på værten). AGENT_SOCKET=0 slår den fra.
var socket = Environment.GetEnvironmentVariable("AGENT_SOCKET") is { } sk ? (sk == "0" ? null : sk) : Path.Combine(paths.Data, "fleetdeck.sock");
// Valgfri https (HTTPS_PORT): ved siden af http, så agenter på http ikke mister forbindelsen (se docs/sikkerhed-agent.md)
var tls = TlsInfo.Load(paths);
var explicitListen = socket is not null || tls.Certificate is not null;
builder.WebHost.ConfigureKestrel(k =>
{
    k.Limits.MaxRequestBodySize = 4L * 1024 * 1024 * 1024;   // store installationsfiler
    if (!explicitListen) return;
    foreach (var u in urls.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(x => new Uri(x.Replace("://*", "://0.0.0.0").Replace("://+", "://0.0.0.0"))))
        k.Listen(System.Net.IPAddress.TryParse(u.Host, out var ip) ? ip : System.Net.IPAddress.Any, u.Port);
    // certifikatet læses ved hver forbindelse, så ESP-flash kan give det et navn mere uden genstart (samme nøgle)
    if (tls.Certificate is not null)
        k.ListenAnyIP(tls.HttpsPort!.Value, o => o.UseHttps(new Microsoft.AspNetCore.Server.Kestrel.Https.HttpsConnectionAdapterOptions
            { ServerCertificateSelector = (_, _) => tls.Certificate }));
    if (socket is null) return;
    try { File.Delete(socket); } catch (IOException) { }
    k.ListenUnixSocket(socket);
});
if (!explicitListen) builder.WebHost.UseUrls(urls);
builder.Services.AddSingleton(tls);

builder.Services.AddSingleton(paths);
builder.Services.AddDbContext<AppDb>(o => o.UseSqlite($"Data Source={Path.Combine(paths.Data, "fleetdeck.db")}"));
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never;
});
builder.Services.AddSignalR().AddJsonProtocol();
builder.Services.AddHttpClient("webhook", c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddHttpClient(OwnNetwork.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(3));
builder.Services.AddHttpClient("homeassistant", c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddHttpClient("os", c => { c.Timeout = Timeout.InfiniteTimeSpan; c.DefaultRequestHeaders.UserAgent.ParseAdd("Fleetdeck/2.0"); });
builder.Services.AddHttpClient("update", c => { c.Timeout = TimeSpan.FromSeconds(20); c.DefaultRequestHeaders.UserAgent.ParseAdd("Fleetdeck/" + ThisVersion.Value); });
builder.Services.AddHttpClient("winget", c => { c.Timeout = TimeSpan.FromMinutes(3); c.DefaultRequestHeaders.UserAgent.ParseAdd("Fleetdeck/2.0"); });
builder.Services.AddHttpClient("appwatch", c => { c.Timeout = TimeSpan.FromSeconds(15); c.DefaultRequestHeaders.UserAgent.ParseAdd("Fleetdeck/" + ThisVersion.Value); });
builder.Services.AddHttpClient("esp", c => { c.Timeout = TimeSpan.FromSeconds(60); c.DefaultRequestHeaders.UserAgent.ParseAdd("Fleetdeck/" + ThisVersion.Value); })
    // omdirigeringer følges af EspFetch selv, så en katalog-nøgle aldrig sendes videre til en anden vært
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = System.Net.DecompressionMethods.All });
// BIOS-kataloger og -pakker: omdirigeringer følges kun inden for leverandørernes værter (BiosHttp), så klienten følger dem ikke selv
builder.Services.AddHttpClient(Fleetdeck.Server.Services.Bios.BiosHttp.ClientName, c => { c.Timeout = TimeSpan.FromMinutes(10); c.DefaultRequestHeaders.UserAgent.ParseAdd("Fleetdeck/" + ThisVersion.Value); })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = System.Net.DecompressionMethods.All });
builder.Services.AddHttpClient("appstore", c => { c.Timeout = TimeSpan.FromSeconds(20); c.DefaultRequestHeaders.UserAgent.ParseAdd("Fleetdeck/2.0"); });

builder.Services.AddScoped<SettingsService>();
builder.Services.AddScoped<SetupService>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<RecoveryKeyVault>();
builder.Services.AddSingleton<ReauthTokens>();
builder.Services.AddSingleton<TerminalGate>();
builder.Services.AddSingleton<TerminalVault>();
builder.Services.AddSingleton<TerminalBroker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TerminalBroker>());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<TwoFactorService>();
builder.Services.AddSingleton<KontrolrumSso>();
builder.Services.AddSingleton<ApiKeyGuard>();
builder.Services.AddScoped<JobService>();
builder.Services.AddScoped<HomeAssistantApi>();
builder.Services.AddScoped<PowerWatchService>();
builder.Services.AddScoped<HelpdeskService>();
builder.Services.AddScoped<AppWatchService>();
builder.Services.AddScoped<ExtensionService>();
builder.Services.AddScoped<ComplianceService>();
builder.Services.AddScoped<ProvisionService>();
builder.Services.AddScoped<HealthService>();
builder.Services.AddScoped<HelperService>();
builder.Services.AddScoped<SeedService>();
builder.Services.AddScoped<DeployService>();
builder.Services.AddScoped<MaintenanceService>();
builder.Services.AddScoped<DebloatService>();
builder.Services.AddScoped<UpdateService>();
builder.Services.AddSingleton<OsLibrary>();
builder.Services.AddSingleton<PiImageLibrary>();
builder.Services.AddSingleton<Fleetdeck.Server.Services.Bios.BiosCatalog>();
builder.Services.AddSingleton<Fleetdeck.Server.Services.Bios.BiosPackages>();
builder.Services.AddSingleton<Fleetdeck.Server.Services.Bios.BiosSecrets>();
builder.Services.AddSingleton(_ => new Fleetdeck.Server.Services.Bios.BiosUsbSessions());
builder.Services.AddHostedService<Fleetdeck.Server.Services.Bios.BiosCatalogService>();
builder.Services.AddScoped<PiCardService>();
builder.Services.AddScoped<PiNetboot>();
builder.Services.AddScoped<SpareCardService>();
builder.Services.AddSingleton<TunnelInside>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TunnelInside>());
builder.Services.AddScoped<MachineViews>();
builder.Services.AddScoped<AmtService>();
builder.Services.AddScoped<FamilySettings>();
builder.Services.AddScoped<LockedProfiles>();
builder.Services.AddScoped<IpadSupervision>();
builder.Services.AddSingleton<UsbIpadStore>();
builder.Services.AddScoped<PublicAccessSettings>();
builder.Services.AddScoped<ReserveDnsSettings>();
builder.Services.AddScoped<MdmSettings>();
builder.Services.AddScoped<EmergencySheetService>();
builder.Services.AddScoped<Fleetdeck.Server.Services.Reports.ReportScheduler>();
builder.Services.AddScoped<ISupervisionIdentitySource, HouseholdSupervisionIdentity>();
builder.Services.AddScoped<MdmService>();
builder.Services.AddSingleton<MdmEngine>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MdmEngine>());
builder.Services.AddSingleton<IpadEngine>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IpadEngine>());
builder.Services.AddSingleton<AppStoreEngine>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AppStoreEngine>());
builder.Services.AddSingleton<Live>();
builder.Services.AddSingleton<Notifier>();
builder.Services.AddSingleton<WinSetupStore>();
builder.Services.AddSingleton<AgentBinary>();
builder.Services.AddSingleton<SigningKeys>();
builder.Services.AddSingleton<JobSigningKey>();
builder.Services.AddScoped<EnrollmentService>();
builder.Services.AddScoped<AccessRequestService>();
builder.Services.AddSingleton<AccessRequestMemory>();
builder.Services.AddHostedService<HomeAssistantBridge>();
builder.Services.AddSingleton<Fleetdeck.Server.Services.Printers.PrinterDiscovery>();
builder.Services.AddHostedService<Fleetdeck.Server.Services.Printers.PrinterMonitor>();
builder.Services.AddHostedService<Fleetdeck.Server.Services.Printers.PrinterAutoSearch>();
builder.Services.AddSingleton<WingetIndex>();
builder.Services.AddHostedService<BackgroundWorker>();
builder.Services.AddHostedService<DiscoveryResponder>();
builder.Services.AddSingleton<CheckRunner>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CheckRunner>());
builder.Services.AddSingleton<EspFirmwareService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EspFirmwareService>());
builder.Services.AddScoped<EspWifi>();
builder.Services.AddScoped<EspCatalogSecret>();
builder.Services.AddSingleton<SelfBackupService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SelfBackupService>());

builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(paths.Data, "keys"))).SetApplicationName("Fleetdeck");
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.Cookie.Name = Csrf.SessionCookie;
    o.Cookie.HttpOnly = true;
    // Lax + Path=/ så Kontrolrum kan vise siden (via sin proxy) og logge ind automatisk. Secure kun over https.
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.Cookie.Path = "/";
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    // 7 dage, der forlænges ved brug: en aktiv bruger mærker intet, en glemt eller kopieret cookie udløber
    o.ExpireTimeSpan = SessionLifetime.Days;
    o.SlidingExpiration = true;
    o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
    o.Events.OnValidatePrincipal = async c =>
    {
        // Ugyldiggør sessioner når adgangskoden skiftes eller brugeren slettes
        var db = c.HttpContext.RequestServices.GetRequiredService<AppDb>();
        var id = int.TryParse(c.Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var i) ? i : 0;
        var stamp = c.Principal?.FindFirstValue("stamp");
        var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (u is null || u.SecurityStamp != stamp) { c.RejectPrincipal(); await c.HttpContext.SignOutAsync(); }
    };
});
// Alle politikker kræver totrinsbekræftelse, når politikken siger det (TwoFactorRequirement) – undtagen "session",
// som kun bruges til /me, opsætning af totrinsbekræftelse og log ud.
builder.Services.AddAuthorizationBuilder()
    .SetDefaultPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().AddRequirements(new TwoFactorRequirement()).Build())
    .AddPolicy("admin", p => p.RequireRole(Roles.Admin).AddRequirements(new TwoFactorRequirement()))
    .AddPolicy("user", p => p.RequireRole(Roles.Admin, Roles.Viewer).AddRequirements(new TwoFactorRequirement()))
    .AddPolicy("family", p => p.RequireRole(Roles.Admin, Roles.Parent).AddRequirements(new TwoFactorRequirement()))
    .AddPolicy("any", p => p.RequireAuthenticatedUser().AddRequirements(new TwoFactorRequirement()))
    .AddPolicy("session", p => p.RequireAuthenticatedUser());
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "?",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 8, Window = TimeSpan.FromMinutes(5) }));
    // Nødkortet: få forsøg pr. IP (rate-limiteren kører før login-tjekket), så en stjålen session ikke kan gætte adgangskoden
    o.AddPolicy(EmergencyAccess.RateLimitPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
        "emergency|" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "?"),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(10) }));
    // Login fra Kontrolrum (signeret SSO): egen grænse pr. klient-IP
    KontrolrumSso.AddRateLimit(o);
    // Terminal: nye sessioner pr. klient, så en stjålen session ikke kan åbne skaller i massevis
    o.AddPolicy(TerminalSecurity.RateLimitPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
        "terminal|" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "?"),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) }));
    // "Anmod om adgang" (uden nøgle): få nye anmodninger pr. IP og et loft over, hvor ofte der spørges
    AccessRequestService.AddRateLimits(o);
});

var app = builder.Build();

// Database: kør migrationer automatisk ved opstart
using (var scope = app.Services.CreateScope())
{
    var sp = scope.ServiceProvider;
    var migrateDb = sp.GetRequiredService<AppDb>();
    await SelfBackupService.BeforeMigrate(migrateDb, paths.Data, ThisVersion.Full, app.Logger);
    await migrateDb.Database.MigrateAsync();
    await AdminReset.FromFile(migrateDb, paths, app.Logger);
    var settings = sp.GetRequiredService<SettingsService>();
    if (string.IsNullOrEmpty(await settings.Get("enroll_key"))) await settings.Set("enroll_key", Crypto.Token(18));
    // nøglen til signerede jobs laves ved første start (data/keys/job-signing.pem)
    app.Logger.LogInformation("Jobnøgle {Fp}", sp.GetRequiredService<JobSigningKey>().Fingerprint);
    if (tls.Error is { } tlsErr) app.Logger.LogError("https er slået fra: {Msg}", tlsErr);
    else if (tls.Pin is { } tlsPin) app.Logger.LogInformation("https på port {Port}, TLS-nøgle {Pin}", tls.HttpsPort, tlsPin);
    var db = sp.GetRequiredService<AppDb>();
    if (!await db.Packages.AnyAsync() && File.Exists(Path.Combine(paths.WinSetup, "apps.json")))
        await sp.GetRequiredService<SeedService>().ImportAppsJson("system");
    // SERVER_URL (miljøvariabel) bruges som adresse, hvis den ikke er sat i Indstillinger endnu
    // SERVER_URL (miljøvariabel/Unraid-skabelon): bruges når den er ny eller ændret – ellers gælder Indstillinger.
    // Agenterne får adressen ved hvert check-in og flytter selv med, hvis den ændres.
    if (Environment.GetEnvironmentVariable("SERVER_URL") is { Length: > 0 } envUrl)
    {
        envUrl = envUrl.TrimEnd('/');
        if ((await settings.Get("server_url")).Length == 0 || await settings.Get("server_url_env") != envUrl)
        {
            await settings.Set("server_url", envUrl);
            await settings.Set("server_url_env", envUrl);
        }
    }
    // Programkataloget (de populære gratis programmer) lægges i Pakker første gang
    if (await settings.Get("catalog_imported") != "1")
    {
        await AppCatalog.Import(db, sp.GetRequiredService<AuditService>(), "system");
        await settings.Set("catalog_imported", "1");
    }
    var url = await settings.Get("server_url");
    if (url.Length > 0)
        sp.GetRequiredService<WinSetupStore>().PublishAgent(url, await settings.Get("enroll_key"), sp.GetRequiredService<AgentBinary>().ExePath);
    await sp.GetRequiredService<DeployService>().Sync();
    MdmPublicHost.Set(await settings.Get("mdm_public_host"));
    TwoFactorPolicy.Current = await settings.Get(TwoFactorPolicy.SettingKey);
    await TerminalDefaults.MarkProtected(migrateDb, settings, sp.GetRequiredService<AuditService>());
    await TerminalDefaults.SeedSnippets(migrateDb, settings);
    // Den gamle Kontrolrum-nøgle (klartekst) flyttes til API-nøglen "Kontrolrum" (kun hash) – Kontrolrum kalder videre med samme nøgle
    if (await ApiKeyMigration.FromKontrolrumKey(migrateDb, settings, DateTime.UtcNow))
        app.Logger.LogInformation("Den gamle Kontrolrum-nøgle er flyttet til API-nøglen \"{Name}\"", ApiKeyMigration.MigratedName);
    if (TrustedProxies.Set(await settings.Get(TrustedProxies.SettingKey)) is { } proxyErr) app.Logger.LogWarning("Betroede proxyer: {Err}", proxyErr);
    // efter en opdatering af Fleetdeck: skriv resultatet i loggen
    try { await sp.GetRequiredService<UpdateService>().Settle(); }
    catch (Exception e) when (e is not OperationCanceledException) { app.Logger.LogWarning(e, "Opdateringsstatus kunne ikke læses"); }
}

// TCP-modparten, før X-Forwarded-For læses: kun en Cloudflare-tunnel fra hjemmenettet/containeren må regnes hjemme (se HomeNetwork)
app.Use((ctx, next) => { ctx.Items[HomeNetwork.PeerKey] = ctx.Connection.RemoteIpAddress; return next(); });
// Apple-styring: på den offentlige MDM-adresse (gennem tunnelen) svarer Fleetdeck kun på tilmeldingslinks – aldrig brugerfladen eller /api.
// Skal køre FØR UseForwardedHeaders: ellers kan en klient på den offentlige adresse sende sin egen X-Forwarded-Host (så Host
// skiftes og filteret ikke genkender adressen) eller X-Forwarded-Prefix: /enroll (så /enroll/api/... bliver til /api/...).
app.Use((ctx, next) =>
{
    if (MdmPublicHost.IsPublic(ctx.Request) &&
        (!MdmPublicHost.Allowed(ctx.Request.Path.Value) || ctx.Request.Headers.ContainsKey("X-Forwarded-Prefix")))
    { ctx.Response.StatusCode = 404; return Task.CompletedTask; }
    return next();
});
// X-Forwarded-For læses kun fra betroede proxyer (rate limits og audit); proto/host/prefix som før (se ProxyHeaders)
app.UseProxyHeaders();
// Under en understi (fx Kontrolrums /open/fleetdeck/): X-Forwarded-Prefix bliver PathBase. Fjerner proxyen ikke selv
// præfikset fra stien, gør vi det her, så ruterne virker begge veje.
app.Use((ctx, next) =>
{
    if (ctx.Request.PathBase.HasValue && ctx.Request.Path.StartsWithSegments(ctx.Request.PathBase, out var rest)) ctx.Request.Path = rest;
    // Må vises i en iframe fra Kontrolrum (ingen X-Frame-Options)
    ctx.Response.Headers.ContentSecurityPolicy = "frame-ancestors " + Ui.FrameAncestors;
    ctx.Response.Headers.XContentTypeOptions = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "same-origin";
    return next();
});
app.Use(Csrf.Middleware);
// index.html med <base href> der passer til understien (resten af siden bruger relative adresser)
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path == "/" || ctx.Request.Path == "/index.html") { await Ui.Index(ctx); return; }
    await next();
});
// index.html må aldrig caches (så en ny version vises med det samme); filerne i assets/ har hash i navnet og kan caches længe
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = c =>
        c.Context.Response.Headers.CacheControl = c.File.Name.Equals("index.html", StringComparison.OrdinalIgnoreCase)
            ? "no-cache, no-store, must-revalidate" : c.Context.Request.Path.StartsWithSegments("/assets") ? "public, max-age=31536000, immutable" : "no-cache",
});
app.UseRouting();   // efter præfikset er fjernet fra stien, så ruterne findes under understien
app.UseRateLimiter();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
// Windows PowerShell 5.1 (Invoke-RestMethod) sender JSON i Latin-1 uden at sige det: æøå bliver til "�".
// Er en lille JSON-forespørgsel ikke gyldig UTF-8, omsættes den fra Latin-1, før den læses.
app.Use(async (ctx, next) =>
{
    var req = ctx.Request;
    if (req.Path.StartsWithSegments("/api") && !req.Path.StartsWithSegments("/api/mdm/hook") && (HttpMethods.IsPost(req.Method) || HttpMethods.IsPut(req.Method) || HttpMethods.IsPatch(req.Method)))
    {
        if (req.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true && req.ContentLength is > 0 and < 1_000_000)
        {
            using var ms = new MemoryStream();
            await req.Body.CopyToAsync(ms);
            var raw = ms.ToArray();
            if (!Fleetdeck.Shared.TextCodec.IsUtf8(raw)) raw = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.Latin1.GetString(raw));
            req.Body = new MemoryStream(raw);
            req.ContentLength = raw.Length;
        }
    }
    await next();
});
app.UseAuthentication();
app.UseAuthorization();

// "app": markøren, "Find automatisk" genkender serveren på, når den leder på det lokale net (Discovery.FleetdeckHealthVersion)
app.MapGet("/api/health", () => Results.Ok(new { ok = true, version = ThisVersion.Value, release = ThisVersion.Full, app = Fleetdeck.Shared.Discovery.HealthApp }));
app.MapAuthEndpoints();
app.MapSetupEndpoints();
app.MapTwoFactorEndpoints();
app.MapAdminEndpoints();
app.MapPiCardEndpoints();
app.MapBiosEndpoints();
app.MapSpareCardEndpoints();
app.MapSiteConfigEndpoints();
app.MapReportEndpoints();
app.MapAgentEndpoints();
app.MapSecurityEndpoints();
app.MapAccessRequestEndpoints();
app.MapHelperEndpoints();
app.MapDeployEndpoints();
app.MapKontrolrumEndpoints();
app.MapApiKeyEndpoints();
app.MapCheckEndpoints();
app.MapIpadEndpoints();
app.MapUsbIpadEndpoints();
app.MapIpadNoWipeEndpoints();
app.MapPublicAccessEndpoints();
app.MapReserveDnsEndpoints();
app.MapPrinterEndpoints();
app.MapMdmEndpoints();
app.MapStatusPageEndpoints();
app.MapEmergencyEndpoints();
app.MapUpdateEndpoints();
app.MapBackupEndpoints();
app.MapDebloatEndpoints();
app.MapEspEndpoints();
app.MapKeepAwakeEndpoints();
app.MapPowerEndpoints();
app.MapHelpdeskEndpoints();
app.MapAppWatchEndpoints();
app.MapAuditEndpoints();
app.MapEncryptionEndpoints();
app.MapTerminalEndpoints();
app.MapSysLogEndpoints();
app.MapTimelineEndpoints();
app.MapHub<UiHub>("/hubs/ui");
app.MapHub<AgentHub>("/hubs/agent");
app.MapFallback(async ctx =>
{
    if (ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/hubs")) { ctx.Response.StatusCode = 404; return; }
    await Ui.Index(ctx);
});

await app.RunAsync();
return SelfBackupService.ExitCode;   // 75 = genstart for at lægge en gendannelse ind

/// <summary>Websiden: index.html med &lt;base href&gt; for understien og tilladte iframe-forældre.</summary>
public static class Ui
{
    public static readonly string FrameAncestors = Environment.GetEnvironmentVariable("FRAME_ANCESTORS")
        ?? "'self'";   // fx FRAME_ANCESTORS="'self' http://dashboard.local" for at vise siden indlejret i et andet dashboard
    private static string? _html;
    private static DateTime _mtime;

    public static async Task Index(HttpContext ctx)
    {
        var file = Path.Combine(ctx.RequestServices.GetRequiredService<IWebHostEnvironment>().WebRootPath ?? "wwwroot", "index.html");
        if (!File.Exists(file)) { ctx.Response.StatusCode = 404; return; }
        var mt = File.GetLastWriteTimeUtc(file);
        if (_html is null || mt != _mtime) { _html = await File.ReadAllTextAsync(file); _mtime = mt; }
        var prefix = ctx.Request.PathBase.Value ?? "";
        if (!System.Text.RegularExpressions.Regex.IsMatch(prefix, "^(/[A-Za-z0-9._~-]+)*$")) prefix = "";
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        await ctx.Response.WriteAsync(_html.Replace("<base href=\"/\" />", $"<base href=\"{prefix}/\" />"));
    }
}

public static class ThisVersion
{
    public static readonly string Value = typeof(ThisVersion).Assembly.GetName().Version?.ToString(3) ?? "2.0.0";
    /// <summary>Hele versionen fra Directory.Build.props, fx 3.16.0-beta.1 (uden "+commit").</summary>
    public static readonly string Full =
        (System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(ThisVersion).Assembly)?.InformationalVersion
            ?.Split('+')[0]) is { Length: > 0 } v && Fleetdeck.Server.Services.SemVer.Parse(v) is not null ? v : Value;
}
