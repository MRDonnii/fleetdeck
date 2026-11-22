using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Unix;

/// <summary>
/// macOS: sørger for, at Fleetdeck faktisk står i menulinjen hos den, der sidder ved skærmen – og at opsætningsvinduet kommer frem,
/// når Mac'en ikke kan blive tilmeldt af sig selv. Før blev LaunchAgent'en kun skrevet: den startede først ved næste login, og
/// ingen tjekkede, om ikonet kørte. Hvert minut (også mens agenten venter på tilmelding):
///   1) filerne og LaunchAgent'en på plads (<see cref="MacAppSync.EnsureMenu"/>, gør intet, når de passer),
///   2) ikonet indlæst og kørende i brugerens session (launchctl bootstrap/kickstart som root i gui/&lt;uid&gt;),
///      men aldrig igen, hvis brugeren selv har valgt "Skjul ikonet" (exit 0) – så venter det til næste login,
///   3) opsætningsvinduet (Fleetdeck.app --setup), højst hver 6. time, når agenten ikke er tilmeldt og ikke venter på godkendelse.
/// Ikonet kører altid som brugeren (LaunchAgent), agenten som root; ingen hemmeligheder kommer i brugerens filer.
/// </summary>
public static class MacMenuBar
{
    private static readonly Lock L = new();
    private static readonly MacMenuInfo Info = new();
    // pr. bruger (uid): hvornår ikonet sidst blev genstartet, og hvornår launchctl sidst afviste at starte det
    private static readonly Dictionary<int, DateTime> Kicked = [], BootFailed = [];
    /// <summary>Tilmeldingsnøglen er afvist af serveren: tjenesten venter (i stedet for at gå ned i ring), og opsætningen vises.</summary>
    public static volatile bool KeyRejected;
    private const string SetupShown = Mac.Dir + "/.setup-shown";
    private static readonly TimeSpan SetupEvery = TimeSpan.FromHours(6);

    /// <summary>Til helbredsrapporten (HealthJson → "macMenu").</summary>
    public static MacMenuInfo Report()
    {
        lock (L)
            return new MacMenuInfo
            {
                AppVersion = Directory.Exists(MacPaths.InstalledApp) ? MacAppSync.AppVersion() : null,
                NativeMenu = MacMenuStamp.IsCurrent(MacPaths.NativeMenu),
                LoginItem = File.Exists(MacPaths.MenuBarPlist),
                User = Info.User, Running = Info.Running, Detail = Info.Detail,
            };
    }

    public static async Task Loop(AgentConfig cfg, CancellationToken stop)
    {
        if (!Mac.Is) return;
        try { await Task.Delay(TimeSpan.FromSeconds(5), stop); } catch (OperationCanceledException) { return; }
        while (!stop.IsCancellationRequested)
        {
            try
            {
                MacAppSync.EnsureMenu();
                // alle loginsessioner: også brugere, der er logget ind i baggrunden (hurtigt brugerskift), får ikonet – ikke kun den ved skærmen
                var s = Mac.Session();
                if (s is null) Remember(null, null, "Ingen logget ind");
                foreach (var u in Mac.Sessions(s)) EnsureRunning(u, report: u.Uid == s?.Uid);
                PromptSetup(cfg, s);
            }
            // aldrig lade én uventet fejl stoppe løkken for altid (så kom ikonet og opsætningen aldrig igen før næste genstart)
            catch (Exception e) when (e is not OperationCanceledException)
            { Log.Warn($"Menulinjen: {e.Message}"); }
            try { await Task.Delay(TimeSpan.FromMinutes(1), stop); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// "install" (som root) eller en ny app/menu: start ikonet nu hos alle, der er logget ind (også i baggrunden) – ikke først ved
    /// næste login.
    /// </summary>
    public static void Restart()
    {
        if (!Mac.Is || !File.Exists(MacPaths.MenuBarPlist)) return;
        var console = Mac.Session();
        foreach (var s in Mac.Sessions(console))
        {
            // stop den gamle menu (Swift-hjælperen eller appens ikon) og start det, plisten nu peger på
            Shell.Run($"launchctl bootout gui/{s.Uid}/{MacPaths.MenuBarLabel} 2>/dev/null; sleep 1; launchctl bootstrap gui/{s.Uid} {Pkg.Q(MacPaths.MenuBarPlist)} 2>&1 || true",
                TimeSpan.FromSeconds(20));
            Log.Info($"Menulinjen er (gen)startet for {s.User}{(s.Uid == console?.Uid ? "" : " (logget ind i baggrunden)")}");
            if (s.Uid == console?.Uid) Remember(s.User, null, "Startet af agenten");
        }
    }

    /// <summary>
    /// Kører ikonet hos brugeren? Ellers indlæs eller start det (højst hvert 10. minut ved et ikon, der går ned). <paramref name="report"/>:
    /// brugeren ved skærmen, hvis tilstand står i helbredsrapporten (brugere i baggrunden logges kun).
    /// </summary>
    public static void EnsureRunning(UserSession? s, bool report = true)
    {
        void Remember(string? user, bool? running, string detail) { if (report) MacMenuBar.Remember(user, running, detail); }
        if (s is null) { Remember(null, null, "Ingen logget ind"); return; }
        if (!File.Exists(MacPaths.MenuBarPlist)) { Remember(s.User, false, "LaunchAgent mangler (Fleetdeck.app er ikke lagt på)"); return; }
        var target = $"gui/{s.Uid}/{MacPaths.MenuBarLabel}";
        var p = Shell.Run($"launchctl print {target} 2>&1", TimeSpan.FromSeconds(15));
        switch (MacMenuLaunch.Decide(p.ExitCode, p.Output))
        {
            case MacMenuLaunch.Action.Bootstrap:
                // afvist før (fx slået fra under Systemindstillinger → Generelt → Loginemner): prøv kun igen hver time – ikke hvert minut
                if (BootFailed.TryGetValue(s.Uid, out var failed) && DateTime.UtcNow - failed < TimeSpan.FromHours(1)) break;
                var b = Shell.Run($"launchctl bootstrap gui/{s.Uid} {Pkg.Q(MacPaths.MenuBarPlist)} 2>&1", TimeSpan.FromSeconds(20));
                if (b.ExitCode == 0) { BootFailed.Remove(s.Uid); Log.Info($"Menulinjen var ikke startet for {s.User}{(report ? "" : " (logget ind i baggrunden)")} – startet nu"); }
                else { BootFailed[s.Uid] = DateTime.UtcNow; Log.Warn($"Menulinjen kunne ikke startes for {s.User} (launchctl: {b.Output.Trim()}) – er Fleetdeck slået fra under Loginemner?"); }
                Remember(s.User, b.ExitCode == 0, b.ExitCode == 0 ? "Startet af agenten" : $"Kunne ikke startes (slået fra under Loginemner?): {b.Output.Trim()}");
                break;
            case MacMenuLaunch.Action.Kickstart:
                if (Kicked.TryGetValue(s.Uid, out var kicked) && DateTime.UtcNow - kicked < TimeSpan.FromMinutes(10)) { Remember(s.User, false, "Kører ikke (går ned?)"); break; }
                Kicked[s.Uid] = DateTime.UtcNow;
                var k = Shell.Run($"launchctl kickstart {target} 2>&1", TimeSpan.FromSeconds(20));
                Log.Info($"Menulinjen kørte ikke for {s.User} – genstartet{(k.ExitCode == 0 ? "" : $" (launchctl: {k.Output.Trim()})")}");
                Remember(s.User, k.ExitCode == 0, "Genstartet af agenten");
                break;
            default:
                var running = MacMenuLaunch.Running(p.Output);
                Remember(s.User, running, running ? "Kører" : "Skjult af brugeren (til næste login)");
                break;
        }
    }

    /// <summary>Opsætningsvinduet hos brugeren på skærmen, når Mac'en ikke kan blive tilmeldt af sig selv (højst hver 6. time).</summary>
    private static void PromptSetup(AgentConfig cfg, UserSession? s)
    {
        if (s is null || !Directory.Exists(MacPaths.InstalledApp)) return;
        if (!MacMenuLaunch.NeedsSetup(cfg.Token.Length > 0, cfg.PendingKey is { Length: > 0 }, cfg.Access is not null, cfg.AccessState, KeyRejected)) return;
        if (File.Exists(SetupShown) && DateTime.UtcNow - File.GetLastWriteTimeUtc(SetupShown) < SetupEvery) return;
        File.WriteAllText(SetupShown, s.User);
        // -n: en ny kopi, der giver "setup" videre til en kørende app (ellers ville argumentet gå tabt)
        var r = Shell.Run($"launchctl asuser {s.Uid} sudo -u {Pkg.Q(s.User)} open -n -a {Pkg.Q(MacPaths.InstalledApp)} --args --setup 2>&1", TimeSpan.FromSeconds(20));
        Log.Info($"Agenten er ikke tilmeldt – opsætningsvinduet er åbnet for {s.User}{(r.ExitCode == 0 ? "" : $" (fejl: {r.Output.Trim()})")}");
    }

    private static void Remember(string? user, bool? running, string detail)
    {
        lock (L) { Info.User = user; Info.Running = running; Info.Detail = detail; }
    }
}
