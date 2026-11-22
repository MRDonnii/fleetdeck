using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Fleetdeck.Agent.Windows;
using Fleetdeck.Shared;

// Fleetdeck.exe
//   install --server <url> --key <nøgle> [--provision] [--profile <id>] [--deploy <id>] [--disable-smartapp]
//                                                          installér som Windows-tjeneste og tilmeld
//   install --server <url> --request [--message <tekst>] [--name <navn>]
//                                                          ingen nøgle: bed administratoren om adgang (exit 9 = venter, koden vises)
//   uninstall                                              fjern tjenesten
//   run [--once] [--dry-run] [--dir <mappe>]               kør i konsollen (test)
//   setup                                                  opsætningsvinduet (også ved dobbeltklik på filen)
//   (ingen argumenter)                                     kører som Windows-tjeneste – eller viser opsætningsvinduet,
//                                                          hvis filen er åbnet af en bruger
const string ServiceName = "Fleetdeck";

string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
bool Flag(string name) => args.Contains(name);
var cmd = args.FirstOrDefault(a => !a.StartsWith('-')) ?? "service";
// Dobbeltklik på filen: ingen argumenter og ikke startet af Windows' tjenestestyring -> opsætningsvinduet
// Dobbeltklik på filen: er Fleetdeck installeret, vises status (ikonet i proceslinjen) – ellers installationsvinduet
if (cmd == "service" && !Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService())
    cmd = File.Exists(new AgentOptions().ConfigFile) || Installer.Sc($"query {ServiceName}") == 0 ? "status" : "setup";
try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (IOException) { }   // æøå i installationsteksten

switch (cmd)
{
    case "install": return await Install();
    case "verify": return IsAdmin() ? await Verify(DateTime.UtcNow.AddDays(-30), false, TimeSpan.FromSeconds(60)) : 5;
    case "setup": return Setup();
    case "tray": return Tray(showWindow: false);
    case "status": return Tray(showWindow: true);
    case "tray-cmd": return args.Length > 1 ? TrayApp.WriteAdminCommand(args[1]) : 2;
    case "uninstall": return Uninstall();
    case "run": return await RunHost(service: false);
    case "service": return await RunHost(service: true);
    default:
        Console.WriteLine("Brug: Fleetdeck.exe install --server <url> --key <nøgle> [--provision] | uninstall | run [--once] [--dry-run]");
        return 2;
}

async Task<int> RunHost(bool service)
{
    var opt = new AgentOptions { DryRun = Flag("--dry-run"), Once = Flag("--once"), IsService = service };
    // Overgang fra WinStyring: den gamle tjeneste har hentet Fleetdeck som "opdatering" – flyt og stop
    if (service && Installer.RunningAsLegacy && Installer.MigrateFromLegacy(opt, m => { try { File.AppendAllText(Path.Combine(opt.Dir, "agent.log"), $"{DateTime.Now:HH:mm:ss} {m}\r\n"); } catch (IOException) { } }))
        return 0;
    if (Arg("--dir") is { } d) opt.Dir = d;
    // Selvreparation ved hver start: også ældre installationer (hvor userwork gav brugere "Rediger") får de rigtige rettigheder
    if (service && AgentOptions.IsSystem)
        Hardening.Apply(opt, m => { try { File.AppendAllText(opt.LogFile, $"{DateTime.Now:HH:mm:ss} {m}\r\n"); } catch (IOException) { } }, Installer.InstallDir);
    // Test uden tjeneste: "run --server <url> --key <nøgle> --dir <mappe>" tilmelder sig ved første kørsel
    if (!service && Arg("--server") is { } srv && Arg("--key") is { } k && !File.Exists(opt.ConfigFile))
        new AgentConfig { Server = srv.TrimEnd('/'), PendingKey = k }.Save(opt.ConfigFile);
    var b = Host.CreateApplicationBuilder();
    b.Services.AddWindowsService(o => o.ServiceName = ServiceName);
    b.Logging.ClearProviders();
    b.Logging.AddProvider(new FileLoggerProvider(opt.LogFile));
    if (!service) b.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
    b.Logging.AddFilter("System.Net.Http", LogLevel.Warning);
    b.Logging.AddFilter("Microsoft", LogLevel.Warning);
    b.Services.AddSingleton(opt);
    // https med selvsigneret certifikat: det fastlåste fingeraftryk (TlsPin) accepteres ud over certifikater, Windows stoler på
    try { TlsPinning.Set(AgentConfig.Load(opt.ConfigFile)?.TlsPin); } catch (Exception e) when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { }
    b.Services.AddHttpClient("server", c => { c.Timeout = TimeSpan.FromMinutes(10); c.DefaultRequestHeaders.UserAgent.ParseAdd("Fleetdeck-Agent/2.0"); })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { SslOptions = { RemoteCertificateValidationCallback = TlsPinning.Validate } });
    b.Services.AddSingleton<Runner>();
    b.Services.AddSingleton<Collectors>();
    b.Services.AddSingleton<Winget>();
    b.Services.AddSingleton<JobRunner>();
    b.Services.AddSingleton<DriveMapper>();
    b.Services.AddSingleton<PrinterKeeper>();
    b.Services.AddSingleton<TrayBridge>();
    b.Services.AddSingleton<HelpdeskBridge>();
    b.Services.AddSingleton<LogCollector>();
    b.Services.AddHostedService<AgentWorker>();
    await b.Build().RunAsync();
    return 0;
}

// Opsætningsvinduet kræver administrator (det installerer en tjeneste): start igen med UAC-spørgsmålet, hvis nødvendigt.
// Ikonet i proceslinjen: én kopi pr. bruger. Startes den igen, viser den kørende kopi sit statusvindue.
int Tray(bool showWindow)
{
    Native.FreeConsole();
    if (!showWindow && !TrayApp.Autostart) return 0;   // slået fra af brugeren
    using var show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\FleetdeckTrayShow", out _);
    using var single = new Mutex(true, @"Local\FleetdeckTray", out var first);
    if (!first) { if (showWindow) show.Set(); return 0; }
    var t = new Thread(() =>
    {
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
        using var app = new TrayApp(showWindow);
        // en anden kopi bad om at vise vinduet
        var ui = System.Threading.SynchronizationContext.Current;
        ThreadPool.RegisterWaitForSingleObject(show, (_, _) => ui?.Post(_ => app.ShowFromOther(), null), null, -1, false);
        System.Windows.Forms.Application.Run(app);
    });
    t.SetApartmentState(ApartmentState.STA);
    t.Start(); t.Join();
    return 0;
}

int Setup()
{
    Native.FreeConsole();   // ingen sort konsol bag vinduet ved dobbeltklik
    if (!IsAdmin())
    {
        try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "setup") { UseShellExecute = true, Verb = "runas" }); return 0; }
        catch (System.ComponentModel.Win32Exception) { return 5; }   // brugeren sagde nej til UAC
    }
    var current = AgentConfig.Load(new AgentOptions().ConfigFile)?.Server;
    var t = new Thread(() =>
    {
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
        System.Windows.Forms.Application.Run(new SetupForm(current));
    });
    t.SetApartmentState(ApartmentState.STA);   // WinForms og udklipsholderen kræver STA
    t.Start(); t.Join();
    return 0;
}

static bool IsAdmin() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

static int Sc(string arguments)
{
    using var p = Process.Start(new ProcessStartInfo("sc.exe", arguments) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
    p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    return p.ExitCode;
}

async Task<int> Install()
{
    if (!IsAdmin()) { Console.Error.WriteLine("Kør som administrator."); return 5; }
    var server = Arg("--server")?.TrimEnd('/');
    var request = Flag("--request");
    var key = request ? null : Arg("--key");
    if (server is null || (key is null && !request)) { Console.Error.WriteLine("Mangler --server og --key (eller --request for at bede om adgang)"); return 2; }
    var opt = new AgentOptions();
    // "Anmod om adgang" på en pc, der allerede er tilmeldt, ville fjerne dens token: kun med --force (opsætningen spørger først)
    if (request && !Flag("--force"))
    {
        try
        {
            if (AgentConfig.Load(opt.ConfigFile)?.Token is { Length: > 0 })
            {
                Console.Error.WriteLine("Pc'en er allerede tilmeldt Fleetdeck. Brug --force (i PowerShell: $env:FLEETDECK_FORCE = '1') for at starte forfra og bede om adgang igen.");
                return 10;
            }
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { }
    }
    int? profile = int.TryParse(Arg("--profile"), out var pi) ? pi : null;
    int? deploy = int.TryParse(Arg("--deploy"), out var di) ? di : null;

    Console.WriteLine($"Fleetdeck: installerer agenten mod {server}");
    // Smart App Control blokerer ellers programmer, Fleetdeck installerer – kun når brugeren har sagt ja til det
    var rebootNeeded = false;
    var installStart = DateTime.UtcNow;
    if (Flag("--disable-smartapp")) { var (msg, rb) = SmartAppControl.Disable(); Console.WriteLine(msg); rebootNeeded = rb; }
    var hadService = File.Exists(opt.ConfigFile);
    Installer.Sc($"stop {ServiceName}");
    await Task.Delay(1500);
    Installer.CopySelf();
    Installer.PrepareDirs(opt);

    var cfg = new AgentConfig { Server = server };
    // Geninstallation på samme maskine: det gamle token beviser, at det er den samme maskine (ellers venter den på godkendelse i Fleetdeck)
    AgentConfig? old = null; string? oldToken = null;
    try { old = AgentConfig.Load(opt.ConfigFile); oldToken = old?.PlainToken(); }
    catch (Exception e) when (e is IOException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException or FormatException or UnauthorizedAccessException) { }
    TrustSetup.Keep(cfg, old);
    var cliPin = Arg("--tls-pin");
    if (cliPin is not null && TlsPinning.Normalize(cliPin) is null) { Console.Error.WriteLine("Ugyldigt --tls-pin (forventer sha256//… eller hex)"); return 2; }
    TlsPinning.Set(TlsPinning.Normalize(cliPin) ?? cfg.TlsPin);
    if (request)
    {
        // "Anmod om adgang": ingen nøgle – tjenesten venter på administratoren og tilmelder sig selv, når han har godkendt
        var name = Arg("--name") is { Length: > 0 } n ? n : Environment.MachineName;
        var obs = new AccessTlsObserver(null, firstContact: true);
        try
        {
            using var ah = obs.Client();
            var facts = new SystemFacts { Hostname = Environment.MachineName, Os = System.Runtime.InteropServices.RuntimeInformation.OSDescription };
            // "Anmod igen": den tidligere anmodning fra denne pc lukkes, når den nye er oprettet (hemmeligheden beviser, at den er vores)
            var replaces = old?.PlainAccess() is { } prev && old.Server.TrimEnd('/') == server ? new AccessReplace(prev.Id, prev.Secret) : null;
            var p = await AccessRequestClient.Request(ah, obs, server, name, Platforms.Windows, facts, Environment.UserName, Arg("--message"), replaces);
            cfg.SetAccess(p);
            cfg.TlsPin = TlsPinning.Normalize(cliPin) ?? cfg.TlsPin;
            cfg.PendingPreviousToken = oldToken is { Length: > 0 } ? old!.Token : null;
            Console.WriteLine($"Anmodning sendt. Kode: {p.Code}");
            Console.WriteLine("Giv koden til administratoren. Når han har godkendt, tilmelder pc'en sig selv.");
        }
        catch (AccessRequestException e) { Console.Error.WriteLine(e.Message); Restart(); return 3; }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { Console.Error.WriteLine($"Serveren kan ikke nås ({e.Message}). Prøv igen."); Restart(); return 6; }
        cfg.Save(opt.ConfigFile);
        try { File.Delete(Path.Combine(opt.UserWork, "status.json")); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        if (!Installer.CreateService()) { Console.Error.WriteLine("Kunne ikke oprette tjenesten"); return 4; }
        Installer.RemoveLegacy();
        Console.WriteLine("Agenten er installeret som Windows-tjenesten \"Fleetdeck Agent\" og venter på godkendelse.");
        return 9;

        // anmodningen fejlede: konfigurationen er urørt, så den tidligere tjeneste startes igen (ellers står ikonet på "svarer ikke")
        void Restart() { if (hadService) Installer.Sc($"start {ServiceName}"); }
    }
    try
    {
        using var http = new HttpClient(new SocketsHttpHandler { SslOptions = { RemoteCertificateValidationCallback = TlsPinning.Validate } }) { Timeout = TimeSpan.FromSeconds(20) };
        var res = await ServerClient.Enroll(http, server, new EnrollRequest(key!, Environment.MachineName, Flag("--provision"), null, profile, deploy,
            Extensions: Arg("--extensions")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(), PreviousToken: oldToken));
        cfg.MachineId = res.MachineId; cfg.SetToken(res.Token);
        TrustSetup.FromEnroll(cfg, res, cliPin);
        Console.WriteLine($"Tilmeldt som maskine {res.MachineId}.");
        if (res.Pending) Console.WriteLine("Navnet findes allerede i Fleetdeck: maskinen venter på, at en administrator godkender den.");
    }
    catch (UnauthorizedAccessException e) { Console.Error.WriteLine(e.Message); return 3; }
    catch (Exception e)
    {
        // Intet netværk endnu (fx under Windows-installationen): tjenesten tilmelder sig selv senere
        cfg.PendingKey = key; cfg.PendingProvision = Flag("--provision"); cfg.PendingProfile = profile; cfg.PendingDeploy = deploy;
        cfg.TlsPin = TlsPinning.Normalize(cliPin) ?? cfg.TlsPin; cfg.PendingPreviousToken = oldToken is { Length: > 0 } ? old!.Token : null;
        Console.WriteLine($"Serveren kan ikke nås lige nu ({e.Message}). Tjenesten tilmelder sig automatisk, når der er netværk.");
    }
    cfg.Save(opt.ConfigFile);

    try { File.Delete(Path.Combine(opt.UserWork, "status.json")); } catch (IOException) { } catch (UnauthorizedAccessException) { }   // ingen gammel status
    if (!Installer.CreateService()) { Console.Error.WriteLine("Kunne ikke oprette tjenesten"); return 4; }
    Installer.RemoveLegacy();   // en gammel WinStyring-agent på samme pc fjernes
    Console.WriteLine("Agenten er installeret som Windows-tjenesten \"Fleetdeck Agent\". Bekræfter første forbindelse…");
    return await Verify(installStart, rebootNeeded, Flag("--no-wait") ? TimeSpan.Zero : TimeSpan.FromSeconds(120));
}

// verify: kør bekræftelsen igen (fx "Tjek igen" i installationsvinduet)
async Task<int> Verify(DateTime since, bool rebootNeeded, TimeSpan timeout)
{
    if (timeout == TimeSpan.Zero) return 0;
    var (code, message) = await Installer.VerifyFirstContact(new AgentOptions(), since, timeout);
    if (code == 0 && rebootNeeded) { Console.WriteLine(message + " Genstart pc'en for at gøre ændringen af Smart App Control færdig."); return 8; }
    (code == 0 ? Console.Out : Console.Error).WriteLine(message);
    return code;
}

int Uninstall()
{
    if (!IsAdmin()) { Console.Error.WriteLine("Kør som administrator."); return 5; }
    Sc($"stop {ServiceName}");
    Thread.Sleep(2000);
    Sc($"delete {ServiceName}");
    Autostart.Remove();
    var opt = new AgentOptions();
    try { Directory.Delete(opt.Dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    try { File.Delete(TrayBridge.ShortcutPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    var installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Fleetdeck");
    // den kørende .exe kan ikke slette sig selv: planlæg sletning
    Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 3 >nul & rmdir /s /q \"{installDir}\"") { CreateNoWindow = true, UseShellExecute = false });
    Console.WriteLine("Fleetdeck-agenten er fjernet.");
    return 0;
}
