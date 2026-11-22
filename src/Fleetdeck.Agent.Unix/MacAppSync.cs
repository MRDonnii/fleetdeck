using System.Reflection;
using System.Security.Cryptography;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Unix;

/// <summary>
/// macOS: Fleetdeck.app og menulinjen følger agentens version. Før blev kun agenten opdateret, så appen (svarvinduet,
/// "Skriv til support…") og en håndbygget FleetdeckMenu blev hængende på den version, Mac'en blev installeret med – og en Mac,
/// der var sat op med Terminal-kommandoen eller "kun agenten", fik aldrig nogen app, intet opsætningsvindue og intet ikon.
/// Hver 6. time (og lige efter start): mangler appen (og nogen er logget ind), eller er den ældre end agenten, hentes disk-imaget
/// fra serveren (signeret med agentfilernes nøgle) og lægges i /Programmer. Menulinjen (filerne og LaunchAgent'en) holdes på plads
/// hvert minut af <see cref="MacMenuBar"/>.
/// </summary>
public static class MacAppSync
{
    private static DateTime _next = DateTime.MinValue;
    private static readonly TimeSpan Every = TimeSpan.FromHours(6), WaitForUser = TimeSpan.FromMinutes(10);
    private const string Tmp = Mac.Dir + "/Fleetdeck.dmg.download";
    private static readonly Lock MenuLock = new();

    /// <summary>Agentens egen version (Directory.Build.props), fx "3.37.0".</summary>
    public static string? AgentVersion =>
        typeof(MacAppSync).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0];

    /// <summary>Fra check-in-løkken (uanset om serverens svar er godkendt: disk-imaget tjekkes selv mod serverens pinnede nøgle).</summary>
    public static async Task Tick(Api api, AgentConfig cfg, CancellationToken ct)
    {
        if (!Mac.Is || DateTime.UtcNow < _next) return;
        _next = DateTime.UtcNow + Every;
        try
        {
            var exists = Directory.Exists(MacPaths.InstalledApp);
            var installed = exists ? AppVersion() : null;
            switch (MacAppUpdate.Decide(exists, installed, AgentVersion, Mac.Session() is not null, cfg.NoApp))
            {
                case MacAppUpdate.Plan.WaitForUser:
                    _next = DateTime.UtcNow + WaitForUser;   // en Mac uden nogen logget ind: prøv igen om lidt
                    return;
                case MacAppUpdate.Plan.Install or MacAppUpdate.Plan.Update:
                    if (await UpdateApp(api, cfg, installed, ct)) EnsureMenu(restart: true);
                    else if (!exists) _next = DateTime.UtcNow + TimeSpan.FromMinutes(30);   // mangler stadig: prøv igen snart
                    break;
                default:
                    EnsureMenu();
                    break;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException or InvalidOperationException)
        { Log.Warn($"Fleetdeck.app/menulinjen: {e.Message}"); }
    }

    public static string? AppVersion()
    {
        var plist = Path.Combine(MacPaths.InstalledApp, "Contents", "Info.plist");
        return File.Exists(plist) ? MacAppUpdate.BundleVersion(File.ReadAllText(plist)) : null;
    }

    private static async Task<bool> UpdateApp(Api api, AgentConfig cfg, string? installed, CancellationToken ct)
    {
        if (cfg.SigningKey is not { Length: > 0 } key) { Log.Warn("Fleetdeck.app hentes ikke: serverens nøgle er ikke husket endnu"); return false; }
        try
        {
            var (hash, sig) = await api.Download($"/api/agent/download?platform={Platforms.Mac}&arch={MacInstall.ThisArch}&kind=dmg", Tmp, ct);
            if (AgentSigning.Check(Tmp, key, hash, sig) is { Ok: false } bad) { Log.Warn($"Fleetdeck.app afvist: {bad.Why}"); return false; }
            var r = Shell.Run(MacAppUpdate.InstallScript(Tmp), TimeSpan.FromMinutes(5));
            if (r.ExitCode != 0) { Log.Warn($"Fleetdeck.app kunne ikke lægges på: {r.Output.Trim()}"); return false; }
            Log.Info(installed is null ? $"Fleetdeck.app {AppVersion()} er lagt i Programmer (manglede)" : $"Fleetdeck.app opdateret fra {installed} til {AppVersion()}");
            return true;
        }
        finally { try { File.Delete(Tmp); } catch (IOException) { } }
    }

    /// <summary>
    /// Menulinjen: den native menu fra appen (med det rigtige stempel) bygges som FleetdeckMenu.app i /usr/local/fleetdeck/menu; en forældet
    /// fjernes. LaunchAgent'en skrives, så ikonet starter ved login for alle brugere. Ændres noget, genindlæses den for brugeren på
    /// skærmen (så ikonet skifter med det samme). Gør intet, når alt allerede passer (kaldes hvert minut).
    /// </summary>
    public static bool EnsureMenu(bool restart = false, string? appBundle = null)
    {
        if (!Mac.Is) return false;
        appBundle ??= MacPaths.InstalledApp;
        if (!Directory.Exists(appBundle)) return false;
        lock (MenuLock)
        {
            // "install" og tjenesten kan nå at gøre det samtidig: kun én proces ad gangen (flock via FileShare.None)
            FileStream guard;
            try { guard = new FileStream(Path.Combine(Mac.Dir, ".menu.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { return false; }
            using var _ = guard;
            var changed = restart;   // ny app: også appens eget ikon skal startes igen i den nye udgave
            var bundled = Path.Combine(appBundle, "Contents", "Resources", "menu", MacMenuBundle.Executable);
            if (MacMenuStamp.IsCurrent(bundled))
            {
                var sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(bundled)));
                var recorded = ReadOrNull(Path.Combine(MacPaths.NativeMenuApp, "Contents", "Resources", MacMenuBundle.SourceHashFile));
                var info = ReadOrNull(Path.Combine(MacPaths.NativeMenuApp, "Contents", "Info.plist"));
                if (MacMenuBundle.NeedsBuild(recorded, sha, info) || !MacMenuStamp.IsCurrent(MacPaths.NativeMenu) || File.Exists(MacPaths.LegacyNativeMenu))
                {
                    if (!BuildMenuApp(bundled, sha)) return false;
                    Log.Info($"Menulinjen (FleetdeckMenu.app, {MacMenuStamp.Marker}) er lagt på fra Fleetdeck.app");
                    changed = true;
                }
            }
            else
            {
                // forældet (fx håndbygget med swiftc uden "Skriv til support…"): væk med den, så appens egen menu tager over
                foreach (var stale in new[] { MacPaths.LegacyNativeMenu, MacPaths.NativeMenu })
                    if (File.Exists(stale) && !MacMenuStamp.IsCurrent(stale))
                    {
                        if (stale == MacPaths.NativeMenu) Directory.Delete(MacPaths.NativeMenuApp, true); else File.Delete(stale);
                        Log.Info("En forældet FleetdeckMenu er fjernet – Fleetdeck.app viser selv menuen i menulinjen");
                        changed = true;
                    }
            }
            var plist = MacInstall.MenuBarAgentPlist(appBundle);
            if (!File.Exists(MacPaths.MenuBarPlist) || File.ReadAllText(MacPaths.MenuBarPlist) != plist)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(MacPaths.MenuBarPlist)!);
                File.WriteAllText(MacPaths.MenuBarPlist, plist);
                Shell.Run($"chown root:wheel {Pkg.Q(MacPaths.MenuBarPlist)}; chmod 644 {Pkg.Q(MacPaths.MenuBarPlist)}", TimeSpan.FromSeconds(10));
                Log.Info("Menulinjen starter ved login for alle brugere (LaunchAgent skrevet)");
                changed = true;
            }
            if (changed) MacMenuBar.Restart();
            return changed;
        }
    }

    /// <summary>Byg FleetdeckMenu.app ved siden af (skjult mappe), og byt den ind som root.</summary>
    private static bool BuildMenuApp(string bundled, string sha)
    {
        var staged = Path.Combine(Mac.Dir, ".FleetdeckMenu.new.app");
        try
        {
            if (Directory.Exists(staged)) Directory.Delete(staged, true);
            var contents = Path.Combine(staged, "Contents");
            Directory.CreateDirectory(Path.Combine(contents, "MacOS"));
            var res = Directory.CreateDirectory(Path.Combine(contents, "Resources")).FullName;
            File.WriteAllText(Path.Combine(contents, "Info.plist"), MacMenuBundle.InfoPlist());
            File.WriteAllText(Path.Combine(contents, "PkgInfo"), "APPL????");
            File.Copy(bundled, Path.Combine(contents, "MacOS", MacMenuBundle.Executable), true);
            var icon = Path.Combine(Path.GetDirectoryName(bundled)!, MacMenuBundle.Icon);
            if (File.Exists(icon)) File.Copy(icon, Path.Combine(res, MacMenuBundle.Icon), true);
            File.WriteAllText(Path.Combine(res, MacMenuBundle.SourceHashFile), sha);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"FleetdeckMenu.app kunne ikke bygges: {e.Message}");
            return false;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(MacPaths.NativeMenuApp)!);
        var r = Shell.Run(MacMenuBundle.InstallScript(staged), TimeSpan.FromSeconds(60));
        if (r.ExitCode != 0) { Log.Warn($"Menulinjen kunne ikke lægges på: {r.Output.Trim()}"); return false; }
        return true;
    }

    private static string? ReadOrNull(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
