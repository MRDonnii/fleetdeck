using Fleetdeck.Server.Data;
using Fleetdeck.Shared;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Server.Services;

/// <summary>Netværksdrev: hvilke en maskine skal have (profilens + maskinens egne) og hvilke delte mapper der kan vælges.</summary>
public static class Drives
{
    public static string Key(NetworkDrive d) => $"d{d.Id}";

    /// <summary>Maskinens drev: profilens og maskinens egne; maskinens vinder for samme mappe eller bogstav.</summary>
    public static async Task<List<NetworkDrive>> For(AppDb db, Machine m)
    {
        var own = await db.NetworkDrives.AsNoTracking().Where(d => d.MachineId == m.Id && !d.Ignored).ToListAsync();
        var prof = m.ProfileId is int pid ? await db.NetworkDrives.AsNoTracking().Where(d => d.ProfileId == pid).ToListAsync() : [];
        var taken = own.Select(d => $"{d.Host}/{d.Share}".ToLowerInvariant()).ToHashSet();
        var letters = own.Where(d => !string.IsNullOrEmpty(d.Letter)).Select(d => d.Letter!.ToUpperInvariant()).ToHashSet();
        return [.. own, .. prof.Where(d => !taken.Contains($"{d.Host}/{d.Share}".ToLowerInvariant()) && (string.IsNullOrEmpty(d.Letter) || !letters.Contains(d.Letter.ToUpperInvariant())))
            .OrderBy(d => d.Letter ?? "~").ThenBy(d => d.Name)];
    }

    /// <summary>Gem drev, brugeren selv har koblet på maskinen, så de holdes forbundet. Returnerer antal nye.</summary>
    public static async Task<int> Adopt(AppDb db, Machine m, IEnumerable<FoundDrive> found)
    {
        var known = (await db.NetworkDrives.AsNoTracking().Where(d => d.MachineId == m.Id).ToListAsync())   // også dem, man har sagt nej til
            .Concat(await For(db, m)).Select(d => $"{d.Host}/{d.Share}".ToLowerInvariant()).ToHashSet();
        var n = 0;
        foreach (var f in found.Where(f => f.Host.Length > 0 && f.Share.Length > 0).DistinctBy(f => $"{f.Host}/{f.Share}".ToLowerInvariant()))
        {
            if (!known.Add($"{f.Host}/{f.Share}".ToLowerInvariant())) continue;
            var letter = f.Letter is { Length: 1 } l && char.IsAsciiLetter(l[0]) ? l.ToUpperInvariant() : null;
            db.NetworkDrives.Add(new NetworkDrive
            {
                MachineId = m.Id, Host = f.Host.Trim(), Share = f.Share.Trim().Trim('/'), Letter = letter,
                Name = SafeName(string.IsNullOrWhiteSpace(f.Name) ? f.Share : f.Name), Adopted = true, Created = DateTime.UtcNow,
            });
            n++;
        }
        if (n > 0) await db.SaveChangesAsync();
        return n;
    }

    /// <summary>Behold tidspunkter fra forrige helbred: sidst forbundet og siden hvornår (forbundet/afbrudt).</summary>
    public static void Stamp(List<DriveState> now, List<DriveState> before)
    {
        var t = DateTime.UtcNow;
        foreach (var d in now)
        {
            var p = before.FirstOrDefault(x => x.Key == d.Key);
            d.Since = p is not null && p.Ok == d.Ok ? p.Since ?? t : t;
            d.LastOk = d.Ok ? t : p?.LastOk;
        }
    }

    public static DriveSpec Spec(NetworkDrive d) => new() { Key = Key(d), Host = d.Host, Share = d.Share, Letter = d.Letter, Name = d.Name, OwnLogin = d.Adopted || d.OwnLogin };

    /// <summary>Delte mapper fra tilmeldte Unraid-servere (fra deres helbred) med serverens adresse.</summary>
    public static async Task<List<object>> Shares(AppDb db)
    {
        var list = new List<object>();
        foreach (var m in await db.Machines.AsNoTracking().Where(x => x.HealthJson != null && x.HealthJson.Contains("\"shares\"")).ToListAsync())
        {
            var u = Json.Des<HealthReport>(m.HealthJson).Unraid;
            if (u is null) continue;
            var host = m.Ip ?? m.Name;
            foreach (var s in u.Shares.Where(s => s.Smb)) list.Add(new { server = m.Name, host, share = s.Name, comment = s.Comment });
        }
        return list;
    }

    /// <summary>Mappenavn til Linux/Mac og navn i Stifinder: kun bogstaver, tal, - og _.</summary>
    public static string SafeName(string s)
    {
        var n = new string(s.Trim().Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray()).Trim('_');
        return n.Length == 0 ? "Drev" : n.Length > 40 ? n[..40] : n;
    }
}
