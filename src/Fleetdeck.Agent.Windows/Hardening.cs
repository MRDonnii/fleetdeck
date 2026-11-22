using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Fleetdeck.Agent.Windows;

/// <summary>
/// Adgangsrettigheder på Windows: agenten kører som SYSTEM, så alt, hvad den kører eller indlæser, må kun kunne ændres af
/// SYSTEM, administratorer og TrustedInstaller. Ellers kan en almindelig bruger bytte filen (eller lægge en DLL ved siden af)
/// og få sin kode kørt med SYSTEM-rettigheder.
/// </summary>
/// <remarks>
/// Logikken her er ren (ingen Windows-kald), så den kan testes på alle platforme. <see cref="WindowsAcl"/> læser og skriver
/// de rigtige ACL'er.
/// </remarks>
public static class Sids
{
    public const string System = "S-1-5-18";
    public const string Administrators = "S-1-5-32-544";
    public const string Users = "S-1-5-32-545";
    public const string CreatorOwner = "S-1-3-0";
    public const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    /// <summary>
    /// Kun disse må kunne ændre noget, agenten kører: SYSTEM, administratorer og tjeneste-SID'er (NT SERVICE\…, fx
    /// TrustedInstaller og AppX-tjenesten på WindowsApps). En almindelig bruger kan aldrig få en tjeneste-SID.
    /// </summary>
    public static bool Trusted(string? sid) => sid is System or Administrators || sid?.StartsWith("S-1-5-80-", StringComparison.Ordinal) == true;
}

/// <summary>Adgangsmasker (Windows' FILE_*-rettigheder), så logikken ikke afhænger af Windows-typer.</summary>
public static class Access
{
    public const int WriteData = 0x2;            // fil: skriv; mappe: opret filer
    public const int AppendData = 0x4;           // fil: tilføj; mappe: opret undermapper
    public const int WriteEa = 0x10;
    public const int DeleteChild = 0x40;         // mappe: slet filer og undermapper
    public const int WriteAttributes = 0x100;
    public const int Delete = 0x10000;
    public const int WriteDac = 0x40000;
    public const int WriteOwner = 0x80000;
    public const int GenericAll = 0x10000000;
    public const int GenericWrite = 0x40000000;

    public const int ReadAndExecute = 0x1200A9;
    public const int Modify = 0x1301BF;
    public const int FullControl = 0x1F01FF;

    /// <summary>Rettigheder, der lader nogen ændre, slette eller give sig selv adgang til objektet.</summary>
    public const int ChangesObject = WriteData | AppendData | Delete | WriteDac | WriteOwner | GenericAll | GenericWrite;
    /// <summary>På mappen med filen: oprette (fx en DLL ved siden af programmet), slette eller omdøbe noget i den.</summary>
    public const int ChangesFolder = WriteData | AppendData | DeleteChild | Delete | WriteDac | WriteOwner | GenericAll | GenericWrite;
    /// <summary>På mapperne længere oppe: omdøbe eller slette en mappe på vejen, eller overtage den.</summary>
    public const int ChangesAncestor = DeleteChild | Delete | WriteDac | WriteOwner | GenericAll;
}

public enum AceScope { ThisOnly, ThisAndChildren, FilesOnly }

public sealed record AceInfo(string Sid, int Mask, bool Allow = true, bool InheritOnly = false);

/// <summary>Sikkerheden på én sti. Null fra læseren = stien findes ikke.</summary>
public sealed record PathSecurity(string? OwnerSid, bool IsReparsePoint, bool IsDirectory, IReadOnlyList<AceInfo> Aces);

public interface ISecurityReader
{
    /// <summary>Null hvis stien ikke findes. Kaster, hvis den ikke kan læses (så afvises den).</summary>
    PathSecurity? Read(string path);
}

/// <summary>Afviser at køre (eller indlæse) noget fra en placering, som andre end SYSTEM og administratorer kan ændre.</summary>
public static class ExecGuard
{
    /// <summary>Null = sikker. Ellers hvorfor filen ikke må køres.</summary>
    public static string? Check(string file, ISecurityReader reader) => CheckPath(file, reader, isFile: true);

    /// <summary>Null = mappen er sikker at køre programmer fra (ingen andre kan lægge filer i den eller bytte den).</summary>
    public static string? CheckFolder(string dir, ISecurityReader reader) => CheckPath(dir, reader, isFile: false);

    private static string? CheckPath(string path, ISecurityReader reader, bool isFile)
    {
        if (WinPath.Normalize(path) is not { } full) return $"{path} er ikke en fuld sti på et lokalt drev";
        try
        {
            var level = isFile ? 0 : 1;   // 0 = selve filen, 1 = mappen med filen, 2+ = længere oppe
            for (var cur = full; cur is not null; cur = WinPath.Parent(cur), level++)
            {
                var s = reader.Read(cur);
                if (s is null) return $"{cur} findes ikke";
                if (s.IsReparsePoint) return $"{cur} er en genvej (reparse point)";
                if (level == 0 && s.IsDirectory) return $"{cur} er en mappe, ikke en fil";
                if (!Sids.Trusted(s.OwnerSid)) return $"{cur} ejes af {s.OwnerSid ?? "ukendt"}";
                var bad = level == 0 ? Access.ChangesObject : level == 1 ? Access.ChangesFolder : Access.ChangesAncestor;
                if (s.Aces.FirstOrDefault(a => a.Allow && !a.InheritOnly && a.Sid != Sids.CreatorOwner && !Sids.Trusted(a.Sid) && (a.Mask & bad) != 0) is { } ace)
                    return $"{cur} kan ændres af {ace.Sid}";
            }
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException
                                      or System.Security.SecurityException or PlatformNotSupportedException)
        {
            return $"rettighederne på {path} kunne ikke læses ({e.Message})";
        }
    }

    /// <summary>Programfilen i en kommandolinje (fx en UninstallString): "C:\…\x.exe" /S eller C:\…\x.exe /S. Null hvis ingen fuld sti.</summary>
    public static string? ExeOfCommandLine(string? cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return null;
        var c = cmd.Trim();
        string exe;
        if (c.StartsWith('"')) { var end = c.IndexOf('"', 1); if (end < 0) return null; exe = c[1..end]; }
        else
        {
            var i = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            exe = i < 0 ? c.Split(' ')[0] : c[..(i + 4)];
        }
        return WinPath.Normalize(exe) is not null ? exe : null;
    }
}

/// <summary>Windows-stier uden at spørge styresystemet (så reglerne også kan testes på Linux).</summary>
public static class WinPath
{
    /// <summary>"C:\\a\\b" (uden afsluttende \\, undtagen roden "C:\\"). Null for relative stier, netværksstier og stier med "..".</summary>
    public static string? Normalize(string? p)
    {
        if (p is null) return null;
        p = p.Replace('/', '\\');
        if (p.Length < 3 || !char.IsAsciiLetter(p[0]) || p[1] != ':' || p[2] != '\\') return null;
        var parts = p[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(x => x is "." or "..") || p.IndexOfAny(['*', '?', '"', '<', '>', '|']) >= 0 || p.IndexOf(':', 2) >= 0) return null;
        return parts.Length == 0 ? p[..3] : p[..3] + string.Join('\\', parts);
    }

    /// <summary>Mappen over stien; null for roden.</summary>
    public static string? Parent(string p)
    {
        if (p.Length <= 3) return null;
        var i = p.LastIndexOf('\\');
        return i <= 2 ? p[..3] : p[..i];
    }
}

/// <summary>Ønsket ACL på én mappe.</summary>
public sealed record DirAcl(string Path, string OwnerSid, bool Protected, IReadOnlyList<(string Sid, int Mask, AceScope Scope)> Aces);

/// <summary>Hvad agenten selv sætter op: datamappen, arbejdsmappen og brugermappen til ikonet.</summary>
public static class Hardening
{
    /// <summary>Filer og mapper i userwork, som tjenesten selv laver. Alt andet (rester fra ældre versioner) fjernes.</summary>
    public static readonly string[] UserWorkKeep = ["status.json", "helpdesk.json", "cmd", "out"];

    /// <summary>Programmappen (Program Files\Fleetdeck): kun SYSTEM og administratorer skriver; brugere kører ikonet herfra.</summary>
    public static DirAcl InstallDir(string path) => new(path, Sids.Administrators, true,
        [(Sids.System, Access.FullControl, AceScope.ThisAndChildren), (Sids.Administrators, Access.FullControl, AceScope.ThisAndChildren),
         (Sids.Users, Access.ReadAndExecute, AceScope.ThisAndChildren)]);

    /// <summary>Datamappen (ProgramData\Fleetdeck): kun SYSTEM og administratorer. work arver herfra.</summary>
    public static DirAcl DataDir(string path) => new(path, Sids.Administrators, true,
        [(Sids.System, Access.FullControl, AceScope.ThisAndChildren), (Sids.Administrators, Access.FullControl, AceScope.ThisAndChildren)]);

    /// <summary>En undermappe, der kun arver (fx work), uden egne regler.</summary>
    public static DirAcl Inherit(string path) => new(path, Sids.Administrators, false, []);

    /// <summary>userwork: brugere må kun læse. Ingen af dem kan lægge filer, oprette mapper eller genveje her.</summary>
    public static DirAcl UserWork(string path) => new(path, Sids.Administrators, true,
        [(Sids.System, Access.FullControl, AceScope.ThisAndChildren), (Sids.Administrators, Access.FullControl, AceScope.ThisAndChildren),
         (Sids.Users, Access.ReadAndExecute, AceScope.ThisAndChildren)]);

    /// <summary>
    /// userwork\cmd (ordrer fra ikonet) og userwork\out (output fra jobs i brugerens session): brugere må oprette filer – ikke
    /// mapper, så der kan ikke laves en junction herinde – og kun ændre de filer, de selv har lavet. Tjenesten kører aldrig noget herfra.
    /// </summary>
    public static DirAcl Dropbox(string path) => new(path, Sids.Administrators, false,
        [(Sids.Users, Access.WriteData, AceScope.ThisOnly), (Sids.CreatorOwner, Access.Modify, AceScope.FilesOnly)]);

    /// <summary>En midlertidig mappe til et script eller installationsprogram, der køres i brugerens session: kun den bruger må læse.</summary>
    public static DirAcl UserRun(string path, string userSid) => new(path, Sids.Administrators, false,
        [(userSid, Access.ReadAndExecute, AceScope.ThisAndChildren)]);

    /// <summary>Hvad der skal fjernes fra userwork: alt, der ikke er tjenestens eget, og alt, der er en genvej eller ejes af en bruger.</summary>
    public static List<string> UserWorkLeftovers(IEnumerable<(string Name, PathSecurity Sec)> entries)
    {
        var del = new List<string>();
        foreach (var (name, sec) in entries)
        {
            var keep = UserWorkKeep.Contains(name, StringComparer.OrdinalIgnoreCase) && !sec.IsReparsePoint && Sids.Trusted(sec.OwnerSid)
                       && sec.IsDirectory == (name is "cmd" or "out");
            if (!keep) del.Add(name);
        }
        return del;
    }

    /// <summary>Er mappen allerede, som den skal være (ingen andre end de betroede kan skrive)? Ellers sættes ACL'en igen.</summary>
    public static bool NeedsRepair(string dir, ISecurityReader reader) => ExecGuard.CheckFolder(dir, reader) is not null;

    /// <summary>
    /// Sætter rettighederne på programmappen, datamappen, work og userwork – ved installation og ved hver start af tjenesten,
    /// så også ældre installationer (hvor userwork gav brugere "Rediger") bliver rettet. Fejl logges og stopper ikke agenten.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static void Apply(AgentOptions opt, Action<string> log, string? installDir = null)
    {
        var r = new WindowsAcl();
        void Try(string what, Action a)
        {
            try { a(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException or ArgumentException)
            { log($"Rettigheder ({what}): {e.Message}"); }
        }
        if (installDir is { } inst && Directory.Exists(inst))
            Try(inst, () => { if (NeedsRepair(inst, r)) { WindowsAcl.Apply(InstallDir(inst)); log($"Rettighederne på {inst} er rettet"); } });
        Try(opt.Dir, () =>
        {
            WindowsAcl.EnsurePlainDir(opt.Dir);
            if (NeedsRepair(opt.Dir, r)) { WindowsAcl.Apply(DataDir(opt.Dir)); log($"Rettighederne på {opt.Dir} er rettet"); }
        });
        Try(opt.Work, () =>
        {
            WindowsAcl.EnsurePlainDir(opt.Work);
            if (NeedsRepair(opt.Work, r)) { WindowsAcl.Apply(Inherit(opt.Work)); log($"Rettighederne på {opt.Work} er rettet"); }
        });
        Try(opt.UserWork, () =>
        {
            WindowsAcl.EnsurePlainDir(opt.UserWork);
            WindowsAcl.Apply(UserWork(opt.UserWork));
            var entries = new DirectoryInfo(opt.UserWork).EnumerateFileSystemInfos()
                .Select(i => (i.Name, Sec: r.Read(i.FullName) ?? new PathSecurity(null, false, false, []))).ToList();
            foreach (var name in UserWorkLeftovers(entries))
            {
                WindowsAcl.RemoveNoFollow(Path.Combine(opt.UserWork, name));
                log($"Fjernet fra userwork: {name}");
            }
        });
        EnsureDropboxes(opt, log);
    }

    /// <summary>userwork\cmd og userwork\out findes og har de rigtige rettigheder (aldrig gennem en genvej).</summary>
    [SupportedOSPlatform("windows")]
    public static void EnsureDropboxes(AgentOptions opt, Action<string> log)
    {
        foreach (var d in new[] { Path.Combine(opt.UserWork, "cmd"), Path.Combine(opt.UserWork, "out") })
        {
            try
            {
                WindowsAcl.EnsurePlainDir(d);
                WindowsAcl.Apply(Dropbox(d));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException or ArgumentException)
            { log($"Rettigheder ({d}): {e.Message}"); }
        }
    }

    /// <summary>
    /// Ny mappe i work til et script eller installationsprogram. Kører det i brugerens session, får kun den bruger lov at læse;
    /// ingen bruger kan skrive i den. Slet den med <see cref="WindowsAcl.RemoveNoFollow"/> bagefter.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static string NewRunDir(AgentOptions opt, bool forUser)
    {
        WindowsAcl.EnsurePlainDir(opt.Work);
        var dir = Path.Combine(opt.Work, (forUser ? "u-" : "run-") + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        if (forUser) WindowsAcl.Apply(UserRun(dir, Native.ConsoleUserSid() ?? "S-1-5-4" /* INTERACTIVE, hvis brugeren ikke kan slås op */));
        return dir;
    }
}

/// <summary>De rigtige ACL'er på Windows.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAcl : ISecurityReader
{
    public PathSecurity? Read(string path)
    {
        var isDir = Directory.Exists(path);
        if (!isDir && !File.Exists(path)) return null;
        FileSystemSecurity sec = isDir ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner)
                                       : new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        var attrs = File.GetAttributes(path);
        var owner = sec.GetOwner(typeof(SecurityIdentifier))?.Value;
        var aces = sec.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Select(a => new AceInfo(a.IdentityReference.Value, (int)a.FileSystemRights, a.AccessControlType == AccessControlType.Allow,
                a.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)))
            .ToList();
        return new PathSecurity(owner, attrs.HasFlag(FileAttributes.ReparsePoint), isDir, aces);
    }

    public static void Apply(DirAcl a)
    {
        var ds = new DirectorySecurity();
        ds.SetAccessRuleProtection(a.Protected, false);
        ds.SetOwner(new SecurityIdentifier(a.OwnerSid));
        foreach (var (sid, mask, scope) in a.Aces)
        {
            var (inh, prop) = scope switch
            {
                AceScope.ThisOnly => (InheritanceFlags.None, PropagationFlags.None),
                AceScope.FilesOnly => (InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly),
                _ => (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None),
            };
            ds.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), (FileSystemRights)mask, inh, prop, AccessControlType.Allow));
        }
        new DirectoryInfo(a.Path).SetAccessControl(ds);
    }

    /// <summary>Mappen findes og er en almindelig mappe. Er den en genvej (junction/symlink) eller en fil, fjernes den først – uden at følge den.</summary>
    public static void EnsurePlainDir(string dir)
    {
        if (File.Exists(dir) || (Directory.Exists(dir) && File.GetAttributes(dir).HasFlag(FileAttributes.ReparsePoint))) RemoveNoFollow(dir);
        Directory.CreateDirectory(dir);
    }

    /// <summary>Sletter en fil, en mappe (rekursivt) eller en genvej – en genvej slettes selv, aldrig det, den peger på.</summary>
    public static void RemoveNoFollow(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        var attrs = File.GetAttributes(path);
        if (attrs.HasFlag(FileAttributes.Directory))
        {
            if (attrs.HasFlag(FileAttributes.ReparsePoint)) { Directory.Delete(path, false); return; }   // fjerner kun genvejen
            foreach (var e in new DirectoryInfo(path).EnumerateFileSystemInfos()) RemoveNoFollow(e.FullName);
            Directory.Delete(path, false);
        }
        else
        {
            if (attrs.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
            File.Delete(path);
        }
    }
}
