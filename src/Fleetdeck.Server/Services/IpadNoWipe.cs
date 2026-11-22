using Fleetdeck.Server.Data;
using Fleetdeck.Shared;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Server.Services;

/// <summary>
/// "Overvåg uden datatab": backupperne fra begge lagre (Mac-sæt i data/ipad-backups/&lt;id&gt;/ og USB-sæt i data/ipad-backups/ipad-&lt;id&gt;/) som
/// kandidater, og hvilke sæt et kørende job kan bruge (de må ikke ryddes op undervejs).
/// </summary>
public static class NoWipeBackups
{
    public static List<NoWipeCandidate> Candidates(Paths paths, int ipadId) => Candidates(IpadBackups.Dir(paths, ipadId), UsbIpadBackups.Dir(paths, ipadId));

    public static List<NoWipeCandidate> Candidates(string macDir, string usbDir)
    {
        var list = new List<NoWipeCandidate>();
        foreach (var x in IpadBackups.ListDir(macDir).Where(x => x.Complete))
            list.Add(new NoWipeCandidate
            {
                Store = NoWipeCandidate.Mac, Set = x.Set, Created = x.Created, Bytes = x.Bytes, Parts = x.Parts, Encrypted = x.Encrypted,
                Verified = x.PasswordVerified, SourceEmpty = x.DeviceEmpty, SourceUdid = x.Udid, DeviceName = x.DeviceName, Pinned = x.Pinned,
            });
        foreach (var x in UsbIpadBackups.ListDir(usbDir))
            list.Add(new NoWipeCandidate
            {
                Store = NoWipeCandidate.Usb, Set = x.Set, Created = x.Created, Bytes = x.Bytes, Parts = x.Parts, Encrypted = x.Encrypted,
                Verified = x.Verified, SourceEmpty = x.DeviceEmpty, SourceUdid = x.Udid, DeviceName = x.DeviceName, Pinned = x.Pinned,
            });
        return [.. list.OrderByDescending(x => x.Created)];
    }

    /// <summary>Filen med en del af et sæt (null = ukendt sæt eller del).</summary>
    public static string? PartFile(Paths paths, int ipadId, string store, string set, int part)
    {
        if (part < 1) return null;
        if (store == NoWipeCandidate.Mac)
            return IpadBackups.ParseSet(set) is { } s && s.IpadId == ipadId ? IpadBackups.PartFile(paths, new IpadBackups.Part(ipadId, set, part)) : null;
        if (store == NoWipeCandidate.Usb)
            return UsbIpadBackups.SafeSet(set) is { } n ? UsbIpadBackups.Part(UsbIpadBackups.Dir(paths, ipadId), n, part) : null;
        return null;
    }

    public static void Pin(Paths paths, int ipadId, string store, string set)
    {
        if (store == NoWipeCandidate.Mac) IpadBackups.Pin(paths, ipadId, set);
        else if (store == NoWipeCandidate.Usb) UsbIpadBackups.Pin(UsbIpadBackups.Dir(paths, ipadId), set);
    }

    /// <summary>De sæt, kørende "Overvåg uden datatab"-jobs på iPad'en kan vælge imellem – de slettes ikke af oprydningen.</summary>
    public static async Task<HashSet<string>> Protected(AppDb db, int ipadId)
    {
        var rows = await db.Jobs.AsNoTracking().Where(j => (j.Type == JobTypes.Ios || j.Type == JobTypes.UsbIpad) && JobStatus.Active.Contains(j.Status))
            .Select(j => j.ParamsJson).ToListAsync();
        return rows.Select(Json.Des<JobParams>).Where(p => p.Action == IosActions.SuperviseNoWipe && p.IpadId == ipadId)
            .SelectMany(p => (p.Candidates ?? []).Select(c => c.Set).Append(p.BackupSet ?? "")).Where(s => s.Length > 0).ToHashSet(StringComparer.Ordinal);
    }
}
