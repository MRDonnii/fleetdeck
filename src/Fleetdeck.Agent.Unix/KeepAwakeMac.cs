using System.Runtime.InteropServices;
using System.Text.Json;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Unix;

/// <summary>
/// macOS: "Hold vågen på strøm (også med lukket låg)". Strømkilden læses hvert 5. sekund via IOKit (ingen processer);
/// pmset startes kun, når noget skifter (og hvert 5. minut for at se, om brugeren selv har ændret disablesleep).
/// Logikken ligger i <see cref="MacKeepAwake"/> (testet); her er kun det, der rører Mac'en.
/// </summary>
public static class KeepAwakeMac
{
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint Utf8 = 0x08000100;

    [DllImport(IOKit)] private static extern IntPtr IOPSCopyPowerSourcesInfo();
    [DllImport(IOKit)] private static extern IntPtr IOPSGetProvidingPowerSourceType(IntPtr snapshot);
    [DllImport(CF)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFStringGetCString(IntPtr s, byte[] buffer, nint size, uint enc);
    [DllImport(CF)] private static extern void CFRelease(IntPtr cf);

    private static readonly Lock L = new(), T = new();
    private static MacKeepAwake.Local _local = new();
    private static KeepAwakeState _state = new();
    private static bool? _lastAc, _lastSetting, _sleepDisabled;
    private static DateTime _checkedAt = DateTime.MinValue;
    private static DateTime? _sentAt;
    private static bool _ioKitBroken, _started;
    private static Action? _changed;

    /// <summary>Sættes før en bevidst genstart (selvopdatering, serverflytning): så slås dvale ikke til et øjeblik med lukket låg.</summary>
    public static bool Restarting { get; set; }

    /// <summary>Tilstanden lige nu (til check-in og menulinjen). Null på andet end macOS.</summary>
    public static KeepAwakeState? Current { get { if (!Mac.Is) return null; lock (L) return Copy(_state); } }

    /// <summary>
    /// Ved opstart, før alt andet: læs den sidst kendte indstilling, læg vagten på plads, afstem med det samme
    /// (batteri = dvale, også selvom serveren ikke kan nås), og start løkken. Ved stop slås dvale til igen.
    /// </summary>
    public static void Start(Action changed, CancellationToken stop)
    {
        if (!Mac.Is || _started) return;
        _started = true;
        _changed = changed;
        lock (L) _local = Load();
        EnsureWatchdog();
        Tick(force: true);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try { await Task.Delay(MacKeepAwake.Poll, stop); } catch (OperationCanceledException) { break; }
                try { Tick(force: false); } catch (Exception e) { Log.Warn($"Hold vågen: {e.Message}"); }
            }
        });
    }

    /// <summary>Lokal ændring fra menulinjen eller appen: gælder med det samme og meldes til serveren ved næste check-in.</summary>
    public static void SetLocal(bool on)
    {
        if (!Mac.Is) return;
        lock (L) { _local = MacKeepAwake.Toggle(on, DateTime.UtcNow); Save(_local); }
        Log.Info($"Hold vågen på strøm slået {(on ? "til" : "fra")} på Mac'en");
        Tick(force: true);
    }

    /// <summary>Til check-in: en ubekræftet lokal ændring (husker, hvilken der blev sendt).</summary>
    public static KeepAwakeChange? Outgoing()
    {
        if (!Mac.Is) return null;
        lock (L)
        {
            var c = MacKeepAwake.Outgoing(_local, DateTime.UtcNow);
            _sentAt = c?.At;
            return c;
        }
    }

    /// <summary>Svaret fra serveren (efter signaturtjekket; null = behold den sidst kendte).</summary>
    public static void FromServer(KeepAwakeSetting? s)
    {
        if (!Mac.Is) return;
        bool changed;
        lock (L)
        {
            var merged = MacKeepAwake.Merge(_local, s, _sentAt);
            changed = merged.On != _local.On || merged.Pending != _local.Pending || merged.At != _local.At || merged.Source != _local.Source;
            if (changed)
            {
                if (merged.On != _local.On) Log.Info($"Hold vågen på strøm slået {(merged.On ? "til" : "fra")} fra Fleetdeck");
                _local = merged; Save(_local);
            }
        }
        if (changed) Tick(force: true);
    }

    /// <summary>Hvert 5. sekund: læs strømkilden; afstem, når den eller indstillingen har skiftet (og hvert 5. minut).</summary>
    private static void Tick(bool force)
    {
        lock (T) TickLocked(force);   // løkken, menulinjen og check-in kan kalde samtidig: én ad gangen
    }

    private static void TickLocked(bool force)
    {
        var ac = ReadIsAc();
        bool setting; lock (L) setting = _local.On;
        var now = DateTime.UtcNow;
        if (!force && ac == _lastAc && setting == _lastSetting && now - _checkedAt < MacKeepAwake.Recheck) return;
        var marker = File.Exists(MacKeepAwake.Marker);
        bool? sleepDisabled = MacKeepAwake.NeedsSleepDisabled(setting, ac, marker) ? ReadSleepDisabled() : _sleepDisabled;
        var d = MacKeepAwake.Decide(setting, ac, marker, sleepDisabled);
        string? error = d.State == MacKeepAwake.States.Error ? "pmset kunne ikke læses" : null;
        switch (d.Act)
        {
            case MacKeepAwake.Act.Enable:
                var on = Shell.Run("pmset -a disablesleep 1", TimeSpan.FromSeconds(15));
                if (on.ExitCode == 0)
                {
                    WriteMarker();
                    sleepDisabled = true;
                    Log.Info("Strøm: holdes vågen (disablesleep 1, også med lukket låg)");
                }
                else { error = $"pmset fejlede: {on.Output.Trim()}"; d = d with { State = MacKeepAwake.States.Error }; }
                break;
            case MacKeepAwake.Act.Disable:
                if (Release()) { sleepDisabled = false; Log.Info(ac ? "Hold vågen er slået fra: dvale som normalt (disablesleep 0)" : "Batteri: dvale som normalt (disablesleep 0)"); }
                else { error = "pmset kunne ikke slå dvale til igen (vagten prøver igen)"; d = d with { State = MacKeepAwake.States.Error }; }
                break;
        }
        _lastAc = ac; _lastSetting = setting; _checkedAt = now; _sleepDisabled = sleepDisabled;
        KeepAwakeState next;
        lock (L)
        {
            next = new KeepAwakeState { Setting = setting, OnAc = ac, State = d.State, Error = error, ChangedAt = _local.At, ChangedFrom = _local.Source };
            if (Same(next, _state)) return;
            _state = next;
        }
        MacTray.KeepAwake(Copy(next));
        _changed?.Invoke();   // meld den nye tilstand til Fleetdeck med det samme
    }

    /// <summary>disablesleep 0 og fjern markøren (kun hvis pmset lykkedes – ellers prøver vagten igen).</summary>
    private static bool Release()
    {
        var r = Shell.Run("pmset -a disablesleep 0", TimeSpan.FromSeconds(15));
        if (r.ExitCode != 0) return false;
        try { File.Delete(MacKeepAwake.Marker); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return true;
    }

    /// <summary>Tjenesten stoppes (SIGTERM, nedlukning, afinstallation): slå dvale til igen, hvis Fleetdeck satte den.</summary>
    private static void Shutdown()
    {
        if (Restarting || !File.Exists(MacKeepAwake.Marker)) return;
        try { if (Release()) Log.Info("Agenten stopper: dvale som normalt igen (disablesleep 0)"); }
        catch (Exception) { /* vagten tager den */ }
    }

    private static void WriteMarker()
    {
        try
        {
            File.WriteAllText(MacKeepAwake.Marker, $"Sat af Fleetdeck {DateTime.UtcNow:O}: pmset -a disablesleep 1 (Hold vågen på strøm)\n");
            File.SetUnixFileMode(MacKeepAwake.Marker, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warn($"Hold vågen: markøren kunne ikke skrives: {e.Message}"); }
    }

    private static bool? ReadSleepDisabled() => MacKeepAwake.ParseSleepDisabled(Shell.Out("pmset -g 2>/dev/null", 15));

    /// <summary>IOPSGetProvidingPowerSourceType – uden at starte en proces. Virker IOKit ikke, bruges "pmset -g ps". Ukendt = batteri.</summary>
    private static bool ReadIsAc()
    {
        if (!_ioKitBroken)
        {
            try
            {
                var blob = IOPSCopyPowerSourcesInfo();
                if (blob != IntPtr.Zero)
                {
                    try
                    {
                        var type = IOPSGetProvidingPowerSourceType(blob);   // "Get": ejes af blob'en, frigives ikke
                        if (type == IntPtr.Zero) return false;
                        var buf = new byte[64];
                        return CFStringGetCString(type, buf, buf.Length, Utf8) && MacKeepAwake.IsAc(System.Text.Encoding.UTF8.GetString(buf).TrimEnd('\0'));
                    }
                    finally { CFRelease(blob); }
                }
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
            {
                _ioKitBroken = true;
                Log.Warn($"Hold vågen: IOKit kan ikke bruges ({e.Message}) – læser strømkilden med pmset");
            }
        }
        return MacKeepAwake.ParsePsIsAc(Shell.Out("pmset -g ps 2>/dev/null", 10));
    }

    /// <summary>Vagten (LaunchDaemon hvert 30. sekund): lægges på plads ved installation og ved hver opstart (dvs. også efter opdatering).</summary>
    public static void EnsureWatchdog()
    {
        if (!Mac.Is || Environment.UserName != "root") return;
        try
        {
            Directory.CreateDirectory(MacPaths.AgentDir);
            var changed = WriteIfChanged(MacKeepAwake.WatchdogScript, MacKeepAwake.WatchdogScriptText(), 0b111_101_101);
            changed |= WriteIfChanged(MacKeepAwake.WatchdogPlist, MacKeepAwake.WatchdogPlistText(), 0b110_100_100);
            var loaded = Shell.Run($"launchctl print system/{MacKeepAwake.WatchdogLabel} >/dev/null 2>&1", TimeSpan.FromSeconds(15)).ExitCode == 0;
            if (!changed && loaded) return;
            var r = Shell.Run($"chown root:wheel {Pkg.Q(MacKeepAwake.WatchdogScript)} {Pkg.Q(MacKeepAwake.WatchdogPlist)}; " +
                $"launchctl bootout system/{MacKeepAwake.WatchdogLabel} 2>/dev/null; launchctl bootstrap system {Pkg.Q(MacKeepAwake.WatchdogPlist)}", TimeSpan.FromSeconds(30));
            if (r.ExitCode == 0) Log.Info($"Vagten for \"Hold vågen\" kører ({MacKeepAwake.WatchdogLabel}, hvert 30. sekund)");
            else Log.Warn($"Vagten for \"Hold vågen\" kunne ikke startes: {r.Output.Trim()}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warn($"Vagten for \"Hold vågen\": {e.Message}"); }
    }

    private static bool WriteIfChanged(string path, string text, int mode)
    {
        if (File.Exists(path) && File.ReadAllText(path) == text) return false;
        var tmp = path + ".new";
        File.WriteAllText(tmp, text);
        File.SetUnixFileMode(tmp, (UnixFileMode)mode);
        File.Move(tmp, path, true);
        return true;
    }

    private static MacKeepAwake.Local Load()
    {
        try { return File.Exists(MacKeepAwake.StateFile) ? JsonSerializer.Deserialize<MacKeepAwake.Local>(File.ReadAllText(MacKeepAwake.StateFile), Api.Json) ?? new() : new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { Log.Warn($"Hold vågen: indstillingen kunne ikke læses ({e.Message}) – regnes som fra"); return new(); }
    }

    private static void Save(MacKeepAwake.Local l)
    {
        try
        {
            var tmp = MacKeepAwake.StateFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(l, Api.Json));
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(tmp, MacKeepAwake.StateFile, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warn($"Hold vågen: indstillingen kunne ikke gemmes: {e.Message}"); }
    }

    private static bool Same(KeepAwakeState a, KeepAwakeState b) =>
        a.Setting == b.Setting && a.OnAc == b.OnAc && a.State == b.State && a.Error == b.Error && a.ChangedAt == b.ChangedAt && a.ChangedFrom == b.ChangedFrom;

    private static KeepAwakeState Copy(KeepAwakeState s) =>
        new() { Setting = s.Setting, OnAc = s.OnAc, State = s.State, Error = s.Error, ChangedAt = s.ChangedAt, ChangedFrom = s.ChangedFrom };
}
