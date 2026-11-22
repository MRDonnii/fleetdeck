using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace Fleetdeck.Shared;

/// <summary>
/// Læser Apples plist-filer (binær "bplist00" og XML) – nok til en iPad-backups Manifest.plist og Info.plist. Værdierne bliver
/// Dictionary&lt;string, object?&gt;, List&lt;object?&gt;, string, long, double, bool, byte[] eller DateTime (UTC).
/// </summary>
public static class ApplePlist
{
    private static readonly DateTime Epoch = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static object? Read(byte[] data)
    {
        if (data.Length >= 8 && Encoding.ASCII.GetString(data, 0, 8) == "bplist00") return new Binary(data).Root();
        var doc = XDocument.Parse(Encoding.UTF8.GetString(data));
        var root = doc.Root?.Name.LocalName == "plist" ? doc.Root.Elements().FirstOrDefault() : doc.Root;
        return root is null ? null : Xml(root);
    }

    public static Dictionary<string, object?>? ReadDict(byte[] data) => Read(data) as Dictionary<string, object?>;

    private static object? Xml(XElement e) => e.Name.LocalName switch
    {
        "dict" => XmlDict(e),
        "array" => e.Elements().Select(Xml).ToList(),
        "string" => e.Value,
        "integer" => long.Parse(e.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture),
        "real" => double.Parse(e.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture),
        "true" => true,
        "false" => false,
        "data" => Convert.FromBase64String(string.Concat(e.Value.Where(c => !char.IsWhiteSpace(c)))),
        "date" => DateTime.Parse(e.Value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
        _ => null,
    };

    private static Dictionary<string, object?> XmlDict(XElement e)
    {
        var d = new Dictionary<string, object?>();
        var items = e.Elements().ToList();
        for (var i = 0; i + 1 < items.Count; i += 2)
            if (items[i].Name.LocalName == "key") d[items[i].Value] = Xml(items[i + 1]);
        return d;
    }

    private sealed class Binary(byte[] b)
    {
        private int _offSize, _refSize;
        private long _count, _table;
        private int _depth;

        public object? Root()
        {
            if (b.Length < 40) throw new InvalidDataException("plist er for kort");
            var t = b.AsSpan(b.Length - 32);
            _offSize = t[6]; _refSize = t[7];
            _count = (long)BinaryPrimitives.ReadUInt64BigEndian(t[8..]);
            var top = (long)BinaryPrimitives.ReadUInt64BigEndian(t[16..]);
            _table = (long)BinaryPrimitives.ReadUInt64BigEndian(t[24..]);
            if (_offSize is < 1 or > 8 || _refSize is < 1 or > 8 || _count < 1 || top >= _count || _table + _count * _offSize > b.Length - 32)
                throw new InvalidDataException("plist er ødelagt");
            return Obj(top);
        }

        private long Uint(long at, int size)
        {
            if (at < 0 || at + size > b.Length) throw new InvalidDataException("plist er ødelagt");
            ulong v = 0;
            for (var i = 0; i < size; i++) v = (v << 8) | b[at + i];
            return (long)v;
        }

        private object? Obj(long index)
        {
            if (index < 0 || index >= _count || ++_depth > 64) throw new InvalidDataException("plist er ødelagt");
            try
            {
                var at = Uint(_table + index * _offSize, _offSize);
                if (at < 8 || at >= b.Length - 32) throw new InvalidDataException("plist er ødelagt");
                var marker = b[at];
                int kind = marker >> 4, info = marker & 0xF;
                switch (kind)
                {
                    case 0x0: return info switch { 0x8 => false, 0x9 => true, _ => null };
                    case 0x1: return Int(at + 1, 1 << info);
                    case 0x2: return info == 2 ? BitConverter.Int32BitsToSingle((int)Uint(at + 1, 4)) : BitConverter.Int64BitsToDouble(Uint(at + 1, 8));
                    case 0x3: return Epoch.AddSeconds(BitConverter.Int64BitsToDouble(Uint(at + 1, 8)));
                    case 0x8: return Uint(at + 1, info + 1);   // UID (NSKeyedArchiver)
                }
                var (len, start) = Length(at, info);
                switch (kind)
                {
                    case 0x4: Check(start, len); return b.AsSpan((int)start, (int)len).ToArray();
                    case 0x5: Check(start, len); return Encoding.ASCII.GetString(b, (int)start, (int)len);
                    case 0x6: Check(start, len * 2); return Encoding.BigEndianUnicode.GetString(b, (int)start, (int)len * 2);
                    case 0xA:
                    case 0xC:
                    {
                        var list = new List<object?>();
                        for (long i = 0; i < len; i++) list.Add(Obj(Uint(start + i * _refSize, _refSize)));
                        return list;
                    }
                    case 0xD:
                    {
                        var d = new Dictionary<string, object?>();
                        for (long i = 0; i < len; i++)
                        {
                            var k = Obj(Uint(start + i * _refSize, _refSize)) as string ?? throw new InvalidDataException("plist-nøgle er ikke tekst");
                            d[k] = Obj(Uint(start + (len + i) * _refSize, _refSize));
                        }
                        return d;
                    }
                    default: throw new InvalidDataException($"Ukendt plist-type 0x{marker:X2}");
                }
            }
            finally { _depth--; }
        }

        private long Int(long at, int size) => size switch
        {
            1 => b[at],
            2 => (long)Uint(at, 2),
            4 => (long)Uint(at, 4),
            8 => Uint(at, 8),
            16 => Uint(at + 8, 8),
            _ => throw new InvalidDataException("plist-tal er ødelagt"),
        };

        private (long Len, long Start) Length(long at, int info)
        {
            if (info != 0xF) return (info, at + 1);
            var m = b[at + 1];
            if (m >> 4 != 0x1) throw new InvalidDataException("plist-længde er ødelagt");
            var size = 1 << (m & 0xF);
            return (Int(at + 2, size), at + 2 + size);
        }

        private void Check(long start, long len)
        {
            if (len < 0 || start + len > b.Length) throw new InvalidDataException("plist er ødelagt");
        }
    }
}
