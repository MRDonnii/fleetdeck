using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.AspNetCore.SignalR.Client;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Windows;

/// <summary>Agentens hovedløkke: tilmelding, live-forbindelse, check-in, helbred, inventar, jobs og selvopdatering.</summary>
public sealed class AgentWorker(AgentOptions opt, IHttpClientFactory httpFactory, Collectors collectors, Winget winget, JobRunner jobs,
    DriveMapper drives, PrinterKeeper printers, TrayBridge tray, HelpdeskBridge helpdesk, LogCollector logs, IHostApplicationLifetime life, ILogger<AgentWorker> log) : BackgroundService
{
    private readonly SemaphoreSlim _wake = new(0, 1);
    private HubConnection? _hub;
    private string _myVersion = "";

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        Directory.CreateDirectory(opt.Work);
        CleanupOldBinary();
        _myVersion = HashOfSelf();
        var cfg = AgentConfig.Load(opt.ConfigFile) ?? throw new InvalidOperationException($"Ingen konfiguration i {opt.ConfigFile}. Kør 'install' først.");
        var http = httpFactory.CreateClient("server");
        log.LogInformation("Fleetdeck-agent {Version} starter (server {Server}, SYSTEM={System}, testtilstand={Dry})", _myVersion, cfg.Server, AgentOptions.IsSystem, opt.DryRun);

        // ESP Flasher på denne pc: http://localhost:<port> er en sikker side for Chrome og Edge – uden certifikat (se LocalWeb)
        // /_fd/: USB-stik med BIOS-opdateringen (claim virker, når agenten er tilmeldt – se jobs.BiosUsbClaim nedenfor)
        if (opt.IsService && !opt.Once && !opt.DryRun)
            _ = Task.Run(() => LocalWeb.Run(LocalWeb.ResolvePort(cfg.LocalWebPort), () => cfg.Server, m => log.LogInformation("{Msg}", m),
                url => tray.Status.LocalUrl = url, stop, jobs.BiosUsbLocalReply), stop);
        await EnsureEnrolled(cfg, http, stop);
        var api = new ServerClient(http, cfg);
        await PinSigningKey(api, cfg, stop);
        jobs.SendInventory = ct => SendInventory(api, ct);
        jobs.SendHealth = ct => SendHealth(api, cfg, ct);
        jobs.DownloadFile = (url, file, ct) => api.Download(url, file, ct);
        jobs.UsbIo = api.UsbIo;
        jobs.BiosSecret = api.BiosSecret;
        jobs.BiosUpdateIo = api.BiosUpdateIo;
        jobs.BiosUsbClaim = (nonce, ct) => api.Post<BiosUsbClaimResponse>("/api/agent/bios/usb/claim", new BiosUsbClaimRequest { Nonce = nonce }, ct);
        // BIOS-opdatering: meld resultatet efter genstarten (og meldinger, serveren ikke fik)
        if (opt.IsService && !opt.Once && !opt.DryRun)
            _ = BiosUpdateJob.ResumeLoop(() => BiosUpdates.Updater(opt), api.BiosUpdateIo, BiosUpdates.Store(opt), m => log.LogInformation("{Msg}", m), stop);
        jobs.CollectLogs = async (jobId, ct) =>
        {
            var zip = await logs.Build("hentet fra Fleetdeck", ct);
            await api.Post("/api/agent/logs", new LogUpload { JobId = jobId, Data = Convert.ToBase64String(zip) }, ct);
            return $"Logfilerne er sendt ({LogBundle.Size(zip.Length)}): agentens log, seneste fejl og advarsler fra hændelsesloggen og systeminfo. Hent dem under Beskeder.";
        };
        helpdesk.CollectLogs = ct => logs.Build("sendt af brugeren", ct);
        // iPad via USB: vagten kigger kun efter iPads, når udvidelsen er installeret
        if (opt.IsService && !opt.Once && !opt.DryRun)
            _ = Task.Run(() => new UsbIpadWatcher((r, ct) => api.Post("/api/agent/usb-ipads", r, ct), msg => log.LogInformation("{Msg}", msg)).Run(stop), stop);
        if (!opt.Once) _ = ConnectHub(cfg, stop);
        tray.Status.Machine = Environment.MachineName;
        tray.Status.Server = cfg.Server;
        tray.Status.Version = typeof(AgentWorker).Assembly.GetName().Version?.ToString(3) ?? _myVersion;
        if (opt.IsService) _ = CommandLoop(api, cfg, stop);
        var extAt = DateTime.MinValue;

        var poll = 30;
        // Efter opstart eller vækning (fx Wake-on-LAN uden nogen ved tastaturet) lægger Windows sig til at sove igen efter ~2 min.
        // Hold pc'en vågen et par minutter, så ventende jobs når at blive hentet; derefter følger den sin strømplan igen.
        IDisposable? grace = null; var graceUntil = DateTime.MinValue; var lastLoop = DateTime.UtcNow;
        void Grace(string why) { if (!opt.IsService) return; grace ??= KeepAwake.Begin($"Fleetdeck: {why}"); graceUntil = DateTime.UtcNow.AddMinutes(5); }
        Grace("forbinder efter opstart");
        while (!stop.IsCancellationRequested)
        {
            if (DateTime.UtcNow - lastLoop > TimeSpan.FromSeconds(Math.Max(120, poll * 3))) Grace("forbinder efter dvale");
            lastLoop = DateTime.UtcNow;
            if (grace is not null && DateTime.UtcNow > graceUntil) { grace.Dispose(); grace = null; }
            try
            {
                var nonce = CommandSigning.NewNonce();
                var r = await api.Post<CheckinResponse>("/api/agent/checkin",
                    new CheckinRequest { AgentVersion = _myVersion, Facts = collectors.Live(), Nonce = nonce, Trust = TrustSetup.Report(cfg, _trustError),
                        Caps = opt.IsService ? [TerminalPolicy.Capability, LiveLogPolicy.Capability] : null }, stop);
                _tlsFailures = 0;
                // signerede jobs: med pinnet nøgle bruges kun det signerede indhold (se docs/sikkerhed-agent.md)
                var gate = CommandGate.Apply(r, cfg, CommandSigning.TokenId(cfg.PlainToken()), nonce, DateTime.UtcNow);
                if (gate.ConfigChanged)
                {
                    cfg.Save(opt.ConfigFile);
                    log.LogInformation("Serverens jobnøgle er pinnet ({Fp}). Usignerede jobs afvises herefter.", AgentSigning.Fingerprint(cfg.JobKey!));
                }
                if (gate.Error != _trustError)
                {
                    if (gate.Error is { } te) log.LogWarning("{Why}", te); else log.LogInformation("Signerede svar fra serveren godkendes igen");
                    _trustError = gate.Error;
                }
                tray.Status.Security = gate.Error;
                CheckLoop.Start(r.Checks, res => api.Post("/api/agent/checks", res, stop), msg => log.LogInformation("{Msg}", msg));   // overvågning herfra
                poll = Math.Max(10, r.PollSeconds);
                var st = tray.Status;
                st.Connected = true; st.Error = null; st.LastContact = DateTime.UtcNow; st.PowerAt = PlannedPower.Current?.At; st.PowerMode = PlannedPower.Current?.Mode; st.Profile = r.Profile; st.Upgrades = r.Upgrades; st.Alerts = r.Alerts;
                st.Health = r.Health; st.WebUrl = r.WebUrl ?? cfg.Server; st.PausedUntil = cfg.PausedUntil > DateTime.UtcNow ? cfg.PausedUntil : null;
                if (DateTime.UtcNow - extAt > TimeSpan.FromMinutes(10))
                {
                    try { st.Extensions = await api.Get<List<ExtensionView>>("/api/agent/extensions", stop); extAt = DateTime.UtcNow; }
                    catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException) { }
                }
                tray.Write();
                if (opt.IsService) tray.EnsureTray();
                // Beskeder: nye beskeder til brugeren (kun fra et godkendt svar) og svar, der ikke nåede frem før
                if (opt.IsService && gate.Trusted) { await helpdesk.Deliver(api, r.Helpdesk, stop); await helpdesk.Flush(api, stop); }
                jobs.Share = r.Share;
                if (opt.IsService && gate.Trusted)
                    TerminalAgent.Start(gate.Terminals, cfg.Server, cfg.PlainToken(),
                        () => new SocketsHttpHandler { SslOptions = { RemoteCertificateValidationCallback = TlsPinning.Validate } },
                        ConPty.Start, m => log.LogInformation("{Message}", m), stop);
                if (opt.IsService && gate.Trusted)      // live-log til siden (kun læsning af hændelsesloggen)
                    LiveLogAgent.Start(gate.LiveLogs, cfg.Server, cfg.PlainToken(),
                        () => new SocketsHttpHandler { SslOptions = { RemoteCertificateValidationCallback = TlsPinning.Validate } },
                        t => new WinLiveSource(t.Level == "all"), m => log.LogInformation("{Message}", m), stop);
                // netværksdrev i brugerens session: vagthund i baggrunden, sender selv helbred og brugerens egne drev, når noget har ændret sig
                if (!opt.Once && !opt.DryRun && gate.Trusted)
                    drives.Tick(r.Drives, r.Share, cfg, ct => SendHealth(api, cfg, ct), (f, ct) => api.Post("/api/agent/drives/found", f, ct), stop);
                // printere: læg på og hold på (i baggrunden, ved hver helbredsrunde og når listen ændres)
                if (opt.IsService && !opt.Once && gate.Trusted)
                    printers.Tick(r.Printers, r.HealthDue, cfg, ct => SendHealth(api, cfg, ct), stop);
                if (opt.IsService && !opt.Once && !opt.DryRun && gate.Trusted)
                    printers.Scout(r.PrinterSearchAt, ct => SendHealth(api, cfg, ct), stop);   // find printere på netværket, når Fleetdeck søger
                if (opt.IsService && r.AgentVersion.Length > 0 && r.AgentVersion != _myVersion && await UpdateSelf(api, cfg, r.AgentVersion, gate.AgentSha256, stop)) return;
                if (opt.IsService && await MoveServer(cfg, http, r.ServerUrl, gate.Pinned ? gate.TlsPin : null, stop)) return;
                // en ny agentversion kan have nye tjek (fx AMD-driveren): send inventar med det samme i stedet for at vente op til 6 timer
                if (r.InventoryDue || NewVersionSinceInventory()) { await SendInventory(api, stop); MarkInventoryVersion(); }
                else if (r.HealthDue) await SendHealth(api, cfg, stop);
                if (cfg.PausedUntil > DateTime.UtcNow && r.Jobs.Count > 0)
                {
                    log.LogInformation("Sat på pause fra ikonet indtil {Until:HH:mm} – {N} job(s) venter", cfg.PausedUntil.Value.ToLocalTime(), r.Jobs.Count);
                    r.Jobs.Clear();
                }
                // hold pc'en vågen, så længe der arbejdes (ingen dvale midt i en opdatering); slippes igen bagefter
                using var awake = r.Jobs.Count > 0 ? KeepAwake.Begin($"Fleetdeck: {r.Jobs[0].Title}{(r.Jobs.Count > 1 ? $" (+{r.Jobs.Count - 1} job)" : "")}") : null;
                foreach (var j in r.Jobs)
                {
                    var outcome = await RunJob(api, j, stop);
                    if (outcome == Next.Stop) return;
                    if (outcome == Next.Wait) break;    // bevar rækkefølgen: senere jobs venter til brugeren er klar
                }
            }
            // også HttpClient-timeout (TaskCanceledException): ellers stopper tjenesten med kode 0, og Windows genstarter den ikke
            catch (Exception e) when (!stop.IsCancellationRequested)
            {
                log.LogWarning("Check-in fejlede{Route}: {Message}", RouteTo(cfg.Server), e.Message);
                // https-certifikatet passer ikke længere (fx skiftet på serveren): tilbage til den forrige adresse, hvis den beviser at være samme server
                if (opt.IsService && IsTlsFailure(e) && ++_tlsFailures >= 20 && await FallBack(cfg, http, stop)) return;
                tray.Status.Connected = false; tray.Status.Error = $"Check-in fejlede: {e.Message}"; tray.Write();
            }
            if (opt.Once) { life.StopApplication(); return; }
            // vent på næste check-in eller et "Wake" fra serveren (nyt job)
            await _wake.WaitAsync(TimeSpan.FromSeconds(poll), stop).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    private string? _trustError;
    private int _tlsFailures;

    private static bool IsTlsFailure(Exception e)
    {
        for (var x = e; x is not null; x = x.InnerException)
            if (x is System.Security.Authentication.AuthenticationException) return true;
        return false;
    }

    /// <summary>
    /// Efter en flytning til https: kan agenten ikke længere tale med serveren på grund af certifikatet, prøves den forrige adresse.
    /// Den bruges kun, hvis svaret derfra er signeret med den pinnede jobnøgle – så en falsk server kan ikke lokke agenten tilbage.
    /// </summary>
    private async Task<bool> FallBack(AgentConfig cfg, HttpClient http, CancellationToken ct)
    {
        _tlsFailures = 0;
        if (cfg.PreviousServer is not { Length: > 0 } prev || cfg.JobKey is not { Length: > 0 }) return false;
        try
        {
            var nonce = CommandSigning.NewNonce();
            using var req = new HttpRequestMessage(HttpMethod.Post, prev.TrimEnd('/') + "/api/agent/checkin");
            req.Headers.Authorization = new("Bearer", cfg.PlainToken());
            req.Content = System.Net.Http.Json.JsonContent.Create(new CheckinRequest { AgentVersion = _myVersion, Facts = collectors.Live(), Nonce = nonce, Trust = TrustSetup.Report(cfg, "https-forbindelsen fejler") }, options: AgentJson.Web);
            using var res = await http.SendAsync(req, ct);
            res.EnsureSuccessStatusCode();
            var r = await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<CheckinResponse>(res.Content, AgentJson.Web, ct);
            if (r is null || !CommandSigning.Verify(cfg.JobKey, r.Signed, CommandSigning.TokenId(cfg.PlainToken()), nonce, DateTime.UtcNow).Ok) return false;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { return false; }
        log.LogWarning("https-forbindelsen til {Server} fejler: skifter tilbage til {Prev}", cfg.Server, prev);
        (cfg.Server, cfg.PreviousServer) = (prev, cfg.Server);
        cfg.Save(opt.ConfigFile);
        Environment.Exit(1);
        return true;
    }

    private async Task EnsureEnrolled(AgentConfig cfg, HttpClient http, CancellationToken stop)
    {
        if (cfg.Token.Length > 0) return;
        if (cfg.PendingKey is not { Length: > 0 } && (cfg.Access is not null || cfg.AccessState is not null)) { await WaitForAccess(cfg, http, stop); return; }
        if (cfg.PendingKey is not { Length: > 0 } key) throw new InvalidOperationException("Agenten er ikke tilmeldt og har ingen tilmeldingsnøgle");
        var delay = TimeSpan.FromSeconds(10);
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var res = await ServerClient.Enroll(http, cfg.Server, new EnrollRequest(key, Environment.MachineName, cfg.PendingProvision, collectors.System(), cfg.PendingProfile, cfg.PendingDeploy,
                    PreviousToken: cfg.PlainPreviousToken()), stop);
                cfg.MachineId = res.MachineId; cfg.SetToken(res.Token); cfg.PendingKey = null; cfg.PendingProvision = false; cfg.PendingProfile = null; cfg.PendingDeploy = null;
                cfg.PendingPreviousToken = null;
                TrustSetup.FromEnroll(cfg, res);
                cfg.Save(opt.ConfigFile);
                log.LogInformation("Tilmeldt som maskine {Id}", res.MachineId);
                return;
            }
            catch (UnauthorizedAccessException) { log.LogError("Tilmeldingsnøglen blev afvist af serveren"); tray.Status.Error = "Tilmeldingsnøglen blev afvist af serveren"; tray.Write(); throw; }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogInformation("Venter på netværk/server for at tilmelde sig: {Message}", e.Message);
                tray.Status.Connected = false; tray.Status.Error = $"Venter på at kunne tilmelde sig: {e.Message}"; tray.Write();
                await Task.Delay(delay, stop);
                delay = TimeSpan.FromSeconds(Math.Min(120, delay.TotalSeconds * 2));
            }
        }
    }

    /// <summary>
    /// "Anmod om adgang": spørg serveren hvert 30. sekund (langsommere ved fejl), vis koden i ikonet, og tilmeld med det samme,
    /// når administratoren har godkendt. Afvist eller udløbet: vent – en ny opsætning starter tjenesten igen.
    /// </summary>
    private async Task WaitForAccess(AgentConfig cfg, HttpClient http, CancellationToken stop)
    {
        tray.Status.Machine = Environment.MachineName; tray.Status.Server = cfg.Server; tray.Status.Connected = false;
        var failures = 0;
        string? lastError = null;
        while (!stop.IsCancellationRequested)
        {
            var a = cfg.PlainAccess();
            if (a is null)
            {
                tray.Status.AccessRequest = new TrayAccessRequest { State = cfg.AccessState ?? AccessStates.Unknown, Reason = cfg.AccessReason };
                tray.Status.Error = tray.Status.AccessRequest.Text; tray.Write();
                if (opt.IsService) tray.EnsureTray();
                await Task.Delay(Timeout.Infinite, stop);
                return;
            }
            var view = new TrayAccessRequest { Code = a.Code, Name = a.Name, Since = a.Created, Expires = a.Expires };
            try
            {
                // selvsigneret certifikat: det fastlåste fingeraftryk; ellers almindelig certifikatkontrol
                using var ah = new AccessTlsObserver(a.PinNeeded ? a.Pin : null).Client();
                var r = await AccessRequestClient.Poll(ah, cfg.Server, a.Id, a.Secret, stop);
                failures = 0; lastError = null;
                view.State = r.State; view.Reason = r.Reason;
                if (r.State == AccessStates.Issued && AccessRequestClient.OpenKey(a, r) is { } key)
                {
                    // fingeraftrykket fra anmodningen er bekræftet af koden, administratoren sammenlignede
                    if (a.PinNeeded && a.Pin is { Length: > 0 } pin) { cfg.TlsPin = pin; TlsPinning.Add(pin); }
                    try
                    {
                        var res = await ServerClient.Enroll(http, cfg.Server, new EnrollRequest(key, r.Name ?? a.Name, false, collectors.System(),
                            PreviousToken: cfg.PlainPreviousToken(), AccessSecret: a.Secret), stop);
                        cfg.MachineId = res.MachineId; cfg.SetToken(res.Token); cfg.Access = null; cfg.AccessState = null; cfg.AccessReason = null; cfg.PendingPreviousToken = null;
                        TrustSetup.FromEnroll(cfg, res);
                        cfg.Save(opt.ConfigFile);
                        tray.Status.AccessRequest = null; tray.Status.Error = null; tray.Write();
                        log.LogInformation("Adgang godkendt: tilmeldt som maskine {Id}", res.MachineId);
                        return;
                    }
                    catch (UnauthorizedAccessException) { log.LogWarning("Engangskoden fra godkendelsen blev afvist – spørger igen"); }
                }
                else if (r.State == AccessStates.Issued) log.LogWarning("Den godkendte engangskode kunne ikke åbnes – spørger igen");
                else if (!AccessStates.Open(r.State))
                {
                    log.LogWarning("Anmodningen om adgang er afsluttet: {State} {Reason}", r.State, r.Reason);
                    cfg.Access = null; cfg.AccessState = r.State; cfg.AccessReason = r.Reason; cfg.Save(opt.ConfigFile);
                    continue;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException || !stop.IsCancellationRequested)
            {
                failures++; lastError = e.Message;
                log.LogInformation("Venter på serveren (anmodning om adgang): {Message}", e.Message);
                // udløbet, mens serveren ikke kunne nås: stop med at spørge
                if (DateTime.UtcNow > a.Expires.AddMinutes(5))
                {
                    cfg.Access = null; cfg.AccessState = AccessStates.Expired; cfg.Save(opt.ConfigFile);
                    continue;
                }
            }
            // flere fejl i træk: sig det (ellers tror både brugeren og administratoren, at den venter pænt)
            if (failures >= AccessRequestClient.ShowProblemAfter) view.Problem = lastError;
            tray.Status.AccessRequest = view; tray.Status.Error = view.Text; tray.Write();
            if (opt.IsService) tray.EnsureTray();
            await Task.Delay(AccessRequestClient.Backoff(failures), stop);
        }
    }

    private async Task ConnectHub(AgentConfig cfg, CancellationToken stop)
    {
        _hub = new HubConnectionBuilder()
            .WithUrl(cfg.Server.TrimEnd('/') + "/hubs/agent", o =>
            {
                o.AccessTokenProvider = () => Task.FromResult<string?>(cfg.PlainToken());
                // samme TLS-regler som check-in (fastlåst nøgle til et selvsigneret certifikat)
                o.HttpMessageHandlerFactory = h => { if (h is HttpClientHandler hc) hc.ServerCertificateCustomValidationCallback = (_, c, ch, e) => TlsPinning.Validate(null, c, ch, e); return h; };
                o.WebSocketConfiguration = ws => ws.RemoteCertificateValidationCallback = TlsPinning.Validate;
            })
            .WithAutomaticReconnect(new ForeverRetry())
            .Build();
        _hub.On(AgentHubMethods.Wake, () => { if (_wake.CurrentCount == 0) _wake.Release(); });
        while (!stop.IsCancellationRequested)
        {
            try { await _hub.StartAsync(stop); log.LogInformation("Live-forbindelse til serveren oprettet"); return; }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogDebug("SignalR: {Message}", e.Message); await Task.Delay(TimeSpan.FromSeconds(60), stop); }
        }
    }

    private sealed class ForeverRetry : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext c) => TimeSpan.FromSeconds(Math.Min(60, 2 + c.PreviousRetryCount * 5));
    }

    private enum Next { Continue, Wait, Stop }

    /// <summary>Hvilket netkort trafikken til serveren går ud ad (fx Tailscale i stedet for det lokale net) – til loggen ved fejl.</summary>
    private static string RouteTo(string server)
    {
        try
        {
            var uri = new Uri(server);
            if (uri.Scheme == "unix" || !System.Net.IPAddress.TryParse(uri.Host, out var ip)) return "";
            using var s = new System.Net.Sockets.Socket(ip.AddressFamily, System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
            s.Connect(ip, uri.Port);   // UDP: vælger kun ruten, sender intet
            var local = ((System.Net.IPEndPoint)s.LocalEndPoint!).Address;
            var nic = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(local)));
            return $" (via {nic?.Name ?? "?"} {local})";
        }
        catch (Exception e) when (e is System.Net.Sockets.SocketException or UriFormatException or InvalidOperationException) { return ""; }
    }

    /// <summary>Ordrer fra ikonet i proceslinjen: pause/genoptag (lokalt) og opdatér/synkronisér/udvidelse (via serveren).</summary>
    private async Task CommandLoop(ServerClient api, AgentConfig cfg, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try { await Task.Delay(3000, stop); } catch (OperationCanceledException) { return; }
            foreach (var c in tray.TakeCommands())
            {
                try
                {
                    switch (c.Action)
                    {
                        case "pause":
                            cfg.PausedUntil = DateTime.UtcNow.AddMinutes(Math.Clamp(c.Minutes ?? 60, 5, 24 * 60)); cfg.Save(opt.ConfigFile);
                            tray.Status.PausedUntil = cfg.PausedUntil; tray.Write();
                            log.LogInformation("Pause fra ikonet indtil {Until:HH:mm}", cfg.PausedUntil.Value.ToLocalTime());
                            break;
                        case "power_postpone":
                            if (WinPower.Cancel(PlannedPower.PostponeMinutes * 60))
                                log.LogInformation("Brugeren udsatte {Mode} til {At:HH:mm}", PlannedPower.Mode, PlannedPower.At?.ToLocalTime());
                            tray.Status.PowerAt = PlannedPower.At; tray.Status.PowerMode = PlannedPower.Mode; tray.Write();
                            break;
                        case "resume":
                            cfg.PausedUntil = null; cfg.Save(opt.ConfigFile); tray.Status.PausedUntil = null; tray.Write();
                            break;
                        case "power_now":
                            // kun den handling, Fleetdeck har planlagt her. Fleetdeck får besked først (output + revisionslog), så genstarter den
                            if (PlannedPower.NowAllowed(PlannedPower.Current, c.Id) is { } nowMode)
                            {
                                log.LogInformation("Brugeren valgte {Label}", PlannedPower.NowLabel(nowMode));
                                using (var tell = CancellationTokenSource.CreateLinkedTokenSource(stop))
                                {
                                    tell.CancelAfter(TimeSpan.FromSeconds(8));
                                    try { await api.Post("/api/agent/action", new AgentActionRequest { Action = "power_now", Id = nowMode }, tell.Token); }
                                    catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException) { log.LogWarning("Kunne ikke melde Genstart nu: {Msg}", e.Message); }
                                }
                                WinPower.Now(nowMode);
                                tray.Status.PowerAt = PlannedPower.At; tray.Status.PowerMode = PlannedPower.Mode; tray.Write();
                            }
                            break;
                        case "helpdesk_reply" or "helpdesk_read" or "helpdesk_logs":
                            await helpdesk.Handle(api, c, stop);
                            break;
                        case "upgrade_all" or "sync" or "extension":
                            await api.Post("/api/agent/action", new AgentActionRequest { Action = c.Action, Id = c.Id }, stop);
                            if (c.Action == "extension") tray.Status.Extensions.Where(e => e.Id == c.Id).ToList().ForEach(e => e.Pending = true);
                            tray.Write();
                            break;
                    }
                }
                catch (Exception e) when (e is HttpRequestException or IOException) { log.LogWarning("Ordre fra ikonet fejlede: {Msg}", e.Message); }
                if (_wake.CurrentCount == 0) _wake.Release();   // check ind med det samme
            }
        }
    }

    private async Task<Next> RunJob(ServerClient api, AgentJob j, CancellationToken stop)
    {
        var start = await api.Post<JobStartResponse>($"/api/agent/jobs/{j.Id}/start", new { }, stop);
        if (!start.Ok) return Next.Continue;
        log.LogInformation("Job {Id} start: {Title} [{RunAs}]", j.Id, j.Title, j.RunAs);
        JobOutcome res;
        LiveJob.Begin(j.Id);
        tray.Status.Job = new TrayJob { Title = j.Title }; tray.Write();
        using var live = new CancellationTokenSource();
        using var job = CancellationTokenSource.CreateLinkedTokenSource(stop);   // annulleres, hvis jobbet stoppes i Fleetdeck
        var reporter = ReportProgress(api, j.Id, job, live.Token);
        try { res = await jobs.Execute(j, job.Token); }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested) { res = new(JobStatus.Cancelled, null, "Annulleret fra Fleetdeck."); }
        catch (Exception e) when (e is not OperationCanceledException) { res = new(JobStatus.Failed, -1, $"Agent-fejl: {e.Message}"); }
        finally { LiveJob.End(j.Id); live.Cancel(); await reporter; tray.Status.Job = null; tray.Status.PowerAt = PlannedPower.Current?.At; tray.Status.PowerMode = PlannedPower.Current?.Mode; tray.Write(); }
        if (job.IsCancellationRequested && !stop.IsCancellationRequested && res.Status != JobStatus.Cancelled)
            res = res with { Status = JobStatus.Cancelled };
        await api.Post($"/api/agent/jobs/{j.Id}/result", new JobResult
        {
            Status = res.Status, ExitCode = res.ExitCode, Output = res.Output,
            PowerAt = j.Type is JobTypes.Reboot or JobTypes.Shutdown or JobTypes.CancelPower ? PlannedPower.At : null,
            PowerMode = j.Type is JobTypes.Reboot or JobTypes.Shutdown or JobTypes.CancelPower ? PlannedPower.Mode : null,
            Ios = res.Ios, Update = res.Update,
            BiosSettings = res.Bios?.BiosSettings, BiosSettingsBefore = res.Bios?.BiosSettingsBefore, BiosApply = res.Bios?.BiosApply,
            BiosUpdate = res.Bios?.BiosUpdate, BiosUsb = res.Bios?.BiosUsb,
        }, stop);
        log.LogInformation("Job {Id} slut: {Status} ({Code})", j.Id, res.Status, res.ExitCode);
        // nyinstalleret program (fx fjernskrivebord): send helbred med det samme, så det bliver sat op og vist
        if (j.Type is JobTypes.Install or JobTypes.Script && res.Status == JobStatus.Success && jobs.SendHealth is { } sh)
            try { await sh(stop); } catch (Exception e) when (e is HttpRequestException or IOException) { log.LogDebug("Helbred: {Msg}", e.Message); }
        // afinstalleret fra programlisten: send inventaret med det samme, så programmet forsvinder fra listen
        if (j.Type == JobTypes.RemoveApp && res.Status is JobStatus.Success or JobStatus.Failed && jobs.SendInventory is { } si)
            try { await si(stop); } catch (Exception e) when (e is HttpRequestException or IOException) { log.LogDebug("Inventar: {Msg}", e.Message); }
        if (res.Status == JobStatus.WaitingUser) return Next.Wait;
        if (res.After is null) return Next.Continue;
        await res.After();
        // genstart/slukning er kun planlagt: agenten kører videre, så nedtællingen kan ses og annulleres
        return j.Type == JobTypes.RemoveAgent ? Next.Stop : Next.Continue;
    }

    /// <summary>
    /// Sender jobbets fremdrift til serveren hvert 4. sekund, når der er noget nyt, og mindst hvert 10. sekund.
    /// Svaret fortæller, om jobbet er stoppet i Fleetdeck – så annulleres det her og installationen lukkes.
    /// </summary>
    private async Task ReportProgress(ServerClient api, int jobId, CancellationTokenSource job, CancellationToken ct)
    {
        var last = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(4000, ct); } catch (OperationCanceledException) { return; }
            var t = LiveJob.Take(DateTime.UtcNow - last > TimeSpan.FromSeconds(10));
            if (t is null) continue;
            last = DateTime.UtcNow;
            if (tray.Status.Job is { } tj) { tj.Percent = t.Value.Post.Percent; tj.Step = t.Value.Post.Step; tray.Write(); }
            try
            {
                var r = await api.Post<JobProgressResponse>($"/api/agent/jobs/{t.Value.JobId}/progress", t.Value.Post, ct);
                if (r?.Cancel == true && t.Value.JobId == jobId && !job.IsCancellationRequested)
                {
                    log.LogWarning("Job {Id} stoppet fra Fleetdeck", jobId);
                    job.Cancel();
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { }
        }
    }

    private async Task SendHealth(ServerClient api, AgentConfig cfg, CancellationToken ct)
    {
        var h = collectors.Health(false);
        h.Drives = drives.States;
        h.Printers = printers.States;
        h.LocalPrinters = printers.Local;
        h.NetPrinters = printers.Net;
        h.NetPrintersAt = printers.NetAt;
        if (opt.IsService)
            try { h.Remote = await Task.Run(() => RemoteDesk.Ensure(cfg, opt.ConfigFile), ct); }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning("Fjernskrivebord: {Msg}", e.Message); }
            try { h.Autostart = await Task.Run(Autostart.Ensure, ct); if (h.Autostart.Fixed is { } fx) log.LogInformation("Opstart rettet: {Fix}", fx); }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning("Opstart: {Msg}", e.Message); }
            try { h.Amt = await Amt.Info(ct: ct); }   // Intel AMT: kun på maskiner, der har det
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning("Intel AMT: {Msg}", e.Message); }
        DiskCrypto.Snapshot? enc = null;
        if (opt.IsService)
            try { enc = await DiskCrypto.Collect(ct); h.Encryption = enc?.Info; }   // BitLocker (kun som SYSTEM)
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning("BitLocker: {Msg}", e.GetType().Name); }
        if (opt.IsService)
            try { h.Tpm = await Task.Run(TpmCollect.Collect, ct); }   // TPM (kun som SYSTEM)
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning("TPM: {Msg}", e.GetType().Name); }
        try { h.Network = await Task.Run(NetCollect.Collect, ct); }   // netværksadaptere: status, hastighed, Wi-Fi
        catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning("Netværk: {Msg}", e.GetType().Name); }
        // fejlloggen: højst hvert 10. minut, fra hvor serveren sidst fik den (kun som SYSTEM)
        var logAt = DateTime.UtcNow;
        if (opt.IsService && SysLogCollect.Schedule.Due(logAt))
        {
            SysLogCollect.Schedule.Tried(logAt);
            try { h.SysLog = await Task.Run(() => SysLogCollect.Collect(SysLogCollect.Schedule.Since(logAt)), ct); }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning("Fejllog: {Msg}", e.GetType().Name); }
        }
        HealthAck? ack = null;
        try { ack = await api.Post<HealthAck>("/api/agent/health", new HealthPost { Health = h }, ct); }
        catch (System.Text.Json.JsonException) { }   // ældre server uden svar
        if (h.SysLog is not null) SysLogCollect.Schedule.Sent(logAt);
        log.LogInformation("Helbred sendt");
        await Escrow(api, enc, ack?.EscrowWanted == true, ct);
    }

    /// <summary>BitLocker-gendannelsesnøgler til Fleetdeck: kun når de er ændret, eller serveren mangler dem. Nøglerne logges aldrig.</summary>
    private async Task Escrow(ServerClient api, DiskCrypto.Snapshot? enc, bool wanted, CancellationToken ct)
    {
        if (enc is not { Keys.Count: > 0 } || !DiskCrypto.Due(opt.Dir, enc.Info.KeysHash, wanted)) return;
        try
        {
            await api.Post("/api/agent/recovery-keys", new RecoveryKeyPost { Keys = enc.Keys, Full = true }, ct);
            DiskCrypto.Sent(opt.Dir, enc.Info.KeysHash!);
            log.LogInformation("Gendannelsesnøgler gemt i Fleetdeck ({N} drev)", enc.Keys.Select(k => k.Volume).Distinct().Count());
        }
        catch (HttpRequestException e) { log.LogWarning("Gendannelsesnøgler blev ikke sendt: {Msg}", e.StatusCode?.ToString() ?? e.GetType().Name); }
    }

    private string InventoryVersionFile => Path.Combine(opt.Dir, "inventory-version.txt");
    private bool NewVersionSinceInventory() => !opt.DryRun && InventoryVersion.Changed(InventoryVersionFile, _myVersion);
    private void MarkInventoryVersion() { try { File.WriteAllText(InventoryVersionFile, _myVersion); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private async Task SendInventory(ServerClient api, CancellationToken ct)
    {
        log.LogInformation("Samler inventar…");
        var report = new InventoryReport
        {
            Facts = collectors.System(), Health = collectors.Health(true), Software = await collectors.Software(ct),
            WingetIds = await winget.ExportIds(ct), Upgrades = await winget.Upgrades(ct),
        };
        // den dybe scanning: Windows Update (med Office og drivere), Office (Klik og kør) og AMD-grafikdriveren
        report.Upgrades.AddRange(await DeepUpdates.Collect(report.Software, m => log.LogWarning("{Msg}", m), ct));
        try { report.Drivers = await Task.Run(DriverCollect.Collect, ct); }   // til ændringsloggen
        catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning("Drivere: {Msg}", e.GetType().Name); }
        var r = await api.Post<InventoryResponse>("/api/agent/inventory", report, ct);
        log.LogInformation("Inventar sendt: {Sw} programmer, {W} winget-pakker, {U} opdateringer, {J} nye jobs",
            report.Software.Count, report.WingetIds.Count, report.Upgrades.Count, r.JobsCreated);
        // Ikoner bagefter: kun dem, serveren mangler. Fejl her må aldrig vælte inventaret.
        await ProgramIcons.Upload(api, report.Software, log, ct);
    }

    // ---------------------------------------------------------------- selvopdatering
    private static string HashOfSelf()
    {
        var p = Environment.ProcessPath;
        if (p is null || !File.Exists(p)) return "dev";
        using var fs = File.OpenRead(p);
        return Convert.ToHexStringLower(SHA256.HashData(fs))[..12];
    }

    /// <summary>Slet gamle udgaver (Fleetdeck.exe.old, .old-…); dem, der stadig er i brug, tages næste gang.</summary>
    private static void CleanupOldBinary()
    {
        var exe = Environment.ProcessPath!;
        foreach (var f in Directory.EnumerateFiles(Path.GetDirectoryName(exe)!, Path.GetFileName(exe) + ".old*"))
            try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Luk ikonet og andre Fleetdeck-vinduer i brugernes sessioner, så ingen holder på den gamle fil. Tjenesten starter ikonet igen.</summary>
    private void StopUserCopies()
    {
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Environment.ProcessPath!)))
        {
            try { if (p.SessionId != 0 && p.Id != Environment.ProcessId) { p.Kill(); p.WaitForExit(5000); } }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { log.LogDebug("Luk {Id}: {Msg}", p.Id, e.Message); }
            finally { p.Dispose(); }
        }
    }

    /// <summary>Serveren er flyttet (ny adresse): skift når den nye adresse svarer, og genstart tjenesten.</summary>
    private async Task<bool> MoveServer(AgentConfig cfg, HttpClient http, string? url, string? newTlsPin, CancellationToken ct)
    {
        url = url?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(url) || string.Equals(url, cfg.Server.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return false;
        // ny https-adresse med selvsigneret certifikat: fingeraftrykket kommer fra det signerede svar
        if (u.Scheme == "https") TlsPinning.Add(newTlsPin);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            using var res = await http.GetAsync(url + "/api/health", cts.Token);
            if (!res.IsSuccessStatusCode) return false;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            log.LogInformation("Serveren siger, at den har en ny adresse {Url}, men den svarer ikke endnu", url);
            return false;
        }
        // den nye adresse skal være den samme server: samme nøgle
        if (cfg.SigningKey is { Length: > 0 } pinned)
        {
            try
            {
                var pem = (await http.GetStringAsync(url + "/api/agent/signing-key", ct)).Trim();
                if (AgentSigning.Fingerprint(pem) != AgentSigning.Fingerprint(pinned)) { log.LogWarning("Flytning til {Url} afvist: serveren har en anden nøgle", url); return false; }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or CryptographicException or ArgumentException)
            { log.LogWarning("Flytning til {Url} afvist: {Msg}", url, e.Message); return false; }
        }
        log.LogInformation("Serveren er flyttet: {Old} -> {New}. Genstarter agenten.", cfg.Server, url);
        cfg.PreviousServer = cfg.Server;
        if (u.Scheme == "https" && newTlsPin is { Length: > 0 }) cfg.TlsPin = newTlsPin;
        cfg.Server = url;
        cfg.Save(opt.ConfigFile);
        Environment.Exit(1);   // tjenestens fejlhandling genstarter med den nye adresse
        return true;
    }

    private static bool ContainsAscii(string file, string text)
    {
        var needle = System.Text.Encoding.ASCII.GetBytes(text);
        var data = File.ReadAllBytes(file);
        return data.AsSpan().IndexOf(needle) >= 0;
    }

    /// <summary>Husk serverens nøgle første gang (tilmelding eller opgradering fra en agent uden signering).</summary>
    private async Task PinSigningKey(ServerClient api, AgentConfig cfg, CancellationToken ct)
    {
        if (cfg.SigningKey is { Length: > 0 } || !opt.IsService) return;
        try
        {
            var pem = (await api.GetText("/api/agent/signing-key", ct)).Trim();
            cfg.SigningKey = pem; cfg.Save(opt.ConfigFile);
            log.LogInformation("Serverens nøgle er husket ({Fp}). Kun signerede opdateringer installeres herefter.", AgentSigning.Fingerprint(pem));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or CryptographicException or ArgumentException)
        { log.LogWarning("Kunne ikke hente serverens nøgle endnu: {Msg}", e.Message); }
    }

    private async Task<bool> UpdateSelf(ServerClient api, AgentConfig cfg, string wanted, string? signedSha256, CancellationToken ct)
    {
        var exe = Environment.ProcessPath!;
        var tmp = exe + ".new";
        try
        {
            await PinSigningKey(api, cfg, ct);   // aldrig en opdatering uden en husket nøgle at tjekke den mod
            var (hash, sig) = await api.Download("/api/agent/download", tmp, ct);
            string got;
            await using (var fs = File.OpenRead(tmp)) got = Convert.ToHexStringLower(SHA256.HashData(fs))[..12];
            if (got != wanted) { log.LogWarning("Opdatering afvist: hash {Got} ≠ {Wanted}", got, wanted); SelfUpdate.Failed("den hentede fil passer ikke med serverens version"); File.Delete(tmp); return false; }
            // aldrig "opdatere" til en anden slags agent (fx en WinStyring 2.x-server, der udleverer sin gamle agent).
            // Versionsoplysningerne kommer ikke med, når exe'en bygges på Linux, så der kigges efter programfilens navn i pakken.
            if (!ContainsAscii(tmp, "Fleetdeck.dll"))
            {
                log.LogWarning("Opdatering afvist: serveren udleverer ikke en Fleetdeck-agent – opgradér serveren til Fleetdeck");
                SelfUpdate.Failed("serveren udleverer en anden agent (fx WinStyring 2.x) – opgradér serveren til Fleetdeck");
                File.Delete(tmp); return false;
            }
            if (AgentSigning.CheckPinned(tmp, cfg.SigningKey, hash, sig) is { Ok: false } bad)
            { log.LogWarning("Opdatering afvist: {Why}", bad.Why); SelfUpdate.Failed($"afvist: {bad.Why}"); File.Delete(tmp); return false; }
            // med pinnet jobnøgle: filen skal også passe med den fulde SHA-256 i det signerede check-in-svar
            if (CommandGate.CheckUpdate(tmp, cfg.JobKey is { Length: > 0 }, signedSha256) is { Ok: false } unsigned)
            { log.LogWarning("Opdatering afvist: {Why}", unsigned.Why); SelfUpdate.Failed($"afvist: {unsigned.Why}"); File.Delete(tmp); return false; }
            // en kørende .exe må gerne omdøbes – men en gammel .old kan stadig være i brug (fx af ikonet): så et nyt navn
            StopUserCopies();
            CleanupOldBinary();
            var old = exe + ".old";
            if (File.Exists(old)) old = $"{exe}.old-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Move(exe, old);
            try { File.Move(tmp, exe, true); }
            catch { File.Move(old, exe); throw; }   // aldrig efterlade tjenesten uden .exe (fx låst af antivirus)
            log.LogInformation("Agent opdateret til {Version}. Genstarter tjenesten.", wanted);
            Environment.Exit(1);                    // tjenestens fejlhandling genstarter med den nye version
            return true;
        }
        catch (Exception e) { log.LogWarning("Selvopdatering fejlede: {Message}", e.Message); SelfUpdate.Failed(e.Message); try { File.Delete(tmp); } catch (IOException) { } return false; }
    }
}
