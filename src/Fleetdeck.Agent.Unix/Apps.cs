using System.Text;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Unix;

/// <summary>
/// Programikoner (PNG, højst 64×64 og SoftwareIcons.MaxBytes). De findes under inventaret og huskes i hukommelsen pr. kildefil;
/// efter inventaret sendes kun dem, serveren mangler.
/// </summary>
public static class AppIcons
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, byte[]> Png = new();                       // hash → PNG
    private static readonly Dictionary<string, (DateTime At, string? Hash)> Cache = new(); // kildefil → hash (null = kunne ikke bruges)

    private static DateTime MTime(string path) { try { return File.GetLastWriteTimeUtc(path); } catch (Exception) { return default; } }

    /// <summary>Gyldig PNG inden for grænserne (bredde/højde står i IHDR, byte 16–23)?</summary>
    public static bool Fits(byte[] b) =>
        b.Length is > 24 and <= SoftwareIcons.MaxBytes && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G'
        && BE(b, 16) is > 0 and <= 64 && BE(b, 20) is > 0 and <= 64;

    private static int BE(byte[] b, int o) => b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3];

    private static string? Remember(string source, DateTime at, byte[]? png)
    {
        string? hash = null;
        lock (Gate)
        {
            if (png is not null && Fits(png)) { hash = SoftwareIcons.HashOf(png); Png[hash] = png; }
            Cache[source] = (at, hash);
        }
        return hash;
    }

    /// <summary>
    /// Kildefiler → ikon-hash. En PNG, der allerede passer, læses direkte; resten konverteres af <paramref name="command"/>
    /// (kilde, mål-PNG → shell-kommando, null = kan ikke konverteres) – alle i ét shell-kald pr. 100 ikoner.
    /// Resultatet huskes pr. kildefil og ændringstid, så næste inventar ikke starter nogen processer.
    /// </summary>
    public static Dictionary<string, string?> Load(IEnumerable<string> sources, Func<string, string, string?> command)
    {
        var result = new Dictionary<string, string?>();
        var todo = new List<(string Src, DateTime At, string Cmd, string Out)>();
        string? tmp = null;
        try
        {
            foreach (var src in sources.Distinct())
            {
                var at = MTime(src);
                lock (Gate)
                    if (Cache.TryGetValue(src, out var c) && c.At == at) { result[src] = c.Hash; continue; }
                if (src.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        if (new FileInfo(src).Length <= 4 * SoftwareIcons.MaxBytes && File.ReadAllBytes(src) is var b && Fits(b)) { result[src] = Remember(src, at, b); continue; }
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { result[src] = Remember(src, at, null); continue; }
                }
                tmp ??= Directory.CreateTempSubdirectory("fleetdeck-icons-").FullName;
                var outFile = Path.Combine(tmp, todo.Count + ".png");
                if (command(src, outFile) is { } cmd) todo.Add((src, at, cmd, outFile));
                else result[src] = Remember(src, at, null);
            }
            foreach (var chunk in todo.Chunk(100))
            {
                var script = new StringBuilder();
                foreach (var t in chunk) script.Append(t.Cmd).Append(" >/dev/null 2>&1; ");
                script.Append("true");
                Shell.Run(script.ToString(), TimeSpan.FromSeconds(60), null, 2000);
                foreach (var t in chunk)
                {
                    byte[]? png = null;
                    try { if (File.Exists(t.Out)) png = File.ReadAllBytes(t.Out); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                    result[t.Src] = Remember(t.Src, t.At, png);
                }
            }
        }
        finally
        {
            if (tmp is not null) try { Directory.Delete(tmp, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return result;
    }

    /// <summary>Efter inventaret: spørg hvilke ikoner serveren mangler, og send dem i bidder. Fejl her må aldrig vælte inventaret.</summary>
    public static async Task Upload(Api api, IEnumerable<SoftwareEntry> sw, CancellationToken ct)
    {
        List<string> hashes;
        lock (Gate) hashes = sw.Select(s => s.IconHash).OfType<string>().Distinct().Where(Png.ContainsKey).ToList();
        if (hashes.Count == 0) return;
        var missing = (await api.Post<IconHashesResponse>("/api/agent/icons/missing", new IconHashesRequest { Hashes = hashes }, ct)).Missing ?? [];
        List<(string Hash, byte[] Png)> send;
        lock (Gate) send = missing.Distinct().Where(Png.ContainsKey).Take(SoftwareIcons.MaxPerRun).Select(h => (h, Png[h])).ToList();
        foreach (var chunk in send.Chunk(SoftwareIcons.PerRequest))
            await api.Post("/api/agent/icons", new IconUploadRequest { Icons = chunk.Select(x => new IconUpload { Hash = x.Hash, Png = Convert.ToBase64String(x.Png) }).ToList() }, ct);
        if (send.Count > 0) Log.Info($"Programikoner sendt: {send.Count}");
    }
}

/// <summary>
/// Linux: programmernes .desktop-filer giver beskrivelse (Comment), programfil (Exec), mappe og ikon til de pakker, de hører til.
/// Ejeren findes med ét samlet kald pr. pakkehåndtering (dpkg -S, rpm -qf, pacman -Qo, apk info --who-owns);
/// flatpak ud fra app-id'et (filnavnet) og snap ud fra "&lt;snap&gt;_&lt;app&gt;.desktop".
/// </summary>
public static class DesktopApps
{
    private sealed record Entry(string File, string Stem, string? Comment, string? Exec, string? TryExec, string? Icon, bool NoDisplay);

    private static readonly string[] SystemDirs =
        ["/usr/share/applications", "/usr/local/share/applications", "/var/lib/flatpak/exports/share/applications", "/var/lib/snapd/desktop/applications"];
    private const string FlatpakExports = "/exports/share/applications";
    private const string SnapDir = "/var/lib/snapd/desktop/applications";
    private static readonly string[] PathDirs = ["/usr/local/sbin", "/usr/local/bin", "/usr/sbin", "/usr/bin", "/sbin", "/bin", "/usr/games", "/snap/bin"];
    private static readonly string[] Interpreters = ["sh", "bash", "python", "python3", "perl", "ruby", "java", "mono", "gjs", "node", "wine"];

    public static void Enrich(List<SoftwareEntry> sw)
    {
        try { EnrichCore(sw); }
        catch (Exception e) { Log.Warn($"Programoplysninger fra .desktop-filer: {e.Message}"); }   // må aldrig vælte inventaret
    }

    private static void EnrichCore(List<SoftwareEntry> sw)
    {
        var dirs = SystemDirs.ToList();
        foreach (var home in SafeDirs("/home").Append("/root"))
        {
            dirs.Add(home + "/.local/share/applications");
            dirs.Add(home + "/.local/share/flatpak" + FlatpakExports);
        }
        var entries = new List<Entry>();
        foreach (var d in dirs.Where(Directory.Exists))
            foreach (var f in SafeFiles(d).Order(StringComparer.Ordinal))
                if (Parse(f) is { NoDisplay: false } e) entries.Add(e);   // skjulte hjælpere (NoDisplay) er ikke programmer
        if (entries.Count == 0) return;

        var native = sw.Where(s => s.Scope is not ("snap" or "flatpak")).ToLookup(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var flatpaks = sw.Where(s => s.Scope == "flatpak").ToLookup(s => s.Name, StringComparer.Ordinal);
        var snaps = sw.Where(s => s.Scope == "snap").ToLookup(s => s.Name, StringComparer.Ordinal);

        // .desktop → (pakkenavn, slags)
        var owners = Owners(entries.Where(e => SystemDirs.Any(d => e.File.StartsWith(d + "/")) && !IsFlatpak(e.File) && !e.File.StartsWith(SnapDir + "/")).Select(e => e.File).ToList());
        var best = new Dictionary<(string Kind, string Pkg), (Entry E, int Score)>();
        foreach (var e in entries)
        {
            string kind, pkg;
            if (IsFlatpak(e.File))
            {
                kind = "flatpak";
                pkg = flatpaks.Contains(e.Stem) ? e.Stem
                    : flatpaks.Select(g => g.Key).Where(id => e.Stem.StartsWith(id + ".")).MaxBy(id => id.Length) ?? "";
            }
            else if (e.File.StartsWith(SnapDir + "/")) { kind = "snap"; pkg = e.Stem.Split('_')[0]; if (!snaps.Contains(pkg)) pkg = ""; }
            else
            {
                kind = "native";
                pkg = owners.GetValueOrDefault(e.File) ?? "";
                // reserve: filnavnet eller programfilens navn er pakkens navn (fx Void, Solus og brugerens egne genveje)
                if (pkg.Length == 0 || !native.Contains(pkg))
                    pkg = native.Contains(e.Stem) ? e.Stem : ExecToken(e) is { } x && native.Contains(Path.GetFileName(x)) ? Path.GetFileName(x) : "";
            }
            if (pkg.Length == 0) continue;
            // navnet som pakken før andre; et ikon er et plus
            var score = (string.Equals(e.Stem, pkg, StringComparison.OrdinalIgnoreCase) || e.Stem.StartsWith(pkg + "_") ? 2 : 0) + (e.Icon is { Length: > 0 } ? 1 : 0);
            if (!best.TryGetValue((kind, pkg), out var cur) || score > cur.Score) best[(kind, pkg)] = (e, score);
        }

        // programfil, mappe og ikonets kildefil
        var icons = new List<(SoftwareEntry S, string Src)>();
        foreach (var ((kind, pkg), (e, _)) in best)
        {
            var targets = (kind switch { "flatpak" => flatpaks[pkg], "snap" => snaps[pkg], _ => native[pkg] }).ToList();
            string? exe, dir, flatRoot = null;
            if (kind == "flatpak")
            {
                flatRoot = e.File[..e.File.IndexOf(FlatpakExports, StringComparison.Ordinal)];
                dir = $"{flatRoot}/app/{pkg}";
                var cmd = Tokens(e.Exec ?? "").FirstOrDefault(t => t.StartsWith("--command="))?["--command=".Length..];
                exe = cmd is { Length: > 0 } && File.Exists($"{dir}/current/active/files/bin/{cmd}") ? $"{dir}/current/active/files/bin/{cmd}" : dir;
            }
            else if (kind == "snap")
            {
                dir = $"/snap/{pkg}/current";
                exe = ExecToken(e) is { } x && x.StartsWith('/') && File.Exists(x) ? x : dir;   // /snap/bin/<app> (ikke /usr/bin/snap, som den peger på)
            }
            else
            {
                exe = ExecToken(e) is { } x && Path.GetFileName(x) is not ("false" or "true") ? Resolve(x) : null;
                dir = exe is not null ? Path.GetDirectoryName(exe) : null;
            }
            var src = IconSource(e, kind, pkg, flatRoot);
            foreach (var s in targets)
            {
                if (string.IsNullOrWhiteSpace(s.Description)) s.Description = SoftwareIcons.Clip(e.Comment);
                s.Exe ??= exe;
                s.InstallPath ??= dir;
                if (src is not null) icons.Add((s, src));
            }
        }
        if (icons.Count == 0) return;

        // ikoner: PNG ≤ 64×64 bruges som de er; større PNG skaleres med ImageMagick, SVG med rsvg-convert (hvis de findes)
        string? magick = null, rsvg = null;
        var toolsChecked = false;
        string? Command(string src, string outFile)
        {
            if (!toolsChecked)
            {
                toolsChecked = true;
                var found = Shell.Out("for t in rsvg-convert magick convert; do command -v $t; done; true").Split('\n').Select(l => l.Trim()).ToList();
                rsvg = found.FirstOrDefault(l => l.EndsWith("/rsvg-convert"));
                magick = found.FirstOrDefault(l => l.EndsWith("/magick")) ?? found.FirstOrDefault(l => l.EndsWith("/convert"));
            }
            if (src.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                return rsvg is null ? null : $"{rsvg} -w 48 -h 48 -a -f png -o {Pkg.Q(outFile)} {Pkg.Q(src)}";
            return magick is null ? null : $"{magick} {Pkg.Q(src)} -resize '48x48>' -strip png:{Pkg.Q(outFile)}";
        }
        var hashes = AppIcons.Load(icons.Select(i => i.Src), Command);
        foreach (var (s, src) in icons)
            if (hashes.GetValueOrDefault(src) is { } h) s.IconHash = h;
    }

    private static bool IsFlatpak(string file) => file.Contains("/flatpak" + FlatpakExports + "/", StringComparison.Ordinal);

    private static IEnumerable<string> SafeDirs(string dir)
    {
        try { return Directory.GetDirectories(dir); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    private static IEnumerable<string> SafeFiles(string dir)
    {
        try { return Directory.GetFiles(dir, "*.desktop", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true }); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>[Desktop Entry]-afsnittet: kun programmer (Type=Application), ikke slettede (Hidden=true).</summary>
    private static Entry? Parse(string file)
    {
        string? type = null, comment = null, commentDa = null, exec = null, tryExec = null, icon = null;
        bool noDisplay = false, hidden = false, inMain = false;
        try
        {
            foreach (var raw in File.ReadLines(file))
            {
                var l = raw.Trim();
                if (l.StartsWith('[')) { if (inMain) break; inMain = l == "[Desktop Entry]"; continue; }
                var i = l.IndexOf('=');
                if (!inMain || i <= 0) continue;
                var v = l[(i + 1)..].Trim();
                switch (l[..i].Trim())
                {
                    case "Type": type = v; break;
                    case "Comment": comment = v; break;
                    case "Comment[da]": commentDa = v; break;
                    case "Comment[da_DK]": commentDa ??= v; break;
                    case "Exec": exec = v; break;
                    case "TryExec": tryExec = v; break;
                    case "Icon": icon = v; break;
                    case "NoDisplay": noDisplay = v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                    case "Hidden": hidden = v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        if (hidden || type != "Application") return null;
        return new Entry(file, Path.GetFileNameWithoutExtension(file), commentDa is { Length: > 0 } ? commentDa : comment, exec, tryExec, icon, noDisplay);
    }

    /// <summary>Ejer-pakken for hver .desktop-fil – ét kald for dem alle.</summary>
    private static Dictionary<string, string> Owners(List<string> files)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (files.Count == 0) return map;
        foreach (var chunk in files.Chunk(200))
        {
            var args = string.Join(' ', chunk.Select(Pkg.Q));
            switch (Pkg.Native)
            {
                case Pkg.Manager.Apt:
                    // "pakke[:arch][, pakke2]: /usr/share/applications/x.desktop"
                    foreach (var l in Shell.Out($"dpkg -S {args} 2>/dev/null; true", 30).Split('\n'))
                    {
                        var i = l.IndexOf(": /", StringComparison.Ordinal);
                        if (i > 0 && !l.StartsWith("diversion")) map[l[(i + 2)..].Trim()] = l[..i].Split(',')[0].Split(':')[0].Trim();
                    }
                    break;
                case Pkg.Manager.Dnf or Pkg.Manager.Yum or Pkg.Manager.Zypper or Pkg.Manager.Transactional or Pkg.Manager.RpmOstree:
                {
                    // én linje pr. fil, i samme rækkefølge ("file … is not owned by any package" for de løse); passer antallet ikke, bruges reserven
                    var lines = Shell.Out($"rpm -qf --qf '%{{NAME}}\\n' {args} 2>/dev/null; true", 30).Split('\n');
                    if (lines.Length == chunk.Length)
                        for (var k = 0; k < lines.Length; k++)
                            if (!lines[k].Contains(' ') && lines[k].Trim().Length > 0) map[chunk[k]] = lines[k].Trim();
                    break;
                }
                case Pkg.Manager.Pacman or Pkg.Manager.SteamOS:
                    // "/usr/share/applications/x.desktop is owned by pakke 1.2-1"
                    foreach (var l in Shell.Out($"pacman -Qo {args} 2>/dev/null; true", 30).Split('\n'))
                    {
                        var i = l.IndexOf(" is owned by ", StringComparison.Ordinal);
                        if (i > 0) map[l[..i].Trim()] = l[(i + 13)..].Trim().Split(' ')[0];
                    }
                    break;
                case Pkg.Manager.Apk:
                    // "/usr/share/applications/x.desktop is owned by pakke-1.2-r0"
                    foreach (var l in Shell.Out($"apk info --who-owns {args} 2>/dev/null; true", 30).Split('\n'))
                    {
                        var i = l.IndexOf(" is owned by ", StringComparison.Ordinal);
                        if (i > 0) map[l[..i].Trim()] = Pkg.SplitPkgVer(l[(i + 13)..].Trim()).Name;
                    }
                    break;
            }
        }
        return map;
    }

    /// <summary>Exec-linjen delt i ord (med "…"-citater), uden feltkoder som %U.</summary>
    private static List<string> Tokens(string exec)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false, any = false;
        for (var i = 0; i < exec.Length; i++)
        {
            var c = exec[i];
            if (c == '"') { quoted = !quoted; any = true; }
            else if (c == '\\' && quoted && i + 1 < exec.Length) sb.Append(exec[++i]);
            else if (c == ' ' && !quoted) { if (sb.Length > 0 || any) list.Add(sb.ToString()); sb.Clear(); any = false; }
            else sb.Append(c);
        }
        if (sb.Length > 0) list.Add(sb.ToString());
        return list.Where(t => !(t.Length == 2 && t[0] == '%')).ToList();
    }

    /// <summary>Selve programmet i Exec (uden env, VAR=værdi og fortolkere som python3 /sti/til/script.py).</summary>
    private static string? ExecToken(Entry e)
    {
        var t = Tokens(e.Exec ?? e.TryExec ?? "");
        var k = 0;
        if (k < t.Count && Path.GetFileName(t[k]) == "env")
        {
            k++;
            while (k < t.Count && (t[k].StartsWith('-') || t[k].Contains('=') && !t[k].StartsWith('/'))) k++;
        }
        if (k >= t.Count) return e.TryExec is { Length: > 0 } te ? te : null;
        var prog = t[k];
        if (!Interpreters.Contains(Path.GetFileName(prog))) return prog;
        // fortolker: selve scriptet er programmet (sh -c '…' og python3 -m … giver intet)
        for (var j = k + 1; j < t.Count; j++)
        {
            if (t[j].StartsWith('/')) return t[j];
            if (!t[j].StartsWith('-')) break;
        }
        return e.TryExec is { Length: > 0 } te2 ? te2 : null;
    }

    /// <summary>Absolut sti til programfilen; symlinks følges (fx /usr/bin/firefox → /usr/lib/firefox/firefox), men ikke via /etc/alternatives.</summary>
    private static string? Resolve(string prog)
    {
        var path = prog.StartsWith('/') ? prog : PathDirs.Select(d => Path.Combine(d, prog)).FirstOrDefault(File.Exists);
        if (path is null || !File.Exists(path)) return null;
        try
        {
            var link = new FileInfo(path).LinkTarget;
            if (link is null || link.Contains("/etc/alternatives/")) return path;
            return File.ResolveLinkTarget(path, true)?.FullName is { } real && File.Exists(real) ? real : path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return path; }
    }

    private static readonly string[] Sizes = ["48x48", "64x64", "32x32", "128x128", "256x256", "512x512"];

    /// <summary>Ikonets fil: absolut sti, ellers hicolor-temaet (flatpak's egne først), /usr/share/pixmaps og snap'ens meta/gui.</summary>
    private static string? IconSource(Entry e, string kind, string pkg, string? flatRoot)
    {
        var icon = e.Icon?.Trim() ?? "";
        if (kind == "snap") icon = icon.Replace("${SNAP}", $"/snap/{pkg}/current");
        static bool Usable(string p) => (p.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) && File.Exists(p);
        if (icon.StartsWith('/') && Usable(icon)) return icon;
        if ((icon.Length == 0 || icon.StartsWith('/')) && kind != "snap") return null;
        var name = icon.StartsWith('/') ? Path.GetFileNameWithoutExtension(icon)
            : icon.EndsWith(".png") || icon.EndsWith(".svg") || icon.EndsWith(".xpm") ? icon[..^4] : icon;
        var themes = new List<string>();
        if (flatRoot is not null) themes.Add(flatRoot + "/exports/share/icons/hicolor");
        themes.Add("/usr/share/icons/hicolor");
        themes.Add("/usr/local/share/icons/hicolor");
        if (name.Length > 0)
        {
            foreach (var size in Sizes)
                foreach (var t in themes)
                    if ($"{t}/{size}/apps/{name}.png" is var p && File.Exists(p)) return p;
            if ($"/usr/share/pixmaps/{name}.png" is var pm && File.Exists(pm)) return pm;
        }
        if (kind == "snap")
            foreach (var p in new[] { $"/snap/{pkg}/current/meta/gui/{name}.png", $"/snap/{pkg}/current/meta/gui/icon.png", $"/snap/{pkg}/current/meta/gui/icon.svg" })
                if (Usable(p)) return p;
        if (name.Length == 0) return null;
        foreach (var t in themes)
            if ($"{t}/scalable/apps/{name}.svg" is var p && File.Exists(p)) return p;
        return $"/usr/share/pixmaps/{name}.svg" is var ps && File.Exists(ps) ? ps : null;
    }
}
