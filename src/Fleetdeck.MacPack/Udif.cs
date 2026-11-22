using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Fleetdeck.MacPack;

/// <summary>
/// Apples komprimerede disk-image-format (UDIF "UDZO", det hdiutil laver): datadelen er zlib-komprimerede bidder af det rå billede,
/// bagefter en XML-plist med en "blkx"-tabel (mish) over bidderne, og til sidst en 512-byte "koly"-trailer.
/// Billedet er hele disken uden partitionstabel (som "hdiutil create -layout NONE"); macOS finder HFS+-volumet direkte.
/// CRC32-kontrolsummerne (pr. bid-tabel, data og master) passer, så Finders "Bekræfter…" godkender billedet.
/// </summary>
public static class Udif
{
    public const int SectorSize = 512;
    public const int ChunkSectors = 2048;   // 1 MiB pr. bid
    private const uint ChunkZero = 0x00000002, ChunkRaw = 0x00000001, ChunkZlib = 0x80000005, ChunkEnd = 0xFFFFFFFF;
    private const uint ChecksumCrc32 = 2;
    public const string PartitionName = "whole disk (Apple_HFS : 0)";

    public sealed record Chunk(uint Type, ulong Sector, ulong Sectors, ulong Offset, ulong Length);

    /// <summary>Skriv et UDZO-billede ud fra et rå diskbillede (størrelsen skal gå op i 512).</summary>
    public static void Write(Stream raw, Stream output, int level = 9)
    {
        if (raw.Length % SectorSize != 0) throw new ArgumentException("Billedets størrelse skal gå op i 512");
        var totalSectors = (ulong)(raw.Length / SectorSize);
        var chunks = new List<Chunk>();
        var dataCrc = new Crc32();
        var rawCrc = new Crc32();
        var buf = new byte[ChunkSectors * SectorSize];
        ulong sector = 0, offset = 0;
        raw.Position = 0;
        var compression = level >= 9 ? CompressionLevel.SmallestSize : CompressionLevel.Optimal;
        while (sector < totalSectors)
        {
            var n = (int)Math.Min((ulong)buf.Length, (totalSectors - sector) * SectorSize);
            raw.ReadExactly(buf, 0, n);
            var span = buf.AsSpan(0, n);
            rawCrc.Append(span);
            var sectors = (ulong)(n / SectorSize);
            if (!span.ContainsAnyExcept((byte)0)) { chunks.Add(new Chunk(ChunkZero, sector, sectors, offset, 0)); sector += sectors; continue; }
            using var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, compression, leaveOpen: true)) z.Write(span);
            var (type, bytes) = ms.Length < n ? (ChunkZlib, ms.ToArray()) : (ChunkRaw, span.ToArray());
            output.Write(bytes);
            dataCrc.Append(bytes);
            chunks.Add(new Chunk(type, sector, sectors, offset, (ulong)bytes.Length));
            offset += (ulong)bytes.Length; sector += sectors;
        }
        chunks.Add(new Chunk(ChunkEnd, totalSectors, 0, offset, 0));
        var dataLength = offset;
        var blkCrc = rawCrc.GetCurrentHashAsUInt32();
        var mish = Mish(totalSectors, chunks, blkCrc);
        var plist = Plist(mish);
        var xmlOffset = dataLength;
        output.Write(plist);
        // master-kontrolsum: CRC32 over bid-tabellernes kontrolsummer (big-endian)
        var be = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(be, blkCrc);
        var master = Crc32.HashToUInt32(be);
        output.Write(Koly(dataLength, dataCrc.GetCurrentHashAsUInt32(), xmlOffset, (ulong)plist.Length, master, totalSectors));
        output.Flush();
    }

    private static byte[] Mish(ulong sectors, List<Chunk> chunks, uint crc)
    {
        var b = new byte[204 + 40 * chunks.Count];
        var s = b.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(s, 0x6D697368);       // 'mish'
        BinaryPrimitives.WriteUInt32BigEndian(s[4..], 1);
        BinaryPrimitives.WriteUInt64BigEndian(s[8..], 0);           // første sektor
        BinaryPrimitives.WriteUInt64BigEndian(s[16..], sectors);
        BinaryPrimitives.WriteUInt64BigEndian(s[24..], 0);          // dataOffset
        BinaryPrimitives.WriteUInt32BigEndian(s[32..], ChunkSectors + 8);   // buffersNeeded
        BinaryPrimitives.WriteUInt32BigEndian(s[36..], 0xFFFFFFFE); // hele disken
        BinaryPrimitives.WriteUInt32BigEndian(s[64..], ChecksumCrc32);
        BinaryPrimitives.WriteUInt32BigEndian(s[68..], 32);
        BinaryPrimitives.WriteUInt32BigEndian(s[72..], crc);
        BinaryPrimitives.WriteUInt32BigEndian(s[200..], (uint)chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            var c = s[(204 + 40 * i)..];
            BinaryPrimitives.WriteUInt32BigEndian(c, chunks[i].Type);
            BinaryPrimitives.WriteUInt64BigEndian(c[8..], chunks[i].Sector);
            BinaryPrimitives.WriteUInt64BigEndian(c[16..], chunks[i].Sectors);
            BinaryPrimitives.WriteUInt64BigEndian(c[24..], chunks[i].Offset);
            BinaryPrimitives.WriteUInt64BigEndian(c[32..], chunks[i].Length);
        }
        return b;
    }

    private static byte[] Plist(byte[] mish)
    {
        var b64 = Convert.ToBase64String(mish);
        var lines = string.Join("\n", Enumerable.Range(0, (b64.Length + 51) / 52).Select(i => "\t\t\t\t" + b64.Substring(i * 52, Math.Min(52, b64.Length - i * 52))));
        var xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
            	<key>resource-fork</key>
            	<dict>
            		<key>blkx</key>
            		<array>
            			<dict>
            				<key>Attributes</key>
            				<string>0x0050</string>
            				<key>CFName</key>
            				<string>{PartitionName}</string>
            				<key>Data</key>
            				<data>
            {lines}
            				</data>
            				<key>ID</key>
            				<string>-1</string>
            				<key>Name</key>
            				<string>{PartitionName}</string>
            			</dict>
            		</array>
            	</dict>
            </dict>
            </plist>

            """.Replace("\r\n", "\n");
        return Encoding.UTF8.GetBytes(xml);
    }

    private static byte[] Koly(ulong dataLength, uint dataCrc, ulong xmlOffset, ulong xmlLength, uint master, ulong sectors)
    {
        var k = new byte[512];
        var s = k.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(s, 0x6B6F6C79);      // 'koly'
        BinaryPrimitives.WriteUInt32BigEndian(s[4..], 4);
        BinaryPrimitives.WriteUInt32BigEndian(s[8..], 512);
        BinaryPrimitives.WriteUInt32BigEndian(s[12..], 1);         // flattened
        BinaryPrimitives.WriteUInt64BigEndian(s[16..], 0);         // runningDataForkOffset
        BinaryPrimitives.WriteUInt64BigEndian(s[24..], 0);         // dataForkOffset
        BinaryPrimitives.WriteUInt64BigEndian(s[32..], dataLength);
        BinaryPrimitives.WriteUInt64BigEndian(s[40..], 0);         // rsrcForkOffset
        BinaryPrimitives.WriteUInt64BigEndian(s[48..], 0);         // rsrcForkLength
        BinaryPrimitives.WriteUInt32BigEndian(s[56..], 1);         // segmentNumber
        BinaryPrimitives.WriteUInt32BigEndian(s[60..], 1);         // segmentCount
        Guid.NewGuid().ToByteArray().CopyTo(s[64..]);              // segmentID
        BinaryPrimitives.WriteUInt32BigEndian(s[80..], ChecksumCrc32);
        BinaryPrimitives.WriteUInt32BigEndian(s[84..], 32);
        BinaryPrimitives.WriteUInt32BigEndian(s[88..], dataCrc);
        BinaryPrimitives.WriteUInt64BigEndian(s[216..], xmlOffset);
        BinaryPrimitives.WriteUInt64BigEndian(s[224..], xmlLength);
        BinaryPrimitives.WriteUInt32BigEndian(s[352..], ChecksumCrc32);
        BinaryPrimitives.WriteUInt32BigEndian(s[356..], 32);
        BinaryPrimitives.WriteUInt32BigEndian(s[360..], master);
        BinaryPrimitives.WriteUInt32BigEndian(s[488..], 1);        // imageVariant
        BinaryPrimitives.WriteUInt64BigEndian(s[492..], sectors);
        return k;
    }

    /// <summary>Læs et UDZO-billede tilbage til det rå billede (til test): tjekker koly, plist, bidder og kontrolsummer.</summary>
    public static byte[] Read(byte[] dmg)
    {
        var k = dmg.AsSpan(dmg.Length - 512);
        if (BinaryPrimitives.ReadUInt32BigEndian(k) != 0x6B6F6C79) throw new InvalidDataException("ingen koly-trailer");
        var dataLength = (long)BinaryPrimitives.ReadUInt64BigEndian(k[32..]);
        var xmlOff = (int)BinaryPrimitives.ReadUInt64BigEndian(k[216..]);
        var xmlLen = (int)BinaryPrimitives.ReadUInt64BigEndian(k[224..]);
        var sectors = BinaryPrimitives.ReadUInt64BigEndian(k[492..]);
        if (Crc32.HashToUInt32(dmg.AsSpan(0, (int)dataLength)) != BinaryPrimitives.ReadUInt32BigEndian(k[88..])) throw new InvalidDataException("data-kontrolsum passer ikke");
        var xml = Encoding.UTF8.GetString(dmg, xmlOff, xmlLen);
        var doc = System.Xml.Linq.XDocument.Parse(xml);
        var data = doc.Descendants("data").First().Value;
        var mish = Convert.FromBase64String(string.Concat(data.Where(c => !char.IsWhiteSpace(c))));
        if (BinaryPrimitives.ReadUInt32BigEndian(mish) != 0x6D697368) throw new InvalidDataException("ingen mish");
        var raw = new byte[sectors * SectorSize];
        var n = (int)BinaryPrimitives.ReadUInt32BigEndian(mish.AsSpan(200));
        for (var i = 0; i < n; i++)
        {
            var c = mish.AsSpan(204 + 40 * i);
            var type = BinaryPrimitives.ReadUInt32BigEndian(c);
            var sec = (long)BinaryPrimitives.ReadUInt64BigEndian(c[8..]); var cnt = (long)BinaryPrimitives.ReadUInt64BigEndian(c[16..]);
            var off = (int)BinaryPrimitives.ReadUInt64BigEndian(c[24..]); var len = (int)BinaryPrimitives.ReadUInt64BigEndian(c[32..]);
            var dest = raw.AsSpan((int)(sec * SectorSize), (int)(cnt * SectorSize));
            switch (type)
            {
                case ChunkZlib:
                    using (var z = new ZLibStream(new MemoryStream(dmg, off, len), CompressionMode.Decompress)) z.ReadExactly(dest);
                    break;
                case ChunkRaw: dmg.AsSpan(off, len).CopyTo(dest); break;
                case ChunkZero or ChunkEnd: break;
                default: throw new InvalidDataException($"ukendt bid-type {type:x}");
            }
        }
        if (Crc32.HashToUInt32(raw) != BinaryPrimitives.ReadUInt32BigEndian(mish.AsSpan(72))) throw new InvalidDataException("billedets kontrolsum passer ikke");
        return raw;
    }
}
