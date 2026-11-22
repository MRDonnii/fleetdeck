using System.Runtime.InteropServices;

namespace Fleetdeck.Agent.Windows;

/// <summary>
/// Holder pc'en vågen, mens Fleetdeck arbejder (opdateringer, installationer …), så den ikke går i dvale midt i et job.
/// Bruger Windows' strømanmodninger (synlige med "powercfg /requests"); når arbejdet er færdigt, følger pc'en sin normale strømplan igen.
/// </summary>
internal sealed class KeepAwake : IDisposable
{
    private const int SystemRequired = 1, ExecutionRequired = 3;
    private readonly IntPtr handle;
    private readonly IntPtr reasonPtr;
    private bool disposed;

    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext { public uint Version; public uint Flags; public IntPtr SimpleReasonString; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr PowerCreateRequest(ref ReasonContext context);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool PowerSetRequest(IntPtr h, int type);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool PowerClearRequest(IntPtr h, int type);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);

    private KeepAwake(string reason)
    {
        reasonPtr = Marshal.StringToHGlobalUni(reason);
        var ctx = new ReasonContext { Version = 0, Flags = 1 /* simpel tekst */, SimpleReasonString = reasonPtr };
        handle = PowerCreateRequest(ref ctx);
        if (handle != IntPtr.Zero && handle != new IntPtr(-1)) { PowerSetRequest(handle, SystemRequired); PowerSetRequest(handle, ExecutionRequired); }
    }

    public static IDisposable? Begin(string reason)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try { return new KeepAwake(reason); } catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    /// <summary>Hold pc'en vågen i et tidsrum (fx indtil en planlagt genstart sker – ellers kan den nå at falde i søvn først).</summary>
    public static void HoldFor(TimeSpan time, string reason)
    {
        var h = Begin(reason);
        if (h is null) return;
        _ = Task.Delay(time).ContinueWith(_ => h.Dispose(), TaskScheduler.Default);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (handle != IntPtr.Zero && handle != new IntPtr(-1))
        {
            PowerClearRequest(handle, ExecutionRequired); PowerClearRequest(handle, SystemRequired); CloseHandle(handle);
        }
        Marshal.FreeHGlobal(reasonPtr);
    }
}
