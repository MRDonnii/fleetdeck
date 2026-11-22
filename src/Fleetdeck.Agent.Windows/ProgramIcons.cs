using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Windows;

/// <summary>
/// Programmers mappe, programfil og ikon ud fra afinstallations-posten (InstallLocation, DisplayIcon, UninstallString).
/// Ikoner hentes med Win32 (PrivateExtractIcons), tegnes som 32×32 PNG med gennemsigtig baggrund og gemmes i hukommelsen
/// for hele processens levetid, så de næste inventarer er billige. Virker i tjenesten (SYSTEM, session 0) – intet skrivebord kræves.
/// </summary>
internal static partial class ProgramIcons
{
    private const int Size = 32;

    /// <summary>Ikon pr. kilde (sti|indeks|ændringstid) → hash, eller null hvis kilden ikke gav et brugbart ikon.</summary>
    private static readonly ConcurrentDictionary<string, string?> BySource = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>PNG pr. hash, så de kan sendes, hvis serveren mangler dem.</summary>
    private static readonly ConcurrentDictionary<string, byte[]> Pngs = new(StringComparer.OrdinalIgnoreCase);

    public static byte[]? Png(string hash) => Pngs.TryGetValue(hash, out var b) ? b : null;

    /// <summary>Udfyld InstallPath, Exe, Description og IconHash fra en afinstallations-nøgle. Fejl giver blot tomme felter.</summary>
    public static void Fill(SoftwareEntry e, string? installLocation, string? displayIcon, string? uninstall, string? comments)
    {
        try
        {
            var desc = SoftwareIcons.Clip(comments);
            if (desc is not null && !desc.Equals(e.Name, StringComparison.OrdinalIgnoreCase)) e.Description = desc;

            var (iconPath, iconIndex) = ParseIconSpec(displayIcon);
            var uninstExe = ParseCommandExe(uninstall);

            e.InstallPath = CleanDir(installLocation)
                            ?? FolderFrom(iconPath)
                            ?? FolderFrom(uninstExe);

            if (iconPath is not null && IsExe(iconPath) && !IsUninstaller(iconPath) && !IsGenericCache(iconPath) && File.Exists(iconPath)
                && (e.InstallPath is null || iconPath.StartsWith(e.InstallPath.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
                e.Exe = iconPath;
            e.Exe ??= GuessExe(e.InstallPath, e.Name);

            // Et afinstallationsprogram som ikonkilde giver ofte installerens generiske ikon – så foretrækkes selve programmet
            var iconOk = iconPath is not null && File.Exists(iconPath);
            if (iconOk && IsUninstaller(iconPath!) && e.Exe is not null) e.IconHash = HashFor(e.Exe, 0);
            if (e.IconHash is null && iconOk) e.IconHash = HashFor(iconPath!, iconIndex);
            if (e.IconHash is null && e.Exe is not null) e.IconHash = HashFor(e.Exe, 0);
        }
        catch (Exception) { }
    }

    /// <summary>Kun mappen (Store-apps o.l.).</summary>
    public static string? CleanDir(string? dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir)) return null;
            var d = Environment.ExpandEnvironmentVariables(dir.Trim().Trim('"').Trim()).TrimEnd('\\');
            if (d.Length == 2 && d[1] == ':') return null;               // et helt drev er ikke en programmappe
            return Path.IsPathFullyQualified(d) && Directory.Exists(d) ? d : null;
        }
        catch (Exception) { return null; }
    }

    // ---------------------------------------------------------------- stier

    /// <summary>"C:\x\a.exe,0", "\"C:\x\a.exe\",-101" eller blot en sti.</summary>
    private static (string? Path, int Index) ParseIconSpec(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return (null, 0);
        var m = IconSpec().Match(spec.Trim());
        if (!m.Success) return (null, 0);
        var p = Environment.ExpandEnvironmentVariables(m.Groups[1].Value.Trim().Trim('"').Trim());
        var idx = m.Groups[2].Success && int.TryParse(m.Groups[2].Value, out var i) ? i : 0;
        return Path.IsPathFullyQualified(p) ? (p, idx) : (null, 0);
    }

    /// <summary>Programfilen i en kommandolinje som "\"C:\x\uninst.exe\" /S" eller "C:\x\uninst.exe /S".</summary>
    private static string? ParseCommandExe(string? cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return null;
        var s = Environment.ExpandEnvironmentVariables(cmd.Trim());
        string p;
        if (s.StartsWith('"')) { var end = s.IndexOf('"', 1); if (end < 0) return null; p = s[1..end]; }
        else { var i = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase); if (i < 0) return null; p = s[..(i + 4)]; }
        return Path.IsPathFullyQualified(p) ? p : null;
    }

    /// <summary>Mappen for en fil, hvis den ligger under Program Files/ProgramData/AppData og ikke er en generel installations-cache.</summary>
    private static string? FolderFrom(string? file)
    {
        if (file is null || IsGenericCache(file)) return null;
        var dir = Path.GetDirectoryName(file);
        if (dir is null) return null;
        var name = Path.GetFileName(dir);
        if (name.Equals("uninstall", StringComparison.OrdinalIgnoreCase) || name.Equals("uninst", StringComparison.OrdinalIgnoreCase)
            || name.Equals("uninstaller", StringComparison.OrdinalIgnoreCase))
            dir = Path.GetDirectoryName(dir);                         // …\Program\Uninstall\uninst.exe → …\Program
        if (dir is null || !UnderProgramRoot(dir)) return null;
        return CleanDir(dir);
    }

    private static readonly string[] ProgramRoots = new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    }.Where(r => r.Length > 0).Select(r => r.TrimEnd('\\') + "\\").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static bool UnderProgramRoot(string dir)
    {
        var d = dir.TrimEnd('\\') + "\\";
        if (d.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase)) return d.Count(c => c == '\\') >= 6;   // C:\Users\x\AppData\Local\Prog\
        // Ikke selve rodmappen (C:\Program Files\) – kun en mappe under den
        return ProgramRoots.Any(r => d.StartsWith(r, StringComparison.OrdinalIgnoreCase) && d.Length > r.Length);
    }

    private static bool IsGenericCache(string path) =>
        path.Contains(@"\Windows\Installer\", StringComparison.OrdinalIgnoreCase)
        || path.Contains(@"\Package Cache\", StringComparison.OrdinalIgnoreCase)
        || path.Contains("InstallShield Installation Information", StringComparison.OrdinalIgnoreCase)
        || path.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase)
        || path.Contains(@"\Windows\System32\", StringComparison.OrdinalIgnoreCase)
        || path.Contains(@"\Windows\SysWOW64\", StringComparison.OrdinalIgnoreCase)
        || Path.GetFileName(path).StartsWith("msiexec", StringComparison.OrdinalIgnoreCase)
        || GuidFolder().IsMatch(path);                                  // C:\ProgramData\{GUID}\setup.exe o.l.

    private static bool IsExe(string p) => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsUninstaller(string p)
    {
        var f = Path.GetFileNameWithoutExtension(p);
        return f.StartsWith("unins", StringComparison.OrdinalIgnoreCase) || f.Contains("uninstall", StringComparison.OrdinalIgnoreCase)
            || f.Equals("setup", StringComparison.OrdinalIgnoreCase) || f.Contains("installer", StringComparison.OrdinalIgnoreCase)
            || f.Contains("update", StringComparison.OrdinalIgnoreCase) || f.Contains("crashreport", StringComparison.OrdinalIgnoreCase)
            || f.Contains("helper", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Uden DisplayIcon: en .exe i programmappen, hvis navnet passer til programmet, eller hvis der kun er én.</summary>
    private static string? GuessExe(string? dir, string name)
    {
        if (dir is null) return null;
        try
        {
            var exes = Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly).Take(40).Where(f => !IsUninstaller(f)).ToList();
            if (exes.Count == 1) return exes[0];
            var words = NonWord().Split(name.ToLowerInvariant()).Where(w => w.Length >= 3).ToHashSet();
            return exes.FirstOrDefault(f => words.Contains(Path.GetFileNameWithoutExtension(f).ToLowerInvariant()))
                ?? exes.FirstOrDefault(f => NonWord().Replace(name, "").StartsWith(Path.GetFileNameWithoutExtension(f), StringComparison.OrdinalIgnoreCase)
                                            && Path.GetFileNameWithoutExtension(f).Length >= 3);
        }
        catch (Exception) { return null; }
    }

    // ---------------------------------------------------------------- ikoner

    /// <summary>Hash for ikonet i en fil (gemt i hukommelsen pr. sti, indeks og ændringstid).</summary>
    private static string? HashFor(string file, int index)
    {
        DateTime stamp;
        try { stamp = File.GetLastWriteTimeUtc(file); } catch (Exception) { return null; }
        return BySource.GetOrAdd($"{file}|{index}|{stamp.Ticks}", _ =>
        {
            var png = Render(file, index);
            if (png is null || png.Length > SoftwareIcons.MaxBytes) return null;
            var hash = SoftwareIcons.HashOf(png);
            Pngs.TryAdd(hash, png);
            return hash;
        });
    }

    private static byte[]? Render(string file, int index)
    {
        try
        {
            if (file.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                using var img = Image.FromFile(file);
                return ToPng(img);
            }
            var h = Extract(file, index);
            if (h == IntPtr.Zero && index != 0) h = Extract(file, 0);
            if (h == IntPtr.Zero) return null;
            try
            {
                using var icon = Icon.FromHandle(h);
                using var bmp = icon.ToBitmap();
                return ToPng(bmp);
            }
            finally { DestroyIcon(h); }
        }
        catch (Exception) { return null; }
    }

    /// <summary>Ét ikon i 32×32 (Windows vælger den bedste størrelse i filen). Negativt indeks = ressource-id.</summary>
    private static IntPtr Extract(string file, int index)
    {
        var icons = new IntPtr[1];
        var ids = new uint[1];
        var n = PrivateExtractIcons(file, index, Size, Size, icons, ids, 1, 0);
        if (n != 0 && n != uint.MaxValue && icons[0] != IntPtr.Zero) return icons[0];
        if (index < 0)
        {
            var large = new IntPtr[1];
            if (ExtractIconEx(file, index, large, null, 1) > 0 && large[0] != IntPtr.Zero) return large[0];
        }
        return IntPtr.Zero;
    }

    /// <summary>Tegn i høj kvalitet på 32×32 med gennemsigtig baggrund; helt gennemsigtige billeder springes over.</summary>
    private static byte[]? ToPng(Image src)
    {
        using var bmp = new Bitmap(Size, Size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.CompositingMode = CompositingMode.SourceOver;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.DrawImage(src, new Rectangle(0, 0, Size, Size));
        }
        if (IsEmpty(bmp)) return null;
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    private static bool IsEmpty(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[data.Stride * data.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            for (var i = 3; i < bytes.Length; i += 4) if (bytes[i] != 0) return false;
            return true;
        }
        finally { bmp.UnlockBits(data); }
    }

    // ---------------------------------------------------------------- upload

    /// <summary>Send de ikoner, serveren mangler (efter inventaret). Fejl logges og stopper ikke noget.</summary>
    public static async Task Upload(ServerClient api, IEnumerable<SoftwareEntry> software, ILogger log, CancellationToken ct)
    {
        var hashes = software.Select(s => s.IconHash).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (hashes.Count == 0) return;
        try
        {
            var missing = await api.Post<IconHashesResponse>("/api/agent/icons/missing", new IconHashesRequest { Hashes = hashes }, ct);
            var send = (missing?.Missing ?? [])
                .Select(h => (Hash: h, Png: Png(h)))
                .Where(x => x.Png is { Length: > 0 } p && p.Length <= SoftwareIcons.MaxBytes)
                .Take(SoftwareIcons.MaxPerRun)
                .Select(x => new IconUpload { Hash = x.Hash, Png = Convert.ToBase64String(x.Png!) })
                .ToList();
            foreach (var batch in send.Chunk(SoftwareIcons.PerRequest))
                await api.Post("/api/agent/icons", new IconUploadRequest { Icons = [.. batch] }, ct);
            log.LogDebug("Ikoner: {N} kendt, {S} sendt", hashes.Count, send.Count);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("Ikoner kunne ikke sendes: {Msg}", e.Message);
        }
    }

    [GeneratedRegex(@"^(""[^""]*""|[^,]*?)\s*(?:,\s*(-?\d+))?\s*$")] private static partial Regex IconSpec();
    [GeneratedRegex(@"[^a-z0-9]+", RegexOptions.IgnoreCase)] private static partial Regex NonWord();
    [GeneratedRegex(@"\\\{[0-9a-f-]{36}\}(\\|$)", RegexOptions.IgnoreCase)] private static partial Regex GuidFolder();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, uint[] ids, uint count, uint flags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool DestroyIcon(IntPtr h);
}
