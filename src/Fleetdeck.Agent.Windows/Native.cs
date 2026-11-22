using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Fleetdeck.Agent.Windows;

/// <summary>Win32-kald: brugersessioner, processer som bruger, beskeder, CPU og RAM.</summary>
internal static class Native
{
    public const uint InvalidSession = 0xFFFFFFFF;

    [DllImport("kernel32.dll")] public static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("kernel32.dll")] public static extern bool FreeConsole();
    [DllImport("wtsapi32.dll", SetLastError = true)] public static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);
    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool WTSQuerySessionInformation(IntPtr server, uint sessionId, int infoClass, out IntPtr buffer, out int bytes);
    [DllImport("wtsapi32.dll")] public static extern void WTSFreeMemory(IntPtr p);
    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool WTSSendMessage(IntPtr server, uint sessionId, string title, int titleLength, string message, int messageLength,
        uint style, int timeoutSeconds, out int response, bool wait);
    [DllImport("wtsapi32.dll", SetLastError = true)] public static extern bool WTSLogoffSession(IntPtr server, uint sessionId, bool wait);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returnLength);
    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool DuplicateTokenEx(IntPtr existing, uint access, IntPtr attributes, int impersonationLevel, int tokenType, out IntPtr newToken);
    [DllImport("userenv.dll", SetLastError = true)] public static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);
    [DllImport("userenv.dll")] public static extern bool DestroyEnvironmentBlock(IntPtr env);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CreateProcessAsUser(IntPtr token, string? application, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string? currentDirectory, ref StartupInfo si, out ProcessInformation pi);
    [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll")] public static extern bool GetExitCodeProcess(IntPtr handle, out uint code);
    [DllImport("kernel32.dll")] public static extern bool TerminateProcess(IntPtr handle, uint code);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")] public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx m);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct StartupInfo
    {
        public int cb; public string? lpReserved; public string? lpDesktop; public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessInformation { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    [StructLayout(LayoutKind.Sequential)]
    public struct MemoryStatusEx
    {
        public uint dwLength, dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
            ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    private const int WTSUserName = 5, WTSDomainName = 7;
    private const int TokenElevationType = 18, TokenLinkedToken = 19;
    private const uint MaximumAllowed = 0x02000000;
    private const uint CreateUnicodeEnvironment = 0x00000400, CreateNoWindow = 0x08000000;

    public static uint ConsoleSession() => WTSGetActiveConsoleSessionId();

    /// <summary>Den indloggede konsolbruger som "DOMÆNE\bruger", eller null.</summary>
    public static string? ConsoleUser()
    {
        var s = ConsoleSession();
        if (s == InvalidSession) return null;
        var user = SessionString(s, WTSUserName);
        if (string.IsNullOrEmpty(user)) return null;
        var dom = SessionString(s, WTSDomainName);
        return string.IsNullOrEmpty(dom) ? user : $"{dom}\\{user}";
    }

    /// <summary>SID'en for den indloggede konsolbruger, eller null.</summary>
    public static string? ConsoleUserSid()
    {
        if (ConsoleUser() is not { } u) return null;
        try { return ((System.Security.Principal.SecurityIdentifier)new System.Security.Principal.NTAccount(u).Translate(typeof(System.Security.Principal.SecurityIdentifier))).Value; }
        catch (Exception e) when (e is System.Security.Principal.IdentityNotMappedException or SystemException) { return null; }
    }

    private static string? SessionString(uint session, int cls)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, session, cls, out var buf, out _)) return null;
        try { return Marshal.PtrToStringUni(buf); } finally { WTSFreeMemory(buf); }
    }

    /// <summary>Viser en besked i brugerens session (virker også på Windows Home, hvor msg.exe mangler).</summary>
    public static bool SendMessage(string title, string text)
    {
        var s = ConsoleSession();
        return s != InvalidSession && WTSSendMessage(IntPtr.Zero, s, title, title.Length * 2, text, text.Length * 2, 0x40 /*MB_ICONINFORMATION*/, 900, out _, false);
    }

    public static bool Logoff()
    {
        var s = ConsoleSession();
        return s != InvalidSession && WTSLogoffSession(IntPtr.Zero, s, false);
    }

    /// <summary>
    /// Starter en kommandolinje i den indloggede brugers session. elevated=true bruger brugerens fulde
    /// administrator-token (UAC "linked token"), ellers det almindelige begrænsede token.
    /// Returnerer null hvis ingen er logget ind.
    /// </summary>
    /// <param name="wait">false: start og returnér straks (fx ikonet i proceslinjen, der skal blive ved med at køre)</param>
    public static (int ExitCode, bool TimedOut)? RunAsConsoleUser(string commandLine, bool elevated, TimeSpan timeout, string? workDir = null, bool wait = true)
    {
        var session = ConsoleSession();
        if (session == InvalidSession || !WTSQueryUserToken(session, out var userToken)) return null;
        IntPtr token = IntPtr.Zero, env = IntPtr.Zero;
        try
        {
            token = elevated ? ElevatedOrSame(userToken) : Duplicate(userToken);
            if (!CreateEnvironmentBlock(out env, token, false)) env = IntPtr.Zero;
            var si = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>(), lpDesktop = @"winsta0\default" };
            var cmd = new StringBuilder(commandLine);
            if (!CreateProcessAsUser(token, null, cmd, IntPtr.Zero, IntPtr.Zero, false, CreateUnicodeEnvironment | CreateNoWindow,
                    env, workDir, ref si, out var pi))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUser fejlede");
            try
            {
                if (!wait) return (0, false);
                var waited = WaitForSingleObject(pi.hProcess, (uint)Math.Min(timeout.TotalMilliseconds, uint.MaxValue - 1));
                if (waited != 0) { TerminateProcess(pi.hProcess, 1); return (-1, true); }
                GetExitCodeProcess(pi.hProcess, out var code);
                return (unchecked((int)code), false);
            }
            finally { CloseHandle(pi.hThread); CloseHandle(pi.hProcess); }
        }
        finally
        {
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            if (token != IntPtr.Zero && token != userToken) CloseHandle(token);
            CloseHandle(userToken);
        }
    }

    private static IntPtr Duplicate(IntPtr token) =>
        DuplicateTokenEx(token, MaximumAllowed, IntPtr.Zero, 2 /*SecurityImpersonation*/, 1 /*TokenPrimary*/, out var dup) ? dup : token;

    private static IntPtr ElevatedOrSame(IntPtr token)
    {
        var buf = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            // 3 = TokenElevationTypeLimited: der findes et fuldt (elevated) token bag det begrænsede
            if (GetTokenInformation(token, TokenElevationType, buf, 4, out _) && Marshal.ReadInt32(buf) == 3
                && GetTokenInformation(token, TokenLinkedToken, buf, IntPtr.Size, out _))
            {
                var linked = Marshal.ReadIntPtr(buf);
                var dup = Duplicate(linked);
                if (dup != linked) CloseHandle(linked);
                return dup;
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return Duplicate(token);
    }

    // ---------------------------------------------------------------- CPU og RAM
    public static double CpuPercent(int sampleMs = 500)
    {
        GetSystemTimes(out var i1, out var k1, out var u1);
        Thread.Sleep(sampleMs);
        GetSystemTimes(out var i2, out var k2, out var u2);
        var total = (k2 - k1) + (u2 - u1);
        return total <= 0 ? 0 : Math.Round(100.0 * (total - (i2 - i1)) / total, 1);
    }

    public static (double PctUsed, double UsedGb, double TotalGb) Memory()
    {
        var m = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref m)) return (0, 0, 0);
        double total = m.ullTotalPhys / 1073741824.0, used = (m.ullTotalPhys - m.ullAvailPhys) / 1073741824.0;
        return (Math.Round(100 * used / total, 1), Math.Round(used, 1), Math.Round(total, 1));
    }
}
