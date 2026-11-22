using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Fleetdeck.Server.Data;
using Fleetdeck.Shared;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Server.Services;

/// <summary>
/// iPads i kabel på en pc med udvidelsen "iPad via USB" (pymobiledevice3). Agenternes meldinger holdes i hukommelsen: de er "lige nu"
/// og kommer igen inden for et minut efter en genstart af serveren. Kun koblingen til iPad'en i Fleetdeck (ECID, serienummer, iPadOS,
/// overvåget, låst profil) gemmes – i de samme felter som iPads set af en Mac med Apple Configurator.
/// </summary>
public sealed class UsbIpadStore
{
    /// <summary>Agenten melder mindst hvert minut; ældre meldinger regnes for forældede (agenten er væk, eller udvidelsen er fjernet).</summary>
    public static readonly TimeSpan Fresh = TimeSpan.FromSeconds(150);

    private sealed record Entry(UsbIpadReport Report, DateTime At, string Key);
    private readonly ConcurrentDictionary<int, Entry> _m = new();
    private readonly ConcurrentDictionary<int, DateTime> _linked = new();

    /// <summary>De linkede iPads opdateres ved ændringer og ellers højst hvert 5. minut (sidst set via USB).</summary>
    public bool LinkDue(int machineId, bool changed, DateTime? now = null)
    {
        var t = now ?? DateTime.UtcNow;
        if (!changed && _linked.TryGetValue(machineId, out var last) && t - last < TimeSpan.FromMinutes(5)) return false;
        _linked[machineId] = t;
        return true;
    }

    /// <summary>Gem en melding. Returnerer true, hvis noget, brugerfladen viser, er ændret.</summary>
    public bool Put(int machineId, UsbIpadReport r, DateTime? now = null)
    {
        foreach (var d in r.Devices)
        {
            d.Ecid = Cfgutil.NormEcid(d.Ecid);
            if (d.Ecid.Length == 0) d.Ecid = Pmd3Parse.EcidFromUdid(d.Udid);
        }
        r.Devices = [.. r.Devices.Where(d => d.Udid is { Length: > 0 })];
        var key = UsbIpadWatcher.Key(r);
        var old = Get(machineId, now);
        _m[machineId] = new Entry(r, now ?? DateTime.UtcNow, key);
        return old is null || old.Value.Key != key;
    }

    public (UsbIpadReport Report, DateTime At, string Key)? Get(int machineId, DateTime? now = null) =>
        _m.TryGetValue(machineId, out var e) && (now ?? DateTime.UtcNow) - e.At < Fresh ? (e.Report, e.At, e.Key) : null;

    public bool Ready(int machineId) => Get(machineId)?.Report.Ready == true;

    public List<(int MachineId, UsbIpadReport Report, DateTime At)> All(DateTime? now = null) =>
        [.. _m.Keys.Select(id => (id, e: Get(id, now))).Where(x => x.e is not null).Select(x => (x.id, x.e!.Value.Report, x.e.Value.At))];

    /// <summary>Den tilsluttede enhed med dette UDID på maskinen (null = ikke sat i lige nu).</summary>
    public IosDeviceInfo? Device(int machineId, string? udid) =>
        udid is { Length: > 0 } ? Get(machineId)?.Report.Devices.FirstOrDefault(d => d.Udid == udid) : null;

    /// <summary>Nyere oplysninger fra et job (fx efter installation af profilen) lægges oven i det, vagten meldte.</summary>
    public void Merge(int machineId, IosDeviceInfo d)
    {
        if (!_m.TryGetValue(machineId, out var e)) return;
        var i = e.Report.Devices.FindIndex(x => x.Udid == d.Udid);
        if (i < 0) return;
        e.Report.Devices[i] = Cfgutil.Merge(e.Report.Devices[i], d);
    }
}

/// <summary>Kobler tilsluttede enheder til iPads i Fleetdeck (samme felter og regler som cfgutil-enheder: ECID, ellers serienummer).</summary>
public static class UsbIpadLink
{
    /// <summary>Opdatér de linkede iPads. Kun enheder, der er parret (alle oplysninger er læst), bruges. Returnerer true, hvis en iPad blev opdateret.</summary>
    public static async Task<bool> Apply(AppDb db, IEnumerable<IosDeviceInfo> devices, DateTime now)
    {
        var list = devices.Where(d => d.Paired != false && (Cfgutil.NormEcid(d.Ecid).Length > 0 || d.Serial is { Length: > 0 })).ToList();
        if (list.Count == 0) return false;
        var ipads = await db.Ipads.Where(i => i.Ecid != null || i.Serial != null).ToListAsync();
        var changed = false;
        foreach (var d in list)
        {
            var ecid = Cfgutil.NormEcid(d.Ecid);
            var p = (ecid.Length > 0 ? ipads.FirstOrDefault(i => i.Ecid == ecid) : null)
                    ?? (d.Serial is { Length: > 0 } sn ? ipads.FirstOrDefault(i => i.Serial == sn) : null);
            if (p is null) continue;
            if (ecid.Length == 0) d.Ecid = p.Ecid ?? "";
            IosInventory.Link(p, d, now);
            changed = true;
        }
        return changed;
    }
}

/// <summary>Tjek af en indsat PEM-keybag. Selve identiteten ligger i <see cref="IpadSupervision"/> (husstandens ene identitet).</summary>
public static class UsbIpadKeybag
{
    /// <summary>null hvis PEM'en har både et certifikat og den private nøgle, der passer til det; ellers hvad der er galt.</summary>
    public static string? Validate(string? pem)
    {
        if (string.IsNullOrWhiteSpace(pem)) return "Indsæt nøgle og certifikat (PEM)";
        try
        {
            using var cert = X509Certificate2.CreateFromPem(pem, pem);
            return cert.HasPrivateKey ? null : "Den private nøgle mangler";
        }
        catch (CryptographicException e) { return $"Kunne ikke læses: {e.Message}"; }
        catch (ArgumentException e) { return $"Kunne ikke læses: {e.Message}"; }
    }
}

/// <summary>Krypterede iPad-backupper i Fleetdeck: data/ipad-backups/ipad-&lt;id&gt;/ipad-ÅÅÅÅMMDD-TTMMSS.partNNN + .json (når alle dele er modtaget).</summary>
public static partial class UsbIpadBackups
{
    public const int Keep = 3, MaxParts = 200;

    public sealed record Manifest(string Set, string Udid, int Parts, long Bytes, DateTime Created, string? Machine, string? DeviceName,
        bool? Encrypted = null, bool? Verified = null, bool? DeviceEmpty = null, long? DeviceBytes = null)
    {
        public bool Pinned { get; init; }
    }

    /// <summary>Det sæt, "Overvåg uden datatab" sidst brugte (&lt;sæt&gt;.pinned): ryddes aldrig op.</summary>
    public static string PinFile(string dir, string set) => Path.Combine(dir, set + ".pinned");

    public static void Pin(string dir, string set)
    {
        if (SafeSet(set) is null || !File.Exists(Path.Combine(dir, set + ".json"))) return;
        foreach (var f in Directory.GetFiles(dir, "ipad-*.pinned")) if (Path.GetFileName(f) != set + ".pinned") File.Delete(f);
        File.WriteAllText(PinFile(dir, set), DateTime.UtcNow.ToString("O"));
    }

    public static string Dir(Paths paths, int ipadId) => Path.Combine(paths.Data, "ipad-backups", $"ipad-{ipadId}");
    public static string? SafeSet(string? name) => name is { Length: > 0 } n && SetRx().IsMatch(n) ? n : null;
    public static string Part(string dir, string set, int part) => Path.Combine(dir, $"{set}.part{part:000}");

    [GeneratedRegex(@"^ipad-\d{8}-\d{6}$")] private static partial Regex SetRx();

    /// <summary>Når sidste del er modtaget: tjek at alle dele findes, og skriv beskrivelsen. null = der mangler dele.</summary>
    public static Manifest? Finish(string dir, string set, int parts, string udid, string? machine, string? deviceName,
        bool? encrypted = null, bool? verified = null, bool? deviceEmpty = null, long? deviceBytes = null)
    {
        long bytes = 0;
        for (var i = 1; i <= parts; i++)
        {
            var f = new FileInfo(Part(dir, set, i));
            if (!f.Exists) return null;
            bytes += f.Length;
        }
        var m = new Manifest(set, udid, parts, bytes, DateTime.UtcNow, machine, deviceName, encrypted, verified, deviceEmpty, deviceBytes);
        File.WriteAllText(Path.Combine(dir, set + ".json"), Json.Ser(m));
        return m;
    }

    public static List<Manifest> List(Paths paths, int ipadId) => ListDir(Dir(paths, ipadId));

    public static List<Manifest> ListDir(string dir)
    {
        if (!Directory.Exists(dir)) return [];
        var list = new List<Manifest>();
        foreach (var f in Directory.GetFiles(dir, "ipad-*.json"))
        {
            var m = Json.Des<ManifestDto>(File.ReadAllText(f)).To();
            if (m is not null && SafeSet(m.Set) == Path.GetFileNameWithoutExtension(f) && Enumerable.Range(1, m.Parts).All(i => File.Exists(Part(dir, m.Set, i))))
                list.Add(m with { Pinned = File.Exists(PinFile(dir, m.Set)) });
        }
        return [.. list.OrderByDescending(x => x.Set, StringComparer.Ordinal)];
    }

    /// <summary>Behold de <paramref name="keep"/> nyeste hele backupper; ufærdige uploads slettes efter et døgn.</summary>
    /// <remarks>Det fastholdte sæt (&lt;sæt&gt;.pinned) og sæt i <paramref name="protect"/> (et kørende "Overvåg uden datatab") slettes aldrig og tæller ikke med.</remarks>
    public static void Prune(string dir, int keep = Keep, IReadOnlySet<string>? protect = null)
    {
        if (!Directory.Exists(dir)) return;
        var sets = Directory.GetFiles(dir, "ipad-*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>()
            .OrderByDescending(x => x, StringComparer.Ordinal).ToList();
        bool Held(string s) => File.Exists(PinFile(dir, s)) || protect?.Contains(s) == true;
        var keepSets = sets.Where(s => !Held(s)).Take(keep).Concat(sets.Where(Held)).ToHashSet();
        foreach (var f in Directory.GetFiles(dir))
        {
            var name = Path.GetFileName(f);
            var set = name.Split('.')[0];
            var old = sets.Contains(set) && !keepSets.Contains(set);
            var stale = !sets.Contains(set) && File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-1);
            if (old || stale) try { File.Delete(f); } catch (IOException) { }
        }
    }

    private sealed class ManifestDto
    {
        public string? Set { get; set; }
        public string? Udid { get; set; }
        public int Parts { get; set; }
        public long Bytes { get; set; }
        public DateTime Created { get; set; }
        public string? Machine { get; set; }
        public string? DeviceName { get; set; }
        public bool? Encrypted { get; set; }
        public bool? Verified { get; set; }
        public bool? DeviceEmpty { get; set; }
        public long? DeviceBytes { get; set; }
        public Manifest? To() => Set is { Length: > 0 } s && Udid is { Length: > 0 } u && Parts is > 0 and <= MaxParts
            ? new(s, u, Parts, Bytes, Created, Machine, DeviceName, Encrypted, Verified, DeviceEmpty, DeviceBytes) : null;
    }
}
