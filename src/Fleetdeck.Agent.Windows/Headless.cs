using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Fleetdeck.Agent.Windows;

/// <summary>
/// Sørger for, at installationer altid kører helt uden vinduer:
/// lydløs-flag efter installationstype, lukning af programmet før opdatering, og en vagt
/// der stopper et installationsprogram, som viser et vindue og venter på et klik.
/// </summary>
public static class Headless
{
    /// <summary>Et installeret program fundet i Windows' afinstallationsliste.</summary>
    public sealed record InstalledApp(string Name, string Kind, string? InstallDir);

    /// <summary>Finder et installeret program ud fra navnet winget viser (navnet kan være forkortet med "…").</summary>
    public static InstalledApp? Find(string name)
    {
        var want = name.TrimEnd('…', '.', ' ');
        if (want.Length < 3) return null;
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var hk = RegistryKey.OpenBaseKey(hive, view);
                using var root = hk.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (root is null) continue;
                foreach (var sub in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(sub);
                    if (k?.GetValue("DisplayName") is not string dn || !dn.StartsWith(want, StringComparison.OrdinalIgnoreCase)) continue;
                    return new InstalledApp(dn, Kind(sub, k), InstallDir(k));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        return null;
    }

    /// <summary>Installationstypen ud fra afinstallations-posten: "inno", "msi", "nullsoft" eller "exe".</summary>
    public static string Kind(string keyName, RegistryKey k) =>
        keyName.EndsWith("_is1", StringComparison.OrdinalIgnoreCase) || k.GetValue("Inno Setup: App Path") is not null ? "inno"
        : k.GetValue("WindowsInstaller") is int wi && wi == 1 ? "msi"
        : (k.GetValue("UninstallString") as string ?? "").Contains("uninst", StringComparison.OrdinalIgnoreCase) ? "nullsoft"
        : "exe";

    /// <summary>Programmets mappe ud fra afinstallations-posten (InstallLocation, Inno-stien eller ikonets mappe).</summary>
    public static string? InstallDir(RegistryKey k)
    {
        var dir = k.GetValue("InstallLocation") as string ?? k.GetValue("Inno Setup: App Path") as string;
        if (string.IsNullOrWhiteSpace(dir) && k.GetValue("DisplayIcon") is string icon)
            try { dir = Path.GetDirectoryName(icon.Split(',')[0].Trim('"')); } catch (ArgumentException) { dir = null; }
        return string.IsNullOrWhiteSpace(dir) ? null : dir.Trim('"').TrimEnd('\\');
    }

    /// <summary>
    /// Ekstra flag til winget's --custom, så installationsprogrammet aldrig spørger om noget.
    /// Inno: luk programmer der bruger filerne (også hvis de ikke vil), og vis ingen beskeder.
    /// </summary>
    public static string CustomArgs(string? kind) => kind switch
    {
        "inno" => "/SUPPRESSMSGBOXES /CLOSEAPPLICATIONS /FORCECLOSEAPPLICATIONS /NORESTARTAPPLICATIONS /NORESTART /SP-",
        "msi" => "REBOOT=ReallySuppress",
        _ => "",
    };

    /// <summary>
    /// Lukker kørende programmer fra programmets mappe (så opdateringen ikke venter på dem) og
    /// returnerer de exe-filer, der kørte i en brugers session, så de kan startes igen bagefter.
    /// </summary>
    public static List<string> CloseRunning(string? installDir, Action<string> log, string why = "før opdateringen")
    {
        var restart = new List<string>();
        if (!SafeDir(installDir)) return restart;
        var dir = installDir!.TrimEnd('\\') + "\\";
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == Environment.ProcessId) continue;
                var file = p.MainModule?.FileName;
                if (file is null || !file.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) continue;
                log($"Lukker {Path.GetFileName(file)} (pid {p.Id}) {why}");
                if (p.SessionId != 0 && !restart.Contains(file, StringComparer.OrdinalIgnoreCase)
                    && !Path.GetFileName(file).Contains("unins", StringComparison.OrdinalIgnoreCase)) restart.Add(file);
                p.Kill(entireProcessTree: true);
                p.WaitForExit(10_000);
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            finally { p.Dispose(); }
        }
        return restart;
    }

    /// <summary>Lukker kun i en rigtig programmappe – aldrig i Windows, Program Files-roden eller et drev.</summary>
    private static bool SafeDir(string? d)
    {
        if (string.IsNullOrWhiteSpace(d) || !Directory.Exists(d)) return false;
        var full = Path.GetFullPath(d).TrimEnd('\\');
        string[] roots = [Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.System), Path.GetPathRoot(full)!.TrimEnd('\\')];
        if (roots.Any(r => r.Length > 0 && (full.Equals(r.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))) return false;
        if (full.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)) return false;
        return full.Count(c => c == '\\') >= 2;   // mindst to niveauer, fx C:\Program Files\Ajax
    }

    // ------------------------------------------------------------------ vagten

    /// <summary>Hvorfor vagten stoppede installationen (null = den stoppede den ikke).</summary>
    public sealed record Verdict(string Reason);

    /// <summary>
    /// Vagt for et program, der kører som SYSTEM på session 0's skjulte skrivebord. Står procestræet stille (ingen CPU,
    /// ingen I/O og intet output) i 60 sek, ser den efter en dialogboks fra træet – fx Inno Setups "… is already installed.
    /// Would you like to replace it?" fra pakkens egen kode, som /SUPPRESSMSGBOXES ikke dækker. Den trykker på den rigtige
    /// knap og skriver det i jobbet. Højst 5 svar pr. installation; står den stille i 10 min uden dialog, stoppes den.
    /// </summary>
    public sealed class Watchdog(Process root, Func<DateTime> lastOutput, Action<string> report)
    {
        public static TimeSpan StallBeforeAnswer = TimeSpan.FromSeconds(60);
        public static TimeSpan IdleLimit = TimeSpan.FromMinutes(10);
        public const int MaxAnswers = 5;

        private TimeSpan lastCpu = TimeSpan.Zero;
        private ulong lastIo;
        private DateTime lastActivity = DateTime.UtcNow;
        private int answers;

        public Verdict? Check()
        {
            var pids = Tree(root.Id);
            var (cpu, io) = Usage(pids);
            if (cpu > lastCpu + TimeSpan.FromMilliseconds(200) || io > lastIo + 64 * 1024) lastActivity = DateTime.UtcNow;
            lastCpu = cpu; lastIo = io;
            var activity = lastOutput() > lastActivity ? lastOutput() : lastActivity;
            var quiet = DateTime.UtcNow - activity;
            if (quiet >= StallBeforeAnswer && answers < MaxAnswers && FindDialog(pids) is { } dlg)
            {
                if (Answer(dlg) is { } said)
                {
                    answers++;
                    report(said);
                    lastActivity = DateTime.UtcNow;   // giv installationen tid til at fortsætte efter svaret
                    return null;
                }
            }
            if (quiet >= IdleLimit)
                return new Verdict(answers >= MaxAnswers
                    ? $"Installationsprogrammet stod stille efter {MaxAnswers} automatiske svar på dialogbokse – stoppet."
                    : $"Installationsprogrammet stod stille i {IdleLimit.TotalMinutes:0} min (ingen dialog fundet) – stoppet.");
            return null;
        }

        private static (TimeSpan Cpu, ulong Io) Usage(HashSet<int> pids)
        {
            var cpu = TimeSpan.Zero; ulong io = 0;
            foreach (var id in pids)
            {
                try
                {
                    using var p = Process.GetProcessById(id);
                    cpu += p.TotalProcessorTime;
                    if (GetProcessIoCounters(p.Handle, out var c)) io += c.ReadTransferCount + c.WriteTransferCount + c.OtherTransferCount;
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            return (cpu, io);
        }
    }

    // ------------------------------------------------------------------ dialogbokse

    /// <summary>En synlig dialog fra procestræet: vindue, titel, tekst og knapper.</summary>
    public sealed record Dialog(IntPtr Hwnd, string Class, string Title, string Text, List<(IntPtr Hwnd, string Text, bool Default)> Buttons);

    private static readonly Regex Positive = new(@"^(yes|ja|ok|next|næste|install|installér|installer|finish|afslut|udfør|continue|fortsæt|replace|erstat|accept|acceptér|i agree|jeg accepterer|run|kør)\b", RegexOptions.IgnoreCase);
    private static readonly Regex Negative = new(@"^(cancel|annullér|annuller|no|nej|abort|afbryd|stop|luk|close|quit)\b", RegexOptions.IgnoreCase);
    private static readonly Regex Later = new(@"^(no|nej|later|senere|restart later|genstart senere|not now|ikke nu)\b", RegexOptions.IgnoreCase);
    private static readonly Regex AsksRestart = new(@"restart|reboot|genstart", RegexOptions.IgnoreCase);

    /// <summary>
    /// Vælger knap: spørger boksen om genstart, svares "Nej/Senere" (genstart styres af Fleetdeck); ellers standardknappen,
    /// hvis den ikke er en afvisning, så en knap som Ja/OK/Næste/Installér/Fortsæt, så en hvilken som helst ikke-afvisende –
    /// og kun til sidst Annullér/Nej. Returnerer teksten til jobbets output, eller null hvis der ingen knap er.
    /// </summary>
    public static string? Answer(Dialog d)
    {
        if (d.Buttons.Count == 0) return null;
        var i = Choose(d.Title, d.Text, d.Buttons.Select(b => (b.Text, b.Default)).ToList());
        var btn = d.Buttons[i];
        Click(d, btn.Hwnd);
        var text = d.Text.Length > 300 ? d.Text[..300] + "…" : d.Text;
        return $"Svarede \"{Clean(btn.Text)}\" på dialog \"{d.Title}\": {text}";
    }

    private static string Clean(string t) => t.Replace("&", "").Trim();

    /// <summary>Hvilken knap (indeks) der skal trykkes på – ren logik, så den kan testes.</summary>
    public static int Choose(string title, string text, IReadOnlyList<(string Text, bool Default)> buttons)
    {
        int Find(Func<(string Text, bool Default), bool> ok) { for (var k = 0; k < buttons.Count; k++) if (ok(buttons[k])) return k; return -1; }
        var i = AsksRestart.IsMatch(title + " " + text) ? Find(b => Later.IsMatch(Clean(b.Text))) : -1;
        if (i < 0) i = Find(b => b.Default && !Negative.IsMatch(Clean(b.Text)));
        if (i < 0) i = Find(b => Positive.IsMatch(Clean(b.Text)));
        if (i < 0) i = Find(b => !Negative.IsMatch(Clean(b.Text)));
        return i < 0 ? 0 : i;
    }

    private static void Click(Dialog d, IntPtr btn)
    {
        var id = GetDlgCtrlID(btn);
        // Standarddialog (MessageBox): WM_COMMAND med knappens id er det mest pålidelige. Andre (Inno o.l.): BM_CLICK på knappen.
        if (d.Class == "#32770" && id != 0) PostMessageW(d.Hwnd, 0x0111 /*WM_COMMAND*/, (IntPtr)(id & 0xFFFF), btn);
        else PostMessageW(btn, 0x00F5 /*BM_CLICK*/, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Første synlige dialog (eller vindue med knapper) fra en af processerne, ellers null.</summary>
    public static Dialog? FindDialog(HashSet<int> pids)
    {
        Dialog? found = null;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            GetWindowThreadProcessId(h, out var pid);
            if (!pids.Contains((int)pid)) return true;
            if (!GetWindowRect(h, out var r) || r.Right - r.Left < 50 || r.Bottom - r.Top < 30) return true;
            var cls = ClassOf(h);
            var buttons = new List<(IntPtr, string, bool)>();
            var text = new StringBuilder();
            var defId = cls == "#32770" ? (long)SendMessageW(h, 0x0400 /*DM_GETDEFID*/, IntPtr.Zero, IntPtr.Zero) : 0;
            var defCtl = (defId >> 16) == 0x534B /*DC_HASDEFID*/ ? (int)(defId & 0xFFFF) : -1;
            EnumChildWindows(h, (c, _) =>
            {
                if (!IsWindowVisible(c)) return true;
                var cc = ClassOf(c);
                if (cc is "Button" or "TNewButton" or "TButton")
                {
                    var style = GetWindowLongW(c, -16 /*GWL_STYLE*/) & 0xF;
                    if (cc == "Button" && style is not (0 /*BS_PUSHBUTTON*/ or 1 /*BS_DEFPUSHBUTTON*/)) return true;   // ikke flueben/radioknapper
                    if (!IsWindowEnabled(c)) return true;
                    buttons.Add((c, TextOf(c), style == 1 || GetDlgCtrlID(c) == defCtl));
                }
                else if (cc is "Static" or "TNewStaticText" or "TNewMemo" or "RichEdit20W" or "TRichEditViewer" && TextOf(c) is { Length: > 0 } t)
                    text.Append(text.Length > 0 ? " " : "").Append(t.Replace("\r", " ").Replace("\n", " "));
                return true;
            }, IntPtr.Zero);
            if (buttons.Count == 0) return true;   // et statusvindue uden knapper venter ikke på et klik
            found = new Dialog(h, cls, TextOf(h), text.ToString().Trim(), buttons);
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Processen og alle dens efterkommere.</summary>
    public static HashSet<int> Tree(int root)
    {
        var parents = new List<(int Pid, int Parent)>();
        var snap = CreateToolhelp32Snapshot(0x2 /*TH32CS_SNAPPROCESS*/, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return [root];
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            for (var ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                parents.Add(((int)e.th32ProcessID, (int)e.th32ParentProcessID));
        }
        finally { CloseHandle(snap); }
        var set = new HashSet<int> { root };
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var (pid, parent) in parents)
                if (pid != 0 && set.Contains(parent) && set.Add(pid)) grew = true;
        }
        return set;
    }

    private static string ClassOf(IntPtr h) { var sb = new StringBuilder(256); GetClassNameW(h, sb, sb.Capacity); return sb.ToString(); }
    private static string TextOf(IntPtr h) { var sb = new StringBuilder(512); GetWindowTextW(h, sb, sb.Capacity); return sb.ToString().Trim(); }

    // ------------------------------------------------------------------ Win32
    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr h);
    [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr h);
    [DllImport("user32.dll")] private static extern int GetWindowLongW(IntPtr h, int index);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] private static extern bool GetProcessIoCounters(IntPtr process, out IO_COUNTERS c);
    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize, cntUsage, th32ProcessID; public IntPtr th32DefaultHeapID; public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase; public uint dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }
}
