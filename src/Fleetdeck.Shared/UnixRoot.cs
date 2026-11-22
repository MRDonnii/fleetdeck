namespace Fleetdeck.Shared;

/// <summary>
/// Unix-agenten kører som root. Arbejdsmapper til gendannelsessæt og iPad-filer ligger derfor under /var/lib/fleetdeck
/// (ejet af root), aldrig i /var/tmp, hvor enhver bruger kan oprette mappen på forhånd eller lægge en genvej, før root bruger den.
/// </summary>
public static class UnixRoot
{
    public const string Base = "/var/lib/fleetdeck";
    /// <summary>Gendannelsespunkter (pakning og udpakning).</summary>
    public const string KitWork = Base + "/kit";
    /// <summary>iPad-jobs (backup, profiler).</summary>
    public const string IosWork = Base + "/ios";

    /// <summary>
    /// PATH for scripts, root kører: systemmapperne først. På en Mac med Intel kan /usr/local/bin ejes af den bruger, der
    /// installerede Homebrew – den må ikke kunne lægge et "ls" eller "tar", som root så kører.
    /// </summary>
    public static string RootPath(bool mac) => mac
        ? "/usr/sbin:/usr/bin:/sbin:/bin:/usr/local/sbin:/usr/local/bin:/opt/homebrew/bin"
        : "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin:/snap/bin";

    /// <summary>
    /// Opretter en mappe under <see cref="Base"/>, som kun root kan bruge (0700 på hvert niveau under /var/lib/fleetdeck).
    /// /var/lib ejes af root, så ingen anden kan have lavet mapperne på forhånd. Er et niveau en genvej eller en fil, fjernes det
    /// (genvejen selv, aldrig det, den peger på).
    /// </summary>
    public static string EnsureRootOnly(string dir)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        var rel = Relative(dir) ?? throw new ArgumentException($"{dir} ligger ikke under {Base}");
        EnsureBase(Base);
        const UnixFileMode rwx = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        var cur = Base;
        foreach (var part in rel)
        {
            cur += "/" + part;
            RemoveIfNotPlainDir(cur);
            Directory.CreateDirectory(cur, rwx);
            File.SetUnixFileMode(cur, rwx);
        }
        return cur;
    }

    /// <summary>Delene af stien under <see cref="Base"/> (null, hvis den ikke ligger dér eller indeholder "..").</summary>
    public static string[]? Relative(string dir)
    {
        if (!dir.StartsWith(Base + "/", StringComparison.Ordinal)) return null;
        var parts = dir[(Base.Length + 1)..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 || parts.Any(p => p is "." or "..") ? null : parts;
    }

    /// <summary>/var/lib/fleetdeck: almindelig mappe, 0755 (statusfilen læses af ikonet), ingen genvej.</summary>
    private static void EnsureBase(string dir)
    {
        RemoveIfNotPlainDir(dir);
        const UnixFileMode m = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                               UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        Directory.CreateDirectory(dir, m);
        // aldrig skrivbar for gruppe/andre (fx hvis nogen har lavet den med 0777)
        var cur = File.GetUnixFileMode(dir);
        if ((cur & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0) File.SetUnixFileMode(dir, cur & ~(UnixFileMode.GroupWrite | UnixFileMode.OtherWrite));
    }

    /// <summary>Fjerner en genvej eller en fil på stien. En almindelig mappe bevares.</summary>
    public static void RemoveIfNotPlainDir(string path)
    {
        var fi = new FileInfo(path);
        if (fi.LinkTarget is not null || (fi.Exists && !fi.Attributes.HasFlag(FileAttributes.Directory))) { fi.Delete(); return; }
        var di = new DirectoryInfo(path);
        if (di.Exists && di.LinkTarget is not null) di.Delete();
    }
}
