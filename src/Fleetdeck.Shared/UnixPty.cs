using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Fleetdeck.Shared;

/// <summary>
/// Pseudoterminal på Linux og Mac. <c>forkpty</c> kan ikke bruges fra .NET (barnet ville fortsætte i runtimen efter fork), så
/// agenten åbner PTY'en her og starter sig selv med <see cref="HelperCommand"/>; hjælperen gør PTY'en til styrende terminal og kalder execv.
/// </summary>
public sealed class UnixPty : IPtySession
{
    public const string HelperCommand = "__pty-exec";
    private const string Lib = "fleetdeck_libc";
    private readonly int _master;
    private readonly Process _proc;
    private int _closed;

    static UnixPty()
    {
        NativeLibrary.SetDllImportResolver(typeof(UnixPty).Assembly, (name, _, _) =>
        {
            if (name != Lib) return IntPtr.Zero;
            string[] names = OperatingSystem.IsMacOS() ? ["/usr/lib/libSystem.B.dylib"]
                : ["libc.so.6", "libc.musl-x86_64.so.1", "libc.musl-aarch64.so.1", "libc.musl-armv7.so.1", "libc"];
            foreach (var n in names) if (NativeLibrary.TryLoad(n, out var h)) return h;
            return IntPtr.Zero;
        });
    }

    [DllImport(Lib, SetLastError = true)] private static extern int posix_openpt(int flags);
    [DllImport(Lib, SetLastError = true)] private static extern int grantpt(int fd);
    [DllImport(Lib, SetLastError = true)] private static extern int unlockpt(int fd);
    [DllImport(Lib, SetLastError = true)] private static extern unsafe int ptsname_r(int fd, byte* buf, nuint len);
    [DllImport(Lib, SetLastError = true)] private static extern unsafe nint read(int fd, byte* buf, nuint n);
    [DllImport(Lib, SetLastError = true)] private static extern unsafe nint write(int fd, byte* buf, nuint n);
    [DllImport(Lib, SetLastError = true)] private static extern int close(int fd);
    [DllImport(Lib, SetLastError = true)] private static extern int setsid();
    [DllImport(Lib, SetLastError = true)] private static extern int dup2(int a, int b);
    [DllImport(Lib, SetLastError = true)] private static extern int chdir([MarshalAs(UnmanagedType.LPUTF8Str)] string p);
    [DllImport(Lib, SetLastError = true, EntryPoint = "open")] private static extern int open_([MarshalAs(UnmanagedType.LPUTF8Str)] string p, int flags);
    [DllImport(Lib, SetLastError = true)] private static extern int execv([MarshalAs(UnmanagedType.LPUTF8Str)] string p, [In] IntPtr[] argv);
    [DllImport(Lib, SetLastError = true)] private static extern int setenv([MarshalAs(UnmanagedType.LPUTF8Str)] string k, [MarshalAs(UnmanagedType.LPUTF8Str)] string v, int o);
    [DllImport(Lib, SetLastError = true)] private static extern int unsetenv([MarshalAs(UnmanagedType.LPUTF8Str)] string k);
    [DllImport(Lib)] private static extern IntPtr signal(int sig, IntPtr handler);
    [DllImport(Lib)] private static extern unsafe int sigprocmask(int how, byte* set, byte* old);
    [DllImport(Lib, SetLastError = true, EntryPoint = "ioctl")] private static extern int ioctl_(int fd, nuint req, IntPtr arg);
    // Apple Silicon lægger variadiske argumenter på stakken: de otte registre fyldes, så arg havner dér
    [DllImport(Lib, SetLastError = true, EntryPoint = "ioctl")] private static extern int ioctl_arm(int fd, nuint req, long a, long b, long c, long d, long e, long f, IntPtr arg);

    private static bool Mac => OperatingSystem.IsMacOS();
    private static int Ioctl(int fd, nuint req, IntPtr arg) =>
        Mac && RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? ioctl_arm(fd, req, 0, 0, 0, 0, 0, 0, arg) : ioctl_(fd, req, arg);
    private static nuint TIOCSWINSZ => Mac ? 0x80087467u : 0x5414u;
    private static nuint TIOCSCTTY => Mac ? 0x20007461u : 0x540Eu;
    private static nuint FIOCLEX => Mac ? 0x20006601u : 0x5451u;
    private static int O_NOCTTY => Mac ? 0x20000 : 0x100;
    private const int O_RDWR = 2;

    private static unsafe void SetSize(int fd, int cols, int rows)
    {
        ushort* ws = stackalloc ushort[4] { (ushort)rows, (ushort)cols, 0, 0 };
        Ioctl(fd, TIOCSWINSZ, (IntPtr)ws);
    }

    private UnixPty(int master, Process p) { _master = master; _proc = p; }

    /// <param name="exe">Agentens egen fil.</param><param name="prefix">Argumenter før hjælperens (bruges af test).</param>
    public static unsafe UnixPty Start(int cols, int rows, string exe, params string[] prefix)
    {
        var m = posix_openpt(O_RDWR | O_NOCTTY);
        if (m < 0) throw new IOException($"posix_openpt fejlede ({Marshal.GetLastPInvokeError()})");
        try
        {
            Ioctl(m, FIOCLEX, IntPtr.Zero);        // skallen må ikke arve master-enden
            if (grantpt(m) != 0 || unlockpt(m) != 0) throw new IOException($"PTY kunne ikke låses op ({Marshal.GetLastPInvokeError()})");
            byte* name = stackalloc byte[256];
            if (ptsname_r(m, name, 256) != 0) throw new IOException($"ptsname_r fejlede ({Marshal.GetLastPInvokeError()})");
            var slave = Marshal.PtrToStringUTF8((IntPtr)name)!;
            SetSize(m, cols, rows);
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var a in prefix) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add(HelperCommand); psi.ArgumentList.Add(slave); psi.ArgumentList.Add(cols.ToString()); psi.ArgumentList.Add(rows.ToString());
            return new UnixPty(m, Process.Start(psi) ?? throw new IOException("Hjælperen kunne ikke startes"));
        }
        catch { close(m); throw; }
    }

    /// <summary>Hjælperen: bliver til skallen. Vender kun tilbage, hvis det mislykkes.</summary>
    public static unsafe int RunHelper(string slave, int cols, int rows)
    {
        setsid();
        var fd = open_(slave, O_RDWR);
        if (fd < 0) return 111;
        Ioctl(fd, TIOCSCTTY, IntPtr.Zero);
        SetSize(fd, cols, rows);
        dup2(fd, 0); dup2(fd, 1); dup2(fd, 2);
        if (fd > 2) close(fd);
        // .NET ignorerer SIGPIPE og blokerer signaler; det må skallen ikke arve
        signal(13, IntPtr.Zero);
        byte* empty = stackalloc byte[128]; new Span<byte>(empty, 128).Clear();
        sigprocmask(Mac ? 3 : 2, empty, null);
        foreach (var k in Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(k => k.StartsWith("DOTNET_", StringComparison.Ordinal))) unsetenv(k);
        setenv("TERM", "xterm-256color", 1); setenv("COLORTERM", "truecolor", 1);
        var home = Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } h ? h : Mac ? "/var/root" : "/root";
        setenv("HOME", home, 1); setenv("LANG", Environment.GetEnvironmentVariable("LANG") ?? "C.UTF-8", 0);
        chdir(home);
        var shell = Shell();
        var argv = new[] { Marshal.StringToCoTaskMemUTF8(shell), Marshal.StringToCoTaskMemUTF8("-l"), IntPtr.Zero };
        execv(shell, argv);
        return 127;
    }

    public static string Shell()
    {
        string[] order = Mac ? ["/bin/zsh", "/bin/bash", "/bin/sh"] : ["/bin/bash", "/usr/bin/bash", "/bin/ash", "/bin/sh"];
        return order.FirstOrDefault(File.Exists) ?? "/bin/sh";
    }

    public unsafe int Read(byte[] buffer)
    {
        fixed (byte* p = buffer)
            while (true)
            {
                var n = read(_master, p, (nuint)buffer.Length);
                if (n >= 0) return (int)n;
                if (Marshal.GetLastPInvokeError() == 4) continue;      // EINTR
                return 0;                                              // EIO: skallen er lukket
            }
    }

    public unsafe void Write(ReadOnlySpan<byte> data)
    {
        fixed (byte* p = data)
        {
            var off = 0;
            while (off < data.Length)
            {
                var n = write(_master, p + off, (nuint)(data.Length - off));
                if (n < 0) { if (Marshal.GetLastPInvokeError() == 4) continue; throw new IOException("Skallen er lukket"); }
                off += (int)n;
            }
        }
    }

    public void Resize(int cols, int rows) => SetSize(_master, cols, rows);
    public int WaitForExit() { _proc.WaitForExit(); return _proc.ExitCode; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        _proc.WaitForExit(3000);
        close(_master);
        _proc.Dispose();
    }
}
