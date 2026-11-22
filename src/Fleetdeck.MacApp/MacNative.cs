using System.Diagnostics;
using System.Runtime.InteropServices;
using Fleetdeck.Shared;

namespace Fleetdeck.MacApp;

/// <summary>
/// De få AppKit-kald, Avalonia ikke selv har: aktiveringspolitikken (Dock-ikon) og "hent appen frem" – via objc_msgSend.
/// Alt er forsigtigt: fejler et kald (fx på en anden platform), sker der bare ingenting.
/// </summary>
public static class MacNative
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Msg(IntPtr receiver, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern long MsgLong(IntPtr receiver, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool MsgSetLong(IntPtr receiver, IntPtr selector, long value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void MsgBool(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool value);

    private static IntPtr App()
    {
        var cls = objc_getClass("NSApplication");
        return cls == IntPtr.Zero ? IntPtr.Zero : Msg(cls, sel_registerName("sharedApplication"));
    }

    /// <summary>Sæt NSApp's aktiveringspolitik (MacDock.Regular = i Dock og Cmd-Tab), hvis den ikke allerede er det. Kun på UI-tråden.</summary>
    public static void EnsurePolicy(long policy)
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            var app = App();
            if (app == IntPtr.Zero) return;
            if (MsgLong(app, sel_registerName("activationPolicy")) != policy) MsgSetLong(app, sel_registerName("setActivationPolicy:"), policy);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException) { }
    }

    /// <summary>Hent appen forrest (activateIgnoringOtherApps: YES – virker fra macOS 10.15 til og med 26).</summary>
    public static void Activate()
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            var app = App();
            if (app != IntPtr.Zero) MsgBool(app, sel_registerName("activateIgnoringOtherApps:"), true);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException) { }
    }
}

/// <summary>
/// Startvagten: hver start skrives i ~/Library/Logs/Fleetdeck/app-start.log, og en fejl, der stopper appen, skrives dér og vises
/// i et macOS-vindue ("Fleetdeck kunne ikke starte: …") – så en start aldrig fejler i stilhed.
/// </summary>
public static class StartupGuard
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static int _shown;
    private static bool _ready;

    public static string LogFile => Path.Combine(MacStartupLog.Dir(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), MacStartupLog.FileName);

    public static void Begin(string mode)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Fail(_ready ? "under kørsel" : "opstart", ex, alert: !_ready);
        };
        TaskScheduler.UnobservedTaskException += (_, e) => { Write(MacStartupLog.CrashBlock(DateTime.Now, "baggrundsopgave", e.Exception)); e.SetObserved(); };
        Write(MacStartupLog.StartLine(DateTime.Now, MacSys.Version, Environment.OSVersion.Version.ToString(), RuntimeInformation.OSArchitecture.ToString(),
            Environment.Version.ToString(), MacCompat.IsLegacyBuild, mode) + "\n");
    }

    /// <summary>Første vindue eller ikon er vist.</summary>
    public static void Ready()
    {
        if (_ready) return;
        _ready = true;
        Write(MacStartupLog.ReadyLine(DateTime.Now, Clock.Elapsed) + "\n");
    }

    public static void Fail(string where, Exception e, bool alert = true)
    {
        Write(MacStartupLog.CrashBlock(DateTime.Now, where, e));
        if (alert && Interlocked.Exchange(ref _shown, 1) == 0) Alert(MacStartupLog.AlertText(e));
    }

    private static void Write(string text)
    {
        try
        {
            var f = LogFile;
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            var old = File.Exists(f) ? File.ReadAllText(f) : "";
            File.WriteAllText(f, MacStartupLog.Trim(old + text));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Et macOS-advarselsvindue via osascript (virker også, når Avalonia ikke kunne starte).</summary>
    private static void Alert(string text)
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            var msg = $"{text}\n\nDetaljer: ~/Library/Logs/Fleetdeck/{MacStartupLog.FileName}";
            var psi = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add($"display alert \"Fleetdeck\" message {Fleetdeck.Shared.MacInstall.AppleScript(msg)} as critical buttons {{\"OK\"}} default button 1");
            using var p = Process.Start(psi);
            p?.WaitForExit(TimeSpan.FromMinutes(5));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }
}
