using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fleetdeck.Shared;

// Reservekort på en Raspberry Pi: en natlig kopi af hele systemet til et SD-kort i en USB-kortlæser, så et dødt kort kan
// skiftes på få minutter. Alt her er ren logik plus selve kopieringen, der kun taler med maskinen gennem ISpareHost –
// agenten giver den rigtige maskine, testene en falsk med filer i en midlertidig mappe.
// Sikkerhed: kortet, Pi'en kører fra, og alt der hedder mmcblk*, skrives der aldrig til. Kun den USB-enhed, brugeren har
// valgt og bekræftet i Fleetdeck (ud fra /dev/disk/by-id), bliver slettet og skrevet.

/// <summary>En USB-blokenhed, Pi'en kan se, og som kan bruges som reservekort.</summary>
public sealed class SpareDisk
{
    /// <summary>Navnet i /dev/disk/by-id (fx usb-Generic_STORAGE_DEVICE_000000000819-0:0) – følger læseren og kortet, ikke sdX.</summary>
    public string Id { get; set; } = "";
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string Model { get; set; } = "";
    public string Vendor { get; set; } = "";
    public string Serial { get; set; } = "";
    public int Partitions { get; set; }
    /// <summary>Monteret et sted (fx automatisk af skrivebordet) – så kan den ikke bruges, før den er afmonteret.</summary>
    public bool Mounted { get; set; }
    /// <summary>Kortlæser uden kort (størrelse 0).</summary>
    public bool Empty => Size <= 0;
}

/// <summary>Jobbets indstillinger fra serveren (JobParams.Spare).</summary>
public sealed class SpareCardJob
{
    /// <summary>Den valgte enhed i /dev/disk/by-id.</summary>
    public string DiskId { get; set; } = "";
    /// <summary>Serienummeret, brugeren bekræftede – skal stadig passe, ellers skrives der intet.</summary>
    public string? Serial { get; set; }
    /// <summary>Kortets disk-id (PTUUID) efter sidste klargøring. Passer det, kopieres partitionstabellen ikke igen.</summary>
    public string? KnownDiskId { get; set; }
    /// <summary>Send kerne, opstartsfiler og moduler til Fleetdeck, så netboot-nødsystemet passer til Pi'en.</summary>
    public bool Netboot { get; set; }
    /// <summary>Nødsystemets nuværende kerne-signatur; er den den samme, sendes intet.</summary>
    public string? NetbootSignature { get; set; }
}

/// <summary>Det, agenten melder tilbage efter en kopi (POST /api/agent/sparecard).</summary>
public sealed class SpareCardReport
{
    public int JobId { get; set; }
    /// <summary>ok | advarsel | fejl</summary>
    public string Status { get; set; } = "";
    public string Message { get; set; } = "";
    /// <summary>Kortets disk-id efter kopien (bruges næste nat til at springe klargøringen over).</summary>
    public string? DiskId { get; set; }
    public bool Initialized { get; set; }
    public string? Kernel { get; set; }
    public long Bytes { get; set; }
    public int Missing { get; set; }
    public bool FsckOk { get; set; }
    public double Minutes { get; set; }
    /// <summary>Kerne-signaturen, hvis agenten har lagt en netboot-pakke klar (/api/agent/sparecard/netboot).</summary>
    public string? NetbootSignature { get; set; }
}

public sealed record SpareCmd(int Exit, string Output);

/// <summary>Alt, kopieringen gør på maskinen. Kommandoer køres uden skal (hvert argument for sig), så intet kan blive til en kommando.</summary>
public interface ISpareHost
{
    SpareCmd Run(string file, IReadOnlyList<string> args, string? stdin = null, int timeoutMin = 30);
    string? ReadText(string path);
    void WriteText(string path, string text);
    bool Exists(string path);
    void MakeDir(string path);
    /// <summary>Hvad et link peger på (realpath), fx /dev/disk/by-id/usb-… → /dev/sda.</summary>
    string? RealPath(string path);
    string[] ListDir(string path);
    /// <summary>Alle stier under <paramref name="root"/> på samme filsystem (find -xdev), relativt til roden.</summary>
    List<string> ListTree(string root);
}

/// <summary>En disk eller partition fra lsblk -J.</summary>
public sealed record BlockDev(string Name, string Path, string Type, long Size, string Tran, string Serial, string Model, string Vendor,
    string PtUuid, string PtType, string PartUuid, string FsType, string Label, List<string> Mounts, List<BlockDev> Children,
    string DiskSeq = "");

public static partial class SpareCard
{
    /// <summary>Arbejdsmappen (kortet monteres her). Root-ejet under /var/lib, aldrig /var/tmp, hvor en bruger kan lave mappen eller en genvej først.</summary>
    public const string Work = UnixRoot.Base + "/spare";
    public const string Marker = "fleetdeck-reservekort.txt";
    public const double StaleHours = 48;
    public const string LsblkColumns = "NAME,PATH,TYPE,SIZE,TRAN,SERIAL,MODEL,VENDOR,PTUUID,PTTYPE,PARTUUID,FSTYPE,LABEL,MOUNTPOINTS";

    [GeneratedRegex(@"^[A-Za-z0-9._:+@\-]{3,200}$")] private static partial Regex IdRx();
    /// <summary>Et navn fra /dev/disk/by-id: ingen skråstreg, ingen "..", ingen mellemrum.</summary>
    public static bool SafeId(string? id) => id is not null && IdRx().IsMatch(id) && !id.Contains("..");

    /// <summary>mmcblk* (Pi'ens eget kortstik) må aldrig bruges som reservekort.</summary>
    public static bool IsInternalCard(string? path) => path is not null && System.IO.Path.GetFileName(path).StartsWith("mmcblk", StringComparison.Ordinal);

    // ------------------------------------------------------------------ lsblk

    public static List<BlockDev> ParseLsblk(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("blockdevices", out var a) ? a.EnumerateArray().Select(Dev).ToList() : [];
        static string S(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";
        static long L(JsonElement e, string k) => e.TryGetProperty(k, out var v) ? v.ValueKind switch
        {
            JsonValueKind.Number => v.GetInt64(),
            JsonValueKind.String when long.TryParse(v.GetString(), out var n) => n,
            _ => 0,
        } : 0;
        static BlockDev Dev(JsonElement e)
        {
            var mounts = new List<string>();
            if (e.TryGetProperty("mountpoints", out var mp) && mp.ValueKind == JsonValueKind.Array)
                mounts.AddRange(mp.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));
            else if (S(e, "mountpoint") is { Length: > 0 } one) mounts.Add(one);
            var kids = e.TryGetProperty("children", out var c) && c.ValueKind == JsonValueKind.Array ? c.EnumerateArray().Select(Dev).ToList() : [];
            var name = S(e, "name");
            return new BlockDev(name, S(e, "path") is { Length: > 0 } p ? p : "/dev/" + name, S(e, "type"), L(e, "size"), S(e, "tran"), S(e, "serial"),
                S(e, "model"), S(e, "vendor"), S(e, "ptuuid"), S(e, "pttype"), S(e, "partuuid"), S(e, "fstype"), S(e, "label"), mounts, kids);
        }
    }

    /// <summary>Disken, som "/" ligger på (eller null, hvis roden er over netværket, fx netboot).</summary>
    public static BlockDev? RootDisk(List<BlockDev> devs) =>
        devs.FirstOrDefault(d => d.Type == "disk" && (d.Mounts.Contains("/") || d.Children.Any(c => c.Mounts.Contains("/"))));

    /// <summary>USB-diske, der kan bruges som reservekort, med deres navn i /dev/disk/by-id.</summary>
    /// <param name="byId">by-id-navn → den enhed, linket peger på (fx usb-…-0:0 → /dev/sda).</param>
    public static List<SpareDisk> Candidates(List<BlockDev> devs, IReadOnlyDictionary<string, string> byId)
    {
        var root = RootDisk(devs);
        var o = new List<SpareDisk>();
        foreach (var d in devs.Where(d => d.Type == "disk" && d.Tran == "usb" && !IsInternalCard(d.Path) && d.Path != root?.Path))
        {
            // det mest sigende navn: usb-… frem for wwn-…, aldrig et -partN-link
            var id = byId.Where(kv => kv.Value == d.Path && !Regex.IsMatch(kv.Key, @"-part\d+$") && SafeId(kv.Key))
                .Select(kv => kv.Key).OrderBy(k => k.StartsWith("usb-", StringComparison.Ordinal) ? 0 : 1).ThenBy(k => k, StringComparer.Ordinal).FirstOrDefault();
            if (id is null) continue;
            o.Add(new SpareDisk
            {
                Id = id, Path = d.Path, Size = d.Size, Model = d.Model, Vendor = d.Vendor, Serial = d.Serial, Partitions = d.Children.Count,
                Mounted = d.Mounts.Count > 0 || d.Children.Any(c => c.Mounts.Count > 0),
            });
        }
        return o;
    }

    public static string Label(SpareDisk d)
    {
        var name = string.Join(' ', new[] { d.Vendor, d.Model }.Where(x => x.Length > 0)).Trim();
        return $"{(name.Length > 0 ? name : "USB-enhed")} · {(d.Empty ? "intet kort" : Size(d.Size))}";
    }

    public static string Size(long bytes) => bytes >= 1_000_000_000 ? $"{bytes / 1e9:0.#} GB" : $"{bytes / 1e6:0} MB";

    // ------------------------------------------------------------------ partitionstabel

    /// <summary>Lav et nyt disk-id til et MBR-kort (8 hex-tegn), der ikke er det samme som systemkortets.</summary>
    public static string NewDiskId(string? notThis = null)
    {
        while (true)
        {
            var id = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4));
            if (id != "00000000" && !string.Equals(id, Hex(notThis), StringComparison.OrdinalIgnoreCase)) return id;
        }
    }
    private static string Hex(string? id) => (id ?? "").Trim().ToLowerInvariant().Replace("0x", "");

    /// <summary>
    /// Systemkortets partitionstabel (sfdisk --dump) skrevet om til reservekortet: nyt disk-id (så PARTUUID ikke er det samme som
    /// systemkortets – ellers ved Pi'en ikke, hvilket kort den skal starte fra), ingen enhedsnavne, og sidste partition fylder resten
    /// af kortet. GPT: disk- og partitions-id'er laves nye af sfdisk.
    /// </summary>
    public static string PartitionTable(string dump, string newDiskId)
    {
        var lines = dump.Replace("\r", "").Split('\n');
        var gpt = lines.Any(l => Regex.IsMatch(l.Trim(), @"^label:\s*gpt$"));
        var head = new List<string>();
        var parts = new List<string>();
        foreach (var raw in lines)
        {
            var l = raw.Trim();
            if (l.Length == 0) continue;
            if (l.Contains("start=", StringComparison.Ordinal))
            {
                // "/dev/mmcblk0p1 : start= 8192, size= 1048576, type=c" → "start=8192, size=1048576, type=c"
                var body = l.Contains(" : ") ? l[(l.IndexOf(" : ", StringComparison.Ordinal) + 3)..] : l;
                var fields = body.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Select(f => Regex.Replace(f, @"\s*=\s*", "="))
                    .Where(f => !(gpt && f.StartsWith("uuid=", StringComparison.Ordinal)))
                    .ToList();
                parts.Add(string.Join(", ", fields));
                continue;
            }
            var key = l.Split(':', 2)[0].Trim();
            switch (key)
            {
                case "device" or "last-lba": continue;   // enhedsnavnet og GPT-slutningen hører til systemkortet
                case "label-id": if (!gpt) head.Add($"label-id: 0x{Hex(newDiskId)}"); continue;
                default: head.Add(l); break;
            }
        }
        if (parts.Count == 0) throw new InvalidOperationException("Ingen partitioner i systemkortets partitionstabel");
        if (!gpt && !head.Any(h => h.StartsWith("label-id:", StringComparison.Ordinal))) head.Add($"label-id: 0x{Hex(newDiskId)}");
        // sidste partition fylder resten af kortet (så et større kort kan bruges fuldt ud, og et lidt mindre stadig kan bruges)
        parts[^1] = string.Join(", ", parts[^1].Split(", ").Where(f => !f.StartsWith("size=", StringComparison.Ordinal)));
        return string.Join('\n', head) + "\n\n" + string.Join('\n', parts) + "\n";
    }

    // ------------------------------------------------------------------ cmdline.txt og fstab

    /// <summary>Reservekortets cmdline.txt: root= peger på kortets egen rodpartition; netboot-flag (nfsroot=, ip= til NFS) fjernes.</summary>
    public static string FixCmdline(string cmdline, string rootPartUuid)
    {
        var words = cmdline.Replace("\r", "").Replace("\n", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var nfs = words.Any(w => w is "root=/dev/nfs" || w.StartsWith("nfsroot=", StringComparison.Ordinal));
        var o = new List<string>();
        var hadRoot = false;
        foreach (var w in words)
        {
            if (w.StartsWith("root=", StringComparison.Ordinal)) { if (!hadRoot) o.Add($"root=PARTUUID={rootPartUuid}"); hadRoot = true; continue; }
            if (w.StartsWith("nfsroot=", StringComparison.Ordinal) || w == "nfsrootdebug") continue;
            if (nfs && (w.StartsWith("ip=", StringComparison.Ordinal) || w == "rootfstype=nfs")) continue;
            if (nfs && w.StartsWith("rootwait", StringComparison.Ordinal)) continue;   // sættes igen nedenfor
            o.Add(w);
        }
        if (!hadRoot) o.Insert(0, $"root=PARTUUID={rootPartUuid}");
        if (nfs || !o.Any(w => w.StartsWith("rootfstype=", StringComparison.Ordinal))) { o.RemoveAll(w => w.StartsWith("rootfstype=", StringComparison.Ordinal)); o.Add("rootfstype=ext4"); }
        if (!o.Contains("rootwait")) o.Add("rootwait");
        return string.Join(' ', o) + "\n";
    }

    /// <summary>
    /// Reservekortets fstab: systemkortets PARTUUID'er og enhedsnavne skiftes til kortets egne; "/" og boot-partitionen peger
    /// altid på kortet (også hvis Pi'en havde roden over NFS). Andre linjer (netværksdrev, tmpfs) bliver stående.
    /// </summary>
    public static string FixFstab(string fstab, IReadOnlyDictionary<string, string> map, string bootMount, string bootPartUuid, string rootPartUuid)
    {
        var o = new List<string>();
        bool root = false, boot = false;
        foreach (var raw in fstab.Replace("\r", "").Split('\n'))
        {
            var l = raw;
            var t = l.Trim();
            if (t.Length == 0 || t.StartsWith('#')) { o.Add(l); continue; }
            var f = Regex.Split(t, @"\s+");
            if (f.Length < 2) { o.Add(l); continue; }
            if (map.TryGetValue(f[0], out var to)) f[0] = to;
            if (f[1] == "/")
            {
                root = true;
                f[0] = $"PARTUUID={rootPartUuid}";
                if (f.Length < 3 || f[2] is "nfs" or "nfs4") f = [f[0], "/", "ext4", "defaults,noatime", "0", "1"];
            }
            else if (f[1] == bootMount) { boot = true; f[0] = $"PARTUUID={bootPartUuid}"; }
            o.Add(string.Join("  ", f));
        }
        while (o.Count > 0 && o[^1].Length == 0) o.RemoveAt(o.Count - 1);
        if (!boot) o.Insert(0, $"PARTUUID={bootPartUuid}  {bootMount}  vfat  defaults  0  2");
        if (!root) o.Insert(0, $"PARTUUID={rootPartUuid}  /  ext4  defaults,noatime  0  1");
        return string.Join('\n', o) + "\n";
    }

    // ------------------------------------------------------------------ kopiering

    /// <summary>Det, der aldrig kopieres (rsync-mønstre fra roden). Mapperne selv kommer med, så kortet kan starte.</summary>
    public static readonly string[] Exclude =
    [
        "/dev/*", "/proc/*", "/sys/*", "/run/*", "/tmp/*", "/mnt/*", "/media/*", "/lost+found",
        Work, UnixRoot.KitWork, UnixRoot.IosWork, "/var/tmp/fleetdeck-spare", "/var/tmp/fleetdeck-kit", "/var/swap", "/swapfile", "/var/cache/apt/archives/*.deb",
    ];

    /// <summary>Samme liste som mapper til sammenligningen (relative; et mønster tæller som hele sin mappe).</summary>
    public static IReadOnlyList<string> TreeExclude(string bootMount) =>
        [.. Exclude.Select(e => e.TrimStart('/')).Select(e => e.Contains('*') ? e[..e.LastIndexOf('/', e.IndexOf('*'))] : e).Distinct(), bootMount.TrimStart('/')];

    /// <summary>Fjern de udeladte mapper (og alt under dem) fra en filliste.</summary>
    public static List<string> FilterTree(IEnumerable<string> paths, IReadOnlyList<string> exclude) =>
        paths.Select(p => p.Trim().TrimStart('/')).Where(p => p.Length > 0 && !exclude.Any(e => p == e || p.StartsWith(e + "/", StringComparison.Ordinal))).ToList();

    public sealed record ListDiff(int Source, int Copy, int Missing, List<string> Samples)
    {
        /// <summary>Godt nok: alt kom med, bortset fra en lille rest, der kan være opstået, mens kopien kørte (logfiler o.l.).</summary>
        public bool Ok => Missing <= Math.Max(25, Source / 1000);
    }

    /// <summary>Sammenlign fillisterne: hvad findes på Pi'en, men ikke på kortet?</summary>
    public static ListDiff CompareLists(IReadOnlyCollection<string> source, IReadOnlyCollection<string> copy)
    {
        var have = copy.ToHashSet(StringComparer.Ordinal);
        var missing = source.Where(s => !have.Contains(s)).ToList();
        return new ListDiff(source.Count, copy.Count, missing.Count, missing.Order(StringComparer.Ordinal).Take(10).ToList());
    }

    /// <summary>Er kopien for gammel (over 48 timer, eller aldrig lavet 48 timer efter, at kortet blev valgt)?</summary>
    public static bool Stale(DateTime? lastOk, DateTime? since, DateTime now) =>
        (lastOk ?? since) is { } t && (now - t).TotalHours > StaleHours;

    /// <summary>Netboot-pakken: kerner, dtb'er, overlays og firmware fra boot-partitionen og modulerne til den kørende kerne.</summary>
    public static List<string> NetbootPaths(string bootMount, IEnumerable<string> bootFiles, string kernel)
    {
        var b = bootMount.TrimStart('/');
        var o = bootFiles.Where(NetbootBootFile).Select(f => $"{b}/{f}").ToList();
        o.Add($"{b}/overlays");
        o.Add($"lib/modules/{kernel}");
        return o;
    }

    /// <summary>De opstartsfiler, der skal passe til kernen. Aldrig config.txt og cmdline.txt – de hører til netboot-opsætningen.</summary>
    public static bool NetbootBootFile(string name) =>
        Regex.IsMatch(name, @"^(kernel[\w\-]*\.img|[\w\-]+\.dtb|start[\w]*\.elf|fixup[\w]*\.dat)$", RegexOptions.IgnoreCase);

    // ------------------------------------------------------------------ selve kopien

    /// <summary>
    /// Den natlige kopi. Kører alt med laveste prioritet og læser kun fra systemkortet. Rækkefølge: tjek enheden → klargør kortet
    /// (kun første gang: partitionstabel og filsystemer) → monter → rsync af rod og boot → ret cmdline.txt og fstab → sammenlign
    /// fillister → afmonter → fsck -n → evt. netboot-pakke. Afmonterer altid igen, også ved fejl.
    /// </summary>
    public static SpareCardReport Run(ISpareHost h, SpareCardJob job, Action<string> log, Func<DateTime>? clock = null)
    {
        var now = clock ?? (() => DateTime.UtcNow);
        var started = now();
        var r = new SpareCardReport();
        SpareCardReport Fail(string msg) { r.Status = "fejl"; r.Message = msg; r.Minutes = Math.Round((now() - started).TotalMinutes, 1); log("Fejl: " + msg); return r; }
        string Out(SpareCmd c) => c.Output.Trim().Split('\n').LastOrDefault()?.Trim() ?? "";

        if (!SafeId(job.DiskId)) return Fail("Intet reservekort valgt i Fleetdeck");
        var target = h.RealPath($"/dev/disk/by-id/{job.DiskId}");
        if (target is null) return Fail($"Kortlæseren findes ikke ({job.DiskId}). Sidder den i Pi'en?");
        if (IsInternalCard(target)) return Fail($"{target} er Pi'ens eget kortstik – det bruges aldrig som reservekort");

        var ls = h.Run("lsblk", ["-J", "-b", "-o", LsblkColumns]);
        if (ls.Exit != 0) return Fail("lsblk fejlede: " + Out(ls));
        var devs = ParseLsblk(ls.Output);
        var src = RootDisk(devs);
        if (src is null) return Fail("Pi'en kører ikke fra sit SD-kort (rodfilsystemet er over netværket). Kopien tages, når den kører fra kortet igen.");
        if (IsSame(src, target)) return Fail("Det valgte kort er det, Pi'en kører fra – det skrives der aldrig til");
        var dst = devs.FirstOrDefault(d => d.Path == target && d.Type == "disk");
        if (dst is null) return Fail($"{target} er ikke en hel disk");
        if (dst.Tran != "usb") return Fail($"{target} sidder ikke på USB");
        if (dst.Size <= 0) return Fail("Der sidder intet kort i kortlæseren");
        if (job.Serial is { Length: > 0 } ser && !string.Equals(ser, dst.Serial, StringComparison.Ordinal))
            return Fail($"Serienummeret passer ikke (forventet {ser}, fundet {(dst.Serial.Length > 0 ? dst.Serial : "intet")}). Vælg kortet igen i Fleetdeck.");

        // systemkortets layout: én boot-partition (vfat) og én rodpartition (ext4)
        var srcRoot = src.Children.FirstOrDefault(c => c.Mounts.Contains("/"));
        var bootMount = h.Exists("/boot/firmware/config.txt") || src.Children.Any(c => c.Mounts.Contains("/boot/firmware")) ? "/boot/firmware" : "/boot";
        var srcBoot = src.Children.FirstOrDefault(c => c.Mounts.Contains(bootMount));
        if (srcRoot is null || srcBoot is null || srcBoot.FsType != "vfat" || srcRoot.FsType != "ext4")
            return Fail($"Kun det almindelige Pi-layout understøttes (boot som vfat på {bootMount} og roden som ext4)");
        var bootIdx = src.Children.IndexOf(srcBoot);
        var rootIdx = src.Children.IndexOf(srcRoot);

        // ikke i brug andre steder (et skrivebord, der selv har monteret kortet, eller en tidligere kopi, der er afbrudt)
        foreach (var m in dst.Children.SelectMany(c => c.Mounts.Select(mp => (c.Path, mp))).Concat(dst.Mounts.Select(mp => (dst.Path, mp))).ToList())
        {
            if (!m.mp.StartsWith(Work, StringComparison.Ordinal)) return Fail($"Kortet er monteret på {m.mp}. Afmonter det først.");
            h.Run("umount", [m.Path]);
        }

        // plads nok? (brugt plads på roden + boot-partitionens start og størrelse, med lidt luft)
        var used = h.Run("df", ["-B1", "--output=used", "/"]);
        if (used.Exit == 0 && long.TryParse(Out(used), out var usedBytes) && usedBytes * 1.05 + srcBoot.Size + 16_000_000 > dst.Size)
            return Fail($"Reservekortet er for lille: {Size(dst.Size)}, Pi'en bruger {Size(usedBytes)}");

        // klargør kortet første gang (eller hvis det er skiftet ud): partitionstabel og filsystemer
        var ready = job.KnownDiskId is { Length: > 0 } known && string.Equals(Hex(known), Hex(dst.PtUuid), StringComparison.OrdinalIgnoreCase)
            && dst.Children.Count == src.Children.Count && dst.Children.ElementAtOrDefault(bootIdx)?.FsType == "vfat" && dst.Children.ElementAtOrDefault(rootIdx)?.FsType == "ext4";
        if (!ready)
        {
            log("Klargør reservekortet (partitionstabel og filsystemer – kun første gang)");
            var dump = h.Run("sfdisk", ["--dump", src.Path]);
            if (dump.Exit != 0) return Fail("Kunne ikke læse systemkortets partitionstabel: " + Out(dump));
            string table;
            try { table = PartitionTable(dump.Output, NewDiskId(src.PtUuid)); }
            catch (InvalidOperationException e) { return Fail(e.Message); }
            var wipe = h.Run("wipefs", ["-a", dst.Path]);
            if (wipe.Exit != 0) return Fail("wipefs fejlede: " + Out(wipe));
            var sf = h.Run("sfdisk", ["--wipe", "always", "--wipe-partitions", "always", dst.Path], table);
            if (sf.Exit != 0) return Fail("Partitionstabellen kunne ikke skrives: " + Out(sf));
            h.Run("udevadm", ["settle"]);
            ls = h.Run("lsblk", ["-J", "-b", "-o", LsblkColumns]);
            dst = ParseLsblk(ls.Output).FirstOrDefault(d => d.Path == target);
            if (dst is null || dst.Children.Count != src.Children.Count) return Fail("Kortets partitioner dukkede ikke op efter klargøringen");
            var fb = h.Run("mkfs.vfat", ["-F", "32", "-n", srcBoot.Label is { Length: > 0 } bl ? bl : "bootfs", dst.Children[bootIdx].Path]);
            if (fb.Exit != 0) return Fail("Boot-partitionen kunne ikke formateres: " + Out(fb));
            var fr = h.Run("mkfs.ext4", ["-F", "-q", "-L", srcRoot.Label is { Length: > 0 } rl ? rl : "rootfs", dst.Children[rootIdx].Path], timeoutMin: 60);
            if (fr.Exit != 0) return Fail("Rodpartitionen kunne ikke formateres: " + Out(fr));
            h.Run("udevadm", ["settle"]);
            ls = h.Run("lsblk", ["-J", "-b", "-o", LsblkColumns]);
            dst = ParseLsblk(ls.Output).FirstOrDefault(d => d.Path == target) ?? dst;
            r.Initialized = true;
        }
        var dBoot = dst.Children[bootIdx];
        var dRoot = dst.Children[rootIdx];
        if (dBoot.PartUuid.Length == 0 || dRoot.PartUuid.Length == 0) return Fail("Kortets partitioner har intet PARTUUID");
        if (dRoot.PartUuid == srcRoot.PartUuid) return Fail("Kortet har samme PARTUUID som systemkortet – det ville forvirre Pi'en ved opstart");
        r.DiskId = Hex(dst.PtUuid);

        var mnt = $"{Work}/root";
        var mntBoot = mnt + bootMount;
        var mounted = new List<string>();
        try
        {
            h.MakeDir(mnt);
            var m1 = h.Run("mount", ["-o", "noatime", dRoot.Path, mnt]);
            if (m1.Exit != 0) return Fail("Rodpartitionen kunne ikke monteres: " + Out(m1));
            mounted.Add(mnt);
            h.MakeDir(mntBoot);
            var m2 = h.Run("mount", [dBoot.Path, mntBoot]);
            if (m2.Exit != 0) return Fail("Boot-partitionen kunne ikke monteres: " + Out(m2));
            mounted.Add(mntBoot);

            log("Kopierer rodfilsystemet (rsync, laveste prioritet)");
            var rs = h.Run("nice", ["-n", "19", "ionice", "-c3", "rsync", "-aHAXx", "--numeric-ids", "--delete", .. Exclude.Select(e => $"--exclude={e}"), "/", mnt + "/"], timeoutMin: 240);
            if (rs.Exit is not (0 or 24)) return Fail($"rsync af roden fejlede ({rs.Exit}): {Out(rs)}");   // 24 = filer forsvandt undervejs
            log("Kopierer boot-partitionen");
            var rb = h.Run("nice", ["-n", "19", "ionice", "-c3", "rsync", "-rt", "--modify-window=2", "--delete", $"--filter=P {Marker}", bootMount + "/", mntBoot + "/"], timeoutMin: 30);
            if (rb.Exit is not (0 or 24)) return Fail($"rsync af boot fejlede ({rb.Exit}): {Out(rb)}");

            // kortets egne PARTUUID'er i cmdline.txt og fstab
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < src.Children.Count && i < dst.Children.Count; i++)
            {
                if (src.Children[i].PartUuid.Length > 0) map[$"PARTUUID={src.Children[i].PartUuid}"] = $"PARTUUID={dst.Children[i].PartUuid}";
                map[src.Children[i].Path] = $"PARTUUID={dst.Children[i].PartUuid}";
            }
            var cmdPath = mntBoot + "/cmdline.txt";
            if (h.ReadText(cmdPath) is not { } cmd) return Fail("cmdline.txt mangler på boot-partitionen");
            h.WriteText(cmdPath, FixCmdline(cmd, dRoot.PartUuid));
            h.WriteText(mnt + "/etc/fstab", FixFstab(h.ReadText(mnt + "/etc/fstab") ?? "", map, bootMount, dBoot.PartUuid, dRoot.PartUuid));
            h.WriteText(mntBoot + "/" + Marker, $"Reservekort fra Fleetdeck\nKopieret: {now():yyyy-MM-dd HH:mm} UTC\nRod: PARTUUID={dRoot.PartUuid}\n");

            log("Sammenligner fillisterne");
            var excl = TreeExclude(bootMount);
            var diff = CompareLists(FilterTree(h.ListTree("/"), excl), FilterTree(h.ListTree(mnt), excl));
            r.Missing = diff.Missing;
            if (!diff.Ok) return Fail($"{diff.Missing} filer mangler på kortet, fx {string.Join(", ", diff.Samples.Take(3))}");
            var du = h.Run("df", ["-B1", "--output=used", mnt]);
            if (du.Exit == 0 && long.TryParse(Out(du), out var b)) r.Bytes = b;
            h.Run("sync", []);
        }
        finally
        {
            for (var i = mounted.Count - 1; i >= 0; i--)
                if (h.Run("umount", [mounted[i]]).Exit != 0) h.Run("umount", ["-l", mounted[i]]);
        }

        log("Tjekker filsystemerne (fsck -n)");
        var f1 = h.Run("fsck.vfat", ["-n", dBoot.Path]);
        var f2 = h.Run("e2fsck", ["-n", "-f", dRoot.Path], timeoutMin: 60);
        r.FsckOk = f1.Exit == 0 && f2.Exit == 0;

        var k = h.Run("uname", ["-r"]);
        if (k.Exit == 0) r.Kernel = Out(k);
        r.Status = r.FsckOk ? "ok" : "fejl";   // et kort, fsck ikke kan godkende, er ikke en kopi at stole på
        r.Message = !r.FsckOk ? $"Kopien er lavet, men fsck fandt noget: {(f1.Exit != 0 ? "boot " : "")}{(f2.Exit != 0 ? "rod" : "")}".Trim()
            : r.Missing > 0 ? $"Kopien er lavet. {r.Missing} filer kom ikke med (opstod mens kopien kørte)."
            : $"Kopien er lavet{(r.Initialized ? " (kortet blev klargjort)" : "")}. {Size(r.Bytes)} på kortet.";
        r.Minutes = Math.Round((now() - started).TotalMinutes, 1);
        log(r.Message);
        return r;
    }

    private static bool IsSame(BlockDev disk, string path) => disk.Path == path || disk.Children.Any(c => c.Path == path);

    /// <summary>Kerne-signatur: kernens version og opstartsfilernes navne og størrelser – ændrer sig, når kernen opdateres.</summary>
    public static string NetbootSignature(string kernel, IEnumerable<(string Name, long Size)> bootFiles) =>
        kernel + ":" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('|', bootFiles.Where(f => NetbootBootFile(f.Name)).OrderBy(f => f.Name, StringComparer.Ordinal).Select(f => $"{f.Name}={f.Size}")))))[..16];
}
