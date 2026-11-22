using System.Diagnostics;
using Fleetdeck.Shared;
using Microsoft.Win32;

namespace Fleetdeck.Agent.Windows;

/// <summary>
/// Sikrer, at Fleetdeck altid starter af sig selv – også uden at nogen logger ind: tjenesten startes automatisk (forsinket, så en langsom
/// opstart ikke får Windows til at give op), genstartes ved fejl, og en vagthund (planlagt opgave som SYSTEM ved opstart og hvert 5. minut)
/// starter den, hvis den alligevel står stille. Tjekkes og rettes ved installation, ved start og ved hvert helbredstjek.
/// </summary>
internal static class Autostart
{
    private const string Svc = Installer.ServiceName;
    public const string TaskEvery = @"Fleetdeck\Vagthund", TaskBoot = @"Fleetdeck\Vagthund ved opstart";
    private static string WatchCmd => $"cmd.exe /c sc query {Svc} | findstr /i RUNNING >nul || sc start {Svc}";

    public static AutostartInfo Ensure()
    {
        var fixes = new List<string>();
        var problems = new List<string>();
        using (var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{Svc}"))
        {
            if (k is null) return new AutostartInfo { Ok = false, Detail = "Tjenesten findes ikke" };
            var start = k.GetValue("Start") as int?;
            var delayed = k.GetValue("DelayedAutostart") as int?;
            if (start != 2 || delayed != 1)
            {
                if (Run("sc.exe", $"config {Svc} start= delayed-auto") == 0) fixes.Add("automatisk start (forsinket) slået til");
                else problems.Add("tjenesten starter ikke automatisk");
            }
            if (k.GetValue("FailureActions") is null)
            {
                Run("sc.exe", $"failure {Svc} reset= 86400 actions= restart/5000/restart/10000/restart/30000");
                Run("sc.exe", $"failureflag {Svc} 1");
                fixes.Add("genstart ved fejl slået til");
            }
        }
        foreach (var (name, args) in new[]
        {
            (TaskEvery, $"/Create /F /RU SYSTEM /RL HIGHEST /SC MINUTE /MO 5 /TN \"{TaskEvery}\" /TR \"{WatchCmd}\""),
            (TaskBoot, $"/Create /F /RU SYSTEM /RL HIGHEST /SC ONSTART /DELAY 0001:30 /TN \"{TaskBoot}\" /TR \"{WatchCmd}\""),
        })
        {
            if (Run("schtasks.exe", $"/Query /TN \"{name}\"") == 0) continue;
            if (Run("schtasks.exe", args) == 0) fixes.Add($"vagthund oprettet ({name.Split('\\')[1]})");
            else problems.Add($"vagthunden \"{name}\" kunne ikke oprettes");
        }
        return new AutostartInfo
        {
            Ok = problems.Count == 0,
            Detail = problems.Count > 0 ? string.Join("; ", problems) : "Starter selv ved opstart uden login; vagthund hvert 5. minut",
            Fixed = fixes.Count > 0 ? string.Join("; ", fixes) : null,
        };
    }

    private static int Run(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
            return p.WaitForExit(30_000) ? p.ExitCode : -1;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return -1; }
    }

    /// <summary>Ved afinstallation: fjern vagthundene.</summary>
    public static void Remove()
    {
        Run("schtasks.exe", $"/Delete /F /TN \"{TaskEvery}\"");
        Run("schtasks.exe", $"/Delete /F /TN \"{TaskBoot}\"");
    }
}
