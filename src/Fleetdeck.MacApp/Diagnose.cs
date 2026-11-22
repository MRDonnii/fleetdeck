using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Fleetdeck.Shared;

namespace Fleetdeck.MacApp;

/// <summary>
/// "Gem diagnose…" (Installeret → Oversigt): samler agentens log, launchctl-status for menuen og agenten, appens version og om ikonet
/// kører i én tekstfil på skrivebordet (uden hemmeligheder, se <see cref="MacDiagnose"/>), og viser den i Finder, så ejeren kan sende den.
/// Alt kan læses som brugeren selv – der spørges ikke om adgangskode.
/// </summary>
public static class Diagnose
{
    public static async Task<(string? Path, string? Error)> Save()
    {
        var uid = int.TryParse((await MacSys.Run("/usr/bin/id", ["-u"], TimeSpan.FromSeconds(10))).Output.Trim(), out var u) ? u : -1;
        var menu = MacSys.Run("/bin/launchctl", MacDiagnose.MenuArgs(uid), TimeSpan.FromSeconds(15));
        var agent = MacSys.Run("/bin/launchctl", MacDiagnose.AgentArgs, TimeSpan.FromSeconds(15));
        string? log = null, logErr = null;
        try { log = File.Exists(MacPaths.Log) ? ReadTail(MacPaths.Log, 512 * 1024) : null; logErr = log is null ? "findes ikke" : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { logErr = $"kan ikke læses som {Environment.UserName} ({e.Message})"; }
        TrayStatus? status = null; string? statusErr = null;
        try { status = File.Exists(MacPaths.StatusFile) ? JsonSerializer.Deserialize<TrayStatus>(await File.ReadAllTextAsync(MacPaths.StatusFile), TrayCommands.Json) : null; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { statusErr = $"kan ikke læses ({e.Message})"; }
        var text = MacDiagnose.Build(new MacDiagnose.Input
        {
            At = DateTime.UtcNow, AppVersion = MacSys.Version, Os = Environment.OSVersion.VersionString,
            Arch = RuntimeInformation.OSArchitecture.ToString(), User = Environment.UserName, Uid = uid,
            AgentInstalled = MacSys.Installed, LoginItem = File.Exists(MacPaths.MenuBarPlist), NativeMenu = MacMenuStamp.IsCurrent(MacPaths.NativeMenu),
            NativeMenuProcess = Running("FleetdeckMenu"), AutostartAtLogin = Settings.Current.Autostart,
            Status = status, StatusError = statusErr, MenuLaunchctl = await menu, AgentLaunchctl = await agent, AgentLog = log, AgentLogError = logErr,
        });
        var dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (dir.Length == 0 || !Directory.Exists(dir)) dir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = Path.Combine(dir, MacDiagnose.FileName(DateTime.Now));
        try { await File.WriteAllTextAsync(path, text); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (null, e.Message); }
        MacSys.Reveal(path);
        return (path, null);
    }

    /// <summary>Kører en proces med præcis det navn? (Uden shell og uden mønstersøgning i kommandolinjer, der kan ramme sig selv.)</summary>
    private static bool Running(string name)
    {
        var all = Process.GetProcessesByName(name);
        try { return all.Length > 0; } finally { foreach (var p in all) p.Dispose(); }
    }

    /// <summary>Kun slutningen af en stor log (resten skæres fra i <see cref="MacDiagnose.Tail"/>).</summary>
    private static string ReadTail(string path, int maxBytes)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (f.Length > maxBytes) f.Seek(-maxBytes, SeekOrigin.End);
        using var r = new StreamReader(f);
        return r.ReadToEnd();
    }
}
