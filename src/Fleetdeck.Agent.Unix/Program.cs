// Fleetdeck-agent til Linux.
//   fleetdeck-agent install --server <url> --key <nøgle>   tilmeld og gem konfiguration (kaldes af install.sh)
//   fleetdeck-agent install --server <url> --request [--message <tekst>]
//                                                           ingen nøgle: bed administratoren om adgang (koden vises)
//   fleetdeck-agent status [--json]                         tilmeldt, venter på godkendelse (kode) eller afvist
//   fleetdeck-agent                                         kør (tjenesten fleetdeck-agent)
//   fleetdeck-agent facts                                   vis hvad agenten indsamler (test)
//   fleetdeck-agent setup                                   forbind maskinen: små vinduer (zenity/kdialog) eller spørgsmål i terminalen
//   fleetdeck-agent install ... --service                   (fra setup) kopiér til /opt/fleetdeck og opret tjenesten (systemd, OpenRC, runit eller SysV)
//   fleetdeck-agent install ... --menubar <Fleetdeck.app>   (macOS, fra Fleetdeck.app) vis appen i menulinjen ved login
//   fleetdeck-agent install ... --no-app                    (macOS, install.sh med WS_NO_APP=1) kun agenten – huskes, så appen ikke lægges på senere
//   fleetdeck-agent tray-cmd <base64>                       (macOS, root) administrator-ordre fra menulinjen (pause, udvidelse)
//   fleetdeck-agent uninstall                               (macOS, root) fjern agenten, tjenesten og konfigurationen
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR.Client;
using Fleetdeck.Agent.Unix;
using Fleetdeck.Shared;

// Terminal: hjælperen bliver til skallen og må ikke røre resten af agenten
if (args is [UnixPty.HelperCommand, var ptySlave, var ptyCols, var ptyRows])
    return UnixPty.RunHelper(ptySlave, int.Parse(ptyCols), int.Parse(ptyRows));

string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
var cmd = args.FirstOrDefault(a => !a.StartsWith('-')) ?? "run";
var http = new HttpClient(Net.Handler()) { Timeout = TimeSpan.FromMinutes(10) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("Fleetdeck-LinuxAgent/2.0");

switch (cmd)
{
    case "install":
    {
        if (Environment.UserName != "root") { Console.Error.WriteLine("Kør som root (sudo)."); return 5; }
        var server = Arg("--server")?.TrimEnd('/'); var request = args.Contains("--request"); var key = request ? null : Arg("--key");
        if (server is null || (key is null && !request)) { Console.Error.WriteLine("Mangler --server og --key (eller --request for at bede om adgang)"); return 2; }
        // --name: fra Ubuntu-installeren (der har et midlertidigt værtsnavn). --provision: genskab fra seneste kit.
        var name = Arg("--name") is { Length: > 0 } n ? n : Environment.MachineName;
        var provision = args.Contains("--provision");
        // --profile: valgt ved installationen (0 = bevidst ingen profil)
        int? profile = int.TryParse(Arg("--profile"), out var pi) && pi >= 0 ? pi : null;
        var cfg = new AgentConfig { Server = server };
        // Geninstallation på samme maskine: det gamle token beviser, at det er den samme maskine (ellers venter den på godkendelse i Fleetdeck)
        AgentConfig? old = null;
        try { old = AgentConfig.Load(); } catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        var oldToken = old?.Token is { Length: > 0 } ot ? ot : old?.PendingPreviousToken;
        TrustSetup.Keep(cfg, old);
        // WS_NO_APP=1 huskes, så tjenesten ikke lægger appen på alligevel senere (en installation med appen slår det fra igen)
        cfg.NoApp = MacAppUpdate.NoApp(args.Contains("--no-app"), Arg("--menubar") is { Length: > 0 }, old?.NoApp ?? false);
        var cliPin = Arg("--tls-pin");
        if (cliPin is { Length: > 0 } && TlsPinning.Normalize(cliPin) is null) { Console.Error.WriteLine("Ugyldigt --tls-pin (forventer sha256//… eller hex)"); return 2; }
        if (cliPin is { Length: 0 }) cliPin = null;
        TlsPinning.Set(TlsPinning.Normalize(cliPin) ?? cfg.TlsPin);
        if (request)
        {
            // "Anmod om adgang": tjenesten venter på administratoren og tilmelder sig selv, når han har godkendt
            if (old?.Token is { Length: > 0 } && !args.Contains("--force"))
            {
                Console.Error.WriteLine("Maskinen er allerede tilmeldt Fleetdeck. Brug --force (install.sh: WS_FORCE=1) for at starte forfra og bede om adgang igen.");
                return 10;
            }
            try
            {
                var obs = Net.IsUnix(server) ? null : new AccessTlsObserver(null, firstContact: true);
                using var ah = obs?.Client();
                var who = Environment.GetEnvironmentVariable("SUDO_USER") is { Length: > 0 } su && su != "root" ? su : Environment.UserName;
                // "Anmod igen": maskinens egen tidligere anmodning lukkes, når den nye er oprettet
                var replaces = old?.Access is { Secret.Length: > 0 } prev && old.Server.TrimEnd('/') == server ? new AccessReplace(prev.Id, prev.Secret) : null;
                var p = await AccessRequestClient.Request(ah ?? http, obs, Net.Base(server), name, Platforms.Current, Collect.SystemInfo(), who, Arg("--message"), replaces);
                cfg.Access = p; cfg.AccessState = null; cfg.AccessReason = null;
                cfg.TlsPin = TlsPinning.Normalize(cliPin) ?? cfg.TlsPin; cfg.PendingPreviousToken = oldToken;
                Console.WriteLine($"Anmodning sendt. Kode: {p.Code}");
                Console.WriteLine("Giv koden til administratoren. Venter på godkendelse – maskinen tilmelder sig selv bagefter.");
            }
            catch (AccessRequestException e) { Console.Error.WriteLine(e.Message); return 3; }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { Console.Error.WriteLine($"Serveren kan ikke nås ({e.Message}). Prøv igen."); return 6; }
            // den kørende tjeneste stoppes, før den nye konfiguration gemmes: ellers kan den nå at skrive sin gamle (nu erstattede
            // anmodning) oven i. Først efter anmodningen, så en afvist anmodning ikke efterlader tjenesten stoppet.
            if (args.Contains("--service")) Setup.StopService();
            cfg.Save();
            AccessStatus.Write(cfg, AccessStatus.View(cfg));
            if (!args.Contains("--service")) return 0;
            var src = Setup.InstallService();
            if (src == 0) Legacy.RemoveOld();
            if (src == 0 && Mac.Is) MacTray.InstallMenuBar(Arg("--menubar") is { Length: > 0 } mapp ? mapp : MacPaths.InstalledApp);   // også uden --menubar (install.sh, "setup")
            return src;
        }
        var deferred = false;   // serveren kunne ikke nås: tjenesten tilmelder sig selv senere
        try
        {
            var res = await Api.Enroll(http, server, new EnrollRequest(key!, name, provision, Collect.SystemInfo(), ProfileId: profile, Platform: Platforms.Current,
                Extensions: Arg("--extensions")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(), PreviousToken: oldToken));
            cfg.MachineId = res.MachineId; cfg.Token = res.Token;
            TrustSetup.FromEnroll(cfg, res, cliPin);
            Console.WriteLine($"Tilmeldt som maskine {res.MachineId}.");
            if (res.Pending) Console.WriteLine("Navnet findes allerede i Fleetdeck: maskinen venter på, at en administrator godkender den.");
        }
        catch (UnauthorizedAccessException e) { Console.Error.WriteLine(e.Message); return 3; }
        catch (Exception e)
        {
            cfg.PendingKey = key; cfg.PendingName = name; cfg.PendingProvision = provision; cfg.PendingProfile = profile;
            cfg.PendingPreviousToken = oldToken; cfg.TlsPin = TlsPinning.Normalize(cliPin) ?? cfg.TlsPin;
            Console.WriteLine($"Serveren kan ikke nås lige nu ({e.Message}). Tjenesten tilmelder sig, når der er netværk.");
            deferred = true;
        }
        cfg.Save();
        if (!args.Contains("--service")) return 0;
        var rc = Setup.InstallService();
        if (rc == 0) Legacy.RemoveOld();   // en gammel WinStyring-agent på samme maskine fjernes
        if (rc == 0 && Mac.Is) MacTray.InstallMenuBar(Arg("--menubar") is { Length: > 0 } app ? app : MacPaths.InstalledApp);   // også uden --menubar (install.sh, "setup")
        // "setup" spørger med --offline-exit: installeret, men ikke tilmeldt endnu = exit 7 (som på Windows) i stedet for at lede efter en
        // tekst i outputtet (som ikke samles op i terminalen). Uden flaget er exitkoden uændret 0 (install.sh, Mac-appen).
        return rc == 0 && deferred && args.Contains("--offline-exit") ? 7 : rc;
    }
    case "setup":
        return await Setup.Run(args);
    case "tray-cmd":
        return MacTray.WriteAdminCommand(args.SkipWhile(a => a != "tray-cmd").Skip(1).FirstOrDefault());
    case "uninstall":
        return MacTray.Uninstall();
    case "sensors":
        foreach (var l in MacSensors.Diagnose().Concat(MacTempReader.Diagnose())) Console.WriteLine(l);
        return 0;
    case "status":
        return AccessStatus.Print(args.Contains("--json"));
    case "facts":
        Console.WriteLine(JsonSerializer.Serialize(new { system = Collect.SystemInfo(), live = Collect.Live(), health = Collect.Health() }, Api.Json));
        return 0;
    case "run":
        return await new Agent(http).Run();
    default:
        Console.WriteLine("Brug: fleetdeck-agent [setup | install --server <url> --key <nøgle> | install --server <url> --request | status | facts | uninstall]");
        return 2;
}

namespace Fleetdeck.Agent.Unix
{
    public static class Log
    {
        public static void Info(string msg) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {msg}");
        public static void Warn(string msg) => Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss} ADVARSEL {msg}");
    }

    /// <summary>/etc/fleetdeck/agent.json – kun læsbar for root.</summary>
    public sealed class AgentConfig : ITrustStore
    {
        public const string Dir = "/etc/fleetdeck", File = Dir + "/agent.json";
        public string Server { get; set; } = "";
        public int MachineId { get; set; }
        public string Token { get; set; } = "";
        public string? PendingKey { get; set; }
        public string? PendingName { get; set; }
        public bool PendingProvision { get; set; }
        public int? PendingProfile { get; set; }
        /// <summary>Fast adgangskode til RustDesk (ubemandet adgang), lavet af agenten.</summary>
        public string? RemotePassword { get; set; }
        /// <summary>Serverens offentlige nøgle (PEM), husket ved første kontakt: kun agentfiler signeret med den installeres.</summary>
        public string? SigningKey { get; set; }
        /// <summary>Serverens jobnøgle (PEM), pinnet ved tilmelding eller første signerede svar: herefter afvises usignerede jobs.</summary>
        public string? JobKey { get; set; }
        /// <summary>Fastlåst TLS-nøgle for https (selvsigneret certifikat), SHA-256 af SPKI i base64.</summary>
        public string? TlsPin { get; set; }
        /// <summary>Adressen før seneste flytning: bruges, hvis https-forbindelsen holder op med at virke (kun med signeret svar).</summary>
        public string? PreviousServer { get; set; }
        /// <summary>Det gamle token fra før en geninstallation, til tilmeldingen når der er netværk.</summary>
        public string? PendingPreviousToken { get; set; }
        /// <summary>"Anmod om adgang", der venter på administratoren (hemmeligheden er beskyttet af filens rettigheder, som tokenet).</summary>
        public PendingAccess? Access { get; set; }
        /// <summary>Sidste svar, når anmodningen er afsluttet uden tilmelding (afvist, udløbet …).</summary>
        public string? AccessState { get; set; }
        public string? AccessReason { get; set; }
        /// <summary>Sat på pause fra menulinjen (macOS): ingen nye jobs før dette tidspunkt (UTC).</summary>
        public DateTime? PausedUntil { get; set; }
        /// <summary>ESP Flasher på denne maskine (http://localhost:porten): null = standard (8587), 0 = slået fra.</summary>
        public int? LocalWebPort { get; set; }
        /// <summary>macOS, installeret med WS_NO_APP=1 (install --no-app): tjenesten lægger ikke Fleetdeck.app på af sig selv.</summary>
        public bool NoApp { get; set; }

        public static AgentConfig? Load() => System.IO.File.Exists(File) ? JsonSerializer.Deserialize<AgentConfig>(System.IO.File.ReadAllText(File)) : null;
        public void Save()
        {
            Directory.CreateDirectory(Dir);
            System.IO.File.SetUnixFileMode(Dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var tmp = File + ".tmp";
            System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            System.IO.File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            System.IO.File.Move(tmp, File, true);
            // Unraid kører fra RAM: kopien på USB-stikket skal følge med (fx den huskede nøgle og en ny serveradresse)
            if (Unraid.Is && Directory.Exists(Unraid.Flash))
                try { System.IO.File.Copy(File, Unraid.Flash + "/agent.json", true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public sealed class Api(HttpClient http, AgentConfig cfg)
    {
        public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        private HttpRequestMessage Req(HttpMethod m, string path, object? body = null)
        {
            var r = new HttpRequestMessage(m, Net.Base(cfg.Server) + path);
            r.Headers.Authorization = new("Bearer", cfg.Token);
            if (body is not null) r.Content = JsonContent.Create(body, body.GetType(), options: Json);
            return r;
        }
        public async Task<T> Post<T>(string path, object body, CancellationToken ct = default)
        {
            using var res = await http.SendAsync(Req(HttpMethod.Post, path, body), ct);
            res.EnsureSuccessStatusCode();
            return (await res.Content.ReadFromJsonAsync<T>(Json, ct))!;
        }
        public async Task Post(string path, object body, CancellationToken ct = default)
        {
            using var res = await http.SendAsync(Req(HttpMethod.Post, path, body), ct);
            res.EnsureSuccessStatusCode();
        }
        /// <summary>Send en fil (gendannelsespunkt) som rå data.</summary>
        public async Task PutFile(string path, string file, CancellationToken ct = default)
        {
            await using var fs = System.IO.File.OpenRead(file);
            using var req = Req(HttpMethod.Put, path);
            req.Content = new StreamContent(fs);
            req.Content.Headers.ContentType = new("application/gzip");
            using var res = await http.SendAsync(req, ct);
            res.EnsureSuccessStatusCode();
        }
        /// <summary>Lange overførsler (iPad-backups på op til 1,8 GB pr. del): ingen samlet tidsgrænse, kun jobbets annullering.</summary>
        private static readonly Lazy<HttpClient> LongHttp = new(() => new HttpClient(Net.Handler()) { Timeout = Timeout.InfiniteTimeSpan });

        /// <summary>Send en strøm (fx en del af en tar.gz) som rå data med PUT.</summary>
        public async Task PutStream(string path, Stream body, CancellationToken ct = default)
        {
            using var req = Req(HttpMethod.Put, path);
            req.Content = new StreamContent(body, 1 << 20);
            req.Content.Headers.ContentType = new("application/gzip");
            using var res = await LongHttp.Value.SendAsync(req, ct);
            res.EnsureSuccessStatusCode();
        }

        /// <summary>Hent til en strøm. false, hvis serveren ikke har filen (404).</summary>
        public async Task<bool> GetToStream(string path, Stream target, CancellationToken ct = default)
        {
            using var res = await LongHttp.Value.SendAsync(Req(HttpMethod.Get, path), HttpCompletionOption.ResponseHeadersRead, ct);
            if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return false;
            res.EnsureSuccessStatusCode();
            await res.Content.CopyToAsync(target, ct);
            return true;
        }

        /// <summary>Store filer (iPad-backup i dele på op til 1,5 GB): streamet og uden den almindelige tidsgrænse på 10 minutter.</summary>
        public async Task PutLargeFile(string path, string file, CancellationToken ct = default)
        {
            await using var fs = System.IO.File.OpenRead(file);
            using var req = Req(HttpMethod.Put, path);
            req.Content = new StreamContent(fs, 1 << 20);
            req.Content.Headers.ContentType = new("application/octet-stream");
            req.Content.Headers.ContentLength = fs.Length;
            using var res = await Large.SendAsync(req, ct);
            res.EnsureSuccessStatusCode();
        }
        private static readonly HttpClient Large = new(Net.Handler()) { Timeout = Timeout.InfiniteTimeSpan };
        /// <summary>bios_settings_apply: byt jobbets engangsbillet til BIOS-adgangskoden (kun i hukommelsen).</summary>
        public async Task<BiosSecretResponse?> BiosSecret(BiosSecretRequest r, CancellationToken ct) => await Post<BiosSecretResponse>("/api/agent/bios/secret", r, ct);
        /// <summary>bios_update: kopien før opdateringen, meldinger (også efter genstart) og pakken til netop denne kørsel.</summary>
        public BiosUpdateIo BiosUpdateIo => new(
            async (r, ct) => await Post<BiosUpdatePostResponse>("/api/agent/bios/update", r, ct),
            async (path, file, ct) => await Download(path, file, ct));
        /// <summary>Det, iPad-jobs (iPad via USB) skal bruge fra serveren.</summary>
        public UsbIpadIo UsbIo => new(
            (r, ct) => Post<UsbIpadSecretResponse>("/api/agent/usb-ipad/secret", r, ct),
            PutLargeFile,
            async (path, file, ct) => await Download(path, file, ct),
            (path, body, ct) => Post(path, body, ct));
        /// <summary>Hent en fil; svaret giver serverens kontrolsum og signatur (til agentfiler).</summary>
        public async Task<(string? Hash, string? Signature)> Download(string path, string file, CancellationToken ct = default)
        {
            using var res = await http.SendAsync(Req(HttpMethod.Get, path), HttpCompletionOption.ResponseHeadersRead, ct);
            res.EnsureSuccessStatusCode();
            await using (var fs = System.IO.File.Create(file)) await res.Content.CopyToAsync(fs, ct);
            return (Header(res, AgentSigning.HashHeader), Header(res, AgentSigning.SignatureHeader));
        }
        private static string? Header(HttpResponseMessage r, string name) => r.Headers.TryGetValues(name, out var v) ? v.FirstOrDefault() : null;
        public async Task<string> GetText(string path, CancellationToken ct = default)
        {
            using var res = await http.SendAsync(Req(HttpMethod.Get, path), ct);
            res.EnsureSuccessStatusCode();
            return await res.Content.ReadAsStringAsync(ct);
        }
        public async Task Upload(string path, string file, CancellationToken ct = default)
        {
            await using var fs = System.IO.File.OpenRead(file);
            using var req = Req(HttpMethod.Post, path);
            req.Content = new StreamContent(fs);
            req.Content.Headers.ContentType = new("application/gzip");
            using var res = await http.SendAsync(req, ct);
            res.EnsureSuccessStatusCode();
        }
        public static async Task<EnrollResponse> Enroll(HttpClient http, string server, EnrollRequest req, CancellationToken ct = default)
        {
            using var res = await http.PostAsJsonAsync(Net.Base(server) + "/api/agent/enroll", req, Json, ct);
            if (res.StatusCode == System.Net.HttpStatusCode.Forbidden) throw new UnauthorizedAccessException("Forkert tilmeldingsnøgle");
            res.EnsureSuccessStatusCode();
            return (await res.Content.ReadFromJsonAsync<EnrollResponse>(Json, ct))!;
        }
    }

    public sealed class Agent(HttpClient http)
    {
        private readonly SemaphoreSlim _wake = new(0, 1);
        private string _version = "";

        public async Task<int> Run()
        {
            using var stop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.Cancel();
            // Overgang fra WinStyring: flyt til Fleetdeck og afslut – den nye tjeneste overtager
            if (Legacy.Running && Legacy.Migrate() == 0)
            {
                // vent på, at den gamle tjeneste bliver slået fra (ellers genstarter den os igen og igen)
                try { await Task.Delay(TimeSpan.FromMinutes(2), stop.Token); } catch (OperationCanceledException) { }
                return 0;
            }
            Legacy.CopyConfig();
            var cfg = AgentConfig.Load();
            if (cfg is null) { Log.Warn($"Ingen konfiguration i {AgentConfig.File}. Kør install.sh."); return 1; }
            TlsPinning.Set(cfg.TlsPin);
            // macOS: den faste lokale signatur ændrer filen – meld stadig serverens version, ellers opdaterer den i ring
            _version = Mac.Is ? MacSign.ReportedVersion(Environment.ProcessPath!) : HashOf(Environment.ProcessPath!);
            if (Mac.Is && MacSign.EnsureSelfSigned(_version)) return 1;   // launchd (KeepAlive) starter den signerede fil
            Log.Info($"Fleetdeck Linux-agent {_version} starter (server {cfg.Server})");
            CleanupOld();
            LowPriority();
            if (Unraid.Is) Unraid.EnsureScripts();   // start-scripts på USB-stikket følger med agentens version
            MacTray.Start(cfg, _version);   // macOS: status til Fleetdeck.app i menulinjen (også mens den venter på tilmelding)
            // macOS "Hold vågen på strøm": afstem først (sidst kendte indstilling, batteri = dvale), også før serveren kan nås
            KeepAwakeMac.Start(() => { if (_wake.CurrentCount == 0) _wake.Release(); }, stop.Token);
            if (cfg.Token.Length == 0) MacTray.Failed("Venter på at kunne tilmelde sig serveren");
            // macOS: ikonet i menulinjen kører hos brugeren, og opsætningsvinduet kommer frem, hvis Mac'en ikke kan tilmelde sig selv
            _ = MacMenuBar.Loop(cfg, stop.Token);
            await EnsureEnrolled(cfg, stop.Token);
            var api = new Api(http, cfg);
            await PinSigningKey(api, cfg, stop.Token);
            var jobs = new JobRunner(api, cfg);
            // "Genstart nu"/"Sluk nu" fra skærmen: Fleetdeck får besked (jobbets output og revisionsloggen), før maskinen går ned
            Power.Report = async mode =>
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                cts.CancelAfter(TimeSpan.FromSeconds(8));
                try { await api.Post("/api/agent/action", new AgentActionRequest { Action = "power_now", Id = mode }, cts.Token); }
                catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException) { Log.Warn($"Kunne ikke melde {PlannedPower.NowLabel(mode)}: {e.Message}"); }
            };
            _ = MacTray.CommandLoop(api, cfg, () => { if (_wake.CurrentCount == 0) _wake.Release(); }, stop.Token);
            Drives.Start(api, () => jobs.SendHealth(stop.Token), stop.Token);   // netværksdrev: vagt hvert minut + fundne drev til serveren
            if (!Unraid.Is)   // iPad via USB: vagten kigger kun efter iPads, når udvidelsen er installeret
                _ = Task.Run(() => new UsbIpadWatcher((r, ct) => api.Post("/api/agent/usb-ipads", r, ct), Log.Info).Run(stop.Token));
            _ = ConnectHub(cfg, stop.Token);
            // BIOS-opdatering: meld resultatet efter genstarten (og meldinger, serveren ikke fik)
            if (!Mac.Is) _ = BiosUpdateJob.ResumeLoop(BiosUpdates.Updater, api.BiosUpdateIo, BiosUpdates.Store, Log.Info, stop.Token);
            // ESP Flasher på denne maskine: http://localhost:<port> er en sikker side for Chrome og Edge – uden certifikat (se LocalWeb)
            if (!Unraid.Is)
                _ = Task.Run(() => LocalWeb.Run(LocalWeb.ResolvePort(cfg.LocalWebPort), () => cfg.Server, Log.Info, MacTray.LocalUrl, stop.Token,
                    (path, q, ct) => BiosUsbAgent.Local(api, path, q, ct)));   // /_fd/: USB-stik med BIOS-opdateringen (Linux og Mac)
            var poll = 30;
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var nonce = CommandSigning.NewNonce();
                    var r = await api.Post<CheckinResponse>("/api/agent/checkin",
                        new CheckinRequest { AgentVersion = _version, Arch = Arch, Facts = Collect.Live(), Nonce = nonce, Trust = TrustSetup.Report(cfg, _trustError),
                            KeepAwake = KeepAwakeMac.Outgoing(), Caps = [TerminalPolicy.Capability, LiveLogPolicy.Capability] }, stop.Token);
                    _tlsFailures = 0;
                    // signerede jobs: med pinnet nøgle bruges kun det signerede indhold (se docs/sikkerhed-agent.md)
                    var gate = CommandGate.Apply(r, cfg, CommandSigning.TokenId(cfg.Token), nonce, DateTime.UtcNow);
                    if (gate.ConfigChanged) { cfg.Save(); Log.Info($"Serverens jobnøgle er pinnet ({AgentSigning.Fingerprint(cfg.JobKey!)}). Usignerede jobs afvises herefter."); }
                    if (gate.Error != _trustError)
                    {
                        if (gate.Error is { } te) Log.Warn(te); else Log.Info("Signerede svar fra serveren godkendes igen");
                        _trustError = gate.Error;
                    }
                    poll = Math.Max(10, r.PollSeconds);
                    KeepAwakeMac.FromServer(r.KeepAwake);   // afvist/usigneret svar: null = behold den sidst kendte
                    await MacTray.Checked(api, cfg, r, gate.Error, stop.Token);
                    if (r.AgentVersion.Length > 0 && r.AgentVersion != _version && await UpdateSelf(api, cfg, r.AgentVersion, gate.AgentSha256, stop.Token)) return Restart();
                    if (await MoveServer(cfg, r.ServerUrl, gate.Pinned ? gate.TlsPin : null, stop.Token)) return Restart();
                    if (gate.Trusted)
                    {
                        Drives.Want(r, () => jobs.SendHealth(stop.Token));   // netværksdrev: i baggrunden, når listen er ændret
                        Keeper.Tick(r.KeepRunning);   // "Hold kørende": start stoppede tjenester igen med det samme
                        Printers.Want(r.Printers, r.HealthDue, () => jobs.SendHealth(stop.Token));   // printere: læg på og hold på (i baggrunden)
                        Printers.Scout(r.PrinterSearchAt, () => jobs.SendHealth(stop.Token), stop.Token);   // find printere på netværket, når Fleetdeck søger
                        TerminalAgent.Start(gate.Terminals, cfg.Server, cfg.Token, Net.Handler, (c, rw) => UnixPty.Start(c, rw, Environment.ProcessPath!), Log.Info, stop.Token);
                        LiveLogAgent.Start(gate.LiveLogs, cfg.Server, cfg.Token, Net.Handler, SysLogUnix.Live, Log.Info, stop.Token);   // live-log til siden (kun læsning)
                        await HelpdeskUnix.Deliver(api, r.Helpdesk, stop.Token);   // Beskeder: svarvindue (Mac: Fleetdeck.app, Linux: yad/zenity/kdialog)
                    }
                    await MacAppSync.Tick(api, cfg, stop.Token);   // macOS: Fleetdeck.app lægges på, hvis den mangler, og følger agentens version (disk-imaget er signeret)
                    CheckLoop.Start(r.Checks, res => api.Post("/api/agent/checks", res, stop.Token), msg => Console.Error.WriteLine(msg));   // overvågning herfra
                    await jobs.FlushResults(stop.Token);   // jobresultater, der ikke nåede frem sidst
                    if (r.InventoryDue) await jobs.SendInventory(stop.Token);
                    else if (r.HealthDue) await jobs.SendHealth(stop.Token);
                    if (cfg.PausedUntil > DateTime.UtcNow && r.Jobs.Count > 0)
                    {
                        Log.Info($"Sat på pause fra menulinjen indtil {cfg.PausedUntil.Value.ToLocalTime():HH:mm} – {r.Jobs.Count} job(s) venter");
                        r.Jobs.Clear();
                    }
                    foreach (var j in r.Jobs)
                        if (!await jobs.Run(j, stop.Token)) return 0;   // false = agenten skal stoppe (fjernet)
                }
                catch (Exception e) when (!stop.Token.IsCancellationRequested)
                {
                    Log.Warn($"Check-in fejlede: {e.Message}"); MacTray.Failed($"Check-in fejlede: {e.Message}");
                    // https-certifikatet passer ikke længere (fx skiftet på serveren): tilbage til den forrige adresse, hvis den beviser at være samme server
                    if (IsTlsFailure(e) && ++_tlsFailures >= 20 && await FallBack(cfg, stop.Token)) return Restart();
                }
                try { await _wake.WaitAsync(TimeSpan.FromSeconds(poll), stop.Token); } catch (OperationCanceledException) { }
            }
            return 0;
        }

        private string? _trustError;
        private int _tlsFailures;

        /// <summary>Bevidst genstart (launchd starter den nye med det samme): "Hold vågen" slippes ikke imens (låget kan være lukket).</summary>
        private static int Restart() { KeepAwakeMac.Restarting = true; return 1; }

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
        private async Task<bool> FallBack(AgentConfig cfg, CancellationToken ct)
        {
            _tlsFailures = 0;
            if (cfg.PreviousServer is not { Length: > 0 } prev || cfg.JobKey is not { Length: > 0 }) return false;
            try
            {
                var nonce = CommandSigning.NewNonce();
                using var req = new HttpRequestMessage(HttpMethod.Post, Net.Base(prev) + "/api/agent/checkin");
                req.Headers.Authorization = new("Bearer", cfg.Token);
                req.Content = JsonContent.Create(new CheckinRequest { AgentVersion = _version, Arch = Arch, Facts = Collect.Live(), Nonce = nonce, Trust = TrustSetup.Report(cfg, "https-forbindelsen fejler") }, options: Api.Json);
                using var res = await http.SendAsync(req, ct);
                Net.Base(cfg.Server);   // socket-filen (hvis nogen) følger den aktuelle adresse igen
                res.EnsureSuccessStatusCode();
                var r = await res.Content.ReadFromJsonAsync<CheckinResponse>(Api.Json, ct);
                if (r is null || !CommandSigning.Verify(cfg.JobKey, r.Signed, CommandSigning.TokenId(cfg.Token), nonce, DateTime.UtcNow).Ok) return false;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { return false; }
            Log.Warn($"https-forbindelsen til {cfg.Server} fejler: skifter tilbage til {prev}");
            (cfg.Server, cfg.PreviousServer) = (prev, cfg.Server);
            cfg.Save();
            Environment.Exit(1);
            return true;
        }

        private async Task EnsureEnrolled(AgentConfig cfg, CancellationToken stop)
        {
            if (cfg.Token.Length > 0) return;
            if (cfg.PendingKey is not { Length: > 0 } && (cfg.Access is not null || cfg.AccessState is not null)) { await WaitForAccess(cfg, stop); return; }
            if (cfg.PendingKey is not { Length: > 0 } key)
            {
                // macOS: ikke gå ned i ring (så når menulinjen aldrig at vise opsætningsvinduet) – vent, til opsætningen installerer igen
                if (Mac.Is) { MacTray.Failed("Ikke tilmeldt – åbn Fleetdeck for at sætte Mac'en op"); Log.Warn("Agenten er ikke tilmeldt og har ingen tilmeldingsnøgle – venter på opsætningen"); await Task.Delay(Timeout.Infinite, stop); }
                throw new InvalidOperationException("Agenten er ikke tilmeldt og har ingen tilmeldingsnøgle");
            }
            var delay = 10;
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var res = await Api.Enroll(http, cfg.Server, new EnrollRequest(key, cfg.PendingName ?? Environment.MachineName, cfg.PendingProvision,
                        Collect.SystemInfo(), ProfileId: cfg.PendingProfile, Platform: Platforms.Current, PreviousToken: cfg.PendingPreviousToken), stop);
                    cfg.MachineId = res.MachineId; cfg.Token = res.Token; cfg.PendingKey = null; cfg.PendingName = null; cfg.PendingProvision = false; cfg.PendingProfile = null;
                    cfg.PendingPreviousToken = null;
                    TrustSetup.FromEnroll(cfg, res);
                    cfg.Save();
                    Log.Info($"Tilmeldt som maskine {res.MachineId}");
                    return;
                }
                catch (UnauthorizedAccessException)
                {
                    Log.Warn("Tilmeldingsnøglen blev afvist");
                    // macOS: vent i stedet for at gå ned i ring, så opsætningsvinduet kan komme frem hos brugeren (en ny "install" genstarter os)
                    if (Mac.Is) { MacMenuBar.KeyRejected = true; MacTray.Failed("Tilmeldingsnøglen blev afvist – åbn Fleetdeck og sæt Mac'en op igen"); await Task.Delay(Timeout.Infinite, stop); }
                    throw;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Log.Info($"Venter på serveren for at tilmelde sig: {e.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(delay), stop);
                    delay = Math.Min(120, delay * 2);
                }
            }
        }

        /// <summary>
        /// "Anmod om adgang": spørg hvert 30. sekund (langsommere ved fejl), vis koden (statusfil og menulinje), og tilmeld med det samme,
        /// når administratoren har godkendt. Afvist eller udløbet: vent – en ny "install --request" starter tjenesten igen.
        /// </summary>
        private async Task WaitForAccess(AgentConfig cfg, CancellationToken stop)
        {
            var failures = 0;
            string? notified = null, lastError = null;
            while (!stop.IsCancellationRequested)
            {
                if (cfg.Access is not { } a)
                {
                    AccessStatus.Write(cfg, AccessStatus.View(cfg));
                    Log.Warn($"Anmodningen om adgang er afsluttet ({cfg.AccessState}). Kør install.sh eller \"fleetdeck-agent setup\" igen.");
                    await Task.Delay(Timeout.Infinite, stop);
                    return;
                }
                var view = AccessStatus.View(cfg);
                try
                {
                    // selvsigneret certifikat: det fastlåste fingeraftryk; ellers almindelig certifikatkontrol
                    var obs = Net.IsUnix(cfg.Server) ? null : new AccessTlsObserver(a.PinNeeded ? a.Pin : null);
                    using var ah = obs?.Client();
                    var r = await AccessRequestClient.Poll(ah ?? http, Net.Base(cfg.Server), a.Id, a.Secret, stop);
                    failures = 0; lastError = null;
                    view.State = r.State; view.Reason = r.Reason;
                    if (r.State == AccessStates.Issued && AccessRequestClient.OpenKey(a, r) is { } key)
                    {
                        // fingeraftrykket fra anmodningen er bekræftet af koden, administratoren sammenlignede
                        if (a.PinNeeded && a.Pin is { Length: > 0 } pin) { cfg.TlsPin = pin; TlsPinning.Add(pin); }
                        try
                        {
                            var res = await Api.Enroll(http, cfg.Server, new EnrollRequest(key, r.Name ?? a.Name, false, Collect.SystemInfo(), Platform: Platforms.Current,
                                PreviousToken: cfg.PendingPreviousToken, AccessSecret: a.Secret), stop);
                            cfg.MachineId = res.MachineId; cfg.Token = res.Token; cfg.Access = null; cfg.AccessState = null; cfg.AccessReason = null; cfg.PendingPreviousToken = null;
                            TrustSetup.FromEnroll(cfg, res);
                            cfg.Save();
                            AccessStatus.Write(cfg, null);
                            Log.Info($"Adgang godkendt: tilmeldt som maskine {res.MachineId}");
                            return;
                        }
                        catch (UnauthorizedAccessException) { Log.Warn("Engangskoden fra godkendelsen blev afvist – spørger igen"); }
                    }
                    else if (r.State == AccessStates.Issued) Log.Warn("Den godkendte engangskode kunne ikke åbnes – spørger igen");
                    else if (!AccessStates.Open(r.State))
                    {
                        cfg.Access = null; cfg.AccessState = r.State; cfg.AccessReason = r.Reason; cfg.Save();
                        continue;
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException || !stop.IsCancellationRequested)
                {
                    failures++; lastError = e.Message;
                    Log.Info($"Venter på serveren (anmodning om adgang): {e.Message}");
                    // udløbet, mens serveren ikke kunne nås: stop med at spørge
                    if (DateTime.UtcNow > a.Expires.AddMinutes(5))
                    {
                        cfg.Access = null; cfg.AccessState = AccessStates.Expired; cfg.Save();
                        continue;
                    }
                }
                // flere fejl i træk: sig det (statusfil, menulinje og "fleetdeck-agent status")
                if (failures >= AccessRequestClient.ShowProblemAfter) view.Problem = lastError;
                AccessStatus.Write(cfg, view);
                if (notified != view.Text) { Log.Info(view.Text); notified = view.Text; }
                await Task.Delay(AccessRequestClient.Backoff(failures), stop);
            }
        }

        private async Task ConnectHub(AgentConfig cfg, CancellationToken stop)
        {
            if (Net.IsUnix(cfg.Server)) return;   // gennem socket-filen: ingen live-forbindelse, agenten spørger selv jævnligt
            var hub = new HubConnectionBuilder()
                .WithUrl(cfg.Server.TrimEnd('/') + "/hubs/agent", o =>
                {
                    o.AccessTokenProvider = () => Task.FromResult<string?>(cfg.Token);
                    // samme TLS-regler som check-in (fastlåst nøgle til et selvsigneret certifikat)
                    o.HttpMessageHandlerFactory = h => { if (h is HttpClientHandler hc) hc.ServerCertificateCustomValidationCallback = (_, c, ch, e) => TlsPinning.Validate(null, c, ch, e); return h; };
                    o.WebSocketConfiguration = ws => ws.RemoteCertificateValidationCallback = TlsPinning.Validate;
                })
                .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(60)])
                .Build();
            hub.On(AgentHubMethods.Wake, () => { if (_wake.CurrentCount == 0) _wake.Release(); });
            while (!stop.IsCancellationRequested)
            {
                try { await hub.StartAsync(stop); return; }
                catch (Exception e) when (e is not OperationCanceledException) { Log.Info($"Live-forbindelse ikke klar: {e.Message}"); await Task.Delay(30_000, stop); }
            }
        }

        private static string HashOf(string file)
        {
            using var fs = System.IO.File.OpenRead(file);
            return Convert.ToHexStringLower(SHA256.HashData(fs))[..12];
        }

        private static void CleanupOld() { try { System.IO.File.Delete(Environment.ProcessPath + ".old"); } catch (IOException) { } }

        /// <summary>Lav prioritet for CPU og disk (også apt og dpkg som agenten starter), så kiosk/skrivebord altid går forrest.</summary>
        private static void LowPriority()
        {
            if (Mac.Is || Unraid.Is) return;   // macOS: i launchd-plisten; Unraid: nice/ionice i start-sløjfen
            if (!Init.IsSystemd || Shell.Out("systemctl show fleetdeck-agent -p CPUWeight --value") == "20") return;
            var r = Shell.Run("systemctl set-property fleetdeck-agent CPUWeight=20 IOWeight=20", TimeSpan.FromSeconds(20));
            Log.Info(r.ExitCode == 0 ? "Lav CPU- og diskprioritet er sat" : $"Kunne ikke sætte lav prioritet: {r.Output.Trim()}");
        }

        /// <summary>x64, arm64 eller arm – samme navne som serverens /api/agent/download?arch= (Intel-Mac med macOS ældre end 12: x64-legacy)</summary>
        private static string Arch => Mac.Is
            ? MacCompat.ArchFor(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture, Environment.OSVersion.Version)
            : (System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier.Contains("musl") ? "musl-" : "") +
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
            {
                System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
                System.Runtime.InteropServices.Architecture.Arm => "arm",
                _ => "x64",
            };

        /// <summary>Husk serverens nøgle første gang (tilmelding eller opgradering fra en agent uden signering).</summary>
        private static async Task PinSigningKey(Api api, AgentConfig cfg, CancellationToken ct)
        {
            if (cfg.SigningKey is { Length: > 0 }) return;
            try
            {
                var pem = (await api.GetText("/api/agent/signing-key", ct)).Trim();
                cfg.SigningKey = pem; cfg.Save();
                Log.Info($"Serverens nøgle er husket ({AgentSigning.Fingerprint(pem)}). Kun signerede opdateringer installeres herefter.");
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Security.Cryptography.CryptographicException or ArgumentException)
            { Log.Warn($"Kunne ikke hente serverens nøgle endnu: {e.Message}"); }
        }

        private static async Task<bool> UpdateSelf(Api api, AgentConfig cfg, string wanted, string? signedSha256, CancellationToken ct)
        {
            var exe = Environment.ProcessPath!; var tmp = exe + ".new";
            try
            {
                await PinSigningKey(api, cfg, ct);   // aldrig en opdatering uden en husket nøgle at tjekke den mod
                var (hash, sig) = await api.Download($"/api/agent/download?platform={Platforms.Current}&arch={Arch}", tmp, ct);
                var got = HashOf(tmp);
                if (got != wanted) { Log.Warn($"Opdatering afvist: hash {got} ≠ {wanted}"); SelfUpdate.Failed("den hentede fil passer ikke med serverens version"); System.IO.File.Delete(tmp); return false; }
                // aldrig "opdatere" til en anden slags agent (fx en WinStyring 2.x-server, der udleverer sin gamle agent)
                if (System.IO.File.ReadAllBytes(tmp).AsSpan().IndexOf("fleetdeck-agent.dll"u8) < 0)
                {
                    Log.Warn("Opdatering afvist: serveren udleverer ikke en Fleetdeck-agent – opgradér serveren til Fleetdeck");
                    SelfUpdate.Failed("serveren udleverer en anden agent (fx WinStyring 2.x) – opgradér serveren til Fleetdeck");
                    System.IO.File.Delete(tmp); return false;
                }
                if (AgentSigning.CheckPinned(tmp, cfg.SigningKey, hash, sig) is { Ok: false } bad)
                { Log.Warn($"Opdatering afvist: {bad.Why}"); SelfUpdate.Failed($"afvist: {bad.Why}"); System.IO.File.Delete(tmp); return false; }
                // med pinnet jobnøgle: filen skal også passe med den fulde SHA-256 i det signerede check-in-svar
                if (CommandGate.CheckUpdate(tmp, cfg.JobKey is { Length: > 0 }, signedSha256) is { Ok: false } unsigned)
                { Log.Warn($"Opdatering afvist: {unsigned.Why}"); SelfUpdate.Failed($"afvist: {unsigned.Why}"); System.IO.File.Delete(tmp); return false; }
                System.IO.File.SetUnixFileMode(tmp, (UnixFileMode)0b111_101_101);
                // macOS: fast lokal signatur, så Fuld diskadgang bliver (ellers ad hoc som før – opdateringen fortsætter uanset)
                if (Mac.Is) MacSign.Sign(tmp, got);
                // kopi i stedet for flyt: rename over den kørende fil er atomisk, så der er aldrig et øjeblik uden binær
                System.IO.File.Copy(exe, exe + ".old", true);
                System.IO.File.Move(tmp, exe, true);
                Log.Info($"Agent opdateret til {wanted}. Genstarter.");
                KeepAwakeMac.Restarting = true;
                Environment.Exit(1);   // systemd (Restart=always) starter den nye version
                return true;
            }
            catch (Exception e) { Log.Warn($"Selvopdatering fejlede: {e.Message}"); SelfUpdate.Failed(e.Message); try { System.IO.File.Delete(tmp); } catch (IOException) { } return false; }
        }

        private async Task<bool> MoveServer(AgentConfig cfg, string? url, string? newTlsPin, CancellationToken ct)
        {
            url = url?.Trim().TrimEnd('/');
            if (string.IsNullOrEmpty(url) || Net.IsUnix(cfg.Server) || string.Equals(url, cfg.Server.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) return false;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return false;
            // ny https-adresse med selvsigneret certifikat: fingeraftrykket kommer fra det signerede svar
            if (u.Scheme == "https") TlsPinning.Add(newTlsPin);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(TimeSpan.FromSeconds(10));
                using var res = await http.GetAsync(url + "/api/health", cts.Token);
                if (!res.IsSuccessStatusCode) return false;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return false; }
            // den nye adresse skal være den samme server: samme nøgle
            if (cfg.SigningKey is { Length: > 0 } pinned)
            {
                try
                {
                    var pem = (await http.GetStringAsync(url + "/api/agent/signing-key", ct)).Trim();
                    if (AgentSigning.Fingerprint(pem) != AgentSigning.Fingerprint(pinned)) { Log.Warn($"Flytning til {url} afvist: serveren har en anden nøgle"); return false; }
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Security.Cryptography.CryptographicException or ArgumentException)
                { Log.Warn($"Flytning til {url} afvist: {e.Message}"); return false; }
            }
            Log.Info($"Serveren er flyttet: {cfg.Server} -> {url}. Genstarter.");
            cfg.PreviousServer = cfg.Server;
            if (u.Scheme == "https" && newTlsPin is { Length: > 0 }) cfg.TlsPin = newTlsPin;
            cfg.Server = url; cfg.Save();
            Environment.Exit(1);
            return true;
        }
    }
}
