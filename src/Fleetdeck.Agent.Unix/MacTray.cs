using System.Text.Json;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Unix;

/// <summary>
/// macOS: tjenesten (root) ↔ Fleetdeck.app i menulinjen (brugeren). Samme kontrakt som Windows' TrayBridge:
/// status skrives til /Library/Application Support/Fleetdeck/status.json (alle kan læse), ordrer kommer som filer i
/// …/Fleetdeck/cmd (alle må lægge filer – kun "opdatér", "synkronisér", "udsæt", "tjek adgang" og "hold vågen") og /usr/local/fleetdeck/cmd
/// (kun root – pause og udvidelser; lagt dér af "fleetdeck-agent tray-cmd", som appen starter med administratoradgangskoden).
/// </summary>
public static class MacTray
{
    public static TrayStatus Status { get; } = new();
    private static readonly Lock L = new();
    private static DateTime _accessAt = DateTime.MinValue, _extAt = DateTime.MinValue, _restartedFor = DateTime.MinValue;
    private static readonly DateTime Started = DateTime.UtcNow;

    public static void Start(AgentConfig cfg, string version)
    {
        if (!Mac.Is) return;
        Status.Machine = Environment.MachineName; Status.Server = cfg.Server; Status.Version = version;
        Status.Enrolled = cfg.MachineId > 0 && cfg.Token.Length > 0;
        Status.PausedUntil = cfg.PausedUntil > DateTime.UtcNow ? cfg.PausedUntil : null;
        try
        {
            Directory.CreateDirectory(MacPaths.SupportDir);
            File.SetUnixFileMode(MacPaths.SupportDir, (UnixFileMode)0b111_101_101);
            Directory.CreateDirectory(MacPaths.UserCmdDir);
            // som /tmp: alle må lægge en fil, ingen kan se eller slette andres (sticky + ingen læseret)
            Shell.Run($"chown root:wheel {Pkg.Q(MacPaths.UserCmdDir)}; chmod 1733 {Pkg.Q(MacPaths.UserCmdDir)}", TimeSpan.FromSeconds(10));
            Directory.CreateDirectory(MacPaths.AdminCmdDir);
            File.SetUnixFileMode(MacPaths.AdminCmdDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warn($"Menulinje-status: {e.Message}"); }
        RefreshAccess(force: true);
        Write();
    }

    /// <summary>ESP Flasher på denne maskine: adressen, mens agenten lytter på localhost (null = ikke tilgængelig).</summary>
    public static void LocalUrl(string? url)
    {
        lock (L) Status.LocalUrl = url;
        Write();
    }

    public static void Write()
    {
        if (!Mac.Is) return;
        try
        {
            string json;
            lock (L) { Status.Written = DateTime.UtcNow; json = JsonSerializer.Serialize(Status, TrayCommands.Json); }
            var tmp = MacPaths.StatusFile + ".tmp";
            File.WriteAllText(tmp, json);
            File.SetUnixFileMode(tmp, (UnixFileMode)0b110_100_100);
            File.Move(tmp, MacPaths.StatusFile, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Efter hvert check-in: forbindelse, profil, nøgletal, pause, planlagt genstart, udvidelser (hvert 10. min.) og Fuld diskadgang.</summary>
    public static async Task Checked(Api api, AgentConfig cfg, CheckinResponse r, string? security, CancellationToken ct)
    {
        if (!Mac.Is) return;
        lock (L)
        {
            var st = Status;
            st.Security = security;   // afviste usignerede/ugyldige jobs (vises i menulinjen)
            st.Connected = true; st.Error = null; st.LastContact = DateTime.UtcNow; st.Server = cfg.Server; st.Enrolled = cfg.Token.Length > 0;
            st.Profile = r.Profile; st.Upgrades = r.Upgrades; st.Alerts = r.Alerts; st.Health = r.Health; st.WebUrl = r.WebUrl ?? cfg.Server;
            st.PausedUntil = cfg.PausedUntil > DateTime.UtcNow ? cfg.PausedUntil : null;
            var p = Power.Current();
            st.PowerAt = p?.At; st.PowerMode = p?.Mode;
            st.KeepAwake = KeepAwakeMac.Current;
        }
        var temps = MacTempReader.State();   // uden for låsen: kan tage et øjeblik (powermetrics)
        lock (L) Status.Temps = temps;
        bool refresh; lock (L) refresh = MacSetup.RefreshExtensions(Status, _extAt, DateTime.UtcNow);
        if (refresh)
        {
            try
            {
                var list = JsonSerializer.Deserialize<List<ExtensionView>>(await api.GetText("/api/agent/extensions", ct), Api.Json) ?? [];
                lock (L) Status.Extensions = list;
                _extAt = DateTime.UtcNow;
            }
            catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException) { }
        }
        RefreshAccess(force: false);
        Write();
    }

    /// <summary>"Anmod om adgang": koden og tilstanden til menulinjen (null = ingen anmodning).</summary>
    public static void Access(TrayAccessRequest? request, bool enrolled)
    {
        if (!Mac.Is) return;
        lock (L) { Status.AccessRequest = request; Status.Enrolled = enrolled; if (request is not null) { Status.Connected = false; Status.Error = request.Text; } }
        Write();
    }

    public static void Failed(string error)
    {
        if (!Mac.Is) return;
        lock (L) { Status.Connected = false; Status.Error = error; }
        Write();
    }

    /// <summary>"Hold vågen på strøm": ny tilstand (menulinjen viser den og flueben ved indstillingen).</summary>
    public static void KeepAwake(KeepAwakeState s)
    {
        if (!Mac.Is) return;
        lock (L) Status.KeepAwake = s;
        Write();
    }

    public static void JobStarted(string title) { if (!Mac.Is) return; lock (L) Status.Job = new TrayJob { Title = title }; Write(); }
    public static void JobProgress(int? percent, string step)
    {
        if (!Mac.Is) return;
        lock (L) { if (Status.Job is not { } j) return; j.Percent = percent; j.Step = step; }
        Write();
    }
    /// <summary>En udvidelse er installeret (og programlisten sendt): hent udvidelsernes tilstand igen ved næste check-in.</summary>
    public static void ExtensionsChanged() { if (Mac.Is) lock (L) _extAt = DateTime.MinValue; }

    public static void JobEnded()
    {
        if (!Mac.Is) return;
        lock (L) { Status.Job = null; var p = Power.Current(); Status.PowerAt = p?.At; Status.PowerMode = p?.Mode; }
        Write();
    }

    /// <summary>Fuld diskadgang: hvert 5. minut, hvert halve minut mens den mangler (så appen ser det, lige når brugeren har givet den).</summary>
    private static void RefreshAccess(bool force)
    {
        var every = Status.MacAccess?.FullDiskAccess == false ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(5);
        if (!force && DateTime.UtcNow - _accessAt < every) return;
        _accessAt = DateTime.UtcNow;
        try { var a = MacSign.Access(); lock (L) Status.MacAccess = a; }
        catch (Exception e) { Log.Warn($"Fuld diskadgang kunne ikke tjekkes: {e.Message}"); }
    }

    /// <summary>"fleetdeck-agent tray-cmd &lt;base64&gt;" (som root, fra appen med administratoradgangskoden): læg ordren i roots mappe.</summary>
    public static int WriteAdminCommand(string? b64)
    {
        if (Environment.UserName != "root") { Console.Error.WriteLine("Kør som root."); return 5; }
        if (b64 is null || TrayCommands.Decode(b64) is not { } c) { Console.Error.WriteLine("Ugyldig ordre"); return 2; }
        Directory.CreateDirectory(MacPaths.AdminCmdDir);
        File.SetUnixFileMode(MacPaths.AdminCmdDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Combine(MacPaths.AdminCmdDir, $"{Guid.NewGuid():N}.json"), TrayCommands.Serialize(c));
        Console.WriteLine($"Ordren '{c.Action}' er sendt til Fleetdeck-tjenesten.");
        return 0;
    }

    /// <summary>Henter ordrer fra begge mapper (og sletter dem). Fra brugermappen kun dem, alle må give; aldrig symlinks eller store filer.</summary>
    public static List<TrayCommand> TakeCommands()
    {
        var list = new List<TrayCommand>();
        foreach (var (dir, admin) in new[] { (MacPaths.AdminCmdDir, true), (MacPaths.UserCmdDir, false) })
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.GetFiles(dir, "*.json"))
                {
                    try
                    {
                        var fi = new FileInfo(f);
                        if (fi.LinkTarget is not null || fi.Length > 32 * 1024) continue;   // et svar fra Beskeder kan fylde op til 4.000 tegn
                        var c = TrayCommands.Parse(File.ReadAllText(f));
                        if (c is not null && (admin || TrayCommands.UserAllowed(c.Action))) list.Add(c);
                        else if (c is not null) Log.Warn($"Ordre '{c.Action}' fra menulinjen afvist (kræver administrator)");
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                    finally { try { File.Delete(f); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return list;
    }

    /// <summary>Ordrer fra menulinjen: pause/genoptag og udsæt (lokalt), opdatér/synkronisér/udvidelse (via serveren), tjek adgang.</summary>
    public static async Task CommandLoop(Api api, AgentConfig cfg, Action wake, CancellationToken stop)
    {
        if (!Mac.Is) return;
        while (!stop.IsCancellationRequested)
        {
            try { await Task.Delay(3000, stop); } catch (OperationCanceledException) { return; }
            foreach (var c in TakeCommands())
            {
                try
                {
                    switch (c.Action)
                    {
                        case "pause":
                            cfg.PausedUntil = DateTime.UtcNow.AddMinutes(Math.Clamp(c.Minutes ?? 60, 5, 24 * 60)); cfg.Save();
                            lock (L) Status.PausedUntil = cfg.PausedUntil;
                            Log.Info($"Pause fra menulinjen indtil {cfg.PausedUntil.Value.ToLocalTime():HH:mm}");
                            break;
                        case "resume":
                            cfg.PausedUntil = null; cfg.Save(); lock (L) Status.PausedUntil = null;
                            break;
                        case "power_postpone":
                            var mode = PlannedPower.Mode;
                            Power.Cancel(PlannedPower.PostponeMinutes);
                            Log.Info($"Brugeren udsatte den planlagte {(mode == JobTypes.Reboot ? "genstart" : "slukning")} {PlannedPower.PostponeMinutes} min.");
                            break;
                        case "power_now":
                            await Power.Now(c.Id);   // kun den planlagte handling; ellers sker der intet
                            break;
                        case "upgrade_all" or "sync" or "extension":
                            await api.Post("/api/agent/action", new AgentActionRequest { Action = c.Action, Id = c.Id }, stop);
                            if (c.Action == "extension")
                                lock (L)
                                {
                                    Status.Extensions.Where(e => e.Id == c.Id).ToList().ForEach(e => e.Pending = true);
                                    _extAt = DateTime.MinValue;   // hent den rigtige tilstand ved næste check-in (opsætningsvinduet følger med)
                                }
                            break;
                        case "recheck_access":
                            RecheckAccess();
                            break;
                        case "helpdesk_reply" or "helpdesk_read" or "helpdesk_logs":
                            await HelpdeskUnix.Handle(api, c, stop);   // Beskeder: svar, set og "Send logfiler" fra appens vindue
                            break;
                        case "keepawake" when c.On is bool on:
                            KeepAwakeMac.SetLocal(on);   // gælder med det samme; meldes til Fleetdeck ved check-in lige herefter
                            break;
                        case MacFeatures.FeatureAction when c.Id == MacTemps.FeatureId && c.On is bool tempsOn:
                            // Funktioner → Temperaturmåling (administrator): gælder med det samme; meldes til Fleetdeck ved check-in lige herefter
                            var ts = MacTempReader.Set(tempsOn);
                            lock (L) Status.Temps = ts;
                            Log.Info($"Temperaturmåling slået {(tempsOn ? "til" : "fra")} fra appen ({MacTemps.Text(ts)})");
                            break;
                    }
                    Write();
                }
                catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException) { Log.Warn($"Ordre fra menulinjen fejlede: {e.Message}"); }
                wake();   // check ind med det samme
            }
        }
    }

    /// <summary>
    /// Brugeren har givet Fuld diskadgang: tjek igen. Ser tjenesten det stadig ikke, genstarter den sig selv (launchd starter den igen),
    /// for macOS giver først adgangen til en ny proces. Højst hvert 2. minut og aldrig midt i et job.
    /// </summary>
    private static void RecheckAccess()
    {
        RefreshAccess(force: true);
        if (Status.MacAccess?.FullDiskAccess != false || Status.Job is not null) return;
        if (DateTime.UtcNow - Started < TimeSpan.FromSeconds(20) || DateTime.UtcNow - _restartedFor < TimeSpan.FromMinutes(2)) return;
        _restartedFor = DateTime.UtcNow;
        Log.Info("Fuld diskadgang ses ikke endnu – genstarter tjenesten, så macOS giver den til den nye proces");
        Write();
        Environment.Exit(1);   // KeepAlive i launchd-plisten starter os igen med det samme
    }

    /// <summary>
    /// "install" (som root): Fleetdeck i menulinjen ved login for alle brugere – og med det samme hos den, der sidder ved skærmen
    /// (før kom ikonet først efter næste login). Mangler appen (kun agenten), lægger tjenesten den på ved første check-in.
    /// </summary>
    public static void InstallMenuBar(string appBundle)
    {
        if (!Mac.Is) return;
        if (!Directory.Exists(appBundle)) { Console.WriteLine("Fleetdeck.app er ikke installeret endnu – tjenesten henter den og viser ikonet, når nogen er logget ind."); return; }
        try
        {
            MacAppSync.EnsureMenu(appBundle: appBundle);   // genstarter kun, hvis noget er ændret (en kørende app med opsætningen lukkes ikke)
            var console = Mac.Session();
            foreach (var u in Mac.Sessions(console)) MacMenuBar.EnsureRunning(u, report: u.Uid == console?.Uid);   // også brugere i baggrunden
            Console.WriteLine("Fleetdeck vises i menulinjen (også ved login for alle brugere).");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Console.Error.WriteLine($"Menulinjen: {e.Message}"); }
    }

    /// <summary>"fleetdeck-agent uninstall" (som root): fjern tjenesten, ikonet ved login og al konfiguration.</summary>
    public static int Uninstall()
    {
        if (Environment.UserName != "root") { Console.Error.WriteLine("Kør som root (sudo)."); return 5; }
        if (!Mac.Is) { Console.Error.WriteLine("Kun macOS – på Linux: fjern maskinen fra Fleetdeck."); return 2; }
        var r = Shell.Run(MacInstall.UninstallScript(), TimeSpan.FromSeconds(60));
        Console.WriteLine(r.Output.Trim());
        return r.ExitCode == 0 ? 0 : 4;
    }
}
