using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Windows;

/// <summary>
/// USB-stik med BIOS-opdateringen fra en Windows-pc (logikken ligger i Fleetdeck.Shared/BiosUsbWin.cs, samme protokol som Linux):
/// usb_disks tager øjebliksbilledet og venter på et nyt stik; bios_usb_write skriver kun det stik, brugeren har bekræftet her på pc'en
/// (/_fd/confirm) inden for 5 minutter. Browseren taler med agenten gennem LocalWeb (kun loopback) – agentens token forlader aldrig agenten.
/// </summary>
public sealed partial class JobRunner
{
    /// <summary>/_fd/claim: send engangskoden til serveren over agentens egen kanal (sat, når agenten er tilmeldt).</summary>
    public Func<string, CancellationToken, Task<BiosUsbClaimResponse>>? BiosUsbClaim { get; set; }

    /// <summary>LocalWebs handler for /_fd/ (claim og confirm).</summary>
    public Task<LocalWeb.LocalReply> BiosUsbLocalReply(string path, IReadOnlyDictionary<string, string> q, CancellationToken ct) =>
        BiosUsbWin.Local(path, q, new WinUsbHost(runner), BiosUsbLocal.Shared, BiosUsbClaim, ct);

    private async Task<JobOutcome> BiosUsbJob(AgentJob j, CancellationToken ct)
    {
        if (j.Params.BiosUsb is not { SessionId.Length: > 0 } o) return new(JobStatus.Failed, 1, "Jobbet mangler guidens session.");
        var h = new WinUsbHost(runner);
        if (j.Type == JobTypes.UsbDisks)
        {
            var rep = await BiosUsbWin.List(h, o, BiosUsbLocal.Shared, ct);
            return rep is null ? new(JobStatus.Failed, 1, BiosUsbWin.ListFailed)
                : new(JobStatus.Success, 0, rep.Message, Bios: new JobResult { BiosUsb = rep });
        }

        // bios_usb_write: hent pakken til agentens arbejdsmappe (kun SYSTEM og administratorer), tjek den og skriv stikket
        if (DownloadFile is null) return new(JobStatus.Failed, 1, "Agenten er ikke klar til USB-stik endnu.");
        var work = Path.Combine(opt.Work, "usb-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(work);
            var file = Path.Combine(work, "package.bin");
            LiveJob.Phase(0, 5, "Henter pakken …");
            try { await DownloadFile($"/api/agent/bios/usb/{Uri.EscapeDataString(o.SessionId)}/package", file, ct); }
            catch (HttpRequestException e) { return new(JobStatus.Failed, 1, "Pakken kunne ikke hentes fra Fleetdeck: " + e.Message); }
            var log = new List<string>();
            var r = await Task.Run(() => BiosUsbWin.Write(h, o, file, BiosUsbLocal.Shared, log.Add, LiveJob.Phase, ct), ct);
            var ok = r.Outcome == "ok";
            return new(ok ? JobStatus.Success : JobStatus.Failed, ok ? 0 : 1, string.Join('\n', log), Bios: new JobResult { BiosUsb = r });
        }
        finally { try { Directory.Delete(work, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    }
}

/// <summary>Den rigtige Windows-maskine: PowerShell som SYSTEM, afmontering med FSCTL og "Sikker fjernelse" med CM_Request_Device_Eject.</summary>
internal sealed class WinUsbHost(Runner runner) : IWinUsbHost
{
    public async Task<WinPs> PowerShell(string script, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            var r = await runner.PowerShell(script, RunAs.System, timeout, ct);
            return new WinPs(r.ExitCode, r.Output, r.TimedOut);
        }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException) { return new WinPs(null, "PowerShell kunne ikke startes: " + e.Message); }
    }

    public string Root(string letter) => letter + @":\";

    public string? Dismount(string letter)
    {
        if (letter.Length != 1 || !char.IsAsciiLetter(letter[0])) return "ugyldigt drevbogstav";
        using var h = CreateFileW($@"\\.\{letter}:", GenericRead | GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (h.IsInvalid) return new Win32Exception(Marshal.GetLastWin32Error()).Message;
        if (!FlushFileBuffers(h)) return new Win32Exception(Marshal.GetLastWin32Error()).Message;
        // Lås, hvis ingen andre har filer åbne (Stifinder, antivirus); afmonteringen virker også uden og lukker deres håndtag
        for (var i = 0; i < 5 && !DeviceIoControl(h, FsctlLockVolume, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero); i++) Thread.Sleep(300);
        return DeviceIoControl(h, FsctlDismountVolume, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero)
            ? null : new Win32Exception(Marshal.GetLastWin32Error()).Message;
    }

    public bool Eject(string devicePath)
    {
        if (BiosUsbWin.InstanceId(devicePath) is not { } id || CM_Locate_DevNodeW(out var disk, id, 0) != 0) return false;
        // USB-enheden (forælderen til disken) skubbes ud – som "Sikker fjernelse af hardware". Ellers disken selv.
        var targets = CM_Get_Parent(out var parent, disk, 0) == 0 ? new[] { parent, disk } : [disk];
        var veto = new char[260];
        for (var attempt = 0; attempt < 3; attempt++)
        {
            foreach (var t in targets)
                if (CM_Request_Device_EjectW(t, out var vetoType, veto, veto.Length, 0) == 0 && vetoType == 0) return true;
            Thread.Sleep(1000);
        }
        return false;
    }

    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, FileShareRead = 1, FileShareWrite = 2, OpenExisting = 3;
    private const uint FsctlLockVolume = 0x00090018, FsctlDismountVolume = 0x00090020;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FlushFileBuffers(SafeFileHandle h);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, uint inSize, IntPtr outBuf, uint outSize, out uint returned, IntPtr overlapped);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern int CM_Locate_DevNodeW(out uint devInst, string id, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern int CM_Get_Parent(out uint parent, uint devInst, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Request_Device_EjectW(uint devInst, out int vetoType, [Out] char[] vetoName, int nameLength, uint flags);
}
