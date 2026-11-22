using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Fleetdeck.Shared;

// USB-stik med BIOS-opdateringen (design afsnit 7), som reserve til opdateringen inde fra styresystemet.
// Samme sikkerhedsmodel som reservekortet (SpareCard): diske udpeges ved deres navn i /dev/disk/by-id, brugeren bekræfter
// serienummer og størrelse, og agenten tjekker det hele igen lige før skrivning. Alle hårde udelukkelser ligger her i agenten,
// ikke i UI'en. Kun en disk, der er sat i, efter at guiden tog sit øjebliksbillede (hotplug-diff), og som er bekræftet lokalt på
// selve pc'en (http://localhost, /_fd/confirm) inden for 5 minutter, bliver slettet og skrevet.
// Linux her; Windows (MSFT_Disk, FAT32 ≤ 32 GB, eject) i BiosUsbWin.cs og Mac (diskutil) i BiosUsbMac.cs – samme protokol.

public static class BiosUsbPhases
{
    /// <summary>Første gang pr. guide: øjebliksbillede af diskene. Derefter: vent op til 90 s på et nyt stik og meld listen.</summary>
    public const string List = "list";
    public const string Write = "write";
}

public static class BiosUsbLayouts
{
    /// <summary>Lenovo ThinkCentre: hele USB-pakken (zip) pakkes ud i roden af stikket.</summary>
    public const string LenovoZip = "lenovo-zip";
    /// <summary>Dell: BIOS-programmet (.exe) i roden (F12 → BIOS Flash Update).</summary>
    public const string DellExe = "dell-exe";
    /// <summary>HP (beta): .bin under \EFI\HP\BIOS\New.</summary>
    public const string HpBin = "hp-bin";
    public static readonly string[] All = [LenovoZip, DellExe, HpBin];
}

/// <summary>En USB-disk, som guiden viser. Systemdiske meldes slet ikke.</summary>
public sealed class BiosUsbDisk
{
    /// <summary>Navnet i /dev/disk/by-id (følger stikket, ikke sdX).</summary>
    public string Id { get; set; } = "";
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string Model { get; set; } = "";
    public string Vendor { get; set; } = "";
    public string Serial { get; set; } = "";
    public int Partitions { get; set; }
    /// <summary>Etiketter og filsystemer på stikket nu (det, der bliver slettet).</summary>
    public List<string> Labels { get; set; } = [];
    public bool Selectable { get; set; }
    /// <summary>Hvorfor disken ikke kan vælges (null = kan vælges).</summary>
    public string? Why { get; set; }
    /// <summary>Over grænsen (256 GB) – kan kun vælges med en ekstra bekræftelse (Why er så kun grænsen).</summary>
    public bool Large { get; set; }

    /// <summary>Kan vælges – eventuelt kun med den ekstra bekræftelse for store diske.</summary>
    public bool Usable(bool allowLarge, long maxBytes) => Selectable || Large && allowLarge && (Why == BiosUsb.TooLarge(maxBytes) || Why == BiosUsb.ExternalDrive);
}

/// <summary>JobParams.BiosUsb: hvad agenten skal gøre (usb_disks og bios_usb_write).</summary>
public sealed class BiosUsbOrder
{
    public string SessionId { get; set; } = "";
    public string Phase { get; set; } = BiosUsbPhases.List;
    public long MaxBytes { get; set; } = BiosUsb.DefaultMaxBytes;
    public bool AllowLarge { get; set; }
    // kun bios_usb_write
    public string? DiskId { get; set; }
    public string? Serial { get; set; }
    public long Size { get; set; }
    /// <summary>Kvitteringen fra den lokale bekræftelse (/_fd/confirm) – skal passe til sessionen, disken og størrelsen.</summary>
    public string? ConfirmHash { get; set; }
    public string? Layout { get; set; }
    public string? FileName { get; set; }
    public string? Sha256 { get; set; }
    public long PackageSize { get; set; }
    public string? Version { get; set; }
    /// <summary>LÆS-MIG.txt til roden af stikket.</summary>
    public string? Readme { get; set; }
}

/// <summary>JobResult.BiosUsb: diskene eller resultatet af skrivningen.</summary>
public sealed class BiosUsbReport
{
    public string SessionId { get; set; } = "";
    public string Phase { get; set; } = BiosUsbPhases.List;
    /// <summary>ok | failed | refused</summary>
    public string Outcome { get; set; } = "ok";
    public string Message { get; set; } = "";
    public List<BiosUsbDisk>? Disks { get; set; }
    /// <summary>Første liste: øjebliksbilledet er taget, og diskene, der sad i, kan ikke vælges.</summary>
    public bool Baseline { get; set; }
    public int Files { get; set; }
    public long Bytes { get; set; }
    public bool Verified { get; set; }
    public bool Ejected { get; set; }
    public string? DiskLabel { get; set; }
}

/// <summary>/_fd/claim og /_fd/confirm: agentens svar til browseren på samme pc.</summary>
public sealed class BiosUsbClaimRequest { public string Nonce { get; set; } = ""; }
public sealed class BiosUsbClaimResponse { public string SessionId { get; set; } = ""; public string Machine { get; set; } = ""; }

/// <summary>
/// Det, agenten husker for en guide: øjebliksbilledet af diskene og de lokale bekræftelser (højst 5 minutter, bruges én gang).
/// Kun i hukommelsen – genstarter agenten, starter guiden forfra.
/// </summary>
public sealed class BiosUsbLocal(Func<DateTime>? clock = null)
{
    public static readonly BiosUsbLocal Shared = new();
    public static readonly TimeSpan ConfirmFor = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan BaselineFor = TimeSpan.FromHours(1);
    private readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);
    /// <summary>En disk glemmes først, når den har manglet i mindst så mange scanninger i træk …</summary>
    public const int ForgetAfterScans = 2;
    /// <summary>… og i mindst så lang tid (en disk, der kortvarigt genopremses eller mangler i én scanning, glemmes ikke).</summary>
    public static readonly TimeSpan ForgetAfter = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<string, (ImmutableHashSet<string> Keys, DateTime At)> _baselines = new();
    private readonly ConcurrentDictionary<string, Dictionary<string, (int Misses, DateTime Since)>> _missing = new();
    private readonly ConcurrentDictionary<string, (string Disk, string Serial, long Size, string Hash, DateTime At)> _confirms = new();
    private readonly Lock _gate = new();

    /// <summary>Øjebliksbilledet (uforanderligt – <see cref="Forget"/> erstatter det, så det kan læses uden lås).</summary>
    public ImmutableHashSet<string>? Baseline(string session) =>
        _baselines.TryGetValue(session, out var b) && _now() - b.At < BaselineFor ? b.Keys : null;

    /// <summary>
    /// Glem diske i øjebliksbilledet, der ikke er sat i længere – kun dem uden diskseq (dem med diskseq får en ny nøgle ved næste
    /// indsættelse alligevel). Så tæller et stik, der er taget ud og sat i igen, som nyt. En disk glemmes først, når den har manglet i
    /// <see cref="ForgetAfterScans"/> scanninger i træk over mindst <see cref="ForgetAfter"/>; dukker den op igen før, nulstilles tællingen.
    /// </summary>
    public void Forget(string session, IEnumerable<string> present)
    {
        var now = present.ToHashSet(StringComparer.Ordinal);
        lock (_gate)
        {
            if (!_baselines.TryGetValue(session, out var b) || _now() - b.At >= BaselineFor) return;
            var miss = _missing.GetOrAdd(session, _ => new(StringComparer.Ordinal));
            var gone = new List<string>();
            foreach (var k in b.Keys.Where(k => k.Count(c => c == '|') == 1))
            {
                if (now.Contains(k)) { miss.Remove(k); continue; }
                var m = miss.TryGetValue(k, out var x) ? (x.Misses + 1, x.Since) : (1, _now());
                miss[k] = m;
                if (m.Item1 >= ForgetAfterScans && _now() - m.Item2 >= ForgetAfter) gone.Add(k);
            }
            if (gone.Count == 0) return;
            foreach (var k in gone) miss.Remove(k);
            _baselines[session] = (b.Keys.Except(gone), b.At);
        }
    }

    public void SetBaseline(string session, IEnumerable<string> keys)
    {
        lock (_gate)
        {
            foreach (var k in _baselines.Where(kv => _now() - kv.Value.At >= BaselineFor).Select(kv => kv.Key).ToList())
            {
                _baselines.TryRemove(k, out _);
                _missing.TryRemove(k, out _);
            }
            _baselines[session] = (keys.ToImmutableHashSet(StringComparer.Ordinal), _now());
            _missing.TryRemove(session, out _);
        }
    }

    /// <summary>Brugeren har bekræftet disken på denne pc: gem den i 5 minutter og giv en kvittering, serveren lægger i jobbet.</summary>
    public string Confirm(string session, string disk, string serial, long size)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{Convert.ToHexString(RandomNumberGenerator.GetBytes(32))}|{session}|{disk}|{serial}|{size}")));
        _confirms[session] = (disk, serial, size, hash, _now());
        return hash;
    }

    /// <summary>Passer bekræftelsen (samme session, disk, serie, størrelse og kvittering, højst 5 minutter gammel)? Bruges én gang.</summary>
    public bool Take(string session, string disk, string serial, long size, string? hash)
    {
        if (!_confirms.TryRemove(session, out var c)) return false;
        return hash is { Length: > 0 } && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(c.Hash), Encoding.ASCII.GetBytes(hash))
            && c.Disk == disk && c.Serial == serial && c.Size == size && _now() - c.At <= ConfirmFor && _now() >= c.At;
    }
}

public static class BiosUsb
{
    /// <summary>Diske over 256 GB kræver en ekstra bekræftelse (kan ændres i Indstillinger).</summary>
    public const long DefaultMaxBytes = 256_000_000_000;
    /// <summary>FAT32-partitionen bliver højst 32 GiB (Windows' grænse; BIOS'erne kræver kun FAT32).</summary>
    public const long FatMaxBytes = 32L * 1024 * 1024 * 1024;
    public const string VolumeLabel = "FLEETBIOS";
    public const string ReadmeName = "LÆS-MIG.txt";
    public const string MkfsMissing = "mkfs.vfat mangler. Installér dosfstools.";
    public const string PlatformLater = "USB-stik kan kun laves fra en Linux-pc, en Windows-pc eller en Mac med Fleetdeck-agenten. Brug \"Hent pakke\" og følg LÆS-MIG.txt.";

    public const string NotUsb = "Disken er ikke et USB-stik.";
    public const string SystemDisk = "Disken bruges af systemet.";
    public const string NoMedia = "Der er intet medie i læseren.";
    public const string NotNew = "Stikket sad i, før guiden begyndte. Tag det ud og sæt det i igen.";
    public const string NoStableName = "Disken har intet fast navn i /dev/disk/by-id.";
    /// <summary>En ekstern harddisk/SSD (ikke flytbart medie): kan kun vælges med den ekstra bekræftelse ligesom en stor disk (Large).</summary>
    public const string ExternalDrive = "Det ligner en ekstern harddisk eller SSD, ikke et USB-stik.";
    public static string TooLarge(long max) => $"Disken er større end {max / 1_000_000_000} GB.";

    private static readonly string[] SystemMounts = ["/", "/boot", "/boot/efi", "/efi", "[SWAP]", "/usr", "/var", "/home"];
    private static readonly string[] SystemFs = ["LVM2_member", "crypto_LUKS", "linux_raid_member", "swap", "zfs_member", "bcache"];
    private static readonly string[] Removable = ["/media/", "/run/media/"];

    /// <summary>
    /// Nøglen til øjebliksbilledet: stien, serienummeret og kernens disksekvens (/sys/block/X/diskseq, tælles op ved hver indsættelse).
    /// Uden diskseq (kerne før 5.15) får et stik, der tages ud og sættes i igen, samme /dev/sdX og samme serie; så glemmes forsvundne
    /// diske i stedet (<see cref="BiosUsbLocal.Forget"/>).
    /// </summary>
    public static string Key(BlockDev d) => d.DiskSeq.Length > 0 ? $"{d.Path}|{d.Serial}|{d.DiskSeq}" : $"{d.Path}|{d.Serial}";

    /// <summary>Sæt diskseq på diskene fra sysfs (tom, hvis kernen ikke har den).</summary>
    public static List<BlockDev> WithSeq(List<BlockDev> devs, ISpareHost h) =>
        devs.Select(d => d.Type == "disk" && d.Name.Length > 0 && !d.Name.Contains('/') && h.ReadText($"/sys/block/{d.Name}/diskseq")?.Trim() is { Length: > 0 } s
            && s.All(char.IsAsciiDigit) ? d with { DiskSeq = s } : d).ToList();

    private static IEnumerable<BlockDev> Tree(BlockDev d) => new[] { d }.Concat(d.Children.SelectMany(Tree));

    /// <summary>
    /// Hvorfor disken ikke må skrives (null = den må). Hårde udelukkelser: ikke USB eller ikke en hel disk, systemets disk (/, /boot,
    /// swap, LVM/LUKS/RAID), monteret uden for /media, intet medie, større end grænsen (uden ekstra bekræftelse), og ikke sat i efter
    /// øjebliksbilledet.
    /// </summary>
    public static string? Refusal(BlockDev d, List<BlockDev> all, ISet<string>? baseline, long maxBytes, bool allowLarge)
    {
        if (d.Type != "disk" || d.Tran != "usb" || SpareCard.IsInternalCard(d.Path)) return NotUsb;
        if (InUse(d, all)) return SystemDisk;
        if (d.Size <= 0) return NoMedia;
        if (baseline is null || baseline.Contains(Key(d))) return NotNew;
        if (d.Size > maxBytes && !allowLarge) return TooLarge(maxBytes);
        return null;
    }

    /// <summary>Systemets disk, eller en disk med noget monteret uden for /media – den vises slet ikke.</summary>
    public static bool InUse(BlockDev d, List<BlockDev> all)
    {
        if (SpareCard.RootDisk(all) is { } root && root.Path == d.Path) return true;
        foreach (var x in Tree(d))
        {
            if (SystemFs.Contains(x.FsType, StringComparer.OrdinalIgnoreCase)) return true;
            foreach (var mp in x.Mounts)
                if (SystemMounts.Contains(mp) || !Removable.Any(r => mp.StartsWith(r, StringComparison.Ordinal))) return true;
        }
        return false;
    }

    /// <summary>by-id-navnet for disken (usb-… frem for wwn-…, aldrig et -partN-link).</summary>
    public static string? IdOf(BlockDev d, IReadOnlyDictionary<string, string> byId) =>
        byId.Where(kv => kv.Value == d.Path && !kv.Key.Contains("-part", StringComparison.Ordinal) && SpareCard.SafeId(kv.Key))
            .Select(kv => kv.Key).OrderBy(k => k.StartsWith("usb-", StringComparison.Ordinal) ? 0 : 1).ThenBy(k => k, StringComparer.Ordinal).FirstOrDefault();

    /// <summary>USB-diskene til guiden. Systemdiske (også en USB-disk, maskinen kører fra) meldes ikke.</summary>
    public static List<BiosUsbDisk> Evaluate(List<BlockDev> devs, IReadOnlyDictionary<string, string> byId, ISet<string>? baseline, long maxBytes)
    {
        var o = new List<BiosUsbDisk>();
        foreach (var d in devs.Where(d => d.Type == "disk" && d.Tran == "usb" && !SpareCard.IsInternalCard(d.Path)))
        {
            if (InUse(d, devs)) continue;
            var id = IdOf(d, byId);
            var why = id is null ? NoStableName : Refusal(d, devs, baseline, maxBytes, allowLarge: false);
            o.Add(new BiosUsbDisk
            {
                Id = id ?? "", Path = d.Path, Size = d.Size, Model = d.Model, Vendor = d.Vendor, Serial = d.Serial, Partitions = d.Children.Count,
                Labels = d.Children.Select(c => string.Join(' ', new[] { c.Label, c.FsType }.Where(s => s.Length > 0))).Where(s => s.Length > 0).ToList(),
                Selectable = why is null, Why = why, Large = d.Size > maxBytes,
            });
        }
        return o;
    }

    public static string Label(BiosUsbDisk d)
    {
        var name = string.Join(' ', new[] { d.Vendor, d.Model }.Where(x => x.Length > 0)).Trim();
        return $"{(name.Length > 0 ? name : "USB-stik")} · {SpareCard.Size(d.Size)}";
    }

    /// <summary>Første partition: sda → sda1, nvme0n1 → nvme0n1p1.</summary>
    public static string PartitionPath(string disk) => char.IsAsciiDigit(disk[^1]) ? disk + "p1" : disk + "1";

    /// <summary>sfdisk: MBR med én aktiv FAT32-partition (type c, LBA), højst 32 GiB.</summary>
    public static string SfdiskScript(long size) =>
        "label: dos\n" + (size > FatMaxBytes ? "size=32GiB, type=c, bootable\n" : "type=c, bootable\n");

    /// <summary>Layoutet ud fra leverandøren og pakken.</summary>
    public static string? LayoutFor(string vendor, string kind) => (vendor, kind) switch
    {
        ("lenovo", "usb-zip") => BiosUsbLayouts.LenovoZip,
        ("dell", _) => BiosUsbLayouts.DellExe,
        ("hp", _) => BiosUsbLayouts.HpBin,
        _ => null,
    };

    /// <summary>Et sikkert navn fra zip'en: relativt, ingen "..", ingen drevbogstaver eller mærkelige tegn.</summary>
    public static string? SafeEntry(string name)
    {
        var n = name.Replace('\\', '/');
        if (n.Length == 0 || n.StartsWith('/') || n.Contains(':') || n.Any(ch => ch < ' ')) return null;
        var parts = n.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p is "." or "..")) return null;
        return string.Join('/', parts);
    }

    public static string Sha256File(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(f));
    }

    /// <summary>
    /// Læg pakken på stikket (monteret i <paramref name="dir"/>) efter layoutet. Returnerer filerne med deres SHA-256 (fra kilden),
    /// så de kan læses tilbage og tjekkes bagefter.
    /// </summary>
    public static List<(string Path, string Sha, long Size)> Extract(string package, string layout, string fileName, string dir, string? readme)
    {
        var manifest = new List<(string, string, long)>();
        var root = System.IO.Path.GetFullPath(dir);
        string Target(string rel)
        {
            var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, rel));
            // Linux: "/run/x/…"; Windows: "E:\…" (GetFullPath giver omvendte skråstreger)
            var prefix = root.TrimEnd('/', System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException($"Ugyldig sti i pakken: {rel}");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            return full;
        }
        void Copy(Stream src, string rel)
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buf = new byte[1 << 16];
            long n = 0;
            using (var o = File.Create(Target(rel)))
                for (int r; (r = src.Read(buf)) > 0;) { sha.AppendData(buf, 0, r); o.Write(buf, 0, r); n += r; }
            manifest.Add((rel, Convert.ToHexStringLower(sha.GetHashAndReset()), n));
        }
        var safeName = SafeEntry(fileName) is { } sn && !sn.Contains('/') ? sn : throw new InvalidDataException("Ugyldigt filnavn.");
        switch (layout)
        {
            case BiosUsbLayouts.LenovoZip:
                using (var z = ZipFile.OpenRead(package))
                    foreach (var e in z.Entries)
                    {
                        if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\')) continue;
                        var rel = SafeEntry(e.FullName) ?? throw new InvalidDataException($"Ugyldig sti i pakken: {e.FullName}");
                        if (rel.Equals(ReadmeName, StringComparison.OrdinalIgnoreCase)) continue;
                        using var s = e.Open();
                        Copy(s, rel);
                    }
                break;
            case BiosUsbLayouts.DellExe:
                using (var s = File.OpenRead(package)) Copy(s, safeName);
                break;
            case BiosUsbLayouts.HpBin:
                using (var s = File.OpenRead(package)) Copy(s, "EFI/HP/BIOS/New/" + safeName);
                break;
            default: throw new InvalidDataException($"Ukendt layout: {layout}");
        }
        if (readme is { Length: > 0 })
            using (var s = new MemoryStream(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(readme.Replace("\r\n", "\n").Replace("\n", "\r\n"))).ToArray()))
                Copy(s, ReadmeName);
        return manifest;
    }

    /// <summary>Læs hver fil tilbage fra stikket og sammenlign med kilden. Returnerer de filer, der ikke passer.</summary>
    public static List<string> Verify(string dir, IEnumerable<(string Path, string Sha, long Size)> manifest)
    {
        var bad = new List<string>();
        foreach (var (rel, sha, size) in manifest)
        {
            var p = System.IO.Path.Combine(dir, rel);
            if (!File.Exists(p) || new FileInfo(p).Length != size || !string.Equals(Sha256File(p), sha, StringComparison.OrdinalIgnoreCase)) bad.Add(rel);
        }
        return bad;
    }

    /// <summary>Pakken og jobbet passer sammen (layout, filnavn, størrelse og SHA-256)? null = ja. Bruges af både Linux og Windows.</summary>
    public static (string Outcome, string Text)? PackageProblem(BiosUsbOrder o, string package)
    {
        if (o.Layout is not { } layout || !BiosUsbLayouts.All.Contains(layout) || o.FileName is null) return ("refused", "Jobbet mangler pakkens layout.");
        if (!File.Exists(package) || new FileInfo(package).Length != o.PackageSize)
            return ("failed", "Pakken har ikke den forventede størrelse. Intet er skrevet.");
        if (o.Sha256 is not { Length: 64 } want || !string.Equals(Sha256File(package), want, StringComparison.OrdinalIgnoreCase))
            return ("failed", "Pakkens SHA-256 passer ikke. Intet er skrevet.");
        return null;
    }

    /// <summary>
    /// Skriv stikket (Linux). Rækkefølge: pakken passer (SHA-256 og størrelse) → disken findes stadig ved sit by-id-navn med samme serie og
    /// størrelse (TOCTOU) → alle udelukkelser holder stadig → lokal bekræftelse under 5 minutter → mkfs.vfat findes → afmontér → ny
    /// partitionstabel → FAT32 → montér → kopiér → sync → afmontér og montér igen → læs alt tilbage → afmontér → skub ud.
    /// Afmonterer altid igen, også ved fejl.
    /// </summary>
    public static BiosUsbReport Write(ISpareHost h, BiosUsbOrder o, string package, BiosUsbLocal local, Action<string> log,
        Action<int, int, string>? phase = null, string mountRoot = "/run")
    {
        var rep = new BiosUsbReport { SessionId = o.SessionId, Phase = BiosUsbPhases.Write };
        BiosUsbReport Stop(string outcome, string text) { rep.Outcome = outcome; rep.Message = text; log(text); return rep; }
        phase ??= (_, _, _) => { };

        // 1. pakken
        phase(0, 5, "Kontrollerer pakken …");
        if (PackageProblem(o, package) is { } pkgBad) return Stop(pkgBad.Outcome, pkgBad.Text);
        var layout = o.Layout!;

        // 2. disken: samme stik som bekræftet, og alle udelukkelser holder stadig
        if (!SpareCard.SafeId(o.DiskId) || o.Serial is null) return Stop("refused", "Ugyldigt disknavn.");
        var ls = h.Run("lsblk", ["-J", "-b", "-o", SpareCard.LsblkColumns]);
        if (ls.Exit != 0) return Stop("failed", "lsblk fejlede: " + ls.Output.Trim());
        var devs = WithSeq(SpareCard.ParseLsblk(ls.Output), h);
        var path = h.RealPath("/dev/disk/by-id/" + o.DiskId);
        var d = devs.FirstOrDefault(x => x.Path == path && x.Type == "disk");
        if (path is null || d is null) return Stop("refused", "Stikket er ikke sat i længere. Intet er skrevet.");
        if (d.Serial != o.Serial || d.Size != o.Size) return Stop("refused", "Stikket er ikke det samme som det, du bekræftede. Intet er skrevet.");
        if (Refusal(d, devs, local.Baseline(o.SessionId), o.MaxBytes, o.AllowLarge) is { } why) return Stop("refused", why + " Intet er skrevet.");
        if (!local.Take(o.SessionId, o.DiskId!, o.Serial, o.Size, o.ConfirmHash))
            return Stop("refused", "Stikket er ikke bekræftet på denne pc inden for de sidste 5 minutter. Intet er skrevet.");
        if (!new[] { "/usr/sbin/mkfs.vfat", "/sbin/mkfs.vfat", "/usr/bin/mkfs.vfat" }.Any(h.Exists)) return Stop("failed", MkfsMissing);
        rep.DiskLabel = $"{string.Join(' ', new[] { d.Vendor, d.Model }.Where(x => x.Length > 0))} · {SpareCard.Size(d.Size)} · {d.Serial}".Trim();
        log($"Stik: {rep.DiskLabel} ({d.Path})");

        // 3. formatér
        phase(5, 25, "Formaterer …");
        foreach (var mp in Tree(d).SelectMany(x => x.Mounts.Select(m => (x.Path, m))))
        {
            var u = h.Run("umount", [mp.Path]);
            if (u.Exit != 0) return Stop("failed", $"Stikket kunne ikke afmonteres ({mp.m}): {u.Output.Trim()}");
        }
        foreach (var (file, args, stdin) in new (string, string[], string?)[]
        {
            ("wipefs", ["-a", d.Path], null),
            ("sfdisk", ["--wipe", "always", d.Path], SfdiskScript(d.Size)),
        })
        {
            var r = h.Run(file, args, stdin);
            if (r.Exit != 0) return Stop("failed", $"{file} fejlede: {r.Output.Trim()}");
        }
        h.Run("udevadm", ["settle", "--timeout=15"]);
        var part = PartitionPath(d.Path);
        var mk = h.Run("mkfs.vfat", ["-F", "32", "-n", VolumeLabel, part]);
        if (mk.Exit != 0) return Stop("failed", "mkfs.vfat fejlede: " + mk.Output.Trim());

        // 4. kopiér og kontrollér
        var dir = System.IO.Path.Combine(mountRoot, "fleetdeck-bios-usb-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)));
        var mounted = false;
        try
        {
            h.MakeDir(dir);
            bool Mount()
            {
                var m = h.Run("mount", ["-t", "vfat", "-o", "uid=0,gid=0,umask=077,flush", part, dir]);
                mounted = m.Exit == 0;
                if (!mounted) log("mount fejlede: " + m.Output.Trim());
                return mounted;
            }
            bool Unmount()
            {
                h.Run("sync", []);
                var u = h.Run("umount", [dir]);
                if (u.Exit == 0) mounted = false;
                return !mounted;
            }
            if (!Mount()) return Stop("failed", "Stikket kunne ikke monteres efter formateringen.");
            phase(25, 75, "Kopierer …");
            List<(string Path, string Sha, long Size)> manifest;
            try { manifest = Extract(package, layout, o.FileName!, dir, o.Readme); }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            { return Stop("failed", "Kopieringen fejlede: " + e.Message); }
            rep.Files = manifest.Count;
            rep.Bytes = manifest.Sum(m => m.Size);
            log($"Kopieret {rep.Files} filer ({SpareCard.Size(rep.Bytes)}).");
            phase(75, 92, "Kontrollerer …");
            if (!Unmount()) return Stop("failed", "Stikket kunne ikke afmonteres efter kopieringen.");
            if (!Mount()) return Stop("failed", "Stikket kunne ikke monteres igen til kontrollen.");
            var bad = Verify(dir, manifest);
            if (bad.Count > 0) return Stop("failed", $"{bad.Count} fil(er) på stikket passer ikke med pakken (fx {bad[0]}). Brug et andet stik.");
            rep.Verified = true;
            if (!Unmount()) return Stop("failed", "Stikket kunne ikke afmonteres efter kontrollen.");
        }
        finally
        {
            if (mounted) { h.Run("sync", []); h.Run("umount", [dir]); }
            h.Run("rmdir", [dir]);
        }

        // 5. skub ud
        phase(92, 100, "Skubber ud …");
        if (new[] { "/usr/bin/udisksctl", "/bin/udisksctl" }.Any(h.Exists))
            rep.Ejected = h.Run("udisksctl", ["power-off", "-b", d.Path]).Exit == 0;
        rep.Outcome = "ok";
        rep.Message = $"Stikket er klar: {rep.Files} filer kopieret og kontrolleret." + (rep.Ejected ? " Det er skubbet ud og kan tages ud." : " Tag det ud.");
        log(rep.Message);
        return rep;
    }
}
