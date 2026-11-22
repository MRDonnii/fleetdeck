using System.Diagnostics;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Windows;

/// <summary>Planlagt genstart/slukning med shutdown.exe. Planen huskes, så ikonet kan tælle ned, og websiden kan annullere.</summary>
public static class WinPower
{
    public static void Schedule(string mode, int delaySec, string msg, bool force = true)
    {
        Shutdown("/a");   // afløser en tidligere plan
        Shutdown(PlannedPower.WindowsArgs(mode, delaySec, msg, force));
        PlannedPower.Set(mode, DateTime.UtcNow.AddSeconds(delaySec));
    }

    /// <summary>
    /// "Genstart nu"/"Sluk nu" fra skærmen: samme vej som det planlagte tidspunkt, bare nu og altid med /f.
    /// Kun den handling, Fleetdeck har planlagt på denne maskine – ellers sker der intet (null).
    /// </summary>
    public static string? Now(string? asked = null)
    {
        if (PlannedPower.NowAllowed(PlannedPower.Current, asked) is not { } mode) return null;
        Schedule(mode, 0, mode == JobTypes.Reboot ? "Fleetdeck: genstarter nu (valgt på skærmen)." : "Fleetdeck: slukker nu (valgt på skærmen).", force: true);
        return mode;
    }

    /// <summary>Annullér; med <paramref name="postponeSec"/> planlægges samme handling igen så meget senere. Falsk, hvis intet var planlagt.</summary>
    public static bool Cancel(int? postponeSec = null)
    {
        var cur = PlannedPower.Current;
        Shutdown("/a");
        PlannedPower.Clear();
        if (cur is null) return false;
        if (postponeSec is int s && s > 0)
            Schedule(cur.Value.Mode, s, $"Fleetdeck: {(cur.Value.Mode == JobTypes.Reboot ? "pc'en genstarter" : "pc'en slukker")} om {s / 60} minutter (udsat).");
        return true;
    }

    private static void Shutdown(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("shutdown.exe", args) { CreateNoWindow = true, UseShellExecute = false });
        p?.WaitForExit(10000);
    }
}
