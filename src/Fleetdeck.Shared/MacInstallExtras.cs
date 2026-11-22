using System.Text.RegularExpressions;

namespace Fleetdeck.Shared;

/// <summary>
/// Tilføjelser til <see cref="MacInstall"/> uden at røre MacApp.cs (den native menus versionsstempel bor dér):
/// osascripts fejltekst renset til brugeren, og arkitekturen med legacy-bygget til ældre Intel-Mac'er.
/// </summary>
public static partial class MacInstall
{
    [GeneratedRegex(@"^\s*\d+:\d+:\s*execution error:\s*(.*?)\s*\(-?\d+\)\s*$", RegexOptions.Singleline)] private static partial Regex OsaExecutionError();
    [GeneratedRegex(@"^\s*\d+:\d+:\s*execution error:")] private static partial Regex OsaErrorStart();

    /// <summary>
    /// osascripts fejltekst uden AppleScript-indpakningen: "0:767: execution error: Serveren tager ikke imod … (3)" → "Serveren tager ikke imod …".
    /// "do shell script" giver scriptets linjer med \r imellem; de bliver til almindelige linjeskift. Anden tekst røres ikke.
    /// </summary>
    public static string CleanOsaOutput(string output)
    {
        var s = output.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = s.Split('\n').ToList();
        // fejlen står på den første linje, der starter med "N:M: execution error:", og fortsætter til "(n)" til sidst
        var start = lines.FindIndex(l => OsaErrorStart().IsMatch(l));
        if (start < 0) return s.Trim();
        var tail = string.Join("\n", lines.Skip(start));
        var m = OsaExecutionError().Match(tail);
        if (!m.Success) return s.Trim();
        return string.Join("\n", lines.Take(start).Append(m.Groups[1].Value)).Trim();
    }

    /// <summary>Som <see cref="Arch(System.Runtime.InteropServices.Architecture)"/>, men "x64-legacy" på en Intel-Mac med macOS ældre end 12 (se <see cref="MacCompat"/>).</summary>
    public static string Arch(System.Runtime.InteropServices.Architecture a, Version? macOS) => MacCompat.ArchFor(a, macOS);

    /// <summary>Denne Macs arkitektur og macOS-udgave (Environment.OSVersion er macOS-udgaven, fx 11.7.10).</summary>
    public static string ThisArch => Arch(System.Runtime.InteropServices.RuntimeInformation.OSArchitecture, Environment.OSVersion.Version);
}
