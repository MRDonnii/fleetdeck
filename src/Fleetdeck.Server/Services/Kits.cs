using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fleetdeck.Server.Services;

/// <summary>Gendannelsespunkter (Linux): pakker med en maskines opsætning, gemt under data/kits/&lt;maskine&gt;/.</summary>
public static partial class Kits
{
    public static string Dir(Paths paths, int machineId) => Path.Combine(paths.Data, "kits", machineId.ToString());

    public static string? SafeName(string? name) => name is { Length: > 0 } n && FileName().IsMatch(n) ? n : null;

    /// <summary>Behold de nyeste <paramref name="keep"/>, men altid mindst én pr. uge den seneste måned (så en skjult fejl kan rulles tilbage).</summary>
    public static void Prune(string dir, int keep)
    {
        // iPad-backups ("ipad-…") har deres egen mappe og oprydning – de må aldrig tælle med her
        var files = new DirectoryInfo(dir).GetFiles("*.tar.gz").Where(f => !IpadBackups.IsIpad(f.Name)).OrderByDescending(f => f.Name).ToList();
        var weekly = files.Where(f => f.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-35))
            .GroupBy(f => System.Globalization.ISOWeek.GetWeekOfYear(f.LastWriteTime)).Select(g => g.First()).ToHashSet();
        foreach (var f in files.Skip(keep).Where(f => !weekly.Contains(f))) { try { f.Delete(); File.Delete(f.FullName + ".check.json"); } catch (IOException) { } }
    }

    public sealed record Check(bool Ok, int Files, bool Manifest, int Units, string? Error, DateTime Checked);

    /// <summary>Åbner hele pakken og tjekker, at den kan læses til ende og har sin manifest (resultatet gemmes ved siden af pakken).</summary>
    public static Check Verify(string file)
    {
        Check r;
        try
        {
            using var fs = File.OpenRead(file);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var tr = new TarReader(gz);
            int n = 0, units = 0; var manifest = false;
            while (tr.GetNextEntry(copyData: false) is { } e)
            {
                n++;
                if (e.Name.EndsWith("manifest/units.txt", StringComparison.Ordinal) && e.DataStream is { } ds)
                { manifest = true; using var sr = new StreamReader(ds); units = sr.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length; }
                else e.DataStream?.CopyTo(Stream.Null);
            }
            r = new(n > 0 && manifest, n, manifest, units, manifest ? null : "Pakken mangler sin manifest", DateTime.UtcNow);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or FormatException or ArgumentException)
        { r = new(false, 0, false, 0, $"Pakken kan ikke læses: {e.Message}", DateTime.UtcNow); }
        try { File.WriteAllText(file + ".check.json", JsonSerializer.Serialize(r)); } catch (IOException) { }
        return r;
    }

    private static Check? Checked(FileInfo f)
    {
        var side = f.FullName + ".check.json";
        try { if (File.Exists(side)) return JsonSerializer.Deserialize<Check>(File.ReadAllText(side)); } catch (Exception e) when (e is IOException or JsonException) { }
        return Verify(f.FullName);
    }

    public static IEnumerable<object> List(Paths paths, int machineId)
    {
        var d = Dir(paths, machineId);
        if (!Directory.Exists(d)) return [];
        return new DirectoryInfo(d).GetFiles("*.tar.gz").Where(f => !IpadBackups.IsIpad(f.Name)).OrderByDescending(f => f.Name).ToList()
            .Select(f => { var c = Checked(f); return new { name = f.Name, size = f.Length, created = f.LastWriteTimeUtc, machineId, ok = c?.Ok, files = c?.Files, units = c?.Units, error = c?.Error }; });
    }

    [GeneratedRegex(@"^[\w\-.]{1,80}\.tar\.gz$")] private static partial Regex FileName();
}
