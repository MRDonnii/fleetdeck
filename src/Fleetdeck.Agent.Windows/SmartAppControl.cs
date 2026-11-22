using System.Diagnostics;
using Microsoft.Win32;

namespace Fleetdeck.Agent.Windows;

/// <summary>
/// Smart App Control (Windows 11) blokerer programmer uden kendt omdømme – også dem, Fleetdeck installerer og opdaterer.
/// Den kan slås fra her, men Microsoft tillader ikke at slå den til igen uden at nulstille/geninstallere Windows.
/// </summary>
internal static class SmartAppControl
{
    private const string PolicyKey = @"SYSTEM\CurrentControlSet\Control\CI\Policy";
    private const string Value = "VerifiedAndReputablePolicyState";

    /// <summary>0 = fra, 1 = til, 2 = evaluering (kan selv slå sig til), null = findes ikke (Windows 10 o.l.).</summary>
    public static int? State()
    {
        try { using var k = Registry.LocalMachine.OpenSubKey(PolicyKey); return k?.GetValue(Value) is int v ? v : null; }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    public static bool Active => State() is 1 or 2;

    public static string Describe(int? s) => s switch { 1 => "slået til", 2 => "i evalueringstilstand", 0 => "slået fra", _ => "findes ikke" };

    /// <summary>Slår den fra og anvender politikken med det samme (CiTool); ellers ved næste genstart. Kræver administrator.</summary>
    public static (string Message, bool RebootNeeded) Disable()
    {
        var before = State();
        if (before is null or 0) return ($"Smart App Control er {Describe(before)} – intet at gøre.", false);
        using (var k = Registry.LocalMachine.CreateSubKey(PolicyKey, true))
            k.SetValue(Value, 0, RegistryValueKind.DWord);
        var ci = Path.Combine(Environment.SystemDirectory, "CiTool.exe");
        var applied = false;
        if (File.Exists(ci))
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(ci, "--refresh --json") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
                applied = p.WaitForExit(30_000) && p.ExitCode == 0;
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        }
        return applied ? ("Smart App Control er slået fra.", false) : ("Smart App Control er slået fra (træder i kraft efter næste genstart).", true);
    }
}
