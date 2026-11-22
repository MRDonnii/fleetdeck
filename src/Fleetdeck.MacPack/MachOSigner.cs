using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Fleetdeck.MacPack;

/// <summary>
/// Ad hoc-kodesignatur af et 64-bit Mach-O-program (som "codesign -s -"), skrevet i ren C#, så .app-pakken kan signeres på Linux.
/// Signaturen er en SuperBlob med CodeDirectory (SHA-256, sider på 4 KiB), et tomt krav-sæt og en tom CMS-blob.
/// Til hovedprogrammet i en .app bindes Info.plist (slot -1) og _CodeSignature/CodeResources (slot -3) ind, så hele pakken er forseglet.
/// En eksisterende signatur (fx .NET SDK'ens ad hoc-signatur) erstattes; ellers tilføjes LC_CODE_SIGNATURE.
/// </summary>
public static class MachOSigner
{
    public const uint MhMagic64 = 0xfeedfacf;
    private const uint LcSegment64 = 0x19, LcCodeSignature = 0x1d;
    private const uint CsMagicEmbeddedSignature = 0xfade0cc0, CsMagicCodeDirectory = 0xfade0c02, CsMagicRequirements = 0xfade0c01, CsMagicBlobWrapper = 0xfade0b01;
    private const uint CsAdhoc = 0x2;
    public const int PageSize = 4096;
    private const int CdHeaderSize = 88;   // version 0x20400 (med execSeg-felterne)
    public const int HashSize = 32;

    /// <summary>Er filen et 64-bit Mach-O (little-endian, som alle nuværende Mac-programmer)?</summary>
    public static bool IsMachO64(ReadOnlySpan<byte> d) => d.Length >= 32 && BinaryPrimitives.ReadUInt32LittleEndian(d) == MhMagic64;

    public sealed record Layout(int SigCmdOffset, uint DataOff, uint DataSize, int LinkEditCmdOffset, ulong LinkEditFileOff, ulong TextFileSize, uint FileType, int HeaderEnd, int FirstSectionOffset);

    public static Layout Parse(ReadOnlySpan<byte> d)
    {
        if (!IsMachO64(d)) throw new InvalidDataException("Ikke et 64-bit Mach-O-program (fat/universal understøttes ikke)");
        var fileType = BinaryPrimitives.ReadUInt32LittleEndian(d[12..]);
        var ncmds = BinaryPrimitives.ReadUInt32LittleEndian(d[16..]);
        int o = 32, sig = -1, le = -1, firstSect = int.MaxValue;
        uint dataOff = 0, dataSize = 0; ulong leOff = 0, textSize = 0;
        for (var i = 0; i < ncmds; i++)
        {
            var cmd = BinaryPrimitives.ReadUInt32LittleEndian(d[o..]);
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[(o + 4)..]);
            if (cmd == LcSegment64)
            {
                var name = Encoding.ASCII.GetString(d.Slice(o + 8, 16)).TrimEnd('\0');
                var fileOff = BinaryPrimitives.ReadUInt64LittleEndian(d[(o + 40)..]);
                var fileSize = BinaryPrimitives.ReadUInt64LittleEndian(d[(o + 48)..]);
                if (name == "__LINKEDIT") { le = o; leOff = fileOff; }
                if (name == "__TEXT") textSize = fileSize;
                var nsects = BinaryPrimitives.ReadUInt32LittleEndian(d[(o + 64)..]);
                for (var s = 0; s < nsects; s++)
                {
                    var secOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[(o + 72 + s * 80 + 48)..]);
                    if (secOff > 0) firstSect = Math.Min(firstSect, secOff);
                }
            }
            else if (cmd == LcCodeSignature)
            {
                sig = o;
                dataOff = BinaryPrimitives.ReadUInt32LittleEndian(d[(o + 8)..]);
                dataSize = BinaryPrimitives.ReadUInt32LittleEndian(d[(o + 12)..]);
            }
            o += size;
        }
        if (le < 0) throw new InvalidDataException("Mach-O uden __LINKEDIT");
        return new Layout(sig, dataOff, dataSize, le, leOff, textSize, fileType, o, firstSect);
    }

    /// <summary>Hashes, der bindes ind i signaturen (null = slot nul'et).</summary>
    public sealed record Special(byte[]? InfoPlist = null, byte[]? CodeResources = null);

    /// <summary>Signér filen på stedet. Returnerer cdhash (de første 20 byte af SHA-256 over CodeDirectory).</summary>
    public static byte[] SignFile(string path, string identifier, Special? special = null)
    {
        var signed = Sign(File.ReadAllBytes(path), identifier, special, out var cdhash);
        File.WriteAllBytes(path, signed);
        return cdhash;
    }

    public static byte[] Sign(byte[] input, string identifier, Special? special, out byte[] cdhash)
    {
        special ??= new Special();
        var lay = Parse(input);
        // koden går til signaturen (eller filens slutning, hvis der ingen er): alt herfra og ud erstattes
        var codeLimit = lay.SigCmdOffset >= 0 ? (int)lay.DataOff : input.Length;
        var d = new byte[codeLimit];
        Array.Copy(input, d, codeLimit);
        var sigCmd = lay.SigCmdOffset;
        if (sigCmd < 0)
        {
            // tilføj LC_CODE_SIGNATURE i den ledige plads efter load-kommandoerne
            if (lay.HeaderEnd + 16 > lay.FirstSectionOffset) throw new InvalidDataException("Ingen plads til LC_CODE_SIGNATURE");
            sigCmd = lay.HeaderEnd;
            BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(sigCmd), LcCodeSignature);
            BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(sigCmd + 4), 16);
            BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(16), BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(16)) + 1);
            BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(20), BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(20)) + 16);
        }
        // signaturen starter 16-byte-justeret
        var sigOff = (codeLimit + 15) & ~15;
        if (sigOff != codeLimit) { Array.Resize(ref d, sigOff); codeLimit = sigOff; }
        var nSpecial = special.CodeResources is not null ? 3 : 2;
        var nCode = (codeLimit + PageSize - 1) / PageSize;
        var ident = Encoding.UTF8.GetBytes(identifier + "\0");
        var cdLen = CdHeaderSize + ident.Length + (nSpecial + nCode) * HashSize;
        const int reqLen = 12, cmsLen = 8;
        var superLen = 12 + 3 * 8 + cdLen + reqLen + cmsLen;
        var sigSize = (superLen + 15) & ~15;

        // load-kommandoerne skal pege på den nye signatur, før siderne hashes (side 0 indeholder dem)
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(sigCmd + 8), (uint)sigOff);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(sigCmd + 12), (uint)sigSize);
        var leFileSize = (ulong)(sigOff + sigSize) - lay.LinkEditFileOff;
        BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(lay.LinkEditCmdOffset + 48), leFileSize);
        var vmSize = (leFileSize + 0x3fff) & ~0x3fffUL;
        if (BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(lay.LinkEditCmdOffset + 32)) < vmSize)
            BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(lay.LinkEditCmdOffset + 32), vmSize);

        var req = new byte[reqLen];
        BinaryPrimitives.WriteUInt32BigEndian(req, CsMagicRequirements);
        BinaryPrimitives.WriteUInt32BigEndian(req.AsSpan(4), reqLen);   // antal krav: 0
        var cms = new byte[cmsLen];
        BinaryPrimitives.WriteUInt32BigEndian(cms, CsMagicBlobWrapper);
        BinaryPrimitives.WriteUInt32BigEndian(cms.AsSpan(4), cmsLen);

        var cd = new byte[cdLen];
        var c = cd.AsSpan();
        var hashOffset = CdHeaderSize + ident.Length + nSpecial * HashSize;
        BinaryPrimitives.WriteUInt32BigEndian(c, CsMagicCodeDirectory);
        BinaryPrimitives.WriteUInt32BigEndian(c[4..], (uint)cdLen);
        BinaryPrimitives.WriteUInt32BigEndian(c[8..], 0x20400);
        BinaryPrimitives.WriteUInt32BigEndian(c[12..], CsAdhoc);
        BinaryPrimitives.WriteUInt32BigEndian(c[16..], (uint)hashOffset);
        BinaryPrimitives.WriteUInt32BigEndian(c[20..], CdHeaderSize);
        BinaryPrimitives.WriteUInt32BigEndian(c[24..], (uint)nSpecial);
        BinaryPrimitives.WriteUInt32BigEndian(c[28..], (uint)nCode);
        BinaryPrimitives.WriteUInt32BigEndian(c[32..], (uint)codeLimit);
        c[36] = HashSize; c[37] = 2 /* SHA-256 */; c[38] = 0; c[39] = 12 /* 4096 */;
        // 40 spare2, 44 scatterOffset, 48 teamOffset, 52 spare3, 56 codeLimit64: 0
        BinaryPrimitives.WriteUInt64BigEndian(c[64..], 0);                    // execSegBase (__TEXT starter i filens begyndelse)
        BinaryPrimitives.WriteUInt64BigEndian(c[72..], lay.TextFileSize);     // execSegLimit
        BinaryPrimitives.WriteUInt64BigEndian(c[80..], lay.FileType == 2 ? 1UL : 0UL);   // CS_EXECSEG_MAIN_BINARY for programmer
        ident.CopyTo(c[CdHeaderSize..]);
        void Slot(int slot, byte[]? hash) { if (hash is not null) hash.CopyTo(cd, hashOffset + slot * HashSize); }
        Slot(-1, special.InfoPlist);
        Slot(-2, SHA256.HashData(req));
        if (nSpecial >= 3) Slot(-3, special.CodeResources);
        for (var i = 0; i < nCode; i++)
        {
            var start = i * PageSize;
            Slot(i, SHA256.HashData(d.AsSpan(start, Math.Min(PageSize, codeLimit - start))));
        }
        cdhash = SHA256.HashData(cd)[..20];

        var sb = new byte[sigSize];
        var s = sb.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(s, CsMagicEmbeddedSignature);
        BinaryPrimitives.WriteUInt32BigEndian(s[4..], (uint)superLen);
        BinaryPrimitives.WriteUInt32BigEndian(s[8..], 3);
        var off = 12 + 3 * 8;
        void Index(int i, uint type, byte[] blob)
        {
            BinaryPrimitives.WriteUInt32BigEndian(sb.AsSpan(12 + i * 8), type);
            BinaryPrimitives.WriteUInt32BigEndian(sb.AsSpan(16 + i * 8), (uint)off);
            blob.CopyTo(sb, off); off += blob.Length;
        }
        Index(0, 0, cd);            // CSSLOT_CODEDIRECTORY
        Index(1, 2, req);           // CSSLOT_REQUIREMENTS
        Index(2, 0x10000, cms);     // CSSLOT_SIGNATURESLOT (tom ved ad hoc)

        var output = new byte[sigOff + sigSize];
        d.CopyTo(output, 0);
        sb.CopyTo(output, sigOff);
        return output;
    }

    /// <summary>Tjek en signatur, som macOS gør for siderne: hver side skal passe med sin hash. Bruges i test og efter signering.</summary>
    public static (bool Ok, string Why, string Identifier, int SpecialSlots) Verify(byte[] d, byte[]? infoPlist = null, byte[]? codeResources = null)
    {
        var lay = Parse(d);
        if (lay.SigCmdOffset < 0) return (false, "ingen signatur", "", 0);
        var sb = d.AsSpan((int)lay.DataOff, (int)lay.DataSize);
        if (BinaryPrimitives.ReadUInt32BigEndian(sb) != CsMagicEmbeddedSignature) return (false, "ingen SuperBlob", "", 0);
        var count = BinaryPrimitives.ReadUInt32BigEndian(sb[8..]);
        for (var i = 0; i < count; i++)
        {
            if (BinaryPrimitives.ReadUInt32BigEndian(sb[(12 + i * 8)..]) != 0) continue;
            var cdOff = (int)BinaryPrimitives.ReadUInt32BigEndian(sb[(16 + i * 8)..]);
            var cd = sb[cdOff..];
            var len = (int)BinaryPrimitives.ReadUInt32BigEndian(cd[4..]);
            cd = cd[..len];
            var hashOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(cd[16..]);
            var identOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(cd[20..]);
            var nSpecial = (int)BinaryPrimitives.ReadUInt32BigEndian(cd[24..]);
            var nCode = (int)BinaryPrimitives.ReadUInt32BigEndian(cd[28..]);
            var codeLimit = (int)BinaryPrimitives.ReadUInt32BigEndian(cd[32..]);
            var ident = Encoding.UTF8.GetString(cd[identOffset..][..cd[identOffset..].IndexOf((byte)0)]);
            if (codeLimit != lay.DataOff) return (false, "codeLimit passer ikke med signaturens placering", ident, nSpecial);
            if (nCode != (codeLimit + PageSize - 1) / PageSize) return (false, "forkert antal sider", ident, nSpecial);
            for (var p = 0; p < nCode; p++)
            {
                var start = p * PageSize;
                if (!SHA256.HashData(d.AsSpan(start, Math.Min(PageSize, codeLimit - start))).AsSpan().SequenceEqual(cd.Slice(hashOffset + p * HashSize, HashSize)))
                    return (false, $"side {p} passer ikke", ident, nSpecial);
            }
            if (infoPlist is not null && (nSpecial < 1 || !SHA256.HashData(infoPlist).AsSpan().SequenceEqual(cd.Slice(hashOffset - HashSize, HashSize))))
                return (false, "Info.plist passer ikke", ident, nSpecial);
            if (codeResources is not null && (nSpecial < 3 || !SHA256.HashData(codeResources).AsSpan().SequenceEqual(cd.Slice(hashOffset - 3 * HashSize, HashSize))))
                return (false, "CodeResources passer ikke", ident, nSpecial);
            return (true, "", ident, nSpecial);
        }
        return (false, "ingen CodeDirectory", "", 0);
    }

    /// <summary>Kodehashes fra en eksisterende signatur (til at sammenligne med .NET SDK'ens egen signatur).</summary>
    public static List<byte[]> CodeHashes(byte[] d)
    {
        var lay = Parse(d);
        var sb = d.AsSpan((int)lay.DataOff, (int)lay.DataSize);
        var cdOff = (int)BinaryPrimitives.ReadUInt32BigEndian(sb[16..]);
        var cd = sb[cdOff..].ToArray();
        var hashOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(cd.AsSpan(16));
        var nCode = (int)BinaryPrimitives.ReadUInt32BigEndian(cd.AsSpan(28));
        return Enumerable.Range(0, nCode).Select(p => cd.AsSpan(hashOffset + p * HashSize, HashSize).ToArray()).ToList();
    }
}
