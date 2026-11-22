using System.Security.Cryptography;
using Fleetdeck.Shared;
using static Fleetdeck.Shared.MacCodeSign;

namespace Fleetdeck.Agent.Unix;

/// <summary>
/// macOS: signér agenten med en fast lokal identitet (se <see cref="MacCodeSign"/>), så "Fuld diskadgang" overlever selvopdateringer.
/// Identiteten (selvsigneret certifikat + nøgle) laves første gang og ligger i en egen nøglering i /usr/local/fleetdeck,
/// låst op med en tilfældig adgangskode, som kun root kan læse. Går noget galt, bruges ad hoc-signatur som før – opdateringen fejler aldrig af det.
/// </summary>
public static class MacSign
{
    public const string TccDb = "/Library/Application Support/com.apple.TCC/TCC.db";
    private const string TriedFile = Dir + "/signing.tried";
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(30), Long = TimeSpan.FromMinutes(2);

    public static string Hash(string file)
    {
        using var fs = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(fs))[..12];
    }

    private static string? Read(string file) { try { return File.Exists(file) ? File.ReadAllText(file).Trim() : null; } catch (IOException) { return null; } catch (UnauthorizedAccessException) { return null; } }

    private static void WriteRootOnly(string file, string text)
    {
        File.WriteAllText(file, text);
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static string Tail(string s) { s = s.Trim().Replace('\n', ' '); return s.Length > 200 ? s[^200..] : s; }

    /// <summary>Den version agenten skal melde til serveren (serverens hash, også efter vi har signeret filen).</summary>
    public static string ReportedVersion(string exe) => MacCodeSign.ReportedVersion(Hash(exe), Read(OriginFile));

    /// <summary>Signaturen på en fil (codesign -dv).</summary>
    public static Signature Describe(string file) => MacCodeSign.Describe(Shell.Run(DescribeCmd(file), Short, null, int.MaxValue).Output);

    /// <summary>
    /// Signér en ny agentfil, før den lægges på plads. Fast identitet, hvis det kan lade sig gøre, ellers ad hoc (som før).
    /// Returnerer true ved fast signatur. Kaster aldrig.
    /// </summary>
    public static bool Sign(string file, string originalHash)
    {
        string why;
        try
        {
            Shell.Run($"xattr -d com.apple.quarantine {Q(file)} 2>/dev/null; true", Short);
            var sha1 = EnsureIdentity(out why);
            if (sha1 is not null)
            {
                Shell.Run(UnlockCmd(Read(PasswordFile) ?? ""), Short);
                var r = Shell.Run(SignCmd(file, sha1), Long);
                // ældre macOS (fx 13 på Intel): en tjeneste uden skærm kan ikke bruge nøglen ("could not be found in the keychain"),
                // men det kan den godt inde i den indloggede brugers session – stadig som root
                if (r.ExitCode != 0 && r.Output.Contains("could not be found in the keychain", StringComparison.OrdinalIgnoreCase)
                    && Shell.Out("stat -f %u /dev/console").Trim() is { Length: > 0 } uid && uid != "0")
                    r = Shell.Run($"launchctl asuser {uid} sh -c {Q(UnlockCmd(Read(PasswordFile) ?? "") + " && " + SignCmd(file, sha1))}", Long);
                var sig = Describe(file);
                if (r.ExitCode == 0 && sig.Stable)
                {
                    WriteOrigin(file, originalHash);
                    Log.Info($"Agenten er signeret med den faste lokale identitet ({CommonName}, {sha1[..8]}…). Fuld diskadgang bevares ved opdateringer.");
                    return true;
                }
                why = $"codesign: {Tail(r.Output)} (signatur nu: {sig.Text})";
            }
        }
        catch (Exception e) { why = e.Message; }
        Log.Warn($"Kunne ikke signere agenten med den faste lokale identitet: {why}. Bruger ad hoc-signatur i stedet – " +
                 "Fuld diskadgang forsvinder så ved hver opdatering og skal gives igen.");
        try { Shell.Run(AdhocCmd(file), Long); WriteOrigin(file, originalHash); }
        catch (Exception e) { Log.Warn($"Ad hoc-signatur fejlede også: {e.Message}"); }
        return false;
    }

    private static void WriteOrigin(string file, string originalHash)
    {
        try { WriteRootOnly(OriginFile, OriginLine(Hash(file), originalHash)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warn($"Kunne ikke gemme agentens version: {e.Message}"); }
    }

    /// <summary>SHA-1 på den faste identitet. Laves første gang (og igen, hvis nøgleringen er gået i stykker).</summary>
    private static string? EnsureIdentity(out string why)
    {
        why = "";
        if (File.Exists(Keychain) && Read(PasswordFile) is { Length: > 0 } old)
        {
            if (Shell.Run(UnlockCmd(old), Short).ExitCode == 0
                && FindIdentity(Shell.Run(FindIdentityCmd(), Short, null, int.MaxValue).Output) is { } have) return have;
            Log.Warn("Den lokale signeringsnøglering kan ikke bruges – laver en ny. Fuld diskadgang skal gives igen én gang.");
        }
        Directory.CreateDirectory(Dir);
        Shell.Run($"{Env} security delete-keychain {Q(Keychain)} >/dev/null 2>&1; rm -f {Q(Keychain)}", Short);
        var pw = NewPassword();
        WriteRootOnly(PasswordFile, pw);
        var r = Shell.Run(CreateKeychainCmd(pw), Short);
        if (r.ExitCode != 0) { why = "kunne ikke oprette nøgleringen: " + Tail(r.Output); return null; }
        if ((r = Shell.Run(UnlockCmd(pw), Short)).ExitCode != 0) { why = "kunne ikke låse nøgleringen op: " + Tail(r.Output); return null; }

        var pfxPw = NewPassword();
        var (pfx, sha1) = CreateIdentity(pfxPw);
        var pfxFile = Dir + "/signing-import.p12";
        try
        {
            File.WriteAllBytes(pfxFile, pfx);
            File.SetUnixFileMode(pfxFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            if ((r = Shell.Run(ImportCmd(pfxFile, pfxPw), Short)).ExitCode != 0) { why = "kunne ikke importere certifikatet: " + Tail(r.Output); return null; }
        }
        finally { try { File.Delete(pfxFile); } catch (IOException) { } }
        // uden partitionslisten venter codesign på en godkendelse, som ingen kan se (agenten kører uden skærm)
        if ((r = Shell.Run(PartitionCmd(pw), Short)).ExitCode != 0) { why = "kunne ikke give codesign adgang til nøglen: " + Tail(r.Output); return null; }
        var found = FindIdentity(Shell.Run(FindIdentityCmd(), Short, null, int.MaxValue).Output);
        if (found != sha1) { why = "identiteten kan ikke findes i nøgleringen efter import"; return null; }
        Log.Info($"Lavede en fast lokal signeringsidentitet \"{CommonName}\" ({sha1[..8]}…) i {Keychain}.");
        return sha1;
    }

    /// <summary>
    /// Ved start: kører agenten med ad hoc-signatur (fx lige efter opdatering fra en version uden fast signatur), så signér en kopi,
    /// læg den på plads og genstart (true). Prøves én gang pr. fil, så en Mac, hvor det ikke virker, ikke genstarter i ring.
    /// </summary>
    public static bool EnsureSelfSigned(string version)
    {
        try
        {
            var exe = Environment.ProcessPath!;
            if (!exe.StartsWith(Dir + "/", StringComparison.Ordinal) || Describe(exe).Stable) return false;
            var hash = Hash(exe);
            if (Read(TriedFile) == hash) return false;
            WriteRootOnly(TriedFile, hash);
            var tmp = exe + ".new";
            File.Copy(exe, tmp, true);
            File.SetUnixFileMode(tmp, (UnixFileMode)0b111_101_101);
            if (!Sign(tmp, version)) { File.Delete(tmp); return false; }
            WriteRootOnly(TriedFile, Hash(tmp));
            File.Move(tmp, exe, true);
            Log.Info("Agenten har fået en fast signatur. Genstarter. Giv Fuld diskadgang igen én sidste gang, hvis Fleetdeck beder om det.");
            return true;
        }
        catch (Exception e) { Log.Warn($"Kunne ikke give agenten en fast signatur ved start: {e.Message}"); return false; }
    }

    /// <summary>Helbred: har agenten Fuld diskadgang, bruges den til noget (iPad-backup), og er signaturen fast?</summary>
    public static MacAccessInfo Access()
    {
        bool? fda;
        // systemets TCC-database kan kun åbnes med Fuld diskadgang – også af root
        try { using var _ = File.OpenRead(TccDb); fda = true; }
        catch (UnauthorizedAccessException) { fda = false; }
        catch (IOException) { fda = null; }
        var backup = $"{Mac.Session()?.Home ?? "/var/root"}/Library/Application Support/MobileSync/Backup";
        var hasBackupDir = Directory.Exists(backup);
        if (fda is null && hasBackupDir)
        {
            try { _ = Directory.EnumerateFileSystemEntries(backup).Any(); fda = true; }
            catch (UnauthorizedAccessException) { fda = false; }
            catch (IOException) { }
        }
        var exe = Environment.ProcessPath ?? Dir + "/fleetdeck-agent";
        var sig = Describe(exe);
        return new MacAccessInfo
        {
            FullDiskAccess = fda, NeededForIpadBackup = Ios.Tool is not null || hasBackupDir,
            StableSignature = sig.Stable, Signature = sig.Text, Path = exe,
        };
    }
}
