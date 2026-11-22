using System.Buffers.Binary;
using System.Text;

namespace Fleetdeck.MacPack;

/// <summary>
/// Skriver et skrivebeskyttet HFS+-diskbillede (HFSX: binær navnesammenligning) uden Mac og uden root:
/// mapper, filer (med rettigheder) og symlinks – nok til et installations-disk-image (Fleetdeck.app + genvej til Programmer).
/// Ingen journal, ingen udvidede attributter, hver fil ligger samlet i ét stykke (ingen overløbs-extents).
/// Kan tjekkes på Linux med fsck.hfsplus (hfsprogs) og 7z.
/// </summary>
public sealed class HfsPlus
{
    public const int BlockSize = 4096;
    private const int CatalogNodeSize = 8192, ExtentsNodeSize = 4096;
    private const uint RootParentId = 1, RootFolderId = 2, FirstUserId = 16;
    private static readonly DateTime Epoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public abstract class Entry
    {
        public required string Name { get; init; }
        public uint Mode { get; init; }
        public ushort FinderFlags { get; set; }
        internal uint Id;
        internal Folder? Parent;
    }
    public sealed class Folder : Entry { public List<Entry> Children { get; } = []; }
    public sealed class FileEntry : Entry
    {
        public string? SourcePath { get; init; }
        public byte[]? Data { get; init; }
        public uint FileType { get; init; }
        public uint Creator { get; init; }
        internal long Length => Data?.LongLength ?? new FileInfo(SourcePath!).Length;
        internal uint StartBlock, Blocks;
    }

    public const ushort FinderInvisible = 0x4000, FinderHasCustomIcon = 0x0400;
    private const uint SIfDir = 0x4000, SIfReg = 0x8000, SIfLnk = 0xA000;

    public string VolumeName { get; }
    public Folder Root { get; }
    public DateTime Created { get; set; } = DateTime.UtcNow;
    /// <summary>Ekstra ledig plads i billedet (det er skrivebeskyttet; lidt plads gør det pænere i Finder).</summary>
    public int SpareBlocks { get; set; } = 64;

    public HfsPlus(string volumeName)
    {
        VolumeName = volumeName;
        Root = new Folder { Name = volumeName, Mode = SIfDir | 0x1ED };
    }

    public Folder AddFolder(Folder parent, string name, uint perm = 0x1ED) => Add(parent, new Folder { Name = name, Mode = SIfDir | (perm & 0xFFF) });
    public FileEntry AddFile(Folder parent, string name, byte[] data, uint perm = 0x1A4) => Add(parent, new FileEntry { Name = name, Data = data, Mode = SIfReg | (perm & 0xFFF) });
    public FileEntry AddFile(Folder parent, string name, string source, uint perm) => Add(parent, new FileEntry { Name = name, SourcePath = source, Mode = SIfReg | (perm & 0xFFF) });
    /// <summary>Symlink som macOS laver dem: filtype 'slnk', skaber 'rhap', målet som indhold.</summary>
    public FileEntry AddSymlink(Folder parent, string name, string target) =>
        Add(parent, new FileEntry { Name = name, Data = Encoding.UTF8.GetBytes(target), Mode = SIfLnk | 0x1ED, FileType = 0x736C6E6B, Creator = 0x72686170 });

    private T Add<T>(Folder parent, T e) where T : Entry
    {
        if (e.Name.Length is 0 or > 255 || e.Name.Contains(':') || e.Name.Contains('/')) throw new ArgumentException($"Ugyldigt navn: {e.Name}");
        if (parent.Children.Any(c => c.Name == e.Name)) throw new ArgumentException($"Findes allerede: {e.Name}");
        e.Parent = parent; parent.Children.Add(e); return e;
    }

    /// <summary>Kopiér en mappe fra disken (fx Fleetdeck.app) med rettigheder og symlinks.</summary>
    public Folder AddTree(Folder parent, string name, string sourceDir)
    {
        var f = AddFolder(parent, name, Perm(sourceDir, 0x1ED));
        foreach (var d in Directory.GetDirectories(sourceDir).Order(StringComparer.Ordinal))
        {
            var di = new DirectoryInfo(d);
            if (di.LinkTarget is { } lt) AddSymlink(f, di.Name, lt);
            else AddTree(f, di.Name, d);
        }
        foreach (var file in Directory.GetFiles(sourceDir).Order(StringComparer.Ordinal))
        {
            var fi = new FileInfo(file);
            if (fi.LinkTarget is { } lt) AddSymlink(f, fi.Name, lt);
            else AddFile(f, fi.Name, file, Perm(file, 0x1A4));
        }
        return f;
    }

    private static uint Perm(string path, uint fallback) =>
        OperatingSystem.IsWindows() ? fallback : (uint)File.GetUnixFileMode(path) & 0xFFF;

    // ------------------------------------------------------------------ skrivning

    public void Write(string path)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite);
        Write(fs);
    }

    public void Write(Stream output)
    {
        // 1) id'er (roden = 2, resten fra 16) i fast rækkefølge
        var all = new List<Entry>();
        var next = FirstUserId;
        Root.Id = RootFolderId;
        void Walk(Folder f)
        {
            foreach (var c in f.Children.OrderBy(c => Hfs(c.Name), Utf16Comparer.Instance))
            {
                c.Id = next++; all.Add(c);
                if (c is Folder sub) Walk(sub);
            }
        }
        Walk(Root);
        var files = all.OfType<FileEntry>().ToList();
        var folders = all.OfType<Folder>().ToList();

        // 2) katalogets poster: hver mappe og fil har en post + en "thread"-post (id → forælder og navn)
        var date = HfsDate(Created);
        var records = new List<(byte[] Key, byte[] Data)>
        {
            (CatalogKey(RootParentId, VolumeName), FolderRecord(Root, date)),
            (CatalogKey(RootFolderId, ""), ThreadRecord(3, RootParentId, VolumeName)),
        };
        foreach (var e in all)
        {
            records.Add((CatalogKey(e.Parent!.Id, e.Name), e is Folder fo ? FolderRecord(fo, date) : FileRecord((FileEntry)e, date)));
            records.Add((CatalogKey(e.Id, ""), ThreadRecord(e is Folder ? (short)3 : (short)4, e.Parent.Id, e.Name)));
        }
        records.Sort((a, b) => CompareKeys(a.Key, b.Key));

        var catalog = BTree.Build(records, CatalogNodeSize, maxKeyLength: 516, keyCompareType: 0xBC, attributes: 6, spareNodes: 8);
        var extents = BTree.Build([], ExtentsNodeSize, maxKeyLength: 10, keyCompareType: 0, attributes: 2, spareNodes: 3);

        // 3) placering: blok 0 (opstartsblokke + volume header), bitmap, extents, katalog, filernes data, ledig plads, sidste blok (kopi af header)
        var extBlocks = (uint)(extents.Length / BlockSize);
        var catBlocks = (uint)(catalog.Length / BlockSize);
        var dataBlocks = (uint)files.Sum(f => (f.Length + BlockSize - 1) / BlockSize);
        uint bitmapBlocks = 1, total;
        while (true)
        {
            total = 1 + bitmapBlocks + extBlocks + catBlocks + dataBlocks + (uint)SpareBlocks + 1;
            var need = (uint)((total + 8 * BlockSize - 1) / (8 * BlockSize));
            if (need <= bitmapBlocks) break;
            bitmapBlocks = need;
        }
        uint pos = 1;
        var bitmapStart = pos; pos += bitmapBlocks;
        var extStart = pos; pos += extBlocks;
        var catStart = pos; pos += catBlocks;
        foreach (var f in files)
        {
            f.Blocks = (uint)((f.Length + BlockSize - 1) / BlockSize);
            f.StartBlock = f.Blocks > 0 ? pos : 0;
            pos += f.Blocks;
        }
        var used = pos;   // blok 0 … used-1 er i brug, plus den sidste
        // katalogets filposter kender først nu deres blokke: byg det igen (samme størrelse)
        records = records.Select(r => (r.Key, ReplaceFork(r.Data, files))).ToList();
        catalog = BTree.Build(records, CatalogNodeSize, maxKeyLength: 516, keyCompareType: 0xBC, attributes: 6, spareNodes: 8);
        if (catalog.Length / BlockSize != catBlocks) throw new InvalidOperationException("Kataloget ændrede størrelse");

        output.SetLength((long)total * BlockSize);
        void At(long block, byte[] data) { output.Position = block * BlockSize; output.Write(data); }

        // bitmap
        var bitmap = new byte[bitmapBlocks * BlockSize];
        void Mark(uint b) => bitmap[b >> 3] |= (byte)(0x80 >> (int)(b & 7));
        for (uint b = 0; b < used; b++) Mark(b);
        Mark(total - 1);
        At(bitmapStart, bitmap);
        At(extStart, extents);
        At(catStart, catalog);
        var buf = new byte[1 << 20];
        foreach (var f in files.Where(f => f.Blocks > 0))
        {
            output.Position = (long)f.StartBlock * BlockSize;
            if (f.Data is not null) output.Write(f.Data);
            else
            {
                using var src = File.OpenRead(f.SourcePath!);
                int n; while ((n = src.Read(buf)) > 0) output.Write(buf, 0, n);
            }
        }

        // volume header (og kopien 1024 byte før slutningen)
        var vh = new byte[512];
        var v = vh.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(v, 0x4858);          // 'HX'
        BinaryPrimitives.WriteUInt16BigEndian(v[2..], 5);           // HFSX
        BinaryPrimitives.WriteUInt32BigEndian(v[4..], 0x100);       // pænt afmonteret
        BinaryPrimitives.WriteUInt32BigEndian(v[8..], 0x31302E30);  // '10.0'
        BinaryPrimitives.WriteUInt32BigEndian(v[16..], date);       // createDate
        BinaryPrimitives.WriteUInt32BigEndian(v[20..], date);       // modifyDate
        BinaryPrimitives.WriteUInt32BigEndian(v[28..], date);       // checkedDate
        BinaryPrimitives.WriteUInt32BigEndian(v[32..], (uint)files.Count);
        BinaryPrimitives.WriteUInt32BigEndian(v[36..], (uint)folders.Count);
        BinaryPrimitives.WriteUInt32BigEndian(v[40..], BlockSize);
        BinaryPrimitives.WriteUInt32BigEndian(v[44..], total);
        BinaryPrimitives.WriteUInt32BigEndian(v[48..], total - used - 1);
        BinaryPrimitives.WriteUInt32BigEndian(v[52..], used);       // nextAllocation
        BinaryPrimitives.WriteUInt32BigEndian(v[56..], 65536);
        BinaryPrimitives.WriteUInt32BigEndian(v[60..], 65536);
        BinaryPrimitives.WriteUInt32BigEndian(v[64..], next);       // nextCatalogID
        BinaryPrimitives.WriteUInt32BigEndian(v[68..], 1);          // writeCount
        BinaryPrimitives.WriteUInt64BigEndian(v[72..], 1);          // encodingsBitmap: MacRoman
        // finderInfo[6..7]: volumens 64-bit id
        var uuid = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes($"{VolumeName}|{date}|{total}"));
        uuid.AsSpan(0, 8).CopyTo(v[(80 + 24)..]);
        Fork(v[112..], bitmapStart, bitmapBlocks, (ulong)bitmapBlocks * BlockSize);
        Fork(v[192..], extStart, extBlocks, (ulong)extents.Length);
        Fork(v[272..], catStart, catBlocks, (ulong)catalog.Length);
        output.Position = 1024; output.Write(vh);
        output.Position = (long)total * BlockSize - 1024; output.Write(vh);
        output.Flush();
    }

    private static void Fork(Span<byte> f, uint start, uint blocks, ulong logical)
    {
        BinaryPrimitives.WriteUInt64BigEndian(f, logical);
        BinaryPrimitives.WriteUInt32BigEndian(f[8..], 0);      // clumpSize
        BinaryPrimitives.WriteUInt32BigEndian(f[12..], blocks);
        if (blocks == 0) return;
        BinaryPrimitives.WriteUInt32BigEndian(f[16..], start);
        BinaryPrimitives.WriteUInt32BigEndian(f[20..], blocks);
    }

    /// <summary>HFS+ gemmer navne dekomponeret (NFD) i UTF-16.</summary>
    internal static string Hfs(string name) => name.Normalize(NormalizationForm.FormD);

    internal static uint HfsDate(DateTime utc) => (uint)Math.Clamp((utc.ToUniversalTime() - Epoch).TotalSeconds, 0, uint.MaxValue);

    internal static byte[] CatalogKey(uint parent, string name)
    {
        var n = Hfs(name);
        var k = new byte[2 + 6 + 2 * n.Length];
        BinaryPrimitives.WriteUInt16BigEndian(k, (ushort)(6 + 2 * n.Length));
        BinaryPrimitives.WriteUInt32BigEndian(k.AsSpan(2), parent);
        BinaryPrimitives.WriteUInt16BigEndian(k.AsSpan(6), (ushort)n.Length);
        for (var i = 0; i < n.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(k.AsSpan(8 + 2 * i), n[i]);
        return k;
    }

    /// <summary>Katalognøgler: forælder-id, derefter navnet tegn for tegn (HFSX binær sammenligning, kortest først).</summary>
    internal static int CompareKeys(byte[] a, byte[] b)
    {
        var pa = BinaryPrimitives.ReadUInt32BigEndian(a.AsSpan(2)); var pb = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(2));
        if (pa != pb) return pa.CompareTo(pb);
        int la = BinaryPrimitives.ReadUInt16BigEndian(a.AsSpan(6)), lb = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(6));
        for (var i = 0; i < Math.Min(la, lb); i++)
        {
            var ca = BinaryPrimitives.ReadUInt16BigEndian(a.AsSpan(8 + 2 * i)); var cb = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(8 + 2 * i));
            if (ca != cb) return ca.CompareTo(cb);
        }
        return la.CompareTo(lb);
    }

    private sealed class Utf16Comparer : IComparer<string>
    {
        public static readonly Utf16Comparer Instance = new();
        public int Compare(string? x, string? y) => string.CompareOrdinal(x, y);
    }

    private static void Bsd(Span<byte> p, uint mode)
    {
        BinaryPrimitives.WriteUInt32BigEndian(p, 0);       // ejer: root (macOS ignorerer ejere på disk-images)
        BinaryPrimitives.WriteUInt32BigEndian(p[4..], 80);  // gruppe: admin
        BinaryPrimitives.WriteUInt16BigEndian(p[10..], (ushort)mode);
    }

    private static void Dates(Span<byte> d, uint date) { for (var i = 0; i < 4; i++) BinaryPrimitives.WriteUInt32BigEndian(d[(i * 4)..], date); }

    private static byte[] FolderRecord(Folder f, uint date)
    {
        var r = new byte[88];
        var s = r.AsSpan();
        BinaryPrimitives.WriteInt16BigEndian(s, 1);
        BinaryPrimitives.WriteUInt32BigEndian(s[4..], (uint)f.Children.Count);
        BinaryPrimitives.WriteUInt32BigEndian(s[8..], f.Id);
        Dates(s[12..], date);                  // create, contentMod, attributeMod, access (backup = 0)
        Bsd(s[32..], f.Mode);
        BinaryPrimitives.WriteUInt16BigEndian(s[56..], f.FinderFlags);   // userInfo.frFlags
        return r;
    }

    private static byte[] FileRecord(FileEntry f, uint date)
    {
        var r = new byte[248];
        var s = r.AsSpan();
        BinaryPrimitives.WriteInt16BigEndian(s, 2);
        BinaryPrimitives.WriteUInt16BigEndian(s[2..], 0x0002);   // kHFSThreadExistsMask
        BinaryPrimitives.WriteUInt32BigEndian(s[8..], f.Id);
        Dates(s[12..], date);
        Bsd(s[32..], f.Mode);
        BinaryPrimitives.WriteUInt32BigEndian(s[48..], f.FileType);
        BinaryPrimitives.WriteUInt32BigEndian(s[52..], f.Creator);
        BinaryPrimitives.WriteUInt16BigEndian(s[56..], f.FinderFlags);
        Fork(s[88..], f.StartBlock, f.Blocks, (ulong)f.Length);   // data fork; ressource fork (168..) tom
        return r;
    }

    private static byte[] ReplaceFork(byte[] record, List<FileEntry> files)
    {
        if (BinaryPrimitives.ReadInt16BigEndian(record) != 2) return record;
        var id = BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(8));
        var f = files.First(x => x.Id == id);
        var copy = (byte[])record.Clone();
        Fork(copy.AsSpan(88), f.StartBlock, f.Blocks, (ulong)f.Length);
        return copy;
    }

    private static byte[] ThreadRecord(short type, uint parent, string name)
    {
        var n = Hfs(name);
        var r = new byte[10 + 2 * n.Length];
        BinaryPrimitives.WriteInt16BigEndian(r, type);
        BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(4), parent);
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(8), (ushort)n.Length);
        for (var i = 0; i < n.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(10 + 2 * i), n[i]);
        return r;
    }

    /// <summary>B-træ (katalog/extents): header-node 0, blade 1…n, derefter indeks-niveauerne. Returnerer hele filen.</summary>
    internal static class BTree
    {
        public static byte[] Build(List<(byte[] Key, byte[] Data)> records, int nodeSize, ushort maxKeyLength, byte keyCompareType, uint attributes, int spareNodes)
        {
            var nodes = new List<byte[]> { new byte[nodeSize] };   // 0 = header
            var level = new List<(byte[] FirstKey, int Node)>();
            // blade
            var leaves = Pack(records.Select(r => Concat(r.Key, r.Data)).ToList(), records.Select(r => r.Key).ToList(), nodeSize);
            var firstLeaf = nodes.Count;
            foreach (var (recs, first) in leaves) { level.Add((first, nodes.Count)); nodes.Add(Node(nodeSize, -1, 1, recs)); }
            Link(nodes, firstLeaf, leaves.Count);
            var depth = leaves.Count > 0 ? 1 : 0;
            var root = leaves.Count > 0 ? firstLeaf : 0;
            // indeks-niveauer, til der er én node tilbage
            while (level.Count > 1)
            {
                depth++;
                var entries = level.Select(l => { var p = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(p, (uint)l.Node); return Concat(l.FirstKey, p); }).ToList();
                var packed = Pack(entries, level.Select(l => l.FirstKey).ToList(), nodeSize);
                var start = nodes.Count;
                var up = new List<(byte[], int)>();
                foreach (var (recs, first) in packed) { up.Add((first, nodes.Count)); nodes.Add(Node(nodeSize, 0, (byte)depth, recs)); }
                Link(nodes, start, packed.Count);
                level = up;
                root = up[0].Item2;
            }
            var used = nodes.Count;
            var total = used + spareNodes;
            var mapBytes = nodeSize - 14 - 106 - 128 - 8;
            if (total > mapBytes * 8) throw new InvalidOperationException("For mange noder til header-nodens kort");
            // header-node
            var h = nodes[0].AsSpan();
            h[8] = 1;   // kind = header
            BinaryPrimitives.WriteUInt16BigEndian(h[10..], 3);
            var hr = h[14..];
            BinaryPrimitives.WriteUInt16BigEndian(hr, (ushort)depth);
            BinaryPrimitives.WriteUInt32BigEndian(hr[2..], (uint)root);
            BinaryPrimitives.WriteUInt32BigEndian(hr[6..], (uint)records.Count);
            BinaryPrimitives.WriteUInt32BigEndian(hr[10..], (uint)(leaves.Count > 0 ? firstLeaf : 0));
            BinaryPrimitives.WriteUInt32BigEndian(hr[14..], (uint)(leaves.Count > 0 ? firstLeaf + leaves.Count - 1 : 0));
            BinaryPrimitives.WriteUInt16BigEndian(hr[18..], (ushort)nodeSize);
            BinaryPrimitives.WriteUInt16BigEndian(hr[20..], maxKeyLength);
            BinaryPrimitives.WriteUInt32BigEndian(hr[22..], (uint)total);
            BinaryPrimitives.WriteUInt32BigEndian(hr[26..], (uint)(total - used));
            BinaryPrimitives.WriteUInt32BigEndian(hr[32..], (uint)nodeSize);   // clumpSize
            hr[36] = 0;                // btreeType
            hr[37] = keyCompareType;
            BinaryPrimitives.WriteUInt32BigEndian(hr[38..], attributes);
            var map = h[(14 + 106 + 128)..];
            for (var i = 0; i < used; i++) map[i >> 3] |= (byte)(0x80 >> (i & 7));
            BinaryPrimitives.WriteUInt16BigEndian(h[(nodeSize - 2)..], 14);
            BinaryPrimitives.WriteUInt16BigEndian(h[(nodeSize - 4)..], 14 + 106);
            BinaryPrimitives.WriteUInt16BigEndian(h[(nodeSize - 6)..], 14 + 106 + 128);
            BinaryPrimitives.WriteUInt16BigEndian(h[(nodeSize - 8)..], (ushort)(nodeSize - 8));
            var file = new byte[total * nodeSize];
            for (var i = 0; i < nodes.Count; i++) nodes[i].CopyTo(file, i * nodeSize);
            return file;
        }

        private static byte[] Concat(byte[] a, byte[] b) { var r = new byte[a.Length + b.Length]; a.CopyTo(r, 0); b.CopyTo(r, a.Length); return r; }

        /// <summary>Fyld noder i rækkefølge: descriptor (14) + poster + offset-tabel (2 byte pr. post + 1).</summary>
        private static List<(List<byte[]> Recs, byte[] FirstKey)> Pack(List<byte[]> recs, List<byte[]> keys, int nodeSize)
        {
            var result = new List<(List<byte[]>, byte[])>();
            List<byte[]>? cur = null; var used = 0;
            for (var i = 0; i < recs.Count; i++)
            {
                var r = recs[i];
                if (14 + r.Length + 4 > nodeSize) throw new InvalidOperationException("Post for stor til en node");
                if (cur is null || used + r.Length + 2 * (cur.Count + 2) > nodeSize - 14)
                {
                    cur = []; used = 0; result.Add((cur, keys[i]));
                }
                cur.Add(r); used += r.Length;
            }
            return result;
        }

        private static byte[] Node(int nodeSize, sbyte kind, byte height, List<byte[]> recs)
        {
            var n = new byte[nodeSize];
            n[8] = (byte)kind; n[9] = height;
            BinaryPrimitives.WriteUInt16BigEndian(n.AsSpan(10), (ushort)recs.Count);
            var off = 14;
            for (var i = 0; i < recs.Count; i++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(n.AsSpan(nodeSize - 2 * (i + 1)), (ushort)off);
                recs[i].CopyTo(n, off); off += recs[i].Length;
            }
            BinaryPrimitives.WriteUInt16BigEndian(n.AsSpan(nodeSize - 2 * (recs.Count + 1)), (ushort)off);   // start på ledig plads
            return n;
        }

        private static void Link(List<byte[]> nodes, int start, int count)
        {
            for (var i = 0; i < count; i++)
            {
                var n = nodes[start + i];
                BinaryPrimitives.WriteUInt32BigEndian(n, (uint)(i + 1 < count ? start + i + 1 : 0));   // fLink
                BinaryPrimitives.WriteUInt32BigEndian(n.AsSpan(4), (uint)(i > 0 ? start + i - 1 : 0)); // bLink
            }
        }
    }
}
