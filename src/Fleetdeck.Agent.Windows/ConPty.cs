using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Windows;

/// <summary>Terminal på Windows med ConPTY (Windows 10 1809 eller nyere). Hele skallens procestræ lukkes sammen med sessionen.</summary>
internal sealed class ConPty : IPtySession
{
    private const uint ExtendedStartupInfoPresent = 0x00080000, CreateUnicodeEnvironment = 0x00000400, Infinite = 0xFFFFFFFF, StartfUseStdHandles = 0x100;
    private static readonly IntPtr PseudoConsoleAttribute = 0x00020016;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;

    [StructLayout(LayoutKind.Sequential)] private struct Coord { public short X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute; public uint dwFlags; public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr lpAttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimit { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong A, B, C, D, E, F; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimit { public BasicLimit Basic; public IoCounters Io; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }

    [DllImport("kernel32", SetLastError = true)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attrs, int size);
    [DllImport("kernel32", SetLastError = true)] private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr pc);
    [DllImport("kernel32", SetLastError = true)] private static extern int ResizePseudoConsole(IntPtr pc, Coord size);
    [DllImport("kernel32", SetLastError = true)] private static extern void ClosePseudoConsole(IntPtr pc);
    [DllImport("kernel32", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attr, IntPtr value, IntPtr size, IntPtr prev, IntPtr ret);
    [DllImport("kernel32", SetLastError = true)] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? app, System.Text.StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string? cwd, ref StartupInfoEx si, out ProcessInformation pi);
    [DllImport("kernel32", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObjectW(IntPtr attrs, string? name);
    [DllImport("kernel32", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int cls, ref ExtendedLimit info, int size);
    [DllImport("kernel32", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    private readonly Lock _gate = new();
    private readonly FileStream _in, _out;
    private readonly IntPtr _process, _job;
    private IntPtr _pc;
    private int _disposed;

    private ConPty(FileStream input, FileStream output, IntPtr pc, IntPtr process, IntPtr job)
    {
        _in = input; _out = output; _pc = pc; _process = process; _job = job;
        // ConPTY lukker ikke uddata af sig selv, når skallen stopper
        new Thread(() => { WaitForSingleObject(_process, Infinite); ClosePc(); }) { IsBackground = true, Name = "conpty-exit" }.Start();
    }

    private static string ShellCommand()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pwsh = Path.Combine(pf, "PowerShell", "7", "pwsh.exe");
        if (File.Exists(pwsh)) return $"\"{pwsh}\" -NoLogo";
        return $"\"{Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")}\" -NoLogo";
    }

    public static ConPty Start(int cols, int rows)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) throw new PlatformNotSupportedException("Terminal kræver Windows 10 version 1809 eller nyere");
        if (!CreatePipe(out var inRead, out var inWrite, IntPtr.Zero, 0) || !CreatePipe(out var outRead, out var outWrite, IntPtr.Zero, 0)) throw new Win32Exception();
        var hr = CreatePseudoConsole(new Coord { X = (short)cols, Y = (short)rows }, inRead, outWrite, 0, out var pc);
        if (hr != 0) throw new Win32Exception(hr);
        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        var list = Marshal.AllocHGlobal(size);
        try
        {
            if (!InitializeProcThreadAttributeList(list, 1, 0, ref size)) throw new Win32Exception();
            if (!UpdateProcThreadAttribute(list, 0, PseudoConsoleAttribute, pc, IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();
            var si = new StartupInfoEx { lpAttributeList = list };
            si.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
            si.StartupInfo.dwFlags = StartfUseStdHandles;      // arv ikke agentens egne håndtag
            var job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero) throw new Win32Exception();
            var limit = new ExtendedLimit { Basic = new BasicLimit { LimitFlags = KillOnJobClose } };
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limit, Marshal.SizeOf<ExtendedLimit>())) throw new Win32Exception();
            var cwd = Path.GetPathRoot(Environment.SystemDirectory);
            if (!CreateProcessW(null, new System.Text.StringBuilder(ShellCommand()), IntPtr.Zero, IntPtr.Zero, false, ExtendedStartupInfoPresent | CreateUnicodeEnvironment, IntPtr.Zero, cwd, ref si, out var pi))
                throw new Win32Exception();
            AssignProcessToJobObject(job, pi.hProcess);
            CloseHandle(pi.hThread);
            inRead.Dispose(); outWrite.Dispose();      // ConPTY har sine egne kopier
            return new ConPty(new FileStream(inWrite, FileAccess.Write, 0), new FileStream(outRead, FileAccess.Read, 0), pc, pi.hProcess, job);
        }
        finally { DeleteProcThreadAttributeList(list); Marshal.FreeHGlobal(list); }
    }

    public int Read(byte[] buffer)
    {
        try { return _out.Read(buffer, 0, buffer.Length); }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { return 0; }
    }

    public void Write(ReadOnlySpan<byte> data) { _in.Write(data); _in.Flush(); }

    public void Resize(int cols, int rows)
    {
        lock (_gate) if (_pc != IntPtr.Zero) ResizePseudoConsole(_pc, new Coord { X = (short)cols, Y = (short)rows });
    }

    public int WaitForExit()
    {
        WaitForSingleObject(_process, Infinite);
        return GetExitCodeProcess(_process, out var code) ? (int)code : -1;
    }

    private void ClosePc()
    {
        IntPtr pc;
        lock (_gate) { pc = _pc; _pc = IntPtr.Zero; }
        if (pc != IntPtr.Zero) ClosePseudoConsole(pc);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        CloseHandle(_job);      // KILL_ON_JOB_CLOSE: skallen og alt, den har startet, stopper
        var stopped = WaitForSingleObject(_process, 3000) == 0;
        ClosePc();
        _in.Dispose(); _out.Dispose();
        if (stopped) CloseHandle(_process);      // ellers venter tråden til skallens afslutning stadig på håndtaget
    }
}
